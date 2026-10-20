using System.Collections.Generic;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// A CPU copy of a texture's pixels (rows bottom-up, like Texture2D.GetPixels32). The game's art is imported
    /// non-readable (PawImportRules), so the copy is made once per texture through a temporary RenderTexture
    /// (Graphics.Blit + ReadPixels: a texture copy, not a camera capture) and cached. Main thread only; meant
    /// for one-off work such as the share card, never per frame.
    /// </summary>
    public sealed class PixelImage
    {
        public readonly int Width, Height;
        public readonly Color32[] Pixels;

        public PixelImage(int w, int h, Color32[] px)
        {
            Width = w;
            Height = h;
            Pixels = px;
        }

        private static readonly Dictionary<Texture2D, PixelImage> cache = new Dictionary<Texture2D, PixelImage>(16);

        /// <summary>Readable pixels of <paramref name="tex"/> (null when it cannot be read).</summary>
        public static PixelImage Of(Texture2D tex)
        {
            if (tex == null) return null;
            PixelImage img;
            if (cache.TryGetValue(tex, out img)) return img;
            img = Read(tex);
            if (img != null) cache[tex] = img;
            return img;
        }

        /// <summary>Drops the cached copies (call after a share so the memory goes back).</summary>
        public static void ClearCache() { cache.Clear(); }

        private static PixelImage Read(Texture2D tex)
        {
            int w = tex.width, h = tex.height;
            try
            {
                if (tex.isReadable) return new PixelImage(w, h, tex.GetPixels32());
            }
            catch (System.Exception) { }
            RenderTexture rt = null;
            var prev = RenderTexture.active;
            try
            {
                // gamma colour space (PawProjectSetup): no sRGB conversion on the way in or out
                rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
                rt.filterMode = FilterMode.Point;
                Graphics.Blit(tex, rt);
                RenderTexture.active = rt;
                var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
                copy.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
                copy.Apply(false, false);
                var px = copy.GetPixels32();
                if (Application.isPlaying) Object.Destroy(copy);
                else Object.DestroyImmediate(copy);
                return new PixelImage(w, h, px);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[PawNoon] could not read texture " + tex.name + ": " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = prev;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
            }
        }
    }

    /// <summary>
    /// A small software canvas for pixel compositions that are saved as images (the share card): blits sprites
    /// and sprite-sheet cells, fills rects, draws BitmapFont text (with the same 1 px outline as BitmapText), all
    /// on whole pixels. Coordinates are canvas px with y down from the top-left; storage is bottom-up rows so
    /// the buffer drops straight into Texture2D.SetPixels32.
    /// </summary>
    public sealed class PixelCanvas
    {
        public readonly int Width, Height;
        public readonly Color32[] Pixels;

        public PixelCanvas(int w, int h)
        {
            Width = w;
            Height = h;
            Pixels = new Color32[w * h];
        }

        private int Index(int x, int y) { return (Height - 1 - y) * Width + x; }

        public void Set(int x, int y, Color32 c)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return;
            Pixels[Index(x, y)] = c;
        }

        public Color32 Get(int x, int y)
        {
            if (x < 0 || x >= Width || y < 0 || y >= Height) return default(Color32);
            return Pixels[Index(x, y)];
        }

        /// <summary>Source-over with the source alpha (pixel art is mostly 0 / 255; partial alpha still blends).</summary>
        public void Blend(int x, int y, Color32 c)
        {
            if (c.a == 0 || x < 0 || x >= Width || y < 0 || y >= Height) return;
            int i = Index(x, y);
            if (c.a == 255) { Pixels[i] = c; return; }
            var d = Pixels[i];
            int a = c.a, ia = 255 - a;
            Pixels[i] = new Color32((byte)((c.r * a + d.r * ia) / 255), (byte)((c.g * a + d.g * ia) / 255),
                (byte)((c.b * a + d.b * ia) / 255), (byte)System.Math.Min(255, a + d.a * ia / 255));
        }

        public void Fill(Color32 c)
        {
            for (int i = 0; i < Pixels.Length; i++) Pixels[i] = c;
        }

        public void FillRect(RectInt r, Color32 c)
        {
            for (int y = r.yMin; y < r.yMax; y++)
                for (int x = r.xMin; x < r.xMax; x++) Set(x, y, c);
        }

        /// <summary>
        /// Darkens rows [y0, y1) toward <paramref name="ink"/>: keep = fraction of the original colour that stays.
        /// The first and last <paramref name="edgeRows"/> rows use a checkerboard of keep and (1 + keep) / 2, so the
        /// band has a dithered pixel edge instead of a hard line or a smooth ramp.
        /// </summary>
        public void Darken(int y0, int y1, float keep, Color32 ink, int edgeRows)
        {
            int k = Mathf.RoundToInt(Mathf.Clamp01(keep) * 256f);
            int kEdge = Mathf.RoundToInt(Mathf.Clamp01((1f + keep) * 0.5f) * 256f);
            for (int y = Mathf.Max(0, y0); y < Mathf.Min(Height, y1); y++)
            {
                bool edge = y < y0 + edgeRows || y >= y1 - edgeRows;
                for (int x = 0; x < Width; x++)
                {
                    int kk = edge && ((x + y) & 1) == 0 ? kEdge : k;
                    int i = Index(x, y);
                    var c = Pixels[i];
                    Pixels[i] = new Color32((byte)((c.r * kk + ink.r * (256 - kk)) >> 8), (byte)((c.g * kk + ink.g * (256 - kk)) >> 8),
                        (byte)((c.b * kk + ink.b * (256 - kk)) >> 8), 255);
                }
            }
        }

        /// <summary>
        /// Copies a texture region (texture px, x/y from the TOP-left of the texture) to the canvas at (dx, dy),
        /// clipped to <paramref name="maxH"/> rows when it is &gt;= 0.
        /// </summary>
        public void BlitRegion(PixelImage src, int sx, int syTop, int w, int h, int dx, int dy, int maxH = -1)
        {
            if (src == null) return;
            if (maxH >= 0 && h > maxH) h = maxH;
            for (int y = 0; y < h; y++)
            {
                int ty = src.Height - 1 - (syTop + y);
                if (ty < 0 || ty >= src.Height) continue;
                int row = ty * src.Width;
                for (int x = 0; x < w; x++)
                {
                    int tx = sx + x;
                    if (tx < 0 || tx >= src.Width) continue;
                    Blend(dx + x, dy + y, src.Pixels[row + tx]);
                }
            }
        }

        /// <summary>Draws a sprite with its top-left at (dx, dy).</summary>
        public void BlitSprite(Sprite s, int dx, int dy)
        {
            if (s == null) return;
            var img = PixelImage.Of(s.texture);
            if (img == null) return;
            var r = s.rect;   // texture px, origin bottom-left
            int w = Mathf.RoundToInt(r.width), h = Mathf.RoundToInt(r.height);
            int syTop = img.Height - Mathf.RoundToInt(r.y) - h;
            BlitRegion(img, Mathf.RoundToInt(r.x), syTop, w, h, dx, dy);
        }

        /// <summary>A 9-slice panel (borders copied 1:1, edges and centre tiled, never stretched).</summary>
        public void BlitSliced(Sprite s, RectInt dst, int border)
        {
            if (s == null) return;
            var img = PixelImage.Of(s.texture);
            if (img == null) return;
            var r = s.rect;
            int sw = Mathf.RoundToInt(r.width), sh = Mathf.RoundToInt(r.height);
            int sx = Mathf.RoundToInt(r.x), sy = img.Height - Mathf.RoundToInt(r.y) - sh;
            int b = Mathf.Clamp(border, 0, Mathf.Min(sw, sh) / 2);
            int[] sxs = { 0, b, sw - b, sw }, dxs = { dst.x, dst.x + b, dst.xMax - b, dst.xMax };
            int[] sys = { 0, b, sh - b, sh }, dys = { dst.y, dst.y + b, dst.yMax - b, dst.yMax };
            for (int j = 0; j < 3; j++)
                for (int i = 0; i < 3; i++)
                {
                    int pw = sxs[i + 1] - sxs[i], ph = sys[j + 1] - sys[j];
                    if (pw <= 0 || ph <= 0) continue;
                    for (int ty = dys[j]; ty < dys[j + 1]; ty += ph)
                        for (int tx = dxs[i]; tx < dxs[i + 1]; tx += pw)
                            BlitRegion(img, sx + sxs[i], sy + sys[j], Mathf.Min(pw, dxs[i + 1] - tx), Mathf.Min(ph, dys[j + 1] - ty), tx, ty);
                }
        }

        // ------------------------------------------------------------------ text

        /// <summary>Width of a string in px, as BitmapText measures it (trailing spacing column dropped).</summary>
        public static int TextWidth(BitmapFont f, string s, bool mono)
        {
            if (f == null || string.IsNullOrEmpty(s)) return 0;
            int pen = 0;
            for (int i = 0; i < s.Length; i++)
            {
                Glyph g;
                f.TryGet(s[i], out g);
                pen += mono && f.DigitAdv > 0 && s[i] >= '0' && s[i] <= '9' ? f.DigitAdv : g.adv;
            }
            return Mathf.Max(0, pen - 1);
        }

        /// <summary>
        /// Draws text with its alignment point at x and the top of the line at y (BitmapText semantics): outline
        /// (4-neighbour, 1 px) first when <paramref name="outline"/> has alpha, then the fill. Returns the width.
        /// </summary>
        public int Text(BitmapFont f, string s, int x, int y, Color32 fill, Color32 outline, TextAlign align, bool mono = false)
        {
            if (f == null || string.IsNullOrEmpty(s)) return 0;
            int w = TextWidth(f, s, mono);
            int startX = align == TextAlign.Left ? x : (align == TextAlign.Center ? x - w / 2 : x - w);
            var img = f.Fallback ? null : PixelImage.Of(f.Texture);
            if (outline.a > 0)
            {
                Glyphs(f, img, s, startX - 1, y, outline, mono);
                Glyphs(f, img, s, startX + 1, y, outline, mono);
                Glyphs(f, img, s, startX, y - 1, outline, mono);
                Glyphs(f, img, s, startX, y + 1, outline, mono);
            }
            Glyphs(f, img, s, startX, y, fill, mono);
            return w;
        }

        private void Glyphs(BitmapFont f, PixelImage img, string s, int x, int y, Color32 col, bool mono)
        {
            int pen = x;
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                Glyph g;
                f.TryGet(ch, out g);
                int adv = g.adv, shift = 0;
                if (mono && f.DigitAdv > 0 && ch >= '0' && ch <= '9') { shift = (f.DigitAdv - g.adv) / 2; adv = f.DigitAdv; }
                if (g.w > 0 && g.h > 0)
                {
                    int gx = pen + shift + g.ox, gy = y + g.oy;
                    for (int yy = 0; yy < g.h; yy++)
                    {
                        for (int xx = 0; xx < g.w; xx++)
                        {
                            bool on;
                            if (img == null) on = true;   // missing font: box glyphs, as BitmapText draws them
                            else
                            {
                                int ty = img.Height - 1 - (g.y + yy);
                                int tx = g.x + xx;
                                on = ty >= 0 && ty < img.Height && tx >= 0 && tx < img.Width && img.Pixels[ty * img.Width + tx].a > 127;
                            }
                            if (on) Set(gx + xx, gy + yy, col);
                        }
                    }
                }
                pen += adv;
            }
        }

        // ------------------------------------------------------------------ output

        /// <summary>Nearest-neighbour upscale by an integer factor (pure pixel; no filtering).</summary>
        public Color32[] Upscaled(int scale)
        {
            if (scale <= 1) return (Color32[])Pixels.Clone();
            int w = Width * scale, h = Height * scale;
            var dst = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int srcRow = (y / scale) * Width;
                int dstRow = y * w;
                for (int x = 0; x < w; x++) dst[dstRow + x] = Pixels[srcRow + x / scale];
            }
            return dst;
        }
    }
}
