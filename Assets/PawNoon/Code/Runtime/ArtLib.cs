using System.Collections.Generic;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>An animation strip (or single sprite) with its metadata, pivot in cell px (top-left origin).</summary>
    [System.Serializable]
    public sealed class SheetDef
    {
        public string Name;
        public Sprite[] Frames;
        public float Fps = 10f;
        public bool Loop = true;
        public Vector2Int Cell;
        public Vector2Int Pivot;
        /// <summary>Frame index of the "fire" event, or -1.</summary>
        public int FireFrame = -1;
        /// <summary>Per-frame muzzle in cell px, or null. (-1,-1) = no gun visible.</summary>
        public Vector2Int[] Muzzle;
        /// <summary>Optional inner rect (frames/bars) in cell px.</summary>
        public RectInt Inner;
        public bool HasInner;
        public Vector2Int ContentOffset;
        public bool Missing;

        public int Count { get { return Frames == null ? 0 : Frames.Length; } }
        public Sprite First { get { return Count > 0 ? Frames[0] : null; } }

        public float Duration { get { return Fps > 0f ? Count / Fps : 0f; } }

        public bool TryMuzzle(int frame, out Vector2Int m)
        {
            m = new Vector2Int(-1, -1);
            if (Muzzle == null || Muzzle.Length == 0) return false;
            if (frame < 0) frame = 0;
            if (frame >= Muzzle.Length) frame = Muzzle.Length - 1;
            m = Muzzle[frame];
            return m.x >= 0 && m.y >= 0;
        }
    }

    /// <summary>
    /// Resources loader for textures, strips and JSON manifests. Missing files give a magenta
    /// placeholder and exactly one warning per path; nothing here ever throws.
    /// </summary>
    public static class ArtLib
    {
        private static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        private static readonly Dictionary<string, object> jsons = new Dictionary<string, object>();
        private static readonly Dictionary<string, SheetDef> sheets = new Dictionary<string, SheetDef>();
        private static readonly HashSet<string> warned = new HashSet<string>();
        private static readonly HashSet<string> missingTex = new HashSet<string>();
        private static Texture2D white;
        private static Sprite whiteSprite;

        public static void Warn(string key, string message)
        {
            if (warned.Add(key)) Debug.LogWarning("[PawNoon] " + message);
        }

        public static void ClearCaches()
        {
            textures.Clear();
            missingTex.Clear();
            jsons.Clear();
            sheets.Clear();
        }

        public static Texture2D WhiteTexture
        {
            get
            {
                if (white == null)
                {
                    white = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    white.SetPixel(0, 0, Color.white);
                    white.Apply(false, false);
                    white.filterMode = FilterMode.Point;
                    white.wrapMode = TextureWrapMode.Clamp;
                    white.name = "PawWhite";
                }
                return white;
            }
        }

        /// <summary>1x1 white sprite, pivot bottom-left. Tint with SpriteRenderer.color; size with transform scale (FX only).</summary>
        public static Sprite WhitePixel
        {
            get
            {
                if (whiteSprite == null)
                    whiteSprite = Sprite.Create(WhiteTexture, new Rect(0, 0, 1, 1), new Vector2(0f, 0f), 1f, 0, SpriteMeshType.FullRect);
                return whiteSprite;
            }
        }

        public static Texture2D Placeholder(int w, int h)
        {
            w = Mathf.Clamp(w, 1, 1024);
            h = Mathf.Clamp(h, 1, 1024);
            var t = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            var mag = new Color32(255, 0, 255, 255);
            var dark = new Color32(120, 0, 120, 255);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[y * w + x] = (x == 0 || y == 0 || x == w - 1 || y == h - 1 || ((x / 4 + y / 4) & 1) == 0) ? mag : dark;
            t.SetPixels32(px);
            t.Apply(false, false);
            t.filterMode = FilterMode.Point;
            t.wrapMode = TextureWrapMode.Clamp;
            t.name = "PawPlaceholder";
            return t;
        }

        /// <summary>Loads a texture; null (no warning) if missing.</summary>
        public static Texture2D TryTexture(string path)
        {
            Texture2D t;
            if (textures.TryGetValue(path, out t)) return t;
            if (missingTex.Contains(path)) return null; // do not hit Resources again for a known-missing file
            t = Resources.Load<Texture2D>(path);
            if (t != null)
            {
                t.filterMode = FilterMode.Point;
                t.wrapMode = TextureWrapMode.Clamp;
                textures[path] = t;
            }
            else missingTex.Add(path);
            return t;
        }

        /// <summary>Loads a texture or returns a magenta placeholder of the given size (one warning).</summary>
        public static Texture2D Texture(string path, int placeholderW, int placeholderH)
        {
            var t = TryTexture(path);
            if (t != null) return t;
            Warn("tex:" + path, "Missing texture Resources/" + path + ".png -> magenta placeholder");
            t = Placeholder(placeholderW, placeholderH);
            textures[path] = t;
            return t;
        }

        public static object Json(string path, bool warnIfMissing = true)
        {
            object o;
            if (jsons.TryGetValue(path, out o)) return o;
            var ta = Resources.Load<TextAsset>(path);
            if (ta == null)
            {
                if (warnIfMissing) Warn("json:" + path, "Missing manifest Resources/" + path + ".json -> defaults");
                jsons[path] = null;
                return null;
            }
            o = PnJson.Parse(ta.text);
            if (o == null) Warn("jsonbad:" + path, "Could not parse Resources/" + path + ".json -> defaults");
            jsons[path] = o;
            return o;
        }

        /// <summary>Sprite with pivot given in px from the cell's top-left. border = (l, t, r, b) in px.</summary>
        public static Sprite MakeSprite(Texture2D tex, RectInt cellRectTopLeft, Vector2Int pivotPx, Vector4 borderLTRB)
        {
            float w = Mathf.Max(1, cellRectTopLeft.width);
            float h = Mathf.Max(1, cellRectTopLeft.height);
            // texture rect in Unity space (bottom-left origin)
            var rect = new Rect(cellRectTopLeft.x, tex.height - cellRectTopLeft.y - h, w, h);
            if (rect.x < 0) rect.x = 0;
            if (rect.y < 0) rect.y = 0;
            if (rect.xMax > tex.width) rect.width = tex.width - rect.x;
            if (rect.yMax > tex.height) rect.height = tex.height - rect.y;
            var pivot = new Vector2(pivotPx.x / rect.width, 1f - pivotPx.y / rect.height);
            // Unity border order: left, bottom, right, top
            var border = new Vector4(borderLTRB.x, borderLTRB.w, borderLTRB.z, borderLTRB.y);
            var s = Sprite.Create(tex, rect, pivot, 1f, 0, SpriteMeshType.FullRect, border);
            s.name = tex.name;
            return s;
        }

        /// <summary>Horizontal strip of equal cells (frames clamp to what the texture holds).</summary>
        public static Sprite[] Strip(Texture2D tex, Vector2Int cell, int frames, Vector2Int pivot, Vector4 borderLTRB)
        {
            if (cell.x <= 0) cell.x = tex.width;
            if (cell.y <= 0) cell.y = tex.height;
            int fit = Mathf.Max(1, tex.width / Mathf.Max(1, cell.x));
            if (frames <= 0 || frames > fit) frames = fit;
            var arr = new Sprite[frames];
            for (int i = 0; i < frames; i++)
                arr[i] = MakeSprite(tex, new RectInt(i * cell.x, 0, cell.x, cell.y), pivot, borderLTRB);
            return arr;
        }

        /// <summary>
        /// Builds a SheetDef from a manifest entry (schema of SPEC §4: file, cell, frames, fps, loop, pivot,
        /// events.fire, muzzle, border [l,t,r,b], inner [x,y,w,h], size, contentOffset).
        /// </summary>
        public static SheetDef Sheet(string folder, string name, object entry, Vector2Int defaultCell, Vector2Int defaultPivot, bool centerPivotByDefault)
        {
            string file = PnJson.Str(entry, "file", name);
            string path = folder + file;
            string key = path + "|" + name;
            SheetDef cached;
            if (sheets.TryGetValue(key, out cached)) return cached;

            var d = new SheetDef();
            d.Name = name;
            int frames = PnJson.Int(entry, "frames", 1);
            d.Fps = (float)PnJson.Num(entry, "fps", 10.0);
            d.Loop = PnJson.Bool(entry, "loop", true);

            Vector2Int cell = defaultCell;
            int sw = cell.x, sh = cell.y;
            bool hasCell = PnJson.Pair(entry, "cell", ref sw, ref sh);
            int zw = 0, zh = 0;
            bool hasSize = PnJson.Pair(entry, "size", ref zw, ref zh);
            if (hasCell) cell = new Vector2Int(sw, sh);
            else if (hasSize) cell = new Vector2Int(frames > 1 ? zw / frames : zw, zh);

            var tex = TryTexture(path);
            if (tex == null)
            {
                int pw = cell.x > 0 ? cell.x : 16, ph = cell.y > 0 ? cell.y : 16;
                Warn("tex:" + path, "Missing texture Resources/" + path + ".png -> magenta placeholder");
                tex = Placeholder(pw * Mathf.Max(1, frames), ph);
                textures[path] = tex;
                d.Missing = true;
            }
            if (cell.x <= 0 || cell.y <= 0)
                cell = new Vector2Int(frames > 1 ? tex.width / frames : tex.width, tex.height);
            d.Cell = cell;

            int px = centerPivotByDefault ? cell.x / 2 : defaultPivot.x;
            int py = centerPivotByDefault ? cell.y / 2 : defaultPivot.y;
            PnJson.Pair(entry, "pivot", ref px, ref py);
            d.Pivot = new Vector2Int(px, py);

            Vector4 border = Vector4.zero;
            var b = PnJson.NumArray(entry, "border");
            if (b != null && b.Length >= 4) border = new Vector4((float)b[0], (float)b[1], (float)b[2], (float)b[3]);

            var inner = PnJson.NumArray(entry, "inner");
            if (inner != null && inner.Length >= 4)
            {
                d.HasInner = true;
                d.Inner = new RectInt((int)inner[0], (int)inner[1], (int)inner[2], (int)inner[3]);
            }
            int cox = 0, coy = 0;
            if (PnJson.Pair(entry, "contentOffset", ref cox, ref coy)) d.ContentOffset = new Vector2Int(cox, coy);

            d.Frames = Strip(tex, cell, frames, d.Pivot, border);

            var ev = PnJson.Obj(entry, "events");
            if (ev != null) d.FireFrame = PnJson.Int(ev, "fire", -1);

            var mz = PnJson.Arr(entry, "muzzle");
            if (mz != null && mz.Count > 0)
            {
                d.Muzzle = new Vector2Int[mz.Count];
                for (int i = 0; i < mz.Count; i++)
                {
                    var p = mz[i] as List<object>;
                    if (p != null && p.Count >= 2)
                        d.Muzzle[i] = new Vector2Int((int)PnJson.ToNum(p[0], -1), (int)PnJson.ToNum(p[1], -1));
                    else
                        d.Muzzle[i] = new Vector2Int(-1, -1);
                }
            }
            sheets[key] = d;
            return d;
        }
    }

    /// <summary>Named sprite catalog built from a manifest such as ui.json or fx.json.</summary>
    public sealed class SpriteCatalog
    {
        private readonly string folder;
        private readonly object root;
        private readonly Dictionary<string, SheetDef> cache = new Dictionary<string, SheetDef>();

        public SpriteCatalog(string folder, string manifest)
        {
            this.folder = folder;
            var r = ArtLib.Json(folder + manifest);
            // tolerate {"sprites": {...}} or a flat map
            var inner = PnJson.Obj(r, "sprites");
            root = inner != null ? (object)inner : r;
        }

        public bool Has(string name)
        {
            return PnJson.Has(root, name) || ArtLib.TryTexture(folder + name) != null;
        }

        public SheetDef Get(string name)
        {
            SheetDef d;
            if (cache.TryGetValue(name, out d)) return d;
            var entry = PnJson.Obj(root, name);
            if (entry == null)
            {
                var e = new Dictionary<string, object>();
                e["file"] = name;
                entry = e;
            }
            d = ArtLib.Sheet(folder, name, entry, Vector2Int.zero, Vector2Int.zero, true);
            cache[name] = d;
            return d;
        }

        /// <summary>Returns null (silently) when neither manifest entry nor texture exists.</summary>
        public SheetDef TryGet(string name)
        {
            if (!Has(name)) return null;
            return Get(name);
        }
    }

    /// <summary>Character manifest (hero.json / bandit.json).</summary>
    [System.Serializable]
    public sealed class CharacterDef
    {
        public string Folder;
        public Vector2Int Cell;
        public Vector2Int Pivot;
        public Vector2Int HitPoint;
        [System.NonSerialized] public readonly Dictionary<string, SheetDef> Anims = new Dictionary<string, SheetDef>();
        [System.NonSerialized] public Sprite Portrait;
        [System.NonSerialized] public Sprite PortraitHurt;

        public static CharacterDef Load(string folder, string manifestName, string prefix, Vector2Int defCell, Vector2Int defPivot, Vector2Int defHit)
        {
            var c = new CharacterDef();
            c.Folder = folder;
            var root = ArtLib.Json(folder + manifestName);
            int a = defCell.x, b = defCell.y;
            PnJson.Pair(root, "cell", ref a, ref b);
            c.Cell = new Vector2Int(a, b);
            a = defPivot.x; b = defPivot.y;
            PnJson.Pair(root, "pivot", ref a, ref b);
            c.Pivot = new Vector2Int(a, b);
            a = defHit.x; b = defHit.y;
            PnJson.Pair(root, "hitPoint", ref a, ref b);
            c.HitPoint = new Vector2Int(a, b);

            var anims = PnJson.Obj(root, "anims");
            string[] required = { "idle", "ready", "draw_fire", "aim", "hit", "down", "win", "foul" };
            for (int i = 0; i < required.Length; i++)
            {
                string n = required[i];
                object entry = anims != null ? (object)PnJson.Obj(anims, n) : null;
                if (entry == null)
                {
                    var e = new Dictionary<string, object>();
                    e["file"] = prefix + "_" + n;
                    e["frames"] = 1.0;
                    e["fps"] = 8.0;
                    e["loop"] = (n == "idle" || n == "ready" || n == "aim" || n == "win");
                    entry = e;
                }
                if (!PnJson.Has(entry, "pivot"))
                {
                    var e2 = entry as Dictionary<string, object>;
                    if (e2 != null) e2["pivot"] = new List<object> { (double)c.Pivot.x, (double)c.Pivot.y };
                }
                if (!PnJson.Has(entry, "cell"))
                {
                    var e3 = entry as Dictionary<string, object>;
                    if (e3 != null) e3["cell"] = new List<object> { (double)c.Cell.x, (double)c.Cell.y };
                }
                c.Anims[n] = ArtLib.Sheet(folder, n, entry, c.Cell, c.Pivot, false);
            }

            string portrait = PnJson.Str(root, "portrait", prefix + "_portrait");
            string hurt = PnJson.Str(root, "portraitHurt", portrait + "_hurt");
            var pt = ArtLib.Texture(folder + portrait, 28, 28);
            c.Portrait = ArtLib.MakeSprite(pt, new RectInt(0, 0, pt.width, pt.height), new Vector2Int(0, 0), Vector4.zero);
            var ht = ArtLib.TryTexture(folder + hurt);
            c.PortraitHurt = ht != null ? ArtLib.MakeSprite(ht, new RectInt(0, 0, ht.width, ht.height), new Vector2Int(0, 0), Vector4.zero) : c.Portrait;
            return c;
        }

        public SheetDef Anim(string name)
        {
            SheetDef d;
            return Anims.TryGetValue(name, out d) ? d : null;
        }
    }
}
