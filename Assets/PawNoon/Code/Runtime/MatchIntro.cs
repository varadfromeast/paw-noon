using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Match intro (SPEC_V2 §8.7), presentation only. On the first match of a session it starts from the title
    /// positions (both cats are on screen there, so nothing may jump): the hero stays and settles into his stance
    /// (2 px, 1 px, rest), the opponent steps out past the right edge and walks back in to his mark (or just
    /// settles, feel_config "introOpponent": "stay"), then the two name plates slam down onto the HUD with the
    /// stamp sfx, then the normal standoff runs. Timeline and offsets: <see cref="IntroMath"/> (idle frames,
    /// whole pixels, 12 steps/s).
    /// Tap to skip. Owned and ticked by <see cref="DuelDirector"/> while it is in Standoff; it never touches the
    /// authority, the round schedule or input timing (the countdown simply has not started yet).
    /// Reduced Motion: no slides, the plates just appear (with the stamp) and the intro is shorter.
    /// </summary>
    public sealed class MatchIntro
    {
        // ---- session / review switches
        /// <summary>Set after the intro played (or was skipped) once; the intro is a first-match-of-session beat.</summary>
        public static bool ShownThisSession;
        /// <summary>ReviewDirector.SetIntro: 0 auto (default), 1 always on (every match, review runs included), -1 off.</summary>
        public static int ReviewMode;
        /// <summary>Set by ReviewDirector.StartDuel: the next match is an automated run, so no intro (unless ReviewMode = 1).</summary>
        public static bool SkipNextForReview;

        /// <summary>Statics survive play-mode entry when domain reload is disabled: start every session clean.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            ShownThisSession = false;
            ReviewMode = 0;
            SkipNextForReview = false;
        }

        public static string StatusText
        {
            get { return (ReviewMode > 0 ? "on" : (ReviewMode < 0 ? "off" : "auto")) + (ShownThisSession ? "(shown)" : ""); }
        }

        // ---- timeline: IntroMath (seconds, offsets)
        private const float SkipAfter = 0.15f;      // ignore the press that started the duel
        /// <summary>Slam: plate y offset per 40 ms step (drop in, 1 px overshoot, rest).</summary>
        private static readonly int[] Slam = { -7, -2, 1 };
        private const float SlamStep = 0.04f;

        private readonly DuelActor hero;
        private readonly DuelActor opp;
        private readonly DuelHud hud;
        private readonly DuelCues cues;
        private readonly PawAudio audio;
        private readonly PixelPipeline pipe;
        private SpriteRenderer oppShadow;
        private Vector2Int shadowAt;

        private bool active;
        private bool reduced;
        private bool hintAfter;
        private float t;
        private int exitDx;
        private bool oppWalks;
        private bool heroSettled;
        private int lastHeroDy = int.MinValue, lastOppDx = int.MinValue, lastOppDy = int.MinValue;
        private readonly bool[] slammed = new bool[2];

        public MatchIntro(DuelActor hero, DuelActor opponent, DuelHud hud, DuelCues cues, PawAudio audio, PixelPipeline pipe)
        {
            this.hero = hero;
            opp = opponent;
            this.hud = hud;
            this.cues = cues;
            this.audio = audio;
            this.pipe = pipe;
        }

        public bool Active { get { return active; } }

        /// <summary>Optional: the opponent's ground shadow (a separate sprite) slides with him.</summary>
        public void SetShadow(SpriteRenderer shadow, Vector2Int canvasAt)
        {
            oppShadow = shadow;
            shadowAt = canvasAt;
        }

        /// <summary>Whether the next match opens with the intro. Consumes the review-run skip flag.</summary>
        public static bool ShouldPlay(bool cfgEnabled, bool autoTapping)
        {
            bool reviewRun = SkipNextForReview;
            SkipNextForReview = false;
            if (ReviewMode < 0) return false;
            if (ReviewMode > 0) return true;
            return cfgEnabled && !ShownThisSession && !reviewRun && !autoTapping;
        }

        private int heroTitleFrame = -1, oppTitleFrame = -1;

        /// <summary>
        /// Call before the standoff is set up (it switches both cats to "ready"): remembers the idle frame each cat
        /// shows on the title so <see cref="Begin"/> continues the same loop on the same frame.
        /// </summary>
        public void CaptureTitlePose()
        {
            heroTitleFrame = hero.IsPlaying("idle") ? hero.Anim.Frame : -1;
            oppTitleFrame = opp.IsPlaying("idle") ? opp.Anim.Frame : -1;
        }

        /// <summary>Starts the intro (call right after the standoff was set up). hintAfter = show the swipe tutorial when done.</summary>
        public void Begin(bool reducedMotion, bool hintAfter)
        {
            active = true;
            reduced = reducedMotion;
            this.hintAfter = hintAfter;
            t = 0f;
            slammed[0] = slammed[1] = false;
            ShownThisSession = true;

            // start from where the title left them (rest marks, idle): frame 0 has every offset at 0
            exitDx = IntroMath.ExitDistance(pipe.Visible.xMax, opp.Anchor.x - opp.Def.Pivot.x);
            oppWalks = FeelConfig.Current.IntroOpponentWalks;
            heroSettled = false;
            lastHeroDy = lastOppDx = lastOppDy = int.MinValue;

            // keep the title's idle loops on the frame they showed (no pose pop on the DUEL tap)
            if (heroTitleFrame >= 0) hero.Play("idle", heroTitleFrame); else hero.Loop("idle");
            if (oppTitleFrame >= 0) opp.Play("idle", oppTitleFrame); else opp.Loop("idle");
            heroTitleFrame = oppTitleFrame = -1;
            cues.ShowGetReady(false);
            hud.ShowHint(false);
            hud.SetNamePlate(0, false, 0);
            hud.SetNamePlate(1, false, 0);
            Apply();
        }

        /// <summary>Advances the intro. Returns true while it is still running (the standoff timer waits).</summary>
        public bool Tick(float unscaledDt)
        {
            if (!active) return false;
            if (t >= SkipAfter && UiPointer.Down && !UiPointer.Consumed)
            {
                UiPointer.Consumed = true;
                Finish();
                return false;
            }
            t += unscaledDt;
            Apply();
            if (t >= IntroMath.Duration(reduced)) { Finish(); return false; }
            return true;
        }

        /// <summary>Stops without the end beat (director Stop / review): everything back at rest.</summary>
        public void Cancel()
        {
            if (!active) return;
            active = false;
            Rest();
        }

        private void Finish()
        {
            active = false;
            Rest();
            hero.Loop("ready");
            opp.Play("ready");
            cues.ShowGetReady(true);
            if (hintAfter) hud.ShowHint(true);
        }

        private void Rest()
        {
            hero.Offset = Vector2.zero;
            opp.Offset = Vector2.zero;
            lastHeroDy = lastOppDx = lastOppDy = 0;
            if (oppShadow != null) PawGfx.Place(oppShadow, shadowAt.x, shadowAt.y);
            hud.SetNamePlate(0, true, 0);
            hud.SetNamePlate(1, true, 0);
        }

        private void Apply()
        {
            var p = IntroMath.Sample(t, reduced, oppWalks, exitDx);
            // only touch transforms when a stepped value changes (12 Hz motion, no per-frame work otherwise)
            if (p.heroDy != lastHeroDy)
            {
                lastHeroDy = p.heroDy;
                hero.Offset = new Vector2(0f, p.heroDy);
            }
            if (p.oppDx != lastOppDx || p.oppDy != lastOppDy)
            {
                lastOppDx = p.oppDx;
                lastOppDy = p.oppDy;
                opp.Offset = new Vector2(p.oppDx, p.oppDy);
                // the ground shadow slides with him but stays on the ground (no bob)
                if (oppShadow != null) PawGfx.Place(oppShadow, shadowAt.x + p.oppDx, shadowAt.y);
            }
            if (p.heroReady && !heroSettled)
            {
                heroSettled = true;
                hero.Loop("ready");   // settled into his stance
            }
            Plate(0, IntroMath.HeroPlate(reduced));
            Plate(1, IntroMath.OppPlate(reduced));
        }

        private void Plate(int slot, float at)
        {
            if (t < at) { hud.SetNamePlate(slot, false, 0); return; }
            int step = Mathf.FloorToInt((t - at) / SlamStep);
            int dy = reduced || step >= Slam.Length ? 0 : Slam[step];
            hud.SetNamePlate(slot, true, dy);
            if (!slammed[slot])
            {
                slammed[slot] = true;
                audio.Play("sfx_stamp", slot == 0 ? 0.7f : 0.8f, slot == 0 ? 1.06f : 0.94f);
                HapticMoments.NamePlate(slot);
                if (!reduced) pipe.AddShake(1f);
            }
        }
    }
}
