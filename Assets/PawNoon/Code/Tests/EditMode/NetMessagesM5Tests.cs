using System;
using System.Text;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>M5 wire messages: room-create, room-join (waiting / ready), leaderboard-read, verdict rating + room, JWT claims.</summary>
    public class NetMessagesM5Tests
    {
        private const string Ready = @"{""status"":""ready"",""matchId"":""9b2f6c1e-3a57-4d2e-9f0a-2c4b8e7d1a10"",""mode"":""casual"",""season"":null,
            ""difficulty"":""hard"",""seed"":4242,""rounds"":3,""maxAttempts"":9,""gunsNormalised"":false,
            ""issuedAt"":""2026-10-20T10:00:00.000Z"",""deadlineAt"":""2026-10-20T10:04:33.000Z"",""you"":{""mmr"":1200,""rd"":350},
            ""opponent"":{""recordingId"":""r"",""displayName"":""Pardner Pete"",""character"":""whiskers"",""loadout"":{""gun"":""standard""},
              ""mmr"":1187,""isHouse"":false,""rounds"":[{""roundNumber"":1,""kind"":""shot"",""reactionNs"":250000000}]},
            ""room"":{""code"":""HOWL""}}";

        [Test]
        public void RoomCreatedParses()
        {
            string err;
            var r = RoomCreated.Parse(@"{""code"":""HOWL"",""status"":""open"",""expiresAt"":""2026-10-20T10:10:00Z"",""ttlSec"":600}", out err);
            Assert.IsNotNull(r, err);
            Assert.AreEqual("HOWL", r.Code);
            Assert.AreEqual(600, r.TtlSec);
            Assert.AreEqual("2026-10-20T10:10:00Z", r.ExpiresAt);
            Assert.IsNull(RoomCreated.Parse(@"{""code"":""HOWLS""}", out err));
            Assert.IsNull(RoomCreated.Parse("nope", out err));
        }

        [Test]
        public void RoomJoinWaitingParses()
        {
            string err;
            var a = RoomJoinAnswer.Parse(@"{""status"":""waiting"",""code"":""HOWL"",""hostName"":""Pardner Pete"",""expiresAt"":""2026-10-20T10:10:00Z"",""pollAfterSec"":3}", out err);
            Assert.IsNotNull(a, err);
            Assert.IsTrue(a.Waiting);
            Assert.AreEqual("HOWL", a.Code);
            Assert.AreEqual("Pardner Pete", a.HostName);
            Assert.AreEqual(3, a.PollAfterSec);
            Assert.IsNull(a.Match);
            var b = RoomJoinAnswer.Parse(@"{""status"":""waiting"",""code"":""HOWL"",""pollAfterSec"":0}", out err);
            Assert.AreEqual(1, b.PollAfterSec, "never poll faster than once a second");
        }

        [Test]
        public void RoomJoinReadyIsAMatchStartWithTheRoom()
        {
            string err;
            var a = RoomJoinAnswer.Parse(Ready, out err);
            Assert.IsNotNull(a, err);
            Assert.IsFalse(a.Waiting);
            Assert.IsNotNull(a.Match);
            Assert.AreEqual(4242, a.Match.Seed, "the host's seed: the friend gets the same holds");
            Assert.AreEqual("HOWL", a.Match.Room);
            Assert.AreEqual("HOWL", a.Code);
            Assert.AreEqual("Pardner Pete", a.HostName);
            Assert.AreEqual(1, a.Match.OpponentAttempts.Length);
            Assert.IsNull(MatchStartInfo.Parse(Ready.Replace(@"""room"":{""code"":""HOWL""}", @"""room"":null"), out err).Room, "a quick duel has no room");
        }

        [Test]
        public void VerdictCarriesRatingAndRoom()
        {
            string err;
            var v = MatchVerdict.Parse(@"{""matchId"":""m"",""status"":""completed"",""winner"":""you"",""endReason"":""rounds"",
                ""hp"":{""you"":40,""opponent"":10},""roundWins"":{""you"":2,""opponent"":1},""rounds"":[],
                ""rating"":{""season"":202643,""before"":1202,""after"":1214,""delta"":12,""rd"":250},""recordingId"":""r"",
                ""room"":{""code"":""DUST"",""status"":""recorded""}}", out err);
            Assert.IsNotNull(v, err);
            Assert.IsTrue(v.HasRating);
            Assert.AreEqual(1214, v.RatingAfter);
            Assert.AreEqual(12, v.RatingDelta);
            Assert.AreEqual("DUST", v.Room);
        }

        [Test]
        public void LeaderboardParsesTopAndYou()
        {
            string err;
            var b = LeaderboardBoard.Parse(@"{""board"":""ranked_2026_43"",""kind"":""ranked"",""unit"":""mmr"",""season"":202643,
                ""closesAt"":""2026-10-26T00:00:00.000Z"",""minMatches"":5,""total"":412,
                ""top"":[{""rank"":1,""displayName"":""Old Tom"",""character"":""blackpaw"",""value"":1702,""matches"":30,""you"":false},
                         {""rank"":1,""displayName"":""Dusty"",""character"":""whiskers"",""value"":1702,""matches"":12,""you"":true}],
                ""you"":{""rank"":1,""value"":1702,""matches"":12,""eligible"":true}}", out err);
            Assert.IsNotNull(b, err);
            Assert.IsTrue(b.IsRanked);
            Assert.AreEqual(2, b.Top.Length);
            Assert.AreEqual(1, b.Top[1].Rank, "ties share a rank");
            Assert.IsTrue(b.Top[1].You);
            Assert.AreEqual(1702, b.Top[0].Value);
            Assert.AreEqual(412, b.Total);
            Assert.IsTrue(b.YouEligible);
            Assert.AreEqual("WEEK 43", LeaderboardBoard.WeekLabel(b.Board));

            var f = LeaderboardBoard.Parse(@"{""board"":""best_reaction_hard"",""kind"":""best_reaction_hard"",""unit"":""ns"",""season"":null,
                ""closesAt"":null,""minMatches"":0,""total"":0,""top"":[],""you"":null}", out err);
            Assert.IsNotNull(f, err);
            Assert.IsFalse(f.IsRanked);
            Assert.AreEqual(0, f.Top.Length);
            Assert.IsNull(f.You, "never shot: no row");

            var n = LeaderboardBoard.Parse(@"{""kind"":""ranked"",""board"":""ranked_season_7"",""top"":[],
                ""you"":{""rank"":null,""value"":1188,""matches"":2,""eligible"":false}}", out err);
            Assert.AreEqual(0, n.You.Rank, "not ranked yet");
            Assert.IsFalse(n.YouEligible);
            Assert.AreEqual("SEASON 7", LeaderboardBoard.WeekLabel(n.Board));
            Assert.IsNull(LeaderboardBoard.Parse(@"{""kind"":""weekly""}", out err));
        }

        [Test]
        public void FastestValuesAreExactNanoseconds()
        {
            string err;
            var b = LeaderboardBoard.Parse(@"{""kind"":""best_reaction_hard"",""top"":[{""rank"":1,""displayName"":""A"",""value"":151300123,""matches"":3,""you"":false}]}", out err);
            Assert.AreEqual(151300123L, b.Top[0].Value);
        }

        private static string Jwt(string payloadJson)
        {
            string p = Convert.ToBase64String(Encoding.UTF8.GetBytes(payloadJson)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return "eyJhbGciOiJIUzI1NiJ9." + p + ".sig";
        }

        [Test]
        public void JwtIsLinkedOnlyForAPositivelyNonAnonymousToken()
        {
            Assert.IsTrue(JwtClaims.IsLinked(Jwt(@"{""sub"":""u"",""is_anonymous"":false,""role"":""authenticated""}")));
            Assert.IsFalse(JwtClaims.IsLinked(Jwt(@"{""sub"":""u"",""is_anonymous"":true}")));
            Assert.IsFalse(JwtClaims.IsLinked(Jwt(@"{""sub"":""u""}")), "no claim: treat as a guest");
            Assert.IsFalse(JwtClaims.IsLinked("not.a.jwt!"));
            Assert.IsFalse(JwtClaims.IsLinked(""));
            Assert.IsFalse(JwtClaims.IsLinked(null));
            // base64url with '-' / '_' and no padding
            Assert.AreEqual("ü?>", PnJson.Str(JwtClaims.Payload(Jwt(@"{""n"":""ü?>"",""is_anonymous"":false}")), "n", null));
        }
    }
}
