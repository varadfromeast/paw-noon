// POST /functions/v1/match-submit  { matchId: uuid, recording: Recording }
// Validates the recording (§4.3 + M5 plausibility A1-A3), recomputes the whole match from both recordings
// with the ported rules (never trusting the client's result), then computes everything the match changes
// (ranked MMR for both sides, the pooled recording with its quarantine, stats, the owner's notification,
// rolling plausibility state) and hands it to pn_complete_match, which writes it in ONE transaction (D1).
import { requireUser } from "../_shared/auth.ts";
import { admin, type Db, must } from "../_shared/db.ts";
import { nsToMs } from "../_shared/duel/config.ts";
import { hardConfig } from "../_shared/duel/load_config.ts";
import { curveFor } from "../_shared/duel/loadouts.ts";
import type { MatchResult } from "../_shared/duel/recorded_match.ts";
import { HttpError, json, readJson, serve } from "../_shared/http.ts";
import type { MatchSubmitResponse, RoundView, Side, SlotView } from "../_shared/match_types.ts";
import { type AccountStats, assessMatch } from "../_shared/plausibility.ts";
import { houseRating, loadRating, planGame, RATING_WRITE_ATTEMPTS } from "../_shared/ratings.ts";
import type { AccountStatsRow, MatchRow, ProfileRow } from "../_shared/rows.ts";
import { parseRecording, playableAttempts, type Recording, ShapeError } from "../_shared/recording.ts";
import { allowInjectedInput, MATCH, quarantineMatches, rankedSeason, requireSamplesRanked } from "../_shared/server_config.ts";
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
  if (reason === "wall_clock" || reason === "samples" || reason === "input_source") {
    // plausibility rejections are kept with their data for threshold tuning (never shown to the player)
    const { error } = await db.from("plausibility_log").insert({
      user_id: m.player_id, match_id: m.id, check_name: reason, action: "reject", detail: { detail: detail.slice(0, 500) },
    });
    if (error) console.error(`[match-submit] plausibility log failed match=${m.id}:`, error.message);
  }
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

/**
 * Review 2026-10-20 F1, simplified by D1: the match was claimed (issued -> submitting) but the function failed
 * before pn_complete_match committed. Since that one transaction is the only write, nothing was written: the
 * match goes back to `issued` so the client's offline queue may resend before the deadline. Guarded by
 * `status = 'submitting'`, so a completion that did commit (and only its answer got lost) is never undone.
 */
async function releaseClaim(db: Db, m: MatchRow, cause: unknown): Promise<void> {
  try {
    const { error } = await db.from("matches").update({ status: "issued", submitted_at: null }).eq("id", m.id).eq("status", "submitting");
    if (error) console.error(`[match-submit] release claim failed match=${m.id}:`, error.message);
    else console.warn(`[match-submit] claim released match=${m.id}: ${String(cause).slice(0, 200)}`);
  } catch (e) {
    console.error(`[match-submit] release claim threw match=${m.id}:`, e);
  }
}

function statsFromRow(row: AccountStatsRow | null): AccountStats | null {
  if (!row) return null;
  return { shots: Array.isArray(row.shots) ? row.shots : [], wonShots: row.won_shots, narrowWins: row.narrow_wins, matches: row.matches };
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

  // Everything after the claim: on an unexpected failure release the claim (see releaseClaim) so the row
  // never stays in `submitting`. Recording rejections (422) have already voided the match themselves.
  try {
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
      ranked,
      elapsedNs: (now.getTime() - Date.parse(m.issued_at)) * 1_000_000,
      requireSamples: requireSamplesRanked(),
    }, { allowInjected: allowInjectedInput() });
    if (!verdict.ok) await rejectMatch(db, m, verdict.reason, verdict.detail, true);
    const result = (verdict as { ok: true; result: MatchResult }).result;

    const winner: Side | "draw" = result.winner === 0 ? "you" : result.winner === 1 ? "opponent" : "draw";
    const score = result.winner === 0 ? 1 : result.winner === 1 ? 0 : 0.5;
    const yourReactions = rec.rounds.map((r) => r.reactionNs);
    const oppReactions = snap.attempts.map((a) => a.reactionNs);
    const yourBest = bestShotNs(result, 0, yourReactions);
    const lightning = result.attempts.filter((a) => a.outcomes[0] === "Shot" && a.grades[0] === "Lightning").length;

    // ---- plausibility (A4 + rolling B5-B9): never rejects, never shows; decides hold / shadow / quarantine
    const statsRow = must<AccountStatsRow | null>(
      await db.from("account_stats").select("shots, won_shots, narrow_wins, matches").eq("user_id", user.id).maybeSingle(),
      "load account stats",
    );
    const assess = assessMatch(statsFromRow(statsRow), rec, result, snap.attempts);
    if (assess.log.length) console.warn(`[match-submit] plausibility match=${m.id} user=${user.id}`, JSON.stringify(assess.log));

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
    const poolable = result.endReason === "rounds" || result.endReason === "ko" || result.endReason === "double_ko";
    const ownerId = !snap.isHouse && snap.ownerId ? snap.ownerId : null;

    // ---- compute, then write everything in one transaction; a rating row that changed meanwhile (two
    // players finishing against the same ghost) answers `conflict` with nothing written: re-read and retry
    const season: number | null = m.season;
    let rating: MatchSubmitResponse["rating"] = null;
    let done: { status: string; recordingId?: string | null; opponentMmrAfter?: number | null; roomCode?: string | null } | null = null;
    for (let attempt = 0; attempt < RATING_WRITE_ATTEMPTS && !done; attempt++) {
      const mine = await loadRating(db, user.id, season ?? rankedSeason(), now); // casual: pool by current MMR
      let ratingArg: Record<string, unknown> | null = null;
      let ownerDelta: number | null = null;
      if (ranked && season !== null) {
        const owner = ownerId ? await loadRating(db, ownerId, season, now) : null;
        const opp = owner ?? houseRating(snap.mmr);
        // both sides are updated from the pre-match ratings, like one simultaneous game
        const p = planGame(mine, opp, score);
        const o = owner ? planGame(owner, mine, 1 - score) : null;
        rating = { season, before: Math.round(mine.mmr), after: Math.round(p.next.mmr), delta: Math.round(p.next.mmr) - Math.round(mine.mmr), rd: Math.round(p.next.rd) };
        ownerDelta = o ? Math.round(o.next.mmr) - Math.round(opp.mmr) : null;
        ratingArg = {
          season,
          playerBefore: rating.before,
          playerAfter: rating.after,
          opponentBefore: Math.round(opp.mmr),
          player: { expectMatches: p.expectMatches, ...p.row },
          owner: o && ownerId ? { userId: ownerId, expectMatches: o.expectMatches, ...o.row, after: Math.round(o.next.mmr) } : null,
        };
      }

      const recordingArg = poolable
        ? {
          seed: m.seed,
          mode: m.mode,
          character: rec.character,
          gun: rec.loadout.gun,
          displayName: profile.display_name,
          mmr: Math.round(rating ? rating.after : mine.mmr),
          attempts: playableAttempts(rec),
          audit: rec,
          bestReactionNs: yourBest,
          expiresAt: new Date(now.getTime() + MATCH.poolMaxAgeDays * DAY_MS).toISOString(),
          quarantineLeft: quarantineMatches(),
          held: assess.hold,
        }
        : null;

      const ownerResult = result.winner === 1 ? "won" : result.winner === 0 ? "lost" : "draw";
      const ownerBest = bestShotNs(result, 1, oppReactions);
      const resultArg = {
        winner: winner === "you" ? "player" : winner,
        result: { endReason: result.endReason, hp: result.hp, roundWins: result.roundWins, rounds },
        // a match that tripped a plausibility check does not set a leaderboard best
        stats: { won: result.winner === 0, bestNs: assess.hold ? null : yourBest, lightning },
        notification: ownerId
          ? {
            userId: ownerId,
            payload: {
              matchId: m.id,
              mode: m.mode,
              result: ownerResult,
              from: { displayName: profile.display_name, character: rec.character },
              yourBestNs: ownerBest,
              theirBestNs: yourBest,
              mmrDelta: ownerDelta,
              message: notificationText(profile.display_name, ownerResult, ownerBest),
            },
          }
          : null,
      };
      const plausArg = {
        clean: assess.clean,
        shadow: assess.shadow,
        stats: { ...assess.rolling.next, trips: assess.rolling.trips },
        log: assess.log,
      };

      const res = must<{ status: string; recordingId?: string | null; opponentMmrAfter?: number | null; roomCode?: string | null; matchStatus?: string } | null>(
        await db.rpc("pn_complete_match", {
          p_match_id: m.id,
          p_result: resultArg,
          p_rating: ratingArg,
          p_recording: recordingArg,
          p_plaus: plausArg,
        }),
        "complete match",
      );
      if (!res) throw new HttpError(500, "db_error", "database error: complete match");
      if (res.status === "completed") done = res;
      else if (res.status === "not_submitting") {
        throw new HttpError(409, "already_submitted", `match is ${res.matchStatus}`, { matchStatus: res.matchStatus });
      } else console.warn(`[match-submit] rating conflict (${JSON.stringify(res)}) match=${m.id}, retrying`);
    }
    if (!done) throw new HttpError(503, "busy", "rating is being updated concurrently, retry");

    const out: MatchSubmitResponse = {
      matchId: m.id,
      status: "completed",
      mode: m.mode,
      winner,
      endReason: result.endReason,
      hp: { you: result.hp[0], opponent: result.hp[1] },
      roundWins: { you: result.roundWins[0], opponent: result.roundWins[1] },
      rounds,
      rating,
      recordingId: done.recordingId ?? null,
    };
    if (done.roomCode) out.room = { code: done.roomCode, status: "recorded" };
    return json(out);
  } catch (e) {
    if (!(e instanceof HttpError && (e.code === "recording_rejected" || e.code === "already_submitted"))) await releaseClaim(db, m, e);
    throw e;
  }
});
