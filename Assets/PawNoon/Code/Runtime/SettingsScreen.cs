using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
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
        private PixelButton account;   // M6: Settings > ACCOUNT (link Apple / Google, delete account)
        private const int AccountH = 26;
        private readonly BitmapText[] labels = new BitmapText[NRows];
        private readonly SpriteRenderer[] icons = new SpriteRenderer[NRows];
        private readonly SpriteRenderer[] toggles = new SpriteRenderer[NRows];
        private SpriteRenderer segOrig, segClassic, segReplay;
        private BitmapText segOrigText, segClassicText, segReplayText;
        private readonly RectInt[] rows = new RectInt[NRows];
        private RectInt segOrigRect, segClassicRect, segReplayRect;
        private RectInt panelRect;
        private readonly bool[] shown = new bool[NRows];
        private readonly float[] slideLeft = new float[NRows];
        private Sprite midToggle;
        private bool midTried;
        private const float SlideSeconds = 0.07f;
        private const int NRows = 7;
        /// <summary>"Tutorial" row: a REPLAY tile instead of a toggle (Docs/TUTORIAL.md).</summary>
        private const int TutorialRow = 6;
        private static readonly string[] Names = { "Music", "Music style", "Sound FX", "Haptics", "Reduced Motion", "Swipe Hint", "Tutorial" };
        private static readonly string[] IconOn = { "ico_music_on", "ico_music_on", "ico_sound_on", "ico_haptic_on", "ico_motion", "tap_hint", "tap_hint" };
        private static readonly string[] IconOff = { "ico_music_off", "ico_music_on", "ico_sound_off", "ico_haptic_off", "ico_motion", "tap_hint", "tap_hint" };

        /// <summary>Raised after any change (game applies settings).</summary>
        public System.Action Changed;

        protected override void Build()
        {
            dim = UiKit.Dim(transform, O - 2);
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, 232, 236));
            title = InkText("Title", kit.Medium, O + 3, TextAlign.Center);
            title.SetText("SETTINGS");
            back = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_back", new RectInt(0, 0, 26, 26), O + 2);
            account = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "ACCOUNT", null, new RectInt(0, 0, 140, AccountH), O + 2);
            for (int i = 0; i < NRows; i++)
            {
                labels[i] = InkText("L" + i, kit.Medium, O + 3, TextAlign.Left);
                labels[i].SetText(Names[i]);
                icons[i] = PawGfx.NewSprite("I" + i, transform, O + 3);
                if (i != 1 && i != TutorialRow) toggles[i] = PawGfx.NewSprite("T" + i, transform, O + 3);
            }
            segOrig = PawGfx.NewSprite("SegO", transform, O + 2);
            segClassic = PawGfx.NewSprite("SegC", transform, O + 2);
            segOrigText = LightText("SegOT", kit.Small, O + 4, TextAlign.Center);
            segOrigText.SetText("ORIGINAL");
            segClassicText = LightText("SegCT", kit.Small, O + 4, TextAlign.Center);
            segClassicText.SetText("CLASSIC");
            segReplay = PawGfx.NewSprite("SegR", transform, O + 2);
            segReplayText = LightText("SegRT", kit.Small, O + 4, TextAlign.Center);
            segReplayText.SetText("REPLAY");
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
            UiKit.FillVisible(dim, pipe);
            int w = Mathf.Min(232, vis.width - 16);
            int h = RowsTop + NRows * RowH + 6 + 13 + HowTo.Length * 10 + 10 + AccountH + 8;
            int x = CenterX - w / 2;
            var safe = pipe.Safe;
            int y = Mathf.Max(safe.y + 8, vis.y + (vis.height - h) / 2 - 10);
            if (y + h > safe.yMax - 4) y = Mathf.Max(safe.y + 2, safe.yMax - 4 - h);
            panelRect = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), panelRect);
            back.SetRect(new RectInt(x + 6, y + 6, 26, 26));
            title.At(x + w / 2, y + 14);
            for (int i = 0; i < NRows; i++)
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
            int ty = y + RowsTop + TutorialRow * RowH + (RowH - segH) / 2;
            segReplayRect = new RectInt(x + w - 12 - segW, ty, segW, segH);
            segReplayText.At(segReplayRect.x + segW / 2, segReplayRect.y + (segH - kit.Small.LineHeight) / 2 + 1);
            int hy = y + RowsTop + NRows * RowH + 6;
            howTitle.At(x + 14, hy);
            for (int i = 0; i < howLines.Length; i++) howLines[i].At(x + 14, hy + 13 + i * 10);
            account.SetRect(new RectInt(x + w / 2 - 70, hy + 13 + howLines.Length * 10 + 6, 140, AccountH));
            Refresh();
        }

        private bool Value(int i)
        {
            switch (i)
            {
                case 0: return PawSettings.Music;
                case 2: return PawSettings.Sfx;
                case 3: return PawSettings.Haptics;
                case 4: return PawSettings.ReducedMotion;
                case 5: return PawSettings.SwipeHint;
                default: return true;
            }
        }

        public void Refresh() { Refresh(false); }

        private void Refresh(bool animate)
        {
            for (int i = 0; i < NRows; i++)
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
            kit.PlaceSeg(segOrig, !PawSettings.ClassicMusic, segOrigRect);
            kit.PlaceSeg(segClassic, PawSettings.ClassicMusic, segClassicRect);
            kit.PlaceSeg(segReplay, true, segReplayRect);
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
            for (int i = 0; i < NRows; i++)
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
            account.SetVisible(AccountsUi.Enabled);
            if (AccountsUi.Enabled && account.Tick()) return UiAction.OpenAccount;
            if (!UiPointer.Down || UiPointer.Consumed) return UiAction.None;
            for (int i = 0; i < NRows; i++)
            {
                if (!UiPointer.In(rows[i])) continue;
                UiPointer.Consumed = true;
                if (i == TutorialRow)
                {
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.8f);
                    PawHaptics.Impact(0);
                    return UiAction.Tutorial;   // the game runs it on the duel stage, then returns to the title
                }
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
                    case 5: PawSettings.SwipeHint = !PawSettings.SwipeHint; break;
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
}
