namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Holds the embedded Python strings and the dynamic values
    /// for mill tool-sweep geometry (toolpath → solids in FreeCAD).
    /// </summary>
    internal static class FreeCadScriptMillClipper
    {
        // Python header: imports, documentation, banner.
        // NOTE: C# will typically prepend something like:
        //   output_step = r"C:\path\to\my_shape_stp.stp"
        // BEFORE this header, so the tail can use `output_step`.
        internal const string HeadPY = @"
# ------------------------------------------------------------
# Clipper LOOP (OUTER/ISLAND) -> WIRE -> EXTRUDE OUTER -> CUT ISLANDS -> TRANSFORM -> STEP
# ------------------------------------------------------------

import FreeCAD as App
import Part
import os

# ------------------------------------------------------------
# INPUT (paste your LOOP text here)
# New format supported:
#   LINE x1 y1   x2 y2
#   ARC  x1 y1   xm ym   x2 y2
# (No ARC3_CW/ARC3_CCW required)
# ------------------------------------------------------------

# If True: run removeSplitter() on each merged group at the end (can be slower)
CutRemoveSplitterAtEnd = False

";

        // Injected into the generated .py as Python literals: True / False

        public static string CutRemoveSplitterAtEnd = "False";

        // Holds the mill shape text from clipper processing
        // (TDIA/TLENGTH/ZPLANE + LINE x1 y1   x2 y2, ARC  x1 y1   xm ym   x2 y2).
        public static string MillShapeClipper = "Not yet set MillShapeClipper";

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
# Helpers
# ------------------------------------------------------------
TOL = 1e-7

def _dist2(ax, ay, bx, by):
    dx = ax - bx
    dy = ay - by
    return dx * dx + dy * dy

def point_in_poly(x, y, poly_xy):
    inside = False
    n = len(poly_xy)
    if n < 3:
        return False
    j = n - 1
    for i in range(n):
        xi, yi = poly_xy[i]
        xj, yj = poly_xy[j]
        denom = (yj - yi)
        if abs(denom) < 1e-20:
            denom = 1e-20
        intersect = ((yi > y) != (yj > y)) and (x < (xj - xi) * (y - yi) / denom + xi)
        if intersect:
            inside = not inside
        j = i
    return inside

def _bake_placement_into_shape(shape, placement):
    """"""
    Bake placement into B-rep geometry.
    This avoids relying on obj.Placement being exported as an occurrence transform in STEP.
    """"""
    m = placement.toMatrix()

    # Strategy:
    #  1) Prefer transformGeometry (returns a new shape on many builds)
    #  2) Fallback to transformShape
    #  3) Final fallback: copy + in-place transformGeometry if available
    try:
        out = shape.transformGeometry(m)
        if out is not None:
            return out
    except Exception:
        pass

    try:
        out = shape.transformShape(m, True)  # (matrix, copy)
        if out is not None:
            return out
    except Exception:
        pass

    # last resort
    out = shape.copy()
    try:
        tg = out.transformGeometry(m)
        if tg is not None:
            out = tg
    except Exception:
        # If even this fails, return the copy unchanged (better than crash)
        pass
    return out

# --------------------------------------------------------------------
# Parse new embedded loop code:
#   LINE x1 y1   x2 y2
#   ARC  x1 y1   xm ym   x2 y2
# We map ARC -> (""ARC3_CCW"", data) so build_edges() stays unchanged.
# --------------------------------------------------------------------
def parse_loops(text):
    tool_len = None
    z_plane = None

    loops = []  # [{type:'OUTER'/'ISLAND', id:int, segs:[(stype,data)], poly:[(x,y),...]}]
    cur = None

    for raw in text.splitlines():
        line = raw.strip()
        if not line:
            continue

        up = line.upper()

        if up.startswith(""TLENGTH""):
            parts = line.split()
            if len(parts) >= 2:
                tool_len = float(parts[1])
            continue

        if up.startswith(""ZPLANE""):
            parts = line.split()
            if len(parts) >= 2:
                z_plane = float(parts[1])
            continue

        if line.startswith(""---"") and ""LOOP"" in up:
            t = ""OUTER"" if ""OUTER"" in up else (""ISLAND"" if ""ISLAND"" in up else ""UNKNOWN"")
            loop_id = -1
            try:
                parts = up.replace(""-"", "" "").split()
                k = parts.index(""LOOP"")
                loop_id = int(parts[k + 1])
            except Exception:
                loop_id = len(loops)

            cur = {""type"": t, ""id"": loop_id, ""segs"": [], ""poly"": []}
            loops.append(cur)
            continue

        if cur is None:
            continue

        parts = line.split()
        if not parts:
            continue

        key = parts[0].upper()

        if key == ""LINE"" and len(parts) >= 5:
            x1, y1, x2, y2 = map(float, parts[1:5])
            cur[""segs""].append((""LINE"", (x1, y1, x2, y2)))
            cur[""poly""].append((x1, y1))
            continue

        if key == ""ARC"" and len(parts) >= 7:
            x1, y1, xm, ym, x2, y2 = map(float, parts[1:7])
            cur[""segs""].append((""ARC3_CCW"", (x1, y1, xm, ym, x2, y2)))
            cur[""poly""].append((x1, y1))
            continue

    if tool_len is None:
        raise ValueError(""Missing TLENGTH"")
    if z_plane is None:
        raise ValueError(""Missing ZPLANE"")
    if not loops:
        raise ValueError(""No LOOP blocks found"")

    # generator guarantees closed; remove duplicate last point if present
    for L in loops:
        poly = L[""poly""]
        if len(poly) >= 2:
            if _dist2(poly[0][0], poly[0][1], poly[-1][0], poly[-1][1]) <= 1e-12:
                poly.pop()

    return tool_len, z_plane, loops

def build_edges(loop, z_plane):
    edges = []
    for stype, data in loop[""segs""]:
        if stype == ""LINE"":
            x1, y1, x2, y2 = data
            if _dist2(x1, y1, x2, y2) <= (TOL * TOL):
                continue
            p1 = App.Vector(x1, y1, z_plane)
            p2 = App.Vector(x2, y2, z_plane)
            edges.append(Part.makeLine(p1, p2))

        elif stype in (""ARC3_CW"", ""ARC3_CCW""):
            x1, y1, xm, ym, x2, y2 = data
            p1 = App.Vector(x1, y1, z_plane)
            pm = App.Vector(xm, ym, z_plane)
            p2 = App.Vector(x2, y2, z_plane)
            edges.append(Part.Arc(p1, pm, p2).toShape())

    if not edges:
        raise ValueError(""Loop has no usable edges: {} {}"".format(loop[""type""], loop[""id""]))

    return edges

def build_wire(edges):
    try:
        return Part.Wire(edges)
    except Exception:
        sorted_sets = Part.sortEdges(edges)
        best = None
        best_len = -1
        for s in sorted_sets:
            if len(s) > best_len:
                best = s
                best_len = len(s)
        if not best:
            raise
        return Part.Wire(best)

def extrude_wire_to_solid(wire, tool_len):
    face = Part.Face(wire)
    return face.extrude(App.Vector(0.0, 0.0, tool_len))

def main():
    out_dir = os.path.dirname(output_step)
    if out_dir and not os.path.isdir(out_dir):
        os.makedirs(out_dir, exist_ok=True)

    tool_len, z_plane, loops = parse_loops(CLIPPER_TEXT)

    outers = [L for L in loops if L[""type""] == ""OUTER""]
    islands = [L for L in loops if L[""type""] == ""ISLAND""]

    if not outers:
        raise ValueError(""No OUTER loop found"")

    print(""Parsed:"")
    print(""  TLENGTH ="", tool_len)
    print(""  ZPLANE  ="", z_plane)
    print(""  OUTER   ="", len(outers))
    print(""  ISLAND  ="", len(islands))
    print(""Transform (to be baked into B-rep at end):"")
    print(""  RotZ ="", TRANSFORM_ROTZ, ""deg"")
    print(""  RotY ="", TRANSFORM_ROTY, ""deg"")
    print(""  T    ="", (TRANSFORM_TX, TRANSFORM_TY, TRANSFORM_TZ))

    doc = App.newDocument(""Clipper_OuterMinusIslands_Baked"")

    # Build outer polygons for containment assignment (supports multiple OUTER)
    outer_polys = []
    for o in outers:
        if len(o[""poly""]) < 3:
            raise ValueError(""OUTER loop polygon too small (need 3+ verts). Loop id={}"".format(o[""id""]))
        outer_polys.append((o, o[""poly""]))

    islands_by_outer = {id(o): [] for o in outers}

    for isl in islands:
        poly = isl[""poly""]
        if len(poly) < 3:
            continue
        tx, ty = poly[0][0], poly[0][1]
        assigned = False
        for o, opoly in outer_polys:
            if point_in_poly(tx, ty, opoly):
                islands_by_outer[id(o)].append(isl)
                assigned = True
                break
        if not assigned:
            print(""WARN: island not inside any outer -> ignored. Island loop:"", isl[""id""])

    final_objs = []

    for oi, o in enumerate(outers, start=1):
        outer_edges = build_edges(o, z_plane)
        outer_wire = build_wire(outer_edges)
        outer_solid = extrude_wire_to_solid(outer_wire, tool_len)

        for isl in islands_by_outer.get(id(o), []):
            isl_edges = build_edges(isl, z_plane)
            isl_wire = build_wire(isl_edges)
            isl_solid = extrude_wire_to_solid(isl_wire, tool_len)
            outer_solid = outer_solid.cut(isl_solid)

        if CutRemoveSplitterAtEnd:
            try:
                outer_solid = outer_solid.removeSplitter()
            except Exception:
                pass

        # ------------------------------------------------------------
        # BAKE TRANSFORM INTO GEOMETRY HERE (no obj.Placement)
        # ------------------------------------------------------------
        outer_solid = _bake_placement_into_shape(outer_solid, PlacementTransform)

        obj = doc.addObject(""Part::Feature"", ""Outer_{:03d}"".format(oi))
        obj.Shape = outer_solid
        final_objs.append(obj)

    doc.recompute()

    if not final_objs:
        print(""FAIL: no solids created"")
        return 3

    try:
        Part.export(final_objs, output_step)
        print(""Exported STEP:"", output_step)
        return 0
    except Exception as ex:
        print(""EXPORT_FAIL:"", ex)
        return 5

raise SystemExit(main())





";
    }
}
