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

        // ------------------------------------------------------------------ presets (Core Haptics / Android data)

        [Test]
        public void EveryPresetFollowsTheAuthoringRules()
        {
            for (int id = 0; id < HapticPreset.BuiltInCount; id++)
            {
                var p = HapticPresets.Get(id);
                Assert.IsNull(HapticPresets.Validate(p), HapticPresets.Name(id));
                Assert.That(HapticPresets.Duration(p), Is.LessThanOrEqualTo(0.8f), HapticPresets.Name(id) + " too long");
                // everything starts on its trigger, except the bullet landing just after the recoil (45 ms)
                Assert.That(p[0], Is.LessThanOrEqualTo(0.05f), HapticPresets.Name(id) + " starts late");
            }
            // the rule the Core Haptics curves rely on
            Assert.IsNotNull(HapticPresets.Validate(new[] { 0f, 0.1f, 1f, 0f, 0.5f, 0.5f, 0.05f, 0f, 1f, 1f, 1f, 1f }));
            Assert.IsNotNull(HapticPresets.Validate(new[] { 0f, 0f, 1.2f, 1.2f, 0.5f, 0.5f }));
            Assert.IsNotNull(HapticPresets.Validate(new[] { 0.1f, 0f, 1f, 1f, 1f, 1f, 0f, 0f, 1f, 1f, 1f, 1f }));
            Assert.IsNotNull(HapticPresets.Validate(new float[5]));
            Assert.IsNotNull(HapticPresets.Validate(null));
        }

        [Test]
        public void DrawCueOpensWithTheStrongestSharpestTransient()
        {
            var d = HapticPresets.Get(HapticPreset.DrawCue);
            Assert.IsTrue(HapticPresets.IsTransient(d, 0));
            Assert.AreEqual(0f, d[0]);           // onset exactly at the scheduled (photon) time
            Assert.AreEqual(1f, d[2]);
            Assert.AreEqual(1f, d[4]);
            Assert.IsFalse(HapticPresets.IsRhythmic(HapticPreset.DrawCue));   // Reduced Motion keeps the whole cue
            // nothing else in the game is both as strong and as sharp
            for (int id = 0; id < HapticPreset.BuiltInCount; id++)
            {
                if (id == HapticPreset.DrawCue) continue;
                var p = HapticPresets.Get(id);
                for (int e = 0; e < HapticPresets.EventCount(p); e++)
                    Assert.IsFalse(p[e * 6 + 2] >= 1f && p[e * 6 + 4] >= 1f, HapticPresets.Name(id) + " copies the DRAW crack");
            }
        }

        [Test]
        public void StampPatternsConvertToTransientsOnTheSameRhythm()
        {
            var l = HapticPresets.FromPattern(StampHaptics.Lightning);
            Assert.AreEqual(3, HapticPresets.EventCount(l));
            Assert.AreEqual(0f, l[0], 1e-6);
            Assert.AreEqual(0.065f, l[6], 1e-6);
            Assert.AreEqual(0.130f, l[12], 1e-6);
            for (int e = 0; e < 3; e++)
            {
                Assert.IsTrue(HapticPresets.IsTransient(l, e));
                Assert.AreEqual(HapticPresets.StyleIntensity[2], l[e * 6 + 2]);
            }
            Assert.IsNull(HapticPresets.Validate(l));
            var p = HapticPresets.FromPattern(StampHaptics.Perfect);
            Assert.AreEqual(2, HapticPresets.EventCount(p));
            Assert.AreEqual(HapticPresets.StyleIntensity[1], p[2]);
            Assert.AreEqual(HapticPresets.StyleIntensity[2], p[8]);
            Assert.AreEqual(0.085f, p[6], 1e-6);
            // notification pulses stay on the system generator / sequencer
            Assert.IsNull(HapticPresets.FromPattern(StampHaptics.TooEarly));
            Assert.IsNull(HapticPresets.FromPattern(StampHaptics.NoShot));
            Assert.IsNull(HapticPresets.FromPattern(null));

            // the native preset and the frame sequencer agree on every pulse time (1 ms frames)
            var seq = Run(StampHaptics.Lightning, 1, 400);
            for (int i = 0; i < 3; i++) Assert.AreEqual(l[i * 6] * 1000.0, seq[i].Key, 1.0);
        }

        [Test]
        public void FirstEventOnlyKeepsJustTheLub()
        {
            var hb = HapticPresets.Get(HapticPreset.Heartbeat);
            Assert.AreEqual(2, HapticPresets.EventCount(hb));
            var one = HapticPresets.FirstEventOnly(hb);
            Assert.AreEqual(1, HapticPresets.EventCount(one));
            Assert.AreEqual(hb[2], one[2]);
            Assert.AreEqual(2, HapticPresets.EventCount(hb), "source untouched");
            Assert.IsTrue(HapticPresets.IsRhythmic(HapticPreset.Heartbeat));
            Assert.IsFalse(HapticPresets.IsRhythmic(HapticPreset.HitTaken));
        }

        // ------------------------------------------------------------------ policy: gating, heartbeat, DRAW lead

        [Test]
        public void GatingToggleSystemAndReducedMotion()
        {
            Assert.IsTrue(HapticPolicy.Allowed(true, true));
            Assert.IsFalse(HapticPolicy.Allowed(false, true));    // in-game toggle off
            Assert.IsFalse(HapticPolicy.Allowed(true, false));    // Android Touch feedback off
            Assert.IsFalse(HapticPolicy.Allowed(false, false));
            Assert.IsTrue(HapticPolicy.FirstOnly(true, true));
            Assert.IsFalse(HapticPolicy.FirstOnly(true, false));  // single gestures stay whole
            Assert.IsFalse(HapticPolicy.FirstOnly(false, true));
            Assert.AreEqual(3, HapticPolicy.PatternPulses(StampHaptics.Lightning, false));
            Assert.AreEqual(1, HapticPolicy.PatternPulses(StampHaptics.Lightning, true));
            Assert.AreEqual(1, HapticPolicy.PatternPulses(StampHaptics.Great, true));
            Assert.AreEqual(0, HapticPolicy.PatternPulses(null, true));
        }

        [Test]
        public void HeartbeatScalesWithFrenzyAndStaysSubtle()
        {
            Assert.AreEqual(0f, HapticPolicy.HeartbeatScale(0f, false));
            Assert.AreEqual(0f, HapticPolicy.HeartbeatScale(0.34f, false));          // calm beats: sound only
            Assert.AreEqual(0.30f, HapticPolicy.HeartbeatScale(0.35f, false), 1e-5);
            Assert.AreEqual(0.80f, HapticPolicy.HeartbeatScale(1f, false), 1e-5);
            Assert.AreEqual(0.80f, HapticPolicy.HeartbeatScale(5f, false), 1e-5);
            Assert.That(HapticPolicy.HeartbeatScale(0.1f, true), Is.GreaterThan(0f)); // a skipped beat is always felt
            Assert.AreEqual(0.80f, HapticPolicy.HeartbeatScale(1f, true), 1e-5);      // never above the cap
            Assert.AreEqual(0f, HapticPolicy.HeartbeatScale(float.NaN, false));
            float prev = 0f;
            for (float f = 0.35f; f <= 1.0001f; f += 0.05f)
            {
                float s = HapticPolicy.HeartbeatScale(f, false);
                Assert.That(s, Is.GreaterThanOrEqualTo(prev));
                Assert.That(s, Is.InRange(0.3f, 0.8f));
                prev = s;
            }
            // the heart's own sharpness is low (a thump, not a click)
            var hb = HapticPresets.Get(HapticPreset.Heartbeat);
            Assert.That(hb[4], Is.LessThanOrEqualTo(0.2f));
            Assert.That(hb[10], Is.LessThanOrEqualTo(0.2f));
        }

        [Test]
        public void DrawCueLeadMaths()
        {
            double frame120 = 1.0 / 120.0;
            // zero output latency: the haptic is scheduled exactly as far ahead as the DRAW sound
            Assert.AreEqual(frame120, HapticPolicy.DrawCueDelaySeconds(frame120, 0), 1e-12);
            // latency is taken off the lead
            Assert.AreEqual(frame120 - 0.005, HapticPolicy.DrawCueDelaySeconds(frame120, 0.005), 1e-12);
            // latency longer than the lead: fire now, never "in the past"
            Assert.AreEqual(0, HapticPolicy.DrawCueDelaySeconds(frame120, 0.010));
            // late frame (photon time already passed) and garbage
            Assert.AreEqual(0, HapticPolicy.DrawCueDelaySeconds(-0.003, 0));
            Assert.AreEqual(0, HapticPolicy.DrawCueDelaySeconds(double.NaN, 0));
            Assert.AreEqual(frame120, HapticPolicy.DrawCueDelaySeconds(frame120, double.NaN), 1e-12);
            Assert.AreEqual(frame120, HapticPolicy.DrawCueDelaySeconds(frame120, -1), 1e-12);
            // a stale lead can never hold the cue back
            Assert.AreEqual(HapticPolicy.MaxDrawLeadSeconds, HapticPolicy.DrawCueDelaySeconds(3.0, 0), 1e-12);

            // the lead is the same expression PresentDraw feeds the audio: (cuePresentedNs - nowNs) / 1e9
            long now = 5_000_000_000L, presentLatency = (long)(1e9 / 120.0);
            long cuePresented = now + presentLatency;
            double ahead = (cuePresented - now) / 1e9;
            Assert.AreEqual(presentLatency / 1e9, HapticPolicy.DrawCueDelaySeconds(ahead, 0), 1e-12);

            Assert.AreEqual(8, HapticPolicy.DelayMs(frame120));
            Assert.AreEqual(17, HapticPolicy.DelayMs(1.0 / 60.0));
            Assert.AreEqual(0, HapticPolicy.DelayMs(-1));
            Assert.AreEqual(0, HapticPolicy.DelayMs(double.NaN));
            Assert.AreEqual(10000, HapticPolicy.DelayMs(99));
        }

        [Test]
        public void LevelsQuantizeToTwentiethsAndRoundTrip()
        {
            Assert.AreEqual(21, HapticPolicy.Levels);
            Assert.AreEqual(0, HapticPolicy.Level(0f));
            Assert.AreEqual(0, HapticPolicy.Level(-1f));
            Assert.AreEqual(0, HapticPolicy.Level(float.NaN));
            Assert.AreEqual(20, HapticPolicy.Level(1f));
            Assert.AreEqual(20, HapticPolicy.Level(7f));
            Assert.AreEqual(6, HapticPolicy.Level(0.30f));
            Assert.AreEqual(16, HapticPolicy.Level(0.80f));
            Assert.AreEqual(1, HapticPolicy.Level(0.025f));    // rounds half up
            for (int l = 0; l < HapticPolicy.Levels; l++) Assert.AreEqual(l, HapticPolicy.Level(HapticPolicy.FromLevel(l)));
        }

        // ------------------------------------------------------------------ Android waveform raster

        private static long Sum(long[] t) { long s = 0; foreach (var x in t) s += x; return s; }

        [Test]
        public void RasterTransientsAndLeadingSilence()
        {
            long[] t; int[] a; int start;
            // hit dealt: one sharp tap 45 ms in -> the 45 ms become a start delay, the waveform is the pulse alone
            HapticRaster.Build(HapticPresets.Get(HapticPreset.HitDealt), 1f, true, out t, out a, out start);
            Assert.AreEqual(45, start);
            Assert.AreEqual(1, t.Length);
            Assert.AreEqual(HapticRaster.TransientMs(0.95f), t[0]);
            Assert.AreEqual(HapticRaster.Amplitude(0.6f, 1f), a[0]);
            Assert.AreEqual(9, HapticRaster.TransientMs(0.95f));
            Assert.AreEqual(20, HapticRaster.TransientMs(0f));
            Assert.AreEqual(8, HapticRaster.TransientMs(1f));

            // lightning: tap, silence, tap, silence, tap with exact 65 ms spacing
            HapticRaster.Build(HapticPresets.FromPattern(StampHaptics.Lightning), 1f, true, out t, out a, out start);
            Assert.AreEqual(0, start);
            Assert.AreEqual(5, t.Length);
            Assert.AreEqual(65, t[0] + t[1]);
            Assert.AreEqual(65, t[2] + t[3]);
            Assert.AreEqual(0, a[1]);
            Assert.AreEqual(255, a[0]);
        }

        [Test]
        public void RasterContinuousRampsAndScale()
        {
            long[] t; int[] a; int start;
            var draw = HapticPresets.Get(HapticPreset.DrawCue);
            HapticRaster.Build(draw, 1f, true, out t, out a, out start);
            Assert.AreEqual(0, start);
            Assert.AreEqual(255, a[0]);                                // the crack
            Assert.AreEqual(122, Sum(t));                              // 12 ms + 110 ms rumble
            Assert.AreEqual(8, t[0]);                                  // crack: 8 ms at full sharpness
            Assert.AreEqual(0, a[1]);                                  // 4 ms notch before the rumble
            for (int i = 3; i < a.Length; i++) Assert.That(a[i], Is.LessThan(a[i - 1]), "rumble decays");
            for (int i = 1; i < a.Length; i++) Assert.AreNotEqual(a[i], a[i - 1], "neighbours merged");

            // scale multiplies every amplitude
            HapticRaster.Build(HapticPresets.Get(HapticPreset.Heartbeat), 0.3f, true, out t, out a, out start);
            Assert.AreEqual(HapticRaster.Amplitude(1f, 0.3f), a[0]);
            Assert.AreEqual(77, a[0]);

            // no amplitude control: taps stay full pulses at any scale, quiet continuous steps drop out
            HapticRaster.Build(HapticPresets.Get(HapticPreset.Heartbeat), 0.3f, false, out t, out a, out start);
            Assert.AreEqual(255, a[0]);
            Assert.AreEqual(3, t.Length);                              // lub, gap, dub
            HapticRaster.Build(HapticPresets.Get(HapticPreset.Swipe), 1f, false, out t, out a, out start);
            Assert.AreEqual(60, start);                                // the swish is too quiet for on/off motors
            foreach (int x in a) Assert.That(x == 0 || x == 255);

            // scale 0: nothing at all
            HapticRaster.Build(HapticPresets.Get(HapticPreset.Recoil), 0f, true, out t, out a, out start);
            Assert.AreEqual(0, t.Length);
        }

        [Test]
        public void CompositionTransientsOnlyForTapPresets()
        {
            Assert.IsNull(HapticRaster.Transients(HapticPresets.Get(HapticPreset.DrawCue), 1f));
            var m = HapticRaster.Transients(HapticPresets.Get(HapticPreset.Misfire), 0.5f);
            Assert.AreEqual(6, m.Length);
            Assert.AreEqual(0f, m[0]);
            Assert.AreEqual(70f, m[3]);
            Assert.AreEqual(0.425f, m[1], 1e-6);
            Assert.AreEqual(1f, m[2]);
        }
    }
}
