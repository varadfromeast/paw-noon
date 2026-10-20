using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Friend duel cards (M5), one wood card with four faces:
    ///   Menu     CREATE ROOM / JOIN ROOM
    ///   Created  the code in four big tiles (font_code), SHARE (native sheet or clipboard) and DUEL (record the ghost)
    ///   Join     four tiles + the A-Z pixel keypad (no OS keyboard) + JOIN
    ///   Waiting  the code, "waiting for HOST", the room's time left
    /// The flow (network, mock, polling) is FriendDuel; this class only draws and reports taps.
    /// </summary>
    public sealed class FriendCard : M5Card
    {
        public enum Face { Menu, Created, Join, Waiting }
        public enum Action { None, Back, Create, JoinMenu, Share, Duel, Join }

        private const int W = 196;
        private const int BtnH = 30;

        private Face face;
        private BitmapText sub, desc1, desc2, line1, line2, status;
        private PixelButton create, joinMenu, share, duel, join, cancel;
        private CodeTiles tiles;
        private PixelKeypad keypad;
        private RoomCodeEntry entry;
        private float dotT;
        private int dotIndex = -1;
        private string waitBase = "";
        private readonly char[] waitBuf = new char[48];
        private readonly char[] clockBuf = new char[24];
        private int clockLen;
        private string clockPrefix = "";
        private int shownSeconds = int.MinValue;

        public Face Current { get { return face; } }

        protected override void Build()
        {
            BuildFrame("panel_wood", false);
            sub = Line("Sub", kit.Small, Pal.Sand0);
            desc1 = Line("Desc1", kit.Small, Pal.Cream1);
            desc2 = Line("Desc2", kit.Small, Pal.Cream1);
            line1 = Line("Line1", kit.Small, Pal.Cream1);
            line2 = Line("Line2", kit.Small, Pal.Sand0);
            status = Line("Status", kit.Small, Pal.Brass1);
            create = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "CREATE ROOM", null, new RectInt(0, 0, 164, BtnH), O + 2);
            joinMenu = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "JOIN ROOM", null, new RectInt(0, 0, 164, BtnH), O + 2);
            share = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "SHARE", "ico_share", new RectInt(0, 0, 80, BtnH), O + 2);
            duel = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "DUEL", null, new RectInt(0, 0, 84, BtnH), O + 2);
            join = new PixelButton(kit, transform, "btn_large", "btn_large_pressed", "JOIN", null, new RectInt(0, 0, 120, BtnH), O + 2);
            cancel = new PixelButton(kit, transform, "btn_large_alt", "btn_large_alt_pressed", "CANCEL", null, new RectInt(0, 0, 120, BtnH), O + 2);
            tiles = new CodeTiles(kit, transform, O + 1);
            keypad = new PixelKeypad(kit, transform, O + 1);
        }

        private int Height()
        {
            switch (face)
            {
                case Face.Menu: return 150;
                case Face.Created: return 156;
                case Face.Join: return 40 + CodeTiles.Tile + 18 + PixelKeypad.Height + 8 + BtnH + 12;
                default: return 156;
            }
        }

        protected override void Layout()
        {
            var r = PlaceCard(W, Height());
            int cx = r.x + r.width / 2;
            int bw = Mathf.Min(164, r.width - 24);
            switch (face)
            {
                case Face.Menu:
                    sub.At(cx, r.y + 40);
                    create.SetRect(new RectInt(cx - bw / 2, r.y + 52, bw, BtnH));
                    desc1.At(cx, r.y + 52 + BtnH + 3);
                    joinMenu.SetRect(new RectInt(cx - bw / 2, r.y + 98, bw, BtnH));
                    desc2.At(cx, r.y + 98 + BtnH + 3);
                    break;
                case Face.Created:
                {
                    tiles.Layout(cx, r.y + 40);
                    line1.At(cx, r.y + 76);
                    line2.At(cx, r.y + 86);
                    status.At(cx, r.y + 99);
                    int sw = 80, dw = 84, gap = 8;
                    int x0 = cx - (sw + gap + dw) / 2;
                    share.SetRect(new RectInt(x0, r.y + 114, sw, BtnH));
                    duel.SetRect(new RectInt(x0 + sw + gap, r.y + 114, dw, BtnH));
                    break;
                }
                case Face.Join:
                {
                    tiles.Layout(cx, r.y + 40);
                    int hy = r.y + 40 + CodeTiles.Tile + 5;
                    status.At(cx, hy);
                    int ky = hy + 13;
                    keypad.Layout(cx, ky);
                    // JOIN once 4 letters are in; until then CANCEL holds the same spot (no empty hole under the keys)
                    join.SetRect(new RectInt(cx - 60, ky + PixelKeypad.Height + 8, 120, BtnH));
                    cancel.SetRect(new RectInt(cx - 60, ky + PixelKeypad.Height + 8, 120, BtnH));
                    break;
                }
                case Face.Waiting:
                    tiles.Layout(cx, r.y + 40);
                    line1.At(cx, r.y + 76);
                    line2.At(cx, r.y + 86);
                    status.At(cx, r.y + 99);
                    cancel.SetRect(new RectInt(cx - 60, r.y + 114, 120, BtnH));
                    break;
            }
        }

        private void ShowFace(Face f, string titleText)
        {
            face = f;
            title.SetText(titleText);
            bool menu = f == Face.Menu, created = f == Face.Created, joinF = f == Face.Join, waiting = f == Face.Waiting;
            sub.Visible = menu;
            desc1.Visible = menu;
            desc2.Visible = menu;
            create.SetVisible(menu);
            joinMenu.SetVisible(menu);
            share.SetVisible(created);
            duel.SetVisible(created);
            join.SetVisible(false);
            cancel.SetVisible(waiting);
            line1.Visible = created || waiting;
            line2.Visible = created || waiting;
            status.Visible = !menu;
            tiles.SetVisible(!menu);
            keypad.SetVisible(joinF);
            shownSeconds = int.MinValue;
            dotIndex = -1;
            dotT = 0f;
            waitBase = "";
            if (!IsOpen) Open();
            else Layout();
        }

        // ------------------------------------------------------------------ faces

        public void ShowMenu()
        {
            ShowFace(Face.Menu, "FRIEND DUEL");
            SetLine(sub, "One code. One friend. Your ghost.", Pal.Sand0);
            SetLine(desc1, "Get a code to send a friend.", Pal.Cream1);
            SetLine(desc2, "Type the code a friend sent.", Pal.Cream1);
        }

        /// <summary>Host card. <paramref name="code"/> null = still opening (blank tiles).</summary>
        public void ShowCreated(string code)
        {
            ShowFace(Face.Created, "YOUR ROOM");
            tiles.SetCode(code);
            SetLine(line1, "Send the code, then win a duel:", Pal.Cream1);
            SetLine(line2, "your friend faces that ghost.", Pal.Sand0);
            share.SetVisible(code != null);
            duel.SetVisible(code != null);
            if (code == null) SetStatus("OPENING A ROOM...", Pal.Brass1);
        }

        public void ShowJoin(RoomCodeEntry e, string hint, bool isError)
        {
            entry = e;
            ShowFace(Face.Join, "JOIN A ROOM");
            RefreshEntry();
            SetStatus(hint, isError ? Pal.SigRed : Pal.Sand0);
        }

        public void ShowWaiting(string code, string hostName)
        {
            ShowFace(Face.Waiting, "WAITING");
            tiles.SetCode(code);
            waitBase = "WAITING FOR " + OnlineDirector.NameForHud(hostName);
            TickDots(0f);
            SetLine(line2, "They must finish one duel first.", Pal.Sand0);
            SetStatus("", Pal.Brass1);
        }

        /// <summary>The small coloured line under the tiles (status, hints, errors).</summary>
        public void SetStatus(string text, Color32 c)
        {
            clockPrefix = "";
            shownSeconds = int.MinValue;
            SetLine(status, text, c);
        }

        /// <summary>"<paramref name="prefix"/>M:SS" on the status line, re-texted only when the second changes (no garbage).</summary>
        public void SetClock(string prefix, int seconds, Color32 c)
        {
            if (seconds < 0) seconds = 0;
            if (seconds == shownSeconds && ReferenceEquals(prefix, clockPrefix)) return;
            shownSeconds = seconds;
            clockPrefix = prefix;
            int n = 0;
            for (int i = 0; i < prefix.Length && n < clockBuf.Length - 6; i++) clockBuf[n++] = prefix[i];
            int m = seconds / 60, s = seconds % 60;
            if (m >= 10) clockBuf[n++] = (char)('0' + (m / 10) % 10);
            clockBuf[n++] = (char)('0' + m % 10);
            clockBuf[n++] = ':';
            clockBuf[n++] = (char)('0' + s / 10);
            clockBuf[n++] = (char)('0' + s % 10);
            clockLen = n;
            status.SetChars(clockBuf, clockLen);
            status.Color = c;
            status.Visible = true;
            status.MarkDirty();
        }

        /// <summary>Re-reads the entry into the tiles (cursor on the next empty slot) and shows JOIN when complete.</summary>
        public void RefreshEntry()
        {
            if (entry == null) return;
            tiles.Set(entry.Chars, entry.Count, entry.Complete ? -1 : entry.Count);
            join.SetVisible(face == Face.Join && entry.Complete);
            cancel.SetVisible(face == Face.Waiting || (face == Face.Join && !entry.Complete));
        }

        private void TickDots(float dt)
        {
            if (face != Face.Waiting || waitBase.Length == 0) return;
            dotT += dt;
            int i = PawSettings.ReducedMotion ? 3 : ((int)(dotT / 0.3f)) % 4;
            if (i == dotIndex) return;
            dotIndex = i;
            int n = 0;
            for (int k = 0; k < waitBase.Length && n < waitBuf.Length - 3; k++) waitBuf[n++] = waitBase[k];
            for (int k = 0; k < i; k++) waitBuf[n++] = '.';
            line1.SetChars(waitBuf, n);
            line1.Color = Pal.Cream1;
            line1.Visible = true;
            line1.MarkDirty();
        }

        // ------------------------------------------------------------------ input

        /// <summary>Taps this frame. Typing goes straight into the entry (the tiles update here).</summary>
        public Action Tick()
        {
            RelayoutIfNeeded();
            TickDots(PawTime.UnscaledDt);
            if (BackOrOutside()) return Action.Back;
            switch (face)
            {
                case Face.Menu:
                    if (create.Tick()) return Action.Create;
                    if (joinMenu.Tick()) return Action.JoinMenu;
                    break;
                case Face.Created:
                    if (share.Tick()) return Action.Share;
                    if (duel.Tick()) return Action.Duel;
                    break;
                case Face.Join:
                {
                    if (join.Tick()) return Action.Join;
                    if (cancel.Tick()) return Action.Back;
                    int k = keypad.Tick();
                    if (k >= 0 && entry != null)
                    {
                        bool changed = k == KeypadLayout.Delete ? entry.Backspace() : entry.Add(KeypadLayout.LetterAt(k));
                        if (changed) RefreshEntry();
                    }
#if UNITY_EDITOR
                    EditorTyping();
#endif
                    break;
                }
                case Face.Waiting:
                    if (cancel.Tick()) return Action.Back;
                    break;
            }
            SwallowTaps();
            return Action.None;
        }

#if UNITY_EDITOR
        /// <summary>Editor convenience: the Mac keyboard types into the pixel keypad too (letters, Backspace).</summary>
        private void EditorTyping()
        {
            if (entry == null || UiPointer.LegacyDead) return;
            try
            {
                string s = UnityEngine.Input.inputString;
                bool changed = false;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '\b') changed |= entry.Backspace();
                    else changed |= entry.Add(s[i]);
                }
                if (changed) RefreshEntry();
            }
            catch (System.InvalidOperationException)
            {
                // Input System only: no legacy keyboard in the Editor, the pixel keypad still works
            }
        }
#endif
    }
}
