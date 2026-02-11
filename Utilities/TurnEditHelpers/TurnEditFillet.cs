// File: Utilities/TurnEditHelpers/TurnEditFillet.cs
using CNC_Improvements_gcode_solids.Properties;
using CNC_Improvements_gcode_solids.Utilities; // TurnEditArcLaw
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CNC_Improvements_gcode_solids.Utilities.TurnEditHelpers
{
    internal static class TurnEditFillet
    {
        // ------------------------------------------------------------
        // Host: TurnEditWindow supplies preview + logging only.
        // Tool returns ONE region-input segment line on Keep.
        // ------------------------------------------------------------
        internal interface IHost
        {
            bool MapValid { get; }
            void ClearPreviewOnly();

            // Preview drawing in WORLD space (host handles mapping + TAG_PREVIEW)
            void DrawPreviewPolylineWorld(IReadOnlyList<Point> worldPts, Brush stroke, double thickness, double opacity);
            void DrawPreviewPointWorld(Point worldPt, Brush fill, double diamPx, double opacity);

            // Logging (optional)
            bool LogWindowShow { get; }
            void ShowLogWindow(string title, string text);
        }

        // ------------------------------------------------------------
        // Tool-side segment views (no EditSeg dependency)
        // World coords: Point.X = X, Point.Y = Z
        // ------------------------------------------------------------
        internal enum SegKind { Line, Arc }

       
        internal sealed class SegView
        {
            public SegKind Kind;
            public Point A;
            public Point B;

            // Arc-only:
            public Point M;
            public Point C;
            public bool CCW;

            public double Radius => (Kind == SegKind.Arc) ? TurnEditMath.Dist(A, C) : 0.0;
        }

        internal sealed class Pick
        {
            public SegView Seg = null!;
            public Point PickEndWorld;   // chosen endpoint (X,Z)
        }






       





        // ------------------------------------------------------------
        // Public entry
        // ------------------------------------------------------------
        public static bool Run(
    Pick aPick,
    Pick bPick,
    IHost host,
    out string? regionInputLine,
    out string statusText,
    double? seedRadius)
        {
            // IMPORTANT: don't touch out params inside lambdas.
            string? outLineLocal = null;
            string statusLocal = "";

            regionInputLine = null;
            statusText = "";

            if (host == null) throw new ArgumentNullException(nameof(host));
            if (aPick == null || bPick == null || aPick.Seg == null || bPick.Seg == null)
            {
                statusLocal = "Fillet: invalid selection.";
                statusText = statusLocal;
                return false;
            }

            if (!host.MapValid)
            {
                statusLocal = "Fillet: view not ready.";
                statusText = statusLocal;
                return false;
            }

            bool keepAccepted = false;

            List<TurnEditArcLaw.FilletArcData> lastCandidates = new();
            double lastR = 0.0;
            int highlightIndex = -1;

            var w = new Window
            {
                Title = "Fillet Circles",
                Width = 620,
                Height = 260,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ResizeMode = ResizeMode.NoResize,
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
                Owner = Application.Current?.MainWindow
            };

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // radius row
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // info row
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // controls row
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hint row

            var row0 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            row0.Children.Add(new TextBlock
            {
                Text = "Radius:",
                Foreground =   UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                Width = 70,
                VerticalAlignment = VerticalAlignment.Center
            });

            var tbRad = new TextBox
            {
                Width = 140,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Text = (seedRadius.HasValue && seedRadius.Value > 1e-12)
    ? seedRadius.Value.ToString("0.###", CultureInfo.InvariantCulture)
    : "1.0"
            };
            row0.Children.Add(tbRad);

            Grid.SetRow(row0, 0);
            root.Children.Add(row0);

            var txtInfo = new TextBlock
            {
                Text = "Candidates: 0",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor),
                FontFamily = new FontFamily("Consolas"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(txtInfo, 1);
            root.Children.Add(txtInfo);

            var row2 = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row2.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            var btnCycle = new Button
            {
                Content = "Cycle thru fillets",
                Width = 160,
                Margin = new Thickness(0, 0, 12, 0)
            };

            //var chkTrim = new CheckBox
           // {
              //  Content = "Trim",
              //  Foreground = _graphicText,
              //  VerticalAlignment = VerticalAlignment.Center,
              //  IsChecked = false
           // };

            leftControls.Children.Add(btnCycle);
            //leftControls.Children.Add(chkTrim);

            Grid.SetColumn(leftControls, 0);
            row2.Children.Add(leftControls);

            var rightControls = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var btnKeep = new Button { Content = "Keep", Width = 120, Margin = new Thickness(0, 0, 10, 0) };
            var btnCancel = new Button { Content = "Cancel", Width = 120 };

            rightControls.Children.Add(btnKeep);
            rightControls.Children.Add(btnCancel);

            Grid.SetColumn(rightControls, 1);
            row2.Children.Add(rightControls);

            Grid.SetRow(row2, 2);
            root.Children.Add(row2);

            var hint = new TextBlock
            {
                Text = "Shows FILLET ARC candidates (50% opacity). Cycle highlights one (100%). Keep returns the highlighted candidate as ARC3_* region input text.",
                Foreground = UiUtilities.HexBrush(Settings.Default.GraphicTextColor)
            };
            Grid.SetRow(hint, 3);
            root.Children.Add(hint);

            w.Content = root;

            // ------------------------------------------------------------
            // Local helpers
            // ------------------------------------------------------------
            static string GetPairTypeLabel(SegView a, SegView b)
            {
                bool aL = a.Kind == SegKind.Line;
                bool bL = b.Kind == SegKind.Line;
                bool aA = a.Kind == SegKind.Arc;
                bool bA = b.Kind == SegKind.Arc;

                if (aL && bL) return "LINE-LINE";
                if ((aL && bA) || (aA && bL)) return "LINE-ARC";
                if (aA && bA) return "ARC-ARC";
                return "UNSUPPORTED";
            }

            static bool TryParseRadius(string? s, out double r)
            {
                r = 0.0;
                string t = (s ?? "").Trim();
                if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out r))
                    return false;
                return r > 1e-12;
            }

            void RenderCandidatesWithHighlight(List<TurnEditArcLaw.FilletArcData> list, int hiIndex, double baseOpacity, double hiOpacity)
            {
                if (!host.MapValid) return;
                if (list == null || list.Count == 0) return;

                const int ARC_SAMPLES = 96;
                const double MARK_DIAM_PX = 7.0;

                for (int i = 0; i < list.Count; i++)
                {
                    var d = list[i];

                    bool isHighlight = (i == hiIndex);
                    double op = isHighlight ? hiOpacity : baseOpacity;
                    if (op <= 0.0) continue;

                    double sweepSigned = +d.ShortSweep;
                    double thickness = d.Is180Complement ? 1.5 : 3.0;

                    var worldPts = TurnEditMath.SampleArc_CWFromZ(d.CP, d.R, d.AngTan1, sweepSigned, ARC_SAMPLES);
                    if (worldPts.Count >= 2)
                        host.DrawPreviewPolylineWorld(worldPts, Brushes.Yellow, thickness, op);

                    host.DrawPreviewPointWorld(d.Tan1, Brushes.Red, MARK_DIAM_PX, op);
                    host.DrawPreviewPointWorld(d.Tan2, Brushes.Lime, MARK_DIAM_PX, op);
                    host.DrawPreviewPointWorld(d.MidShort, Brushes.Orange, MARK_DIAM_PX, op);
                    host.DrawPreviewPointWorld(d.CP, Brushes.DeepSkyBlue, MARK_DIAM_PX, op);
                }
            }

            void RecomputePreview()
            {
                highlightIndex = -1;

                if (!TryParseRadius(tbRad.Text, out double r))
                {
                    lastCandidates = new List<TurnEditArcLaw.FilletArcData>();
                    lastR = 0.0;

                    host.ClearPreviewOnly();
                    txtInfo.Text = "Candidates: 0 (invalid radius)";
                    return;
                }

                List<Point> centers = BuildAllFilletCentersForSelection(aPick.Seg, bPick.Seg, r);

                lastCandidates = new List<TurnEditArcLaw.FilletArcData>();
                lastR = r;

                string pairLabel = GetPairTypeLabel(aPick.Seg, bPick.Seg);

                bool TryTanA(Point cp, double rr, out Point tan) => TryGetTangentPointOnSeg(aPick.Seg, cp, rr, out tan);
                bool TryTanB(Point cp, double rr, out Point tan) => TryGetTangentPointOnSeg(bPick.Seg, cp, rr, out tan);

                TurnEditArcLaw.BuildCandidates(lastCandidates, pairLabel, r, centers, TryTanA, TryTanB);

                host.ClearPreviewOnly();
                RenderCandidatesWithHighlight(lastCandidates, highlightIndex, baseOpacity: 0.50, hiOpacity: 1.00);

                txtInfo.Text = $"Candidates: {lastCandidates.Count}   Pair: {pairLabel}   r={r.ToString("0.###", CultureInfo.InvariantCulture)}   Highlight: -";

                
            }

            void RedrawPreviewAll()
            {
                host.ClearPreviewOnly();

                if (lastCandidates == null || lastCandidates.Count == 0 || lastR <= 1e-9)
                    return;

                RenderCandidatesWithHighlight(lastCandidates, highlightIndex, baseOpacity: 0.50, hiOpacity: 1.00);
            }

            // ------------------------------------------------------------
            // UI events
            // ------------------------------------------------------------
            tbRad.TextChanged += (_, __) => RecomputePreview();

            btnCycle.Click += (_, __) =>
            {
                if (lastCandidates == null || lastCandidates.Count == 0)
                {
                    txtInfo.Text = "Candidates: 0 (nothing to cycle)";
                    return;
                }

                if (highlightIndex < 0) highlightIndex = 0;
                else highlightIndex = (highlightIndex + 1) % lastCandidates.Count;

                RedrawPreviewAll();

                string pairLabel = GetPairTypeLabel(aPick.Seg, bPick.Seg);
                txtInfo.Text = $"Candidates: {lastCandidates.Count}   Pair: {pairLabel}   r={lastR.ToString("0.###", CultureInfo.InvariantCulture)}   Highlight: {highlightIndex + 1}/{lastCandidates.Count}";
            };

            btnKeep.Click += (_, __) =>
            {
                if (lastCandidates == null || lastCandidates.Count == 0 || lastR <= 1e-9)
                {
                    txtInfo.Text = "Nothing to keep.";
                    return;
                }

                if (highlightIndex < 0 || highlightIndex >= lastCandidates.Count)
                {
                    txtInfo.Text = "Cycle to select a fillet first, then Keep.";
                    return;
                }

                var d = lastCandidates[highlightIndex];

                // NOTE: assign locals, not out params
                outLineLocal = BuildArc3Line_FromCandidate(d);


                // Show log ONLY on Keep (not on every radius change)
                if (host.LogWindowShow)
                {
                    string pairLabel = GetPairTypeLabel(aPick.Seg, bPick.Seg);
                    string logText = TurnEditArcLaw.BuildLog(lastCandidates, lastR, pairLabel);
                    host.ShowLogWindow("Arc Law Test (Fillet)", logText);
                }


                statusLocal = "Fillet: candidate returned (inserted by caller).";
                keepAccepted = true;

                host.ClearPreviewOnly();

                w.DialogResult = true;
                w.Close();
            };

            btnCancel.Click += (_, __) =>
            {
                keepAccepted = false;
                host.ClearPreviewOnly();
                statusLocal = "Fillet: cancelled.";
                w.DialogResult = false;
                w.Close();
            };

            w.Closed += (_, __) =>
            {
                if (!keepAccepted)
                    host.ClearPreviewOnly();
            };

            w.Loaded += (_, __) =>
            {
                // Position bottom-right of owner (or work area), then do normal init.
                try
                {
                    Rect wa = SystemParameters.WorkArea;

                    Window? owner = w.Owner;
                    double left, top;

                    const double M = 12.0;

                    if (owner != null)
                    {
                        left = owner.Left + owner.ActualWidth - w.ActualWidth - M;
                        top = owner.Top + owner.ActualHeight - w.ActualHeight - M;
                    }
                    else
                    {
                        left = wa.Right - w.ActualWidth - M;
                        top = wa.Bottom - w.ActualHeight - M;
                    }

                    // Clamp inside screen work area
                    if (left < wa.Left) left = wa.Left;
                    if (top < wa.Top) top = wa.Top;
                    if (left + w.ActualWidth > wa.Right) left = wa.Right - w.ActualWidth;
                    if (top + w.ActualHeight > wa.Bottom) top = wa.Bottom - w.ActualHeight;

                    w.Left = left;
                    w.Top = top;
                }
                catch { }

                tbRad.Focus();
                tbRad.SelectAll();
                RecomputePreview();
            };


            w.ShowDialog();

            // Copy locals to out params here (legal)
            regionInputLine = outLineLocal;
            statusText = statusLocal;

            return keepAccepted;
        }


        // ------------------------------------------------------------
        // Candidate -> region input text (same format as your current code)
        // ------------------------------------------------------------
        public static string BuildArc3Line_FromCandidate(TurnEditArcLaw.FilletArcData d)
        {
            var inv = CultureInfo.InvariantCulture;

            double xs = d.Tan1.X;
            double zs = d.Tan1.Y;

            double xm = d.MidShort.X;
            double zm = d.MidShort.Y;

            double xe = d.Tan2.X;
            double ze = d.Tan2.Y;

            double cx = d.CP.X;
            double cz = d.CP.Y;

            double vSx = cx - xs;
            double vSz = cz - zs;
            double vEx = cx - xe;
            double vEz = cz - ze;

            return string.Format(inv,
                "ARC3_CCW {0} {1}   {2} {3}   {4} {5}   {6} {7}   {8} {9}   {10} {11}",
                xs.ToString("R", inv), zs.ToString("R", inv),
                xm.ToString("R", inv), zm.ToString("R", inv),
                xe.ToString("R", inv), ze.ToString("R", inv),
                cx.ToString("R", inv), cz.ToString("R", inv),
                vSx.ToString("R", inv), vSz.ToString("R", inv),
                vEx.ToString("R", inv), vEz.ToString("R", inv)
            );
        }

        // ============================================================
        // Fillet centers (LL / LA / AA) — tool-owned
        // ============================================================

        private static List<Point> BuildAllFilletCentersForSelection(SegView s1, SegView s2, double r)
        {
            if (s1.Kind == SegKind.Line && s2.Kind == SegKind.Line)
                return BuildLineLineOffsetIntersectionCenters(s1, s2, r);

            if (s1.Kind == SegKind.Line && s2.Kind == SegKind.Arc)
                return BuildLineArcOffsetIntersectionCenters(s1, s2, r);

            if (s1.Kind == SegKind.Arc && s2.Kind == SegKind.Line)
                return BuildLineArcOffsetIntersectionCenters(s2, s1, r);

            if (s1.Kind == SegKind.Arc && s2.Kind == SegKind.Arc)
                return BuildArcArcOffsetIntersectionCenters(s1, s2, r);

            return new List<Point>();
        }

        private static List<Point> BuildLineLineOffsetIntersectionCenters(SegView l1, SegView l2, double r)
        {
            var outPts = new List<Point>();
            if (r <= 1e-12) return outPts;

            if (!TurnEditMath.TryUnitDirAndLeftNormal(l1.A, l1.B, out _, out Vector n1))
                return outPts;

            if (!TurnEditMath.TryUnitDirAndLeftNormal(l2.A, l2.B, out _, out Vector n2))
                return outPts;

            int[] sides = new[] { -1, +1 };

            for (int i = 0; i < sides.Length; i++)
            {
                int s1 = sides[i];

                Point a1 = l1.A + s1 * r * n1;
                Point b1 = l1.B + s1 * r * n1;

                for (int j = 0; j < sides.Length; j++)
                {
                    int s2 = sides[j];

                    Point a2 = l2.A + s2 * r * n2;
                    Point b2 = l2.B + s2 * r * n2;

                    if (TurnEditMath.TryIntersectInfiniteLines(a1, b1, a2, b2, out Point ip))
                        TurnEditMath.AddUniquePoint(outPts, ip, 1e-6);
                }
            }

            return outPts;
        }

        private static List<Point> BuildLineArcOffsetIntersectionCenters(SegView line, SegView arc, double r)
        {
            var outPts = new List<Point>();
            if (r <= 1e-12) return outPts;

            if (arc.Kind != SegKind.Arc)
                return outPts;

            if (!TurnEditMath.TryUnitDirAndLeftNormal(line.A, line.B, out _, out Vector nL))
                return outPts;

            double arcR = arc.Radius;
            if (arcR < 1e-9)
                return outPts;

            int[] sides = new[] { -1, +1 };

            for (int i = 0; i < sides.Length; i++)
            {
                int sLine = sides[i];

                Point p1 = line.A + sLine * r * nL;
                Point p2 = line.B + sLine * r * nL;

                for (int j = 0; j < sides.Length; j++)
                {
                    int sArc = sides[j];

                    double ro = arcR + sArc * r;
                    if (ro <= 1e-9)
                        continue;

                    var hits = TurnEditMath.IntersectLineCircle_Infinite(p1, p2, arc.C, ro);
                    for (int k = 0; k < hits.Count; k++)
                        TurnEditMath.AddUniquePoint(outPts, hits[k], 1e-6);
                }
            }

            return outPts;
        }

        private static List<Point> BuildArcArcOffsetIntersectionCenters(SegView a1, SegView a2, double r)
        {
            var outPts = new List<Point>();
            if (r <= 1e-12) return outPts;

            if (a1.Kind != SegKind.Arc || a2.Kind != SegKind.Arc)
                return outPts;

            double r1 = a1.Radius;
            double r2 = a2.Radius;

            if (r1 < 1e-9 || r2 < 1e-9)
                return outPts;

            int[] sides = new[] { -1, +1 };

            for (int i = 0; i < sides.Length; i++)
            {
                int s1 = sides[i];
                double ro1 = r1 + s1 * r;
                if (ro1 <= 1e-9) continue;

                for (int j = 0; j < sides.Length; j++)
                {
                    int s2 = sides[j];
                    double ro2 = r2 + s2 * r;
                    if (ro2 <= 1e-9) continue;

                    var hits = TurnEditMath.IntersectCircleCircle(a1.C, ro1, a2.C, ro2);
                    for (int k = 0; k < hits.Count; k++)
                        TurnEditMath.AddUniquePoint(outPts, hits[k], 1e-6);
                }
            }

            return outPts;
        }

        // ============================================================
        // Tangency points for ArcLaw (tool-owned)
        // ============================================================

        private static bool TryGetTangentPointOnSeg(SegView seg, Point filletCenter, double rFillet, out Point tanWorld)
        {
            tanWorld = new Point();

            if (seg.Kind == SegKind.Line)
            {
                Vector d = seg.B - seg.A;
                double dd = d.X * d.X + d.Y * d.Y;
                if (dd < 1e-18) return false;

                Vector f = filletCenter - seg.A;
                double t = (f.X * d.X + f.Y * d.Y) / dd;
                tanWorld = seg.A + t * d;
                return true;
            }

            if (seg.Kind == SegKind.Arc)
            {
                double arcR = seg.Radius;
                if (arcR < 1e-9) return false;

                Vector v = filletCenter - seg.C;
                double len = v.Length;
                if (len < 1e-12) return false;

                Vector u = v / len;
                tanWorld = seg.C + arcR * u;
                return true;
            }

            return false;
        }
    }
}
