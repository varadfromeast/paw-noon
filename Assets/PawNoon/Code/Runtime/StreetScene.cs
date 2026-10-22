using System.Collections.Generic;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Builds the high-noon street from environment.json: full-canvas layers (heat shimmer on "heat"
    /// layers), animated props with phase offsets, drifting clouds that wrap, a circling vulture,
    /// occasional tumbleweeds rolling across with bounce + dust, and a gently pulsing sun.
    /// </summary>
    public sealed class StreetScene : MonoBehaviour
    {
        private sealed class Cloud
        {
            public SpriteRenderer sr;
            public float x;
            public float y;
            public float speed;
            public int w;
        }

        private const string Folder = "Art/Environment/";
        private readonly List<Cloud> clouds = new List<Cloud>();
        private readonly List<SpriteRenderer> heatLayers = new List<SpriteRenderer>();
        private SpriteRenderer sun;
        private SpriteRenderer vulture;
        private SpriteRenderer dawn;
        private float dawnTarget, dawnAlpha;

        /// <summary>Title mood: 1 = dawn plate fully shown (crossfades over ~1.2 s), 0 = high noon.</summary>
        public void SetDawn(float target) { dawnTarget = Mathf.Clamp01(target); }

        // weather pass (Docs/TOWN.md): a dusk floor under SetDawn, a plate tint, extra tumbleweed sway
        private float weatherDawn;
        private float plateTint = 1f;
        private float windSway = 1f;
        private float dawnShown;
        private readonly List<SpriteRenderer> plateLayers = new List<SpriteRenderer>();

        /// <summary>Dusk: the dawn plate never shows less than this (LAST LIGHT / title can still raise it to 1). 0 = off.</summary>
        public void SetWeatherDawn(float floor) { weatherDawn = Mathf.Clamp01(floor); }

        /// <summary>Stepped alpha of the dawn plate on screen right now (0..1, in 1/8 steps).</summary>
        public float DawnShown { get { return dawnShown; } }

        /// <summary>Brightness of the painted plate (1 = as painted; the dust storm uses 0.9). Applied at once.</summary>
        public float PlateTint { get { return plateTint; } }

        public void SetPlateTint(float k)
        {
            plateTint = Mathf.Clamp01(k);
            var c = new Color(plateTint, plateTint, plateTint, 1f);
            for (int i = 0; i < plateLayers.Count; i++) plateLayers[i].color = c;
            if (dawn != null) dawn.color = new Color(plateTint, plateTint, plateTint, dawnShown);
        }

        /// <summary>Dust storm: tumbleweeds bounce and sway more (1 = normal).</summary>
        public void SetWindSway(float k) { windSway = Mathf.Max(1f, k); }
        private Vector2 vultureCenter = new Vector2(150, 120);
        private Vector2 vultureRadius = new Vector2(40, 12);
        private float vultureAngle;
        private SpriteRenderer tumble;
        private SpriteAnimator tumbleAnim;
        private int[] tumbleLane = { 400, 440 };
        private bool tumbleActive;
        private float tumbleX, tumbleY, tumbleDir, tumbleSpeed, tumblePhase, tumbleTimer;
        private int lastBounce;
        private int canvasW = 360;
        private PixelParticles particles;
        private PawAudio audioMgr;
        private MaterialPropertyBlock mpb;
        private bool reducedMotion;
        private bool heatOn = true;
        private float lastHeatTime = -1f;
        private static readonly int HeatTimeId = Shader.PropertyToID("_HeatTime");

        /// <summary>Seconds between ambient tumbleweeds (a rare beat, not wallpaper).</summary>
        public Vector2 TumbleInterval = new Vector2(24f, 42f);

        public void Init(GameLayout layout, PixelParticles parts, PawAudio audio)
        {
            particles = parts;
            audioMgr = audio;
            mpb = new MaterialPropertyBlock();
            canvasW = layout.Canvas.x;
            int cw = layout.Canvas.x, ch = layout.Canvas.y;
            var env = ArtLib.Json(Folder + "environment");

            // --- layers
            var layers = PnJson.Arr(env, "layers");
            if (layers == null)
            {
                layers = new List<object>();
                string[] names = { "sky", "sun", "mesas_far", "mesas_near", "town_far", "ground", "buildings_left", "buildings_right", "foreground" };
                int[] orders = { 0, 5, 20, 30, 40, 50, 60, 60, 85 };
                for (int i = 0; i < names.Length; i++)
                {
                    var d = new Dictionary<string, object>();
                    d["name"] = names[i];
                    d["file"] = names[i];
                    d["order"] = (double)orders[i];
                    d["heat"] = i >= 2 && i <= 4;
                    layers.Add(d);
                }
            }
            for (int i = 0; i < layers.Count; i++)
            {
                var L = layers[i];
                string file = PnJson.Str(L, "file", PnJson.Str(L, "name", "layer" + i));
                string lname = PnJson.Str(L, "name", file);
                var tex = ArtLib.Texture(Folder + file, cw, ch);
                var spr = ArtLib.MakeSprite(tex, new RectInt(0, 0, tex.width, tex.height), new Vector2Int(0, 0), Vector4.zero);
                var sr = PawGfx.NewSprite("L_" + lname, transform, PnJson.Int(L, "order", 0));
                sr.sprite = spr;
                PawGfx.Place(sr, 0, 0); // canvas-sized layers: top-left at canvas (0,0)
                if (PnJson.Bool(L, "heat", false)) heatLayers.Add(sr);
                if (lname != "sun") plateLayers.Add(sr);
                if (lname == "sun") sun = sr;
            }
            SetHeat(true);

            // --- optional dawn variant of the painted plate (title screen mood), crossfaded over the noon plate
            var dawnTex = ArtLib.TryTexture(Folder + "plate_dawn");
            if (dawnTex != null)
            {
                var dspr = ArtLib.MakeSprite(dawnTex, new RectInt(0, 0, dawnTex.width, dawnTex.height), new Vector2Int(0, 0), Vector4.zero);
                dawn = PawGfx.NewSprite("L_plate_dawn", transform, 1);
                dawn.sprite = dspr;
                PawGfx.Place(dawn, 0, 0);
                dawn.color = new Color(1f, 1f, 1f, 0f);
                dawn.enabled = false;
            }

            // --- sheets & props
            var sheetsNode = PnJson.Obj(env, "sheets");
            var props = PnJson.Arr(env, "props");
            if (props != null)
            {
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    string sheetName = PnJson.Str(p, "sheet", "");
                    var sheet = EnvSheet(sheetsNode, sheetName);
                    if (sheet == null) continue;
                    var sr = PawGfx.NewSprite("P_" + sheetName + i, transform, PnJson.Int(p, "order", 62));
                    var an = sr.gameObject.AddComponent<SpriteAnimator>();
                    an.Renderer = sr;
                    an.Ambient = true;
                    an.Play(sheet);
                    an.SetPhase((float)PnJson.Num(p, "phase", Random.value));
                    PawGfx.Place(sr, PnJson.Int(p, "x", 0), PnJson.Int(p, "y", 0));
                }
            }

            // --- clouds
            var cl = PnJson.Arr(env, "clouds");
            if (cl != null)
            {
                for (int i = 0; i < cl.Count; i++)
                {
                    var c = cl[i];
                    string file = PnJson.Str(c, "file", "cloud_a");
                    var tex = ArtLib.Texture(Folder + file, 32, 12);
                    var cloud = new Cloud();
                    cloud.sr = PawGfx.NewSprite("C_" + file + i, transform, PnJson.Int(c, "order", 10));
                    cloud.sr.sprite = ArtLib.MakeSprite(tex, new RectInt(0, 0, tex.width, tex.height), new Vector2Int(0, 0), Vector4.zero);
                    cloud.x = PnJson.Int(c, "x", 0);
                    cloud.y = PnJson.Int(c, "y", 60);
                    cloud.speed = (float)PnJson.Num(c, "speed", 1.5);
                    cloud.w = tex.width;
                    clouds.Add(cloud);
                }
            }

            // --- vulture
            var vNode = PnJson.Obj(env, "vulture");
            if (vNode != null)
            {
                var vs = EnvSheet(sheetsNode, PnJson.Str(vNode, "sheet", "vulture"));
                if (vs != null)
                {
                    vulture = PawGfx.NewSprite("Vulture", transform, PnJson.Int(vNode, "order", 12));
                    var an = vulture.gameObject.AddComponent<SpriteAnimator>();
                    an.Renderer = vulture;
                    an.Ambient = true;
                    an.Play(vs);
                    int a = 150, b = 120;
                    if (PnJson.Pair(vNode, "center", ref a, ref b)) vultureCenter = new Vector2(a, b);
                    a = 40; b = 12;
                    if (PnJson.Pair(vNode, "radius", ref a, ref b)) vultureRadius = new Vector2(a, b);
                }
            }

            // --- tumbleweed
            var tNode = PnJson.Obj(env, "tumbleweed");
            if (tNode != null)
            {
                var ts = EnvSheet(sheetsNode, PnJson.Str(tNode, "sheet", "tumbleweed"));
                if (ts != null)
                {
                    tumble = PawGfx.NewSprite("Tumbleweed", transform, PnJson.Int(tNode, "order", 78));
                    tumbleAnim = tumble.gameObject.AddComponent<SpriteAnimator>();
                    tumbleAnim.Renderer = tumble;
                    tumbleAnim.Ambient = true;
                    tumbleAnim.Play(ts);
                    tumble.enabled = false;
                    int a = tumbleLane[0], b = tumbleLane[1];
                    if (PnJson.Pair(tNode, "laneY", ref a, ref b)) tumbleLane = new[] { a, b };
                }
            }
            tumbleTimer = Random.Range(6f, 10f);
        }

        private static SheetDef EnvSheet(object sheetsNode, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            var entry = PnJson.Obj(sheetsNode, name);
            if (entry == null)
            {
                if (ArtLib.TryTexture(Folder + name) == null) return null;
                var e = new Dictionary<string, object>();
                e["file"] = name;
                entry = e;
            }
            return ArtLib.Sheet(Folder, name, entry, Vector2Int.zero, Vector2Int.zero, true);
        }

        public void SetReducedMotion(bool on)
        {
            reducedMotion = on;
            SetHeat(!on);
        }

        private void SetHeat(bool on)
        {
            heatOn = on;
            if (mpb == null) return;
            for (int i = 0; i < heatLayers.Count; i++)
            {
                var sr = heatLayers[i];
                sr.GetPropertyBlock(mpb);
                mpb.SetFloat(PawGfx.HeatAmpId, on ? 1f : 0f);
                sr.SetPropertyBlock(mpb);
            }
        }

        /// <summary>
        /// The shimmer runs on ambient time, so it eases to a stop with the rest of the street during the
        /// duel hold and the air starts moving again at DRAW. Only pushed when the clock moved.
        /// </summary>
        private void PushHeatClock()
        {
            if (!heatOn || mpb == null || heatLayers.Count == 0) return;
            float t = PawTime.AmbientT;
            if (t == lastHeatTime) return;
            lastHeatTime = t;
            for (int i = 0; i < heatLayers.Count; i++)
            {
                var sr = heatLayers[i];
                sr.GetPropertyBlock(mpb);
                mpb.SetFloat(HeatTimeId, t);
                sr.SetPropertyBlock(mpb);
            }
        }

        /// <summary>Starts a tumbleweed now (review / juice).</summary>
        public void LaunchTumbleweed()
        {
            if (tumble == null) return;
            tumbleActive = true;
            tumbleDir = Random.value < 0.5f ? 1f : -1f;
            tumbleX = tumbleDir > 0 ? -24f : canvasW + 24f;
            tumbleY = Random.Range(tumbleLane[0], tumbleLane[1] + 1);
            tumbleSpeed = Random.Range(38f, 60f);
            tumblePhase = 0f;
            lastBounce = 0;
            tumble.enabled = true;
            tumble.flipX = tumbleDir < 0;
            if (audioMgr != null) audioMgr.Play("sfx_whoosh", 0.25f);
        }

        private void Update()
        {
            float want = Mathf.Max(dawnTarget, weatherDawn);
            if (dawn != null && (dawnAlpha != want))
            {
                dawnAlpha = Mathf.MoveTowards(dawnAlpha, want, PawTime.UnscaledDt / 1.2f);
                // stepped alpha keeps the crossfade pixel-honest (no soft blend bands)
                float q = Mathf.Round(dawnAlpha * 8f) / 8f;
                dawnShown = q;
                dawn.enabled = q > 0f;
                dawn.color = new Color(plateTint, plateTint, plateTint, q);
            }
            PushHeatClock();
            float dt = PawTime.AmbientDt;
            if (dt <= 0f) return;

            for (int i = 0; i < clouds.Count; i++)
            {
                var c = clouds[i];
                c.x += c.speed * dt;
                if (c.x > canvasW) c.x = -c.w;
                PawGfx.Place(c.sr, c.x, c.y);
            }

            if (sun != null)
            {
                // breathing glow: tiny tint pulse (no scaling of pixel art)
                float k = 0.94f + 0.06f * Mathf.Sin(PawTime.AmbientT * 1.3f);
                sun.color = new Color(k, k, k, 1f);
            }

            if (vulture != null)
            {
                vultureAngle += dt * 0.35f;
                float x = vultureCenter.x + Mathf.Cos(vultureAngle) * vultureRadius.x;
                float y = vultureCenter.y + Mathf.Sin(vultureAngle) * vultureRadius.y;
                vulture.flipX = Mathf.Sin(vultureAngle) > 0f;
                PawGfx.Place(vulture, x, y);
            }

            if (tumble != null)
            {
                if (!tumbleActive)
                {
                    tumbleTimer -= dt;
                    if (tumbleTimer <= 0f && !reducedMotion)
                    {
                        LaunchTumbleweed();
                        tumbleTimer = Random.Range(TumbleInterval.x, Mathf.Max(TumbleInterval.x, TumbleInterval.y));
                    }
                }
                else
                {
                    tumbleX += tumbleDir * tumbleSpeed * dt;
                    tumblePhase += dt * 2.6f;
                    float hop = Mathf.Abs(Mathf.Sin(tumblePhase * Mathf.PI)) * 7f * windSway * Mathf.Max(0.3f, 1f - tumblePhase * 0.04f);
                    // storm: a slow side-to-side sway on top of the roll (whole pixels via Place)
                    float sway = (windSway - 1f) * 4f * Mathf.Sin(tumblePhase * 1.7f);
                    int bounceIdx = (int)tumblePhase;
                    if (bounceIdx != lastBounce)
                    {
                        lastBounce = bounceIdx;
                        if (particles != null) particles.Dust(new Vector2(tumbleX, tumbleY), 77, 1, 8f);
                    }
                    PawGfx.Place(tumble, tumbleX + sway, tumbleY - hop);
                    if (tumbleX < -40f || tumbleX > canvasW + 40f)
                    {
                        tumbleActive = false;
                        tumble.enabled = false;
                    }
                }
            }
        }
    }
}
