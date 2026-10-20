using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    public enum UiAction { None, Play, OpenSettings, CloseSettings, Rematch, Menu, Continue, ChooseEasy, ChooseHard }

    /// <summary>Common base: a root object that can be shown/hidden, laid out against the pipeline rects.</summary>
    public abstract class ScreenBase : MonoBehaviour
    {
        protected UiKit kit;
        protected GameLayout layout;
        protected PixelPipeline pipe;
        private int laidOut = -1;

        public void Setup(UiKit k, GameLayout l, PixelPipeline p)
        {
            kit = k;
            layout = l;
            pipe = p;
            Build();
            Layout();
            laidOut = pipe.LayoutVersion;
            gameObject.SetActive(false);
        }

        protected abstract void Build();
        protected abstract void Layout();

        public bool IsOpen { get { return gameObject.activeSelf; } }

        public virtual void Open()
        {
            gameObject.SetActive(true);
            Layout();
            laidOut = pipe.LayoutVersion;
        }

        public virtual void Close() { gameObject.SetActive(false); }

        protected void RelayoutIfNeeded()
        {
            if (laidOut != pipe.LayoutVersion) { Layout(); laidOut = pipe.LayoutVersion; }
        }

        protected int CenterX { get { return pipe.Visible.x + pipe.Visible.width / 2; } }

        /// <summary>First canvas y below the duel HUD (portraits + round sign), inside the safe area.</summary>
        protected int HudBottom
        {
            get
            {
                int fh = kit.Ui.Get("portrait_frame").Cell.y;
                int rh = kit.Ui.Get("round_sign").Cell.y;
                return pipe.Safe.y + layout.HudMargin + fh + 2 + rh + 4;
            }
        }

        /// <summary>Top of the holster graphic (the HUD draws it 6 px above the safe bottom, 40 px tall).</summary>
        protected int HolsterTop
        {
            get
            {
                var h = kit.Ui.TryGet("holster_zone");
                return pipe.Safe.yMax - 6 - (h != null ? h.Cell.y : 40);
            }
        }

        protected void PlaceCentered(SpriteRenderer sr, SheetDef d, int cx, int cy)
        {
            PawGfx.Place(sr, cx - d.Cell.x / 2 + d.Pivot.x, cy - d.Cell.y / 2 + d.Pivot.y);
        }

        protected BitmapText InkText(string name, BitmapFont f, int order, TextAlign a)
        {
            var t = BitmapText.Create(name, transform, f, order, a);
            t.SetColors(Pal.Ink, Pal.Ink, Pal.Ink2);
            t.Outline = false;
            t.Shadow = false;
            return t;
        }

        protected BitmapText LightText(string name, BitmapFont f, int order, TextAlign a)
        {
            var t = BitmapText.Create(name, transform, f, order, a);
            t.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            return t;
        }

        public static string FormatSeconds(long ns)
        {
            return ns < 0 ? "--" : NumFmt.Seconds6(ns) + " s";
        }
    }

    // ------------------------------------------------------------------------------------- Title
    public sealed class TitleScreen : ScreenBase
    {
        // EASY / HARD segmented toggle under PLAY (same seg sprites as the settings music-style row)
        private const int SegW = 48;
        private const int SegH = 20;
        private const int SegGap = 2;
        // first-launch choice card (above the title, below settings)
        private const int CO = 270;
        private const int CardW = 196;
        private const int CardH = 136;
        private const int CardBtnW = 164;
        private const int CardBtnH = 30;

        private SpriteRenderer logo;
        private SheetDef logoDef;
        private PixelButton play;
        private PixelButton gear;
        private BitmapText best;
        private float t;

        private Transform diffRoot;
        private SpriteRenderer segEasy, segHard;
        private BitmapText segEasyText, segHardText;
        private RectInt segEasyRect, segHardRect;

        private Transform choiceRoot;
        private SpriteRenderer choiceDim;
        private SpriteRenderer choicePanel;
        private BitmapText choiceTitle, choiceSub, easyDesc, hardDesc;
        private PixelButton chooseEasy, chooseHard;
        private RectInt choiceRect;
        private bool choiceOpen;
        private bool forceChoice;

        /// <summary>True while the one-time EASY / HARD card is up (PLAY waits for it).</summary>
        public bool ChoiceOpen { get { return choiceOpen; } }

        protected override void Build()
        {
            // prefer the generated logo_v3 when it has been dropped into Art/UI
            logoDef = kit.Ui.TryGet("logo_v3") ?? kit.Ui.Get("logo");
            logo = PawGfx.NewSprite("Logo", transform, 240);
            logo.sprite = logoDef.First;
            // wooden sign button; one sprite for both states, the label sinks 1 px when pressed
            play = new PixelButton(kit, transform, "panel_wood", "panel_wood", "PLAY", null, new RectInt(0, 0, 152, 36), 240);
            gear = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_gear", new RectInt(0, 0, 30, 30), 240);
            best = LightText("Best", kit.Medium, 242, TextAlign.Center);
            best.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);

            diffRoot = new GameObject("Difficulty").transform;
            diffRoot.SetParent(transform, false);
            segEasy = PawGfx.NewSprite("SegEasy", diffRoot, 240);
            segHard = PawGfx.NewSprite("SegHard", diffRoot, 240);
            segEasyText = BitmapText.Create("SegEasyT", diffRoot, kit.Small, 242, TextAlign.Center);
            segEasyText.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            segEasyText.SetText("EASY");
            segHardText = BitmapText.Create("SegHardT", diffRoot, kit.Small, 242, TextAlign.Center);
            segHardText.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            segHardText.SetText("HARD");

            choiceRoot = new GameObject("DifficultyChoice").transform;
            choiceRoot.SetParent(transform, false);
            choiceDim = PawGfx.NewSprite("Dim", choiceRoot, CO - 2);
            choiceDim.sprite = ArtLib.WhitePixel;
            choiceDim.color = new Color32(Pal.Ink.r, Pal.Ink.g, Pal.Ink.b, 150);
            choicePanel = kit.Panel("panel_wood", choiceRoot, CO, new RectInt(0, 0, CardW, CardH));
            choiceTitle = BitmapText.Create("ChoiceTitle", choiceRoot, kit.Medium, CO + 3, TextAlign.Center);
            choiceTitle.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            choiceTitle.SetText("PICK YOUR DUEL");
            choiceSub = BitmapText.Create("ChoiceSub", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            choiceSub.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            choiceSub.SetText("Switch it any time on this screen.");
            chooseEasy = new PixelButton(kit, choiceRoot, "btn_large_alt", "btn_large_alt_pressed", "EASY", null, new RectInt(0, 0, CardBtnW, CardBtnH), CO + 2);
            chooseHard = new PixelButton(kit, choiceRoot, "btn_large", "btn_large_pressed", "HARD", null, new RectInt(0, 0, CardBtnW, CardBtnH), CO + 2);
            easyDesc = BitmapText.Create("EasyDesc", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            easyDesc.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            easyDesc.SetText("Slower outlaw. Kinder timing.");
            hardDesc = BitmapText.Create("HardDesc", choiceRoot, kit.Small, CO + 3, TextAlign.Center);
            hardDesc.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            hardDesc.SetText("The real high noon.");
            choiceRoot.gameObject.SetActive(false);
        }

        private int LogoY()
        {
            // upper third, clear of the Dynamic Island / status bar
            return Mathf.Max(layout.Logo.y, pipe.Safe.y + 40 + logoDef.Cell.y / 2);
        }

        protected override void Layout()
        {
            var safe = pipe.Safe;
            int cx = CenterX;
            PlaceCentered(logo, logoDef, cx, LogoY() + Mathf.RoundToInt(Mathf.Sin(t * 1.6f) * 1.5f));
            // PLAY lives in the thumb zone (bottom quarter of the safe area); the difficulty toggle right under
            // it, BEST under the toggle (36 + 5 + 20 + 5 + 10 = 76 px, fits the 84 px reserved below playTop)
            int playTop = Mathf.Min(layout.PlayButton.y - 18, safe.yMax - 84);
            play.SetRect(new RectInt(cx - 76, playTop, 152, 36));
            int segY = playTop + 36 + 5;
            segEasyRect = new RectInt(cx - SegGap / 2 - SegW, segY, SegW, SegH);
            segHardRect = new RectInt(segEasyRect.xMax + SegGap, segY, SegW, SegH);
            int ty = segY + (SegH - kit.Small.LineHeight) / 2 + 1;
            segEasyText.At(segEasyRect.x + SegW / 2, ty);
            segHardText.At(segHardRect.x + SegW / 2, ty);
            best.At(cx, segY + SegH + 5);
            gear.SetRect(new RectInt(safe.xMax - 4 - 30, safe.y + 2, 30, 30));
            RefreshDifficulty();
            LayoutChoice();
        }

        private void LayoutChoice()
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            choiceDim.transform.position = Px.World(vis.x, vis.yMax);
            choiceDim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            int w = Mathf.Min(CardW, vis.width - 16);
            int x = CenterX - w / 2;
            // thumb zone, where PLAY sits; never up over the logo
            int logoBottom = LogoY() + logoDef.Cell.y / 2 + 6;
            int y = Mathf.Max(logoBottom, safe.yMax - 12 - CardH);
            choiceRect = new RectInt(x, y, w, CardH);
            UiKit.SetRect(choicePanel, kit.Ui.Get("panel_wood"), choiceRect);
            int cx = x + w / 2;
            choiceTitle.At(cx, y + 11);
            choiceSub.At(cx, y + 24);
            int bw = Mathf.Min(CardBtnW, w - 20);
            chooseEasy.SetRect(new RectInt(cx - bw / 2, y + 38, bw, CardBtnH));
            easyDesc.At(cx, y + 38 + CardBtnH + 3);
            chooseHard.SetRect(new RectInt(cx - bw / 2, y + 84, bw, CardBtnH));
            hardDesc.At(cx, y + 84 + CardBtnH + 3);
        }

        private void PlaceSeg(SpriteRenderer sr, bool on, RectInt r)
        {
            var d = kit.Ui.TryGet(on ? "seg_on" : "seg_off");
            if (d == null) { sr.sprite = null; return; }
            sr.sprite = d.First;
            sr.drawMode = SpriteDrawMode.Sliced;
            UiKit.SetRect(sr, d, r);
        }

        /// <summary>Re-reads PawSettings.Difficulty into the toggle.</summary>
        public void RefreshDifficulty()
        {
            bool easy = PawSettings.Difficulty == DuelConfig.DifficultyEasy;
            PlaceSeg(segEasy, easy, segEasyRect);
            PlaceSeg(segHard, !easy, segHardRect);
            // the yellow segment marks the pick; labels stay light on both
            segEasyText.SetColors(easy ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            segHardText.SetColors(!easy ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            segEasyText.MarkDirty();
            segHardText.MarkDirty();
        }

        /// <summary>Review: open the title with the first-launch card even if a difficulty is stored.</summary>
        public void ForceChoice() { forceChoice = true; }

        public override void Open()
        {
            choiceOpen = forceChoice || !PawSettings.DifficultyChosen;
            forceChoice = false;
            base.Open();
            best.SetText(PawSettings.BestNs > 0 ? "BEST " + NumFmt.Seconds6(PawSettings.BestNs).Substring(0, 5) + " s" : "BEST --");
            ApplyChoiceVisibility();
        }

        private void ApplyChoiceVisibility()
        {
            choiceRoot.gameObject.SetActive(choiceOpen);
            play.SetVisible(!choiceOpen);
            diffRoot.gameObject.SetActive(!choiceOpen);
            best.Visible = !choiceOpen;
            gear.SetVisible(!choiceOpen);
        }

        /// <summary>Closes the first-launch card (after the game stored the pick).</summary>
        public void CloseChoice()
        {
            choiceOpen = false;
            RefreshDifficulty();
            ApplyChoiceVisibility();
        }

        public UiAction Tick(bool interactive)
        {
            RelayoutIfNeeded();
            t += PawTime.UnscaledDt;
            PlaceCentered(logo, logoDef, CenterX, LogoY() + Mathf.RoundToInt(Mathf.Sin(t * 1.6f) * 1.5f));
            if (!interactive) return UiAction.None;
            if (choiceOpen)
            {
                if (chooseEasy.Tick()) return UiAction.ChooseEasy;
                if (chooseHard.Tick()) return UiAction.ChooseHard;
                // the card is modal: swallow taps elsewhere
                if (UiPointer.Down) UiPointer.Consumed = true;
                return UiAction.None;
            }
            if (gear.Tick()) return UiAction.OpenSettings;
            if (play.Tick()) return UiAction.Play;
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                bool easy = PawSettings.Difficulty == DuelConfig.DifficultyEasy;
                if (UiPointer.In(segEasyRect))
                {
                    UiPointer.Consumed = true;
                    if (!easy) return Toggled(UiAction.ChooseEasy);
                }
                else if (UiPointer.In(segHardRect))
                {
                    UiPointer.Consumed = true;
                    if (easy) return Toggled(UiAction.ChooseHard);
                }
            }
            return UiAction.None;
        }

        private UiAction Toggled(UiAction a)
        {
            if (kit.Audio != null) kit.Audio.Play("sfx_ui_toggle");
            PawHaptics.Impact(0);
            return a;
        }
    }

    // ---------------------------------------------------------------------------------- Settings
    public sealed class SettingsScreen : ScreenBase
    {
        private const int O = 300;
        private const int RowH = 26;
        private const int RowsTop = 36;
        private static readonly string[] HowTo =
        {
            "3 - 2 - 1 ... DRAW!",
            "Swipe up from the holster.",
            "Faster draw = more damage.",
            "Early draw = jam.",
        };
        private SpriteRenderer dim;
        private SpriteRenderer panel;
        private BitmapText title;
        private BitmapText howTitle;
        private readonly BitmapText[] howLines = new BitmapText[4];
        private PixelButton back;
        private readonly BitmapText[] labels = new BitmapText[5];
        private readonly SpriteRenderer[] icons = new SpriteRenderer[5];
        private readonly SpriteRenderer[] toggles = new SpriteRenderer[5];
        private SpriteRenderer segOrig, segClassic;
        private BitmapText segOrigText, segClassicText;
        private readonly RectInt[] rows = new RectInt[5];
        private RectInt segOrigRect, segClassicRect;
        private RectInt panelRect;
        private readonly bool[] shown = new bool[5];
        private readonly float[] slideLeft = new float[5];
        private Sprite midToggle;
        private bool midTried;
        private const float SlideSeconds = 0.07f;
        private static readonly string[] Names = { "Music", "Music style", "Sound FX", "Haptics", "Reduced Motion" };
        private static readonly string[] IconOn = { "ico_music_on", "ico_music_on", "ico_sound_on", "ico_haptic_on", "ico_motion" };
        private static readonly string[] IconOff = { "ico_music_off", "ico_music_on", "ico_sound_off", "ico_haptic_off", "ico_motion" };

        /// <summary>Raised after any change (game applies settings).</summary>
        public System.Action Changed;

        protected override void Build()
        {
            dim = PawGfx.NewSprite("Dim", transform, O - 2);
            dim.sprite = ArtLib.WhitePixel;
            dim.color = new Color32(Pal.Ink.r, Pal.Ink.g, Pal.Ink.b, 150);
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, 232, 236));
            title = InkText("Title", kit.Medium, O + 3, TextAlign.Center);
            title.SetText("SETTINGS");
            back = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_back", new RectInt(0, 0, 26, 26), O + 2);
            for (int i = 0; i < 5; i++)
            {
                labels[i] = InkText("L" + i, kit.Medium, O + 3, TextAlign.Left);
                labels[i].SetText(Names[i]);
                icons[i] = PawGfx.NewSprite("I" + i, transform, O + 3);
                if (i != 1) toggles[i] = PawGfx.NewSprite("T" + i, transform, O + 3);
            }
            segOrig = PawGfx.NewSprite("SegO", transform, O + 2);
            segClassic = PawGfx.NewSprite("SegC", transform, O + 2);
            segOrigText = LightText("SegOT", kit.Small, O + 4, TextAlign.Center);
            segOrigText.SetText("ORIGINAL");
            segClassicText = LightText("SegCT", kit.Small, O + 4, TextAlign.Center);
            segClassicText.SetText("CLASSIC");
            howTitle = InkText("HowTitle", kit.Medium, O + 3, TextAlign.Left);
            howTitle.SetText("HOW TO PLAY");
            for (int i = 0; i < howLines.Length; i++)
            {
                howLines[i] = InkText("How" + i, kit.Small, O + 3, TextAlign.Left);
                howLines[i].SetText(HowTo[i]);
                howLines[i].SetColors(Pal.Wood3, Pal.Ink, Pal.Ink2);
                howLines[i].Outline = false;
                howLines[i].Shadow = false;
            }
        }

        protected override void Layout()
        {
            var vis = pipe.Visible;
            dim.transform.position = Px.World(vis.x, vis.yMax);
            dim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            int w = Mathf.Min(232, vis.width - 16);
            int h = RowsTop + 5 * RowH + 6 + 13 + HowTo.Length * 10 + 10;
            int x = CenterX - w / 2;
            var safe = pipe.Safe;
            int y = Mathf.Max(safe.y + 8, vis.y + (vis.height - h) / 2 - 10);
            if (y + h > safe.yMax - 4) y = Mathf.Max(safe.y + 2, safe.yMax - 4 - h);
            panelRect = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), panelRect);
            back.SetRect(new RectInt(x + 6, y + 6, 26, 26));
            title.At(x + w / 2, y + 14);
            for (int i = 0; i < 5; i++)
            {
                int ry = y + RowsTop + i * RowH;
                rows[i] = new RectInt(x + 6, ry, w - 12, RowH);
                var idd = kit.Ui.TryGet(IconOn[i]);
                if (idd != null) PawGfx.Place(icons[i], x + 16 - idd.Cell.x / 2 + idd.Pivot.x, ry + RowH / 2 - idd.Cell.y / 2 + idd.Pivot.y);
                labels[i].At(x + 30, ry + (RowH - kit.Medium.LineHeight) / 2);
                if (toggles[i] != null)
                {
                    var td = kit.Ui.TryGet("toggle_on");
                    if (td != null) PawGfx.Place(toggles[i], x + w - 12 - td.Cell.x + td.Pivot.x, ry + (RowH - td.Cell.y) / 2 + td.Pivot.y);
                }
            }
            var sd = kit.Ui.TryGet("seg_on");
            int segW = Mathf.Max(sd != null ? sd.Cell.x : 0, 48), segH = Mathf.Max(sd != null ? sd.Cell.y : 0, 14);
            int sy = y + RowsTop + RowH + (RowH - segH) / 2;
            segClassicRect = new RectInt(x + w - 12 - segW, sy, segW, segH);
            segOrigRect = new RectInt(segClassicRect.x - 2 - segW, sy, segW, segH);
            segOrigText.At(segOrigRect.x + segW / 2, segOrigRect.y + (segH - kit.Small.LineHeight) / 2 + 1);
            segClassicText.At(segClassicRect.x + segW / 2, segClassicRect.y + (segH - kit.Small.LineHeight) / 2 + 1);
            int hy = y + RowsTop + 5 * RowH + 6;
            howTitle.At(x + 14, hy);
            for (int i = 0; i < howLines.Length; i++) howLines[i].At(x + 14, hy + 13 + i * 10);
            Refresh();
        }

        private void PlaceSeg(SpriteRenderer sr, bool on, RectInt r)
        {
            var d = kit.Ui.TryGet(on ? "seg_on" : "seg_off");
            if (d == null) { sr.sprite = null; return; }
            sr.sprite = d.First;
            if (d.Count > 0 && d.First != null && d.First.border != Vector4.zero)
            {
                sr.drawMode = SpriteDrawMode.Sliced;
                UiKit.SetRect(sr, d, r);
            }
            else
            {
                sr.drawMode = SpriteDrawMode.Simple;
                PawGfx.Place(sr, r.x + d.Pivot.x, r.y + d.Pivot.y);
            }
        }

        private bool Value(int i)
        {
            switch (i)
            {
                case 0: return PawSettings.Music;
                case 2: return PawSettings.Sfx;
                case 3: return PawSettings.Haptics;
                case 4: return PawSettings.ReducedMotion;
                default: return true;
            }
        }

        public void Refresh() { Refresh(false); }

        private void Refresh(bool animate)
        {
            for (int i = 0; i < 5; i++)
            {
                bool v = Value(i);
                var icon = kit.Ui.TryGet(v ? IconOn[i] : IconOff[i]);
                icons[i].sprite = icon != null ? icon.First : null;
                if (toggles[i] == null) continue;
                bool changed = animate && shown[i] != v;
                shown[i] = v;
                Sprite mid = (changed && !PawSettings.ReducedMotion) ? MidToggle() : null;
                if (mid != null)
                {
                    // knob slides in two steps: centre frame, then the final state
                    toggles[i].sprite = mid;
                    slideLeft[i] = SlideSeconds;
                }
                else
                {
                    slideLeft[i] = 0f;
                    SetToggleSprite(i);
                }
            }
            PlaceSeg(segOrig, !PawSettings.ClassicMusic, segOrigRect);
            PlaceSeg(segClassic, PawSettings.ClassicMusic, segClassicRect);
        }

        private void SetToggleSprite(int i)
        {
            var d = kit.Ui.TryGet(shown[i] ? "toggle_on" : "toggle_off");
            toggles[i].sprite = d != null ? d.First : null;
        }

        /// <summary>
        /// Centre frame of the toggle knob, built once at runtime from toggle_on/toggle_off (the art ships no
        /// in-between): the knob of "off" shifted a quarter width over a base that is on-coloured behind the
        /// knob and off-coloured ahead of it. Returns null (instant switch) if anything is unavailable.
        /// </summary>
        private Sprite MidToggle()
        {
            if (midToggle != null || midTried) return midToggle;
            midTried = true;
            try
            {
                var on = kit.Ui.TryGet("toggle_on");
                var off = kit.Ui.TryGet("toggle_off");
                if (on == null || off == null || on.Missing || off.Missing || on.First == null || off.First == null) return null;
                int w, h, w2, h2;
                var a = ReadSprite(on.First, out w, out h);
                var b = ReadSprite(off.First, out w2, out h2);
                if (a == null || b == null || w != w2 || h != h2 || w < 16 || h < 6) return null;
                int half = w / 2, shift = w / 4;
                var m = new Color32[w * h];
                var knob = new bool[w * h];
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < w; x++)
                    {
                        int i = y * w + x;
                        var c = b[i];
                        knob[i] = x <= half && c.a > 0 && c.r > 200;
                        Color32 basePx = x < half ? a[i] : c;
                        if (x >= half && c.a > 0 && c.r > 200) basePx = new Color32(116, 116, 138, 255); // off-state glyph -> plain track
                        m[i] = basePx;
                    }
                for (int y = 0; y < h; y++)
                    for (int x = 0; x <= half; x++)
                    {
                        int i = y * w + x;
                        bool take = knob[i];
                        var c = b[i];
                        if (!take && y >= 1 && y <= h - 2 && c.a > 0 && c.r < 60 && c.g < 40 && c.b < 70)
                        {
                            for (int dy = -1; dy <= 1 && !take; dy++)
                                for (int dx = -1; dx <= 1; dx++)
                                {
                                    int nx = x + dx, ny = y + dy;
                                    if (nx >= 0 && nx <= half && ny >= 0 && ny < h && knob[ny * w + nx]) { take = true; break; }
                                }
                        }
                        if (take && x + shift < w) m[y * w + x + shift] = c;
                    }
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                tex.SetPixels32(m);
                tex.Apply(false, false);
                tex.filterMode = FilterMode.Point;
                tex.wrapMode = TextureWrapMode.Clamp;
                tex.name = "PawToggleMid";
                midToggle = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(on.Pivot.x / (float)w, 1f - on.Pivot.y / (float)h), 1f, 0, SpriteMeshType.FullRect);
            }
            catch (System.Exception e)
            {
                ArtLib.Warn("togglemid", "toggle slide frame unavailable (instant switch): " + e.Message);
                midToggle = null;
            }
            return midToggle;
        }

        /// <summary>CPU copy of a sprite's pixels via a blit (the art is imported non-readable). Row 0 = bottom.</summary>
        private static Color32[] ReadSprite(Sprite s, out int w, out int h)
        {
            w = (int)s.rect.width;
            h = (int)s.rect.height;
            var tex = s.texture;
            var rt = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            Texture2D rd = null;
            try
            {
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                rd = new Texture2D(w, h, TextureFormat.RGBA32, false);
                rd.ReadPixels(new Rect(s.rect.x, s.rect.y, w, h), 0, 0);
                rd.Apply(false, false);
                return rd.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
                if (rd != null) UnityEngine.Object.Destroy(rd);
            }
        }

        public UiAction Tick()
        {
            RelayoutIfNeeded();
            float sdt = PawTime.UnscaledDt;
            for (int i = 0; i < 5; i++)
            {
                if (slideLeft[i] <= 0f) continue;
                slideLeft[i] -= sdt;
                if (slideLeft[i] <= 0f) SetToggleSprite(i);
            }
            if (back.Tick())
            {
                if (kit.Audio != null) kit.Audio.Play("sfx_ui_back");
                return UiAction.CloseSettings;
            }
            if (!UiPointer.Down || UiPointer.Consumed) return UiAction.None;
            for (int i = 0; i < 5; i++)
            {
                if (!UiPointer.In(rows[i])) continue;
                UiPointer.Consumed = true;
                switch (i)
                {
                    case 0: PawSettings.Music = !PawSettings.Music; break;
                    case 1:
                        if (UiPointer.In(segOrigRect)) PawSettings.ClassicMusic = false;
                        else if (UiPointer.In(segClassicRect)) PawSettings.ClassicMusic = true;
                        else PawSettings.ClassicMusic = !PawSettings.ClassicMusic;
                        break;
                    case 2: PawSettings.Sfx = !PawSettings.Sfx; break;
                    case 3: PawSettings.Haptics = !PawSettings.Haptics; break;
                    case 4: PawSettings.ReducedMotion = !PawSettings.ReducedMotion; break;
                }
                PawSettings.Save();
                if (Changed != null) Changed();
                if (kit.Audio != null) kit.Audio.Play("sfx_ui_toggle");
                PawHaptics.Impact(0);
                Refresh(true);
                return UiAction.None;
            }
            // tap outside the panel closes
            if (!UiPointer.In(panelRect))
            {
                UiPointer.Consumed = true;
                if (kit.Audio != null) kit.Audio.Play("sfx_ui_back");
                return UiAction.CloseSettings;
            }
            return UiAction.None;
        }
    }

    // ------------------------------------------------------------------------------ Round result
    public struct RoundView
    {
        public string youName;
        public string botName;
        public long youNs;      // -1 = none
        public long botNs;      // -1 = none
        public bool youFoul;
        public bool youNoShot;
        public bool botFoul;
        public bool frameQuantized;
        public Grade grade;
        public double dealt;
        public double taken;
        /// <summary>Reaction replay strip under the card (SPEC_V2 §8.1); default (invalid) = no strip.</summary>
        public ReplayMarks replay;
    }

    public sealed class ResultPanel : ScreenBase
    {
        private const int O = 220;
        private SpriteRenderer panel;
        private SpriteRenderer card;     // painted parchment (Art/UI/result_card.png), used instead of panel_paper when present
        private SheetDef cardDef;
        private BitmapText youLabel, youTime, botLabel, botTime, gap, dmg, note;
        private BitmapText diffTag;   // "EASY" / "HARD" after the note line (text only, no art)
        private PopSprite stamp;
        private ReplayStrip replay;
        private RectInt rect;
        private float stampDelay;
        private string stampName;
        public string StampSound;
        // slide-in: 6 px from above, ease-out, whole-pixel steps
        private const float SlideSecs = 0.18f;
        private const int SlideFrom = -6;
        private float slideT = SlideSecs;
        private int slideDy;

        protected override void Build()
        {
            cardDef = kit.Ui.TryGet("result_card");
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, layout.ResultPanelSize.x, layout.ResultPanelSize.y));
            if (cardDef != null)
            {
                card = PawGfx.NewSprite("Card", transform, O);
                card.sprite = cardDef.First;
                panel.enabled = false;
            }
            youLabel = InkText("YouL", kit.Medium, O + 2, TextAlign.Left);
            youTime = InkText("YouT", kit.Timer, O + 2, TextAlign.Right);
            youTime.MonoDigits = true;
            botLabel = InkText("BotL", kit.Small, O + 2, TextAlign.Left);
            botTime = InkText("BotT", kit.Medium, O + 2, TextAlign.Right);
            botTime.MonoDigits = true;
            gap = InkText("Gap", kit.Medium, O + 2, TextAlign.Left);
            dmg = InkText("Dmg", kit.Medium, O + 2, TextAlign.Left);
            note = InkText("Note", kit.Small, O + 2, TextAlign.Left);
            diffTag = InkText("Difficulty", kit.Small, O + 2, TextAlign.Left);
            SetDifficulty(false);
            stamp = new PopSprite("Grade", transform, O + 4);
            replay = new ReplayStrip(transform, kit.Small, O + 1);
        }

        protected override void Layout()
        {
            int w = Mathf.Min(layout.ResultPanelSize.x, pipe.Visible.width - 12);
            int h = layout.ResultPanelSize.y;
            if (cardDef != null) { w = cardDef.Cell.x; h = cardDef.Cell.y; }
            int x = CenterX - w / 2;
            // upper third: below the HUD and round sign, never down over Pawtruth (hat top ~ canvas y 320)
            int y = layout.ResultPanel.y - h / 2;
            y = Mathf.Min(y, 316 - h);
            y = Mathf.Max(y, HudBottom);
            rect = new RectInt(x, y, w, h); // resting rect (stamp anchors to it)
            y += slideDy;                   // everything below follows the slide offset
            if (card != null) PlaceCentered(card, cardDef, x + w / 2, y + h / 2);
            else UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), new RectInt(x, y, w, h));
            // painted card: inner parchment is inset ~16 px from the torn edge
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            youLabel.At(x + ix, y + iy + 2);
            youTime.At(x + w - ix, y + iy - 2);
            botLabel.At(x + ix, y + iy + 20);
            botTime.At(x + w - ix, y + iy + 18);
            gap.At(x + ix, y + iy + 36);
            dmg.At(x + ix, y + iy + 50);
            note.At(x + ix, y + h - iy - 8);
            PlaceTag();
            if (replay != null) replay.PlaceAt(CenterX - ReplayStripMath.Width / 2, y + h + ReplayGapPx);
        }

        private const int ReplayGapPx = 2;

        /// <summary>The replay strip is showing under the card.</summary>
        public bool ReplayVisible { get { return replay != null && replay.Visible && IsOpen; } }

        /// <summary>Canvas y just below the replay strip at rest (after the slide-in).</summary>
        public int ReplayBottom { get { return rect.yMax + ReplayGapPx + ReplayStripMath.Height; } }

        /// <summary>Difficulty the match is played on (shown as a small tag on the card).</summary>
        public void SetDifficulty(bool easy)
        {
            diffTag.SetText(easy ? "EASY" : "HARD");
            diffTag.Color = easy ? Pal.Green2 : Pal.PaintRed;
            diffTag.MarkDirty();
        }

        /// <summary>The tag follows the note text on the same baseline (note width changes per round).</summary>
        private void PlaceTag()
        {
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            int w = Mathf.Min(layout.ResultPanelSize.x, pipe.Visible.width - 12);
            int h = layout.ResultPanelSize.y;
            if (cardDef != null) { w = cardDef.Cell.x; h = cardDef.Cell.y; }
            int x = CenterX - w / 2;
            int y = rect.y + slideDy;
            diffTag.At(x + ix + note.Width + 6, y + h - iy - 8);
        }

        public void Show(RoundView v)
        {
            if (PawSettings.ReducedMotion) { slideT = SlideSecs; slideDy = 0; }
            else { slideT = 0f; slideDy = SlideFrom; }
            Open();
            youLabel.SetText(v.youName);
            botLabel.SetText(v.botName);
            youTime.SetText((v.youFoul || v.youNoShot) ? "--" : FormatSeconds(v.youNs));
            botTime.SetText(v.botFoul ? "FOUL" : FormatSeconds(v.botNs));

            if (v.youFoul) { gap.SetText("TOO EARLY!"); gap.Color = Pal.PaintRed; }
            else if (v.youNoShot) { gap.SetText("NO SHOT"); gap.Color = Pal.PaintRed; }
            else if (v.youNs >= 0 && v.botNs >= 0)
            {
                long g = v.botNs - v.youNs;
                bool faster = g >= 0;
                gap.SetText((faster ? "+" : "-") + NumFmt.Ms2(g) + " ms " + (faster ? "FASTER" : "SLOWER"));
                gap.Color = faster ? Pal.Green2 : Pal.PaintRed;
            }
            else if (v.youNs >= 0) { gap.SetText("CLEAN SHOT!"); gap.Color = Pal.Green2; }
            else { gap.SetText(""); }
            gap.MarkDirty();

            if (v.dealt > 0.0001) { dmg.SetText("HIT -" + Mathf.RoundToInt((float)v.dealt) + " HP"); dmg.Color = Pal.PaintRed; }
            else if (v.taken > 0.0001) { dmg.SetText("TOOK -" + Mathf.RoundToInt((float)v.taken) + " HP"); dmg.Color = Pal.Wood4; }
            else { dmg.SetText("NO DAMAGE"); dmg.Color = Pal.Wood4; }
            dmg.MarkDirty();

            note.SetText(v.frameQuantized ? "FRAME-TIMED INPUT" : "TAP TO CONTINUE");
            PlaceTag();

            stampName = null;
            if (v.youFoul) stampName = "stamp_too_early";
            else if (v.youNoShot) stampName = "stamp_no_shot";
            else if (v.grade != Grade.None) stampName = GradeSprite(v.grade);
            StampSound = v.grade == Grade.Lightning ? "sfx_lightning" : null;
            stamp.Hide();
            stampDelay = 0.35f;
            replay.Show(v.replay, v.youName, v.botName, PawSettings.ReducedMotion);
        }

        public static string GradeSprite(Grade g)
        {
            switch (g)
            {
                case Grade.Lightning: return "grade_lightning";
                case Grade.Perfect: return "grade_perfect";
                case Grade.Great: return "grade_great";
                case Grade.Good: return "grade_good";
                case Grade.Slow: return "grade_slow";
                default: return "grade_slowpoke";
            }
        }

        /// <summary>Returns true at the moment the stamp lands (caller plays sfx/haptic).</summary>
        public bool Tick()
        {
            RelayoutIfNeeded();
            float dt = PawTime.UnscaledDt;
            if (slideT < SlideSecs)
            {
                slideT += dt;
                int dy = slideT >= SlideSecs ? 0 : Mathf.RoundToInt(SlideFrom * (1f - Ease.OutCubic(slideT / SlideSecs)));
                if (dy != slideDy) { slideDy = dy; Layout(); }
            }
            stamp.Tick(dt);
            replay.Tick(dt);
            if (stampName != null && stampDelay > 0f)
            {
                stampDelay -= dt;
                if (stampDelay <= 0f)
                {
                    var d = kit.Ui.Get(stampName);
                    stamp.Show(d, new Vector2Int(rect.xMax - 20 - d.Cell.x + d.Pivot.x, rect.yMax - 16 - d.Cell.y + d.Pivot.y), -10f, 0.05f);
                    return true;
                }
            }
            return false;
        }

        public void ShowStampNow()
        {
            stampDelay = 0.0001f;
        }
    }

    // ------------------------------------------------------------------------------ Match result
    public sealed class MatchResultScreen : ScreenBase
    {
        private const int O = 260;
        private const int BtnW = 164;
        private const int BtnH = 30;
        private PopSprite banner;
        private BitmapText bannerText;
        private SpriteRenderer panel;
        private BitmapText[] keys = new BitmapText[3];
        private BitmapText[] vals = new BitmapText[3];
        private PixelButton rematch, menu;
        private BitmapText newBest;
        private bool win;
        // banner drop: two falling steps, land, one small bounce, settle (whole pixels)
        private static readonly int[] BannerDrop = { -18, -9, 0, -4, 0 };

        protected override void Build()
        {
            banner = new PopSprite("Banner", transform, O + 2);
            bannerText = LightText("BannerText", kit.Medium, O + 3, TextAlign.Center);
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, 180, 62));
            string[] k = { "BEST", "AVG", "ALL-TIME" };
            for (int i = 0; i < 3; i++)
            {
                keys[i] = InkText("K" + i, kit.Medium, O + 2, TextAlign.Left);
                keys[i].SetText(k[i]);
                vals[i] = InkText("V" + i, kit.Medium, O + 2, TextAlign.Right);
                vals[i].MonoDigits = true;
            }
            newBest = LightText("NewBest", kit.Small, O + 3, TextAlign.Center);
            newBest.SetColors(Pal.SigYellow, Pal.Ink, Pal.Ink2);
            rematch = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "REMATCH", "ico_retry", new RectInt(0, 0, BtnW, BtnH), O + 2);
            menu = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "MENU", "ico_home", new RectInt(0, 0, BtnW, BtnH), O + 2);
        }

        /// <summary>Banner + stats sit in the upper third under the HUD, so Pawtruth (canvas y 330+) stays visible.</summary>
        private int Top()
        {
            return Mathf.Max(HudBottom, layout.ResultPanel.y - 30);
        }

        protected override void Layout()
        {
            int cx = CenterX;
            int top = Top();
            var bd = kit.Ui.Get(win ? "banner_win" : "banner_lose");
            banner.Show(bd, new Vector2Int(cx - bd.Cell.x / 2 + bd.Pivot.x, top + bd.Pivot.y), 0f, 0f);
            bannerText.At(cx, top + 14);
            int py = top + bd.Cell.y + 8;
            int pw = 180;
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), new RectInt(cx - pw / 2, py, pw, 62));
            for (int i = 0; i < 3; i++)
            {
                keys[i].At(cx - pw / 2 + 10, py + 9 + i * 15);
                vals[i].At(cx + pw / 2 - 10, py + 9 + i * 15);
            }
            newBest.At(cx, py + 66);
            // buttons: bottom thumb zone, stacked above the holster graphic
            int menuY = HolsterTop - 6 - BtnH;
            int rematchY = menuY - 6 - BtnH;
            rematch.SetRect(new RectInt(cx - BtnW / 2, rematchY, BtnW, BtnH));
            menu.SetRect(new RectInt(cx - BtnW / 2, menuY, BtnW, BtnH));
        }

        public void Show(bool playerWon, bool isDeadHeat, long bestNs, long avgNs, long allTimeNs, bool isNewBest)
        {
            win = playerWon;
            bool deadHeat = isDeadHeat;
            Open();
            var bd = kit.Ui.Get(win ? "banner_win" : "banner_lose");
            int cx = CenterX;
            int top = Top();
            var at = new Vector2Int(cx - bd.Cell.x / 2 + bd.Pivot.x, top + bd.Pivot.y);
            if (PawSettings.ReducedMotion) banner.Show(bd, at, 0f, 0f); // instant appear
            else banner.ShowSteps(bd, at, BannerDrop, 0.05f, 0.06f);
            // double KO: keep the dark ribbon and letter STANDOFF over it (both cats down)
            bannerText.SetText(deadHeat ? "STANDOFF" : "");
            bannerText.Visible = deadHeat;
            vals[0].SetText(FormatSeconds(bestNs));
            vals[1].SetText(FormatSeconds(avgNs));
            vals[2].SetText(FormatSeconds(allTimeNs));
            newBest.SetText(isNewBest ? "NEW ALL-TIME BEST!" : "");
        }

        public UiAction Tick()
        {
            RelayoutIfNeeded();
            banner.Tick(PawTime.UnscaledDt);
            if (rematch.Tick()) return UiAction.Rematch;
            if (menu.Tick()) return UiAction.Menu;
            return UiAction.None;
        }
    }
}
