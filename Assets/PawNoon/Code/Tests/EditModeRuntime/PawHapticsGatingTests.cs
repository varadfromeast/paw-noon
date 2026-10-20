using NUnit.Framework;
using PawNoon.Core;
using PawNoon.Runtime;

namespace PawNoon.Tests
{
    /// <summary>PawHaptics gating in the editor backend (log only): toggle, Reduced Motion, patterns, DRAW lead.</summary>
    public class PawHapticsGatingTests
    {
        private bool enabled, reduced, log;
        private double latency;

        [SetUp]
        public void SetUp()
        {
            enabled = PawHaptics.Enabled;
            reduced = PawHaptics.ReducedMotion;
            log = PawHaptics.EditorLog;
            latency = PawHaptics.DrawCueOutputLatencySeconds;
            PawHaptics.EditorLog = false;
            PawHaptics.Enabled = true;
            PawHaptics.ReducedMotion = false;
            PawHaptics.CancelPattern();
        }

        [TearDown]
        public void TearDown()
        {
            PawHaptics.CancelPattern();
            PawHaptics.Enabled = enabled;
            PawHaptics.ReducedMotion = reduced;
            PawHaptics.EditorLog = log;
            PawHaptics.DrawCueOutputLatencySeconds = latency;
        }

        [Test]
        public void ToggleOffSilencesPatterns()
        {
            PawHaptics.Enabled = false;
            PawHaptics.Pattern(StampHaptics.Lightning);
            Assert.IsFalse(PawHaptics.PatternActive);
        }

        [Test]
        public void ToggleOffCancelsARunningPattern()
        {
            PawHaptics.Pattern(StampHaptics.Lightning);
            Assert.IsTrue(PawHaptics.PatternActive);
            PawHaptics.Enabled = false;
            Assert.IsFalse(PawHaptics.PatternActive);
            PawHaptics.Enabled = true;
            PawHaptics.Tick(1f);
            Assert.IsFalse(PawHaptics.PatternActive);
        }

        [Test]
        public void ReducedMotionKeepsOnlyTheFirstPulse()
        {
            PawHaptics.ReducedMotion = true;
            PawHaptics.Pattern(StampHaptics.Lightning);
            Assert.IsFalse(PawHaptics.PatternActive, "one pulse, fired at once");
            PawHaptics.Pattern(StampHaptics.Perfect);
            Assert.IsFalse(PawHaptics.PatternActive);
        }

        [Test]
        public void PatternAdvancesOnTheFrameTick()
        {
            PawHaptics.Pattern(StampHaptics.Perfect);    // medium, 85 ms, heavy
            Assert.IsTrue(PawHaptics.PatternActive);
            PawHaptics.Tick(0.05f);
            Assert.IsTrue(PawHaptics.PatternActive);
            PawHaptics.Tick(0.04f);
            Assert.IsFalse(PawHaptics.PatternActive);
        }

        [Test]
        public void DrawCueDelayTakesTheBackendLatencyOff()
        {
            double lead = 1.0 / 120.0;
            PawHaptics.DrawCueOutputLatencySeconds = 0;
            PawHaptics.ScheduleDrawCue(lead);
            Assert.AreEqual(lead, PawHaptics.LastDrawDelaySeconds, 1e-12);
            PawHaptics.DrawCueOutputLatencySeconds = 0.002;
            PawHaptics.ScheduleDrawCue(lead);
            Assert.AreEqual(lead - 0.002, PawHaptics.LastDrawDelaySeconds, 1e-12);
            PawHaptics.DrawCueOutputLatencySeconds = 0.020;
            PawHaptics.ScheduleDrawCue(lead);
            Assert.AreEqual(0, PawHaptics.LastDrawDelaySeconds);
        }

        [Test]
        public void NothingThrowsOnOddInput()
        {
            Assert.DoesNotThrow(() =>
            {
                PawHaptics.Prepare();
                PawHaptics.Impact(-5);
                PawHaptics.Impact(9);
                PawHaptics.Notify(7);
                PawHaptics.Notify(-1);
                PawHaptics.Transient(float.NaN, 2f);
                PawHaptics.Transient(0f, 0.5f);
                PawHaptics.Continuous(-1f, 1f, 1f);
                PawHaptics.Continuous(9f, 0.5f, 0.5f, 0.1);
                PawHaptics.Preset(999);
                PawHaptics.Preset(-1);
                PawHaptics.Preset(HapticPreset.Heartbeat, float.NaN);
                PawHaptics.Heartbeat(float.NaN, true);
                PawHaptics.Pattern(null);
                PawHaptics.Pattern(new int[0]);
                PawHaptics.ScheduleDrawCue(double.NaN);
                PawHaptics.Tick(-1f);
                for (int n = 3; n >= 0; n--) HapticMoments.CountdownTick(n);
                HapticMoments.Stamp(null);
                HapticMoments.MatchEnd(true, false);
                PawHaptics.StopAll();
                Assert.IsNotEmpty(PawHaptics.Backend);
            });
        }
    }
}
