using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// First-run tutorial on the real duel stage (Docs/TUTORIAL.md). Presentation only: it drives
    /// <see cref="TutorialFlow"/> (Core, the step machine), the <see cref="TutorialCard"/> and the director's stage
    /// hooks (DuelDirector.Tutorial.cs).
    ///
    /// Measurement is the match's own path: presses come out of the same <see cref="SwipeDraw"/> queue (holster
    /// zone, threshold crossing, OS timestamps), and a private <see cref="DuelClient"/> on a private
    /// <see cref="LoopbackTransport"/> classifies them exactly as in a match (Early before the cue photons,
    /// Shot = inputNs - CuePresentedNs, no-shot timeout), with the cue presented through PollCue at frame start plus
    /// the director's present-latency estimate. The tutorial plays the authority's part for that one client
    /// (RoundStart at the hold, DrawCue when the hold ends); nothing reaches the real authority, the bot or the
    /// all-time best. Allocation-free per frame (strings are built only on step changes).
    /// </summary>
    public sealed class TutorialDirector
    {
        // ---- review / bridge switches (ReviewDirector.Tutorial)
        /// <summary>1 = run at every StartDuel (review runs included), -1 = never, 0 = auto (first run only).</summary>
        public static int ReviewMode;
        /// <summary>Simulated thumb for the bridge: -1 real input, 0 lifted, 1 resting on the holster.</summary>
        public static int ReviewThumb = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            ReviewMode = 0;
            ReviewThumb = -1;
        }

        /// <summary>Whether StartDuel should open with the tutorial. reviewRun = an automated run (intro skip / auto-tap).</summary>
        public static bool ShouldAutoRun(bool reviewRun)
        {
            if (ReviewMode < 0) return false;
            if (ReviewMode > 0) return true;
            return !PawSettings.TutorialDone && !reviewRun;
        }

        private readonly DuelDirector director;
        private readonly DuelConfig cfg;
        private readonly SwipeDraw input;
        private readonly DuelHud hud;
        private readonly DuelCues cues;
        private readonly PawAudio audio;
        private readonly TutorialCard card;
        private readonly LoopbackTransport transport = new LoopbackTransport();
        private readonly DuelClient client;
        private readonly TutorialFlow flow;
        private System.Action<bool> onDone;
        private bool replay;
        private int shownRevision = -1;
        private TutorialStep shownStep = TutorialStep.Off;
        private int roundId;
        private bool finished = true;

        public TutorialDirector(DuelDirector director, DuelConfig cfg, SwipeDraw input, DuelHud hud, DuelCues cues, PawAudio audio,
            TutorialCard card, int seed)
        {
            this.director = director;
            this.cfg = cfg;
            this.input = input;
            this.hud = hud;
            this.cues = cues;
            this.audio = audio;
            this.card = card;
            client = new DuelClient(0, transport, cfg);
            flow = new TutorialFlow(cfg, FeelConfig.Current.Tutorial, seed);
        }

        public TutorialFlow Flow { get { return flow; } }
        public bool Active { get { return flow.Active; } }
        public bool Replay { get { return replay; } }

        /// <summary>Stages the duel scene and starts at step 1. onDone(skipped) fires once at the end.</summary>
        public void Begin(System.Action<bool> done, bool isReplay)
        {
            onDone = done;
            replay = isReplay;
            finished = false;
            transport.Clear();
            client.Reset();
            director.TutorialStage();
            // first run: the cats are already on their marks when the match starts, so no walk-in intro after it
            if (!isReplay) MatchIntro.ShownThisSession = true;
            card.SetReducedMotion(PawSettings.ReducedMotion);
            flow.Begin();
            shownRevision = -1;
            shownStep = TutorialStep.Off;
        }

        /// <summary>The duel was stopped (title, lifecycle): drop everything without calling back.</summary>
        public void Cancel()
        {
            onDone = null;
            finished = true;
            flow.Cancel();
            shownRevision = flow.Revision;
            shownStep = TutorialStep.Off;
            transport.Clear();
            client.Reset();
            card.Hide();
            hud.SetResting(false);
        }

        /// <summary>
        /// The app was backgrounded (DuelDirector.VoidForLifecycle, device only): a hold or DRAW in flight goes back to
        /// "thumb on the holster" without a fail, and the practice round is dropped (the OS ate the touch, the
        /// stopwatch kept running: neither a lift nor a no-shot is the player's doing).
        /// </summary>
        public void Interrupt()
        {
            if (!flow.Active) return;
            if (flow.Interrupt())
            {
                transport.Clear();
                client.Reset();
            }
        }

        /// <summary>Per frame from DuelDirector.Tick while it is in Pres.Tutorial.</summary>
        public void Tick(long frameStartNs)
        {
            if (flow.Step == TutorialStep.Done && !finished) { Finish(); return; }   // skipped / confirmed from the bridge
            if (!flow.Active) { input.Clear(); return; }
            bool resting = ReviewThumb >= 0 ? ReviewThumb == 1 : input.FingerResting;

            // card buttons (pointer), then time
            if (card.TickSkip()) flow.Skip();
            else if (card.TickStart()) flow.Confirm();
            flow.Tick(PawTime.UnscaledDt, resting);
            Present(frameStartNs);
            if (flow.Step == TutorialStep.Done) { Finish(); return; }

            // the match's measurement path: cue presented at frame start + latency estimate, presses classified by the client
            if (client.PollCue(frameStartNs, director.PresentLatencyNs)) director.TutorialPresentDraw(client.CuePresentedNs);
            TimedPress p;
            while (input.TryDequeue(out p))
            {
                var r = client.Press(p.tNs, p.source, p.frameQuantized);
                if (r == PressResult.Early) flow.Early();
                else if (r == PressResult.Shot) flow.Shot(client.LastReport.reactionNs);
            }
            bool wasClosed = client.Closed;
            client.Tick(frameStartNs);
            if (client.Closed && !wasClosed && !client.Reported) flow.NoShot();
            ShotReport sink;
            while (transport.TryReceiveReport(out sink)) { }   // nobody adjudicates practice reports

            if (flow.Step == TutorialStep.Draw && client.HasCue && !client.Reported)
            {
                long e = client.ElapsedSinceCueNs(frameStartNs);
                if (e > cfg.NoShotTimeoutNs) e = cfg.NoShotTimeoutNs;
                cues.SetTimer(e, false);
            }
            Present(frameStartNs);
            var st = flow.Step;
            hud.SetResting(resting && (st == TutorialStep.Grip || st == TutorialStep.Regrip || st == TutorialStep.Hold));
        }

        private void Finish()
        {
            finished = true;
            bool skipped = flow.Skipped;
            PawSettings.TutorialDone = true;
            PawSettings.Save();
            card.Hide();
            hud.SetResting(false);
            transport.Clear();
            client.Reset();
            director.TutorialFinish();
            var cb = onDone;
            onDone = null;
            if (cb != null) cb(skipped);
        }

        // ------------------------------------------------------------------ step presentation (once per transition)

        private void Present(long frameStartNs)
        {
            if (flow.Revision == shownRevision) return;
            shownRevision = flow.Revision;
            var from = shownStep;
            shownStep = flow.Step;
            switch (flow.Step)
            {
                case TutorialStep.Grip:
                    hud.SetCueMode(SwipeCue.Mode.Grip);
                    card.Show(1, "REST YOUR THUMB ON THE HOLSTER", Pal.Ink, "Rest it on the holster below", "and keep it there.", true);
                    break;
                case TutorialStep.Hold:
                    StartPracticeRound(frameStartNs);
                    director.TutorialBeginHold();
                    card.Show(2, "WAIT... DON'T MOVE", Pal.Ink, "The heartbeat means WAIT.", "Swipe now and you lose the round.", true);
                    break;
                case TutorialStep.Fail:
                    PresentFail();
                    break;
                case TutorialStep.Regrip:
                    director.TutorialRegrip();
                    if (from == TutorialStep.Verdict)
                        card.Show(2, "ONE MORE TIME", Pal.Ink, "Thumb on the holster, wait for DRAW,", "then swipe up as fast as you can.", true);
                    else
                        card.Show(2, "THUMB BACK ON THE HOLSTER", Pal.Ink, "Rest it there and wait.", "Only DRAW means go.", true);
                    break;
                case TutorialStep.Draw:
                    // the authority's part for our one client: the cue arrives now and is presented by PollCue below
                    var cue = new DrawCue();
                    cue.roundId = roundId;
                    cue.cueSeq = 1;
                    transport.BroadcastCue(cue);
                    card.Show(3, "DRAW! SWIPE UP!", Pal.PaintRed, "Fast as you can!", null, true);
                    break;
                case TutorialStep.Verdict:
                    PresentVerdict();
                    break;
                case TutorialStep.Ready:
                    director.TutorialEndHold();   // standoff music / ambience back while the card waits
                    cues.HideAll();
                    card.ShowReady("YOU'RE READY.", director.TutorialOpponentName + " IS WAITING.", replay ? "BACK TO TOWN" : "TAP TO START");   // lint: alloc-ok (step change)
                    break;
            }
        }

        private void StartPracticeRound(long frameStartNs)
        {
            roundId++;
            transport.Clear();
            client.Reset();
            var rs = new RoundStartMsg();
            rs.roundId = roundId;
            rs.roundNumber = 1;
            rs.countdownBeats = 0;
            rs.beatNs = 0;
            transport.BroadcastRoundStart(rs);
            RoundStartMsg got;
            client.PollRoundStart(frameStartNs, out got);   // presses from now on count (earlier ones are ignored)
        }

        private void PresentFail()
        {
            director.TutorialEndHold();
            cues.HideDraw();
            cues.HideTimer();
            switch (flow.LastFail)
            {
                case TutorialFail.Swiped:
                    director.TutorialHeroJam();   // the real jam: foul anim, TOO EARLY stamp, misfire, error haptic
                    card.Show(2, "TOO EARLY!", Pal.PaintRed, "Keep your thumb down until DRAW.", "Early swipe = your gun jams.", true);
                    break;
                case TutorialFail.Lifted:
                    audio.Play("sfx_misfire", 0.5f);
                    HapticMoments.Stamp(StampHaptics.NoShot);
                    card.Show(2, "STAY ON THE HOLSTER", Pal.PaintRed, "Keep your thumb down until DRAW.", "Then swipe up from there.", true);
                    break;
                default:
                    cues.ShowStamp("stamp_no_shot");
                    audio.Play("sfx_misfire", 0.5f);
                    HapticMoments.Stamp(StampHaptics.NoShot);
                    card.Show(3, "NO SHOT!", Pal.PaintRed, "When DRAW pops up, swipe up", "from the holster. Fast!", true);
                    break;
            }
            card.Shake();
        }

        private void PresentVerdict()
        {
            director.TutorialHeroDraw();   // the real draw: draw_fire, muzzle, recoil (nobody is hit in practice)
            long ns = flow.ReactionNs;
            cues.SetTimer(ns, true);
            var g = flow.Grade;
            HapticMoments.Stamp(StampHaptics.For(false, false, g));
            string secs = Seconds3(ns);   // lint: alloc-ok (step change)
            if (!flow.Slow)
            {
                string word = g == Grade.Lightning ? "LIGHTNING!" : (g == Grade.Perfect ? "PERFECT!" : "NICE!");
                card.Show(3, secs + " s - " + word, Pal.Green2, "Faster draw = more damage.", "Every millisecond counts.", true);   // lint: alloc-ok (step change)
            }
            else if (flow.Retrying)
                card.Show(3, secs + " s - SLOW", Pal.PaintRed, "Try again? Swipe the moment", "you see DRAW.", true);   // lint: alloc-ok (step change)
            else
                card.Show(3, secs + " s - SLOW", Pal.PaintRed, "You'll get faster.", "Faster draw = more damage.", true);   // lint: alloc-ok (step change)
        }

        /// <summary>"0.312" (truncated, like the 6-decimal timer).</summary>
        private static string Seconds3(long ns)
        {
            if (ns < 0) ns = 0;
            long ms = ns / 1000000L;
            long whole = ms / 1000, frac = ms % 1000;
            return whole.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." +
                   frac.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>Bridge status: "off" or the step, its card number, attempts, last fail and reaction.</summary>
        public string StatusText
        {
            get
            {
                string s = flow.Active ? flow.Step + "(" + flow.CardStep + "/3)" : "off";
                if (flow.Active)
                {
                    s += ",attempts:" + flow.Attempts + ",fail:" + flow.LastFail +
                         ",reaction:" + (flow.ReactionNs >= 0 ? Seconds3(flow.ReactionNs) + "s" : "--") +
                         (replay ? ",replay" : "");
                }
                return s + " tutorialDone=" + (PawSettings.TutorialDone ? "1" : "0") +
                       " tutorialMode=" + (ReviewMode > 0 ? "on" : (ReviewMode < 0 ? "off" : "auto")) +
                       (ReviewThumb >= 0 ? " tutorialThumb=" + (ReviewThumb == 1 ? "grip" : "lift") : "");
            }
        }

        // ------------------------------------------------------------------ review

        /// <summary>Bridge: SKIP / TAP TO START without the pointer.</summary>
        public void ReviewSkip() { flow.Skip(); }
        public void ReviewConfirm() { flow.Confirm(); }
    }
}
