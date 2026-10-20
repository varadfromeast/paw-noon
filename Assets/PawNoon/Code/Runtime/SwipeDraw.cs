using System;
using System.Collections.Generic;
using PawNoon.Core;
using UnityEngine;
#if PAWNOON_INPUT_SYSTEM
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif

namespace PawNoon.Runtime
{
    /// <summary>
    /// Swipe-to-draw (SPEC §11). A draw is an upward swipe that starts inside the holster zone (bottom
    /// band of the safe area) and travels <see cref="ThresholdPx"/> screen pixels upward. The shot time
    /// is the moment the swipe crosses the threshold, interpolated between the two OS touch samples that
    /// straddle it, on the stopwatch clock. Taps and sideways swipes are ignored. Resting a finger in the
    /// zone is allowed; the zone glows while a finger rests.
    ///
    /// Sources (in priority order): Input System touch/mouse events (OS timestamps), legacy Input as a
    /// frame-quantized fallback, Space bar = instant valid draw (editor/desktop testing), Inject() for
    /// review automation. Everything lands in the same TimedPress queue used by DuelDirector.
    /// </summary>
    public sealed class SwipeDraw : IDisposable
    {
        private struct Sample { public long tNs; public float y; }

        private sealed class Track
        {
            public int id;
            public bool inZone;
            public bool crossed;
            public float startX, startY;
            public Sample prev;
            public bool hasPrev;
        }

        private readonly IDuelClock clock;
        private readonly Queue<TimedPress> queue = new Queue<TimedPress>(8);
        private readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>(4);
        private readonly List<int> dead = new List<int>(4);
        private int lastPreciseFrame = -100;
        private bool enabled;
        private Track mouseTrack;

        /// <summary>Fraction of screen height the swipe must travel (SPEC: 0.12).</summary>
        public float ThresholdFraction = 0.12f;
        /// <summary>Holster zone: fraction of the safe-area height from the bottom (SPEC: ~0.25).</summary>
        public float ZoneFraction = 0.25f;
        /// <summary>Reject swipes that drift more sideways than upward.</summary>
        public float MaxSidewaysRatio = 1.0f;

        /// <summary>True while at least one finger rests in the holster zone (for the grip glow).</summary>
        public bool FingerResting { get; private set; }
        /// <summary>Progress 0..1 of the most advanced live swipe toward the threshold (for the arrow hint).</summary>
        public float Progress { get; private set; }
        public long LastOffsetNs { get; private set; }
        public bool HasPreciseTimestamps
        {
            get
            {
#if PAWNOON_INPUT_SYSTEM
                return true;
#else
                return false;
#endif
            }
        }

        public float ThresholdPx { get { return Mathf.Max(8f, Screen.height * ThresholdFraction); } }

#if PAWNOON_INPUT_SYSTEM
        private Action<InputEventPtr, InputDevice> onEventHandler;
#endif

        public SwipeDraw(IDuelClock clock) { this.clock = clock; }

        public void Enable()
        {
            if (enabled) return;
            enabled = true;
#if PAWNOON_INPUT_SYSTEM
            try
            {
                onEventHandler = OnInputEvent;
                InputSystem.onEvent += onEventHandler;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] Input System hooks failed, using legacy input: " + e.Message);
            }
#endif
        }

        public void Dispose()
        {
            if (!enabled) return;
            enabled = false;
#if PAWNOON_INPUT_SYSTEM
            try { if (onEventHandler != null) InputSystem.onEvent -= onEventHandler; } catch (Exception) { }
#endif
        }

        // ------------------------------------------------------------------ zone geometry

        private float ZoneTopY()
        {
            var sa = Screen.safeArea; // bottom-left origin, pixels
            return sa.yMin + sa.height * ZoneFraction;
        }

        public bool InZone(Vector2 screenPos)
        {
            var sa = Screen.safeArea;
            return screenPos.y >= sa.yMin - 2f && screenPos.y <= ZoneTopY() && screenPos.x >= sa.xMin && screenPos.x <= sa.xMax;
        }

        // ------------------------------------------------------------------ gesture core (shared by all sources)

        private void Begin(Track t, long tNs, Vector2 pos)
        {
            t.inZone = InZone(pos);
            t.crossed = false;
            t.startX = pos.x; t.startY = pos.y;
            t.prev = new Sample { tNs = tNs, y = pos.y };
            t.hasPrev = true;
        }

        /// <summary>Feeds a position sample; returns true (and the interpolated crossing time) on the draw.</summary>
        private bool Move(Track t, long tNs, Vector2 pos, out long crossNs)
        {
            crossNs = 0;
            if (!t.inZone || t.crossed || !t.hasPrev) { t.prev = new Sample { tNs = tNs, y = pos.y }; return false; }
            float dy = pos.y - t.startY;
            float dx = Mathf.Abs(pos.x - t.startX);
            float th = ThresholdPx;
            float p = Mathf.Clamp01(dy / th);
            if (p > Progress) Progress = p;
            if (dy >= th)
            {
                if (dx > dy * MaxSidewaysRatio) { t.inZone = false; return false; } // sideways: not a draw
                // interpolate the crossing between the previous and this sample
                float prevDy = t.prev.y - t.startY;
                float span = dy - prevDy;
                float k = span > 1e-3f ? Mathf.Clamp01((th - prevDy) / span) : 1f;
                long dt = tNs - t.prev.tNs;
                crossNs = t.prev.tNs + (long)(dt * k);
                if (crossNs > tNs) crossNs = tNs;
                t.crossed = true;
                return true;
            }
            t.prev = new Sample { tNs = tNs, y = pos.y };
            return false;
        }

        private void Push(long tNs, InputSource src, bool quantized)
        {
            if (!quantized) lastPreciseFrame = Time.frameCount;
            if (queue.Count > 16) queue.Dequeue();
            queue.Enqueue(new TimedPress { tNs = tNs, source = src, frameQuantized = quantized });
        }

        // ------------------------------------------------------------------ Input System path

#if PAWNOON_INPUT_SYSTEM
        private long ToStopwatch(double eventTime)
        {
            long swNow = clock.NowNs;
            double isNow = InputState.currentTime;
            long offset = swNow - TimeConv.SecondsToNs(isNow);
            LastOffsetNs = offset;
            long t = TimeConv.SecondsToNs(eventTime) + offset;
            return t > swNow ? swNow : t;
        }

        private void OnInputEvent(InputEventPtr eventPtr, InputDevice device)
        {
            if (!eventPtr.IsA<StateEvent>() && !eventPtr.IsA<DeltaStateEvent>()) return;
            long tNs = ToStopwatch(eventPtr.time);
            float v;
            var ts = device as Touchscreen;
            if (ts != null)
            {
                // every touch control in the event: read phase/position from the event, not the device state
                var touches = ts.touches;
                for (int i = 0; i < touches.Count; i++)
                {
                    var tc = touches[i];
                    int id;
                    float fid;
                    if (!tc.touchId.ReadValueFromEvent(eventPtr, out fid)) continue;
                    id = (int)fid;
                    if (id == 0) continue;
                    float ph;
                    if (!tc.phase.ReadValueFromEvent(eventPtr, out ph)) continue;
                    var phase = (UnityEngine.InputSystem.TouchPhase)(int)ph;
                    Vector2 pos;
                    if (!tc.position.ReadValueFromEvent(eventPtr, out pos)) continue;
                    HandleTouch(id, phase, pos, tNs);
                }
                return;
            }
            var mouse = device as Mouse;
            if (mouse != null)
            {
                Vector2 pos;
                if (!mouse.position.ReadValueFromEvent(eventPtr, out pos)) pos = mouse.position.ReadValue();
                bool down = mouse.leftButton.ReadValueFromEvent(eventPtr, out v) ? v > 0.5f : mouse.leftButton.isPressed;
                HandleMouse(down, pos, tNs);
                return;
            }
            var kb = device as Keyboard;
            if (kb != null)
            {
                if (kb.spaceKey.ReadValueFromEvent(eventPtr, out v) && v > 0.5f && !kb.spaceKey.isPressed)
                    Push(tNs, InputSource.Keyboard, false);
            }
        }

        private void HandleTouch(int id, UnityEngine.InputSystem.TouchPhase phase, Vector2 pos, long tNs)
        {
            Track t;
            switch (phase)
            {
                case UnityEngine.InputSystem.TouchPhase.Began:
                    if (!tracks.TryGetValue(id, out t)) { t = new Track { id = id }; tracks[id] = t; }
                    Begin(t, tNs, pos);
                    break;
                case UnityEngine.InputSystem.TouchPhase.Moved:
                case UnityEngine.InputSystem.TouchPhase.Stationary:
                    if (tracks.TryGetValue(id, out t))
                    {
                        long cross;
                        if (Move(t, tNs, pos, out cross)) Push(cross, InputSource.Touch, false);
                    }
                    break;
                case UnityEngine.InputSystem.TouchPhase.Ended:
                case UnityEngine.InputSystem.TouchPhase.Canceled:
                    if (tracks.TryGetValue(id, out t))
                    {
                        long cross;
                        if (Move(t, tNs, pos, out cross)) Push(cross, InputSource.Touch, false);
                        tracks.Remove(id);
                    }
                    break;
            }
        }

        private void HandleMouse(bool down, Vector2 pos, long tNs)
        {
            if (down)
            {
                if (mouseTrack == null) { mouseTrack = new Track { id = -1 }; Begin(mouseTrack, tNs, pos); }
                else
                {
                    long cross;
                    if (Move(mouseTrack, tNs, pos, out cross)) Push(cross, InputSource.Mouse, false);
                }
            }
            else if (mouseTrack != null)
            {
                long cross;
                if (Move(mouseTrack, tNs, pos, out cross)) Push(cross, InputSource.Mouse, false);
                mouseTrack = null;
            }
        }
#endif

        // ------------------------------------------------------------------ legacy / per-frame

        /// <summary>
        /// Call at frame start. Updates resting/progress state and, when the Input System delivered nothing
        /// recently, runs the gesture on legacy Input (frame-quantized timestamps).
        /// </summary>
        public void BeginFrame(long frameStartNs)
        {
            bool precise = Time.frameCount - lastPreciseFrame <= 2;
#if PAWNOON_INPUT_SYSTEM
            bool legacy = false; // the Input System path owns gestures when the package is present
#else
            bool legacy = true;
#endif
            bool resting = false;
            float prog = 0f;
            if (UiPointer.LegacyDead) legacy = false; // Active Input Handling = Input System only: legacy Input throws
            if (legacy)
            {
                for (int i = 0; i < Input.touchCount; i++)
                {
                    var tc = Input.GetTouch(i);
                    Track t;
                    if (tc.phase == UnityEngine.TouchPhase.Began)
                    {
                        if (!tracks.TryGetValue(tc.fingerId, out t)) { t = new Track { id = tc.fingerId }; tracks[tc.fingerId] = t; }
                        Begin(t, frameStartNs, tc.position);
                    }
                    else if (tracks.TryGetValue(tc.fingerId, out t))
                    {
                        long cross;
                        if (Move(t, frameStartNs, tc.position, out cross) && !precise) Push(cross, InputSource.Touch, true);
                        if (tc.phase == UnityEngine.TouchPhase.Ended || tc.phase == UnityEngine.TouchPhase.Canceled) dead.Add(tc.fingerId);
                    }
                }
                for (int i = 0; i < dead.Count; i++) tracks.Remove(dead[i]);
                dead.Clear();
                if (Input.touchCount == 0)
                {
                    bool down = Input.GetMouseButton(0);
                    HandleMouseLegacy(down, Input.mousePosition, frameStartNs, precise);
                }
                if (Input.GetKeyDown(KeyCode.Space) && !precise) Push(frameStartNs, InputSource.Keyboard, true);
            }
            // resting / progress read-outs (both paths)
            foreach (var kv in tracks) { if (kv.Value.inZone && !kv.Value.crossed) resting = true; }
            if (mouseTrack != null && mouseTrack.inZone && !mouseTrack.crossed) resting = true;
            if (!UiPointer.LegacyDead)
            {
                try
                {
                    if (Input.touchCount == 0 && Input.GetMouseButton(0) && InZone(Input.mousePosition)) resting = true;
                    for (int i = 0; i < Input.touchCount; i++) if (InZone(Input.GetTouch(i).position)) resting = true;
                }
                catch (InvalidOperationException) { UiPointer.LegacyDead = true; }
            }
            FingerResting = resting;
            if (!resting && tracks.Count == 0 && mouseTrack == null) Progress = 0f;
            Progress = Mathf.Max(prog, Progress);
        }

        private void HandleMouseLegacy(bool down, Vector2 pos, long tNs, bool precise)
        {
            if (down)
            {
                if (mouseTrack == null) { mouseTrack = new Track { id = -1 }; Begin(mouseTrack, tNs, pos); }
                else
                {
                    long cross;
                    if (Move(mouseTrack, tNs, pos, out cross) && !precise) Push(cross, InputSource.Mouse, true);
                }
            }
            else if (mouseTrack != null) mouseTrack = null;
        }

        /// <summary>Review hook: a completed valid draw at tNs (same queue and report path).</summary>
        public void Inject(long tNs, InputSource src)
        {
            queue.Enqueue(new TimedPress { tNs = tNs, source = src, frameQuantized = false });
        }

        public bool TryDequeue(out TimedPress p)
        {
            if (queue.Count > 0) { p = queue.Dequeue(); return true; }
            p = default(TimedPress);
            return false;
        }

        public void Clear()
        {
            queue.Clear();
            Progress = 0f;
        }

        /// <summary>
        /// Forgets every live gesture (touch tracks and the mouse track) without touching queued presses.
        /// Called when the app is paused or loses focus: an Ended / Canceled event may never arrive for a
        /// finger that was down at that moment, and its track would keep the holster glowing forever
        /// (REVIEW_2026-10-20 D3). Presentation only: no timestamps, thresholds or queued shots change.
        /// </summary>
        public void ResetGestures()
        {
            tracks.Clear();
            dead.Clear();
            mouseTrack = null;
            FingerResting = false;
            Progress = 0f;
        }
    }
}
