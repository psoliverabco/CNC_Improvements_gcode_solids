namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Holds the embedded Python strings and the dynamic paths
    /// for profile.txt and STEP output.
    /// </summary>
    internal static class FreeCadScript
    {
        // Fixed head of the Python file
        internal const string HeadPY = @"
import FreeCAD
import Part
import os
import math

# ------------------------------------------------------------
# CONFIG
# ------------------------------------------------------------";

        // These will be set from WPF (TurningPage export)
        public static string ProfilePth = "#Not yet set profilepath";
        public static string StepPth = "#Not yet set step";

        // Embedded profile text (TurningPage builds this)
        public static string Profile = "#Not yet set step";

        // NEW: sewing / snap tolerance (mm)
        // Set from WPF: Properties.Settings.Default.SewTol (Invariant).
        public static string SewTolPY = @"
SEW_TOL = 0.001
";

        // Fixed body of the Python file (kept as-is)
        internal const string BodyPY = @"
# ------------------------------------------------------------
# TRANSFORM (Injected example)
# Order: RotZ -> RotY -> Translate
# Example: RotZ=45, RotY=180, Tz=-150
# ------------------------------------------------------------
# TRANSFORM_ROTZ = 45.0
# TRANSFORM_ROTY = 180.0
# TRANSFORM_TX = 0.0
# TRANSFORM_TY = 0.0
# TRANSFORM_TZ = -150.0
";

        // Transform string injected from WPF
        public static string TransPY = @"
TRANSFORM_ROTZ = 45.0
TRANSFORM_ROTY = 180.0
TRANSFORM_TX = 0.0
TRANSFORM_TY = 0.0
TRANSFORM_TZ = -150.0
";

        internal const string TailPy = @"

_rotZ = FreeCAD.Rotation(FreeCAD.Vector(0, 0, 1), TRANSFORM_ROTZ)
_rotY = FreeCAD.Rotation(FreeCAD.Vector(0, 1, 0), TRANSFORM_ROTY)
_rot = _rotY.multiply(_rotZ)  # RotZ first, then RotY
_trans = FreeCAD.Vector(TRANSFORM_TX, TRANSFORM_TY, TRANSFORM_TZ)

PlacementTransform = FreeCAD.Placement(_trans, _rot)

# ------------------------------------------------------------
# PARSE PROFILE.TXT (embedded string)
# Supported syntax:
#   LINE    Xs Zs   Xe Ze
#   ARC3_CW  Xs Zs   Xm Zm   Xe Ze
#   ARC3_CCW Xs Zs   Xm Zm   Xe Ze
# ------------------------------------------------------------
def parse_profile_text(profile_text: str):
    segs = []
    print(""=========================================="")
    print("" Loading embedded latheProfile"")
    print(""=========================================="")
    print(""SEW_TOL ="", SEW_TOL)

    for raw in profile_text.splitlines():
        line = raw.strip()
        if not line:
            continue

        print(""PARSE:"", line)
        parts = line.split()
        if len(parts) == 0:
            continue

        tag = parts[0].upper()

        if tag == ""LINE"":
            if len(parts) < 5:
                raise Exception(""Bad LINE format: "" + line)
            Xs = float(parts[1])
            Zs = float(parts[2])
            Xe = float(parts[3])
            Ze = float(parts[4])
            segs.append((""LINE"", Xs, Zs, Xe, Ze))

        elif tag.startswith(""ARC3_""):
            if len(parts) < 7:
                raise Exception(""Bad ARC3 format: "" + line)
            Xs = float(parts[1])
            Zs = float(parts[2])
            Xm = float(parts[3])
            Zm = float(parts[4])
            Xe = float(parts[5])
            Ze = float(parts[6])
            segs.append((""ARC3"", tag, Xs, Zs, Xm, Zm, Xe, Ze))

        else:
            print(""WARNING: Unknown tag, skipping:"", tag)

    print(""TOTAL SEGMENTS:"", len(segs))
    print(""=========================================="")
    return segs

# ------------------------------------------------------------
# SNAP / SEW HELPERS (tolerance-driven)
# We snap all endpoints/midpoints used to build edges so that
# FreeCAD sees coincident vertices within SEW_TOL.
# ------------------------------------------------------------
_canon_pts = []  # list[FreeCAD.Vector]

def _dist(a: FreeCAD.Vector, b: FreeCAD.Vector) -> float:
    dx = a.x - b.x
    dy = a.y - b.y
    dz = a.z - b.z
    return math.sqrt(dx*dx + dy*dy + dz*dz)

def snap_point(p: FreeCAD.Vector) -> FreeCAD.Vector:
    # If SEW_TOL <= 0 we do nothing.
    if SEW_TOL is None or SEW_TOL <= 0.0:
        return p

    for q in _canon_pts:
        if _dist(p, q) <= SEW_TOL:
            return q

    _canon_pts.append(p)
    return p

# ------------------------------------------------------------
# BUILD EDGES IN XZ PLANE
# - X is radius → FreeCAD X
# - Z is axial  → FreeCAD Z
# - Y is 0
# ------------------------------------------------------------
def build_edges(segments):
    edges = []

    for i, seg in enumerate(segments):
        print(""\n-- SEG[%d] ---------------------------"" % i)
        print(seg)

        if seg[0] == ""LINE"":
            _, Xs, Zs, Xe, Ze = seg

            p1 = snap_point(FreeCAD.Vector(Xs, 0.0, Zs))
            p2 = snap_point(FreeCAD.Vector(Xe, 0.0, Ze))

            print("" LINE P1:"", p1)
            print("" LINE P2:"", p2)

            edges.append(Part.LineSegment(p1, p2).toShape())

        elif seg[0] == ""ARC3"":
            _, tag, Xs, Zs, Xm, Zm, Xe, Ze = seg

            p1 = snap_point(FreeCAD.Vector(Xs, 0.0, Zs))
            pm = snap_point(FreeCAD.Vector(Xm, 0.0, Zm))
            p2 = snap_point(FreeCAD.Vector(Xe, 0.0, Ze))

            print("" ARC3 type:"", tag)
            print(""  P1:"", p1)
            print(""  PM:"", pm)
            print(""  P2:"", p2)

            arc = Part.Arc(p1, pm, p2)
            edges.append(arc.toShape())

        else:
            print(""WARNING: Unknown segment type:"", seg[0])

    return edges

# ------------------------------------------------------------
# MAIN
# ------------------------------------------------------------
segments = parse_profile_text(latheProfile)
edges = build_edges(segments)

print(""\nBuilding wire..."")
wire = Part.Wire(edges)

print(""Wire closed?:"", wire.isClosed())
if not wire.isClosed():
    # hard fail: we still require closed after snapping
    print(""ERROR: Wire NOT closed (even after snap). Increase SewTol or fix trim/chaining."")
    raise SystemExit(1)

print(""Creating face..."")
face = Part.Face(wire)

print(""Revolving around Z axis..."")
axis_point = FreeCAD.Vector(0.0, 0.0, 0.0)
axis_dir   = FreeCAD.Vector(0.0, 0.0, 1.0)

solid = face.revolve(axis_point, axis_dir, 360.0)
print(""SOLID OK. Volume:"", solid.Volume)

print(""Creating FreeCAD document object for STEP export..."")
doc = FreeCAD.newDocument(""RevolveDoc"")
obj = doc.addObject(""Part::Feature"", ""RevolvedSolid"")
obj.Shape = solid
doc.recompute()

# ------------------------------------------------------------
# APPLY TRANSFORM TO FINAL OBJECT (after revolve)
# ------------------------------------------------------------
obj.Placement = PlacementTransform.multiply(obj.Placement)
doc.recompute()

print(""Exporting STEP:"", output_step)
Part.export([obj], output_step)

print(""\nSTEP export COMPLETE.\n"")
";
    }
}
