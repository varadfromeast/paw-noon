using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    public class BanterTests
    {
        [Test]
        public void ContextPriority()
        {
            Assert.AreEqual(BanterContext.MatchStart, BanterPicker.Context(true, 10, 10, 30, true, true, 1));
            Assert.AreEqual(BanterContext.LastOne, BanterPicker.Context(false, 100, 29, 30, true, true, 1));
            Assert.AreEqual(BanterContext.LastOne, BanterPicker.Context(false, 12, 100, 30, false, false, -1));
            Assert.AreEqual(BanterContext.Lightning, BanterPicker.Context(false, 100, 40, 30, true, true, 0));
            Assert.AreEqual(BanterContext.PlayerEarly, BanterPicker.Context(false, 100, 40, 30, false, true, 1));
            Assert.AreEqual(BanterContext.BotWonRound, BanterPicker.Context(false, 70, 100, 30, false, false, 1));
            Assert.AreEqual(BanterContext.BotLostRound, BanterPicker.Context(false, 100, 70, 30, false, false, 0));
            Assert.AreEqual(BanterContext.None, BanterPicker.Context(false, 70, 70, 30, false, false, -1)); // tie round
            // exactly 30 HP is not "under 30"; 0 HP (dead) never counts
            Assert.AreEqual(BanterContext.None, BanterPicker.Context(false, 30, 0, 30, false, false, -1));
        }

        [Test]
        public void NeverTheSameLineTwiceInARow()
        {
            var lines = new BanterLines();
            var p = new BanterPicker(9);
            string prev = null;
            var contexts = new[] { BanterContext.BotWonRound, BanterContext.BotLostRound, BanterContext.LastOne, BanterContext.MatchStart };
            for (int i = 0; i < 2000; i++)
            {
                var s = p.Pick(lines, contexts[i % 2 == 0 ? 0 : (i / 2) % contexts.Length], 1.0);
                Assert.IsNotNull(s);
                Assert.AreNotEqual(prev, s);
                prev = s;
            }
            // a single-line pool is allowed to repeat (nothing else to say)
            Assert.AreEqual("A", p.Line(new[] { "A" }));
            Assert.AreEqual("A", p.Line(new[] { "A" }));
        }

        [Test]
        public void MatchStartAndLastOneAlwaysSpeak()
        {
            var lines = new BanterLines();
            var p = new BanterPicker(3);
            for (int i = 0; i < 50; i++)
            {
                Assert.IsNotNull(p.Pick(lines, BanterContext.MatchStart, 0.0));
                Assert.IsNotNull(p.Pick(lines, BanterContext.LastOne, 0.0));
                Assert.IsNotNull(p.Pick(lines, BanterContext.MatchWin, 0.0));
                Assert.IsNull(p.Pick(lines, BanterContext.BotWonRound, 0.0));
                Assert.IsNull(p.Pick(lines, BanterContext.None, 1.0));
            }
        }

        [Test]
        public void OtherContextsSpeakAboutHalfTheTime()
        {
            var lines = new BanterLines();
            var p = new BanterPicker(17);
            int said = 0, n = 20000;
            for (int i = 0; i < n; i++) if (p.Pick(lines, BanterContext.BotLostRound, 0.55) != null) said++;
            double rate = said / (double)n;
            Assert.Greater(rate, 0.52);
            Assert.Less(rate, 0.58);
        }

        [Test]
        public void LinesComeFromConfigWithTwoToThreePerContext()
        {
            var c = DuelConfig.FromJson("{\"banterChance\": 0.3, \"blackpawLines\": {\"botLost\": [\"Huh.\", \"Fine.\"], \"botWon\": []}}");
            Assert.AreEqual(0.3, c.banterChance, 1e-12);
            CollectionAssert.AreEqual(new[] { "Huh.", "Fine." }, c.blackpawLines.botLost);
            Assert.AreEqual(new BanterLines().botWon.Length, c.blackpawLines.botWon.Length); // empty list keeps defaults
            var d = new BanterLines();
            var all = new[] { d.matchStart, d.lastOne, d.lightning, d.playerEarly, d.botWon, d.botLost, d.matchWin };
            foreach (var pool in all)
            {
                Assert.GreaterOrEqual(pool.Length, 2);
                Assert.LessOrEqual(pool.Length, 3);
            }
            Assert.Contains("Yer hand's shakin', kid.", d.matchStart);
            Assert.Contains("Too slow, pardner.", d.botWon);
            Assert.Contains("Lucky shot.", d.botLost);
            Assert.Contains("Jumpy, ain't ya?", d.playerEarly);
            Assert.Contains("Last one, pardner.", d.lastOne);
            Assert.Contains("...Who taught you that?", d.lightning);
            Assert.Contains("Sleep tight, kid.", d.matchWin);
        }
    }

    public class BubbleGeometryTests
    {
        private static readonly PxRect Screen = new PxRect(45, 0, 270, 640);

        private static void TailTip(int tailX, bool tailLeft, PxRect body, out int cx, out int bottom)
        {
            var last = BubbleGeometry.Step(BubbleGeometry.TailSteps - 1, tailLeft, tailX, body.yMax);
            cx = last.x + last.w / 2;
            bottom = last.yMax;
        }

        [Test]
        public void SizedToTextPlusPaddingAndTailTipOnAnchor()
        {
            int tailX;
            var r = BubbleGeometry.Layout(100, 8, 264, 351, false, Screen, 80, 2, out tailX);
            Assert.AreEqual(112, r.w);
            Assert.AreEqual(20, r.h);
            int cx, bottom;
            TailTip(tailX, false, r, out cx, out bottom);
            Assert.AreEqual(264, cx);
            Assert.AreEqual(351, bottom);
            Assert.LessOrEqual(r.xMax, 264 + 6); // bubble sits left of the speaker (tail starts 12 px in from its right end)

            r = BubbleGeometry.Layout(60, 8, 100, 300, true, Screen, 80, 2, out tailX);
            TailTip(tailX, true, r, out cx, out bottom);
            Assert.AreEqual(100, cx);
            Assert.AreEqual(300, bottom);
            Assert.GreaterOrEqual(r.x, 94);
        }

        [Test]
        public void TailStepsShrinkOnePixel()
        {
            for (int k = 1; k < BubbleGeometry.TailSteps; k++)
            {
                Assert.AreEqual(BubbleGeometry.Step(k - 1, false, 50, 100).w - 1, BubbleGeometry.Step(k, false, 50, 100).w);
                Assert.AreEqual(BubbleGeometry.Step(k - 1, true, 50, 100).w - 1, BubbleGeometry.Step(k, true, 50, 100).w);
            }
        }

        [Test]
        public void ClampedInsideVisibleRectAndBelowHud()
        {
            int tailX;
            // anchor far left: body must stay inside the visible rect, tail stays on the body
            var r = BubbleGeometry.Layout(150, 8, 60, 351, false, Screen, 80, 2, out tailX);
            Assert.GreaterOrEqual(r.x, Screen.x + 2);
            Assert.LessOrEqual(r.xMax, Screen.xMax - 2);
            Assert.GreaterOrEqual(tailX, r.x + 3);
            Assert.LessOrEqual(tailX + 8, r.xMax - 3);

            // anchor up under the HUD: never above minY
            r = BubbleGeometry.Layout(80, 8, 200, 70, false, Screen, 96, 2, out tailX);
            Assert.GreaterOrEqual(r.y, 96);

            // anchor at the very bottom: body + tail stay on screen
            r = BubbleGeometry.Layout(80, 8, 200, 700, true, Screen, 96, 2, out tailX);
            Assert.LessOrEqual(r.yMax + BubbleGeometry.TailBelow, Screen.yMax - 2);
        }
    }
}
