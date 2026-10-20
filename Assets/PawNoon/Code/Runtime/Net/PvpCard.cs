using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// The PVP card (M5): CASUAL | RANKED segmented toggle (seg sprites, as the title's EASY | HARD) and FIND RIVAL.
    /// RANKED is greyed with "SIGN IN TO RANK" until the account is linked (SupabaseAuth.IsLinked, M6).
    /// </summary>
    public sealed class PvpCard : M5Card
    {
        public enum Action { None, Back, Find, LockedTap }

        private const int W = 196;
        private const int H = 150;
        private const int SegW = 74;
        private const int SegH = 20;

        private SpriteRenderer segCasual, segRanked;
        private BitmapText casualText, rankedText, desc1, desc2, lockLine;
        private PixelButton find;
        private RectInt casualRect, rankedRect;
        private bool ranked;
        private bool linked;
        private float lockFlash;

        public bool Ranked { get { return ranked; } }

        protected override void Build()
        {
            BuildFrame("panel_wood", false);
            title.SetText("PVP DUEL");
            segCasual = PawGfx.NewSprite("SegCasual", transform, O + 1);
            segRanked = PawGfx.NewSprite("SegRanked", transform, O + 1);
            casualText = Line("CasualT", kit.Small, Pal.Cream0);
            casualText.SetText("CASUAL");
            rankedText = Line("RankedT", kit.Small, Pal.Cream0);
            rankedText.SetText("RANKED");
            desc1 = Line("Desc1", kit.Small, Pal.Cream1);
            desc2 = Line("Desc2", kit.Small, Pal.Sand0);
            lockLine = Line("Lock", kit.Small, Pal.Brass1);
            find = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "FIND RIVAL", null, new RectInt(0, 0, 164, 30), O + 2);
        }

        protected override void Layout()
        {
            var r = PlaceCard(W, H);
            int cx = r.x + r.width / 2;
            casualRect = new RectInt(cx - 1 - SegW, r.y + 40, SegW, SegH);
            rankedRect = new RectInt(cx + 1, r.y + 40, SegW, SegH);
            int ty = r.y + 40 + (SegH - kit.Small.LineHeight) / 2;
            casualText.At(casualRect.x + SegW / 2, ty);
            rankedText.At(rankedRect.x + SegW / 2, ty);
            desc1.At(cx, r.y + 68);
            desc2.At(cx, r.y + 78);
            lockLine.At(cx, r.y + 91);
            int bw = Mathf.Min(164, r.width - 24);
            find.SetRect(new RectInt(cx - bw / 2, r.y + 106, bw, 30));
            Refresh();
        }

        /// <summary>Opens the card with the stored pick; ranked falls back to casual while not linked.</summary>
        public void Show(bool rankedPick, bool isLinked)
        {
            linked = isLinked;
            ranked = rankedPick && linked;
            lockFlash = 0f;
            if (!IsOpen) Open();
            else Layout();
        }

        private void Refresh()
        {
            PlaceSeg(segCasual, !ranked, casualRect);
            PlaceSeg(segRanked, ranked, rankedRect);
            // the picked (yellow) segment reads cream-on-yellow like EASY | HARD; a locked RANKED is dimmed wood
            casualText.SetColors(!ranked ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            rankedText.SetColors(linked ? (ranked ? Pal.Cream0 : Pal.Cream1) : Pal.Sand3, Pal.Ink, Pal.Ink2);
            casualText.MarkDirty();
            rankedText.MarkDirty();
            if (ranked)
            {
                SetLine(desc1, "Rated. Standard guns for all.", Pal.Cream1);
                SetLine(desc2, "Climb this week's board.", Pal.Sand0);
            }
            else
            {
                SetLine(desc1, "Unrated. Hard rules, 3 rounds.", Pal.Cream1);
                SetLine(desc2, "Most damage wins.", Pal.Sand0);
            }
            SetLine(lockLine, linked ? null : "SIGN IN TO RANK", lockFlash > 0f ? Pal.SigRed : Pal.Brass1);
        }

        public Action Tick()
        {
            RelayoutIfNeeded();
            if (lockFlash > 0f)
            {
                lockFlash -= PawTime.UnscaledDt;
                if (lockFlash <= 0f) Refresh();
            }
            if (BackOrOutside()) return Action.Back;
            if (find.Tick()) return Action.Find;
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                if (UiPointer.In(casualRect))
                {
                    UiPointer.Consumed = true;
                    if (ranked) { ranked = false; Toggled(); }
                    return Action.None;
                }
                if (UiPointer.In(rankedRect))
                {
                    UiPointer.Consumed = true;
                    if (!linked)
                    {
                        // greyed: say why (the line turns red for a moment), no switch
                        lockFlash = 0.6f;
                        Refresh();
                        PawHaptics.Notify(1);
                        return Action.LockedTap;
                    }
                    if (!ranked) { ranked = true; Toggled(); }
                    return Action.None;
                }
            }
            SwallowTaps();
            return Action.None;
        }

        private void Toggled()
        {
            Click("sfx_ui_toggle");
            PawHaptics.Impact(0);
            Refresh();
        }
    }
}
