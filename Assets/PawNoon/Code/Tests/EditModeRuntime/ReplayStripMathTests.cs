using NUnit.Framework;
using PawNoon.Core;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>Reaction replay strip (SPEC_V2 §8.1): ms -> px mapping, label placement, build-in steps.</summary>
    public class ReplayStripMathTests
    {
        private const int W = ReplayStripMath.Width;
        private const int L = ReplayStripMath.RulerLeft;
        private const int R = ReplayStripMath.RulerRight;

        private static long Ms(double ms) { return DuelConfig.MsToNs(ms); }

        [Test]
        public void SpanIsAtLeast600AndCoversTheSlowerShotPlus50()
        {
            Assert.AreEqual(600, ReplayStripMath.SpanMs(ReplayMarks.Shots(Ms(214.382), Ms(288.105), 0)), 1e-9);
            Assert.AreEqual(783, ReplayStripMath.SpanMs(ReplayMarks.Shots(Ms(200), Ms(733), 0)), 1e-9);
            Assert.AreEqual(550 + 50, ReplayStripMath.SpanMs(ReplayMarks.Shots(Ms(550), Ms(120), 0)), 1e-9);
            var noTimes = new ReplayMarks { valid = true, you = ReplayMarkKind.NoShot, opp = ReplayMarkKind.Early, youNs = -1, oppNs = -1 };
            Assert.AreEqual(600, ReplayStripMath.SpanMs(noTimes), 1e-9);
        }

        [Test]
        public void MsToPxIsLinearRoundedAndClamped()
        {
            Assert.AreEqual(L, ReplayStripMath.MsToPx(0, 600));
            Assert.AreEqual(R, ReplayStripMath.MsToPx(600, 600));
            Assert.AreEqual(L + 94, ReplayStripMath.MsToPx(300, 600), "187 px * 0.5 = 93.5 rounds half up");
            Assert.AreEqual(L + 67, ReplayStripMath.MsToPx(214.382, 600));
            Assert.AreEqual(L, ReplayStripMath.MsToPx(-5, 600));
            Assert.AreEqual(L, ReplayStripMath.MsToPx(double.NaN, 600));
            Assert.AreEqual(R, ReplayStripMath.MsToPx(5000, 600));
            Assert.AreEqual(R, ReplayStripMath.MsToPx(600, 0), "bad span falls back to 600");
            int prev = int.MinValue;
            for (double ms = 0; ms <= 800; ms += 0.5)
            {
                int x = ReplayStripMath.MsToPx(ms, 783);
                Assert.GreaterOrEqual(x, prev, "monotonic at " + ms);
                Assert.That(x, Is.InRange(L, R));
                prev = x;
            }
        }

        [Test]
        public void TicksEvery100Ms()
        {
            Assert.AreEqual(7, ReplayStripMath.TickCount(600));   // 0..600
            Assert.AreEqual(7, ReplayStripMath.TickCount(650));
            Assert.AreEqual(8, ReplayStripMath.TickCount(783));
            Assert.AreEqual(L, ReplayStripMath.TickX(0, 600));
            Assert.AreEqual(R, ReplayStripMath.TickX(6, 600));
        }

        [Test]
        public void CrossedMarksSitAtTheirTimeOrTheRightEdge()
        {
            var m = new ReplayMarks { valid = true, you = ReplayMarkKind.Foul, youNs = Ms(80), opp = ReplayMarkKind.Shot, oppNs = Ms(260) };
            int y, o;
            ReplayStripMath.MarkXs(m, ReplayStripMath.SpanMs(m), out y, out o);
            Assert.AreEqual(ReplayStripMath.MsToPx(80, 600), y, "anticipation foul at its time");
            m = new ReplayMarks { valid = true, you = ReplayMarkKind.NoShot, youNs = -1, opp = ReplayMarkKind.Shot, oppNs = Ms(301) };
            ReplayStripMath.MarkXs(m, ReplayStripMath.SpanMs(m), out y, out o);
            Assert.AreEqual(R, y, "no shot at the right edge");
            m = new ReplayMarks { valid = true, you = ReplayMarkKind.Early, youNs = -1, opp = ReplayMarkKind.Stopped, oppNs = -1 };
            ReplayStripMath.MarkXs(m, ReplayStripMath.SpanMs(m), out y, out o);
            Assert.AreEqual(R, o);
            Assert.AreEqual(R - ReplayStripMath.BothEdgeOffset, y, "both at the edge: kept apart");
            Assert.IsTrue(ReplayStripMath.IsCrossed(ReplayMarkKind.Foul));
            Assert.IsFalse(ReplayStripMath.IsCrossed(ReplayMarkKind.Shot));
        }

        [Test]
        public void LabelsCentreOnTicksAndStayInside()
        {
            Assert.AreEqual(100 - 7, ReplayStripMath.Centered(100, 14));
            Assert.AreEqual(ReplayStripMath.EdgeMargin, ReplayStripMath.Centered(3, 14));
            Assert.AreEqual(W - ReplayStripMath.EdgeMargin - 14, ReplayStripMath.Centered(W - 1, 14));

            int la, lb;
            ReplayStripMath.PlaceLabels(40, 14, 150, 40, out la, out lb);   // far apart: plain centring
            Assert.AreEqual(33, la);
            Assert.AreEqual(130, lb);
        }

        [Test]
        public void CloseLabelsPushApartWithoutOverlap()
        {
            const int wy = 14, wo = 40, gap = ReplayStripMath.LabelGap;
            for (int xa = L; xa <= R; xa += 3)
                for (int d = -30; d <= 30; d += 2)
                {
                    int xb = xa + d;
                    if (xb < L || xb > R) continue;
                    int la, lb;
                    ReplayStripMath.PlaceLabels(xa, wy, xb, wo, out la, out lb);
                    string at = "xa=" + xa + " xb=" + xb;
                    Assert.GreaterOrEqual(la, ReplayStripMath.EdgeMargin, at);
                    Assert.GreaterOrEqual(lb, ReplayStripMath.EdgeMargin, at);
                    Assert.LessOrEqual(la + wy, W - ReplayStripMath.EdgeMargin, at);
                    Assert.LessOrEqual(lb + wo, W - ReplayStripMath.EdgeMargin, at);
                    if (xa <= xb) Assert.GreaterOrEqual(lb, la + wy + gap, "a left of b, " + at);
                    else Assert.GreaterOrEqual(la, lb + wo + gap, "b left of a, " + at);
                }
        }

        [Test]
        public void GapTextRoundsToTenthsWithSign()
        {
            Assert.AreEqual("+73.7 ms", ReplayStripMath.GapText(Ms(288.105) - Ms(214.382)));
            Assert.AreEqual("+24.9 ms", ReplayStripMath.GapText(24949999));
            Assert.AreEqual("-25.0 ms", ReplayStripMath.GapText(-24950000));
            Assert.AreEqual("+0.0 ms", ReplayStripMath.GapText(0));
            Assert.AreEqual("+120.5 ms", ReplayStripMath.GapText(120450000));
            Assert.IsTrue(ReplayStripMath.HasGap(ReplayMarks.Shots(1, 2, 0)));
            var foul = ReplayMarks.Shots(1, 2, 0);
            foul.you = ReplayMarkKind.Foul;
            Assert.IsFalse(ReplayStripMath.HasGap(foul));
        }

        [Test]
        public void BuildInIsSteppedAndReducedMotionIsInstant()
        {
            const float total = 0.35f;
            Assert.AreEqual(10, ReplayStripMath.SweepColumns(0f, total));
            int prev = 0;
            for (float t = 0f; t < 0.4f; t += 0.004f)
            {
                int c = ReplayStripMath.SweepColumns(t, total);
                Assert.AreEqual(0, c % ReplayStripMath.SweepStepPx, "whole steps");
                Assert.GreaterOrEqual(c, prev);
                prev = c;
            }
            Assert.AreEqual(W, ReplayStripMath.SweepColumns(total * ReplayStripMath.SweepShare, total));
            Assert.IsFalse(ReplayStripMath.TicksVisible(0.1f, total));
            Assert.IsTrue(ReplayStripMath.TicksVisible(0.22f, total));
            Assert.AreEqual(-4, ReplayStripMath.DropOffset(0.22f, total));
            Assert.AreEqual(-2, ReplayStripMath.DropOffset(0.27f, total));
            Assert.AreEqual(0, ReplayStripMath.DropOffset(0.33f, total));
            Assert.IsFalse(ReplayStripMath.Landed(0.34f, total));
            Assert.IsTrue(ReplayStripMath.Landed(0.35f, total));
            // Reduced Motion / seconds 0: everything at once
            Assert.AreEqual(W, ReplayStripMath.SweepColumns(0f, 0f));
            Assert.IsTrue(ReplayStripMath.TicksVisible(0f, 0f));
            Assert.IsTrue(ReplayStripMath.Landed(0f, 0f));
            Assert.AreEqual(0, ReplayStripMath.DropOffset(0f, 0f));
        }

        [Test]
        public void MarksComeFromTheRoundRecord()
        {
            var r = new RoundRecord();
            r.roundId = 3;
            r.slots[0].outcome = SlotOutcome.Shot;
            r.slots[0].reactionNs = Ms(214);
            r.slots[1].outcome = SlotOutcome.Anticipation;
            r.slots[1].reactionNs = Ms(90);
            var m = ReplayMarks.FromRound(r, 0.35f);
            Assert.IsTrue(m.valid);
            Assert.AreEqual(ReplayMarkKind.Shot, m.you);
            Assert.AreEqual(Ms(214), m.youNs);
            Assert.AreEqual(ReplayMarkKind.Foul, m.opp);
            Assert.AreEqual(Ms(90), m.oppNs);
            Assert.AreEqual(0.35f, m.seconds);

            r.slots[0].outcome = SlotOutcome.Foul; r.slots[0].reactionNs = -1;
            r.slots[1].outcome = SlotOutcome.Stopped; r.slots[1].reactionNs = -1;
            m = ReplayMarks.FromRound(r, 0f);
            Assert.AreEqual(ReplayMarkKind.Early, m.you);
            Assert.AreEqual(ReplayMarkKind.Stopped, m.opp);
            Assert.AreEqual(-1, m.youNs);

            r.slots[0].outcome = SlotOutcome.NoShot;
            Assert.AreEqual(ReplayMarkKind.NoShot, ReplayMarks.FromRound(r, 0f).you);
            r.isVoid = true;
            Assert.IsFalse(ReplayMarks.FromRound(r, 0f).valid);
            Assert.IsFalse(ReplayMarks.FromRound(null, 0f).valid);
            Assert.IsFalse(default(ReplayMarks).valid, "default RoundView shows no strip");
        }
    }
}
