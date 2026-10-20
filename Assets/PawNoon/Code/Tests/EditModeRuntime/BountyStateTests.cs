using System;
using NUnit.Framework;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>Daily bounty + streak state machine (SPEC_V2 §7, feel wave 2): local days, offline queue, server answers.</summary>
    public class BountyStateTests
    {
        private static readonly DateTime Mon = new DateTime(2026, 10, 19, 21, 0, 0, DateTimeKind.Local);
        private static DateTime Day(int offset, int hour = 12) { return Mon.Date.AddDays(offset).AddHours(hour); }

        private static BountyState Fresh() { return BountyState.Parse(null, 3); }

        [Test]
        public void ThreeWinsMakeTheClaimDue()
        {
            var b = Fresh();
            Assert.AreEqual("BOUNTY: WIN 3 PVP DUELS", b.Line(Mon, true, 20));
            Assert.IsFalse(b.RecordPvpWin(Mon));
            Assert.AreEqual("BOUNTY: 1/3 WINS TODAY", b.Line(Mon, true, 20));
            Assert.IsFalse(b.RecordPvpWin(Mon));
            Assert.AreEqual("BOUNTY: 2/3 WINS TODAY", b.Line(Mon, true, 20));
            Assert.IsTrue(b.RecordPvpWin(Mon), "third win: claim");
            Assert.IsTrue(b.ShouldClaim(Mon));
            Assert.AreEqual("BOUNTY: 3/3 CLAIMING...", b.Line(Mon, true, 20));
            Assert.AreEqual("BOUNTY: 3/3 WILL SYNC", b.Line(Mon, false, 20));
        }

        [Test]
        public void ClaimedPaysOnceAndSetsTheStreak()
        {
            var b = Fresh();
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Mon);
            Assert.IsTrue(b.Apply(Mon, BountyState.ClaimOutcome.Claimed, 3, 1, 20), "celebrate");
            Assert.IsFalse(b.ShouldClaim(Mon));
            Assert.IsTrue(b.ClaimedToday(Mon));
            Assert.AreEqual(1, b.DisplayStreak(Mon));
            Assert.AreEqual(20, b.Coins);
            Assert.AreEqual("BOUNTY PAID  +20", b.Line(Mon, true, 20));
            // a fourth win the same day owes nothing
            Assert.IsFalse(b.RecordPvpWin(Mon));
            Assert.AreEqual(3, b.DisplayWins(Mon), "capped at the requirement for display");
            // already claimed (e.g. the other device claimed first): no second celebration
            Assert.IsFalse(b.Apply(Mon, BountyState.ClaimOutcome.AlreadyClaimed, 4, 1, 20));
        }

        [Test]
        public void DayRolloverIsLocalMidnight()
        {
            var b = Fresh();
            var lateMon = Mon.Date.AddHours(23).AddMinutes(59);
            var earlyTue = Mon.Date.AddDays(1).AddMinutes(1);
            b.RecordPvpWin(lateMon);
            b.RecordPvpWin(lateMon);
            Assert.AreEqual(2, b.DisplayWins(lateMon));
            Assert.AreEqual(0, b.DisplayWins(earlyTue), "a new local day starts at 0");
            Assert.IsFalse(b.RecordPvpWin(earlyTue));
            Assert.AreEqual(1, b.DisplayWins(earlyTue));
            Assert.AreEqual(BountyState.DayOf(lateMon) + 1, BountyState.DayOf(earlyTue));
        }

        [Test]
        public void StreakSurvivesOneDayAndBreaksAfterAMissedDay()
        {
            var b = Fresh();
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Day(0));
            b.Apply(Day(0), BountyState.ClaimOutcome.Claimed, 3, 4, 80);
            Assert.AreEqual(4, b.DisplayStreak(Day(0)));
            Assert.AreEqual(4, b.DisplayStreak(Day(1, 23)), "still alive the next day (claim today to extend)");
            Assert.AreEqual(0, b.DisplayStreak(Day(2, 0)), "a whole day missed");
            // next day's claim, server offline in simulation: +1 from yesterday
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Day(1));
            Assert.IsTrue(b.Apply(Day(1), BountyState.ClaimOutcome.Claimed, 3, -1, -1));
            Assert.AreEqual(5, b.DisplayStreak(Day(1)));
            Assert.AreEqual(80, b.Coins, "unknown coins keep the last known balance");
        }

        [Test]
        public void OfflineClaimWaitsAndSurvivesARestart()
        {
            var b = Fresh();
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Mon);
            Assert.IsFalse(b.Apply(Mon, BountyState.ClaimOutcome.Offline, -1, -1, -1));
            Assert.IsTrue(b.ShouldClaim(Mon), "still owed");
            var restored = BountyState.Parse(b.Serialize(), 3);
            Assert.IsTrue(restored.ShouldClaim(Mon), "persisted across launches");
            Assert.AreEqual(3, restored.DisplayWins(Mon));
            // the server has not seen the queued submissions yet: keep waiting
            Assert.IsFalse(restored.Apply(Mon, BountyState.ClaimOutcome.NotYet, 1, 0, 0, false));
            Assert.IsTrue(restored.ShouldClaim(Mon));
            Assert.AreEqual(3, restored.DisplayWins(Mon));
            // the queue drained and the server pays
            Assert.IsTrue(restored.Apply(Mon, BountyState.ClaimOutcome.Claimed, 3, 2, 40, true));
            Assert.AreEqual(2, restored.DisplayStreak(Mon));
        }

        [Test]
        public void NotYetWithNothingQueuedTrustsTheServer()
        {
            var b = Fresh();
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Mon);
            // one local win was not confirmed (server verdict differed): stop retrying, show the server's count
            Assert.IsFalse(b.Apply(Mon, BountyState.ClaimOutcome.NotYet, 2, 0, 0, true));
            Assert.IsFalse(b.ShouldClaim(Mon));
            Assert.AreEqual(2, b.DisplayWins(Mon));
            Assert.IsTrue(b.RecordPvpWin(Mon), "the next win makes it due again");
        }

        [Test]
        public void AClaimOwedYesterdayIsDroppedAtMidnight()
        {
            var b = Fresh();
            for (int i = 0; i < 3; i++) b.RecordPvpWin(Day(0, 23));
            Assert.IsTrue(b.ShouldClaim(Day(0, 23)));
            Assert.IsFalse(b.ShouldClaim(Day(1, 0)), "the server pays per day; yesterday's bounty is gone");
            Assert.AreEqual(0, b.DisplayWins(Day(1, 0)));
        }

        [Test]
        public void ParseIsForgiving()
        {
            Assert.AreEqual(0, BountyState.Parse("garbage", 3).WinsToday);
            Assert.AreEqual(0, BountyState.Parse("v9|1|2|1|0|0|0", 3).WinsToday);
            var b = BountyState.Parse("v1|740000|2|0|739999|5|120", 3);
            Assert.AreEqual(740000, b.Day);
            Assert.AreEqual(2, b.WinsToday);
            Assert.AreEqual(5, b.Streak);
            Assert.AreEqual(120, b.Coins);
            Assert.AreEqual("v1|740000|2|0|739999|5|120", b.Serialize());
        }
    }
}
