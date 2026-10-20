using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using PawNoon.Core;

namespace PawNoon.Tests
{
    /// <summary>
    /// M6 accounts (Code/Core/Accounts.cs): display-name rules against the vectors the server tests too
    /// (Tools/Parity/display_names.json, supabase/functions/_shared/account.test.ts), the profile / auth-user / auth-error
    /// JSON the client reads, the link-answer classification and the once-only KEEP YOUR BOUNTY gate.
    /// </summary>
    public class AccountTests
    {
        // ------------------------------------------------------------------ display names

        [Test]
        public void DisplayNamesMatchTheSharedVectors()
        {
            string path = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, "..", "Tools", "Parity", "display_names.json"));
            Assert.IsTrue(File.Exists(path), "missing " + path);
            var cases = PnJson.Arr(PnJson.Parse(File.ReadAllText(path)), "cases");
            Assert.IsNotNull(cases);
            Assert.GreaterOrEqual(cases.Count, 30);
            for (int i = 0; i < cases.Count; i++)
            {
                string input = PnJson.Str(cases[i], "in", null);
                bool ok = PnJson.Bool(cases[i], "ok", false);
                string normalized;
                var e = DisplayName.Validate(input, out normalized);
                string label = "case " + i + " \"" + input + "\"";
                if (ok)
                {
                    Assert.AreEqual(DisplayNameError.None, e, label);
                    Assert.AreEqual(PnJson.Str(cases[i], "name", null), normalized, label);
                }
                else
                {
                    Assert.AreEqual(PnJson.Str(cases[i], "reason", null), DisplayName.WireName(e), label);
                }
            }
        }

        [Test]
        public void DisplayNameBasics()
        {
            Assert.AreEqual("A B C", DisplayName.Normalize("  a  b  c "));
            Assert.AreEqual("", DisplayName.Normalize(null));
            Assert.AreEqual(DisplayNameError.TooShort, DisplayName.Validate(null, out _));
            Assert.IsTrue(DisplayName.IsValid("BILLY THE CAT"));
            Assert.IsFalse(DisplayName.IsValid("BILLY THE CAT JR"));
            Assert.AreEqual(3, DisplayName.MinLength);
            Assert.AreEqual(14, DisplayName.MaxLength);
            foreach (DisplayNameError e in System.Enum.GetValues(typeof(DisplayNameError)))
                Assert.AreEqual(e, DisplayName.FromWire(DisplayName.WireName(e)), "wire round trip " + e);
            for (char c = (char)0; c < 128; c++)
                Assert.AreEqual((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == ' ', DisplayName.IsAllowedChar(c), "char " + (int)c);
        }

        [Test]
        public void GuestNamesAndDisplay()
        {
            Assert.AreEqual("STRANGER #1234", DisplayName.GuestName(1234));
            Assert.AreEqual("STRANGER #0042", DisplayName.GuestName(42));
            Assert.AreEqual("STRANGER #0007", DisplayName.GuestName(-10007));
            int n = DisplayName.GuestNumber("5d0b1c9e-1f7e-4d53-a1a6-6f3f8b9d2c44");
            Assert.That(n, Is.InRange(0, 9999));
            Assert.AreEqual(n, DisplayName.GuestNumber("5d0b1c9e-1f7e-4d53-a1a6-6f3f8b9d2c44"), "stable");
            Assert.AreEqual("STRANGER #1234", DisplayName.ForDisplay("Stranger #1234"));
            Assert.AreEqual("BILLY", DisplayName.ForDisplay("  billy\n"));
            Assert.AreEqual("STRANGER", DisplayName.ForDisplay(""));
            Assert.AreEqual("STRANGER", DisplayName.ForDisplay("éé"));
            Assert.LessOrEqual(DisplayName.ForDisplay("ABCDEFGHIJKLMNOPQRSTUVWXYZ").Length, 16);
        }

        // ------------------------------------------------------------------ profile JSON

        private const string ProfileJson =
            "{\"userId\":\"u1\",\"displayName\":\"Stranger #0042\",\"character\":\"whiskers\",\"isGuest\":true,\"providers\":[],\"linkedAt\":null," +
            "\"stats\":{\"matches\":12,\"wins\":7,\"winRate\":0.583,\"bestReactionNs\":198412000,\"lightning\":3}," +
            "\"rating\":{\"season\":1,\"mmr\":1234,\"rd\":120,\"matches\":4},\"coins\":20,\"dailyStreak\":2," +
            "\"ownedItems\":[\"peacemaker\"],\"nameRules\":{\"min\":3,\"max\":14,\"chars\":\"A-Z 0-9 space\"}}";

        [Test]
        public void ProfileParsesTheServerAnswer()
        {
            string err;
            var p = ProfileInfo.Parse(ProfileJson, out err);
            Assert.IsNotNull(p, err);
            Assert.AreEqual("u1", p.UserId);
            Assert.AreEqual("Stranger #0042", p.DisplayName);
            Assert.AreEqual("whiskers", p.Character);
            Assert.IsTrue(p.IsGuest);
            Assert.AreEqual(0, p.Providers.Length);
            Assert.AreEqual(12, p.Matches);
            Assert.AreEqual(7, p.Wins);
            Assert.AreEqual(58, p.WinPercent);
            Assert.AreEqual(198412000L, p.BestReactionNs);
            Assert.AreEqual(3, p.Lightning);
            Assert.AreEqual(1234, p.Mmr);
            Assert.IsTrue(p.Rated);
            Assert.AreEqual(20, p.Coins);
            Assert.AreEqual(2, p.DailyStreak);
            CollectionAssert.AreEqual(new[] { "peacemaker" }, p.OwnedItems);
        }

        [Test]
        public void ProfileToleratesNullsAndRejectsGarbage()
        {
            string err;
            var p = ProfileInfo.Parse(
                "{\"displayName\":\"BILLY\",\"isGuest\":false,\"providers\":[\"apple\"],\"stats\":{\"matches\":0,\"wins\":3,\"bestReactionNs\":null,\"lightning\":-2}," +
                "\"rating\":{\"season\":1,\"mmr\":1200,\"rd\":350,\"matches\":0}}", out err);
            Assert.IsNotNull(p, err);
            Assert.IsFalse(p.IsGuest);
            CollectionAssert.AreEqual(new[] { "apple" }, p.Providers);
            Assert.AreEqual(-1L, p.BestReactionNs, "null best = none");
            Assert.AreEqual(0, p.Wins, "wins never exceed matches");
            Assert.AreEqual(0, p.Lightning);
            Assert.AreEqual(0, p.WinPercent);
            Assert.IsFalse(p.Rated);
            Assert.AreEqual(0, p.OwnedItems.Length);

            Assert.IsNull(ProfileInfo.Parse("{\"stats\":{}}", out err));
            Assert.AreEqual("missing displayName", err);
            Assert.IsNull(ProfileInfo.Parse("not json", out err));
            Assert.IsNull(ProfileInfo.Parse(null, out err));
            Assert.IsNull(ProfileInfo.Parse("[1,2]", out err));
        }

        [Test]
        public void CombinedBestTakesTheLowerOfServerAndDevice()
        {
            string err;
            var p = ProfileInfo.Parse(ProfileJson, out err);
            Assert.AreEqual(198412000L, ProfileInfo.CombinedBest(p, -1));
            Assert.AreEqual(150000000L, ProfileInfo.CombinedBest(p, 150000000L));
            Assert.AreEqual(198412000L, ProfileInfo.CombinedBest(p, 250000000L));
            Assert.AreEqual(250000000L, ProfileInfo.CombinedBest(null, 250000000L));
            Assert.AreEqual(-1L, ProfileInfo.CombinedBest(null, 0));
        }

        // ------------------------------------------------------------------ auth user / errors / link answers

        [Test]
        public void AuthUserFromGoTrueSessions()
        {
            var anon = AuthUserInfo.Parse(PnJson.Parse(
                "{\"id\":\"u1\",\"is_anonymous\":true,\"app_metadata\":{},\"identities\":[],\"created_at\":\"2026-10-20T10:00:00Z\"}"));
            Assert.AreEqual("u1", anon.Id);
            Assert.IsTrue(anon.IsAnonymous);
            Assert.IsFalse(anon.IsLinked);
            Assert.AreEqual(1792490400L, anon.CreatedAt);

            var apple = AuthUserInfo.Parse(PnJson.Parse(
                "{\"id\":\"u1\",\"is_anonymous\":false,\"app_metadata\":{\"provider\":\"apple\",\"providers\":[\"apple\"]}," +
                "\"identities\":[{\"provider\":\"apple\",\"id\":\"001234.abc\"}]}"));
            Assert.IsTrue(apple.IsLinked);
            CollectionAssert.AreEqual(new[] { "apple" }, apple.Providers);

            var both = AuthUserInfo.Parse(PnJson.Parse(
                "{\"id\":\"u1\",\"is_anonymous\":false,\"app_metadata\":{\"provider\":\"anonymous\",\"providers\":[\"anonymous\",\"google\"]},\"identities\":[{\"provider\":\"apple\"}]}"));
            CollectionAssert.AreEquivalent(new[] { "apple", "google" }, both.Providers);
            Assert.IsTrue(both.HasProvider("google"));

            // older Auth without is_anonymous: no identity = anonymous
            Assert.IsTrue(AuthUserInfo.Parse(PnJson.Parse("{\"id\":\"u1\",\"identities\":[]}")).IsAnonymous);
            Assert.IsFalse(AuthUserInfo.Parse(PnJson.Parse("{\"id\":\"u1\",\"identities\":[{\"provider\":\"google\"}]}")).IsAnonymous);
            Assert.IsNull(AuthUserInfo.Parse(PnJson.Parse("{\"is_anonymous\":true}")));
            Assert.IsNull(AuthUserInfo.Parse(null));

            CollectionAssert.AreEqual(new[] { "apple", "google" }, AuthUserInfo.Split(AuthUserInfo.Join(new[] { "apple", "google" })));
            Assert.AreEqual(0, AuthUserInfo.Split("").Length);
        }

        [Test]
        public void AuthErrorsOldAndNew()
        {
            var a = AuthError.Parse(422, "{\"code\":422,\"error_code\":\"identity_already_exists\",\"msg\":\"Identity is already linked to another user\"}");
            Assert.AreEqual("identity_already_exists", a.Code);
            Assert.IsTrue(a.IdentityTaken);

            var b = AuthError.Parse(422, "{\"code\":422,\"msg\":\"Identity is already linked to another user\"}");
            Assert.IsTrue(b.IdentityTaken, "message-only fallback");

            var c = AuthError.Parse(400, "{\"error\":\"invalid_grant\",\"error_description\":\"Bad ID token\"}");
            Assert.AreEqual("invalid_grant", c.Code);
            Assert.AreEqual("Bad ID token", c.Message);
            Assert.IsFalse(c.IdentityTaken);

            var d = AuthError.Parse(400, "{\"code\":400,\"error_code\":\"manual_linking_disabled\",\"msg\":\"Manual linking is disabled\"}");
            Assert.AreEqual("manual_linking_disabled", d.Code);
            Assert.IsFalse(d.IdentityTaken);

            Assert.AreEqual("http_502", AuthError.Parse(502, "<html>").Code);
        }

        [Test]
        public void LinkAnswerClassification()
        {
            long now = 1792490400L;
            var same = new AuthUserInfo { Id = "guest", IsAnonymous = false, CreatedAt = now - 86400 };
            Assert.AreEqual(LinkOutcome.Linked, LinkAnswer.Classify("guest", same, now));
            var fresh = new AuthUserInfo { Id = "other", IsAnonymous = false, CreatedAt = now - 3 };
            Assert.AreEqual(LinkOutcome.LinkUnsupported, LinkAnswer.Classify("guest", fresh, now), "new user made by the grant");
            var older = new AuthUserInfo { Id = "other", IsAnonymous = false, CreatedAt = now - 30 * 86400 };
            Assert.AreEqual(LinkOutcome.IdentityTaken, LinkAnswer.Classify("guest", older, now), "returning player");
            var unknownAge = new AuthUserInfo { Id = "other", CreatedAt = 0 };
            Assert.AreEqual(LinkOutcome.IdentityTaken, LinkAnswer.Classify("guest", unknownAge, now));
            Assert.AreEqual(LinkOutcome.Failed, LinkAnswer.Classify("guest", null, now));
        }

        // ------------------------------------------------------------------ KEEP YOUR BOUNTY once

        private sealed class MemFlags : IFlagStore
        {
            public readonly Dictionary<string, bool> Values = new Dictionary<string, bool>();
            public bool Get(string key) { bool v; return Values.TryGetValue(key, out v) && v; }
            public void Set(string key, bool value) { Values[key] = value; }
        }

        [Test]
        public void BountySheetIsOfferedOnceAfterTheFirstSoloMatch()
        {
            var store = new MemFlags();
            var gate = new BountyGate(store);
            Assert.IsFalse(gate.ShouldOffer(false, false, true), "no match finished yet");
            Assert.IsFalse(gate.ShouldOffer(true, true, true), "already linked");
            Assert.IsFalse(gate.ShouldOffer(true, false, false), "accounts not available (offline build)");
            Assert.IsTrue(gate.ShouldOffer(true, false, true));
            gate.MarkOffered();
            Assert.IsFalse(gate.ShouldOffer(true, false, true), "shown once (LATER counts)");
            Assert.IsTrue(store.Values[BountyGate.Key], "persisted under " + BountyGate.Key);

            var again = new BountyGate(store);   // a restart
            Assert.IsTrue(again.Offered);
            Assert.IsFalse(again.ShouldOffer(true, false, true));

            again.Reset();                        // account deleted
            Assert.IsTrue(new BountyGate(store).ShouldOffer(true, false, true));
        }
    }
}
