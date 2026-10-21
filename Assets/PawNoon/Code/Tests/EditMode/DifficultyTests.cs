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
            CollectionAssert.AreEqual(new double[] { 200, 250, 300, 350, 450, 650, 1050, 1500 }, e.damageMs);
            CollectionAssert.AreEqual(new double[] { 40, 36, 30, 24, 16, 8, 4, 2 }, e.damageValue);
            Assert.AreEqual(250, e.gradeLightningMs, 1e-12);
            Assert.AreEqual(300, e.gradePerfectMs, 1e-12);
            Assert.AreEqual(370, e.gradeGreatMs, 1e-12);
            Assert.AreEqual(470, e.gradeGoodMs, 1e-12);
            Assert.AreEqual(750, e.gradeSlowMs, 1e-12);
            Assert.AreEqual(360, e.botMeanStartMs, 1e-12);
            Assert.AreEqual(290, e.botMeanEndMs, 1e-12);
            Assert.AreEqual(70, e.botSdStartMs, 1e-12);
            Assert.AreEqual(50, e.botSdEndMs, 1e-12);
            Assert.AreEqual(0.15, e.botFeintChance, 1e-12);
            Assert.AreEqual(0.40, e.botFeintMaxChance, 1e-12);
            Assert.AreEqual(0.45, e.botTellChance, 1e-12);
            Assert.AreEqual(2.0, e.noShotTimeoutSeconds, 1e-12);
            Assert.AreEqual(3.0, e.holdMaxSeconds, 1e-12);
        }

        [Test]
        public void EasyDamageAt300MatchesHardAround250()
        {
            var hard = Hard();
            var easy = Easy();
            double e300 = DamageCurve.Evaluate(easy, 300);
            // ~+50 ms slack: an Easy 300 ms draw hits like a Hard 250 ms one
            Assert.AreEqual(DamageCurve.Evaluate(hard, 250), e300, 1e-9);
            Assert.AreEqual(Grading.FromMs(hard, 250), Grading.FromMs(easy, 300));
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

        /// <summary>
        /// CopyFrom must carry EVERY field, not only the ones Easy and Hard happen to disagree on (review
        /// 2026-10-20): every public field of the source is set to a value no loader produces, then copied.
        /// </summary>
        [Test]
        public void CopyFromCopiesEveryField()
        {
            var src = new DuelConfig();
            int k = 0;
            foreach (var f in typeof(DuelConfig).GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                k++;
                if (f.FieldType == typeof(double)) f.SetValue(src, 1000.0 + k + 0.25);
                else if (f.FieldType == typeof(int)) f.SetValue(src, 1000 + k);
                else if (f.FieldType == typeof(string)) f.SetValue(src, "x" + k);
                else if (f.FieldType == typeof(bool)) f.SetValue(src, !(bool)f.GetValue(src));
                else if (f.FieldType == typeof(double[])) f.SetValue(src, new double[] { k, k + 1 });
                else Assert.Fail("DuelConfig." + f.Name + " has a type this test does not know (" + f.FieldType.Name + "): extend it");
            }
            var dst = new DuelConfig();
            dst.CopyFrom(src);
            Assert.IsEmpty(ChangedFields(dst, src));
            Assert.AreEqual(src.Difficulty, dst.Difficulty);
            Assert.AreNotSame(src.damageValue, dst.damageValue, "arrays are copied, not shared");
        }

        /// <summary>
        /// PawGame loads the overlay with Resources.Load("Data/duel_config.easy"): the dotted name must resolve
        /// to the TextAsset (otherwise Easy silently plays Hard values under an EASY tag).
        /// </summary>
        [Test]
        public void EasyOverlayResolvesThroughResources()
        {
            var ta = UnityEngine.Resources.Load<UnityEngine.TextAsset>("Data/duel_config.easy");
            Assert.IsNotNull(ta, "Resources.Load(\"Data/duel_config.easy\") returned null");
            CollectionAssert.AreEquivalent(Whitelist, PnJson.ParseObject(ta.text).Keys.ToArray());
            var hard = UnityEngine.Resources.Load<UnityEngine.TextAsset>("Data/duel_config");
            Assert.IsNotNull(hard);
            Assert.AreNotEqual(ta.text, hard.text, "the two names must not resolve to the same asset");
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
