using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    public sealed class MatchResultScreen : ScreenBase
    {
        private const int O = 260;
        private const int BtnW = 164;
        private const int BtnH = 30;
        private PopSprite banner;
        private BitmapText bannerText;
        private SpriteRenderer panel;
        private BitmapText[] keys = new BitmapText[3];
        private BitmapText[] vals = new BitmapText[3];
        private PixelButton rematch, menu;
        private ShareButton share;   // feel wave 2: share card of the match (best round + verdict)
        private BitmapText newBest;
        private bool win;
        private bool standoff;
        // banner drop: two falling steps, land, one small bounce, settle (whole pixels)
        private static readonly int[] BannerDrop = { -18, -9, 0, -4, 0 };

        protected override void Build()
        {
            banner = new PopSprite("Banner", transform, O + 2);
            bannerText = LightText("BannerText", kit.Medium, O + 3, TextAlign.Center);
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, 180, 62));
            string[] k = { "BEST", "AVG", "ALL-TIME" };
            for (int i = 0; i < 3; i++)
            {
                keys[i] = InkText("K" + i, kit.Medium, O + 2, TextAlign.Left);
                keys[i].SetText(k[i]);
                vals[i] = InkText("V" + i, kit.Medium, O + 2, TextAlign.Right);
                vals[i].MonoDigits = true;
            }
            newBest = LightText("NewBest", kit.Small, O + 3, TextAlign.Center);
            newBest.SetColors(Pal.SigYellow, Pal.Ink, Pal.Ink2);
            rematch = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "REMATCH", "ico_retry", new RectInt(0, 0, BtnW, BtnH), O + 2);
            menu = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "MENU", "ico_home", new RectInt(0, 0, BtnW, BtnH), O + 2);
            share = new ShareButton(kit, transform, O + 2);
        }

        /// <summary>Banner + stats sit in the upper third under the HUD, so Pawtruth (canvas y 330+) stays visible.</summary>
        private int Top()
        {
            return Mathf.Max(HudBottom, layout.ResultPanel.y - 30);
        }

        protected override void Layout()
        {
            int cx = CenterX;
            int top = Top();
            var bd = kit.Ui.Get(win ? "banner_win" : "banner_lose");
            banner.Show(bd, new Vector2Int(cx - bd.Cell.x / 2 + bd.Pivot.x, top + bd.Pivot.y), 0f, 0f);
            bannerText.At(cx, top + 14);
            int py = top + bd.Cell.y + 8;
            int pw = 180;
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), new RectInt(cx - pw / 2, py, pw, 62));
            for (int i = 0; i < 3; i++)
            {
                keys[i].At(cx - pw / 2 + 10, py + 9 + i * 15);
                vals[i].At(cx + pw / 2 - 10, py + 9 + i * 15);
            }
            newBest.At(cx, py + 66);
            // buttons: bottom thumb zone, stacked above the holster graphic
            int menuY = HolsterTop - 6 - BtnH;
            int rematchY = menuY - 6 - BtnH;
            rematch.SetRect(new RectInt(cx - BtnW / 2, rematchY, BtnW, BtnH));
            menu.SetRect(new RectInt(cx - BtnW / 2, menuY, BtnW, BtnH));
            // SHARE: right of REMATCH, same row (82 + 4 + 30 <= 135, inside the 270-px core)
            share.PlaceAt(cx + BtnW / 2 + 4, rematchY + (BtnH - ShareButton.Size) / 2);
        }

        /// <summary>REMATCH label (PvP: "NEW RIVAL"; null restores REMATCH).</summary>
        public void SetRematchLabel(string label) { rematch.SetLabel(string.IsNullOrEmpty(label) ? "REMATCH" : label); }

        /// <summary>First free canvas y under the stats panel and the new-best line (online result card goes here).</summary>
        public int ContentBottom
        {
            get
            {
                var bd = kit.Ui.Get(win ? "banner_win" : "banner_lose");
                return Top() + bd.Cell.y + 8 + 62 + 4 + 12;
            }
        }

        /// <summary>Top of the REMATCH button (the online card must stay above it).</summary>
        public int ButtonsTop { get { return rematch.Rect.y; } }

        public void Show(bool playerWon, bool isDeadHeat, long bestNs, long avgNs, long allTimeNs, bool isNewBest)
        {
            win = playerWon;
            bool deadHeat = isDeadHeat;
            standoff = deadHeat;
            Open();
            var bd = kit.Ui.Get(win ? "banner_win" : "banner_lose");
            int cx = CenterX;
            int top = Top();
            var at = new Vector2Int(cx - bd.Cell.x / 2 + bd.Pivot.x, top + bd.Pivot.y);
            if (PawSettings.ReducedMotion) banner.Show(bd, at, 0f, 0f); // instant appear
            else banner.ShowSteps(bd, at, BannerDrop, 0.05f, 0.06f);
            // double KO: keep the dark ribbon and letter STANDOFF over it (both cats down)
            bannerText.SetText(deadHeat ? "STANDOFF" : "");
            bannerText.Visible = deadHeat;
            vals[0].SetText(FormatSeconds(bestNs));
            vals[1].SetText(FormatSeconds(avgNs));
            vals[2].SetText(FormatSeconds(allTimeNs));
            newBest.SetText(isNewBest ? "NEW ALL-TIME BEST!" : "");
        }

        public UiAction Tick()
        {
            RelayoutIfNeeded();
            banner.Tick(PawTime.UnscaledDt);
            share.Tick(ShareKind.Match, standoff ? -1 : (win ? 0 : 1));
            if (rematch.Tick()) return UiAction.Rematch;
            if (menu.Tick()) return UiAction.Menu;
            return UiAction.None;
        }
    }
}
