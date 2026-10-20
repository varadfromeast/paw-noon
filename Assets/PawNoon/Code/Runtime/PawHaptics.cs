using PawNoon.Core;
using UnityEngine;
#if UNITY_IOS && !UNITY_EDITOR
using System.Runtime.InteropServices;
#endif

namespace PawNoon.Runtime
{
    /// <summary>
    /// Haptics: iOS UIFeedbackGenerators via Plugins/iOS/PawHaptics.mm, Android VibrationEffect
    /// (API 26+), no-op elsewhere. Styles: 0 light, 1 medium, 2 heavy. Notify: 0 success, 1 warning, 2 error.
    /// Timed patterns (SPEC_V2 §8.6 stamp language): <see cref="Pattern"/> starts one, the duel director's
    /// frame tick advances it (<see cref="Tick"/>); no coroutines, no allocations.
    /// </summary>
    public static class PawHaptics
    {
        /// <summary>Settings toggle (Haptics on/off). Off = every call is a no-op, patterns included.</summary>
        public static bool Enabled = true;
        /// <summary>Reduced Motion: patterns collapse to their first pulse (one tap, no rhythm).</summary>
        public static bool ReducedMotion;

        private static readonly HapticSequencer sequencer = new HapticSequencer();

        /// <summary>
        /// Plays { pulse, gapMs, pulse, gapMs, pulse ... } (pulse codes in <see cref="HapticStyle"/>: 0..2 impact,
        /// 10..12 notify). The first pulse fires now; the rest fire from <see cref="Tick"/>. Replaces a running pattern.
        /// </summary>
        public static void Pattern(int[] stylesWithGapsMs)
        {
            if (!Enabled || stylesWithGapsMs == null || stylesWithGapsMs.Length == 0)
            {
                sequencer.Cancel();
                return;
            }
            sequencer.Start(stylesWithGapsMs, ReducedMotion);
            Pump();
        }

        /// <summary>Advances a running pattern by real (unscaled) seconds. Call once per frame.</summary>
        public static void Tick(float unscaledDt)
        {
            if (!sequencer.Active) return;
            if (!Enabled) { sequencer.Cancel(); return; }
            sequencer.Advance(unscaledDt * 1000.0);
            Pump();
        }

        public static void CancelPattern() { sequencer.Cancel(); }

        public static bool PatternActive { get { return sequencer.Active; } }

        private static void Pump()
        {
            int code;
            while ((code = sequencer.Poll()) >= 0)
            {
                if (HapticStyle.IsNotify(code)) Notify(HapticStyle.NotifyType(code));
                else Impact(code);
            }
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void PawHaptics_Prepare();
        [DllImport("__Internal")] private static extern void PawHaptics_Impact(int style);
        [DllImport("__Internal")] private static extern void PawHaptics_Notify(int type);
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaObject vibrator;
        private static AndroidJavaClass effectClass;
        private static bool androidInit;
        // Never true at runtime; referencing Handheld.Vibrate makes Unity add the VIBRATE permission.
        private static bool neverTrue = System.DateTime.Now.Year < 1900;

        private static void AndroidInit()
        {
            if (androidInit) return;
            androidInit = true;
            if (neverTrue) Handheld.Vibrate();
            try
            {
                using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
                    vibrator = activity.Call<AndroidJavaObject>("getSystemService", "vibrator");
                }
                effectClass = new AndroidJavaClass("android.os.VibrationEffect");
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] Android vibrator unavailable: " + e.Message);
                vibrator = null;
            }
        }

        private static void AndroidVibrate(long ms, int amplitude)
        {
            AndroidInit();
            if (vibrator == null || effectClass == null) return;
            try
            {
                using (var effect = effectClass.CallStatic<AndroidJavaObject>("createOneShot", ms, amplitude))
                {
                    vibrator.Call("vibrate", effect);
                }
            }
            catch (System.Exception) { }
        }
#endif

        public static void Prepare()
        {
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Prepare();
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidInit();
#endif
        }

        public static void Impact(int style)
        {
            if (!Enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Impact(style);
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidVibrate(style >= 2 ? 40 : (style == 1 ? 22 : 12), style >= 2 ? 255 : (style == 1 ? 170 : 90));
#endif
        }

        public static void Notify(int type)
        {
            if (!Enabled) return;
#if UNITY_IOS && !UNITY_EDITOR
            PawHaptics_Notify(type);
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidVibrate(type == 0 ? 30 : 55, type == 0 ? 200 : 140);
#endif
        }
    }
}
