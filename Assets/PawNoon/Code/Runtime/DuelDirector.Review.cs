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
            killPunchPending = false;
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

        public void ReviewRoundResult() { ReviewRoundResult(null); }

        /// <summary>
        /// Round card still. <paramref name="variant"/> = space-separated tokens: a verdict (faster [default], beaten,
        /// deadheat / tie, early / foul, noshot, clean), "streakN" / "xN" (streak tag, e.g. streak3: the tag, its sfx and
        /// haptic, and "ON A ROLL" from streakRollMin) and "newbest" (ribbon + confetti + sfx + haptic). Sample values;
        /// nothing is recorded (the all-time best is untouched).
        /// </summary>
        public void ReviewRoundResult(string variant)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(1);
            string kind = "faster";
            int run = 0;
            bool best = false;
            var tokens = (variant ?? "").ToLowerInvariant().Split(new[] { ' ', ',' }, System.StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < tokens.Length; i++)
            {
                string t = tokens[i];
                int n;
                if (t == "newbest" || t == "best") best = true;
                else if (t.StartsWith("streak", System.StringComparison.Ordinal) && int.TryParse(t.Substring(6), out n)) run = n;
                else if (t.StartsWith("x", System.StringComparison.Ordinal) && int.TryParse(t.Substring(1), out n)) run = n;
                else kind = t;
            }
            long you = 214382000L, botNs = 288105000L;
            var v = new RoundView();
            v.youName = "YOU";
            v.botName = OpponentName;
            switch (kind)
            {
                case "beaten": you = 291044000L; botNs = 260112000L; break;
                case "deadheat":
                case "tie": you = 251004000L; botNs = 248112000L; v.tie = true; break;
                case "early":
                case "foul": v.youFoul = true; you = -1; break;
                case "noshot": v.youNoShot = true; you = -1; break;
                case "clean": v.botFoul = true; you = 231400000L; botNs = -1; break;
            }
            v.youNs = you;
            v.botNs = botNs;
            v.grade = you >= 0 ? Grading.FromMs(cfg, DuelConfig.NsToMs(you)) : Grade.None;
            // nobody is dropped in these samples, so both shots land (the card shows HIT when you dealt any)
            v.dealt = you >= 0 ? DamageCurve.Evaluate(cfg, DuelConfig.NsToMs(you)) : 0;
            v.taken = botNs >= 0 ? DamageCurve.Evaluate(cfg, DuelConfig.NsToMs(botNs)) : 0;
            v.streak = run;
            v.newBest = best;
            hud.SetHpImmediate(0, (float)((cfg.maxHp - v.taken) / cfg.maxHp));
            hud.SetHpImmediate(1, (float)((cfg.maxHp - v.dealt) / cfg.maxHp));
            hero.Play(you >= 0 ? "aim" : (v.youFoul ? "foul" : "ready"));
            if (v.dealt > 0) bandit.Play("hit", 3); else bandit.Play("aim");
            if (you >= 0) cues.SetTimer(you, true);
            stampHaptic = StampHaptics.For(v.youFoul, v.youNoShot, v.grade);
            v.replay = you >= 0 && botNs >= 0 ? ReplayMarks.Shots(you, botNs, (float)cfg.replayStripSeconds) : default(ReplayMarks);
            panel.Show(v);
            if (v.tie)
            {
                var dmgText = PanelText("Dmg");
                if (dmgText != null)
                {
                    dmgText.SetText("HIT -" + Mathf.RoundToInt((float)v.dealt) + "  TOOK -" + Mathf.RoundToInt((float)v.taken) + " HP");
                    dmgText.Color = Pal.PaintRed;
                    dmgText.MarkDirty();
                }
            }
            panel.ShowStampNow();
        }

        /// <summary>
        /// Review still of the kill-shot punch: Pawtruth reeling from the match-deciding hit, the white flash held for
        /// <paramref name="holdFrames"/> frames (in play it is killPunchFlashFrames, normally one) and the camera kick.
        /// Reduced Motion shows the pose without flash or kick, as in play.
        /// </summary>
        public void ReviewKillPunch(int holdFrames)
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(3);
            hud.SetHpImmediate(0, 0.62f);
            hud.SetHpImmediate(1, 0f);
            hero.Play("aim");
            bandit.Play("down", 1);
            if (reducedMotion) return;
            bandit.FlashFrames(Mathf.Max(1, holdFrames));
            pipe.Kick(new Vector2(0f, FeelConfig.Current.killPunchKickPx));
        }

        /// <summary>Review still: the standoff after a 3-win run: "ON A ROLL" on the round sign and a rattled line.</summary>
        public void ReviewRattled()
        {
            EnterReview();
            hud.SetVisible(true);
            hud.SetRound(4);
            hud.SetHpImmediate(0, 1f);
            hud.SetHpImmediate(1, 0.1f);
            hero.Play("ready");
            bandit.Play("ready");
            hud.ShowHolster(true);
            hud.FlashRoundLabel(OnARollLabel, 6f);
            Say(banter.Line(OpponentLines.rattled), 6f);
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
