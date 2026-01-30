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
# CNC_Improvements_gcode_solids : MERGE+FUSE+SUBTRACT SCRIPT (with logging)
#
# CHANGE (your request):
# - REMOVE invalid-topology testing (it did nothing for you)
# - AFTER TURN FUSE: aggressively remove splitter edges / merge coplanar faces
#   to avoid drill tools intersecting a coplanar face seam edge.
#
# Rules:
# - TURN: fuse sequentially (FATAL on fail)
# - MILL: cut ONLY when MILL step has exactly 1 solid
#         * if MILL step has >1 solids: export those solids as extra bodies (no cut)
# - DRILL: always subtract (per solid, continue on fail)
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
            _LOG_FH.flush()
    except Exception:
        pass


def _log_header(out_step):
    _log(""============================================================"")
    _log(""NPC MERGE SCRIPT START"")
    _log(""Time (local): %s"" % datetime.datetime.now().strftime(""%Y-%m-%d %H:%M:%S""))
    try:
        _log(""Time (UTC)  : %s"" % datetime.datetime.utcnow().strftime(""%Y-%m-%d %H:%M:%S""))
    except Exception:
        pass
    _log(""OUT_STEP    : %s"" % out_step)
    _log(""LOG_PATH    : %s"" % _LOG_PATH)
    _log(""Python      : %s"" % sys.version.replace(""\n"", "" ""))
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


def _solids_only(shape, label):
    if shape is None or shape.isNull():
        raise Exception(""%s: shape is null."" % label)

    solids = list(shape.Solids)
    if len(solids) == 0:
        raise Exception(""%s: result has ZERO solids."" % label)

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


def _aggressive_splitter_cleanup(shape, label):
    # Your goal: collapse coplanar face seams / remove splitter edges after TURN fuse.
    if shape is None or shape.isNull():
        return shape

    s = shape
    # removeSplitter can be order-dependent; run a few passes
    for k in range(3):
        s2 = _try_remove_splitter(s)
        if s2 is None or s2.isNull():
            break
        s = s2

    # optional: try a second strategy used by some OCCT builds (refineShape)
    # (safe: if unavailable it just skips)
    try:
        s2 = s.removeSplitter()
        if s2 and not s2.isNull():
            s = s2
    except Exception:
        pass

    # Ensure solids-only output
    s = _solids_only(s, ""%s: after splitter cleanup solids-only"" % label)
    return s


def _read_step_solids(step_path, label):
    s = _load_step_shape(step_path)
    solids = list(s.Solids)
    if len(solids) == 0:
        raise Exception(""%s: STEP contains 0 solids: %s"" % (label, step_path))
    return solids


def _fuse_solids_in_order(solids, label):
    if not solids:
        raise Exception(""%s: no solids to fuse."" % label)

    base = solids[0]
    base = _solids_only(base, ""%s: init solids-only"" % label)

    for i in range(1, len(solids)):
        base = _try_remove_splitter(base.fuse(solids[i]))
        base = _solids_only(base, ""%s: fuse step %d/%d solids-only"" % (label, i + 1, len(solids)))

    return base


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


def _shape_volume(shape):
    if shape is None or shape.isNull():
        return 0.0
    try:
        solids = list(shape.Solids)
        if solids:
            return float(sum(s.Volume for s in solids))
        return float(shape.Volume)
    except Exception:
        return 0.0


def _cut_one_logged(base, tool, label):
    if base is None or base.isNull():
        raise Exception(""%s: base is null before cut."" % label)
    if tool is None or tool.isNull():
        raise Exception(""%s: tool is null before cut."" % label)

    pre_v = _shape_volume(base)

    # intersection volume (diagnostic)
    inter_v = 0.0
    try:
        inter = base.common(tool)
        inter_v = _shape_volume(inter)
    except Exception:
        inter_v = 0.0

    # bboxes (diagnostic)
    try:
        bbA = base.BoundBox
        bbT = tool.BoundBox
        _log(""%s: BB_BASE [%.3f..%.3f, %.3f..%.3f, %.3f..%.3f]  BB_TOOL [%.3f..%.3f, %.3f..%.3f, %.3f..%.3f]"" % (
            label,
            bbA.XMin, bbA.XMax, bbA.YMin, bbA.YMax, bbA.ZMin, bbA.ZMax,
            bbT.XMin, bbT.XMax, bbT.YMin, bbT.YMax, bbT.ZMin, bbT.ZMax
        ))
    except Exception:
        pass

    _log(""%s: PRE_VOL=%.6f  INTER_VOL=%.6f"" % (label, pre_v, inter_v))

    out = _try_remove_splitter(base.cut(tool))
    out = _solids_only(out, ""%s: cut result solids-only"" % label)

    post_v = _shape_volume(out)
    _log(""%s: POST_VOL=%.6f  DELTA=%.6f"" % (label, post_v, (pre_v - post_v)))

    return out


def _export_step_objects(doc_objs, out_step_path):
    out_step_path = _norm_step_path(out_step_path)
    if not out_step_path:
        raise Exception(""OUT_STEP is empty."")

    out_dir = os.path.dirname(out_step_path)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    _log(""EXPORT: %s"" % out_step_path)
    Part.export(doc_objs, out_step_path)

    if (not os.path.isfile(out_step_path)) or (os.path.getsize(out_step_path) <= 0):
        raise Exception(""OUT_STEP was not created (or is empty)."")


# ============================================================
# USER-EMITTED VARIABLES GO HERE (your C# writes these)
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

    _log(""INPUT COUNTS:"")
    _log(""  TURN  : %d"" % len(turn_paths))
    _log(""  MILL  : %d"" % len(mill_paths))
    _log(""  DRILL : %d"" % len(drill_paths))
    _log("""")

    _require_files(turn_paths, ""TURN"")
    if mill_paths:
        _require_files(mill_paths, ""MILL"")
    if drill_paths:
        _require_files(drill_paths, ""DRILL"")

    doc = App.newDocument(""NPC_MERGE"")

    fuse_ok = 0
    fuse_fail = 0

    mill_gate_cut = 0
    mill_gate_export_only = 0
    mill_cut_ok = 0
    mill_cut_fail = 0
    mill_cut_skipped = []
    mill_export_only_count = 0

    drill_read_ok = 0
    drill_read_fail = 0
    drill_cut_ok = 0
    drill_cut_fail = 0
    drill_cut_skipped = []

    # -----------------------------
    # 1) TURN fuse sequentially (FATAL on fail)
    # -----------------------------
    _log(""=== TURN: FUSE (sequential order) ==="")
    turn_base = None

    for i, p in enumerate(turn_paths):
        t1 = time.time()
        fn = os.path.basename(p)

        try:
            solids = _read_step_solids(p, ""TURN"")
            if len(solids) == 1:
                shape_one = solids[0]
            else:
                shape_one = _fuse_solids_in_order(solids, ""TURN file fuse (%s)"" % fn)

            if turn_base is None:
                turn_base = _solids_only(shape_one, ""TURN init"")
            else:
                turn_base = _try_remove_splitter(turn_base.fuse(shape_one))
                turn_base = _solids_only(turn_base, ""TURN fuse running"")

            fuse_ok += 1
            _log(""TURN_FUSE %d/%d OK   %s   (%s)"" %
                 (i + 1, len(turn_paths), fn, _fmt_secs(time.time() - t1)))

        except Exception as ex:
            fuse_fail += 1
            _log(""TURN_FUSE %d/%d FAIL %s   (%s)"" %
                 (i + 1, len(turn_paths), fn, _fmt_secs(time.time() - t1)))
            _log(""  ERROR: %s"" % str(ex))
            _log(traceback.format_exc())
            raise

    # >>> YOUR REQUEST: cleanup coplanar face seams AFTER TURN FUSE
    _log(""TURN: SPLITTER CLEANUP (aggressive) ..."")
    t_sc = time.time()
    turn_base = _aggressive_splitter_cleanup(turn_base, ""TURN fused body"")
    _log(""TURN: SPLITTER CLEANUP DONE (%s)"" % _fmt_secs(time.time() - t_sc))

    turn_base = _solids_only(turn_base, ""TURN fused body"")
    _log(""TURN: DONE"")
    _log("""")

    # -----------------------------
    # 2) MILL gate by solid count
    # -----------------------------
    _log(""=== MILL: ANALYZE / GATE BY SOLID COUNT ==="")
    mill_export_only = []  # (solid, name)
    mill_cut_tools = []    # (solid, filename)

    for i, p in enumerate(mill_paths):
        fn = os.path.basename(p)
        t1 = time.time()

        solids = _read_step_solids(p, ""MILL"")
        n = len(solids)
        stem = os.path.splitext(fn)[0]

        if n == 1:
            mill_cut_tools.append((solids[0], fn))
            mill_gate_cut += 1
            _log(""MILL_GATE %d/%d CUT         solids=%d  %s   (%s)"" %
                 (i + 1, len(mill_paths), n, fn, _fmt_secs(time.time() - t1)))
        else:
            for j, sld in enumerate(solids):
                nm = ""MILL_SKIP_%03d_%03d_%s"" % (i + 1, j + 1, stem)
                mill_export_only.append((sld, nm))
            mill_export_only_count += n
            mill_gate_export_only += 1
            _log(""MILL_GATE %d/%d EXPORT_ONLY solids=%d  %s   (%s)"" %
                 (i + 1, len(mill_paths), n, fn, _fmt_secs(time.time() - t1)))

    _log("""")
    _log(""=== MILL: CUT (sequential, only single-solid files) ==="")
    after_mill = turn_base

    for i, (tool, fn) in enumerate(mill_cut_tools):
        t1 = time.time()
        label = ""MILL cut %s (%d/%d)"" % (fn, i + 1, len(mill_cut_tools))

        try:
            ov = _bb_overlap(after_mill.BoundBox, tool.BoundBox)
            _log(""MILL_BB %d/%d %s  overlap=%s"" % (i + 1, len(mill_cut_tools), fn, str(ov)))
            if not ov:
                mill_cut_skipped.append(""%s (SKIP_NO_BB_OVERLAP)"" % fn)
                _log(""MILL_CUT %d/%d SKIP_NO_BB_OVERLAP %s   (%s)"" %
                     (i + 1, len(mill_cut_tools), fn, _fmt_secs(time.time() - t1)))
                continue
        except Exception:
            pass

        try:
            after_mill = _cut_one_logged(after_mill, tool, label)
            mill_cut_ok += 1
            _log(""MILL_CUT %d/%d OK   %s   (%s)"" %
                 (i + 1, len(mill_cut_tools), fn, _fmt_secs(time.time() - t1)))
        except Exception as ex:
            mill_cut_fail += 1
            mill_cut_skipped.append(""%s (FAIL: %s)"" % (fn, str(ex)))
            _log(""MILL_CUT %d/%d FAIL %s   (%s)"" %
                 (i + 1, len(mill_cut_tools), fn, _fmt_secs(time.time() - t1)))
            _log(""  ERROR: %s"" % str(ex))
            _log(traceback.format_exc())
            continue

    after_mill = _solids_only(after_mill, ""POST_MILL solids-only"")
    _log(""MILL: DONE"")
    _log("""")

    # -----------------------------
    # 3) DRILL subtract (SEQUENTIAL per STEP, per solid)
    # -----------------------------
    _log(""=== DRILL: CUT (sequential per file / per solid) ==="")
    final_shape = after_mill

    if drill_paths:
        for i, p in enumerate(drill_paths):
            bn = os.path.basename(p)
            t_read = time.time()

            try:
                solids = _read_step_solids(p, ""DRILL"")
                drill_read_ok += 1
                _log(""DRILL_READ %d/%d OK   %s   solids=%d   (%s)"" %
                     (i + 1, len(drill_paths), bn, len(solids), _fmt_secs(time.time() - t_read)))
            except Exception as ex:
                drill_read_fail += 1
                _log(""DRILL_READ %d/%d FAIL %s   (%s)"" %
                     (i + 1, len(drill_paths), bn, _fmt_secs(time.time() - t_read)))
                _log(""  ERROR: %s"" % str(ex))
                _log(traceback.format_exc())
                raise

            for j, tool in enumerate(solids):
                t1 = time.time()
                label = ""DRILL cut %s solid %d/%d"" % (bn, j + 1, len(solids))

                try:
                    ov = _bb_overlap(final_shape.BoundBox, tool.BoundBox)
                    _log(""DRILL_BB %d/%d.%d/%d %s overlap=%s"" %
                         (i + 1, len(drill_paths), j + 1, len(solids), bn, str(ov)))
                    if not ov:
                        drill_cut_skipped.append(""%s solid %d/%d (SKIP_NO_BB_OVERLAP)"" %
                                                 (bn, j + 1, len(solids)))
                        _log(""DRILL_CUT  %d/%d.%d/%d SKIP_NO_BB_OVERLAP %s   (%s)"" %
                             (i + 1, len(drill_paths), j + 1, len(solids), bn, _fmt_secs(time.time() - t1)))
                        continue
                except Exception:
                    pass

                try:
                    final_shape = _cut_one_logged(final_shape, tool, label)
                    drill_cut_ok += 1
                    _log(""DRILL_CUT  %d/%d.%d/%d OK   %s   (%s)"" %
                         (i + 1, len(drill_paths), j + 1, len(solids), bn, _fmt_secs(time.time() - t1)))
                except Exception as ex:
                    drill_cut_fail += 1
                    drill_cut_skipped.append(""%s solid %d/%d (FAIL: %s)"" %
                                             (bn, j + 1, len(solids), str(ex)))
                    _log(""DRILL_CUT  %d/%d.%d/%d FAIL %s   (%s)"" %
                         (i + 1, len(drill_paths), j + 1, len(solids), bn, _fmt_secs(time.time() - t1)))
                    _log(""  ERROR: %s"" % str(ex))
                    _log(traceback.format_exc())
                    continue
    else:
        _log(""DRILL: no drill steps -> skipping cut"")

    final_shape = _solids_only(final_shape, ""FINAL solids-only"")
    _log(""DRILL: DONE"")
    _log("""")

    # -----------------------------
    # 4) Create export objects
    # -----------------------------
    _log(""=== EXPORT OBJECTS ==="")
    export_objs = []

    obj_res = doc.addObject(""Part::Feature"", ""RESULT"")
    obj_res.Shape = final_shape
    export_objs.append(obj_res)

    if mill_export_only:
        _log(""EXPORT_ONLY: adding %d skipped MILL solids as extra bodies"" % len(mill_export_only))
        for (shp, nm) in mill_export_only:
            o = doc.addObject(""Part::Feature"", nm[:60])
            o.Shape = _solids_only(shp, ""%s solids-only"" % nm)
            export_objs.append(o)

    doc.recompute()

    # -----------------------------
    # 5) Export STEP + verify
    # -----------------------------
    _log("""")
    _log(""=== EXPORT STEP ==="")
    t1 = time.time()
    _export_step_objects(export_objs, out_step)
    _log(""EXPORT OK   (%s)"" % _fmt_secs(time.time() - t1))

    try:
        App.closeDocument(doc.Name)
    except Exception:
        pass

    # Summary
    _log("""")
    _log(""============================================================"")
    _log(""=== SUMMARY ==="")
    _log(""TURN fuse OK   : %d"" % fuse_ok)
    _log(""TURN fuse FAIL : %d"" % fuse_fail)
    _log("""")
    _log(""MILL gated CUT files        : %d"" % mill_gate_cut)
    _log(""MILL gated EXPORT_ONLY files: %d"" % mill_gate_export_only)
    _log(""MILL CUT OK    : %d"" % mill_cut_ok)
    _log(""MILL CUT FAIL  : %d"" % mill_cut_fail)
    _log(""MILL extra exported bodies (skipped cut due to multi-solid): %d"" % mill_export_only_count)
    if mill_cut_skipped:
        _log(""MILL SKIPPED TOOLS:"")
        for s in mill_cut_skipped:
            _log(""  "" + s)
    _log("""")
    _log(""DRILL READ OK  : %d"" % drill_read_ok)
    _log(""DRILL READ FAIL: %d"" % drill_read_fail)
    _log(""DRILL CUT OK   : %d"" % drill_cut_ok)
    _log(""DRILL CUT FAIL : %d"" % drill_cut_fail)
    if drill_cut_skipped:
        _log(""DRILL SKIPPED TOOLS:"")
        for s in drill_cut_skipped:
            _log(""  "" + s)
    _log("""")
    try:
        _log(""OUT_STEP size: %d bytes"" % os.path.getsize(out_step))
    except Exception:
        pass
    _log(""Total time: %s"" % _fmt_secs(_elapsed()))
    _log(""STATUS: SUCCESS"")
    _log(""============================================================"")
    _log("""")


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
        _log(""=== MERGE FAILED ==="")
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

            if (string.IsNullOrWhiteSpace(FuseTurnFiles))
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseFiles is not set.");

            if (string.IsNullOrWhiteSpace(FuseMillFiles))
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseMillFiles is not set.");

            if (string.IsNullOrWhiteSpace(FuseDrillFiles))
                throw new InvalidOperationException("FreeCadScriptExportAll.FuseDrillFiles is not set.");





            string pn = ProjectName.Replace("\"", "\\\"");

            var sb = new StringBuilder();
            sb.AppendLine(HeadPY.TrimEnd());
            sb.AppendLine();
            // sb.AppendLine($"PROJECT_NAME = \"{pn}\"");
            sb.AppendLine($"OUT_STEP = r\"{OutStepPath}\"");
            sb.AppendLine();
            sb.AppendLine("Turn_steps = [");
            sb.AppendLine(FuseTurnFiles);
            sb.AppendLine("]");

            sb.AppendLine("Mill_steps = [");
            sb.AppendLine(FuseMillFiles);
            sb.AppendLine("]");

            sb.AppendLine("Drill_steps = [");
            sb.AppendLine(FuseDrillFiles);
            sb.AppendLine("]");



            sb.AppendLine(TailPY);




            return sb.ToString();
        }



    }












}
