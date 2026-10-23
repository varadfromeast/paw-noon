using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The "swipe up to draw" teaching cue (SPEC §11): a ghost thumb pad rises out of the holster leaving a
    /// bright dotted trail, while three chevrons stacked above the holster light up bottom-to-top like an
    /// escalator arrow. Purely suggestive: everything is a stepped-alpha overlay on the UI layer, sized to
    /// read at a glance and placed where the swipe actually happens, so it never covers the duel itself.
    /// Procedural sprites (no art needed), no per-frame allocations.
    /// </summary>
    public sealed class SwipeCue
    {
        /// <summary>What the cue is teaching right now. Grip: rest the thumb on the holster (standoff). Wait: keep it
        /// there, the thumb only breathes (countdown + hold). Go: DRAW happened, the thumb rises (loops until the draw).</summary>
        public enum Mode { Grip, Wait, Go }
        private Mode mode = Mode.Grip;
        public Mode Current { get { return mode; } }

        public void SetMode(Mode m)
        {
            if (m == mode) return;
            mode = m;
            t = 0f;
            lastTrail = 0f;
            for (int i = 0; i < TrailN; i++) { trailAge[i] = -1f; trail[i].enabled = false; }
            if (on) Apply();
        }
        private const int ChevronN = 3, TrailN = 10;
        private const float Period = 1.1f;        // one Go cycle
        private const float RiseStart = 0.05f, RiseSeconds = 0.4f, Hold = 0.15f, Fade = 0.2f;

        private readonly SpriteRenderer thumb;
        private readonly SpriteRenderer[] chevrons = new SpriteRenderer[ChevronN];
        private readonly SpriteRenderer[] trail = new SpriteRenderer[TrailN];
        private readonly float[] trailAge = new float[TrailN];
        private int cx, startY, rise;              // thumb centre x, thumb top-left y at rest, rise in px
        private int chevX, chevY0;                 // chevron column: left x and bottom chevron top y
        private float t, lastTrail;
        private bool on, reduced;
        private static Sprite thumbSprite, chevronSprite, dotSprite;

        public bool On { get { return on; } }
        /// <summary>True while the thumb is resting on the grip (the holster glows then).</summary>
        public bool Gripping { get { return on && (mode != Mode.Go || t < RiseStart); } }

        public SwipeCue(Transform parent, int order)
        {
            thumb = PawGfx.NewSprite("SwipeThumb", parent, order + 2);
            thumb.sprite = ThumbSprite();
            thumb.enabled = false;
            for (int i = 0; i < ChevronN; i++)
            {
                chevrons[i] = PawGfx.NewSprite("SwipeChevron" + i, parent, order + 1);
                chevrons[i].sprite = ChevronSprite();
                chevrons[i].enabled = false;
            }
            for (int i = 0; i < TrailN; i++)
            {
                trail[i] = PawGfx.NewSprite("SwipeTrail" + i, parent, order);
                trail[i].sprite = DotSprite();
                trail[i].enabled = false;
                trailAge[i] = -1f;
            }
        }

        /// <summary>Lay out around the holster: gripX/gripY is the grip's centre on the canvas; top is the lowest y the cue may reach.</summary>
        public void Place(int gripX, int gripY, int top)
        {
            cx = gripX;
            startY = gripY - 9;                                  // thumb pad 18 px tall, centred on the grip
            rise = Mathf.Clamp(gripY - 12 - top, 36, 64);
            chevX = gripX - 8;                                   // chevrons 16 px wide, centred
            chevY0 = gripY - 16 - 10;                            // bottom chevron just above the grip
        }

        public void SetReducedMotion(bool v) { reduced = v; }

        public void Show(bool v)
        {
            on = v;
            t = 0f;
            lastTrail = 0f;
            thumb.enabled = false;
            for (int i = 0; i < ChevronN; i++) chevrons[i].enabled = v;
            for (int i = 0; i < TrailN; i++) { trailAge[i] = -1f; trail[i].enabled = false; }
            if (v) Apply();
        }

        public void Tick(float dt)
        {
            if (!on) return;
            t += dt;
            if (t >= Period) { t -= Period; lastTrail = 0f; }
            for (int i = 0; i < TrailN; i++)
            {
                if (trailAge[i] < 0f) continue;
                trailAge[i] += dt;
                float k = 1f - trailAge[i] / 0.45f;
                if (k <= 0f) { trailAge[i] = -1f; trail[i].enabled = false; continue; }
                trail[i].color = new Color(1f, 1f, 1f, 0.9f * Stepped(k));
            }
            Apply();
        }

        private void Apply()
        {
            if (mode != Mode.Go)
            {
                // Grip / Wait: thumb sits on the grip and breathes (2 px, slow); chevrons dim, no rise, no trail.
                // Wait is calmer still: it is the moment the player must NOT move.
                for (int i = 0; i < ChevronN; i++)
                {
                    var c = Pal.Cream1;
                    chevrons[i].color = new Color(c.r / 255f, c.g / 255f, c.b / 255f, mode == Mode.Grip ? 0.5f : 0.3f);
                    PawGfx.Place(chevrons[i], chevX, chevY0 - i * 11);
                }
                thumb.enabled = !reduced;
                if (!reduced)
                {
                    float breathe = mode == Mode.Grip ? 1.2f : 0.8f;
                    int dy = Mathf.RoundToInt(Mathf.Abs(Mathf.Sin(t * Mathf.PI / breathe)) * 2f);
                    thumb.color = new Color(1f, 1f, 1f, 0.75f);
                    PawGfx.Place(thumb, cx - 8, startY + dy);
                }
                return;
            }
            // Go: chevrons light up bottom -> top in step with the rise; the thumb rises, holds, fades, repeats
            float phase = t < RiseStart ? 0f : Mathf.Clamp01((t - RiseStart) / RiseSeconds);
            for (int i = 0; i < ChevronN; i++)
            {
                bool lit = reduced ? true : phase * ChevronN >= i + 0.5f && t < RiseStart + RiseSeconds + Hold;
                var c = lit ? Pal.SigYellow : Pal.Cream1;
                chevrons[i].color = new Color(c.r / 255f, c.g / 255f, c.b / 255f, lit ? 1f : 0.55f);
                PawGfx.Place(chevrons[i], chevX, chevY0 - i * 11);
            }
            if (reduced) { thumb.enabled = false; return; }
            float a;
            if (t < RiseStart) a = 1f;
            else if (t < RiseStart + RiseSeconds + Hold) a = 1f;
            else if (t < RiseStart + RiseSeconds + Hold + Fade) a = 1f - (t - RiseStart - RiseSeconds - Hold) / Fade;
            else a = 0f;
            bool show = a > 0.01f;
            thumb.enabled = show;
            if (!show) return;
            float ease = 1f - (1f - phase) * (1f - phase);
            int y = startY - Mathf.RoundToInt(ease * rise);
            thumb.color = new Color(1f, 1f, 1f, 0.75f * Stepped(a));
            PawGfx.Place(thumb, cx - 8, y);
            float travelled = startY - y;
            while (phase > 0f && lastTrail + 5f <= travelled)
            {
                lastTrail += 5f;
                Spawn(cx - 1, startY - Mathf.RoundToInt(lastTrail) + 14);
            }
        }

        private void Spawn(int x, int y)
        {
            int best = -1; float oldest = -1f;
            for (int i = 0; i < TrailN; i++)
            {
                if (trailAge[i] < 0f) { best = i; break; }
                if (trailAge[i] > oldest) { oldest = trailAge[i]; best = i; }
            }
            trailAge[best] = 0f;
            trail[best].enabled = true;
            trail[best].color = Color.white;
            PawGfx.Place(trail[best], x, y);
        }

        /// <summary>Hard 3-step alpha (house rule: no smooth glows).</summary>
        private static float Stepped(float v) { return Mathf.Ceil(Mathf.Clamp01(v) * 3f - 0.001f) / 3f; }

        // ------------------------------------------------------------------ procedural pixel sprites

        /// <summary>16x18 thumb pad: cream fill, 1 px ink outline, a brass nail line. Reads as "your thumb" at phone size.</summary>
        private static Sprite ThumbSprite()
        {
            if (thumbSprite != null) return thumbSprite;
            string[] rows =
            {
                "......XXXX......",
                "....XX....XX....",
                "...X........X...",
                "..X..........X..",
                "..X..........X..",
                ".X............X.",
                ".X............X.",
                ".X............X.",
                ".X............X.",
                ".X............X.",
                ".X............X.",
                "..X..........X..",
                "..X..........X..",
                "..X..........X..",
                "..X..........X..",
                "..X..........X..",
                "..X..........X..",
                "..XXXXXXXXXXXX..",
            };
            thumbSprite = FromRows("PawSwipeThumb", rows, Pal.Cream0, Pal.Ink, true);
            return thumbSprite;
        }

        /// <summary>16x8 solid chevron band pointing up: white fill (tinted by the renderer), 1 px ink outline.</summary>
        private static Sprite ChevronSprite()
        {
            if (chevronSprite != null) return chevronSprite;
            string[] rows =
            {
                "......XXXX......",
                ".....XFFFFX.....",
                "....XFFFFFFX....",
                "...XFFFXXFFFX...",
                "..XFFFX..XFFFX..",
                ".XFFFX....XFFFX.",
                "XFFFX......XFFFX",
                "XXXX........XXXX",
            };
            chevronSprite = FromRows("PawSwipeChevron", rows, Pal.White, Pal.Ink, false);
            return chevronSprite;
        }

        private static Sprite DotSprite()
        {
            if (dotSprite != null) return dotSprite;
            dotSprite = FromRows("PawSwipeDot", new[] { "XXX", "XXX", "XXX" }, Pal.Cream0, Pal.Cream0, false);
            return dotSprite;
        }

        /// <summary>Rows of '.'/'X' -> sprite (pivot top-left). With fill=true the enclosed interior is filled (scanline), the X ring is the outline.
        /// Also used by TutorialCard's step dots.</summary>
        internal static Sprite FromRows(string name, string[] rows, Color32 fillCol, Color32 lineCol, bool fill)
        {
            int h = rows.Length, w = rows[0].Length;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = name;
            var px = new Color32[w * h];
            var clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < h; y++)
            {
                int first = rows[y].IndexOf('X'), last = rows[y].LastIndexOf('X');
                for (int x = 0; x < w; x++)
                {
                    bool line = rows[y][x] == 'X';
                    bool inside = rows[y][x] == 'F' || (fill && first >= 0 && x > first && x < last);
                    px[(h - 1 - y) * w + x] = line ? lineCol : (inside ? fillCol : clear);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
        }
    }
}
