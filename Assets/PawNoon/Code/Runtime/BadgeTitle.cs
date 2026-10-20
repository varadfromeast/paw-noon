using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// "LAST LIGHT" over the HUD badge's baked "HIGH NOON" title (feel wave 2). The badge art has the words painted
    /// in; during sudden death a plate-coloured patch covers exactly that text (rect measured from hud_badge.png,
    /// ui.json hud_badge.titleText) and the medium bitmap font writes LAST LIGHT on it in signal yellow with a 1 px
    /// ink outline, matching the baked lettering. It sits on the HUD (overlay layer), so it never zooms or shakes,
    /// and is hidden whenever the badge or LAST LIGHT is off. No per-frame work: it only changes on Show / Place.
    /// </summary>
    public sealed class BadgeTitle
    {
        /// <summary>Baked title text incl. its 1 px outline, in badge px (x, y, w, h): measured from hud_badge.png.</summary>
        public static readonly RectInt DefaultTextRect = new RectInt(49, 7, 54, 9);
        /// <summary>The sign plate's fill behind the baked letters (hud_badge.png, = Pal.Wood4).</summary>
        public static readonly Color32 PlateColor = new Color32(0x4A, 0x2C, 0x1C, 255);
        public const string Label = "LAST LIGHT";

        private readonly SpriteRenderer patch;
        private readonly BitmapText text;
        private readonly RectInt textRect;
        private RectInt patchRect;
        private bool on;
        private bool badgeShown = true;

        public BadgeTitle(UiKit kit, Transform parent, int order)
        {
            textRect = ReadTextRect();
            patch = PawGfx.NewSprite("BadgeTitlePatch", parent, order);
            patch.sprite = ArtLib.WhitePixel;
            patch.color = PlateColor;
            text = BitmapText.Create("BadgeTitle", parent, kit.Medium, order + 1, TextAlign.Left);
            text.SetColors(Pal.SigYellow, Pal.Ink, Pal.Ink2);
            text.Outline = true;
            text.Shadow = false;
            text.SetText(Label);
            Apply();
        }

        /// <summary>ui.json hud_badge.titleText [x, y, w, h] when present, else the measured default.</summary>
        private static RectInt ReadTextRect()
        {
            var root = ArtLib.Json("Art/UI/ui", false);
            var a = PnJson.NumArray(PnJson.Obj(root, "hud_badge"), "titleText");
            if (a == null || a.Length < 4) return DefaultTextRect;
            return new RectInt((int)a[0], (int)a[1], Mathf.Max(1, (int)a[2]), Mathf.Max(1, (int)a[3]));
        }

        /// <summary>
        /// Where the patch and the label go for a badge whose top-left canvas pixel is (badgeX, badgeY): the patch
        /// covers the baked text rect, widened (centred) when the label plus its outline is wider; the label is
        /// centred on the baked text's centre with its outline inside the patch. Pure maths, for tests.
        /// </summary>
        public static void Layout(int badgeX, int badgeY, RectInt baked, int labelWidth, int labelHeight,
            out RectInt patchRect, out Vector2Int labelTopLeft)
        {
            int outlined = labelWidth + 2;
            int w = Mathf.Max(baked.width, outlined);
            int x = badgeX + baked.x - (w - baked.width) / 2;
            patchRect = new RectInt(x, badgeY + baked.y, w, baked.height);
            // outline is 1 px on every side: the fill starts 1 px in from the patch
            int lx = x + (w - outlined) / 2 + 1;
            int ly = badgeY + baked.y + (baked.height - labelHeight) / 2;
            labelTopLeft = new Vector2Int(lx, ly);
        }

        /// <summary>Called by the HUD's layout with the badge's top-left canvas pixel.</summary>
        public void Place(int badgeX, int badgeY)
        {
            // cap height (7 px for the medium font) + outline rows = the baked 9 px band
            int capH = Mathf.Max(1, textRect.height - 2);
            Vector2Int at;
            Layout(badgeX, badgeY, textRect, text.Width, capH, out patchRect, out at);
            patch.transform.position = Px.World(patchRect.x, patchRect.yMax);
            patch.transform.localScale = new Vector3(patchRect.width, patchRect.height, 1f);
            text.At(at.x, at.y);
        }

        public RectInt PatchRect { get { return patchRect; } }

        /// <summary>LAST LIGHT on / off (the director's sudden-death state).</summary>
        public void Show(bool lastLight)
        {
            if (on == lastLight) return;
            on = lastLight;
            Apply();
        }

        /// <summary>Follows DuelHud.ShowBadge: no badge, no title.</summary>
        public void SetBadgeShown(bool shown)
        {
            if (badgeShown == shown) return;
            badgeShown = shown;
            Apply();
        }

        public bool Visible { get { return on && badgeShown; } }

        private void Apply()
        {
            bool v = on && badgeShown;
            patch.enabled = v;
            text.Visible = v;
        }
    }
}
