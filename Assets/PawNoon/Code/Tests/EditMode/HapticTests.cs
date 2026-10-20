using System.Collections.Generic;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>SPEC_V2 §8.6: the stamp haptic language and the frame-ticked pattern scheduler.</summary>
    public class HapticTests
    {
        /// <summary>Runs a sequencer at a fixed frame length; returns (time ms, pulse) for every pulse fired.</summary>
        private static List<KeyValuePair<double, int>> Run(int[] pattern, double frameMs, double totalMs, bool firstOnly = false)
        {
            var s = new HapticSequencer();
            var fired = new List<KeyValuePair<double, int>>();
            s.Start(pattern, firstOnly);
            int c;
            while ((c = s.Poll()) >= 0) fired.Add(new KeyValuePair<double, int>(0, c));
            for (double t = frameMs; t <= totalMs + 1e-9; t += frameMs)
            {
                s.Advance(frameMs);
                while ((c = s.Poll()) >= 0) fired.Add(new KeyValuePair<double, int>(t, c));
            }
            Assert.IsFalse(s.Active, "pattern should be finished");
            return fired;
        }

        [Test]
        public void StampMapping()
        {
            Assert.AreEqual(3, StampHaptics.Pulses(StampHaptics.For(false, false, Grade.Lightning)));
            Assert.AreEqual(2, StampHaptics.Pulses(StampHaptics.For(false, false, Grade.Perfect)));
            CollectionAssert.AreEqual(new[] { HapticStyle.Medium }, StampHaptics.For(false, false, Grade.Great));
            CollectionAssert.AreEqual(new[] { HapticStyle.Light }, StampHaptics.For(false, false, Grade.Good));
            CollectionAssert.AreEqual(new[] { HapticStyle.Light }, StampHaptics.For(false, false, Grade.Slow));
            CollectionAssert.AreEqual(new[] { HapticStyle.Light }, StampHaptics.For(false, false, Grade.Slowpoke));
            CollectionAssert.AreEqual(new[] { HapticStyle.Error }, StampHaptics.For(true, false, Grade.None));
            CollectionAssert.AreEqual(new[] { HapticStyle.Warning }, StampHaptics.For(false, true, Grade.None));
            // a foul outranks everything (the card shows TOO EARLY), no-shot outranks a grade
            Assert.AreSame(StampHaptics.TooEarly, StampHaptics.For(true, true, Grade.Lightning));
            Assert.AreSame(StampHaptics.NoShot, StampHaptics.For(false, true, Grade.Lightning));
            Assert.IsNull(StampHaptics.For(false, false, Grade.None));
            // every pattern is distinct (the point of a language)
            var all = new[] { StampHaptics.Lightning, StampHaptics.Perfect, StampHaptics.Great, StampHaptics.Plain, StampHaptics.TooEarly, StampHaptics.NoShot };
            for (int i = 0; i < all.Length; i++)
                for (int j = i + 1; j < all.Length; j++)
                    CollectionAssert.AreNotEqual(all[i], all[j]);
        }

        [Test]
        public void NotifyCodes()
        {
            Assert.IsFalse(HapticStyle.IsNotify(HapticStyle.Heavy));
            Assert.IsTrue(HapticStyle.IsNotify(HapticStyle.Success));
            Assert.AreEqual(0, HapticStyle.NotifyType(HapticStyle.Success));
            Assert.AreEqual(1, HapticStyle.NotifyType(HapticStyle.Warning));
            Assert.AreEqual(2, HapticStyle.NotifyType(HapticStyle.Error));
        }

        [Test]
        public void FirstPulseIsImmediateAndGapsAreHonoured()
        {
            var s = new HapticSequencer();
            s.Start(new[] { 2, 65, 1, 65, 0 });
            Assert.AreEqual(2, s.Poll());
            Assert.AreEqual(-1, s.Poll());
            s.Advance(64.9);
            Assert.AreEqual(-1, s.Poll());
            s.Advance(0.1);
            Assert.AreEqual(1, s.Poll());
            Assert.AreEqual(-1, s.Poll());
            s.Advance(65);
            Assert.AreEqual(0, s.Poll());
            Assert.IsFalse(s.Active);
            Assert.AreEqual(-1, s.Poll());
        }

        [Test]
        public void RhythmHoldsAtAnyFrameRate()
        {
            // 60 Hz: pulses land on the first frame at or after 0 / 65 / 130 ms, never drifting late
            var f60 = Run(StampHaptics.Lightning, 1000.0 / 60.0, 400);
            Assert.AreEqual(3, f60.Count);
            Assert.AreEqual(0, f60[0].Key, 1e-9);
            Assert.That(f60[1].Key, Is.GreaterThanOrEqualTo(65).And.LessThan(65 + 1000.0 / 60.0));
            Assert.That(f60[2].Key, Is.GreaterThanOrEqualTo(130).And.LessThan(130 + 1000.0 / 60.0));

            // 120 Hz: same pulses, tighter
            var f120 = Run(StampHaptics.Lightning, 1000.0 / 120.0, 400);
            Assert.AreEqual(3, f120.Count);
            Assert.That(f120[2].Key, Is.GreaterThanOrEqualTo(130).And.LessThan(130 + 1000.0 / 120.0));

            // a 200 ms hitch: everything still fires, in order, on the frame after the hitch
            var hitch = Run(StampHaptics.Lightning, 200, 200);
            Assert.AreEqual(3, hitch.Count);
            Assert.AreEqual(200, hitch[1].Key, 1e-9);
            Assert.AreEqual(200, hitch[2].Key, 1e-9);
            for (int i = 0; i < 3; i++) Assert.AreEqual(HapticStyle.Heavy, hitch[i].Value);
        }

        [Test]
        public void ReducedMotionPlaysOnlyTheFirstPulse()
        {
            var f = Run(StampHaptics.Lightning, 16, 400, true);
            Assert.AreEqual(1, f.Count);
            Assert.AreEqual(HapticStyle.Heavy, f[0].Value);
            var p = Run(StampHaptics.Perfect, 16, 400, true);
            Assert.AreEqual(1, p.Count);
            Assert.AreEqual(HapticStyle.Medium, p[0].Value);
        }

        [Test]
        public void RestartCancelAndEdgeCases()
        {
            var s = new HapticSequencer();
            s.Start(StampHaptics.Lightning);
            Assert.AreEqual(HapticStyle.Heavy, s.Poll());
            s.Start(StampHaptics.TooEarly);                 // a new stamp replaces the running pattern
            Assert.AreEqual(HapticStyle.Error, s.Poll());
            Assert.IsFalse(s.Active);

            s.Start(StampHaptics.Perfect);
            s.Cancel();
            Assert.IsFalse(s.Active);
            s.Advance(1000);
            Assert.AreEqual(-1, s.Poll());

            s.Start(null);
            Assert.IsFalse(s.Active);
            Assert.AreEqual(-1, s.Poll());
            s.Start(new int[0]);
            Assert.AreEqual(-1, s.Poll());

            // trailing gap and zero / negative gaps are harmless
            s.Start(new[] { 1, 0, 2, -5, 0, 40 });
            Assert.AreEqual(1, s.Poll());
            Assert.AreEqual(2, s.Poll());
            Assert.AreEqual(0, s.Poll());
            Assert.IsFalse(s.Active);

            // negative dt never rewinds
            s.Start(new[] { 1, 50, 2 });
            s.Poll();
            s.Advance(-100);
            s.Advance(50);
            Assert.AreEqual(2, s.Poll());
        }
    }
}
