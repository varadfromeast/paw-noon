using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Match intro (SPEC_V2 §8.7), presentation only. On the first match of a session the opponent slides in from
    /// the right and the hero from the bottom-left (idle frames, whole-pixel offsets, stepped ease at 12 steps/s),
    /// then the two name plates slam down onto the HUD with the stamp sfx, then the normal standoff runs.
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

        // ---- timeline (seconds)
        private const float StepHz = 12f;          // stepped motion: positions update 12 times a second
        private const float OppFrom = 0.00f, OppTo = 0.85f;
        private const float HeroFrom = 0.15f, HeroTo = 1.05f;
        private const float HeroPlateAt = 1.10f, OppPlateAt = 1.24f;
        private const float Total = 1.60f;
        private const float RmHeroPlateAt = 0.20f, RmOppPlateAt = 0.40f, RmTotal = 0.90f;
        private const float SkipAfter = 0.15f;      // ignore the press that started the duel
        private const int HeroDrop = 40;            // the hero also rises in from below (px)
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
        private int heroDx, heroDy, oppDx;
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

        /// <summary>Starts the intro (call right after the standoff was set up). hintAfter = show the swipe tutorial when done.</summary>
        public void Begin(bool reducedMotion, bool hintAfter)
        {
            active = true;
            reduced = reducedMotion;
            this.hintAfter = hintAfter;
            t = 0f;
            slammed[0] = slammed[1] = false;
            ShownThisSession = true;

            var vis = pipe.Visible;
            // fully off the visible rect: opponent past the right edge, hero past the left edge (and a little low)
            oppDx = reduced ? 0 : Mathf.Max(0, vis.xMax - (opp.Anchor.x - opp.Def.Pivot.x)) + 2;
            heroDx = reduced ? 0 : -(Mathf.Max(0, hero.Anchor.x - hero.Def.Pivot.x + hero.Def.Cell.x - vis.x) + 2);
            heroDy = reduced ? 0 : HeroDrop;

            hero.Play("idle");
            opp.Play("idle");
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
            if (t >= (reduced ? RmTotal : Total)) { Finish(); return false; }
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
            hero.Play("ready");
            opp.Play("ready");
            cues.ShowGetReady(true);
            if (hintAfter) hud.ShowHint(true);
        }

        private void Rest()
        {
            hero.Offset = Vector2.zero;
            opp.Offset = Vector2.zero;
            if (oppShadow != null) PawGfx.Place(oppShadow, shadowAt.x, shadowAt.y);
            hud.SetNamePlate(0, true, 0);
            hud.SetNamePlate(1, true, 0);
        }

        private void Apply()
        {
            float o = 1f - Progress(t, OppFrom, OppTo);
            float h = 1f - Progress(t, HeroFrom, HeroTo);
            int ox = Mathf.RoundToInt(oppDx * o);
            hero.Offset = new Vector2(Mathf.RoundToInt(heroDx * h), Mathf.RoundToInt(heroDy * h));
            opp.Offset = new Vector2(ox, 0f);
            if (oppShadow != null) PawGfx.Place(oppShadow, shadowAt.x + ox, shadowAt.y);
            Plate(0, reduced ? RmHeroPlateAt : HeroPlateAt);
            Plate(1, reduced ? RmOppPlateAt : OppPlateAt);
        }

        /// <summary>Stepped ease-out: time quantised to StepHz, cubic ease, whole pixels downstream.</summary>
        private static float Progress(float time, float from, float to)
        {
            if (time <= from) return 0f;
            if (time >= to) return 1f;
            float steps = Mathf.Max(1f, Mathf.Round((to - from) * StepHz));
            float k = Mathf.Floor((time - from) / (to - from) * steps) / steps;
            return Ease.OutCubic(k);
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
