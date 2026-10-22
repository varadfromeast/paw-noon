using System.Globalization;
using PawNoon.Core;

namespace PawNoon.Runtime
{
    /// <summary>
    /// Review / automation hooks (screenshots, MCP, tests in Play mode). Every method takes zero or
    /// one string and returns a short status string. Never throws.
    /// </summary>
    public static partial class ReviewDirector
    {
        public static readonly string[] States = { "title", "countdown2", "draw", "roundresult", "win", "lose", "settings", "foul", "difficulty", "lastlight",
            "bounty", "profile", "account", "keypad", "delete", "switch",
            "pvp", "friend", "room", "join", "waiting", "leaderboard", "fastest", "shop", "haptics" };

        private static PawGame Game()
        {
            return PawGame.Instance;
        }

        private static string NotRunning() { return "error: game not running (enter Play mode)"; }

        /// <summary>Forces a deterministic still: title, countdown2, draw, roundresult, win, lose, settings, foul,
        /// difficulty (title with the first-launch EASY / HARD card; the stored pick is kept), haptics (the haptics
        /// lab: every haptic as a button, see Docs/HAPTICS.md).</summary>
        public static string Show(string state)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (state ?? "").Trim().ToLowerInvariant();
            if (s == "haptics" || s == "haptic" || s == "hapticslab") return HapticsLab.Open();
            HapticsLab.Close();
            try
            {
                switch (s)
                {
                    case "title":
                        g.ShowTitle();
                        break;
                    case "countdown2":
                    case "countdown":
                        g.EnterReviewDuel();
                        g.Director.ReviewCountdown(2);
                        break;
                    case "draw":
                        g.EnterReviewDuel();
                        g.Director.ReviewDraw(183245000L);
                        break;
                    case "roundresult":
                    case "result":
                        g.EnterReviewDuel();
                        g.Director.ReviewRoundResult();
                        break;
                    case "win":
                        g.ShowMatchResultSample(true);
                        break;
                    case "lose":
                        g.ShowMatchResultSample(false);
                        break;
                    case "settings":
                        g.ShowSettingsSample();
                        break;
                    case "foul":
                        g.EnterReviewDuel();
                        g.Director.ReviewFoul();
                        break;
                    case "difficulty":
                    case "choice":
                        g.ShowDifficultyChoice(false);
                        break;
                    case "lastlight":
                    case "suddendeath":
                        g.EnterReviewDuel();
                        g.Director.ReviewLastLight();
                        break;
                    // M6 accounts: KEEP YOUR BOUNTY over the title, the profile card, Settings > ACCOUNT, the name
                    // keypad, the first delete confirm, the "account already used" switch prompt
                    case "bounty":
                    case "profile":
                    case "account":
                    case "keypad":
                    case "delete":
                    case "switch":
                    {
                        string r = g.Accounts.ShowForReview(s);
                        if (r != "ok") return r;
                        break;
                    }
                    // M5 online screens, always on the mock (no server needed)
                    case "pvp":
                    case "friend":
                    case "room":
                    case "join":
                    case "waiting":
                    case "leaderboard":
                    case "fastest":
                    {
                        string r = g.Online.ReviewShow(s);
                        if (r != "ok") return r;
                        break;
                    }
                    case "shop":
                    case "store":
                        g.ShowTitle();
                        g.Shop.Open();
                        break;
                    default:
                        return "error: unknown state '" + state + "' (use " + string.Join(", ", States) + ")";
                }
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: showing " + s;
        }

        /// <summary>
        /// Plays one haptic of the lab by name (e.g. "drawcue", "heart70", "lightning", "shothit"; "list" names them all).
        /// On a device it is felt; in the editor it is logged ([Haptics] ...). Returns the backend in use.
        /// </summary>
        public static string Haptic(string name)
        {
            if (Game() == null) return NotRunning();
            string s = (name ?? "").Trim();
            if (s == "" || s.ToLowerInvariant() == "list") return "ok: " + HapticsLab.Names() + " backend=" + PawHaptics.Backend;
            try
            {
                return HapticsLab.Play(s);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// Sets the difficulty ("easy" / "hard"), stored like the title toggle (PlayerPrefs "pn_difficulty").
        /// Applies now on the title screen, otherwise from the next match. "reset" forgets the pick and shows
        /// the first-launch card on the title.
        /// </summary>
        public static string SetDifficulty(string difficulty)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (difficulty ?? "").Trim().ToLowerInvariant();
            try
            {
                if (s == "reset" || s == "clear")
                {
                    g.ShowDifficultyChoice(true);
                    return "ok: difficulty cleared, first-launch choice shown";
                }
                if (s != DuelConfig.DifficultyEasy && s != DuelConfig.DifficultyHard)
                    return "error: expected easy, hard or reset";
                g.SetDifficulty(s);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            bool now = g.Config.Difficulty == s;
            return "ok: difficulty " + s + (now ? " (active now)" : " (from the next match)") +
                   " active: botMeanStart=" + g.Config.botMeanStartMs.ToString("0", CultureInfo.InvariantCulture) +
                   " holdMax=" + g.Config.holdMaxSeconds.ToString("0.0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// "SetHp hero bandit" (one string, e.g. "20 20"): sets both HP during a running duel, between exchanges,
        /// to force LAST LIGHT (sudden death, both at or below suddenDeathHp). In the standoff it applies at once
        /// (the standoff restarts), on the result card from the next standoff.
        /// </summary>
        public static string SetHp(string heroAndBandit)
        {
            var g = Game();
            if (g == null) return NotRunning();
            var parts = (heroAndBandit ?? "").Trim().Split(new[] { ' ', ',', '/' }, System.StringSplitOptions.RemoveEmptyEntries);
            double h, b;
            if (parts.Length != 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out h) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                return "error: expected two numbers \"<hero> <bandit>\", e.g. \"20 20\"";
            try
            {
                return g.Director.ReviewSetHp(h, b);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
        }

        public static string StartDuel()
        {
            var g = Game();
            if (g == null) return NotRunning();
            MatchIntro.SkipNextForReview = true;   // automated runs skip the match intro unless SetIntro on
            g.StartDuel();
            return "ok: duel started (seed " + g.Director.Seed + ")";
        }

        /// <summary>"on" | "off": the SWIPE cue setting (it switches itself off after one taught match).</summary>
        public static string SwipeHint(string onOff)
        {
            bool on = (onOff ?? "").Trim().ToLowerInvariant() == "on";
            PawSettings.SwipeHint = on;
            PawSettings.Save();
            return "ok: swipe hint " + (on ? "on" : "off");
        }

        /// <summary>
        /// Match intro (SPEC_V2 §8.7) for review: "on" = play it at every StartDuel (review runs included, ignores the
        /// once-per-session rule), "off" = never, "auto" = default (first match of a session, not in review runs,
        /// cfg.introEnabled), "reset" = auto and forget that it was shown this session.
        /// </summary>
        public static string SetIntro(string mode)
        {
            string s = (mode ?? "").Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true") MatchIntro.ReviewMode = 1;
            else if (s == "off" || s == "0" || s == "false") MatchIntro.ReviewMode = -1;
            else if (s == "auto" || s == "") MatchIntro.ReviewMode = 0;
            else if (s == "reset") { MatchIntro.ReviewMode = 0; MatchIntro.ShownThisSession = false; }
            else return "error: expected on, off, auto or reset";
            return "ok: intro " + MatchIntro.StatusText;
        }

        /// <summary>Injects a press timestamped now through the real input queue and player client.</summary>
        public static string SimulateTap() { return SimulateSwipe(); }

        /// <summary>Injects a completed draw swipe now (same queue + report path as a real swipe).</summary>
        public static string SimulateSwipe()
        {
            var g = Game();
            if (g == null) return NotRunning();
            g.Input.Inject(g.Clock.NowNs, InputSource.Injected);
            return "ok: swipe injected (phase " + g.Director.Authority.Phase + ")";
        }

        /// <summary>Auto-tap N ms after each DRAW cue; "off" disables.</summary>
        public static string SetAutoTap(string ms)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (ms ?? "").Trim().ToLowerInvariant();
            if (s == "off" || s == "" || s == "-1")
            {
                g.Director.SetAutoTapMs(-1);
                return "ok: auto-tap off";
            }
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0)
                return "error: expected milliseconds or 'off'";
            g.Director.SetAutoTapMs(v);
            return "ok: auto-tap " + v.ToString("0.###", CultureInfo.InvariantCulture) + " ms after each cue";
        }

        /// <summary>Presentation time scale (animations/FX only; duel timing is unaffected).</summary>
        public static string SetTimeScale(string x)
        {
            double v;
            if (!double.TryParse((x ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0 || v > 10)
                return "error: expected a number 0..10";
            PawTime.ReviewScale = (float)v;
            return "ok: presentation time scale " + v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        public static string Status()
        {
            var g = Game();
            if (g == null) return NotRunning();
            return "screen=" + g.CurrentMode + " settings=" + g.SettingsOpenState + " difficulty=" + g.Config.Difficulty +
                   (g.Difficulty != g.Config.Difficulty ? "->" + g.Difficulty : "") + " " + g.Director.StatusLine() +
                   " fps=" + UnityEngine.Application.targetFrameRate + " virtual=" + g.Pipeline.VirtualW + "x" + g.Pipeline.VirtualH +
                   " preciseInput=" + g.Input.HasPreciseTimestamps +
                   " intro=" + MatchIntro.StatusText + (g.Director.Intro.Active ? "(playing)" : "") +
                   " tutorial=" + TutorialStatus() +
                   " haptics=" + (PawHaptics.Enabled ? "on" : "off") + (HapticsLab.IsOpen ? "(lab)" : "") +
                   " opponent=" + (g.Director.Persona != null ? g.Director.Persona.id : "cfg") +
                   " " + (g.Online != null ? g.Online.Status() : "online=none") +
                   " " + (g.Accounts != null ? g.Accounts.Status() : "account=none") +
                   " " + (g.Shop != null ? g.Shop.Status() : "gun=standard shop=none");
        }

        // ------------------------------------------------------------------ accounts (M6)

        /// <summary>
        /// Simulates the account side without Apple / Google / a server: "apple" | "google" = a successful link
        /// (same user id; through the open sheet's UI if one is up), "taken" = the next link finds that account
        /// already used (switch prompt), "cancel" | "fail" = the next native sheet is cancelled / fails, "reset" =
        /// unlinked guest again and KEEP YOUR BOUNTY re-armed.
        /// </summary>
        public static string MockLink(string what)
        {
            var g = Game();
            if (g == null) return NotRunning();
            try
            {
                return g.Accounts.MockLink((what ?? "").Trim().ToLowerInvariant());
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
        }

        // ------------------------------------------------------------------ shop / loadout (M7)

        /// <summary>
        /// Buys an item through the mock store right now ("peacemaker"): granted locally, never server-verified,
        /// so it works in solo only (PvP keeps the standard gun until purchase-validate confirms a real purchase).
        /// </summary>
        public static string MockBuy(string item)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string gun = Guns.Normalize(item);
            if (gun == Guns.Standard) return "error: unknown item '" + item + "' (use peacemaker)";
            g.Shop.MockBuy(gun);
            return "ok: " + g.Shop.Status();
        }

        /// <summary>Equips "peacemaker" or "standard" (must be owned); applies from the next match.</summary>
        public static string Equip(string gun)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (gun ?? "").Trim().ToLowerInvariant();
            if (s != Guns.Peacemaker && s != Guns.Standard) return "error: use peacemaker or standard";
            if (!g.Shop.Equip(s)) return "error: " + s + " is not owned (MockBuy peacemaker first)";
            return "ok: " + g.Shop.Status();
        }

        /// <summary>QA: forgets every gun and the mock store's sales (fresh install).</summary>
        public static string ResetGuns()
        {
            var g = Game();
            if (g == null) return NotRunning();
            g.Shop.ResetGuns();
            return "ok: " + g.Shop.Status();
        }

        // ------------------------------------------------------------------ online PvP (M4)

        /// <summary>
        /// Plays a recorded (ghost) PvP match right now on the given seed against the built-in fake recording
        /// (Marshal Mittens, see NetMock). No network: the verdict is the local recompute (what the server does).
        /// Combine with SetAutoTap for a hands-free run.
        /// </summary>
        public static string StartGhostDuel(string seed)
        {
            var g = Game();
            if (g == null) return NotRunning();
            int v;
            if (!int.TryParse((seed ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                v = Net.NetMock.RandomSeed();
            try
            {
                g.Online.StartGhostDuel(v);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: ghost duel started (seed " + v + ", rival Marshal Mittens, " + Net.NetMock.FakeRecording().Length + " recorded attempts)";
        }

        /// <summary>"on": the PVP button plays the built-in mock (fake match-start + local verdict). "off": net_config.json decides.</summary>
        public static string SetNetMock(string onOff)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (onOff ?? "").Trim().ToLowerInvariant();
            if (s != "on" && s != "off" && s != "true" && s != "false" && s != "1" && s != "0")
                return "error: expected on or off";
            bool on = s == "on" || s == "true" || s == "1";
            g.Online.SetMock(on);
            return "ok: net mock " + (on ? "on" : "off") + " (" + g.Online.Status() + ")";
        }

        /// <summary>
        /// FIND RIVAL from the title, skipping the PVP card: "casual" / "ranked", or empty for the card's stored pick.
        /// Uses the server unless mock is on. (Ranked against a server needs a linked account: SetLinked on is only
        /// the client's view; the server still answers 403 link_required for a guest unless PN_RANKED_ALLOW_GUESTS.)
        /// </summary>
        public static string StartPvp() { return StartPvp(null); }

        /// <summary>See StartPvp(): "casual" or "ranked".</summary>
        public static string StartPvp(string mode)
        {
            var g = Game();
            if (g == null) return NotRunning();
            try
            {
                g.ShowTitle();
                string m = (mode ?? "").Trim().ToLowerInvariant();
                g.Online.FindRival(m == "ranked" || m == "casual" ? m : g.Online.PvpMode);
            }
            catch (System.Exception e)
            {
                return "error: " + e.GetType().Name + ": " + e.Message;
            }
            return "ok: " + g.Online.Status();
        }

        /// <summary>
        /// Ranked toggle preview (SupabaseAuth.IsLinked seam until M6): "on" = treat the account as linked, "off" = guest,
        /// "auto" = read the session token.
        /// </summary>
        public static string SetLinked(string mode)
        {
            var g = Game();
            if (g == null) return NotRunning();
            string s = (mode ?? "").Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true") Net.SupabaseAuth.LinkedOverride = 1;
            else if (s == "off" || s == "0" || s == "false") Net.SupabaseAuth.LinkedOverride = -1;
            else if (s == "auto" || s == "") Net.SupabaseAuth.LinkedOverride = 0;
            else return "error: expected on, off or auto";
            return "ok: linked=" + (g.Online.Linked ? "yes" : "no") + " (" + (Net.SupabaseAuth.LinkedOverride == 0 ? "token" : "override") + ")";
        }

        /// <summary>Drops the stored guest session; the next PVP signs in as a new guest (so it can be matched against your previous recording).</summary>
        public static string NetSignOut()
        {
            var g = Game();
            if (g == null) return NotRunning();
            g.Online.SignOut();
            return "ok: signed out, next online call is a new guest";
        }

        /// <summary>The player's last PvP recording as JSON (what match-submit sent).</summary>
        public static string LastRecording()
        {
            var g = Game();
            if (g == null) return NotRunning();
            return g.Online.LastRecordingJson();
        }

        /// <summary>Seed for the next match (authority RNG and bot RNG derive from it).</summary>
        public static string Seed(string n)
        {
            var g = Game();
            if (g == null) return NotRunning();
            int v;
            if (!int.TryParse((n ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                return "error: expected an integer";
            g.Director.SetSeed(v);
            return "ok: seed " + v + " for the next match";
        }
    }
}
