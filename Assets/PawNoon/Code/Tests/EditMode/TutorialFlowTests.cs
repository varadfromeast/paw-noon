using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>The first-run tutorial's step machine (Core/TutorialFlow.cs, Docs/TUTORIAL.md).</summary>
    public class TutorialFlowTests
    {
        private const double Dt = 1.0 / 60.0;

        private static TutorialFlow Make(int slowRetries = 1)
        {
            var t = new TutorialTuning();
            t.slowRetries = slowRetries;
            var f = new TutorialFlow(DuelConfig.Default(), t, 7);
            f.Begin();
            return f;
        }

        private static void Run(TutorialFlow f, double seconds, bool resting)
        {
            int n = (int)System.Math.Ceiling(seconds / Dt);
            for (int i = 0; i < n; i++) f.Tick(Dt, resting);
        }

        private static void ToHold(TutorialFlow f)
        {
            Run(f, f.Tuning.gripSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Hold, f.Step);
        }

        private static void ToDraw(TutorialFlow f)
        {
            ToHold(f);
            Run(f, f.HoldSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Draw, f.Step);
        }

        [Test]
        public void StartsAtGripAndCardStepOne()
        {
            var f = Make();
            Assert.AreEqual(TutorialStep.Grip, f.Step);
            Assert.AreEqual(1, f.CardStep);
            Assert.IsTrue(f.Active);
        }

        [Test]
        public void GripNeedsAContinuousRest()
        {
            var f = Make();
            Run(f, 0.4, true);
            Run(f, 0.1, false);   // lifted: the rest timer starts over
            Run(f, 0.4, true);
            Assert.AreEqual(TutorialStep.Grip, f.Step);
            Run(f, 0.25, true);
            Assert.AreEqual(TutorialStep.Hold, f.Step);
            Assert.AreEqual(2, f.CardStep);
            Assert.AreEqual(1, f.Attempts);
        }

        [Test]
        public void HoldLengthIsInTheTunedRange()
        {
            var f = Make();
            ToHold(f);
            Assert.That(f.HoldSeconds, Is.InRange(f.Tuning.holdMinSeconds, f.Tuning.holdMaxSeconds));
        }

        [Test]
        public void EarlySwipeFailsAndRestartsStepTwo()
        {
            var f = Make();
            ToHold(f);
            Run(f, 1.0, true);
            f.Early();
            Assert.AreEqual(TutorialStep.Fail, f.Step);
            Assert.AreEqual(TutorialFail.Swiped, f.LastFail);
            Assert.AreEqual(2, f.CardStep);
            Run(f, f.Tuning.failSeconds + 0.05, false);
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            Run(f, f.Tuning.gripSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Hold, f.Step);
            Assert.AreEqual(2, f.Attempts);
        }

        [Test]
        public void ShortLiftIsJitterLongLiftFails()
        {
            var f = Make();
            ToHold(f);
            Run(f, f.Tuning.liftGraceSeconds * 0.5, false);
            Run(f, 0.1, true);
            Assert.AreEqual(TutorialStep.Hold, f.Step);
            Run(f, f.Tuning.liftGraceSeconds + 0.05, false);
            Assert.AreEqual(TutorialStep.Fail, f.Step);
            Assert.AreEqual(TutorialFail.Lifted, f.LastFail);
        }

        [Test]
        public void HoldEndsInDrawAndAGoodShotIsReady()
        {
            var f = Make();
            ToDraw(f);
            Assert.AreEqual(3, f.CardStep);
            f.Shot(DuelConfig.MsToNs(312));
            Assert.AreEqual(TutorialStep.Verdict, f.Step);
            Assert.AreEqual(Grade.Great, f.Grade);
            Assert.IsFalse(f.Slow);
            Assert.AreEqual(DuelConfig.MsToNs(312), f.ReactionNs);
            Run(f, f.Tuning.verdictSeconds + 0.05, false);   // lifting after the draw is fine
            Assert.AreEqual(TutorialStep.Ready, f.Step);
            f.Confirm();
            Assert.AreEqual(TutorialStep.Done, f.Step);
            Assert.IsFalse(f.Skipped);
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void ShotBelowTheAnticipationFloorIsAGuess()
        {
            var f = Make();
            ToDraw(f);
            f.Shot(DuelConfig.MsToNs(60));
            Assert.AreEqual(TutorialStep.Fail, f.Step);
            Assert.AreEqual(TutorialFail.Swiped, f.LastFail);
            Assert.AreEqual(-1, f.ReactionNs);
        }

        [Test]
        public void SlowShotRetriesOnceThenLetsThrough()
        {
            var f = Make(1);
            ToDraw(f);
            f.Shot(DuelConfig.MsToNs(800));
            Assert.IsTrue(f.Slow);
            Assert.IsTrue(f.Retrying);
            Run(f, f.Tuning.verdictSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            Run(f, f.Tuning.gripSeconds + 0.05, true);
            Run(f, f.HoldSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Draw, f.Step);
            f.Shot(DuelConfig.MsToNs(800));
            Assert.IsFalse(f.Retrying);
            Run(f, f.Tuning.verdictSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Ready, f.Step);
        }

        [Test]
        public void NoShotFailsBackToStepTwo()
        {
            var f = Make();
            ToDraw(f);
            f.NoShot();
            Assert.AreEqual(TutorialStep.Fail, f.Step);
            Assert.AreEqual(TutorialFail.NoShot, f.LastFail);
            Run(f, f.Tuning.failSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
        }

        [Test]
        public void InputOutsideItsStepIsIgnored()
        {
            var f = Make();
            f.Early();
            f.Shot(DuelConfig.MsToNs(250));
            f.NoShot();
            f.Confirm();
            Assert.AreEqual(TutorialStep.Grip, f.Step);
            ToHold(f);
            f.Shot(DuelConfig.MsToNs(250));   // no cue yet
            Assert.AreEqual(TutorialStep.Hold, f.Step);
        }

        [Test]
        public void SkipFromAnyStepFinishes()
        {
            var f = Make();
            ToHold(f);
            f.Skip();
            Assert.AreEqual(TutorialStep.Done, f.Step);
            Assert.IsTrue(f.Skipped);
            int rev = f.Revision;
            f.Skip();
            Assert.AreEqual(rev, f.Revision, "skip after done is a no-op");
        }

        [Test]
        public void EveryTransitionBumpsTheRevision()
        {
            var f = Make();
            int r0 = f.Revision;
            ToHold(f);
            Assert.AreEqual(r0 + 1, f.Revision);
            f.Early();
            Assert.AreEqual(r0 + 2, f.Revision);
            f.Cancel();
            Assert.AreEqual(TutorialStep.Off, f.Step);
            Assert.IsFalse(f.Active);
        }

        [Test]
        public void SameSeedSameHolds()
        {
            var a = new TutorialFlow(DuelConfig.Default(), new TutorialTuning(), 42);
            var b = new TutorialFlow(DuelConfig.Default(), new TutorialTuning(), 42);
            a.Begin(); b.Begin();
            ToHold(a); ToHold(b);
            Assert.AreEqual(a.HoldSeconds, b.HoldSeconds);
        }

        [Test]
        public void TuningIsSanitized()
        {
            var t = new TutorialTuning();
            t.holdMinSeconds = 0.2;
            t.holdMaxSeconds = 0.1;
            t.slowRetries = -3;
            t.Sanitize();
            Assert.GreaterOrEqual(t.holdMinSeconds, 1.0);
            Assert.GreaterOrEqual(t.holdMaxSeconds, t.holdMinSeconds);
            Assert.AreEqual(0, t.slowRetries);
        }

        // ---- app backgrounded (review 2026-10-23): DuelDirector.VoidForLifecycle -> TutorialDirector.Interrupt

        [Test]
        public void InterruptDuringHoldGoesBackToRegripWithoutAFail()
        {
            var f = Make();
            ToHold(f);
            int rev = f.Revision;
            Assert.IsTrue(f.Interrupt());
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            Assert.AreEqual(TutorialFail.None, f.LastFail);
            Assert.Greater(f.Revision, rev, "the presenter sees the change");
            Assert.AreEqual(2, f.CardStep);
            // a resumed lift is not a fail: Regrip just waits for the thumb
            Run(f, 1.0, false);
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            Run(f, f.Tuning.gripSeconds + 0.05, true);
            Assert.AreEqual(TutorialStep.Hold, f.Step);
            Assert.AreEqual(2, f.Attempts);
        }

        [Test]
        public void InterruptDuringDrawGoesBackToRegripAndIgnoresALateNoShot()
        {
            var f = Make();
            ToDraw(f);
            Assert.IsTrue(f.Interrupt());
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            f.NoShot();   // the client's window closing on resume must not turn into a NO SHOT card
            f.Shot(DuelConfig.MsToNs(250));
            Assert.AreEqual(TutorialStep.Regrip, f.Step);
            Assert.AreEqual(TutorialFail.None, f.LastFail);
        }

        [Test]
        public void InterruptElsewhereChangesNothing()
        {
            var f = Make();
            int rev = f.Revision;
            Assert.IsFalse(f.Interrupt(), "Grip");
            Assert.AreEqual(TutorialStep.Grip, f.Step);
            Assert.AreEqual(rev, f.Revision);
            ToHold(f);
            f.Early();
            Assert.AreEqual(TutorialStep.Fail, f.Step);
            Assert.IsFalse(f.Interrupt(), "Fail times out by itself");
            Assert.AreEqual(TutorialFail.Swiped, f.LastFail);
            f.Skip();
            Assert.IsFalse(f.Interrupt(), "Done");
            Assert.AreEqual(TutorialStep.Done, f.Step);
        }
    }
}
