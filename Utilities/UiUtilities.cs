using CNC_Improvements_gcode_solids.Pages;
using CNC_Improvements_gcode_solids.SetManagement;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities
{
    /// <summary>
    /// UI helper utilities for WPF brushes, colours, etc.
    /// </summary>
    public static class UiUtilities
    {

        







        /// <summary>
        /// Clears ALL loaded project data in MainWindow:
        /// - GcodeLines + GcodeEditor text
        /// - TurnSets / MillSets / DrillSets
        /// - any transform matrix collections if found
        /// - selected set references if found
        /// - any other RegionSet collections (reflection fallback)
        ///
        /// This is intended for: user typed a NEW project name that doesn't exist.
        /// No restart, no file IO here.
        /// </summary>
        public static void HardResetProjectState(object mainWindow, string? newProjectNameForTitle, bool preserveFirstTransform)
        {
            if (mainWindow == null) return;

            // 1) Clear editor text + backing GcodeLines if present
            TryClearGcodeLines(mainWindow);
            TryClearRichTextEditor(mainWindow);

            // 2) Clear known RegionSet collections
            TryClearCollectionProperty(mainWindow, "TurnSets");
            TryClearCollectionProperty(mainWindow, "MillSets");
            TryClearCollectionProperty(mainWindow, "DrillSets");

            // 3) Clear transforms BUT optionally keep first item (standard)
            if (preserveFirstTransform)
            {
                TryClearCollectionPropertyKeepingFirst(mainWindow, "TransformMatrices");
                TryClearCollectionPropertyKeepingFirst(mainWindow, "TransformMatrixSets");
                TryClearCollectionPropertyKeepingFirst(mainWindow, "TransMatrixSets");
                TryClearCollectionPropertyKeepingFirst(mainWindow, "TransMatrices");
                TryClearCollectionPropertyKeepingFirst(mainWindow, "TransSets");
            }
            else
            {
                TryClearCollectionProperty(mainWindow, "TransformMatrices");
                TryClearCollectionProperty(mainWindow, "TransformMatrixSets");
                TryClearCollectionProperty(mainWindow, "TransMatrixSets");
                TryClearCollectionProperty(mainWindow, "TransMatrices");
                TryClearCollectionProperty(mainWindow, "TransSets");
            }

            // 4) Clear likely selected-set properties
            TrySetNull(mainWindow, "SelectedTurnSet");
            TrySetNull(mainWindow, "SelectedMillSet");
            TrySetNull(mainWindow, "SelectedDrillSet");

            // 5) Optional: update window title
            if (!string.IsNullOrWhiteSpace(newProjectNameForTitle))
                TrySetString(mainWindow, "Title", "CNC_Improvements_gcode_solids - " + newProjectNameForTitle!.Trim());
        }

        // KEEP your existing method, but change it to call the overload:
        public static void HardResetProjectState(object mainWindow, string? newProjectNameForTitle = null)
        {
            HardResetProjectState(mainWindow, newProjectNameForTitle, preserveFirstTransform: false);
        }

        private static void TryClearCollectionPropertyKeepingFirst(object target, string propName)
        {
            object? obj = TryGetProp(target, propName);
            if (obj == null) return;

            if (obj is System.Collections.IList list)
            {
                try
                {
                    // keep index 0, remove 1..end
                    for (int i = list.Count - 1; i >= 1; i--)
                        list.RemoveAt(i);
                }
                catch { }
                return;
            }

            // fallback: if not IList, we can't safely keep-first
            TryInvokeClear(obj);
        }


        // -------------------- internals --------------------

        private static void TryClearGcodeLines(object mainWindow)
        {
            // Typical: public List<string> GcodeLines {get;}
            object? obj = TryGetProp(mainWindow, "GcodeLines");
            if (obj is IList list)
            {
                try { list.Clear(); } catch { }
            }
        }

        private static void TryClearRichTextEditor(object mainWindow)
        {
            // Typical: public RichTextBox GcodeEditor {get;}
            object? obj = TryGetProp(mainWindow, "GcodeEditor");
            if (obj is RichTextBox rtb)
            {
                try
                {
                    rtb.Document.Blocks.Clear();
                    // Keep document valid; optionally add empty paragraph
                    rtb.Document.Blocks.Add(new Paragraph(new Run("")));
                }
                catch { }
            }
        }

        private static void TryClearCollectionProperty(object target, string propName)
        {
            object? obj = TryGetProp(target, propName);
            if (obj == null) return;

            if (obj is IList list)
            {
                try { list.Clear(); } catch { }
                return;
            }

            if (obj is ICollection coll)
            {
                // ICollection non-generic doesn't have Clear. Try reflection.
                TryInvokeClear(obj);
                return;
            }

            // generic collection but not IList/ICollection (rare)
            TryInvokeClear(obj);
        }

       

       
        

        private static void TryInvokeClear(object obj)
        {
            try
            {
                var mi = obj.GetType().GetMethod("Clear", BindingFlags.Instance | BindingFlags.Public);
                if (mi != null && mi.GetParameters().Length == 0)
                    mi.Invoke(obj, null);
            }
            catch { }
        }

        private static void TrySetNull(object target, string propName)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null) return;
                if (!p.CanWrite) return;

                // only set null on ref types / nullable
                if (p.PropertyType.IsValueType && Nullable.GetUnderlyingType(p.PropertyType) == null)
                    return;

                p.SetValue(target, null);
            }
            catch { }
        }

        private static void TrySetString(object target, string propName, string value)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null) return;
                if (!p.CanWrite) return;
                if (p.PropertyType != typeof(string)) return;

                p.SetValue(target, value);
            }
            catch { }
        }

        private static object? TryGetProp(object target, string propName)
        {
            try
            {
                var p = target.GetType().GetProperty(propName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (p == null) return null;
                return p.GetValue(target);
            }
            catch
            {
                return null;
            }
        }
















        /// <summary>
        /// Closes any open viewer/log windows we know about.
        /// Call this at the start of Export / View clicks to avoid window spam.
        /// </summary>
        public static void CloseAllToolWindows()
        {
            if (Application.Current == null)
                return;

            // NOTE: hard-coded window types (as requested).
            // Add more here later when you create new tool windows.
            CloseByType(typeof(LogWindow));
            CloseByType(typeof(MillViewWindow));
            CloseByType(typeof(DrillViewWindow));
            CloseByType(typeof(ProfileViewWindow));
            CloseByType(typeof(TurnEditWindow));
            CloseByType(typeof(DrillViewWindowV2));
            CloseByType(typeof(ColorPickerWindow));
        }

        private static void CloseByType(Type t)
        {
            if (Application.Current == null)
                return;

            var toClose = Application.Current.Windows
                .OfType<Window>()
                .Where(w => w != null && w.GetType() == t)
                .ToList(); // materialize before closing

            foreach (var w in toClose)
            {
                try { w.Close(); }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Returns a Brush from a XAML-style colour string:
        ///   "#RGB", "#ARGB", "#RRGGBB", "#AARRGGBB"
        /// Same formats as XAML; case-insensitive.
        /// </summary>
        public static Brush HexBrush(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Colour string cannot be null or empty.", nameof(value));

            string hex = value.Trim();

            // Strip leading '#' if present
            if (hex.StartsWith("#"))
                hex = hex.Substring(1);

            string aStr, rStr, gStr, bStr;

            switch (hex.Length)
            {
                case 3:
                    // #RGB => #RRGGBB with alpha = FF
                    aStr = "FF";
                    rStr = new string(hex[0], 2);
                    gStr = new string(hex[1], 2);
                    bStr = new string(hex[2], 2);
                    break;

                case 4:
                    // #ARGB => #AARRGGBB (each nibble doubled)
                    aStr = new string(hex[0], 2);
                    rStr = new string(hex[1], 2);
                    gStr = new string(hex[2], 2);
                    bStr = new string(hex[3], 2);
                    break;

                case 6:
                    // #RRGGBB => alpha = FF
                    aStr = "FF";
                    rStr = hex.Substring(0, 2);
                    gStr = hex.Substring(2, 2);
                    bStr = hex.Substring(4, 2);
                    break;

                case 8:
                    // #AARRGGBB
                    aStr = hex.Substring(0, 2);
                    rStr = hex.Substring(2, 2);
                    gStr = hex.Substring(4, 2);
                    bStr = hex.Substring(6, 2);
                    break;

                default:
                    throw new FormatException(
                        $"Invalid colour string length '{hex.Length}' for '{value}'. " +
                        "Expected 3, 4, 6 or 8 hex digits.");
            }

            byte a = ParseHexByte(aStr);
            byte r = ParseHexByte(rStr);
            byte g = ParseHexByte(gStr);
            byte b = ParseHexByte(bStr);

            var color = Color.FromArgb(a, r, g, b);


            
                return new SolidColorBrush(color);
        }

        /// <summary>
        /// Convenience: create a Brush from explicit ARGB bytes.
        /// </summary>
        public static Brush ArgbBrush(byte a, byte r, byte g, byte b)
        {
            var color = Color.FromArgb(a, r, g, b);
            return new SolidColorBrush(color);
        }

        private static byte ParseHexByte(string hex)
        {
            try
            {
                return byte.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                throw new FormatException($"Invalid hex component '{hex}' in colour string.", ex);
            }
        }




        // ============================================================
        // NUMBERED LINE HELPERS (RichTextBox FlowDocument)
        // Assumes lines are rendered as paragraphs beginning with "NNNN:"
        // (your RefreshHighlighting uses: $"{i + 1,5}: ")
        // ============================================================

        /// <summary>
        /// Try to find the Paragraph/TextPointer for a given 1-based numbered line
        /// in a FlowDocument that contains line numbers as leading text ("1234:").
        /// This is LOGICAL-line based (paragraphs), so word-wrap does not matter.
        /// </summary>
        public static bool TryFindNumberedLineStart(
            FlowDocument doc,
            int lineNo1Based,
            out Paragraph? paragraph,
            out TextPointer? lineStart)
        {
            paragraph = null;
            lineStart = null;

            if (doc == null)
                return false;

            if (lineNo1Based <= 0)
                return false;

            foreach (Block b in doc.Blocks)
            {
                if (b is not Paragraph p)
                    continue;

                string t = new TextRange(p.ContentStart, p.ContentEnd).Text ?? "";
                t = t.Replace("\r", "").Replace("\n", "");

                if (!TryParseLeadingNumberedPrefix(t, out int n))
                    continue;

                if (n == lineNo1Based)
                {
                    paragraph = p;
                    lineStart = p.ContentStart;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Convenience overload for RichTextBox.
        /// </summary>
        public static bool TryFindNumberedLineStart(
            RichTextBox rtb,
            int lineNo1Based,
            out Paragraph? paragraph,
            out TextPointer? lineStart)
        {
            paragraph = null;
            lineStart = null;

            if (rtb == null || rtb.Document == null)
                return false;

            return TryFindNumberedLineStart(rtb.Document, lineNo1Based, out paragraph, out lineStart);
        }

        /// <summary>
        /// Build an index of lineNo -> Paragraph for fast lookups.
        /// Call this right after any RefreshHighlighting() (or other editor rebuild).
        /// </summary>
        public static Dictionary<int, Paragraph> BuildNumberedLineIndex(FlowDocument doc)
        {
            var map = new Dictionary<int, Paragraph>();

            if (doc == null)
                return map;

            foreach (Block b in doc.Blocks)
            {
                if (b is not Paragraph p)
                    continue;

                string t = new TextRange(p.ContentStart, p.ContentEnd).Text ?? "";
                t = t.Replace("\r", "").Replace("\n", "");

                if (!TryParseLeadingNumberedPrefix(t, out int n))
                    continue;

                // first wins (should be unique anyway)
                if (!map.ContainsKey(n))
                    map[n] = p;
            }

            return map;
        }




        /// <summary>
        /// Adds the standard "NNNNN: " line number prefix run to a paragraph,
        /// matching the formatting used by RefreshHighlighting.
        /// </summary>
        public static void AddNumberedLinePrefix(Paragraph p, int lineNo1Based, Brush foreground, int padWidth = 5)
        {
            if (p == null)
                return;

            if (lineNo1Based < 1)
                lineNo1Based = 1;

            if (padWidth < 1)
                padWidth = 1;

            // Match $"{i + 1,5}: " behaviour (left padded)
            string num = lineNo1Based.ToString(CultureInfo.InvariantCulture).PadLeft(padWidth, ' ');
            string prefix = num + ": ";

            p.Inlines.Add(new Run(prefix) { Foreground = foreground });

            


        }

        // ============================================================
        // GCODE EDITOR LINE INDEX (lineNo -> TextPointer)
        // Stores the index on the RichTextBox via Resources so any page can rebuild it
        // and MainWindow (JumpEditorToRegionStart) can consume it.
        // ============================================================

        public const string GcodeLineStartIndexResourceKey = "__GCODE_LINE_START_INDEX__";

        /// <summary>
        /// Build a lookup index from leading "NNNN:" prefixes to the paragraph start pointer.
        /// This is LOGICAL-line based (Paragraphs), so word-wrap does not matter.
        /// </summary>
        public static Dictionary<int, TextPointer> BuildNumberedLineStartIndex(FlowDocument doc)
        {
            var map = new Dictionary<int, TextPointer>();

            if (doc == null)
                return map;

            foreach (Block b in doc.Blocks)
            {
                if (b is not Paragraph p)
                    continue;

                string t = new TextRange(p.ContentStart, p.ContentEnd).Text ?? "";
                t = t.Replace("\r", "").Replace("\n", "");

                if (!TryParseLeadingNumberedPrefix(t, out int n))
                    continue;

                // First wins (should be unique)
                if (!map.ContainsKey(n))
                    map[n] = p.ContentStart;
            }

            return map;
        }

        /// <summary>
        /// Rebuild the index from the current RichTextBox document and store it in rtb.Resources.
        /// Call this at the end of every RefreshHighlighting() (all 3 places).
        /// </summary>
        // File: Utilities/UiUtilities.cs

        public static void RebuildAndStoreNumberedLineStartIndex(RichTextBox rtb)
        {
            if (rtb == null || rtb.Document == null)
                return;

            var map = BuildNumberedLineStartIndex(rtb.Document);
            rtb.Resources[GcodeLineStartIndexResourceKey] = map;

            // After numbering is rebuilt, colorize any (u:)/(t:)/(m:)/(d:) suffix tags for DISPLAY ONLY
            ColorizeUniqueTagsInRichTextBox(rtb);
        }


        // Unique tag styling: (u:xxxx) / (t:xxxx) / (m:xxxx) / (d:xxxx)
        private static readonly Brush UniqueTagBrush = UniqueTagColor.UniqueTagBrush;

        // Put near your other constants:
        private static readonly string[] UNIQUE_TAG_MARKERS = new[]
        {
    "(u:", "(t:", "(m:", "(d:"
};

        private static void ColorizeUniqueTagsInRichTextBox(RichTextBox rtb)
        {
            if (rtb?.Document == null)
                return;

            // IMPORTANT:
            // Do NOT foreach over Document.Blocks while editing Runs/Inlines.
            // Copy blocks first to avoid "Collection was modified" from WPF enumerators.
            var blocks = rtb.Document.Blocks.ToList();

            // Optional: prevents tag colour blending with highlighted paragraph backgrounds
            Brush tagBg = rtb.Background;
            if (tagBg == null)
                tagBg = SystemColors.WindowBrush;
            else if (tagBg is SolidColorBrush scb && scb.Color.A == 0)
                tagBg = SystemColors.WindowBrush;

            foreach (var block in blocks)
            {
                if (block is not Paragraph p)
                    continue;

                // Copy runs first so we can safely edit the InlineCollection
                var runs = p.Inlines.OfType<Run>().ToList();

                foreach (var run in runs)
                {
                    string text = run.Text ?? "";
                    if (text.Length == 0)
                        continue;

                    // If this run is already a tag-only run and already coloured, skip
                    // (prevents repeated splitting / runaway changes)
                    if (run.Foreground == UniqueTagBrush &&
                        StartsWithAnyUniqueMarker(text) &&
                        text.EndsWith(")", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Find the last occurrence of ANY allowed marker in this run
                    int idx = LastIndexOfAnyUniqueMarker(text);
                    if (idx < 0)
                        continue;

                    int close = text.IndexOf(')', idx);
                    if (close < 0)
                        continue;

                    // Only colorize if the tag is at the END of this run
                    if (close != text.Length - 1)
                        continue;

                    string baseText = text.Substring(0, idx);
                    string tagText = text.Substring(idx);

                    var baseRun = new Run(baseText)
                    {
                        Foreground = run.Foreground,
                        Background = run.Background,
                        FontFamily = run.FontFamily,
                        FontSize = run.FontSize,
                        FontStyle = run.FontStyle,
                        FontWeight = run.FontWeight,
                        FontStretch = run.FontStretch
                    };

                    var tagRun = new Run(tagText)
                    {
                        Foreground = UniqueTagBrush,
                        Background = tagBg, // prevents blending with pink/yellow paragraph backgrounds
                        FontFamily = run.FontFamily,
                        FontSize = run.FontSize,
                        FontStyle = run.FontStyle,
                        FontWeight = run.FontWeight,
                        FontStretch = run.FontStretch
                    };

                    p.Inlines.InsertBefore(run, baseRun);
                    p.Inlines.InsertAfter(baseRun, tagRun);
                    p.Inlines.Remove(run);
                }
            }
        }













        private static bool StartsWithAnyUniqueMarker(string text)
        {
            for (int i = 0; i < UNIQUE_TAG_MARKERS.Length; i++)
            {
                if (text.StartsWith(UNIQUE_TAG_MARKERS[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static int LastIndexOfAnyUniqueMarker(string text)
        {
            int best = -1;
            for (int i = 0; i < UNIQUE_TAG_MARKERS.Length; i++)
            {
                int idx = text.LastIndexOf(UNIQUE_TAG_MARKERS[i], StringComparison.OrdinalIgnoreCase);
                if (idx > best)
                    best = idx;
            }
            return best;
        }








        /// <summary>
        /// Try to fetch the stored line index from rtb.Resources.
        /// </summary>
        public static bool TryGetStoredNumberedLineStartIndex(RichTextBox rtb, out Dictionary<int, TextPointer> map)
        {
            map = new Dictionary<int, TextPointer>();

            if (rtb == null)
                return false;

            if (rtb.Resources.Contains(GcodeLineStartIndexResourceKey) &&
                rtb.Resources[GcodeLineStartIndexResourceKey] is Dictionary<int, TextPointer> m)
            {
                map = m;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Get the TextPointer for a numbered line from the stored index.
        /// If the index doesn't exist, it is built and stored automatically.
        /// </summary>
        public static bool TryGetNumberedLineStartPointer(RichTextBox rtb, int lineNo1Based, out TextPointer? ptr)
        {
            ptr = null;

            if (rtb == null || rtb.Document == null)
                return false;

            if (lineNo1Based <= 0)
                return false;

            if (!TryGetStoredNumberedLineStartIndex(rtb, out var map) || map.Count == 0)
            {
                RebuildAndStoreNumberedLineStartIndex(rtb);
                if (!TryGetStoredNumberedLineStartIndex(rtb, out map))
                    return false;
            }

            if (map.TryGetValue(lineNo1Based, out TextPointer p))
            {
                ptr = p;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Parses leading "NNNN:" (with optional left padding spaces).
        /// </summary>
        private static bool TryParseLeadingNumberedPrefix(string line, out int lineNo1Based)
        {
            lineNo1Based = 0;

            if (string.IsNullOrWhiteSpace(line))
                return false;

            string t = line.TrimStart();

            int i = 0;
            while (i < t.Length && char.IsDigit(t[i]))
                i++;

            if (i == 0)
                return false;

            if (i >= t.Length || t[i] != ':')
                return false;

            if (!int.TryParse(t.Substring(0, i), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return false;

            if (n <= 0)
                return false;

            lineNo1Based = n;
            return true;
        }




    }
}
