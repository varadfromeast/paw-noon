using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Root of the game. Built entirely from code after the (empty) scene loads. Owns the core
    /// (authority, clients, clock) and all presentation systems, and runs the frame in a fixed order.
    /// </summary>
    [DefaultExecutionOrder(-500)]
    public sealed class PawGame : MonoBehaviour
    {
        public enum Mode { Title, Duel, MatchResult }

        public static PawGame Instance { get; private set; }

        public Mode CurrentMode { get; private set; }
        public DuelConfig Config { get; private set; }
        public GameLayout Layout { get; private set; }
        public StopwatchClock Clock { get; private set; }
        public PixelPipeline Pipeline { get; private set; }
        public DuelDirector Director { get; private set; }
        public SwipeDraw Input { get; private set; }
        public PawAudio Audio { get; private set; }
        public StreetScene Street { get; private set; }

        private PixelParticles particles;
        private DuelHud hud;
        private DuelCues cues;
        private TitleScreen title;
        private SettingsScreen settings;
        private ResultPanel resultPanel;
        private MatchResultScreen matchScreen;
        private DuelActor hero;
        private DuelActor bandit;
        private SpriteRenderer banditShadow;
        private int boundsVersion = -1;
        private long bestAtMatchStart = -1;
        private bool reviewSettings;
        // difficulty (SPEC_V2 §1): Hard = duel_config.json, Easy = duel_config.easy.json merged on top
        private string hardJson;
        private string easyOverlayJson;
        private DuelConfig pendingConfig;   // rules picked mid-match, applied at the next StartDuel

        /// <summary>Difficulty of the rules the next / current match uses ("easy" / "hard").</summary>
        public string Difficulty { get { return pendingConfig != null ? pendingConfig.Difficulty : Config.Difficulty; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (Instance != null) return;
            if (FindAnyObjectByType<PawGame>() != null) return;
            var go = new GameObject("PawNoon");
            DontDestroyOnLoad(go);
            go.AddComponent<PawGame>();
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            ConfigureApp();
            Build();
        }

        private static void ApplyFrameRate()
        {
            double hz = 60.0;
            try { hz = Screen.currentResolution.refreshRateRatio.value; } catch (System.Exception) { hz = 60.0; }
            if (double.IsNaN(hz) || hz < 60.0) hz = 60.0;
            Application.targetFrameRate = Mathf.RoundToInt((float)hz);
        }

        private static void ConfigureApp()
        {
            QualitySettings.vSyncCount = 0;
            ApplyFrameRate();
            QualitySettings.maxQueuedFrames = 1;
            try { UnityEngine.Input.multiTouchEnabled = false; } catch (System.InvalidOperationException) { }
            Screen.sleepTimeout = SleepTimeout.SystemSetting;
            Screen.orientation = ScreenOrientation.Portrait;
        }

        private void Build()
        {
            // --- config & layout
            var ta = Resources.Load<TextAsset>("Data/duel_config");
            string json = ta != null ? ta.text : null;
            if (ta == null) ArtLib.Warn("cfg", "Missing Resources/Data/duel_config.json -> built-in defaults");
            var easyTa = Resources.Load<TextAsset>("Data/duel_config.easy");
            if (easyTa == null) ArtLib.Warn("cfg.easy", "Missing Resources/Data/duel_config.easy.json -> Easy plays as Hard");
            hardJson = json;
            easyOverlayJson = easyTa != null ? easyTa.text : null;
            PawSettings.Load();
            Config = BuildConfig(PawSettings.Difficulty);
            Layout = GameLayout.FromJson(PnJson.Parse(json));
            Layout.ApplyEnvironment(ArtLib.Json("Art/Environment/environment"));
            Px.SetCanvas(Layout.Canvas);

            // --- render pipeline
            var pgo = new GameObject("Pipeline");
            pgo.transform.SetParent(transform, false);
            Pipeline = pgo.AddComponent<PixelPipeline>();
            Pipeline.Init(Layout, Pal.Sky3);

            // --- audio
            var ago = new GameObject("Audio");
            ago.transform.SetParent(transform, false);
            Audio = ago.AddComponent<PawAudio>();
            Audio.Init();

            // --- world
            var world = new GameObject("World").transform;
            world.SetParent(transform, false);
            var fxCatalog = new SpriteCatalog("Art/FX/", "fx");
            var partGo = new GameObject("Particles");
            partGo.transform.SetParent(world, false);
            particles = partGo.AddComponent<PixelParticles>();
            particles.Init(160, fxCatalog);
            particles.SetBounds(Pipeline.Visible);

            var sgo = new GameObject("Street");
            sgo.transform.SetParent(world, false);
            Street = sgo.AddComponent<StreetScene>();
            Street.Init(Layout, particles, Audio);

            var heroDef = CharacterDef.Load("Art/Characters/Hero/", "hero", "hero", new Vector2Int(200, 300), new Vector2Int(0, 300), new Vector2Int(110, 120));
            var banditDef = CharacterDef.Load("Art/Characters/Bandit/", "bandit", "bandit", new Vector2Int(128, 128), new Vector2Int(64, 124), new Vector2Int(64, 64));
            bandit = DuelActor.Create("Bandit", banditDef, Layout.BanditFeet, 70, world);
            hero = DuelActor.Create("Hero", heroDef, Layout.HeroBottomLeft, 90, world);
            var shadowDef = fxCatalog.TryGet("shadow_bandit");
            banditShadow = PawGfx.NewSprite("BanditShadow", world, 69);
            if (shadowDef != null) banditShadow.sprite = shadowDef.First;
            else banditShadow.enabled = false;
            PawGfx.Place(banditShadow, Layout.BanditFeet.x, Layout.BanditFeet.y);
            particles.SpawnMotes(26, 66);

            // --- UI kit
            var kit = new UiKit();
            kit.Ui = new SpriteCatalog("Art/UI/", "ui");
            kit.Small = BitmapFont.Load("font_small", 8);
            kit.Medium = BitmapFont.Load("font_medium", 10);
            kit.Timer = BitmapFont.Load("font_timer", 17);
            kit.Audio = Audio;

            var ui = new GameObject("UI").transform;
            ui.SetParent(transform, false);
            hud = NewChild<DuelHud>("Hud", ui);
            hud.MaxHp = (float)Config.maxHp;
            hud.Init(kit, Layout, Pipeline, heroDef, banditDef, "YOU", Config.botName);
            cues = NewChild<DuelCues>("Cues", ui);
            cues.Init(kit, Layout);
            resultPanel = NewChild<ResultPanel>("RoundResult", ui);
            resultPanel.Setup(kit, Layout, Pipeline);
            title = NewChild<TitleScreen>("Title", ui);
            title.Setup(kit, Layout, Pipeline);
            settings = NewChild<SettingsScreen>("Settings", ui);
            settings.Setup(kit, Layout, Pipeline);
            settings.Changed = ApplySettings;
            matchScreen = NewChild<MatchResultScreen>("MatchResult", ui);
            matchScreen.Setup(kit, Layout, Pipeline);
            Pipeline.SetOverlayRoot(ui);
            hero.Anim.Jitter = 0.35f;
            bandit.Anim.Jitter = 0.3f;

            // --- core (rules authority + clients) and input
            Clock = new StopwatchClock();
            int seed = System.Environment.TickCount & 0x7fffffff;
            var transport = new LoopbackTransport();
            var authority = new LocalAuthority(Config, Clock, transport, seed);
            var player = new DuelClient(0, transport, Config);
            var bot = new BotClient(1, transport, Config, seed ^ 0x5bd1e995);
            Input = new SwipeDraw(Clock);
            Input.Enable();
            Director = new DuelDirector(Config, Layout, Clock, authority, player, bot, Input, hero, bandit, particles, cues, hud,
                resultPanel, Audio, Pipeline, seed);
            Director.MatchFinished = OnMatchFinished;

            Audio.StartAmbience();
            ApplySettings();
            ApplyDifficultyTags();
            ShowTitle();
        }

        // ------------------------------------------------------------------ difficulty

        private DuelConfig BuildConfig(string difficulty)
        {
            string d = DuelConfig.NormalizeDifficulty(difficulty);
            return DuelConfig.Load(hardJson, d == DuelConfig.DifficultyEasy ? easyOverlayJson : null, d);
        }

        /// <summary>
        /// Stores the difficulty (PlayerPrefs "pn_difficulty") and reloads the rules. The config object is
        /// shared by reference with the authority, clients, bot and director, so it is updated in place:
        /// immediately on the title screen, otherwise at the next StartDuel (a running match keeps its rules).
        /// </summary>
        public void SetDifficulty(string difficulty)
        {
            PawSettings.SaveDifficulty(difficulty);
            var next = BuildConfig(PawSettings.Difficulty);
            if (CurrentMode == Mode.Title)
            {
                Config.CopyFrom(next);
                pendingConfig = null;
                ApplyDifficultyTags();
            }
            else
            {
                pendingConfig = next;
            }
            if (title.IsOpen)
            {
                if (title.ChoiceOpen) title.CloseChoice();
                else title.RefreshDifficulty();
            }
        }

        private void ApplyPendingConfig()
        {
            if (pendingConfig == null) return;
            Config.CopyFrom(pendingConfig);
            pendingConfig = null;
            ApplyDifficultyTags();
        }

        private void ApplyDifficultyTags()
        {
            hud.SetDifficulty(Config.IsEasy);
            resultPanel.SetDifficulty(Config.IsEasy);
        }

        /// <summary>Review: title with the first-launch EASY / HARD card; clear = also forget the stored pick.</summary>
        public void ShowDifficultyChoice(bool clear)
        {
            if (clear) PawSettings.ClearDifficulty();
            else title.ForceChoice();
            ShowTitle();
        }

        private static T NewChild<T>(string name, Transform parent) where T : Component
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.AddComponent<T>();
        }

        public void ApplySettings()
        {
            Audio.MusicOn = PawSettings.Music;
            Audio.SfxOn = PawSettings.Sfx;
            Audio.SetMusicStyle(PawSettings.ClassicMusic);
            PawHaptics.Enabled = PawSettings.Haptics;
            Pipeline.ReducedMotion = PawSettings.ReducedMotion;
            Street.SetReducedMotion(PawSettings.ReducedMotion);
            cues.SetReducedMotion(PawSettings.ReducedMotion);
            Director.SetReducedMotion(PawSettings.ReducedMotion);
        }

        // ------------------------------------------------------------------ modes

        public void ShowTitle()
        {
            CurrentMode = Mode.Title;
            reviewSettings = false;
            Screen.sleepTimeout = SleepTimeout.SystemSetting; // the duel keeps the screen awake; menus must not
            Director.Stop();
            ApplyPendingConfig();
            hud.SetVisible(false);
            cues.HideAll();
            resultPanel.Close();
            matchScreen.Close();
            settings.Close();
            title.Open();
            Street.SetDawn(1f);
            hero.ResetPose();
            bandit.ResetPose();
            hero.Play("idle");
            bandit.Play("idle");
            Audio.PlayMusic(PawAudio.Track.Title);
        }

        public void StartDuel()
        {
            title.Close();
            settings.Close();
            matchScreen.Close();
            if (title.ChoiceOpen) title.CloseChoice();
            ApplyPendingConfig();
            CurrentMode = Mode.Duel;
            Street.SetDawn(0f);
            bestAtMatchStart = PawSettings.BestNs;
            Director.StartMatch();
        }

        public void OpenSettings()
        {
            settings.Open();
            settings.Refresh();
        }

        private void OnMatchFinished(bool won, bool deadHeat)
        {
            CurrentMode = Mode.MatchResult;
            var m = Director.Authority.Match;
            bool newBest = PawSettings.BestNs > 0 && (bestAtMatchStart < 0 || PawSettings.BestNs < bestAtMatchStart);
            matchScreen.Show(won, deadHeat, m.BestNs(0), m.AverageNs(0), PawSettings.BestNs, newBest);
        }

        /// <summary>Review: show the match result screen with deterministic sample values.</summary>
        public void ShowMatchResultSample(bool win)
        {
            title.Close();
            settings.Close();
            CurrentMode = Mode.MatchResult;
            Director.ReviewMatchEnd(win);
            matchScreen.Show(win, false, 198412000L, 244019000L, 191233000L, false);
        }

        public void ShowSettingsSample()
        {
            ShowTitle();
            reviewSettings = true;
            OpenSettings();
        }

        /// <summary>Review: enter duel mode for deterministic stills (director in Review state).</summary>
        public void EnterReviewDuel()
        {
            title.Close();
            settings.Close();
            matchScreen.Close();
            CurrentMode = Mode.Duel;
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            long frameStart = Clock.NowNs;
            PawTime.Begin(Time.unscaledDeltaTime);
            UiPointer.Update(Pipeline);
            Input.BeginFrame(frameStart);
            Audio.SetPitch(Mathf.Lerp(0.72f, 1f, Mathf.InverseLerp(0.35f, 1f, PawTime.SlowMoFactor)));
#if UNITY_EDITOR
            EditorKeys();
#endif
            if (boundsVersion != Pipeline.LayoutVersion)
            {
                boundsVersion = Pipeline.LayoutVersion;
                particles.SetBounds(Pipeline.Visible);
            }

            switch (CurrentMode)
            {
                case Mode.Title:
                    Input.Clear();
                    if (settings.IsOpen)
                    {
                        title.Tick(false);
                        if (settings.Tick() == UiAction.CloseSettings) settings.Close();
                    }
                    else
                    {
                        var a = title.Tick(true);
                        if (a == UiAction.Play) StartDuel();
                        else if (a == UiAction.OpenSettings) OpenSettings();
                        else if (a == UiAction.ChooseEasy) SetDifficulty(DuelConfig.DifficultyEasy);
                        else if (a == UiAction.ChooseHard) SetDifficulty(DuelConfig.DifficultyHard);
                    }
                    break;
                case Mode.Duel:
                    Director.Tick(frameStart);
                    break;
                case Mode.MatchResult:
                    Director.Tick(frameStart);
                    var r = matchScreen.Tick();
                    if (r == UiAction.Rematch) StartDuel();
                    else if (r == UiAction.Menu) ShowTitle();
                    break;
            }
        }

#if UNITY_EDITOR
        private void EditorKeys()
        {
            if (UnityEngine.Input.GetKeyDown(KeyCode.F1)) ReviewDirector.Show("title");
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F2)) ReviewDirector.Show("countdown2");
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F3)) ReviewDirector.Show("draw");
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F4)) ReviewDirector.Show("roundresult");
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F5)) ReviewDirector.Show("win");
            else if (UnityEngine.Input.GetKeyDown(KeyCode.F6)) ReviewDirector.Show("settings");
        }
#endif

        // ------------------------------------------------------------------ lifecycle

        private bool appPaused;
        private bool appUnfocused;

        /// <summary>
        /// Backgrounded / interrupted (call, control centre): void any live round, silence music + ambience
        /// (AudioListener.pause keeps every source's play position), and resume cleanly afterwards.
        /// Outside the editor only (clicking away from the Game view must not mute the editor).
        /// </summary>
        private void ApplyLifecycle()
        {
#if !UNITY_EDITOR
            bool silent = appPaused || appUnfocused;
            if (silent)
            {
                if (Director != null) Director.VoidForLifecycle();
                AudioListener.pause = true;
            }
            else
            {
                AudioListener.pause = false;
                ApplyFrameRate();
                if (Input != null) Input.Clear();
            }
#endif
        }

        private void OnApplicationPause(bool paused)
        {
            appPaused = paused;
            ApplyLifecycle();
        }

        private void OnApplicationFocus(bool focused)
        {
            appUnfocused = !focused;
            ApplyLifecycle();
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            AudioListener.pause = false;
            if (Input != null) Input.Dispose();
        }

        public string SettingsOpenState { get { return settings.IsOpen ? (reviewSettings ? "review" : "open") : "closed"; } }
    }
}
