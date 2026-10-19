using System.Collections.Generic;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    [System.Serializable]
    public struct Glyph
    {
        public int x, y, w, h, ox, oy, adv;
    }

    /// <summary>Pixel font from &lt;font&gt;.png + &lt;font&gt;.json (SPEC §4.3). Missing font -> box glyphs + one warning.</summary>
    [System.Serializable]
    public sealed class BitmapFont
    {
        public string Name;
        [System.NonSerialized] public Texture2D Texture;
        [System.NonSerialized] public Material Material;
        public int LineHeight = 8;
        public int Baseline = 6;
        public int DigitAdv;
        public bool Fallback;
        [System.NonSerialized] private readonly Dictionary<char, Glyph> glyphs = new Dictionary<char, Glyph>(128);
        [System.NonSerialized] private Glyph fallbackGlyph;

        public static BitmapFont Load(string name, int fallbackLine)
        {
            var f = new BitmapFont();
            f.Name = name;
            string path = "Art/Fonts/" + name;
            var root = ArtLib.Json(path);
            var tex = ArtLib.TryTexture(path);
            if (root == null || tex == null)
            {
                ArtLib.Warn("font:" + name, "Missing font " + path + " -> box glyphs");
                f.Fallback = true;
                f.Texture = ArtLib.WhiteTexture;
                f.LineHeight = fallbackLine;
                f.Baseline = fallbackLine - 2;
                int w = Mathf.Max(3, fallbackLine / 2);
                var g = new Glyph { x = 0, y = 0, w = w, h = fallbackLine - 2, ox = 0, oy = 0, adv = w + 1 };
                f.fallbackGlyph = g;
                f.DigitAdv = g.adv;
            }
            else
            {
                f.Texture = tex;
                f.LineHeight = PnJson.Int(root, "lineHeight", fallbackLine);
                f.Baseline = PnJson.Int(root, "baseline", f.LineHeight - 2);
                var gl = PnJson.Obj(root, "glyphs");
                int maxDigit = 0;
                if (gl != null)
                {
                    foreach (var kv in gl)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || kv.Key.Length != 1) continue;
                        var g = new Glyph();
                        g.x = PnJson.Int(kv.Value, "x", 0);
                        g.y = PnJson.Int(kv.Value, "y", 0);
                        g.w = PnJson.Int(kv.Value, "w", 0);
                        g.h = PnJson.Int(kv.Value, "h", 0);
                        g.ox = PnJson.Int(kv.Value, "ox", 0);
                        g.oy = PnJson.Int(kv.Value, "oy", 0);
                        g.adv = PnJson.Int(kv.Value, "adv", g.w + 1);
                        char c = kv.Key[0];
                        f.glyphs[c] = g;
                        if (c >= '0' && c <= '9' && g.adv > maxDigit) maxDigit = g.adv;
                    }
                }
                f.DigitAdv = PnJson.Int(root, "digitAdv", maxDigit);
                f.fallbackGlyph = new Glyph { x = 0, y = 0, w = 0, h = 0, adv = Mathf.Max(2, f.LineHeight / 2) };
            }
            f.Material = PawGfx.NewMaterial(f.Texture);
            f.Material.name = "PawFont_" + name;
            return f;
        }

        public bool TryGet(char c, out Glyph g)
        {
            if (Fallback)
            {
                g = fallbackGlyph;
                if (c == ' ') { g.w = 0; g.h = 0; }
                return true;
            }
            if (glyphs.TryGetValue(c, out g)) return true;
            // case fallback (fonts may ship only one case)
            char alt = char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c);
            if (alt != c && glyphs.TryGetValue(alt, out g)) return true;
            g = fallbackGlyph;
            return false;
        }
    }

    public enum TextAlign { Left, Center, Right }

    /// <summary>
    /// Text drawn as ONE mesh of glyph quads (shadow + 1px outline + fill layers via extra quads).
    /// The transform sits at the alignment point of the top of the line, in whole pixels.
    /// </summary>
    public sealed class BitmapText : MonoBehaviour
    {
        [System.NonSerialized] public BitmapFont Font;
        public Color32 Color = Pal.Cream0;
        public Color32 OutlineColor = Pal.Ink;
        public Color32 ShadowColor = Pal.Ink2;
        public bool Outline = true;
        public bool Shadow = true;
        public bool MonoDigits;
        public TextAlign Align = TextAlign.Left;

        private Mesh mesh;
        private MeshRenderer mr;
        private readonly char[] buf = new char[96];
        private int len;
        private bool dirty = true;
        private int width;
        private readonly List<Vector3> verts = new List<Vector3>(256);
        private readonly List<Vector2> uvs = new List<Vector2>(256);
        private readonly List<Color32> cols = new List<Color32>(256);
        private readonly List<int> tris = new List<int>(384);

        private static readonly Vector2Int[] OutlineOffs = { new Vector2Int(-1, 0), new Vector2Int(1, 0), new Vector2Int(0, -1), new Vector2Int(0, 1) };
        private static readonly Vector2Int[] ShadowOffsOutlined = { new Vector2Int(-1, 2), new Vector2Int(0, 2), new Vector2Int(1, 2) };
        private static readonly Vector2Int[] ShadowOffsPlain = { new Vector2Int(0, 1) };

        public static BitmapText Create(string name, Transform parent, BitmapFont font, int order, TextAlign align)
        {
            var go = new GameObject(name);
            go.layer = 0;
            if (parent != null) go.transform.SetParent(parent, false);
            var t = go.AddComponent<BitmapText>();
            t.Font = font;
            t.Align = align;
            var mf = go.AddComponent<MeshFilter>();
            t.mr = go.AddComponent<MeshRenderer>();
            t.mr.sharedMaterial = font.Material;
            t.mr.sortingOrder = order;
            t.mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            t.mr.receiveShadows = false;
            t.mesh = new Mesh();
            t.mesh.name = "BitmapText";
            t.mesh.MarkDynamic();
            mf.sharedMesh = t.mesh;
            return t;
        }

        public int SortingOrder
        {
            get { return mr.sortingOrder; }
            set { mr.sortingOrder = value; }
        }

        public int Width { get { if (dirty) Rebuild(); return width; } }
        public int Height { get { return Font.LineHeight; } }

        public void SetText(string s)
        {
            if (s == null) s = "";
            int n = Mathf.Min(s.Length, buf.Length);
            if (n == len)
            {
                bool same = true;
                for (int i = 0; i < n; i++) if (buf[i] != s[i]) { same = false; break; }
                if (same) return;
            }
            for (int i = 0; i < n; i++) buf[i] = s[i];
            len = n;
            dirty = true;
        }

        /// <summary>Allocation-free update (timer).</summary>
        public void SetChars(char[] src, int count)
        {
            int n = Mathf.Min(count, buf.Length);
            if (n == len)
            {
                bool same = true;
                for (int i = 0; i < n; i++) if (buf[i] != src[i]) { same = false; break; }
                if (same) return;
            }
            for (int i = 0; i < n; i++) buf[i] = src[i];
            len = n;
            dirty = true;
        }

        public void SetColors(Color32 fill, Color32 outline, Color32 shadow)
        {
            Color = fill;
            OutlineColor = outline;
            ShadowColor = shadow;
            dirty = true;
        }

        public void MarkDirty() { dirty = true; }

        /// <summary>Places the text at a canvas point (alignment point, top of line).</summary>
        public void At(int canvasX, int canvasY)
        {
            transform.position = Px.World(canvasX, canvasY);
        }

        public bool Visible
        {
            get { return mr.enabled; }
            set { mr.enabled = value; }
        }

        private int Advance(char c, ref Glyph g, out int shift)
        {
            shift = 0;
            if (MonoDigits && Font.DigitAdv > 0 && c >= '0' && c <= '9')
            {
                shift = (Font.DigitAdv - g.adv) / 2;
                return Font.DigitAdv;
            }
            return g.adv;
        }

        private int Measure()
        {
            int pen = 0;
            for (int i = 0; i < len; i++)
            {
                Glyph g;
                Font.TryGet(buf[i], out g);
                int sh;
                pen += Advance(buf[i], ref g, out sh);
            }
            // drop trailing spacing column
            return Mathf.Max(0, pen - 1);
        }

        private void LateUpdate()
        {
            if (dirty) Rebuild();
        }

        private void Rebuild()
        {
            dirty = false;
            width = Measure();
            int startX = Align == TextAlign.Left ? 0 : (Align == TextAlign.Center ? -(width / 2) : -width);
            verts.Clear();
            uvs.Clear();
            cols.Clear();
            tris.Clear();

            if (Shadow)
            {
                var offs = Outline ? ShadowOffsOutlined : ShadowOffsPlain;
                for (int k = 0; k < offs.Length; k++) Emit(startX, offs[k], ShadowColor);
            }
            if (Outline)
                for (int k = 0; k < OutlineOffs.Length; k++) Emit(startX, OutlineOffs[k], OutlineColor);
            Emit(startX, Vector2Int.zero, Color);

            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateBounds();
        }

        /// <summary>Emits one layer. off = (dx, dy) in canvas px (y down).</summary>
        private void Emit(int startX, Vector2Int off, Color32 col)
        {
            var tex = Font.Texture;
            float iw = 1f / tex.width, ih = 1f / tex.height;
            int pen = startX;
            for (int i = 0; i < len; i++)
            {
                char c = buf[i];
                Glyph g;
                Font.TryGet(c, out g);
                int shift;
                int adv = Advance(c, ref g, out shift);
                if (g.w > 0 && g.h > 0)
                {
                    float x0 = pen + shift + g.ox + off.x;
                    float yTop = -(g.oy + off.y);
                    float x1 = x0 + g.w;
                    float y0 = yTop - g.h;
                    float u0, v0, u1, v1;
                    if (Font.Fallback)
                    {
                        u0 = 0f; v0 = 0f; u1 = 1f; v1 = 1f;
                    }
                    else
                    {
                        u0 = g.x * iw;
                        u1 = (g.x + g.w) * iw;
                        v1 = 1f - g.y * ih;
                        v0 = 1f - (g.y + g.h) * ih;
                    }
                    int b = verts.Count;
                    verts.Add(new Vector3(x0, y0, 0f));
                    verts.Add(new Vector3(x0, yTop, 0f));
                    verts.Add(new Vector3(x1, yTop, 0f));
                    verts.Add(new Vector3(x1, y0, 0f));
                    uvs.Add(new Vector2(u0, v0));
                    uvs.Add(new Vector2(u0, v1));
                    uvs.Add(new Vector2(u1, v1));
                    uvs.Add(new Vector2(u1, v0));
                    cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
                    tris.Add(b); tris.Add(b + 1); tris.Add(b + 2);
                    tris.Add(b); tris.Add(b + 2); tris.Add(b + 3);
                }
                pen += adv;
            }
        }

        private void OnDestroy()
        {
            if (mesh != null) Destroy(mesh);
        }
    }

    /// <summary>Allocation-free number formatting into char buffers (for per-frame text).</summary>
    public static class NumFmt
    {
        /// <summary>Writes seconds with 6 decimals from ns ("0.214382"), returns length. Truncates (never rounds up).</summary>
        public static int Seconds6(long ns, char[] dst, int at)
        {
            if (ns < 0) ns = 0;
            long us = ns / 1000; // microseconds
            long whole = us / 1000000;
            long frac = us % 1000000;
            int p = at;
            p = WriteInt(whole, dst, p);
            dst[p++] = '.';
            long div = 100000;
            for (int i = 0; i < 6; i++)
            {
                dst[p++] = (char)('0' + (frac / div) % 10);
                div /= 10;
            }
            return p - at;
        }

        public static int WriteInt(long v, char[] dst, int at)
        {
            int p = at;
            if (v < 0) { dst[p++] = '-'; v = -v; }
            if (v == 0) { dst[p++] = '0'; return p; }
            int start = p;
            while (v > 0) { dst[p++] = (char)('0' + v % 10); v /= 10; }
            // reverse
            for (int i = start, j = p - 1; i < j; i++, j--) { char t = dst[i]; dst[i] = dst[j]; dst[j] = t; }
            return p;
        }

        public static string Seconds6(long ns)
        {
            var b = new char[24];
            int n = Seconds6(ns, b, 0);
            return new string(b, 0, n);
        }

        /// <summary>"73.72" style ms with 2 decimals.</summary>
        public static string Ms2(long ns)
        {
            if (ns < 0) ns = -ns;
            long hundredths = (ns + 5000) / 10000; // round to 0.01 ms
            long whole = hundredths / 100;
            long frac = hundredths % 100;
            return whole.ToString(System.Globalization.CultureInfo.InvariantCulture) + "." + (frac < 10 ? "0" : "") + frac.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
