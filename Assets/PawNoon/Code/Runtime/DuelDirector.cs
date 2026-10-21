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
        public enum Pres { Off, Standoff, Live, Result, MatchEnd, Review }

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
        private static readonly Color32 SigOrange = new Color32(0xFF, 0x9C, 0x3D, 255);
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
            State = Pres.Off;
        }

        public void SetReducedMotion(bool on)
        {
            reducedMotion = on;
            if (hud != null) hud.SetReducedMotion(on);
            if (bubble != null) bubble.SetReducedMotion(on);
        }

        /// <summary>The opponent's speech bubble (created on first use under the cues' UI root unless one is supplied).</summary>
        public SpeechBubble Bubble { get { return EnsureBubble(); } }

        /// <summary>Optional: supply a bubble built with the game's shared UiKit (else the director builds its own).</summary>
        public void SetSpeechBubble(SpeechBubble b)
        {
            if (bubble != null && bubble != b) bubble.Hide();
            bubble = b;
            if (bubble != null) bubble.SetReducedMotion(reducedMotion);
        }

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
        private BanterLines OpponentLines { get { return persona != null && persona.lines != null ? persona.lines : cfg.blackpawLines; } }

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
            BeginStandoff(1.4f, BanterContext.MatchStart);
            if (playIntro) intro.Begin(reducedMotion, firstMatch);
            audio.PlayMusic(PawAudio.Track.DuelBed);   // composed standoff bed (falls back to music_standoff)
            audio.DuelAmbience(1f);
            Screen.sleepTimeout = SleepTimeout.NeverSleep;
        }

        public void Stop()
        {
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

        /// <summary>Context for the standoff after a resolved / voided round (reads the last round record).</summary>
        private BanterContext NextBanterContext()
        {
            var r = authority.Round;
            var m = authority.Match;
            bool hasLast = r.roundId > 0 && !r.isVoid;
            bool lightning = hasLast && r.slots[0].outcome == SlotOutcome.Shot && r.slots[0].grade == Grade.Lightning;
            return BanterPicker.Context(false, m.Hp[0], m.Hp[1], cfg.banterLowHp, lightning, prevHeroEarly, hasLast ? r.winnerSlot : -1);
        }

        private SpeechBubble EnsureBubble()
        {
            if (bubble != null) return bubble;
            if (uiCatalog == null) uiCatalog = new SpriteCatalog("Art/UI/", "ui");
            var parent = cues != null ? (cues.transform.parent != null ? cues.transform.parent : cues.transform) : null;
            bubble = SpeechBubble.Create("SpeechBubble", parent, uiCatalog, BitmapFont.Load("font_small", 8), audio, pipe);
            bubble.SetReducedMotion(reducedMotion);
            return bubble;
        }

        private SpriteCatalog UiCatalog
        {
            get
            {
                if (uiCatalog == null) uiCatalog = new SpriteCatalog("Art/UI/", "ui");
                return uiCatalog;
            }
        }

        /// <summary>First canvas y below the duel HUD (portraits + round sign), as ScreenBase.HudBottom.</summary>
        private int HudBottom()
        {
            var ui = UiCatalog;
            var f = ui.TryGet("portrait_frame");
            var rs = ui.TryGet("round_sign");
            return pipe.Safe.y + layout.HudMargin + (f != null ? f.Cell.y : 48) + 2 + (rs != null ? rs.Cell.y : 16) + 4;
        }

        private void Say(string line, float seconds)
        {
            if (string.IsNullOrEmpty(line) || dead[1] || !opponent.HostActing) return;
            var b = EnsureBubble();
            b.MinY = HudBottom();
            var p = bandit.CanvasFromCell(BanterMouthCell);
            b.Show(line, new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), false, seconds);
            if (persona != null) audio.PlayMeow(persona.meow);   // his "meow signature" (SPEC_V2 §8.2)
        }

        private void HideBubble()
        {
            if (bubble != null) bubble.Hide();
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
            if (zoomRelaxing)
            {
                // slow ease-out back to the neutral framing (about 1.2 s); stops when the hold starts a new push-in
                if (holdPresented || cuePresented) zoomRelaxing = false;
                else
                {
                    pipe.Zoom = Mathf.MoveTowards(pipe.Zoom, 1f, PawTime.UnscaledDt * 0.06f);
                    if (Mathf.Abs(pipe.Zoom - 1f) < 0.0005f) { pipe.Zoom = 1f; zoomRelaxing = false; }
                }
            }
            TickPresentation();
        }

        private void TickLive(long frameStartNs, long lat)
        {
            RoundStartMsg rs;
            if (player.PollRoundStart(frameStartNs, out rs))
            {
                lastNumeral = -1;
                ShowRoundSign(rs.roundNumber);
            }

            if (player.PollCue(frameStartNs, lat)) PresentDraw(frameStartNs);

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

        /// <summary>One bead of sweat rolling off the side of the hero's head (seen from behind).</summary>
        private void SweatDrop()
        {
            if (dead[0] || cuePresented) return;
            // cell px of the right side of his head, under the hat brim (hero cell 295x275, pivot at the feet)
            var p = hero.CanvasFromCell(new Vector2Int(173, 60));
            fx.Burst(fx.Fx("sweat_drop"), p, 92, new Vector2(2f, 6f), 70f, 0.9f);
        }

        private void PresentDraw(long frameStartNs)
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
            double ahead = (player.CuePresentedNs - clock.NowNs) / 1e9;
            if (ahead < 0) ahead = 0;
            audio.PlayDrawAt(AudioSettings.dspTime + ahead);
            HapticMoments.DrawCue(ahead);   // same lead: the haptic lands with the photons (Docs/HAPTICS.md)
            PawTime.FreezeAmbient(Mathf.Max(1, (int)cfg.drawHitStopFrames));
            audio.Duck(0.85f);
            audio.Heartbeat(0f);
            // the cue stays through DRAW (it now says NOW! SWIPE UP) and hides itself on the draw
            cues.SetTimer(0, false);
        }

        // ------------------------------------------------------------------ the bot's acting (presentation only)

        private void TickActing(long frameStartNs)
        {
            var r = authority.Round;
            if (State == Pres.Live && r.schedule != null && r.cueDecidedNs < 0 && !r.isVoid && actingRoundId != r.roundId &&
                opponent.Client.InRound && opponent.Client.RoundId == r.roundId)
            {
                actingRoundId = r.roundId;
                if (!opponent.HostActing || opponent.FoulPlanned || dead[1]) acting.Clear();
                else acting.Plan(r.countdownStartNs + r.schedule.CountdownNs, r.countdownStartNs + r.schedule.CueOffsetNs, itch);
            }
            BotActKind k;
            while ((k = acting.Poll(frameStartNs)) != BotActKind.None)
            {
                if (State != Pres.Live || cuePresented || dead[1]) continue;
                if (k == BotActKind.Feint) PresentFeint();
                else PresentTell();
            }
        }

        private void PresentFeint()
        {
            if (dead[1] || cuePresented || banditTelling) return;
            bandit.Feint((float)cfg.feintAnimSpeed);
            audio.Play("sfx_spurs", 0.8f);
            fx.Dust(new Vector2(layout.BanditFeet.x - 6, layout.BanditFeet.y), 71, 1, 6f);
        }

        /// <summary>The late tell: ears flatten a fixed lead before the cue. Silent: a read, not a cue.</summary>
        private void PresentTell()
        {
            if (dead[1] || cuePresented) return;
            banditTelling = true;
            if (tellSheet != null) bandit.Play("tell");
            else
            {
                // no art yet: keep the ready pose and settle 1 px (shoulders drop) so the read still exists
                bandit.Loop("ready");
                bandit.PoseOffset = new Vector2(0f, 1f);
            }
        }

        private void EndTell()
        {
            banditTelling = false;
            bandit.PoseOffset = Vector2.zero;
        }

        private void PresentHeroDraw()
        {
            heroPredictedFire = true;
            audio.Play(heroDrawSfx, 0.9f);
            hud.SetDrawn(true);
            // holster shows empty (frame 2) until the next standoff
            hero.Play("draw_fire");
            restAnim[0] = "aim";
            var d = hero.Sheet("draw_fire");
            if (d == null || d.FireFrame < 0) HeroFired();
            else HapticMoments.Swipe();   // the recoil follows at the fire frame
        }

        private void PresentHeroFoul()
        {
            if (heroFoulShown) return;
            heroFoulShown = true;
            heroFouledThisRound = true;
            hero.Play("foul");
            restAnim[0] = "ready";
            cues.ShowNumeral(0);
            cues.ShowStamp("stamp_too_early");
            audio.Play("sfx_misfire");                 // the dry jam click
            audio.Play("sfx_cock", 0.35f, 0.8f);       // hammer catching, low and dull
            HapticMoments.Foul();
        }

        // ------------------------------------------------------------------ fire / impact

        private void HeroFired()
        {
            if (fired[0] || dead[0]) return;
            fired[0] = true;
            Vector2 m;
            if (!hero.TryMuzzleCanvas(out m)) m = new Vector2(layout.HeroBottomLeft.x + 150, layout.HeroBottomLeft.y - 250);
            muzzleAt[0] = m;
            lastFiredSlot = 0;
            float rm = reducedMotion ? 0.5f : 1f;
            float rk = (float)cfg.heroRecoilPx * rm;
            hero.Impulse(new Vector2(-rk, rk * 0.67f), (float)cfg.recoilSeconds);   // gun kicks back and down-left
            fx.Burst(HeroMuzzle, m, 96, Vector2.zero);
            fx.Sparks(m, 97, 3);
            fx.Smoke(m, 95, 2);
            fx.Shell(new Vector2(m.x - 18, m.y + 16), 96, m.y + 70);
            pipe.FlashScreen(0.22f * heroFlashBoost, heroFlashBoost > 1f ? new Color(0.85f, 0.95f, 1f) : new Color(1f, 0.93f, 0.7f));
            pipe.AddShake((float)cfg.shakeShot);
            pipe.Kick(new Vector2(-1.5f, 1f));
            audio.PlayVariant(heroShotSfx, 3);
            audio.Play("sfx_shell", 0.4f);
            HapticMoments.HeroShot();
            if (pendingImpact[0]) Land(0);
        }

        private void BanditFired()
        {
            if (fired[1] || dead[1]) return;
            fired[1] = true;
            Vector2 m;
            if (!bandit.TryMuzzleCanvas(out m)) m = bandit.HitPointCanvas + new Vector2(-10, 4);
            muzzleAt[1] = m;
            lastFiredSlot = 1;
            bandit.Impulse(new Vector2(0f, reducedMotion ? 0f : -1f), (float)cfg.recoilSeconds);
            fx.Burst(fx.Fx("muzzle_front"), m, 76, Vector2.zero);
            fx.Sparks(m, 76, 2);
            fx.Smoke(m, 74, 2);
            pipe.FlashScreen(0.3f, new Color(1f, 0.95f, 0.75f));
            pipe.AddShake((float)cfg.shakeShot * 0.6f);
            if (!reducedMotion)
            {
                vignettePulse = (float)cfg.banditShotVignette;
                vignettePulseT = 0f;
            }
            audio.PlayVariant("sfx_gun_bandit", 3);
            if (pendingImpact[1]) Land(1);
        }

        private void QueueShot(DuelEvent e)
        {
            int s = e.slot;
            pendingImpact[s] = true;
            pendingEvent[s] = e;
            pendingAge[s] = 0f;
            if (e.simultaneous) tieRound = true;
            var shooter = s == 0 ? hero : bandit;
            if (fired[s]) { Land(s); return; }
            var draw = shooter.Sheet("draw_fire");
            if (s == 0)
            {
                if (!heroPredictedFire) PresentHeroDraw();
            }
            else
            {
                EndTell();
                restAnim[1] = "aim";
                int start = draw != null && draw.FireFrame > 0 ? draw.FireFrame - 1 : 0;
                bandit.Play("draw_fire", start);
                if (draw == null || draw.FireFrame < 0) BanditFired();
            }
        }

        /// <summary>A shot's impact is due (its shooter fired). In a tie both impacts wait for the second trigger pull.</summary>
        private void Land(int s)
        {
            if (!pendingImpact[s]) return;
            if (tieRound)
            {
                if (fired[0] && fired[1]) LandTie();
                return;
            }
            ApplyImpact(s);
        }

        /// <summary>Both tied shots hit at the moment of the second shot, then the near-miss beat.</summary>
        private void LandTie()
        {
            if (tieLanded) return;
            tieLanded = true;
            int first = lastFiredSlot == 0 ? 1 : 0;   // the shooter whose flash is already fading
            if (pendingImpact[0]) ApplyImpact(0);
            if (pendingImpact[1]) ApplyImpact(1);
            TieDrama(first);
        }

        private void TieDrama(int firstShooter)
        {
            // both muzzle flashes hang in the air together: re-light the earlier one under the slow-mo
            if (fired[firstShooter])
            {
                if (firstShooter == 0) fx.Burst(HeroMuzzle, muzzleAt[0], 96, Vector2.zero);
                else fx.Burst(fx.Fx("muzzle_front"), muzzleAt[1], 76, Vector2.zero);
            }
            float secs = (float)cfg.tieSlowMoSeconds;
            PawTime.SlowMo((float)cfg.tieSlowMoScale, secs);
            // cfg.tieDesaturate is unused until PixelPipeline grows a desaturation control (the reflection probe
            // that looked for one was removed in the 21 Oct cleanup: the pipeline never had it)
            if (UiCatalog.Has("stamp_too_close")) cues.ShowStamp("stamp_too_close");
            else cues.ShowMessage("TOO CLOSE!", secs + 0.9f);
            audio.Play("sfx_lightning");
            HapticMoments.Tie();
        }

        private void ApplyImpact(int shooterSlot)
        {
            if (!pendingImpact[shooterSlot]) return;
            pendingImpact[shooterSlot] = false;
            var e = pendingEvent[shooterSlot];
            int victim = 1 - shooterSlot;
            var v = victim == 0 ? hero : bandit;
            var hit = v.HitPointCanvas;
            if (victim == 1) EndTell();
            float rm = reducedMotion ? 0.5f : 1f;
            v.Flash(0.12f);
            if (victim == 1)
            {
                float kb = (float)cfg.knockBanditPx * rm;          // pushed away from the camera, up-right
                v.Impulse(new Vector2(kb * 0.8f, -kb * 0.6f), (float)cfg.knockSeconds);
            }
            else
            {
                float kh = (float)cfg.knockHeroPx * rm;            // pushed back toward the camera
                v.Impulse(new Vector2(-kh * 0.34f, kh), (float)cfg.knockSeconds);
            }
            fx.Burst(fx.Fx("hit_puff"), hit, victim == 0 ? 97 : 76, Vector2.zero);
            fx.Burst(fx.Fx("flash_ring"), hit, victim == 0 ? 96 : 75, Vector2.zero);
            fx.Sparks(hit, victim == 0 ? 97 : 76, 4);
            fx.Pixels(hit, victim == 0 ? 97 : 76, 6, victim == 0 ? Pal.Sand0 : Pal.Steel1, 70f, 160f);
            PawTime.HitStop((float)cfg.hitStopMs / 1000f);
            pipe.AddShake((float)cfg.shakeHit);
            audio.Play("sfx_hit");
            audio.Play("sfx_hit_cat", 0.9f, victim == 0 ? 1.08f : 0.92f);
            audio.Play("sfx_hp_tick", 0.5f);
            if (victim == 1) HapticMoments.HitDealt(e.killed);
            else HapticMoments.HitTaken(e.killed);
            if (victim == 0) pipe.HeroHurt(); else pipe.PopAt(hit);
            hud.Hit(victim, (float)(e.targetHpAfter / cfg.maxHp));
            if (e.killed)
            {
                // killing blow: reel in slow-mo, then fall; dust + body-fall land with the body (TickFalls)
                dead[victim] = true;
                fallStage[victim] = 1;
                fallT[victim] = 0f;
                v.Play("hit");
                PawTime.SlowMo((float)cfg.killSlowMoScale, (float)cfg.killSlowMoSeconds);
                pipe.PushTo(hit, 0.02f, (float)cfg.killSlowMoSeconds + 0.25f);
            }
            else
            {
                v.Play("hit");
            }
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
                        if (e.slot == 0 && !noShotShown) { noShotShown = true; cues.ShowStamp("stamp_no_shot"); hud.ShowHintOnce(); }
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
            if (s0.outcome == SlotOutcome.Shot && !(s0.hasReport && s0.report.inputSource == InputSource.Injected))
                PawSettings.SubmitBest(s0.reactionNs);

            cues.HideDraw();
            cues.HideStamp();
            panel.Show(v);
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

        /// <summary>Tie line on the round panel: "±x.xx ms  TOO CLOSE" in sigOrange, and both hits.</summary>
        private void DecorateTiePanel(RoundRecord r)
        {
            var gapText = PanelText("Gap");
            long gapNs;
            if (gapText != null && r.TryGetGapNs(out gapNs))
            {
                Glyph g;
                bool hasPm = gapText.Font != null && !gapText.Font.Fallback && gapText.Font.TryGet('\u00B1', out g);
                gapText.SetText((hasPm ? "\u00B1" : "+-") + NumFmt.Ms2(gapNs) + " ms  TOO CLOSE");
                gapText.Color = SigOrange;
                gapText.MarkDirty();
            }
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

        // ------------------------------------------------------------------ review (deterministic stills)

        public void EnterReview()
        {
            State = Pres.Review;
            intro.Cancel();
            pipe.SetTension(0f);
            PawTime.ClearEffects();
            pipe.Zoom = 1f;
            pipe.Vignette = baseVignette;
            vignettePulseT = 99f;
            acting.Clear();
            EndTell();
            HideBubble();
            EndLastLight();
            banterLine = null;
            sweatT = -1f;
            fallStage[0] = fallStage[1] = 0;
            audio.Duck(1f);
            audio.Heartbeat(0f);
            cues.HideAll();
            panel.Close();
            dead[0] = dead[1] = false;
            hero.ResetPose();
            bandit.ResetPose();
            hud.ShowHint(false);
        }

        public void ReviewCountdown(int numeral)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            hero.Play("ready");
            bandit.Play("ready");
            cues.ShowNumeral(numeral);
            hud.ShowHolster(true);
            hud.ShowHint(true);
        }

        public void ReviewDraw(long timerNs)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            hero.Play("ready");
            bandit.Play("ready");
            cues.ShowDraw();
            cues.SetTimer(timerNs, false);
        }

        public void ReviewFoul()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(2);
            hud.SetHpImmediate(0, 0.76f);
            hud.SetHpImmediate(1, 0.66f);
            hero.Play("foul");
            bandit.Play("ready");
            cues.ShowStamp("stamp_too_early");
        }

        public void ReviewRoundResult()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            long you = 214382000L, botNs = 288105000L;
            double dealt = DamageCurve.Evaluate(cfg, DuelConfig.NsToMs(you));
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, (float)((cfg.maxHp - dealt) / cfg.maxHp));
            hero.Play("aim");
            bandit.Play("hit", 3);
            cues.SetTimer(you, true);
            var v = new RoundView();
            v.youName = "YOU";
            v.botName = OpponentName;
            v.youNs = you;
            v.botNs = botNs;
            v.grade = Grading.FromMs(cfg, DuelConfig.NsToMs(you));
            v.dealt = dealt;
            stampHaptic = StampHaptics.For(false, false, v.grade);
            v.replay = ReplayMarks.Shots(you, botNs, (float)cfg.replayStripSeconds);
            panel.Show(v);
            panel.ShowStampNow();
        }

        public void ReviewMatchEnd(bool win)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(4);
            hud.SetHpImmediate(0, win ? 0.52f : 0f);
            hud.SetHpImmediate(1, win ? 0f : 0.37f);
            if (win) { hero.Play("win"); bandit.Play("down", 99); }
            else { hero.Play("down", 99); bandit.Play("win"); }
        }

        public string StatusLine()
        {
            var m = authority.Match;
            var r = authority.Round;
            string you = r.slots[0].reactionNs >= 0 ? NumFmt.Seconds6(r.slots[0].reactionNs) : "--";
            string b = r.slots[1].reactionNs >= 0 ? NumFmt.Seconds6(r.slots[1].reactionNs) : "--";
            return "pres=" + State + " phase=" + authority.Phase + " round=" + m.RoundNumber +
                   " hp=" + Mathf.RoundToInt((float)m.Hp[0]) + "/" + Mathf.RoundToInt((float)m.Hp[1]) +
                   " wins=" + m.RoundWins[0] + "-" + m.RoundWins[1] +
                   " last=" + you + "s vs " + b + "s" +
                   " outcomes=" + r.slots[0].outcome + "/" + r.slots[1].outcome +
                   " tie=" + r.isTie +
                   " over=" + m.Over + " winner=" + m.Winner +
                   " autoTapMs=" + (autoTapNs < 0 ? "off" : (autoTapNs / 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) +
                   " seed=" + seed + " rejected=" + authority.RejectedReports +
                   " lastLight=" + lastLight + " dmgMult=" + authority.Round.damageMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   "/next " + authority.PendingDamageMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   (rules != null ? " pvp=attempts:" + rules.Attempts + ",resolved:" + rules.Resolved + ",end:" + RecordedMatchRules.ToWire(rules.EndReason) : "");
        }
    }
}
