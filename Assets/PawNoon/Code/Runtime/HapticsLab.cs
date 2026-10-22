using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Haptics lab: a debug screen listing every haptic moment of the game as a button. A press plays it on the
    /// device (on finger-down, so nothing else is felt first), so each one can be judged on its own.
    /// Open: <c>ReviewDirector.Show haptics</c> (bridge / editor), or on a development build hold a finger on the
    /// top-left corner of the title screen for 1.5 s (release builds: bridge only). CLOSE (or any other Show) returns to the title.
    /// Debug only; self-contained (own MonoBehaviour after PawGame's frame, own UI kit, created on first open).
    /// </summary>
    [DefaultExecutionOrder(-400)]
    public sealed class HapticsLab : MonoBehaviour
    {
        private const int O = 900;                 // above every other overlay (settings 300)
        private const float GestureSeconds = 1.5f;
        private const int GestureSize = 44;
        private const float PlayAllGap = 1.1f;

        private struct Entry
        {
            public string Label;
            public string Note;
            public System.Action Play;
            public Entry(string label, string note, System.Action play) { Label = label; Note = note; Play = play; }
        }

        private static Entry[] entries;
        private static HapticsLab instance;

        private PawGame game;
        private Transform root;
        private UiKit kit;
        private SpriteRenderer bg;
        private BitmapText title, backend, note;
        private LabButton[] buttons;
        private LabButton playAll, close;
        private bool open;
        private int laidOut = -1;
        private float gestureT;
        private int playAllIndex = -1;
        private float playAllT;

        /// <summary>True while the lab is on screen.</summary>
        public static bool IsOpen { get { return instance != null && instance.open; } }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (instance != null) return;
            var go = new GameObject("HapticsLab");
            DontDestroyOnLoad(go);
            instance = go.AddComponent<HapticsLab>();
        }

        // ------------------------------------------------------------------ catalog

        private static Entry[] Entries()
        {
            if (entries != null) return entries;
            entries = new[]
            {
                new Entry("TICK 3", "countdown 3: tap 0.40 / sharp 0.55", () => HapticMoments.CountdownTick(3)),
                new Entry("TICK 2", "countdown 2: tap 0.55 / sharp 0.60", () => HapticMoments.CountdownTick(2)),
                new Entry("TICK 1", "countdown 1: tap 0.70 / sharp 0.65", () => HapticMoments.CountdownTick(1)),
                new Entry("HEART .40", "hold beat, frenzy 0.40: lub-dub x0.35", () => HapticMoments.HoldBeat(0.40f, false)),
                new Entry("HEART .70", "hold beat, frenzy 0.70: lub-dub x0.55", () => HapticMoments.HoldBeat(0.70f, false)),
                new Entry("HEART MAX", "hold beat, frenzy 1.0: lub-dub x0.80", () => HapticMoments.HoldBeat(1f, false)),
                new Entry("HEART SKIP", "skipped beat, frenzy 0.50: x0.50", () => HapticMoments.HoldBeat(0.5f, true)),
                new Entry("DRAW CUE", "DRAW: crack + rumble, 1 frame ahead", PlayDraw),
                new Entry("SWIPE", "swipe: leather swish + click", HapticMoments.Swipe),
                new Entry("SHOT", "your shot: crack + 70 ms recoil", HapticMoments.HeroShot),
                new Entry("SHOT+HIT", "shot, then the tock as it lands", () => { HapticMoments.HeroShot(); HapticMoments.HitDealt(false); }),
                new Entry("SHOT+KILL", "shot, tock, slow-mo rumble", () => { HapticMoments.HeroShot(); HapticMoments.HitDealt(true); }),
                new Entry("HIT TAKEN", "you are hit: low body blow", () => HapticMoments.HitTaken(false)),
                new Entry("KILLED", "you go down: blow + long fade", () => HapticMoments.HitTaken(true)),
                new Entry("BODY FALL", "a cat hits the dirt", HapticMoments.BodyFall),
                new Entry("TOO EARLY", "foul: jam click + hammer", HapticMoments.Foul),
                new Entry("TOO CLOSE", "tie: two cracks + ring", HapticMoments.Tie),
                new Entry("LAST LIGHT", "monitor spike after the flat line", HapticMoments.LastLightSpike),
                new Entry("NAME PLATE", "intro plate slam", () => HapticMoments.NamePlate(1)),
                new Entry("UI TAP", "button press", HapticMoments.UiTap),
                new Entry("LIGHTNING", "stamp: heavy x3, 65 ms", () => HapticMoments.Stamp(StampHaptics.Lightning)),
                new Entry("PERFECT", "stamp: medium + heavy, 85 ms", () => HapticMoments.Stamp(StampHaptics.Perfect)),
                new Entry("GREAT", "stamp: medium", () => HapticMoments.Stamp(StampHaptics.Great)),
                new Entry("GOOD/SLOW", "stamp: light", () => HapticMoments.Stamp(StampHaptics.Plain)),
                new Entry("STAMP EARLY", "stamp: notify error", () => HapticMoments.Stamp(StampHaptics.TooEarly)),
                new Entry("NO SHOT", "stamp: notify warning", () => HapticMoments.Stamp(StampHaptics.NoShot)),
                new Entry("STREAK x2", "streak tag: two rising taps", () => HapticMoments.StreakStep(2)),
                new Entry("NEW BEST", "best time: 3 sparkle taps + swell", HapticMoments.NewBest),
                new Entry("WIN", "match won: rising taps + swell", () => HapticMoments.MatchEnd(true, PawHaptics.ReducedMotion)),
                new Entry("LOSE", "match lost: drop + slow fade", () => HapticMoments.MatchEnd(false, PawHaptics.ReducedMotion)),
            };
            return entries;
        }

        private static void PlayDraw()
        {
            double hz = Application.targetFrameRate > 0 ? Application.targetFrameRate : 60.0;
            HapticMoments.DrawCue(1.0 / hz);
        }

        /// <summary>Names for the bridge (lower case, no spaces / punctuation): tick3, heart70, drawcue, "SHOT+HIT" = shothit ...</summary>
        public static string Names()
        {
            var e = Entries();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < e.Length; i++) { if (i > 0) sb.Append(", "); sb.Append(Key(e[i].Label)); }
            return sb.ToString();
        }

        private static string Key(string label)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in label.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        /// <summary>Plays one entry by name (bridge: ReviewDirector.Haptic). Returns a status line.</summary>
        public static string Play(string name)
        {
            string k = Key(name ?? "");
            var e = Entries();
            for (int i = 0; i < e.Length; i++)
            {
                if (Key(e[i].Label) != k) continue;
                PawHaptics.Prepare();
                e[i].Play();
                if (instance != null && instance.note != null) instance.note.SetText(e[i].Label + ": " + e[i].Note);
                return "ok: " + e[i].Label + " (" + e[i].Note + ") backend=" + PawHaptics.Backend +
                       (PawHaptics.Enabled ? "" : " HAPTICS OFF") + (PawHaptics.ReducedMotion ? " reducedMotion" : "");
            }
            return "error: unknown haptic '" + name + "' (use " + Names() + ")";
        }

        // ------------------------------------------------------------------ open / close

        /// <summary>Opens the lab (review mode under it, so taps never reach the game). Returns a status line.</summary>
        public static string Open()
        {
            if (instance == null) Boot();
            var g = PawGame.Instance;
            if (g == null) return "error: game not running (enter Play mode)";
            instance.Show(g);
            return "ok: haptics lab (" + Entries().Length + " haptics) backend=" + PawHaptics.Backend;
        }

        /// <summary>Closes the lab without changing the screen under it (another review still takes over).</summary>
        public static void Close()
        {
            if (IsOpen) instance.Hide(false);
        }

        private void Show(PawGame g)
        {
            game = g;
            if (root == null) Build();
            g.EnterReviewDuel();
            g.Director.EnterReview();
            root.gameObject.SetActive(true);
            open = true;
            playAllIndex = -1;
            Layout();
            PawHaptics.Prepare();
            RefreshStatus();
            note.SetText("TAP A ROW TO FEEL IT");
        }

        private void Hide(bool toTitle)
        {
            open = false;
            playAllIndex = -1;
            if (root != null) root.gameObject.SetActive(false);
            if (toTitle && game != null) game.ShowTitle();
        }

        // ------------------------------------------------------------------ frame

        private void Update()
        {
            var g = PawGame.Instance;
            if (g == null) return;
            game = g;
            // the duel director ticks timed patterns in a duel; on the title (bridge Haptic calls) the lab does
            if (g.CurrentMode == PawGame.Mode.Title) PawHaptics.Tick(Time.unscaledDeltaTime);
            if (!open)
            {
                WatchGesture(g);
                return;
            }
            // any other screen taking over (ReviewDirector.Show, a match) closes the lab
            if (g.CurrentMode != PawGame.Mode.Duel || g.Director.State != DuelDirector.Pres.Review)
            {
                Hide(false);
                return;
            }
            if (laidOut != g.Pipeline.LayoutVersion) Layout();

            if (playAllIndex >= 0)
            {
                if (UiPointer.Down) { UiPointer.Consumed = true; playAllIndex = -1; note.SetText("PLAY ALL STOPPED"); return; }
                playAllT -= Time.unscaledDeltaTime;
                if (playAllT <= 0f)
                {
                    var e = Entries();
                    if (playAllIndex >= e.Length) { playAllIndex = -1; note.SetText("PLAY ALL DONE"); return; }
                    Fire(playAllIndex);
                    playAllIndex++;
                    playAllT = PlayAllGap;
                }
                return;
            }

            for (int i = 0; i < buttons.Length; i++)
            {
                buttons[i].Tick();
                if (buttons[i].PressedNow) Fire(i);
            }
            playAll.Tick();
            if (playAll.PressedNow) { playAllIndex = 0; playAllT = 0.4f; note.SetText("PLAY ALL: TAP TO STOP"); }
            close.Tick();
            if (close.Released) Hide(true);
        }

        private void Fire(int i)
        {
            var e = Entries()[i];
            PawHaptics.Prepare();
            e.Play();
            note.SetText(e.Label + ": " + e.Note);
            RefreshStatus();
        }

        private void RefreshStatus()
        {
            string s = PawHaptics.Backend;
            if (!PawHaptics.Enabled) s = "HAPTICS OFF IN SETTINGS";
            else if (PawHaptics.ReducedMotion) s += " +RM";
            backend.SetText(s.ToUpperInvariant());
        }

        /// <summary>Title screen: a finger held on the top-left corner for 1.5 s opens the lab.</summary>
        private void WatchGesture(PawGame g)
        {
            // review pass 2: development builds only (the corner is also where SHOP sits: a long press on SHOP must not
            // open a debug screen for players), and never over the shop or an account card
            bool eligible = (Application.isEditor || Debug.isDebugBuild) &&
                            g.CurrentMode == PawGame.Mode.Title && g.SettingsOpenState == "closed" &&
                            (g.Online == null || !g.Online.CardOpen) &&
                            (g.Shop == null || !g.Shop.IsOpen) && (g.Accounts == null || !g.Accounts.Open);
            var s = g.Pipeline.Safe;
            if (eligible && UiPointer.Held && UiPointer.In(new RectInt(s.x, s.y, GestureSize, GestureSize)))
            {
                gestureT += Time.unscaledDeltaTime;
                if (gestureT >= GestureSeconds)
                {
                    gestureT = 0f;
                    UiPointer.Consumed = true;
                    Show(g);
                }
            }
            else gestureT = 0f;
        }

        // ------------------------------------------------------------------ build / layout

        private void Build()
        {
            var ui = game.transform.Find("UI");
            var go = new GameObject("HapticsLabUi");
            go.transform.SetParent(ui != null ? ui : game.transform, false);
            root = go.transform;
            kit = new UiKit();
            kit.Ui = new SpriteCatalog("Art/UI/", "ui");
            kit.Small = BitmapFont.Load("font_small", 8);
            kit.Medium = BitmapFont.Load("font_medium", 10);
            kit.Audio = game.Audio;

            bg = kit.Panel("panel_paper", root, O, new RectInt(0, 0, 64, 64));
            title = Text("Title", kit.Medium, TextAlign.Center, Pal.Ink);
            title.SetText("HAPTICS LAB");
            backend = Text("Backend", kit.Small, TextAlign.Center, Pal.Wood3);
            note = Text("Note", kit.Small, TextAlign.Center, Pal.PaintRed);
            var e = Entries();
            buttons = new LabButton[e.Length];
            for (int i = 0; i < e.Length; i++)
                buttons[i] = new LabButton(kit, root, (i / 2) % 2 == 0 ? "btn_large" : "btn_large_alt", e[i].Label, O + 2);
            playAll = new LabButton(kit, root, "btn_large_alt", "PLAY ALL", O + 2);
            close = new LabButton(kit, root, "btn_large", "CLOSE", O + 2);
            SetLayer(root, PixelPipeline.OverlayLayer);
            root.gameObject.SetActive(false);
        }

        private BitmapText Text(string name, BitmapFont f, TextAlign a, Color32 c)
        {
            var t = BitmapText.Create(name, root, f, O + 3, a);
            t.SetColors(c, Pal.Ink, Pal.Ink2);
            t.Outline = false;
            t.Shadow = false;
            return t;
        }

        private void Layout()
        {
            var pipe = game.Pipeline;
            laidOut = pipe.LayoutVersion;
            var v = pipe.Visible;
            var s = pipe.Safe;
            UiKit.SetRect(bg, kit.Ui.Get("panel_paper"), v);
            int cx = s.x + s.width / 2;
            title.At(cx, s.y + 6);
            backend.At(cx, s.y + 20);
            note.At(cx, s.y + 31);

            const int margin = 8, gap = 4;
            int rows = (buttons.Length + 1) / 2 + 1;          // + PLAY ALL / CLOSE
            int top = s.y + 44;
            int avail = s.yMax - 6 - top;
            int pitch = Mathf.Clamp(avail / rows, 18, 28);
            int bh = pitch - 3;
            int bw = (s.width - margin * 2 - gap) / 2;
            for (int i = 0; i < buttons.Length; i++)
            {
                int col = i % 2, row = i / 2;
                buttons[i].SetRect(new RectInt(s.x + margin + col * (bw + gap), top + row * pitch, bw, bh));
            }
            int lastY = top + ((buttons.Length + 1) / 2) * pitch;
            playAll.SetRect(new RectInt(s.x + margin, lastY, bw, bh));
            close.SetRect(new RectInt(s.x + margin + bw + gap, lastY, bw, bh));
        }

        private static void SetLayer(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }

        /// <summary>
        /// Lab button: reacts on finger-down (PressedNow) without PixelButton's own UI-tap haptic, so the only
        /// thing felt is the haptic under test. Released = press and release inside (for CLOSE).
        /// </summary>
        private sealed class LabButton
        {
            private readonly SpriteRenderer bg;
            private readonly BitmapText label;
            private readonly SheetDef up, down;
            private RectInt rect;
            private bool pressed;
            public bool PressedNow;
            public bool Released;

            public LabButton(UiKit kit, Transform parent, string sprite, string text, int order)
            {
                up = kit.Ui.Get(sprite);
                down = kit.Ui.TryGet(sprite + "_pressed") ?? up;
                bg = PawGfx.NewSprite("Lab_" + text, parent, order);
                bg.sprite = up.First;
                bg.drawMode = SpriteDrawMode.Sliced;
                label = BitmapText.Create("label", bg.transform, kit.Small, order + 2, TextAlign.Center);
                label.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
                label.SetText(text);
            }

            public void SetRect(RectInt r)
            {
                rect = r;
                Layout();
            }

            private void Layout()
            {
                var d = pressed ? down : up;
                bg.sprite = d.First;
                int sink = pressed && down == up ? 1 : 0;
                UiKit.SetRect(bg, d, new RectInt(rect.x, rect.y + sink, rect.width, rect.height));
                var co = d.ContentOffset;
                label.At(rect.x + rect.width / 2 + co.x, rect.y + rect.height / 2 + co.y + sink - label.Height / 2);
            }

            public void Tick()
            {
                PressedNow = false;
                Released = false;
                bool inside = UiPointer.In(rect);
                if (UiPointer.Down && inside && !UiPointer.Consumed)
                {
                    UiPointer.Consumed = true;
                    pressed = true;
                    PressedNow = true;
                    Layout();
                }
                else if (pressed && (UiPointer.Up || !UiPointer.Held))
                {
                    pressed = false;
                    Released = inside;
                    Layout();
                }
            }
        }
    }
}
