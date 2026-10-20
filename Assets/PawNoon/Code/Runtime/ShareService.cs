using System;
using System.IO;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Renders the share card (ShareCardLayout) into a Texture2D from the game's own sprites and bitmap fonts
    /// (CPU composition through PixelCanvas: no camera, no screenshot), encodes a PNG, saves it under
    /// Application.temporaryCachePath and hands it to <see cref="NativeShare"/>. Main thread, on a tap only.
    /// </summary>
    public static class ShareService
    {
        /// <summary>The game's UI kit (fonts + UI sprites). Set by the first ShareButton (PawGame.Build).</summary>
        public static UiKit Kit;

        public const string FileName = "pawnoon_share.png";

        private static readonly Color32 Ink = Pal.Ink;

        /// <summary>Renders, saves and opens the share sheet. Returns the PNG path, or null on failure.</summary>
        public static string Share(ShareCardData d)
        {
            string path = null;
            try
            {
                path = Path.Combine(Application.temporaryCachePath, FileName);
                if (!RenderToFile(d, path)) return null;
                NativeShare.ShareImage(path, ShareCardText.Caption(d));
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] share failed: " + e.Message);
                return null;
            }
            return path;
        }

        /// <summary>Renders the card and writes it as a PNG to <paramref name="path"/> (directories are created).</summary>
        public static bool RenderToFile(ShareCardData d, string path)
        {
            var png = RenderPng(d);
            if (png == null) return false;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);
            return true;
        }

        public static byte[] RenderPng(ShareCardData d)
        {
            var tex = Render(d);
            if (tex == null) return null;
            try { return tex.EncodeToPNG(); }
            finally { DestroyTex(tex); }
        }

        private static void DestroyTex(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>The 540x960 card as a point-filtered Texture2D (caller owns it).</summary>
        public static Texture2D Render(ShareCardData d)
        {
            var kit = Kit;
            if (kit == null || kit.Ui == null) { Debug.LogWarning("[PawNoon] share: no UI kit yet"); return null; }
            var c = Compose(d, kit);
            var o = ShareCardLayout.Output(ShareCardLayout.Scale);
            var tex = new Texture2D(o.x, o.y, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawShareCard";
            tex.SetPixels32(c.Upscaled(ShareCardLayout.Scale));
            tex.Apply(false, false);
            PixelImage.ClearCache();
            return tex;
        }

        private static Vector2Int Size(SheetDef s) { return s != null && s.First != null ? s.Cell : Vector2Int.zero; }

        /// <summary>The 270x480 composition (canvas px).</summary>
        public static PixelCanvas Compose(ShareCardData d, UiKit kit)
        {
            const int W = ShareCardLayout.Width, H = ShareCardLayout.Height;
            var c = new PixelCanvas(W, H);
            c.Fill(Ink);
            bool match = d.kind == ShareKind.Match;

            // --- sprites
            var logo = kit.Ui.TryGet("logo_v3") ?? kit.Ui.TryGet("logo");
            var header = match ? kit.Ui.TryGet(d.matchWinner == 0 ? "banner_win" : "banner_lose") : kit.Ui.TryGet("round_sign");
            var card = kit.Ui.TryGet("result_card");
            string stampName = ShareCardText.Stamp(d);
            var stamp = stampName != null ? kit.Ui.TryGet(stampName) : null;
            Vector2Int oppCell, oppPivot, heroCell, heroPivot;
            var oppTex = CharacterTexture("Bandit", "bandit", out oppCell, out oppPivot);
            var heroTex = CharacterTexture("Hero", "hero", out heroCell, out heroPivot);

            var r = ShareCardLayout.Compute(match, Size(logo), Size(header), Size(card), Size(stamp), oppCell, oppPivot, heroCell);

            // --- backdrop: the street plate (dawn for LAST LIGHT), darkened behind the content band
            var plate = ArtLib.TryTexture("Art/Environment/" + (d.lastLight ? "plate_dawn" : "plate")) ?? ArtLib.TryTexture("Art/Environment/plate");
            if (plate != null) c.BlitRegion(PixelImage.Of(plate), ShareCardLayout.PlateX, ShareCardLayout.PlateY, W, H, 0, 0);
            c.Darken(r.band.y, r.band.yMax, ShareCardLayout.BandKeep, Ink, ShareCardLayout.BandEdgeRows);

            // --- the duel below the band: the opponent in the street, your hat and shoulders in front
            if (oppTex != null) c.BlitRegion(PixelImage.Of(oppTex), 0, 0, oppCell.x, oppCell.y, r.opp.x, r.opp.y, r.footer.y - r.opp.y);
            if (heroTex != null) c.BlitRegion(PixelImage.Of(heroTex), 0, 0, heroCell.x, heroCell.y, r.hero.x, r.hero.y, r.hero.height);

            // --- wordmark
            if (logo != null) c.BlitSprite(logo.First, r.logo.x, r.logo.y);

            // --- header: round sign (ROUND n / LAST LIGHT) or the WIN / LOSE banner
            if (header != null) c.BlitSprite(header.First, r.header.x, r.header.y);
            if (!match)
            {
                int ty = r.header.y + Mathf.Max(0, (r.header.height - kit.Small.LineHeight) / 2) + 1;
                c.Text(kit.Small, ShareCardText.RoundLabel(d), W / 2, ty, d.lastLight ? Pal.SigYellow : Pal.Cream0, Pal.Wood4, TextAlign.Center);
            }
            else if (d.matchWinner < 0)
            {
                c.Text(kit.Medium, "STANDOFF", W / 2, r.header.y + 14, Pal.Cream0, Pal.Ink, TextAlign.Center);
            }

            // --- the round card: both reaction times, the gap, the damage, the stamp
            if (card != null) c.BlitSprite(card.First, r.card.x, r.card.y);
            else c.FillRect(r.card, Pal.Cream1);
            int ix = r.card.x + ShareCardLayout.CardInsetX, iy = r.card.y + ShareCardLayout.CardInsetY;
            int rx = r.card.xMax - ShareCardLayout.CardInsetX;
            var none = default(Color32);
            c.Text(kit.Medium, d.youName, ix, iy + 2, Pal.Ink, none, TextAlign.Left);
            c.Text(kit.Timer, ShareCardText.YouTime(d), rx, iy - 2, Pal.Ink, none, TextAlign.Right, true);
            c.Text(kit.Small, d.oppName, ix, iy + 20, Pal.Ink, none, TextAlign.Left);
            c.Text(kit.Medium, ShareCardText.OppTime(d), rx, iy + 18, Pal.Ink, none, TextAlign.Right, true);
            ShareCardText.Tone tone;
            string gap = ShareCardText.Gap(d, out tone);
            c.Text(kit.Medium, gap, ix, iy + 36, ToneColor(tone, true), none, TextAlign.Left);
            string dmg = ShareCardText.Damage(d, out tone);
            c.Text(kit.Medium, dmg, ix, iy + 50, ToneColor(tone, false), none, TextAlign.Left);
            if (stamp != null) c.BlitSprite(stamp.First, r.stamp.x, r.stamp.y);

            // --- the replay strip, pixel-identical to the one under the round card (landed state)
            DrawStrip(c, kit, d, r.strip);

            // --- call to action, footer
            c.Text(kit.Medium, ShareCardText.CallToAction, W / 2, r.ctaY, Pal.SigYellow, Pal.Ink, TextAlign.Center);
            c.FillRect(r.footer, Ink);
            c.FillRect(new RectInt(0, r.footer.y, W, 1), Pal.Wood4);
            int fy = r.footer.y + (ShareCardLayout.FooterH - kit.Small.LineHeight) / 2 + 1;
            string mode = ShareCardText.Mode(d);
            c.Text(kit.Small, mode, 6, fy, mode == "EASY" ? Pal.SigGreen : Pal.Brass1, none, TextAlign.Left);
            c.Text(kit.Small, ShareCardText.Tag, W / 2, fy, Pal.Cream0, none, TextAlign.Center);
            c.Text(kit.Small, ShareCardText.Date(d.date), W - 6, fy, Pal.Sand3, none, TextAlign.Right);
            return c;
        }

        private static Color32 ToneColor(ShareCardText.Tone t, bool gapLine)
        {
            switch (t)
            {
                case ShareCardText.Tone.Good: return Pal.Green2;
                case ShareCardText.Tone.Bad: return Pal.PaintRed;
                case ShareCardText.Tone.Tie: return new Color32(0xFF, 0x9C, 0x3D, 255);   // sigOrange, as the tie line
                default: return gapLine ? Pal.Ink : Pal.Wood4;
            }
        }

        private static void DrawStrip(PixelCanvas c, UiKit kit, ShareCardData d, RectInt at)
        {
            var m = d.replay;
            if (!m.valid) m = ReplayMarks.Shots(Math.Max(0, d.youNs), Math.Max(0, d.oppNs), 0f);
            double span = ReplayStripMath.SpanMs(m);
            int youX, oppX;
            ReplayStripMath.MarkXs(m, span, out youX, out oppX);
            bool hasGap = ReplayStripMath.HasGap(m);
            ReplayStripPainter.Paint(c.Pixels, c.Width, c.Height, at.x, at.y, m, span, youX, oppX, hasGap,
                ReplayStripMath.Width, true, 0, true);
            var f = kit.Small;
            int yw = PixelCanvas.TextWidth(f, d.youName, false), ow = PixelCanvas.TextWidth(f, d.oppName, false);
            int yl, ol;
            ReplayStripMath.PlaceLabels(youX, yw, oppX, ow, out yl, out ol);
            var none = default(Color32);
            c.Text(f, d.youName, at.x + yl, at.y + ReplayStripMath.LabelTop, ReplayStripPainter.YouCol, none, TextAlign.Left);
            c.Text(f, d.oppName, at.x + ol, at.y + ReplayStripMath.LabelTop, ReplayStripPainter.OppCol, none, TextAlign.Left);
            if (hasGap)
            {
                string g = ReplayStripMath.GapText(m.oppNs - m.youNs);
                int gx = ReplayStripMath.Centered((youX + oppX) / 2, PixelCanvas.TextWidth(f, g, false));
                c.Text(f, g, at.x + gx, at.y + ReplayStripMath.GapLabelTop, ReplayStripPainter.GapCol, none, TextAlign.Left);
            }
        }

        /// <summary>Idle sheet of a character (frame 0 is the top-left cell) and its cell / pivot from the manifest.</summary>
        private static Texture2D CharacterTexture(string folder, string name, out Vector2Int cell, out Vector2Int pivot)
        {
            string dir = "Art/Characters/" + folder + "/";
            var root = ArtLib.Json(dir + name, false);
            cell = new Vector2Int(117, 120);
            pivot = new Vector2Int(60, 119);
            int cx = cell.x, cy = cell.y, px = pivot.x, py = pivot.y;
            PnJson.Pair(root, "cell", ref cx, ref cy);
            PnJson.Pair(root, "pivot", ref px, ref py);
            cell = new Vector2Int(cx, cy);
            pivot = new Vector2Int(px, py);
            string file = PnJson.Str(PnJson.Obj(PnJson.Obj(root, "anims"), "idle"), "file", name + "_idle");
            return ArtLib.TryTexture(dir + file);
        }
    }

    /// <summary>
    /// The OS share sheet. iOS: UIActivityViewController (Plugins/iOS/PawShare.mm). Android: ACTION_SEND with a
    /// FileProvider content:// URI (needs the provider in the manifest, see Docs/DECISIONS.md "Feel wave 2");
    /// without it the text alone is shared. Editor / desktop: the PNG is copied next to the project
    /// (&lt;project&gt;/Share/) and the path is logged.
    /// </summary>
    public static class NativeShare
    {
        /// <summary>FileProvider authority suffix (full authority = applicationId + this).</summary>
        public const string AndroidAuthoritySuffix = ".pawshare";

        /// <summary>Last path handed to the OS (review / status).</summary>
        public static string LastPath;

#if UNITY_IOS && !UNITY_EDITOR
        [System.Runtime.InteropServices.DllImport("__Internal")]
        private static extern void PawShare_ShareImage(string path, string text);
#endif

        public static void ShareImage(string path, string text)
        {
            LastPath = path;
#if UNITY_IOS && !UNITY_EDITOR
            PawShare_ShareImage(path, text ?? "");
#elif UNITY_ANDROID && !UNITY_EDITOR
            AndroidShare(path, text);
#else
            EditorShare(path);
#endif
        }

#if !UNITY_IOS && !UNITY_ANDROID || UNITY_EDITOR
        private static void EditorShare(string path)
        {
            try
            {
                string dir = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Share"));
                Directory.CreateDirectory(dir);
                string dst = Path.Combine(dir, "share_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                File.Copy(path, dst, true);
                LastPath = dst;
                Debug.Log("[PawNoon] share card written: " + dst);
            }
            catch (Exception e)
            {
                Debug.Log("[PawNoon] share card at " + path + " (copy next to the project failed: " + e.Message + ")");
            }
        }
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private static void AndroidShare(string path, string text)
        {
            try
            {
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                using (var activity = player.GetStatic<AndroidJavaObject>("currentActivity"))
                using (var intent = new AndroidJavaObject("android.content.Intent"))
                {
                    intent.Call<AndroidJavaObject>("setAction", "android.intent.action.SEND");
                    if (!string.IsNullOrEmpty(text)) intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.TEXT", text);
                    AndroidJavaObject uri = null;
                    try
                    {
                        using (var file = new AndroidJavaObject("java.io.File", path))
                        using (var provider = new AndroidJavaClass("androidx.core.content.FileProvider"))
                        {
                            string authority = activity.Call<string>("getPackageName") + AndroidAuthoritySuffix;
                            uri = provider.CallStatic<AndroidJavaObject>("getUriForFile", activity, authority, file);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[PawNoon] share: no FileProvider (" + e.Message + "), sharing text only");
                        uri = null;
                    }
                    if (uri != null)
                    {
                        intent.Call<AndroidJavaObject>("setType", "image/png");
                        intent.Call<AndroidJavaObject>("putExtra", "android.intent.extra.STREAM", uri);
                        intent.Call<AndroidJavaObject>("addFlags", 1);   // FLAG_GRANT_READ_URI_PERMISSION
                        uri.Dispose();
                    }
                    else intent.Call<AndroidJavaObject>("setType", "text/plain");
                    using (var chooser = new AndroidJavaClass("android.content.Intent").CallStatic<AndroidJavaObject>("createChooser", intent, "Share your draw"))
                        activity.Call("startActivity", chooser);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[PawNoon] share sheet failed: " + e.Message);
            }
        }
#endif
    }
}
