using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Presentation time only (animation, tweens, particles). Slow-mo, hit-stop and the review time
    /// scale live here. Duel timing NEVER reads this: it uses the Stopwatch clock (PawNoon.Core).
    /// Time.timeScale is never touched.
    /// </summary>
    public static class PawTime
    {
        /// <summary>Scaled presentation delta (0 during hit-stop).</summary>
        public static float Dt;
        /// <summary>Delta for ambient motion (clouds, props, motes); also frozen by the DRAW freeze.</summary>
        public static float AmbientDt;
        /// <summary>Real delta (clamped), for UI and timers that must ignore slow-mo.</summary>
        public static float UnscaledDt;
        public static float T;
        public static float AmbientT;
        public static float UnscaledT;
        public static int Frame;

        /// <summary>Set by ReviewDirector.SetTimeScale. Presentation only.</summary>
        public static float ReviewScale = 1f;

        private static float slowMoScale = 1f;
        private static float slowMoLeft;
        private static float hitStopLeft;
        private static int ambientFreezeFrames;
        private static float ambientScale = 1f;
        private static float ambientTarget = 1f;
        private static float ambientEaseSeconds = 0.5f;

        public static bool InHitStop { get { return hitStopLeft > 0f; } }
        public static bool InSlowMo { get { return slowMoLeft > 0f; } }

        /// <summary>Seconds over which slow-mo ramps back to 1x at its end (no snap back to full speed).</summary>
        public const float SlowMoExitRamp = 0.18f;

        /// <summary>
        /// Current slow-mo factor (1 outside slow-mo; eases back to 1 over the last SlowMoExitRamp seconds).
        /// Audio can follow it for a pitch drop on the killing blow.
        /// </summary>
        public static float SlowMoFactor
        {
            get
            {
                if (slowMoLeft <= 0f) return 1f;
                if (slowMoLeft >= SlowMoExitRamp) return slowMoScale;
                return Mathf.Lerp(1f, slowMoScale, Ease.OutQuad(slowMoLeft / SlowMoExitRamp));
            }
        }

        public static void Begin(float realDt)
        {
            if (realDt > 0.1f) realDt = 0.1f;
            if (realDt < 0f) realDt = 0f;
            UnscaledDt = realDt;
            UnscaledT += realDt;
            Frame++;

            float scale = ReviewScale;
            if (slowMoLeft > 0f)
            {
                slowMoLeft -= realDt;
                scale *= SlowMoFactor;
            }
            if (hitStopLeft > 0f)
            {
                hitStopLeft -= realDt;
                scale = 0f;
            }
            Dt = realDt * scale;
            T += Dt;

            if (ambientFreezeFrames > 0)
            {
                ambientFreezeFrames--;
                AmbientDt = 0f;
            }
            else
            {
                if (ambientScale != ambientTarget)
                    ambientScale = Mathf.MoveTowards(ambientScale, ambientTarget, ambientEaseSeconds > 0f ? realDt / ambientEaseSeconds : 1f);
                AmbientDt = Dt * ambientScale;
            }
            AmbientT += AmbientDt;
        }

        public static void HitStop(float seconds)
        {
            if (seconds > hitStopLeft) hitStopLeft = seconds;
        }

        public static void SlowMo(float scale, float seconds)
        {
            slowMoScale = Mathf.Clamp(scale, 0.01f, 1f);
            slowMoLeft = seconds;
        }

        public static void FreezeAmbient(int frames)
        {
            if (frames > ambientFreezeFrames) ambientFreezeFrames = frames;
        }

        /// <summary>
        /// Eases ambient motion (clouds, props, motes, shimmer) toward a scale over easeSeconds (real time).
        /// The duel hold uses 0 so the whole street holds its breath.
        /// </summary>
        public static void HoldAmbient(float scale, float easeSeconds)
        {
            ambientTarget = Mathf.Clamp01(scale);
            ambientEaseSeconds = Mathf.Max(0f, easeSeconds);
        }

        /// <summary>Ambient motion back to normal at once (DRAW).</summary>
        public static void ReleaseAmbient()
        {
            ambientTarget = 1f;
            ambientScale = 1f;
        }

        public static bool AmbientHeld { get { return ambientTarget < 1f; } }

        public static void ClearEffects()
        {
            ambientTarget = 1f;
            ambientScale = 1f;
            slowMoLeft = 0f;
            hitStopLeft = 0f;
            ambientFreezeFrames = 0;
            slowMoScale = 1f;
        }
    }

    /// <summary>Easing curves (t in 0..1).</summary>
    public static class Ease
    {
        public static float Clamp01(float t) { return t < 0f ? 0f : (t > 1f ? 1f : t); }
        public static float OutCubic(float t) { t = Clamp01(t) - 1f; return t * t * t + 1f; }
        public static float InCubic(float t) { t = Clamp01(t); return t * t * t; }
        public static float InOutSine(float t) { t = Clamp01(t); return -(Mathf.Cos(Mathf.PI * t) - 1f) * 0.5f; }
        public static float OutQuad(float t) { t = Clamp01(t); return 1f - (1f - t) * (1f - t); }

        public static float OutBack(float t, float s = 1.70158f)
        {
            t = Clamp01(t) - 1f;
            return t * t * ((s + 1f) * t + s) + 1f;
        }

        public static float OutBounce(float t)
        {
            t = Clamp01(t);
            const float n1 = 7.5625f, d1 = 2.75f;
            if (t < 1f / d1) return n1 * t * t;
            if (t < 2f / d1) { t -= 1.5f / d1; return n1 * t * t + 0.75f; }
            if (t < 2.5f / d1) { t -= 2.25f / d1; return n1 * t * t + 0.9375f; }
            t -= 2.625f / d1;
            return n1 * t * t + 0.984375f;
        }

        /// <summary>Damped "pop" offset: starts at amplitude, oscillates, settles to 0.</summary>
        public static float Pop(float t, float amplitude, float freq = 3f, float decay = 6f)
        {
            if (t < 0f) return amplitude;
            return amplitude * Mathf.Exp(-decay * t) * Mathf.Cos(freq * 2f * Mathf.PI * t);
        }

        /// <summary>Exponential smoothing factor independent of frame rate.</summary>
        public static float Damp(float sharpness, float dt)
        {
            return 1f - Mathf.Exp(-sharpness * dt);
        }
    }
}
