using NUnit.Framework;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>Match intro timeline (feel wave 2): starts from the title positions, never jumps, never leaves the canvas.</summary>
    public class IntroMathTests
    {
        // canvas 360x640; bandit cell 117 wide, pivot x 60, feet at x 278 (duel_config layout)
        private const int CanvasW = 360;
        private const int CellW = 117;
        private const int CellLeft = 278 - 60;
        private static readonly int[] VisibleRights = { 315, 328, 360 };   // phones (270 wide), 9:16, iPad-ish

        private const float Dt = 1f / 120f;

        [Test]
        public void FrameZeroIsTheTitlePose()
        {
            foreach (bool walks in new[] { true, false })
                foreach (bool reduced in new[] { true, false })
                {
                    var p = IntroMath.Sample(0f, reduced, walks, IntroMath.ExitDistance(315, CellLeft));
                    Assert.AreEqual(0, p.heroDy, "hero");
                    Assert.AreEqual(0, p.oppDx, "opponent x");
                    Assert.AreEqual(0, p.oppDy, "opponent y");
                    // the first rendered frame after the tap (one 120 Hz frame and one 30 Hz frame later) still is
                    var q = IntroMath.Sample(1f / 30f, reduced, walks, IntroMath.ExitDistance(315, CellLeft));
                    Assert.AreEqual(0, q.heroDy);
                    Assert.AreEqual(0, q.oppDx);
                }
        }

        [Test]
        public void EndsAtRestOnTheMarks()
        {
            foreach (bool walks in new[] { true, false })
            {
                var p = IntroMath.Sample(IntroMath.Duration(false), false, walks, 99);
                Assert.AreEqual(0, p.heroDy);
                Assert.AreEqual(0, p.oppDx);
                Assert.AreEqual(0, p.oppDy);
                Assert.IsTrue(p.heroReady);
                // the opponent is home before his name plate slams
                var q = IntroMath.Sample(IntroMath.OppPlate(false), false, walks, 99);
                Assert.AreEqual(0, q.oppDx);
            }
        }

        [Test]
        public void HeroOnlySettlesTwoFrames()
        {
            int frames = 0, prev = 0, changes = 0;
            for (float t = 0f; t <= IntroMath.Total; t += Dt)
            {
                var p = IntroMath.Sample(t, false, true, 99);
                Assert.That(p.heroDy, Is.InRange(0, 2), "hero never moves more than 2 px, never sideways");
                if (p.heroDy != 0) frames++;
                if (p.heroDy != prev) { changes++; prev = p.heroDy; }
            }
            Assert.AreEqual(3, changes, "0 -> 2 -> 1 -> 0");
            Assert.That(frames * Dt, Is.EqualTo(2f / IntroMath.StepHz).Within(2 * Dt), "two 12 Hz steps");
        }

        [Test]
        public void OpponentLeavesTheViewAndComesBack()
        {
            foreach (int vr in VisibleRights)
            {
                int exit = IntroMath.ExitDistance(vr, CellLeft);
                Assert.AreEqual(vr - CellLeft + IntroMath.ExitMargin, exit);
                // off screen (whole cell past the visible edge) at the turn, home at the end
                var away = IntroMath.Sample((IntroMath.OutTo + IntroMath.InFrom) * 0.5f, false, true, exit);
                Assert.AreEqual(exit, away.oppDx);
                Assert.Greater(CellLeft + away.oppDx, vr, "fully out of view at the turn");
            }
        }

        [Test]
        public void PositionsNeverOffCanvasAndNeverJump()
        {
            foreach (int vr in VisibleRights)
                foreach (bool reduced in new[] { false, true })
                {
                    int exit = IntroMath.ExitDistance(vr, CellLeft);
                    int prevDx = 0;
                    // the biggest legal step: the last walk-out step (quad, 5 steps: 36 %) or the first walk-in step
                    // (cubic, 7 steps: 37 %), both while he is mostly out of view
                    int maxStep = (int)System.Math.Ceiling(exit * 0.40);
                    for (float t = 0f; t <= IntroMath.Duration(reduced) + 0.1f; t += Dt)
                    {
                        var p = IntroMath.Sample(t, reduced, true, exit);
                        Assert.That(p.oppDx, Is.InRange(0, exit), "never farther than just out of view");
                        Assert.LessOrEqual(CellLeft + p.oppDx, CanvasW + IntroMath.ExitMargin, "just out of view, never far off the canvas");
                        Assert.GreaterOrEqual(CellLeft + p.oppDx + CellW, 0);
                        Assert.That(p.oppDy, Is.InRange(-1, 0), "1 px walk bob at most");
                        Assert.LessOrEqual(System.Math.Abs(p.oppDx - prevDx), maxStep, "no teleport at t=" + t);
                        prevDx = p.oppDx;
                        if (reduced) { Assert.AreEqual(0, p.oppDx); Assert.AreEqual(0, p.heroDy); }
                    }
                }
        }

        [Test]
        public void StayModeKeepsTheOpponentOnHisMark()
        {
            for (float t = 0f; t <= IntroMath.Total; t += Dt)
            {
                var p = IntroMath.Sample(t, false, false, 99);
                Assert.AreEqual(0, p.oppDx);
                Assert.That(p.oppDy, Is.InRange(0, 2));
            }
        }

        [Test]
        public void MotionIsSteppedAt12Hz()
        {
            // inside one step the offsets do not change, whatever the frame rate
            float stepLen = (IntroMath.InTo - IntroMath.InFrom) / 7f;
            var a = IntroMath.Sample(IntroMath.InFrom + 0.0001f, false, true, 99);
            var b = IntroMath.Sample(IntroMath.InFrom + stepLen - 0.001f, false, true, 99);
            var c = IntroMath.Sample(IntroMath.InFrom + stepLen + 0.001f, false, true, 99);
            Assert.AreEqual(a.oppDx, b.oppDx);
            Assert.Less(c.oppDx, b.oppDx, "the next step moves him in");
        }

        [Test]
        public void FeelConfigDefaultsAndParsing()
        {
            Assert.IsTrue(FeelConfig.Load(null).IntroOpponentWalks);
            Assert.IsFalse(FeelConfig.Load("{\"introOpponent\":\"stay\"}").IntroOpponentWalks);
            Assert.IsTrue(FeelConfig.Load("{broken").IntroOpponentWalks);
            Assert.AreEqual(3, FeelConfig.Load("{}").bountyWinsRequired);
        }
    }
}
