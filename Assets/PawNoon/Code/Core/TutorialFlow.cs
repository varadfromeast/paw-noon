namespace PawNoon.Core
{
    /// <summary>Where the first-run tutorial is (Docs/TUTORIAL.md). Steps 1..3 on the card map onto these.</summary>
    public enum TutorialStep
    {
        Off = 0,
        /// <summary>Step 1: rest the thumb on the holster for <see cref="TutorialTuning.gripSeconds"/>.</summary>
        Grip = 1,
        /// <summary>Step 2: the hold (heart monitor). Ends in Draw, or in Fail on a swipe / lift.</summary>
        Hold = 2,
        /// <summary>Step 2 failed (too early, thumb lifted, or no shot after DRAW): the card says why, then Regrip.</summary>
        Fail = 3,
        /// <summary>Step 2 again: thumb back on the holster, then the hold restarts.</summary>
        Regrip = 4,
        /// <summary>Step 3: DRAW is up, waiting for the swipe.</summary>
        Draw = 5,
        /// <summary>Step 3: the reaction time is on the card.</summary>
        Verdict = 6,
        /// <summary>YOU'RE READY + TAP TO START.</summary>
        Ready = 7,
        /// <summary>Finished or skipped (see <see cref="TutorialFlow.Skipped"/>).</summary>
        Done = 8
    }

    /// <summary>Why step 2 failed.</summary>
    public enum TutorialFail { None = 0, Swiped = 1, Lifted = 2, NoShot = 3 }

    /// <summary>Tutorial pacing (client-only presentation, loaded from feel_config.json by the runtime).</summary>
    public sealed class TutorialTuning
    {
        /// <summary>Thumb must rest this long on the holster before the hold starts.</summary>
        public double gripSeconds = 0.6;
        /// <summary>The hold is uniform in [holdMinSeconds, holdMaxSeconds] (about 3.5 s), a new draw each attempt.</summary>
        public double holdMinSeconds = 2.8;
        public double holdMaxSeconds = 4.2;
        /// <summary>A lift shorter than this during the hold is touch jitter, not a lift.</summary>
        public double liftGraceSeconds = 0.2;
        /// <summary>How long the TOO EARLY / NO SHOT card stays before asking for the thumb again.</summary>
        public double failSeconds = 1.8;
        /// <summary>How long the reaction-time card stays before YOU'RE READY (or the retry).</summary>
        public double verdictSeconds = 1.8;
        /// <summary>How many SLOW draws are sent back for another try before the player is let through anyway.</summary>
        public int slowRetries = 1;

        public void Sanitize()
        {
            if (gripSeconds < 0.1) gripSeconds = 0.1;
            if (holdMinSeconds < 1.0) holdMinSeconds = 1.0;
            if (holdMaxSeconds < holdMinSeconds) holdMaxSeconds = holdMinSeconds;
            if (liftGraceSeconds < 0.0) liftGraceSeconds = 0.0;
            if (failSeconds < 0.5) failSeconds = 0.5;
            if (verdictSeconds < 0.5) verdictSeconds = 0.5;
            if (slowRetries < 0) slowRetries = 0;
        }
    }

    /// <summary>
    /// The first-run tutorial's step machine, engine-free (Docs/TUTORIAL.md). Presentation only: it never
    /// measures anything. The runtime feeds it the thumb-on-holster state each frame, the player client's press
    /// results (Early / Shot with the reaction the DuelClient measured / no-shot timeout) and the card buttons; it
    /// says which step to present. Every transition bumps <see cref="Revision"/> so the presenter can react once.
    /// No allocations after construction.
    /// </summary>
    public sealed class TutorialFlow
    {
        private readonly DuelConfig cfg;
        private readonly TutorialTuning tune;
        private readonly System.Random rng;
        private double t;          // seconds in the current step
        private double restT;      // Grip / Regrip: continuous rest
        private double liftT;      // Hold: continuous lift
        private int slowLeft;

        public TutorialStep Step { get; private set; }
        /// <summary>Bumped on every transition (also when a step is re-entered).</summary>
        public int Revision { get; private set; }
        public TutorialFail LastFail { get; private set; }
        /// <summary>Target length of the current / last hold, seconds.</summary>
        public double HoldSeconds { get; private set; }
        /// <summary>Holds started (1 on the first try).</summary>
        public int Attempts { get; private set; }
        /// <summary>The last valid draw's reaction (ns) as the DuelClient measured it, or -1.</summary>
        public long ReactionNs { get; private set; }
        public Grade Grade { get; private set; }
        /// <summary>The last draw graded SLOW or SLOWPOKE.</summary>
        public bool Slow { get; private set; }
        /// <summary>True when the verdict sends the player back for another try.</summary>
        public bool Retrying { get; private set; }
        public bool Skipped { get; private set; }
        public double StepTime { get { return t; } }
        /// <summary>Step 2 progress 0..1 of the rest (Grip / Regrip).</summary>
        public double GripProgress { get { return tune.gripSeconds > 0 ? System.Math.Min(1.0, restT / tune.gripSeconds) : 1.0; } }

        /// <summary>1, 2 or 3 for the card's step dots (0 when off).</summary>
        public int CardStep
        {
            get
            {
                switch (Step)
                {
                    case TutorialStep.Grip: return 1;
                    case TutorialStep.Hold:
                    case TutorialStep.Fail:
                    case TutorialStep.Regrip: return 2;
                    case TutorialStep.Draw:
                    case TutorialStep.Verdict:
                    case TutorialStep.Ready: return 3;
                    default: return 0;
                }
            }
        }

        public bool Active { get { return Step != TutorialStep.Off && Step != TutorialStep.Done; } }

        public TutorialFlow(DuelConfig cfg, TutorialTuning tuning, int seed)
        {
            this.cfg = cfg ?? DuelConfig.Default();
            tune = tuning ?? new TutorialTuning();
            tune.Sanitize();
            rng = new System.Random(seed);
            Step = TutorialStep.Off;
            ReactionNs = -1;
        }

        public TutorialTuning Tuning { get { return tune; } }

        /// <summary>Starts (or restarts) at step 1.</summary>
        public void Begin()
        {
            Attempts = 0;
            Skipped = false;
            LastFail = TutorialFail.None;
            ReactionNs = -1;
            Grade = Grade.None;
            Slow = false;
            Retrying = false;
            slowLeft = tune.slowRetries;
            Go(TutorialStep.Grip);
        }

        /// <summary>Back to Off without finishing (the duel was stopped).</summary>
        public void Cancel()
        {
            if (Step == TutorialStep.Off) return;
            Go(TutorialStep.Off);
        }

        /// <summary>Per frame. dt in seconds (unscaled presentation time), thumbResting = a finger rests on the holster.</summary>
        public void Tick(double dt, bool thumbResting)
        {
            if (dt < 0) dt = 0;
            t += dt;
            switch (Step)
            {
                case TutorialStep.Grip:
                case TutorialStep.Regrip:
                    restT = thumbResting ? restT + dt : 0.0;
                    if (restT >= tune.gripSeconds) StartHold();
                    break;
                case TutorialStep.Hold:
                    liftT = thumbResting ? 0.0 : liftT + dt;
                    if (liftT > tune.liftGraceSeconds) Fail(TutorialFail.Lifted);
                    else if (t >= HoldSeconds) Go(TutorialStep.Draw);
                    break;
                case TutorialStep.Fail:
                    if (t >= tune.failSeconds) Go(TutorialStep.Regrip);
                    break;
                case TutorialStep.Verdict:
                    if (t >= tune.verdictSeconds) Go(Retrying ? TutorialStep.Regrip : TutorialStep.Ready);
                    break;
            }
        }

        /// <summary>The player client classified a press as Early (swipe before DRAW).</summary>
        public void Early()
        {
            if (Step == TutorialStep.Hold || Step == TutorialStep.Draw) Fail(TutorialFail.Swiped);
        }

        /// <summary>
        /// A valid draw with the reaction the player client measured. Below the anticipation floor it counts as
        /// a guess (same rule as a match: a jam), otherwise it is graded with the match's grade bounds.
        /// </summary>
        public void Shot(long reactionNs)
        {
            if (Step != TutorialStep.Draw) return;
            if (reactionNs < cfg.AnticipationNs) { Fail(TutorialFail.Swiped); return; }
            ReactionNs = reactionNs;
            Grade = Grading.FromMs(cfg, DuelConfig.NsToMs(reactionNs));
            Slow = Grade == Grade.Slow || Grade == Grade.Slowpoke;
            Retrying = Slow && slowLeft > 0;
            if (Retrying) slowLeft--;
            Go(TutorialStep.Verdict);
        }

        /// <summary>The no-shot window after DRAW closed without a draw.</summary>
        public void NoShot()
        {
            if (Step == TutorialStep.Draw) Fail(TutorialFail.NoShot);
        }

        /// <summary>
        /// The app was backgrounded mid-hold or mid-DRAW: back to <see cref="TutorialStep.Regrip"/> with no fail recorded
        /// (the next hold draws a new length). Other steps carry on. True when it changed the step.
        /// </summary>
        public bool Interrupt()
        {
            if (Step != TutorialStep.Hold && Step != TutorialStep.Draw) return false;
            LastFail = TutorialFail.None;
            Go(TutorialStep.Regrip);
            return true;
        }

        /// <summary>SKIP on the card: done, from any active step.</summary>
        public void Skip()
        {
            if (!Active) return;
            Skipped = true;
            Go(TutorialStep.Done);
        }

        /// <summary>TAP TO START on the ready card.</summary>
        public void Confirm()
        {
            if (Step == TutorialStep.Ready) Go(TutorialStep.Done);
        }

        private void StartHold()
        {
            Attempts++;
            double span = tune.holdMaxSeconds - tune.holdMinSeconds;
            HoldSeconds = tune.holdMinSeconds + (span > 0 ? rng.NextDouble() * span : 0.0);
            Go(TutorialStep.Hold);
        }

        private void Fail(TutorialFail why)
        {
            LastFail = why;
            Go(TutorialStep.Fail);
        }

        private void Go(TutorialStep s)
        {
            Step = s;
            t = 0.0;
            restT = 0.0;
            liftT = 0.0;
            Revision++;
        }
    }
}
