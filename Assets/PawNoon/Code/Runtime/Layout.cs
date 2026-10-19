using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Canvas/layout constants. Defaults = SPEC §9; overridden by duel_config.json "layout" and by
    /// environment.json canvas/anchors and character manifests (read by their loaders).
    /// All points are canvas coordinates (top-left origin, y down).
    /// </summary>
    public sealed class GameLayout
    {
        public Vector2Int Canvas = new Vector2Int(360, 640);
        public Vector2Int Core = new Vector2Int(270, 480);
        public Vector2Int Cue = new Vector2Int(180, 190);
        public Vector2Int Timer = new Vector2Int(180, 240);
        public Vector2Int ResultPanel = new Vector2Int(180, 150);
        public Vector2Int ResultPanelSize = new Vector2Int(220, 104);
        public Vector2Int Hint = new Vector2Int(262, 528);
        public Vector2Int Logo = new Vector2Int(180, 150);
        public Vector2Int PlayButton = new Vector2Int(180, 470);
        public Vector2Int BanditFeet = new Vector2Int(200, 432);
        public Vector2Int HeroBottomLeft = new Vector2Int(14, 640);
        public Vector2Int Sun = new Vector2Int(268, 150);
        public int HudMargin = 4;
        public int BadgeWidth = 72;

        public static GameLayout FromJson(object root)
        {
            var l = new GameLayout();
            var o = PnJson.Obj(root, "layout");
            if (o == null) return l;
            Read(o, "canvas", ref l.Canvas);
            Read(o, "core", ref l.Core);
            Read(o, "cue", ref l.Cue);
            Read(o, "timer", ref l.Timer);
            Read(o, "resultPanel", ref l.ResultPanel);
            Read(o, "resultPanelSize", ref l.ResultPanelSize);
            Read(o, "hint", ref l.Hint);
            Read(o, "logo", ref l.Logo);
            Read(o, "playButton", ref l.PlayButton);
            Read(o, "banditFeet", ref l.BanditFeet);
            Read(o, "heroBottomLeft", ref l.HeroBottomLeft);
            l.HudMargin = PnJson.Int(o, "hudMargin", l.HudMargin);
            l.BadgeWidth = PnJson.Int(o, "badgeWidth", l.BadgeWidth);
            return l;
        }

        /// <summary>Environment manifest wins for canvas and scene anchors.</summary>
        public void ApplyEnvironment(object env)
        {
            if (env == null) return;
            Read(env, "canvas", ref Canvas);
            var anchors = PnJson.Obj(env, "anchors");
            if (anchors != null)
            {
                Read(anchors, "banditFeet", ref BanditFeet);
                Read(anchors, "heroBottomLeft", ref HeroBottomLeft);
                Read(anchors, "sun", ref Sun);
            }
        }

        public static void Read(object node, string key, ref Vector2Int v)
        {
            int a = v.x, b = v.y;
            if (PnJson.Pair(node, key, ref a, ref b)) v = new Vector2Int(a, b);
        }
    }

    /// <summary>Canvas (top-left, y down) to world (1 unit = 1 px, origin = canvas centre, y up).</summary>
    public static class Px
    {
        public static int HalfW = 180;
        public static int HalfH = 320;

        public static void SetCanvas(Vector2Int canvas)
        {
            HalfW = canvas.x / 2;
            HalfH = canvas.y / 2;
        }

        public static Vector3 World(float cx, float cy, float z = 0f)
        {
            return new Vector3(cx - HalfW, HalfH - cy, z);
        }

        public static Vector3 World(Vector2Int c)
        {
            return new Vector3(c.x - HalfW, HalfH - c.y, 0f);
        }

        /// <summary>World position snapped to whole pixels.</summary>
        public static Vector3 WorldSnap(float cx, float cy)
        {
            return new Vector3(Mathf.Round(cx) - HalfW, HalfH - Mathf.Round(cy), 0f);
        }

        public static Vector2 Canvas(Vector3 world)
        {
            return new Vector2(world.x + HalfW, HalfH - world.y);
        }
    }

    /// <summary>Palette colours used by code (exact hex from SPEC §3).</summary>
    public static class Pal
    {
        public static readonly Color32 Ink = new Color32(0x1A, 0x10, 0x20, 255);
        public static readonly Color32 Ink2 = new Color32(0x2D, 0x1B, 0x2E, 255);
        public static readonly Color32 ShadowPurp = new Color32(0x4A, 0x2E, 0x45, 255);
        public static readonly Color32 White = new Color32(255, 255, 255, 255);
        public static readonly Color32 Cream0 = new Color32(0xFF, 0xF8, 0xEA, 255);
        public static readonly Color32 Cream1 = new Color32(0xF1, 0xE2, 0xC6, 255);
        public static readonly Color32 Sand0 = new Color32(0xFB, 0xE3, 0xAE, 255);
        public static readonly Color32 Sand3 = new Color32(0xC3, 0x8E, 0x50, 255);
        public static readonly Color32 Wood3 = new Color32(0x6E, 0x44, 0x28, 255);
        public static readonly Color32 Wood4 = new Color32(0x4A, 0x2C, 0x1C, 255);
        public static readonly Color32 Brass0 = new Color32(0xFF, 0xEB, 0x99, 255);
        public static readonly Color32 Brass1 = new Color32(0xF2, 0xC1, 0x4E, 255);
        public static readonly Color32 PaintRed = new Color32(0xB2, 0x43, 0x3A, 255);
        public static readonly Color32 Red1 = new Color32(0xD6, 0x3C, 0x2F, 255);
        public static readonly Color32 Green2 = new Color32(0x57, 0x7E, 0x3C, 255);
        public static readonly Color32 SigGreen = new Color32(0x6B, 0xDB, 0x5E, 255);
        public static readonly Color32 SigRed = new Color32(0xEE, 0x4B, 0x3E, 255);
        public static readonly Color32 SigYellow = new Color32(0xFF, 0xE4, 0x5C, 255);
        public static readonly Color32 Sun1 = new Color32(0xFF, 0xF2, 0xA8, 255);
        public static readonly Color32 Steel1 = new Color32(0xBC, 0xC7, 0xCF, 255);
        public static readonly Color32 Sky0 = new Color32(0x1E, 0x4F, 0x9C, 255);
        public static readonly Color32 Sky3 = new Color32(0x5C, 0x9E, 0xE0, 255);
        public static readonly Color32 Clear = new Color32(0, 0, 0, 0);
    }
}
