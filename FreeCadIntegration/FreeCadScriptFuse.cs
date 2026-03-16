using System;
using System.Text;

namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Holds the embedded Python strings and the dynamic values
    /// for mill tool-sweep geometry (toolpath → solids in FreeCAD).
    /// </summary>
    internal static class FreeCadScriptFuse
    {
        // Python header: imports, documentation, banner.
        // NOTE: C# will typically prepend something like:
        //   output_step = r"C:\path\to\my_shape_stp.stp"
        // BEFORE this header, so the tail can use `output_step`.
        internal const string HeadPY = @"

# ============================================================
# CNC_Improvements_gcode_solids : MERGE+FUSE+SUBTRACT SCRIPT (CLI FAST, SAME BEHAVIOR)
#
# SAME BEHAVIOR AS YOUR ORIGINAL:
# - TURN: fuse sequentially (FATAL on fail)
# - MILL: if MILL STEP has exactly 1 solid => CUT
#         if >1 solids => EXPORT ONLY (no cut)
# - DRILL: subtract ALL solids from each DRILL STEP (continue on per-solid fail)
#
# FAST CHANGES:
# - NO common(), NO volume calcs
# - minimal logging
# - removeSplitter only at end (and optional after TURN)
# - one recompute before export
#
# SOLIDS-ONLY export.
# Log file: <OUT_STEP without extension>.log
# ============================================================

import os
import sys
import time
import traceback
import datetime

import FreeCAD as App
import Part


# -----------------------------
# SETTINGS
# -----------------------------
DO_TURN_SPLITTER_CLEANUP = False
DO_FINAL_SPLITTER_CLEANUP = True
SPLITTER_PASSES = 2


# -----------------------------
# PATH + LOG
# -----------------------------
def _norm_step_path(p):
    if p is None:
        return """"
    s = str(p).strip()
    if (len(s) >= 2) and ((s[0] == '""' and s[-1] == '""') or (s[0] == ""'"" and s[-1] == ""'"")):
        s = s[1:-1].strip()
    s = os.path.expandvars(s)
    s = os.path.normpath(s)
    return s


def _as_path_list(lst, varname):
    if lst is None:
        raise Exception(""%s is None (must be a list of STEP file paths)."" % varname)
    if not isinstance(lst, (list, tuple)):
        raise Exception(""%s must be a list/tuple of STEP file paths."" % varname)

    out = []
    for i, item in enumerate(lst):
        if not isinstance(item, str):
            raise Exception(""%s[%d] is not a string STEP path."" % (varname, i))
        p = _norm_step_path(item)
        if p:
            out.append(p)
    return out


def _require_files(paths, label):
    missing = [p for p in paths if not os.path.isfile(p)]
    if missing:
        raise Exception(""%s: missing STEP files:\n%s"" % (label, ""\n"".join(missing)))


_LOG_FH = None
_LOG_PATH = None
_T0 = None


def _log(msg):
    global _LOG_FH
    s = str(msg)
    try:
        print(s, flush=True)
    except Exception:
        pass
    try:
        if _LOG_FH is not None:
            _LOG_FH.write(s + ""\n"")
    except Exception:
        pass


def _log_header(out_step):
    _log(""============================================================"")
    _log(""NPC FUSE SCRIPT START (CLI FAST, SAME BEHAVIOR)"")
    _log(""Time (local): %s"" % datetime.datetime.now().strftime(""%Y-%m-%d %H:%M:%S""))
    _log(""OUT_STEP    : %s"" % out_step)
    _log(""LOG_PATH    : %s"" % _LOG_PATH)
    try:
        _log(""FreeCAD     : %s"" % App.Version())
    except Exception:
        _log(""FreeCAD     : (version unavailable)"")
    _log(""============================================================"")
    _log("""")


def _elapsed():
    if _T0 is None:
        return 0.0
    return time.time() - _T0


def _fmt_secs(sec):
    try:
        return ""%.3fs"" % float(sec)
    except Exception:
        return str(sec)


# -----------------------------
# GEOM UTIL
# -----------------------------
def _load_step_shape(step_path):
    shp = Part.Shape()
    shp.read(step_path)
    if shp.isNull():
        raise Exception(""Failed to read STEP (null shape): %s"" % step_path)
    return shp


def _solids_list_or_throw(shape, label):
    if shape is None or shape.isNull():
        raise Exception(""%s: shape is null."" % label)
    solids = list(shape.Solids)
    if not solids:
        raise Exception(""%s: result has ZERO solids."" % label)
    return solids


def _as_solids_only_shape(shape, label):
    solids = _solids_list_or_throw(shape, label)
    if len(solids) == 1:
        return solids[0]
    return Part.Compound(solids)


def _try_remove_splitter(shape):
    if shape is None or shape.isNull():
        return shape
    try:
        return shape.removeSplitter()
    except Exception:
        return shape


def _splitter_cleanup(shape, label):
    if shape is None or shape.isNull():
        return shape
    s = shape
    for _ in range(max(0, int(SPLITTER_PASSES))):
        s2 = _try_remove_splitter(s)
        if s2 is None or s2.isNull():
            break
        s = s2
    return _as_solids_only_shape(s, ""%s: after splitter cleanup"" % label)


def _bb_overlap(bb1, bb2):
    try:
        if bb1 is None or bb2 is None:
            return True
        if bb1.XMax < bb2.XMin or bb2.XMax < bb1.XMin:
            return False
        if bb1.YMax < bb2.YMin or bb2.YMax < bb1.YMin:
            return False
        if bb1.ZMax < bb2.ZMin or bb2.ZMax < bb1.ZMin:
            return False
        return True
    except Exception:
        return True


# ============================================================
# USER-EMITTED VARIABLES GO HERE
# ============================================================





";
        public static string ProjectName = "";
        public static string OutStepPath = "";



        public static string FuseTurnFiles = "";
        public static string FuseMillFiles = "";
        public static string FuseDrillFiles = "";

        // Tail: parses TOOLPATH_TEXT, builds segment solids, exports STEP.
        internal const string TailPY = @"




# ============================================================
# MAIN
# ============================================================
def main():
    global _LOG_FH, _LOG_PATH, _T0

    out_step = _norm_step_path(OUT_STEP)
    if not out_step:
        raise Exception(""OUT_STEP is empty/invalid."")

    _LOG_PATH = os.path.splitext(out_step)[0] + "".log""
    log_dir = os.path.dirname(_LOG_PATH)
    if log_dir and not os.path.isdir(log_dir):
        os.makedirs(log_dir, exist_ok=True)

    _LOG_FH = open(_LOG_PATH, ""w"", encoding=""utf-8"", errors=""replace"")
    _T0 = time.time()

    _log_header(out_step)

    turn_paths = _as_path_list(Turn_steps, ""Turn_steps"")
    mill_paths = _as_path_list(Mill_steps, ""Mill_steps"")
    drill_paths = _as_path_list(Drill_steps, ""Drill_steps"")

    _log(""INPUT COUNTS: TURN=%d  MILL=%d  DRILL=%d"" %
         (len(turn_paths), len(mill_paths), len(drill_paths)))

    _require_files(turn_paths, ""TURN"")
    if mill_paths:
        _require_files(mill_paths, ""MILL"")
    if drill_paths:
        _require_files(drill_paths, ""DRILL"")

    doc = App.newDocument(""NPC_FUSE_CLI_FAST_SAME"")

    # -----------------------------
    # 1) TURN fuse (fatal)
    # -----------------------------
    _log("""")
    _log(""=== TURN: FUSE ==="")

    turn_base = None

    for i, p in enumerate(turn_paths):
        fn = os.path.basename(p)
        shp = _load_step_shape(p)
        solids = _solids_list_or_throw(shp, ""TURN %s"" % fn)

        # If TURN file has >1 solids, fuse them within the file first
        if len(solids) == 1:
            shape_one = solids[0]
        else:
            base = solids[0]
            for k in range(1, len(solids)):
                base = base.fuse(solids[k])
            shape_one = _as_solids_only_shape(base, ""TURN file fused %s"" % fn)

        shape_one = _as_solids_only_shape(shape_one, ""TURN file solids-only %s"" % fn)

        if turn_base is None:
            turn_base = shape_one
        else:
            turn_base = turn_base.fuse(shape_one)
            turn_base = _as_solids_only_shape(turn_base, ""TURN running fuse"")

        if ((i + 1) % 10) == 0 or (i + 1) == len(turn_paths):
            _log(""TURN_FUSE %d/%d OK"" % (i + 1, len(turn_paths)))

    turn_base = _as_solids_only_shape(turn_base, ""TURN fused body"")

    if DO_TURN_SPLITTER_CLEANUP:
        _log(""TURN: splitter cleanup ..."")
        turn_base = _splitter_cleanup(turn_base, ""TURN"")

    # -----------------------------
    # 2) MILL gate + CUT (ONLY single-solid files)
    # -----------------------------
    _log("""")
    _log(""=== MILL: GATE + CUT ==="")

    after_mill = turn_base
    mill_export_only_solids = []  # list of shapes (extra bodies)

    for i, p in enumerate(mill_paths):
        fn = os.path.basename(p)
        shp = _load_step_shape(p)
        solids = _solids_list_or_throw(shp, ""MILL %s"" % fn)

        if len(solids) == 1:
            tool = solids[0]
            try:
                try:
                    if not _bb_overlap(after_mill.BoundBox, tool.BoundBox):
                        continue
                except Exception:
                    pass

                after_mill = after_mill.cut(tool)
                after_mill = _as_solids_only_shape(after_mill, ""POST_MILL cut"")
            except Exception:
                # match original: continue on fail
                continue
        else:
            # match original: DO NOT CUT multi-solid mill steps, export them as extra bodies
            for sld in solids:
                mill_export_only_solids.append(_as_solids_only_shape(sld, ""MILL export-only solid""))

        if ((i + 1) % 25) == 0 or (i + 1) == len(mill_paths):
            _log(""MILL_DONE %d/%d"" % (i + 1, len(mill_paths)))

    after_mill = _as_solids_only_shape(after_mill, ""POST_MILL solids-only"")

    # -----------------------------
    # 3) DRILL subtract ALL solids from each drill step
    # -----------------------------
    _log("""")
    _log(""=== DRILL: CUT (ALL SOLIDS) ==="")

    final_shape = after_mill

    for i, p in enumerate(drill_paths):
        bn = os.path.basename(p)
        shp = _load_step_shape(p)
        solids = _solids_list_or_throw(shp, ""DRILL %s"" % bn)

        for j, tool in enumerate(solids):
            try:
                try:
                    if not _bb_overlap(final_shape.BoundBox, tool.BoundBox):
                        continue
                except Exception:
                    pass

                final_shape = final_shape.cut(tool)
                final_shape = _as_solids_only_shape(final_shape, ""POST_DRILL cut"")
            except Exception:
                # match original: continue per-solid failure
                continue

        if ((i + 1) % 25) == 0 or (i + 1) == len(drill_paths):
            _log(""DRILL_DONE %d/%d"" % (i + 1, len(drill_paths)))

    final_shape = _as_solids_only_shape(final_shape, ""FINAL solids-only"")

    # Splitter cleanup at end (requested)
    if DO_FINAL_SPLITTER_CLEANUP:
        _log("""")
        _log(""FINAL: splitter cleanup ..."")
        final_shape = _splitter_cleanup(final_shape, ""FINAL"")

    # -----------------------------
    # 4) EXPORT via document objects (reliable)
    # -----------------------------
    _log("""")
    _log(""=== EXPORT STEP ==="")

    out_dir = os.path.dirname(out_step)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    export_objs = []

    obj_res = doc.addObject(""Part::Feature"", ""RESULT"")
    obj_res.Shape = final_shape
    export_objs.append(obj_res)

    # add export-only MILL solids as extra bodies
    for idx, shp in enumerate(mill_export_only_solids):
        o = doc.addObject(""Part::Feature"", ""MILL_SKIP_%04d"" % (idx + 1))
        o.Shape = shp
        export_objs.append(o)

    doc.recompute()

    Part.export(export_objs, out_step)

    if (not os.path.isfile(out_step)) or (os.path.getsize(out_step) <= 0):
        raise Exception(""OUT_STEP was not created (or is empty)."")

    _log(""EXPORT OK  bodies=%d"" % len(export_objs))
    _log(""Total time: %s"" % _fmt_secs(_elapsed()))
    _log(""STATUS: SUCCESS"")

    try:
        App.closeDocument(doc.Name)
    except Exception:
        pass


# ============================================================
# RUNNER TAIL
# ============================================================
try:
    main()
except Exception as ex:
    try:
        out_step = _norm_step_path(OUT_STEP)
        if out_step and os.path.isfile(out_step):
            try:
                os.remove(out_step)
            except Exception:
                pass
    except Exception:
        pass

    try:
        _log("""")
        _log(""============================================================"")
        _log(""=== FUSE FAILED ==="")
        _log(str(ex))
        _log(traceback.format_exc())
        _log(""STATUS: FAIL"")
        _log(""============================================================"")
        _log("""")
    except Exception:
        pass
    raise
finally:
    try:
        if _LOG_FH is not None:
            _LOG_FH.flush()
            _LOG_FH.close()
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

            // TURN is mandatory (base body). MILL + DRILL may be empty.
            if (FuseTurnFiles == null)
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseFiles is not set.");

            if (FuseMillFiles == null)
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseMillFiles is not set.");

            if (FuseDrillFiles == null)
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseDrillFiles is not set.");

            // Also enforce TURN has at least one entry (otherwise FreeCAD script cannot proceed)
            if (string.IsNullOrWhiteSpace(FuseTurnFiles))
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseFiles (TURN) is empty.");

            string pn = ProjectName.Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.AppendLine(HeadPY.TrimEnd());
            sb.AppendLine();

            sb.AppendLine($"OUT_STEP = r\"{OutStepPath}\"");
            sb.AppendLine();

            sb.AppendLine("Turn_steps = [");
            sb.AppendLine(FuseTurnFiles);   // may contain 1+ python list items
            sb.AppendLine("]");

            sb.AppendLine("Mill_steps = [");
            sb.AppendLine(FuseMillFiles);   // can be empty
            sb.AppendLine("]");

            sb.AppendLine("Drill_steps = [");
            sb.AppendLine(FuseDrillFiles);  // can be empty
            sb.AppendLine("]");

            sb.AppendLine(TailPY);

            return sb.ToString();
        }




    }












}
