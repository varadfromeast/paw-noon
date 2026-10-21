using System.Collections.Generic;
using System.IO;
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
        public void BuiltInLinesHaveTwoToThreePerContext()
        {
            var c = DuelConfig.FromJson("{\"banterChance\": 0.3}");
            Assert.AreEqual(0.3, c.banterChance, 1e-12);
            var parsed = BanterLines.FromJson(PnJson.ParseObject("{\"botLost\": [\"Huh.\", \"Fine.\"], \"botWon\": []}"));
            CollectionAssert.AreEqual(new[] { "Huh.", "Fine." }, parsed.botLost);
            Assert.AreEqual(new BanterLines().botWon.Length, parsed.botWon.Length); // empty list keeps defaults
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

    /// <summary>SPEC_V2 §8.2: per-character banter in Resources/Data/characters.json, built-in fallback per context.</summary>
    public class CharacterBanterTests
    {
        private const string CharactersPath = "Assets/PawNoon/Resources/Data/characters.json";
        private const string ConfigPath = "Assets/PawNoon/Resources/Data/duel_config.json";

        private static string ReadData(string relPath)
        {
            string p = Path.Combine(Directory.GetCurrentDirectory(), relPath);
            Assert.IsTrue(File.Exists(p), "missing " + p);
            return File.ReadAllText(p);
        }

        private static string[][] Pools(BanterLines l)
        {
            return new[] { l.matchStart, l.lastOne, l.lightning, l.playerEarly, l.botWon, l.botLost, l.matchWin };
        }

        [Test]
        public void CharactersFileLoadsBothCats()
        {
            var cfg = DuelConfig.FromJson(ReadData(ConfigPath));
            var r = CharacterRoster.Load(ReadData(CharactersPath), cfg);
            Assert.IsTrue(r.FromFile, "characters.json must parse");
            var w = r.Get(CharacterRoster.Whiskers);
            var p = r.Get(CharacterRoster.Pawtruth);
            Assert.AreEqual("WHISKERS", w.name);
            Assert.AreEqual("PAWTRUTH", p.name);
            Assert.AreEqual("sfx_meow_whiskers", w.meow);
            Assert.AreEqual("sfx_meow_pawtruth", p.meow);
            Assert.AreEqual("PAWTRUTH", cfg.botName, "the bot persona is Pawtruth (formerly Blackpaw)");

            int whiskersLines = 0;
            var seen = new HashSet<string>();
            foreach (var pool in Pools(p.lines)) foreach (var line in pool) seen.Add(line);
            foreach (var pool in Pools(w.lines))
            {
                Assert.GreaterOrEqual(pool.Length, 2);
                Assert.LessOrEqual(pool.Length, 3);
                foreach (var line in pool)
                {
                    whiskersLines++;
                    Assert.LessOrEqual(line.Length, 30, "one-line bubble: " + line);
                    Assert.IsFalse(seen.Contains(line), "Whiskers must not reuse Pawtruth's line: " + line);
                }
            }
            Assert.GreaterOrEqual(whiskersLines, 20);
            // the file's Whiskers lines are the built-in defaults too (the game says the same without the file)
            var d = BanterLines.WhiskersDefaults();
            var a = Pools(w.lines);
            var b = Pools(d);
            for (int i = 0; i < a.Length; i++) CollectionAssert.AreEqual(b[i], a[i]);
        }

        [Test]
        public void MissingFileFallsBackToConfigAndBuiltIns()
        {
            // the old duel_config.json "blackpawLines" key is ignored now (cleanup 21 Oct): built-ins only
            var cfg = DuelConfig.FromJson("{\"botName\": \"OLDNAME\", \"blackpawLines\": {\"botLost\": [\"Legacy.\"]}}");
            foreach (var json in new[] { null, "", "{ not json" })
            {
                var r = CharacterRoster.Load(json, cfg);
                Assert.IsFalse(r.FromFile);
                var p = r.Get(CharacterRoster.Pawtruth);
                Assert.AreEqual("OLDNAME", p.name);
                CollectionAssert.AreEqual(new BanterLines().botLost, p.lines.botLost);            // legacy key ignored
                CollectionAssert.AreEqual(new BanterLines().matchStart, p.lines.matchStart);      // built-in
                var w = r.Get(CharacterRoster.Whiskers);
                Assert.AreEqual("WHISKERS", w.name);
                CollectionAssert.AreEqual(BanterLines.WhiskersDefaults().botWon, w.lines.botWon);
            }
            // no config at all
            var bare = CharacterRoster.Load(null, null);
            Assert.AreEqual("PAWTRUTH", bare.Get(CharacterRoster.Pawtruth).name);
            Assert.AreEqual("sfx_meow_pawtruth", bare.Get(CharacterRoster.Pawtruth).meow);
        }

        [Test]
        public void PartialEntriesFallBackPerContext()
        {
            var cfg = DuelConfig.FromJson("{}");
            var r = CharacterRoster.Load(
                "{\"pawtruth\": {\"lines\": {\"botWon\": [\"New win.\", \"Again.\"], \"lastOne\": []}}," +
                " \"whiskers\": {\"name\": \"SNOW\", \"lines\": {\"matchWin\": [\"Bye.\"]}}}", cfg);
            Assert.IsTrue(r.FromFile);
            var p = r.Get(CharacterRoster.Pawtruth);
            Assert.AreEqual(cfg.botName, p.name);                                         // no name in the entry
            CollectionAssert.AreEqual(new[] { "New win.", "Again." }, p.lines.botWon);   // characters.json wins
            CollectionAssert.AreEqual(new BanterLines().botLost, p.lines.botLost);       // missing -> built-in
            CollectionAssert.AreEqual(new BanterLines().lastOne, p.lines.lastOne);       // empty list -> built-in
            var w = r.Get(CharacterRoster.Whiskers);
            Assert.AreEqual("SNOW", w.name);
            CollectionAssert.AreEqual(new[] { "Bye." }, w.lines.matchWin);
            CollectionAssert.AreEqual(BanterLines.WhiskersDefaults().lightning, w.lines.lightning);
        }

        [Test]
        public void IdsNormaliseAndLegacyBlackpawIsPawtruth()
        {
            var r = CharacterRoster.Load(null, null);
            Assert.AreSame(r.Get("pawtruth"), r.Get("blackpaw"));
            Assert.AreSame(r.Get("pawtruth"), r.Get(" PawTruth "));
            Assert.AreSame(r.Get("pawtruth"), r.Get("nobody"));                          // unknown -> Pawtruth, never null
            Assert.IsTrue(r.Has("whiskers"));
            Assert.IsFalse(r.Has("nobody"));
            Assert.AreEqual(CharacterRoster.Pawtruth, CharacterRoster.OpponentOf(CharacterRoster.Whiskers));
            Assert.AreEqual(CharacterRoster.Whiskers, CharacterRoster.OpponentOf("blackpaw"));
            CollectionAssert.AreEqual(new[] { "pawtruth", "whiskers" }, r.Ids);
        }

        [Test]
        public void PickerWorksWithEitherCatsLines()
        {
            var r = CharacterRoster.Load(ReadData(CharactersPath), DuelConfig.FromJson(ReadData(ConfigPath)));
            var picker = new BanterPicker(5);
            foreach (var id in r.Ids)
            {
                var lines = r.Get(id).lines;
                var s = picker.Pick(lines, BanterContext.MatchStart, 0.0);
                Assert.IsNotNull(s);
                CollectionAssert.Contains(lines.matchStart, s);
            }
        }

        [Test]
        public void IntroFlagParsesAndDefaultsOn()
        {
            Assert.IsTrue(new DuelConfig().introEnabled);
            Assert.IsTrue(DuelConfig.FromJson(ReadData(ConfigPath)).introEnabled);
            Assert.IsFalse(DuelConfig.FromJson("{\"introEnabled\": false}").introEnabled);
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
