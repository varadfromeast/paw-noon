// POST /functions/v1/match-submit  { matchId: uuid, recording: Recording }
// Validates the recording (§4.3), recomputes the whole match from both recordings with the ported
// rules (never trusting the client's result), writes the result, updates ranked MMR (Glicko-lite),
// pools the recording as somebody's next opponent and notifies the opponent's owner.
import { requireUser } from "../_shared/auth.ts";
import { admin, type Db, must } from "../_shared/db.ts";
import { nsToMs } from "../_shared/duel/config.ts";
import { hardConfig } from "../_shared/duel/load_config.ts";
import { curveFor } from "../_shared/duel/loadouts.ts";
import type { MatchResult } from "../_shared/duel/recorded_match.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import type { MatchSubmitResponse, RoundView, Side, SlotView } from "../_shared/match_types.ts";
import { applyGame, houseRating, loadRating } from "../_shared/ratings.ts";
import type { MatchRow, ProfileRow } from "../_shared/rows.ts";
import { parseRecording, playableAttempts, type Recording, ShapeError } from "../_shared/recording.ts";
import { allowInjectedInput, MATCH, rankedSeason } from "../_shared/server_config.ts";
import { validateAndScore } from "../_shared/validate_recording.ts";

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DAY_MS = 86_400_000;


/** Void the match, log the reason, and (unless excused) count a rejection towards the flag. */
async function rejectMatch(db: Db, m: MatchRow, reason: string, detail: string, counts: boolean): Promise<never> {
  must(
    await db.from("matches").update({ status: "void", void_reason: reason, void_detail: detail.slice(0, 500) }).eq("id", m.id),
    "void match",
  );
  console.warn(`[match-submit] rejected match=${m.id} user=${m.player_id} reason=${reason} detail=${detail}`);
  let flagged = false;
  if (counts) {
    const rows = must<{ flagged: boolean }[] | null>(
      await db.rpc("pn_register_rejection", { p_user: m.player_id, p_flag_at: MATCH.rejectionsToFlag }),
      "register rejection",
    );
    flagged = rows?.[0]?.flagged ?? false;
  }
  throw new HttpError(422, "recording_rejected", detail, { reason, matchStatus: "void", flagged });
}

function side(slot: number): Side | null {
  return slot === 0 ? "you" : slot === 1 ? "opponent" : null;
}

function slotView(r: MatchResult["attempts"][number], slot: 0 | 1, reactionNs: number): SlotView {
  const shotLike = r.outcomes[slot] === "Shot" || r.outcomes[slot] === "Anticipation";
  return { outcome: r.outcomes[slot], reactionNs: shotLike ? reactionNs : null, damage: r.damage[slot], grade: r.grades[slot] };
}

function bestShotNs(result: MatchResult, slot: 0 | 1, reactions: number[]): number | null {
  let best: number | null = null;
  result.attempts.forEach((a, i) => {
    if (a.outcomes[slot] === "Shot" && (best === null || reactions[i] < best)) best = reactions[i];
  });
  return best;
}

function notificationText(name: string, result: "won" | "lost" | "draw", ownerBestNs: number | null): string {
  const ms = ownerBestNs === null ? null : `${Math.round(nsToMs(ownerBestNs))} ms`;
  if (result === "lost") return ms ? `${name} beat your ${ms}` : `${name} beat your ghost`;
  if (result === "won") return ms ? `Your ${ms} ghost beat ${name}` : `Your ghost beat ${name}`;
  return `${name} drew with your ghost`;
}

serve("match-submit", async (req) => {
  const db = admin();
  const user = await requireUser(req, db);
  const body = await readJson(req, MATCH.maxBodyBytes);

  const matchId = body.matchId;
  if (typeof matchId !== "string" || !UUID.test(matchId)) throw new HttpError(400, "bad_request", "matchId must be a uuid");
  let rec: Recording;
  try {
    rec = parseRecording(body.recording);
  } catch (e) {
    if (e instanceof ShapeError) throw new HttpError(400, "bad_recording", e.message);
    throw e;
  }

  // One submission per match id: atomically move issued -> submitting.
  const now = new Date();
  const m = must<MatchRow | null>(
    await db.from("matches").update({ status: "submitting", submitted_at: now.toISOString() })
      .eq("id", matchId).eq("player_id", user.id).eq("status", "issued")
      .select("*").maybeSingle(),
    "claim match",
  );
  if (!m) {
    const existing = must<{ status: string } | null>(
      await db.from("matches").select("status").eq("id", matchId).eq("player_id", user.id).maybeSingle(),
      "find match",
    );
    if (!existing) throw new HttpError(404, "match_not_found", "no such match for this player");
    throw new HttpError(409, "already_submitted", `match is ${existing.status}`, { matchStatus: existing.status });
  }

  // §4.3: within matchDuration + 60 s of match/start. A late upload (offline queue) voids the match
  // but does not count towards the cheat flag.
  if (now.getTime() > Date.parse(m.deadline_at)) {
    await rejectMatch(db, m, "late", `submitted after the deadline ${m.deadline_at}`, false);
  }

  const profile = must<Pick<ProfileRow, "display_name" | "owned_items">>(
    await db.from("profiles").select("display_name, owned_items").eq("id", user.id).single(),
    "load profile",
  );
  const owned: string[] = profile.owned_items ?? [];
  if (rec.loadout.gun !== "standard" && !owned.includes(rec.loadout.gun)) {
    await rejectMatch(db, m, "loadout_not_owned", `gun ${rec.loadout.gun} is not owned`, true);
  }

  const cfg = hardConfig();
  const ranked = m.mode === "ranked";
  const snap = m.opponent_snapshot;
  const verdict = validateAndScore(cfg, rec, {
    seed: m.seed,
    rounds: m.rounds,
    maxAttempts: m.max_attempts,
    opponent: snap.attempts,
    curves: [curveFor(cfg, rec.loadout.gun, ranked), curveFor(cfg, snap.gun, ranked)],
  }, { allowInjected: allowInjectedInput() });
  if (!verdict.ok) await rejectMatch(db, m, verdict.reason, verdict.detail, true);
  const result = (verdict as { ok: true; result: MatchResult }).result;

  const winner: Side | "draw" = result.winner === 0 ? "you" : result.winner === 1 ? "opponent" : "draw";
  const score = result.winner === 0 ? 1 : result.winner === 1 ? 0 : 0.5;

  // ---- ratings (ranked only; the ghost's owner is rated too, as if they had played live)
  let rating: MatchSubmitResponse["rating"] = null;
  let oppBefore: number | null = null;
  let oppAfter: number | null = null;
  const season: number | null = m.season;
  const mine = await loadRating(db, user.id, season ?? rankedSeason()); // casual: pool by current MMR
  if (ranked && season !== null) {
    const owner = !snap.isHouse && snap.ownerId ? await loadRating(db, snap.ownerId, season) : null;
    const opp = owner ?? houseRating(snap.mmr);
    // both sides are updated from the pre-match ratings, like one simultaneous game
    const next = await applyGame(db, user.id, season, mine, opp, score, now);
    rating = { season, before: Math.round(mine.mmr), after: Math.round(next.mmr), delta: Math.round(next.mmr - mine.mmr), rd: Math.round(next.rd) };
    oppBefore = Math.round(opp.mmr);
    if (owner && snap.ownerId) {
      const ownerNext = await applyGame(db, snap.ownerId, season, owner, mine, 1 - score, now);
      oppAfter = Math.round(ownerNext.mmr);
    }
  }

  // ---- the player's recording joins the pool (only complete matches make good ghosts)
  const yourReactions = rec.rounds.map((r) => r.reactionNs);
  const oppReactions = snap.attempts.map((a) => a.reactionNs);
  const yourBest = bestShotNs(result, 0, yourReactions);
  let recordingId: string | null = null;
  if (result.endReason === "rounds" || result.endReason === "ko" || result.endReason === "double_ko") {
    const inserted = must<{ id: string }>(
      await db.from("recordings").insert({
        owner_id: user.id,
        is_house: false,
        source_match_id: m.id,
        seed: m.seed,
        difficulty: "hard",
        mode: m.mode,
        character: rec.character,
        gun: rec.loadout.gun,
        display_name: profile.display_name,
        mmr: Math.round(rating ? rating.after : mine.mmr),
        attempts: playableAttempts(rec),
        audit: rec,
        best_reaction_ns: yourBest,
        expires_at: new Date(now.getTime() + MATCH.poolMaxAgeDays * DAY_MS).toISOString(),
      }).select("id").single(),
      "pool recording",
    );
    recordingId = inserted.id;
  }

  const rounds: RoundView[] = result.attempts.map((a, i) => ({
    attempt: i + 1,
    roundNumber: a.roundNumber,
    holdNs: a.holdNs,
    void: a.void,
    tie: a.tie,
    winner: side(a.winner),
    you: slotView(a, 0, yourReactions[i]),
    opponent: slotView(a, 1, oppReactions[i]),
    hpAfter: { you: a.hp[0], opponent: a.hp[1] },
  }));

  must(
    await db.from("matches").update({
      status: "completed",
      winner: winner === "you" ? "player" : winner,
      result: { endReason: result.endReason, hp: result.hp, roundWins: result.roundWins, rounds },
      player_recording_id: recordingId,
      player_mmr_before: rating?.before ?? null,
      player_mmr_after: rating?.after ?? null,
      opponent_mmr_before: oppBefore,
      opponent_mmr_after: oppAfter,
    }).eq("id", m.id),
    "complete match",
  );

  const lightning = result.attempts.filter((a) => a.outcomes[0] === "Shot" && a.grades[0] === "Lightning").length;
  must(
    await db.rpc("pn_record_match_stats", { p_user: user.id, p_won: result.winner === 0, p_best_ns: yourBest, p_lightning: lightning }),
    "record stats",
  );

  if (!snap.isHouse && snap.ownerId) {
    const ownerResult = result.winner === 1 ? "won" : result.winner === 0 ? "lost" : "draw";
    const ownerBest = bestShotNs(result, 1, oppReactions);
    must(
      await db.from("notifications").insert({
        user_id: snap.ownerId,
        kind: "ghost_result",
        payload: {
          matchId: m.id,
          mode: m.mode,
          result: ownerResult,
          from: { displayName: profile.display_name, character: rec.character },
          yourBestNs: ownerBest,
          theirBestNs: yourBest,
          mmrDelta: oppAfter !== null && oppBefore !== null ? oppAfter - oppBefore : null,
          message: notificationText(profile.display_name, ownerResult, ownerBest),
        },
      }),
      "notify opponent",
    );
  }

  const res: MatchSubmitResponse = {
    matchId: m.id,
    status: "completed",
    mode: m.mode,
    winner,
    endReason: result.endReason,
    hp: { you: result.hp[0], opponent: result.hp[1] },
    roundWins: { you: result.roundWins[0], opponent: result.roundWins[1] },
    rounds,
    rating,
    recordingId,
  };
  return json(res);
});
