using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The inside of one HOW TO DUEL comic panel: a 220x168 scene rect whose top-left (X, Y) already includes the
    /// panel's slam / shake offset. Beats place every sprite in scene-local px through <see cref="Put"/> each frame,
    /// so the whole picture moves with the panel. Also the shared helpers: sprite crops, the procedural backdrops,
    /// white flash, comic words. Allocation-free after construction.
    /// </summary>
    public sealed class HowToStage
    {
        public const int W = 220, H = 168;
        public readonly Transform Root;
        public readonly UiKit Kit;
        public readonly HowToTuning Tune;
        /// <summary>Art/FX catalog (fx.json), shared with the world's particles' manifest.</summary>
        public readonly SpriteCatalog Fx;
        /// <summary>Base sorting order of scene content (backdrop = Order, content Order+1..Order+12).</summary>
        public readonly int Order;
        /// <summary>Scene top-left on the canvas, slam / shake offset included (set by the comic before every Pose).</summary>
        public int X, Y;
        public bool Reduced;
        /// <summary>Set by a beat: shake the panel this many seconds (the comic ignores it under Reduced Motion).</summary>
        public float ShakeRequest;
        /// <summary>Set by a beat: white flash frames over the scene (ignored under Reduced Motion).</summary>
        public int FlashRequest;
        /// <summary>Font for comic words and WAIT (font_code, else font_medium).</summary>
        public readonly BitmapFont Loud;

        private readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();

        public HowToStage(Transform root, UiKit kit, HowToTuning tune, SpriteCatalog fx, int order)
        {
            Fx = fx;
            Root = root;
            Kit = kit;
            Tune = tune;
            Order = order;
            Loud = kit.Code != null && !kit.Code.Fallback ? kit.Code : kit.Medium;
        }

        public PawAudio Audio { get { return Kit.Audio; } }

        public void Sfx(string key, float volume = 1f, float pitch = 1f)
        {
            if (Kit.Audio != null) Kit.Audio.Play(key, volume, pitch);
        }

        public SpriteRenderer NewSprite(string name, int orderOffset, Sprite s)
        {
            var sr = PawGfx.NewSprite(name, Root, Order + orderOffset);
            sr.sprite = s;
            sr.enabled = false;
            return sr;
        }

        /// <summary>A flat palette block (WhitePixel, pivot bottom-left); size it with <see cref="PutBlock"/>.</summary>
        public SpriteRenderer Block(string name, int orderOffset, Color32 c)
        {
            var sr = NewSprite(name, orderOffset, ArtLib.WhitePixel);
            sr.color = c;
            return sr;
        }

        public void PutBlock(SpriteRenderer sr, int lx, int ly, int w, int h)
        {
            sr.transform.localScale = new Vector3(w, h, 1f);
            Put(sr, lx, ly + h);
        }

        /// <summary>Places a renderer's pivot at scene-local (lx, ly).</summary>
        public void Put(SpriteRenderer sr, int lx, int ly) { PawGfx.Place(sr, X + lx, Y + ly); }

        public void PutText(BitmapText t, int lx, int ly) { t.At(X + lx, Y + ly); }

        /// <summary>Loud outlined comic word ("PAT!", "JAM", "BANG").</summary>
        public BitmapText Word(string name, string text, Color32 fill)
        {
            var t = BitmapText.Create(name, Root, Loud, Order + 10, TextAlign.Center);
            t.SetColors(fill, Pal.Ink, Pal.Ink2);
            t.SetText(text);
            t.Visible = false;
            return t;
        }

        /// <summary>Small plain line ("comfy.", "heh.").</summary>
        public BitmapText Note(string name, string text, Color32 fill, TextAlign a)
        {
            var t = BitmapText.Create(name, Root, Kit.Small, Order + 10, a);
            t.SetColors(fill, Pal.Ink, Pal.Ink2);
            t.SetText(text);
            t.Visible = false;
            return t;
        }

        /// <summary>White sprite flash (SpriteFx _Flash) on or off.</summary>
        public void Flash(SpriteRenderer sr, bool on)
        {
            sr.GetPropertyBlock(mpb);
            mpb.SetFloat(PawGfx.FlashId, on ? 1f : 0f);
            sr.SetPropertyBlock(mpb);
        }

        /// <summary>Crop of a Resources texture (rect in px from the top-left), pivot top-left. Null when the art is missing.</summary>
        public static Sprite Crop(string path, int x, int y, int w, int h)
        {
            var tex = ArtLib.TryTexture(path);
            if (tex == null) return null;
            return ArtLib.MakeSprite(tex, new RectInt(x, y, w, h), Vector2Int.zero, Vector4.zero);
        }

        /// <summary>A sheet frame index for a one-shot that started at <paramref name="start"/>; -1 when not playing.</summary>
        public static int OneShot(SheetDef d, float t, float start)
        {
            if (d == null || d.Count == 0 || t < start) return -1;
            float fps = d.Fps > 0f ? d.Fps : 12f;
            int f = (int)((t - start) * fps);
            return f < d.Count ? f : -1;
        }

        /// <summary>Shows frame f of d on sr (pivot at scene-local lx, ly), or hides it when f &lt; 0.</summary>
        public void Frame(SpriteRenderer sr, SheetDef d, int f, int lx, int ly)
        {
            bool on = f >= 0 && d != null && f < d.Count;
            sr.enabled = on;
            if (!on) return;
            sr.sprite = d.Frames[f];
            Put(sr, lx, ly);
        }

        /// <summary>A peek crop (top <paramref name="rows"/> of a portrait) placed so it rises from the scene floor.</summary>
        public static Sprite[] Peeks(string path, int size)
        {
            var arr = new Sprite[3];
            for (int i = 0; i < 3; i++) arr[i] = Crop(path, 0, 0, size, size * (i + 1) / 3);
            return arr;
        }

        // ------------------------------------------------------------------ procedural backdrops (built once)

        public enum Backdrop { Halftone, Hatch, Sunburst }

        /// <summary>A W x H backdrop texture in palette colours (pivot top-left).</summary>
        public static Sprite MakeBackdrop(Backdrop kind, int burstX, int burstY)
        {
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawHowTo" + kind;
            var px = new Color32[W * H];
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    Color32 c;
                    if (kind == Backdrop.Halftone)
                    {
                        // warm close-up: sand, a halftone that thickens toward the floor
                        int step = y > H * 2 / 3 ? 4 : (y > H / 3 ? 6 : 0);
                        bool dot = step > 0 && y % step == 0 && (x + (y / step) * (step / 2)) % step == 0;
                        c = dot ? Pal.Brass0 : Pal.Sand0;
                        if (y >= H - 3) c = Pal.Sand3;
                    }
                    else if (kind == Backdrop.Hatch)
                    {
                        // tense night: ink with a fine purple diagonal hatch
                        c = (x + y) % 6 == 0 ? Pal.ShadowPurp : Pal.Ink2;
                    }
                    else
                    {
                        // action sunburst around the opponent's face, 28 alternating rays
                        float a = Mathf.Atan2(y - burstY, x - burstX);
                        int ray = (int)((a + Mathf.PI) / (2f * Mathf.PI) * 28f) & 1;
                        c = ray == 0 ? Pal.Sun1 : Pal.Sand0;
                    }
                    px[(H - 1 - y) * W + x] = c;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return UnityEngine.Sprite.Create(tex, new Rect(0, 0, W, H), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
        }

        /// <summary>Scene-local y offset of a stepped keyframe list (one entry per stepSecs), clamped to the last.</summary>
        public static int Steps(int[] keys, float t, float stepSecs)
        {
            if (t < 0f) return keys[0];
            int i = (int)(t / stepSecs);
            return keys[i < keys.Length ? i : keys.Length - 1];
        }

        public static bool Same(Color32 a, Color32 b) { return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a; }

        /// <summary>True on the frame time crosses <paramref name="at"/> (prev &lt; at &lt;= t).</summary>
        public static bool Crossed(float prev, float t, float at) { return prev < at && t >= at; }
    }
}
