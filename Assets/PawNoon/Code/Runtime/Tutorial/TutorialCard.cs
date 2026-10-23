using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The tutorial's paper card (Docs/TUTORIAL.md): panel_paper 9-slice just under the HIGH NOON badge (so it
    /// never covers the badge's heart monitor / timer window, the opponent, the guns or the holster), an animated
    /// paw (tap_hint) at the top-left, a Medium ink title, two Small wood-brown lines, the step dots (1/3) at the
    /// bottom-left and a SKIP text button at the bottom-right. The ready card is taller and carries a btn_large
    /// TAP TO START button instead of the dots and SKIP. Motion is whole-pixel keyframes only (drop-in, a sideways
    /// shake on a mistake); Reduced Motion turns both off. Allocation-free per frame.
    /// </summary>
    public sealed class TutorialCard : MonoBehaviour
    {
        /// <summary>Over the duel HUD (200..206) and the round panel (220), under the speech bubble (240).</summary>
        public const int Order = 230;
        private const int MaxW = 232;
        private const int H = 42;
        private const int ReadyH = 60;
        private const int ButtonW = 124, ButtonH = 24;
        private const float StepSeconds = 0.04f;
        private static readonly int[] Drop = { -4, -2, 1, 0 };
        private static readonly int[] ShakeDx = { -3, 3, -2, 2, -1, 1, 0 };
        private static readonly string[] DotFill = { ".XX.", "XXXX", "XXXX", ".XX." };
        private static readonly string[] DotRing = { ".XX.", "X..X", "X..X", ".XX." };
        private static Sprite dotFillSprite, dotRingSprite;

        private UiKit kit;
        private GameLayout layout;
        private PixelPipeline pipe;
        private SheetDef paperDef, pawDef;
        private SpriteRenderer panel, paw, skipLine;
        private BitmapText title, line1, line2, skip;
        private readonly SpriteRenderer[] dots = new SpriteRenderer[3];
        private PixelButton start;
        private bool shown, ready, skipOn, skipPressed, reducedMotion;
        private int step;
        private RectInt rect;       // resting rect (canvas px)
        private RectInt skipRect;   // touch target
        private float popT = 99f, shakeT = 99f, pawT;
        private int pawFrame = -1;
        private int placedDx = int.MinValue, placedDy = int.MinValue;
        private int laidOutVersion = -1;

        public bool Visible { get { return shown; } }
        public RectInt Rect { get { return rect; } }

        public void Init(UiKit k, GameLayout l, PixelPipeline p)
        {
            kit = k;
            layout = l;
            pipe = p;
            paperDef = kit.Ui.Get("panel_paper");
            pawDef = kit.Ui.TryGet("tap_hint");
            panel = kit.Panel("panel_paper", transform, Order, new RectInt(0, 0, MaxW, H));
            paw = PawGfx.NewSprite("Paw", transform, Order + 2);
            if (pawDef != null) paw.sprite = pawDef.First;
            title = Ink("Title", kit.Medium, Pal.Ink, TextAlign.Center);
            line1 = Ink("Line1", kit.Small, Pal.Wood3, TextAlign.Center);
            line2 = Ink("Line2", kit.Small, Pal.Wood3, TextAlign.Center);
            skip = Ink("Skip", kit.Small, Pal.Wood4, TextAlign.Right);
            skip.SetText("SKIP");
            skipLine = PawGfx.NewSprite("SkipLine", transform, Order + 3);
            skipLine.sprite = ArtLib.WhitePixel;
            skipLine.color = Pal.Wood4;
            for (int i = 0; i < dots.Length; i++) dots[i] = PawGfx.NewSprite("Dot" + i, transform, Order + 2);
            start = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "TAP TO START", null, new RectInt(0, 0, ButtonW, ButtonH), Order + 2);
            Relayout();
            SetShown(false);
        }

        private BitmapText Ink(string name, BitmapFont f, Color32 c, TextAlign a)
        {
            var t = BitmapText.Create(name, transform, f, Order + 3, a);
            t.SetColors(c, c, c);
            t.Outline = false;
            t.Shadow = false;
            return t;
        }

        public void SetReducedMotion(bool on) { reducedMotion = on; }

        /// <summary>A step card: dots show <paramref name="cardStep"/> of 3. Pops in (drop) when the text changes.</summary>
        public void Show(int cardStep, string titleText, Color32 titleColor, string l1, string l2, bool allowSkip)
        {
            ready = false;
            skipOn = allowSkip;
            step = cardStep;
            SetTexts(titleText, titleColor, l1, l2);
            Relayout();
            SetShown(true);
            Pop();
        }

        /// <summary>The last card: taller, a TAP TO START button (label may differ for a replay), no dots or SKIP.</summary>
        public void ShowReady(string titleText, string l1, string buttonLabel)
        {
            ready = true;
            skipOn = false;
            step = 3;
            SetTexts(titleText, Pal.Ink, l1, null);
            start.SetLabel(buttonLabel);
            Relayout();
            SetShown(true);
            Pop();
        }

        /// <summary>Sideways whole-pixel shake (a mistake). Nothing under Reduced Motion.</summary>
        public void Shake()
        {
            if (!reducedMotion) shakeT = 0f;
        }

        public void Hide()
        {
            SetShown(false);
        }

        /// <summary>SKIP pressed and released on the text (generous touch target). Consumes the pointer.</summary>
        public bool TickSkip()
        {
            if (!shown || !skipOn) { skipPressed = false; return false; }
            bool inside = UiPointer.In(skipRect);
            if (UiPointer.Down && inside && !UiPointer.Consumed)
            {
                skipPressed = true;
                UiPointer.Consumed = true;
                HapticMoments.UiTap();
                placedDx = int.MinValue;   // sink 1 px
            }
            else if (skipPressed && UiPointer.Up)
            {
                skipPressed = false;
                placedDx = int.MinValue;
                if (inside)
                {
                    if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.8f);
                    return true;
                }
            }
            else if (skipPressed && UiPointer.Held && !inside)
            {
                skipPressed = false;
                placedDx = int.MinValue;
            }
            return false;
        }

        /// <summary>TAP TO START clicked (ready card only).</summary>
        public bool TickStart()
        {
            return shown && ready && start.Tick();
        }

        // ------------------------------------------------------------------ layout

        private void SetTexts(string titleText, Color32 titleColor, string l1, string l2)
        {
            title.SetText(titleText);
            title.SetColors(titleColor, titleColor, titleColor);
            line1.SetText(l1 ?? "");
            line2.SetText(l2 ?? "");
        }

        private void SetShown(bool v)
        {
            shown = v;
            if (!v) { skipPressed = false; popT = 99f; shakeT = 99f; }
            panel.enabled = v;
            paw.enabled = v && pawDef != null;
            title.Visible = v;
            line1.Visible = v;
            line2.Visible = v && !ready;
            skip.Visible = v && skipOn;
            skipLine.enabled = v && skipOn;
            for (int i = 0; i < dots.Length; i++) dots[i].enabled = v && !ready;
            start.SetVisible(v && ready);
            if (v) ApplyDots();
        }

        private void Pop()
        {
            popT = reducedMotion ? 99f : 0f;
            placedDx = int.MinValue;
            Place(0, reducedMotion ? 0 : Drop[0]);
        }

        /// <summary>Card rect: centred on the badge (layout.Cue.x), its top one pixel under the badge art.</summary>
        private void Relayout()
        {
            laidOutVersion = pipe.LayoutVersion;
            var vis = pipe.Visible;
            int w = Mathf.Min(MaxW, vis.width - 16);
            int cx = Mathf.Clamp(layout.Cue.x, vis.x + w / 2 + 4, vis.xMax - w / 2 - 4);
            int top = layout.Timer.y + 20;
            var b = kit.Ui.TryGet("hud_badge");
            if (b != null && !b.Missing)
            {
                int wy = b.HasInner ? b.Inner.y + b.Inner.height / 2 : b.Cell.y / 2;
                top = layout.Cue.y - wy + b.Cell.y + 1;
            }
            int h = ready ? ReadyH : H;
            int bottom = top + H;   // the ready card grows upward (over the badge's timer window), never down onto the opponent
            rect = new RectInt(cx - w / 2, bottom - h, w, h);
            placedDx = int.MinValue;
            Place(0, 0);
        }

        private void Place(int dx, int dy)
        {
            int sink = skipPressed ? 1 : 0;
            if (dx == placedDx && dy == placedDy) return;
            placedDx = dx;
            placedDy = dy;
            int x = rect.x + dx, y = rect.y + dy, w = rect.width;
            UiKit.SetRect(panel, paperDef, new RectInt(x, y, w, rect.height));
            if (pawDef != null) PawGfx.Place(paw, x + 6 + pawDef.Pivot.x, y + 3 + pawDef.Pivot.y);
            title.At(x + w / 2, y + 6);
            line1.At(x + w / 2, y + 19);
            line2.At(x + w / 2, y + 28);
            int sx = x + w - 8;
            skip.At(sx, y + 28 + sink);
            int sw = skip.Width;
            skipLine.transform.position = Px.World(sx - sw, y + 36 + sink);
            skipLine.transform.localScale = new Vector3(sw, 1f, 1f);
            skipRect = new RectInt(sx - sw - 12, y + 18, sw + 22, rect.height - 18 + 10);
            for (int i = 0; i < dots.Length; i++) PawGfx.Place(dots[i], x + 8 + i * 6, y + 30);
            if (ready) start.SetRect(new RectInt(x + w / 2 - ButtonW / 2, y + rect.height - 6 - ButtonH, ButtonW, ButtonH));
        }

        private void ApplyDots()
        {
            for (int i = 0; i < dots.Length; i++)
            {
                bool filled = i < step;
                dots[i].sprite = DotSprite(filled);
                dots[i].color = i == step - 1 ? (Color)Pal.PaintRed : (Color)Pal.Wood3;
            }
        }

        private void Update()
        {
            if (!shown) return;
            if (pipe.LayoutVersion != laidOutVersion) Relayout();
            float dt = PawTime.UnscaledDt;
            int dx = 0, dy = 0;
            if (popT < Drop.Length * StepSeconds)
            {
                popT += dt;
                dy = Drop[Mathf.Min(Drop.Length - 1, (int)(popT / StepSeconds))];
            }
            if (shakeT < ShakeDx.Length * StepSeconds)
            {
                shakeT += dt;
                dx = ShakeDx[Mathf.Min(ShakeDx.Length - 1, (int)(shakeT / StepSeconds))];
            }
            Place(dx, dy);
            // the paw taps twice a second (2-frame tap_hint), a quiet "your thumb goes here" beat
            if (pawDef != null && pawDef.Count > 1)
            {
                pawT += dt;
                int f = reducedMotion ? 0 : ((int)(pawT * 3f)) % pawDef.Count;
                if (f != pawFrame) { pawFrame = f; paw.sprite = pawDef.Frames[f]; }
            }
        }

        // ------------------------------------------------------------------ procedural dots

        private static Sprite DotSprite(bool filled)
        {
            if (filled) { if (dotFillSprite == null) dotFillSprite = SwipeCue.FromRows("PawTutDotFill", DotFill, Pal.White, Pal.White, false); return dotFillSprite; }
            if (dotRingSprite == null) dotRingSprite = SwipeCue.FromRows("PawTutDotRing", DotRing, Pal.White, Pal.White, false);   // white, tinted by the renderer
            return dotRingSprite;
        }
    }
}
