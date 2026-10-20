using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>
    /// Haptic preset ids (slots shared by the iOS Core Haptics bridge and the Android bridge).
    /// 0..BuiltInCount-1 = the designed moments below, PatternBase.. = stamp patterns converted at first use
    /// (<see cref="HapticPresets.FromPattern"/>), +FirstOnlyOffset = the Reduced Motion variant of a slot
    /// (first event only).
    /// </summary>
    public static class HapticPreset
    {
        public const int CountTick3 = 0;
        public const int CountTick2 = 1;
        public const int CountTick1 = 2;
        public const int Heartbeat = 3;
        public const int DrawCue = 4;
        public const int Swipe = 5;
        public const int Recoil = 6;
        public const int HitDealt = 7;
        public const int KillDealt = 8;
        public const int HitTaken = 9;
        public const int KilledYou = 10;
        public const int BodyFall = 11;
        public const int Misfire = 12;
        public const int Tie = 13;
        public const int LastLightSpike = 14;
        public const int NamePlate = 15;
        public const int UiTap = 16;
        public const int Win = 17;
        public const int Lose = 18;
        /// <summary>Impact(0/1/2) = Light + style.</summary>
        public const int Light = 19;
        public const int Medium = 20;
        public const int Heavy = 21;
        /// <summary>Notify(0/1/2) on Android (iOS keeps UINotificationFeedbackGenerator) = NotifySuccess + type.</summary>
        public const int NotifySuccess = 22;
        public const int NotifyWarning = 23;
        public const int NotifyError = 24;
        public const int BuiltInCount = 25;

        /// <summary>First slot for stamp patterns registered at runtime.</summary>
        public const int PatternBase = 32;
        public const int PatternSlots = 32;
        /// <summary>Slot + this = first-event-only variant (Reduced Motion on a rhythmic preset).</summary>
        public const int FirstOnlyOffset = 64;
        public const int TotalSlots = 128;
    }

    /// <summary>
    /// The designed haptic moments as data. Each preset is a flat float array of events, stride
    /// <see cref="Stride"/>: { time s, duration s (0 = transient), intensity start, intensity end,
    /// sharpness start, sharpness end }. Transients use the start values. Continuous events ramp linearly
    /// (Core Haptics: parameter curves; Android: 10 ms amplitude steps). Rule (checked by tests): no transient
    /// lies inside a continuous event's window, because a Core Haptics intensity curve scales every event it
    /// overlaps. Static arrays: choosing a preset never allocates. Pure C#, unit-tested.
    /// </summary>
    public static class HapticPresets
    {
        public const int Stride = 6;

        private static readonly float[][] data = new float[HapticPreset.BuiltInCount][];
        private static readonly bool[] rhythmic = new bool[HapticPreset.BuiltInCount];
        private static readonly string[] names = new string[HapticPreset.BuiltInCount];

        /// <summary>Impact styles as transients: { intensity, sharpness } for light, medium, heavy.</summary>
        public static readonly float[] StyleIntensity = { 0.45f, 0.70f, 1.00f };
        public static readonly float[] StyleSharpness = { 0.70f, 0.60f, 0.75f };

        static HapticPresets()
        {
            // countdown: 3-2-1 grow with the tick volume (0.55 / 0.68 / 0.82), crisp, never heavy
            Def(HapticPreset.CountTick3, "count 3", false, T(0f, 0.40f, 0.55f));
            Def(HapticPreset.CountTick2, "count 2", false, T(0f, 0.55f, 0.60f));
            Def(HapticPreset.CountTick1, "count 1", false, T(0f, 0.70f, 0.65f));
            // hold heart: lub-dub matched to sfx_heartbeat (lub peak 10 ms, dub 245 ms at pitch 1; the pitch rises
            // to x1.12 with the frenzy, so the dub sits at 225 ms); low sharpness = a thump, not a click. Played
            // scaled 0.3..0.8 (HapticPolicy.HeartbeatScale), so these are the x1 values.
            Def(HapticPreset.Heartbeat, "heartbeat", true, T(0f, 1.00f, 0.12f), T(0.225f, 0.70f, 0.08f));
            // DRAW: the sharpest, strongest crack in the game, then a short decaying rumble (sfx_draw decays ~150 ms)
            Def(HapticPreset.DrawCue, "draw cue", false, T(0f, 1.00f, 1.00f), C(0.012f, 0.110f, 0.70f, 0f, 0.50f, 0.15f));
            // swipe: the gun clearing leather (sfx_holster_draw rises to its peak at ~70 ms): soft swish, then a click
            Def(HapticPreset.Swipe, "swipe", false, C(0f, 0.055f, 0.12f, 0.35f, 0.25f, 0.35f), T(0.060f, 0.45f, 0.55f));
            // shot: hard crack + 70 ms recoil buzz
            Def(HapticPreset.Recoil, "recoil", false, T(0f, 1.00f, 0.80f), C(0.010f, 0.070f, 0.75f, 0f, 0.45f, 0.15f));
            // your bullet lands: a dry high tock just after the recoil peak (same frame as the shot when the bot is
            // standing right there), so it reads as a second event
            Def(HapticPreset.HitDealt, "hit dealt", false, T(0.045f, 0.60f, 0.95f));
            // killing shot: the tock, then a low rumble that rides the kill slow-mo
            Def(HapticPreset.KillDealt, "kill dealt", false, T(0.045f, 0.75f, 0.90f), C(0.060f, 0.350f, 0.50f, 0f, 0.15f, 0.05f));
            // hit taken: a body blow (low, heavy, 200 ms fade)
            Def(HapticPreset.HitTaken, "hit taken", false, T(0f, 1.00f, 0.25f), C(0.010f, 0.200f, 0.90f, 0f, 0.20f, 0.05f));
            // you go down: the blow, then a long dull fade
            Def(HapticPreset.KilledYou, "killed", false, T(0f, 1.00f, 0.20f), C(0.010f, 0.550f, 1.00f, 0f, 0.15f, 0f));
            Def(HapticPreset.BodyFall, "body fall", false, T(0f, 0.75f, 0.20f), C(0.010f, 0.120f, 0.50f, 0f, 0.10f, 0f));
            // too early: a dry jam click and the hammer catching (sfx_misfire + low sfx_cock)
            Def(HapticPreset.Misfire, "misfire", true, T(0f, 0.85f, 1.00f), T(0.070f, 0.35f, 0.70f));
            // TOO CLOSE: two hard cracks 60 ms apart (both guns), a ringing tail under the slow-mo
            Def(HapticPreset.Tie, "tie", true, T(0f, 1.00f, 0.95f), T(0.060f, 1.00f, 0.95f), C(0.075f, 0.250f, 0.40f, 0f, 0.60f, 0.30f));
            // LAST LIGHT: the monitor spike after the flat line, a defibrillator thoom
            Def(HapticPreset.LastLightSpike, "last light", false, T(0f, 1.00f, 0.45f), C(0.010f, 0.280f, 0.75f, 0f, 0.20f, 0.05f));
            // intro name-plate slam: a stamp, firm and short
            Def(HapticPreset.NamePlate, "name plate", false, T(0f, 0.55f, 0.75f));
            // UI press: light and crisp
            Def(HapticPreset.UiTap, "ui tap", false, T(0f, 0.40f, 0.80f));
            // match won: three rising taps as the banner lands, a soft swell under the sting
            Def(HapticPreset.Win, "win", true, T(0f, 0.50f, 0.50f), T(0.090f, 0.75f, 0.65f), T(0.180f, 1.00f, 0.80f), C(0.200f, 0.300f, 0.40f, 0f, 0.30f, 0.20f));
            // match lost: one dull drop and a slow fade
            Def(HapticPreset.Lose, "lose", false, T(0f, 0.60f, 0.15f), C(0.010f, 0.450f, 0.55f, 0f, 0.10f, 0f));
            Def(HapticPreset.Light, "impact light", false, T(0f, StyleIntensity[0], StyleSharpness[0]));
            Def(HapticPreset.Medium, "impact medium", false, T(0f, StyleIntensity[1], StyleSharpness[1]));
            Def(HapticPreset.Heavy, "impact heavy", false, T(0f, StyleIntensity[2], StyleSharpness[2]));
            // notification look-alikes for Android (iOS uses the system generator)
            Def(HapticPreset.NotifySuccess, "notify success", true, T(0f, 0.60f, 0.50f), T(0.100f, 1.00f, 0.70f));
            Def(HapticPreset.NotifyWarning, "notify warning", true, T(0f, 1.00f, 0.70f), T(0.120f, 0.60f, 0.50f));
            Def(HapticPreset.NotifyError, "notify error", true, T(0f, 0.85f, 0.80f), T(0.065f, 0.85f, 0.80f), T(0.130f, 1.00f, 0.90f));
        }

        private static float[] T(float t, float i, float s) { return new[] { t, 0f, i, i, s, s }; }
        private static float[] C(float t, float d, float i0, float i1, float s0, float s1) { return new[] { t, d, i0, i1, s0, s1 }; }

        private static void Def(int id, string name, bool isRhythmic, params float[][] events)
        {
            var a = new float[events.Length * Stride];
            for (int e = 0; e < events.Length; e++) Array.Copy(events[e], 0, a, e * Stride, Stride);
            data[id] = a;
            rhythmic[id] = isRhythmic;
            names[id] = name;
        }

        /// <summary>The events of a built-in preset (shared array: do not modify).</summary>
        public static float[] Get(int id) { return id >= 0 && id < data.Length ? data[id] : null; }

        /// <summary>True when Reduced Motion should play only the first event (a rhythm, not a single gesture).</summary>
        public static bool IsRhythmic(int id) { return id >= 0 && id < rhythmic.Length && rhythmic[id]; }

        public static string Name(int id) { return id >= 0 && id < names.Length ? names[id] : "preset " + id; }

        public static int EventCount(float[] events) { return events == null ? 0 : events.Length / Stride; }

        public static bool IsTransient(float[] events, int e) { return events[e * Stride + 1] <= 0f; }

        /// <summary>Total length in seconds (end of the last event; a transient counts as an instant).</summary>
        public static float Duration(float[] events)
        {
            float end = 0f;
            int n = EventCount(events);
            for (int e = 0; e < n; e++)
            {
                float t = events[e * Stride] + Math.Max(0f, events[e * Stride + 1]);
                if (t > end) end = t;
            }
            return end;
        }

        /// <summary>True when the preset has any continuous event.</summary>
        public static bool HasContinuous(float[] events)
        {
            int n = EventCount(events);
            for (int e = 0; e < n; e++) if (!IsTransient(events, e)) return true;
            return false;
        }

        /// <summary>
        /// Checks a preset against the authoring rules: stride, times >= 0, values in 0..1, events sorted by
        /// time, no transient strictly inside a continuous window. Returns null when valid, else the reason.
        /// </summary>
        public static string Validate(float[] events)
        {
            if (events == null || events.Length == 0) return "empty";
            if (events.Length % Stride != 0) return "length not a multiple of " + Stride;
            int n = EventCount(events);
            float prev = -1f;
            for (int e = 0; e < n; e++)
            {
                int o = e * Stride;
                if (events[o] < 0f) return "negative time at event " + e;
                if (events[o] < prev) return "events not sorted at " + e;
                prev = events[o];
                if (events[o + 1] < 0f) return "negative duration at event " + e;
                for (int k = 2; k < Stride; k++)
                    if (events[o + k] < 0f || events[o + k] > 1f || float.IsNaN(events[o + k])) return "value out of 0..1 at event " + e;
            }
            for (int c = 0; c < n; c++)
            {
                if (IsTransient(events, c)) continue;
                float a = events[c * Stride], b = a + events[c * Stride + 1];
                for (int t = 0; t < n; t++)
                {
                    if (!IsTransient(events, t)) continue;
                    float tt = events[t * Stride];
                    if (tt > a && tt < b) return "transient " + t + " inside continuous " + c;
                }
            }
            return null;
        }

        /// <summary>Reduced Motion variant: the first event only (a new array; call once per preset, then cache).</summary>
        public static float[] FirstEventOnly(float[] events)
        {
            if (events == null || events.Length < Stride) return events;
            var a = new float[Stride];
            Array.Copy(events, 0, a, 0, Stride);
            a[0] = 0f;
            return a;
        }

        /// <summary>
        /// Converts a stamp pattern { pulse, gapMs, pulse, ... } (<see cref="HapticStyle"/> codes) into transients
        /// with the impact-style values. Returns null when the pattern contains a notification code (those stay
        /// on the system notification generator / the frame sequencer). Allocates: convert once per pattern.
        /// </summary>
        public static float[] FromPattern(int[] stylesWithGapsMs)
        {
            if (stylesWithGapsMs == null || stylesWithGapsMs.Length == 0) return null;
            int pulses = (stylesWithGapsMs.Length + 1) / 2;
            var a = new float[pulses * Stride];
            double t = 0;
            for (int p = 0; p < pulses; p++)
            {
                int code = stylesWithGapsMs[p * 2];
                if (HapticStyle.IsNotify(code)) return null;
                int s = code < 0 ? 0 : (code > 2 ? 2 : code);
                int o = p * Stride;
                a[o] = (float)(t / 1000.0);
                a[o + 1] = 0f;
                a[o + 2] = a[o + 3] = StyleIntensity[s];
                a[o + 4] = a[o + 5] = StyleSharpness[s];
                if (p * 2 + 1 < stylesWithGapsMs.Length)
                {
                    int gap = stylesWithGapsMs[p * 2 + 1];
                    t += gap > 0 ? gap : 0;
                }
            }
            return a;
        }
    }

    /// <summary>
    /// Haptic decisions that must hold on every platform: gating, Reduced Motion, heartbeat scaling, quantization
    /// and the DRAW cue lead. Pure C#, unit-tested.
    /// </summary>
    public static class HapticPolicy
    {
        /// <summary>Intensity / sharpness / scale quantization: 0.05 steps (21 levels), so native players can be
        /// compiled once per level and reused (no allocation per beat).</summary>
        public const int Levels = 21;

        /// <summary>Hold heartbeat: no haptic below this frenzy (the first, calm beats are sound only).</summary>
        public const float HeartGate = 0.35f;
        public const float HeartMin = 0.30f;
        public const float HeartMax = 0.80f;
        /// <summary>A skipped (early, arrhythmic) beat lands a little harder.</summary>
        public const float HeartSkipBoost = 0.10f;

        /// <summary>Never schedule the DRAW haptic further ahead than this (a stale lead must not delay it).</summary>
        public const double MaxDrawLeadSeconds = 0.25;

        /// <summary>Any haptic plays only with the in-game toggle on and the OS allowing it (Android touch feedback).</summary>
        public static bool Allowed(bool toggle, bool systemAllows) { return toggle && systemAllows; }

        /// <summary>Reduced Motion keeps one pulse of a rhythm (stamps, heart lub-dub, win taps); single gestures stay whole.</summary>
        public static bool FirstOnly(bool reducedMotion, bool rhythmic) { return reducedMotion && rhythmic; }

        /// <summary>Pattern pulses to play (all, or 1 under Reduced Motion).</summary>
        public static int PatternPulses(int[] pattern, bool reducedMotion)
        {
            int n = StampHaptics.Pulses(pattern);
            return reducedMotion && n > 1 ? 1 : n;
        }

        /// <summary>0..1 to a level 0..Levels-1 (round to nearest; NaN = 0).</summary>
        public static int Level(float v)
        {
            if (float.IsNaN(v) || v <= 0f) return 0;
            if (v >= 1f) return Levels - 1;
            return (int)Math.Round(v * (Levels - 1), MidpointRounding.AwayFromZero);
        }

        public static float FromLevel(int level)
        {
            if (level <= 0) return 0f;
            if (level >= Levels - 1) return 1f;
            return level / (float)(Levels - 1);
        }

        /// <summary>
        /// Heartbeat haptic scale for a beat at this frenzy (0..1): 0 = no haptic (below the gate, unless the beat
        /// skipped), else 0.30 at the gate rising linearly to 0.80 at full frenzy, +0.10 on a skipped beat (capped).
        /// </summary>
        public static float HeartbeatScale(float frenzy, bool skipped)
        {
            if (float.IsNaN(frenzy)) frenzy = 0f;
            if (frenzy < HeartGate && !skipped) return 0f;
            float k = (frenzy - HeartGate) / (1f - HeartGate);
            if (k < 0f) k = 0f;
            if (k > 1f) k = 1f;
            float s = HeartMin + (HeartMax - HeartMin) * k;
            if (skipped) s = Math.Min(HeartMax, s + HeartSkipBoost);
            return s;
        }

        /// <summary>
        /// Delay before the DRAW haptic is triggered so its onset lands at the estimated photon time:
        /// lead (the same seconds-ahead the DRAW sound is scheduled with) minus the haptic output latency of the
        /// backend, clamped to 0..MaxDrawLeadSeconds. NaN / negative inputs count as 0.
        /// </summary>
        public static double DrawCueDelaySeconds(double leadSeconds, double outputLatencySeconds)
        {
            if (double.IsNaN(leadSeconds) || leadSeconds < 0) leadSeconds = 0;
            if (leadSeconds > MaxDrawLeadSeconds) leadSeconds = MaxDrawLeadSeconds;
            if (double.IsNaN(outputLatencySeconds) || outputLatencySeconds < 0) outputLatencySeconds = 0;
            double d = leadSeconds - outputLatencySeconds;
            return d > 0 ? d : 0;
        }

        /// <summary>Seconds to whole milliseconds for the Android scheduler (round to nearest, never negative).</summary>
        public static int DelayMs(double seconds)
        {
            if (double.IsNaN(seconds) || seconds <= 0) return 0;
            if (seconds > 10) return 10000;
            return (int)Math.Round(seconds * 1000.0, MidpointRounding.AwayFromZero);
        }
    }

    /// <summary>
    /// Rasterizes a preset into an Android waveform (createWaveform timings + amplitudes). Continuous events are
    /// stepped every <see cref="StepMs"/> ms (linear ramp sampled mid-step); a transient is a short full pulse whose
    /// length falls with sharpness (sharp = short = click, soft = longer = thump). Overlaps take the max.
    /// Leading silence is returned as a start delay instead (a new vibrate() cancels the running one on Android,
    /// so silence at the start would cut the previous effect for nothing). Without amplitude control a transient is
    /// always a full pulse (a tap stays a tap at any scale) and a continuous step is on only above
    /// <see cref="OnOffThreshold"/>. Allocates: compile once per preset level.
    /// </summary>
    public static class HapticRaster
    {
        public const int StepMs = 10;
        /// <summary>Without amplitude control an amplitude below this is off, anything above is full.</summary>
        public const int OnOffThreshold = 96;

        /// <summary>Transient pulse length (ms) for a sharpness 0..1: 20 ms (soft) .. 8 ms (sharp).</summary>
        public static int TransientMs(float sharpness)
        {
            if (float.IsNaN(sharpness)) sharpness = 0.5f;
            if (sharpness < 0f) sharpness = 0f;
            if (sharpness > 1f) sharpness = 1f;
            return (int)Math.Round(20 - 12 * sharpness, MidpointRounding.AwayFromZero);
        }

        public static int Amplitude(float intensity, float scale)
        {
            float v = intensity * scale;
            if (float.IsNaN(v) || v <= 0f) return 0;
            int a = (int)Math.Round(v * 255f, MidpointRounding.AwayFromZero);
            return a < 1 ? 1 : (a > 255 ? 255 : a);
        }

        /// <summary>
        /// Builds { timings ms, amplitudes 0..255 } with equal neighbours merged. startDelayMs = leading silence
        /// removed. Without amplitude control amplitudes are binarized (0 / 255).
        /// </summary>
        public static void Build(float[] events, float scale, bool amplitudeControl, out long[] timings, out int[] amps, out int startDelayMs)
        {
            timings = new long[0];
            amps = new int[0];
            startDelayMs = 0;
            int n = HapticPresets.EventCount(events);
            if (n == 0) return;
            int totalMs = 0;
            for (int e = 0; e < n; e++)
            {
                int o = e * HapticPresets.Stride;
                int start = (int)Math.Round(events[o] * 1000.0);
                int len = events[o + 1] > 0f ? (int)Math.Round(events[o + 1] * 1000.0) : TransientMs(events[o + 4]);
                if (start + len > totalMs) totalMs = start + len;
            }
            if (totalMs <= 0) return;
            var buf = new int[totalMs];
            var tap = new bool[totalMs];
            for (int e = 0; e < n; e++)
            {
                int o = e * HapticPresets.Stride;
                int start = (int)Math.Round(events[o] * 1000.0);
                if (events[o + 1] <= 0f)
                {
                    int len = TransientMs(events[o + 4]);
                    int a = Amplitude(events[o + 2], scale);
                    for (int m = start; m < start + len && m < totalMs; m++)
                    {
                        if (a > buf[m]) buf[m] = a;
                        if (a > 0) tap[m] = true;
                    }
                }
                else
                {
                    int len = (int)Math.Round(events[o + 1] * 1000.0);
                    for (int step = 0; step * StepMs < len; step++)
                    {
                        int s0 = step * StepMs;
                        int s1 = Math.Min(len, s0 + StepMs);
                        float u = len > 0 ? ((s0 + s1) * 0.5f) / len : 0f;
                        float i = events[o + 2] + (events[o + 3] - events[o + 2]) * u;
                        int a = Amplitude(i, scale);
                        for (int m = start + s0; m < start + s1 && m < totalMs; m++) if (a > buf[m]) buf[m] = a;
                    }
                }
            }
            if (!amplitudeControl)
                for (int m = 0; m < totalMs; m++) buf[m] = (tap[m] || buf[m] >= OnOffThreshold) ? 255 : 0;
            int first = 0;
            while (first < totalMs && buf[first] == 0) first++;
            int last = totalMs - 1;
            while (last >= first && buf[last] == 0) last--;
            if (first > last) { startDelayMs = 0; return; }   // all silent (scale 0)
            startDelayMs = first;
            var tl = new List<long>();
            var al = new List<int>();
            int runStart = first;
            for (int m = first + 1; m <= last + 1; m++)
            {
                if (m == last + 1 || buf[m] != buf[runStart])
                {
                    tl.Add(m - runStart);
                    al.Add(buf[runStart]);
                    runStart = m;
                }
            }
            timings = tl.ToArray();
            amps = al.ToArray();
        }

        /// <summary>
        /// Transient list { time ms, intensity*scale, sharpness } for an Android Composition (primitives), or null
        /// when the preset has a continuous event (then the waveform is used).
        /// </summary>
        public static float[] Transients(float[] events, float scale)
        {
            if (events == null || HapticPresets.HasContinuous(events)) return null;
            int n = HapticPresets.EventCount(events);
            var a = new float[n * 3];
            for (int e = 0; e < n; e++)
            {
                int o = e * HapticPresets.Stride;
                a[e * 3] = (float)Math.Round(events[o] * 1000.0);
                float v = events[o + 2] * scale;
                a[e * 3 + 1] = v < 0f ? 0f : (v > 1f ? 1f : v);
                a[e * 3 + 2] = events[o + 4];
            }
            return a;
        }
    }
}
