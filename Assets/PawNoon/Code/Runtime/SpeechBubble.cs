using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// One-line pixel speech bubble: panel_paper 9-slice auto-sized to the text (+6 px padding), a tail of
    /// 3 stacked rectangles (each 1 px narrower) pointing at the speaker's mouth, font_small ink text.
    /// Pops in / out with a 1 px offset bounce (no scaling), pixel-snapped, clamped inside the visible rect
    /// and below the duel HUD. Runs on unscaled time so slow-mo / hit-stop never stretch it.
    /// </summary>
    public sealed class SpeechBubble : MonoBehaviour
    {
        public const int DefaultOrder = 240;   // over the world and the round panel (220), under the match screen (260)
        private const float PopSeconds = 0.07f;
        private const int Margin = 2;

        private static readonly Color32 PaperRim = new Color32(0xD4, 0xBF, 0xA0, 255);   // panel_paper bottom rim

        private SpriteRenderer body;
        private SheetDef paperDef;
        private BitmapText label;
        private readonly SpriteRenderer[] tailInk = new SpriteRenderer[BubbleGeometry.TailSteps];
        private readonly SpriteRenderer[] tailPaper = new SpriteRenderer[BubbleGeometry.TailSteps];
        private PawAudio audio;
        private PixelPipeline pipe;
        private bool built;
        private bool reducedMotion;

        private bool showing;
        private float t;
        private float total;
        private PxRect rect;
        private int tailX;
        private bool tailLeft;
        private int lastOff = int.MinValue;

        /// <summary>First canvas y the bubble may use (below the HUD). Set by the owner.</summary>
        public int MinY;

        public static SpeechBubble Create(string name, Transform parent, SpriteCatalog ui, BitmapFont font, PawAudio audio, PixelPipeline pipe, int order = DefaultOrder)
        {
            var go = new GameObject(name);
            if (parent != null) go.transform.SetParent(parent, false);
            var b = go.AddComponent<SpeechBubble>();
            b.Init(ui, font, audio, pipe, order);
            return b;
        }

        public void Init(SpriteCatalog ui, BitmapFont font, PawAudio audioOut, PixelPipeline pipeline, int order = DefaultOrder)
        {
            audio = audioOut;
            pipe = pipeline;
            paperDef = ui.Get("panel_paper");
            body = PawGfx.NewSprite("Body", transform, order);
            body.sprite = paperDef.First;
            body.drawMode = SpriteDrawMode.Sliced;
            for (int k = 0; k < BubbleGeometry.TailSteps; k++)
            {
                tailInk[k] = PawGfx.NewSprite("TailInk" + k, transform, order + 1);
                tailInk[k].sprite = ArtLib.WhitePixel;
                tailInk[k].color = Pal.Ink2;
                tailPaper[k] = PawGfx.NewSprite("TailPaper" + k, transform, order + 2);
                tailPaper[k].sprite = ArtLib.WhitePixel;
                tailPaper[k].color = PaperRim;
            }
            label = BitmapText.Create("Text", transform, font, order + 3, TextAlign.Left);
            label.Outline = false;
            label.Shadow = false;
            label.SetColors(Pal.Ink, Pal.Ink, Pal.Ink);
            built = true;
            SetVisible(false);
        }

        public void SetReducedMotion(bool on) { reducedMotion = on; }

        public bool Visible { get { return showing; } }

        /// <summary>Body rect in canvas px of the current / last bubble.</summary>
        public RectInt Rect { get { return new RectInt(rect.x, rect.y, rect.w, rect.h); } }

        /// <summary>
        /// Shows one line. anchorCanvas = where the tail tip points (the speaker's mouth). tailLeft = tail at the
        /// bubble's left end (bubble to the right of the speaker). seconds = time fully shown (pop in/out added).
        /// </summary>
        public void Show(string text, Vector2Int anchorCanvas, bool tailLeft, float seconds)
        {
            if (!built || string.IsNullOrEmpty(text)) return;
            label.SetText(text);
            this.tailLeft = tailLeft;
            var vis = pipe != null ? pipe.Visible : new RectInt(0, 0, Px.HalfW * 2, Px.HalfH * 2);
            var bounds = new PxRect(vis.x, vis.y, vis.width, vis.height);
            rect = BubbleGeometry.Layout(label.Width, label.Height, anchorCanvas.x, anchorCanvas.y, tailLeft, bounds, MinY, Margin, out tailX);
            t = 0f;
            total = Mathf.Max(0.1f, seconds) + PopSeconds * 2f;
            showing = true;
            lastOff = int.MinValue;
            SetVisible(true);
            Place(PopOffset());
            if (audio != null) audio.Play("sfx_ui_tap", 0.4f);
        }

        public void Hide()
        {
            showing = false;
            if (built) SetVisible(false);
        }

        private void SetVisible(bool v)
        {
            body.enabled = v;
            label.Visible = v;
            for (int k = 0; k < tailInk.Length; k++)
            {
                tailInk[k].enabled = v;
                tailPaper[k].enabled = v;
            }
        }

        /// <summary>1 px lift on the first and last frames: the pop.</summary>
        private int PopOffset()
        {
            if (reducedMotion) return 0;
            if (t < PopSeconds || t > total - PopSeconds) return -1;
            return 0;
        }

        private void Place(int off)
        {
            if (off == lastOff) return;
            lastOff = off;
            UiKit.SetRect(body, paperDef, new RectInt(rect.x, rect.y + off, rect.w, rect.h));
            label.At(rect.x + BubbleGeometry.Pad, rect.y + BubbleGeometry.Pad + off);
            for (int k = 0; k < BubbleGeometry.TailSteps; k++)
            {
                var s = BubbleGeometry.Step(k, tailLeft, tailX, rect.yMax);
                FillRect(tailInk[k], s.x, s.y + off, s.w, s.h);
                int ph = k == BubbleGeometry.TailSteps - 1 ? s.h - 1 : s.h;
                FillRect(tailPaper[k], s.x + 1, s.y + off, s.w - 2, ph);
            }
        }

        /// <summary>WhitePixel has a bottom-left pivot: place at the rect's bottom edge, scale to size.</summary>
        private static void FillRect(SpriteRenderer sr, int x, int y, int w, int h)
        {
            sr.transform.localScale = new Vector3(Mathf.Max(0, w), Mathf.Max(0, h), 1f);
            PawGfx.Place(sr, x, y + h);
        }

        private void Update()
        {
            if (!showing) return;
            t += PawTime.UnscaledDt;
            if (t >= total)
            {
                Hide();
                return;
            }
            Place(PopOffset());
        }
    }
}
