using System.Collections.Generic;

namespace PawNoon.Core
{
    /// <summary>One attempt of the local player's recording (SPEC_V2 §4.2 round). Times on the phone's clock.</summary>
    public struct RecordedAttempt
    {
        public int roundId;
        public int roundNumber;
        public AttemptKind kind;
        public long reactionNs;
        public long cuePresentedNs;
        public long inputNs;
        /// <summary>When "3" appeared (DuelClient.CountdownStartNs); -1 if unknown (omitted from the JSON).</summary>
        public long countdownStartNs;
        public InputSource inputSource;
        public bool frameQuantized;
        public bool hasReport;
    }

    /// <summary>
    /// Builds the player's recording from the real ShotReports the player's DuelClient sent (observed on a
    /// <see cref="TapTransport"/>), one entry per countdown attempt in play order (void replays included,
    /// repeating roundNumber), plus countdownStartNs per attempt. An attempt without a report is a no-shot.
    /// Nothing from presentation is read. Serialises to recording.schema.json / match-submit.request.
    /// </summary>
    public sealed class RecordingBuilder : IDuelTap
    {
        public const int Version = 1;

        private readonly int slot;
        private readonly DuelConfig cfg;
        private DuelClient player;
        private readonly List<RecordedAttempt> attempts = new List<RecordedAttempt>(16);
        private RecordedAttempt cur;
        private bool open;

        public bool Recording { get; private set; }

        // metadata (recording.schema.json)
        public int Seed;
        public string Difficulty = DuelConfig.DifficultyHard;
        public string Character = "whiskers";
        public string Gun = "standard";
        public string DeviceModel = "unknown";
        public string DeviceOs = "unknown";
        public double RefreshHz = 60;
        public string ClientBuild = "0.0.0";

        public RecordingBuilder(int slot, DuelConfig cfg)
        {
            this.slot = slot;
            this.cfg = cfg ?? new DuelConfig();
        }

        public int Count { get { return attempts.Count; } }
        public RecordedAttempt this[int i] { get { return attempts[i]; } }

        /// <summary>Starts a fresh recording for a match on <paramref name="seed"/>; <paramref name="playerClient"/> supplies countdownStartNs and the cue time of a no-shot.</summary>
        public void Begin(int seed, DuelClient playerClient)
        {
            Seed = seed;
            player = playerClient;
            attempts.Clear();
            open = false;
            Recording = true;
        }

        /// <summary>Closes the last attempt and stops listening.</summary>
        public void Finish()
        {
            if (!Recording) return;
            Close();
            Recording = false;
        }

        public void Cancel()
        {
            Recording = false;
            open = false;
        }

        public void OnRoundStart(RoundStartMsg msg)
        {
            if (!Recording) return;
            // broadcast before the clients poll it: the player client still holds the previous attempt
            Close();
            cur = new RecordedAttempt();
            cur.roundId = msg.roundId;
            cur.roundNumber = msg.roundNumber;
            cur.kind = AttemptKind.NoShot;
            cur.countdownStartNs = -1;
            open = true;
        }

        public void OnReport(ShotReport r)
        {
            if (!Recording || !open || r.slot != slot || r.roundId != cur.roundId || cur.hasReport) return;
            cur.hasReport = true;
            cur.reactionNs = r.reactionNs;
            cur.cuePresentedNs = r.cuePresentedNs;
            cur.inputNs = r.inputNs;
            cur.inputSource = r.inputSource;
            cur.frameQuantized = r.frameQuantized;
            cur.kind = Classify(cfg, r);
            Capture();
        }

        /// <summary>shot / anticipation (reaction under anticipationMs) / early, as the authority will judge it.</summary>
        public static AttemptKind Classify(DuelConfig cfg, ShotReport r)
        {
            if (r.kind == ReportKind.Early) return AttemptKind.Early;
            return r.reactionNs < cfg.AnticipationNs ? AttemptKind.Anticipation : AttemptKind.Shot;
        }

        private void Capture()
        {
            if (player == null || player.RoundId != cur.roundId) return;
            cur.countdownStartNs = player.CountdownStartNs;
            if (!cur.hasReport && player.HasCue) cur.cuePresentedNs = player.CuePresentedNs;
        }

        private void Close()
        {
            if (!open) return;
            Capture();
            if (!cur.hasReport)
            {
                cur.kind = AttemptKind.NoShot;
                cur.reactionNs = 0;
                cur.inputNs = 0;
                cur.inputSource = InputSource.Unknown;
                cur.frameQuantized = false;
            }
            attempts.Add(cur);
            open = false;
        }

        /// <summary>Test / replay helper: appends a finished attempt directly.</summary>
        public void Add(RecordedAttempt a) { attempts.Add(a); }

        /// <summary>What an opponent would replay from this recording (server playableAttempts()).</summary>
        public GhostAttempt[] ToGhostAttempts()
        {
            var r = new GhostAttempt[attempts.Count];
            for (int i = 0; i < r.Length; i++)
            {
                var a = attempts[i];
                var g = new GhostAttempt();
                g.roundNumber = a.roundNumber;
                g.kind = a.kind;
                g.reactionNs = (a.kind == AttemptKind.Shot || a.kind == AttemptKind.Anticipation) ? a.reactionNs : 0;
                g.inputOffsetNs = (a.kind == AttemptKind.Early && a.countdownStartNs >= 0) ? a.inputNs - a.countdownStartNs : -1;
                r[i] = g;
            }
            return r;
        }

        public static string InputSourceWire(InputSource s)
        {
            switch (s)
            {
                case InputSource.Touch: return "touch";
                case InputSource.Mouse: return "mouse";
                case InputSource.Keyboard: return "keyboard";
                case InputSource.Bot: return "bot";
                case InputSource.Injected: return "injected";
                case InputSource.Remote: return "remote";
                default: return "unknown";
            }
        }

        /// <summary>The recording object (recording.schema.json). <paramref name="submittedAtIso"/>: ISO-8601 UTC.</summary>
        public void Write(JsonOut j, string submittedAtIso)
        {
            j.BeginObject();
            j.Prop("version", Version);
            j.Prop("seed", Seed);
            j.Prop("difficulty", Difficulty);
            j.Prop("character", Character);
            j.Key("loadout").BeginObject().Prop("gun", Gun).EndObject();
            j.Key("rounds").BeginArray();
            for (int i = 0; i < attempts.Count; i++)
            {
                var a = attempts[i];
                j.BeginObject();
                j.Prop("roundNumber", a.roundNumber);
                j.Prop("kind", AttemptKinds.ToWire(a.kind));
                j.Prop("reactionNs", a.reactionNs);
                j.Prop("cuePresentedNs", a.cuePresentedNs);
                j.Prop("inputNs", a.inputNs);
                if (a.countdownStartNs >= 0) j.Prop("countdownStartNs", a.countdownStartNs);
                j.Prop("inputSource", InputSourceWire(a.inputSource));
                j.Prop("frameQuantized", a.frameQuantized);
                j.EndObject();
            }
            j.EndArray();
            j.Key("device").BeginObject()
                .Prop("model", Clip(DeviceModel, 128))
                .Prop("os", Clip(DeviceOs, 128))
                .Prop("refreshHz", RefreshHz)
                .EndObject();
            j.Prop("clientBuild", Clip(ClientBuild, 64));
            j.Prop("submittedAt", submittedAtIso);
            j.EndObject();
        }

        public string ToJson(string submittedAtIso)
        {
            var j = new JsonOut(256 + attempts.Count * 200);
            Write(j, submittedAtIso);
            return j.ToString();
        }

        /// <summary>The full match-submit request body (match-submit.request.schema.json).</summary>
        public string SubmitBody(string matchId, string submittedAtIso)
        {
            var j = new JsonOut(300 + attempts.Count * 200);
            j.BeginObject();
            j.Prop("matchId", matchId);
            j.Key("recording");
            Write(j, submittedAtIso);
            j.EndObject();
            return j.ToString();
        }

        private static string Clip(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            return s.Length <= max ? s : s.Substring(0, max);
        }
    }
}
