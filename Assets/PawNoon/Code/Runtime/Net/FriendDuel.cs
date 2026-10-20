using System;
using System.Globalization;
using PawNoon.Core;
using UnityEngine;

namespace PawNoon.Runtime.Net
{
    /// <summary>
    /// Friend duels by room code (SPEC_V2 §4.4, M5), behind net_config "friendDuel".
    ///
    /// Host: FRIEND -> CREATE ROOM -> POST /room-create -> the code, big, with SHARE (NativeShare: share sheet or
    /// clipboard) and DUEL. The server reserves the host's NEXT recording for the one friend who joins: DUEL starts
    /// an ordinary casual quick duel, and that match's recording becomes the friend's opponent.
    ///
    /// Friend: FRIEND -> JOIN ROOM -> 4 letters on the pixel keypad -> POST /room-join. 202 waiting: the WAITING card
    /// polls every pollAfterSec (3 s) until the room closes (10 min); 200 ready: the host's recording is the rival
    /// (same rival card and match path as a quick duel, OnlineDirector.ShowRival).
    /// Every failure is a short status line in the card (never raw codes). Mock: NetMock rooms.
    /// </summary>
    public sealed class FriendDuel
    {
        public enum State { Closed, Menu, Creating, Created, Join, Joining, Waiting }

        private const float MockAnswerSeconds = 0.7f;
        private const int MockPollsBeforeReady = 2;

        private readonly OnlineDirector online;
        private readonly SupabaseApi api;
        private readonly FriendCard card;
        private readonly RoomCodeEntry entry = new RoomCodeEntry();
        private State state = State.Closed;
        private int token;
        private bool mock;
        private string code;
        private string hostName = "";
        private DateTime expiresUtc;
        private float pollT;
        private float pollEvery = 3f;
        private bool pollInFlight;
        private float mockT = -1f;
        private int mockPolls;
        private float statusHold;

        public State Current { get { return state; } }
        public bool Open { get { return state != State.Closed; } }
        public string Code { get { return code; } }
        public RoomCodeEntry Entry { get { return entry; } }

        public FriendDuel(OnlineDirector online, SupabaseApi api, FriendCard card)
        {
            this.online = online;
            this.api = api;
            this.card = card;
        }

        // ------------------------------------------------------------------ entry points

        public void OpenMenu(bool useMock)
        {
            token++;
            mock = useMock;
            state = State.Menu;
            card.ShowMenu();
        }

        public void Close()
        {
            token++;
            state = State.Closed;
            mockT = -1f;
            pollInFlight = false;
            card.Close();
        }

        private void CreateRoom()
        {
            token++;
            state = State.Creating;
            code = null;
            card.ShowCreated(null);
            if (mock) { mockT = MockAnswerSeconds; return; }
            int my = token;
            api.RoomCreate(r => { if (my == token && state == State.Creating) OnCreated(r); });
        }

        private void OnCreated(ApiResult r)
        {
            if (!r.Ok)
            {
                card.SetStatus(Friendly(r), Pal.SigRed);
                return;
            }
            string err;
            var room = RoomCreated.Parse(r.Body, out err);
            if (room == null)
            {
                Debug.LogWarning("[PawNoon] room-create answer unreadable: " + err);
                card.SetStatus("GARBLED WIRE. TRY AGAIN.", Pal.SigRed);
                return;
            }
            ShowCreated(room.Code, ParseUtc(room.ExpiresAt, room.TtlSec));
        }

        private void ShowCreated(string c, DateTime expires)
        {
            state = State.Created;
            code = c;
            expiresUtc = expires;
            statusHold = 0f;
            card.ShowCreated(c);
            TickClock();
        }

        private void OpenJoin(string hint, bool isError)
        {
            token++;
            state = State.Join;
            card.ShowJoin(entry, hint ?? "Type the 4 letters.", isError);
        }

        private void SubmitJoin()
        {
            if (!entry.Complete) return;
            token++;
            state = State.Joining;
            code = entry.Code;
            pollInFlight = true;
            card.SetStatus("KNOCKING...", Pal.Brass1);
            if (mock) { mockPolls = 0; mockT = MockAnswerSeconds; return; }
            int my = token;
            api.RoomJoin(code, r => { if (my == token) OnJoinAnswer(r); });
        }

        private void Poll()
        {
            pollInFlight = true;
            pollT = 0f;
            if (mock) { mockT = 0.2f; return; }
            int my = token;
            api.RoomJoin(code, r => { if (my == token) OnJoinAnswer(r); });
        }

        private void OnJoinAnswer(ApiResult r)
        {
            pollInFlight = false;
            if (state != State.Joining && state != State.Waiting) return;
            if (!r.Ok)
            {
                // a dropped poll is not the end of the room: keep waiting, the next poll tries again
                if (state == State.Waiting && r.Offline) { card.SetStatus("NO SIGNAL. RETRYING...", Pal.SigRed); statusHold = 2f; return; }
                OpenJoin(Friendly(r), true);
                return;
            }
            string err;
            var a = RoomJoinAnswer.Parse(r.Body, out err);
            if (a == null)
            {
                Debug.LogWarning("[PawNoon] room-join answer unreadable: " + err + " " + Http.Short(r.Body));
                OpenJoin("GARBLED WIRE. TRY AGAIN.", true);
                return;
            }
            Apply(a);
        }

        private void Apply(RoomJoinAnswer a)
        {
            if (a.Waiting)
            {
                if (state != State.Waiting)
                {
                    state = State.Waiting;
                    hostName = a.HostName;
                    expiresUtc = ParseUtc(a.ExpiresAt, 600);
                    card.ShowWaiting(code, a.HostName);
                }
                pollEvery = Mathf.Clamp(a.PollAfterSec, 1, 30);
                pollT = 0f;
                TickClock();
                return;
            }
            // ready: the host's ghost is the rival; the online director takes it from here
            token++;
            state = State.Closed;
            card.Close();
            online.ShowRival(a.Match, mock);
        }

        // ------------------------------------------------------------------ frame

        /// <summary>Drives the open card. Returns true when the flow closed back to the title.</summary>
        public bool Tick()
        {
            if (state == State.Closed) return true;
            float dt = PawTime.UnscaledDt;
            var act = card.Tick();
            switch (act)
            {
                case FriendCard.Action.Back:
                    if (state == State.Menu) { Close(); return true; }
                    if (state == State.Waiting || state == State.Joining) { OpenJoin(null, false); return false; }
                    token++;
                    state = State.Menu;
                    card.ShowMenu();
                    return false;
                case FriendCard.Action.Create: CreateRoom(); return false;
                case FriendCard.Action.JoinMenu: OpenJoin(null, false); return false;
                case FriendCard.Action.Join: SubmitJoin(); return false;
                case FriendCard.Action.Share: Share(); return false;
                case FriendCard.Action.Duel:
                    // the next recording is the friend's: an ordinary casual quick duel records it
                    Close();
                    online.FindRival("casual");
                    return true;
            }

            if (mockT > 0f)
            {
                mockT -= dt;
                if (mockT <= 0f) MockAnswer();
            }

            if (state == State.Waiting)
            {
                pollT += dt;
                if (!pollInFlight && pollT >= pollEvery) Poll();
                if (DateTime.UtcNow >= expiresUtc)
                {
                    OpenJoin("ROOM CLOSED. ASK FOR A NEW CODE.", true);
                    return false;
                }
            }
            if (statusHold > 0f) { statusHold -= dt; if (statusHold <= 0f) TickClock(true); }
            else TickClock();
            return false;
        }

        private void TickClock(bool force = false)
        {
            if (statusHold > 0f && !force) return;
            if (state != State.Created && state != State.Waiting) return;
            int secs = (int)Math.Ceiling((expiresUtc - DateTime.UtcNow).TotalSeconds);
            card.SetClock("CLOSES IN ", secs, secs <= 60 ? Pal.SigRed : Pal.Brass1);
        }

        private void Share()
        {
            if (string.IsNullOrEmpty(code)) return;
            var res = NativeShare.Share(NativeShare.RoomMessage(code));
            card.SetStatus(res == NativeShare.Result.Shared ? "SENT!" : (res == NativeShare.Result.Copied ? "COPIED! PASTE IT TO A FRIEND." : "SHOW THEM THIS SCREEN."), Pal.SigGreen);
            statusHold = 2.5f;
        }

        // ------------------------------------------------------------------ mock

        private void MockAnswer()
        {
            switch (state)
            {
                case State.Creating:
                    var room = NetMock.CreateRoom();
                    ShowCreated(room.Code, ParseUtc(room.ExpiresAt, room.TtlSec));
                    break;
                case State.Joining:
                case State.Waiting:
                    pollInFlight = false;
                    string why = NetMock.JoinError(code);
                    if (why != null) { OpenJoin(why, true); break; }
                    Apply(NetMock.JoinRoom(code, mockPolls++ >= MockPollsBeforeReady));
                    break;
            }
        }

        // ------------------------------------------------------------------ review

        /// <summary>Review stills: "room" (host card with a code), "join" (keypad, two letters typed), "waiting".</summary>
        public void ReviewShow(string what)
        {
            mock = true;
            token++;
            mockT = -1f;
            switch (what)
            {
                case "room":
                    var room = NetMock.CreateRoom();
                    ShowCreated(room.Code, ParseUtc(room.ExpiresAt, room.TtlSec));
                    break;
                case "join":
                    entry.Set("HO");
                    OpenJoin(null, false);
                    break;
                case "waiting":
                    entry.Set("HOWL");
                    code = entry.Code;
                    mockPolls = 0;
                    state = State.Joining;
                    Apply(NetMock.JoinRoom(code, false));
                    break;
                default:
                    OpenMenu(true);
                    break;
            }
        }

        public string Status()
        {
            return "friend=" + state + (code != null ? "(" + code + ")" : "") + (state == State.Join ? " typed=" + entry.Count : "") +
                   (state == State.Waiting ? " host=\"" + hostName + "\"" : "");
        }

        // ------------------------------------------------------------------ helpers

        private static DateTime ParseUtc(string iso, int fallbackSeconds)
        {
            DateTime t;
            if (!string.IsNullOrEmpty(iso) &&
                DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out t))
                return t;
            return DateTime.UtcNow.AddSeconds(fallbackSeconds);
        }

        /// <summary>Short pixel-font words for a failed room call.</summary>
        public static string Friendly(ApiResult r)
        {
            if (r == null) return "TELEGRAPH IS DOWN. TRY AGAIN.";
            if (r.Offline) return "NO SIGNAL. TRY AGAIN.";
            switch (r.Error.code)
            {
                case "room_not_found": return "NO ROOM WITH THAT CODE.";
                case "room_expired": return "ROOM CLOSED. ASK FOR A NEW CODE.";
                case "room_used": return "THAT ROOM WAS ALREADY PLAYED.";
                case "own_room": return "THAT'S YOUR OWN ROOM. SHARE IT!";
                case "room_taken": return "ANOTHER FRIEND GOT THERE FIRST.";
                case "rate_limited": return "EASY, PARTNER. WAIT A MINUTE.";
                case "not_configured": return "ONLINE IS NOT SET UP.";
                case "auth_failed":
                case "unauthorized":
                case "no_profile": return "COULD NOT SIGN IN. TRY AGAIN.";
                default: return "TELEGRAPH IS DOWN. TRY AGAIN.";
            }
        }
    }
}
