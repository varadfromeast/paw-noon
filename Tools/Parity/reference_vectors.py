#!/usr/bin/env python3
"""
Paw Noon parity vectors: independent Python reference.

This is a line-by-line port of the C# rules in Assets/PawNoon/Code/Core (System.Random as used by
Unity/Mono and .NET, RoundSchedule, DamageCurve, Grading, MatchState, LocalAuthority) written
WITHOUT looking at the TypeScript port, so the committed Tools/Parity/vectors.json is cross-checked
by two independent implementations before Unity ever runs.

The source of truth is the C# EditMode test ParityVectorsTests.cs, which regenerates vectors.json in
Unity (same case list, same writer layout). This script exists so vectors can be produced without
Unity; if the two ever disagree, the C# output wins and this script has a bug.

Usage:  python3 Tools/Parity/reference_vectors.py            (writes Tools/Parity/vectors.json)
        python3 Tools/Parity/reference_vectors.py --check    (exit 1 if the file would change)
"""
import json
import math
import os
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
CONFIG_PATH = os.path.join(ROOT, "Assets", "PawNoon", "Resources", "Data", "duel_config.json")
OUT_PATH = os.path.join(ROOT, "Tools", "Parity", "vectors.json")

# --------------------------------------------------------------------------- int32 / System.Random

MBIG = 2147483647
MSEED = 161803398


def i32(v):
    v &= 0xFFFFFFFF
    return v - (1 << 32) if v & 0x80000000 else v


class NetRandom:
    """System.Random(int Seed): Knuth subtractive generator (reference source, Mono, .NET compat impl)."""

    def __init__(self, seed):
        sa = [0] * 56
        sub = MBIG if seed == -2147483648 else abs(seed)
        mj = i32(MSEED - sub)
        sa[55] = mj
        mk = 1
        for i in range(1, 55):
            ii = (21 * i) % 55
            sa[ii] = mk
            mk = i32(mj - mk)
            if mk < 0:
                mk = i32(mk + MBIG)
            mj = sa[ii]
        for _k in range(1, 5):
            for i in range(1, 56):
                sa[i] = i32(sa[i] - sa[1 + (i + 30) % 55])
                if sa[i] < 0:
                    sa[i] = i32(sa[i] + MBIG)
        self.sa = sa
        self.inext = 0
        self.inextp = 21  # sic: the famous 21, not 31

    def internal_sample(self):
        a = self.inext + 1
        if a >= 56:
            a = 1
        b = self.inextp + 1
        if b >= 56:
            b = 1
        r = i32(self.sa[a] - self.sa[b])
        if r == MBIG:
            r -= 1
        if r < 0:
            r = i32(r + MBIG)
        self.sa[a] = r
        self.inext = a
        self.inextp = b
        return r

    def next(self):
        return self.internal_sample()

    def next_double(self):
        return self.internal_sample() * (1.0 / MBIG)


# --------------------------------------------------------------------------- config (core subset)

def cs_round(x):
    """C# Math.Round(double): banker's rounding. Python round() on a float is half-to-even too."""
    return int(round(x))


def sec_to_ns(s):
    return cs_round(s * 1e9)


def ms_to_ns(ms):
    return cs_round(ms * 1e6)


def ns_to_ms(ns):
    return ns / 1e6


class Cfg:
    pass


def load_config(path=CONFIG_PATH):
    with open(path, "r", encoding="utf-8") as f:
        root = json.load(f)
    c = Cfg()
    d = dict(countdownBeats=3, beatSeconds=0.70, holdMinSeconds=1.0, holdMaxSeconds=4.0,
             noShotTimeoutSeconds=1.5, commitGraceMs=25.0, anticipationMs=100.0, tieWindowMs=15.0,
             reportSlackMs=50.0, maxHp=100.0,
             gradeLightningMs=200.0, gradePerfectMs=250.0, gradeGreatMs=320.0, gradeGoodMs=420.0,
             gradeSlowMs=700.0)
    for k, v in d.items():
        val = root.get(k, v)
        setattr(c, k, float(val))
    c.countdownBeats = cs_round(float(root.get("countdownBeats", 3)))
    dm, dv = root.get("damageMs"), root.get("damageValue")
    if isinstance(dm, list) and isinstance(dv, list) and len(dm) == len(dv) and len(dm) >= 2:
        c.damageMs = [float(x) for x in dm]
        c.damageValue = [float(x) for x in dv]
    else:
        c.damageMs = [150.0, 200.0, 250.0, 300.0, 400.0, 600.0, 1000.0, 1500.0]
        c.damageValue = [40.0, 36.0, 30.0, 24.0, 16.0, 8.0, 4.0, 2.0]
    # Sanitize (core subset)
    c.countdownBeats = max(1, c.countdownBeats)
    c.beatSeconds = max(0.05, c.beatSeconds)
    c.holdMinSeconds = max(0.0, c.holdMinSeconds)
    c.holdMaxSeconds = max(c.holdMinSeconds, c.holdMaxSeconds)
    c.noShotTimeoutSeconds = max(0.2, c.noShotTimeoutSeconds)
    c.commitGraceMs = max(0.0, c.commitGraceMs)
    c.anticipationMs = max(0.0, c.anticipationMs)
    c.tieWindowMs = max(0.0, c.tieWindowMs)
    c.reportSlackMs = max(0.0, c.reportSlackMs)
    c.maxHp = max(1.0, c.maxHp)
    c.beatNs = sec_to_ns(c.beatSeconds)
    c.noShotNs = sec_to_ns(c.noShotTimeoutSeconds)
    c.graceNs = ms_to_ns(c.commitGraceMs)
    c.anticipationNs = ms_to_ns(c.anticipationMs)
    c.tieNs = ms_to_ns(c.tieWindowMs)
    c.slackNs = ms_to_ns(c.reportSlackMs)
    return c


# --------------------------------------------------------------------------- schedule, damage, grade

class Schedule:
    def __init__(self, beats, beat_ns, hold_ns):
        self.beats = beats
        self.beat_ns = beat_ns
        self.hold_ns = hold_ns
        self.countdown_ns = beats * beat_ns
        self.cue_offset_ns = self.countdown_ns + hold_ns


def create_schedule(cfg, rng):
    h = cfg.holdMinSeconds + rng.next_double() * (cfg.holdMaxSeconds - cfg.holdMinSeconds)
    return Schedule(cfg.countdownBeats, cfg.beatNs, sec_to_ns(h))


def damage(cfg, reaction_ms):
    xs, ys = cfg.damageMs, cfg.damageValue
    n = min(len(xs), len(ys))
    if n == 0:
        return 0.0
    if math.isnan(reaction_ms) or reaction_ms < 0:
        return 0.0
    if reaction_ms <= xs[0]:
        return ys[0]
    if reaction_ms > xs[n - 1]:
        return 0.0
    for k in range(1, n):
        if reaction_ms <= xs[k]:
            span = xs[k] - xs[k - 1]
            t = 1.0 if span <= 0 else (reaction_ms - xs[k - 1]) / span
            return ys[k - 1] + (ys[k] - ys[k - 1]) * t
    return ys[n - 1]


def grade(cfg, ms):
    if ms <= cfg.gradeLightningMs:
        return "Lightning"
    if ms <= cfg.gradePerfectMs:
        return "Perfect"
    if ms <= cfg.gradeGreatMs:
        return "Great"
    if ms <= cfg.gradeGoodMs:
        return "Good"
    if ms <= cfg.gradeSlowMs:
        return "Slow"
    return "Slowpoke"


# --------------------------------------------------------------------------- MatchState + LocalAuthority

class Match:
    def __init__(self, max_hp):
        self.reset(max_hp)

    def reset(self, max_hp):
        self.max_hp = max_hp
        self.hp = [max_hp, max_hp]
        self.round_wins = [0, 0]
        self.round_number = 1
        self.over = False
        self.winner = -1

    def finish(self, w):
        self.over = True
        self.winner = w if 0 <= w < 2 else -1


class SlotRec:
    def __init__(self):
        self.outcome = "Pending"
        self.has_report = False
        self.report = None
        self.reaction = -1
        self.dmg = 0.0
        self.grade = "None"

    @property
    def settled(self):
        return self.outcome != "Pending"

    @property
    def is_foul(self):
        return self.outcome in ("Foul", "Anticipation")


class Report:
    def __init__(self, round_id, slot, kind, reaction, cue_presented, input_ns):
        self.round_id = round_id
        self.slot = slot
        self.kind = kind  # "Shot" | "Early"
        self.reaction = reaction
        self.cue_presented = cue_presented
        self.input = input_ns


class Authority:
    TOL = 1000

    def __init__(self, cfg, seed):
        self.cfg = cfg
        self.rng = NetRandom(seed)
        self.match = Match(cfg.maxHp)
        self.phase = "Standoff"
        self.next_round_id = 1
        self.clear_round()

    def clear_round(self):
        self.round_id = 0
        self.round_number = 0
        self.schedule = None
        self.countdown_start = 0
        self.cue_decided = -1
        self.cue_sent = False
        self.is_void = False
        self.is_tie = False
        self.winner_slot = -1
        self.slots = [SlotRec(), SlotRec()]

    def start_match(self):
        self.match.reset(self.cfg.maxHp)
        self.clear_round()
        self.phase = "Standoff"

    def start_countdown(self, now):
        if self.phase != "Standoff" or self.match.over:
            return False
        self.clear_round()
        self.round_id = self.next_round_id
        self.next_round_id += 1
        self.round_number = self.match.round_number
        self.schedule = create_schedule(self.cfg, self.rng)
        self.countdown_start = now
        self.phase = "Countdown"
        return True

    def next_round(self):
        if self.phase != "Result" or self.match.over:
            return False
        self.match.round_number += 1
        self.phase = "Standoff"
        return True

    def tick(self, now, reports):
        if self.phase == "Countdown" and now - self.countdown_start >= self.schedule.countdown_ns:
            self.phase = "Hold"
        if self.phase == "Hold" and now - self.countdown_start >= self.schedule.cue_offset_ns:
            self.cue_sent = True
            self.cue_decided = now
            self.phase = "Draw"
        for r in reports:
            self.submit(r, now)
        if self.phase in ("Draw", "Resolving"):
            self.commit(now)
            self.timeouts(now)
            self.commit(now)
            if self.slots[0].settled and self.slots[1].settled:
                self.resolve()

    def validate(self, r, now):
        c = self.cfg
        if r.slot < 0 or r.slot >= 2:
            return "BadSlot"
        if r.round_id < self.round_id:
            return "Stale"
        if r.round_id != self.round_id:
            return "WrongRound"
        if self.phase in ("Standoff", "Result") or self.is_void:
            return "Stale"
        s = self.slots[r.slot]
        if s.has_report:
            return "Duplicate"
        if s.settled:
            return "Stale"
        if r.kind == "Early":
            if r.cue_presented != 0 and r.input >= r.cue_presented:
                return "Inconsistent"
            return "Accepted"
        if r.kind != "Shot":
            return "Implausible"
        if not self.cue_sent:
            return "Implausible"
        if r.reaction < 0:
            return "Implausible"
        if r.reaction > c.noShotNs + c.slackNs:
            return "Implausible"
        diff = (r.input - r.cue_presented) - r.reaction
        if diff > self.TOL or diff < -self.TOL:
            return "Inconsistent"
        if (now - self.cue_decided) + c.slackNs < r.reaction:
            return "Implausible"
        return "Accepted"

    def submit(self, r, now):
        v = self.validate(r, now)
        if v != "Accepted":
            return v
        s = self.slots[r.slot]
        s.has_report = True
        s.report = r
        if r.kind == "Early":
            s.outcome = "Foul"
            self.check_both_foul()
            return v
        s.reaction = r.reaction
        if r.reaction < self.cfg.anticipationNs:
            s.outcome = "Anticipation"
            self.check_both_foul()
        return v

    def check_both_foul(self):
        if self.slots[0].is_foul and self.slots[1].is_foul:
            self.is_void = True
            self.phase = "Standoff"

    @staticmethod
    def pending_shot(s):
        return s.has_report and s.outcome == "Pending" and s.report.kind == "Shot"

    def commit(self, now):
        if self.phase not in ("Draw", "Resolving"):
            return
        for _guard in range(4):
            s0, s1 = self.slots
            p0, p1 = self.pending_shot(s0), self.pending_shot(s1)
            if not p0 and not p1:
                return
            if p0 and p1:
                a = 0 if s0.reaction <= s1.reaction else 1
            else:
                a = 0 if p0 else 1
            b = 1 - a
            sa, sb = self.slots[a], self.slots[b]
            if self.pending_shot(sb):
                if sb.reaction - sa.reaction <= self.cfg.tieNs:
                    self.fire_simultaneous()
                    continue
                self.fire(a, False)
                if self.match.hp[b] <= 0:
                    self.stop(b)
                continue
            if sb.settled:
                self.fire(a, False)
                continue
            if now - self.cue_decided >= sa.reaction + self.cfg.tieNs + self.cfg.graceNs:
                self.fire(a, False)
                if self.match.hp[b] <= 0:
                    self.stop(b)
                continue
            return

    def damage_for(self, reaction_ns):
        return damage(self.cfg, ns_to_ms(reaction_ns))

    def fire(self, slot, simultaneous):
        s = self.slots[slot]
        target = 1 - slot
        d = self.damage_for(s.reaction)
        self.match.hp[target] = max(0.0, self.match.hp[target] - d)
        self.commit_shot(slot, s, d)

    def fire_simultaneous(self):
        s0, s1 = self.slots
        d0 = self.damage_for(s0.reaction)
        d1 = self.damage_for(s1.reaction)
        self.is_tie = True
        self.match.hp[1] = max(0.0, self.match.hp[1] - d0)
        self.match.hp[0] = max(0.0, self.match.hp[0] - d1)
        self.commit_shot(0, s0, d0)
        self.commit_shot(1, s1, d1)

    def commit_shot(self, slot, s, d):
        s.outcome = "Shot"
        s.dmg = d
        s.grade = grade(self.cfg, ns_to_ms(s.reaction))
        self.phase = "Resolving"

    def stop(self, slot):
        s = self.slots[slot]
        if s.settled:
            return
        s.outcome = "Stopped"

    def timeouts(self, now):
        if not self.cue_sent:
            return
        limit = self.cfg.noShotNs + self.cfg.slackNs
        for i in range(2):
            s = self.slots[i]
            if not s.settled and not s.has_report and now - self.cue_decided > limit:
                s.outcome = "NoShot"
                self.phase = "Resolving"

    def resolve(self):
        d0, d1 = self.slots[0].dmg, self.slots[1].dmg
        if self.is_tie:
            self.winner_slot = -1
        else:
            self.winner_slot = 0 if d0 > d1 else (1 if d1 > d0 else -1)
        if self.winner_slot >= 0:
            self.match.round_wins[self.winner_slot] += 1
        self.phase = "Result"
        hp = self.match.hp
        if hp[0] <= 0 or hp[1] <= 0:
            self.match.finish(0 if hp[0] > 0 else (1 if hp[1] > 0 else -1))


# --------------------------------------------------------------------------- harness (mirrors ParityVectorsTests.cs)

T0 = 10_000_000_000
AFTER_CUE_NS = 2_000_000_000   # every report is delivered 2 s after the cue (past timeout + slack)
BETWEEN_NS = 1_000_000_000
EARLY_AFTER_START_NS = 1_000_000_000


def make_report(auth, slot, spec, cue, start):
    kind = spec["kind"]
    if kind == "noshot":
        return None
    if kind == "early":
        return Report(auth.round_id, slot, "Early", 0, 0, start + EARLY_AFTER_START_NS)
    r = spec["reactionNs"]
    return Report(auth.round_id, slot, "Shot", r, cue, cue + r)


def run_exchange(auth, now, s0, s1):
    """StartCountdown at now, tick at the scheduled cue, deliver both reports 2 s later. Returns new now."""
    assert auth.start_countdown(now)
    start = now
    now = start + auth.schedule.cue_offset_ns
    auth.tick(now, [])
    assert auth.phase == "Draw"
    cue = auth.cue_decided
    now = cue + AFTER_CUE_NS
    reps = [x for x in (make_report(auth, 0, s0, cue, start), make_report(auth, 1, s1, cue, start)) if x]
    auth.tick(now, reps)
    return now


def round_view(auth):
    return {
        "roundNumber": auth.round_number,
        "holdNs": auth.schedule.hold_ns,
        "cueOffsetNs": auth.schedule.cue_offset_ns,
        "void": auth.is_void,
        "tie": auth.is_tie,
        "winner": auth.winner_slot,
        "outcomes": [auth.slots[0].outcome, auth.slots[1].outcome],
        "damage": [auth.slots[0].dmg, auth.slots[1].dmg],
        "grades": [auth.slots[0].grade, auth.slots[1].grade],
        "hp": [auth.match.hp[0], auth.match.hp[1]],
        "matchOver": auth.match.over,
        "matchWinner": auth.match.winner,
    }


def play_round_case(cfg, case):
    auth = Authority(cfg, 1)
    auth.start_match()
    auth.match.hp[0], auth.match.hp[1] = float(case["hp"][0]), float(case["hp"][1])
    run_exchange(auth, T0, case["slots"][0], case["slots"][1])
    v = round_view(auth)
    del v["roundNumber"], v["holdNs"], v["cueOffsetNs"]
    return v


def play_match_case(cfg, case):
    seed, a, b = case["seed"], case["a"], case["b"]
    rounds, max_attempts = case["rounds"], case["maxAttempts"]
    auth = Authority(cfg, seed)
    auth.start_match()
    now = T0
    attempts = []
    resolved = 0
    k = 0
    while True:
        if auth.match.over:
            end = "ko" if auth.match.winner >= 0 else "double_ko"
            break
        if resolved >= rounds:
            end = "rounds"
            break
        if k >= max_attempts:
            end = "max_attempts"
            break
        if k >= len(b):
            end = "opponent_exhausted"
            break
        if k >= len(a):
            end = "player_exhausted"
            break
        now = run_exchange(auth, now, a[k], b[k])
        attempts.append(round_view(auth))
        if not auth.is_void:
            resolved += 1
        k += 1
        now += BETWEEN_NS
        if auth.phase == "Result" and not auth.match.over:
            auth.next_round()
    hp = auth.match.hp
    if auth.match.over:
        winner = auth.match.winner
    else:
        winner = 0 if hp[0] > hp[1] else (1 if hp[1] > hp[0] else -1)
    return {
        "attempts": attempts,
        "attemptsUsed": k,
        "resolvedRounds": resolved,
        "endReason": end,
        "winner": winner,
        "hp": [hp[0], hp[1]],
        "roundWins": [auth.match.round_wins[0], auth.match.round_wins[1]],
    }


# --------------------------------------------------------------------------- case list (keep identical to the C#)

SEEDS = [0, 1, 2, 3, 7, 42, 99, 1234, 31337, 161803398, 161803399, 987654321, 2147483647, -1, -42, -2147483648]
HOLDS_PER_SEED = 8
NEXT_PER_SEED = 5

DAMAGE_MS = [-1.0, 0.0, 99.9, 100.0, 149.999, 150.0, 175.0, 200.0, 200.001, 225.0, 250.0, 263.5, 300.0,
             333.333333, 400.0, 420.0, 500.0, 600.0, 700.0, 700.5, 999.0, 1000.0, 1250.0, 1500.0, 1500.001, 2000.0]


def S(ms):
    return {"kind": "shot", "reactionNs": ms_to_ns(ms)}


def SN(ns):
    return {"kind": "shot", "reactionNs": ns}


EARLY = {"kind": "early", "reactionNs": 0}
NOSHOT = {"kind": "noshot", "reactionNs": 0}

ROUND_CASES = [
    ("p0_faster", [100, 100], [S(200), S(290)]),
    ("p1_faster", [100, 100], [S(310), S(240)]),
    ("tie_exact_window", [100, 100], [S(250), S(265)]),
    ("tie_reverse_order", [100, 100], [S(265), S(250)]),
    ("just_outside_tie", [100, 100], [SN(250_000_000), SN(265_000_001)]),
    ("tie_double_ko", [5, 5], [S(250), S(250.5)]),
    ("tie_one_ko", [5, 50], [S(250), S(260)]),
    ("kill_stops_second", [100, 10], [S(180), S(240)]),
    ("kill_stops_second_slot0", [5, 100], [S(290), S(250)]),
    ("same_damage_no_tie", [100, 100], [S(120), S(140)]),
    ("anticipation_vs_shot", [100, 100], [S(90), S(300)]),
    ("anticipation_boundary", [100, 100], [SN(99_999_999), SN(100_000_000)]),
    ("both_100ms_tie", [100, 100], [S(100), S(100)]),
    ("anticipation_vs_early_void", [100, 100], [S(50), EARLY]),
    ("early_vs_early_void", [100, 100], [EARLY, EARLY]),
    ("anticipation_both_void", [100, 100], [S(20), S(99)]),
    ("early_vs_shot", [100, 100], [EARLY, S(420)]),
    ("early_vs_noshot", [100, 100], [EARLY, NOSHOT]),
    ("noshot_both", [100, 100], [NOSHOT, NOSHOT]),
    ("shot_vs_noshot", [100, 100], [S(333.333333), NOSHOT]),
    ("shot_1500_vs_noshot", [100, 100], [S(1500), NOSHOT]),
    ("shot_over_curve_vs_noshot", [100, 100], [SN(1_500_000_001), NOSHOT]),
    ("shot_in_slack_vs_shot", [100, 100], [S(1520), S(700)]),
    ("shot_implausible_becomes_noshot", [100, 100], [S(1600), S(700)]),
    ("fractional", [100, 100], [SN(263_456_789), SN(333_333_333)]),
    ("low_hp_noshot_kill", [100, 16], [S(400), NOSHOT]),
    ("both_low_slower_kills_after_survive", [30, 30], [S(260), S(500)]),
]

MATCH_CASES = [
    ("three_clean_rounds", 1, [S(210), S(250), S(230)], [S(300), S(240), S(260)]),
    ("void_replays_round", 7, [EARLY, S(200), S(220), S(210)], [EARLY, S(300), S(330), S(260)]),
    ("ko_in_round_three", 42, [S(140), S(140), S(140)], [S(300), S(300), S(300)]),
    ("double_ko_draw", 3, [S(140), S(140), S(140)], [S(145), S(140), S(150)]),
    ("opponent_exhausted", 1234, [EARLY, S(200), S(200)], [EARLY, S(250)]),
    ("hp_equal_draw", 99, [S(200), S(300), NOSHOT], [S(300), S(200), NOSHOT]),
    ("mixed_fouls", 31337, [S(80), EARLY, NOSHOT, S(450)], [S(260), S(500), S(1200), S(380)]),
    ("three_ties_hp_decides", 987654321, [S(231), S(265), S(244)], [S(230), S(266), S(250)]),
    ("ghost_wins", 5, [S(300), S(280), S(260)], [S(240), S(250), S(230)]),
    ("max_attempts", 2, [EARLY, EARLY, EARLY, EARLY, EARLY], [EARLY, EARLY, EARLY, EARLY, EARLY], 4),
]
MATCH_ROUNDS = 3
MATCH_MAX_ATTEMPTS = 9  # rounds * 3, same as the server default


def build(cfg):
    out = {}
    out["meta"] = [{
        "version": 1,
        "source": "Assets/PawNoon/Resources/Data/duel_config.json",
        "generator": "ParityVectorsTests.cs (Unity) / Tools/Parity/reference_vectors.py",
        "tieWindowMs": cfg.tieWindowMs, "anticipationMs": cfg.anticipationMs,
        "noShotTimeoutSeconds": cfg.noShotTimeoutSeconds, "reportSlackMs": cfg.reportSlackMs,
        "commitGraceMs": cfg.commitGraceMs, "maxHp": cfg.maxHp,
        "holdMinSeconds": cfg.holdMinSeconds, "holdMaxSeconds": cfg.holdMaxSeconds,
        "countdownBeats": cfg.countdownBeats, "beatNs": cfg.beatNs,
    }]
    sch = []
    for seed in SEEDS:
        r = NetRandom(seed)
        nxt = [r.next() for _ in range(NEXT_PER_SEED)]
        r = NetRandom(seed)
        holds, offs = [], []
        for _ in range(HOLDS_PER_SEED):
            s = create_schedule(cfg, r)
            holds.append(s.hold_ns)
            offs.append(s.cue_offset_ns)
        sch.append({"seed": seed, "next": nxt, "holdNs": holds, "cueOffsetNs": offs})
    out["schedules"] = sch
    out["damage"] = [{"ms": ms, "damage": damage(cfg, ms), "grade": grade(cfg, ms)} for ms in DAMAGE_MS]
    rc = []
    for name, hp, slots in ROUND_CASES:
        case = {"name": name, "hp": [float(hp[0]), float(hp[1])], "slots": slots}
        case["expect"] = play_round_case(cfg, case)
        rc.append(case)
    out["rounds"] = rc
    mc = []
    for mcase in MATCH_CASES:
        name, seed, a, b = mcase[:4]
        max_attempts = mcase[4] if len(mcase) > 4 else MATCH_MAX_ATTEMPTS
        case = {"name": name, "seed": seed, "rounds": MATCH_ROUNDS, "maxAttempts": max_attempts, "a": a, "b": b}
        case["expect"] = play_match_case(cfg, case)
        mc.append(case)
    out["matches"] = mc
    return out


# --------------------------------------------------------------------------- writer (same layout as the C# writer)

def fmt(v):
    if isinstance(v, bool):
        return "true" if v else "false"
    if isinstance(v, int):
        return str(v)
    if isinstance(v, float):
        if v == int(v) and abs(v) < 1e15:
            return str(int(v))
        return repr(v)
    if isinstance(v, str):
        return json.dumps(v)
    if isinstance(v, list):
        return "[" + ",".join(fmt(x) for x in v) + "]"
    if isinstance(v, dict):
        return "{" + ",".join(json.dumps(k) + ":" + fmt(x) for k, x in v.items()) + "}"
    raise TypeError(type(v))


SECTION_ORDER = ["meta", "schedules", "damage", "rounds", "matches"]


def write(data):
    parts = []
    for key in SECTION_ORDER:
        items = ",\n".join("    " + fmt(x) for x in data[key])
        parts.append("  " + json.dumps(key) + ": [\n" + items + "\n  ]")
    return "{\n" + ",\n".join(parts) + "\n}\n"


def main():
    cfg = load_config()
    text = write(build(cfg))
    json.loads(text)  # sanity
    if "--check" in sys.argv:
        with open(OUT_PATH, "r", encoding="utf-8") as f:
            ok = f.read() == text
        print("vectors.json up to date" if ok else "vectors.json differs from the reference")
        sys.exit(0 if ok else 1)
    with open(OUT_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", OUT_PATH)


if __name__ == "__main__":
    main()
