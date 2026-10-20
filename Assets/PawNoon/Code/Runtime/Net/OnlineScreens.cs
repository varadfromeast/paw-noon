using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// The online card over the title (existing kit only: panel_wood, the wood sign buttons, bitmap fonts):
    /// FINDING A RIVAL (dots + CANCEL), the opponent card (name, cat, rating + DUEL), and friendly errors (OK).
    /// Same footprint and thumb-zone placement as the first-launch EASY / HARD card.
    /// </summary>
    public sealed class OnlineCard : ScreenBase
    {
        private const int O = 280;          // above the title (240) and its choice card (270), below settings (300)
        private const int CardW = 196;
        private const int BtnW = 150;
        private const int BtnH = 30;
        private static readonly string[] Dots = { "", ".", "..", "..." };

        private SpriteRenderer dim;
        private SpriteRenderer panel;
        private BitmapText title, big, line1, line2, dots;
        private PixelButton button;
        private string bigS, l1S, l2S;
        private bool hasButton;
        private bool showDots;
        private int dotIndex = -1;
        private float dotT;

        protected override void Build()
        {
            dim = PawGfx.NewSprite("Dim", transform, O - 2);
            dim.sprite = ArtLib.WhitePixel;
            dim.color = new Color32(Pal.Ink.r, Pal.Ink.g, Pal.Ink.b, 150);
            panel = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, CardW, 100));
            title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
            title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            big = BitmapText.Create("Big", transform, kit.Medium, O + 3, TextAlign.Center);
            big.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            line1 = BitmapText.Create("Line1", transform, kit.Small, O + 3, TextAlign.Center);
            line1.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            line2 = BitmapText.Create("Line2", transform, kit.Small, O + 3, TextAlign.Center);
            line2.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            // left-aligned at a fixed x so the growing dots never shift the line
            dots = BitmapText.Create("Dots", transform, kit.Medium, O + 3, TextAlign.Left);
            dots.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            button = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "OK", null, new RectInt(0, 0, BtnW, BtnH), O + 2);
        }

        private static bool Has(string s) { return !string.IsNullOrEmpty(s); }

        /// <summary>Card height for the current content (whole pixels).</summary>
        private int ContentHeight()
        {
            int h = 12 + kit.Medium.LineHeight;                       // top pad + title
            if (showDots) h += 6 + kit.Medium.LineHeight;
            if (Has(bigS)) h += 8 + kit.Medium.LineHeight;
            if (Has(l1S)) h += 6 + kit.Small.LineHeight;
            if (Has(l2S)) h += 3 + kit.Small.LineHeight;
            if (hasButton) h += 10 + BtnH;
            return h + 12;
        }

        protected override void Layout()
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            dim.transform.position = Px.World(vis.x, vis.yMax);
            dim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            int w = Mathf.Min(CardW, vis.width - 16);
            int h = ContentHeight();
            int x = CenterX - w / 2;
            // thumb zone like the EASY / HARD card, never up over the logo (upper third)
            int y = Mathf.Max(layout.Logo.y + 64, safe.yMax - 12 - h);
            if (y + h > safe.yMax - 4) y = Mathf.Max(safe.y + 4, safe.yMax - 4 - h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_wood"), new RectInt(x, y, w, h));
            int cx = x + w / 2;
            int cy = y + 12;
            title.At(cx, cy);
            cy += kit.Medium.LineHeight;
            if (showDots)
            {
                cy += 6;
                dots.At(cx - 6, cy);
                cy += kit.Medium.LineHeight;
            }
            if (Has(bigS)) { cy += 8; big.At(cx, cy); cy += kit.Medium.LineHeight; }
            if (Has(l1S)) { cy += 6; line1.At(cx, cy); cy += kit.Small.LineHeight; }
            if (Has(l2S)) { cy += 3; line2.At(cx, cy); cy += kit.Small.LineHeight; }
            if (hasButton)
            {
                cy += 10;
                int bw = Mathf.Min(BtnW, w - 24);
                button.SetRect(new RectInt(cx - bw / 2, cy, bw, BtnH));
            }
        }

        /// <summary>Shows the card. <paramref name="buttonLabel"/> null = no button; <paramref name="withDots"/> = animated "..." under the title.</summary>
        public void Show(string titleText, string bigText, string l1, string l2, string buttonLabel, bool withDots)
        {
            bigS = bigText;
            l1S = l1;
            l2S = l2;
            title.SetText(titleText ?? "");
            big.SetText(bigText ?? "");
            line1.SetText(l1 ?? "");
            line2.SetText(l2 ?? "");
            big.Visible = Has(bigText);
            line1.Visible = Has(l1);
            line2.Visible = Has(l2);
            showDots = withDots;
            dots.Visible = withDots;
            dotIndex = -1;
            dotT = 0f;
            hasButton = Has(buttonLabel);
            button.SetVisible(hasButton);
            if (hasButton) button.SetLabel(buttonLabel);
            if (!IsOpen) Open();
            else Layout();
            TickDots(0f);
        }

        private void TickDots(float dt)
        {
            if (!showDots) return;
            dotT += dt;
            int i = PawSettings.ReducedMotion ? 3 : ((int)(dotT / 0.3f)) % Dots.Length;
            if (i == dotIndex) return;
            dotIndex = i;
            dots.SetText(Dots[i]);   // cached strings: no allocation
        }

        /// <summary>True on a click of the card's button. Taps elsewhere are swallowed (the card is modal).</summary>
        public bool Tick()
        {
            RelayoutIfNeeded();
            TickDots(PawTime.UnscaledDt);
            if (hasButton && button.Tick()) return true;
            if (UiPointer.Down) UiPointer.Consumed = true;
            return false;
        }
    }

    /// <summary>
    /// PvP strip on the match result screen: "VS &lt;name&gt;" and the server's verdict line (SENDING... until
    /// match-submit answers). Painted paper like the stats panel, under it.
    /// </summary>
    public sealed class OnlineResultCard : ScreenBase
    {
        private const int O = 264;          // over the match result screen (260..263)
        private const int W = 180;
        private const int H = 34;
        private static readonly string[] Dots = { "SENDING", "SENDING.", "SENDING..", "SENDING..." };

        private SpriteRenderer panel;
        private BitmapText vs, status;
        private int top = -1;
        private bool sending;
        private int dotIndex = -1;
        private float dotT;

        protected override void Build()
        {
            panel = kit.Panel("panel_paper", transform, O, new RectInt(0, 0, W, H));
            vs = InkText("Vs", kit.Medium, O + 2, TextAlign.Center);
            status = InkText("Status", kit.Small, O + 2, TextAlign.Center);
        }

        /// <summary>Card top in canvas px (from MatchResultScreen.ContentBottom / ButtonsTop).</summary>
        public void Place(int y, int maxBottom)
        {
            top = Mathf.Min(y, maxBottom - H);
            if (IsOpen) Layout();
        }

        protected override void Layout()
        {
            int cx = CenterX;
            int w = Mathf.Min(W, pipe.Visible.width - 16);
            int y = top >= 0 ? top : layout.ResultPanel.y + 110;
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), new RectInt(cx - w / 2, y, w, H));
            vs.At(cx, y + 6);
            status.At(cx, y + 6 + kit.Medium.LineHeight + 3);
        }

        public void Show(string vsText, string statusText, Color32 statusColor)
        {
            if (!IsOpen) Open();
            vs.SetText(vsText ?? "");
            SetStatus(statusText, statusColor);
        }

        /// <summary>null text = the animated SENDING line.</summary>
        public void SetStatus(string text, Color32 color)
        {
            sending = text == null;
            dotIndex = -1;
            dotT = 0f;
            status.Color = color;
            if (sending) TickDots(0f);
            else status.SetText(text);
            status.MarkDirty();
        }

        private void TickDots(float dt)
        {
            dotT += dt;
            int i = PawSettings.ReducedMotion ? 3 : ((int)(dotT / 0.3f)) % Dots.Length;
            if (i == dotIndex) return;
            dotIndex = i;
            status.SetText(Dots[i]);
        }

        public void Tick()
        {
            RelayoutIfNeeded();
            if (sending) TickDots(PawTime.UnscaledDt);
        }
    }
}
