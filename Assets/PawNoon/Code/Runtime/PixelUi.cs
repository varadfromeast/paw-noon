using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>Pointer state in canvas px (legacy Input; works with Active Input Handling = Both).</summary>
    public static class UiPointer
    {
        public static bool Down;
        public static bool Held;
        public static bool Up;
        public static Vector2 Pos;
        /// <summary>Set when a widget consumed this frame's press (so the press doesn't also act on the game).</summary>
        public static bool Consumed;

        /// <summary>True once the legacy Input API threw (Active Input Handling = Input System only).</summary>
        public static bool LegacyDead;

        public static void Update(PixelPipeline pipe)
        {
            Consumed = false;
            if (!LegacyDead)
            {
                try
                {
                    bool held = Input.GetMouseButton(0);
                    Down = Input.GetMouseButtonDown(0);
                    Up = Input.GetMouseButtonUp(0);
                    Held = held;
                    if (Down || Held || Up) Pos = pipe.ScreenToCanvas(Input.mousePosition);
                    return;
                }
                catch (System.InvalidOperationException)
                {
                    LegacyDead = true;
                }
            }
#if PAWNOON_INPUT_SYSTEM
            // Input System fallback: the current pointer (touch or mouse)
            var ptr = UnityEngine.InputSystem.Pointer.current;
            if (ptr != null)
            {
                Held = ptr.press.isPressed;
                Down = ptr.press.wasPressedThisFrame;
                Up = ptr.press.wasReleasedThisFrame;
                if (Down || Held || Up) Pos = pipe.ScreenToCanvas(ptr.position.ReadValue());
                return;
            }
#endif
            Down = false; Held = false; Up = false;
        }

        public static bool In(RectInt r)
        {
            return Pos.x >= r.xMin && Pos.x < r.xMax && Pos.y >= r.yMin && Pos.y < r.yMax;
        }
    }

    /// <summary>Shared UI resources.</summary>
    public sealed class UiKit
    {
        public SpriteCatalog Ui;
        public BitmapFont Small;
        public BitmapFont Medium;
        public BitmapFont Timer;
        public PawAudio Audio;

        public SpriteRenderer Sprite(string name, Transform parent, int order)
        {
            var sr = PawGfx.NewSprite(name, parent, order);
            var d = Ui.TryGet(name);
            if (d != null) sr.sprite = d.First;
            else sr.enabled = false; // optional art missing: nothing drawn, never a throw
            return sr;
        }

        /// <summary>9-slice panel (SpriteRenderer Sliced; sprite pivot from ui.json, expected top-left).</summary>
        public SpriteRenderer Panel(string name, Transform parent, int order, RectInt canvasRect)
        {
            var sr = PawGfx.NewSprite(name, parent, order);
            var d = Ui.Get(name);
            sr.sprite = d.First;
            sr.drawMode = SpriteDrawMode.Sliced;
            SetRect(sr, d, canvasRect);
            return sr;
        }

        /// <summary>Sizes a sliced renderer to a canvas rect, honoring the sprite's pivot.</summary>
        public static void SetRect(SpriteRenderer sr, SheetDef d, RectInt r)
        {
            sr.size = new Vector2(r.width, r.height);
            float px = d.Cell.x > 0 ? d.Pivot.x / (float)d.Cell.x : 0f;
            float py = d.Cell.y > 0 ? d.Pivot.y / (float)d.Cell.y : 0f;
            PawGfx.Place(sr, r.x + Mathf.Round(px * r.width), r.y + Mathf.Round(py * r.height));
        }

        public BitmapText Text(string name, Transform parent, BitmapFont font, int order, TextAlign align, string s)
        {
            var t = BitmapText.Create(name, parent, font, order, align);
            t.SetText(s);
            return t;
        }
    }

    /// <summary>Large 9-slice button (>= 24 px tall) with label/icon, press state, SFX + haptic.</summary>
    public sealed class PixelButton
    {
        public RectInt Rect;
        public GameObject Root;
        private readonly SpriteRenderer bg;
        private readonly SpriteRenderer icon;
        private readonly BitmapText label;
        private readonly SheetDef up;
        private readonly SheetDef down;
        private readonly UiKit kit;
        private bool pressed;
        private bool visible = true;
        public bool IsIcon;

        public PixelButton(UiKit kit, Transform parent, string spriteUp, string spriteDown, string text, string iconName, RectInt rect, int order)
        {
            this.kit = kit;
            Root = new GameObject("Btn_" + (text ?? iconName));
            Root.transform.SetParent(parent, false);
            up = kit.Ui.Get(spriteUp);
            down = kit.Ui.Get(spriteDown);
            bg = PawGfx.NewSprite("bg", Root.transform, order);
            bg.sprite = up.First;
            IsIcon = string.IsNullOrEmpty(text);
            if (!IsIcon)
            {
                bg.drawMode = SpriteDrawMode.Sliced;
                label = BitmapText.Create("label", Root.transform, kit.Medium, order + 2, TextAlign.Center);
                label.SetText(text);
                label.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            }
            if (!string.IsNullOrEmpty(iconName))
            {
                icon = PawGfx.NewSprite("icon", Root.transform, order + 2);
                var idef = kit.Ui.TryGet(iconName);
                if (idef != null) icon.sprite = idef.First;
                else icon.enabled = false;
            }
            SetRect(rect);
        }

        public void SetRect(RectInt r)
        {
            Rect = r;
            Layout();
        }

        public void SetVisible(bool v)
        {
            if (v == visible && Root.activeSelf == v) return;   // some owners call this every frame
            visible = v;
            Root.SetActive(v);
            if (!v) pressed = false;
        }

        public bool Visible { get { return visible; } }

        public void SetLabel(string s)
        {
            if (label != null) { label.SetText(s); Layout(); }
        }

        private void Layout()
        {
            var d = pressed ? down : up;
            bg.sprite = d.First;
            // a button that reuses one sprite for both states (e.g. panel_wood) sinks 1 px when pressed
            // (face + label together); buttons with a real pressed sprite already carry their own offset
            int sink = (pressed && down == up) ? 1 : 0;
            var r = new RectInt(Rect.x, Rect.y + sink, Rect.width, Rect.height);
            if (!IsIcon) UiKit.SetRect(bg, d, r);
            else PawGfx.Place(bg, r.x + Mathf.Round(d.Pivot.x * (r.width / (float)Mathf.Max(1, d.Cell.x))), r.y + Mathf.Round(d.Pivot.y * (r.height / (float)Mathf.Max(1, d.Cell.y))));
            var co = d.ContentOffset;
            co = new Vector2Int(co.x, co.y + sink);
            int cx = Rect.x + Rect.width / 2 + co.x;
            int cy = Rect.y + Rect.height / 2 + co.y;
            if (label != null)
            {
                int iconW = icon != null ? 14 : 0;
                int w = label.Width + iconW;
                int lx = cx - w / 2 + label.Width / 2;
                label.At(lx, cy - label.Height / 2);
                if (icon != null)
                {
                    var idf = icon.sprite;
                    PlaceIcon(lx + label.Width / 2 + 4 + 6, cy, idf);
                }
            }
            else if (icon != null)
            {
                PlaceIcon(cx, cy, icon.sprite);
            }
        }

        private void PlaceIcon(int cx, int cy, Sprite s)
        {
            if (s == null) return;
            // icons from ui.json have a centre pivot (6,6) on a 12x12 cell
            float ox = s.rect.width * (s.pivot.x / Mathf.Max(1f, s.rect.width)) - s.rect.width / 2f;
            float oy = s.rect.height * (1f - s.pivot.y / Mathf.Max(1f, s.rect.height)) - s.rect.height / 2f;
            PawGfx.Place(icon, cx + Mathf.Round(ox), cy + Mathf.Round(oy));
        }

        public void SetIcon(string iconName)
        {
            if (icon == null) return;
            var idef = kit.Ui.TryGet(iconName);
            if (idef != null) { icon.sprite = idef.First; icon.enabled = true; }
            else icon.enabled = false;
            Layout();
        }

        /// <summary>Returns true on a click (press and release inside).</summary>
        public bool Tick()
        {
            if (!visible) return false;
            bool inside = UiPointer.In(Rect);
            bool click = false;
            if (UiPointer.Down && inside && !UiPointer.Consumed)
            {
                pressed = true;
                UiPointer.Consumed = true;
                HapticMoments.UiTap();
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
}
