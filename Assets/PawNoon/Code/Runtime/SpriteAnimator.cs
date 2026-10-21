using System;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>Shared materials and sprite-renderer helpers.</summary>
    public static class PawGfx
    {
        public static readonly int FlashId = Shader.PropertyToID("_Flash");
        public static readonly int HeatAmpId = Shader.PropertyToID("_HeatAmp");

        private static Material spriteMat;
        private static Shader spriteShader;

        public static Shader SpriteShader
        {
            get
            {
                if (spriteShader == null)
                {
                    spriteShader = Resources.Load<Shader>("Shaders/SpriteFx");
                    if (spriteShader == null)
                    {
                        ArtLib.Warn("shader:spritefx", "Missing Resources/Shaders/SpriteFx.shader -> Sprites/Default");
                        spriteShader = Shader.Find("Sprites/Default");
                    }
                }
                return spriteShader;
            }
        }

        public static Material SpriteMaterial
        {
            get
            {
                if (spriteMat == null)
                {
                    spriteMat = new Material(SpriteShader);
                    spriteMat.name = "PawSpriteFx";
                }
                return spriteMat;
            }
        }

        public static Material NewMaterial(Texture tex)
        {
            var m = new Material(SpriteShader);
            m.mainTexture = tex;
            return m;
        }

        public static SpriteRenderer NewSprite(string name, Transform parent, int order)
        {
            var go = new GameObject(name);
            go.layer = 0;
            if (parent != null) go.transform.SetParent(parent, false);
            var sr = go.AddComponent<SpriteRenderer>();
            sr.sharedMaterial = SpriteMaterial;
            sr.sortingOrder = order;
            return sr;
        }

        public static void Place(Component c, float canvasX, float canvasY)
        {
            c.transform.position = Px.WorldSnap(canvasX, canvasY);
        }
    }

    /// <summary>Plays SheetDef strips on a SpriteRenderer using presentation time.</summary>
    public sealed class SpriteAnimator : MonoBehaviour
    {
        public SpriteRenderer Renderer;
        /// <summary>Ambient animators use PawTime.AmbientDt (frozen by the DRAW freeze).</summary>
        public bool Ambient;
        public float Speed = 1f;
        /// <summary>
        /// Organic pacing for LOOPING strips (0 = exact fps). Frame 0 (the rest pose) holds longer and more
        /// variably (x[1-j/2 .. 1+2j]); other frames vary x[1-j/2 .. 1+j/2]. Breathing / idle stop reading
        /// as a metronome. Own RNG: never touches UnityEngine.Random.
        /// </summary>
        public float Jitter;
        /// <summary>Invoked when the current sheet enters its fire frame.</summary>
        public Action OnFire;
        /// <summary>Invoked once when a non-looping sheet ends (last frame shown for one frame duration).</summary>
        public Action OnFinished;

        private SheetDef sheet;
        private float t;
        private int frame;
        private bool finished;
        private int revertAt = -1;
        private SheetDef revertTo;
        private float jitterLeft = -1f;   // time left on the current frame in jitter mode (< 0 = not started)
        private uint rng;
        private static uint seedCounter = 0x9E3779B9u;

        public SheetDef Current { get { return sheet; } }
        public int Frame { get { return frame; } }
        public bool Finished { get { return finished; } }
        public bool Playing { get { return sheet != null && !finished; } }

        public bool IsPlaying(SheetDef s) { return sheet == s && !finished; }

        public void Play(SheetDef s, int startFrame = 0, bool restart = true)
        {
            if (s == null || s.Count == 0) return;
            if (!restart && sheet == s) return;
            sheet = s;
            revertAt = -1;
            revertTo = null;
            finished = false;
            frame = Mathf.Clamp(startFrame, 0, s.Count - 1);
            t = s.Fps > 0f ? frame / s.Fps : 0f;
            jitterLeft = -1f;
            Apply();
            if (frame == s.FireFrame && OnFire != null) OnFire();
        }

        /// <summary>Plays s, but switches to "then" upon reaching frame revertAtFrame (no events at/after it).</summary>
        public void PlayPartial(SheetDef s, int revertAtFrame, SheetDef then)
        {
            Play(s, 0, true);
            revertAt = revertAtFrame;
            revertTo = then;
        }

        /// <summary>Offsets the playhead by a fraction of the loop (props desync).</summary>
        public void SetPhase(float phase01)
        {
            if (sheet == null || sheet.Fps <= 0f) return;
            t = Mathf.Repeat(phase01, 1f) * sheet.Duration;
            frame = Mathf.Clamp((int)(t * sheet.Fps), 0, sheet.Count - 1);
            jitterLeft = -1f;
            Apply();
        }

        public void Show(Sprite s)
        {
            sheet = null;
            finished = true;
            if (Renderer != null) Renderer.sprite = s;
        }

        private float FrameSeconds(int f)
        {
            float j = Mathf.Clamp01(Jitter);
            float u = Rand01();
            float k = f == 0 ? Mathf.Lerp(1f - 0.5f * j, 1f + 2f * j, u * u) : Mathf.Lerp(1f - 0.5f * j, 1f + 0.5f * j, u);
            return Mathf.Max(0.02f, k / sheet.Fps);
        }

        private float Rand01()
        {
            if (rng == 0u) { seedCounter += 0x6D2B79F5u; rng = seedCounter | 1u; }
            rng ^= rng << 13;
            rng ^= rng >> 17;
            rng ^= rng << 5;
            return (rng & 0xFFFFFF) / 16777216f;
        }

        private void Apply()
        {
            if (Renderer != null && sheet != null && frame < sheet.Count) Renderer.sprite = sheet.Frames[frame];
        }

        private void Update()
        {
            if (sheet == null || finished || sheet.Fps <= 0f) return;
            float dt = (Ambient ? PawTime.AmbientDt : PawTime.Dt) * Speed;
            if (dt <= 0f) return;
            t += dt;
            int n = sheet.Count;
            int target = (int)(t * sheet.Fps);
            if (sheet.Loop && Jitter > 0f && n > 1)
            {
                if (jitterLeft < 0f) jitterLeft = FrameSeconds(frame);
                jitterLeft -= dt;
                int guard = n * 2;
                bool changed = false;
                while (jitterLeft <= 0f && guard-- > 0)
                {
                    frame = (frame + 1) % n;
                    jitterLeft += FrameSeconds(frame);
                    changed = true;
                }
                if (jitterLeft <= 0f) jitterLeft = FrameSeconds(frame);
                if (changed) Apply();
                t = frame / sheet.Fps;   // keeps the exact path consistent if Jitter is turned off
                return;
            }
            if (sheet.Loop)
            {
                if (target >= n)
                {
                    t = Mathf.Repeat(t, sheet.Duration);
                    target = (int)(t * sheet.Fps) % n;
                    // a loop wrap passes frame 0..target; fire events on looping sheets are not used
                }
                if (target != frame)
                {
                    frame = target;
                    Apply();
                }
                return;
            }

            var s0 = sheet;
            while (frame < target && !finished && sheet == s0)
            {
                if (frame + 1 >= n)
                {
                    finished = true;
                    if (OnFinished != null) OnFinished();
                    break;
                }
                frame++;
                if (revertAt >= 0 && frame >= revertAt && revertTo != null)
                {
                    var r = revertTo;
                    revertAt = -1;
                    revertTo = null;
                    Play(r, 0, true);
                    return;
                }
                Apply();
                if (frame == sheet.FireFrame && OnFire != null) OnFire();
                if (sheet != s0) return;
            }
        }
    }
}
