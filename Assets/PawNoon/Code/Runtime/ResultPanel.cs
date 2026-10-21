using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    public struct RoundView
    {
        public string youName;
        public string botName;
        public long youNs;      // -1 = none
        public long botNs;      // -1 = none
        public bool youFoul;
        public bool youNoShot;
        public bool botFoul;
        public bool frameQuantized;
        public Grade grade;
        public double dealt;
        public double taken;
        /// <summary>Reaction replay strip under the card (SPEC_V2 §8.1); default (invalid) = no strip.</summary>
        public ReplayMarks replay;
    }

    public sealed class ResultPanel : ScreenBase
    {
        private const int O = 220;
        private SpriteRenderer panel;
        private SpriteRenderer card;     // painted parchment (Art/UI/result_card.png), used instead of panel_paper when present
        private SheetDef cardDef;
        private BitmapText youLabel, youTime, botLabel, botTime, gap, dmg, note;
        private BitmapText diffTag;   // "EASY" / "HARD" after the note line (text only, no art)
        private PopSprite stamp;
        private ReplayStrip replay;
        private ShareButton share;   // feel wave 2: share card of this round
        private RectInt rect;
        private float stampDelay;
        private string stampName;
        public string StampSound;
        // slide-in: 6 px from above, ease-out, whole-pixel steps
        private const float SlideSecs = 0.18f;
        private const int SlideFrom = -6;
        private float slideT = SlideSecs;
        private int slideDy;

        protected override void Build()
        {
            cardDef = kit.Ui.TryGet("result_card");
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, layout.ResultPanelSize.x, layout.ResultPanelSize.y));
            if (cardDef != null)
            {
                card = PawGfx.NewSprite("Card", transform, O);
                card.sprite = cardDef.First;
                panel.enabled = false;
            }
            youLabel = InkText("YouL", kit.Medium, O + 2, TextAlign.Left);
            youTime = InkText("YouT", kit.Timer, O + 2, TextAlign.Right);
            youTime.MonoDigits = true;
            botLabel = InkText("BotL", kit.Small, O + 2, TextAlign.Left);
            botTime = InkText("BotT", kit.Medium, O + 2, TextAlign.Right);
            botTime.MonoDigits = true;
            gap = InkText("Gap", kit.Medium, O + 2, TextAlign.Left);
            dmg = InkText("Dmg", kit.Medium, O + 2, TextAlign.Left);
            note = InkText("Note", kit.Small, O + 2, TextAlign.Left);
            diffTag = InkText("Difficulty", kit.Small, O + 2, TextAlign.Left);
            SetDifficulty(false);
            stamp = new PopSprite("Grade", transform, O + 4);
            replay = new ReplayStrip(transform, kit.Small, O + 1);
            share = new ShareButton(kit, transform, O + 3);
        }

        protected override void Layout()
        {
            int w = Mathf.Min(layout.ResultPanelSize.x, pipe.Visible.width - 12);
            int h = layout.ResultPanelSize.y;
            if (cardDef != null) { w = cardDef.Cell.x; h = cardDef.Cell.y; }
            int x = CenterX - w / 2;
            // upper third: below the HUD and round sign, never down over Pawtruth (hat top ~ canvas y 320)
            int y = layout.ResultPanel.y - h / 2;
            y = Mathf.Min(y, 316 - h);
            y = Mathf.Max(y, HudBottom);
            rect = new RectInt(x, y, w, h); // resting rect (stamp anchors to it)
            y += slideDy;                   // everything below follows the slide offset
            if (card != null) PlaceCentered(card, cardDef, x + w / 2, y + h / 2);
            else UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), new RectInt(x, y, w, h));
            // painted card: inner parchment is inset ~16 px from the torn edge
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            youLabel.At(x + ix, y + iy + 2);
            youTime.At(x + w - ix, y + iy - 2);
            botLabel.At(x + ix, y + iy + 20);
            botTime.At(x + w - ix, y + iy + 18);
            gap.At(x + ix, y + iy + 36);
            dmg.At(x + ix, y + iy + 50);
            note.At(x + ix, y + h - iy - 8);
            PlaceTag();
            if (replay != null) replay.PlaceAt(CenterX - ReplayStripMath.Width / 2, y + h + ReplayGapPx);
            // SHARE sits right of the strip, centred on it (fits the 270-px core: 100 + 3 + 30 <= 135)
            if (share != null) share.PlaceAt(CenterX + ReplayStripMath.Width / 2 + 3, y + h + ReplayGapPx + (ReplayStripMath.Height - ShareButton.Size) / 2);
        }

        /// <summary>A share sheet was just opened from this card: the director keeps the card up.</summary>
        public bool ShareHolding { get { return share != null && share.Holding && IsOpen; } }

        private const int ReplayGapPx = 2;

        /// <summary>The replay strip is showing under the card.</summary>
        public bool ReplayVisible { get { return replay != null && replay.Visible && IsOpen; } }

        /// <summary>Canvas y just below the replay strip at rest (after the slide-in).</summary>
        public int ReplayBottom { get { return rect.yMax + ReplayGapPx + ReplayStripMath.Height; } }

        /// <summary>Difficulty the match is played on (shown as a small tag on the card).</summary>
        public void SetDifficulty(bool easy)
        {
            diffTag.SetText(easy ? "EASY" : "HARD");
            diffTag.Color = easy ? Pal.Green2 : Pal.PaintRed;
            diffTag.MarkDirty();
        }

        /// <summary>The tag follows the note text on the same baseline (note width changes per round).</summary>
        private void PlaceTag()
        {
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            int w = Mathf.Min(layout.ResultPanelSize.x, pipe.Visible.width - 12);
            int h = layout.ResultPanelSize.y;
            if (cardDef != null) { w = cardDef.Cell.x; h = cardDef.Cell.y; }
            int x = CenterX - w / 2;
            int y = rect.y + slideDy;
            diffTag.At(x + ix + note.Width + 6, y + h - iy - 8);
        }

        public void Show(RoundView v)
        {
            if (PawSettings.ReducedMotion) { slideT = SlideSecs; slideDy = 0; }
            else { slideT = 0f; slideDy = SlideFrom; }
            Open();
            youLabel.SetText(v.youName);
            botLabel.SetText(v.botName);
            youTime.SetText((v.youFoul || v.youNoShot) ? "--" : FormatSeconds(v.youNs));
            botTime.SetText(v.botFoul ? "FOUL" : FormatSeconds(v.botNs));

            if (v.youFoul) { gap.SetText("TOO EARLY!"); gap.Color = Pal.PaintRed; }
            else if (v.youNoShot) { gap.SetText("NO SHOT"); gap.Color = Pal.PaintRed; }
            else if (v.youNs >= 0 && v.botNs >= 0)
            {
                long g = v.botNs - v.youNs;
                bool faster = g >= 0;
                gap.SetText((faster ? "+" : "-") + NumFmt.Ms2(g) + " ms " + (faster ? "FASTER" : "SLOWER"));
                gap.Color = faster ? Pal.Green2 : Pal.PaintRed;
            }
            else if (v.youNs >= 0) { gap.SetText("CLEAN SHOT!"); gap.Color = Pal.Green2; }
            else { gap.SetText(""); }
            gap.MarkDirty();

            if (v.dealt > 0.0001) { dmg.SetText("HIT -" + Mathf.RoundToInt((float)v.dealt) + " HP"); dmg.Color = Pal.PaintRed; }
            else if (v.taken > 0.0001) { dmg.SetText("TOOK -" + Mathf.RoundToInt((float)v.taken) + " HP"); dmg.Color = Pal.Wood4; }
            else { dmg.SetText("NO DAMAGE"); dmg.Color = Pal.Wood4; }
            dmg.MarkDirty();

            note.SetText(v.frameQuantized ? "FRAME-TIMED INPUT" : "TAP TO CONTINUE");
            PlaceTag();

            stampName = null;
            if (v.youFoul) stampName = "stamp_too_early";
            else if (v.youNoShot) stampName = "stamp_no_shot";
            else if (v.grade != Grade.None) stampName = GradeSprite(v.grade);
            StampSound = v.grade == Grade.Lightning ? "sfx_lightning" : null;
            stamp.Hide();
            stampDelay = 0.35f;
            replay.Show(v.replay, v.youName, v.botName, PawSettings.ReducedMotion);
            ShareLog.Capture(v);
            share.ReleaseHold();
        }

        public static string GradeSprite(Grade g)
        {
            switch (g)
            {
                case Grade.Lightning: return "grade_lightning";
                case Grade.Perfect: return "grade_perfect";
                case Grade.Great: return "grade_great";
                case Grade.Good: return "grade_good";
                case Grade.Slow: return "grade_slow";
                default: return "grade_slowpoke";
            }
        }

        /// <summary>Returns true at the moment the stamp lands (caller plays sfx/haptic).</summary>
        public bool Tick()
        {
            RelayoutIfNeeded();
            float dt = PawTime.UnscaledDt;
            if (slideT < SlideSecs)
            {
                slideT += dt;
                int dy = slideT >= SlideSecs ? 0 : Mathf.RoundToInt(SlideFrom * (1f - Ease.OutCubic(slideT / SlideSecs)));
                if (dy != slideDy) { slideDy = dy; Layout(); }
            }
            stamp.Tick(dt);
            replay.Tick(dt);
            share.Tick(ShareKind.Round, 0);   // before the director's tap-to-continue (it consumes its own press)
            if (stampName != null && stampDelay > 0f)
            {
                stampDelay -= dt;
                if (stampDelay <= 0f)
                {
                    var d = kit.Ui.Get(stampName);
                    stamp.Show(d, new Vector2Int(rect.xMax - 20 - d.Cell.x + d.Pivot.x, rect.yMax - 16 - d.Cell.y + d.Pivot.y), -10f, 0.05f);
                    return true;
                }
            }
            return false;
        }

        public void ShowStampNow()
        {
            stampDelay = 0.0001f;
        }
    }
}
