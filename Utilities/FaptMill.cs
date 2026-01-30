// File: Utilities/FaptMill.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// MILL FAPT support (G1062 ... G1206).
    /// For now: translator is a placeholder returning a fixed sample block.
    /// </summary>
    internal static class FaptMill
    {
        public static List<string> TextToLines_All(string allText)
        {
            if (allText == null) allText = "";
            allText = allText.Replace("\r\n", "\n").Replace("\r", "\n");

            var lines = allText.Split('\n')
                               .Select(s => (s ?? "").TrimEnd())
                               .ToList();

            // Drop final empty line if RTB adds one
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[^1]))
                lines.RemoveAt(lines.Count - 1);

            return lines;
        }

        /// <summary>
        /// Build MILL regions:
        ///  - region starts at a line containing "(G1062"
        ///  - region ends at a line containing "(G1206" (inclusive)
        /// </summary>
        public static List<List<string>> BuildFaptMillRegions(List<string> lines)
        {
            var regions = new List<List<string>>();
            if (lines == null || lines.Count == 0)
                return regions;

            int i = 0;
            while (i < lines.Count)
            {
                string line = lines[i] ?? "";
                if (line.IndexOf("(G1062", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var reg = new List<string>();
                    reg.Add(line);

                    i++;

                    while (i < lines.Count)
                    {
                        string l2 = lines[i] ?? "";
                        reg.Add(l2);

                        if (l2.IndexOf("(G1206", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            i++; // move past end marker
                            break;
                        }

                        i++;
                    }

                    regions.Add(reg);
                    continue;
                }

                i++;
            }

            return regions;
        }

        /// <summary>
        /// Placeholder translator: returns the fixed sample toolpath you provided.
        /// NOTE: This is NOT yet parsing the region text.
        /// </summary>
        public static List<string> TranslateFaptRegionToMillGcode(List<string> regionLines)
        {
            return new List<string>
            {
                "G0  X169.000000   Y0.000000",
                "Z-14",
                "G2  X168.995120   Y-1.284475   I-169.000000  J0.000026",
                "G2  X161.439070   Y-10.905300  I-9.999710    J0.076006",
                "G2  X148.720600   Y-13.490050  I-34.211240   J135.755630",
                "G3  X137.910390   Y-24.098248  I1.995750     J-12.845893",
                "G2  X129.680750   Y-52.753243  I-137.910385  J24.098250",
                "G3  X133.158640   Y-67.432336  I12.041780    J-4.898515",
                "G2  X142.563020   Y-76.325651  I-111.330648  J-127.147504",
                "G2  X143.935850   Y-88.563375  I-7.144080    J-6.997290",
                "G2  X142.635330   Y-90.643062  I-143.935870  J88.563340",
                "G2  X131.129170   Y-94.797890  I-8.439970    J5.363493",
                "G2  X118.973580   Y-90.250119  I42.926750    J133.256497",
                "G3  X104.184520   Y-93.517843  I-5.114790    J-11.951521",
                "G2  X89.735155    Y-107.459770 I-104.184510  J93.517850",
                "G3  X85.914531    Y-122.053410 I8.332549     J-9.978410"
            };
        }

        public static List<string> FormatMillGcodeBlock(string name, List<string> rawGcode, char alpha)
        {
            var outLines = new List<string>();
            if (rawGcode == null) rawGcode = new List<string>();

            // wrapper
            outLines.Add($"({name} ST)");

            // tag formatting (match your style: (m:a0000) etc)
            const int TAG_PAD_COLUMN = 75;

            int n = 0;
            for (int i = 0; i < rawGcode.Count; i++)
            {
                string gl = (rawGcode[i] ?? "").TrimEnd();
                if (string.IsNullOrWhiteSpace(gl))
                    continue;

                string tag = $"(m:{alpha}{n.ToString("0000", CultureInfo.InvariantCulture)})";
                n++;

                // pad so tag starts around column 75
                int pad = TAG_PAD_COLUMN - gl.Length;
                if (pad < 1) pad = 1;

                outLines.Add(gl + new string(' ', pad) + tag);
            }

            outLines.Add($"({name} END)");
            return outLines;
        }



    }
}
