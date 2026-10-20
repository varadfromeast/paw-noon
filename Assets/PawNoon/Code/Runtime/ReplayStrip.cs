using System;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>How one duelist's exchange is drawn on the replay strip.</summary>
    public enum ReplayMarkKind
    {
        None = 0,
        /// <summary>Accepted shot: a solid tick at its reaction time.</summary>
        Shot,
        /// <summary>Anticipation foul (fired after the cue but faster than humanly possible): crossed tick at its time.</summary>
        Foul,
        /// <summary>Drew before the cue (TOO EARLY): crossed tick at the right edge (no time after DRAW).</summary>
        Early,
        /// <summary>Never fired: crossed tick at the right edge.</summary>
        NoShot,
        /// <summary>Hit before firing (a kill stopped the slower shot): crossed tick at the right edge.</summary>
        Stopped
    }

    /// <summary>What the strip shows for one round. Built from the authority's RoundRecord (pure presentation).</summary>
    public struct ReplayMarks
    {
        public bool valid;
        public ReplayMarkKind you, opp;
        /// <summary>Reaction (ns) for Shot / Foul marks, else -1.</summary>
        public long youNs, oppNs;
        /// <summary>Build-in time (s); 0 = appear at once.</summary>
        public float seconds;

        public static ReplayMarkKind KindOf(SlotRecord s)
        {
            if (s == null) return ReplayMarkKind.None;
            switch (s.outcome)
            {
                case SlotOutcome.Shot: return s.reactionNs >= 0 ? ReplayMarkKind.Shot : ReplayMarkKind.NoShot;
                case SlotOutcome.Anticipation: return s.reactionNs >= 0 ? ReplayMarkKind.Foul : ReplayMarkKind.Early;
                case SlotOutcome.Foul: return ReplayMarkKind.Early;
                case SlotOutcome.Stopped: return ReplayMarkKind.Stopped;
                default: return ReplayMarkKind.NoShot;
            }
        }

        /// <summary>Reads slot 0 (you) and slot 1 (opponent) of a resolved round. Void / empty rounds are not valid.</summary>
        public static ReplayMarks FromRound(RoundRecord r, float seconds)
        {
            var m = new ReplayMarks();
            m.youNs = m.oppNs = -1;
            m.seconds = seconds;
            if (r == null || r.roundId <= 0 || r.isVoid) return m;
            m.valid = true;
            m.you = KindOf(r.slots[0]);
            m.opp = KindOf(r.slots[1]);
            if (ReplayStripMath.IsTimed(m.you)) m.youNs = r.slots[0].reactionNs;
            if (ReplayStripMath.IsTimed(m.opp)) m.oppNs = r.slots[1].reactionNs;
            return m;
        }

        /// <summary>Two clean shots (review stills).</summary>
        public static ReplayMarks Shots(long youNs, long oppNs, float seconds)
        {
            var m = new ReplayMarks();
            m.valid = true;
            m.you = ReplayMarkKind.Shot;
            m.opp = ReplayMarkKind.Shot;
            m.youNs = youNs;
            m.oppNs = oppNs;
            m.seconds = seconds;
            return m;
        }
    }

    /// <summary>
    /// Layout maths of the reaction replay strip (SPEC_V2 §8.1), engine-free so EditMode tests can pin it.
    /// Strip coordinates: x right, y down, origin at the strip's top-left pixel.
    /// </summary>
    public static class ReplayStripMath
    {
        public const int Width = 200;
        public const int Height = 24;
        /// <summary>x of DRAW (0 ms) and of the right end of the ruler.</summary>
        public const int RulerLeft = 6;
        public const int RulerRight = Width - 7;
        /// <summary>Row of the ruler line; ticks hang off it.</summary>
        public const int Baseline = 15;
        /// <summary>Top row for the name labels and the gap label (BitmapText top-of-line).</summary>
        public const int LabelTop = 1;
        public const int GapLabelTop = 17;
        public const int EdgeMargin = 2;
        public const int LabelGap = 3;
        /// <summary>When both marks sit at the right edge, "you" moves this far left so the two crosses stay apart.</summary>
        public const int BothEdgeOffset = 8;
        public const double MinSpanMs = 600.0;
        public const double TailMs = 50.0;
        public const double TickMs = 100.0;
        /// <summary>Fraction of the build-in spent sweeping the ruler; the rest drops the ticks in.</summary>
        public const float SweepShare = 0.6f;
        public const int SweepStepPx = 10;
        private static readonly int[] DropSteps = { -4, -2, 0 };

        public static bool IsTimed(ReplayMarkKind k) { return k == ReplayMarkKind.Shot || k == ReplayMarkKind.Foul; }

        public static bool IsCrossed(ReplayMarkKind k) { return k != ReplayMarkKind.Shot && k != ReplayMarkKind.None; }

        /// <summary>Ruler span: max(600, slower timed reaction + 50) ms.</summary>
        public static double SpanMs(ReplayMarks m)
        {
            double slower = 0;
            if (IsTimed(m.you) && m.youNs >= 0) slower = Math.Max(slower, m.youNs / 1e6);
            if (IsTimed(m.opp) && m.oppNs >= 0) slower = Math.Max(slower, m.oppNs / 1e6);
            return Math.Max(MinSpanMs, slower + TailMs);
        }

        /// <summary>ms -> strip x, linear from RulerLeft (0 ms) to RulerRight (span), rounded, clamped.</summary>
        public static int MsToPx(double ms, double spanMs)
        {
            if (!(spanMs > 0)) spanMs = MinSpanMs;
            if (double.IsNaN(ms) || ms < 0) ms = 0;
            if (ms > spanMs) ms = spanMs;
            return RulerLeft + (int)Math.Floor(ms / spanMs * (RulerRight - RulerLeft) + 0.5);
        }

        /// <summary>Number of ruler ticks (0, 100, 200 ... up to the span).</summary>
        public static int TickCount(double spanMs)
        {
            if (!(spanMs > 0)) return 1;
            return (int)Math.Floor(spanMs / TickMs + 1e-9) + 1;
        }

        public static int TickX(int i, double spanMs) { return MsToPx(i * TickMs, spanMs); }

        /// <summary>x of each mark: timed marks at their reaction, others at the right edge (apart if both are).</summary>
        public static void MarkXs(ReplayMarks m, double spanMs, out int youX, out int oppX)
        {
            bool ty = IsTimed(m.you) && m.youNs >= 0, to = IsTimed(m.opp) && m.oppNs >= 0;
            youX = ty ? MsToPx(m.youNs / 1e6, spanMs) : RulerRight;
            oppX = to ? MsToPx(m.oppNs / 1e6, spanMs) : RulerRight;
            if (!ty && !to) youX = RulerRight - BothEdgeOffset;
        }

        /// <summary>Left x of a label of width w centred on x, kept inside the strip margins.</summary>
        public static int Centered(int x, int w)
        {
            int l = x - w / 2;
            int max = Width - EdgeMargin - w;
            if (l > max) l = max;
            if (l < EdgeMargin) l = EdgeMargin;
            return l;
        }

        /// <summary>
        /// Name labels over two ticks: each centred on its tick; if they would collide, the left one ends just
        /// left of the midpoint and the right one starts LabelGap px after it; then both shift to stay inside.
        /// </summary>
        public static void PlaceLabels(int xa, int wa, int xb, int wb, out int la, out int lb)
        {
            la = Centered(xa, wa);
            lb = Centered(xb, wb);
            bool aLeft = xa <= xb;
            int xl = aLeft ? xa : xb, wl = aLeft ? wa : wb, xr = aLeft ? xb : xa, wr = aLeft ? wb : wa;
            int ll = aLeft ? la : lb, lr = aLeft ? lb : la;
            if (ll + wl + LabelGap > lr)
            {
                int mid = (xl + xr) / 2;
                ll = mid - LabelGap / 2 - wl;
                lr = ll + wl + LabelGap;
                if (ll < EdgeMargin) { ll = EdgeMargin; lr = ll + wl + LabelGap; }
                if (lr + wr > Width - EdgeMargin) { lr = Width - EdgeMargin - wr; ll = lr - LabelGap - wl; }
            }
            la = aLeft ? ll : lr;
            lb = aLeft ? lr : ll;
        }

        public static bool HasGap(ReplayMarks m)
        {
            return m.you == ReplayMarkKind.Shot && m.opp == ReplayMarkKind.Shot && m.youNs >= 0 && m.oppNs >= 0;
        }

        /// <summary>"+24.9 ms": opponent minus you, so + means you were faster (as on the card). Rounded to 0.1 ms.</summary>
        public static string GapText(long gapNs)
        {
            bool plus = gapNs >= 0;
            long a = plus ? gapNs : -gapNs;
            long tenths = (a + 50000) / 100000;
            return (plus ? "+" : "-") + (tenths / 10).ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   (char)('0' + (int)(tenths % 10)) + " ms";
        }

        // ------------------------------------------------------------------ build-in (stepped, whole pixels)

        private static float Sweep(float total) { return total * SweepShare; }

        /// <summary>Columns of the ruler revealed at time t (multiples of SweepStepPx, then the full width).</summary>
        public static int SweepColumns(float t, float total)
        {
            float s = Sweep(total);
            if (total <= 0f || t >= s) return Width;
            if (t < 0f) t = 0f;
            int c = ((int)(t / s * Width) / SweepStepPx + 1) * SweepStepPx;
            return c > Width ? Width : c;
        }

        public static bool TicksVisible(float t, float total) { return total <= 0f || t >= Sweep(total); }

        public static bool Landed(float t, float total) { return total <= 0f || t >= total; }

        /// <summary>Vertical offset of the dropping ticks: -4, -2, then 0 (landed).</summary>
        public static int DropOffset(float t, float total)
        {
            if (Landed(t, total)) return 0;
            float s = Sweep(total);
            if (t < s) return DropSteps[0];
            float step = (total - s) / DropSteps.Length;
            int i = step > 0f ? (int)((t - s) / step) : DropSteps.Length - 1;
            if (i >= DropSteps.Length) i = DropSteps.Length - 1;
            return DropSteps[i];
        }
    }

    /// <summary>
    /// The reaction replay strip under the round card: a ms ruler from DRAW to the slower shot, your tick
    /// (white), the opponent's (red), crosses for fouls / no-shots, and the gap. Drawn into a small
    /// point-filtered texture like EcgMonitor, redrawn only when the stepped build-in changes (no per-frame
    /// allocations). Labels are BitmapText in the small font.
    /// </summary>
    public sealed class ReplayStrip
    {
        private const int W = ReplayStripMath.Width, H = ReplayStripMath.Height;
        private readonly SpriteRenderer sr;
        private readonly Texture2D tex;
        private readonly Color32[] px = new Color32[W * H];
        private readonly BitmapText youLabel, oppLabel, gapLabel;
        private ReplayMarks marks;
        private double span;
        private int youX, oppX, youLx, oppLx, gapLx;
        private bool hasGap;
        private float t, total;
        private bool shown;
        private int lastSweep = -1, lastOff = int.MinValue;
        private bool lastTicks, lastLanded;
        private int left, top;

        private static readonly Color32 YouCol = ReplayStripPainter.YouCol;
        private static readonly Color32 OppCol = ReplayStripPainter.OppCol;
        private static readonly Color32 GapCol = ReplayStripPainter.GapCol;

        public ReplayStrip(Transform parent, BitmapFont font, int order)
        {
            tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawReplayStrip";
            var spr = Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
            sr = PawGfx.NewSprite("ReplayStrip", parent, order);
            sr.sprite = spr;
            sr.color = Color.white;
            youLabel = Label("ReplayYou", parent, font, order + 1, YouCol);
            oppLabel = Label("ReplayOpp", parent, font, order + 1, OppCol);
            gapLabel = Label("ReplayGap", parent, font, order + 1, GapCol);
            Hide();
        }

        private static BitmapText Label(string name, Transform parent, BitmapFont font, int order, Color32 c)
        {
            var t = BitmapText.Create(name, parent, font, order, TextAlign.Left);
            t.SetColors(c, Pal.Ink, Pal.Ink2);
            t.Outline = false;
            t.Shadow = false;
            t.Visible = false;
            return t;
        }

        public bool Visible { get { return shown; } }

        /// <summary>Starts the build-in (instantly when reducedMotion or seconds &lt;= 0). Invalid marks hide the strip.</summary>
        public void Show(ReplayMarks m, string youName, string oppName, bool reducedMotion)
        {
            if (!m.valid) { Hide(); return; }
            marks = m;
            span = ReplayStripMath.SpanMs(m);
            ReplayStripMath.MarkXs(m, span, out youX, out oppX);
            youLabel.SetText(string.IsNullOrEmpty(youName) ? "YOU" : youName);
            oppLabel.SetText(oppName ?? "");
            ReplayStripMath.PlaceLabels(youX, youLabel.Width, oppX, oppLabel.Width, out youLx, out oppLx);
            hasGap = ReplayStripMath.HasGap(m);
            if (hasGap)
            {
                gapLabel.SetText(ReplayStripMath.GapText(m.oppNs - m.youNs));
                gapLx = ReplayStripMath.Centered((youX + oppX) / 2, gapLabel.Width);
            }
            total = reducedMotion ? 0f : Mathf.Max(0f, m.seconds);
            t = 0f;
            shown = true;
            sr.enabled = true;
            lastSweep = -1;
            lastOff = int.MinValue;
            lastTicks = lastLanded = false;
            PlaceLabels();
            Refresh(true);
        }

        public void Hide()
        {
            shown = false;
            sr.enabled = false;
            youLabel.Visible = oppLabel.Visible = gapLabel.Visible = false;
        }

        /// <summary>Top-left of the strip on the canvas.</summary>
        public void PlaceAt(int canvasLeft, int canvasTop)
        {
            left = canvasLeft;
            top = canvasTop;
            PawGfx.Place(sr, left, top);
            PlaceLabels();
        }

        private void PlaceLabels()
        {
            youLabel.At(left + youLx, top + ReplayStripMath.LabelTop);
            oppLabel.At(left + oppLx, top + ReplayStripMath.LabelTop);
            gapLabel.At(left + gapLx, top + ReplayStripMath.GapLabelTop);
        }

        public void Tick(float dt)
        {
            if (!shown || ReplayStripMath.Landed(t, total)) return;
            t += dt;
            Refresh(false);
        }

        private void Refresh(bool force)
        {
            int sweep = ReplayStripMath.SweepColumns(t, total);
            bool ticks = ReplayStripMath.TicksVisible(t, total);
            int off = ReplayStripMath.DropOffset(t, total);
            bool landed = ReplayStripMath.Landed(t, total);
            if (!force && sweep == lastSweep && ticks == lastTicks && off == lastOff && landed == lastLanded) return;
            lastSweep = sweep;
            lastTicks = ticks;
            lastOff = off;
            lastLanded = landed;
            Render(sweep, ticks, off, landed);
            youLabel.Visible = oppLabel.Visible = landed;
            gapLabel.Visible = landed && hasGap;
        }

        private void Render(int sweep, bool ticks, int off, bool landed)
        {
            ReplayStripPainter.Paint(px, W, H, 0, 0, marks, span, youX, oppX, hasGap, sweep, ticks, off, landed);
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }
    }

    /// <summary>
    /// Draws the replay strip (frame, ruler, DRAW post, ticks, gap) into any Color32 buffer whose rows run
    /// bottom-up like a Texture2D. Used by the live strip under the round card (its own 200x24 texture) and by
    /// the share card (at an offset inside the 270x480 composition), so both are pixel-identical.
    /// Strip coordinates: x right, y down from the strip's top-left; pixels outside the strip or the buffer are skipped.
    /// </summary>
    public static class ReplayStripPainter
    {
        private const int W = ReplayStripMath.Width, H = ReplayStripMath.Height;
        public static readonly Color32 Bg = Pal.Ink;
        public static readonly Color32 Frame = Pal.Wood4;
        public static readonly Color32 Ruler = Pal.Sand3;
        public static readonly Color32 DrawCol = Pal.SigYellow;
        public static readonly Color32 YouCol = Pal.Cream0;
        public static readonly Color32 OppCol = Pal.SigRed;
        public static readonly Color32 GapCol = Pal.SigYellow;

        public static void Paint(Color32[] buf, int bufW, int bufH, int left, int top, ReplayMarks marks, double span,
            int youX, int oppX, bool hasGap, int sweep, bool ticks, int off, bool landed)
        {
            var c = new Target { buf = buf, w = bufW, h = bufH, left = left, top = top };
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++) c.Put(x, y, Bg);
            for (int x = 0; x < W; x++) { c.Put(x, 0, Frame); c.Put(x, H - 1, Frame); }
            for (int y = 0; y < H; y++) { c.Put(0, y, Frame); c.Put(W - 1, y, Frame); }

            int b = ReplayStripMath.Baseline;
            // ruler, swept in from the left
            for (int x = ReplayStripMath.RulerLeft; x <= ReplayStripMath.RulerRight && x < sweep; x++) c.Put(x, b, Ruler);
            int n = ReplayStripMath.TickCount(span);
            for (int i = 0; i < n; i++)
            {
                int x = ReplayStripMath.TickX(i, span);
                if (x >= sweep) break;
                c.Put(x, b + 1, Ruler);
                c.Put(x, b + 2, Ruler);
            }
            // DRAW (0 ms): a bright post with a small star on top
            int dx = ReplayStripMath.RulerLeft;
            if (dx < sweep)
            {
                for (int y = 8; y <= b + 2; y++) c.Put(dx, y, DrawCol);
                c.Put(dx, 5, DrawCol);
                c.Put(dx - 1, 6, DrawCol);
                c.Put(dx, 6, Pal.White);
                c.Put(dx + 1, 6, DrawCol);
                c.Put(dx, 7, DrawCol);
            }

            if (ticks)
            {
                if (landed && hasGap)
                {
                    int a = youX < oppX ? youX : oppX, e = youX < oppX ? oppX : youX;
                    for (int x = a; x <= e; x++) c.Put(x, b, GapCol);
                }
                // the slower / later mark first, so the faster one stays on top where they touch
                bool youFirst = youX >= oppX;
                DrawMark(ref c, youFirst ? marks.you : marks.opp, youFirst ? youX : oppX, youFirst ? YouCol : OppCol, off);
                DrawMark(ref c, youFirst ? marks.opp : marks.you, youFirst ? oppX : youX, youFirst ? OppCol : YouCol, off);
            }
        }

        private static void DrawMark(ref Target c, ReplayMarkKind k, int x, Color32 col, int off)
        {
            if (k == ReplayMarkKind.None) return;
            int b = ReplayStripMath.Baseline;
            if (!ReplayStripMath.IsCrossed(k))
            {
                // a solid tick with a 3 px cap, landing on a 3 px foot on the ruler
                for (int d = -1; d <= 1; d++) c.Put(x + d, 8 + off, col);
                for (int y = 9 + off; y < b; y++) c.Put(x, y, col);
                if (off == 0) for (int d = -1; d <= 1; d++) c.Put(x + d, b, col);
                return;
            }
            // crossed tick: a 5x5 X with a short stem down to the ruler
            for (int i = 0; i < 5; i++)
            {
                c.Put(x - 2 + i, 8 + off + i, col);
                c.Put(x + 2 - i, 8 + off + i, col);
            }
            for (int y = 13 + off; y < b; y++) c.Put(x, y, col);
        }

        private struct Target
        {
            public Color32[] buf;
            public int w, h, left, top;

            public void Put(int x, int y, Color32 col)
            {
                if (x < 0 || x >= W || y < 0 || y >= H) return;
                int bx = left + x, by = top + y;
                if (bx < 0 || bx >= w || by < 0 || by >= h) return;
                buf[(h - 1 - by) * w + bx] = col;
            }
        }
    }
}
