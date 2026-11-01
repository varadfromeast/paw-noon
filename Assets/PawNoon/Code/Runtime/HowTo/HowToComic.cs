using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// HOW TO DUEL (Docs/TUTORIAL.md): a ~10 s, three-beat animated comic shown as a modal over the town. Each beat is
    /// a wood-framed parchment panel with a one-line caption, a tiny sub-line and a looping pixel scene built from the
    /// game's own sprites (<see cref="HowToBeatHolster"/>, <see cref="HowToBeatWait"/>, <see cref="HowToBeatDraw"/>).
    /// Panels slam in (whole-pixel drop, one white frame), turn by themselves after beatSeconds, a tap turns at once,
    /// page dots show 1-2-3, SKIP sits top-right, the last beat ends on GOT IT!. Reduced Motion: no slam, flash or
    /// shake (sounds stay). Everything sits inside the 270x480 core. Allocation-free per frame.
    /// </summary>
    public sealed class HowToComic : ScreenBase
    {
        public const int O = 400;   // over every title card (accounts 320..360), under the haptics lab (900)
        private const int W = 244;
        private const int PanelH = HowToStage.H + 55;
        private const int BtnW = 140, BtnH = 30;
        private const int Total = 14 + PanelH + 8 + BtnH;
        private static readonly string[] Captions = { "PAW ON THE HOLSTER", "HEARTBEAT = WAIT", "DRAW! SWIPE UP" };
        private static readonly string[] Subs = { "rest your thumb here", "twitch early and your gun jams", "fastest paw wins" };
        private static readonly string[] DotFill = { ".XX.", "XXXX", "XXXX", ".XX." };
        private static readonly string[] DotRing = { ".XX.", "X..X", "X..X", ".XX." };
        private static readonly int[] BtnPop = { -6, -3, 0, -1, 0 };

        private HowToTuning tune;
        private HowToStage stage;
        private readonly HowToBeat[] beats = new HowToBeat[3];
        private SpriteRenderer dim, wood, paper, flash, skipLine;
        private readonly SpriteRenderer[] border = new SpriteRenderer[4];
        private readonly SpriteRenderer[] dots = new SpriteRenderer[3];
        private Sprite dotFill, dotRing;
        private BitmapText header, skip, caption, sub, hint;
        private PixelButton gotIt;
        private int[] slamKeys, shakeKeys;
        private int top, bx, cx;
        private RectInt skipRect;
        private int beat;
        private float pageT, slamT = 99f, shakeT = 99f, btnT = 99f;
        private bool landed = true, hold, frozen, reduced, skipPressed;
        private int flashLeft, btnDy = int.MinValue, posedDx = int.MinValue, posedDy = int.MinValue;

        /// <summary>The comic ended through SKIP (false: GOT IT, or still open).</summary>
        public bool Skipped { get; private set; }
        /// <summary>Current beat, 1..3.</summary>
        public int Beat { get { return beat + 1; } }

        protected override void Build()
        {
            tune = FeelConfig.Current.HowTo;
            slamKeys = new[] { -tune.slamPx, -tune.slamPx / 2, 0, -2, 0 };
            int p = tune.shakePx, h = (tune.shakePx + 1) / 2;
            shakeKeys = new[] { -p, p, -h, h, 0 };
            dim = UiKit.Dim(transform, O - 2, 170);
            wood = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, W, PanelH));
            paper = kit.Panel("panel_paper", transform, O + 1, new RectInt(0, 0, W - 10, PanelH - 10));
            header = LightText("Header", kit.Medium, O + 3, TextAlign.Center);
            header.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            header.SetText("HOW TO DUEL");
            skip = LightText("Skip", kit.Small, O + 3, TextAlign.Right);
            skip.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            skip.SetText("SKIP");
            skipLine = UiKit.Dim(transform, O + 3, 255);
            skipLine.color = Pal.Cream1;
            caption = InkText("Caption", kit.Medium, O + 3, TextAlign.Center);
            sub = InkText("Sub", kit.Small, O + 3, TextAlign.Center);
            sub.SetColors(Pal.Wood3, Pal.Wood3, Pal.Wood3);
            hint = LightText("Hint", kit.Small, O + 3, TextAlign.Center);
            hint.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            hint.SetText("TAP FOR NEXT");
            dotFill = SwipeCue.FromRows("PawHowToDotFill", DotFill, Pal.White, Pal.White, false);
            dotRing = SwipeCue.FromRows("PawHowToDotRing", DotRing, Pal.White, Pal.White, false);
            for (int i = 0; i < dots.Length; i++) dots[i] = PawGfx.NewSprite("Dot" + i, transform, O + 3);
            gotIt = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "GOT IT!", null, new RectInt(0, 0, BtnW, BtnH), O + 2);

            var sceneRoot = new GameObject("Scene").transform;
            sceneRoot.SetParent(transform, false);
            stage = new HowToStage(sceneRoot, kit, tune, new SpriteCatalog("Art/FX/", "fx"), O + 4);
            beats[0] = new HowToBeatHolster(stage);
            beats[1] = new HowToBeatWait(stage);
            beats[2] = new HowToBeatDraw(stage);
            for (int i = 0; i < border.Length; i++) border[i] = stage.Block("Border" + i, 16, Pal.Ink);
            flash = stage.Block("Flash", 17, Pal.White);
            flash.enabled = false;
        }

        protected override void Layout()
        {
            var safe = pipe.Safe;
            // the 270x480 core, centred on the canvas; never under the notch or the home bar
            int coreY = (layout.Canvas.y - layout.Core.y) / 2;
            int rTop = Mathf.Max(coreY, safe.y + 4);
            int rBot = Mathf.Min(coreY + layout.Core.y, safe.yMax - 4);
            top = rTop + Mathf.Max(0, (rBot - rTop - Total) / 2);
            cx = layout.Canvas.x / 2;
            bx = cx - W / 2;
            UiKit.FillVisible(dim, pipe);
            header.At(cx, top);
            int sx = bx + W - 2;
            skip.At(sx, top + 1);
            int sw = skip.Width;
            UiKit.Fill(skipLine, new RectInt(sx - sw, top + 10, sw, 1));
            skipRect = new RectInt(sx - sw - 14, top - 8, sw + 22, 26);
            hint.At(cx, top + 14 + PanelH + 8 + 11);
            btnDy = int.MinValue;
            posedDx = int.MinValue;
            PlaceButton();
            Pose(0, 0);
        }

        public void SetReducedMotion(bool on) { reduced = on; if (stage != null) stage.Reduced = on; }

        public override void Open()
        {
            Skipped = false;
            hold = false;
            frozen = false;
            skipPressed = false;
            base.Open();
            ShowBeat(0, 0f);
        }

        public override void Close()
        {
            for (int i = 0; i < beats.Length; i++) if (beats[i] != null) beats[i].End();
            flashLeft = 0;
            if (flash != null) flash.enabled = false;
            base.Close();
        }

        /// <summary>Turns the page (beats 1 and 2); false on the last beat.</summary>
        public bool Next()
        {
            if (beat >= beats.Length - 1) return false;
            ShowBeat(beat + 1, 0f);
            return true;
        }

        /// <summary>Review: jump to beat 1..3 and stay there; at >= 0 also freezes the scene at that time (a still).</summary>
        public void ReviewBeat(int number, float at)
        {
            hold = true;
            frozen = at >= 0f;
            ShowBeat(Mathf.Clamp(number - 1, 0, beats.Length - 1), Mathf.Max(0f, at));
            if (frozen) { slamT = 99f; landed = true; flashLeft = 0; btnT = 99f; }
        }

        public string StatusText
        {
            get { return IsOpen ? "beat" + Beat + (hold ? "(hold" + (frozen ? ",frozen@" + beats[beat].Time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "") + ")" : "") : "off"; }
        }

        private void ShowBeat(int i, float at)
        {
            for (int k = 0; k < beats.Length; k++) if (k != i) beats[k].End();
            beat = i;
            pageT = 0f;
            shakeT = 99f;
            caption.SetText(Captions[i]);
            sub.SetText(Subs[i]);
            for (int k = 0; k < dots.Length; k++)
            {
                dots[k].sprite = k <= i ? dotFill : dotRing;
                dots[k].color = k == i ? Pal.PaintRed : (k < i ? Pal.Wood3 : Pal.Sand3);
            }
            bool last = i == beats.Length - 1;
            hint.Visible = !last;
            gotIt.SetVisible(last);
            btnT = last && !reduced ? 0f : 99f;
            btnDy = int.MinValue;
            stage.Reduced = reduced;
            stage.ShakeRequest = 0f;
            stage.FlashRequest = 0;
            if (reduced) { slamT = 99f; landed = true; Sfx("sfx_stamp"); }
            else { slamT = 0f; landed = false; Sfx("sfx_whoosh"); }
            posedDx = int.MinValue;
            beats[i].Begin(at);
            PlaceButton();
            Pose(0, slamT < 99f ? slamKeys[0] : 0);   // placed before the next render, not one frame late
        }

        private void Sfx(string key)
        {
            if (kit.Audio != null) kit.Audio.Play(key, 0.5f);
        }

        private void PlaceButton()
        {
            int bdy = btnT < BtnPop.Length * 0.04f ? HowToStage.Steps(BtnPop, btnT, 0.04f) : 0;
            if (bdy == btnDy) return;
            btnDy = bdy;
            gotIt.SetRect(new RectInt(cx - BtnW / 2, top + 14 + PanelH + 8 + bdy, BtnW, BtnH));
        }

        /// <summary>Per frame while open. True when the comic is done (GOT IT! or SKIP; see <see cref="Skipped"/>).</summary>
        public bool Tick()
        {
            RelayoutIfNeeded();
            float dt = PawTime.UnscaledDt;
            if (TickSkip()) { Skipped = true; return true; }
            if (gotIt.Tick()) { Skipped = false; return true; }
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                UiPointer.Consumed = true;   // modal: every tap is ours; on beats 1-2 it turns the page
                Next();
            }
            pageT += dt;
            if (!hold && beat < beats.Length - 1 && pageT >= tune.beatSeconds) Next();

            beats[beat].Tick(frozen ? 0f : dt);
            if (stage.ShakeRequest > 0f) { if (!reduced) shakeT = 0f; stage.ShakeRequest = 0f; }
            if (stage.FlashRequest > 0) { if (!reduced) flashLeft = Mathf.Max(flashLeft, stage.FlashRequest); stage.FlashRequest = 0; }

            int dy = 0, dx = 0;
            if (slamT < slamKeys.Length * tune.slamStepSeconds)
            {
                slamT += dt;
                dy = HowToStage.Steps(slamKeys, slamT, tune.slamStepSeconds);
                if (!landed && slamT >= 2 * tune.slamStepSeconds)
                {
                    landed = true;
                    flashLeft = tune.flashFrames;
                    Sfx("sfx_stamp");
                }
            }
            if (shakeT < shakeKeys.Length * 0.04f)
            {
                shakeT += dt;
                dx = HowToStage.Steps(shakeKeys, shakeT, 0.04f);
            }
            if (btnT < BtnPop.Length * 0.04f) btnT += dt;
            PlaceButton();
            Pose(dx, dy);
            // exactly flashFrames rendered frames of white over the scene
            flash.enabled = flashLeft > 0;
            if (flashLeft > 0) flashLeft--;
            return false;
        }

        private void Pose(int dx, int dy)
        {
            int px = bx + dx, py = top + 14 + dy;
            stage.X = px + 5 + 7;
            stage.Y = py + 5 + 30;
            if (dx != posedDx || dy != posedDy)
            {
                posedDx = dx;
                posedDy = dy;
                UiKit.SetRect(wood, kit.Ui.Get("panel_wood"), new RectInt(px, py, W, PanelH));
                UiKit.SetRect(paper, kit.Ui.Get("panel_paper"), new RectInt(px + 5, py + 5, W - 10, PanelH - 10));
                caption.At(cx + dx, py + 12);
                sub.At(cx + dx, py + 24);
                for (int i = 0; i < dots.Length; i++) PawGfx.Place(dots[i], cx + dx - 12 + i * 8, stage.Y + HowToStage.H + 5);
                stage.PutBlock(border[0], -1, -1, HowToStage.W + 2, 1);
                stage.PutBlock(border[1], -1, HowToStage.H, HowToStage.W + 2, 1);
                stage.PutBlock(border[2], -1, 0, 1, HowToStage.H);
                stage.PutBlock(border[3], HowToStage.W, 0, 1, HowToStage.H);
                stage.PutBlock(flash, 0, 0, HowToStage.W, HowToStage.H);
            }
            if (beats[beat] != null && beats[beat].On) beats[beat].Pose();
        }

        /// <summary>SKIP pressed and released on the text (generous target). Consumes the pointer.</summary>
        private bool TickSkip()
        {
            bool inside = UiPointer.In(skipRect);
            if (UiPointer.Down && inside && !UiPointer.Consumed)
            {
                skipPressed = true;
                UiPointer.Consumed = true;
                HapticMoments.UiTap();
            }
            else if (skipPressed && UiPointer.Up)
            {
                skipPressed = false;
                if (inside) { if (kit.Audio != null) kit.Audio.Play("sfx_ui_tap", 0.8f); return true; }
            }
            else if (skipPressed && UiPointer.Held && !inside) skipPressed = false;
            return false;
        }
    }
}
