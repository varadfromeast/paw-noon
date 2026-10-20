using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>Fonts the M5 screens add to the kit (loaded once, lazily).</summary>
    public static class M5Fonts
    {
        private static BitmapFont code;

        /// <summary>font_code: Jersey 15 at its native 27 px (15 px caps, A-Z 0-9 : -), for room codes.</summary>
        public static BitmapFont Code
        {
            get
            {
                if (code == null) code = BitmapFont.Load("font_code", 18);
                return code;
            }
        }
    }

    /// <summary>
    /// Modal card over the title in the house style of the M4 online card (dim, panel_wood or panel_paper, a title,
    /// a back icon top-left like Settings) placed in the thumb zone, never up over the logo's upper third.
    /// </summary>
    public abstract class M5Card : ScreenBase
    {
        protected const int BackSize = 30;      // the btn_icon art is 30 x 30 (the gear and the trophy use the same)
        protected int O = 282;            // above the online card (280), below settings (300)
        protected SpriteRenderer dim;
        protected SpriteRenderer panel;
        protected string panelName = "panel_wood";
        protected BitmapText title;
        protected PixelButton back;
        protected RectInt card;

        protected void BuildFrame(string panelSprite, bool darkText)
        {
            panelName = panelSprite;
            dim = PawGfx.NewSprite("Dim", transform, O - 2);
            dim.sprite = ArtLib.WhitePixel;
            dim.color = new Color32(Pal.Ink.r, Pal.Ink.g, Pal.Ink.b, 150);
            panel = kit.Panel(panelSprite, transform, O, new RectInt(0, 0, 196, 100));
            if (darkText) title = InkText("Title", kit.Medium, O + 3, TextAlign.Center);
            else
            {
                title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
                title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            }
            back = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_back", new RectInt(0, 0, BackSize, BackSize), O + 2);
        }

        /// <summary>Lays out the dim and a w x h card in the thumb zone; returns the card rect (title + back placed).</summary>
        protected RectInt PlaceCard(int w, int h)
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            dim.transform.position = Px.World(vis.x, vis.yMax);
            dim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            w = Mathf.Min(w, vis.width - 16);
            int x = CenterX - w / 2;
            int y = Mathf.Max(layout.Logo.y + 64, safe.yMax - 12 - h);
            if (y + h > safe.yMax - 4) y = Mathf.Max(safe.y + 4, safe.yMax - 4 - h);
            card = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get(panelName), card);
            back.SetRect(new RectInt(x + 6, y + 6, BackSize, BackSize));
            title.At(x + w / 2, y + 14);
            return card;
        }

        protected BitmapText Line(string name, BitmapFont f, Color32 fill)
        {
            var t = BitmapText.Create(name, transform, f, O + 3, TextAlign.Center);
            t.SetColors(fill, Pal.Ink, Pal.Ink2);
            return t;
        }

        protected static void SetLine(BitmapText t, string s, Color32 c)
        {
            t.SetText(s ?? "");
            t.Color = c;
            t.Visible = !string.IsNullOrEmpty(s);
            t.MarkDirty();
        }

        /// <summary>A tap outside the card closes it like the back icon (the card is modal otherwise).</summary>
        protected bool BackOrOutside()
        {
            if (back.Tick()) { Click("sfx_ui_back"); return true; }
            if (UiPointer.Down && !UiPointer.Consumed && !UiPointer.In(card))
            {
                UiPointer.Consumed = true;
                Click("sfx_ui_back");
                return true;
            }
            return false;
        }

        protected void SwallowTaps()
        {
            if (UiPointer.Down) UiPointer.Consumed = true;
        }

        protected void Click(string sfx)
        {
            if (kit.Audio != null) kit.Audio.Play(sfx);
        }

        /// <summary>A sliced seg sprite (seg_on / seg_off) sized to a rect.</summary>
        protected void PlaceSeg(SpriteRenderer sr, bool on, RectInt r)
        {
            var d = kit.Ui.TryGet(on ? "seg_on" : "seg_off");
            if (d == null) { sr.sprite = null; return; }
            sr.sprite = d.First;
            sr.drawMode = SpriteDrawMode.Sliced;
            UiKit.SetRect(sr, d, r);
        }
    }

    /// <summary>
    /// Four big letter tiles for a room code: seg_off wood tiles with font_code letters; the next slot to type
    /// glows (seg_on). Allocation-free updates (one cached char per tile).
    /// </summary>
    public sealed class CodeTiles
    {
        public const int Tile = 30;
        public const int Gap = 6;
        public static int Width { get { return RoomCode.Length * Tile + (RoomCode.Length - 1) * Gap; } }

        private readonly UiKit kit;
        private readonly SpriteRenderer[] tiles = new SpriteRenderer[RoomCode.Length];
        private readonly BitmapText[] letters = new BitmapText[RoomCode.Length];
        private readonly char[][] one = new char[RoomCode.Length][];
        private readonly bool[] lit = new bool[RoomCode.Length];
        private readonly RectInt[] rects = new RectInt[RoomCode.Length];
        private bool visible = true;

        public CodeTiles(UiKit kit, Transform parent, int order)
        {
            this.kit = kit;
            var font = M5Fonts.Code;
            for (int i = 0; i < RoomCode.Length; i++)
            {
                tiles[i] = PawGfx.NewSprite("Tile" + i, parent, order);
                letters[i] = BitmapText.Create("Letter" + i, parent, font, order + 2, TextAlign.Center);
                letters[i].SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
                one[i] = new char[1];
            }
        }

        /// <summary>Tiles centred on <paramref name="cx"/>, top at <paramref name="y"/>.</summary>
        public void Layout(int cx, int y)
        {
            int x = cx - Width / 2;
            var font = M5Fonts.Code;
            for (int i = 0; i < RoomCode.Length; i++)
            {
                rects[i] = new RectInt(x + i * (Tile + Gap), y, Tile, Tile);
                Paint(i);
                // the 15 px caps sit in the tile's face (the seg art has a 3 px darker lip at the bottom)
                letters[i].At(rects[i].x + Tile / 2, y + (Tile - 3 - font.Baseline) / 2);
            }
        }

        private void Paint(int i)
        {
            var d = kit.Ui.TryGet(lit[i] ? "seg_on" : "seg_off");
            if (d == null) { tiles[i].sprite = null; return; }
            tiles[i].sprite = d.First;
            tiles[i].drawMode = SpriteDrawMode.Sliced;
            UiKit.SetRect(tiles[i], d, rects[i]);
        }

        /// <summary>Shows <paramref name="count"/> letters of <paramref name="chars"/>; <paramref name="cursor"/> lights that slot (-1 none).</summary>
        public void Set(char[] chars, int count, int cursor)
        {
            for (int i = 0; i < RoomCode.Length; i++)
            {
                bool has = chars != null && i < count;
                if (has)
                {
                    one[i][0] = chars[i];
                    letters[i].SetChars(one[i], 1);
                }
                else letters[i].SetChars(one[i], 0);
                bool l = i == cursor;
                if (l != lit[i]) { lit[i] = l; Paint(i); }
            }
        }

        /// <summary>Shows a whole code (null / short = blanks).</summary>
        public void SetCode(string code)
        {
            for (int i = 0; i < RoomCode.Length; i++)
            {
                bool has = code != null && i < code.Length;
                if (has) { one[i][0] = code[i]; letters[i].SetChars(one[i], 1); }
                else letters[i].SetChars(one[i], 0);
                if (lit[i]) { lit[i] = false; Paint(i); }
            }
        }

        public void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            for (int i = 0; i < RoomCode.Length; i++)
            {
                tiles[i].enabled = v;
                letters[i].Visible = v;
            }
        }
    }

    /// <summary>
    /// The A-Z pixel keypad (no OS keyboard): 7 x 4 seg_off keys with font_medium letters, DEL two keys wide.
    /// A key fires on release inside it (like PixelButton); the pressed key shows seg_on. Geometry: KeypadLayout.
    /// </summary>
    public sealed class PixelKeypad
    {
        public const int KeyW = 22;
        public const int KeyH = 20;
        public const int Gap = 2;
        public static int Width { get { return KeypadLayout.Width(KeyW, Gap); } }
        public static int Height { get { return KeypadLayout.Height(KeyH, Gap); } }

        private readonly UiKit kit;
        private readonly SpriteRenderer[] keys = new SpriteRenderer[KeypadLayout.KeyCount];
        private readonly BitmapText[] labels = new BitmapText[KeypadLayout.KeyCount];
        private int ox, oy;
        private int pressed = -1;
        private bool visible = true;

        public PixelKeypad(UiKit kit, Transform parent, int order)
        {
            this.kit = kit;
            for (int i = 0; i < KeypadLayout.KeyCount; i++)
            {
                keys[i] = PawGfx.NewSprite("Key" + i, parent, order);
                labels[i] = BitmapText.Create("KeyL" + i, parent, kit.Medium, order + 2, TextAlign.Center);
                labels[i].SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
                labels[i].SetText(i == KeypadLayout.Delete ? "DEL" : KeypadLayout.LetterAt(i).ToString());
            }
        }

        /// <summary>Keypad centred on <paramref name="cx"/>, top at <paramref name="y"/>.</summary>
        public void Layout(int cx, int y)
        {
            ox = cx - Width / 2;
            oy = y;
            for (int i = 0; i < KeypadLayout.KeyCount; i++) Paint(i);
        }

        private void Paint(int i)
        {
            var r = KeypadLayout.KeyRect(i, ox, oy, KeyW, KeyH, Gap);
            var d = kit.Ui.TryGet(i == pressed ? "seg_on" : "seg_off");
            if (d != null)
            {
                keys[i].sprite = d.First;
                keys[i].drawMode = SpriteDrawMode.Sliced;
                // pressed keys sink 1 px with their label, like the wood sign buttons
                UiKit.SetRect(keys[i], d, new RectInt(r.x, r.y + (i == pressed ? 1 : 0), r.w, r.h));
            }
            labels[i].At(r.x + r.w / 2, r.y + (KeyH - 3 - kit.Medium.LineHeight) / 2 + 1 + (i == pressed ? 1 : 0));
        }

        public void SetVisible(bool v)
        {
            if (visible == v) return;
            visible = v;
            if (!v) pressed = -1;
            for (int i = 0; i < KeypadLayout.KeyCount; i++)
            {
                keys[i].enabled = v;
                labels[i].Visible = v;
            }
        }

        /// <summary>Key index released this frame (0..25 letters, 26 DEL), else -1.</summary>
        public int Tick()
        {
            if (!visible) return -1;
            int hit = KeypadLayout.HitTest(Mathf.FloorToInt(UiPointer.Pos.x), Mathf.FloorToInt(UiPointer.Pos.y), ox, oy, KeyW, KeyH, Gap);
            if (UiPointer.Down && !UiPointer.Consumed && hit >= 0)
            {
                UiPointer.Consumed = true;
                SetPressed(hit);
                PawHaptics.Impact(0);
                return -1;
            }
            if (pressed >= 0 && UiPointer.Up)
            {
                int was = pressed;
                SetPressed(-1);
                if (hit == was)
                {
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.7f);
                    return was;
                }
                return -1;
            }
            if (pressed >= 0 && UiPointer.Held && hit != pressed) SetPressed(-1);   // slid off: cancel
            return -1;
        }

        private void SetPressed(int i)
        {
            if (pressed == i) return;
            int old = pressed;
            pressed = i;
            if (old >= 0) Paint(old);
            if (i >= 0) Paint(i);
        }
    }
}
