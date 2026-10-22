using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The seam for the first-run tutorial (Runtime/Tutorial/TutorialDirector.cs, Docs/TUTORIAL.md). The tutorial
    /// owns its step machine, card and private player client; the director only lends it the stage: the same cats,
    /// holster, cues, hold presentation (EnterHold), DRAW presentation (PresentDraw) and hero draw / jam. No
    /// authority round runs and the opponent never shoots (Pres.Tutorial skips the authority and opponent ticks).
    /// </summary>
    public sealed partial class DuelDirector
    {
        private TutorialDirector tutorial;

        /// <summary>The tutorial (null until <see cref="AttachTutorial"/>).</summary>
        public TutorialDirector Tutorial { get { return tutorial; } }

        /// <summary>Called once at boot with the card (the director has no UiKit of its own).</summary>
        public void AttachTutorial(TutorialCard card)
        {
            if (card == null) return;
            tutorial = new TutorialDirector(this, cfg, input, hud, cues, audio, card, seed ^ 0x7A3C51E9);
        }

        /// <summary>
        /// Runs the tutorial on the duel stage; <paramref name="onDone"/>(skipped) fires once when it is finished or
        /// skipped (the caller then starts the match or returns to the title). <paramref name="replay"/> = from
        /// Settings (the last button reads BACK TO TOWN instead of TAP TO START).
        /// </summary>
        public void StartTutorial(System.Action<bool> onDone, bool replay)
        {
            if (tutorial == null) { if (onDone != null) onDone(true); return; }
            tutorial.Begin(onDone, replay);
        }

        /// <summary>Name on the opponent's plate (for "PAWTRUTH IS WAITING.").</summary>
        internal string TutorialOpponentName { get { return OpponentName; } }

        private void CancelTutorial()
        {
            if (tutorial != null) tutorial.Cancel();
        }

        private void TickTutorial(long frameStartNs)
        {
            tutorial.Tick(frameStartNs);
            if (State != Pres.Tutorial) return;   // finished this frame: the match / title took over
            RelaxZoom();
            TickPresentation();
        }

        // ------------------------------------------------------------------ stage hooks (TutorialDirector only)

        /// <summary>The duel stage without a match: full HP, PRACTICE sign, both cats ready, holster + SwipeCue (Grip).</summary>
        internal void TutorialStage()
        {
            PawHaptics.Prepare();
            if (State == Pres.Off) duelVignetteT = 0f;
            State = Pres.Tutorial;
            intro.Cancel();
            MatchIntro.ShownThisSession = true;   // the cats are already on their marks: no walk-in after the tutorial
            pipe.SetTension(0f);
            PawTime.ClearEffects();
            EndLastLight();
            HideBubble();
            banterLine = null;
            acting.Clear();
            EndTell();
            panel.Close();
            cues.HideAll();
            input.Clear();
            dead[0] = dead[1] = false;
            fallStage[0] = fallStage[1] = 0;
            hud.SetVisible(true);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            hud.SetRoundLabel("PRACTICE");
            hero.ResetPose();
            bandit.ResetPose();
            hero.Play("ready");
            bandit.Play("ready");
            hud.ShowHolster(true);
            hud.ShowHint(true);
            TutorialRegrip();
            pipe.SetZoomFocus(new Vector2(layout.BanditFeet.x, layout.BanditFeet.y - 40));
            audio.PlayMusic(PawAudio.Track.DuelBed);
            audio.DuelAmbience(1f);
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        /// <summary>Back to "thumb on the holster": hero holstered and ready, no cue, no stamp, SwipeCue in Grip.</summary>
        internal void TutorialRegrip()
        {
            TutorialEndHold();
            fired[0] = fired[1] = false;
            pendingImpact[0] = pendingImpact[1] = false;
            heroPredictedFire = false;
            heroFoulShown = false;
            restAnim[0] = restAnim[1] = "ready";
            hud.SetDrawn(false);
            hud.SetCueMode(SwipeCue.Mode.Grip);
            cues.HideAll();
        }

        /// <summary>The real hold: heart monitor, heartbeat, push-in, ambient freeze, SwipeCue in Wait.</summary>
        internal void TutorialBeginHold()
        {
            hud.SetCueMode(SwipeCue.Mode.Wait);
            cues.HideAll();
            EnterHold();
            cues.SetHoldMood(true);   // EnterHold's ShowNumeral(0) only starts the monitor after a "1"
        }

        /// <summary>Hold (or DRAW) abandoned: music, heartbeat, ambient and framing back to the standoff.</summary>
        internal void TutorialEndHold()
        {
            bool wasHolding = holdPresented || cuePresented;
            holdPresented = false;
            cuePresented = false;
            sweatT = -1f;
            PawTime.ReleaseAmbient();
            zoomRelaxing = true;
            audio.Heartbeat(0f);
            audio.Duck(1f);
            cues.SetHoldMood(false);
            if (wasHolding)
            {
                audio.NextFadeSeconds = 0.6f;
                audio.PlayMusic(PawAudio.Track.DuelBed);
            }
        }

        /// <summary>The real DRAW cue (burst, cue_draw, flash, scheduled sfx + haptic, SwipeCue Go, timer at 0).</summary>
        internal void TutorialPresentDraw(long cuePresentedNs) { PresentDraw(cuePresentedNs); }

        /// <summary>The real hero draw (draw_fire, muzzle, recoil). No impact follows: nobody is hit in practice.</summary>
        internal void TutorialHeroDraw() { PresentHeroDraw(); }

        /// <summary>The real jam: foul anim, TOO EARLY stamp, misfire click, error haptic.</summary>
        internal void TutorialHeroJam()
        {
            heroFoulShown = false;
            PresentHeroFoul();
        }

        /// <summary>Leaves the tutorial state (the caller starts the match or shows the title next).</summary>
        internal void TutorialFinish()
        {
            TutorialEndHold();
            cues.HideAll();
            hud.SetDrawn(false);
            hud.ShowHint(PawSettings.SwipeHint);
            // State stays Tutorial (idle) until the callback calls StartMatch (keeps the duel vignette) or Stop
        }
    }
}
