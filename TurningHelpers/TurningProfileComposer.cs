using System;
using System.Collections.Generic;
using System.Globalization;

namespace CNC_Improvements_gcode_solids.TurningHelpers
{
    /// <summary>
    /// Shared helpers for universal profile script format:
    ///   LINE x1 z1   x2 z2
    ///   ARC3_CW x1 z1   xm zm   x2 z2   [extras...]
    ///   ARC3_CCW x1 z1  xm zm   x2 z2   [extras...]
    ///
    /// This class is intentionally ignorant of where the profile came from:
    /// - G-code -> profile
    /// - Offsetter -> profile
    ///
    /// It only:
    /// - extracts the start/end points
    /// - builds the 3 closing lines
    /// - composes the final closed shape list (entry + profile + exit + close)
    /// </summary>
    internal static class TurningProfileComposer
    {
        public static List<string> BuildClosingLinesForOpenProfile(List<string> profileOpen, double zUser)
        {
            if (profileOpen == null || profileOpen.Count == 0)
                throw new InvalidOperationException("Profile shape is empty; cannot build closing lines.");

            // ----- START POINT from first segment -----
            GetStartPoint(profileOpen[0], out double startX, out double startZ);

            // ----- END POINT from last segment -----
            GetEndPoint(profileOpen[profileOpen.Count - 1], out double endX, out double endZ);

            var closing = new List<string>(3)
            {
                // Entry: startX, Zuser -> startX, startZ
                string.Format(
                    CultureInfo.InvariantCulture,
                    "LINE {0} {1}   {0} {2}",
                    startX, zUser, startZ),

                // Exit: endX, endZ -> endX, Zuser
                string.Format(
                    CultureInfo.InvariantCulture,
                    "LINE {0} {1}   {0} {2}",
                    endX, endZ, zUser),

                // Close: endX, Zuser -> startX, Zuser
                string.Format(
                    CultureInfo.InvariantCulture,
                    "LINE {0} {1}   {2} {1}",
                    endX, zUser, startX)
            };

            return closing;
        }

        public static List<string> ComposeClosedShape(List<string> profileOpen, List<string> closing3)
        {
            if (profileOpen == null || profileOpen.Count == 0)
                throw new InvalidOperationException("profileOpen is empty.");

            // NEW: allow "no closing" meaning "already closed"
            if (closing3 == null || closing3.Count == 0)
                return new List<string>(profileOpen);

            if (closing3.Count != 3)
                throw new InvalidOperationException("closing3 must contain exactly 3 LINE entries (or be empty).");

            var closed = new List<string>(profileOpen.Count + 3);

            // entry, profile, exit, close
            closed.Add(closing3[0]);
            closed.AddRange(profileOpen);
            closed.Add(closing3[1]);
            closed.Add(closing3[2]);

            return closed;
        }


        private static void GetStartPoint(string firstLine, out double startX, out double startZ)
        {
            if (string.IsNullOrWhiteSpace(firstLine))
                throw new InvalidOperationException("First profile line is empty.");

            var parts = SplitParts(firstLine);
            if (parts.Length < 5)
                throw new InvalidOperationException("First profile line has invalid format (needs at least 5 tokens).");

            string cmd = parts[0].ToUpperInvariant();

            if (cmd == "LINE")
            {
                startX = ParseInv(parts[1]);
                startZ = ParseInv(parts[2]);
                return;
            }

            if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
            {
                // ARC3_* x1 z1 xm zm x2 z2 [extras...]
                if (parts.Length < 7)
                    throw new InvalidOperationException("First ARC3_* line has invalid format (needs at least 7 tokens).");

                startX = ParseInv(parts[1]);
                startZ = ParseInv(parts[2]);
                return;
            }

            throw new InvalidOperationException("First profile line must be LINE or ARC3_* for closing logic.");
        }

        private static void GetEndPoint(string lastLine, out double endX, out double endZ)
        {
            if (string.IsNullOrWhiteSpace(lastLine))
                throw new InvalidOperationException("Last profile line is empty.");

            var parts = SplitParts(lastLine);
            if (parts.Length < 5)
                throw new InvalidOperationException("Last profile line has invalid format (needs at least 5 tokens).");

            string cmd = parts[0].ToUpperInvariant();

            if (cmd == "LINE")
            {
                // LINE x1 z1 x2 z2
                if (parts.Length < 5)
                    throw new InvalidOperationException("Last LINE has invalid format (needs 5 tokens).");

                endX = ParseInv(parts[3]);
                endZ = ParseInv(parts[4]);
                return;
            }

            if (cmd == "ARC3_CW" || cmd == "ARC3_CCW")
            {
                // ARC3_* x1 z1 xm zm x2 z2 [extras...]
                if (parts.Length < 7)
                    throw new InvalidOperationException("Last ARC3_* line has invalid format (needs at least 7 tokens).");

                endX = ParseInv(parts[5]);
                endZ = ParseInv(parts[6]);
                return;
            }

            throw new InvalidOperationException("Last profile line must be LINE or ARC3_* for closing logic.");
        }

        private static string[] SplitParts(string line)
        {
            return line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        }

        private static double ParseInv(string s)
        {
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
