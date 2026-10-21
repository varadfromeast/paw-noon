using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Deterministic review stills and the bridge status line (split from DuelDirector.cs; ReviewDirector entry points).
    /// </summary>
    public sealed partial class DuelDirector
    {
        public void EnterReview()
        {
            State = Pres.Review;
            intro.Cancel();
            pipe.SetTension(0f);
            PawTime.ClearEffects();
            pipe.Zoom = 1f;
            pipe.Vignette = baseVignette;
            vignettePulseT = 99f;
            acting.Clear();
            EndTell();
            HideBubble();
            EndLastLight();
            banterLine = null;
            sweatT = -1f;
            fallStage[0] = fallStage[1] = 0;
            audio.Duck(1f);
            audio.Heartbeat(0f);
            cues.HideAll();
            panel.Close();
            dead[0] = dead[1] = false;
            hero.ResetPose();
            bandit.ResetPose();
            hud.ShowHint(false);
        }

        public void ReviewCountdown(int numeral)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            hero.Play("ready");
            bandit.Play("ready");
            cues.ShowNumeral(numeral);
            hud.ShowHolster(true);
            hud.ShowHint(true);
        }

        public void ReviewDraw(long timerNs)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 1f);
            hero.Play("ready");
            bandit.Play("ready");
            cues.ShowDraw();
            cues.SetTimer(timerNs, false);
        }

        public void ReviewFoul()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(2);
            hud.SetHpImmediate(0, 0.76f);
            hud.SetHpImmediate(1, 0.66f);
            hero.Play("foul");
            bandit.Play("ready");
            cues.ShowStamp("stamp_too_early");
        }

        public void ReviewRoundResult()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            long you = 214382000L, botNs = 288105000L;
            double dealt = DamageCurve.Evaluate(cfg, DuelConfig.NsToMs(you));
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, (float)((cfg.maxHp - dealt) / cfg.maxHp));
            hero.Play("aim");
            bandit.Play("hit", 3);
            cues.SetTimer(you, true);
            var v = new RoundView();
            v.youName = "YOU";
            v.botName = OpponentName;
            v.youNs = you;
            v.botNs = botNs;
            v.grade = Grading.FromMs(cfg, DuelConfig.NsToMs(you));
            v.dealt = dealt;
            stampHaptic = StampHaptics.For(false, false, v.grade);
            v.replay = ReplayMarks.Shots(you, botNs, (float)cfg.replayStripSeconds);
            panel.Show(v);
            panel.ShowStampNow();
        }

        public void ReviewMatchEnd(bool win)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(4);
            hud.SetHpImmediate(0, win ? 0.52f : 0f);
            hud.SetHpImmediate(1, win ? 0f : 0.37f);
            if (win) { hero.Play("win"); bandit.Play("down", 99); }
            else { hero.Play("down", 99); bandit.Play("win"); }
        }

        public string StatusLine()
        {
            var m = authority.Match;
            var r = authority.Round;
            string you = r.slots[0].reactionNs >= 0 ? NumFmt.Seconds6(r.slots[0].reactionNs) : "--";
            string b = r.slots[1].reactionNs >= 0 ? NumFmt.Seconds6(r.slots[1].reactionNs) : "--";
            return "pres=" + State + " phase=" + authority.Phase + " round=" + m.RoundNumber +
                   " hp=" + Mathf.RoundToInt((float)m.Hp[0]) + "/" + Mathf.RoundToInt((float)m.Hp[1]) +
                   " wins=" + m.RoundWins[0] + "-" + m.RoundWins[1] +
                   " last=" + you + "s vs " + b + "s" +
                   " outcomes=" + r.slots[0].outcome + "/" + r.slots[1].outcome +
                   " tie=" + r.isTie +
                   " over=" + m.Over + " winner=" + m.Winner +
                   " autoTapMs=" + (autoTapNs < 0 ? "off" : (autoTapNs / 1e6).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)) +
                   " seed=" + seed + " rejected=" + authority.RejectedReports +
                   " lastLight=" + lastLight + " dmgMult=" + authority.Round.damageMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   "/next " + authority.PendingDamageMultiplier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                   (rules != null ? " pvp=attempts:" + rules.Attempts + ",resolved:" + rules.Resolved + ",end:" + RecordedMatchRules.ToWire(rules.EndReason) : "");
        }
    }
}
