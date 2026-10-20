using PawNoon.Core;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace PawNoon.Runtime
{
    /// <summary>
    /// Haptics (Docs/HAPTICS.md).
    /// iOS: Core Haptics through Plugins/iOS/PawHaptics.mm (a prepared CHHapticEngine playing precompiled pattern
    /// players), UIKit feedback generators as the fallback and for <see cref="Notify"/>.
    /// Android: Plugins/Android/PawHapticsBridge.java (VibratorManager / Vibrator, Composition primitives,
    /// amplitude waveforms, predefined effects; a haptic thread schedules every effect; the system Touch feedback
    /// setting is respected). Editor: log only. Other platforms: no-op.
    /// Every designed moment is a preset (Core/HapticDesign.cs, <see cref="HapticMoments"/> names them).
    /// Hot calls never allocate: presets are compiled once per 0.05 level, Android uses raw JNI with reused args.
    /// Styles: 0 light, 1 medium, 2 heavy. Notify: 0 success, 1 warning, 2 error.
    /// Timed patterns (SPEC_V2 §8.6 stamp language): <see cref="Pattern"/> plays a multi-pulse stamp natively when it
    /// can (exact rhythm on the haptic clock); otherwise the duel director's frame tick advances it (<see cref="Tick"/>).
    /// </summary>
    public static class PawHaptics
    {
        private static bool enabled = true;

        /// <summary>Settings toggle (Haptics on/off). Off = every call is a no-op, and anything playing stops.</summary>
        public static bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled && !value) StopAll();
                enabled = value;
            }
        }

        /// <summary>Reduced Motion: rhythms collapse to their first pulse (stamps, heart lub-dub, win taps).</summary>
        public static bool ReducedMotion;

        /// <summary>Editor: log every haptic to the console.</summary>
        public static bool EditorLog = true;

        /// <summary>
        /// Time from "start now" to the felt onset of the DRAW haptic on this backend, subtracted from the DRAW lead
        /// so the haptic lands on the photon time. iOS Core Haptics schedules on the haptic server's own clock
        /// (startAtTime), assumed 0 (transient rise ~2-4 ms). Android: vibrator HAL + LRA rise, assumed 10 ms.
        /// Both are estimates until measured on device (Docs/HAPTICS.md, "Latency").
        /// </summary>
        public static double DrawCueOutputLatencySeconds =
#if UNITY_ANDROID
            0.010;
#else
            0.0;
#endif

        /// <summary>Delay (s) the last DRAW haptic was scheduled with (debug / lab).</summary>
        public static double LastDrawDelaySeconds { get; private set; }

        /// <summary>False when the OS asks for no haptics (Android Touch feedback off).</summary>
        public static bool SystemAllows { get { return systemAllows; } }

#pragma warning disable 0169, 0414   // some fields are only used by one platform's backend
        private static bool systemAllows = true;
        private static readonly HapticSequencer sequencer = new HapticSequencer();
        private static bool sequencerMuted;   // native plays the pattern; the sequencer only tracks PatternActive

        // stamp patterns converted to native presets (by array reference; the stamp table is static)
        private static readonly int[][] patternRefs = new int[HapticPreset.PatternSlots][];
        private static readonly int[] patternSlot = new int[HapticPreset.PatternSlots];
        private static readonly float[][] patternData = new float[HapticPreset.PatternSlots][];
        private static int patternCount;
        private static readonly float[][] firstOnlyData = new float[HapticPreset.FirstOnlyOffset][];
        private static bool warmed;
#pragma warning restore 0169, 0414

        // ------------------------------------------------------------------ public API

        /// <summary>
        /// Gets the haptics hardware ready: Core Haptics engine started and held up for the next ~12 s, players
        /// precompiled (first call), Android bridge + Touch feedback setting refreshed. Cheap when already up.
        /// Call at duel start, on the last countdown numeral and once a second during the hold.
        /// </summary>
        public static void Prepare()
        {
            if (!enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Prepare();
            if (!warmed)
            {
                warmed = true;
                WarmList();
            }
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!AndroidInit()) return;
            systemAllows = AndroidPrepare();
            if (!warmed)
            {
                warmed = true;
                WarmList();
            }
#endif
        }

        public static void Impact(int style)
        {
            Preset(HapticPreset.Light + (style < 0 ? 0 : (style > 2 ? 2 : style)));
        }

        public static void Notify(int type)
        {
            if (!enabled || !systemAllows) return;
            int t = type < 0 ? 0 : (type > 2 ? 2 : type);
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Notify(t);
#elif UNITY_ANDROID && !UNITY_EDITOR
            Preset(HapticPreset.NotifySuccess + t);
#elif UNITY_EDITOR
            if (EditorLog) Debug.Log("[Haptics] notify " + (t == 0 ? "success" : (t == 1 ? "warning" : "error")));
#endif
        }

        /// <summary>One tap. intensity / sharpness 0..1 (quantized to 0.05), optionally delaySeconds from now.</summary>
        public static void Transient(float intensity, float sharpness, double delaySeconds = 0)
        {
            if (!enabled || !systemAllows) return;
            int il = HapticPolicy.Level(intensity), sl = HapticPolicy.Level(sharpness);
            if (il == 0) return;
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Transient(HapticPolicy.FromLevel(il), HapticPolicy.FromLevel(sl), delaySeconds);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!AndroidInit()) return;
            AndroidCall3(midTransient, il, sl, HapticPolicy.DelayMs(delaySeconds));
#elif UNITY_EDITOR
            if (EditorLog) Debug.Log("[Haptics] transient i=" + HapticPolicy.FromLevel(il).ToString("0.00") + " s=" + HapticPolicy.FromLevel(sl).ToString("0.00") + DelayText(delaySeconds));
#endif
        }

        /// <summary>A buzz of duration seconds (0.01..2, 10 ms steps). Sharpness is iOS only (Android has no equivalent).</summary>
        public static void Continuous(float duration, float intensity, float sharpness, double delaySeconds = 0)
        {
            if (!enabled || !systemAllows) return;
            int il = HapticPolicy.Level(intensity), sl = HapticPolicy.Level(sharpness);
            if (il == 0 || !(duration > 0f)) return;
            int steps = Mathf.Clamp(Mathf.RoundToInt(duration * 100f), 1, 200);
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Continuous(steps / 100f, HapticPolicy.FromLevel(il), HapticPolicy.FromLevel(sl), delaySeconds);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!AndroidInit()) return;
            AndroidCall3(midContinuous, steps, il, HapticPolicy.DelayMs(delaySeconds));
#elif UNITY_EDITOR
            if (EditorLog) Debug.Log("[Haptics] continuous " + (steps * 10) + " ms i=" + HapticPolicy.FromLevel(il).ToString("0.00") + " s=" + HapticPolicy.FromLevel(sl).ToString("0.00") + DelayText(delaySeconds));
#endif
        }

        /// <summary>
        /// The DRAW cue haptic, timed to the estimated photon time. <paramref name="dspLeadSeconds"/> is the same
        /// seconds-ahead the DRAW sound is scheduled with (cuePresentedNs - now); the backend's output latency
        /// is taken off (<see cref="DrawCueOutputLatencySeconds"/>). One native call, nothing else: safe in PresentDraw.
        /// </summary>
        public static void ScheduleDrawCue(double dspLeadSeconds)
        {
            double d = HapticPolicy.DrawCueDelaySeconds(dspLeadSeconds, DrawCueOutputLatencySeconds);
            LastDrawDelaySeconds = d;
            Preset(HapticPreset.DrawCue, 1f, d);
        }

        /// <summary>Plays a designed preset (<see cref="HapticPreset"/>) at scale 0..1, optionally delayed.</summary>
        public static void Preset(int id, float scale = 1f, double delaySeconds = 0)
        {
            if (!enabled || !systemAllows) return;
            if (id < 0 || id >= HapticPreset.FirstOnlyOffset || SlotData(id) == null) return;
            int level = HapticPolicy.Level(scale);
            if (level == 0) return;
            int slot = HapticPolicy.FirstOnly(ReducedMotion, HapticPresets.IsRhythmic(id)) ? id + HapticPreset.FirstOnlyOffset : id;
            PlaySlot(slot, level, delaySeconds);
        }

        /// <summary>Hold heartbeat (lub-dub) scaled by the frenzy: nothing below the gate, 0.3..0.8 above it.</summary>
        public static void Heartbeat(float frenzy, bool skipped)
        {
            float s = HapticPolicy.HeartbeatScale(frenzy, skipped);
            if (s > 0f) Preset(HapticPreset.Heartbeat, s);
        }

        /// <summary>
        /// Plays { pulse, gapMs, pulse, gapMs, pulse ... } (pulse codes in <see cref="HapticStyle"/>: 0..2 impact,
        /// 10..12 notify). Replaces a running pattern. Multi-pulse impact patterns play natively as one preset (exact
        /// rhythm); otherwise the first pulse fires now and the rest from <see cref="Tick"/>. Reduced Motion: first pulse only.
        /// </summary>
        public static void Pattern(int[] stylesWithGapsMs)
        {
            if (!enabled || !systemAllows || stylesWithGapsMs == null || stylesWithGapsMs.Length == 0)
            {
                sequencer.Cancel();
                return;
            }
            if (HapticPolicy.PatternPulses(stylesWithGapsMs, ReducedMotion) > 1)
            {
                int slot = NativePatternSlot(stylesWithGapsMs);
                if (slot >= 0)
                {
                    sequencer.Start(stylesWithGapsMs, false);
                    sequencerMuted = true;
                    PlaySlot(slot, HapticPolicy.Levels - 1, 0);
                    Pump();
                    return;
                }
            }
            sequencer.Start(stylesWithGapsMs, ReducedMotion);
            sequencerMuted = false;
            Pump();
        }

        /// <summary>Advances a running pattern by real (unscaled) seconds. Call once per frame.</summary>
        public static void Tick(float unscaledDt)
        {
            if (!sequencer.Active) return;
            if (!enabled) { sequencer.Cancel(); return; }
            sequencer.Advance(unscaledDt * 1000.0);
            Pump();
        }

        public static void CancelPattern() { sequencer.Cancel(); }

        public static bool PatternActive { get { return sequencer.Active; } }

        /// <summary>Stops anything playing or queued (toggle off).</summary>
        public static void StopAll()
        {
            sequencer.Cancel();
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_StopAll();
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (bridge != null) { try { bridge.CallStatic("cancel"); } catch (System.Exception) { } }
#endif
        }

        /// <summary>Which backend is live, for the haptics lab and Status.</summary>
        public static string Backend
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                int c = PawHaptics_Caps();
                if ((c & 1) == 0) return "iOS UIKit (no Core Haptics)";
                return "iOS CoreHaptics " + ((c & 2) != 0 ? "running" : ((c & 4) != 0 ? "starting" : "stopped, UIKit fallback")) + " resets=" + (c >> 8);
#elif UNITY_ANDROID && !UNITY_EDITOR
                if (!AndroidInit()) return "Android: no vibrator";
                return "Android API " + androidApi + ((androidCaps & CapAmplitude) != 0 ? " amp" : " on/off") +
                       ((androidCaps & CapPrimitives) != 0 ? " primitives" : "") + (systemAllows ? "" : " TOUCH FEEDBACK OFF");
#elif UNITY_EDITOR
                return "Editor (log only)";
#else
                return "none";
#endif
            }
        }

        // ------------------------------------------------------------------ internals

        private static void Pump()
        {
            int code;
            while ((code = sequencer.Poll()) >= 0)
            {
                if (sequencerMuted) continue;
                if (HapticStyle.IsNotify(code)) Notify(HapticStyle.NotifyType(code));
                else Impact(code);
            }
        }

        /// <summary>Preset slot for a multi-pulse impact pattern, registered at first use; -1 = keep the sequencer.</summary>
        private static int NativePatternSlot(int[] p)
        {
#if (UNITY_IOS || UNITY_ANDROID) && !UNITY_EDITOR
            for (int i = 0; i < patternCount; i++) if (ReferenceEquals(patternRefs[i], p)) return patternSlot[i];
            if (patternCount >= HapticPreset.PatternSlots) return -1;
            var data = HapticPresets.FromPattern(p);
            int n = patternCount++;
            patternRefs[n] = p;
            patternData[n] = data;
            patternSlot[n] = data != null ? HapticPreset.PatternBase + n : -1;
            return patternSlot[n];
#else
            return -1;
#endif
        }

        private static float[] SlotData(int slot)
        {
            if (slot >= HapticPreset.FirstOnlyOffset)
            {
                int b = slot - HapticPreset.FirstOnlyOffset;
                if (b < 0 || b >= HapticPreset.FirstOnlyOffset) return null;
                if (firstOnlyData[b] == null) firstOnlyData[b] = HapticPresets.FirstEventOnly(SlotData(b));
                return firstOnlyData[b];
            }
            if (slot < HapticPreset.BuiltInCount) return HapticPresets.Get(slot);
            int p = slot - HapticPreset.PatternBase;
            return p >= 0 && p < patternCount ? patternData[p] : null;
        }

        private static string SlotName(int slot)
        {
            bool first = slot >= HapticPreset.FirstOnlyOffset;
            int b = first ? slot - HapticPreset.FirstOnlyOffset : slot;
            string n = b < HapticPreset.BuiltInCount ? HapticPresets.Name(b) : "pattern " + (b - HapticPreset.PatternBase);
            return first ? n + " (first only)" : n;
        }

        private static string DelayText(double s)
        {
            return s > 0 ? " in " + (s * 1000.0).ToString("0.0") + " ms" : "";
        }

        /// <summary>What Prepare precompiles: every built-in at full scale, the heartbeat levels the frenzy can reach,
        /// the multi-pulse stamps, and the Reduced Motion variants when it is on.</summary>
        private static void WarmList()
        {
            int top = HapticPolicy.Levels - 1;
            for (int id = 0; id < HapticPreset.BuiltInCount; id++)
            {
                Warm(id, top);
                if (ReducedMotion && HapticPresets.IsRhythmic(id)) Warm(id + HapticPreset.FirstOnlyOffset, top);
            }
            int lo = HapticPolicy.Level(HapticPolicy.HeartMin), hi = HapticPolicy.Level(HapticPolicy.HeartMax);
            int hb = ReducedMotion ? HapticPreset.Heartbeat + HapticPreset.FirstOnlyOffset : HapticPreset.Heartbeat;
            for (int l = lo; l <= hi; l++) Warm(hb, l);
            int s;
            if ((s = NativePatternSlot(StampHaptics.Lightning)) >= 0) Warm(s, top);
            if ((s = NativePatternSlot(StampHaptics.Perfect)) >= 0) Warm(s, top);
        }

        private static void PlaySlot(int slot, int level, double delaySeconds)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (!EnsureRegistered(slot)) return;
            PawHaptics_PlayPreset(slot, HapticPolicy.FromLevel(level), delaySeconds);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (!AndroidInit()) return;
            int key = slot * HapticPolicy.Levels + level;
            if (!androidRegistered[key] && !AndroidRegister(slot, level)) return;
            AndroidCall2(midPlay, key, HapticPolicy.DelayMs(delaySeconds));
#elif UNITY_EDITOR
            if (EditorLog) Debug.Log("[Haptics] " + SlotName(slot) + " x" + HapticPolicy.FromLevel(level).ToString("0.00") + DelayText(delaySeconds));
#endif
        }

        private static void Warm(int slot, int level)
        {
#if UNITY_IOS && !UNITY_EDITOR
            if (EnsureRegistered(slot)) PawHaptics_Warm(slot, level);
#elif UNITY_ANDROID && !UNITY_EDITOR
            int key = slot * HapticPolicy.Levels + level;
            if (!androidRegistered[key]) AndroidRegister(slot, level);
#endif
        }

        // ------------------------------------------------------------------ iOS

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void PawHaptics_Prepare();
        [DllImport("__Internal")] private static extern void PawHaptics_Impact(int style);
        [DllImport("__Internal")] private static extern void PawHaptics_Notify(int type);
        [DllImport("__Internal")] private static extern void PawHaptics_RegisterPreset(int slot, float[] data, int count);
        [DllImport("__Internal")] private static extern void PawHaptics_Warm(int slot, int level);
        [DllImport("__Internal")] private static extern void PawHaptics_PlayPreset(int slot, float scale, double delay);
        [DllImport("__Internal")] private static extern void PawHaptics_Transient(float intensity, float sharpness, double delay);
        [DllImport("__Internal")] private static extern void PawHaptics_Continuous(float duration, float intensity, float sharpness, double delay);
        [DllImport("__Internal")] private static extern void PawHaptics_StopAll();
        [DllImport("__Internal")] private static extern int PawHaptics_Caps();

        private static readonly bool[] iosRegistered = new bool[HapticPreset.TotalSlots];

        private static bool EnsureRegistered(int slot)
        {
            if (slot < 0 || slot >= HapticPreset.TotalSlots) return false;
            if (iosRegistered[slot]) return true;
            var d = SlotData(slot);
            if (d == null || d.Length < HapticPresets.Stride) return false;
            PawHaptics_RegisterPreset(slot, d, d.Length);
            iosRegistered[slot] = true;
            return true;
        }
#endif

        // ------------------------------------------------------------------ Android

#if UNITY_ANDROID && !UNITY_EDITOR
        private const int CapVibrator = 1, CapAmplitude = 2, CapPrimitives = 4, CapSystemOn = 8;
        private static AndroidJavaClass bridge;
        private static System.IntPtr cls, midPlay, midTransient, midContinuous, midPrepare;
        private static readonly jvalue[] args0 = new jvalue[0];
        private static readonly jvalue[] args2 = new jvalue[2];
        private static readonly jvalue[] args3 = new jvalue[3];
        private static bool androidTried;
        private static int androidCaps;
        private static int androidApi;
        private static readonly bool[] androidRegistered = new bool[HapticPreset.TotalSlots * HapticPolicy.Levels];
        // Never true at runtime; referencing Handheld.Vibrate makes Unity add the VIBRATE permission.
        private static bool neverTrue = System.DateTime.Now.Year < 1900;

        private static bool AndroidInit()
        {
            if (androidTried) return bridge != null && (androidCaps & CapVibrator) != 0;
            androidTried = true;
            if (neverTrue) Handheld.Vibrate();
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
                {
                    bridge = new AndroidJavaClass("com.pawnoon.haptics.PawHapticsBridge");
                    androidCaps = bridge.CallStatic<int>("init", activity);
                    androidApi = bridge.CallStatic<int>("apiLevel");
                }
                cls = bridge.GetRawClass();
                midPlay = AndroidJNIHelper.GetMethodID(cls, "playPreset", "(II)V", true);
                midTransient = AndroidJNIHelper.GetMethodID(cls, "transientFx", "(III)V", true);
                midContinuous = AndroidJNIHelper.GetMethodID(cls, "continuousFx", "(III)V", true);
                midPrepare = AndroidJNIHelper.GetMethodID(cls, "prepare", "()Z", true);
                systemAllows = (androidCaps & CapSystemOn) != 0;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] Android haptics unavailable: " + e.Message);
                bridge = null;
                androidCaps = 0;
            }
            return bridge != null && (androidCaps & CapVibrator) != 0;
        }

        private static bool AndroidPrepare()
        {
            return AndroidJNI.CallStaticBooleanMethod(cls, midPrepare, args0);
        }

        private static void AndroidCall2(System.IntPtr mid, int a, int b)
        {
            args2[0].i = a;
            args2[1].i = b;
            AndroidJNI.CallStaticVoidMethod(cls, mid, args2);
        }

        private static void AndroidCall3(System.IntPtr mid, int a, int b, int c)
        {
            args3[0].i = a;
            args3[1].i = b;
            args3[2].i = c;
            AndroidJNI.CallStaticVoidMethod(cls, mid, args3);
        }

        /// <summary>One-time compile of (slot, level) into a cached VibrationEffect (allocates; Prepare warms the common ones).</summary>
        private static bool AndroidRegister(int slot, int level)
        {
            var d = SlotData(slot);
            if (d == null || bridge == null) return false;
            float scale = HapticPolicy.FromLevel(level);
            long[] timings;
            int[] amps;
            int startMs;
            HapticRaster.Build(d, scale, (androidCaps & CapAmplitude) != 0, out timings, out amps, out startMs);
            float[] tr = HapticRaster.Transients(d, scale) ?? new float[0];
            for (int i = 0; i < tr.Length; i += 3) tr[i] = Mathf.Max(0f, tr[i] - startMs);
            try
            {
                bridge.CallStatic("registerPreset", slot, level, timings, amps, tr, startMs);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] haptic preset " + slot + " failed: " + e.Message);
                return false;
            }
            androidRegistered[slot * HapticPolicy.Levels + level] = true;
            return true;
        }
#endif
    }
}
