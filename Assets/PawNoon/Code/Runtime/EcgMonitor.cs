using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Pixel-art heart monitor for the HIGH NOON window during the hold: a red trace sweeps left to
    /// right, drawing a P-QRS-T complex on every beat, leaving a fading trail and a short blank gap
    /// ahead of the write head (classic bedside monitor). Drawn into a small point-filtered texture,
    /// updated in place every frame (no allocations). Clean pixels only: no noise, stepped intensity.
    /// </summary>
    public sealed class EcgMonitor
    {
        public readonly SpriteRenderer Renderer;
        /// <summary>Starting beats per minute (the rhythm speeds up from here every beat).</summary>
        public float Bpm = 92f;
        /// <summary>Beats visible across the window (the sweep period).</summary>
        public float BeatsPerSweep = 2f;

        private readonly int w, h, baseY;
        private readonly Texture2D tex;
        private readonly Color32[] px;
        private readonly int[] colY;       // trace height per column (pixels from top)
        private readonly float[] colT;     // time the column was written (< 0 = never)
        private float head;                // fractional write-head column
        private float t;                   // monitor clock (s)
        private int lastCol = -1;
        private bool visible;

        private static readonly Color32 Clear = new Color32(0, 0, 0, 0);
        private static readonly Color32 Grid = new Color32(58, 24, 32, 255);
        private static readonly Color32 HeadCol = new Color32(255, 214, 200, 255);

        public EcgMonitor(Transform parent, int order, int width, int height)
        {
            w = Mathf.Max(16, width);
            h = Mathf.Max(16, height);
            baseY = h / 2 + h / 6;           // baseline a little below centre: the R spike needs headroom
            px = new Color32[w * h];
            colY = new int[w];
            colT = new float[w];
            tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawEcg";
            var spr = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
            Renderer = PawGfx.NewSprite("EcgMonitor", parent, order);
            Renderer.sprite = spr;
            Renderer.color = Color.white;
            Reset();
            Renderer.enabled = false;
        }

        /// <summary>Top-left of the monitor on the canvas.</summary>
        public void PlaceAt(int left, int top) { PawGfx.Place(Renderer, left, top); }

        public void SetVisible(bool on)
        {
            if (on == visible) return;
            visible = on;
            if (on) Reset();
            Renderer.enabled = on;
            if (on) Render();
        }

        public void Reset()
        {
            for (int i = 0; i < w; i++) { colT[i] = -999f; colY[i] = baseY; }
            head = 0f;
            t = 0f;
            lastCol = -1;
            beatCount = 0;
            firedCount = 0;
            pendingPause = 0f;
            Frenzy = 0f;
            ScheduleBeat();
        }

        // ------------------------------------------------------------------ beats
        // Each beat is anchored at its R peak. The rhythm starts at Bpm and gets faster and more
        // irregular with every beat (the duel's nerves): rate ramps per beat, RR jitter and amplitude
        // jitter grow, and from the 3rd beat on an occasional early, wide "skipped" beat (PVC) lands
        // with a compensatory pause after it. Presentation only: never touches duel timing.

        /// <summary>Rate multiplier per beat (1.16 = +16% each beat; holds are 1-4 s, so 2-6 beats).</summary>
        public float RampPerBeat = 1.16f;
        public float MaxBpm = 205f;
        /// <summary>0 at the first beat, 1 once the heart is fully frantic (~5 beats).</summary>
        public float Frenzy { get; private set; }
        /// <summary>Fired on every R peak with (beat index, frenzy 0..1, early/skipped beat).</summary>
        public System.Action<int, float, bool> OnBeat;

        private struct BeatDef { public float rT, scale, amp; public bool wide; }
        private const int MaxBeats = 6;
        private readonly BeatDef[] beats = new BeatDef[MaxBeats];
        private int beatCount;        // beats scheduled so far (the newest one is still in the future)
        private int firedCount;       // R peaks that have passed
        private float pendingPause;   // compensatory pause owed after an early beat

        private float BaseBeat { get { return 60f / Mathf.Max(30f, Bpm); } }

        private void ScheduleBeat()
        {
            float bb = BaseBeat;
            int n = beatCount;
            float f = Mathf.Clamp01(n / 5f);
            float bpm = Mathf.Min(MaxBpm, Bpm * Mathf.Pow(RampPerBeat, n));
            float rr = 60f / bpm;
            rr *= 1f + Random.Range(-1f, 1f) * (0.02f + 0.20f * f);
            bool wide = false;
            if (pendingPause > 0f) { rr += pendingPause; pendingPause = 0f; }
            else if (n >= 2 && Random.value < 0.10f + 0.18f * f)
            {
                wide = true;                      // early ectopic beat, then the heart "catches"
                pendingPause = rr * 0.45f;
                rr *= 0.62f;
            }
            float prevT = n == 0 ? 0.32f * bb - rr : beats[(n - 1) % MaxBeats].rT;
            var b = new BeatDef();
            b.rT = prevT + rr;
            b.scale = Mathf.Max(0.45f, Mathf.Lerp(1f, rr / bb, 0.65f));
            b.amp = (1f + 0.22f * f) * (1f + Random.Range(-1f, 1f) * (0.05f + 0.22f * f));
            b.wide = wide;
            beats[n % MaxBeats] = b;
            beatCount++;
        }

        /// <summary>Signal height above the baseline (px) at monitor time tc: the sum of every nearby beat.</summary>
        private float Signal(float tc)
        {
            float a = h / 64f;
            float bb = BaseBeat;
            float y = 0f;
            int from = Mathf.Max(0, beatCount - MaxBeats);
            for (int i = from; i < beatCount; i++)
            {
                var b = beats[i % MaxBeats];
                float p = (tc - b.rT) / (bb * b.scale) + 0.23f;   // phase of this beat (R peak at 0.23)
                if (p < 0f || p >= 1f) continue;
                y += (b.wide ? WideWave(p) : Wave(p)) * a * b.amp;
            }
            return y;
        }

        /// <summary>Normal P-QRS-T in units of h/64, phase p in [0,1) of one beat (R peak at 0.23).</summary>
        private static float Wave(float p)
        {
            if (p >= 0.06f && p < 0.15f) return 3.5f * Mathf.Sin((p - 0.06f) / 0.09f * Mathf.PI);          // P
            if (p >= 0.19f && p < 0.215f) return -3f;                                                        // Q
            if (p >= 0.215f && p < 0.23f) return Mathf.Lerp(-3f, 24f, (p - 0.215f) / 0.015f);               // R up
            if (p >= 0.23f && p < 0.255f) return Mathf.Lerp(24f, -8f, (p - 0.23f) / 0.025f);               // R down to S
            if (p >= 0.255f && p < 0.285f) return Mathf.Lerp(-8f, 0f, (p - 0.255f) / 0.03f);               // back to baseline
            if (p >= 0.38f && p < 0.56f) return 6f * Mathf.Sin((p - 0.38f) / 0.18f * Mathf.PI);            // T
            return 0f;
        }

        /// <summary>Early ectopic beat: no P, a wide tall bizarre QRS and a deep inverted T.</summary>
        private static float WideWave(float p)
        {
            if (p >= 0.17f && p < 0.23f) return Mathf.Lerp(0f, 27f, (p - 0.17f) / 0.06f);
            if (p >= 0.23f && p < 0.30f) return Mathf.Lerp(27f, -11f, (p - 0.23f) / 0.07f);
            if (p >= 0.30f && p < 0.35f) return Mathf.Lerp(-11f, -2f, (p - 0.30f) / 0.05f);
            if (p >= 0.35f && p < 0.58f) return -2f - 7f * Mathf.Sin((p - 0.35f) / 0.23f * Mathf.PI);
            return 0f;
        }

        public void Tick(float dt)
        {
            if (!visible) return;
            float sweep = BaseBeat * BeatsPerSweep;     // constant paper speed: faster beats bunch up
            t += dt;
            // fire every R peak that has passed; keep one beat scheduled ahead (its P wave precedes it)
            int guardB = 0;
            while (firedCount < beatCount && beats[firedCount % MaxBeats].rT <= t && guardB++ < 8)
            {
                var b = beats[firedCount % MaxBeats];
                Frenzy = Mathf.Clamp01(firedCount / 5f);
                if (OnBeat != null) OnBeat(firedCount, Frenzy, b.wide);
                firedCount++;
                ScheduleBeat();
            }
            head += dt * (w / sweep);
            int target = Mathf.FloorToInt(head);
            // write every column the head passed this frame (frame hitches never leave holes)
            int guard = 0;
            while (lastCol < target && guard++ < w * 2)
            {
                lastCol++;
                int c = ((lastCol % w) + w) % w;
                float colTime = t - (head - lastCol) * (sweep / w);
                colY[c] = Mathf.Clamp(baseY - Mathf.RoundToInt(Signal(colTime)), 1, h - 2);
                colT[c] = colTime;
            }
            Render();
        }

        private void Render()
        {
            for (int i = 0; i < px.Length; i++) px[i] = Clear;
            // faint monitor grid: dots every 8 px (pixel rows are bottom-up in the texture)
            for (int y = 4; y < h; y += 8)
                for (int x = 4; x < w; x += 8)
                    px[(h - 1 - y) * w + x] = Grid;

            float sweep = BaseBeat * BeatsPerSweep;
            float fade = sweep * 0.92f;
            int headCol = lastCol < 0 ? -1 : ((lastCol % w) + w) % w;
            for (int c = 0; c < w; c++)
            {
                float age = t - colT[c];
                if (age < 0f || age > fade) continue;
                // blank gap just ahead of the head (the oldest columns): the classic erase bar
                int ahead = headCol < 0 ? 99 : ((c - headCol) % w + w) % w;
                if (ahead > 0 && ahead <= 4) continue;
                float k = 1f - age / fade;
                k = Mathf.Round(k * 6f) / 6f;          // stepped trail intensity, pixel-honest
                if (k <= 0f) continue;
                Color32 col = c == headCol ? HeadCol : Trace(k);
                int y0 = colY[c];
                int prev = c > 0 ? colY[c - 1] : y0;
                // connect to the previous column only when it is part of the same contiguous sweep
                bool prevValid = c > 0 && t - colT[c - 1] <= fade && colT[c - 1] <= colT[c];
                int lo = prevValid ? Mathf.Min(y0, prev) : y0;
                int hi = prevValid ? Mathf.Max(y0, prev) : y0;
                for (int y = lo; y <= hi; y++) Put(c, y, col);
                // glow: one dim pixel above and below the fresh part of the trace
                if (k >= 0.66f)
                {
                    // opaque halo from the fixed palette (no alpha blending: pixel-honest)
                    var g = k >= 0.99f ? GlowHot : GlowDim;
                    PutIfEmpty(c, lo - 1, g);
                    PutIfEmpty(c, hi + 1, g);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false, false);
        }

        private static Color32 Trace(float k)
        {
            // fixed 6-step palette, dim maroon -> hot red; fully opaque
            int i = Mathf.Clamp(Mathf.RoundToInt(k * 6f) - 1, 0, 5);
            return Ramp[i];
        }

        private static readonly Color32[] Ramp =
        {
            new Color32( 92, 20, 30, 255),
            new Color32(122, 26, 34, 255),
            new Color32(156, 32, 38, 255),
            new Color32(192, 40, 44, 255),
            new Color32(228, 52, 50, 255),
            new Color32(255, 70, 58, 255),
        };
        private static readonly Color32 GlowHot = new Color32(120, 30, 36, 255);
        private static readonly Color32 GlowDim = new Color32(78, 22, 30, 255);

        private void Put(int x, int y, Color32 c)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            px[(h - 1 - y) * w + x] = c;
        }

        private void PutIfEmpty(int x, int y, Color32 c)
        {
            if (x < 0 || x >= w || y < 0 || y >= h) return;
            int i = (h - 1 - y) * w + x;
            if (px[i].a < c.a) px[i] = c;
        }
    }
}
