using System;
using System.Collections.Generic;
using System.Text;

namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    internal static class FreeCadScriptExportAll
    {
        internal const string HeadPY = @"


# ============================================================
# CNC_Improvements_gcode_solids : _ALL STEP MERGE SCRIPT (WITH LOG FILE)
#
# Adds:
#   - Writes a sibling log: OUT_STEP with "".log"" extension
#   - Logs: start header, inputs, per-file import stats,
#           leaf counts, dedupe counts, final export + size, status
#   - Still prints to console (FreeCADCmd), but log is the source of truth
#
# Behavior preserved:
#   - import multiple STEP files
#   - collect ONLY leaf shapes (prevents container+children doubling)
#   - de-duplicate identical solids by geometry signature + equality test
#   - build ONE compound
#   - export ONLY that compound using Part.export (cleaner for NX)
#   - force STEP schema AP203 to reduce product-structure junk
# ============================================================

import os
import sys
import time
import traceback

import FreeCAD as App
import Import
import Part

# -------------------------
# C# injects:
#   PROJECT_NAME
#   OUT_STEP
#   IN_STEPS
# -------------------------


";

        // Dynamic values (set by ExportAll right before running merge)
        public static string ProjectName = "";
        public static string OutStepPath = "";
        public static List<string> FilesToMerge = new List<string>();

        internal const string TailPY = @"
# De-dupe tolerance (model units)
DEDUP_TOL = 1e-6
KEY_ROUND_DP = 6

# -------------------------
# LOGGING
# -------------------------
_LOG_FH = None
_LOG_PATH = """"
_T0 = 0.0

def _fmt_secs(sec):
    try:
        return ""%0.3fs"" % float(sec)
    except Exception:
        return str(sec)

def _elapsed():
    return time.time() - (_T0 or time.time())

def _safe_int(n):
    try:
        return int(n)
    except Exception:
        return 0

def _safe_float(v):
    try:
        return float(v)
    except Exception:
        return 0.0

def _log(line=""""):
    # Log to file + console
    global _LOG_FH
    try:
        s = """" if line is None else str(line)
    except Exception:
        s = ""<unprintable>""
    try:
        if _LOG_FH is not None:
            _LOG_FH.write(s + ""\n"")
            _LOG_FH.flush()
    except Exception:
        pass
    try:
        print(s, flush=True)
    except Exception:
        pass

def _open_log(out_step):
    global _LOG_FH, _LOG_PATH, _T0
    out_step = norm(out_step)
    _LOG_PATH = os.path.splitext(out_step)[0] + "".log""
    d = os.path.dirname(_LOG_PATH)
    if d and not os.path.isdir(d):
        os.makedirs(d, exist_ok=True)
    _LOG_FH = open(_LOG_PATH, ""w"", encoding=""utf-8"", errors=""replace"")
    _T0 = time.time()
    _log(""============================================================"")
    _log(""NPC _ALL MERGE SCRIPT START"")
    _log(""Time (local): %s"" % time.strftime(""%Y-%m-%d %H:%M:%S""))
    try:
        _log(""Time (UTC)  : %s"" % time.strftime(""%Y-%m-%d %H:%M:%S"", time.gmtime()))
    except Exception:
        pass
    _log(""OUT_STEP    : %s"" % out_step)
    _log(""LOG_PATH    : %s"" % _LOG_PATH)
    _log(""PROJECT_NAME: %s"" % str(PROJECT_NAME))
    _log(""DEDUP_TOL   : %s"" % str(DEDUP_TOL))
    _log(""KEY_ROUND_DP: %s"" % str(KEY_ROUND_DP))
    _log(""Python      : %s"" % sys.version.replace(""\n"", "" ""))
    try:
        _log(""FreeCAD     : %s"" % str(App.Version()))
    except Exception:
        pass
    _log(""============================================================"")
    _log("""")

def _close_log():
    global _LOG_FH
    try:
        if _LOG_FH is not None:
            _LOG_FH.flush()
            _LOG_FH.close()
    except Exception:
        pass
    _LOG_FH = None


# -------------------------
# ORIGINAL HELPERS (unchanged logic)
# -------------------------
def norm(p):
    if not p:
        return """"
    p = str(p).strip().strip('""').strip(""'"")
    return os.path.normpath(os.path.expandvars(p))

def set_step_schema_ap203():
    candidates = [
        ""User parameter:BaseApp/Preferences/Mod/Import"",
        ""User parameter:BaseApp/Preferences/Mod/Part"",
        ""User parameter:BaseApp/Preferences/Mod/Part/STEP"",
        ""User parameter:BaseApp/Preferences/Mod/Import/STEP"",
    ]
    for path in candidates:
        try:
            pg = App.ParamGet(path)
            pg.SetString(""STEPStandard"", ""AP203"")
            pg.SetString(""Schema"", ""AP203"")
            pg.SetString(""Scheme"", ""AP203"")
            pg.SetInt(""ExportMode"", 0)
        except Exception:
            pass

def is_real_shape_obj(o):
    if o is None:
        return False
    if not hasattr(o, ""Shape""):
        return False
    try:
        sh = o.Shape
        if sh is None or sh.isNull():
            return False
        return True
    except Exception:
        return False

def is_leaf_shape_obj(o):
    if not is_real_shape_obj(o):
        return False

    # If it owns other objects, it's a container -> skip
    try:
        if hasattr(o, ""OutList"") and o.OutList and len(o.OutList) > 0:
            return False
    except Exception:
        pass

    # Skip obvious container/group types
    try:
        tid = getattr(o, ""TypeId"", """") or """"
        if tid.startswith(""App::Part""):
            return False
        if tid.startswith(""App::DocumentObjectGroup""):
            return False
        if tid.startswith(""App::LinkGroup""):
            return False
    except Exception:
        pass

    return True

def shape_with_placement(o):
    sh = o.Shape.copy()
    try:
        if hasattr(o, ""getGlobalPlacement""):
            sh.Placement = o.getGlobalPlacement()
        elif hasattr(o, ""Placement""):
            sh.Placement = o.Placement
    except Exception:
        pass
    return sh

def shape_key(sh):
    bb = sh.BoundBox
    vol = _safe_float(getattr(sh, ""Volume"", 0.0))
    area = _safe_float(getattr(sh, ""Area"", 0.0))
    try:
        n_sol = len(sh.Solids)
    except Exception:
        n_sol = 0
    try:
        n_fac = len(sh.Faces)
    except Exception:
        n_fac = 0
    try:
        n_edg = len(sh.Edges)
    except Exception:
        n_edg = 0

    r = KEY_ROUND_DP
    return (
        round(bb.XMin, r), round(bb.YMin, r), round(bb.ZMin, r),
        round(bb.XMax, r), round(bb.YMax, r), round(bb.ZMax, r),
        round(vol, r), round(area, r),
        int(n_sol), int(n_fac), int(n_edg),
    )

def bbox_close(a, b, tol):
    try:
        return (
            abs(a.XMin - b.XMin) <= tol and abs(a.YMin - b.YMin) <= tol and abs(a.ZMin - b.ZMin) <= tol and
            abs(a.XMax - b.XMax) <= tol and abs(a.YMax - b.YMax) <= tol and abs(a.ZMax - b.ZMax) <= tol
        )
    except Exception:
        return False

def shapes_equal(sh1, sh2, tol):
    try:
        bb1 = sh1.BoundBox
        bb2 = sh2.BoundBox
        if not bbox_close(bb1, bb2, tol):
            return False
    except Exception:
        pass

    v1 = _safe_float(getattr(sh1, ""Volume"", 0.0))
    v2 = _safe_float(getattr(sh2, ""Volume"", 0.0))
    if abs(v1 - v2) > (tol * max(1.0, abs(v1), abs(v2))):
        return False

    try:
        if hasattr(sh1, ""isEqual""):
            return bool(sh1.isEqual(sh2))
    except Exception:
        pass

    try:
        if hasattr(sh1, ""distToShape""):
            d = sh1.distToShape(sh2)[0]
            return _safe_float(d) <= tol
    except Exception:
        pass

    return False


# -------------------------
# MAIN
# -------------------------
def main():
    out_step = norm(OUT_STEP)
    in_steps = [norm(p) for p in IN_STEPS if p]

    _open_log(out_step)

    _log(""=== MERGE STEP (_ALL) : NX FLATTEN, LEAF + DEDUPE ==="")
    _log(""OUT: %s"" % out_step)
    _log(""IN : %d"" % len(in_steps))
    _log("""")

    ok_inputs = []
    for p in in_steps:
        try:
            if os.path.isfile(p) and os.path.getsize(p) > 0:
                ok_inputs.append(p)
            else:
                _log(""SKIP_MISSING_OR_EMPTY: %s"" % p)
        except Exception:
            _log(""SKIP_STAT_FAIL: %s"" % p)

    if not ok_inputs:
        _log("""")
        _log(""MERGE_FAIL: no valid input STEP files"")
        _log(""STATUS: FAIL"")
        return 2

    out_dir = os.path.dirname(out_step)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    set_step_schema_ap203()

    doc = App.newDocument(""MERGED_STEP"")

    buckets = {}
    all_shapes = []
    total_dup_skipped = 0

    total_files_ok = 0
    total_files_import_fail = 0
    total_leaf_added = 0
    total_leaf_dups = 0
    total_leaf_seen = 0

    for idx, p in enumerate(ok_inputs):
        bn = os.path.basename(p)
        _log("""")
        _log(""------------------------------------------------------------"")
        _log(""IMPORT %d/%d: %s"" % (idx + 1, len(ok_inputs), bn))
        _log(""PATH: %s"" % p)

        before = set(obj.Name for obj in doc.Objects)

        t_imp = time.time()
        try:
            Import.insert(p, doc.Name)
            doc.recompute()
            total_files_ok += 1
            _log(""IMPORT_OK   (%s)"" % _fmt_secs(time.time() - t_imp))
        except Exception as ex:
            total_files_import_fail += 1
            _log(""IMPORT_FAIL (%s)"" % _fmt_secs(time.time() - t_imp))
            _log(""  ERROR: %s"" % str(ex))
            _log(traceback.format_exc())
            continue

        after = set(obj.Name for obj in doc.Objects)
        new_names = list(after - before)

        added_leaf = 0
        dup_leaf = 0
        seen_leaf = 0

        t_leaf = time.time()
        for n in new_names:
            o = doc.getObject(n)
            if not is_leaf_shape_obj(o):
                continue

            seen_leaf += 1
            sh = shape_with_placement(o)

            k = shape_key(sh)
            reps = buckets.get(k, [])

            is_dup = False
            for rep in reps:
                if shapes_equal(sh, rep, DEDUP_TOL):
                    is_dup = True
                    break

            if is_dup:
                dup_leaf += 1
                total_dup_skipped += 1
                continue

            reps.append(sh)
            buckets[k] = reps
            all_shapes.append(sh)
            added_leaf += 1

        total_leaf_seen += seen_leaf
        total_leaf_added += added_leaf
        total_leaf_dups += dup_leaf

        _log(""LEAF_SCAN_OK (%s)"" % _fmt_secs(time.time() - t_leaf))
        _log(""IMPORTED_LEAF_SHAPES_SEEN              : %d"" % seen_leaf)
        _log(""IMPORTED_LEAF_SHAPES_ADDED             : %d"" % added_leaf)
        _log(""IMPORTED_LEAF_SHAPES_DUPLICATE_SKIPPED : %d"" % dup_leaf)

    _log("""")
    _log(""============================================================"")
    _log(""TOTAL_FILES_OK          : %d"" % total_files_ok)
    _log(""TOTAL_FILES_IMPORT_FAIL : %d"" % total_files_import_fail)
    _log(""TOTAL_LEAF_SEEN         : %d"" % total_leaf_seen)
    _log(""TOTAL_UNIQUE_SHAPES     : %d"" % len(all_shapes))
    _log(""TOTAL_DUPLICATES_SKIPPED: %d"" % total_dup_skipped)
    _log(""============================================================"")
    _log("""")

    if not all_shapes:
        _log(""MERGE_FAIL: imported files but found no unique leaf shapes"")
        _log(""STATUS: FAIL"")
        return 3

    _log(""BUILD_COMPOUND: shapes = %d"" % len(all_shapes))
    t_comp = time.time()
    try:
        comp = Part.makeCompound(all_shapes)
        _log(""BUILD_COMPOUND_OK (%s)"" % _fmt_secs(time.time() - t_comp))
    except Exception as ex:
        _log(""MERGE_FAIL: makeCompound failed: %s"" % str(ex))
        _log(traceback.format_exc())
        _log(""STATUS: FAIL"")
        return 4

    merged = doc.addObject(""Part::Feature"", ""Merged_%s"" % str(PROJECT_NAME))
    merged.Label = ""Merged_%s"" % str(PROJECT_NAME)
    merged.Shape = comp
    doc.recompute()

    _log("""")
    _log(""EXPORT (Part.export): %s"" % out_step)
    t_exp = time.time()
    try:
        Part.export([merged], out_step)
        dt = time.time() - t_exp
        try:
            sz = os.path.getsize(out_step)
        except Exception:
            sz = -1
        _log(""MERGE_OK (%s) size=%s"" % (_fmt_secs(dt), str(sz)))
        _log(""Total time: %s"" % _fmt_secs(_elapsed()))
        _log(""STATUS: SUCCESS"")
        return 0
    except Exception as ex:
        _log(""MERGE_FAIL_EXPORT: %s :: %s"" % (out_step, str(ex)))
        _log(traceback.format_exc())
        _log(""Total time: %s"" % _fmt_secs(_elapsed()))
        _log(""STATUS: FAIL"")
        return 5


try:
    rc = main()
    raise SystemExit(rc)
except SystemExit:
    raise
except Exception as ex:
    try:
        _log("""")
        _log(""============================================================"")
        _log(""=== _ALL MERGE FAILED (UNHANDLED) ==="")
        _log(str(ex))
        _log(traceback.format_exc())
        _log(""STATUS: FAIL"")
        _log(""============================================================"")
    except Exception:
        pass
    raise
finally:
    try:
        _close_log()
    except Exception:
        pass
    try:
        sys.stdout.flush()
        sys.stderr.flush()
    except Exception:
        pass

";

        public static string BuildScriptText()
        {
            if (string.IsNullOrWhiteSpace(ProjectName))
                throw new InvalidOperationException("FreeCadScriptExportAll.ProjectName is not set.");

            if (string.IsNullOrWhiteSpace(OutStepPath))
                throw new InvalidOperationException("FreeCadScriptExportAll.OutStepPath is not set.");

            if (FilesToMerge == null || FilesToMerge.Count == 0)
                throw new InvalidOperationException("FreeCadScriptExportAll.FilesToMerge is empty (nothing to merge).");

            string pn = ProjectName.Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.AppendLine(HeadPY.TrimEnd());
            sb.AppendLine();
            sb.AppendLine($"PROJECT_NAME = \"{pn}\"");
            sb.AppendLine($"OUT_STEP = r\"{OutStepPath}\"");
            sb.AppendLine();
            sb.AppendLine("IN_STEPS = [");
            foreach (var p in FilesToMerge)
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                sb.AppendLine($"    r\"{p}\",");
            }
            sb.AppendLine("]");
            sb.AppendLine(TailPY);

            return sb.ToString();
        }
    }
}
