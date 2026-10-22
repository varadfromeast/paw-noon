using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Shot presentation (split from DuelDirector.cs): the hero's draw / foul, muzzle flashes, queued impacts, the
    /// tie beat and hits. Driven by authority events and the actors' fire frames; presentation only.
    /// </summary>
    public sealed partial class DuelDirector
    {
        /// <summary>One bead of sweat rolling off the side of the hero's head (seen from behind).</summary>
        private void SweatDrop()
        {
            if (dead[0] || cuePresented) return;
            // cell px of the right side of his head, under the hat brim (hero cell 295x275, pivot at the feet)
            var p = hero.CanvasFromCell(new Vector2Int(173, 60));
            fx.Burst(fx.Fx("sweat_drop"), p, 92, new Vector2(2f, 6f), 70f, 0.9f);
        }

        private void PresentHeroDraw()
        {
            heroPredictedFire = true;
            audio.Play(heroDrawSfx, 0.9f);
            hud.SetDrawn(true);
            // holster shows empty (frame 2) until the next standoff
            hero.Play("draw_fire");
            restAnim[0] = "aim";
            var d = hero.Sheet("draw_fire");
            if (d == null || d.FireFrame < 0) HeroFired();
            else HapticMoments.Swipe();   // the recoil follows at the fire frame
        }

        private void PresentHeroFoul()
        {
            if (heroFoulShown) return;
            heroFoulShown = true;
            heroFouledThisRound = true;
            hero.Play("foul");
            restAnim[0] = "ready";
            cues.ShowNumeral(0);
            cues.ShowStamp("stamp_too_early");
            if (State != Pres.Tutorial) cues.ShowMessage("WAIT FOR DRAW!", 1.8f);   // coaching line under the stamp (the tutorial card says it)
            audio.Play("sfx_misfire");                 // the dry jam click
            audio.Play("sfx_cock", 0.35f, 0.8f);       // hammer catching, low and dull
            HapticMoments.Foul();
        }

        private void HeroFired()
        {
            if (fired[0] || dead[0]) return;
            fired[0] = true;
            Vector2 m;
            if (!hero.TryMuzzleCanvas(out m)) m = new Vector2(layout.HeroBottomLeft.x + 150, layout.HeroBottomLeft.y - 250);
            muzzleAt[0] = m;
            lastFiredSlot = 0;
            float rm = reducedMotion ? 0.5f : 1f;
            float rk = (float)cfg.heroRecoilPx * rm;
            hero.Impulse(new Vector2(-rk, rk * 0.67f), (float)cfg.recoilSeconds);   // gun kicks back and down-left
            fx.Burst(HeroMuzzle, m, 96, Vector2.zero);
            fx.Sparks(m, 97, 3);
            fx.Smoke(m, 95, 2);
            fx.Shell(new Vector2(m.x - 18, m.y + 16), 96, m.y + 70);
            pipe.FlashScreen(0.22f * heroFlashBoost, heroFlashBoost > 1f ? new Color(0.85f, 0.95f, 1f) : new Color(1f, 0.93f, 0.7f));
            pipe.AddShake((float)cfg.shakeShot);
            pipe.Kick(new Vector2(-1.5f, 1f));
            audio.PlayVariant(heroShotSfx, 3);
            audio.Play("sfx_shell", 0.4f);
            HapticMoments.HeroShot();
            if (pendingImpact[0]) Land(0);
        }

        private void BanditFired()
        {
            if (fired[1] || dead[1]) return;
            fired[1] = true;
            Vector2 m;
            if (!bandit.TryMuzzleCanvas(out m)) m = bandit.HitPointCanvas + new Vector2(-10, 4);
            muzzleAt[1] = m;
            lastFiredSlot = 1;
            bandit.Impulse(new Vector2(0f, reducedMotion ? 0f : -1f), (float)cfg.recoilSeconds);
            fx.Burst(fx.Fx("muzzle_front"), m, 76, Vector2.zero);
            fx.Sparks(m, 76, 2);
            fx.Smoke(m, 74, 2);
            pipe.FlashScreen(0.3f, new Color(1f, 0.95f, 0.75f));
            pipe.AddShake((float)cfg.shakeShot * 0.6f);
            if (!reducedMotion)
            {
                vignettePulse = (float)cfg.banditShotVignette;
                vignettePulseT = 0f;
            }
            audio.PlayVariant("sfx_gun_bandit", 3);
            if (pendingImpact[1]) Land(1);
        }

        private void QueueShot(DuelEvent e)
        {
            int s = e.slot;
            pendingImpact[s] = true;
            pendingEvent[s] = e;
            pendingAge[s] = 0f;
            if (e.simultaneous) tieRound = true;
            var shooter = s == 0 ? hero : bandit;
            if (fired[s]) { Land(s); return; }
            var draw = shooter.Sheet("draw_fire");
            if (s == 0)
            {
                if (!heroPredictedFire) PresentHeroDraw();
            }
            else
            {
                EndTell();
                restAnim[1] = "aim";
                int start = draw != null && draw.FireFrame > 0 ? draw.FireFrame - 1 : 0;
                bandit.Play("draw_fire", start);
                if (draw == null || draw.FireFrame < 0) BanditFired();
            }
        }

        /// <summary>A shot's impact is due (its shooter fired). In a tie both impacts wait for the second trigger pull.</summary>
        private void Land(int s)
        {
            if (!pendingImpact[s]) return;
            if (tieRound)
            {
                if (fired[0] && fired[1]) LandTie();
                return;
            }
            ApplyImpact(s);
        }

        /// <summary>Both tied shots hit at the moment of the second shot, then the near-miss beat.</summary>
        private void LandTie()
        {
            if (tieLanded) return;
            tieLanded = true;
            int first = lastFiredSlot == 0 ? 1 : 0;   // the shooter whose flash is already fading
            if (pendingImpact[0]) ApplyImpact(0);
            if (pendingImpact[1]) ApplyImpact(1);
            TieDrama(first);
        }

        private void TieDrama(int firstShooter)
        {
            // both muzzle flashes hang in the air together: re-light the earlier one under the slow-mo
            if (fired[firstShooter])
            {
                if (firstShooter == 0) fx.Burst(HeroMuzzle, muzzleAt[0], 96, Vector2.zero);
                else fx.Burst(fx.Fx("muzzle_front"), muzzleAt[1], 76, Vector2.zero);
            }
            float secs = (float)cfg.tieSlowMoSeconds;
            PawTime.SlowMo((float)cfg.tieSlowMoScale, secs);
            // cfg.tieDesaturate is unused until PixelPipeline grows a desaturation control (the reflection probe
            // that looked for one was removed in the 21 Oct cleanup: the pipeline never had it)
            if (UiCatalog.Has("stamp_too_close")) cues.ShowStamp("stamp_too_close");
            else cues.ShowMessage("TOO CLOSE!", secs + 0.9f);
            audio.Play("sfx_lightning");
            HapticMoments.Tie();
        }

        private void ApplyImpact(int shooterSlot)
        {
            if (!pendingImpact[shooterSlot]) return;
            pendingImpact[shooterSlot] = false;
            var e = pendingEvent[shooterSlot];
            int victim = 1 - shooterSlot;
            var v = victim == 0 ? hero : bandit;
            var hit = v.HitPointCanvas;
            if (victim == 1) EndTell();
            float rm = reducedMotion ? 0.5f : 1f;
            v.Flash(0.12f);
            if (victim == 1)
            {
                float kb = (float)cfg.knockBanditPx * rm;          // pushed away from the camera, up-right
                v.Impulse(new Vector2(kb * 0.8f, -kb * 0.6f), (float)cfg.knockSeconds);
            }
            else
            {
                float kh = (float)cfg.knockHeroPx * rm;            // pushed back toward the camera
                v.Impulse(new Vector2(-kh * 0.34f, kh), (float)cfg.knockSeconds);
            }
            fx.Burst(fx.Fx("hit_puff"), hit, victim == 0 ? 97 : 76, Vector2.zero);
            fx.Burst(fx.Fx("flash_ring"), hit, victim == 0 ? 96 : 75, Vector2.zero);
            fx.Sparks(hit, victim == 0 ? 97 : 76, 4);
            fx.Pixels(hit, victim == 0 ? 97 : 76, 6, victim == 0 ? Pal.Sand0 : Pal.Steel1, 70f, 160f);
            PawTime.HitStop((float)cfg.hitStopMs / 1000f);
            pipe.AddShake((float)cfg.shakeHit);
            audio.Play("sfx_hit");
            audio.Play("sfx_hit_cat", 0.9f, victim == 0 ? 1.08f : 0.92f);
            audio.Play("sfx_hp_tick", 0.5f);
            if (victim == 1) HapticMoments.HitDealt(e.killed);
            else HapticMoments.HitTaken(e.killed);
            if (victim == 0) pipe.HeroHurt(); else pipe.PopAt(hit);
            hud.Hit(victim, (float)(e.targetHpAfter / cfg.maxHp));
            if (e.killed)
            {
                // killing blow: reel in slow-mo, then fall; dust + body-fall land with the body (TickFalls)
                dead[victim] = true;
                fallStage[victim] = 1;
                if (victim == 1) killPunchPending = true;   // fires after the slow-mo (TickKillPunch)
                fallT[victim] = 0f;
                v.Play("hit");
                PawTime.SlowMo((float)cfg.killSlowMoScale, (float)cfg.killSlowMoSeconds);
                pipe.PushTo(hit, 0.02f, (float)cfg.killSlowMoSeconds + 0.25f);
            }
            else
            {
                v.Play("hit");
            }
        }
    }
}
