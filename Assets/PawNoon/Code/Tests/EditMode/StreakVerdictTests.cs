using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>Win streak counter, exchange classification and the round card's verdict headline (all presentation).</summary>
    public class StreakVerdictTests
    {
        private static RoundRecord Round(SlotOutcome you, long youNs, SlotOutcome him, long himNs, bool tie = false)
        {
            var r = new RoundRecord();
            r.roundId = 1;
            r.slots[0].outcome = you;
            r.slots[0].reactionNs = youNs;
            r.slots[1].outcome = him;
            r.slots[1].reactionNs = himNs;
            r.isTie = tie;
            return r;
        }

        [Test]
        public void ClassifyExchanges()
        {
            Assert.AreEqual(ExchangeResult.Won, RoundVerdict.Classify(Round(SlotOutcome.Shot, 200, SlotOutcome.Shot, 260), 0));
            Assert.AreEqual(ExchangeResult.Lost, RoundVerdict.Classify(Round(SlotOutcome.Shot, 300, SlotOutcome.Shot, 260), 0));
            // the other side of the same round
            Assert.AreEqual(ExchangeResult.Won, RoundVerdict.Classify(Round(SlotOutcome.Shot, 300, SlotOutcome.Shot, 260), 1));
            Assert.AreEqual(ExchangeResult.Tie, RoundVerdict.Classify(Round(SlotOutcome.Shot, 250, SlotOutcome.Shot, 240, true), 0));
            Assert.AreEqual(ExchangeResult.Foul, RoundVerdict.Classify(Round(SlotOutcome.Foul, -1, SlotOutcome.Shot, 260), 0));
            Assert.AreEqual(ExchangeResult.Foul, RoundVerdict.Classify(Round(SlotOutcome.Anticipation, 40, SlotOutcome.Shot, 260), 0));
            Assert.AreEqual(ExchangeResult.NoShot, RoundVerdict.Classify(Round(SlotOutcome.NoShot, -1, SlotOutcome.Shot, 260), 0));
            // killed before your own shot
            Assert.AreEqual(ExchangeResult.Lost, RoundVerdict.Classify(Round(SlotOutcome.Stopped, -1, SlotOutcome.Shot, 260), 0));
            // you killed him first (his shot stopped), he fouled, he never shot: all clean wins
            Assert.AreEqual(ExchangeResult.Won, RoundVerdict.Classify(Round(SlotOutcome.Shot, 200, SlotOutcome.Stopped, -1), 0));
            Assert.AreEqual(ExchangeResult.Won, RoundVerdict.Classify(Round(SlotOutcome.Shot, 200, SlotOutcome.Foul, -1), 0));
            Assert.AreEqual(ExchangeResult.Won, RoundVerdict.Classify(Round(SlotOutcome.Shot, 900, SlotOutcome.NoShot, -1), 0));
            var v = Round(SlotOutcome.Shot, 200, SlotOutcome.Shot, 260);
            v.isVoid = true;
            Assert.AreEqual(ExchangeResult.None, RoundVerdict.Classify(v, 0));
            Assert.AreEqual(ExchangeResult.None, RoundVerdict.Classify(null, 0));
        }

        [Test]
        public void StreakCountsOutrightWinsOnly()
        {
            var s = new StreakCounter();
            Assert.AreEqual(1, s.Record(ExchangeResult.Won));
            Assert.AreEqual(2, s.Record(ExchangeResult.Won));
            Assert.AreEqual(2, s.Record(ExchangeResult.Tie), "a tie neither extends nor breaks the run");
            Assert.AreEqual(2, s.Record(ExchangeResult.None), "a void neither extends nor breaks the run");
            Assert.AreEqual(3, s.Record(ExchangeResult.Won));
            Assert.AreEqual(0, s.Record(ExchangeResult.Lost));
            Assert.AreEqual(3, s.Best);
            s.Record(ExchangeResult.Won);
            Assert.AreEqual(0, s.Record(ExchangeResult.Foul));
            s.Record(ExchangeResult.Won);
            Assert.AreEqual(0, s.Record(ExchangeResult.NoShot));
            s.Reset();
            Assert.AreEqual(0, s.Current);
            Assert.AreEqual(0, s.Best);
        }

        [Test]
        public void TagOnlyOnAWinAtTheMinimum()
        {
            Assert.IsFalse(StreakCounter.ShowsTag(ExchangeResult.Won, 1, 2));
            Assert.IsTrue(StreakCounter.ShowsTag(ExchangeResult.Won, 2, 2));
            Assert.IsTrue(StreakCounter.ShowsTag(ExchangeResult.Won, 5, 2));
            Assert.IsFalse(StreakCounter.ShowsTag(ExchangeResult.Tie, 3, 2), "a tie keeps the run but earns no tag");
            Assert.IsFalse(StreakCounter.ShowsTag(ExchangeResult.Won, 9, 0), "min 0 switches the tag off");
        }

        [Test]
        public void VerdictKinds()
        {
            Assert.AreEqual(VerdictKind.TooEarly, RoundVerdict.Kind(true, false, false, -1, 260));
            Assert.AreEqual(VerdictKind.NoShot, RoundVerdict.Kind(false, true, false, -1, 260));
            Assert.AreEqual(VerdictKind.DeadHeat, RoundVerdict.Kind(false, false, true, 250, 247));
            Assert.AreEqual(VerdictKind.Faster, RoundVerdict.Kind(false, false, false, 200, 212));
            Assert.AreEqual(VerdictKind.Beaten, RoundVerdict.Kind(false, false, false, 291, 260));
            Assert.AreEqual(VerdictKind.CleanShot, RoundVerdict.Kind(false, false, false, 200, -1));
            Assert.AreEqual(VerdictKind.Beaten, RoundVerdict.Kind(false, false, false, -1, 260));
            Assert.AreEqual(VerdictKind.None, RoundVerdict.Kind(false, false, false, -1, -1));
            Assert.AreEqual(VerdictTone.Good, RoundVerdict.Tone(VerdictKind.Faster));
            Assert.AreEqual(VerdictTone.Bad, RoundVerdict.Tone(VerdictKind.Beaten));
            Assert.AreEqual(VerdictTone.Bad, RoundVerdict.Tone(VerdictKind.NoShot));
            Assert.AreEqual(VerdictTone.Even, RoundVerdict.Tone(VerdictKind.DeadHeat));
        }

        [Test]
        public void VerdictTextForms()
        {
            const long ms = 1000000L;
            Assert.AreEqual("YOU WERE FASTER  +12 ms", RoundVerdict.Text(VerdictKind.Faster, RoundVerdict.Long, 12 * ms, true, "Pawtruth", "+-"));
            Assert.AreEqual("FASTER +12 ms", RoundVerdict.Text(VerdictKind.Faster, RoundVerdict.Short, -12 * ms, true, "Pawtruth", "+-"));
            Assert.AreEqual("PAWTRUTH BEAT YOU  -31 ms", RoundVerdict.Text(VerdictKind.Beaten, RoundVerdict.Long, 31 * ms, true, "Pawtruth", "+-"));
            Assert.AreEqual("BEATEN -31 ms", RoundVerdict.Text(VerdictKind.Beaten, RoundVerdict.Short, 31 * ms, true, "Pawtruth", "+-"));
            Assert.AreEqual("PAWTRUTH BEAT YOU", RoundVerdict.Text(VerdictKind.Beaten, RoundVerdict.Long, 0, false, "PAWTRUTH", "+-"));
            Assert.AreEqual("DEAD HEAT  ±3 ms", RoundVerdict.Text(VerdictKind.DeadHeat, RoundVerdict.Long, 3 * ms, true, "X", "±"));
            Assert.AreEqual("DEAD HEAT  +-3 ms", RoundVerdict.Text(VerdictKind.DeadHeat, RoundVerdict.Long, 3 * ms, true, "X", null));
            Assert.AreEqual("TOO EARLY!  wait for DRAW", RoundVerdict.Text(VerdictKind.TooEarly, RoundVerdict.Long, 0, false, "X", "+-"));
            Assert.AreEqual("NO SHOT  swipe up from the holster", RoundVerdict.Text(VerdictKind.NoShot, RoundVerdict.Long, 0, false, "X", "+-"));
            Assert.AreEqual("NO SHOT", RoundVerdict.Text(VerdictKind.NoShot, RoundVerdict.Minimal, 0, false, "X", "+-"));
            Assert.AreEqual("CLEAN SHOT!", RoundVerdict.Text(VerdictKind.CleanShot, RoundVerdict.Long, 0, false, "X", "+-"));
        }

        [Test]
        public void GapIsWholeMillisecondsRoundedHalfUp()
        {
            Assert.AreEqual("12", RoundVerdict.GapMs(12499999L));
            Assert.AreEqual("13", RoundVerdict.GapMs(12500000L));
            Assert.AreEqual("74", RoundVerdict.GapMs(-73723000L));
            Assert.AreEqual("1", RoundVerdict.GapMs(500000L));
            Assert.AreEqual("0.4", RoundVerdict.GapMs(400000L));
            Assert.AreEqual("0", RoundVerdict.GapMs(0L));
        }

        [Test]
        public void FitTakesTheLongestFormThatFits()
        {
            const long ms = 1000000L;
            System.Func<string, int> sixPx = s => s.Length * 6;
            Assert.AreEqual("PAWTRUTH BEAT YOU  -31 ms", RoundVerdict.Fit(VerdictKind.Beaten, 31 * ms, true, "PAWTRUTH", "+-", 200, sixPx));
            Assert.AreEqual("BEATEN -31 ms", RoundVerdict.Fit(VerdictKind.Beaten, 31 * ms, true, "MARSHAL MITTENS", "+-", 120, sixPx));
            Assert.AreEqual("-31 ms", RoundVerdict.Fit(VerdictKind.Beaten, 31 * ms, true, "PAWTRUTH", "+-", 60, sixPx));
            Assert.AreEqual("-31 ms", RoundVerdict.Fit(VerdictKind.Beaten, 31 * ms, true, "PAWTRUTH", "+-", 1, sixPx), "nothing fits: the minimal form");
        }
    }
}
