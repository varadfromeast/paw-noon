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
        /// <summary>Both shots inside the tie window (verdict "DEAD HEAT").</summary>
        public bool tie;
        /// <summary>Win streak to show as the brass tag ("x2"); 0 = no tag (Docs/FEEL.md "Outcome moments").</summary>
        public int streak;
        /// <summary>This exchange set a new all-time best: NEW BEST! ribbon + confetti over the time.</summary>
        public bool newBest;
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
        // outcome moments (Docs/FEEL.md): verdict headline (the "Gap" text), streak pennant, NEW BEST ribbon + confetti
        private SheetDef streakDef, bestDef;
        private SpriteRenderer streakTag, bestTag;
        private BitmapText streakText, bestText;
        private PixelParticles confetti;
        private string plusMinus = "+-";
        private int streakValue;
        private bool newBestValue;
        private float tagClock = 99f;        // seconds since Show (the stamp lands at StampAt)
        private bool streakFired, bestFired;
        private float streakPopT = 99f, bestPopT = 99f;
        private int streakDy, bestDy;
        private RectInt stampRect;           // resting canvas rect of this card's stamp (empty = none)
        private readonly char[] streakBuf = new char[8];
        private const float StampAt = 0.35f;
        private const float TagStep = 0.04f;
        /// <summary>Length of the NEW BEST haptic (HapticMoments.NewBest: taps to 0.14 s, swell to 0.41 s) plus a breath.</summary>
        private const float NewBestSeconds = 0.5f;
        private static readonly int[] TagPop = { -5, -2, 0, -1, 0 };
        /// <summary>Wide stamps (TOO EARLY!) land across the times band instead of the bottom-right corner.</summary>
        private const int WideStamp = 100;
        /// <summary>The streak tag landed (streak value): the director plays sfx_streak + the haptic step.</summary>
        public System.Action<int> OnStreakTag;
        /// <summary>The NEW BEST ribbon landed: the director plays sfx_new_best + the haptic.</summary>
        public System.Action OnNewBest;
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
            Glyph pm;
            if (kit.Medium != null && !kit.Medium.Fallback && kit.Medium.TryGet('\u00B1', out pm) && pm.adv > 0) plusMinus = "\u00B1";
            // streak pennant (brass, "x2") and NEW BEST ribbon (SigYellow): 9-slice tags sized to their text
            streakDef = kit.Ui.TryGet("tag_streak");
            bestDef = kit.Ui.TryGet("tag_best");
            if (streakDef != null) { streakTag = kit.Panel("tag_streak", transform, O + 5, new RectInt(0, 0, streakDef.Cell.x, streakDef.Cell.y)); streakTag.enabled = false; }
            if (bestDef != null) { bestTag = kit.Panel("tag_best", transform, O + 5, new RectInt(0, 0, bestDef.Cell.x, bestDef.Cell.y)); bestTag.enabled = false; }
            streakText = InkText("StreakText", kit.Medium, O + 6, TextAlign.Left);
            streakText.Visible = false;
            bestText = InkText("BestText", kit.Small, O + 6, TextAlign.Left);
            bestText.SetText("NEW BEST!");
            bestText.Visible = false;
            // confetti over the card: its own small pool on the overlay (the world particles zoom, shake and sit
            // under the UI), unscaled so the kill slow-mo never drags it
            var cgo = new GameObject("Confetti");
            cgo.transform.SetParent(transform, false);
            cgo.transform.position = Vector3.zero;   // children are placed at canvas->world coordinates
            confetti = cgo.AddComponent<PixelParticles>();
            confetti.Unscaled = true;
            confetti.Init(FeelConfig.ConfettiMax, null);
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
            PlaceOutcomeTags();
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
            HideOutcomeTags();
            stampName = null;
            if (v.youFoul) stampName = "stamp_too_early";
            else if (v.youNoShot) stampName = "stamp_no_shot";
            else if (v.grade != Grade.None) stampName = GradeSprite(v.grade);
            Open();   // lays out: rect is the resting card rect from here on
            stampRect = StampRect(stampName != null ? kit.Ui.TryGet(stampName) : null);
            youLabel.SetText(v.youName);
            botLabel.SetText(v.botName);
            youTime.SetText((v.youFoul || v.youNoShot) ? "--" : FormatSeconds(v.youNs));
            botTime.SetText(v.botFoul ? "FOUL" : FormatSeconds(v.botNs));
            ShowVerdict(v);

            if (v.dealt > 0.0001) { dmg.SetText("HIT -" + Mathf.RoundToInt((float)v.dealt) + " HP"); dmg.Color = Pal.PaintRed; }
            else if (v.taken > 0.0001) { dmg.SetText("TOOK -" + Mathf.RoundToInt((float)v.taken) + " HP"); dmg.Color = Pal.Wood4; }
            else { dmg.SetText("NO DAMAGE"); dmg.Color = Pal.Wood4; }
            dmg.MarkDirty();

            note.SetText(v.frameQuantized ? "FRAME-TIMED INPUT" : "TAP TO CONTINUE");
            PlaceTag();

            StampSound = v.grade == Grade.Lightning ? "sfx_lightning" : null;
            stamp.Hide();
            stampDelay = StampAt;
            streakValue = v.streak;
            newBestValue = v.newBest;
            tagClock = 0f;
            if (streakValue > 0)
            {
                int n = 0;
                streakBuf[n++] = 'x';
                n = NumFmt.WriteInt(streakValue, streakBuf, n);
                streakText.SetChars(streakBuf, n);
            }
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
            TickOutcomeTags(dt);
            replay.Tick(dt);
            share.Tick(ShareKind.Round, 0);   // before the director's tap-to-continue (it consumes its own press)
            if (stampName != null && stampDelay > 0f)
            {
                stampDelay -= dt;
                if (stampDelay <= 0f)
                {
                    var d = kit.Ui.Get(stampName);
                    var sr = StampRect(d);
                    stamp.Show(d, new Vector2Int(sr.x + d.Pivot.x, sr.y + d.Pivot.y), -10f, 0.05f);
                    return true;
                }
            }
            return false;
        }

        public void ShowStampNow()
        {
            stampDelay = 0.0001f;
            tagClock = StampAt;   // the tags keep their offsets from the stamp
        }

        public override void Close()
        {
            HideOutcomeTags();
            base.Close();
        }

        // ------------------------------------------------------------------ outcome moments (Docs/FEEL.md)

        /// <summary>Resting rect of a stamp on this card (grade / NO SHOT: bottom-right; wide TOO EARLY!: across the times).</summary>
        private RectInt StampRect(SheetDef d)
        {
            if (d == null) return new RectInt(0, 0, 0, 0);
            int iy = card != null ? 14 : 10;
            if (d.Cell.x > WideStamp) return new RectInt(rect.xMax - 16 - d.Cell.x, rect.y + iy - 4, d.Cell.x, d.Cell.y);
            return new RectInt(rect.xMax - 20 - d.Cell.x, rect.yMax - 16 - d.Cell.y, d.Cell.x, d.Cell.y);
        }

        /// <summary>
        /// The verdict headline in the old gap row: the longest form that fits left of the stamp (it lands on top of the
        /// row 0.35 s later and would hide the gap), else the card's inner width. Colour by tone.
        /// </summary>
        private void ShowVerdict(RoundView v)
        {
            var kind = RoundVerdict.Kind(v.youFoul, v.youNoShot, v.tie, v.youNs, v.botNs);
            bool hasGap = v.youNs >= 0 && v.botNs >= 0;
            long gapNs = hasGap ? v.youNs - v.botNs : 0;
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            int textX = rect.x + ix;
            int rowTop = rect.y + iy + 36, rowBottom = rowTop + gap.Height;
            int maxW = rect.width - ix * 2;
            if (stampRect.width > 0 && stampRect.y < rowBottom && stampRect.yMax > rowTop)
                maxW = Mathf.Min(maxW, stampRect.x - 3 - textX);
            for (int f = RoundVerdict.Long; f <= RoundVerdict.Minimal; f++)
            {
                gap.SetText(RoundVerdict.Text(kind, f, gapNs, hasGap, v.botName, plusMinus));
                if (gap.Width <= maxW) break;
            }
            switch (RoundVerdict.Tone(kind))
            {
                case VerdictTone.Good: gap.Color = Pal.Green2; break;
                case VerdictTone.Bad: gap.Color = Pal.PaintRed; break;
                case VerdictTone.Even: gap.Color = Pal.Brass2; break;
                default: gap.Color = Pal.Ink; break;
            }
            gap.MarkDirty();
        }

        private void HideOutcomeTags()
        {
            streakValue = 0;
            newBestValue = false;
            streakFired = bestFired = false;
            streakPopT = bestPopT = 99f;
            streakDy = bestDy = 0;
            tagClock = 99f;
            if (streakTag != null) streakTag.enabled = false;
            if (bestTag != null) bestTag.enabled = false;
            if (streakText != null) streakText.Visible = false;
            if (bestText != null) bestText.Visible = false;
            if (confetti != null) confetti.ClearBursts();
        }

        private void TickOutcomeTags(float dt)
        {
            if (tagClock < 50f) tagClock += dt;
            var feel = FeelConfig.Current;
            bool moved = false;
            if (newBestValue && !bestFired && tagClock >= StampAt + feel.newBestDelay)
            {
                bestFired = true;
                bestPopT = PawSettings.ReducedMotion ? 99f : 0f;
                if (bestTag != null) bestTag.enabled = true;
                bestText.Visible = true;
                moved = true;
                if (!PawSettings.ReducedMotion && feel.confettiCount > 0)
                {
                    var r = BestRect();
                    confetti.Confetti(new Vector2(r.x + r.width / 2, r.y + r.height / 2), O + 7, feel.confettiCount,
                        Pal.SigYellow, Pal.Red1, feel.confettiSeconds, feel.confettiSpeed, feel.confettiGravity);
                }
                if (OnNewBest != null) OnNewBest();
            }
            // with a NEW BEST on the same card the streak waits until the ribbon's haptic swell is over (they would blur)
            float streakAt = StampAt + feel.streakTagDelay;
            if (newBestValue) streakAt = Mathf.Max(streakAt, StampAt + feel.newBestDelay + NewBestSeconds);
            if (streakValue > 0 && !streakFired && tagClock >= streakAt)
            {
                streakFired = true;
                streakPopT = PawSettings.ReducedMotion ? 99f : 0f;
                if (streakTag != null) streakTag.enabled = true;
                streakText.Visible = true;
                moved = true;
                if (OnStreakTag != null) OnStreakTag(streakValue);
            }
            int sdy = PopOffset(ref streakPopT, dt), bdy = PopOffset(ref bestPopT, dt);
            if (sdy != streakDy || bdy != bestDy) { streakDy = sdy; bestDy = bdy; moved = true; }
            if (moved) PlaceOutcomeTags();
        }

        /// <summary>Whole-pixel drop-in: -5, -2, land, -1, land (TagStep each). 0 once done.</summary>
        private static int PopOffset(ref float t, float dt)
        {
            if (t >= 50f) return 0;
            t += dt;
            int i = (int)(t / TagStep);
            if (i >= TagPop.Length) { t = 99f; return 0; }
            return TagPop[i];
        }

        /// <summary>NEW BEST ribbon: over the card's top edge, right-aligned above the time (follows the slide-in).</summary>
        private RectInt BestRect()
        {
            int ix = card != null ? 18 : 10;
            int pad = bestDef != null ? bestDef.Cell.x - 2 : 6;          // 9-slice ends ~ text inset on both sides
            int w = bestText.Width + pad;
            int h = bestDef != null ? bestDef.Cell.y : 11;
            return new RectInt(rect.xMax - ix + 6 - w, rect.y + slideDy - 6, w, h);
        }

        /// <summary>Streak pennant: pinned to the stamp's lower-left edge (no stamp: the card's bottom-right corner).</summary>
        private RectInt StreakRect()
        {
            int w = 8 + streakText.Width + 3 + 6;
            int h = streakDef != null ? streakDef.Cell.y : 13;
            if (stampRect.width > 0 && stampRect.width <= WideStamp)
                return new RectInt(stampRect.x + 5 - w, stampRect.yMax - 2 - h, w, h);
            int ix = card != null ? 18 : 10, iy = card != null ? 14 : 10;
            return new RectInt(rect.xMax - ix - w, rect.yMax - iy - h + slideDy, w, h);
        }

        private void PlaceOutcomeTags()
        {
            if (bestText != null && bestText.Visible)
            {
                var r = BestRect();
                r.y += bestDy;
                if (bestTag != null) UiKit.SetRect(bestTag, bestDef, r);
                bestText.At(r.x + (r.width - bestText.Width) / 2, r.y + 2);
            }
            if (streakText != null && streakText.Visible)
            {
                var r = StreakRect();
                r.y += streakDy;
                if (streakTag != null) UiKit.SetRect(streakTag, streakDef, r);
                streakText.At(r.x + 9, r.y + 3);
            }
        }
    }
}
