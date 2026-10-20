using NUnit.Framework;
using PawNoon.Runtime;
using UnityEngine;

namespace PawNoon.Tests
{
    /// <summary>LAST LIGHT over the HUD badge's baked HIGH NOON (feel wave 2).</summary>
    public class BadgeTitleTests
    {
        [Test]
        public void PatchCoversTheBakedTextAndCentresTheLabel()
        {
            var baked = BadgeTitle.DefaultTextRect;   // 49,7 54x9 measured from hud_badge.png
            const int bx = 100, by = 200;
            // "LAST LIGHT" in font_medium is 58 px wide, caps 7 px
            RectInt patch;
            Vector2Int at;
            BadgeTitle.Layout(bx, by, baked, 58, 7, out patch, out at);
            Assert.LessOrEqual(patch.xMin, bx + baked.xMin);
            Assert.GreaterOrEqual(patch.xMax, bx + baked.xMax);
            Assert.AreEqual(by + baked.yMin, patch.yMin);
            Assert.AreEqual(by + baked.yMax, patch.yMax);
            Assert.AreEqual(60, patch.width, "label + 1 px outline each side");
            // the label's outline (1 px around the fill) stays inside the patch, centred on the baked text
            Assert.AreEqual(patch.xMin + 1, at.x);
            Assert.AreEqual(patch.xMax - 1, at.x + 58);
            Assert.AreEqual(by + 8, at.y, "caps on the baked rows 8..14");
            float bakedCentre = bx + baked.x + baked.width / 2f;
            float labelCentre = at.x + 58 / 2f;
            Assert.LessOrEqual(Mathf.Abs(bakedCentre - labelCentre), 1f);
        }

        [Test]
        public void NarrowLabelKeepsTheBakedPatch()
        {
            RectInt patch;
            Vector2Int at;
            BadgeTitle.Layout(0, 0, BadgeTitle.DefaultTextRect, 30, 7, out patch, out at);
            Assert.AreEqual(BadgeTitle.DefaultTextRect, patch);
            Assert.AreEqual(49 + (54 - 32) / 2 + 1, at.x);
        }
    }
}
