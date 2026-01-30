namespace CNC_Improvements_gcode_solids.FreeCadIntegration
{
    /// <summary>
    /// Holds the embedded Python strings and the dynamic values
    /// for drill-hole geometry (shape params + hole positions).
    /// </summary>
    internal static class FreeCadScriptDrill
    {
        // Python header: imports, documentation, banner.
        // NOTE: output_step is injected from C# BEFORE this header,
        // so the prints here can see it.
        internal const string HeadPY = @"
import math
import FreeCAD
import Part

# ------------------------------------------------------------
# TRANSFORM (Injected example)
# Order: RotZ -> RotY -> Translate
# ------------------------------------------------------------

# ------------------------------------------------------------
# TRANSFORM (Injected example)
# Order: RotZ -> RotY -> Translate
# ------------------------------------------------------------
#TRANSFORM_ROTZ = 0
#TRANSFORM_ROTY = 0
#TRANSFORM_TX = 0.0
#TRANSFORM_TY = 0.0
#TRANSFORM_TZ = 0

";


        public static string TransPY = @"
TRANSFORM_ROTZ = 45.0
TRANSFORM_ROTY = 180.0
TRANSFORM_TX = 0.0
TRANSFORM_TY = 0.0
TRANSFORM_TZ = -150.0
";

        /// <summary>
        /// Python assignments for:
        ///   hole_dia, z_hole_top, point_angle,
        ///   chamfer_len, z_plus_ext, drill_z
        /// </summary>
        public static string HoleShape = "Not yet set HoleShape";



        // Middle part: transform + derived geometry + segment enable rules.
        internal const string MidPY = @"


_rotZ = FreeCAD.Rotation(FreeCAD.Vector(0, 0, 1), TRANSFORM_ROTZ)
_rotY = FreeCAD.Rotation(FreeCAD.Vector(0, 1, 0), TRANSFORM_ROTY)
_rot  = _rotY.multiply(_rotZ)  # RotZ first, then RotY
_trans = FreeCAD.Vector(TRANSFORM_TX, TRANSFORM_TY, TRANSFORM_TZ)
PlacementTransform = FreeCAD.Placement(_trans, _rot)

EPS = 1e-9

# ------------------------------------------------------------
# Segment enable rules:
#   - If chamfer_len <= 0 => chamfer is skipped
#   - If z_plus_ext  <= 0 => extension is skipped
#   - If chamfer is skipped, extension radius == hole radius (dia == hole_dia)
# ------------------------------------------------------------
enable_chamfer = (chamfer_len is not None) and (chamfer_len > EPS)
enable_ext     = (z_plus_ext  is not None) and (z_plus_ext  > EPS)

if hole_dia <= 0.0:
    raise Exception(""hole_dia must be > 0."")

radius = hole_dia / 2.0

# point_angle is the INCLUDED drill point angle (industry standard).
if point_angle <= 0.0 or point_angle > 180.0:
    raise Exception(f""Invalid point_angle={point_angle}. Must be > 0 and <= 180 degrees."")

# Special case: 180° included => flat-bottom hole (no drill point cone).
flat_bottom = abs(point_angle - 180.0) < 1e-9

if not flat_bottom:
    half_angle = point_angle / 2.0
    tan_half = math.tan(math.radians(half_angle))
    if abs(tan_half) < 1e-12:
        raise Exception(f""point_angle={point_angle} gives numerically unstable cone geometry."")
    tip_height = radius / tan_half
else:
    tip_height = 0.0

# Z reference (top surface)
top_ref = z_hole_top

# Cone base / cylinder bottom
bottom_z = tip_height + drill_z

# Main cylinder ends at chamfer start IF chamfer enabled, else at top surface
top_cyl_z = (top_ref - chamfer_len) if enable_chamfer else top_ref

# Chamfer Z range (only meaningful if enabled)
chamfer_z1 = top_cyl_z
chamfer_z2 = top_ref

# Extension Z range (only meaningful if enabled)
ext_z1 = top_ref
ext_z2 = top_ref + z_plus_ext

height_cyl     = top_cyl_z - bottom_z
height_chamfer = (chamfer_z2 - chamfer_z1) if enable_chamfer else 0.0
height_ext     = (ext_z2 - ext_z1) if enable_ext else 0.0

# If chamfer disabled, top radius stays at hole radius => extension dia == hole_dia
top_radius = (radius + chamfer_len) if enable_chamfer else radius

print(""Params / Z-levels:"")
print(f""  radius           = {radius}"")
print(f""  point_angle      = {point_angle}"")
print(f""  flat_bottom      = {flat_bottom}"")
print(f""  drill_z (apex)   = {drill_z}"")
print(f""  tip_height       = {tip_height}"")
print(f""  cone base Z      = {bottom_z}"")
print(f""  cyl   Z range    = [{bottom_z}, {top_cyl_z}]  (h={height_cyl})"")
print(f""  chamfer enabled  = {enable_chamfer}  chamfer_len={chamfer_len}"")
if enable_chamfer:
    print(f""  chamfer Z range  = [{chamfer_z1}, {chamfer_z2}] (h={height_chamfer})"")
else:
    print(""  chamfer Z range  = <SKIPPED>"")
print(f""  ext enabled      = {enable_ext}  z_plus_ext={z_plus_ext}"")
if enable_ext:
    print(f""  ext     Z range  = [{ext_z1}, {ext_z2}] (h={height_ext})"")
else:
    print(""  ext     Z range  = <SKIPPED>"")
print(f""  top radius used  = {top_radius}"")
print("""")

# Validate ONLY what we will build
if height_cyl <= EPS:
    raise Exception(""Main cylinder height <= 0. Check Z values."")
if enable_chamfer and height_chamfer <= EPS:
    raise Exception(""Chamfer is enabled but height <= 0. Check Z values."")
if enable_ext and height_ext <= EPS:
    raise Exception(""Extension is enabled but height <= 0. Check Z values."")
";

        /// <summary>
        /// Python definition of hole_coords.
        /// </summary>
        public static string Positions = "Not yet set Positions";

        // Tail: builds & fuses geometry, applies transform, exports STEP.
        internal const string TailPY = @"
print(""Creating drill-hole geometry..."")
print(""Output STEP:"", output_step)
print("""")

print(""Hole positions (X, Y):"")
for (x, y) in hole_coords:
    print(""  "", x, y)
print("""")

doc = FreeCAD.newDocument(""HoleFusedPerHole"")
objs = []

for idx, (x, y) in enumerate(hole_coords, start=1):
    print(f""Building hole at X={x}, Y={y} (index {idx})..."")

    parts = []

    # 1) Bottom cone (drill point) - only if not flat bottom and tip_height > 0
    if (not flat_bottom) and (tip_height > EPS):
        cone_tip = Part.makeCone(
            0.0,                    # radius at apex
            radius,                 # radius at base
            tip_height,             # height
            FreeCAD.Vector(x, y, drill_z),    # apex
            FreeCAD.Vector(0.0, 0.0, 1.0)     # +Z
        )
        parts.append(cone_tip)
    else:
        if flat_bottom:
            print(""  Flat-bottom hole: skipping drill point cone."")

    # 2) Main cylinder (always)
    cyl_main = Part.makeCylinder(
        radius,
        height_cyl,
        FreeCAD.Vector(x, y, bottom_z),
        FreeCAD.Vector(0.0, 0.0, 1.0)
    )
    parts.append(cyl_main)

    # 3) Chamfer cone (only if enabled and height > 0)
    if enable_chamfer and (height_chamfer > EPS):
        cone_chamfer = Part.makeCone(
            radius,
            top_radius,
            height_chamfer,
            FreeCAD.Vector(x, y, chamfer_z1),
            FreeCAD.Vector(0.0, 0.0, 1.0)
        )
        parts.append(cone_chamfer)

    # 4) Extension cylinder (only if enabled and height > 0)
    # If chamfer disabled, top_radius == radius so ext dia == hole_dia
    if enable_ext and (height_ext > EPS):
        cyl_ext = Part.makeCylinder(
            top_radius,
            height_ext,
            FreeCAD.Vector(x, y, ext_z1),
            FreeCAD.Vector(0.0, 0.0, 1.0)
        )
        parts.append(cyl_ext)

    # Fuse primitives into one solid per hole
    hole_solid = parts[0]
    for p in parts[1:]:
        hole_solid = hole_solid.fuse(p)

    hole_solid = hole_solid.removeSplitter()

    o_hole = doc.addObject(""Part::Feature"", f""Hole{idx}_Solid"")
    o_hole.Shape = hole_solid
    objs.append(o_hole)

doc.recompute()

# Apply transform AFTER removeSplitter (final solids)
for o in objs:
    o.Placement = PlacementTransform.multiply(o.Placement)

doc.recompute()

print(f""\nTotal solids created: {len(objs)} (should be {len(hole_coords)})"")
print(""Exporting all fused hole solids to STEP..."")
Part.export(objs, output_step)
print(""\nDONE. Exported:"", output_step)
";
    }
}
