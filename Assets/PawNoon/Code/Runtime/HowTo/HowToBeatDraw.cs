using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Beat 3, "DRAW! SWIPE UP": a sunburst behind Pawtruth (idle frame, bust crop). Standoff with the thumb on the
    /// glowing grip; DRAW! pops (sfx_draw, one white frame) and the real SwipeCue flicks the thumb up its chevrons
    /// while a little font_timer counts; at reactionMs: BANG (gun sfx, muzzle flash, recoil haptic, scene flash +
    /// shake), the timer freezes brass, the holster shows empty, and Pawtruth's hat (with his ears) hops off his head,
    /// lands crooked-ly back, a flash ring at the crown, an indignant meow, a sweat drop and smoke from the hat hole.
    /// </summary>
    public sealed class HowToBeatDraw : HowToBeat
    {
        private const int GripX = 52, GripY = 126;
        private const int BustX = 128, BustY = HowToStage.H - 64;
        private const int HatRows = 25, BodyRows = 39, BustW = 87, BustCropX = 14;
        private const int TimerX = 98, TimerY = 72;
        private static readonly int[] HatHop = { 4, 8, 10, 11, 10, 8, 5, 0, 2, 0 };
        private static readonly Color32 Fur = new Color32(79, 61, 51, 255);   // Pawtruth's fur (bandit_idle), under the hat

        private readonly SpriteRenderer backdrop, holster, hat, body, dome, draw, muzzle, ring, smoke, sweat;
        private readonly SheetDef holsterDef, drawDef, muzzleDef, ringDef, smokeDef, sweatDef;
        private readonly SwipeCue cue;
        private readonly BitmapText bang, timer;
        private readonly char[] timerBuf = new char[8];
        private int shownMs = -1;
        private bool shownShot;
        private bool goMode;

        public HowToBeatDraw(HowToStage s) : base(s)
        {
            backdrop = S.NewSprite("Backdrop3", 0, HowToStage.MakeBackdrop(HowToStage.Backdrop.Sunburst, BustX + 44, BustY + 28));
            const string idle = "Art/Characters/Bandit/bandit_idle";
            dome = S.Block("HatGap", 2, Fur);
            body = S.NewSprite("PawtruthBody", 3, HowToStage.Crop(idle, BustCropX, HatRows, BustW, BodyRows));
            hat = S.NewSprite("PawtruthHat", 4, HowToStage.Crop(idle, BustCropX, 0, BustW, HatRows));
            holsterDef = S.Kit.Ui.TryGet("holster_zone");
            holster = S.NewSprite("Holster3", 3, holsterDef != null ? holsterDef.First : null);
            cue = new SwipeCue(S.Root, S.Order + 6);
            drawDef = S.Kit.Ui.TryGet("cue_draw");
            draw = S.NewSprite("Draw", 9, drawDef != null ? drawDef.First : null);
            muzzleDef = S.Fx.TryGet("muzzle_side");
            muzzle = S.NewSprite("Muzzle", 8, null);
            ringDef = S.Fx.TryGet("flash_ring");
            ring = S.NewSprite("HatRing", 8, null);
            smokeDef = S.Fx.TryGet("smoke_puff");
            smoke = S.NewSprite("HatSmoke", 5, null);
            sweatDef = S.Fx.TryGet("sweat_drop");
            sweat = S.NewSprite("Sweat", 8, null);
            bang = S.Word("Bang", "BANG", Pal.SigYellow);
            timer = BitmapText.Create("HowToTimer", S.Root, S.Kit.Timer, S.Order + 10, TextAlign.Center);
            timer.MonoDigits = true;
            timer.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            timer.Visible = false;
        }

        private float DrawAt { get { return S.Tune.drawAtSeconds; } }
        private float BangAt { get { return S.Tune.drawAtSeconds + S.Tune.reactionMs / 1000f; } }
        public override float Loop { get { return S.Tune.drawLoopSeconds; } }

        protected override void SetVisible(bool v)
        {
            backdrop.enabled = v;
            holster.enabled = v && holsterDef != null;
            body.enabled = v && body.sprite != null;
            hat.enabled = v && hat.sprite != null;
            dome.enabled = false;
            draw.enabled = false;
            muzzle.enabled = false;
            ring.enabled = false;
            smoke.enabled = false;
            sweat.enabled = false;
            bang.Visible = false;
            timer.Visible = false;
            if (!v) cue.Show(false);
        }

        protected override void Restart()
        {
            // the standoff: thumb resting on the grip; the Go flick starts at DRAW
            goMode = T >= DrawAt && T < BangAt + 0.6f;
            cue.SetMode(goMode ? SwipeCue.Mode.Go : SwipeCue.Mode.Wait);
            cue.Show(T < BangAt + 0.6f);
            shownMs = -1;
        }

        protected override void Advance(float dt) { cue.Tick(dt); }

        protected override void Events(float prev, float t)
        {
            if (HowToStage.Crossed(prev, t, DrawAt))
            {
                S.Sfx("sfx_draw");
                cue.SetMode(SwipeCue.Mode.Go);
                goMode = true;
                HapticMoments.Swipe();
            }
            if (HowToStage.Crossed(prev, t, BangAt))
            {
                if (S.Audio != null) S.Audio.PlayVariant("sfx_gun_hero", 3);
                HapticMoments.HeroShot();
                S.ShakeRequest = 0.16f;
                S.FlashRequest = S.Tune.flashFrames;
            }
            if (HowToStage.Crossed(prev, t, BangAt + 0.45f) && S.Audio != null) S.Audio.PlayMeow("sfx_meow_pawtruth");
            if (HowToStage.Crossed(prev, t, BangAt + 0.6f)) cue.Show(false);
        }

        public override void Pose()
        {
            float t = T;
            float sinceBang = t - BangAt;
            bool shot = sinceBang >= 0f;
            S.Put(backdrop, 0, 0);

            if (holsterDef != null)
            {
                // 1 = grip glowing under the thumb, 2 = drawn (empty, dust)
                int f = shot ? Mathf.Min(2, holsterDef.Count - 1) : (holsterDef.Count > 1 ? 1 : 0);
                holster.sprite = holsterDef.Frames[f];
                S.Put(holster, GripX - holsterDef.Cell.x / 2 + holsterDef.Pivot.x, GripY - 14 + holsterDef.Pivot.y);
            }
            cue.Place(S.X + GripX, S.Y + GripY, S.Y + 60);
            cue.Tick(0f);   // re-applies the cue at this frame's panel offset (slam / shake)

            // Pawtruth: the hat (ears and all) hops off and drops back; his fur fills the gap under it
            int hop = shot && sinceBang < HatHop.Length * 0.04f ? HowToStage.Steps(HatHop, sinceBang, 0.04f) : 0;
            dome.enabled = hop > 0 && body.sprite != null;
            if (dome.enabled) S.PutBlock(dome, BustX + 33, BustY + HatRows - hop - 1, 30, hop + 2);
            S.Put(body, BustX, BustY + HatRows);
            S.Put(hat, BustX, BustY - hop);
            bool white = shot && sinceBang < 0.034f && !S.Reduced;
            if (body.sprite != null) { S.Flash(body, white); S.Flash(hat, white); }

            // DRAW! pops at its time (a whole-pixel drop, one white frame) and goes when the shot lands
            bool drawOn = t >= DrawAt && !shot && drawDef != null;
            draw.enabled = drawOn;
            if (drawOn)
            {
                S.Put(draw, HowToStage.W / 2, 37 + Pop(t - DrawAt) * 2);
                S.Flash(draw, t - DrawAt < 0.034f && !S.Reduced);
            }

            // the tiny reaction timer: counts from DRAW, freezes brass at the shot
            bool timerOn = t >= DrawAt;
            timer.Visible = timerOn;
            if (timerOn)
            {
                int ms = Mathf.Clamp(Mathf.FloorToInt((t - DrawAt) * 1000f), 0, S.Tune.reactionMs);
                if (shot) ms = S.Tune.reactionMs;
                if (ms != shownMs || shot != shownShot)
                {
                    shownMs = ms;
                    shownShot = shot;
                    timerBuf[0] = '0'; timerBuf[1] = '.';
                    timerBuf[2] = (char)('0' + ms / 100 % 10);
                    timerBuf[3] = (char)('0' + ms / 10 % 10);
                    timerBuf[4] = (char)('0' + ms % 10);
                    timerBuf[5] = ' '; timerBuf[6] = 's';
                    timer.SetChars(timerBuf, 7);
                    timer.Color = shot ? Pal.Brass0 : Pal.Cream0;
                    timer.MarkDirty();
                }
                S.PutText(timer, TimerX, TimerY);
            }

            S.Frame(muzzle, muzzleDef, HowToStage.OneShot(muzzleDef, t, BangAt), GripX + 16, GripY - 12);
            S.Frame(ring, ringDef, HowToStage.OneShot(ringDef, t, BangAt), BustX + 50, BustY + 6);
            S.Frame(smoke, smokeDef, HowToStage.OneShot(smokeDef, t, BangAt + 0.5f), BustX + 58, BustY - 2);
            S.Frame(sweat, sweatDef, HowToStage.OneShot(sweatDef, t, BangAt + 0.45f), BustX + 70, BustY + 30);
            bool bangOn = shot && sinceBang < 0.8f;
            bang.Visible = bangOn;
            if (bangOn) S.PutText(bang, 98, 98 + Pop(sinceBang));
        }
    }
}
