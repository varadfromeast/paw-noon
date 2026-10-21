using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// World camera -> point-filtered virtual-resolution RenderTexture -> one full-screen quad drawn by a
    /// display camera with the sharp-bilinear PixelUpscale shader (grade, vignette, flash, sub-pixel
    /// shake and zoom). Exposes the visible and safe rects in canvas px.
    /// Optional overlay: hierarchies registered with SetOverlayRoot (HUD, cues, panels) render to a second
    /// virtual RT (premultiplied) and are composited last: unzoomed, unshaken, never flashed or torn.
    /// Duel juice (all world-only, all off with ReducedMotion): Strike (DRAW tear + chroma), HeroHurt
    /// (red flash + wince), PopAt (stepped radial pop), PushTo (kill lean-in), SetZoomFocus, SetTension.
    /// </summary>
    public sealed class PixelPipeline : MonoBehaviour
    {
        public const int UiLayer = 5;
        /// <summary>Layer for hierarchies registered with SetOverlayRoot (unnamed user layer, never culled by the world camera).</summary>
        public const int OverlayLayer = 30;

        public Camera WorldCam;
        public Camera DisplayCam;
        public Camera OverlayCam;
        public RenderTexture Rt;
        public RenderTexture OverlayRt;
        public int VirtualW { get; private set; }
        public int VirtualH { get; private set; }
        /// <summary>Visible canvas rect (x, y = top-left canvas px).</summary>
        public RectInt Visible { get; private set; }
        /// <summary>Safe-area canvas rect (inside Visible).</summary>
        public RectInt Safe { get; private set; }
        public int LayoutVersion { get; private set; }

        // effect inputs (set by the game every frame)
        public float Zoom = 1f;
        public float Flash;
        public Color FlashColor = Color.white;
        /// <summary>Standoff vignette ("barely there"); the hold tightens it (inner radius shrinks as it rises above this baseline).</summary>
        public float Vignette = 0.24f;
        public float Warm = 0.6f;
        /// <summary>Black crush of the grade (0 = off).</summary>
        public float Crush = 0.025f;
        public bool ReducedMotion;
        /// <summary>DRAW tear / chroma switches (ReducedMotion also disables them).</summary>
        public bool TearEnabled = true;
        public bool ChromaEnabled = true;
        /// <summary>HeroHurt / PopAt switch (ReducedMotion also disables them).</summary>
        public bool HitFxEnabled = true;
        /// <summary>When true the overlay follows the shake/kick in whole pixels (default: rock-steady HUD).</summary>
        public bool OverlayShakes;

        private GameLayout layout;
        private Material mat;
        private GameObject quad;
        private Mesh quadMesh;
        private int lastW, lastH;
        private Rect lastSafe;
        private float shakeAmp;
        private float shakeT;
        private Vector2 kick;
        private Vector2 shakeOffset;
        private float vignetteBase;
        private bool flashFresh;

        // overlay
        private readonly Transform[] overlayRoots = new Transform[4];
        private readonly int[] overlayHierarchyCount = new int[4];   // hierarchyCount at the last re-layer walk
        private int overlayCount;
        private int overlayWalkFrame;

        // 60 Hz "ticks": frame-counted effects last the same real time on 120 Hz displays
        private const float Tick = 1f / 60f;
        private float tearLeft, chromaLeft, winceLeft, popLeft;
        private float popRadius = 26f, popAmount;
        private Vector2 popCanvas;

        // zoom focus + kill push
        private bool hasFocus;
        private Vector2 focusCanvas;
        private Vector2 zoomCenter = new Vector2(0.5f, 0.5f);
        private Vector2 pushCanvas;
        private float pushPeak, pushAmt, pushLeft;

        // tension ladder
        private float tensionTarget, tension;

        private static readonly int TexSizeId = Shader.PropertyToID("_TexSize");
        private static readonly int ZoomId = Shader.PropertyToID("_Zoom");
        private static readonly int OffsetId = Shader.PropertyToID("_Offset");
        private static readonly int FlashAmtId = Shader.PropertyToID("_Flash");
        private static readonly int FlashColId = Shader.PropertyToID("_FlashColor");
        private static readonly int VignetteId = Shader.PropertyToID("_Vignette");
        private static readonly int WarmId = Shader.PropertyToID("_Warm");
        private static readonly int OverlayTexId = Shader.PropertyToID("_OverlayTex");
        private static readonly int OverlayOnId = Shader.PropertyToID("_OverlayOn");
        private static readonly int OverlayOffsetId = Shader.PropertyToID("_OverlayOffset");
        private static readonly int ZoomCenterId = Shader.PropertyToID("_ZoomCenter");
        private static readonly int VigInnerId = Shader.PropertyToID("_VigInner");
        private static readonly int CrushId = Shader.PropertyToID("_Crush");
        private static readonly int TensionId = Shader.PropertyToID("_Tension");
        private static readonly int TearId = Shader.PropertyToID("_Tear");
        private static readonly int ChromaId = Shader.PropertyToID("_Chroma");
        private static readonly int WinceId = Shader.PropertyToID("_Wince");
        private static readonly int PopId = Shader.PropertyToID("_Pop");

        public void Init(GameLayout l, Color clear)
        {
            layout = l;

            var wgo = new GameObject("WorldCamera");
            wgo.transform.SetParent(transform, false);
            WorldCam = wgo.AddComponent<Camera>();
            WorldCam.orthographic = true;
            WorldCam.cullingMask = 1 << 0;
            WorldCam.clearFlags = CameraClearFlags.SolidColor;
            WorldCam.backgroundColor = clear;
            WorldCam.nearClipPlane = 0.1f;
            WorldCam.farClipPlane = 200f;
            WorldCam.depth = 0;
            WorldCam.allowHDR = false;
            WorldCam.allowMSAA = false;
            WorldCam.useOcclusionCulling = false;

            var ogo = new GameObject("OverlayCamera");
            ogo.transform.SetParent(transform, false);
            OverlayCam = ogo.AddComponent<Camera>();
            OverlayCam.orthographic = true;
            OverlayCam.cullingMask = 1 << OverlayLayer;
            OverlayCam.clearFlags = CameraClearFlags.SolidColor;
            OverlayCam.backgroundColor = new Color(0f, 0f, 0f, 0f);
            OverlayCam.nearClipPlane = 0.1f;
            OverlayCam.farClipPlane = 200f;
            OverlayCam.depth = 1;
            OverlayCam.allowHDR = false;
            OverlayCam.allowMSAA = false;
            OverlayCam.useOcclusionCulling = false;
            OverlayCam.enabled = false;   // on once a root is registered

            var dgo = new GameObject("DisplayCamera");
            dgo.transform.SetParent(transform, false);
            dgo.transform.position = new Vector3(0f, -10000f, -10f);
            DisplayCam = dgo.AddComponent<Camera>();
            // exactly one AudioListener in the whole game: ours; any other (e.g. a stray scene camera) is disabled
            dgo.AddComponent<AudioListener>();
            var listeners = FindObjectsByType<AudioListener>();   // 6000.6: the FindObjectsSortMode overloads are obsolete
            for (int i = 0; i < listeners.Length; i++)
                if (listeners[i] != null && listeners[i].gameObject != dgo) listeners[i].enabled = false;
            DisplayCam.orthographic = true;
            DisplayCam.orthographicSize = 1f;
            DisplayCam.cullingMask = 1 << UiLayer;
            DisplayCam.clearFlags = CameraClearFlags.SolidColor;
            DisplayCam.backgroundColor = Color.black;
            DisplayCam.nearClipPlane = 0.1f;
            DisplayCam.farClipPlane = 50f;
            DisplayCam.depth = 10;
            DisplayCam.allowHDR = false;
            DisplayCam.allowMSAA = false;

            var shader = Resources.Load<Shader>("Shaders/PixelUpscale");
            if (shader == null)
            {
                ArtLib.Warn("shader:upscale", "Missing Resources/Shaders/PixelUpscale.shader -> Unlit/Texture (no effects)");
                shader = Shader.Find("Unlit/Texture");
            }
            mat = new Material(shader);
            mat.name = "PawUpscale";
            vignetteBase = Vignette;

            quad = new GameObject("DisplayQuad");
            quad.layer = UiLayer;
            quad.transform.SetParent(transform, false);
            quad.transform.position = new Vector3(0f, -10000f, 0f);
            quadMesh = new Mesh();
            quadMesh.name = "DisplayQuad";
            quadMesh.vertices = new[] { new Vector3(-1, -1, 0), new Vector3(-1, 1, 0), new Vector3(1, 1, 0), new Vector3(1, -1, 0) };
            quadMesh.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            quadMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            quadMesh.RecalculateBounds();
            quad.AddComponent<MeshFilter>().sharedMesh = quadMesh;
            var mr = quad.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;

            Rebuild();
        }

        public void AddShake(float px)
        {
            if (ReducedMotion) return;
            shakeAmp = Mathf.Max(shakeAmp, px);
        }

        /// <summary>Directional kick (canvas px; y down), decays smoothly.</summary>
        public void Kick(Vector2 px)
        {
            if (ReducedMotion) return;
            kick += px;
        }

        public void FlashScreen(float amount, Color c)
        {
            if (ReducedMotion) return;
            Flash = Mathf.Max(Flash, amount);
            FlashColor = c;
            flashFresh = true;
        }

        // ------------------------------------------------------------------ overlay

        /// <summary>
        /// Renders this hierarchy (e.g. the UI root) into the overlay RT, composited after every world effect:
        /// no zoom, no shake (unless OverlayShakes), no flash, tear or vignette. Children created later are
        /// re-layered automatically. Up to 4 roots.
        /// </summary>
        public void SetOverlayRoot(Transform root)
        {
            if (root == null) return;
            for (int i = 0; i < overlayCount; i++) if (overlayRoots[i] == root) return;
            if (overlayCount >= overlayRoots.Length) return;
            overlayHierarchyCount[overlayCount] = root.hierarchyCount;
            overlayRoots[overlayCount++] = root;
            SetLayerRecursive(root, OverlayLayer);
            OverlayCam.enabled = true;
            mat.SetFloat(OverlayOnId, 1f);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            var go = t.gameObject;
            if (go.layer != layer) go.layer = layer;
            int n = t.childCount;
            for (int i = 0; i < n; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        // ------------------------------------------------------------------ duel juice (world only)

        /// <summary>DRAW strike: 2-tick screen tear (alternating 4-px bands, +-1 px) and a 1-tick chromatic split.</summary>
        public void Strike()
        {
            if (ReducedMotion) return;
            if (TearEnabled) tearLeft = 2f * Tick - 0.002f;
            if (ChromaEnabled) chromaLeft = Tick - 0.002f;
        }

        /// <summary>Hero is hit: 1-frame red flash at 25 % and a 3-tick blood-ink wince at the screen edges.</summary>
        public void HeroHurt()
        {
            if (ReducedMotion || !HitFxEnabled) return;
            FlashScreen(0.25f, new Color(0.86f, 0.12f, 0.1f));
            winceLeft = 3f * Tick - 0.002f;
        }

        /// <summary>Stepped, dithered radial brightness pop around a canvas point for 3 ticks (Pawtruth hit).</summary>
        public void PopAt(Vector2 canvas, float radiusPx = 26f, float amount = 0.5f)
        {
            if (ReducedMotion || !HitFxEnabled) return;
            popCanvas = canvas;
            popRadius = Mathf.Max(2f, radiusPx);
            popAmount = Mathf.Clamp01(amount);
            popLeft = 3f * Tick - 0.002f;
        }

        /// <summary>Anchor of Zoom (the hold push-in) at a canvas point instead of the screen centre.</summary>
        public void SetZoomFocus(Vector2 canvas)
        {
            focusCanvas = canvas;
            hasFocus = true;
        }

        public void ClearZoomFocus() { hasFocus = false; }

        /// <summary>
        /// Kill lean-in: eases a zoom of (1 + amount) anchored on a canvas point, holds, then eases back out
        /// by itself after `seconds` (real time). Stacks on top of Zoom.
        /// </summary>
        public void PushTo(Vector2 canvas, float amount = 0.02f, float seconds = 0.6f)
        {
            if (ReducedMotion) return;
            pushCanvas = canvas;
            pushPeak = Mathf.Clamp(amount, 0f, 0.2f);
            pushLeft = Mathf.Max(0.05f, seconds);
        }

        /// <summary>"Last light" ladder 0..1: desaturated, warmer, darker, heavier vignette. Eases over ~2 s.</summary>
        public void SetTension(float t) { tensionTarget = Mathf.Clamp01(t); }

        /// <summary>Tension from both HP fractions: 0 while both are above 70 %, 1 once both are below 40 %.</summary>
        public static float TensionFromHp(double hpFraction0, double hpFraction1)
        {
            float lo = (float)System.Math.Min(hpFraction0, hpFraction1);
            float hi = (float)System.Math.Max(hpFraction0, hpFraction1);
            float byLeader = Mathf.InverseLerp(0.7f, 0.4f, hi);    // both cats wounded
            float byLow = Mathf.InverseLerp(0.7f, 0.25f, lo);      // someone is on the edge
            return Mathf.Clamp01(Mathf.Max(byLeader, byLow * 0.6f));
        }

        private Vector2 CanvasToUv(Vector2 c)
        {
            float u = (c.x - Visible.x) / Mathf.Max(1, VirtualW);
            float v = 1f - (c.y - Visible.y) / Mathf.Max(1, VirtualH);
            return new Vector2(Mathf.Clamp01(u), Mathf.Clamp01(v));
        }

        /// <summary>Screen pixel (bottom-left origin) to canvas px (ignores shake/zoom; UI is shown unzoomed).</summary>
        public Vector2 ScreenToCanvas(Vector2 screen)
        {
            float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            float x = Visible.x + screen.x / sw * VirtualW;
            float y = Visible.y + (sh - screen.y) / sh * VirtualH;
            return new Vector2(x, y);
        }

        private void Rebuild()
        {
            int sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
            lastW = sw;
            lastH = sh;
            lastSafe = Screen.safeArea;
            float aspect = sw / (float)sh;
            float coreAspect = layout.Core.x / (float)layout.Core.y;
            int vw, vh;
            if (aspect <= coreAspect)
            {
                vw = layout.Core.x;
                vh = Mathf.RoundToInt(layout.Core.x / aspect);
            }
            else
            {
                vh = layout.Core.y;
                vw = Mathf.RoundToInt(layout.Core.y * aspect);
            }
            vw = Mathf.Clamp(vw, 16, 2048);
            vh = Mathf.Clamp(vh, 16, 2048);
            VirtualW = vw;
            VirtualH = vh;

            if (Rt != null)
            {
                WorldCam.targetTexture = null;
                Rt.Release();
                Destroy(Rt);
            }
            if (OverlayRt != null)
            {
                OverlayCam.targetTexture = null;
                OverlayRt.Release();
                Destroy(OverlayRt);
            }
            // sprites sort by order, never by depth: no depth buffer needed (saves bandwidth on mobile)
            Rt = NewVirtualRt(vw, vh, "PawVirtual");
            OverlayRt = NewVirtualRt(vw, vh, "PawOverlay");

            WorldCam.targetTexture = Rt;
            WorldCam.orthographicSize = vh * 0.5f;
            // Odd sizes: shift half a pixel so the view edges land on whole pixels.
            float ox = (vw & 1) == 1 ? 0.5f : 0f;
            float oy = (vh & 1) == 1 ? 0.5f : 0f;
            WorldCam.transform.position = new Vector3(ox, oy, -100f);
            OverlayCam.targetTexture = OverlayRt;
            OverlayCam.orthographicSize = vh * 0.5f;
            OverlayCam.transform.position = new Vector3(ox, oy, -100f);

            int visLeft = Mathf.RoundToInt(Px.HalfW + ox - vw * 0.5f);
            int visTop = Mathf.RoundToInt(Px.HalfH - oy - vh * 0.5f);
            Visible = new RectInt(visLeft, visTop, vw, vh);

            var sa = Screen.safeArea;
            float kx = vw / (float)sw, ky = vh / (float)sh;
            int l = Mathf.CeilToInt(sa.xMin * kx);
            int r = Mathf.FloorToInt(sa.xMax * kx);
            int t = Mathf.CeilToInt((sh - sa.yMax) * ky);
            int b = Mathf.FloorToInt((sh - sa.yMin) * ky);
            if (r <= l || b <= t) { l = 0; r = vw; t = 0; b = vh; }
            Safe = new RectInt(visLeft + l, visTop + t, r - l, b - t);

            float qa = sw / (float)sh;
            quad.transform.localScale = new Vector3(qa, 1f, 1f);
            mat.mainTexture = Rt;
            mat.SetTexture(OverlayTexId, OverlayRt);
            mat.SetFloat(OverlayOnId, overlayCount > 0 ? 1f : 0f);
            mat.SetVector(TexSizeId, new Vector4(vw, vh, 1f / vw, 1f / vh));
            LayoutVersion++;
        }

        private static RenderTexture NewVirtualRt(int w, int h, string name)
        {
            var rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
            rt.name = name;
            rt.filterMode = FilterMode.Point;
            rt.wrapMode = TextureWrapMode.Clamp;
            rt.useMipMap = false;
            rt.antiAliasing = 1;
            rt.Create();
            return rt;
        }

        private void LateUpdate()
        {
            if (Screen.width != lastW || Screen.height != lastH || Screen.safeArea != lastSafe) Rebuild();

            float dt = PawTime.UnscaledDt;
            // smooth sub-pixel shake: two incommensurate sines with decaying amplitude
            shakeT += dt;
            if (ReducedMotion) { shakeAmp = 0f; kick = Vector2.zero; }
            shakeAmp = Mathf.Max(0f, shakeAmp - dt * (6f + shakeAmp * 10f));
            kick = Vector2.Lerp(kick, Vector2.zero, Ease.Damp(14f, dt));
            shakeOffset.x = shakeAmp * Mathf.Sin(shakeT * 71f) + kick.x;
            shakeOffset.y = shakeAmp * Mathf.Cos(shakeT * 53f) + kick.y;

            // overlay hierarchies: keep late-created children on the overlay layer (alloc-free walk). The walk runs
            // when a root's hierarchy grew or shrank (new UI is built lazily: cards, bubbles, labs) and, as a safety
            // net, every 120th frame; before, it touched every UI transform every frame.
            bool periodic = ++overlayWalkFrame >= 120;
            if (periodic) overlayWalkFrame = 0;
            for (int i = 0; i < overlayCount; i++)
            {
                var root = overlayRoots[i];
                if (root == null) continue;
                int hc = root.hierarchyCount;
                if (hc == overlayHierarchyCount[i] && !periodic) continue;
                overlayHierarchyCount[i] = hc;
                SetLayerRecursive(root, OverlayLayer);
            }

            if (ReducedMotion) { tearLeft = chromaLeft = winceLeft = popLeft = 0f; pushLeft = 0f; pushAmt = 0f; }

            // kill push: ease in fast, hold, ease out
            if (pushLeft > 0f)
            {
                pushLeft -= dt;
                pushAmt = Mathf.MoveTowards(pushAmt, pushPeak, dt * pushPeak / 0.12f);
            }
            else if (pushAmt > 0f)
            {
                pushAmt = Mathf.MoveTowards(pushAmt, 0f, dt * Mathf.Max(pushPeak, 0.001f) / 0.45f);
            }

            float baseZoom = ReducedMotion ? 1f : Mathf.Max(1f, Zoom);
            float zoom = baseZoom * (1f + pushAmt);
            Vector2 centreTarget = hasFocus && !ReducedMotion ? CanvasToUv(focusCanvas) : new Vector2(0.5f, 0.5f);
            if (pushPeak > 0f && pushAmt > 0f)
                centreTarget = Vector2.Lerp(centreTarget, CanvasToUv(pushCanvas), Mathf.Clamp01(pushAmt / pushPeak));
            // the anchor only matters while zoomed; glide it so a focus change never jumps the frame
            zoomCenter = zoom <= 1.0001f ? centreTarget : Vector2.Lerp(zoomCenter, centreTarget, Ease.Damp(8f, dt));

            tension = Mathf.MoveTowards(tension, tensionTarget, dt * 0.5f);

            float flash = ReducedMotion ? 0f : Flash;
            // stepped flash (1/8 levels): pixel-honest fade, no smooth milk
            flash = Mathf.Floor(Mathf.Clamp01(flash) * 8f + 0.5f) / 8f;
            mat.SetFloat(ZoomId, zoom);
            mat.SetVector(ZoomCenterId, new Vector4(zoomCenter.x, zoomCenter.y, 0f, 0f));
            // shader offset: texture-space px, +y = up in texture; canvas y is down
            mat.SetVector(OffsetId, new Vector4(shakeOffset.x, -shakeOffset.y, 0f, 0f));
            if (OverlayShakes)
                mat.SetVector(OverlayOffsetId, new Vector4(Mathf.Round(shakeOffset.x), -Mathf.Round(shakeOffset.y), 0f, 0f));
            else
                mat.SetVector(OverlayOffsetId, Vector4.zero);
            mat.SetFloat(FlashAmtId, flash);
            mat.SetColor(FlashColId, FlashColor);
            mat.SetFloat(VignetteId, Vignette);
            // the hold tightens the oval: inner radius shrinks as the vignette rises above its baseline
            mat.SetFloat(VigInnerId, 0.32f - Mathf.Clamp(Vignette - vignetteBase, 0f, 0.4f) * 0.5f);
            mat.SetFloat(WarmId, Warm);
            mat.SetFloat(CrushId, Mathf.Clamp(Crush, 0f, 0.2f));
            mat.SetFloat(TensionId, tension);
            // tear: whole-pixel bands, sign flips every frame for a ripped look
            mat.SetFloat(TearId, tearLeft > 0f ? ((PawTime.Frame & 1) == 0 ? 1f : -1f) : 0f);
            mat.SetFloat(ChromaId, chromaLeft > 0f ? 1f : 0f);
            mat.SetFloat(WinceId, winceLeft > 0f ? 0.5f + 0.5f * Mathf.Clamp01(winceLeft / (3f * Tick)) : 0f);
            if (popLeft > 0f)
            {
                // content texel space of the RT (x right, y up)
                float px = popCanvas.x - Visible.x;
                float py = VirtualH - (popCanvas.y - Visible.y);
                float k = Mathf.Clamp01(popLeft / (3f * Tick));
                mat.SetVector(PopId, new Vector4(px, py, popRadius * (1.15f - 0.15f * k), popAmount * (0.4f + 0.6f * k)));
            }
            else mat.SetVector(PopId, Vector4.zero);

            if (tearLeft > 0f) tearLeft -= dt;
            if (chromaLeft > 0f) chromaLeft -= dt;
            if (winceLeft > 0f) winceLeft -= dt;
            if (popLeft > 0f) popLeft -= dt;

            // flash: the peak lasts exactly one frame, then a short stepped fade
            if (Flash > 0.6f) Flash = 0.35f;
            else if (!flashFresh) Flash = Mathf.Max(0f, Flash - dt * 5f);
            flashFresh = false;
        }

        /// <summary>Renders the final upscaled frame into a PNG (review screenshots). Synchronous.</summary>
        public byte[] CapturePng(int width, int height)
        {
            var rt = RenderTexture.GetTemporary(width, height, 16, RenderTextureFormat.ARGB32);
            var prevTarget = DisplayCam.targetTexture;
            WorldCam.Render();
            if (OverlayCam.enabled) OverlayCam.Render();
            DisplayCam.targetTexture = rt;
            DisplayCam.Render();
            DisplayCam.targetTexture = prevTarget;
            var prevActive = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
            tex.Apply(false, false);
            RenderTexture.active = prevActive;
            RenderTexture.ReleaseTemporary(rt);
            var png = tex.EncodeToPNG();
            Destroy(tex);
            return png;
        }

        private void OnDestroy()
        {
            if (Rt != null) { Rt.Release(); Destroy(Rt); }
            if (OverlayRt != null) { OverlayRt.Release(); Destroy(OverlayRt); }
            if (quadMesh != null) Destroy(quadMesh);
            if (mat != null) Destroy(mat);
        }
    }
}
