using System;
using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Leaderboards (SPEC_V2 §7, M5), opened by the trophy on the title. A paper panel like Settings with two tabs:
    /// WEEKLY RANKED (ranked_YYYY_WW, by rating) and FASTEST DRAW (best_reaction_hard, all time). Top 50 in 12 px
    /// rows, drag to scroll by whole rows (no clipped half rows), a 2 px scrollbar, and the player's own row pinned
    /// at the bottom. Both boards are fetched once per opening (POST /leaderboard-read) and cached while open.
    /// Every string is built when a board arrives; scrolling only re-assigns cached strings (no garbage).
    /// </summary>
    public sealed class LeaderboardScreen : M5Card
    {
        private const int MaxW = 232;
        private const int MaxH = 420;
        private const int TabH = 20;
        private const int MaxSlots = 40;
        private const float MockSeconds = 0.6f;

        private enum Phase { Loading, Ready, Failed }

        private sealed class Slot
        {
            public SpriteRenderer stripe;
            public BitmapText rank, name, value;
        }

        private sealed class Cached
        {
            public Phase phase;
            public LeaderboardBoard board;
            public string[] rank, name, value;
            public int youIndex = -1;
            public string youRank, youName, youValue, sub;
            public string failure;
            public int first;
        }

        private SupabaseApi api;
        private bool mock;
        private int token;
        private readonly Cached[] boards = { new Cached(), new Cached() };   // 0 ranked, 1 fastest
        private int tab;
        private float mockT = -1f;

        private SpriteRenderer tabRanked, tabFast, sep, track, thumb;
        private BitmapText tabRankedText, tabFastText, sub, headRank, headName, headValue, message, message2;
        private readonly Slot[] slots = new Slot[MaxSlots];
        private Slot pinned;
        private RectInt tabRankedRect, tabFastRect, listRect;
        private int visible;
        private bool dragging;
        private float dragStartY;
        private int dragStartFirst;
        private int shownFirst = -1;

        public bool Ranked { get { return tab == 0; } }

        public void Init(SupabaseApi supabase) { api = supabase; }

        protected override void Build()
        {
            O = 286;
            BuildFrame("panel_paper", true);
            title.SetText("LEADERBOARD");
            tabRanked = PawGfx.NewSprite("TabRanked", transform, O + 1);
            tabFast = PawGfx.NewSprite("TabFast", transform, O + 1);
            tabRankedText = Line("TabRankedT", kit.Small, Pal.Cream0);
            tabRankedText.SetText("WEEKLY RANKED");
            tabFastText = Line("TabFastT", kit.Small, Pal.Cream0);
            tabFastText.SetText("FASTEST DRAW");
            sub = InkText("Sub", kit.Small, O + 3, TextAlign.Center);
            sub.Color = Pal.Wood3;
            headRank = InkText("HeadRank", kit.Small, O + 3, TextAlign.Right);
            headRank.SetText("#");
            headName = InkText("HeadName", kit.Small, O + 3, TextAlign.Left);
            headName.SetText("NAME");
            headValue = InkText("HeadValue", kit.Small, O + 3, TextAlign.Right);
            headRank.Color = headName.Color = headValue.Color = Pal.Wood4;
            message = InkText("Message", kit.Medium, O + 3, TextAlign.Center);
            message2 = InkText("Message2", kit.Small, O + 3, TextAlign.Center);
            message2.Color = Pal.Wood3;
            for (int i = 0; i < MaxSlots; i++) slots[i] = NewSlot("Row" + i);
            pinned = NewSlot("You");
            sep = PawGfx.NewSprite("Sep", transform, O + 1);
            sep.sprite = ArtLib.WhitePixel;
            sep.color = Pal.Wood3;
            track = PawGfx.NewSprite("Track", transform, O + 1);
            track.sprite = ArtLib.WhitePixel;
            track.color = Pal.Cream1;
            thumb = PawGfx.NewSprite("Thumb", transform, O + 2);
            thumb.sprite = ArtLib.WhitePixel;
            thumb.color = Pal.Wood3;
        }

        private Slot NewSlot(string name)
        {
            var s = new Slot();
            s.stripe = PawGfx.NewSprite(name + "Bg", transform, O + 1);
            s.stripe.sprite = ArtLib.WhitePixel;
            s.rank = InkText(name + "R", kit.Medium, O + 3, TextAlign.Right);
            s.rank.MonoDigits = true;
            s.name = InkText(name + "N", kit.Medium, O + 3, TextAlign.Left);
            s.value = InkText(name + "V", kit.Medium, O + 3, TextAlign.Right);
            s.value.MonoDigits = true;
            return s;
        }

        /// <summary>A solid box with its top-left at canvas (x, y): WhitePixel's pivot is bottom-left, world y is up.</summary>
        private static void Box(SpriteRenderer sr, int x, int y, int w, int h)
        {
            sr.transform.position = Px.World(x, y + h);
            sr.transform.localScale = new Vector3(Mathf.Max(0, w), Mathf.Max(0, h), 1f);
        }

        protected override void Layout()
        {
            var vis = pipe.Visible;
            var safe = pipe.Safe;
            dim.transform.position = Px.World(vis.x, vis.yMax);
            dim.transform.localScale = new Vector3(vis.width, vis.height, 1f);
            int w = Mathf.Min(MaxW, vis.width - 16);
            int h = Mathf.Min(MaxH, safe.height - 16);
            int x = CenterX - w / 2;
            int y = Mathf.Max(safe.y + 8, safe.y + (safe.height - h) / 2);
            card = new RectInt(x, y, w, h);
            UiKit.SetRect(panel, kit.Ui.Get("panel_paper"), card);
            back.SetRect(new RectInt(x + 6, y + 6, BackSize, BackSize));
            title.At(x + w / 2, y + 14);

            int tw = (w - 24 - 2) / 2;
            tabRankedRect = new RectInt(x + 12, y + 38, tw, TabH);
            tabFastRect = new RectInt(x + 12 + tw + 2, y + 38, tw, TabH);
            int ty = y + 38 + (TabH - kit.Small.LineHeight) / 2;
            tabRankedText.At(tabRankedRect.x + tw / 2, ty);
            tabFastText.At(tabFastRect.x + tw / 2, ty);
            sub.At(x + w / 2, y + 63);

            int rankX = x + 34, nameX = x + 40, valueX = x + w - 16;
            headRank.At(rankX, y + 76);
            headName.At(nameX, y + 76);
            headValue.At(valueX, y + 76);

            // list: whole 12 px rows between the header and the pinned row
            int listTop = y + 87;
            int pinnedTop = y + h - 10 - LeaderboardMath.RowH;
            int listBottom = pinnedTop - 5;
            visible = Mathf.Min(MaxSlots, LeaderboardMath.VisibleRows(listBottom - listTop));
            listRect = new RectInt(x + 8, listTop, w - 16, visible * LeaderboardMath.RowH);
            for (int i = 0; i < MaxSlots; i++)
            {
                var s = slots[i];
                int ry = LeaderboardMath.RowTop(listTop, i);
                Box(s.stripe, x + 8, ry, w - 20, LeaderboardMath.RowH);
                s.rank.At(rankX, ry + 1);
                s.name.At(nameX, ry + 1);
                s.value.At(valueX, ry + 1);
            }
            Box(sep, x + 8, pinnedTop - 3, w - 16, 1);
            Box(pinned.stripe, x + 8, pinnedTop, w - 16, LeaderboardMath.RowH);
            pinned.rank.At(rankX, pinnedTop + 1);
            pinned.name.At(nameX, pinnedTop + 1);
            pinned.value.At(valueX, pinnedTop + 1);
            message.At(x + w / 2, listTop + 40);
            message2.At(x + w / 2, listTop + 54);
            shownFirst = -1;
            Refresh();
        }

        // ------------------------------------------------------------------ flow

        /// <summary>Opens on <paramref name="ranked"/> (else FASTEST DRAW) and fetches both boards.</summary>
        public void Show(bool ranked, bool useMock)
        {
            mock = useMock;
            token++;
            tab = ranked ? 0 : 1;
            for (int i = 0; i < 2; i++) { boards[i] = new Cached(); boards[i].phase = Phase.Loading; }
            dragging = false;
            if (!IsOpen) Open();
            else Layout();
            Fetch(0);
            Fetch(1);
        }

        private void Fetch(int which)
        {
            var c = boards[which];
            c.phase = Phase.Loading;
            if (mock) { mockT = MockSeconds; return; }
            int my = token;
            api.LeaderboardRead(which == 0 ? LeaderboardBoard.Ranked : LeaderboardBoard.Fastest, r =>
            {
                if (my != token) return;
                if (r.Ok)
                {
                    string err;
                    var b = LeaderboardBoard.Parse(r.Body, out err);
                    if (b != null) Fill(which, b);
                    else
                    {
                        Debug.LogWarning("[PawNoon] leaderboard answer unreadable: " + err);
                        Fail(which, "GARBLED WIRE", "Tap the tab to try again.");
                    }
                }
                else if (r.Offline) Fail(which, "NO SIGNAL", "Tap the tab to try again.");
                else if (r.Error.code == "not_configured") Fail(which, "ONLINE IS NOT SET UP", "Solo duels work as always.");
                else Fail(which, "TELEGRAPH IS DOWN", "Tap the tab to try again.");
            });
        }

        private void Fail(int which, string big, string small)
        {
            boards[which].phase = Phase.Failed;
            boards[which].failure = big + "\n" + small;
            if (which == tab) Refresh();
        }

        /// <summary>Builds every string of a board once (rank, name, value per row, the pinned row, the sub line).</summary>
        private void Fill(int which, LeaderboardBoard b)
        {
            var c = boards[which];
            c.board = b;
            int n = b.Top.Length;
            c.rank = new string[n];
            c.name = new string[n];
            c.value = new string[n];
            c.youIndex = -1;
            for (int i = 0; i < n; i++)
            {
                var e = b.Top[i];
                c.rank[i] = e.Rank.ToString(CultureInfo.InvariantCulture) + ".";
                c.name[i] = OnlineDirector.NameForHud(e.Name);
                c.value[i] = ValueText(b.IsRanked, e.Value);
                if (e.You && c.youIndex < 0) c.youIndex = i;
            }
            if (b.You != null)
            {
                c.youRank = b.You.Rank > 0 ? b.You.Rank.ToString(CultureInfo.InvariantCulture) + "." : "--";
                if (b.IsRanked && !b.YouEligible)
                    c.youName = "YOU  " + b.You.Matches.ToString(CultureInfo.InvariantCulture) + "/" + b.MinMatches.ToString(CultureInfo.InvariantCulture) + " DUELS";
                else c.youName = "YOU";
                c.youValue = ValueText(b.IsRanked, b.You.Value);
            }
            else
            {
                c.youRank = "--";
                c.youName = b.IsRanked ? "YOU  NO RANKED DUEL" : "YOU  NO SHOT YET";
                c.youValue = "--";
            }
            string count = b.Total.ToString(CultureInfo.InvariantCulture) + (b.Total == 1 ? " GUN" : " GUNS");
            string when = b.IsRanked ? LeaderboardBoard.WeekLabel(b.Board) : "ALL TIME";
            c.sub = (when.Length > 0 ? when + "   " : "") + count + (b.IsRanked ? "   MIN " + b.MinMatches.ToString(CultureInfo.InvariantCulture) + " DUELS" : "");
            c.first = LeaderboardMath.InitialFirst(c.youIndex < 0 ? 0 : c.youIndex, n, visible);
            c.phase = n == 0 ? Phase.Failed : Phase.Ready;
            if (n == 0) c.failure = b.IsRanked ? "NO RANKED GUNS YET\nWin a ranked duel to be first." : "NO SHOTS YET\nWin a duel to be first.";
            if (which == tab) Refresh();
        }

        private static string ValueText(bool ranked, long v)
        {
            if (ranked) return v.ToString(CultureInfo.InvariantCulture);
            if (v <= 0) return "--";
            return NumFmt.Seconds6(v).Substring(0, 5) + " s";
        }

        // ------------------------------------------------------------------ drawing

        private void Refresh()
        {
            bool r = tab == 0;
            PlaceSeg(tabRanked, r, tabRankedRect);
            PlaceSeg(tabFast, !r, tabFastRect);
            tabRankedText.SetColors(r ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            tabFastText.SetColors(!r ? Pal.Cream0 : Pal.Cream1, Pal.Ink, Pal.Ink2);
            tabRankedText.MarkDirty();
            tabFastText.MarkDirty();
            headValue.SetText(r ? "RATING" : "BEST DRAW");

            var c = boards[tab];
            bool ready = c.phase == Phase.Ready;
            sub.SetText(c.board != null ? c.sub : "");
            if (c.phase == Phase.Loading) { message.SetText("LOADING..."); message2.SetText(""); }
            else if (c.phase == Phase.Failed)
            {
                int nl = c.failure != null ? c.failure.IndexOf('\n') : -1;
                message.SetText(nl >= 0 ? c.failure.Substring(0, nl) : (c.failure ?? ""));
                message2.SetText(nl >= 0 ? c.failure.Substring(nl + 1) : "");
            }
            message.Visible = !ready;
            message2.Visible = !ready;
            bool listed = c.board != null;
            headRank.Visible = headName.Visible = headValue.Visible = listed;
            // pinned "you" row: always there once the board arrived (even when the list is empty)
            SetSlot(pinned, listed, c.youRank, c.youName, c.youValue, Pal.Brass1);
            sep.enabled = listed;
            shownFirst = -1;
            DrawRows();
        }

        private void DrawRows()
        {
            var c = boards[tab];
            int n = c.phase == Phase.Ready && c.rank != null ? c.rank.Length : 0;
            int first = LeaderboardMath.ClampFirst(c.first, n, visible);
            if (first == shownFirst) return;
            shownFirst = first;
            c.first = first;
            for (int i = 0; i < MaxSlots; i++)
            {
                int idx = first + i;
                bool on = i < visible && idx < n;
                if (!on) { SetSlot(slots[i], false, null, null, null, Pal.Clear); continue; }
                bool you = idx == c.youIndex;
                // alternate rows get a light stripe; your row is brass
                Color32 bg = you ? Pal.Brass1 : (idx % 2 == 1 ? Pal.Cream1 : Pal.Clear);
                SetSlot(slots[i], true, c.rank[idx], c.name[idx], c.value[idx], bg);
            }
            bool scroll = n > visible;
            track.enabled = scroll;
            thumb.enabled = scroll;
            if (scroll)
            {
                int tx = card.xMax - 10;
                int tt, th;
                LeaderboardMath.Thumb(listRect.y, listRect.height, n, visible, first, out tt, out th);
                Box(track, tx, listRect.y, 2, listRect.height);
                Box(thumb, tx, tt, 2, th);
            }
        }

        private static void SetSlot(Slot s, bool on, string rank, string name, string value, Color32 bg)
        {
            s.stripe.enabled = on && bg.a > 0;
            if (on) s.stripe.color = bg;
            s.rank.Visible = on;
            s.name.Visible = on;
            s.value.Visible = on;
            if (!on) return;
            s.rank.SetText(rank);
            s.name.SetText(name);
            s.value.SetText(value);
        }

        // ------------------------------------------------------------------ input

        /// <summary>True when the screen closed.</summary>
        public bool Tick()
        {
            RelayoutIfNeeded();
            if (mockT > 0f)
            {
                mockT -= PawTime.UnscaledDt;
                if (mockT <= 0f)
                {
                    Fill(0, NetMock.Leaderboard(true));
                    Fill(1, NetMock.Leaderboard(false));
                }
            }
            if (dragging)
            {
                if (UiPointer.Held)
                {
                    var c = boards[tab];
                    int n = c.rank != null ? c.rank.Length : 0;
                    c.first = LeaderboardMath.FirstAfterDrag(dragStartFirst, Mathf.RoundToInt(dragStartY - UiPointer.Pos.y), n, visible);
                    DrawRows();
                }
                if (UiPointer.Up || !UiPointer.Held) dragging = false;
                return false;
            }
            if (BackOrOutside()) { Close(); return true; }
            if (UiPointer.Down && !UiPointer.Consumed)
            {
                if (UiPointer.In(tabRankedRect) || UiPointer.In(tabFastRect))
                {
                    UiPointer.Consumed = true;
                    int t = UiPointer.In(tabRankedRect) ? 0 : 1;
                    if (t != tab)
                    {
                        tab = t;
                        Click("sfx_ui_toggle");
                        PawHaptics.Impact(0);
                    }
                    if (boards[tab].phase == Phase.Failed && boards[tab].board == null) Fetch(tab);   // retry
                    Refresh();
                    return false;
                }
                if (UiPointer.In(listRect))
                {
                    UiPointer.Consumed = true;
                    dragging = true;
                    dragStartY = UiPointer.Pos.y;
                    dragStartFirst = boards[tab].first;
                    return false;
                }
            }
            SwallowTaps();
            return false;
        }

        public override void Close()
        {
            token++;
            mockT = -1f;
            dragging = false;
            base.Close();
        }

        public string Status()
        {
            var c = boards[tab];
            return "board=" + (tab == 0 ? "ranked" : "fastest") + ":" + c.phase + (c.board != null ? "(" + c.board.Top.Length + " rows, first " + c.first + ", you " + c.youRank + ")" : "");
        }
    }
}
