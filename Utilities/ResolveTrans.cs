// File: Utilities/ResolveTrans.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class ResolveTrans
    {
        // NEW CONTRACT (as per your last message):
        // - headerLineWithTokens: the original FAPT header line containing #MIR/#ROT/#LIN tokens
        // - rawMillGcode: the GENERATED gcode from FaptMill.TranslateFaptRegionToMillGcode(...)
        // Returns: 1..N paths (each path = list of gcode lines), depending on chaining success.
        internal static List<List<string>> ResolveTranslation(string headerLineWithTokens, List<string> rawMillGcode)
        {
            var paths = new List<List<string>>();

            if (rawMillGcode == null || rawMillGcode.Count == 0)
                return paths;

            string header = headerLineWithTokens ?? "";

            // Parse transforms from #...# tokens (order matters)
            var ops = ParseOps(header);

            // If no ops, just return single path unchanged
            if (ops.Count == 0)
            {
                paths.Add(rawMillGcode.Where(l => !string.IsNullOrWhiteSpace(l)).ToList());
                return paths;
            }

            // Expand transforms into 1..N copies of the gcode
            var expanded = Expand(rawMillGcode, ops);

            // Chain/join into 1..N paths based on endpoint connectivity
            // (simple greedy, no reversal; if not connectable, keep separate)
            paths = Chain(expanded);

            return paths;
        }

        // --------------------- parsing ---------------------

        private enum OpType { MIR, ROT, LIN }

        private sealed class Op
        {
            public OpType Type;
            public double[] Args = Array.Empty<double>();
            public int Repeats = 1;
        }

        // Extract all #....# blocks and parse MIR/ROT/LIN
        private static List<Op> ParseOps(string header)
        {
            var ops = new List<Op>();
            if (string.IsNullOrEmpty(header))
                return ops;

            foreach (Match m in Regex.Matches(header, @"#([^#]+)#"))
            {
                string token = (m.Groups[1].Value ?? "").Trim();
                if (token.Length == 0)
                    continue;

                string u = token.ToUpperInvariant();

                if (u.StartsWith("MIR", StringComparison.Ordinal))
                {
                    var nums = ExtractNumbers(token);
                    // #MIR,x1,y1,x2,y2#
                    if (nums.Count >= 4)
                    {
                        ops.Add(new Op
                        {
                            Type = OpType.MIR,
                            Args = new[] { nums[0], nums[1], nums[2], nums[3] },
                            Repeats = 1
                        });
                    }
                    continue;
                }

                if (u.StartsWith("ROT", StringComparison.Ordinal))
                {
                    var nums = ExtractNumbers(token);
                    // #ROT,cx,cy,ang,repeats#
                    if (nums.Count >= 3)
                    {
                        int rep = 1;
                        if (nums.Count >= 4) rep = ClampRep(nums[3]);
                        ops.Add(new Op
                        {
                            Type = OpType.ROT,
                            Args = new[] { nums[0], nums[1], nums[2] }, // cx,cy,angDeg
                            Repeats = rep
                        });
                    }
                    continue;
                }

                if (u.StartsWith("LIN", StringComparison.Ordinal))
                {
                    var nums = ExtractNumbers(token);
                    // You described: #LIN,ux,uy,shift,repeats#  (tolerant)
                    if (nums.Count >= 3)
                    {
                        int rep = 1;
                        if (nums.Count >= 4) rep = ClampRep(nums[3]);
                        ops.Add(new Op
                        {
                            Type = OpType.LIN,
                            Args = new[] { nums[0], nums[1], nums[2] }, // ux,uy,shift
                            Repeats = rep
                        });
                    }
                    continue;
                }
            }

            return ops;
        }

        private static int ClampRep(double v)
        {
            int r = (int)Math.Round(v, MidpointRounding.AwayFromZero);
            if (r < 1) r = 1;
            if (r > 360) r = 360;
            return r;
        }

        private static List<double> ExtractNumbers(string s)
        {
            var list = new List<double>();
            if (string.IsNullOrEmpty(s)) return list;

            foreach (Match m in Regex.Matches(s, @"[-+]?\d*\.?\d+(?:[eE][-+]?\d+)?"))
            {
                if (double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
                    list.Add(v);
            }
            return list;
        }

        // --------------------- expansion ---------------------

        private sealed class GSeg
        {
            public string Cmd = ""; // "G0/G1/G2/G3" or "Z" only
            public double? X, Y, Z;
            public double? I, J; // incremental (for g2/g3)
            public string Tail = ""; // keep "F..." etc (best-effort)
        }

        private static List<List<string>> Expand(List<string> raw, List<Op> ops)
        {
            // Convert raw gcode to segments with positions so transforms are doable
            var segs = ParseGcode(raw);

            // Seed with one copy
            var copies = new List<List<GSeg>> { segs };

            foreach (var op in ops)
            {
                if (op.Type == OpType.MIR)
                {
                    // MIR must KEEP originals + ADD mirrored copies (like ROT/LIN expansion)
                    var next = new List<List<GSeg>>(copies.Count * 2);

                    for (int ci = 0; ci < copies.Count; ci++)
                    {
                        var c = copies[ci];

                        // keep original
                        next.Add(c);

                        // add mirrored copy
                        next.Add(ApplyMirror(c, op.Args[0], op.Args[1], op.Args[2], op.Args[3]));
                    }

                    copies = next;
                }

                else if (op.Type == OpType.ROT)
                {
                    var next = new List<List<GSeg>>();
                    double cx = op.Args[0], cy = op.Args[1], angDeg = op.Args[2];

                    for (int k = 0; k < op.Repeats; k++)
                    {
                        double a = (Math.PI / 180.0) * (angDeg * k);
                        foreach (var c in copies)
                            next.Add(ApplyRotate(c, cx, cy, a));
                    }

                    copies = next;
                }
                else if (op.Type == OpType.LIN)
                {
                    var next = new List<List<GSeg>>();
                    double ux = op.Args[0], uy = op.Args[1], shift = op.Args[2];
                    double len = Math.Sqrt(ux * ux + uy * uy);
                    if (len <= 1e-12) len = 1.0;
                    ux /= len; uy /= len;

                    for (int k = 0; k < op.Repeats; k++)
                    {
                        double dx = ux * shift * k;
                        double dy = uy * shift * k;
                        foreach (var c in copies)
                            next.Add(ApplyTranslate(c, dx, dy));
                    }

                    copies = next;
                }
            }

            // Convert back to text, one list per copy
            var outCopies = new List<List<string>>();
            foreach (var c in copies)
                outCopies.Add(FormatGcode(c));

            return outCopies;
        }

        // --------------------- chaining ---------------------

        private static List<List<string>> Chain(List<List<string>> copies)
        {
            // Determine start/end points of each copy and try to join
            var nodes = new List<(List<string> Lines, (double x, double y)? Start, (double x, double y)? End)>();

            foreach (var c in copies)
            {
                var pts = ExtractFirstLastXY(c);
                nodes.Add((c, pts.start, pts.end));
            }

            var remaining = new List<int>(Enumerable.Range(0, nodes.Count));
            var result = new List<List<string>>();

            const double tol = 1e-6;

            while (remaining.Count > 0)
            {
                int seed = remaining[0];
                remaining.RemoveAt(0);

                var cur = new List<string>(nodes[seed].Lines);
                var curEnd = nodes[seed].End;

                bool progressed = true;
                while (progressed && curEnd.HasValue)
                {
                    progressed = false;

                    for (int ri = 0; ri < remaining.Count; ri++)
                    {
                        int idx = remaining[ri];
                        var n = nodes[idx];

                        if (!n.Start.HasValue)
                            continue;

                        if (Dist2(curEnd.Value, n.Start.Value) <= tol * tol)
                        {
                            // Join by concatenation (skip duplicate first move if identical)
                            var toAdd = n.Lines;

                            // Avoid repeating the first line if it's a G0 to the same XY
                            if (toAdd.Count > 0 && cur.Count > 0)
                            {
                                string a = (cur[cur.Count - 1] ?? "").Trim();
                                string b = (toAdd[0] ?? "").Trim();
                                if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                                    toAdd = toAdd.Skip(1).ToList();
                            }

                            cur.AddRange(toAdd);

                            curEnd = n.End;
                            remaining.RemoveAt(ri);
                            progressed = true;
                            break;
                        }
                    }
                }

                result.Add(cur);
            }

            return result;
        }

        private static double Dist2((double x, double y) a, (double x, double y) b)
        {
            double dx = a.x - b.x;
            double dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        private static ((double x, double y)? start, (double x, double y)? end) ExtractFirstLastXY(List<string> lines)
        {
            (double x, double y)? first = null;
            (double x, double y)? last = null;

            double cx = 0, cy = 0;
            bool have = false;

            foreach (var ln in lines)
            {
                var s = (ln ?? "").Trim().ToUpperInvariant();
                if (s.Length == 0) continue;

                if (TryGetWordParam(s, 'X', out double x)) { cx = x; have = true; }
                if (TryGetWordParam(s, 'Y', out double y)) { cy = y; have = true; }

                if (have)
                {
                    if (!first.HasValue) first = (cx, cy);
                    last = (cx, cy);
                }
            }

            return (first, last);
        }

        // --------------------- gcode parse/format ---------------------

        private static List<GSeg> ParseGcode(List<string> raw)
        {
            var segs = new List<GSeg>();

            double cx = 0, cy = 0, cz = 0;
            bool haveX = false, haveY = false, haveZ = false;

            foreach (var ln0 in raw)
            {
                string ln = (ln0 ?? "").Trim();
                if (ln.Length == 0) continue;

                string u = ln.ToUpperInvariant();

                // Z-only line like "G0Z-14" or "Z-14"
                if (!u.Contains("X") && !u.Contains("Y") && u.Contains("Z"))
                {
                    var seg = new GSeg();
                    seg.Cmd = u.StartsWith("G0") ? "G0" : (u.StartsWith("G1") ? "G1" : "");
                    if (TryGetWordParam(u, 'Z', out double z))
                    {
                        seg.Z = z;
                        cz = z; haveZ = true;
                    }
                    segs.Add(seg);
                    continue;
                }

                // Identify command
                string cmd = "";
                if (u.Contains("G0")) cmd = "G0";
                if (u.Contains("G1")) cmd = "G1";
                if (u.Contains("G2")) cmd = "G2";
                if (u.Contains("G3")) cmd = "G3";

                var gs = new GSeg();
                gs.Cmd = cmd;

                if (TryGetWordParam(u, 'X', out double x)) { gs.X = x; cx = x; haveX = true; }
                if (TryGetWordParam(u, 'Y', out double y)) { gs.Y = y; cy = y; haveY = true; }
                if (TryGetWordParam(u, 'Z', out double z2)) { gs.Z = z2; cz = z2; haveZ = true; }

                if (TryGetWordParam(u, 'I', out double i)) gs.I = i;
                if (TryGetWordParam(u, 'J', out double j)) gs.J = j;

                // Keep feed if present (best effort)
                if (TryGetWordParam(u, 'F', out double f))
                    gs.Tail = "F" + f.ToString("0.###", CultureInfo.InvariantCulture);

                // For missing coords, we still keep state for arcs (needed for center absolute calc)
                if (!gs.X.HasValue && haveX) gs.X = cx;
                if (!gs.Y.HasValue && haveY) gs.Y = cy;
                if (!gs.Z.HasValue && haveZ) gs.Z = cz;

                segs.Add(gs);
            }

            return segs;
        }

        private static List<string> FormatGcode(List<GSeg> segs)
        {
            var outLines = new List<string>();

            // Track start point so arcs can be emitted with correct incremental I/J
            double cx = 0, cy = 0;
            bool haveXY = false;

            foreach (var s in segs)
            {
                // Z-only line
                if (!s.X.HasValue && !s.Y.HasValue && s.Z.HasValue)
                {
                    if (!string.IsNullOrEmpty(s.Cmd))
                        outLines.Add(string.Format(CultureInfo.InvariantCulture, "{0}Z{1:0.###}", s.Cmd, s.Z.Value));
                    else
                        outLines.Add(string.Format(CultureInfo.InvariantCulture, "Z{0:0.###}", s.Z.Value));
                    continue;
                }

                string cmd = string.IsNullOrEmpty(s.Cmd) ? "G1" : s.Cmd;

                double x = s.X ?? cx;
                double y = s.Y ?? cy;

                string line = string.Format(CultureInfo.InvariantCulture, "{0}X{1:0.###}Y{2:0.###}", cmd, x, y);

                // For arcs, I/J are already incremental in our model
                if ((cmd == "G2" || cmd == "G3") && s.I.HasValue && s.J.HasValue)
                {
                    line += string.Format(CultureInfo.InvariantCulture, "I{0:0.###}J{1:0.###}", s.I.Value, s.J.Value);
                }

                if (!string.IsNullOrWhiteSpace(s.Tail))
                    line += s.Tail.StartsWith("F", StringComparison.OrdinalIgnoreCase) ? s.Tail : (" " + s.Tail);

                outLines.Add(line);

                cx = x; cy = y; haveXY = true;
            }

            return outLines;
        }

        private static bool TryGetWordParam(string s, char key, out double value)
        {
            value = 0;
            if (string.IsNullOrEmpty(s)) return false;

            int idx = s.IndexOf(key);
            if (idx < 0) return false;

            int i = idx + 1;
            if (i >= s.Length) return false;

            int start = i;
            bool saw = false;

            if (s[i] == '+' || s[i] == '-') i++;

            while (i < s.Length)
            {
                char c = s[i];
                if ((c >= '0' && c <= '9') || c == '.')
                {
                    saw = true;
                    i++;
                    continue;
                }
                break;
            }

            if (!saw) return false;

            string num = s.Substring(start, i - start);
            if (num.StartsWith(".", StringComparison.Ordinal)) num = "0" + num;
            if (num.StartsWith("+.", StringComparison.Ordinal)) num = "+0" + num.Substring(1);
            if (num.StartsWith("-.", StringComparison.Ordinal)) num = "-0" + num.Substring(1);

            return double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        // --------------------- transforms ---------------------

        private static List<GSeg> ApplyTranslate(List<GSeg> segs, double dx, double dy)
        {
            var outSegs = new List<GSeg>(segs.Count);

            foreach (var s in segs)
            {
                var t = Clone(s);

                if (t.X.HasValue) t.X = t.X.Value + dx;
                if (t.Y.HasValue) t.Y = t.Y.Value + dy;

                // For arcs, incremental IJ does not change under translation
                outSegs.Add(t);
            }

            // Recompute arc IJ from absolute centers is NOT needed because we keep incremental.
            return outSegs;
        }

        private static List<GSeg> ApplyRotate(List<GSeg> segs, double cx, double cy, double angRad)
        {
            var outSegs = new List<GSeg>(segs.Count);

            // Rotation affects XY and also arc incremental IJ (vector rotates)
            double c = Math.Cos(angRad);
            double s = Math.Sin(angRad);

            foreach (var g in segs)
            {
                var t = Clone(g);

                if (t.X.HasValue && t.Y.HasValue)
                {
                    RotAbout(cx, cy, t.X.Value, t.Y.Value, c, s, out double rx, out double ry);
                    t.X = rx; t.Y = ry;
                }

                if (t.I.HasValue && t.J.HasValue)
                {
                    // rotate vector (i,j) about origin
                    double vi = t.I.Value;
                    double vj = t.J.Value;
                    double ri = vi * c - vj * s;
                    double rj = vi * s + vj * c;
                    t.I = ri; t.J = rj;
                }

                outSegs.Add(t);
            }

            return outSegs;
        }

        private static List<GSeg> ApplyMirror(List<GSeg> segs, double x1, double y1, double x2, double y2)
        {
            var outSegs = new List<GSeg>(segs.Count);

            // Mirror across the line through (x1,y1)->(x2,y2)
            double vx = x2 - x1;
            double vy = y2 - y1;
            double len2 = vx * vx + vy * vy;
            if (len2 <= 1e-20) len2 = 1.0;

            foreach (var g in segs)
            {
                var t = Clone(g);

                if (t.X.HasValue && t.Y.HasValue)
                {
                    MirrorPoint(x1, y1, vx, vy, len2, t.X.Value, t.Y.Value, out double mx, out double my);
                    t.X = mx; t.Y = my;
                }

                if (t.I.HasValue && t.J.HasValue)
                {
                    // Mirror the vector (i,j) in the same mirror axis.
                    // Do it by mirroring the endpoint of the vector from origin and taking the result as new vector.
                    MirrorPoint(0, 0, vx, vy, len2, t.I.Value, t.J.Value, out double mi, out double mj);
                    t.I = mi; t.J = mj;
                }

                // Mirroring flips CW/CCW => swap G2/G3
                if (string.Equals(t.Cmd, "G2", StringComparison.OrdinalIgnoreCase)) t.Cmd = "G3";
                else if (string.Equals(t.Cmd, "G3", StringComparison.OrdinalIgnoreCase)) t.Cmd = "G2";

                outSegs.Add(t);
            }

            return outSegs;
        }

        private static void RotAbout(double cx, double cy, double x, double y, double c, double s, out double rx, out double ry)
        {
            double dx = x - cx;
            double dy = y - cy;
            rx = cx + (dx * c - dy * s);
            ry = cy + (dx * s + dy * c);
        }

        private static void MirrorPoint(double x1, double y1, double vx, double vy, double len2, double x, double y, out double mx, out double my)
        {
            // Project point onto line, then reflect
            double px = x - x1;
            double py = y - y1;

            double t = (px * vx + py * vy) / len2;

            double projx = x1 + t * vx;
            double projy = y1 + t * vy;

            mx = 2.0 * projx - x;
            my = 2.0 * projy - y;
        }

        private static GSeg Clone(GSeg s)
        {
            return new GSeg
            {
                Cmd = s.Cmd,
                X = s.X,
                Y = s.Y,
                Z = s.Z,
                I = s.I,
                J = s.J,
                Tail = s.Tail
            };
        }
    }
}
