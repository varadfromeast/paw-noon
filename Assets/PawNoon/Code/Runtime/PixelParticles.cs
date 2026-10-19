using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Pooled pixel particles: animated one-shots (muzzle, smoke, puffs, rings), shell casings with
    /// gravity + bounce, sparks, kicked dust and ambient dust motes. Positions snap to whole pixels.
    /// No allocations after Init.
    /// </summary>
    public sealed class PixelParticles : MonoBehaviour
    {
        private struct P
        {
            public bool active;
            public Vector2 pos;      // canvas px (y down)
            public Vector2 vel;      // px/s
            public float gravity;    // px/s^2 (+ = down)
            public float drag;
            public float life;
            public float age;
            public SheetDef sheet;
            public float fps;
            public bool loopAnim;
            public float groundY;    // bounce floor (canvas y), <= 0 = none
            public int bounces;
            public bool ambient;
            public bool mote;
            public float wobble;
            public Color32 color;
            public int order;
        }

        private P[] ps;
        private SpriteRenderer[] srs;
        private int cursor;
        private RectInt bounds;
        private SpriteCatalog fx;
        private SheetDef moteSheet;
        private bool motesOn = true;

        public void Init(int capacity, SpriteCatalog fxCatalog)
        {
            fx = fxCatalog;
            moteSheet = fx.TryGet("dust_mote");
            ps = new P[capacity];
            srs = new SpriteRenderer[capacity];
            for (int i = 0; i < capacity; i++)
            {
                var sr = PawGfx.NewSprite("p" + i, transform, 75);
                sr.enabled = false;
                srs[i] = sr;
            }
        }

        public void SetBounds(RectInt visibleCanvas) { bounds = visibleCanvas; }

        /// <summary>FX sheet by name, or null when the art is missing (callers pass it to Burst, which ignores null).</summary>
        public SheetDef Fx(string name)
        {
            var d = fx.TryGet(name);
            if (d == null) ArtLib.Warn("fx:" + name, "Missing FX sprite '" + name + "' (skipped)");
            return d;
        }

        private int Alloc()
        {
            for (int n = 0; n < ps.Length; n++)
            {
                int i = (cursor + n) % ps.Length;
                if (!ps[i].active) { cursor = (i + 1) % ps.Length; return i; }
            }
            // steal the oldest-ish non-mote slot
            for (int n = 0; n < ps.Length; n++)
            {
                int i = (cursor + n) % ps.Length;
                if (!ps[i].mote) { cursor = (i + 1) % ps.Length; return i; }
            }
            cursor = (cursor + 1) % ps.Length;
            return cursor;
        }

        /// <summary>One-shot animated sprite at a canvas point (sheet pivot sits on the point).</summary>
        public int Burst(SheetDef sheet, Vector2 canvas, int order, Vector2 vel, float gravity = 0f, float lifeOverride = -1f)
        {
            if (sheet == null || sheet.Count == 0) return -1;
            int i = Alloc();
            var p = new P();
            p.active = true;
            p.pos = canvas;
            p.vel = vel;
            p.gravity = gravity;
            p.sheet = sheet;
            p.fps = sheet.Fps > 0 ? sheet.Fps : 12f;
            p.loopAnim = false;
            p.life = lifeOverride > 0f ? lifeOverride : Mathf.Max(0.05f, sheet.Count / p.fps);
            p.color = Pal.White;
            p.order = order;
            ps[i] = p;
            var sr = srs[i];
            sr.sprite = sheet.Frames[0];
            sr.color = Color.white;
            sr.sortingOrder = order;
            sr.flipX = false;
            sr.enabled = true;
            sr.transform.localScale = Vector3.one;
            Place(i);
            return i;
        }

        public void Flip(int index, bool flipX)
        {
            if (index >= 0 && index < srs.Length) srs[index].flipX = flipX;
        }

        public void Smoke(Vector2 canvas, int order, int count)
        {
            var s = fx.TryGet("smoke_puff");
            if (s == null) return;
            for (int k = 0; k < count; k++)
            {
                var v = new Vector2(Random.Range(-6f, 10f), Random.Range(-18f, -8f));
                int i = Burst(s, canvas + new Vector2(Random.Range(-2f, 2f), Random.Range(-2f, 2f)), order, v, 0f, s.Duration * Random.Range(1.2f, 1.8f));
                if (i >= 0) ps[i].drag = 2.5f;
            }
        }

        public void Dust(Vector2 canvasFeet, int order, int count, float spread)
        {
            var s = fx.TryGet("dust_puff");
            if (s == null) return;
            for (int k = 0; k < count; k++)
            {
                var v = new Vector2(Random.Range(-spread, spread), Random.Range(-6f, -1f));
                int i = Burst(s, canvasFeet + new Vector2(Random.Range(-3f, 3f), 0f), order, v, 0f, s.Duration * Random.Range(1.0f, 1.4f));
                if (i >= 0) { ps[i].drag = 3f; Flip(i, v.x < 0f); }
            }
        }

        public void Shell(Vector2 canvas, int order, float groundY)
        {
            var s = fx.TryGet("shell");
            if (s == null) return;
            var v = new Vector2(Random.Range(-70f, -40f), Random.Range(-120f, -90f));
            int i = Burst(s, canvas, order, v, 520f, 2.2f);
            if (i < 0) return;
            ps[i].loopAnim = true;
            ps[i].groundY = groundY;
        }

        public void Sparks(Vector2 canvas, int order, int count)
        {
            var s = fx.TryGet("spark");
            if (s == null) return;
            for (int k = 0; k < count; k++)
            {
                float a = Random.Range(0f, Mathf.PI * 2f);
                var v = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(30f, 80f);
                Burst(s, canvas, order, v, 120f);
            }
        }

        /// <summary>Single-pixel debris (tinted white pixels).</summary>
        public void Pixels(Vector2 canvas, int order, int count, Color32 color, float speed, float gravity)
        {
            for (int k = 0; k < count; k++)
            {
                int i = Alloc();
                var p = new P();
                p.active = true;
                p.pos = canvas;
                float a = Random.Range(-Mathf.PI, 0f);
                p.vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(speed * 0.4f, speed);
                p.gravity = gravity;
                p.drag = 1.5f;
                p.life = Random.Range(0.35f, 0.7f);
                p.color = color;
                p.order = order;
                ps[i] = p;
                var sr = srs[i];
                sr.sprite = ArtLib.WhitePixel;
                sr.color = color;
                sr.sortingOrder = order;
                sr.flipX = false;
                sr.enabled = true;
                Place(i);
            }
        }

        /// <summary>Ambient dust motes drifting through the sunlight (persistent, wrap inside bounds).</summary>
        public void SpawnMotes(int count, int order)
        {
            for (int k = 0; k < count; k++)
            {
                int i = Alloc();
                var p = new P();
                p.active = true;
                p.mote = true;
                p.ambient = true;
                p.pos = new Vector2(Random.Range(bounds.xMin, bounds.xMax), Random.Range(bounds.yMin + 60, bounds.yMax - 40));
                p.vel = new Vector2(Random.Range(2f, 6f), Random.Range(-1.5f, 1.5f));
                p.wobble = Random.Range(0f, 10f);
                p.life = float.MaxValue;
                p.order = order;
                p.color = (k % 3 == 0) ? Pal.Cream0 : Pal.Sand0;
                ps[i] = p;
                var sr = srs[i];
                bool big = moteSheet != null && moteSheet.Count > 1 && (k % 4 == 0);
                sr.sprite = moteSheet != null ? moteSheet.Frames[big ? 1 : 0] : ArtLib.WhitePixel;
                sr.color = moteSheet != null ? (Color)Pal.White : (Color)p.color;
                sr.sortingOrder = order;
                sr.enabled = motesOn;
                Place(i);
            }
        }

        public void SetMotesVisible(bool on)
        {
            motesOn = on;
            for (int i = 0; i < ps.Length; i++) if (ps[i].mote) srs[i].enabled = on;
        }

        public void ClearOneShots()
        {
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i].active && !ps[i].mote)
                {
                    ps[i].active = false;
                    srs[i].enabled = false;
                }
            }
        }

        private void Place(int i)
        {
            srs[i].transform.localPosition = Px.WorldSnap(ps[i].pos.x, ps[i].pos.y);
        }

        private void Update()
        {
            if (ps == null) return;
            for (int i = 0; i < ps.Length; i++)
            {
                if (!ps[i].active) continue;
                float dt = ps[i].ambient ? PawTime.AmbientDt : PawTime.Dt;
                if (dt <= 0f) continue;
                var p = ps[i];
                p.age += dt;
                if (p.mote)
                {
                    p.wobble += dt;
                    p.pos.x += p.vel.x * dt;
                    p.pos.y += (p.vel.y + Mathf.Sin(p.wobble * 0.9f) * 3f) * dt;
                    if (p.pos.x > bounds.xMax + 2) p.pos.x = bounds.xMin - 2;
                    if (p.pos.y < bounds.yMin + 40) p.pos.y = bounds.yMax - 50;
                    if (p.pos.y > bounds.yMax - 30) p.pos.y = bounds.yMin + 60;
                    // twinkle: hide for brief moments (alpha is 0/255 only)
                    bool lit = Mathf.Sin(p.wobble * 2.3f + i) > -0.85f;
                    srs[i].enabled = motesOn && lit;
                    ps[i] = p;
                    Place(i);
                    continue;
                }
                if (p.age >= p.life)
                {
                    p.active = false;
                    ps[i] = p;
                    srs[i].enabled = false;
                    continue;
                }
                p.vel.y += p.gravity * dt;
                if (p.drag > 0f) p.vel *= Mathf.Max(0f, 1f - p.drag * dt);
                p.pos += p.vel * dt;
                if (p.groundY > 0f && p.pos.y > p.groundY && p.vel.y > 0f)
                {
                    p.pos.y = p.groundY;
                    p.vel.y = -p.vel.y * 0.42f;
                    p.vel.x *= 0.6f;
                    p.bounces++;
                    if (p.bounces >= 3 || Mathf.Abs(p.vel.y) < 12f)
                    {
                        p.vel = Vector2.zero;
                        p.gravity = 0f;
                        p.loopAnim = false;
                    }
                }
                if (p.sheet != null)
                {
                    int n = p.sheet.Count;
                    int f = (int)(p.age * p.fps);
                    if (p.loopAnim) f %= n;
                    else if (f >= n) f = n - 1;
                    if (p.vel == Vector2.zero && p.groundY > 0f) f = 0;
                    srs[i].sprite = p.sheet.Frames[f];
                }
                ps[i] = p;
                Place(i);
            }
        }
    }
}
