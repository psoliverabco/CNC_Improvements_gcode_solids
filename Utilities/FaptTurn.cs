// File: Utilities/FaptTurn.cs
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CNC_Improvements_gcode_solids.Utilities
{
    internal static class FaptTurn
    {
        internal static List<string> TextToLines_All(string text)
        {
            if (text == null) text = "";
            text = text.Replace("\r\n", "\n").Replace("\r", "\n");
            return text.Split('\n').ToList();
        }



        // Extract name from a line like:
        // "(G1128....)FAPT TEST                                      (u:a0015)"
        // Rule: take text AFTER first ')' and stop at next '(' (so it ignores (u:...)).
        // If nothing found -> "".
        internal static string ExtractFaptRegionName(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";

            int close = line.IndexOf(')');
            if (close < 0 || close + 1 >= line.Length)
                return "";

            string tail = line.Substring(close + 1);

            // stop at next '(' (this will catch "(u:....)" etc)
            int nextParen = tail.IndexOf('(');
            if (nextParen >= 0)
                tail = tail.Substring(0, nextParen);

            tail = tail.Trim();

            return tail;
        }

        // Region 1 => 'a', Region 2 => 'b', ...
        internal static char IndexToAlpha(int idx0)
        {
            if (idx0 < 0) idx0 = 0;
            return (char)('a' + (idx0 % 26));
        }

        // Formats a generated G-code block:
        // (NAME ST)
        // <gcode lines padded to col 75 + (f:a0000) ..>
        // (NAME END)
        internal static List<string> FormatTurnGcodeBlock(string regionName, List<string> rawGcode, char alpha)
        {
            var outLines = new List<string>();

            regionName = (regionName ?? "").Trim();
            if (string.IsNullOrWhiteSpace(regionName))
                regionName = "FAPT";

            outLines.Add("(" + regionName + " ST)");

            if (rawGcode != null)
            {
                int n = 0;
                foreach (var l in rawGcode)
                {
                    if (string.IsNullOrWhiteSpace(l))
                        continue;

                    string tag = "(f:" + alpha + n.ToString("0000") + ")";
                    outLines.Add(AppendTagAtColumn75(l.TrimEnd(), tag));
                    n++;
                }
            }

            outLines.Add("(" + regionName + " END)");
            return outLines;
        }

        // Put tag starting at column 75 (1-based). If line is longer, just append with one space.
        private static string AppendTagAtColumn75(string line, string tag)
        {
            if (line == null) line = "";
            if (tag == null) tag = "";

            const int col1Based = 75;
            int idx0Based = col1Based - 1; // 74

            if (line.Length >= idx0Based)
                return line + " " + tag;

            return line.PadRight(idx0Based) + " " + tag;
        }





        

        




        /// <summary>
        /// Translate ONE selected FAPT region (list of raw lines) into TURN G-code using current rules:
        /// - X is diameter => X = 2 * V
        /// - Z is H (as-is)
        /// - Arc center given in FAPT as I(zc), J(xc) in RADIUS units
        ///   => G18 incremental I = (J - Vstart)  (DO NOT double)
        ///   => G18 incremental K = (I - Hstart)
        /// - Arc direction: compute CW/CCW from geometry and then SWAP G2/G3 (per your rule)
        /// - Ignore T2 lines (not part of shape)
        /// - Supports: G1450, G1451, G1452, G1453, G1454, G1455. Stops at G1456.
        /// - Feed F is taken from the first G112x line in the region (if present).
        /// </summary>
        public static List<string> TranslateFaptRegionToTurnGcode(List<string> regionLines)
            {
                var outLines = new List<string>();
                if (regionLines == null || regionLines.Count == 0)
                    return outLines;

                // Find feed from the first G112x line (optional)
                double? feed = null;
                foreach (var ln in regionLines)
                {
                    var u = (ln ?? "").Trim().ToUpperInvariant();
                    if (u.Contains("(G112") || u.Contains("G112"))
                    {
                        if (TryGetParam(u, 'F', out double fVal))
                        {
                            feed = fVal;
                            break;
                        }
                    }
                }

                // Gather only the shape lines we care about, stop at G1456.
                // Skip T2 lines.
                var shape = new List<string>();
                foreach (var ln in regionLines)
                {
                    var u = (ln ?? "").Trim();
                    if (u.Length == 0) continue;

                    var uu = u.ToUpperInvariant();
                    if (uu.Contains("T2")) continue;

                    if (uu.Contains("G1456"))
                    {
                        shape.Add(u);
                        break;
                    }

                    if (uu.Contains("G1450") || uu.Contains("G1451") || uu.Contains("G1452") ||
                        uu.Contains("G1453") || uu.Contains("G1454") || uu.Contains("G1455"))
                    {
                        shape.Add(u);
                    }
                }

                // Need a start point from G1450
                string g1450 = shape.FirstOrDefault(s => s.ToUpperInvariant().Contains("G1450"));
                if (g1450 == null)
                    return outLines;

                if (!TryGetParam(g1450, 'H', out double curH) || !TryGetParam(g1450, 'V', out double curV))
                    return outLines;

                // Emit plane (TURN XZ)
                //outLines.Add("G18");

                // Rapid to start
                outLines.Add(string.Format(CultureInfo.InvariantCulture,
                    "G1  X{0:0.####}  Z{1:0.####}",
                    2.0 * curV, curH));

                // Walk shape commands in order (starting after G1450)
                foreach (var ln in shape)
                {
                    var u = ln.ToUpperInvariant();

                    if (u.Contains("G1450"))
                        continue;

                    if (u.Contains("G1456"))
                        break;

                    // LINE (endpoint)
                    if (u.Contains("G1451") || u.Contains("G1454"))
                    {
                        // Endpoint comes from H,V
                        if (!TryGetParam(u, 'H', out double endH) || !TryGetParam(u, 'V', out double endV))
                            continue;

                        if (feed.HasValue)
                        {
                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "G1  X{0:0.####}  Z{1:0.####}  F{2:0.####}",
                                2.0 * endV, endH, feed.Value));
                        }
                        else
                        {
                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "G1  X{0:0.####}  Z{1:0.####}",
                                2.0 * endV, endH));
                        }

                        curH = endH;
                        curV = endV;
                        continue;
                    }

                    // ARC (fillet) and ARC (real) both treated the same here:
                    // endpoint H,V; center I(zc), J(xc)
                    if (u.Contains("G1455") || u.Contains("G1452") || u.Contains("G1453"))
                    {
                        if (!TryGetParam(u, 'H', out double endH) || !TryGetParam(u, 'V', out double endV))
                            continue;

                        // Center is I(zc), J(xc)
                        if (!TryGetParam(u, 'I', out double cenZ) || !TryGetParam(u, 'J', out double cenX))
                            continue;

                        // Compute CW/CCW from vectors (x,z) around center
                        double sx = curV, sz = curH;
                        double ex = endV, ez = endH;
                        double cx = cenX, cz = cenZ;

                        double ax = sx - cx;
                        double az = sz - cz;
                        double bx = ex - cx;
                        double bz = ez - cz;

                        // cross > 0 => CCW (math axes). Then swap G2/G3 per your rule.
                        double cross = (ax * bz) - (az * bx);
                        bool ccw = cross > 0.0;

                        // Normally: CCW => G3, CW => G2. Your rule: swapped.
                        string g = ccw ? "G2" : "G3";

                        // Incremental I (X radius units) and K (Z) from start point.
                        // Rule: I is incremental and must NOT be doubled (radius units).
                        double iInc = (cx - sx);
                        double kInc = (cz - sz);

                        if (feed.HasValue)
                        {
                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "{0}  X{1:0.####}  Z{2:0.####}  I{3:0.####}  K{4:0.####}  F{5:0.####}",
                                g,
                                2.0 * endV, endH,
                                iInc, kInc,
                                feed.Value));
                        }
                        else
                        {
                            outLines.Add(string.Format(CultureInfo.InvariantCulture,
                                "{0}  X{1:0.####}  Z{2:0.####}  I{3:0.####}  K{4:0.####}",
                                g,
                                2.0 * endV, endH,
                                iInc, kInc));
                        }

                        curH = endH;
                        curV = endV;
                        continue;
                    }
                }

                return outLines;
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

                // Accept "F.3" and "F0.3"
                int i = idx + 1;
                if (i >= s.Length)
                    return false;

                // Read sign + digits + dot
                int start = i;
                bool sawAny = false;

                // optional sign
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

                // Handle ".3" => "0.3"
                if (num.StartsWith(".", StringComparison.Ordinal))
                    num = "0" + num;
                if (num.StartsWith("+.", StringComparison.Ordinal))
                    num = "+0" + num.Substring(1);
                if (num.StartsWith("-.", StringComparison.Ordinal))
                    num = "-0" + num.Substring(1);

                if (!double.TryParse(num, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    return false;

                return true;
            }















        // REPLACE the old PickRegionIndex(...) with this:

        // Multi-select picker:
        // - ListBox SelectionMode=Extended (shift/ctrl)
        // - Buttons: All / None / OK / Cancel
        // Returns selected indices (sorted). Empty list = cancel or none selected.
        // Shows a multi-select dialog for FAPT regions.
        // IMPORTANT: The ListBox items carry the original RegionIndex so selection maps correctly.
        internal static List<int> PickRegionIndices(Window owner, List<List<string>> regions)
        {
            if (regions == null) return new List<int>();

            // Item that keeps original region index
            var items = new List<RegionPickItem>();

            for (int i = 0; i < regions.Count; i++)
            {
                var r = regions[i];
                if (r == null || r.Count == 0) continue;

                string first = r[0] ?? "";
                string name = ExtractRegionNameFromG112Line(first);

                // Your rule: if no name after ')' then don't show / don't preview
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                items.Add(new RegionPickItem { RegionIndex = i, Name = name.Trim() });
            }

            if (items.Count == 0)
                return new List<int>();

            // ---- build dialog ----
            var w = new Window
            {
                Title = "Pick FAPT Regions",
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 520,
                Height = 520,
                ResizeMode = ResizeMode.CanResize,
                Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11))
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            w.Content = root;

            var header = new TextBlock
            {
                Text = "Select FAPT regions (multi-select)",
                Foreground = Brushes.LightGray,
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            var list = new ListBox
            {
                SelectionMode = SelectionMode.Extended,
                Background = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x0D)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness = new Thickness(1),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                ItemsSource = items
            };

            // Display only Name, but keep RegionIndex in the object
            list.DisplayMemberPath = nameof(RegionPickItem.Name);

            Grid.SetRow(list, 1);
            root.Children.Add(list);

            var btnRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            Grid.SetRow(btnRow, 2);
            root.Children.Add(btnRow);

            var btnAll = new Button
            {
                Content = "All",
                Width = 90,
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnAll.Click += (s, e) =>
            {
                list.SelectAll();
                list.Focus();
            };
            btnRow.Children.Add(btnAll);

            var btnOk = new Button
            {
                Content = "OK",
                Width = 90,
                Margin = new Thickness(0, 0, 8, 0)
            };
            btnRow.Children.Add(btnOk);

            var btnCancel = new Button
            {
                Content = "Cancel",
                Width = 90
            };
            btnRow.Children.Add(btnCancel);

            List<int> picked = new List<int>();

            btnOk.Click += (s, e) =>
            {
                picked.Clear();

                foreach (var obj in list.SelectedItems)
                {
                    if (obj is RegionPickItem it)
                        picked.Add(it.RegionIndex); // <-- correct mapping
                }

                w.DialogResult = true;
                w.Close();
            };

            btnCancel.Click += (s, e) =>
            {
                picked.Clear();
                w.DialogResult = false;
                w.Close();
            };

            // show
            bool? res = w.ShowDialog();
            if (res != true) return new List<int>();

            // Ensure stable order (optional)
            picked.Sort();
            return picked;
        }

        // Helper DTO for the picker
        private sealed class RegionPickItem
        {
            public int RegionIndex { get; set; }
            public string Name { get; set; } = "";
        }




        // Tag column for "(f:axxxx)" insertion.
        // Rule: if the line is shorter than this, pad spaces so the tag starts here.
        // If the line is already >= this length, just add " " + tag at the end.
        private const int FAPT_TAG_COLUMN = 75;

        // Builds final output:
        //   (NAME ST)
        //   <gcode lines each tagged at col 75 with (f:a0000)...>
        //   (NAME END)
        

        private static string BuildFaptTag(int seq)
        {
            // a0000 style
            if (seq < 0) seq = 0;
            if (seq > 9999) seq = 9999;
            return "(f:a" + seq.ToString("0000", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        

       







        // CRAZY SIMPLE:
        // region starts when line contains "(G112"
        // then include following lines while they contain "(G145"
        // region[i][0] is the G112 line
        internal static List<List<string>> BuildFaptRegions(List<string> lines)
        {
            var regions = new List<List<string>>();
            if (lines == null) return regions;

            int i = 0;
            while (i < lines.Count)
            {

                string s = lines[i] ?? "";
                s = s.ToUpperInvariant();

                bool isStart =
                    s.Contains("(G1125") ||
                    s.Contains("(G1126") ||
                    s.Contains("(G1127") ||
                    s.Contains("(G1128");



                //string s = lines[i] ?? "";

                if (!isStart)
                {
                    i++;
                    continue;
                }

                var region = new List<string>();
                region.Add(s);

                int j = i + 1;
                while (j < lines.Count)
                {
                    string t = lines[j] ?? "";
                    if (Contains(t, "(G145"))
                    {
                        region.Add(t);
                        j++;
                        continue;
                    }
                    break;
                }

                regions.Add(region);
                i = j;
            }

            return regions;
        }

        

        internal static void ShowTextWindow(Window owner, string title, string text)
        {
            var w = new Window
            {
                Title = title,
                Width = 1100,
                Height = 700,
                Owner = owner,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };

            w.Content = new TextBox
            {
                Text = text ?? "",
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                FontSize = 14,
                IsReadOnly = true,
                TextWrapping = TextWrapping.NoWrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            w.Show();
        }

        private static bool Contains(string s, string needle)
        {
            return !string.IsNullOrEmpty(s) &&
                   s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }



        // Extract the region name that is appended after the closing ')' of the (G112x...) line,
        // ignoring any trailing unique tag like "(u:a0015)".
        //
        // Rules:
        // - Find first ')' in the line.
        // - Take text after it.
        // - If there is another '(' later (e.g. the unique tag), stop before that '('.
        // - Else use to end-of-line.
        // - Trim whitespace.
        // - If empty => return "".
        internal static string ExtractRegionNameFromG112Line(string line)
        {
            if (string.IsNullOrEmpty(line))
                return "";

            int closeIdx = line.IndexOf(')');
            if (closeIdx < 0)
                return "";

            int start = closeIdx + 1;
            if (start >= line.Length)
                return "";

            // Find the next '(' after the closing ')'
            int nextParen = line.IndexOf('(', start);

            string rawName;
            if (nextParen >= 0)
                rawName = line.Substring(start, nextParen - start);
            else
                rawName = line.Substring(start);

            rawName = rawName.Trim();

            return rawName; // may be ""
        }





    }
}
