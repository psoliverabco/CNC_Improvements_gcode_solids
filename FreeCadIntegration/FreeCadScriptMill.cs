namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Holds the embedded Python strings and the dynamic values
    /// for mill tool-sweep geometry (toolpath → solids in FreeCAD).
    /// </summary>
    internal static class FreeCadScriptMill
    {
        // Python header: imports, documentation, banner.
        // NOTE: C# will typically prepend something like:
        //   output_step = r"C:\path\to\my_shape_stp.stp"
        // BEFORE this header, so the tail can use `output_step`.
        internal const string HeadPY = @"
import FreeCAD as App
import Part
import math
import os

# ------------------------------------------------------------
# MERGE CONTROL
# ------------------------------------------------------------
# If True: try to fuse segments into a running base to reduce solid count.
# If a fuse fails: finalize current base as a group, start a new base from this segment.
MergeAll = True

# If True: run removeSplitter() on each merged group at the end (can be slower)
MergeRemoveSplitterAtEnd = True

# ------------------------------------------------------------
# TRANSFORM (Injected example)
# Order: RotZ -> RotY -> Translate
# Example: RotZ=45, RotY=180, Tz=-150
# ------------------------------------------------------------
#TRANSFORM_ROTZ = 45.0
#TRANSFORM_ROTY = 180.0
#TRANSFORM_TX = 0.0
#TRANSFORM_TY = 0.0
#TRANSFORM_TZ = -150.0

";

        // Injected into the generated .py as Python literals: True / False
        public static string Fuseall = "True";
        public static string RemoveSplitter = "True";


        // Holds the raw mill shape text (TDIA/TLENGTH/ZPLANE + LINE/ARC3_*).
        public static string MillShape = "Not yet set MillShape";

        // Holds the Transform script
        //TRANSFORM_ROTZ = 45.0
        //TRANSFORM_ROTY = 180.0
        //TRANSFORM_TX = 0.0
        //TRANSFORM_TY = 0.0
        //TRANSFORM_TZ = -150.0
        public static string TransPY = @"
TRANSFORM_ROTZ = 45.0
TRANSFORM_ROTY = 180.0
TRANSFORM_TX = 0.0
TRANSFORM_TY = 0.0
TRANSFORM_TZ = -150.0
";

        // Tail: parses TOOLPATH_TEXT, builds segment solids, exports STEP.
        internal const string TailPY = @"


_rotZ = App.Rotation(App.Vector(0, 0, 1), TRANSFORM_ROTZ)
_rotY = App.Rotation(App.Vector(0, 1, 0), TRANSFORM_ROTY)
_rot = _rotY.multiply(_rotZ)  # RotZ first, then RotY
_trans = App.Vector(TRANSFORM_TX, TRANSFORM_TY, TRANSFORM_TZ)

PlacementTransform = App.Placement(_trans, _rot)

# ------------------------------------------------------------
# Helper: parse the embedded block
# ------------------------------------------------------------
def parse_toolpath(text):
    tool_dia = 10.0
    tool_len = 10.0
    z_plane = 0.0
    segments = []

    for raw in text.splitlines():
        line = raw.strip()
        if not line or line.startswith(""#""):
            continue

        parts = line.split()
        key = parts[0].upper()

        if key == ""TDIA"" and len(parts) >= 2:
            tool_dia = float(parts[1])
        elif key == ""TLENGTH"" and len(parts) >= 2:
            tool_len = float(parts[1])
        elif key == ""ZPLANE"" and len(parts) >= 2:
            z_plane = float(parts[1])
        elif key == ""LINE"" and len(parts) >= 5:
            x1, y1, x2, y2 = map(float, parts[1:5])
            segments.append((""LINE"", (x1, y1, x2, y2)))
        elif key in (""ARC3_CW"", ""ARC3_CCW"") and len(parts) >= 7:
            x1, y1, xm, ym, x2, y2 = map(float, parts[1:7])
            segments.append((key, (x1, y1, xm, ym, x2, y2)))
        else:
            pass

    return tool_dia, tool_len, z_plane, segments


def make_line_sweep(x1, y1, x2, y2, tool_rad, tool_len, z_plane):
    p1 = App.Vector(x1, y1, z_plane)
    p2 = App.Vector(x2, y2, z_plane)

    dx = p2.x - p1.x
    dy = p2.y - p1.y
    seg_len = math.hypot(dx, dy)
    if seg_len < 1e-9:
        raise ValueError(""Segment length is zero for LINE."")

    # left normal relative to p1->p2
    nx = -(dy / seg_len)
    ny = (dx / seg_len)

    p1L = App.Vector(p1.x + nx * tool_rad, p1.y + ny * tool_rad, z_plane)
    p2L = App.Vector(p2.x + nx * tool_rad, p2.y + ny * tool_rad, z_plane)
    p2R = App.Vector(p2.x - nx * tool_rad, p2.y - ny * tool_rad, z_plane)
    p1R = App.Vector(p1.x - nx * tool_rad, p1.y - ny * tool_rad, z_plane)

    wire = Part.makePolygon([p1L, p2L, p2R, p1R, p1L])
    face = Part.Face(wire)

    band = face.extrude(App.Vector(0.0, 0.0, tool_len))

    cyl1 = Part.makeCylinder(tool_rad, tool_len, p1, App.Vector(0.0, 0.0, 1.0))
    cyl2 = Part.makeCylinder(tool_rad, tool_len, p2, App.Vector(0.0, 0.0, 1.0))

    sweep = band.fuse(cyl1)
    sweep = sweep.fuse(cyl2)
    return sweep


def radial_point(base_pt, center, scale_r):
    vx = base_pt.x - center.x
    vy = base_pt.y - center.y
    vz = base_pt.z - center.z
    base_len = math.sqrt(vx * vx + vy * vy + vz * vz)
    if base_len < 1e-9:
        raise ValueError(""Base point coincides with center."")
    factor = scale_r / base_len
    return App.Vector(center.x + vx * factor,
                      center.y + vy * factor,
                      center.z + vz * factor)


def make_arc_band_sweep(x1, y1, xm, ym, x2, y2, tool_rad, tool_len, z_plane):
    p1 = App.Vector(x1, y1, z_plane)
    pm = App.Vector(xm, ym, z_plane)
    p2 = App.Vector(x2, y2, z_plane)

    cl_arc = Part.Arc(p1, pm, p2)
    cl_edge = cl_arc.toShape()
    circ = cl_edge.Curve
    center = circ.Center
    r = circ.Radius

    if r <= tool_rad:
        raise ValueError(""Arc radius <= tool radius in band builder."")

    outer_r = r + tool_rad
    inner_r = r - tool_rad

    p1_outer = radial_point(p1, center, outer_r)
    pm_outer = radial_point(pm, center, outer_r)
    p2_outer = radial_point(p2, center, outer_r)

    p1_inner = radial_point(p1, center, inner_r)
    pm_inner = radial_point(pm, center, inner_r)
    p2_inner = radial_point(p2, center, inner_r)

    outer_arc = Part.Arc(p1_outer, pm_outer, p2_outer)
    inner_arc = Part.Arc(p1_inner, pm_inner, p2_inner)

    outer_edge = outer_arc.toShape()
    inner_edge = inner_arc.toShape()

    inner_edge_rev = inner_edge.copy()
    inner_edge_rev.reverse()

    join1 = Part.makeLine(p2_outer, p2_inner)
    join2 = Part.makeLine(p1_inner, p1_outer)

    band_wire = Part.Wire([outer_edge, join1, inner_edge_rev, join2])
    band_face = Part.Face(band_wire)

    solid_arc = band_face.extrude(App.Vector(0.0, 0.0, tool_len))

    cyl1 = Part.makeCylinder(tool_rad, tool_len, p1, App.Vector(0.0, 0.0, 1.0))
    cyl2 = Part.makeCylinder(tool_rad, tool_len, p2, App.Vector(0.0, 0.0, 1.0))

    sweep = solid_arc.fuse(cyl1)
    sweep = sweep.fuse(cyl2)
    return sweep


def make_arc_small_pie(x1, y1, xm, ym, x2, y2, tool_rad, tool_len, z_plane):
    p1 = App.Vector(x1, y1, z_plane)
    pm = App.Vector(xm, ym, z_plane)
    p2 = App.Vector(x2, y2, z_plane)

    cl_arc = Part.Arc(p1, pm, p2)
    cl_edge = cl_arc.toShape()
    circ = cl_edge.Curve
    center = circ.Center
    r_small = circ.Radius

    if r_small <= 0.0:
        raise ValueError(""CL arc radius non-positive in small-arc builder."")

    big_r = r_small + tool_rad
    if big_r <= 0.0:
        raise ValueError(""Big radius (r_small + tool_rad) non-positive."")

    p_big1 = radial_point(p1, center, big_r)
    p_bigm = radial_point(pm, center, big_r)
    p_big2 = radial_point(p2, center, big_r)

    big_arc = Part.Arc(p_big1, p_bigm, p_big2)
    big_edge = big_arc.toShape()

    line1 = Part.makeLine(center, p_big1)
    line2 = Part.makeLine(p_big2, center)

    sector_wire = Part.Wire([line1, big_edge, line2])
    sector_face = Part.Face(sector_wire)

    pie_solid = sector_face.extrude(App.Vector(0.0, 0.0, tool_len))
    return pie_solid


def main():
    # Ensure output folder exists
    out_dir = os.path.dirname(output_step)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    doc = App.newDocument(""ToolSweep_AllSegments"")

    tool_dia, tool_len, z_plane, segments = parse_toolpath(TOOLPATH_TEXT)
    tool_rad = tool_dia / 2.0

    print(""Parsed:"")
    print(""  ToolDia  ="", tool_dia)
    print(""  ToolRad  ="", tool_rad)
    print(""  ToolLen  ="", tool_len)
    print(""  ZPlane   ="", z_plane)
    print(""  Segments ="", len(segments))
    print(""  MergeAll ="", MergeAll)
    print(""Transform:"")
    print(""  RotZ ="", TRANSFORM_ROTZ, ""deg"")
    print(""  RotY ="", TRANSFORM_ROTY, ""deg"")
    print(""  T    ="", (TRANSFORM_TX, TRANSFORM_TY, TRANSFORM_TZ))

    seg_objects = []

    merged_groups = []
    current_merged = None

    for idx, (stype, data) in enumerate(segments):
        try:
            if stype == ""LINE"":
                x1, y1, x2, y2 = data
                shape = make_line_sweep(x1, y1, x2, y2, tool_rad, tool_len, z_plane)
                name = ""Seg_{:02d}_LINE"".format(idx)

            elif stype in (""ARC3_CW"", ""ARC3_CCW""):
                x1, y1, xm, ym, x2, y2 = data

                p1 = App.Vector(x1, y1, z_plane)
                pm = App.Vector(xm, ym, z_plane)
                p2 = App.Vector(x2, y2, z_plane)

                cl_arc = Part.Arc(p1, pm, p2)
                cl_edge = cl_arc.toShape()
                circ = cl_edge.Curve
                r = circ.Radius

                if r > tool_rad:
                    shape = make_arc_band_sweep(x1, y1, xm, ym, x2, y2, tool_rad, tool_len, z_plane)
                    name = ""Seg_{:02d}_{}_BAND"".format(idx, stype)
                else:
                    shape = make_arc_small_pie(x1, y1, xm, ym, x2, y2, tool_rad, tool_len, z_plane)
                    name = ""Seg_{:02d}_{}_SMALLPIE"".format(idx, stype)

            else:
                print(""Skipping unknown type:"", stype)
                continue

            if not MergeAll:
                obj = doc.addObject(""Part::Feature"", name)
                obj.Shape = shape
                seg_objects.append(obj)
                print(""Created segment:"", name)
            else:
                if current_merged is None:
                    current_merged = shape
                    print(""Merge start:"", name)
                else:
                    try:
                        current_merged = current_merged.fuse(shape)
                        print(""Merged:"", name)
                    except Exception as me:
                        print(""MERGE_SPLIT -> new base:"", name, ""::"", me)
                        merged_groups.append(current_merged)
                        current_merged = shape

        except Exception as e:
            print(""Error building segment {} ({}): {}"".format(idx, stype, e))

    # Finalize merged groups into doc objects
    if MergeAll:
        if current_merged is not None:
            merged_groups.append(current_merged)

        seg_objects = []
        for gi, sh in enumerate(merged_groups, start=1):
            if MergeRemoveSplitterAtEnd:
                try:
                    sh = sh.removeSplitter()
                except Exception:
                    pass

            obj = doc.addObject(""Part::Feature"", ""Merged_{:03d}"".format(gi))
            obj.Shape = sh
            seg_objects.append(obj)

    doc.recompute()

    # ------------------------------------------------------------
    # APPLY TRANSFORM TO FINAL OBJECTS (after merge + removeSplitter)
    # ------------------------------------------------------------
    for o in seg_objects:
        o.Placement = PlacementTransform.multiply(o.Placement)

    doc.recompute()

    # Export
    if not seg_objects:
        print(""MILL_FAIL: no solids created"")
        return 3

    try:
        Part.export(seg_objects, output_step)
        print(""Exported STEP:"", output_step)
        print(""MILL_OK:"", output_step)
        return 0
    except Exception as ex:
        print(""MILL_EXPORT_FAIL:"", ex)
        return 5


raise SystemExit(main())




";
    }
}
