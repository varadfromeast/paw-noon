using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>SPEC_V2 §1: Easy = duel_config.easy.json merged over the Hard duel_config.json.</summary>
    public class DifficultyTests
    {
        private const string HardPath = "Assets/PawNoon/Resources/Data/duel_config.json";
        private const string EasyPath = "Assets/PawNoon/Resources/Data/duel_config.easy.json";

        /// <summary>The only keys Easy may change (SPEC_V2 §1 table).</summary>
        private static readonly string[] Whitelist =
        {
            "damageMs",
            "gradeLightningMs", "gradePerfectMs", "gradeGreatMs", "gradeGoodMs", "gradeSlowMs",
            "botMeanStartMs", "botMeanEndMs",
            "botSdStartMs", "botSdEndMs",
            "botFeintChance", "botFeintMaxChance",
            "botTellChance",
            "noShotTimeoutSeconds",
            "holdMaxSeconds",
        };

        private static string ReadData(string relPath)
        {
            // EditMode tests run with the project root as the working directory
            string p = Path.Combine(Directory.GetCurrentDirectory(), relPath);
            Assert.IsTrue(File.Exists(p), "missing " + p);
            return File.ReadAllText(p);
        }

        private static DuelConfig Hard() { return DuelConfig.Load(ReadData(HardPath)); }
        private static DuelConfig Easy() { return DuelConfig.Load(ReadData(HardPath), ReadData(EasyPath)); }

        private static bool SameValue(object a, object b)
        {
            if (a == null || b == null) return a == b;
            var da = a as double[];
            if (da != null) return da.SequenceEqual((double[])b);
            if (a is BanterLines)
            {
                foreach (var f in typeof(BanterLines).GetFields(BindingFlags.Instance | BindingFlags.Public))
                {
                    var sa = (string[])f.GetValue(a);
                    var sb = (string[])f.GetValue(b);
                    if (!sa.SequenceEqual(sb)) return false;
                }
                return true;
            }
            return a.Equals(b);
        }

        /// <summary>Names of public config fields whose values differ (the Difficulty label itself excluded).</summary>
        private static List<string> ChangedFields(DuelConfig a, DuelConfig b)
        {
            var r = new List<string>();
            foreach (var f in typeof(DuelConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (f.Name == "Difficulty") continue;
                if (!SameValue(f.GetValue(a), f.GetValue(b))) r.Add(f.Name);
            }
            return r;
        }

        [Test]
        public void EasyFileContainsOnlyWhitelistedKeys()
        {
            var overlay = PnJson.ParseObject(ReadData(EasyPath));
            Assert.IsNotNull(overlay, "duel_config.easy.json must parse");
            CollectionAssert.AreEquivalent(Whitelist, overlay.Keys.ToArray());
        }

        [Test]
        public void OverlayChangesExactlyTheWhitelistedKeys()
        {
            var hard = Hard();
            var easy = Easy();
            Assert.AreEqual(DuelConfig.DifficultyHard, hard.Difficulty);
            Assert.AreEqual(DuelConfig.DifficultyEasy, easy.Difficulty);
            Assert.IsTrue(easy.IsEasy);
            Assert.IsFalse(hard.IsEasy);
            CollectionAssert.AreEquivalent(Whitelist, ChangedFields(hard, easy).ToArray());
        }

        [Test]
        public void EasyValuesMatchSpec()
        {
            var e = Easy();
            CollectionAssert.AreEqual(new double[] { 230, 290, 350, 420, 540, 760, 1100, 1500 }, e.damageMs);
            CollectionAssert.AreEqual(new double[] { 40, 36, 30, 24, 16, 8, 4, 2 }, e.damageValue);
            Assert.AreEqual(280, e.gradeLightningMs, 1e-12);
            Assert.AreEqual(340, e.gradePerfectMs, 1e-12);
            Assert.AreEqual(420, e.gradeGreatMs, 1e-12);
            Assert.AreEqual(540, e.gradeGoodMs, 1e-12);
            Assert.AreEqual(800, e.gradeSlowMs, 1e-12);
            Assert.AreEqual(420, e.botMeanStartMs, 1e-12);
            Assert.AreEqual(340, e.botMeanEndMs, 1e-12);
            Assert.AreEqual(90, e.botSdStartMs, 1e-12);
            Assert.AreEqual(70, e.botSdEndMs, 1e-12);
            Assert.AreEqual(0.15, e.botFeintChance, 1e-12);
            Assert.AreEqual(0.40, e.botFeintMaxChance, 1e-12);
            Assert.AreEqual(0.45, e.botTellChance, 1e-12);
            Assert.AreEqual(2.0, e.noShotTimeoutSeconds, 1e-12);
            Assert.AreEqual(3.0, e.holdMaxSeconds, 1e-12);
        }

        [Test]
        public void EasyDamageAt300MatchesHardAround220()
        {
            var hard = Hard();
            var easy = Easy();
            double e300 = DamageCurve.Evaluate(easy, 300);
            // ~+80 ms slack: an Easy 300 ms draw hits like a Hard ~220 ms one (same 200..250 band)
            Assert.AreEqual(DamageCurve.Evaluate(hard, 220), e300, 1.5);
            Assert.LessOrEqual(e300, DamageCurve.Evaluate(hard, 200) + 1e-9);
            Assert.GreaterOrEqual(e300, DamageCurve.Evaluate(hard, 250) - 1e-9);
            Assert.AreEqual(Grading.FromMs(hard, 220), Grading.FromMs(easy, 300));
            // every Easy anchor deals what the matching Hard anchor deals
            for (int i = 0; i < hard.damageMs.Length; i++)
                Assert.AreEqual(DamageCurve.Evaluate(hard, hard.damageMs[i]), DamageCurve.Evaluate(easy, easy.damageMs[i]), 1e-9, "anchor " + i);
            // Easy is never harsher than Hard
            for (double ms = 100; ms <= 1600; ms += 5)
                Assert.GreaterOrEqual(DamageCurve.Evaluate(easy, ms), DamageCurve.Evaluate(hard, ms) - 1e-9, "at " + ms);
        }

        [Test]
        public void AnticipationTieAndCountdownUnchanged()
        {
            var hard = Hard();
            var easy = Easy();
            Assert.AreEqual(100.0, easy.anticipationMs, 1e-12);
            Assert.AreEqual(15.0, easy.tieWindowMs, 1e-12);
            Assert.AreEqual(hard.anticipationMs, easy.anticipationMs, 1e-12);
            Assert.AreEqual(hard.tieWindowMs, easy.tieWindowMs, 1e-12);
            Assert.AreEqual(hard.AnticipationNs, easy.AnticipationNs);
            Assert.AreEqual(hard.TieWindowNs, easy.TieWindowNs);
            Assert.AreEqual(hard.countdownBeats, easy.countdownBeats);
            Assert.AreEqual(hard.beatSeconds, easy.beatSeconds, 1e-12);
            Assert.AreEqual(hard.holdMinSeconds, easy.holdMinSeconds, 1e-12);
        }

        [Test]
        public void HardLoadEqualsFromJson()
        {
            string json = ReadData(HardPath);
            Assert.IsEmpty(ChangedFields(DuelConfig.FromJson(json), DuelConfig.Load(json)));
            Assert.IsEmpty(ChangedFields(DuelConfig.FromJson(json), DuelConfig.Load(json, null)));
            Assert.IsEmpty(ChangedFields(DuelConfig.FromJson(json), DuelConfig.Load(json, "{ not json")));
            Assert.AreEqual(DuelConfig.DifficultyHard, DuelConfig.Load(json, null).Difficulty);
            Assert.AreEqual(DuelConfig.DifficultyHard, DuelConfig.FromJson(json).Difficulty);
        }

        [Test]
        public void MergeOverridesShallowMergesObjectsAndReplacesArrays()
        {
            var b = PnJson.ParseObject("{\"a\":1, \"arr\":[1,2,3], \"o\":{\"x\":1, \"y\":2, \"deep\":{\"k\":1}}, \"keep\":\"s\"}");
            var o = PnJson.ParseObject("{\"a\":5, \"arr\":[9], \"o\":{\"y\":7, \"deep\":{\"z\":3}}, \"new\":true}");
            var m = PnJson.Merge(b, o);
            Assert.AreEqual(5.0, PnJson.Num(m, "a", 0), 1e-12);
            CollectionAssert.AreEqual(new double[] { 9 }, PnJson.NumArray(m, "arr"));
            var mo = PnJson.Obj(m, "o");
            Assert.AreEqual(1.0, PnJson.Num(mo, "x", 0), 1e-12);
            Assert.AreEqual(7.0, PnJson.Num(mo, "y", 0), 1e-12);
            // one level only: the nested "deep" object is replaced, not merged
            var deep = PnJson.Obj(mo, "deep");
            Assert.IsFalse(PnJson.Has(deep, "k"));
            Assert.AreEqual(3.0, PnJson.Num(deep, "z", 0), 1e-12);
            Assert.AreEqual("s", PnJson.Str(m, "keep", null));
            Assert.IsTrue(PnJson.Bool(m, "new", false));
            // inputs untouched
            Assert.AreEqual(1.0, PnJson.Num(b, "a", 0), 1e-12);
            Assert.AreEqual(2.0, PnJson.Num(PnJson.Obj(b, "o"), "y", 0), 1e-12);
            Assert.AreEqual(3, PnJson.NumArray(b, "arr").Length);
        }

        [Test]
        public void OverlayMergesNestedBanterShallowly()
        {
            var c = DuelConfig.Load("{\"blackpawLines\": {\"botLost\": [\"A.\"], \"botWon\": [\"B.\"]}}",
                                    "{\"blackpawLines\": {\"botLost\": [\"C.\"]}}", "easy");
            CollectionAssert.AreEqual(new[] { "C." }, c.blackpawLines.botLost);
            CollectionAssert.AreEqual(new[] { "B." }, c.blackpawLines.botWon);
            Assert.AreEqual(DuelConfig.DifficultyEasy, c.Difficulty);
        }

        [Test]
        public void CopyFromSwapsRulesInPlace()
        {
            var live = Hard();
            var easy = Easy();
            live.CopyFrom(easy);
            Assert.IsEmpty(ChangedFields(live, easy));
            Assert.AreEqual(DuelConfig.DifficultyEasy, live.Difficulty);
            Assert.AreNotSame(easy.damageMs, live.damageMs, "arrays are copied, not shared");
            live.CopyFrom(Hard());
            Assert.IsEmpty(ChangedFields(live, Hard()));
            Assert.AreEqual(DuelConfig.DifficultyHard, live.Difficulty);
        }

        [Test]
        public void DifficultyNamesNormalize()
        {
            Assert.AreEqual("easy", DuelConfig.NormalizeDifficulty(" EASY "));
            Assert.AreEqual("hard", DuelConfig.NormalizeDifficulty("hard"));
            Assert.AreEqual("hard", DuelConfig.NormalizeDifficulty(null));
            Assert.AreEqual("hard", DuelConfig.NormalizeDifficulty("nightmare"));
        }
    }
}
