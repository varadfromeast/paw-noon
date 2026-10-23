#!/usr/bin/env python3
"""
Paw Noon live duels: independent Python reference of the live state machine (Docs/LIVE_DUELS.md).

Written from the protocol description, not translated from live.ts, and resolved with this directory's own Python
port of LocalAuthority (reference_vectors.py), so Tools/Parity/live_vectors.json is cross-checked by three
implementations: this file, supabase/functions/_shared/live.ts (node test live.test.ts) and the client mirror
Assets/PawNoon/Code/Core/LiveMatchState.cs (EditMode LiveMatchStateTests). The C# mirror does not compute the
server-only soft flags; it ignores the `flags` field.

Each case is a short script (join, ready, cue, shots from simulated phones, ticks at deadlines, forfeits). The script
is expanded into concrete ops (server time in ms, full ShotReports on each phone's own clock) and the state after
every op is snapshotted. Implementations replay the concrete ops only.

Usage:  python3 Tools/Parity/live_reference.py            (writes Tools/Parity/live_vectors.json)
        python3 Tools/Parity/live_reference.py --check    (exit 1 if the file would change)
"""
import copy
import json
import math
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import reference_vectors as ref  # noqa: E402

OUT_PATH = os.path.join(ref.ROOT, "Tools", "Parity", "live_vectors.json")
MS = 1_000_000
MAX_SAFE = 9007199254740991
T0 = 1_700_000_000_000

RULES = {
    "version": 1, "rounds": 3, "maxAttempts": 9, "friendLobbyMs": 600_000, "quickLobbyMs": 30_000,
    "readyTimeoutMs": 20_000, "startLeadMs": 800, "networkGraceMs": 1_500, "earlyGraceMs": 1_000,
    "cueEarlyToleranceNs": 200_000_000, "cueLateToleranceNs": 2_000_000_000,
}
PLAUS = {"excessRttFactor": 2, "excessSlackMs": 400, "rttInflateSlackMs": 300, "holdFlaggedRounds": 2}
TERMINAL = ("ko", "double_ko", "rounds", "max_attempts")


def ceil_ms(ns):
    return -(-ns // MS)


def schedule(cfg, seed, k):
    rng = ref.NetRandom(seed)
    s = None
    for _ in range(k + 1):
        s = ref.create_schedule(cfg, rng)
    return s


def report_window_ms(cfg):
    return ceil_ms(cfg.noShotNs + cfg.slackNs) + RULES["networkGraceMs"]


# --------------------------------------------------------------------------- the machine

class Live:
    def __init__(self, cfg, kind, mode, seed, host_gun, now):
        self.cfg = cfg
        self.kind, self.mode, self.seed = kind, mode, seed
        self.guns = [host_gun or "standard", "standard"]
        self.users = ["A", None]
        self.ready = [-1, -1]
        self.phase = "lobby"
        self.deadline = now + (RULES["friendLobbyMs"] if kind == "friend" else RULES["quickLobbyMs"])
        self.attempt = 0
        self.round = None
        self.history = []
        self.hp = [0, 0]
        self.round_wins = [0, 0]
        self.result = None
        self.out = []

    def over(self):
        return self.phase in ("finished", "cancelled")

    # ---- deadlines
    def advance(self, now):
        changed = False
        for _ in range(8):
            if self.over() or now < self.deadline:
                break
            changed = True
            if self.phase == "lobby":
                self.cancel("lobby_expired")
            elif self.phase == "ready":
                r0 = self.ready[0] == self.attempt
                r1 = self.ready[1] == self.attempt
                if r0 and not r1:
                    self.finish(0, "forfeit", 1)
                elif r1 and not r0:
                    self.finish(1, "forfeit", 0)
                else:
                    self.finish(-1, "abandoned", -1)
            elif self.phase == "countdown":
                self.round["cueSent"] = now
                self.phase = "draw"
                self.deadline = now + report_window_ms(self.cfg)
                self.out.append("draw")
            elif self.phase == "draw":
                self.resolve(now)
        return changed

    def enter_ready(self, now):
        self.phase = "ready"
        self.round = None
        self.ready = [-1, -1]
        self.deadline = now + RULES["readyTimeoutMs"]

    def finish(self, winner, reason, forfeited_by):
        self.phase = "finished"
        self.round = None
        self.deadline = MAX_SAFE
        self.result = {"winner": winner, "endReason": reason, "forfeitedBy": forfeited_by,
                       "hp": list(self.hp), "roundWins": list(self.round_wins)}
        self.out.append("match_over")

    def cancel(self, reason):
        self.phase = "cancelled"
        self.round = None
        self.deadline = MAX_SAFE
        self.result = {"winner": -1, "endReason": reason, "forfeitedBy": -1, "hp": list(self.hp), "roundWins": [0, 0]}
        self.out.append("cancelled")

    # ---- actions (each returns a verdict; self.out collects events)
    def join(self, user, gun, now):
        self.advance(now)
        if self.users[1] == user:
            return "Duplicate"
        if self.phase != "lobby":
            return "NotPlaying" if self.over() else "Taken"
        if self.users[0] == user:
            return "Taken"
        self.users[1] = user
        self.guns[1] = gun if gun in ("standard", "peacemaker") else "standard"
        self.hp = [self.cfg.maxHp, self.cfg.maxHp]
        self.enter_ready(now)
        self.out.append("joined")
        return "Accepted"

    def set_ready(self, slot, attempt, now):
        self.advance(now)
        if slot not in (0, 1) or self.phase == "lobby" or self.over():
            return "NotPlaying"
        if self.phase in ("countdown", "draw"):
            if attempt == self.attempt:
                return "Duplicate"
            return "Stale" if attempt < self.attempt else "WrongRound"
        if attempt < self.attempt:
            return "Stale"
        if attempt > self.attempt:
            return "WrongRound"
        if self.ready[slot] == attempt:
            return "Duplicate"
        self.ready[slot] = attempt
        self.out.append("ready")
        if self.ready[1 - slot] == attempt:
            self.start(now)
        return "Accepted"

    def start(self, now):
        k = self.attempt
        sc = schedule(self.cfg, self.seed, k)
        start_at = now + RULES["startLeadMs"]
        due = start_at + ceil_ms(sc.cue_offset_ns)
        resolved = sum(1 for h in self.history if not h["view"]["void"])
        self.round = {"attempt": k, "roundId": k + 1, "roundNumber": resolved + 1, "holdNs": sc.hold_ns,
                      "cueOffsetNs": sc.cue_offset_ns, "startAt": start_at, "cueDue": due, "cueSent": None,
                      "reports": [None, None], "flags": [[], []]}
        self.phase = "countdown"
        self.deadline = due
        self.out.append("round_start")

    def report(self, slot, rep, now):
        self.advance(now)
        v = self.validate(slot, rep, now)
        if v != "Accepted":
            return v
        r = self.round
        stored = dict(rep)
        stored["receivedAtMs"] = now
        r["reports"][slot] = stored
        if rep["kind"] == "Shot":
            r["flags"][slot] = self.shot_flags(r, rep, now)
        if r["reports"][0] is not None and r["reports"][1] is not None:
            self.resolve(now)
        return "Accepted"

    def forfeit(self, slot, now):
        self.advance(now)
        if self.over() or slot not in (0, 1):
            return "NotPlaying"
        if self.phase == "lobby":
            self.cancel("host_left")
        else:
            self.finish(1 - slot, "forfeit", slot)
        return "Accepted"

    def tick(self, now):
        self.advance(now)
        return "Accepted"

    # ---- rules
    def validate(self, slot, rep, now):
        if slot not in (0, 1) or self.phase == "lobby" or self.over():
            return "NotPlaying"
        if self.phase == "ready":
            return "Stale" if rep["roundId"] <= self.attempt else "WrongRound"
        r = self.round
        if rep["roundId"] < r["roundId"]:
            return "Stale"
        if rep["roundId"] != r["roundId"]:
            return "WrongRound"
        if r["reports"][slot] is not None:
            return "Duplicate"
        if rep["countdownStartNs"] <= 0:
            return "Inconsistent"
        sent = r["cueSent"] if r["cueSent"] is not None else r["cueDue"]
        expected = r["cueOffsetNs"] + max(0, sent - r["cueDue"]) * MS
        if rep["kind"] == "Early":
            if r["cueSent"] is not None and now > r["cueSent"] + RULES["earlyGraceMs"]:
                return "Late"
            if rep["inputNs"] < rep["countdownStartNs"]:
                return "Inconsistent"
            if rep["cuePresentedNs"] != 0 and rep["inputNs"] >= rep["cuePresentedNs"]:
                return "Inconsistent"
            if rep["inputNs"] - rep["countdownStartNs"] > expected + RULES["cueLateToleranceNs"]:
                return "Inconsistent"
            return "Accepted"
        if r["cueSent"] is None:
            return "Implausible"
        if now > self.deadline:
            return "Late"
        if rep["reactionNs"] < 0 or rep["reactionNs"] > self.cfg.noShotNs:
            return "Implausible"
        if abs((rep["inputNs"] - rep["cuePresentedNs"]) - rep["reactionNs"]) > 1000:
            return "Inconsistent"
        if rep["cuePresentedNs"] <= rep["countdownStartNs"]:
            return "Inconsistent"
        off = rep["cuePresentedNs"] - rep["countdownStartNs"]
        if off < expected - RULES["cueEarlyToleranceNs"] or off > expected + RULES["cueLateToleranceNs"]:
            return "Inconsistent"
        if (now - r["cueSent"]) * MS + self.cfg.slackNs < rep["reactionNs"]:
            return "Implausible"
        return "Accepted"

    @staticmethod
    def shot_flags(r, rep, now):
        rtt = rep.get("rttMs")
        if rtt is None or rtt < 0 or r["cueSent"] is None:
            return []
        excess = (now - r["cueSent"]) - rep["reactionNs"] / 1e6
        flags = []
        if excess > rtt * PLAUS["excessRttFactor"] + PLAUS["excessSlackMs"]:
            flags.append("excess_latency")
        if rtt > excess + PLAUS["rttInflateSlackMs"]:
            flags.append("rtt_inflated")
        return flags

    def input_of(self, rep):
        if rep is None:
            return {"kind": "noshot", "reactionNs": 0}
        if rep["kind"] == "Early":
            return {"kind": "early", "reactionNs": 0}
        kind = "anticipation" if rep["reactionNs"] < self.cfg.anticipationNs else "shot"
        return {"kind": kind, "reactionNs": rep["reactionNs"]}

    def resolve(self, now):
        r = self.round
        inputs = [self.input_of(r["reports"][0]), self.input_of(r["reports"][1])]
        self.history.append({"attempt": r["attempt"], "roundNumber": r["roundNumber"], "inputs": inputs,
                             "flags": r["flags"], "resolvedAt": now, "view": None})
        case = {"seed": self.seed, "rounds": RULES["rounds"], "maxAttempts": RULES["maxAttempts"],
                "guns": self.guns, "ranked": self.mode == "ranked",
                "a": [h["inputs"][0] for h in self.history], "b": [h["inputs"][1] for h in self.history]}
        res = ref.play_match_case(self.cfg, case)
        assert len(res["attempts"]) == len(self.history), "replay length"
        a = res["attempts"][-1]
        assert a["roundNumber"] == r["roundNumber"], "round number"

        def shot_like(i):
            return inputs[i]["reactionNs"] if a["outcomes"][i] in ("Shot", "Anticipation") else None

        self.history[-1]["view"] = {"void": a["void"], "tie": a["tie"], "winner": a["winner"],
                                    "outcomes": list(a["outcomes"]), "reactionsNs": [shot_like(0), shot_like(1)],
                                    "damage": list(a["damage"]), "grades": list(a["grades"]), "hp": list(a["hp"])}
        self.hp = list(res["hp"])
        self.round_wins = list(res["roundWins"])
        self.round = None
        self.out.append("round_result")
        self.attempt = len(self.history)
        if res["endReason"] in TERMINAL:
            self.finish(res["winner"], res["endReason"], -1)
        else:
            self.enter_ready(now)

    # ---- the snapshot every implementation compares (live.test.ts liveSnapshot, LiveMatchStateTests)
    def snapshot(self, verdict):
        r = self.round
        if r is not None:
            flags = r["flags"]
        elif self.history:
            flags = self.history[-1]["flags"]
        else:
            flags = [[], []]
        return {
            "verdict": verdict,
            "events": list(self.out),
            "phase": self.phase,
            "attempt": self.attempt,
            "deadlineMs": self.deadline,
            "ready": list(self.ready),
            "roundId": r["roundId"] if r else 0,
            "cueDueAtMs": r["cueDue"] if r else 0,
            "cueSentAtMs": (r["cueSent"] if r["cueSent"] is not None else -1) if r else 0,
            "hp": list(self.hp),
            "roundWins": list(self.round_wins),
            "history": [dict({"roundNumber": h["roundNumber"], "kinds": [h["inputs"][0]["kind"], h["inputs"][1]["kind"]]},
                             **h["view"]) for h in self.history],
            "flags": [list(flags[0]), list(flags[1])],
            "result": copy.deepcopy(self.result),
        }


# --------------------------------------------------------------------------- simulated phones

PHONES = {
    # offsetNs: the phone's uptime clock at server T0; down / up: one-way ms; present: cue presentation latency
    "fast": {"offsetNs": 5_000_000_000, "downMs": 20, "upMs": 20, "presentNs": 8 * MS, "rttMs": 45},
    "slow": {"offsetNs": 77_000_000_000, "downMs": 150, "upMs": 140, "presentNs": 16 * MS, "rttMs": 300},
    "unsynced": {"offsetNs": 123_456_789, "downMs": 60, "upMs": 60, "presentNs": 4 * MS, "rttMs": 120, "showsThreeAtArrival": True},
}


def local(p, server_ms):
    return (server_ms - T0) * MS + p["offsetNs"]


def three_at(p, r):
    # a synced phone shows "3" at startAt; an unsynced one when round_start arrives (startAt - lead + down)
    if p.get("showsThreeAtArrival"):
        return local(p, r["startAt"] - RULES["startLeadMs"] + p["downMs"])
    return local(p, r["startAt"])


def make_shot(live, pname, reaction_ms):
    p = PHONES[pname]
    r = live.round
    cue = local(p, r["cueSent"] + p["downMs"]) + p["presentNs"]
    reaction = int(round(reaction_ms * MS))
    rep = {"roundId": r["roundId"], "kind": "Shot", "reactionNs": reaction, "cuePresentedNs": cue,
           "inputNs": cue + reaction, "countdownStartNs": three_at(p, r), "rttMs": p["rttMs"]}
    at = r["cueSent"] + p["downMs"] + math.ceil(reaction_ms + p["presentNs"] / MS) + p["upMs"]
    return rep, at


def make_early(live, pname, after_ms):
    p = PHONES[pname]
    r = live.round
    three = three_at(p, r)
    rep = {"roundId": r["roundId"], "kind": "Early", "reactionNs": 0, "cuePresentedNs": 0,
           "inputNs": three + after_ms * MS, "countdownStartNs": three, "rttMs": p["rttMs"]}
    at = r["startAt"] + after_ms + p["upMs"]
    return rep, at


# --------------------------------------------------------------------------- scripts

def run_case(cfg, name, script, kind="friend", mode="casual", seed=1, guns=("standard", "standard")):
    created = T0
    live = Live(cfg, kind, mode, seed, guns[0], created)
    ops, expect = [], []
    t = created

    def emit(op):
        live.out = []
        if op["op"] == "join":
            v = live.join(op["user"], op["gun"], op["t"])
        elif op["op"] == "ready":
            v = live.set_ready(op["slot"], op["attempt"], op["t"])
        elif op["op"] == "report":
            v = live.report(op["slot"], op["report"], op["t"])
        elif op["op"] == "forfeit":
            v = live.forfeit(op["slot"], op["t"])
        else:
            v = live.tick(op["t"])
        ops.append(op)
        expect.append(live.snapshot(v))

    pending = []  # reports built now, delivered in arrival order by "deliver"
    for step in script:
        cmd = step[0]
        if cmd == "join":
            t += step[1]
            emit({"op": "join", "t": t, "user": step[2] if len(step) > 2 else "B", "gun": guns[1]})
        elif cmd == "ready":  # ("ready", slot, dt[, attempt])
            t += step[2]
            emit({"op": "ready", "t": t, "slot": step[1], "attempt": step[3] if len(step) > 3 else live.attempt})
        elif cmd == "both_ready":
            t += step[1]
            emit({"op": "ready", "t": t, "slot": 0, "attempt": live.attempt})
            t += 7
            emit({"op": "ready", "t": t, "slot": 1, "attempt": live.attempt})
        elif cmd == "cue":  # tick at the due time (+ lateness: a dead driver, the cue goes out later)
            t = live.round["cueDue"] + (step[1] if len(step) > 1 else 0)
            emit({"op": "tick", "t": t})
        elif cmd == "shot":  # ("shot", slot, phone, reactionMs[, patch])
            rep, at = make_shot(live, step[2], step[3])
            if len(step) > 4:
                patch = dict(step[4])
                if "tamper" in patch:
                    apply_tamper(rep, patch.pop("tamper"))
                rep.update(patch)
            pending.append((at, step[1], rep))
        elif cmd == "early":  # ("early", slot, phone, afterStartMs)
            rep, at = make_early(live, step[2], step[3])
            pending.append((at, step[1], rep))
        elif cmd == "deliver":  # in arrival order (ties: slot order); ("deliver", +delayMs) shifts all
            delay = step[1] if len(step) > 1 else 0
            for at, slot, rep in sorted(pending, key=lambda x: (x[0], x[1])):
                t = max(t, at + delay)
                emit({"op": "report", "t": at + delay, "slot": slot, "report": rep})
            pending = []
        elif cmd == "report_at":  # ("report_at", absolute or "cue+N", slot, rep-patch) from the last pending
            at, slot, rep = pending.pop()
            if isinstance(step[1], str) and step[1].startswith("cue+"):
                at = live.round["cueSent"] + int(step[1][4:])
            elif isinstance(step[1], str) and step[1].startswith("start+"):
                at = live.round["startAt"] + int(step[1][6:])
            t = max(t, at)
            emit({"op": "report", "t": at, "slot": slot, "report": rep})
        elif cmd == "deadline":  # tick at the current deadline (+ dt)
            t = live.deadline + (step[1] if len(step) > 1 else 0)
            emit({"op": "tick", "t": t})
        elif cmd == "tick":
            t += step[1]
            emit({"op": "tick", "t": t})
        elif cmd == "forfeit":
            t += step[2]
            emit({"op": "forfeit", "t": t, "slot": step[1]})
        elif cmd == "rawreport":  # ("rawreport", slot, dt, report)
            t += step[2]
            emit({"op": "report", "t": t, "slot": step[1], "report": step[3]})
        else:
            raise ValueError(cmd)
    return {"name": name, "kind": kind, "mode": mode, "seed": seed, "guns": list(guns), "createdAtMs": created,
            "ops": ops, "expect": expect}


def exchange(a, b, pa="fast", pb="slow"):
    """One attempt from ready to result: both ready, cue, both shoot (or jump / stay silent), delivered in order."""
    s = [("both_ready", 3000)]
    pre = [x for x in ((0, a, pa), (1, b, pb)) if x[1] == "early"]
    for slot, _, ph in pre:
        s.append(("early", slot, ph, 900 + 100 * slot))
    if pre:
        s.append(("deliver",))
    if len(pre) == 2:
        return s
    s.append(("cue",))
    for slot, plan, ph in ((0, a, pa), (1, b, pb)):
        if isinstance(plan, (int, float)):
            s.append(("shot", slot, ph, plan))
    s.append(("deliver",))
    if "noshot" in (a, b):
        s.append(("deadline",))
    return s


def cases(cfg):
    J = [("join", 1000)]
    out = []
    out.append(run_case(cfg, "normal_three_rounds", J + exchange(231, 268) + exchange(301, 247) + exchange(255, 290), seed=1))
    out.append(run_case(cfg, "fair_order_slow_phone_faster_reaction",
                        J + exchange(240, 260, "slow", "fast") + exchange(262, 261, "slow", "fast") + exchange(300, 200, "slow", "fast"), seed=3))
    out.append(run_case(cfg, "ko_three_shots", J + exchange(150, "noshot") + exchange(150, "noshot") + exchange(150, "noshot"), seed=42))
    out.append(run_case(cfg, "double_ko_tie", J + exchange(150, 150) + exchange(150, 150) + exchange(200, 205), seed=7))
    out.append(run_case(cfg, "tie_window_edges", J + exchange(250, 265) + exchange(250, 265.000001) + exchange(265, 250), seed=99))
    out.append(run_case(cfg, "fouls_void_replay",
                        J + exchange("early", 280) + exchange(80, 300) + exchange("early", "early") + exchange(240, 250), seed=9))
    out.append(run_case(cfg, "max_attempts_all_void", J + sum((exchange("early", "early") for _ in range(9)), []), seed=1234))
    out.append(run_case(cfg, "timeouts_both_and_one",
                        J + exchange("noshot", "noshot") + exchange(300, "noshot") + exchange("noshot", 410), seed=11))
    # duplicates, out of order, stale, late
    s = J + [("ready", 0, 500, 1), ("ready", 0, 10), ("ready", 0, 10), ("ready", 1, 10), ("ready", 1, 10, 0),
             ("rawreport", 0, 100, {"roundId": 1, "kind": "Shot", "reactionNs": 200 * MS, "cuePresentedNs": 9 * 10**9,
                                    "inputNs": 9 * 10**9 + 200 * MS, "countdownStartNs": 5 * 10**9, "rttMs": 40}),
             ("cue",), ("shot", 0, "fast", 250, {"roundId": 2}), ("report_at", "cue+300"),
             ("shot", 0, "fast", 250), ("report_at", "cue+300"), ("shot", 0, "fast", 250), ("report_at", "cue+350"),
             ("early", 1, "slow", 500), ("report_at", "cue+1001"),
             ("shot", 1, "slow", 400), ("deadline",), ("rawreport", 1, 5, {"roundId": 1, "kind": "Shot", "reactionNs": 400 * MS,
                                                                            "cuePresentedNs": 1, "inputNs": 1 + 400 * MS, "countdownStartNs": 1})]
    out.append(run_case(cfg, "duplicates_stale_late_out_of_order", s, seed=13))
    # the §4.3 checks: every refused report changes nothing; the honest one is accepted last
    s = J + [("both_ready", 2000), ("cue",)]
    for what in ("input", "no_anchor", "cue_too_soon", "cue_too_late", "negative", "over_timeout"):
        s.append(("shot", 0, "fast", 250, {"tamper": what}))
        s.append(("report_at", "cue+300"))
    s += [("shot", 0, "fast", 250), ("report_at", "cue+120"), ("shot", 0, "fast", 250), ("report_at", "cue+300"),
          ("shot", 1, "slow", 270), ("deliver",)]
    out.append(run_case(cfg, "checks_4_3", s, seed=17))
    # soft flags
    s = J + [("both_ready", 2000), ("cue",), ("shot", 0, "fast", 200), ("report_at", "cue+1400"),
             ("shot", 1, "slow", 220, {"rttMs": 900}), ("report_at", "cue+450"),
             ("both_ready", 3000), ("cue",), ("shot", 0, "fast", 200, {"rttMs": None}), ("report_at", "cue+1400"),
             ("shot", 1, "unsynced", 230), ("deliver",)]
    out.append(run_case(cfg, "soft_rtt_flags", s, seed=19))
    out.append(run_case(cfg, "ready_timeout_forfeit", J + exchange(250, 260) + [("ready", 0, 3000), ("deadline",)], seed=21))
    out.append(run_case(cfg, "abandoned_nobody_ready", J + [("deadline",), ("ready", 0, 10)], seed=21))
    out.append(run_case(cfg, "lobby_expired", [("tick", 29_999), ("tick", 1), ("join", 5)], kind="quick", seed=5))
    out.append(run_case(cfg, "lobby_host_left_and_taken", [("join", 10, "A"), ("forfeit", 0, 10), ("join", 10)], seed=6))
    out.append(run_case(cfg, "forfeit_mid_draw", J + exchange(240, 250) + [("both_ready", 3000), ("cue",), ("forfeit", 1, 100), ("forfeit", 0, 10)], mode="ranked", seed=23))
    # reconnection: slot 1 drops (no-shot), comes back 15 s later; then the round driver dies and the cue goes out late
    s = J + exchange(240, "noshot") + [("ready", 0, 1000), ("ready", 1, 15_000), ("cue", 4_000),
                                       ("shot", 0, "fast", 230), ("shot", 1, "unsynced", 260), ("deliver",)]
    s += exchange(250, 255)
    out.append(run_case(cfg, "reconnect_and_late_cue", s, seed=29))
    out.append(run_case(cfg, "guns_casual_peacemaker", J + exchange(190, 200) + exchange(240, 239) + exchange(300, 280),
                        seed=31, guns=("peacemaker", "standard")))
    out.append(run_case(cfg, "guns_ranked_normalised", J + exchange(190, 200) + exchange(240, 239) + exchange(300, 280),
                        mode="ranked", kind="quick", seed=31, guns=("peacemaker", "standard")))
    return out


def apply_tamper(rep, what):
    """The tampered reports of case checks_4_3 (each must be refused and change nothing)."""
    if what == "input":
        rep["inputNs"] += 5000                                   # input - cue != reaction
    elif what == "no_anchor":
        rep["countdownStartNs"] = 0                              # no "3" anchor
    elif what == "cue_too_soon":
        rep["countdownStartNs"] = rep["cuePresentedNs"] - MS     # cue 1 ms after "3" (seed says seconds)
    elif what == "cue_too_late":
        rep["countdownStartNs"] -= 5000 * MS                     # cue far later than countdown + hold
    elif what == "negative":
        rep["reactionNs"] = -1
        rep["inputNs"] = rep["cuePresentedNs"] - 1
    elif what == "over_timeout":
        rep["reactionNs"] = 1501 * MS
        rep["inputNs"] = rep["cuePresentedNs"] + 1501 * MS
    else:
        raise ValueError(what)
    return rep


# --------------------------------------------------------------------------- writer

def build(cfg):
    return {
        "meta": {"version": 1, "generator": "Tools/Parity/live_reference.py", "t0": T0, "rules": RULES, "plaus": PLAUS,
                 "reportWindowMs": report_window_ms(cfg)},
        "cases": cases(cfg),
    }


def fmt(v):
    """reference_vectors.fmt (the C#/Python vector layout) plus null."""
    if v is None:
        return "null"
    if isinstance(v, list):
        return "[" + ",".join(fmt(x) for x in v) + "]"
    if isinstance(v, dict):
        return "{" + ",".join(json.dumps(k) + ":" + fmt(x) for k, x in v.items()) + "}"
    return ref.fmt(v)


def write(data):
    lines = ["{", '  "meta": ' + fmt(data["meta"]) + ",", '  "cases": [']
    cs = []
    for c in data["cases"]:
        head = {k: v for k, v in c.items() if k not in ("ops", "expect")}
        body = ['    {' + fmt(head)[1:-1] + ',',
                '     "ops": [\n' + ",\n".join("      " + fmt(o) for o in c["ops"]) + "\n     ],",
                '     "expect": [\n' + ",\n".join("      " + fmt(e) for e in c["expect"]) + "\n     ]}"]
        cs.append("\n".join(body))
    lines.append(",\n".join(cs))
    lines.append("  ]")
    lines.append("}")
    return "\n".join(lines) + "\n"


def main():
    cfg = ref.load_config()
    text = write(build(cfg))
    json.loads(text)
    if "--check" in sys.argv:
        with open(OUT_PATH, "r", encoding="utf-8") as f:
            ok = f.read() == text
        print("live_vectors.json up to date" if ok else "live_vectors.json differs from the reference")
        sys.exit(0 if ok else 1)
    with open(OUT_PATH, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
    print("wrote", OUT_PATH, "cases:", len(json.loads(text)["cases"]), "ops:", sum(len(c["ops"]) for c in json.loads(text)["cases"]))


if __name__ == "__main__":
    main()
