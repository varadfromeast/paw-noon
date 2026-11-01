using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Beat 1, "PAW ON THE HOLSTER": a warm halftone close-up of the holster. The thumb pad drops onto the grip in
    /// four whole-pixel steps and pats it twice ("PAT" "PAT", soft tap + light haptic, the grip glows on contact), then
    /// rests; Whiskers peeks up from the corner, "comfy.". Loops twice per page.
    /// </summary>
    public sealed class HowToBeatHolster : HowToBeat
    {
        private const int GripX = 110, GripY = 112;
        private const float Pat1 = 0.24f, Pat2 = 0.48f, PeekAt = 0.8f, ComfyAt = 0.98f, LiftAt = 1.45f;
        private static readonly int[] Hover = { 18, 12, 7, 3 };
        // after the first contact: squash 1 px, bounce up, second contact, squash, rest
        private static readonly int[] PatKeys = { -1, 3, 5, 2, -1, 0 };

        private readonly SpriteRenderer backdrop, holster, thumb, peek;
        private readonly SheetDef holsterDef;
        private readonly Sprite[] peeks;
        private readonly BitmapText pat1, pat2, comfy;

        public HowToBeatHolster(HowToStage s) : base(s)
        {
            backdrop = S.NewSprite("Backdrop1", 0, HowToStage.MakeBackdrop(HowToStage.Backdrop.Halftone, 0, 0));
            holsterDef = S.Kit.Ui.TryGet("holster_zone");
            holster = S.NewSprite("Holster1", 3, holsterDef != null ? holsterDef.First : null);
            thumb = S.NewSprite("Thumb1", 7, SwipeCue.ThumbSprite());
            peeks = HowToStage.Peeks("Art/Characters/Hero/hero_portrait", 36);
            peek = S.NewSprite("Whiskers", 5, null);
            pat1 = S.Word("Pat1", "PAT", Pal.Brass1);
            pat2 = S.Word("Pat2", "PAT", Pal.Brass1);
            comfy = S.Note("Comfy", "comfy.", Pal.Ink, TextAlign.Left);
            comfy.Shadow = false;
            comfy.Outline = false;
        }

        /// <summary>Two pats per page.</summary>
        public override float Loop { get { return Mathf.Max(1.65f, S.Tune.beatSeconds * 0.5f); } }

        protected override void SetVisible(bool v)
        {
            backdrop.enabled = v;
            holster.enabled = v && holsterDef != null;
            thumb.enabled = v;
            peek.enabled = false;
            pat1.Visible = false;
            pat2.Visible = false;
            comfy.Visible = false;
        }

        protected override void Events(float prev, float t)
        {
            if (HowToStage.Crossed(prev, t, Pat1)) Pat(0.9f);
            if (HowToStage.Crossed(prev, t, Pat2)) Pat(1.08f);
        }

        private void Pat(float pitch)
        {
            S.Sfx("sfx_ui_tap", 0.75f, pitch);
            HapticMoments.UiTap();
        }

        /// <summary>Thumb height above the grip (px, negative = squashed into it).</summary>
        private int Lift(float t)
        {
            if (t < Pat1) return HowToStage.Steps(Hover, t, Pat1 / Hover.Length);
            if (t < LiftAt) return HowToStage.Steps(PatKeys, t - Pat1, (Pat2 - Pat1) / 4f);
            return t < LiftAt + 0.1f ? 3 : 8;   // lifts off, ready for the next loop
        }

        public override void Pose()
        {
            float t = T;
            S.Put(backdrop, 0, 0);
            int lift = Lift(t);
            if (holsterDef != null)
            {
                // frame 1 = the grip glows while the pad touches it
                int f = lift <= 0 && holsterDef.Count > 1 ? 1 : 0;
                holster.sprite = holsterDef.Frames[f];
                S.Put(holster, GripX - holsterDef.Cell.x / 2 + holsterDef.Pivot.x, GripY - 14 + holsterDef.Pivot.y);
            }
            S.Put(thumb, GripX - 8, GripY - 9 - lift);

            bool w1 = t >= Pat1 && t < 1.2f, w2 = t >= Pat2 && t < 1.2f;
            pat1.Visible = w1;
            pat2.Visible = w2;
            if (w1) S.PutText(pat1, 56, 58 + Pop(t - Pat1));
            if (w2) S.PutText(pat2, 166, 50 + Pop(t - Pat2));

            // Whiskers rises out of the floor in three 12 px steps, then says it
            int step = t < PeekAt ? -1 : Mathf.Min(2, (int)((t - PeekAt) / 0.06f));
            Sprite ps = step >= 0 ? peeks[step] : null;
            peek.enabled = ps != null;
            if (ps != null)
            {
                peek.sprite = ps;
                S.Put(peek, 6, HowToStage.H - (int)ps.rect.height);
            }
            comfy.Visible = t >= ComfyAt && peeks[2] != null;
            if (comfy.Visible) S.PutText(comfy, 46, HowToStage.H - 26);
        }
    }
}
