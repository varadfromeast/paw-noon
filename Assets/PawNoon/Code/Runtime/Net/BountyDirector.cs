using System;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Daily bounty + streak (SPEC_V2 §7): counts PvP wins per local day (BountyState, PlayerPrefs
    /// "pawnoon.bounty"), calls daily-claim through SupabaseApi when the day's wins are in, and celebrates a paid
    /// bounty on the title (coin burst, sfx_stamp, success haptic, strip pop). Offline the wins are counted
    /// locally and the claim is retried after a queued submit is delivered, on the next title and on launch.
    /// Solo wins never count (the server only counts validated PvP wins); mock PvP wins do not count either.
    /// </summary>
    public sealed class BountyDirector
    {
        public const string PrefKey = "pawnoon.bounty";

        private readonly SupabaseApi api;
        private readonly NetConfig cfg;
        private readonly Func<bool> isMock;
        private readonly FeelConfig feel;
        private readonly BountyStrip strip;
        private readonly BountyState state;
        private bool claiming;
        private bool lastOffline;
        private bool celebrateOnTitle;
        private int reward;

        /// <summary>Review: show the strip and simulate the server even without a configured backend.</summary>
        public bool ReviewSimulate;

        public BountyDirector(SupabaseApi api, NetConfig cfg, Func<bool> isMock, UiKit kit, PixelPipeline pipe, Transform uiRoot, TitleScreen title)
        {
            this.api = api;
            this.cfg = cfg;
            this.isMock = isMock;
            feel = FeelConfig.Current;
            reward = feel.bountyReward;
            state = BountyState.Parse(Load(), feel.bountyWinsRequired);
            state.Required = Mathf.Max(1, feel.bountyWinsRequired);
            var go = new GameObject("BountyStrip");
            go.transform.SetParent(uiRoot, false);
            strip = go.AddComponent<BountyStrip>();
            strip.Setup(this, kit, pipe, title);
            if (api != null) api.QueueDrained += TryClaim;
        }

        public BountyState State { get { return state; } }

        private static DateTime Now { get { return DateTime.Now; } }

        /// <summary>The strip is shown on the title: bounty on, and PvP reachable (live server, mock, or review).</summary>
        public bool Available { get { return feel.bountyEnabled && (cfg.IsConfigured || isMock() || ReviewSimulate); } }

        /// <summary>Whether a claim is likely to reach the server now (for the CLAIMING / WILL SYNC copy).</summary>
        public bool Online { get { return !lastOffline; } }

        public int Reward { get { return reward; } }

        // ------------------------------------------------------------------ events

        /// <summary>A PvP match ended. Only real (non-mock) wins count.</summary>
        public void OnPvpFinished(bool won, bool mock)
        {
            if (!won || mock || !feel.bountyEnabled) return;
            RecordWin();
        }

        /// <summary>Counts one PvP win for today and claims when the bounty is due.</summary>
        public void RecordWin()
        {
            bool due = state.RecordPvpWin(Now);
            Save();
            strip.MarkDirty();
            if (due) TryClaim();
        }

        /// <summary>Sends the owed claim if any (idempotent; safe to call from any hook).</summary>
        public void TryClaim()
        {
            if (claiming || !feel.bountyEnabled) return;
            if (!state.ShouldClaim(Now)) return;
            if (Simulated)
            {
                // mock / review: the server's rule, locally (enough wins today -> paid, streak from yesterday's claim)
                int today = BountyState.DayOf(Now);
                int streak = state.StreakAt(today - 1) + 1;
                int coins = Math.Max(0, state.Coins) + reward;
                Answer(BountyState.ClaimOutcome.Claimed, state.WinsToday, streak, coins);
                return;
            }
            if (!cfg.IsConfigured || api == null) return;
            claiming = true;
            api.DailyClaim(OnClaimAnswer);
        }

        private bool Simulated { get { return ReviewSimulate || isMock() || !cfg.IsConfigured; } }

        private void OnClaimAnswer(ApiResult r)
        {
            claiming = false;
            if (r == null || !r.Ok)
            {
                lastOffline = r == null || r.Offline;
                if (r != null && !r.Offline) Debug.Log("[PawNoon] daily-claim refused: " + r.Status + " " + r.Error.code);
                Answer(lastOffline ? BountyState.ClaimOutcome.Offline : BountyState.ClaimOutcome.Failed, -1, -1, -1);
                return;
            }
            lastOffline = false;
            var info = DailyClaimInfo.Parse(r.Body);
            if (info == null)
            {
                Debug.LogWarning("[PawNoon] daily-claim answer unreadable: " + Http.Short(r.Body));
                Answer(BountyState.ClaimOutcome.Failed, -1, -1, -1);
                return;
            }
            if (info.Reward >= 0) reward = info.Reward;
            if (info.WinsRequired > 0) state.Required = info.WinsRequired;
            var outcome = info.Claimed ? BountyState.ClaimOutcome.Claimed
                : (info.AlreadyClaimed ? BountyState.ClaimOutcome.AlreadyClaimed : BountyState.ClaimOutcome.NotYet);
            Answer(outcome, info.WinsToday, info.Streak, info.Coins);
        }

        private void Answer(BountyState.ClaimOutcome outcome, int wins, int streak, int coins)
        {
            bool nothingQueued = api == null || (api.Queue.Count == 0 && !api.Flushing);
            bool paid = state.Apply(Now, outcome, wins, streak, coins, nothingQueued);
            Save();
            strip.MarkDirty();
            if (paid)
            {
                if (strip.OnTitle) strip.Celebrate();
                else celebrateOnTitle = true;
            }
        }

        /// <summary>The title is showing again: claim if owed, celebrate a bounty paid while away.</summary>
        public void OnTitle()
        {
            TryClaim();
            strip.MarkDirty();
        }

        /// <summary>BountyStrip: the title (and the strip) just became visible.</summary>
        internal void StripShown()
        {
            if (!celebrateOnTitle) return;
            celebrateOnTitle = false;
            strip.Celebrate();
        }

        // ------------------------------------------------------------------ persistence

        private static string Load()
        {
            try { return PlayerPrefs.GetString(PrefKey, ""); }
            catch (Exception) { return ""; }
        }

        private void Save()
        {
            try
            {
                PlayerPrefs.SetString(PrefKey, state.Serialize());
                PlayerPrefs.Save();
            }
            catch (Exception e) { Debug.LogWarning("[PawNoon] bounty not saved: " + e.Message); }
        }

        /// <summary>Review: forget everything (today's wins, streak, coins).</summary>
        public void ResetAll()
        {
            var fresh = BountyState.Parse(null, state.Required);
            state.Day = fresh.Day;
            state.WinsToday = 0;
            state.Pending = false;
            state.LastClaimDay = 0;
            state.Streak = 0;
            state.Coins = -1;
            celebrateOnTitle = false;
            Save();
            strip.MarkDirty();
        }

        /// <summary>Review: pretend the last claim was yesterday with this streak (to see the flame and its +1).</summary>
        public void SetStreakYesterday(int streak)
        {
            state.LastClaimDay = BountyState.DayOf(Now) - 1;
            state.Streak = Math.Max(0, streak);
            Save();
            strip.MarkDirty();
        }

        public void CelebrateNow() { strip.Celebrate(); }

        public string Status()
        {
            var now = Now;
            return "bounty=" + state.DisplayWins(now) + "/" + state.Required + (state.ClaimedToday(now) ? " paid" : (state.Pending ? " pending" : "")) +
                   " streak=" + state.DisplayStreak(now) + " coins=" + state.Coins + (claiming ? " claiming" : "") +
                   (lastOffline ? " offline" : "") + (Simulated ? " simulated" : "") + " line=\"" + state.Line(now, Online, reward) + "\"";
        }
    }

    /// <summary>
    /// The bounty line on the title, under BEST: a name_plate strip with the streak flame + count (when the streak
    /// is alive) and "BOUNTY: 2/3 WINS TODAY". Text is rebuilt only when what it says changes (checked a few times
    /// a second, no per-frame allocation); the flame flickers through its 2-frame sheet; a paid bounty pops the
    /// strip (-3, -1, 0 px steps) with a coin burst.
    /// </summary>
    public sealed class BountyStrip : MonoBehaviour
    {
        private const int Order = 243;
        private const int H = 14;
        private const int Pad = 5;
        private static readonly int[] Pop = { -3, -1, 0 };
        private const float PopStep = 0.06f;

        private BountyDirector owner;
        private UiKit kit;
        private PixelPipeline pipe;
        private TitleScreen title;
        private SpriteRenderer plate;
        private SheetDef plateDef;
        private SpriteRenderer flame;
        private SpriteAnimator flameAnim;
        private BitmapText streakText, line;
        private bool dirty = true;
        private bool shown;
        private float checkT;
        private int lastWins = -1, lastStreak = -1, lastPhase = -1, lastY = int.MinValue, lastVersion = -1;
        private float popT = 99f;
        private int popDy;
        private int width;

        public void Setup(BountyDirector d, UiKit k, PixelPipeline p, TitleScreen t)
        {
            owner = d;
            kit = k;
            pipe = p;
            title = t;
            plateDef = kit.Ui.TryGet("name_plate");
            plate = plateDef != null ? kit.Panel("name_plate", transform, Order, new RectInt(0, 0, 40, H)) : PawGfx.NewSprite("Plate", transform, Order);
            flame = PawGfx.NewSprite("Flame", transform, Order + 1);
            var fd = kit.Ui.TryGet("ico_flame");
            if (fd != null)
            {
                flameAnim = flame.gameObject.AddComponent<SpriteAnimator>();
                flameAnim.Renderer = flame;
                flameAnim.Play(fd);
            }
            else flame.enabled = false;
            streakText = BitmapText.Create("Streak", transform, kit.Small, Order + 2, TextAlign.Left);
            streakText.SetColors(Pal.SigYellow, Pal.Ink, Pal.Ink2);
            streakText.Outline = false;
            streakText.Shadow = false;
            line = BitmapText.Create("Bounty", transform, kit.Small, Order + 2, TextAlign.Left);
            line.Outline = false;
            line.Shadow = false;
            SetShown(false);
        }

        public void MarkDirty() { dirty = true; }

        public bool OnTitle { get { return shown; } }

        public void Celebrate()
        {
            popT = PawSettings.ReducedMotion ? 99f : 0f;
            if (kit.Audio != null) kit.Audio.Play("sfx_stamp", 0.9f, 1.08f);
            PawHaptics.Notify(0);
            var parts = FindParticles();
            if (parts != null && shown)
            {
                int cx = pipe.Visible.x + pipe.Visible.width / 2;
                parts.Coins(new Vector2(cx, title.BountyTop + H / 2), 80, PawSettings.ReducedMotion ? 6 : 16);
            }
            dirty = true;
        }

        private static PixelParticles particles;

        private static PixelParticles FindParticles()
        {
            if (particles == null) particles = FindAnyObjectByType<PixelParticles>();
            return particles;
        }

        private void SetShown(bool v)
        {
            if (shown == v && plate.enabled == v) return;
            shown = v;
            plate.enabled = v;
            line.Visible = v;
            if (!v) { flame.enabled = false; streakText.Visible = false; }
            if (v)
            {
                dirty = true;
                owner.StripShown();
            }
        }

        private void Update()
        {
            if (owner == null) return;
            bool want = owner.Available && title != null && title.ControlsShown;
            if (want != shown) SetShown(want);
            if (!shown) return;

            float dt = PawTime.UnscaledDt;
            checkT -= dt;
            if (checkT <= 0f)
            {
                // a day can roll over while the title is up: re-read the state a few times a second (no allocation)
                checkT = 0.25f;
                var now = DateTime.Now;
                var st = owner.State;
                int wins = st.DisplayWins(now), streak = st.DisplayStreak(now), phase = (int)st.PhaseAt(now, owner.Online);
                if (wins != lastWins || streak != lastStreak || phase != lastPhase) dirty = true;
            }
            int y = title.BountyTop;
            if (y != lastY || pipe.LayoutVersion != lastVersion) dirty = true;

            int dy = 0;
            if (popT < Pop.Length * PopStep)
            {
                popT += dt;
                int i = Mathf.Min(Pop.Length - 1, Mathf.FloorToInt(popT / PopStep));
                dy = popT >= Pop.Length * PopStep ? 0 : Pop[i];
            }
            if (dy != popDy) { popDy = dy; dirty = true; }
            if (dirty) Rebuild();
        }

        private void Rebuild()
        {
            dirty = false;
            var now = DateTime.Now;
            var st = owner.State;
            lastWins = st.DisplayWins(now);
            lastStreak = st.DisplayStreak(now);
            var phase = st.PhaseAt(now, owner.Online);
            lastPhase = (int)phase;
            lastY = title.BountyTop;
            lastVersion = pipe.LayoutVersion;

            line.SetText(st.Line(now, owner.Online, owner.Reward));
            line.SetColors(phase == BountyState.Phase.Claimed ? Pal.SigGreen : Pal.Brass0, Pal.Ink, Pal.Ink2);
            bool hasStreak = lastStreak > 0;
            streakText.SetText(hasStreak ? "x" + lastStreak : "");
            int flameW = hasStreak && flameAnim != null ? 8 : 0;
            int streakW = hasStreak ? streakText.Width : 0;
            int lead = hasStreak ? flameW + 2 + streakW + 6 : 0;
            width = Pad + lead + line.Width + Pad;
            int cx = pipe.Visible.x + pipe.Visible.width / 2;
            int x = cx - width / 2;
            int top = lastY + popDy;
            if (plateDef != null) UiKit.SetRect(plate, plateDef, new RectInt(x, top, width, H));
            int ty = top + (H - kit.Small.LineHeight) / 2 + 1;
            if (hasStreak)
            {
                flame.enabled = flameAnim != null;
                PawGfx.Place(flame, x + Pad, top + (H - 10) / 2);
                streakText.Visible = true;
                streakText.At(x + Pad + flameW + 2, ty);
            }
            else
            {
                flame.enabled = false;
                streakText.Visible = false;
            }
            line.At(x + Pad + lead, ty);
        }
    }
}
