using UnityEngine;

namespace PawNoon.Runtime
{
    /// <summary>A sprite that "pops" in with a whole-pixel offset bounce and a few white flash frames.</summary>
    public sealed class PopSprite
    {
        public readonly SpriteRenderer Sr;
        public SpriteAnimator Anim;
        private Vector2Int at;
        private float t = 10f;
        private float amp;
        private float flashLeft;
        private readonly MaterialPropertyBlock mpb = new MaterialPropertyBlock();
        private float lastFlash = -1f;
        private int[] steps;
        private float stepSeconds;

        public PopSprite(string name, Transform parent, int order)
        {
            Sr = PawGfx.NewSprite(name, parent, order);
            Sr.enabled = false;
        }

        /// <summary>Shows a sheet with its pivot at a canvas point. amp = initial offset px (negative = from above).</summary>
        public void Show(SheetDef d, Vector2Int canvas, float amplitude, float flashSeconds)
        {
            if (d == null) return;
            at = canvas;
            amp = amplitude;
            steps = null;
            t = 0f;
            flashLeft = flashSeconds;
            if (d.Count > 1 && d.Fps > 0f)
            {
                if (Anim == null)
                {
                    Anim = Sr.gameObject.AddComponent<SpriteAnimator>();
                    Anim.Renderer = Sr;
                }
                Anim.Play(d);
            }
            else
            {
                if (Anim != null) Anim.Show(d.First);
                Sr.sprite = d.First;
            }
            Sr.enabled = true;
            Tick(0f);
        }

        /// <summary>
        /// Like Show, but the vertical offset follows explicit whole-pixel keyframes (one per stepSeconds),
        /// e.g. {-18, -9, 0, -4, 0} = drop in two steps, land, one small bounce, settle. Pass null for an instant appear.
        /// </summary>
        public void ShowSteps(SheetDef d, Vector2Int canvas, int[] offsets, float stepSecs, float flashSeconds)
        {
            Show(d, canvas, 0f, flashSeconds);
            if (d == null) return;
            steps = (offsets != null && offsets.Length > 0 && stepSecs > 0f) ? offsets : null;
            stepSeconds = stepSecs;
            Tick(0f);
        }

        public void Hide() { Sr.enabled = false; }
        public bool Visible { get { return Sr.enabled; } }

        public void Tick(float dt)
        {
            if (!Sr.enabled) return;
            t += dt;
            int off;
            if (steps != null) off = steps[Mathf.Min(steps.Length - 1, (int)(t / stepSeconds))];
            else off = Mathf.RoundToInt(Ease.Pop(t, amp, 2.2f, 7f));
            PawGfx.Place(Sr, at.x, at.y + off);
            float f = 0f;
            if (flashLeft > 0f)
            {
                flashLeft -= dt;
                f = 1f;
            }
            if (!Mathf.Approximately(f, lastFlash))
            {
                lastFlash = f;
                Sr.GetPropertyBlock(mpb);
                mpb.SetFloat(PawGfx.FlashId, f);
                Sr.SetPropertyBlock(mpb);
            }
        }
    }

    /// <summary>Big cues (3/2/1, DRAW!, burst, GET READY), the live 6-decimal timer, stamps and messages.</summary>
    public sealed class DuelCues : MonoBehaviour
    {
        private const int Order = 180;
        private UiKit kit;
        private GameLayout layout;
        private PopSprite numeral, draw, burst, ready, stamp;
        private BitmapText timer;
        private BitmapText message;
        private readonly char[] timerBuf = new char[24];
        private float messageLeft;
        private bool reducedMotion;
        private SpriteRenderer heart;
        private EcgMonitor ecg;
        /// <summary>Heart monitor R peak during the hold: (beat index, frenzy 0..1, early/skipped beat).</summary>
        public System.Action<int, float, bool> OnHeartBeat;
        private bool holdMood;
        private SheetDef heartDef;
        /// <summary>Beats per minute of the hold heart (matches Loops/heartbeat.ogg).</summary>
        public float HeartBpm = 92f;
        private int lastNumeral = -1;
        private bool timerFrozen;
        private static Sprite heartSprite;

        private static readonly string[] HeartRows =
        {
            ".XX...XX.",
            "XXXX.XXXX",
            "XXXXXXXXX",
            "XXXXXXXXX",
            ".XXXXXXX.",
            "..XXXXX..",
            "...XXX...",
            "....X....",
        };

        private static Sprite HeartSprite()
        {
            if (heartSprite != null) return heartSprite;
            int w = 9, h = HeartRows.Length;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var px = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    px[(h - 1 - y) * w + x] = HeartRows[y][x] == 'X' ? new Color32(255, 255, 255, 255) : new Color32(0, 0, 0, 0);
            tex.SetPixels32(px);
            tex.Apply(false, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.name = "PawHeart";
            heartSprite = Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0f, 1f), 1f, 0, SpriteMeshType.FullRect);
            return heartSprite;
        }

        public void Init(UiKit k, GameLayout l)
        {
            kit = k;
            layout = l;
            burst = new PopSprite("CueBurst", transform, Order);
            draw = new PopSprite("CueDraw", transform, Order + 2);
            numeral = new PopSprite("CueNumeral", transform, Order + 2);
            ready = new PopSprite("GetReady", transform, Order + 1);
            stamp = new PopSprite("Stamp", transform, Order + 4);
            timer = BitmapText.Create("Timer", transform, kit.Timer, Order + 3, TextAlign.Center);
            timer.MonoDigits = true;
            timer.SetColors(Pal.Cream0, Pal.Ink, Pal.Ink2);
            timer.At(layout.Timer.x, layout.Timer.y);
            timer.Visible = false;
            message = BitmapText.Create("Message", transform, kit.Medium, Order + 3, TextAlign.Center);
            message.SetColors(Pal.SigYellow, Pal.Ink, Pal.Ink2);
            message.At(layout.Timer.x, layout.Timer.y + 26);
            message.Visible = false;
            // faint heartbeat glyph in the HIGH NOON numeral window during the hold
            heart = PawGfx.NewSprite("HoldHeart", transform, Order + 1);
            // painted 4-frame heart (Art/UI/heart_beat.png) when the artist delivered it; else the small glyph
            heartDef = kit.Ui.TryGet("heart_beat");
            if (heartDef != null && heartDef.Count >= 2)
            {
                heart.sprite = heartDef.Frames[0];
                heart.color = Color.white;
                PawGfx.Place(heart, layout.Cue.x - heartDef.Cell.x / 2 + heartDef.Pivot.x, layout.Cue.y - heartDef.Cell.y / 2 + heartDef.Pivot.y);
            }
            else
            {
                heart.sprite = HeartSprite();
                PawGfx.Place(heart, layout.Cue.x - 4, layout.Cue.y - 4);
            }
            heart.enabled = false;

            // heart monitor in the numeral window (replaces the heart glyph during the hold)
            var bd = kit.Ui.TryGet("hud_badge");
            int ew = 96, eh = 56;
            if (bd != null && bd.HasInner) { ew = bd.Inner.width - 8; eh = bd.Inner.height - 8; }
            ecg = new EcgMonitor(transform, Order + 1, ew, eh);
            ecg.Bpm = HeartBpm;
            ecg.OnBeat = (i, f, wide) => { if (OnHeartBeat != null) OnHeartBeat(i, f, wide); };
            ecg.PlaceAt(layout.Cue.x - ew / 2, layout.Cue.y - eh / 2);
        }

        /// <summary>
        /// Hold mood: a faint dark-red heart pulsing at about 1.5 Hz in the badge's numeral window.
        /// Turned on automatically when the numeral 1 is dismissed (ShowNumeral(0)), off at DRAW,
        /// stamps, HideAll or a new numeral. Callers may also drive it explicitly.
        /// </summary>
        public void SetHoldMood(bool on)
        {
            if (on == holdMood) return;
            holdMood = on;
            if (heart != null) heart.enabled = false;   // superseded by the heart monitor
            if (ecg != null) ecg.SetVisible(on);
        }

        public void SetReducedMotion(bool on) { reducedMotion = on; }

        private float Amp(float a) { return reducedMotion ? 0f : a; }
        private float FlashT(float s) { return reducedMotion ? 0f : s; }

        public void ShowNumeral(int n)
        {
            if (n <= 0)
            {
                numeral.Hide();
                if (lastNumeral == 1) SetHoldMood(true);
                lastNumeral = 0;
                return;
            }
            lastNumeral = n;
            SetHoldMood(false);
            numeral.Show(kit.Ui.Get("cue_" + n), layout.Cue, Amp(-10f), FlashT(0.05f));
        }

        /// <summary>
        /// Sudden death standoff: the heart monitor replaces GET READY, runs flat for <paramref name="seconds"/>,
        /// then one big spike (OnHeartBeat fires on it) and the rhythm resumes. The next numeral hides it.
        /// </summary>
        public void ShowFlatLine(float seconds)
        {
            ready.Hide();
            numeral.Hide();
            SetHoldMood(true);
            if (ecg != null) ecg.FlatLine(seconds);
        }

        public void ShowGetReady(bool on)
        {
            if (on) ready.Show(kit.Ui.Get("get_ready"), layout.Cue, Amp(-6f), 0f);
            else ready.Hide();
        }

        public void ShowDraw()
        {
            numeral.Hide();
            ready.Hide();
            SetHoldMood(false);
            burst.Show(kit.Ui.Get("cue_burst"), layout.Cue, 0f, 0f);
            draw.Show(kit.Ui.Get("cue_draw"), layout.Cue, Amp(-14f), FlashT(0.034f));
        }

        public void HideDraw()
        {
            draw.Hide();
            burst.Hide();
        }

        public void HideAll()
        {
            numeral.Hide();
            ready.Hide();
            HideDraw();
            stamp.Hide();
            SetHoldMood(false);
            lastNumeral = -1;
            timer.Visible = false;
            message.Visible = false;
        }

        /// <summary>Live timer. frozen = player's shot registered (brass tint).</summary>
        public void SetTimer(long ns, bool frozen)
        {
            int n = NumFmt.Seconds6(ns, timerBuf, 0);
            timerBuf[n++] = ' ';
            timerBuf[n++] = 's';
            timer.SetChars(timerBuf, n); // marks itself dirty only when the text changed
            if (frozen != timerFrozen)
            {
                timerFrozen = frozen;
                timer.Color = frozen ? (Color32)Pal.Brass0 : Pal.Cream0;
                timer.MarkDirty();
            }
            timer.Visible = true;
        }

        public void HideTimer() { timer.Visible = false; }

        public void ShowStamp(string name, int offsetY = 34)
        {
            SetHoldMood(false);
            stamp.Show(kit.Ui.Get(name), new Vector2Int(layout.Cue.x, layout.Cue.y + offsetY), Amp(-12f), FlashT(0.05f));
        }

        public void HideStamp() { stamp.Hide(); }

        public void ShowMessage(string text, float seconds)
        {
            message.SetText(text);
            message.Visible = true;
            messageLeft = seconds;
        }

        private void Update()
        {
            float dt = PawTime.UnscaledDt;
            numeral.Tick(dt);
            draw.Tick(dt);
            burst.Tick(dt);
            ready.Tick(dt);
            stamp.Tick(dt);
            if (holdMood && ecg != null) ecg.Tick(dt);
            if (messageLeft > 0f)
            {
                messageLeft -= dt;
                if (messageLeft <= 0f) message.Visible = false;
            }
        }
    }
}
