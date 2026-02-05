// File: Utilities/FaptMill.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class FaptMill
    {
        internal static List<string> TextToLines_All(string text)
        {
            if (text == null) text = "";
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return text.Split('\n').ToList();
        }

        // MILL regions:
        // Start: line contains "(G106" (typically G1062 / G1063)
        // End:   first "(G1206" line (inclusive)
        internal static List<List<string>> BuildFaptMillRegions(List<string> lines)
        {
            var regions = new List<List<string>>();
            if (lines == null) return regions;

            int i = 0;
            while (i < lines.Count)
            {
                string s0 = (lines[i] ?? "").Trim();
                if (s0.Length == 0) { i++; continue; }

                // Must be a MILL header line
                if (!Contains(s0, "(G106"))
                {
                    i++;
                    continue;
                }

                var region = new List<string>();
                region.Add(lines[i] ?? "");

                int j = i + 1;
                while (j < lines.Count)
                {
                    string t = lines[j] ?? "";
                    region.Add(t);

                    if (Contains(t, "(G1206"))
                        break;

                    j++;
                }

                regions.Add(region);
                i = j + 1;
            }

            return regions;
        }

        /// <summary>
        /// Translate ONE selected MILL FAPT region (raw lines) into MILL XY G-code.
        ///
        /// Rules (matching your notes + the observed Fanuc output):
        /// - Start line we care about is (G1200 ... H.. V.. L..)
        ///     H = start X
        ///     V = start Y
        ///     L = Z plane (depth), emit FIRST as:  G0 Z{L}
        /// - Then emit start XY as: G0 X{H} Y{V}
        /// - For each motion line:
        ///     - Endpoint is H,V  => X,Y
        ///     - Center is I,J (ABSOLUTE center coords) when present
        ///     - Direction is computed from geometry:
        ///           cross > 0 => CCW => G3
        ///           cross < 0 => CW  => G2
        ///     - Output I/J in *incremental* form from start point (Fanuc style)
        /// - Stops at (G1206) (inclusive, but not emitted as motion)
        /// - Feed F: from the (G106x...) header if present (F####)
        /// </summary>
        internal static List<string> TranslateFaptRegionToMillGcode(List<string> regionLines)
        {
            var outLines = new List<string>();
            if (regionLines == null || regionLines.Count == 0)
                return outLines;

            // Feed from G106x header (optional)
            double? feed = null;
            {
                string hdr = (regionLines[0] ?? "").Trim().ToUpperInvariant();
                if (hdr.Contains("(G106"))
                {
                    if (TryGetParam(hdr, 'F', out double fVal))
                        feed = fVal;
                }
            }

            // Find first G1200
            string g1200 = null;
            for (int i = 0; i < regionLines.Count; i++)
            {
                string u = (regionLines[i] ?? "").Trim();
                if (u.Length == 0) continue;

                string uu = u.ToUpperInvariant();
                if (uu.Contains("(G1200") || uu.Contains("G1200"))
                {
                    g1200 = u;
                    break;
                }
            }

            if (g1200 == null)
                return outLines;

            if (!TryGetParam(g1200, 'H', out double curX) || !TryGetParam(g1200, 'V', out double curY))
                return outLines;

            // L is Z plane (depth) per your note. If missing, leave it out.
            if (TryGetParam(g1200, 'L', out double zPlane))
            {
                outLines.Add(string.Format(CultureInfo.InvariantCulture, "G0Z{0:0.####}", zPlane));
            }

            // Rapid to XY start
            outLines.Add(string.Format(CultureInfo.InvariantCulture, "G0X{0:0.####}Y{1:0.####}", curX, curY));

            // Walk subsequent lines until G1206
            for (int i = 0; i < regionLines.Count; i++)
            {
                string ln = (regionLines[i] ?? "").Trim();
                if (ln.Length == 0) continue;

                string u = ln.ToUpperInvariant();

                if (u.Contains("(G1206") || u.Contains("G1206"))
                    break;

                // We handle arc-ish records: G1202 / G1203 / G1205
                bool isArc =
                    u.Contains("G1202") ||
                    u.Contains("G1203") ||
                    u.Contains("G1205");

                if (!isArc)
                    continue;

                // Endpoint H,V
                if (!TryGetParam(u, 'H', out double endX) || !TryGetParam(u, 'V', out double endY))
                    continue;

                // ---- FIX: declare center vars up-front so they exist in this scope ----
                double cenX = 0.0;
                double cenY = 0.0;
                bool hasCenter = TryGetParam(u, 'I', out cenX) && TryGetParam(u, 'J', out cenY);
                // ---------------------------------------------------------------

                if (!hasCenter)
                {
                    // Treat as straight if no center
                    if (feed.HasValue)
                        outLines.Add(string.Format(CultureInfo.InvariantCulture, "G1X{0:0.###}Y{1:0.###}F{2:0.###}", endX, endY, feed.Value));
                    else
                        outLines.Add(string.Format(CultureInfo.InvariantCulture, "G1X{0:0.###}Y{1:0.###}", endX, endY));

                    curX = endX; curY = endY;
                    continue;
                }

                // Determine CW/CCW from geometry around center
                double ax = curX - cenX;
                double ay = curY - cenY;
                double bx = endX - cenX;
                double by = endY - cenY;

                double cross = (ax * by) - (ay * bx);
                bool ccw = cross > 0.0;

                string g = ccw ? "G3" : "G2";

                // Incremental IJ from start
                double iInc = cenX - curX;
                double jInc = cenY - curY;

                if (feed.HasValue)
                {
                    outLines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}X{1:0.###}Y{2:0.###}I{3:0.###}J{4:0.###}F{5:0.###}",
                        g, endX, endY, iInc, jInc, feed.Value));
                }
                else
                {
                    outLines.Add(string.Format(CultureInfo.InvariantCulture,
                        "{0}X{1:0.###}Y{2:0.###}I{3:0.###}J{4:0.###}",
                        g, endX, endY, iInc, jInc));
                }

                curX = endX; curY = endY;
            }

            return outLines;
        }


        private static bool Contains(string s, string needle)
        {
            return !string.IsNullOrEmpty(s) &&
                   s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Pull a numeric parameter from a FAPT line.
        /// Works with tokens like H-2.4, V167.5, F.3, etc.
        /// </summary>
        private static bool TryGetParam(string line, char key, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(line))
                return false;

            string s = line.ToUpperInvariant();
            int idx = s.IndexOf(key);
            if (idx < 0)
                return false;

            int i = idx + 1;
            if (i >= s.Length)
                return false;

            int start = i;
            bool sawAny = false;

            if (s[i] == '+' || s[i] == '-')
                i++;

            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.')
                {
                    sawAny = true;
                    i++;
                    continue;
                }
                break;
            }

            if (!sawAny)
                return false;

            string num = s.Substring(start, i - start);

            if (num.StartsWith(".", StringComparison.Ordinal))
                num = "0" + num;
            if (num.StartsWith("+.", StringComparison.Ordinal))
                num = "+0" + num.Substring(1);
            if (num.StartsWith("-.", StringComparison.Ordinal))
                num = "-0" + num.Substring(1);

            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
