using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Runs the duel each frame: authority tick, bot client, local player client (cue presentation,
    /// timestamped presses), then turns authority events into presentation (anims, FX, HUD, audio,
    /// haptics). Presentation never feeds back into timing.
    /// </summary>
    public sealed partial class DuelDirector
    {
        public enum Pres { Off, Standoff, Live, Result, MatchEnd, Review, Tutorial }

        // wiring
        private readonly DuelConfig cfg;
        private readonly GameLayout layout;
        private readonly IDuelClock clock;
        private readonly LocalAuthority authority;
        private readonly DuelClient player;
        private readonly BotClient soloBot;
        // slot 1: the bot (solo) or a GhostOpponent (recorded PvP), see SetOpponent
        private IOpponent opponent;
        private RecordedMatchRules rules;   // null = solo rules (play until KO)
        private string opponentName;        // null = cfg.botName
        private readonly SwipeDraw input;
        private readonly DuelActor hero;
        private readonly DuelActor bandit;
        private readonly PixelParticles fx;
        private readonly DuelCues cues;
        private readonly DuelHud hud;
        private readonly ResultPanel panel;
        private readonly PawAudio audio;
        private readonly PixelPipeline pipe;

        public Pres State { get; private set; }
        public LocalAuthority Authority { get { return authority; } }
        public DuelClient Player { get { return player; } }
        public BotClient Bot { get { return soloBot; } }
        /// <summary>Who is in slot 1 now (the bot unless a PvP match set a ghost).</summary>
        public IOpponent Opponent { get { return opponent; } }
        /// <summary>Recorded-match rules of the current match, or null in solo.</summary>
        public RecordedMatchRules MatchRules { get { return rules; } }

        /// <summary>Raised once when the match result should be shown (player won?, drawn match = double KO in a tie?).</summary>
        public System.Action<bool, bool> MatchFinished;

        private float stateTimer;
        private int lastNumeral = -1;
        private bool holdPresented;
        private float holdT;
        private bool zoomRelaxing;
        private float prepareT;
        private bool cuePresented;
        private bool heroPredictedFire;
        private bool heroFoulShown;
        private bool noShotShown;
        private readonly bool[] fired = new bool[2];
        private readonly bool[] pendingImpact = new bool[2];
        private readonly DuelEvent[] pendingEvent = new DuelEvent[2];
        private readonly float[] pendingAge = new float[2];
        private readonly bool[] dead = new bool[2];
        private readonly string[] restAnim = { "ready", "ready" };
        private bool resolved;
        private float settleT;
        private bool matchOver;
        private float voidTimer;
        private long autoTapNs = -1;
        private int seed;
        private bool firstMatch = true;
        private bool reducedMotion;

        // the bot's hold acting (feints / late tell): presentation only, never touches timing
        private readonly BotActing acting;
        private readonly SheetDef tellSheet;
        private int actingRoundId = -1;
        private double itch;
        private bool heroFouledThisRound;
        private bool banditTelling;

        // choreography
        private float sweatT = -1f;
        private readonly float baseVignette;
        private float vignettePulse;
        private float vignettePulseT = 99f;
        private float duelVignetteT;      // eases the duel focus vignette in from match start
        private float beatThump, beatThumpT = 99f;
        private readonly int[] fallStage = new int[2];   // 0 none, 1 reeling (hit, slow-mo), 2 falling (down), 3 landed
        private readonly float[] fallT = new float[2];

        // near-miss drama (authority-decided tie): both impacts land together at the second trigger pull
        private bool tieRound;
        private bool tieLanded;
        private int lastFiredSlot = -1;
        private readonly Vector2[] muzzleAt = new Vector2[2];

        // the opponent talks (presentation only)
        /// <summary>Tail tip: just left of the opponent's muzzle, in bandit cell px (cell 117x120, pivot at the feet).</summary>
        private static readonly Vector2Int BanterMouthCell = new Vector2Int(46, 33);
        private SpeechBubble bubble;
        private SpriteCatalog uiCatalog;
        private readonly BanterPicker banter;
        private string banterLine;
        private float banterAt = -1f;
        private bool prevHeroEarly;

        // SPEC_V2 §8.2 / §8.6 / §8.7 (presentation only): opponent persona, stamp haptics, match intro
        private CharacterProfile persona;
        private string foulMessage = "PAWTRUTH JUMPED THE GUN!";
        private int[] stampHaptic;
        private readonly MatchIntro intro;

        // outcome moments (Docs/FEEL.md): outright-win streak, kill-shot punch; presentation only
        private readonly StreakCounter streak = new StreakCounter();
        private bool killPunchPending;
        public const string OnARollLabel = "ON A ROLL";

        /// <summary>The player's current run of outright exchange wins this match.</summary>
        public int Streak { get { return streak.Current; } }

        public DuelDirector(DuelConfig cfg, GameLayout layout, IDuelClock clock, LocalAuthority authority, DuelClient player, BotClient bot,
            SwipeDraw input, DuelActor hero, DuelActor bandit, PixelParticles fx, DuelCues cues, DuelHud hud, ResultPanel panel,
            PawAudio audio, PixelPipeline pipe, int seed)
        {
            this.cfg = cfg;
            this.layout = layout;
            this.clock = clock;
            this.authority = authority;
            this.player = player;
            this.soloBot = bot;
            this.opponent = bot;
            this.input = input;
            this.hero = hero;
            this.bandit = bandit;
            this.fx = fx;
            this.cues = cues;
            this.hud = hud;
            this.panel = panel;
            this.audio = audio;
            this.pipe = pipe;
            this.seed = seed;
            acting = new BotActing(cfg, seed ^ 0x2545F491);
            tellSheet = bandit.LoadOptionalAnim("bandit", "bandit", "tell");
            baseVignette = pipe.Vignette;
            banter = new BanterPicker(seed ^ 0x6C8E9CF5);
            intro = new MatchIntro(hero, bandit, hud, cues, audio, pipe);
            hero.OnFire = HeroFired;
            cues.OnHeartBeat = HoldHeartBeat;
            bandit.OnFire = BanditFired;
            if (panel != null)
            {
                panel.OnStreakTag = StreakTagLanded;
                panel.OnNewBest = NewBestLanded;
            }
            State = Pres.Off;
        }

        public void SetReducedMotion(bool on)
        {
            reducedMotion = on;
            if (hud != null) hud.SetReducedMotion(on);
            if (bubble != null) bubble.SetReducedMotion(on);
        }

        /// <summary>The opponent's speech bubble (created on first use under the cues' UI root).</summary>
        public SpeechBubble Bubble { get { return EnsureBubble(); } }

        public void SetSeed(int s) { seed = s; }

        /// <summary>The first-match-of-session intro (SPEC_V2 §8.7).</summary>
        public MatchIntro Intro { get { return intro; } }

        /// <summary>Persona of the cat across the street (name plate, result card, banter, meow). Null = config fallback.</summary>
        public CharacterProfile Persona { get { return persona; } }

        public void SetPersona(CharacterProfile p)
        {
            persona = p;
            foulMessage = OpponentName + " JUMPED THE GUN!";
            if (hud != null) hud.SetName(1, OpponentName);
        }

        private string OpponentName { get { return opponentName ?? (persona != null && !string.IsNullOrEmpty(persona.name) ? persona.name : cfg.botName); } }
        private static readonly BanterLines DefaultLines = new BanterLines();
        private BanterLines OpponentLines { get { return persona != null && persona.lines != null ? persona.lines : DefaultLines; } }

        /// <summary>
        /// PvP seam (SPEC_V2 §4.6): from the next StartMatch, slot 1 is <paramref name="o"/> and the match
        /// ends under <paramref name="r"/> (3 resolved rounds / 9 attempts / ghost exhausted, HP decides).
        /// SetOpponent(null, null, null) restores the bot and the solo rules.
        /// </summary>
        public void SetOpponent(IOpponent o, RecordedMatchRules r, string displayName)
        {
            opponent = o ?? soloBot;
            rules = o != null ? r : null;
            opponentName = o != null && !string.IsNullOrEmpty(displayName) ? displayName : null;
            foulMessage = OpponentName + " JUMPED THE GUN!";
            hud.SetName(1, OpponentName);
        }

        public int Seed { get { return seed; } }

        /// <summary>Auto-tap N ms after each presented cue; negative disables.</summary>
        public void SetAutoTapMs(double ms) { autoTapNs = ms < 0 ? -1 : DuelConfig.MsToNs(ms); }

        public double AutoTapMs { get { return autoTapNs < 0 ? -1 : autoTapNs / 1e6; } }

        /// <summary>Frame-start to photons estimate: presentLatencyFrames display frames + calibration.</summary>
        public long PresentLatencyNs
        {
            get
            {
                double hz = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60.0;
                return (long)(cfg.presentLatencyFrames * 1e9 / hz + cfg.presentLatencyExtraMs * 1e6);
            }
        }

        // ------------------------------------------------------------------ match / round flow

        public void StartMatch()
        {
            PawHaptics.Prepare();   // Core Haptics engine up and players precompiled before the first countdown
            pipe.SetTension(0f);
            if (State == Pres.Off) duelVignetteT = 0f;
            authority.Reseed(seed);
            opponent.StartMatch(seed * 7919 + 13);
            if (rules != null) rules.Reset();
            acting.Reseed(seed * 104729 + 71);
            banter.Reseed(seed * 15485863 + 7);
            seed++;
            prevHeroEarly = false;
            itch = 0;
            actingRoundId = -1;
            streak.Reset();
            killPunchPending = false;
            authority.StartMatch();
            player.Reset();
            opponent.Client.Reset();
            input.Clear();
            matchOver = false;
            hud.SetVisible(true);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            panel.Close();
            cues.HideAll();
            PawTime.ClearEffects();
            dead[0] = dead[1] = false;
            fallStage[0] = fallStage[1] = 0;
            bool playIntro = MatchIntro.ShouldPlay(cfg.introEnabled, autoTapNs >= 0);
            if (playIntro) intro.CaptureTitlePose();   // before the standoff switches the cats to "ready"
            hero.ResetPose();
            bandit.ResetPose();
            hud.ShowHolster(true);
            TownMatchStart(seed - 1);   // town: weather from this match's seed, onlookers + creak (DuelDirector.Town.cs)
            BeginStandoff(1.4f, BanterContext.MatchStart);
            if (playIntro) intro.Begin(reducedMotion, firstMatch);
            audio.PlayMusic(PawAudio.Track.DuelBed);   // composed standoff bed (falls back to music_standoff)
            audio.DuelAmbience(1f);
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        public void Stop()
        {
            CancelTutorial();
            State = Pres.Off;
            intro.Cancel();
            pipe.SetTension(0f);
            hud.SetDrawn(false);
            hud.ShowHolster(false);
            audio.DuelAmbience(0f);
            cues.HideAll();
            panel.Close();
            pipe.Zoom = 1f;
            pipe.Vignette = baseVignette;
            vignettePulseT = 99f;
            duelVignetteT = 0f;
            beatThumpT = 99f;
            PawTime.ReleaseAmbient();
            acting.Clear();
            audio.Duck(1f);
            audio.Heartbeat(0f);
            HideBubble();
            EndLastLight();
            TownMatchEnd();
            killPunchPending = false;
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
        }

        private void BeginStandoff(float seconds)
        {
            BeginStandoff(seconds, BanterContext.None);
        }

        private void BeginStandoff(float seconds, BanterContext talk)
        {
            State = Pres.Standoff;
            audio.NextFadeSeconds = 0.8f;
            audio.PlayMusic(PawAudio.Track.DuelBed);
            stateTimer = seconds;
            lastNumeral = -1;
            holdPresented = false;
            holdT = 0f;
            cuePresented = false;
            heroPredictedFire = false;
            heroFoulShown = false;
            noShotShown = false;
            resolved = false;
            voidTimer = 0f;
            heroFouledThisRound = false;
            sweatT = -1f;
            acting.Clear();
            EndTell();
            PawTime.ReleaseAmbient();
            for (int i = 0; i < 2; i++)
            {
                fired[i] = false;
                pendingImpact[i] = false;
                restAnim[i] = "ready";
                if (!dead[i]) fallStage[i] = 0;
            }
            if (!dead[0]) { hero.ResetPose(); hero.Play("ready"); }
            if (!dead[1]) { bandit.ResetPose(); bandit.Play("ready"); }
            zoomRelaxing = true;   // ease the camera back out over the standoff, never snap
            pipe.SetTension(PixelPipeline.TensionFromHp(authority.Match.HpFraction(0), authority.Match.HpFraction(1)));
            pipe.SetZoomFocus(new Vector2(layout.BanditFeet.x, layout.BanditFeet.y - 40));
            audio.Duck(1f);
            audio.Heartbeat(0f);
            cues.HideAll();
            ApplySuddenDeath();   // LAST LIGHT rule for this standoff (multiplier, dawn), before anything is shown
            TownStandoff();
            cues.ShowGetReady(true);
            hud.SetRound(authority.Match.RoundNumber);
            PresentLastLightStandoff();
            hud.SetDrawn(false);
            hud.ShowHint(PawSettings.SwipeHint);   // the swipe cue every round until it is switched off in Settings
            hud.ShowHolster(true);
            input.Clear();

            tieRound = false;
            tieLanded = false;
            lastFiredSlot = -1;
            HideBubble();
            banterLine = null;
            banterAt = -1f;
            if (!dead[1] && talk != BanterContext.None && opponent.HostActing)
            {
                banterLine = banter.Pick(OpponentLines, talk, cfg.banterChance);
                if (banterLine != null)
                {
                    // the line lands ~1.2 s before "3": stretch a short standoff so it fits
                    float lead = (float)cfg.banterLeadSeconds;
                    if (stateTimer < lead + 0.05f) stateTimer = lead + 0.05f;
                    banterAt = lead;
                }
            }
        }

        /// <summary>App backgrounded / focus lost mid-round: void and go back to standoff.</summary>
        public void VoidForLifecycle()
        {
            if (State != Pres.Live) return;
            // recorded PvP: never void (the server cannot see it); the attempt plays on and records a no-shot
            if (rules != null) return;
            if (authority.VoidRound())
            {
                DrainEvents();
                BeginStandoff(1.6f);
                cues.ShowMessage("ROUND VOID", 1.5f);
            }
        }

        // ------------------------------------------------------------------ per frame

        public void Tick(long frameStartNs)
        {
            PawHaptics.Tick(PawTime.UnscaledDt);   // timed haptic patterns (stamp language)
            if (State == Pres.Off || State == Pres.Review)
            {
                input.Clear();
                if (State == Pres.Review && panel.IsOpen && panel.Tick()) StampLanded();
                return;
            }
            if (State == Pres.Tutorial) { TickTutorial(frameStartNs); return; }   // DuelDirector.Tutorial.cs
            long lat = PresentLatencyNs;

            opponent.PreAuthorityTick(frameStartNs);
            authority.Tick();
            hud.SetResting(input.FingerResting && State == Pres.Live && !cuePresented);
            opponent.SetPlayerRoundWins(authority.Match.RoundWins[0]);
            opponent.Tick(frameStartNs, lat);
            TickActing(frameStartNs);

            switch (State)
            {
                case Pres.Standoff:
                    if (intro.Tick(PawTime.UnscaledDt)) break;   // match intro holds the standoff
                    stateTimer -= PawTime.UnscaledDt;
                    if (banterLine != null && stateTimer <= banterAt)
                    {
                        Say(banterLine, (float)cfg.banterHoldSeconds);
                        banterLine = null;
                    }
                    if (stateTimer <= 0f)
                    {
                        if (rules != null && rules.Check(authority.Match) != RecordedEnd.None) { EndByRules(); break; }
                        cues.ShowGetReady(false);
                        if (authority.StartCountdown())
                        {
                            if (rules != null) rules.AttemptStarted();
                            State = Pres.Live;
                            TickLive(frameStartNs, lat);
                        }
                    }
                    break;
                case Pres.Live:
                    TickLive(frameStartNs, lat);
                    break;
                case Pres.Result:
                    stateTimer -= PawTime.UnscaledDt;
                    if (panel.Tick()) StampLanded();
                    if (panel.ShareHolding && stateTimer < 0.5f) stateTimer = 0.5f;   // share sheet up: no auto-advance
                    bool tap = UiPointer.Down && !UiPointer.Consumed && stateTimer < cfg.resultAutoAdvanceSeconds - 0.4f;
                    if (tap) UiPointer.Consumed = true;
                    if (stateTimer <= 0f || tap) AdvanceFromResult();
                    break;
                case Pres.MatchEnd:
                    if (panel.IsOpen && panel.Tick()) StampLanded();
                    TickMatchEnd();
                    break;
            }

            if (State != Pres.Live) input.Clear();
            authority.Tick();
            DrainEvents();
            RelaxZoom();
            TickPresentation();
        }

        private void RelaxZoom()
        {
            if (!zoomRelaxing) return;
            // slow ease-out back to the neutral framing (about 1.2 s); stops when the hold starts a new push-in
            if (holdPresented || cuePresented) zoomRelaxing = false;
            else
            {
                pipe.Zoom = Mathf.MoveTowards(pipe.Zoom, 1f, PawTime.UnscaledDt * 0.06f);
                if (Mathf.Abs(pipe.Zoom - 1f) < 0.0005f) { pipe.Zoom = 1f; zoomRelaxing = false; }
            }
        }

        private void TickLive(long frameStartNs, long lat)
        {
            RoundStartMsg rs;
            if (player.PollRoundStart(frameStartNs, out rs))
            {
                lastNumeral = -1;
                ShowRoundSign(rs.roundNumber);
            }

            if (player.PollCue(frameStartNs, lat)) PresentDraw(player.CuePresentedNs);

            // countdown numerals / hold, driven by the client's own receipt time (no cue knowledge)
            if (player.InRound && !player.HasCue)
            {
                int n = RoundSchedule.NumeralAt(player.CountdownBeats, player.BeatNs, frameStartNs - player.CountdownStartNs);
                if (n != lastNumeral)
                {
                    lastNumeral = n;
                    if (n > 0)
                    {
                        if (n == 3) hud.SetCueMode(SwipeCue.Mode.Wait);   // the teaching cue: from "3" on, do not move
                        cues.ShowNumeral(n);
                        audio.Play(n == 3 ? "sfx_tick_3" : (n == 2 ? "sfx_tick_2" : "sfx_tick_1"), n == 3 ? 0.55f : (n == 2 ? 0.68f : 0.82f));
                        audio.Play("sfx_tick_sub", n == 3 ? 0.5f : (n == 2 ? 0.7f : 0.9f));   // low thump for weight (silent if missing)
                        HapticMoments.CountdownTick(n);
                        if (n == 1) PawHaptics.Prepare();
                        if (n == 1) TownDuck();
                    }
                    else EnterHold();
                }
                if (holdPresented)
                {
                    holdT += PawTime.UnscaledDt;
                    float k = Ease.InOutSine(Mathf.Clamp01(holdT / 3.5f));
                    pipe.Zoom = 1f + ((float)cfg.holdZoom - 1f) * k;
                    prepareT -= PawTime.UnscaledDt;
                    if (prepareT <= 0f) { PawHaptics.Prepare(); prepareT = 1.0f; }
                    if (sweatT > 0f)
                    {
                        sweatT -= PawTime.UnscaledDt;
                        if (sweatT <= 0f) { sweatT = -1f; SweatDrop(); }
                    }
                }
            }

            // auto tap (review/testing) goes through the same input queue
            if (autoTapNs >= 0 && player.HasCue && !player.Reported && !player.Closed && frameStartNs >= player.CuePresentedNs + autoTapNs)
            {
                input.Inject(player.CuePresentedNs + autoTapNs, InputSource.Injected);
            }

            TimedPress p;
            while (input.TryDequeue(out p))
            {
                if (dead[0]) continue;
                var r = player.Press(p.tNs, p.source, p.frameQuantized);
                if (r == PressResult.Early) PresentHeroFoul();
                else if (r == PressResult.Shot)
                {
                    if (player.LastReport.reactionNs < cfg.AnticipationNs) PresentHeroFoul();
                    else PresentHeroDraw();
                }
            }

            bool wasClosed = player.Closed;
            player.Tick(frameStartNs);
            if (player.Closed && !wasClosed && !player.Reported && !noShotShown)
            {
                noShotShown = true;
                TownNoShot();
                cues.ShowStamp("stamp_no_shot");
                hud.ShowHintOnce();
                audio.Play("sfx_misfire", 0.5f);
            }

            if (player.HasCue && !resolved)
            {
                if (player.Reported && player.LastReport.kind == ReportKind.Shot)
                    cues.SetTimer(player.LastReport.reactionNs, true);
                else if (!player.Reported)
                {
                    long e = player.ElapsedSinceCueNs(frameStartNs);
                    if (e > cfg.NoShotTimeoutNs) e = cfg.NoShotTimeoutNs;
                    cues.SetTimer(e, false);
                }
            }
        }

        private void EnterHold()
        {
            holdPresented = true;
            holdT = 0f;
            prepareT = 0f;
            cues.ShowNumeral(0);
            audio.NextFadeSeconds = 0.6f;
            audio.PlayMusic(PawAudio.Track.DuelHold);  // suspense loop under the heartbeat (silence if missing)
            audio.Duck(0.5f * LastLightMusicGain);   // LAST LIGHT ducks a further suddenDeathMusicDuckDb
            audio.Heartbeat(0f);   // the heart monitor fires each beat itself (HoldHeartBeat): faster, more erratic
            // the street holds its breath: clouds, props, motes, shimmer ease to a stop; both cats freeze
            // their idle breathing (TickPresentation); only the push-in, the heartbeat and the bot's acting move
            PawTime.HoldAmbient((float)cfg.holdAmbientScale, 0.6f);
            sweatT = Random.value < (float)cfg.holdSweatChance ? Random.Range(0.5f, 1.4f) : -1f;
        }

        /// <summary>DRAW on screen this frame; <paramref name="cuePresentedNs"/> = the client's photon estimate (DRAW sound / haptic lead).</summary>
        private void PresentDraw(long cuePresentedNs)
        {
            hud.SetCueMode(SwipeCue.Mode.Go);
            cuePresented = true;
            holdPresented = false;
            sweatT = -1f;
            HideBubble();
            cues.ShowDraw();
            // 1-frame hit-stop on the whole presentation, the world snaps back to life
            float hz = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60f;
            PawTime.HitStop(Mathf.Max(1f, (float)cfg.drawHitStopFrames) * 0.9f / hz);
            PawTime.ReleaseAmbient();
            if (!reducedMotion) pipe.FlashScreen(1f, Color.white);
            pipe.Strike();
            // the framing stays exactly where the hold left it: nothing on screen moves at the reaction moment
            pipe.Kick(new Vector2(0f, -1.5f));
            // schedule the DRAW sound at the estimated photon time on the DSP clock
            double ahead = (cuePresentedNs - clock.NowNs) / 1e9;
            if (ahead < 0) ahead = 0;
            audio.PlayDrawAt(AudioSettings.dspTime + ahead);
            HapticMoments.DrawCue(ahead);   // same lead: the haptic lands with the photons (Docs/HAPTICS.md)
            PawTime.FreezeAmbient(Mathf.Max(1, (int)cfg.drawHitStopFrames));
            audio.Duck(0.85f);
            audio.Heartbeat(0f);
            // the cue stays through DRAW (it now says NOW! SWIPE UP) and hides itself on the draw
            cues.SetTimer(0, false);
        }

        // ------------------------------------------------------------------ authority events

        private void DrainEvents()
        {
            DuelEvent e;
            while (authority.TryDequeueEvent(out e))
            {
                switch (e.kind)
                {
                    case DuelEventKind.Shot:
                        QueueShot(e);
                        break;
                    case DuelEventKind.Foul:
                        if (e.slot == 1)
                        {
                            acting.Clear();
                            EndTell();
                            bandit.Play("foul");
                            restAnim[1] = "ready";
                            audio.Play("sfx_misfire", 0.8f);
                            cues.ShowMessage(foulMessage, 1.6f);
                        }
                        else PresentHeroFoul();
                        break;
                    case DuelEventKind.NoShot:
                        if (e.slot == 0 && !noShotShown) { noShotShown = true; TownNoShot(); cues.ShowStamp("stamp_no_shot"); cues.ShowMessage("SWIPE UP FROM THE HOLSTER", 1.8f); hud.ShowHintOnce(); }
                        break;
                    case DuelEventKind.RoundVoid:
                        prevHeroEarly = heroFouledThisRound;
                        if (heroFouledThisRound) itch = BotActing.NextItch(cfg, itch, true);
                        heroFouledThisRound = false;
                        player.Reset();
                        opponent.Client.Reset();
                        holdPresented = false;
                        sweatT = -1f;
                        acting.Clear();
                        EndTell();
                        PawTime.ReleaseAmbient();
                        zoomRelaxing = true;
                        audio.Heartbeat(0f);
                        audio.Duck(1f);
                        cues.ShowNumeral(0);
                        if (State == Pres.Live)
                        {
                            cues.ShowMessage("BOTH JUMPED! REDRAW", 1.6f);
                            voidTimer = 1.6f;
                        }
                        break;
                    case DuelEventKind.RoundResolved:
                        prevHeroEarly = heroFouledThisRound || authority.Round.slots[0].IsFoul;
                        if (e.simultaneous) tieRound = true;
                        resolved = true;
                        if (rules != null) rules.AttemptResolved();
                        settleT = (float)cfg.resultSettleSeconds;
                        itch = BotActing.NextItch(cfg, itch, heroFouledThisRound);
                        acting.Clear();
                        break;
                    case DuelEventKind.MatchOver:
                        matchOver = true;
                        authority.SetDamageMultiplier(1.0);
                        break;
                }
            }
        }

        private void TickPresentation()
        {
            for (int s = 0; s < 2; s++)
            {
                if (pendingImpact[s])
                {
                    pendingAge[s] += PawTime.UnscaledDt;
                    if (pendingAge[s] > 0.6f)   // safety: art without a fire event
                    {
                        if (tieRound) LandTie();
                        else ApplyImpact(s);
                    }
                }
                var a = s == 0 ? hero : bandit;
                bool holdingTell = s == 1 && banditTelling;
                if (!dead[s] && !a.Anim.Playing && !holdingTell) a.Loop(restAnim[s]);
                // the hold: both cats stop breathing (frozen ready pose); feints / tell / fouls still play
                if (holdPresented && !cuePresented && a.IsPlaying("ready")) a.Anim.Speed = 0f;
                else if (a.Anim.Speed == 0f) a.Anim.Speed = 1f;
            }
            TickFalls();
            TickKillPunch();
            TickVignette();

            if (voidTimer > 0f)
            {
                voidTimer -= PawTime.UnscaledDt;
                if (voidTimer <= 0f && State == Pres.Live) BeginStandoff(0.8f, NextBanterContext());
            }

            // round panel only once the shot has been seen: impacts applied, draws finished, falls landed,
            // slow-mo / hit-stop over, then a short settle
            if (State == Pres.Live && resolved && !pendingImpact[0] && !pendingImpact[1] && Settled())
            {
                settleT -= PawTime.UnscaledDt;
                if (settleT <= 0f) ShowRoundResult();
            }
        }

        private bool Settled()
        {
            if (PawTime.InHitStop || PawTime.InSlowMo) return false;
            for (int s = 0; s < 2; s++)
            {
                if (fallStage[s] == 1 || fallStage[s] == 2) return false;
                var a = s == 0 ? hero : bandit;
                if (!dead[s] && a.IsPlaying("draw_fire")) return false;
            }
            return true;
        }

        private void TickFalls()
        {
            for (int s = 0; s < 2; s++)
            {
                if (fallStage[s] != 1 && fallStage[s] != 2) continue;
                var a = s == 0 ? hero : bandit;
                fallT[s] += PawTime.Dt;
                if (fallStage[s] == 1)
                {
                    // reel (hit anim) under slow-mo, then go down
                    if (!a.Anim.Playing || fallT[s] > 0.6f)
                    {
                        a.Play("down");
                        fallStage[s] = 2;
                        fallT[s] = 0f;
                    }
                }
                else if (!a.Anim.Playing || fallT[s] > 1.2f)
                {
                    fallStage[s] = 3;
                    Vector2 feet = s == 1
                        ? new Vector2(layout.BanditFeet.x + 6, layout.BanditFeet.y)
                        : a.CanvasFromCell(new Vector2Int(a.Def.Pivot.x + 10, a.Def.Pivot.y - 14));
                    fx.Dust(feet, s == 1 ? 71 : 92, 3, 14f);
                    audio.Play("sfx_bodyfall", 0.9f);
                    pipe.AddShake(1.5f);
                    HapticMoments.BodyFall();
                }
            }
        }

        /// <summary>One beat of the hold heart: sound, a light haptic tap and a vignette thump, all
        /// growing with the frenzy. Presentation only.</summary>
        private void HoldHeartBeat(int index, float frenzy, bool skipped)
        {
            if (!holdPresented || cuePresented)
            {
                if (State == Pres.Standoff) LastLightStandoffBeat(index);
                return;
            }
            float vol = Mathf.Lerp(0.74f, 1f, frenzy) * (skipped ? 1.1f : 1f) * LastLightHeartGain;
            audio.Play("sfx_heartbeat", Mathf.Min(1f, vol), Mathf.Lerp(1f, 1.12f, frenzy) + Random.Range(-0.015f, 0.015f));
            HapticMoments.HoldBeat(frenzy, skipped);   // lub-dub, gated at frenzy 0.35, 0.3..0.8
            if (!reducedMotion)
            {
                beatThump = (float)cfg.beatVignette * Mathf.Lerp(0.4f, 1f, frenzy) * (skipped ? 1.4f : 1f);
                beatThumpT = 0f;
            }
        }

        private void TickVignette()
        {
            float v = baseVignette;
            // duel focus: a slight dithered vignette eases in once the match starts and stays on
            if (State != Pres.Off)
            {
                duelVignetteT = Mathf.Min(1f, duelVignetteT + PawTime.UnscaledDt / 1.2f);
                v += (float)cfg.duelVignette * Ease.InOutSine(duelVignetteT);
            }
            if (beatThumpT < 0.22f)
            {
                beatThumpT += PawTime.UnscaledDt;
                v += beatThump * (1f - Mathf.Clamp01(beatThumpT / 0.22f));
            }
            if (holdPresented && !cuePresented)
                v += (float)cfg.holdVignette * Ease.InOutSine(Mathf.Clamp01(holdT / 2.5f));
            float secs = (float)cfg.vignettePulseSeconds;
            if (vignettePulseT < secs)
            {
                vignettePulseT += PawTime.UnscaledDt;
                v += vignettePulse * (1f - Mathf.Clamp01(vignettePulseT / secs));
            }
            pipe.Vignette = v;
        }

        private void ShowRoundResult()
        {
            var r = authority.Round;
            var s0 = r.slots[0];
            var s1 = r.slots[1];
            var v = new RoundView();
            v.youName = "YOU";
            v.botName = OpponentName;
            audio.Sting("music_duel_result", 0.7f);
            v.youFoul = s0.IsFoul;
            v.youNoShot = s0.outcome == SlotOutcome.NoShot;
            v.botFoul = s1.IsFoul;
            v.youNs = (!v.youFoul && s0.reactionNs >= 0) ? s0.reactionNs : -1;
            v.botNs = (!v.botFoul && s1.reactionNs >= 0) ? s1.reactionNs : -1;
            v.grade = v.youNs >= 0 ? Grading.FromMs(cfg, DuelConfig.NsToMs(v.youNs)) : Grade.None;
            v.dealt = s0.damageDealt;
            v.taken = s1.damageDealt;
            v.frameQuantized = s0.hasReport && s0.report.frameQuantized;
            v.replay = ReplayMarks.FromRound(r, (float)cfg.replayStripSeconds);
            stampHaptic = StampHaptics.For(v.youFoul, v.youNoShot, v.grade);
            // review: automation (ReviewDirector auto-tap / SimulateSwipe) must never become the all-time BEST
            // (and so never earns the NEW BEST ribbon either)
            if (s0.outcome == SlotOutcome.Shot && !(s0.hasReport && s0.report.inputSource == InputSource.Injected))
                v.newBest = PawSettings.SubmitBest(s0.reactionNs);
            v.tie = r.isTie;
            var outcome = RoundVerdict.Classify(r, 0);
            int run = streak.Record(outcome);
            v.streak = StreakCounter.ShowsTag(outcome, run, FeelConfig.Current.streakMin) ? run : 0;

            cues.HideDraw();
            cues.HideStamp();
            panel.Show(v);
            TownExchangeOver(v.grade);
            if (r.isTie) DecorateTiePanel(r);
            if (panel.ReplayVisible && panel.ReplayBottom > layout.Timer.y - 2) cues.HideTimer();   // card + strip say it already
            audio.Duck(1f);

            // recorded PvP: 3 resolved rounds / attempt cap end the match on HP (a KO already ended it)
            if (rules != null && rules.Check(authority.Match) != RecordedEnd.None && !matchOver)
            {
                authority.Match.Finish(RecordedMatchRules.Winner(authority.Match));
                matchOver = true;
            }

            if (matchOver)
            {
                State = Pres.MatchEnd;
                FinishMatchSoon();
            }
            else
            {
                State = Pres.Result;
                stateTimer = (float)cfg.resultAutoAdvanceSeconds;
                audio.Sting("sting_round", 0.7f);
            }
        }

        /// <summary>Tie on the round panel: both hits on the damage line (the verdict line already says DEAD HEAT +-x ms).</summary>
        private void DecorateTiePanel(RoundRecord r)
        {
            var dmgText = PanelText("Dmg");
            if (dmgText != null)
            {
                int dealt = Mathf.RoundToInt((float)r.slots[0].damageDealt);
                int taken = Mathf.RoundToInt((float)r.slots[1].damageDealt);
                dmgText.SetText("HIT -" + dealt + "  TOOK -" + taken + " HP");
                dmgText.Color = Pal.PaintRed;
                dmgText.MarkDirty();
            }
        }

        private BitmapText PanelText(string child)
        {
            var t = panel != null ? panel.transform.Find(child) : null;
            return t != null ? t.GetComponent<BitmapText>() : null;
        }

        /// <summary>The streak tag popped on the round card: rising 2-note sting (a step higher per win), haptic step,
        /// and from streakRollMin wins in a row "ON A ROLL" on the round sign.</summary>
        private void StreakTagLanded(int run)
        {
            float pitch = 1f + 0.059f * Mathf.Clamp(run - 2, 0, 5);   // ~a semitone per extra win
            audio.Play("sfx_streak", 0.85f, pitch);
            HapticMoments.StreakStep(run);
            int roll = FeelConfig.Current.streakRollMin;
            if (roll > 0 && run >= roll) hud.FlashRoundLabel(OnARollLabel, FeelConfig.Current.rollLabelSeconds);
        }

        /// <summary>The NEW BEST ribbon popped (confetti is the panel's): its sfx and haptic.</summary>
        private void NewBestLanded()
        {
            audio.Play("sfx_new_best", 0.9f);
            HapticMoments.NewBest();
        }

        /// <summary>
        /// Kill-shot punch: once the kill slow-mo (and any hit-stop) of the hit that dropped the opponent is over, one
        /// solid white frame on him and a small downward camera kick (PixelPipeline.Kick). Reduced Motion: nothing.
        /// </summary>
        private void TickKillPunch()
        {
            if (!killPunchPending || PawTime.InSlowMo || PawTime.InHitStop) return;
            killPunchPending = false;
            if (reducedMotion) return;
            var feel = FeelConfig.Current;
            if (feel.killPunchFlashFrames > 0) bandit.FlashFrames(feel.killPunchFlashFrames);
            if (feel.killPunchKickPx > 0f) pipe.Kick(new Vector2(0f, feel.killPunchKickPx));
        }

        private void StampLanded()
        {
            audio.Play("sfx_stamp");
            if (!string.IsNullOrEmpty(panel.StampSound)) audio.Play(panel.StampSound);
            pipe.AddShake(1f);
            // haptic language (SPEC_V2 §8.6): LIGHTNING triple, PERFECT double, GREAT medium, GOOD/SLOW light,
            // TOO EARLY error, NO SHOT warning
            HapticMoments.Stamp(stampHaptic);
        }

        /// <summary>Recorded PvP ended between attempts (after a void): no round panel, straight to the match end.</summary>
        private void EndByRules()
        {
            authority.Match.Finish(RecordedMatchRules.Winner(authority.Match));
            matchOver = true;
            cues.HideAll();
            hud.ShowHint(false);
            State = Pres.MatchEnd;
            FinishMatchSoon();
        }

        private float finishTimer;
        private void FinishMatchSoon()
        {
            finishTimer = 1.8f;
            var m = authority.Match;
            bool won = m.Winner == 0;
            if (won) { hero.Play("win"); restAnim[0] = "win"; }
            if (m.Winner == 1) { bandit.Play("win"); restAnim[1] = "win"; }
            audio.Sting(won ? "sting_win" : "sting_lose");
            if (m.IsDraw)
            {
                // double KO: nobody holsters; the match screen gets deadHeat = true (DRAW)
                cues.ShowMessage("STANDOFF", finishTimer + 0.2f);
                return;
            }
            audio.Play(won ? "sfx_holster" : "sfx_spurs", 0.8f);
            if (m.Winner == 1)
                Say(banter.Pick(OpponentLines, BanterContext.MatchWin, 1.0), (float)cfg.banterMatchEndSeconds);
        }

        /// <summary>Called by the game each frame while State == MatchEnd (unscaled).</summary>
        public void TickMatchEnd()
        {
            if (State != Pres.MatchEnd || finishTimer <= 0f) return;
            finishTimer -= PawTime.UnscaledDt;
            bool tap = UiPointer.Down && !UiPointer.Consumed && finishTimer < 1.2f;
            if (tap) UiPointer.Consumed = true;
            if (finishTimer <= 0f || tap)
            {
                finishTimer = 0f;
                EndLastLight();
                panel.Close();
                cues.HideAll();
                firstMatch = false;
                if (PawSettings.SwipeHint) { PawSettings.SwipeHint = false; PawSettings.Save(); }   // one taught match is enough; Settings can bring it back
                Screen.sleepTimeout = SleepTimeout.SystemSetting;
                var m = authority.Match;
                HapticMoments.MatchEnd(m.Winner == 0, reducedMotion);   // lands with the match screen banner
                if (MatchFinished != null) MatchFinished(m.Winner == 0, m.Winner < 0);
            }
        }

        private void AdvanceFromResult()
        {
            panel.Close();
            cues.HideAll();
            if (authority.NextRound()) BeginStandoff(0.9f, NextBanterContext());
        }
    }
}
