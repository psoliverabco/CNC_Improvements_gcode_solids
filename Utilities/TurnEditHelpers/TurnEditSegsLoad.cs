// File: Utilities/TurnEditHelpers/TurnEditSegsLoad.cs
using CNC_Improvements_gcode_solids.Properties;
using CNC_Improvements_gcode_solids.SetManagement;
using CNC_Improvements_gcode_solids.TurningHelpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using CNC_Improvements_gcode_solids.Utilities;


namespace CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers
{
    /// <summary>
    /// Load/decode helpers for TurnEditWindow:
    /// - Build display script from selected RegionSets (same behavior as TurnEditWindow baseline)
    /// - Parse display script into segment DTOs (LINE / ARC3_*), applying style + @TRANSFORM
    /// - Parse a single region-input line (used by Fillet Keep)
    ///
    /// IMPORTANT:
    /// - Does NOT depend on TurnEditWindow's private EditSeg classes.
    /// - Emits SegDto which TurnEditWindow converts into EditLineSeg/EditArcSeg.
    /// </summary>
    internal static class TurnEditSegsLoad
    {
        // ---------------- DTO ----------------

        internal enum SegKind { Line, Arc }

        internal sealed class SegDto
        {
            public SegKind Kind;
            public Brush Stroke = Brushes.White;
            public double Thickness = 1.0;

            // World coords: Point.X = X, Point.Y = Z
            public Point A;
            public Point B;

            // Arc-only
            public Point M;
            public Point C;
            public bool CCW;
        }

        // ============================================================
        // Submit path: Build "View All" script (same as your baseline)
        // ============================================================

        public static string BuildSelectedTurnSetsDisplayScript(
            MainWindow main,
            List<RegionSet> selectedSets,
            List<string> allLines,
            string KEY_ToolUsage,
            string KEY_Quadrant,
            string KEY_StartXLine,
            string KEY_StartZLine,
            string KEY_EndXLine,
            string KEY_EndZLine,
            string KEY_TxtZExt,
            string KEY_NRad)
        {
            string profWidth = Settings.Default.ProfileWidth.ToString("0.###", CultureInfo.InvariantCulture);
            string offWidth = Settings.Default.OffsetWidth.ToString("0.###", CultureInfo.InvariantCulture);
            string closeWidth = Settings.Default.ClosingWidth.ToString("0.###", CultureInfo.InvariantCulture);

            string styleProfile = $"({Settings.Default.ProfileColor},{profWidth})";
            string styleOffset = $"({Settings.Default.OffsetColor},{offWidth})";
            string styleClosing = $"({Settings.Default.ClosingColor},{closeWidth})";

            var sb = new StringBuilder();
            var errors = new List<string>();

            var inv = CultureInfo.InvariantCulture;

            int drawn = 0;

            for (int i = 0; i < selectedSets.Count; i++)
            {
                var set = selectedSets[i];
                if (set == null) continue;

                try
                {
                    if (set.RegionLines == null || set.RegionLines.Count == 0)
                        throw new Exception("Set has no RegionLines stored (Unset).");

                    if (!TryResolveRegionRange(allLines, set.RegionLines, out int start, out int end))
                        throw new Exception("Region not resolved (Missing/Ambiguous).");

                    string sxText = GetSnap(set, KEY_StartXLine, "");
                    string szText = GetSnap(set, KEY_StartZLine, "");
                    string exText = GetSnap(set, KEY_EndXLine, "");
                    string ezText = GetSnap(set, KEY_EndZLine, "");

                    int sx = FindIndexByMarkerTextInRange(allLines, sxText, start, end);
                    int sz = FindIndexByMarkerTextInRange(allLines, szText, start, end);
                    int ex = FindIndexByMarkerTextInRange(allLines, exText, start, end);
                    int ez = FindIndexByMarkerTextInRange(allLines, ezText, start, end);

                    if (sx < 0) throw new Exception("StartX marker line not found in resolved region.");
                    if (sz < 0) throw new Exception("StartZ marker line not found in resolved region.");
                    if (ex < 0) throw new Exception("EndX marker line not found in resolved region.");
                    if (ez < 0) throw new Exception("EndZ marker line not found in resolved region.");

                    var regionLines = new List<string>();
                    for (int ln = start; ln <= end; ln++)
                        regionLines.Add(allLines[ln] ?? "");

                    var moves = BuildGeometryFromGcode_AbsoluteRange(regionLines, allLines, sx, sz);
                    var profileOpen = BuildProfileTextFromMoves(moves);

                    string zExtText = GetSnap(set, KEY_TxtZExt, "-100");
                    if (!double.TryParse(zExtText, NumberStyles.Float, CultureInfo.InvariantCulture, out double zUser))
                        throw new Exception("Invalid TxtZExt in set.");

                    var closing3 = TurningProfileComposer.BuildClosingLinesForOpenProfile(profileOpen, zUser);

                    string usage = GetSnap(set, KEY_ToolUsage, "OFF").Trim();
                    bool isOffset = !string.Equals(usage, "OFF", StringComparison.OrdinalIgnoreCase);

                    List<string> exportProfileOpen = profileOpen;
                    List<string> exportClosing3 = closing3;
                    string exportLabel = "ORIGINAL (G40/OFF)";

                    if (isOffset)
                    {
                        var guideBuilder = new OffsetGuideBuilder(profileOpen, usage);
                        var (_, cornerGuide) = guideBuilder.BuildGuide();

                        string nradText = GetSnap(set, KEY_NRad, "0").Trim();
                        int quadrant = ParseIntOrDefault(GetSnap(set, KEY_Quadrant, "3"), 3);

                        var offsetter = new TurningOffsetter(profileOpen, cornerGuide, usage, nradText, quadrant);
                        var (_, offsetProfileShape) = offsetter.BuildOffsetProfile();

                        if (offsetProfileShape == null || offsetProfileShape.Count == 0)
                            throw new Exception("Offset profile generation produced no segments.");

                        exportProfileOpen = offsetProfileShape;
                        exportClosing3 = TurningProfileComposer.BuildClosingLinesForOpenProfile(offsetProfileShape, zUser);
                        exportLabel = $"OFFSET ({usage.ToUpperInvariant()})";
                    }

                    double rotY = 0.0, rotZ = 0.0, tx = 0.0, ty = 0.0, tz = 0.0;
                    string matrixName = "No Transformation";

                    if (main != null)
                        main.TryGetTransformForRegion(set.Name ?? "", out rotY, out rotZ, out tx, out ty, out tz, out matrixName);

                    string rotYStr = rotY.ToString("0.###", inv);
                    string tzStr = tz.ToString("0.###", inv);
                    string safeMatrixName = (matrixName ?? "").Replace("\"", "'");

                    sb.AppendLine($"; ===== TURN SET: {set.Name} =====");
                    sb.AppendLine($"; Mode: {exportLabel}");
                    sb.AppendLine($"; ToolUsage: {usage.ToUpperInvariant()}");
                    sb.AppendLine($"@TRANSFORM MATRIX \"{safeMatrixName}\" ROTY {rotYStr} TZ {tzStr}");

                    sb.AppendLine(isOffset ? styleOffset : styleProfile);
                    foreach (var line in exportProfileOpen) sb.AppendLine(line);

                    sb.AppendLine(styleClosing);
                    foreach (var line in exportClosing3) sb.AppendLine(line);

                    sb.AppendLine();
                    drawn++;
                }
                catch (Exception oneEx)
                {
                    errors.Add($"{set?.Name ?? "(null)"}: {oneEx.Message}");
                }
            }

            if (drawn == 0)
            {
                if (errors.Count > 0)
                    throw new Exception("No sets could be built:\n\n" + string.Join("\n", errors));

                throw new Exception("No sets could be built (unknown reason).");
            }

            if (errors.Count > 0)
            {
                sb.AppendLine("; ===== ERRORS (some sets skipped) =====");
                foreach (var e in errors)
                    sb.AppendLine($"; {e}");
            }

            return sb.ToString();
        }

        // ============================================================
        // Decode path: Parse script -> SegDto list
        // ============================================================

        public static List<SegDto> ParseSegDtosFromScript(string scriptText, string closingColorHex, bool arcCenterFrom3Pts)
        {
            var segs = new List<SegDto>();
            if (string.IsNullOrWhiteSpace(scriptText))
                return segs;

            string closingHex = NormalizeHexColor(closingColorHex);

            double curRotY = 0.0;
            double curTz = 0.0;

            Brush currentBrush = Brushes.White;
            double currentThickness = 1.0;
            bool ignoreGeom = false;

            var lines = scriptText.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i] ?? "";
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith(";", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("@TRANSFORM", StringComparison.OrdinalIgnoreCase))
                {
                    if (TryParseTransformDirective(line, out double ry, out double tz))
                    {
                        curRotY = ry;
                        curTz = tz;
                    }
                    else
                    {
                        curRotY = 0.0;
                        curTz = 0.0;
                    }
                    continue;
                }

                if (IsStyleLine(line))
                {
                    if (TryParseStyleLine(line, out string styleHex, out double thick))
                    {
                        currentThickness = thick;
                        currentBrush = BrushFromHex(styleHex, Brushes.White);
                        ignoreGeom = (styleHex == closingHex);
                    }
                    continue;
                }

                if (ignoreGeom)
                    continue;

                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 1)
                    continue;

                string cmd = parts[0].ToUpperInvariant();

                if (cmd == "LINE")
                {
                    if (parts.Length < 5)
                        continue;

                    double x1 = ParseDouble(parts[1]);
                    double z1 = ParseDouble(parts[2]);
                    double x2 = ParseDouble(parts[3]);
                    double z2 = ParseDouble(parts[4]);

                    var p1 = ApplyTransform(new Point(x1, z1), curRotY, curTz);
                    var p2 = ApplyTransform(new Point(x2, z2), curRotY, curTz);

                    segs.Add(new SegDto
                    {
                        Kind = SegKind.Line,
                        Stroke = currentBrush,
                        Thickness = currentThickness,
                        A = p1,
                        B = p2
                    });
                }
                else if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
                {
                    // Need: ARC3_* xs zs  xm zm  xe ze  cx cz ...
                    if (parts.Length < 9)
                        continue;

                    bool ccw = (cmd == "ARC3_CCW");
                    if (IsRotY180(curRotY))
                        ccw = !ccw; // mirror Z flips chirality

                    double xs = ParseDouble(parts[1]);
                    double zs = ParseDouble(parts[2]);
                    double xm = ParseDouble(parts[3]);
                    double zm = ParseDouble(parts[4]);
                    double xe = ParseDouble(parts[5]);
                    double ze = ParseDouble(parts[6]);
                    double cx = ParseDouble(parts[7]);
                    double cz = ParseDouble(parts[8]);

                    var pA = ApplyTransform(new Point(xs, zs), curRotY, curTz);
                    var pM = ApplyTransform(new Point(xm, zm), curRotY, curTz);
                    var pB = ApplyTransform(new Point(xe, ze), curRotY, curTz);
                    var pC = ApplyTransform(new Point(cx, cz), curRotY, curTz);

                    if (arcCenterFrom3Pts)
                    {
                        if (TryComputeCircleCenter(pA, pM, pB, out Point c3))
                            pC = c3;
                    }

                    segs.Add(new SegDto
                    {
                        Kind = SegKind.Arc,
                        Stroke = currentBrush,
                        Thickness = currentThickness,
                        A = pA,
                        M = pM,
                        B = pB,
                        C = pC,
                        CCW = ccw
                    });
                }
            }

            return segs;
        }

        // ============================================================
        // Decode ONE region-input geometry line -> SegDto (Fillet Keep)
        // ============================================================

        public static bool TryParseRegionInputLineToSegDto(
            string line,
            Brush stroke,
            double thickness,
            out SegDto? dto,
            out string error)
        {
            dto = null;
            error = "";

            if (string.IsNullOrWhiteSpace(line))
            {
                error = "Empty line.";
                return false;
            }

            string raw = StripTrailingSemicolonComment(line).Trim();
            if (raw.Length == 0)
            {
                error = "Comment/blank line.";
                return false;
            }

            var parts = raw.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                error = "No tokens.";
                return false;
            }

            string cmd = parts[0].ToUpperInvariant();

            Brush useStroke = stroke ?? Brushes.White;
            double useThick = (thickness > 0) ? thickness : 1.0;

            try
            {
                if (cmd == "LINE")
                {
                    if (parts.Length < 5)
                    {
                        error = "LINE requires 4 numbers: LINE x1 z1 x2 z2";
                        return false;
                    }

                    double x1 = ParseDouble(parts[1]);
                    double z1 = ParseDouble(parts[2]);
                    double x2 = ParseDouble(parts[3]);
                    double z2 = ParseDouble(parts[4]);

                    dto = new SegDto
                    {
                        Kind = SegKind.Line,
                        Stroke = useStroke,
                        Thickness = useThick,
                        A = new Point(x1, z1),
                        B = new Point(x2, z2)
                    };
                    return true;
                }

                if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
                {
                    if (parts.Length < 9)
                    {
                        error = "ARC3_* requires at least 8 numbers: ARC3_* xs zs xm zm xe ze cx cz";
                        return false;
                    }

                    bool ccw = (cmd == "ARC3_CCW");

                    double xs = ParseDouble(parts[1]);
                    double zs = ParseDouble(parts[2]);
                    double xm = ParseDouble(parts[3]);
                    double zm = ParseDouble(parts[4]);
                    double xe = ParseDouble(parts[5]);
                    double ze = ParseDouble(parts[6]);
                    double cx = ParseDouble(parts[7]);
                    double cz = ParseDouble(parts[8]);

                    dto = new SegDto
                    {
                        Kind = SegKind.Arc,
                        Stroke = useStroke,
                        Thickness = useThick,
                        A = new Point(xs, zs),
                        M = new Point(xm, zm),
                        B = new Point(xe, ze),
                        C = new Point(cx, cz),
                        CCW = ccw
                    };
                    return true;
                }

                error = $"Unsupported command '{cmd}'. Expected LINE, ARC3_CW, or ARC3_CCW.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ============================================================
        // Internals copied from your baseline (pure helpers)
        // ============================================================

        private static readonly Regex _reTransform =
            new Regex(@"^\s*@TRANSFORM\s+MATRIX\s+""(?<name>[^""]*)""\s+ROTY\s+(?<roty>[-+]?\d+(?:\.\d+)?)\s+TZ\s+(?<tz>[-+]?\d+(?:\.\d+)?)\s*$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static bool TryParseTransformDirective(string line, out double rotYDeg, out double tz)
        {
            rotYDeg = 0.0;
            tz = 0.0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            var m = _reTransform.Match(line);
            if (!m.Success)
                return false;

            if (!double.TryParse(m.Groups["roty"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out rotYDeg))
                rotYDeg = 0.0;

            if (!double.TryParse(m.Groups["tz"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out tz))
                tz = 0.0;

            return true;
        }

        private static string NormalizeHexColor(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            string t = s.Trim();

            if (!t.StartsWith("#", StringComparison.Ordinal))
                t = "#" + t;

            if (t.Length == 7)
                t = "#FF" + t.Substring(1);

            return t.ToUpperInvariant();
        }

        private static bool IsStyleLine(string line)
            => !string.IsNullOrWhiteSpace(line) && line.Trim().StartsWith("(") && line.Trim().EndsWith(")");

        private static bool TryParseStyleLine(string line, out string hexColorNorm, out double thickness)
        {
            hexColorNorm = "";
            thickness = 1.0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string t = line.Trim();
            if (!t.StartsWith("(") || !t.EndsWith(")"))
                return false;

            string inner = t.Substring(1, t.Length - 2);
            var parts = inner.Split(',');

            if (parts.Length < 1)
                return false;

            hexColorNorm = NormalizeHexColor(parts[0].Trim());

            if (parts.Length >= 2)
            {
                if (!double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out thickness))
                    thickness = 1.0;
            }

            return !string.IsNullOrWhiteSpace(hexColorNorm);
        }

        private static Brush BrushFromHex(string hexNorm, Brush fallback)
        {
            try
            {
                var bc = new BrushConverter();
                var b = (Brush)bc.ConvertFromString(hexNorm);
                if (b != null && b.CanFreeze) b.Freeze();
                return b ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        private static double NormalizeDeg360(double deg)
        {
            double n = deg % 360.0;
            if (n < 0.0) n += 360.0;
            return n;
        }

        private static bool IsRotY180(double rotYDeg, double tolDeg = 0.5)
        {
            double n = NormalizeDeg360(rotYDeg);
            return Math.Abs(n - 180.0) <= tolDeg;
        }

        private static Point ApplyTransform(Point p, double rotYDeg, double tz)
        {
            bool flip = IsRotY180(rotYDeg);
            double z = p.Y;
            double z2 = flip ? (-z + tz) : (z + tz);
            return new Point(p.X, z2);
        }

        private static double ParseDouble(string s)
            => double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        private static bool TryComputeCircleCenter(Point p1, Point p2, Point p3, out Point center)
        {
            double x1 = p1.X, y1 = p1.Y;
            double x2 = p2.X, y2 = p2.Y;
            double x3 = p3.X, y3 = p3.Y;

            double a11 = 2.0 * (x2 - x1);
            double a12 = 2.0 * (y2 - y1);
            double a21 = 2.0 * (x3 - x1);
            double a22 = 2.0 * (y3 - y1);

            double det = a11 * a22 - a12 * a21;
            if (Math.Abs(det) < 1e-12)
            {
                center = new Point();
                return false;
            }

            double b1 = x2 * x2 + y2 * y2 - x1 * x1 - y1 * y1;
            double b2 = x3 * x3 + y3 * y3 - x1 * x1 - y1 * y1;

            double cx = (b1 * a22 - b2 * a12) / det;
            double cy = (a11 * b2 - a21 * b1) / det;

            center = new Point(cx, cy);
            return true;
        }

        // ---------- Region resolve helpers (match your baseline) ----------

       

        private static bool TryResolveRegionRange(
    List<string> allLines,
    ObservableCollection<string> regionLines,
    out int start,
    out int end)
        {
            start = -1;
            end = -1;

            if (allLines == null || allLines.Count == 0)
                return false;

            if (regionLines == null || regionLines.Count == 0)
                return false;

            // Use the single source of truth normalizer:
            // - strips optional "1234:"
            // - strips leading "#...#" (this includes your "#uid,n#" anchors)
            // - removes whitespace
            // - uppercases
            if (!SetManagement.Builders.BuiltRegionSearches.FindMultiLine(allLines, regionLines, out int s, out int e, out int matchCount))
                return false;

            if (matchCount != 1)
                return false;

            start = s;
            end = e;
            return true;
        }


        private static string GetSnap(RegionSet set, string key, string def)
        {
            if (set?.PageSnapshot?.Values != null && set.PageSnapshot.Values.TryGetValue(key, out string v))
                return v ?? def;
            return def;
        }

        private static int ParseIntOrDefault(string s, int def)
        {
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                return v;
            return def;
        }

        private static int FindIndexByMarkerTextInRange(List<string> lines, string markerText, int start, int end)
        {
            if (string.IsNullOrWhiteSpace(markerText))
                return -1;

            if (lines == null || lines.Count == 0)
                return -1;

            if (start < 0 || end < 0 || start >= lines.Count || end >= lines.Count || end < start)
                return -1;

            // Uses TextSearching normalization, so it matches even if markerText has "#uid,n#"
            // and editor lines do not, and even if spacing/tag alignment differs.
            return SetManagement.Builders.BuiltRegionSearches.FindSingleLine(lines, markerText, start, end, preferLast: false);
        }


        // ---------- G-code -> GeoMove parsing (same as your baseline) ----------

        private enum MotionMode { None, G1, G2, G3 }

        private sealed class GeoMove
        {
            public string Type = "";    // "LINE", "ARC_CW", "ARC_CCW"
            public double Xs, Zs;
            public double Xe, Ze;
            public double I, K;
            public double R;
        }

        private static bool TryGetCoord(string line, char axis, out double value)
        {
            value = double.NaN;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string s = line.ToUpperInvariant();
            int idx = 0;

            while (true)
            {
                idx = s.IndexOf(axis, idx);
                if (idx < 0)
                    return false;

                int start = idx + 1;
                if (start >= s.Length)
                    return false;

                int end = start;

                while (end < s.Length)
                {
                    char c = s[end];
                    if (char.IsDigit(c) || c == '+' || c == '-' || c == '.')
                        end++;
                    else
                        break;
                }

                if (end > start)
                {
                    string numStr = s.Substring(start, end - start);
                    if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                        return true;
                }

                idx = end;
            }
        }

        private static (double startX, double startZ) GetStartCoordinatesFromMarkerIndices(
            List<string> allLines,
            int startXIdx,
            int startZIdx)
        {
            if (allLines == null || allLines.Count == 0)
                throw new Exception("No G-code lines loaded.");

            if (startXIdx < 0 || startXIdx >= allLines.Count)
                throw new Exception("Invalid Start-X marker index.");

            if (startZIdx < 0 || startZIdx >= allLines.Count)
                throw new Exception("Invalid Start-Z marker index.");

            string lx = allLines[startXIdx];
            string lz = allLines[startZIdx];

            if (!TryGetCoord(lx, 'X', out double startX))
                throw new Exception($"Start-X marker line {startXIdx + 1} does not contain a valid X value.");

            if (!TryGetCoord(lz, 'Z', out double startZ))
                throw new Exception($"Start-Z marker line {startZIdx + 1} does not contain a valid Z value.");

            return (startX, startZ);
        }

        private static List<GeoMove> BuildGeometryFromGcode_AbsoluteRange(
            List<string> regionLines,
            List<string> allLines,
            int startXIdx,
            int startZIdx)
        {
            List<GeoMove> moves = new();

            if (regionLines == null || regionLines.Count == 0)
                return moves;

            (double startX, double startZ) = GetStartCoordinatesFromMarkerIndices(allLines, startXIdx, startZIdx);
            double lastX = startX;
            double lastZ = startZ;

            MotionMode mode = MotionMode.None;

            bool firstLineProcessed = false;
            int trueRegionStart = Math.Max(startXIdx, startZIdx);
            int absStart = Math.Min(startXIdx, startZIdx);

            for (int local = 0; local < regionLines.Count; local++)
            {
                int absIndex = absStart + local;

                string raw = regionLines[local];
                string line = (raw ?? "").ToUpperInvariant().Trim();

                if (line.Contains("G1") || line.Contains("G01") || line.Contains("G0") || line.Contains("G00"))
                    mode = MotionMode.G1;
                if (line.Contains("G2") || line.Contains("G02"))
                    mode = MotionMode.G2;
                if (line.Contains("G3") || line.Contains("G03"))
                    mode = MotionMode.G3;

                if (!firstLineProcessed)
                {
                    firstLineProcessed = true;
                    if (absIndex != trueRegionStart)
                        continue;
                }

                bool hasX = TryGetCoord(line, 'X', out double newX);
                bool hasZ = TryGetCoord(line, 'Z', out double newZ);

                if (!hasX) newX = lastX;
                if (!hasZ) newZ = lastZ;

                if (mode == MotionMode.None)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (!hasX && !hasZ)
                {
                    lastX = newX;
                    lastZ = newZ;
                    continue;
                }

                if (mode == MotionMode.G1)
                {
                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = "LINE",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ
                    });
                }
                else if (mode == MotionMode.G2 || mode == MotionMode.G3)
                {
                    double I = 0, K = 0, R = 0;

                    bool hasI = TryGetCoord(line, 'I', out I);
                    bool hasK = TryGetCoord(line, 'K', out K);
                    bool hasR = TryGetCoord(line, 'R', out R);

                    if (!hasI && !hasK && !hasR)
                        throw new Exception($"ERROR: Arc missing I/K or R at line {absIndex + 1}.");

                    if (lastX == newX && lastZ == newZ)
                    {
                        lastX = newX;
                        lastZ = newZ;
                        continue;
                    }

                    moves.Add(new GeoMove
                    {
                        Type = (mode == MotionMode.G2) ? "ARC_CW" : "ARC_CCW",
                        Xs = lastX,
                        Zs = lastZ,
                        Xe = newX,
                        Ze = newZ,
                        I = I,
                        K = K,
                        R = R
                    });
                }

                lastX = newX;
                lastZ = newZ;
            }

            return moves;
        }

        private static void GetArc3Points(
            GeoMove m,
            out double xsR, out double zs,
            out double xmR, out double zm,
            out double xeR, out double ze,
            out double cxR, out double cz)
        {
            xsR = m.Xs / 2.0;
            xeR = m.Xe / 2.0;
            zs = m.Zs;
            ze = m.Ze;

            double cxRLocal = 0.0;
            double czLocal = 0.0;

            double aStart = 0.0;
            double dAlpha = 0.0;

            bool useR = Math.Abs(m.R) > 1e-9;

            if (!useR)
            {
                cxRLocal = xsR + m.I;
                czLocal = m.Zs + m.K;

                double sx = xsR - cxRLocal;
                double szLoc = m.Zs - czLocal;
                double ex = xeR - cxRLocal;
                double ezLoc = m.Ze - czLocal;

                double a1 = Math.Atan2(szLoc, sx);
                double a2 = Math.Atan2(ezLoc, ex);
                double da = a2 - a1;

                if (m.Type == "ARC_CW")
                {
                    while (da <= 0.0) da += 2.0 * Math.PI;
                }
                else
                {
                    while (da >= 0.0) da -= 2.0 * Math.PI;
                }

                aStart = a1;
                dAlpha = da;
            }
            else
            {
                double r = Math.Abs(m.R);

                double dx = xeR - xsR;
                double dz = m.Ze - m.Zs;
                double d = Math.Sqrt(dx * dx + dz * dz);

                if (d < 1e-9)
                    throw new Exception("R-arc with zero-length chord.");

                if (d > 2.0 * r + 1e-6)
                    throw new Exception("R too small for given arc endpoints.");

                double mx = (xsR + xeR) * 0.5;
                double mz = (m.Zs + m.Ze) * 0.5;

                double px = -dz / d;
                double pz = dx / d;

                double h = Math.Sqrt(Math.Max(r * r - (d * d * 0.25), 0.0));

                double cx1 = mx + h * px;
                double cz1 = mz + h * pz;
                double cx2 = mx - h * px;
                double cz2 = mz - h * pz;

                double s1x = xsR - cx1;
                double s1z = m.Zs - cz1;
                double e1x = xeR - cx1;
                double e1z = m.Ze - cz1;

                double a1_1 = Math.Atan2(s1z, s1x);
                double a2_1 = Math.Atan2(e1z, e1x);
                double da1 = a2_1 - a1_1;

                if (m.Type == "ARC_CW")
                {
                    while (da1 <= 0.0) da1 += 2.0 * Math.PI;
                }
                else
                {
                    while (da1 >= 0.0) da1 -= 2.0 * Math.PI;
                }

                double s2x = xsR - cx2;
                double s2z = m.Zs - cz2;
                double e2x = xeR - cx2;
                double e2z = m.Ze - cz2;

                double a1_2 = Math.Atan2(s2z, s2x);
                double a2_2 = Math.Atan2(e2z, e2x);
                double da2 = a2_2 - a1_2;

                if (m.Type == "ARC_CW")
                {
                    while (da2 <= 0.0) da2 += 2.0 * Math.PI;
                }
                else
                {
                    while (da2 >= 0.0) da2 -= 2.0 * Math.PI;
                }

                bool wantLong = (m.R < 0.0);

                bool ok1 = wantLong ? (Math.Abs(da1) > Math.PI) : (Math.Abs(da1) <= Math.PI);
                bool ok2 = wantLong ? (Math.Abs(da2) > Math.PI) : (Math.Abs(da2) <= Math.PI);

                if (ok1 && !ok2)
                {
                    cxRLocal = cx1;
                    czLocal = cz1;
                    aStart = a1_1;
                    dAlpha = da1;
                }
                else if (!ok1 && ok2)
                {
                    cxRLocal = cx2;
                    czLocal = cz2;
                    aStart = a1_2;
                    dAlpha = da2;
                }
                else
                {
                    if (Math.Abs(da1) <= Math.Abs(da2))
                    {
                        cxRLocal = cx1;
                        czLocal = cz1;
                        aStart = a1_1;
                        dAlpha = da1;
                    }
                    else
                    {
                        cxRLocal = cx2;
                        czLocal = cz2;
                        aStart = a1_2;
                        dAlpha = da2;
                    }
                }
            }

            double sxFinal = xsR - cxRLocal;
            double szFinal = zs - czLocal;

            double radiusFinal = Math.Sqrt(sxFinal * sxFinal + szFinal * szFinal);
            double aMid = aStart + 0.5 * dAlpha;

            xmR = cxRLocal + radiusFinal * Math.Cos(aMid);
            zm = czLocal + radiusFinal * Math.Sin(aMid);

            cxR = cxRLocal;
            cz = czLocal;
        }

        private static List<string> BuildProfileTextFromMoves(List<GeoMove> moves)
        {
            if (moves == null || moves.Count == 0)
                throw new Exception("No feed moves found in selection.");

            List<string> profile = new List<string>();

            foreach (var m in moves)
            {
                if (m.Type == "LINE")
                {
                    double xsR = m.Xs / 2.0;
                    double xeR = m.Xe / 2.0;

                    profile.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "LINE {0} {1}   {2} {3}",
                        xsR, m.Zs,
                        xeR, m.Ze));
                }
                else if (m.Type == "ARC_CW" || m.Type == "ARC_CCW")
                {
                    GetArc3Points(
                        m,
                        out double xsR, out double zs,
                        out double xmR, out double zm,
                        out double xeR, out double ze,
                        out double cxR, out double cz);

                    double vSx = cxR - xsR;
                    double vSz = cz - zs;
                    double vEx = cxR - xeR;
                    double vEz = cz - ze;

                    string tag = (m.Type == "ARC_CW") ? "ARC3_CW" : "ARC3_CCW";

                    profile.Add(string.Format(
                        CultureInfo.InvariantCulture,
                        "{0} {1} {2}   {3} {4}   {5} {6}   {7} {8}   {9} {10}   {11} {12}",
                        tag,
                        xsR, zs,
                        xmR, zm,
                        xeR, ze,
                        cxR, cz,
                        vSx, vSz,
                        vEx, vEz));
                }
                else
                {
                    throw new Exception($"Unknown move type '{m.Type}' in BuildProfileTextFromMoves.");
                }
            }

            return profile;
        }

        // ---------- small helpers ----------

        private static string StripTrailingSemicolonComment(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            int k = s.IndexOf(';');
            return (k >= 0) ? s.Substring(0, k) : s;
        }
    }
}
