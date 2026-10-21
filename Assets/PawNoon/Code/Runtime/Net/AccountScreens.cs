using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    // M6 account UI (SPEC_V2 §5): the KEEP YOUR BOUNTY / ACCOUNT sheet, the profile card ("WANTED" poster), the
    // name keypad and a two-button confirm card. Existing kit only (panel_wood, panel_paper, btn_large(_alt),
    // seg_on/off, btn_icon) plus the brand buttons btn_black / btn_white and ico_apple / ico_google / ico_profile
    // (Tools/ArtGen/accounts/gen_account_ui.py). Whole pixels everywhere; nothing allocates per frame.
    // Sorting orders: settings 300 < profile 320 < sheet 340 < keypad 350 < confirm 360.

    internal static class CardGfx
    {
        public static SpriteRenderer Dim(Transform parent, int order) { return UiKit.Dim(parent, order); }

        /// <summary>Fills a canvas rect with a WhitePixel renderer (pivot bottom-left).</summary>
        public static void Fill(SpriteRenderer sr, RectInt r) { UiKit.Fill(sr, r); }

        public static void FillVisible(SpriteRenderer sr, PixelPipeline pipe) { UiKit.FillVisible(sr, pipe); }

        /// <summary>Card top: in the bottom thumb zone, never up over the logo / HUD area.</summary>
        public static int ThumbTop(PixelPipeline pipe, int h)
        {
            var safe = pipe.Safe;
            int y = safe.yMax - 12 - h;
            int minY = safe.y + 8;
            return Mathf.Max(minY, y);
        }

        public static int CenterTop(PixelPipeline pipe, int h)
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            int y = Mathf.Max(safe.y + 8, vis.y + (vis.height - h) / 2 - 10);
            if (y + h > safe.yMax - 4) y = Mathf.Max(safe.y + 2, safe.yMax - 4 - h);
            return y;
        }
    }

    /// <summary>
    /// Sign in with Apple / Google button: brand skin (btn_black: white logo + white label; btn_white: four-colour G +
    /// ink label), logo on the left of the label, 1 px sink when pressed (the pressed sprite carries it).
    /// </summary>
    public sealed class BrandButton
    {
        public RectInt Rect;
        public readonly GameObject Root;
        private readonly SpriteRenderer bg;
        private readonly SpriteRenderer icon;
        private readonly BitmapText label;
        private readonly SheetDef up, down;
        private readonly UiKit kit;
        private bool pressed;
        private bool visible = true;

        public BrandButton(UiKit kit, Transform parent, bool dark, string iconName, string text, int order)
        {
            this.kit = kit;
            Root = new GameObject("Brand_" + iconName);
            Root.transform.SetParent(parent, false);
            string skin = dark ? "btn_black" : "btn_white";
            up = kit.Ui.TryGet(skin) ?? kit.Ui.Get(dark ? "btn_large" : "btn_large_alt");
            down = kit.Ui.TryGet(skin + "_pressed") ?? kit.Ui.Get(dark ? "btn_large_pressed" : "btn_large_alt_pressed");
            bg = PawGfx.NewSprite("bg", Root.transform, order);
            bg.sprite = up.First;
            bg.drawMode = SpriteDrawMode.Sliced;
            icon = kit.Sprite(iconName, Root.transform, order + 2);
            label = BitmapText.Create("label", Root.transform, kit.Medium, order + 2, TextAlign.Left);
            label.SetText(text);
            if (dark) label.SetColors(Pal.White, Pal.Ink, Pal.Ink2);
            else
            {
                label.SetColors(Pal.Ink2, Pal.Ink, Pal.Ink2);
                label.Outline = false;
                label.Shadow = false;
            }
        }

        public bool Visible { get { return visible; } }

        public void SetVisible(bool v)
        {
            visible = v;
            Root.SetActive(v);
            if (!v) pressed = false;
        }

        public void SetRect(RectInt r)
        {
            Rect = r;
            Layout();
        }

        private void Layout()
        {
            var d = pressed ? down : up;
            bg.sprite = d.First;
            UiKit.SetRect(bg, d, Rect);
            var co = d.ContentOffset;
            int cy = Rect.y + Rect.height / 2 + co.y;
            const int iconW = 12, gap = 6;
            int w = iconW + gap + label.Width;
            int x0 = Rect.x + (Rect.width - w) / 2;
            if (icon.sprite != null)
            {
                // centre the icon on (x0 + 6, cy) whatever its pivot (same maths as PixelButton.PlaceIcon)
                var s = icon.sprite;
                float ox = s.pivot.x - s.rect.width / 2f;
                float oy = (s.rect.height - s.pivot.y) - s.rect.height / 2f;
                PawGfx.Place(icon, x0 + iconW / 2 + Mathf.Round(ox), cy + Mathf.Round(oy));
            }
            label.At(x0 + iconW + gap, cy - label.Height / 2);
        }

        public bool Tick()
        {
            if (!visible) return false;
            bool inside = UiPointer.In(Rect);
            bool click = false;
            if (UiPointer.Down && inside && !UiPointer.Consumed)
            {
                pressed = true;
                UiPointer.Consumed = true;
                PawHaptics.Impact(0);
                Layout();
            }
            else if (pressed && UiPointer.Up)
            {
                pressed = false;
                Layout();
                if (inside)
                {
                    click = true;
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.8f);
                }
            }
            else if (pressed && UiPointer.Held && !inside)
            {
                pressed = false;
                Layout();
            }
            return click;
        }
    }

    // ------------------------------------------------------------------------------------- sheet

    public enum SheetAction { None, Apple, Google, Close, Delete }

    /// <summary>
    /// KEEP YOUR BOUNTY (once, after the first solo match) and ACCOUNT (Settings): Apple (iOS only), Google, a status
    /// line, DELETE ACCOUNT (account mode), LATER / CLOSE. Wood panel in the bottom thumb zone.
    /// </summary>
    public sealed class AccountSheet : ScreenBase
    {
        private const int O = 340;
        private const int CardW = 216;
        private const int BtnH = 30;
        private static readonly string[] Waiting = { "", ".", "..", "..." };

        private SpriteRenderer dim, panel;
        private BitmapText title, big, line1, line2, status;
        private BrandButton apple, google;
        private PixelButton delete, close;
        private string bigS, l1S, l2S, statusS;
        private bool showApple, showGoogle, showDelete;
        private bool busy;
        private int dotIndex = -1;
        private float dotT;
        private string busyBase = "";
        private readonly string[] busyLines = new string[4];

        protected override void Build()
        {
            dim = CardGfx.Dim(transform, O - 2);
            panel = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, CardW, 120));
            title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
            title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            big = BitmapText.Create("Big", transform, kit.Medium, O + 3, TextAlign.Center);
            big.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            line1 = BitmapText.Create("Line1", transform, kit.Small, O + 3, TextAlign.Center);
            line1.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            line2 = BitmapText.Create("Line2", transform, kit.Small, O + 3, TextAlign.Center);
            line2.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            status = BitmapText.Create("Status", transform, kit.Small, O + 3, TextAlign.Center);
            status.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            apple = new BrandButton(kit, transform, true, "ico_apple", "SIGN IN WITH APPLE", O + 2);
            google = new BrandButton(kit, transform, false, "ico_google", "SIGN IN WITH GOOGLE", O + 2);
            delete = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "DELETE ACCOUNT", null, new RectInt(0, 0, 150, BtnH), O + 2);
            close = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "LATER", null, new RectInt(0, 0, 150, BtnH), O + 2);
        }

        private static bool Has(string s) { return !string.IsNullOrEmpty(s); }

        private int ContentHeight()
        {
            int h = 12 + kit.Medium.LineHeight;
            if (Has(bigS)) h += 7 + kit.Medium.LineHeight;
            if (Has(l1S)) h += 7 + kit.Small.LineHeight;
            if (Has(l2S)) h += 3 + kit.Small.LineHeight;
            h += 10;
            if (showApple) h += BtnH + 6;
            if (showGoogle) h += BtnH + 6;
            h += kit.Small.LineHeight + 8;              // status line (always reserved: no jump when it appears)
            if (showDelete) h += BtnH + 6;
            h += BtnH;
            return h + 12;
        }

        protected override void Layout()
        {
            CardGfx.FillVisible(dim, pipe);
            int w = Mathf.Min(CardW, pipe.Visible.width - 16);
            int h = ContentHeight();
            int x = CenterX - w / 2;
            int y = CardGfx.ThumbTop(pipe, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_wood"), new RectInt(x, y, w, h));
            int cx = x + w / 2;
            int cy = y + 12;
            title.At(cx, cy);
            cy += kit.Medium.LineHeight;
            if (Has(bigS)) { cy += 7; big.At(cx, cy); cy += kit.Medium.LineHeight; }
            if (Has(l1S)) { cy += 7; line1.At(cx, cy); cy += kit.Small.LineHeight; }
            if (Has(l2S)) { cy += 3; line2.At(cx, cy); cy += kit.Small.LineHeight; }
            cy += 10;
            int bw = w - 28;
            if (showApple) { apple.SetRect(new RectInt(cx - bw / 2, cy, bw, BtnH)); cy += BtnH + 6; }
            if (showGoogle) { google.SetRect(new RectInt(cx - bw / 2, cy, bw, BtnH)); cy += BtnH + 6; }
            cy += 2;
            status.At(cx, cy);
            cy += kit.Small.LineHeight + 6;
            if (showDelete) { delete.SetRect(new RectInt(cx - bw / 2, cy, bw, BtnH)); cy += BtnH + 6; }
            close.SetRect(new RectInt(cx - bw / 2, cy, bw, BtnH));
        }

        public void Show(string titleText, string bigText, string l1, string l2, bool withApple, bool withGoogle, bool withDelete, string closeLabel)
        {
            bigS = bigText; l1S = l1; l2S = l2;
            title.SetText(titleText ?? "");
            big.SetText(bigText ?? ""); big.Visible = Has(bigText);
            line1.SetText(l1 ?? ""); line1.Visible = Has(l1);
            line2.SetText(l2 ?? ""); line2.Visible = Has(l2);
            showApple = withApple;
            showGoogle = withGoogle;
            showDelete = withDelete;
            close.SetLabel(closeLabel ?? "CLOSE");
            busy = false;
            ApplyButtons();
            SetStatus(null, Pal.Sand0);
            if (!IsOpen) Open();
            else Layout();
        }

        /// <summary>Hides or shows the link buttons after a change (linked now: no more link buttons).</summary>
        public void SetLinkButtons(bool withApple, bool withGoogle)
        {
            showApple = withApple;
            showGoogle = withGoogle;
            ApplyButtons();
            Layout();
        }

        public void SetBig(string s)
        {
            bool had = Has(bigS);
            bigS = s;
            big.SetText(s ?? "");
            big.Visible = Has(s);
            if (had != Has(s)) Layout();
        }

        public void SetLines(string l1, string l2)
        {
            bool relayout = Has(l1S) != Has(l1) || Has(l2S) != Has(l2);
            l1S = l1; l2S = l2;
            line1.SetText(l1 ?? ""); line1.Visible = Has(l1);
            line2.SetText(l2 ?? ""); line2.Visible = Has(l2);
            if (relayout) Layout();
        }

        public void SetCloseLabel(string s) { close.SetLabel(s); }

        private void ApplyButtons()
        {
            apple.SetVisible(showApple);
            google.SetVisible(showGoogle);
            delete.SetVisible(showDelete);
        }

        public void SetDim(bool on) { dim.enabled = on; }

        /// <summary>Status line under the buttons (null = empty).</summary>
        public void SetStatus(string text, Color32 color)
        {
            statusS = text;
            busy = false;
            status.Color = color;
            status.SetText(text ?? "");
            status.MarkDirty();
        }

        /// <summary>Animated "WAITING FOR APPLE..." while the native sheet / server works; input is ignored.</summary>
        public void SetBusy(string baseText)
        {
            busy = true;
            if (busyBase != baseText)
            {
                busyBase = baseText ?? "";
                for (int i = 0; i < busyLines.Length; i++) busyLines[i] = busyBase + Waiting[i];   // built once per message
            }
            status.Color = Pal.Sun1;
            status.MarkDirty();
            dotIndex = -1;
            dotT = 0f;
            TickDots(0f);
        }

        public bool Busy { get { return busy; } }

        private void TickDots(float dt)
        {
            if (!busy) return;
            dotT += dt;
            int i = PawSettings.ReducedMotion ? 3 : ((int)(dotT / 0.3f)) % busyLines.Length;
            if (i == dotIndex) return;
            dotIndex = i;
            status.SetText(busyLines[i]);
        }

        public SheetAction Tick()
        {
            RelayoutIfNeeded();
            TickDots(PawTime.UnscaledDt);
            if (busy)
            {
                // LATER / CLOSE stays live: a native sheet that never answers must not trap the player
                if (close.Tick()) return SheetAction.Close;
                if (UiPointer.Down) UiPointer.Consumed = true;
                return SheetAction.None;
            }
            if (apple.Tick()) return SheetAction.Apple;
            if (google.Tick()) return SheetAction.Google;
            if (delete.Visible && delete.Tick()) return SheetAction.Delete;
            if (close.Tick()) return SheetAction.Close;
            if (UiPointer.Down) UiPointer.Consumed = true;   // modal
            return SheetAction.None;
        }
    }

    // ------------------------------------------------------------------------------------- profile card

    public enum ProfileAction { None, Close, EditName, Account }

    /// <summary>The profile card: a WANTED poster on paper (name, cat, rating, best, duels, wins, LIGHTNING).</summary>
    public sealed class ProfileCard : ScreenBase
    {
        private const int O = 320;
        private const int CardW = 216;
        private const int RowH = 14;
        private const int Rows = 5;
        private static readonly string[] Keys = { "RATING", "BEST", "DUELS", "WINS", "LIGHTNING" };

        private SpriteRenderer dim, panel, rule;
        private BitmapText title, name, sub, status;
        private readonly BitmapText[] keys = new BitmapText[Rows];
        private readonly BitmapText[] vals = new BitmapText[Rows];
        private PixelButton back, edit, account;
        private RectInt panelRect;

        protected override void Build()
        {
            dim = CardGfx.Dim(transform, O - 2);
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, CardW, 190));
            title = InkText("Title", kit.Medium, O + 3, TextAlign.Center);
            title.SetText("WANTED");
            title.Color = Pal.PaintRed;
            name = InkText("Name", kit.Medium, O + 3, TextAlign.Center);
            sub = InkText("Sub", kit.Small, O + 3, TextAlign.Center);
            sub.Color = Pal.Wood3;
            rule = PawGfx.NewSprite("Rule", transform, O + 1);
            rule.sprite = ArtLib.WhitePixel;
            rule.color = Pal.Sand3;
            for (int i = 0; i < Rows; i++)
            {
                keys[i] = InkText("K" + i, kit.Medium, O + 3, TextAlign.Left);
                keys[i].SetText(Keys[i]);
                keys[i].Color = Pal.Wood3;
                vals[i] = InkText("V" + i, kit.Medium, O + 3, TextAlign.Right);
                vals[i].MonoDigits = true;
            }
            status = InkText("Status", kit.Small, O + 3, TextAlign.Center);
            status.Color = Pal.Wood3;
            back = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_back", new RectInt(0, 0, 26, 26), O + 2);
            edit = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "EDIT NAME", null, new RectInt(0, 0, 96, 26), O + 2);
            account = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "ACCOUNT", null, new RectInt(0, 0, 96, 26), O + 2);
        }

        private int Height { get { return 66 + Rows * RowH + 4 + kit.Small.LineHeight + 6 + 26 + 10; } }

        protected override void Layout()
        {
            CardGfx.FillVisible(dim, pipe);
            int w = Mathf.Min(CardW, pipe.Visible.width - 16);
            int h = Height;
            int x = CenterX - w / 2;
            int y = CardGfx.CenterTop(pipe, h);
            panelRect = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), panelRect);
            int cx = x + w / 2;
            back.SetRect(new RectInt(x + 6, y + 6, 26, 26));
            title.At(cx, y + 14);
            name.At(cx, y + 32);
            sub.At(cx, y + 45);
            CardGfx.Fill(rule, new RectInt(x + 14, y + 57, w - 28, 1));
            int ry = y + 64;
            for (int i = 0; i < Rows; i++)
            {
                keys[i].At(x + 14, ry + i * RowH);
                vals[i].At(x + w - 14, ry + i * RowH);
            }
            int sy = ry + Rows * RowH + 2;
            status.At(cx, sy);
            int by = sy + kit.Small.LineHeight + 6;
            int bw = (w - 28 - 6) / 2;
            edit.SetRect(new RectInt(x + 14, by, bw, 26));
            account.SetRect(new RectInt(x + w - 14 - bw, by, bw, 26));
        }

        public void SetDim(bool on) { dim.enabled = on; }

        /// <summary>Fills the poster. Strings are built by the caller only when the profile changes.</summary>
        public void Set(string displayName, string subLine, string rating, string best, string duels, string wins, string lightning)
        {
            name.SetText(displayName ?? "");
            sub.SetText(subLine ?? "");
            vals[0].SetText(rating ?? "--");
            vals[1].SetText(best ?? "--");
            vals[2].SetText(duels ?? "0");
            vals[3].SetText(wins ?? "0");
            vals[4].SetText(lightning ?? "0");
        }

        public void SetStatus(string text, Color32 color)
        {
            status.Color = color;
            status.SetText(text ?? "");
            status.MarkDirty();
        }

        public void SetEditable(bool on) { edit.SetVisible(on); }

        public ProfileAction Tick()
        {
            RelayoutIfNeeded();
            if (back.Tick())
            {
                if (kit.Audio != null) kit.Audio.Play("sfx_ui_back");
                return ProfileAction.Close;
            }
            if (edit.Tick()) return ProfileAction.EditName;
            if (account.Tick()) return ProfileAction.Account;
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                UiPointer.Consumed = true;
                if (!UiPointer.In(panelRect))
                {
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_back");
                    return ProfileAction.Close;
                }
            }
            return ProfileAction.None;
        }
    }

    // ------------------------------------------------------------------------------------- name keypad

    public enum KeypadAction { None, Save, Cancel }

    /// <summary>
    /// The pixel keypad for a display name: A-Z, 0-9, SPACE, DEL (DisplayName rules: 3-14, collapsed spaces).
    /// Letters fill a 6-wide grid (Y Z SPACE DEL close it); the digits sit apart in two rows of five with brass labels,
    /// because font_medium draws O and 0 identically, so the block is what tells them apart. Keys are 32 x 26 px.
    /// The name lives in a fixed char buffer and is drawn with BitmapText.SetChars (no allocation while typing).
    /// </summary>
    public sealed class NameKeypad : ScreenBase
    {
        private const int O = 350;
        private const int CardW = 232;
        private const int Cols = 6;
        private const int KeyH = 26;
        private const int Gap = 3;
        private const int DigitRowGap = 4;   // extra space above the digit row
        private const int FieldH = 22;
        private const int BtnH = 30;
        private const int KeyCount = 38;   // 26 letters + 10 digits + SPACE + DEL
        private const int SpaceKey = 36;
        private const int DelKey = 37;
        private const string Hint = "3-14 LETTERS, NUMBERS, SPACES";

        private SpriteRenderer dim, panel, field, cursor;
        private BitmapText title, text, hint;
        private readonly SpriteRenderer[] keyBg = new SpriteRenderer[KeyCount];
        private readonly BitmapText[] keyLabel = new BitmapText[KeyCount];
        private readonly RectInt[] keyRect = new RectInt[KeyCount];
        private PixelButton cancel, save;
        private readonly char[] buf = new char[DisplayName.MaxLength];
        private int len;
        private int pressedKey = -1;
        private bool busy;
        private float blinkT;
        private RectInt fieldRect;

        public string Text { get { return new string(buf, 0, len); } }
        public bool Busy { get { return busy; } }

        protected override void Build()
        {
            dim = CardGfx.Dim(transform, O - 2);
            panel = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, CardW, 280));
            title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
            title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            title.SetText("YOUR NAME");
            field = kit.Panel("panel_paper", transform, O + 1, new RectInt(0, 0, CardW - 24, FieldH));
            text = InkText("Text", kit.Medium, O + 3, TextAlign.Center);
            cursor = PawGfx.NewSprite("Cursor", transform, O + 3);
            cursor.sprite = ArtLib.WhitePixel;
            cursor.color = Pal.Ink;
            hint = BitmapText.Create("Hint", transform, kit.Small, O + 3, TextAlign.Center);
            for (int i = 0; i < KeyCount; i++)
            {
                keyBg[i] = PawGfx.NewSprite("Key" + i, transform, O + 1);
                keyBg[i].drawMode = SpriteDrawMode.Sliced;
                keyLabel[i] = BitmapText.Create("KeyL" + i, transform, kit.Medium, O + 3, TextAlign.Center);
                keyLabel[i].SetColors(IsDigit(i) ? Pal.Brass0 : Pal.Cream0, Pal.Ink, Pal.Ink2);
                keyLabel[i].SetText(KeyText(i));
            }
            cancel = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "CANCEL", null, new RectInt(0, 0, 96, BtnH), O + 2);
            save = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "SAVE", null, new RectInt(0, 0, 96, BtnH), O + 2);
            ShowHint(null);
        }

        private static string KeyText(int i)
        {
            if (i < 26) return ((char)('A' + i)).ToString();
            if (i < 36) return ((char)('0' + (i - 26))).ToString();
            return i == SpaceKey ? "SPACE" : "DEL";
        }

        private static bool IsDigit(int i) { return i >= 26 && i < 36; }

        private static char KeyChar(int i)
        {
            if (i < 26) return (char)('A' + i);
            if (i < 36) return (char)('0' + (i - 26));
            return ' ';
        }

        /// <summary>Letters (4 rows + Y Z SPACE DEL) and two digit rows.</summary>
        private const int GridH = 5 * (KeyH + Gap) + DigitRowGap + 2 * KeyH + Gap;

        private int Height { get { return 10 + kit.Medium.LineHeight + 6 + FieldH + 4 + kit.Small.LineHeight + 6 + GridH + 8 + BtnH + 12; } }

        protected override void Layout()
        {
            CardGfx.FillVisible(dim, pipe);
            int w = Mathf.Min(CardW, pipe.Visible.width - 16);
            int h = Height;
            int x = CenterX - w / 2;
            int y = CardGfx.CenterTop(pipe, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_wood"), new RectInt(x, y, w, h));
            int cx = x + w / 2;
            int cy = y + 10;
            title.At(cx, cy);
            cy += kit.Medium.LineHeight + 6;
            fieldRect = new RectInt(x + 12, cy, w - 24, FieldH);
            UiKit.SetRect(field, kit.Ui.Get("panel_paper"), fieldRect);
            PlaceText();
            cy += FieldH + 4;
            hint.At(cx, cy);
            cy += kit.Small.LineHeight + 6;
            int kw = (w - 24 - (Cols - 1) * Gap) / Cols;
            int gridW = Cols * kw + (Cols - 1) * Gap;
            int gx = cx - gridW / 2;
            // A..X: four full rows; row 4: Y Z [SPACE x2] [DEL x2]
            for (int i = 0; i < 26; i++)
            {
                int col = i % Cols, row = i / Cols;
                keyRect[i] = new RectInt(gx + col * (kw + Gap), cy + row * (KeyH + Gap), kw, KeyH);
            }
            int row4 = cy + 4 * (KeyH + Gap);
            keyRect[SpaceKey] = new RectInt(gx + 2 * (kw + Gap), row4, 2 * kw + Gap, KeyH);
            keyRect[DelKey] = new RectInt(gx + 4 * (kw + Gap), row4, 2 * kw + Gap, KeyH);
            // 0..4 / 5..9 in two rows of five, under the letters
            int dy = cy + 5 * (KeyH + Gap) + DigitRowGap;
            int dkw = (gridW - 4 * Gap) / 5;
            int dx = gx + (gridW - (5 * dkw + 4 * Gap)) / 2;
            for (int d = 0; d < 10; d++) keyRect[26 + d] = new RectInt(dx + (d % 5) * (dkw + Gap), dy + (d / 5) * (KeyH + Gap), dkw, KeyH);
            for (int i = 0; i < KeyCount; i++) PlaceKey(i);
            int by = dy + 2 * KeyH + Gap + 8;
            int bw = (gridW - 6) / 2;
            cancel.SetRect(new RectInt(gx, by, bw, BtnH));
            save.SetRect(new RectInt(gx + gridW - bw, by, bw, BtnH));
        }

        private void PlaceKey(int i)
        {
            bool down = i == pressedKey;
            var d = kit.Ui.Get(down ? "seg_on" : "seg_off");
            keyBg[i].sprite = d.First;
            UiKit.SetRect(keyBg[i], d, keyRect[i]);
            var r = keyRect[i];
            // seg_on is the pressed look: its face starts 1 px lower
            keyLabel[i].At(r.x + r.width / 2, r.y + (r.height - kit.Medium.LineHeight) / 2 + (down ? 1 : 0));
            keyLabel[i].Color = IsDigit(i) ? Pal.Brass0 : Pal.Cream0;
            keyLabel[i].MarkDirty();
        }

        private void PlaceText()
        {
            text.SetChars(buf, len);
            int ty = fieldRect.y + (FieldH - kit.Medium.LineHeight) / 2 + 1;
            int cx = fieldRect.x + fieldRect.width / 2;
            text.At(cx, ty);
            int tw = len > 0 ? text.Width : 0;
            CardGfx.Fill(cursor, new RectInt(cx + (tw + 1) / 2 + 1, ty - 1, 1, kit.Medium.LineHeight - 1));
        }

        public void SetDim(bool on) { dim.enabled = on; }

        /// <summary>Opens with <paramref name="current"/> when it is a valid name (guest names start empty).</summary>
        public void Begin(string current)
        {
            len = 0;
            string n;
            if (current != null && DisplayName.Validate(current, out n) == DisplayNameError.None)
                for (int i = 0; i < n.Length && len < buf.Length; i++) buf[len++] = n[i];
            busy = false;
            pressedKey = -1;
            blinkT = 0f;
            ShowHint(null);
            if (!IsOpen) Open();
            else Layout();
            PlaceText();
        }

        /// <summary>null = the rules hint; otherwise a red error line.</summary>
        public void ShowHint(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                hint.SetText(Hint);
                hint.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            }
            else
            {
                hint.SetText(error);
                hint.SetColors(Pal.SigRed, Pal.Ink, Pal.Ink2);
            }
        }

        public void SetBusy(bool on)
        {
            busy = on;
            if (on)
            {
                hint.SetText("SAVING...");
                hint.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            }
        }

        private void Type(int key)
        {
            if (key == DelKey)
            {
                if (len > 0) len--;
            }
            else
            {
                char c = KeyChar(key);
                if (c == ' ' && (len == 0 || buf[len - 1] == ' ')) return;   // no leading or double spaces
                if (len >= buf.Length) { ShowHint(DisplayName.Explain(DisplayNameError.TooLong)); return; }
                buf[len++] = c;
            }
            ShowHint(null);
            blinkT = 0f;
            PlaceText();
        }

        public KeypadAction Tick()
        {
            RelayoutIfNeeded();
            float dt = PawTime.UnscaledDt;
            blinkT += dt;
            bool on = PawSettings.ReducedMotion || ((int)(blinkT / 0.5f) % 2) == 0;
            if (cursor.enabled != on) cursor.enabled = on;
            if (busy)
            {
                if (UiPointer.Down) UiPointer.Consumed = true;
                return KeypadAction.None;
            }
            if (cancel.Tick()) return KeypadAction.Cancel;
            if (save.Tick()) return KeypadAction.Save;

            if (UiPointer.Down && !UiPointer.Consumed)
            {
                UiPointer.Consumed = true;
                for (int i = 0; i < KeyCount; i++)
                {
                    if (!UiPointer.In(keyRect[i])) continue;
                    pressedKey = i;
                    PlaceKey(i);
                    PawHaptics.Impact(0);
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.5f);
                    break;
                }
            }
            else if (pressedKey >= 0 && UiPointer.Up)
            {
                int k = pressedKey;
                pressedKey = -1;
                PlaceKey(k);
                if (UiPointer.In(keyRect[k])) Type(k);
            }
            else if (pressedKey >= 0 && UiPointer.Held && !UiPointer.In(keyRect[pressedKey]))
            {
                int k = pressedKey;
                pressedKey = -1;
                PlaceKey(k);
            }
            return KeypadAction.None;
        }
    }

    // ------------------------------------------------------------------------------------- confirm card

    /// <summary>Title, big line, two small lines and one or two buttons (left = safe choice, right = red action).</summary>
    public sealed class ConfirmCard : ScreenBase
    {
        private const int O = 360;
        private const int CardW = 216;
        private const int BtnH = 30;

        private SpriteRenderer dim, panel;
        private BitmapText title, big, line1, line2;
        private PixelButton left, right, single;
        private bool twoButtons;
        private bool waiting;

        protected override void Build()
        {
            dim = CardGfx.Dim(transform, O - 2);
            panel = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, CardW, 120));
            title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
            title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            big = BitmapText.Create("Big", transform, kit.Medium, O + 3, TextAlign.Center);
            big.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            line1 = BitmapText.Create("Line1", transform, kit.Small, O + 3, TextAlign.Center);
            line1.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            line2 = BitmapText.Create("Line2", transform, kit.Small, O + 3, TextAlign.Center);
            line2.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            left = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "NO", null, new RectInt(0, 0, 90, BtnH), O + 2);
            right = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "YES", null, new RectInt(0, 0, 90, BtnH), O + 2);
            single = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "OK", null, new RectInt(0, 0, 150, BtnH), O + 2);
        }

        private int Height { get { return 12 + 10 + 8 + 10 + 7 + 8 + 3 + 8 + 12 + BtnH + 12; } }

        protected override void Layout()
        {
            CardGfx.FillVisible(dim, pipe);
            int w = Mathf.Min(CardW, pipe.Visible.width - 16);
            int h = Height;
            int x = CenterX - w / 2;
            int y = CardGfx.ThumbTop(pipe, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_wood"), new RectInt(x, y, w, h));
            int cx = x + w / 2;
            title.At(cx, y + 12);
            big.At(cx, y + 30);
            line1.At(cx, y + 47);
            line2.At(cx, y + 58);
            int by = y + h - 12 - BtnH;
            int bw = (w - 28 - 6) / 2;
            left.SetRect(new RectInt(x + 14, by, bw, BtnH));
            right.SetRect(new RectInt(x + w - 14 - bw, by, bw, BtnH));
            single.SetRect(new RectInt(x + 14, by, w - 28, BtnH));
        }

        public void SetDim(bool on) { dim.enabled = on; }

        /// <summary><paramref name="leftLabel"/> null = one button (<paramref name="rightLabel"/>, alt skin); both null = no button (waiting).</summary>
        public void Show(string titleText, string bigText, string l1, string l2, string leftLabel, string rightLabel)
        {
            title.SetText(titleText ?? "");
            big.SetText(bigText ?? "");
            line1.SetText(l1 ?? "");
            line2.SetText(l2 ?? "");
            twoButtons = leftLabel != null && rightLabel != null;
            waiting = leftLabel == null && rightLabel == null;
            left.SetVisible(twoButtons);
            right.SetVisible(twoButtons);
            single.SetVisible(!twoButtons && !waiting);
            if (twoButtons) { left.SetLabel(leftLabel); right.SetLabel(rightLabel); }
            else if (!waiting) single.SetLabel(rightLabel ?? leftLabel);
            if (!IsOpen) Open();
            else Layout();
        }

        /// <summary>0 nothing, 1 left (or the single button), 2 right.</summary>
        public int Tick()
        {
            RelayoutIfNeeded();
            if (twoButtons)
            {
                if (left.Tick()) return 1;
                if (right.Tick()) return 2;
            }
            else if (!waiting && single.Tick()) return 1;
            if (UiPointer.Down) UiPointer.Consumed = true;
            return 0;
        }
    }
}
