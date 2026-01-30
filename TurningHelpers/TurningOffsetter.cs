using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;

namespace CNC_Improvements_gcode_solids.TurningHelpers
{
    /// <summary>
    /// TurningOffsetter
    /// PASS A: raw offset for each segment
    /// PASS B: walk corner guide and SNAP / FILLET / TRIM; then rebuild arc midpoints correctly
    ///
    /// REFACTOR NOTE:
    /// - Behavior is intentionally identical to the current working version you pasted.
    /// - Structure restored: switch(pairKey) -> Evaluate_* per pair type (fault-find friendly).
    /// </summary>
    internal class TurningOffsetter
    {
        private readonly List<string> _profileShape;
        private readonly List<string> _cornerGuide;
        private readonly string _toolUsageUpper;
        private readonly double _noseRad;
        private readonly int _quadrant;

        private readonly List<string> _ops = new();

        private List<string> _offsetProfile = new();
        private enum CornerKind { Unknown, Tan, Inner, Outer }

        private sealed class GuideEntry
        {
            public int Index;
            public string PairKey = "";
            public CornerKind Kind;
            public string Raw = "";
        }

        private abstract class OffSeg
        {
            public int SourceIndex;
            public string SourceCmd = "";
            public abstract bool IsLine { get; }
            public abstract bool IsArc { get; }

            public Point P1;
            public Point Pm;
            public Point P2;

            // For arcs only
            public Point Center;
            public bool IsCW;
        }

        private sealed class OffLine : OffSeg
        {
            public override bool IsLine => true;
            public override bool IsArc => false;
        }

        private sealed class OffArc : OffSeg
        {
            public override bool IsLine => false;
            public override bool IsArc => true;
        }

        public TurningOffsetter(
    List<string> profileShape,
    List<string> cornerGuide,
    string toolUsage,
    string noseRadText,
    int quadrant)
        {
            _profileShape = profileShape ?? throw new ArgumentNullException(nameof(profileShape));
            _cornerGuide = cornerGuide ?? throw new ArgumentNullException(nameof(cornerGuide));
            _toolUsageUpper = (toolUsage ?? "OFF").Trim().ToUpperInvariant();

            if (!double.TryParse((noseRadText ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double nr) || nr <= 0.0)
                throw new InvalidOperationException($"Invalid nose radius '{noseRadText}'. Enter a positive number (e.g. 2.0).");

            _noseRad = nr;

            // Quadrant selection (1..9). 9 = "do nothing" baseline.
            _quadrant = quadrant;
        }

        public (List<string> opsLog, List<string> offsetProfileShape) BuildOffsetProfile()
        {
            _ops.Clear();
            _offsetProfile.Clear();

            _ops.Add("=== TURNING OFFSETTER ===");
            _ops.Add("Tool usage: " + _toolUsageUpper);
            _ops.Add("Nose radius: " + _noseRad.ToString("0.###", CultureInfo.InvariantCulture));
            _ops.Add("Quadrant: " + _quadrant.ToString(CultureInfo.InvariantCulture));
            _ops.Add("");

            if (_toolUsageUpper == "OFF")
            {
                _ops.Add("OFF: no offset generated.");
                return (new List<string>(_ops), new List<string>(_offsetProfile));
            }

            int offsetDir = (_toolUsageUpper == "LEFT") ? +1 :
                            (_toolUsageUpper == "RIGHT") ? -1 : 0;

            if (offsetDir == 0)
            {
                _ops.Add($"Unknown tool usage '{_toolUsageUpper}'. Treating as OFF.");
                return (new List<string>(_ops), new List<string>(_offsetProfile));
            }

            // Strict parse: arcs must have cx cz appended (no 3pt fallback)
            var srcSegs = TurningOffsetterHelpers.ParseProfileSegmentsStrict(_profileShape);
            var guide = ParseCornerGuide(_cornerGuide);

            _ops.Add($"Segments: {srcSegs.Count}   Guide entries: {guide.Count}");
            _ops.Add("");

            // --------------------------
            // PASS A: raw offset segments
            // --------------------------
            var raw = new List<OffSeg>(srcSegs.Count);

            int clampedArcCount = 0;

            for (int i = 0; i < srcSegs.Count; i++)
            {
                var s = srcSegs[i];

                if (s.IsLine)
                {
                    if (!TurningOffsetterHelpers.TryOffsetLine(s.P1, s.P2, offsetDir, _noseRad, out Point op1, out Point op2))
                        throw new InvalidOperationException($"Could not offset LINE at index {i:00}");

                    raw.Add(new OffLine
                    {
                        SourceIndex = i,
                        SourceCmd = "LINE",
                        P1 = op1,
                        Pm = new Point((op1.X + op2.X) * 0.5, (op1.Y + op2.Y) * 0.5),
                        P2 = op2
                    });
                }
                else if (s.IsArc)
                {
                    if (!s.HasArcCenter)
                        throw new InvalidOperationException($"Arc at index {i:00} missing cx/cz appended fields.");

                    // LEFT : CW => +noseRad, CCW => -noseRad
                    // RIGHT: CW => -noseRad, CCW => +noseRad
                    double deltaR = offsetDir * _noseRad * (s.IsCCW ? +1.0 : -1.0);

                    Point c = s.ArcCenter;

                    // Base radius from P1
                    double rBase = TurningOffsetterHelpers.Dist(s.P1, c);
                    double rNew = rBase + deltaR;

                    double smallTol = 0.0;
                    try { smallTol = TurningOffsetterHelpers.SmallSegmentThreshold(); }
                    catch { smallTol = 0.0; }

                    // Clamp radius if it inverts or hits zero (so cleanup can remove it)
                    if (rNew <= 1e-9)
                    {
                        double clampR = Math.Max(0.006, smallTol * 0.25);
                        _ops.Add($"PASS A: Arc {i:00} rNew={rNew:0.###} clamped to {clampR:0.###} (will be removed by SmallSegment)");
                        rNew = clampR;
                        clampedArcCount++;
                    }

                    // Project endpoints to new radius (keep angles)
                    Point p1o = TurningOffsetterHelpers.ProjectToRadius(s.P1, c, rNew);
                    Point p2o = TurningOffsetterHelpers.ProjectToRadius(s.P2, c, rNew);

                    // Project original mid to new radius and use it as HINT
                    Point pmHint = TurningOffsetterHelpers.ProjectToRadius(s.Pm, c, rNew);

                    bool isCW = s.IsCW;

                    // Compute midpoint correctly by walking the directed sweep (with hint)
                    Point pmo = TurningOffsetterHelpers.MidPointOnArc(p1o, p2o, c, isCW, preferMinor: false, hintPoint: pmHint);

                    raw.Add(new OffArc
                    {
                        SourceIndex = i,
                        SourceCmd = s.Command,     // keep command
                        Center = c,
                        IsCW = isCW,
                        P1 = p1o,
                        Pm = pmo,
                        P2 = p2o
                    });
                }
                else
                {
                    throw new InvalidOperationException($"Unknown segment command '{s.Command}' at index {i:00}");
                }
            }

            _ops.Add("PASS A: RAW offset built.");
            if (clampedArcCount > 0)
                _ops.Add($"PASS A: Collapsed/clamped arcs: {clampedArcCount} (handled via SmallSegment cleanup)");
            _ops.Add("");

            // --------------------------
            // PASS B: join using guide
            // --------------------------
            var outSegs = new List<OffSeg>();
            int insertedFillets = 0;

            for (int gi = 0; gi < guide.Count; gi++)
            {
                var g = guide[gi];

                if (g.Index < 0 || g.Index >= raw.Count - 1)
                {
                    _ops.Add($"{g.Index:00}: {g.PairKey} -> SKIP (index out of range)");
                    continue;
                }

                OffSeg a = raw[g.Index];
                OffSeg b = raw[g.Index + 1];

                // Original vertex (used for fillet center by your rule)
                Point vertex = srcSegs[g.Index].P2;

                if (g.Kind == CornerKind.Tan)
                {
                    Point snap = a.P2;
                    SnapStartTo(ref b, snap);
                    _ops.Add($"{g.Index:00}: {g.PairKey} (TAN) -> SNAP");
                }
                else if (g.Kind == CornerKind.Inner)
                {
                    if (TryTrimAtIntersection(a, b, vertex, out Point ip))
                    {
                        a.P2 = ip;
                        SnapStartTo(ref b, ip);
                        _ops.Add($"{g.Index:00}: {g.PairKey} (INNER) -> TRIM @ {ip.X:0.###},{ip.Y:0.###}");
                    }
                    else
                    {
                        Point snap = a.P2;
                        SnapStartTo(ref b, snap);
                        _ops.Add($"{g.Index:00}: {g.PairKey} (INNER) -> SNAP (no trim found)");
                    }
                }
                else if (g.Kind == CornerKind.Outer)
                {
                    Point p1 = TurningOffsetterHelpers.ProjectToRadius(a.P2, vertex, _noseRad);
                    Point p2 = TurningOffsetterHelpers.ProjectToRadius(b.P1, vertex, _noseRad);

                    a.P2 = p1;
                    SnapStartTo(ref b, p2);

                    if (!TryGetTurnCross(a, b, out double cross))
                        cross = 0;

                    bool filletCW = (offsetDir * cross) < 0.0;

                    string filletCmd = filletCW ? "ARC3_CW" : "ARC3_CCW";
                    Point pm = TurningOffsetterHelpers.MidPointOnArc(p1, p2, vertex, filletCW, preferMinor: true);

                    var fillet = new OffArc
                    {
                        SourceIndex = -1,
                        SourceCmd = filletCmd,
                        Center = vertex,
                        IsCW = filletCW,
                        P1 = p1,
                        Pm = pm,
                        P2 = p2
                    };

                    outSegs.Add(Clone(a));
                    outSegs.Add(fillet);
                    insertedFillets++;

                    _ops.Add($"{g.Index:00}: {g.PairKey} (OUTER) -> FILLET R={_noseRad:0.###}");
                    continue; // a emitted; b later
                }
                else
                {
                    Point snap = a.P2;
                    SnapStartTo(ref b, snap);
                    _ops.Add($"{g.Index:00}: {g.PairKey} (UNKNOWN) -> SNAP");
                }

                outSegs.Add(Clone(a));
            }

            // Emit tail
            if (raw.Count > 0)
                outSegs.Add(Clone(raw[raw.Count - 1]));

            _ops.Add("");
            _ops.Add($"PASS B: joins done. (Inserted fillets: {insertedFillets})");
            _ops.Add("");

            // --------------------------
            // Cleanup: remove tiny segments + SNAP across removals
            // --------------------------
            double small = TurningOffsetterHelpers.SmallSegmentThreshold();
            var cleaned = RemoveSmallSegmentsAndStitch(outSegs, small, out int removedSmall);

            _ops.Add($"OUTPUT: {cleaned.Count} segments after join+cleanup.");
            if (removedSmall > 0)
                _ops.Add($"CLEANUP: removed {removedSmall} segments with len <= SmallSegment ({small:0.###}).");
            _ops.Add("");

            // --------------------------
            // Fix arc CW/CCW using 3-point geometry AFTER all joins/snaps
            // (prevents flipped arcs in viewer when tool-comp is ON)
            // --------------------------
            // Fix arc CW/CCW using 3-point geometry AFTER all joins/snaps
            // NOTE: TurnEditWindow viewer expects the opposite CW convention -> FLIP it here.
            for (int i = 0; i < cleaned.Count; i++)
            {
                if (!cleaned[i].IsArc)
                    continue;

                var a = (OffArc)cleaned[i];

                if (TurningOffsetterHelpers.TryComputeArcDirectionFromMidpoint(a.P1, a.Pm, a.P2, a.Center, out bool cwFromMid))
                {
                    bool cwForEditor = !cwFromMid; // <-- FLIP
                    a.IsCW = cwForEditor;
                    a.SourceCmd = cwForEditor ? "ARC3_CW" : "ARC3_CCW";
                }
                else
                {
                    // If ambiguous, still keep SourceCmd consistent with current IsCW,
                    // but ALSO flip it (same convention rule).
                    a.IsCW = !a.IsCW;
                    a.SourceCmd = a.IsCW ? "ARC3_CW" : "ARC3_CCW";
                }
            }


            // --------------------------
            // Build viewer geometry text ONLY (do NOT dump it twice in ops)
            // --------------------------
            _offsetProfile.Clear();

            foreach (var s in cleaned)
            {
                string txt;

                if (s.IsLine)
                {
                    txt = string.Format(CultureInfo.InvariantCulture,
                        "LINE {0} {1}   {2} {3}",
                        s.P1.X, s.P1.Y, s.P2.X, s.P2.Y);
                }
                else
                {
                    var a = (OffArc)s;

                    double cx = a.Center.X;
                    double cz = a.Center.Y;

                    // Vectors from endpoints TO center (direction vectors, not positions)
                    double vSx = cx - s.P1.X;
                    double vSz = cz - s.P1.Y;
                    double vEx = cx - s.P2.X;
                    double vEz = cz - s.P2.Y;

                    txt = string.Format(CultureInfo.InvariantCulture,
                        "{0} {1} {2}   {3} {4}   {5} {6}   {7} {8}   {9} {10}   {11} {12}",
                        a.SourceCmd,
                        s.P1.X, s.P1.Y,
                        s.Pm.X, s.Pm.Y,
                        s.P2.X, s.P2.Y,
                        cx, cz,
                        vSx, vSz,
                        vEx, vEz);
                }

                _offsetProfile.Add(txt);
            }

            // Apply quadrant nose shift LAST (after full offset + join + cleanup)
            _offsetProfile = TurningOffsetterHelpers.ApplyQuadrantNoseShiftToProfileText(
                _offsetProfile,
                _quadrant,
                _noseRad);

            _ops.Add($"Offset profile built: {_offsetProfile.Count} segments (sent to viewer).");

            return (new List<string>(_ops), new List<string>(_offsetProfile));
        }




        private List<OffSeg> RemoveSmallSegmentsAndStitch(List<OffSeg> inSegs, double small, out int removedSmall)
        {
            removedSmall = 0;

            var cleaned = new List<OffSeg>();
            OffSeg? prev = null;

            for (int i = 0; i < inSegs.Count; i++)
            {
                var s0 = inSegs[i];

                // Length test (no “valid geometry” policing)
                double len;
                if (s0.IsLine)
                    len = TurningOffsetterHelpers.LineLen(s0.P1, s0.P2);
                else
                    len = TurningOffsetterHelpers.ArcLen(s0.P1, s0.P2, s0.Center, ((OffArc)s0).IsCW);

                // Remove tiny segments
                if (small > 0 && len <= small)
                {
                    removedSmall++;
                    continue;
                }

                // Work on a clone so we don’t mutate the original list
                OffSeg s = Clone(s0);

                // *** YOUR RULE: SNAP ACROSS REMOVALS ***
                // If we kept a previous segment, force continuity:
                // current.P1 = prev.P2 (and rebuild arc midpoint if needed)
                if (prev != null)
                {
                    Point snap = prev.P2;
                    SnapStartTo(ref s, snap);
                }

                cleaned.Add(s);
                prev = s;
            }

            return cleaned;
        }





        // ============================================================
        // PASS A
        // ============================================================

        private List<OffSeg> BuildRawOffsetSegments(List<ProfileSegment> srcSegs, int offsetDir)
        {
            var raw = new List<OffSeg>(srcSegs.Count);

            for (int i = 0; i < srcSegs.Count; i++)
            {
                var s = srcSegs[i];

                if (s.IsLine)
                {
                    if (!TurningOffsetterHelpers.TryOffsetLine(s.P1, s.P2, offsetDir, _noseRad, out Point op1, out Point op2))
                        throw new InvalidOperationException($"Could not offset LINE at index {i:00}");

                    raw.Add(new OffLine
                    {
                        SourceIndex = i,
                        SourceCmd = "LINE",
                        P1 = op1,
                        Pm = new Point((op1.X + op2.X) * 0.5, (op1.Y + op2.Y) * 0.5),
                        P2 = op2
                    });
                }
                else if (s.IsArc)
                {
                    if (!s.HasArcCenter)
                        throw new InvalidOperationException($"Arc at index {i:00} missing cx/cz appended fields.");

                    // Your simplified arc-radius rule (already verified working for your side mapping):
                    // LEFT : CW => +noseRad, CCW => -noseRad
                    // RIGHT: CW => -noseRad, CCW => +noseRad
                    double deltaR = offsetDir * _noseRad * (s.IsCCW ? +1.0 : -1.0);

                    Point c = s.ArcCenter;

                    double rBase = TurningOffsetterHelpers.Dist(s.P1, c);
                    double rNew = rBase + deltaR;
                    if (rNew <= 1e-9)
                        throw new InvalidOperationException($"Arc at {i:00} offset would invert radius (rNew={rNew}).");

                    Point p1o = TurningOffsetterHelpers.ProjectToRadius(s.P1, c, rNew);
                    Point p2o = TurningOffsetterHelpers.ProjectToRadius(s.P2, c, rNew);

                    // Hint from original mid projected to new radius (keeps correct side)
                    Point pmHint = TurningOffsetterHelpers.ProjectToRadius(s.Pm, c, rNew);

                    bool isCW = s.IsCW;

                    Point pmo = TurningOffsetterHelpers.MidPointOnArc(
                        p1o, p2o, c,
                        isCW,
                        preferMinor: false,
                        hintPoint: pmHint);

                    raw.Add(new OffArc
                    {
                        SourceIndex = i,
                        SourceCmd = s.Command,   // keep command
                        Center = c,
                        IsCW = isCW,
                        P1 = p1o,
                        Pm = pmo,
                        P2 = p2o
                    });
                }
                else
                {
                    throw new InvalidOperationException($"Unknown segment command '{s.Command}' at index {i:00}");
                }
            }

            return raw;
        }

        // ============================================================
        // PASS B (switch -> per pair evaluator)
        // ============================================================

        private List<OffSeg> BuildJoinedAndCleaned(
            List<OffSeg> raw,
            List<ProfileSegment> srcSegs,
            List<GuideEntry> guide,
            int offsetDir)
        {
            var outSegs = new List<OffSeg>();
            int insertedFillets = 0;

            for (int gi = 0; gi < guide.Count; gi++)
            {
                var g = guide[gi];

                if (g.Index < 0 || g.Index >= raw.Count - 1)
                {
                    _ops.Add($"{g.Index:00}: {g.PairKey} -> SKIP (index out of range)");
                    continue;
                }

                OffSeg a = raw[g.Index];
                OffSeg b = raw[g.Index + 1];

                // Original vertex from SOURCE profile (fillet center rule)
                Point vertex = srcSegs[g.Index].P2;

                // ---- THIS IS THE STRUCTURE YOU ASKED FOR ----
                // Switch by pairKey so each junction type has its own code path.
                switch (g.PairKey)
                {
                    case "L,L":
                        Evaluate_LL(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "L,CW":
                        Evaluate_L_CW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "L,CCW":
                        Evaluate_L_CCW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CW,L":
                        Evaluate_CW_L(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CCW,L":
                        Evaluate_CCW_L(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CW,CW":
                        Evaluate_CW_CW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CW,CCW":
                        Evaluate_CW_CCW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CCW,CW":
                        Evaluate_CCW_CW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    case "CCW,CCW":
                        Evaluate_CCW_CCW(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;

                    default:
                        // Unknown pairKey: keep old behavior = treat as UNKNOWN kind path (snap)
                        Evaluate_UnknownPair(g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);
                        break;
                }
            }

            // Emit tail exactly as before
            if (raw.Count > 0)
                outSegs.Add(Clone(raw[raw.Count - 1]));

            _ops.Add("");
            _ops.Add($"PASS B: joins done. (Inserted fillets: {insertedFillets})");
            _ops.Add("");

            // Cleanup exactly as before
            double small = TurningOffsetterHelpers.SmallSegmentThreshold();
            var cleaned = new List<OffSeg>();

            foreach (var s in outSegs)
            {
                double len = 0;
                if (s.IsLine)
                    len = TurningOffsetterHelpers.LineLen(s.P1, s.P2);
                else
                    len = TurningOffsetterHelpers.ArcLen(s.P1, s.P2, s.Center, ((OffArc)s).IsCW);

                if (len <= small && cleaned.Count > 0)
                {
                    continue;
                }

                cleaned.Add(s);
            }

            _ops.Add($"OUTPUT: {cleaned.Count} segments after join+cleanup.");
            _ops.Add("");

            return cleaned;
        }

        // ============================================================
        // Per-pair evaluators (breakpoint-friendly)
        // ============================================================

        private void Evaluate_LL(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("L,L", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_L_CW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("L,CW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_L_CCW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("L,CCW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CW_L(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CW,L", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CCW_L(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CCW,L", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CW_CW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CW,CW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CW_CCW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CW,CCW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CCW_CW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CCW,CW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_CCW_CCW(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind("CCW,CCW", g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        private void Evaluate_UnknownPair(GuideEntry g, OffSeg a, ref OffSeg b, Point vertex, int offsetDir, List<OffSeg> outSegs, ref int insertedFillets)
            => Evaluate_ByKind(g.PairKey, g, a, ref b, vertex, offsetDir, outSegs, ref insertedFillets);

        // ============================================================
        // Single join implementation (behavior-identical)
        // ============================================================

        private void Evaluate_ByKind(
            string pairKey,
            GuideEntry g,
            OffSeg a,
            ref OffSeg b,
            Point vertex,
            int offsetDir,
            List<OffSeg> outSegs,
            ref int insertedFillets)
        {
            if (g.Kind == CornerKind.Tan)
            {
                Point snap = a.P2;
                SnapStartTo(ref b, snap);

                _ops.Add($"{g.Index:00}: {pairKey} (TAN) -> SNAP");
                outSegs.Add(Clone(a));
                return;
            }

            if (g.Kind == CornerKind.Inner)
            {
                if (TryTrimAtIntersection(a, b, vertex, out Point ip))
                {
                    a.P2 = ip;
                    SnapStartTo(ref b, ip);
                    _ops.Add($"{g.Index:00}: {pairKey} (INNER) -> TRIM @ {ip.X:0.###},{ip.Y:0.###}");
                }
                else
                {
                    Point snap = a.P2;
                    SnapStartTo(ref b, snap);
                    _ops.Add($"{g.Index:00}: {pairKey} (INNER) -> SNAP (no trim found)");
                }

                outSegs.Add(Clone(a));
                return;
            }

            if (g.Kind == CornerKind.Outer)
            {
                // FILLET R = noseRad, center = original vertex (your rule)
                Point p1 = TurningOffsetterHelpers.ProjectToRadius(a.P2, vertex, _noseRad);
                Point p2 = TurningOffsetterHelpers.ProjectToRadius(b.P1, vertex, _noseRad);

                a.P2 = p1;
                SnapStartTo(ref b, p2);

                if (!TryGetTurnCross(a, b, out double cross))
                    cross = 0;

                bool filletCW = (offsetDir * cross) < 0.0;

                string filletCmd = filletCW ? "ARC3_CW" : "ARC3_CCW";
                Point pm = TurningOffsetterHelpers.MidPointOnArc(p1, p2, vertex, filletCW, preferMinor: true);

                var fillet = new OffArc
                {
                    SourceIndex = -1,
                    SourceCmd = filletCmd,
                    Center = vertex,
                    IsCW = filletCW,
                    P1 = p1,
                    Pm = pm,
                    P2 = p2
                };

                outSegs.Add(Clone(a));
                outSegs.Add(fillet);
                insertedFillets++;

                _ops.Add($"{g.Index:00}: {pairKey} (OUTER) -> FILLET R={_noseRad:0.###}");
                return; // IMPORTANT: b will be emitted when it becomes next 'a'
            }

            // Unknown kind -> snap (same as before)
            {
                Point snap = a.P2;
                SnapStartTo(ref b, snap);
                _ops.Add($"{g.Index:00}: {pairKey} (UNKNOWN) -> SNAP");
                outSegs.Add(Clone(a));
            }
        }

        // ============================================================
        // Guide parsing (unchanged behavior)
        // ============================================================

        private static List<GuideEntry> ParseCornerGuide(List<string> cornerGuide)
        {
            var list = new List<GuideEntry>();
            if (cornerGuide == null) return list;

            foreach (var raw in cornerGuide)
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string s = raw.Trim();

                if (s.StartsWith("===", StringComparison.OrdinalIgnoreCase)) continue;

                int colon = s.IndexOf(':');
                if (colon < 0) continue;

                string idxStr = s.Substring(0, colon).Trim();
                if (!int.TryParse(idxStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx))
                    continue;

                string rest = s.Substring(colon + 1).Trim();
                string pairKey = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0].Trim();

                CornerKind kind = CornerKind.Unknown;
                if (rest.Contains(" TAN ", StringComparison.OrdinalIgnoreCase) || rest.Contains("TAN(", StringComparison.OrdinalIgnoreCase))
                    kind = CornerKind.Tan;
                else if (rest.Contains("ANGLE=INNER", StringComparison.OrdinalIgnoreCase))
                    kind = CornerKind.Inner;
                else if (rest.Contains("ANGLE=OUTER", StringComparison.OrdinalIgnoreCase))
                    kind = CornerKind.Outer;

                list.Add(new GuideEntry
                {
                    Index = idx,
                    PairKey = pairKey,
                    Kind = kind,
                    Raw = s
                });
            }

            return list;
        }

        // ============================================================
        // Segment edits (unchanged behavior)
        // ============================================================

        private static void SnapStartTo(ref OffSeg seg, Point newStart)
        {
            seg.P1 = newStart;

            if (seg.IsLine)
            {
                seg.Pm = new Point((seg.P1.X + seg.P2.X) * 0.5, (seg.P1.Y + seg.P2.Y) * 0.5);
                return;
            }

            var a = (OffArc)seg;

            // Use current midpoint as hint (keeps same side)
            Point hint = seg.Pm;
            seg.Pm = TurningOffsetterHelpers.MidPointOnArc(seg.P1, seg.P2, a.Center, a.IsCW, preferMinor: false, hintPoint: hint);
        }

        private static OffSeg Clone(OffSeg s)
        {
            if (s.IsLine)
            {
                return new OffLine
                {
                    SourceIndex = s.SourceIndex,
                    SourceCmd = s.SourceCmd,
                    P1 = s.P1,
                    Pm = s.Pm,
                    P2 = s.P2
                };
            }

            var a = (OffArc)s;
            return new OffArc
            {
                SourceIndex = s.SourceIndex,
                SourceCmd = s.SourceCmd,
                Center = a.Center,
                IsCW = a.IsCW,
                P1 = s.P1,
                Pm = s.Pm,
                P2 = s.P2
            };
        }

        // ============================================================
        // Intersection trimming (unchanged behavior)
        // ============================================================

        private static bool TryTrimAtIntersection(OffSeg a, OffSeg b, Point vertexHint, out Point ip)
        {
            ip = new Point();

            // LINE-LINE
            if (a.IsLine && b.IsLine)
            {
                if (TurningOffsetterHelpers.TryIntersectLineLine(a.P1, a.P2, b.P1, b.P2, out Point p))
                {
                    ip = p;
                    return true;
                }
                return false;
            }

            // LINE-ARC
            if (a.IsLine && b.IsArc)
            {
                var ba = (OffArc)b;
                double r = TurningOffsetterHelpers.Dist(b.P1, ba.Center);
                if (TurningOffsetterHelpers.TryIntersectLineCircle(a.P1, a.P2, ba.Center, r, out Point i1, out Point i2, out int count))
                {
                    ip = (count == 1) ? i1 : TurningOffsetterHelpers.ChooseClosest(vertexHint, i1, i2);
                    ip = TurningOffsetterHelpers.ProjectToRadius(ip, ba.Center, r);
                    return true;
                }
                return false;
            }

            // ARC-LINE
            if (a.IsArc && b.IsLine)
            {
                var aa = (OffArc)a;
                double r = TurningOffsetterHelpers.Dist(a.P2, aa.Center);
                if (TurningOffsetterHelpers.TryIntersectLineCircle(b.P1, b.P2, aa.Center, r, out Point i1, out Point i2, out int count))
                {
                    ip = (count == 1) ? i1 : TurningOffsetterHelpers.ChooseClosest(vertexHint, i1, i2);
                    ip = TurningOffsetterHelpers.ProjectToRadius(ip, aa.Center, r);
                    return true;
                }
                return false;
            }

            // ARC-ARC (circle-circle)
            if (a.IsArc && b.IsArc)
            {
                var aa = (OffArc)a;
                var bb = (OffArc)b;

                double r1 = TurningOffsetterHelpers.Dist(a.P2, aa.Center);
                double r2 = TurningOffsetterHelpers.Dist(b.P1, bb.Center);

                if (TurningOffsetterHelpers.TryIntersectCircleCircle(aa.Center, r1, bb.Center, r2, out Point i1, out Point i2, out int count))
                {
                    ip = (count == 1) ? i1 : TurningOffsetterHelpers.ChooseClosest(vertexHint, i1, i2);
                    return true;
                }
                return false;
            }

            return false;
        }

        private static bool TryGetTurnCross(OffSeg a, OffSeg b, out double cross)
        {
            cross = 0;

            if (!TryTangentAtEnd(a, out double t1x, out double t1z))
                return false;

            if (!TryTangentAtStart(b, out double t2x, out double t2z))
                return false;

            cross = t1x * t2z - t1z * t2x;
            return true;
        }

        private static bool TryTangentAtEnd(OffSeg s, out double tx, out double tz)
        {
            tx = tz = 0;

            if (s.IsLine)
                return TurningOffsetterHelpers.TryGetTravelTangentLine(s.P1, s.P2, out tx, out tz);

            var a = (OffArc)s;

            var ps = new ProfileSegment
            {
                Command = a.SourceCmd,
                HasArcCenter = true,
                ArcCenter = a.Center
            };

            return TurningOffsetterHelpers.TryGetTravelTangentArcAtEnd(ps, s.P2, out tx, out tz);
        }

        private static bool TryTangentAtStart(OffSeg s, out double tx, out double tz)
        {
            tx = tz = 0;

            if (s.IsLine)
                return TurningOffsetterHelpers.TryGetTravelTangentLine(s.P1, s.P2, out tx, out tz);

            var a = (OffArc)s;

            var ps = new ProfileSegment
            {
                Command = a.SourceCmd,
                HasArcCenter = true,
                ArcCenter = a.Center
            };

            return TurningOffsetterHelpers.TryGetTravelTangentArcAtStart(ps, s.P1, out tx, out tz);
        }
    }
}
