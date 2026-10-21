using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Shop
{
    public enum ShopAction { None, Close, Buy, Equip, Restore }

    /// <summary>What the card shows; ShopDirector fills it from GunLocker + IapService.</summary>
    public struct ShopCardView
    {
        public GunState State;
        public bool Equipped;
        /// <summary>Store price ("₹299.00", "$2.99") or null for the placeholder.</summary>
        public string Price;
        public string Message;
    }

    /// <summary>
    /// The general store (SPEC_V2 §6, M7): one card for the Peacemaker over the dimmed title. Existing kit only
    /// (panel_wood frame, panel_paper card, btn_large / btn_large_alt, bitmap fonts) plus the generated
    /// peacemaker_card art, ico_shop and glyph_rupee. Layout, top to bottom: back + STORE title; the card (name,
    /// revolver, two lines of what it does, price or OWNED, BUY / EQUIP / EQUIPPED); a status line; RESTORE
    /// PURCHASES; the legal line "Ranked uses standard guns."
    /// Text is only rebuilt when the view changes, so an open shop allocates nothing per frame.
    /// </summary>
    public sealed class ShopScreen : ScreenBase
    {
        private const int O = 290;          // over the online card (280), under settings (300)
        private const int PanelW = 220;
        private const int CardH = 132;
        private const int BtnW = 150, BtnH = 30;
        private const int RestoreW = 168, RestoreH = 24;
        public const string PlaceholderPrice = "₹299";

        private SpriteRenderer dim, panel, card, gun, rupee;
        private BitmapText title, gunName, line1, line2, price, status, legal;
        private PixelButton back, buy, buyAlt, restore;
        private SheetDef gunDef, rupeeDef;
        private RectInt panelRect;
        private ShopCardView view;
        private bool hasView;
        private int priceX, priceY;

        protected override void Build()
        {
            dim = UiKit.Dim(transform, O - 2, 160);
            panel = kit.Panel("panel_wood", transform, O, new RectInt(0, 0, PanelW, 300));
            card = kit.Panel("panel_paper", transform, O + 1, new RectInt(0, 0, PanelW - 20, CardH));
            title = BitmapText.Create("Title", transform, kit.Medium, O + 3, TextAlign.Center);
            title.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
            title.SetText("GENERAL STORE");
            back = new PixelButton(kit, transform, "btn_icon", "btn_icon_pressed", null, "ico_back", new RectInt(0, 0, 30, 30), O + 2);

            gunName = InkText("Name", kit.Medium, O + 3, TextAlign.Center);
            gunName.SetText("THE PEACEMAKER");
            gunDef = kit.Ui.TryGet("peacemaker_card");
            gun = PawGfx.NewSprite("Gun", transform, O + 3);
            if (gunDef != null) gun.sprite = gunDef.First;
            else gun.enabled = false;
            line1 = InkText("Line1", kit.Small, O + 3, TextAlign.Center);
            line1.SetText("Fast draws hit harder: 48 at 150 ms.");
            line2 = InkText("Line2", kit.Small, O + 3, TextAlign.Center);
            line2.SetText("Blue fire. Ivory grip. Solo + casual.");
            line2.SetColors(Pal.Wood3, Pal.Ink, Pal.Ink2);

            price = BitmapText.Create("Price", transform, kit.Medium, O + 3, TextAlign.Left);
            price.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            rupeeDef = kit.Ui.TryGet("glyph_rupee");
            rupee = PawGfx.NewSprite("Rupee", transform, O + 3);
            if (rupeeDef != null) rupee.sprite = rupeeDef.First;
            rupee.enabled = false;
            buy = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "BUY", null, new RectInt(0, 0, BtnW, BtnH), O + 2);
            buyAlt = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "EQUIPPED", null, new RectInt(0, 0, BtnW, BtnH), O + 2);

            status = BitmapText.Create("Status", transform, kit.Small, O + 3, TextAlign.Center);
            status.SetColors(Pal.Sand0, Pal.Ink, Pal.Ink2);
            restore = new PixelButton(kit, transform, "panel_wood", "panel_wood", "RESTORE PURCHASES", null, new RectInt(0, 0, RestoreW, RestoreH), O + 2);
            legal = BitmapText.Create("Legal", transform, kit.Small, O + 3, TextAlign.Center);
            legal.SetColors(Pal.Cream1, Pal.Ink, Pal.Ink2);
            legal.SetText("Ranked uses standard guns.");
        }

        private int ContentHeight()
        {
            // pad, title row (30 px back button), card, status, restore, legal, pad
            return 8 + 30 + 6 + CardH + 6 + kit.Small.LineHeight + 6 + RestoreH + 6 + kit.Small.LineHeight + 10;
        }

        protected override void Layout()
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            UiKit.FillVisible(dim, pipe);
            int w = Mathf.Min(PanelW, vis.width - 12);
            int h = ContentHeight();
            int x = CenterX - w / 2;
            // centred in the safe area, never above it
            int y = Mathf.Max(safe.y + 4, safe.y + (safe.height - h) / 2);
            panelRect = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_wood"), panelRect);
            int cx = x + w / 2;
            int cy = y + 8;
            back.SetRect(new RectInt(x + 6, cy, 30, 30));
            title.At(cx + 8, cy + (30 - kit.Medium.LineHeight) / 2 + 1);
            cy += 30 + 6;

            int cw = w - 20;
            var cr = new RectInt(x + 10, cy, cw, CardH);
            UiKit.SetRect(card, kit.Ui.Get("panel_paper"), cr);
            int ccx = cr.x + cw / 2;
            int ry = cy + 8;
            gunName.At(ccx, ry);
            ry += kit.Medium.LineHeight + 3;
            if (gunDef != null)
            {
                PlaceCentered(gun, gunDef, ccx, ry + gunDef.Cell.y / 2);
                ry += gunDef.Cell.y + 3;
            }
            line1.At(ccx, ry);
            ry += kit.Small.LineHeight + 1;
            line2.At(ccx, ry);
            ry += kit.Small.LineHeight + 4;
            priceY = ry;
            ry += kit.Medium.LineHeight + 3;
            int bw = Mathf.Min(BtnW, cw - 16);
            var br = new RectInt(ccx - bw / 2, Mathf.Min(ry, cr.yMax - BtnH - 6), bw, BtnH);
            buy.SetRect(br);
            buyAlt.SetRect(br);
            cy += CardH + 6;

            status.At(cx, cy);
            cy += kit.Small.LineHeight + 6;
            int rw = Mathf.Min(RestoreW, w - 24);
            restore.SetRect(new RectInt(cx - rw / 2, cy, rw, RestoreH));
            cy += RestoreH + 6;
            legal.At(cx, cy);
            LayoutPrice();
        }

        /// <summary>The price line, centred on the card; a leading "₹" is drawn with glyph_rupee (the fonts lack it).</summary>
        private void LayoutPrice()
        {
            int ccx = panelRect.x + panelRect.width / 2;
            bool sign = rupee.sprite != null && rupee.enabled;
            int glyphAdv = sign ? 7 : 0;               // font_medium digit advance + 1 px air
            int total = glyphAdv + price.Width;
            priceX = ccx - total / 2;
            if (sign) PawGfx.Place(rupee, priceX - 1, priceY - 1);
            price.At(priceX + glyphAdv, priceY);
        }

        /// <summary>Updates the card. Cheap when nothing changed (no text rebuilt).</summary>
        public void SetView(ShopCardView v)
        {
            if (hasView && v.State == view.State && v.Equipped == view.Equipped && v.Price == view.Price && v.Message == view.Message) return;
            hasView = true;
            view = v;
            bool owned = v.State == GunState.Owned;
            if (owned)
            {
                rupee.enabled = false;
                price.SetText("OWNED");
                price.SetColors(Pal.SigGreen, Pal.Ink, Pal.Ink2);
            }
            else
            {
                string p = string.IsNullOrEmpty(v.Price) ? PlaceholderPrice : v.Price;
                bool sign = p.Length > 0 && p[0] == '₹';
                rupee.enabled = sign && rupeeDef != null;
                price.SetText(PriceText(sign && rupeeDef != null ? p.Substring(1) : p));
                price.SetColors(Pal.Sun1, Pal.Ink, Pal.Ink2);
            }
            price.MarkDirty();
            buy.SetVisible(!(owned && v.Equipped));
            buyAlt.SetVisible(owned && v.Equipped);
            switch (v.State)
            {
                case GunState.Locked: buy.SetLabel("BUY"); break;
                case GunState.Buying: buy.SetLabel("AT THE STORE..."); break;
                case GunState.Verifying: buy.SetLabel("CHECKING..."); break;
                default: buy.SetLabel("EQUIP"); break;
            }
            status.SetText(v.Message ?? "");
            if (IsOpen) LayoutPrice();
        }

        /// <summary>Keeps only characters the bitmap fonts have; currency signs they lack become ISO-ish words.</summary>
        public static string PriceText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new System.Text.StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '₹') sb.Append("Rs ");
                else if (c == '€') sb.Append("EUR ");
                else if (c == '£') sb.Append("GBP ");
                else if (c == ' ') sb.Append(' ');
                else if (c >= 32 && c < 127) sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        public ShopAction Tick()
        {
            RelayoutIfNeeded();
            if (back.Tick()) return ShopAction.Close;
            if (buy.Tick())
            {
                if (view.State == GunState.Locked) return ShopAction.Buy;
                if (view.State == GunState.Owned) return ShopAction.Equip;
            }
            if (buyAlt.Tick()) return ShopAction.Equip;
            if (restore.Tick()) return ShopAction.Restore;
            // modal: a tap outside the panel closes it, anything else is swallowed
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                UiPointer.Consumed = true;
                if (!UiPointer.In(panelRect)) return ShopAction.Close;
            }
            return ShopAction.None;
        }
    }

    /// <summary>
    /// The SHOP button on the title: a small wood sign (24 px tall, label + ico_shop) in the safe area's top-left
    /// corner, mirroring the gear on the right. ShopDirector shows it only while the title is interactive.
    /// TitleSlot is the one place to move it when the trophy / profile buttons land beside it.
    /// </summary>
    public sealed class ShopButton
    {
        public const int W = 62, H = 24;
        private readonly PixelButton button;
        private readonly PixelPipeline pipe;
        private int laidOut = -1;
        private bool visible = true;

        public ShopButton(UiKit kit, Transform parent, PixelPipeline pipe)
        {
            this.pipe = pipe;
            button = new PixelButton(kit, parent, "panel_wood", "panel_wood", "SHOP", kit.Ui.Has("ico_shop") ? "ico_shop" : null,
                new RectInt(0, 0, W, H), 240);
            SetVisible(false);
        }

        /// <summary>Top-left of the safe area, vertically centred on the 30 px gear (safe.y + 2).</summary>
        public static RectInt TitleSlot(RectInt safe)
        {
            return new RectInt(safe.x + 4, safe.y + 2 + (30 - H) / 2, W, H);
        }

        public void SetVisible(bool v)
        {
            if (v == visible) return;
            visible = v;
            button.SetVisible(v);
            if (v) laidOut = -1;
        }

        public bool Tick()
        {
            if (!visible) return false;
            if (laidOut != pipe.LayoutVersion)
            {
                laidOut = pipe.LayoutVersion;
                button.SetRect(TitleSlot(pipe.Safe));
            }
            return button.Tick();
        }
    }
}
