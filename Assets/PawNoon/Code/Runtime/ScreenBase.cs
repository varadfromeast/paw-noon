using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>Beta switch (net_config.json "accounts": false): hides the profile and ACCOUNT buttons.</summary>
    public static class AccountsUi { public static bool Enabled = true; }

    public enum UiAction { None, Play, OpenSettings, CloseSettings, Rematch, Menu, Continue, ChooseEasy, ChooseHard, Pvp, Friend, OpenProfile, OpenAccount, Leaderboard, Tutorial }

    /// <summary>Common base: a root object that can be shown/hidden, laid out against the pipeline rects.</summary>
    public abstract class ScreenBase : MonoBehaviour
    {
        protected UiKit kit;
        protected GameLayout layout;
        protected PixelPipeline pipe;
        private int laidOut = -1;

        public void Setup(UiKit k, GameLayout l, PixelPipeline p)
        {
            kit = k;
            layout = l;
            pipe = p;
            Build();
            Layout();
            laidOut = pipe.LayoutVersion;
            gameObject.SetActive(false);
        }

        protected abstract void Build();
        protected abstract void Layout();

        public bool IsOpen { get { return gameObject.activeSelf; } }

        public virtual void Open()
        {
            gameObject.SetActive(true);
            Layout();
            laidOut = pipe.LayoutVersion;
        }

        public virtual void Close() { gameObject.SetActive(false); }

        protected void RelayoutIfNeeded()
        {
            if (laidOut != pipe.LayoutVersion) { Layout(); laidOut = pipe.LayoutVersion; }
        }

        protected int CenterX { get { return pipe.Visible.x + pipe.Visible.width / 2; } }

        /// <summary>First canvas y below the duel HUD (portraits + round sign), inside the safe area.</summary>
        protected int HudBottom
        {
            get
            {
                int fh = kit.Ui.Get("portrait_frame").Cell.y;
                int rh = kit.Ui.Get("round_sign").Cell.y;
                return pipe.Safe.y + layout.HudMargin + fh + 2 + rh + 4;
            }
        }

        /// <summary>Top of the holster graphic (the HUD draws it 6 px above the safe bottom, 40 px tall).</summary>
        protected int HolsterTop
        {
            get
            {
                var h = kit.Ui.TryGet("holster_zone");
                return pipe.Safe.yMax - 6 - (h != null ? h.Cell.y : 40);
            }
        }

        protected void PlaceCentered(SpriteRenderer sr, SheetDef d, int cx, int cy)
        {
            PawGfx.Place(sr, cx - d.Cell.x / 2 + d.Pivot.x, cy - d.Cell.y / 2 + d.Pivot.y);
        }

        protected BitmapText InkText(string name, BitmapFont f, int order, TextAlign a)
        {
            var t = BitmapText.Create(name, transform, f, order, a);
            t.SetColors(Pal.Ink, Pal.Ink, Pal.Ink2);
            t.Outline = false;
            t.Shadow = false;
            return t;
        }

        protected BitmapText LightText(string name, BitmapFont f, int order, TextAlign a)
        {
            var t = BitmapText.Create(name, transform, f, order, a);
            t.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            return t;
        }

        public static string FormatSeconds(long ns)
        {
            return ns < 0 ? "--" : NumFmt.Seconds6(ns) + " s";
        }
    }
}
