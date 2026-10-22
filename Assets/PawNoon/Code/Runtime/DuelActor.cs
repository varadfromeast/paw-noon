using System;
using System.Collections.Generic;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>A character on screen: strip animation, white flash, pixel-snapped offsets.</summary>
    public sealed class DuelActor : MonoBehaviour
    {
        public CharacterDef Def;
        public SpriteRenderer Sr;
        public SpriteAnimator Anim;
        /// <summary>Canvas point where the cell pivot sits.</summary>
        public Vector2Int Anchor;
        /// <summary>Extra canvas offset (body fall, recoil), in px.</summary>
        public Vector2 Offset;
        /// <summary>Small held pose shift (e.g. the procedural tell fallback), in px. Cleared by ResetPose.</summary>
        public Vector2 PoseOffset;
        /// <summary>Fired when the current animation reaches its fire frame.</summary>
        public Action OnFire;

        private MaterialPropertyBlock mpb;
        private float flashLeft;
        private float flashTotal;
        private float shakeAmp;
        private float shakeT;
        private float lastFlash = -1f;
        private Vector2 impulse;
        private float impulseT;
        private float impulseTotal;

        public static DuelActor Create(string name, CharacterDef def, Vector2Int anchor, int order, Transform parent)
        {
            var sr = PawGfx.NewSprite(name, parent, order);
            var a = sr.gameObject.AddComponent<DuelActor>();
            a.Def = def;
            a.Sr = sr;
            a.Anchor = anchor;
            a.Anim = sr.gameObject.AddComponent<SpriteAnimator>();
            a.Anim.Renderer = sr;
            a.Anim.OnFire = a.HandleFire;
            a.mpb = new MaterialPropertyBlock();
            a.Play("idle");
            a.Snap();
            return a;
        }

        private void HandleFire()
        {
            if (OnFire != null) OnFire();
        }

        public SheetDef Sheet(string anim) { return Def.Anim(anim); }

        public void Play(string anim, int startFrame = 0)
        {
            var s = Def.Anim(anim);
            if (s != null)
            {
                Anim.Speed = 1f;
                Anim.Play(s, startFrame, true);
            }
        }

        /// <summary>
        /// Loads an optional animation that CharacterDef does not require (e.g. "tell"): manifest entry
        /// anims.&lt;anim&gt; if present, else file "&lt;prefix&gt;_&lt;anim&gt;" (frames = texture width / cell width).
        /// Returns null, silently, when the texture does not exist; callers fall back to another anim.
        /// </summary>
        public SheetDef LoadOptionalAnim(string manifestName, string prefix, string anim)
        {
            var existing = Def.Anim(anim);
            if (existing != null) return existing.Missing ? null : existing;
            var root = ArtLib.Json(Def.Folder + manifestName, false);
            var entry = PnJson.Obj(PnJson.Obj(root, "anims"), anim);
            string file = PnJson.Str(entry, "file", prefix + "_" + anim);
            var tex = ArtLib.TryTexture(Def.Folder + file);
            if (tex == null) return null;
            var e = new Dictionary<string, object>();
            if (entry != null) foreach (var kv in entry) e[kv.Key] = kv.Value;
            e["file"] = file;
            if (!e.ContainsKey("cell")) e["cell"] = new List<object> { (double)Def.Cell.x, (double)Def.Cell.y };
            if (!e.ContainsKey("pivot")) e["pivot"] = new List<object> { (double)Def.Pivot.x, (double)Def.Pivot.y };
            if (!e.ContainsKey("frames")) e["frames"] = (double)Mathf.Max(1, tex.width / Mathf.Max(1, Def.Cell.x));
            if (!e.ContainsKey("fps")) e["fps"] = 8.0;
            if (!e.ContainsKey("loop")) e["loop"] = false;
            var sheet = ArtLib.Sheet(Def.Folder, anim, e, Def.Cell, Def.Pivot, false);
            Def.Anims[anim] = sheet;
            return sheet;
        }

        /// <summary>Plays a looping anim only if it is not already playing.</summary>
        public void Loop(string anim)
        {
            var s = Def.Anim(anim);
            if (s != null && !Anim.IsPlaying(s)) Anim.Play(s, 0, true);
        }

        public bool IsPlaying(string anim)
        {
            var s = Def.Anim(anim);
            return s != null && Anim.IsPlaying(s);
        }

        /// <summary>Paw flick toward the holster: the first frames of draw_fire, reverting before the shot.</summary>
        public void Feint(float speed = 1f)
        {
            var draw = Def.Anim("draw_fire");
            var ready = Def.Anim("ready");
            if (draw == null || ready == null) return;
            int stopAt = draw.FireFrame > 0 ? draw.FireFrame : Mathf.Min(2, draw.Count - 1);
            if (stopAt < 1) stopAt = 1;
            Anim.PlayPartial(draw, stopAt, ready);
            Anim.Speed = Mathf.Max(0.1f, speed);
        }

        /// <summary>
        /// Decaying positional impulse (gun recoil, hit knock-back) in canvas px (y down). Starts at full
        /// displacement and eases back to rest over the given seconds of presentation time (frozen by
        /// hit-stop, stretched by slow-mo). Snapped to whole pixels.
        /// </summary>
        public void Impulse(Vector2 px, float seconds)
        {
            impulse = px;
            impulseTotal = Mathf.Max(0.01f, seconds);
            impulseT = 0f;
        }

        private Vector2 ImpulseNow()
        {
            if (impulseTotal <= 0f || impulseT >= impulseTotal) return Vector2.zero;
            float k = 1f - impulseT / impulseTotal;
            k = k * k; // fast snap back, soft settle
            return impulse * k;
        }

        public void Flash(float seconds)
        {
            flashTotal = seconds;
            flashLeft = seconds;
        }

        /// <summary>Solid white for exactly <paramref name="frames"/> rendered frames (no step-down), e.g. the kill-shot punch.</summary>
        public void FlashFrames(int frames)
        {
            if (frames > flashFrames) flashFrames = frames;
        }

        private int flashFrames;

        public void Shake(float px)
        {
            shakeAmp = Mathf.Max(shakeAmp, px);
            shakeT = 0f;
        }

        public Vector2 CanvasFromCell(Vector2Int cell)
        {
            return new Vector2(Anchor.x + Offset.x + (cell.x - Def.Pivot.x), Anchor.y + Offset.y + (cell.y - Def.Pivot.y));
        }

        public Vector2 HitPointCanvas { get { return CanvasFromCell(Def.HitPoint); } }

        public bool TryMuzzleCanvas(out Vector2 canvas)
        {
            canvas = Vector2.zero;
            var s = Anim.Current;
            Vector2Int m;
            if (s == null || !s.TryMuzzle(Anim.Frame, out m))
            {
                // fallback: the aim pose's muzzle (fire before the anim reached a gun frame)
                var aim = Def.Anim("aim");
                if (aim == null || !aim.TryMuzzle(0, out m)) return false;
            }
            canvas = CanvasFromCell(m);
            return true;
        }

        public void ResetPose()
        {
            Offset = Vector2.zero;
            PoseOffset = Vector2.zero;
            impulse = Vector2.zero;
            impulseTotal = 0f;
            shakeAmp = 0f;
            flashLeft = 0f;
            flashFrames = 0;
            if (Anim != null) Anim.Speed = 1f;
            Snap();
        }

        private void Snap()
        {
            float sx = 0f;
            if (shakeAmp > 0.05f) sx = Mathf.Sign(Mathf.Sin(shakeT * 90f)) * Mathf.Round(shakeAmp);
            var imp = ImpulseNow();
            float x = Anchor.x + Offset.x + PoseOffset.x + Mathf.Round(imp.x) + sx;
            float y = Anchor.y + Offset.y + PoseOffset.y + Mathf.Round(imp.y);
            transform.position = Px.WorldSnap(x, y);
        }

        private void Update()
        {
            float udt = PawTime.UnscaledDt;
            if (impulseTotal > 0f && impulseT < impulseTotal) impulseT += PawTime.Dt;
            if (shakeAmp > 0f)
            {
                shakeT += udt;
                shakeAmp = Mathf.Max(0f, shakeAmp - udt * 18f);
            }
            float f = 0f;
            if (flashFrames > 0)
            {
                flashFrames--;
                f = 1f;
            }
            else if (flashLeft > 0f)
            {
                flashLeft -= udt;
                // hold full white for the first half, then step down in two pixel-honest levels
                float k = flashTotal > 0f ? flashLeft / flashTotal : 0f;
                f = k > 0.5f ? 1f : (k > 0.2f ? 0.6f : (k > 0f ? 0.3f : 0f));
            }
            if (!Mathf.Approximately(f, lastFlash))
            {
                lastFlash = f;
                Sr.GetPropertyBlock(mpb);
                mpb.SetFloat(PawGfx.FlashId, f);
                Sr.SetPropertyBlock(mpb);
            }
            Snap();
        }
    }
}
