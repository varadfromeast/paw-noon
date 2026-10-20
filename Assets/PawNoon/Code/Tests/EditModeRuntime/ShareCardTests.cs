using System;
using NUnit.Framework;
using PawNoon.Core;
using PawNoon.Runtime;
using UnityEngine;

namespace PawNoon.Tests
{
    /// <summary>Share card (feel wave 2): layout maths, wording, round log.</summary>
    public class ShareCardTests
    {
        // the shipped sprite cells (ui.json, bandit.json, hero.json)
        private static readonly Vector2Int Logo = new Vector2Int(234, 60);
        private static readonly Vector2Int Banner = new Vector2Int(250, 47);
        private static readonly Vector2Int Sign = new Vector2Int(56, 18);
        private static readonly Vector2Int Card = new Vector2Int(228, 95);
        private static readonly Vector2Int Stamp = new Vector2Int(62, 60);
        private static readonly Vector2Int OppCell = new Vector2Int(117, 120);
        private static readonly Vector2Int OppPivot = new Vector2Int(60, 119);
        private static readonly Vector2Int HeroCell = new Vector2Int(295, 275);

        private static ShareCardLayout.Rects Layout(bool match)
        {
            return ShareCardLayout.Compute(match, Logo, match ? Banner : Sign, Card, Stamp, OppCell, OppPivot, HeroCell);
        }

        private static void Inside(RectInt r, string what)
        {
            Assert.GreaterOrEqual(r.xMin, 0, what + " left");
            Assert.LessOrEqual(r.xMax, ShareCardLayout.Width, what + " right");
            Assert.GreaterOrEqual(r.yMin, 0, what + " top");
            Assert.LessOrEqual(r.yMax, ShareCardLayout.Height, what + " bottom");
        }

        [Test]
        public void OutputIs540x960AtWholePixelScale()
        {
            Assert.AreEqual(540, ShareCardLayout.OutWidth);
            Assert.AreEqual(960, ShareCardLayout.OutHeight);
            Assert.AreEqual(new Vector2Int(540, 960), ShareCardLayout.Output(ShareCardLayout.Scale));
            Assert.AreEqual(9f / 16f, ShareCardLayout.Width / (float)ShareCardLayout.Height, 1e-6f, "9:16 portrait story format");
        }

        [Test]
        public void ContentFitsTheCanvasAndIsCentred()
        {
            foreach (bool match in new[] { false, true })
            {
                var r = Layout(match);
                Inside(r.logo, "logo");
                Inside(r.header, "header");
                Inside(r.card, "card");
                Inside(r.stamp, "stamp");
                Inside(r.strip, "strip");
                Inside(r.band, "band");
                Inside(r.footer, "footer");
                Assert.AreEqual(ShareCardLayout.Width - r.logo.xMax, r.logo.xMin, "logo centred");
                Assert.AreEqual(ShareCardLayout.Width - r.card.xMax, r.card.xMin, "card centred");
                Assert.AreEqual(ShareCardLayout.Width - r.strip.xMax, r.strip.xMin, "strip centred");
                Assert.AreEqual(ReplayStripMath.Width, r.strip.width);
            }
        }

        [Test]
        public void StackDoesNotOverlap()
        {
            foreach (bool match in new[] { false, true })
            {
                var r = Layout(match);
                Assert.LessOrEqual(r.logo.yMax, r.band.yMin, "the wordmark sits on the sky above the band");
                Assert.GreaterOrEqual(r.header.yMin, r.band.yMin);
                Assert.Less(r.header.yMax, r.card.yMin);
                Assert.Less(r.card.yMax, r.strip.yMin);
                Assert.LessOrEqual(r.strip.yMax, r.band.yMax);
                Assert.LessOrEqual(r.stamp.yMax, r.band.yMax, "the stamp stays on the dark band");
                Assert.LessOrEqual(r.stamp.yMax, r.strip.yMin + 1, "the stamp may touch, never cover, the strip");
                // the stamp sits on the right half of the card, clear of the left-hand text column
                Assert.Greater(r.stamp.xMin, r.card.x + ShareCardLayout.CardInsetX + 110);
                // call to action between the band and the opponent's hat
                Assert.Greater(r.ctaY, r.band.yMax);
                Assert.LessOrEqual(r.ctaY + ShareCardLayout.CtaHeight, r.opp.yMin, "CTA clear of the opponent");
                // the duel stands on the street above the footer
                Assert.LessOrEqual(r.opp.yMax, r.footer.yMin);
                Assert.AreEqual(r.footer.yMin, r.hero.yMax, "the hero is cropped by the footer, like the game's bottom edge");
                Assert.Greater(r.opp.xMin, 0);
                Assert.Less(r.opp.xMax, ShareCardLayout.Width);
            }
        }

        [Test]
        public void WideStampHangsUnderTheTextColumn()
        {
            var wide = new Vector2Int(148, 30);   // stamp_too_early
            foreach (bool match in new[] { false, true })
            {
                var r = ShareCardLayout.Compute(match, Logo, match ? Banner : Sign, Card, wide, OppCell, OppPivot, HeroCell);
                Assert.AreEqual(ShareCardLayout.Width - r.stamp.xMax, r.stamp.xMin, "centred");
                // the last text row (damage, medium font, 10 px) ends at inset + 60
                Assert.GreaterOrEqual(r.stamp.yMin, r.card.y + ShareCardLayout.CardInsetY + 60);
                Assert.Less(r.stamp.yMax, r.strip.yMin, "the strip moves down below it");
                Assert.LessOrEqual(r.strip.yMax, r.band.yMax);
                Assert.LessOrEqual(r.ctaY + ShareCardLayout.CtaHeight, r.opp.yMin);
            }
        }

        [Test]
        public void MatchHeaderPushesTheCardDownByTheBannerHeight()
        {
            var a = Layout(false);
            var b = Layout(true);
            Assert.AreEqual(Banner.y - Sign.y, b.card.y - a.card.y);
            Assert.AreEqual(a.footer, b.footer);
            Assert.AreEqual(a.opp, b.opp, "the duel scene does not move");
        }

        [Test]
        public void MissingSpritesKeepTheirSlot()
        {
            var r = ShareCardLayout.Compute(false, Vector2Int.zero, Vector2Int.zero, Vector2Int.zero, Vector2Int.zero, OppCell, OppPivot, HeroCell);
            Assert.AreEqual(228, r.card.width);
            Assert.AreEqual(ShareCardLayout.HeaderY + 18 + ShareCardLayout.CardGap, r.card.y);
            Inside(r.strip, "strip");
        }

        [Test]
        public void UpscaleIsNearestNeighbour()
        {
            var c = new PixelCanvas(3, 2);
            c.Set(0, 0, new Color32(10, 0, 0, 255));
            c.Set(2, 1, new Color32(0, 20, 0, 255));
            var up = c.Upscaled(2);
            Assert.AreEqual(6 * 4, up.Length);
            // bottom-up rows: canvas (0,0) top-left -> rows 2..3 of the 4-row output, columns 0..1
            Assert.AreEqual(10, up[3 * 6 + 0].r);
            Assert.AreEqual(10, up[3 * 6 + 1].r);
            Assert.AreEqual(10, up[2 * 6 + 1].r);
            Assert.AreEqual(0, up[3 * 6 + 2].r);
            // canvas (2,1) bottom-right -> output rows 0..1, columns 4..5
            Assert.AreEqual(20, up[0 * 6 + 5].g);
            Assert.AreEqual(20, up[1 * 6 + 4].g);
        }

        [Test]
        public void DarkenUsesACheckerEdgeNotARamp()
        {
            var c = new PixelCanvas(4, 10);
            c.Fill(new Color32(200, 200, 200, 255));
            c.Darken(2, 8, 0.3f, new Color32(0, 0, 0, 255), 2);
            Assert.AreEqual(200, c.Get(0, 1).r, "outside the band untouched");
            byte inner = c.Get(0, 4).r, e0 = c.Get(0, 2).r, e1 = c.Get(1, 2).r;
            Assert.AreNotEqual(e0, e1, "edge rows alternate");
            Assert.IsTrue(e0 == inner || e1 == inner, "one checker colour is the band colour");
            Assert.AreEqual(inner, c.Get(3, 5).r, "flat inside");
        }

        [Test]
        public void WordingMatchesTheRoundCard()
        {
            var d = ShareCardData.Sample(ShareKind.Round);
            ShareCardText.Tone t;
            Assert.AreEqual("0.214382 s", ShareCardText.YouTime(d));
            Assert.AreEqual("0.288105 s", ShareCardText.OppTime(d));
            Assert.AreEqual("+73.72 ms FASTER", ShareCardText.Gap(d, out t));
            Assert.AreEqual(ShareCardText.Tone.Good, t);
            Assert.AreEqual("HIT -34 HP", ShareCardText.Damage(d, out t));
            Assert.AreEqual("grade_perfect", ShareCardText.Stamp(d));
            Assert.AreEqual("ROUND 2", ShareCardText.RoundLabel(d));
            d.lastLight = true;
            Assert.AreEqual("LAST LIGHT", ShareCardText.RoundLabel(d));

            d.youFoul = true;
            Assert.AreEqual("--", ShareCardText.YouTime(d));
            Assert.AreEqual("TOO EARLY!", ShareCardText.Gap(d, out t));
            Assert.AreEqual("stamp_too_early", ShareCardText.Stamp(d));
            d.youFoul = false;
            d.oppFoul = true;
            Assert.AreEqual("FOUL", ShareCardText.OppTime(d));
            d.oppFoul = false;
            d.youNs = 300000000L;
            Assert.AreEqual("-11.90 ms SLOWER", ShareCardText.Gap(d, out t));
            Assert.AreEqual(ShareCardText.Tone.Bad, t);

            Assert.AreEqual("20 OCT 2026", ShareCardText.Date(new DateTime(2026, 10, 20, 23, 59, 0)));
            Assert.AreEqual("1 JAN 2027", ShareCardText.Date(new DateTime(2027, 1, 1)));
            Assert.AreEqual("HARD", ShareCardText.Mode(d));
            d.difficulty = DuelConfig.DifficultyEasy;
            Assert.AreEqual("EASY", ShareCardText.Mode(d));
            d.pvp = true;
            Assert.AreEqual("PVP", ShareCardText.Mode(d));
        }

        [Test]
        public void MatchShareUsesTheFastestCleanShot()
        {
            ShareLog.Clear();
            Assert.IsNull(ShareLog.BestRound());
            var a = ShareCardData.Sample(ShareKind.Round); a.roundNumber = 1; a.youNs = 250000000L;
            var b = ShareCardData.Sample(ShareKind.Round); b.roundNumber = 2; b.youNs = 190000000L;
            var c = ShareCardData.Sample(ShareKind.Round); c.roundNumber = 3; c.youFoul = true; c.youNs = -1;
            ShareLog.Add(a, 7);
            ShareLog.Add(b, 7);
            ShareLog.Add(c, 7);
            Assert.AreSame(b, ShareLog.BestRound());
            Assert.AreSame(c, ShareLog.LastRound);
            var m = ShareLog.ForMatch(0);
            Assert.AreEqual(ShareKind.Match, m.kind);
            Assert.AreEqual(190000000L, m.youNs);
            Assert.AreEqual(ShareKind.Round, b.kind, "the log entry is copied, not changed");
            // a new match (new key) starts a new log
            ShareLog.Add(ShareCardData.Sample(ShareKind.Round), 8);
            Assert.AreEqual(1, ShareLog.Count);
            ShareLog.Clear();
        }
    }
}
