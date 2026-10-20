using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Top HUD inside the safe area: corner portraits (hurt swap) with name plates, HP bars with ghost
    /// drain (pixel-cropped, never scaled), round sign, optional centre badge, and the tap hint.
    /// </summary>
    public sealed class DuelHud : MonoBehaviour
    {
        private sealed class Side
        {
            public SpriteRenderer frame, portrait, backdrop, plate, barFrame, fill, ghost;
            public Vector2Int normalOff, hurtOff;
            public BitmapText name;
            public Sprite normal, hurt;
            public float shown = 1f;   // presented hp fraction
            public float ghostV = 1f;
            public float ghostDelay;
            public float hurtLeft;
            public bool right;
            public int lastW = -1, lastGhostW = -1;
            public bool lastLow;
            public Vector2Int barPos;
            public Vector2Int portraitPos;
            // name plate: resting rect (Relayout) + the match intro's slam (hidden / vertical offset)
            public int plateX, plateTop, plateW;
            public int plateDy;
            public bool plateHidden;
            // floating "-34" lost-HP text
            public BitmapText lost;
            public float lostT = 99f;
            public int lostLastY = int.MinValue;
        }

        private const int Order = 200;
        private const float LostSeconds = 0.8f;
        private const int LostRise = 12;
        private readonly char[] lostBuf = new char[8];
        private SheetDef roundSignDef;
        /// <summary>Full HP in game units (so a hit's HP fraction can be shown as "-34"). Set before Init.</summary>
        public float MaxHp = 100f;
        private UiKit kit;
        private readonly Side[] sides = { new Side(), new Side() };
        private Sprite[] fillCrops, fillCropsR, lowCrops, lowCropsR, ghostCrops, ghostCropsR;
        private int barW = 64, barH = 6;
        private RectInt barInner = new RectInt(2, 2, 64, 6);
        private Vector2Int frameSize = new Vector2Int(32, 32);
        private RectInt portraitInner = new RectInt(2, 2, 28, 28);
        private SpriteRenderer roundSign;
        private BitmapText roundText;
        private BitmapText diffText;   // "EASY" / "HARD" tag right of the round sign (text only)
        private SpriteRenderer badge;
        private BadgeTitle badgeTitle;   // "LAST LIGHT" over the baked HIGH NOON (feel wave 2)
        private SpriteRenderer hint;
        private bool hintOn, quietOn, onceUsed, reducedMotion;
        // swipe tutorial (sign + ghost paw)
        private SpriteRenderer signPanel, ghostPaw;
        private BitmapText signText;
        // painted swipe tutorial (src_holster.png): the sign sprite and the 4-frame paw-yank overlay drawn over the holster
        private SheetDef signDef, demoDef;
        private SpriteRenderer signSprite, demo;
        private int demoX, demoY;
        private bool drawnShown;
        private SpriteRenderer[] trail;
        private float[] trailAge;
        private float signT, pawT, quietLeft, lastTrailY;
        private bool ghostGlow;
        private int signX, signY, pawX, pawY, pawRise = 56;
        private const int SignW = 72, SignH = 16, TrailN = 12;
        private static readonly int[] Swing = { -1, 1, -1, 1, 0 };
        private SpriteRenderer arrow;
        private SheetDef holsterDef;
        private bool holsterVisible;
        private bool restingShown;
        private SpriteAnimator hintAnim;
        private BitmapText hint1, hint2;
        private GameLayout layout;
        private int laidOutVersion = -1;
        private PixelPipeline pipe;
        private int roundShown = -1;
        private float signPop;

        public void Init(UiKit k, GameLayout l, PixelPipeline p, CharacterDef hero, CharacterDef bandit, string heroName, string banditName)
        {
            kit = k;
            layout = l;
            pipe = p;
            var pf = kit.Ui.Get("portrait_frame");
            frameSize = pf.Cell;
            if (pf.HasInner) portraitInner = pf.Inner;
            var hf = kit.Ui.Get("hp_frame");
            if (hf.HasInner) barInner = hf.Inner;
            var fill = kit.Ui.Get("hp_fill");
            barW = Mathf.Max(1, fill.Cell.x);
            barH = Mathf.Max(1, fill.Cell.y);
            fillCrops = Crops(fill, false); fillCropsR = Crops(fill, true);
            var low = kit.Ui.Get("hp_fill_low");
            lowCrops = Crops(low, false); lowCropsR = Crops(low, true);
            var gh = kit.Ui.Get("hp_ghost");
            ghostCrops = Crops(gh, false); ghostCropsR = Crops(gh, true);

            for (int i = 0; i < 2; i++)
            {
                var s = sides[i];
                s.right = i == 1;
                var def = i == 0 ? hero : bandit;
                s.normal = FitPortrait(def.Portrait, out s.normalOff);
                s.hurt = def.PortraitHurt == def.Portrait ? s.normal : FitPortrait(def.PortraitHurt, out s.hurtOff);
                if (def.PortraitHurt == def.Portrait) s.hurtOff = s.normalOff;
                var bd = kit.Ui.TryGet("portrait_backdrop");
                s.backdrop = PawGfx.NewSprite("PortraitBack" + i, transform, Order - 1);
                if (bd != null) s.backdrop.sprite = bd.First;
                s.portrait = PawGfx.NewSprite("Portrait" + i, transform, Order);
                s.portrait.sprite = s.normal;
                s.frame = kit.Sprite("portrait_frame", transform, Order + 1);
                s.barFrame = kit.Sprite("hp_frame", transform, Order);
                s.ghost = PawGfx.NewSprite("Ghost" + i, transform, Order + 1);
                s.fill = PawGfx.NewSprite("Fill" + i, transform, Order + 2);
                s.plate = kit.Panel("name_plate", transform, Order + 2, new RectInt(0, 0, 18, 14));
                s.name = kit.Text("Name" + i, transform, kit.Medium, Order + 3, s.right ? TextAlign.Right : TextAlign.Left, i == 0 ? heroName : banditName);
                s.name.SetColors(Pal.Brass0, Pal.Ink, Pal.Ink2);
                s.name.Shadow = false;
                s.lost = BitmapText.Create("Lost" + i, transform, kit.Small, Order + 6, TextAlign.Center);
                s.lost.SetColors(Pal.SigRed, Pal.Ink, Pal.Ink2);
                s.lost.Visible = false;
            }
            roundSignDef = kit.Ui.Get("round_sign");

            roundSign = kit.Sprite("round_sign", transform, Order);
            roundText = kit.Text("RoundText", transform, kit.Small, Order + 2, TextAlign.Center, "ROUND 1");
            roundText.SetColors(Pal.Cream0, Pal.Wood4, Pal.Ink2);
            roundText.Shadow = false;
            diffText = kit.Text("DifficultyTag", transform, kit.Small, Order + 2, TextAlign.Left, "HARD");
            diffText.Shadow = false;
            SetDifficulty(false);

            var b = kit.Ui.TryGet("hud_badge");
            if (b != null)
            {
                badge = PawGfx.NewSprite("Badge", transform, 150);
                badge.sprite = b.First;
                badgeTitle = new BadgeTitle(kit, transform, 151);
            }

            // holster widget (SPEC §11): holster + grip at the bottom centre, arrow + text above it
            holsterDef = kit.Ui.TryGet("holster_zone");
            hint = PawGfx.NewSprite("Holster", transform, Order);
            if (holsterDef != null) hint.sprite = holsterDef.Frames[0];
            var ar = kit.Ui.TryGet("swipe_arrow");
            arrow = PawGfx.NewSprite("SwipeArrow", transform, Order);
            hintAnim = arrow.gameObject.AddComponent<SpriteAnimator>();
            hintAnim.Renderer = arrow;
            if (ar != null) hintAnim.Play(ar);
            // the old flat text lines are retired in favour of the sign + ghost paw (kept so fields stay valid)
            hint1 = kit.Text("Hint1", transform, kit.Small, Order, TextAlign.Center, "");
            hint2 = kit.Text("Hint2", transform, kit.Small, Order, TextAlign.Center, "");
            hint1.Visible = false;
            hint2.Visible = false;
            arrow.enabled = false;
            BuildSwipeDemo();
            ShowHint(false);
            Relayout();
        }

        /// <summary>
        /// Fits a portrait into the frame's window (portraitInner, 36x36): crops anything larger (keeping the
        /// bottom and the horizontal centre) and returns the offset that bottom-centres it in the window.
        /// </summary>
        private Sprite FitPortrait(Sprite src, out Vector2Int off)
        {
            off = Vector2Int.zero;
            if (src == null) return null;
            int ww = portraitInner.width, wh = portraitInner.height;
            int w = (int)src.rect.width, h = (int)src.rect.height;
            if (w <= ww && h <= wh)
            {
                off = new Vector2Int((ww - w) / 2, wh - h);
                return src;
            }
            int cw = Mathf.Min(w, ww), ch = Mathf.Min(h, wh);
            var tex = src.texture;
            int sx = (int)src.rect.x;
            int syTop = tex.height - (int)src.rect.yMax;
            var crop = ArtLib.MakeSprite(tex, new RectInt(sx + (w - cw) / 2, syTop + (h - ch), cw, ch), Vector2Int.zero, Vector4.zero);
            off = new Vector2Int((ww - cw) / 2, wh - ch);
            return crop;
        }

        private Sprite[] Crops(SheetDef d, bool fromRight)
        {
            var arr = new Sprite[barW + 1];
            var src = d.First;
            if (src == null) return arr;
            var tex = src.texture;
            int sx = (int)src.rect.x;
            int syTop = tex.height - (int)src.rect.yMax;
            int w0 = Mathf.Min(barW, (int)src.rect.width);
            for (int w = 1; w <= barW; w++)
            {
                int ww = Mathf.Min(w, w0);
                int x = fromRight ? sx + w0 - ww : sx;
                arr[w] = ArtLib.MakeSprite(tex, new RectInt(x, syTop, ww, Mathf.Min(barH, (int)src.rect.height)), Vector2Int.zero, Vector4.zero);
            }
            return arr;
        }

        public void SetVisible(bool v) { gameObject.SetActive(v); }

        /// <summary>Shows the swipe tutorial (hanging sign + ghost paw demo). The holster itself stays visible during a live round.</summary>
        public void ShowHint(bool v)
        {
            if (v == hintOn && !quietOn) return;
            hintOn = v;
            quietOn = false;
            StartDemo(v);
        }

        /// <summary>Quiet reminder (sign only, no paw) for a player who never swiped. Shows once, auto-hides.</summary>
        public void ShowHintOnce()
        {
            if (onceUsed || hintOn) return;
            onceUsed = true;
            quietOn = true;
            quietLeft = 3f;
            StartDemo(true);
            ghostPaw.enabled = false;
        }

        public void SetReducedMotion(bool on) { reducedMotion = on; }

        private void StartDemo(bool on)
        {
            signT = 0f;
            pawT = 0f;
            ghostGlow = false;
            bool painted = signDef != null;
            signPanel.enabled = on && holsterVisible && !painted;
            signText.Visible = on && holsterVisible && !painted;
            signSprite.enabled = on && holsterVisible && painted;
            ghostPaw.enabled = false;
            demo.enabled = false;
            for (int i = 0; i < TrailN; i++) { trailAge[i] = -1f; trail[i].enabled = false; }
            ApplyHolsterFrame();
            PlaceSign();
        }

        private void BuildSwipeDemo()
        {
            signPanel = kit.Panel("panel_wood", transform, Order + 3, new RectInt(0, 0, SignW, SignH));
            signText = kit.Text("SignText", transform, kit.Small, Order + 5, TextAlign.Center, "YANK IT UP!");
            signText.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink);
            signText.Shadow = false;
            signDef = kit.Ui.TryGet("hint_sign");
            signSprite = PawGfx.NewSprite("HintSign", transform, Order + 5);
            if (signDef != null) signSprite.sprite = signDef.Frames[0];
            signSprite.enabled = false;
            demoDef = kit.Ui.TryGet("holster_demo");
            demo = PawGfx.NewSprite("HolsterDemo", transform, Order + 1);
            demo.enabled = false;
            ghostPaw = PawGfx.NewSprite("GhostPaw", transform, Order + 6);
            ghostPaw.sprite = MakePawSprite();
            trail = new SpriteRenderer[TrailN];
            trailAge = new float[TrailN];
            for (int i = 0; i < TrailN; i++)
            {
                var d = PawGfx.NewSprite("PawTrail" + i, transform, Order + 4);
                d.sprite = ArtLib.WhitePixel;
                d.transform.localScale = new Vector3(2f, 2f, 1f);
                d.enabled = false;
                trail[i] = d;
                trailAge[i] = -1f;
            }
            StartDemo(false);
        }

        /// <summary>12x14 texture: 10x12 paw (pad + 4 toes), cream fill, 1 px ink outline. Tint alpha via the renderer.</summary>
        private static Sprite MakePawSprite()
        {
            const int W = 12, H = 14;
            // build in 10x12 body space via simple shapes (clearer than a literal map)
            var body = new bool[10, 12];
            for (int y = 0; y < 3; y++) for (int x = 0; x < 2; x++) { body[2 + x, y] = true; body[6 + x, y] = true; }
            for (int y = 3; y < 6; y++) for (int x = 0; x < 2; x++) { body[x, y] = true; body[8 + x, y] = true; }
            for (int y = 6; y < 12; y++)
            {
                int x0 = (y == 6 || y == 11) ? 2 : 1;
                int x1 = (y == 6 || y == 11) ? 7 : 8;
                for (int x = x0; x <= x1; x++) body[x, y] = true;
            }
            var tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawGhost";
            var clear = new Color32(0, 0, 0, 0);
            for (int ty = 0; ty < H; ty++)
                for (int x = 0; x < W; x++)
                {
                    // canvas-down row ty -> body coords (x-1, ty-1)
                    int bx = x - 1, by = ty - 1;
                    Color32 c = clear;
                    if (Fill(body, bx, by)) c = Pal.Cream0;
                    else if (Fill(body, bx - 1, by) || Fill(body, bx + 1, by) || Fill(body, bx, by - 1) || Fill(body, bx, by + 1)) c = Pal.Ink;
                    tex.SetPixel(x, H - 1 - ty, c);
                }
            tex.Apply(false, true);
            return ArtLib.MakeSprite(tex, new RectInt(0, 0, W, H), Vector2Int.zero, Vector4.zero);
        }

        private static bool Fill(bool[,] b, int x, int y)
        {
            return x >= 0 && y >= 0 && x < 10 && y < 12 && b[x, y];
        }

        private void ApplyHolsterFrame()
        {
            if (holsterDef == null) return;
            bool glow = restingShown || ghostGlow;
            int f = drawnShown && holsterDef.Count > 2 ? 2 : (glow && holsterDef.Count > 1 ? 1 : 0);
            hint.sprite = holsterDef.Frames[f];
        }

        /// <summary>Holster empty (gun drawn this exchange) vs. holstered; frame 2 of holster_zone when the art has it.</summary>
        public void SetDrawn(bool drawn)
        {
            if (drawn == drawnShown) return;
            drawnShown = drawn;
            ApplyHolsterFrame();
        }

        private void PlaceSign()
        {
            int sw = 0;
            if (!reducedMotion && signT < 0.35f) sw = Swing[Mathf.Min(Swing.Length - 1, (int)(signT / 0.07f))];
            int x = signX + sw;
            if (signDef != null) { PawGfx.Place(signSprite, x + SignW / 2 - signDef.Cell.x / 2 + signDef.Pivot.x, signY + signDef.Pivot.y); return; }
            UiKit.SetRect(signPanel, kit.Ui.Get("panel_wood"), new RectInt(x, signY, SignW, SignH));
            signText.At(x + SignW / 2, signY + Mathf.Max(0, (SignH - signText.Height) / 2));
        }

        private void TickDemo(float dt)
        {
            if (!signPanel.enabled && !signSprite.enabled) return;
            signT += dt;
            PlaceSign();
            if (quietOn)
            {
                quietLeft -= dt;
                if (quietLeft <= 0f) { quietOn = false; StartDemo(false); }
                return;
            }
            if (demoDef != null && demoDef.Count >= 4)
            {
                // painted yank: grip 0.5 s (holster glows) -> 2 rising frames -> gun out with sparkle -> rest (2.2 s cycle).
                // Drawn over the holster as a stepped ghost (never opaque): a suggestion, not a hand on the gun.
                pawT += dt;
                if (pawT >= 2.2f) pawT -= 2.2f;
                float pt = pawT;
                int frame = pt < 0.5f ? 0 : (pt < 0.65f ? 1 : (pt < 0.85f ? 2 : (pt < 1.15f ? 3 : -1)));
                ghostGlow = pt < 0.5f;
                ApplyHolsterFrame();
                bool demoOn = frame >= 0 && !drawnShown;
                demo.enabled = demoOn;
                if (demoOn)
                {
                    float fa = pt < 0.12f ? pt / 0.12f : (pt < 0.95f ? 1f : 1f - (pt - 0.95f) / 0.2f);
                    demo.sprite = demoDef.Frames[frame];
                    demo.color = new Color(1f, 1f, 1f, 0.6f * Stepped(fa));   // a ghost of a hand, never a solid one
                    PawGfx.Place(demo, demoX, demoY);
                }
                return;
            }
            // ghost paw cycle: rest 0.5 s -> rise 0.35 s -> fade 0.25 s -> pause (2.2 s total)
            pawT += dt;
            if (pawT >= 2.2f) { pawT -= 2.2f; lastTrailY = 0f; }
            float t = pawT;
            float rise = t < 0.5f ? 0f : Mathf.Clamp01((t - 0.5f) / 0.35f);
            float ease = 1f - (1f - rise) * (1f - rise);
            float a = 0f;
            if (t < 1.1f) a = t < 0.1f ? t / 0.1f : (t < 0.85f ? 1f : 1f - (t - 0.85f) / 0.25f);
            bool show = a > 0.01f;
            ghostPaw.enabled = show;
            ghostGlow = t < 0.5f;
            ApplyHolsterFrame();
            int py = pawY - Mathf.RoundToInt(ease * pawRise);
            if (show)
            {
                ghostPaw.color = new Color(1f, 1f, 1f, 0.55f * Stepped(a));
                PawGfx.Place(ghostPaw, pawX, py);
            }
            // dotted trail: one 2x2 dot every 6 px travelled
            if (t >= 0.5f && t < 0.85f)
            {
                float travelled = pawY - py;
                while (lastTrailY + 6f <= travelled)
                {
                    lastTrailY += 6f;
                    SpawnDot(pawX + 5, pawY - Mathf.RoundToInt(lastTrailY) + 12);
                }
            }
            for (int i = 0; i < TrailN; i++)
            {
                if (trailAge[i] < 0f) continue;
                trailAge[i] += dt;
                float k = 1f - trailAge[i] / 0.4f;
                if (k <= 0f) { trailAge[i] = -1f; trail[i].enabled = false; continue; }
                trail[i].color = new Color(Pal.Cream0.r / 255f, Pal.Cream0.g / 255f, Pal.Cream0.b / 255f, 0.8f * Stepped(k));
            }
        }

        /// <summary>
        /// Quantizes a 0..1 fade to 3 visible steps (1, 2/3, 1/3, then off): the swipe hint fades in hard
        /// steps like the ECG trail instead of a smooth alpha ramp (house rule: no smooth alpha glows).
        /// </summary>
        private static float Stepped(float v)
        {
            return Mathf.Ceil(Mathf.Clamp01(v) * 3f - 0.001f) / 3f;
        }

        private void SpawnDot(int x, int y)
        {
            int slot = 0;
            float oldest = -1f;
            for (int i = 0; i < TrailN; i++)
            {
                if (trailAge[i] < 0f) { slot = i; oldest = -2f; break; }
                if (trailAge[i] > oldest) { oldest = trailAge[i]; slot = i; }
            }
            trailAge[slot] = 0f;
            trail[slot].enabled = true;
            // white pixel pivots bottom-left: anchor at the dot's bottom edge
            PawGfx.Place(trail[slot], x, y + 2);
        }

        /// <summary>Holster graphic on/off (on for the whole duel, off on menus).</summary>
        public void ShowHolster(bool v)
        {
            holsterVisible = v;
            hint.enabled = v && holsterDef != null;
            if (!v) { hintOn = false; quietOn = false; StartDemo(false); }
            else if (hintOn || quietOn) StartDemo(true);
        }

        /// <summary>
        /// Swaps the holster art for a loadout (M7: "holster_peacemaker"). Same footprint as holster_zone, so the
        /// layout does not move; an unknown key keeps the current art.
        /// </summary>
        public void SetHolster(string key)
        {
            var d = kit.Ui.TryGet(key);
            if (d == null || d == holsterDef) return;
            holsterDef = d;
            ApplyHolsterFrame();
        }

        /// <summary>Grip glows while a thumb rests in the zone.</summary>
        public void SetResting(bool resting)
        {
            if (holsterDef == null || resting == restingShown) return;
            restingShown = resting;
            ApplyHolsterFrame();
        }

        public void SetRound(int n)
        {
            if (n == roundShown) return;
            roundShown = n;
            roundText.SetText("ROUND " + n);
            roundText.Color = Pal.Cream0;
            roundText.MarkDirty();
            signPop = reducedMotion ? 0f : 0.35f; // Reduced Motion: the sign just changes
        }

        /// <summary>
        /// A named round instead of "ROUND N" (sudden death: "LAST LIGHT", signal yellow). The next SetRound(n) restores
        /// the numbered sign. Pops like a round change once per label.
        /// </summary>
        public void SetRoundLabel(string label)
        {
            const int Named = int.MinValue;
            if (roundShown == Named && labelShown == label) return;
            roundShown = Named;
            labelShown = label;
            roundText.SetText(label);
            roundText.Color = Pal.SigYellow;
            roundText.MarkDirty();
            signPop = reducedMotion ? 0f : 0.35f;
        }

        private string labelShown;

        /// <summary>Difficulty tag beside the round sign (set when a match's rules are applied).</summary>
        public void SetDifficulty(bool easy)
        {
            diffText.SetText(easy ? "EASY" : "HARD");
            diffText.SetColors(easy ? Pal.SigGreen : Pal.Brass1, Pal.Ink, Pal.Ink2);
            diffText.MarkDirty();
        }

        private void PlaceRoundSign(SheetDef rs, int cx, int ry)
        {
            PawGfx.Place(roundSign, cx - rs.Cell.x / 2 + rs.Pivot.x, ry + rs.Pivot.y);
            int ty = ry + Mathf.Max(0, (rs.Cell.y - roundText.Height) / 2) + 1;
            roundText.At(cx, ty);
            // tag sits just right of the sign on the same text row (the row below the sign belongs to the result card)
            diffText.At(cx - rs.Cell.x / 2 + rs.Cell.x + 3, ty);
        }

        public void ShowBadge(bool v)
        {
            if (badge != null) badge.enabled = v;
            if (badgeTitle != null) badgeTitle.SetBadgeShown(v);
        }

        /// <summary>Sudden death: the badge's title reads LAST LIGHT instead of the baked HIGH NOON.</summary>
        public void SetBadgeLastLight(bool on) { if (badgeTitle != null) badgeTitle.Show(on); }

        public bool BadgeLastLightShown { get { return badgeTitle != null && badgeTitle.Visible; } }

        /// <summary>Snap HP display (new match / review).</summary>
        public void SetHpImmediate(int slot, float frac)
        {
            var s = sides[slot];
            s.shown = frac;
            s.ghostV = frac;
            s.ghostDelay = 0f;
            s.lostT = 99f;
            if (s.lost != null) s.lost.Visible = false;
        }

        /// <summary>Presented HP drop (call at impact time).</summary>
        public void Hit(int slot, float frac)
        {
            var s = sides[slot];
            float before = s.shown;
            s.shown = Mathf.Clamp01(frac);
            s.ghostDelay = 0.35f;
            s.hurtLeft = 0.9f;
            int lostHp = Mathf.RoundToInt((before - s.shown) * MaxHp);
            if (lostHp > 0 && s.lost != null) StartLost(s, lostHp);
        }

        private void StartLost(Side s, int hp)
        {
            lostBuf[0] = '-';
            int n = NumFmt.WriteInt(hp, lostBuf, 1);
            s.lost.SetChars(lostBuf, n);
            s.lostT = 0f;
            s.lostLastY = int.MinValue;
            s.lost.Visible = true;
            PlaceLost(s);
        }

        /// <summary>Rises LostRise px in whole-pixel steps (ease-out) and blinks off for the last 0.2 s; Reduced Motion: static.</summary>
        private void PlaceLost(Side s)
        {
            int rise = reducedMotion ? 0 : Mathf.RoundToInt(LostRise * Ease.OutQuad(s.lostT / (LostSeconds * 0.75f)));
            int x = s.portraitPos.x + portraitInner.width / 2;
            int y = s.portraitPos.y + portraitInner.height - kit.Small.LineHeight - 2 - rise;
            if (y == s.lostLastY) return;
            s.lostLastY = y;
            s.lost.At(x, y);
        }

        public void Hurt(int slot, float seconds) { sides[slot].hurtLeft = seconds; }

        /// <summary>Name on a slot's plate (the opponent's persona name; re-lays the plate out to fit).</summary>
        public void SetName(int slot, string name)
        {
            var s = sides[slot];
            if (s.name == null) return;
            s.name.SetText(name ?? "");
            s.name.MarkDirty();
            if (kit != null) Relayout();
        }

        /// <summary>
        /// Match intro (SPEC_V2 §8.7): hides a name plate or offsets it vertically by whole pixels (the slam).
        /// SetNamePlate(slot, true, 0) restores the resting plate.
        /// </summary>
        public void SetNamePlate(int slot, bool visible, int dy)
        {
            var s = sides[slot];
            if (s.plate == null || (s.plateHidden == !visible && s.plateDy == dy)) return;
            s.plateHidden = !visible;
            s.plateDy = dy;
            PlacePlate(s);
        }

        private void PlacePlate(Side s)
        {
            int y = s.plateTop + s.plateDy;
            UiKit.SetRect(s.plate, kit.Ui.Get("name_plate"), new RectInt(s.plateX, y, s.plateW, 14));
            s.name.At(s.right ? s.plateX + s.plateW - 6 : s.plateX + 6, y + 2);
            s.plate.enabled = !s.plateHidden;
            s.name.Visible = !s.plateHidden;
        }

        private void Relayout()
        {
            laidOutVersion = pipe.LayoutVersion;
            var safe = pipe.Safe;
            int m = layout.HudMargin;
            int top = safe.y + m;
            for (int i = 0; i < 2; i++)
            {
                var s = sides[i];
                int fx = s.right ? safe.xMax - m - frameSize.x : safe.xMin + m;
                PawGfx.Place(s.frame, fx, top);
                s.portraitPos = new Vector2Int(fx + portraitInner.x, top + portraitInner.y);
                PawGfx.Place(s.backdrop, s.portraitPos.x, s.portraitPos.y);
                PawGfx.Place(s.portrait, s.portraitPos.x + s.normalOff.x, s.portraitPos.y + s.normalOff.y);
                var bf = kit.Ui.Get("hp_frame");
                int bw = bf.Cell.x;
                int bx = s.right ? fx - 3 - bw : fx + frameSize.x + 3;
                int by = top + 14;
                // name plate sits on the bar's outer edge, same row on both sides
                s.plateW = s.name.Width + 12;
                s.plateX = s.right ? bx + bw - s.plateW : bx;
                s.plateTop = top;
                PlacePlate(s);
                PawGfx.Place(s.barFrame, bx, by);
                s.barPos = new Vector2Int(bx + barInner.x, by + barInner.y);
                s.lastW = -1;
                s.lastGhostW = -1;
                s.lostLastY = int.MinValue;
            }
            var rs = kit.Ui.Get("round_sign");
            int cx = pipe.Visible.x + pipe.Visible.width / 2;
            int ry = top + frameSize.y + 2;
            PlaceRoundSign(rs, cx, ry);
            if (badge != null)
            {
                var b = kit.Ui.Get("hud_badge");
                // centre the badge's numeral WINDOW (not its cell) on the cue point
                int bwx = 0, bwy = 0, bww = b.Cell.x, bwh = b.Cell.y;
                if (b.HasInner) { bwx = b.Inner.x; bwy = b.Inner.y; bww = b.Inner.width; bwh = b.Inner.height; }
                int bx = layout.Cue.x - (bwx + bww / 2);
                int by = layout.Cue.y - (bwy + bwh / 2);
                PawGfx.Place(badge, bx + b.Pivot.x, by + b.Pivot.y);
                if (badgeTitle != null) badgeTitle.Place(bx, by);
            }
            // holster: bottom centre of the safe area (thumb reach), arrow and text stacked above it
            int hcx = pipe.Visible.x + pipe.Visible.width / 2;
            int hbottom = safe.yMax - 6;
            int htop = hbottom - (holsterDef != null ? holsterDef.Cell.y : 40);
            int lh = kit.Small.LineHeight;
            if (holsterDef != null) PawGfx.Place(hint, hcx - holsterDef.Cell.x / 2 + holsterDef.Pivot.x, hbottom - holsterDef.Cell.y + holsterDef.Pivot.y);
            if (demoDef != null) { demoX = hcx - demoDef.Cell.x / 2 + demoDef.Pivot.x; demoY = hbottom - demoDef.Cell.y + demoDef.Pivot.y; }
            // stack, top to bottom: swipe arrow, hint text, holster
            // sign sits above the paw's rise so the demo never crosses the text; everything centred on the holster
            int hh = holsterDef != null ? holsterDef.Cell.y : 40;
            pawX = hcx - 6;
            pawY = htop + hh / 2 - 2;   // paw top-left while resting on the grip
            signX = hcx - SignW / 2;
            signY = Mathf.Max(safe.y + 2, pawY - pawRise - 4 - SignH);
            if (demoDef != null && signDef != null)
                signY = Mathf.Max(safe.y + 2, hbottom - demoDef.Cell.y - 2 - signDef.Cell.y);   // just above the yanked gun
            hint1.At(hcx, htop - 4 - (lh * 2 + 1));
            hint2.At(hcx, htop - 4 - lh);
            if (signPanel != null) PlaceSign();
        }

        private void Update()
        {
            if (pipe.LayoutVersion != laidOutVersion) Relayout();
            float dt = PawTime.UnscaledDt;
            for (int i = 0; i < 2; i++)
            {
                var s = sides[i];
                if (s.hurtLeft > 0f) s.hurtLeft -= dt;
                if (s.lostT < LostSeconds)
                {
                    s.lostT += dt;
                    if (s.lostT >= LostSeconds) s.lost.Visible = false;
                    else
                    {
                        PlaceLost(s);
                        // last 0.2 s: 2 blink steps (on/off) instead of a fade
                        s.lost.Visible = s.lostT < LostSeconds - 0.2f || ((int)(s.lostT * 20f) & 1) == 0;
                    }
                }
                bool hurt = s.hurtLeft > 0f;
                s.portrait.sprite = hurt ? s.hurt : s.normal;
                var poff = hurt ? s.hurtOff : s.normalOff;
                // 1px jolt while hurt (first part only)
                int jolt = (hurt && s.hurtLeft > 0.6f && ((int)(s.hurtLeft * 30f) & 1) == 0) ? 1 : 0;
                PawGfx.Place(s.portrait, s.portraitPos.x + poff.x + jolt, s.portraitPos.y + poff.y);

                if (s.ghostDelay > 0f) s.ghostDelay -= dt;
                else s.ghostV = Mathf.MoveTowards(s.ghostV, s.shown, dt * 0.5f);
                if (s.ghostV < s.shown) s.ghostV = s.shown;

                int w = Mathf.Clamp(Mathf.CeilToInt(s.shown * barW - 0.001f), 0, barW);
                bool low = s.shown < 0.3f;
                if (w != s.lastW || low != s.lastLow)
                {
                    s.lastW = w;
                    s.lastLow = low;
                    var crops = low ? (s.right ? lowCropsR : lowCrops) : (s.right ? fillCropsR : fillCrops);
                    s.fill.sprite = w > 0 ? crops[w] : null;
                    PawGfx.Place(s.fill, s.right ? s.barPos.x + barW - w : s.barPos.x, s.barPos.y);
                }
                int gw = Mathf.Clamp(Mathf.CeilToInt(s.ghostV * barW - 0.001f), 0, barW);
                if (gw != s.lastGhostW)
                {
                    s.lastGhostW = gw;
                    s.ghost.sprite = gw > 0 ? (s.right ? ghostCropsR[gw] : ghostCrops[gw]) : null;
                    PawGfx.Place(s.ghost, s.right ? s.barPos.x + barW - gw : s.barPos.x, s.barPos.y);
                }
            }

            if (signPanel != null) TickDemo(dt);

            if (signPop > 0f)
            {
                signPop -= dt;
                // pop = small vertical offset bounce (no scaling)
                int off = signPop > 0f ? Mathf.RoundToInt(Ease.Pop(0.35f - signPop, -3f, 2.5f, 8f)) : 0; // last step = rest
                var rs = roundSignDef;
                int cx = pipe.Visible.x + pipe.Visible.width / 2;
                int ry = pipe.Safe.y + layout.HudMargin + frameSize.y + 2 + off;
                PlaceRoundSign(rs, cx, ry);
            }
        }
    }
}
