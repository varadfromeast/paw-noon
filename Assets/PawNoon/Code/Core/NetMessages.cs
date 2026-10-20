using System;
using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>200 from <c>POST /match-start</c> (schemas/match-start.response.schema.json), the parts the client uses.</summary>
    public sealed class MatchStartInfo
    {
        public string MatchId;
        public string Mode = "casual";
        public int Seed;
        public int Rounds = RecordedMatchRules.DefaultRounds;
        public int MaxAttempts = RecordedMatchRules.DefaultMaxAttempts;
        public string Difficulty = DuelConfig.DifficultyHard;
        public string DeadlineAt;
        public int YouMmr;
        public string RecordingId;
        public string OpponentName = "Stranger";
        /// <summary>Canonical persona id ("pawtruth" / "whiskers"); the legacy server id "blackpaw" is normalised.</summary>
        public string OpponentCharacter = CharacterRoster.Pawtruth;
        public string OpponentGun = "standard";
        public int OpponentMmr;
        public bool OpponentIsHouse;
        public GhostAttempt[] OpponentAttempts = new GhostAttempt[0];
        /// <summary>Friend duel (room-join): the room code this match came from, else null.</summary>
        public string Room;

        /// <summary>Null on anything malformed (error says what).</summary>
        public static MatchStartInfo Parse(string json, out string error)
        {
            error = null;
            var root = PnJson.ParseObject(json);
            if (root == null) { error = "not a JSON object"; return null; }
            var m = new MatchStartInfo();
            m.MatchId = PnJson.Str(root, "matchId", null);
            if (string.IsNullOrEmpty(m.MatchId)) { error = "missing matchId"; return null; }
            if (!PnJson.Has(root, "seed")) { error = "missing seed"; return null; }
            double seed = PnJson.Num(root, "seed", 0);
            if (seed < int.MinValue || seed > int.MaxValue || Math.Floor(seed) != seed) { error = "seed is not an int32"; return null; }
            m.Seed = (int)seed;
            m.Mode = PnJson.Str(root, "mode", "casual");
            m.Rounds = PnJson.Int(root, "rounds", RecordedMatchRules.DefaultRounds);
            m.MaxAttempts = PnJson.Int(root, "maxAttempts", RecordedMatchRules.DefaultMaxAttempts);
            m.Difficulty = PnJson.Str(root, "difficulty", DuelConfig.DifficultyHard);
            m.DeadlineAt = PnJson.Str(root, "deadlineAt", null);
            m.YouMmr = PnJson.Int(PnJson.Obj(root, "you"), "mmr", 0);
            m.Room = RoomCode.Normalize(PnJson.Str(PnJson.Obj(root, "room"), "code", null));
            var opp = PnJson.Obj(root, "opponent");
            if (opp == null) { error = "missing opponent"; return null; }
            m.RecordingId = PnJson.Str(opp, "recordingId", null);
            m.OpponentName = PnJson.Str(opp, "displayName", "Stranger");
            m.OpponentCharacter = CharacterRoster.Normalize(PnJson.Str(opp, "character", CharacterRoster.Pawtruth));
            m.OpponentGun = PnJson.Str(PnJson.Obj(opp, "loadout"), "gun", "standard");
            m.OpponentMmr = PnJson.Int(opp, "mmr", 0);
            m.OpponentIsHouse = PnJson.Bool(opp, "isHouse", false);
            var rounds = PnJson.Arr(opp, "rounds");
            if (rounds == null || rounds.Count == 0) { error = "opponent has no rounds"; return null; }
            m.OpponentAttempts = new GhostAttempt[rounds.Count];
            for (int i = 0; i < rounds.Count; i++)
            {
                GhostAttempt a;
                if (!ParseAttempt(rounds[i], out a)) { error = "opponent.rounds[" + i + "] is malformed"; return null; }
                m.OpponentAttempts[i] = a;
            }
            return m;
        }

        /// <summary>schemas/attempt.schema.json -> GhostAttempt.</summary>
        public static bool ParseAttempt(object node, out GhostAttempt a)
        {
            a = new GhostAttempt();
            a.inputOffsetNs = -1;
            if (!(node is Dictionary<string, object>)) return false;
            AttemptKind k;
            if (!AttemptKinds.TryParse(PnJson.Str(node, "kind", ""), out k)) return false;
            a.kind = k;
            a.roundNumber = PnJson.Int(node, "roundNumber", 0);
            long r = Ns(node, "reactionNs", 0);
            a.reactionNs = (k == AttemptKind.Shot || k == AttemptKind.Anticipation) ? Math.Max(0L, r) : 0;
            if (k == AttemptKind.Early && PnJson.Has(node, "inputOffsetNs"))
            {
                long off = Ns(node, "inputOffsetNs", -1);
                a.inputOffsetNs = off >= 0 ? off : -1;
            }
            return true;
        }

        /// <summary>Integer ns from a JSON number (exact below 2^53).</summary>
        public static long Ns(object node, string key, long def)
        {
            if (!PnJson.Has(node, key)) return def;
            double d = PnJson.Num(node, key, def);
            if (double.IsNaN(d) || double.IsInfinity(d)) return def;
            return (long)Math.Round(d);
        }
    }

    /// <summary>200 from <c>POST /match-submit</c> (schemas/match-submit.response.schema.json): the server's own recompute.</summary>
    public sealed class MatchVerdict
    {
        public string MatchId;
        public string Status;
        /// <summary>"you", "opponent" or "draw".</summary>
        public string Winner;
        public string EndReason;
        public double HpYou, HpOpponent;
        public int RoundWinsYou, RoundWinsOpponent;
        public bool HasRating;
        public int RatingBefore, RatingAfter, RatingDelta;
        /// <summary>Friend duel: this recording was reserved for the friend of the player's open room (code), else null.</summary>
        public string Room;

        /// <summary>0 = you, 1 = opponent, -1 = draw (same slots as the client).</summary>
        public int WinnerSlot { get { return Winner == "you" ? 0 : (Winner == "opponent" ? 1 : -1); } }

        public static MatchVerdict Parse(string json, out string error)
        {
            error = null;
            var root = PnJson.ParseObject(json);
            if (root == null) { error = "not a JSON object"; return null; }
            var v = new MatchVerdict();
            v.MatchId = PnJson.Str(root, "matchId", null);
            v.Status = PnJson.Str(root, "status", null);
            v.Winner = PnJson.Str(root, "winner", null);
            if (v.Winner != "you" && v.Winner != "opponent" && v.Winner != "draw") { error = "missing winner"; return null; }
            v.EndReason = PnJson.Str(root, "endReason", "");
            var hp = PnJson.Obj(root, "hp");
            v.HpYou = PnJson.Num(hp, "you", 0);
            v.HpOpponent = PnJson.Num(hp, "opponent", 0);
            var rw = PnJson.Obj(root, "roundWins");
            v.RoundWinsYou = PnJson.Int(rw, "you", 0);
            v.RoundWinsOpponent = PnJson.Int(rw, "opponent", 0);
            var rating = PnJson.Obj(root, "rating");
            if (rating != null)
            {
                v.HasRating = true;
                v.RatingBefore = PnJson.Int(rating, "before", 0);
                v.RatingAfter = PnJson.Int(rating, "after", 0);
                v.RatingDelta = PnJson.Int(rating, "delta", 0);
            }
            v.Room = RoomCode.Normalize(PnJson.Str(PnJson.Obj(root, "room"), "code", null));
            return v;
        }

        /// <summary>The verdict the server would give for a local recompute (offline mock).</summary>
        public static MatchVerdict FromSim(string matchId, RecordedMatchResult r)
        {
            var v = new MatchVerdict();
            v.MatchId = matchId;
            v.Status = "completed";
            v.Winner = r.Winner == 0 ? "you" : (r.Winner == 1 ? "opponent" : "draw");
            v.EndReason = RecordedMatchRules.ToWire(r.EndReason);
            v.HpYou = r.Hp[0];
            v.HpOpponent = r.Hp[1];
            v.RoundWinsYou = r.RoundWins[0];
            v.RoundWinsOpponent = r.RoundWins[1];
            return v;
        }
    }

    /// <summary>Error body <c>{ error, message, ... }</c> (schemas/error.schema.json).</summary>
    public struct ApiError
    {
        public string code;
        public string message;
        /// <summary>422 recording_rejected: the reason (late, seed_mismatch, ...).</summary>
        public string reason;

        public static ApiError Parse(string json)
        {
            var e = new ApiError();
            var root = PnJson.ParseObject(json);
            e.code = PnJson.Str(root, "error", "");
            e.message = PnJson.Str(root, "message", "");
            e.reason = PnJson.Str(root, "reason", "");
            return e;
        }
    }
}
