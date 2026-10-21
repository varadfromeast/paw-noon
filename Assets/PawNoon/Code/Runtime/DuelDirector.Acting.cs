using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// The opponent's presentation-only acting (split from DuelDirector.cs): banter in the speech bubble and the
    /// bot's hold feints / late tell planned by BotActing. Never touches timing, input or ShotReports.
    /// </summary>
    public sealed partial class DuelDirector
    {
        /// <summary>Context for the standoff after a resolved / voided round (reads the last round record).</summary>
        private BanterContext NextBanterContext()
        {
            var r = authority.Round;
            var m = authority.Match;
            bool hasLast = r.roundId > 0 && !r.isVoid;
            bool lightning = hasLast && r.slots[0].outcome == SlotOutcome.Shot && r.slots[0].grade == Grade.Lightning;
            return BanterPicker.Context(false, m.Hp[0], m.Hp[1], cfg.banterLowHp, lightning, prevHeroEarly, hasLast ? r.winnerSlot : -1);
        }

        private SpeechBubble EnsureBubble()
        {
            if (bubble != null) return bubble;
            if (uiCatalog == null) uiCatalog = new SpriteCatalog("Art/UI/", "ui");
            var parent = cues != null ? (cues.transform.parent != null ? cues.transform.parent : cues.transform) : null;
            bubble = SpeechBubble.Create("SpeechBubble", parent, uiCatalog, BitmapFont.Load("font_small", 8), audio, pipe);
            bubble.SetReducedMotion(reducedMotion);
            return bubble;
        }

        private SpriteCatalog UiCatalog
        {
            get
            {
                if (uiCatalog == null) uiCatalog = new SpriteCatalog("Art/UI/", "ui");
                return uiCatalog;
            }
        }

        /// <summary>First canvas y below the duel HUD (portraits + round sign), as ScreenBase.HudBottom.</summary>
        private int HudBottom()
        {
            var ui = UiCatalog;
            var f = ui.TryGet("portrait_frame");
            var rs = ui.TryGet("round_sign");
            return pipe.Safe.y + layout.HudMargin + (f != null ? f.Cell.y : 48) + 2 + (rs != null ? rs.Cell.y : 16) + 4;
        }

        private void Say(string line, float seconds)
        {
            if (string.IsNullOrEmpty(line) || dead[1] || !opponent.HostActing) return;
            var b = EnsureBubble();
            b.MinY = HudBottom();
            var p = bandit.CanvasFromCell(BanterMouthCell);
            b.Show(line, new Vector2Int(Mathf.RoundToInt(p.x), Mathf.RoundToInt(p.y)), false, seconds);
            if (persona != null) audio.PlayMeow(persona.meow);   // his "meow signature" (SPEC_V2 §8.2)
        }

        private void HideBubble()
        {
            if (bubble != null) bubble.Hide();
        }

        private void TickActing(long frameStartNs)
        {
            var r = authority.Round;
            if (State == Pres.Live && r.schedule != null && r.cueDecidedNs < 0 && !r.isVoid && actingRoundId != r.roundId &&
                opponent.Client.InRound && opponent.Client.RoundId == r.roundId)
            {
                actingRoundId = r.roundId;
                if (!opponent.HostActing || opponent.FoulPlanned || dead[1]) acting.Clear();
                else acting.Plan(r.countdownStartNs + r.schedule.CountdownNs, r.countdownStartNs + r.schedule.CueOffsetNs, itch);
            }
            BotActKind k;
            while ((k = acting.Poll(frameStartNs)) != BotActKind.None)
            {
                if (State != Pres.Live || cuePresented || dead[1]) continue;
                if (k == BotActKind.Feint) PresentFeint();
                else PresentTell();
            }
        }

        private void PresentFeint()
        {
            if (dead[1] || cuePresented || banditTelling) return;
            bandit.Feint((float)cfg.feintAnimSpeed);
            audio.Play("sfx_spurs", 0.8f);
            fx.Dust(new Vector2(layout.BanditFeet.x - 6, layout.BanditFeet.y), 71, 1, 6f);
        }

        /// <summary>The late tell: ears flatten a fixed lead before the cue. Silent: a read, not a cue.</summary>
        private void PresentTell()
        {
            if (dead[1] || cuePresented) return;
            banditTelling = true;
            if (tellSheet != null) bandit.Play("tell");
            else
            {
                // no art yet: keep the ready pose and settle 1 px (shoulders drop) so the read still exists
                bandit.Loop("ready");
                bandit.PoseOffset = new Vector2(0f, 1f);
            }
        }

        private void EndTell()
        {
            banditTelling = false;
            bandit.PoseOffset = Vector2.zero;
        }
    }
}
