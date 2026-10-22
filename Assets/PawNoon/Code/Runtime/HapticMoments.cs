using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Every haptic moment of the game by name (inventory: Docs/HAPTICS.md). The director, the intro and the
    /// haptics lab call these, so what Varad feels in the lab is exactly what the duel plays. Presentation only:
    /// nothing here reads or changes timing, reports or outcomes. No allocations.
    /// </summary>
    public static class HapticMoments
    {
        /// <summary>Countdown numeral 3 / 2 / 1 (grows with the tick volume).</summary>
        public static void CountdownTick(int numeral)
        {
            PawHaptics.Preset(numeral >= 3 ? HapticPreset.CountTick3 : (numeral == 2 ? HapticPreset.CountTick2 : HapticPreset.CountTick1));
        }

        /// <summary>Hold heart monitor R peak: lub-dub, silent until frenzy 0.35, then 0.3..0.8.</summary>
        public static void HoldBeat(float frenzy, bool skipped) { PawHaptics.Heartbeat(frenzy, skipped); }

        /// <summary>DRAW: scheduled to the estimated photon time with the DRAW sound's own lead (seconds ahead).</summary>
        public static void DrawCue(double leadSeconds) { PawHaptics.ScheduleDrawCue(leadSeconds); }

        /// <summary>The swipe registered (gun clears leather); the recoil follows at the fire frame.</summary>
        public static void Swipe() { PawHaptics.Preset(HapticPreset.Swipe); }

        /// <summary>Hero's gun fires (fire frame of draw_fire).</summary>
        public static void HeroShot() { PawHaptics.Preset(HapticPreset.Recoil); }

        /// <summary>Your bullet lands on the bandit (a killing shot rumbles under the slow-mo).</summary>
        public static void HitDealt(bool kill) { PawHaptics.Preset(kill ? HapticPreset.KillDealt : HapticPreset.HitDealt); }

        /// <summary>You are hit (killed = a long dull fade).</summary>
        public static void HitTaken(bool killed) { PawHaptics.Preset(killed ? HapticPreset.KilledYou : HapticPreset.HitTaken); }

        /// <summary>A downed cat hits the ground.</summary>
        public static void BodyFall() { PawHaptics.Preset(HapticPreset.BodyFall); }

        /// <summary>You drew too early: the jam click.</summary>
        public static void Foul() { PawHaptics.Preset(HapticPreset.Misfire); }

        /// <summary>TOO CLOSE: both shots inside the tie window.</summary>
        public static void Tie() { PawHaptics.Preset(HapticPreset.Tie); }

        /// <summary>LAST LIGHT: the monitor spike after the flat line.</summary>
        public static void LastLightSpike() { PawHaptics.Preset(HapticPreset.LastLightSpike); }

        /// <summary>Match intro name plate slams onto the HUD (opponent's a touch harder).</summary>
        public static void NamePlate(int slot) { PawHaptics.Preset(HapticPreset.NamePlate, slot == 0 ? 0.9f : 1f); }

        /// <summary>Round-result stamp lands: the stamp language (null = no stamp: a medium tap).</summary>
        public static void Stamp(int[] pattern)
        {
            if (pattern != null) PawHaptics.Pattern(pattern);
            else PawHaptics.Impact(HapticStyle.Medium);
        }

        /// <summary>Seconds from the match screen opening to the banner landing (MatchResultScreen.BannerDrop:
        /// two 0.05 s steps; Reduced Motion shows it at once).</summary>
        public const double BannerLandSeconds = 0.10;

        /// <summary>Match over, as the match screen banner lands. A double KO (standoff) reads as a loss for the hands.</summary>
        public static void MatchEnd(bool won, bool reducedMotion)
        {
            PawHaptics.Preset(won ? HapticPreset.Win : HapticPreset.Lose, 1f, reducedMotion ? 0 : BannerLandSeconds);
        }

        /// <summary>
        /// Win streak tag pops on the round card (x2, x3...): two rising taps matched to sfx_streak's two notes (90 ms
        /// apart), a little firmer each step of the run. Reduced Motion: the first tap only.
        /// </summary>
        public static void StreakStep(int streak)
        {
            int k = streak < 2 ? 0 : (streak > 6 ? 4 : streak - 2);
            PawHaptics.Transient(0.45f + 0.05f * k, 0.55f);
            if (!PawHaptics.ReducedMotion) PawHaptics.Transient(0.65f + 0.05f * k, 0.80f, 0.09);   // 0.65 .. 0.85
        }

        /// <summary>
        /// NEW BEST ribbon + confetti: three quick rising sparkle taps, then a soft swell after the last one (no transient
        /// inside the continuous window, see HapticPresets). Reduced Motion: the first tap only.
        /// </summary>
        public static void NewBest()
        {
            PawHaptics.Transient(0.50f, 0.90f);
            if (PawHaptics.ReducedMotion) return;
            PawHaptics.Transient(0.70f, 0.90f, 0.07);
            PawHaptics.Transient(0.85f, 0.90f, 0.14);   // under DRAW's 1.0 / 1.0: DRAW stays the sharpest hit
            PawHaptics.Continuous(0.25f, 0.35f, 0.30f, 0.16);
        }

        /// <summary>UI button press (PixelButton). Settings / difficulty toggles keep PawHaptics.Impact(0), a touch fuller.</summary>
        public static void UiTap() { PawHaptics.Preset(HapticPreset.UiTap); }
    }
}
