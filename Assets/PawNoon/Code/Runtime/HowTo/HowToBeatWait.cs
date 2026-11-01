using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Beat 2, "HEARTBEAT = WAIT": night hatch, a mini heart monitor in a brass bezel with the duel's WAIT word thumping
    /// on every R peak (heartbeat sfx + the hold haptic), the thumb resting on the glowing holster. At jamAtSeconds the
    /// thumb twitches up 3 / 6 px: puff, TOO EARLY! stamp, "JAM", misfire + foul haptic, a comic shake; then it settles
    /// back. Pawtruth peeks from the corner the whole time and rises for a smug "heh." after the jam.
    /// </summary>
    public sealed class HowToBeatWait : HowToBeat
    {
        private const int GripX = 110, GripY = 122;
        private const int BezelX = 46, BezelY = 8, BezelW = 128, BezelH = 50;
        private const int EcgW = 118, EcgH = 40;
        private const float ThumpSeconds = 0.14f;
        // twitch (before the jam), jammed, settle
        private static readonly int[] Settle = { 8, 8, 8, 8, 8, 8, 8, 8, 8, 5, 2, 0 };

        private readonly SpriteRenderer backdrop, bezel, bezelInk, holster, thumb, puff, stamp, peek;
        private readonly SheetDef holsterDef, puffDef, stampDef;
        private readonly Sprite[] peeks;
        private readonly EcgMonitor ecg;
        private readonly BitmapText wait, jam, heh;
        private float lastBeat = -9f;

        public HowToBeatWait(HowToStage s) : base(s)
        {
            backdrop = S.NewSprite("Backdrop2", 0, HowToStage.MakeBackdrop(HowToStage.Backdrop.Hatch, 0, 0));
            bezel = S.Block("Bezel", 1, Pal.Brass2);
            bezelInk = S.Block("BezelInk", 2, Pal.Ink);
            ecg = new EcgMonitor(S.Root, S.Order + 3, EcgW, EcgH);
            ecg.OnBeat = OnBeat;
            wait = S.Word("Wait", "WAIT", Pal.Cream0);
            holsterDef = S.Kit.Ui.TryGet("holster_zone");
            holster = S.NewSprite("Holster2", 3, holsterDef != null ? holsterDef.First : null);
            thumb = S.NewSprite("Thumb2", 7, SwipeCue.ThumbSprite());
            puffDef = S.Fx.TryGet("smoke_puff");
            puff = S.NewSprite("JamPuff", 8, null);
            stampDef = S.Kit.Ui.TryGet("stamp_too_early");
            stamp = S.NewSprite("TooEarly", 9, stampDef != null ? stampDef.First : null);
            jam = S.Word("Jam", "JAM", Pal.SigRed);
            peeks = HowToStage.Peeks("Art/Characters/Bandit/bandit_portrait", 36);
            peek = S.NewSprite("Pawtruth2", 5, null);
            heh = S.Note("Heh", "heh.", Pal.Cream0, TextAlign.Center);
        }

        private float Jam { get { return S.Tune.jamAtSeconds; } }

        public override float Loop { get { return Mathf.Max(S.Tune.beatSeconds, Jam + 1.6f); } }

        private void OnBeat(int index, float frenzy, bool skipped)
        {
            if (!On) return;
            lastBeat = T;
            S.Sfx("sfx_heartbeat", 0.9f);
            HapticMoments.HoldBeat(frenzy, skipped);
        }

        protected override void SetVisible(bool v)
        {
            backdrop.enabled = v;
            bezel.enabled = v;
            bezelInk.enabled = v;
            ecg.SetVisible(v);
            wait.Visible = v;
            holster.enabled = v && holsterDef != null;
            thumb.enabled = v;
            puff.enabled = false;
            stamp.enabled = false;
            jam.Visible = false;
            peek.enabled = false;
            heh.Visible = false;
        }

        protected override void Restart()
        {
            // a fresh, calm heart at the top of every loop
            ecg.SetVisible(false);
            ecg.SetVisible(true);
            lastBeat = -9f;
        }

        protected override void Advance(float dt) { ecg.Tick(dt); }

        protected override void Events(float prev, float t)
        {
            if (HowToStage.Crossed(prev, t, Jam))
            {
                S.Sfx("sfx_misfire");
                S.Sfx("sfx_stamp", 0.6f);
                HapticMoments.Foul();
                S.ShakeRequest = 0.24f;
            }
        }

        private int Lift(float t)
        {
            if (t < Jam - 0.1f) return 0;
            if (t < Jam - 0.05f) return 3;
            if (t < Jam) return 6;
            return HowToStage.Steps(Settle, t - Jam, 0.05f);
        }

        public override void Pose()
        {
            float t = T;
            S.Put(backdrop, 0, 0);
            S.PutBlock(bezel, BezelX, BezelY, BezelW, BezelH);
            S.PutBlock(bezelInk, BezelX + 1, BezelY + 1, BezelW - 2, BezelH - 2);
            ecg.PlaceAt(S.X + BezelX + (BezelW - EcgW) / 2, S.Y + BezelY + (BezelH - EcgH) / 2);
            // WAIT thumps with the heart (yellow + 1 px down); Reduced Motion keeps it still and cream
            bool thump = t - lastBeat < ThumpSeconds && t >= lastBeat;
            bool jammed = t >= Jam && t < Jam + 1.1f;
            Color32 wc = jammed ? Pal.SigRed : (thump && !S.Reduced ? Pal.SigYellow : Pal.Cream0);
            if (!HowToStage.Same(wait.Color, wc)) { wait.Color = wc; wait.MarkDirty(); }
            S.PutText(wait, BezelX + BezelW / 2, BezelY + (BezelH - S.Loud.LineHeight) / 2 + (thump && !S.Reduced ? 1 : 0));

            int lift = Lift(t);
            if (holsterDef != null)
            {
                holster.sprite = holsterDef.Frames[lift <= 0 && holsterDef.Count > 1 ? 1 : 0];
                S.Put(holster, GripX - holsterDef.Cell.x / 2 + holsterDef.Pivot.x, GripY - 14 + holsterDef.Pivot.y);
            }
            S.Put(thumb, GripX - 8, GripY - 9 - lift);

            S.Frame(puff, puffDef, HowToStage.OneShot(puffDef, t, Jam), GripX - 6, GripY - 6);
            stamp.enabled = jammed && stampDef != null;
            if (stamp.enabled) S.Put(stamp, 110, 76 + Pop(t - Jam));
            jam.Visible = jammed;
            if (jammed) S.PutText(jam, 172, 96 + Pop(t - Jam - 0.08f));

            // Pawtruth side-eyes from the corner (two thirds up), all the way up for "heh." after the jam
            int step = t < 0.3f ? -1 : (t < 0.36f ? 0 : 1);
            bool smug = t >= Jam + 0.3f && t < Jam + 1.4f;
            if (smug) step = 2;
            Sprite ps = step >= 0 ? peeks[step] : null;
            peek.enabled = ps != null;
            if (ps != null)
            {
                peek.sprite = ps;
                S.Put(peek, HowToStage.W - 42, HowToStage.H - (int)ps.rect.height);
            }
            heh.Visible = smug && ps != null;
            if (heh.Visible) S.PutText(heh, HowToStage.W - 24, HowToStage.H - 48);
        }
    }
}
