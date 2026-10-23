-- Paw Noon phase 2: LIVE duels (D1b; Docs/LIVE_DUELS.md). Both players online at once, a friend room or a quick
-- match. The ghost-duel tables and functions are untouched: a finished live match ALSO writes one ordinary `matches`
-- row per player and one pooled `recordings` row per player, so stats, daily bounty, history, leaderboards and the
-- ghost pool see it like two ghost duels played against each other.
--
--   live_matches  one row per live match: membership (host / guest), status, and the authoritative state machine
--                 (state jsonb, written by the Edge Functions with optimistic `version`, never readable by clients:
--                 it holds the seed and the secret cue time)
--   live_rounds   one row per resolved attempt (audit: reports, inputs, soft flags, the resolved view)
--   pn_live_*     SECURITY DEFINER, service_role only: create / quick pair / join by code / CAS save / finish (ONE
--                 transaction, like pn_complete_match) / sweep
--   Realtime      private channel `live:<id>`: only the two players may listen (and use Presence); nobody but the
--                 server (REST broadcast, service role) may send on it.

-- ---------------------------------------------------------------------------------------- tables
create table public.live_matches (
  id              uuid primary key default gen_random_uuid(),
  kind            text not null check (kind in ('friend', 'quick')),
  mode            text not null check (mode in ('casual', 'ranked')),
  code            text check (code ~ '^[A-Z]{4}$'),
  host_id         uuid not null references auth.users (id) on delete cascade,
  guest_id        uuid references auth.users (id) on delete set null,
  -- lobby: waiting for the guest; playing: ready / countdown / draw; finished: a result (incl. forfeit);
  -- cancelled: the lobby closed; abandoned: both players vanished (no result, nothing rated)
  status          text not null default 'lobby' check (status in ('lobby', 'playing', 'finished', 'cancelled', 'abandoned')),
  season          integer,
  seed            integer not null,
  host_mmr        integer not null default 1200,
  -- C11: shadow / flagged players are paired only with each other (same rule as the ghost pool)
  host_shadow     boolean not null default false,
  state           jsonb not null check (jsonb_typeof(state) = 'object'),
  version         integer not null default 0 check (version >= 0),
  created_at      timestamptz not null default now(),
  updated_at      timestamptz not null default now(),
  expires_at      timestamptz not null,
  started_at      timestamptz,
  finished_at     timestamptz,
  end_reason      text,
  winner_slot     smallint check (winner_slot between -1 and 1),
  result          jsonb,
  host_match_id   uuid references public.matches (id) on delete set null,
  guest_match_id  uuid references public.matches (id) on delete set null,
  constraint live_matches_code_kind check ((kind = 'friend') = (code is not null)),
  constraint live_matches_not_self check (guest_id is null or guest_id <> host_id)
);

comment on table public.live_matches is 'Live duels (phase 2). state = live.ts LiveState (secret: seed, cue time); written by pn_live_* only.';

-- a code is unique among waiting friend lobbies only (codes recycle)
create unique index live_matches_lobby_code_idx on public.live_matches (code) where status = 'lobby' and code is not null;
create index live_matches_code_idx on public.live_matches (code, created_at desc) where code is not null;
create index live_matches_quick_idx on public.live_matches (mode, host_shadow, host_mmr) where status = 'lobby' and kind = 'quick';
create index live_matches_host_idx on public.live_matches (host_id, created_at desc);
create index live_matches_guest_idx on public.live_matches (guest_id, created_at desc) where guest_id is not null;
create index live_matches_open_idx on public.live_matches (updated_at) where status in ('lobby', 'playing');

create table public.live_rounds (
  live_match_id   uuid not null references public.live_matches (id) on delete cascade,
  attempt         smallint not null check (attempt >= 0),
  round_id        integer not null,
  round_number    smallint not null,
  hold_ns         bigint not null,
  start_at_ms     bigint not null,
  cue_sent_at_ms  bigint,
  resolved_at_ms  bigint not null,
  reports         jsonb not null,     -- [LiveReport|null, LiveReport|null]: both phones' raw ShotReports
  inputs          jsonb not null,     -- [AttemptInput, AttemptInput]: what the rules were fed
  flags           jsonb not null,     -- [[...], [...]]: soft reaction-vs-RTT flags (server only)
  view            jsonb not null,     -- the resolved attempt (outcomes, reactions, damage, grades, hp)
  created_at      timestamptz not null default now(),
  primary key (live_match_id, attempt)
);

alter table public.matches add column if not exists live_match_id uuid references public.live_matches (id) on delete set null;
create index if not exists matches_live_idx on public.matches (live_match_id) where live_match_id is not null;

-- ---------------------------------------------------------------------------------------- membership + RLS
-- Membership without exposing anybody's auth id: a SECURITY DEFINER predicate (policies on live_rounds and on
-- realtime.messages call it, so they do not need column grants on live_matches).
create or replace function public.pn_live_is_member(p_match uuid)
returns boolean
language sql stable security definer set search_path = '' as $$
  select exists (select 1 from public.live_matches m
                  where m.id = p_match and (select auth.uid()) is not null
                    and (m.host_id = (select auth.uid()) or m.guest_id = (select auth.uid())));
$$;

-- `live:<uuid>` -> membership (false for any other topic shape, never a cast error)
create or replace function public.pn_live_topic_member(p_topic text)
returns boolean
language plpgsql stable security definer set search_path = '' as $$
begin
  if p_topic is null or p_topic !~ '^live:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' then return false; end if;
  return public.pn_live_is_member(substr(p_topic, 6)::uuid);
end $$;

alter table public.live_matches enable row level security;
alter table public.live_rounds enable row level security;
revoke all on public.live_matches, public.live_rounds from anon, authenticated;

-- players read their own matches, without the secret state, the seed (it is in `result` of match_over only after
-- the end, via the functions), the code-independent auth ids or the version
grant select (id, kind, mode, code, status, season, created_at, started_at, finished_at, expires_at, end_reason,
               winner_slot, result) on public.live_matches to authenticated;
-- resolved attempts of their own matches, without the raw reports / soft flags
grant select (live_match_id, attempt, round_id, round_number, hold_ns, view, created_at) on public.live_rounds to authenticated;

create policy live_matches_select_member on public.live_matches
  for select to authenticated using (public.pn_live_is_member(id));
create policy live_rounds_select_member on public.live_rounds
  for select to authenticated using (public.pn_live_is_member(live_match_id));
-- no insert / update / delete policies or grants: every write is a pn_live_* function (service role)

revoke all on function public.pn_live_is_member(uuid) from public, anon;
revoke all on function public.pn_live_topic_member(text) from public, anon;
grant execute on function public.pn_live_is_member(uuid) to authenticated, service_role;
grant execute on function public.pn_live_topic_member(text) to authenticated, service_role;

-- Realtime Authorization (private channels). Only on a real Supabase stack (the plain-Postgres test harness has no
-- realtime schema). Listening (broadcast + presence) needs membership; sending is Presence only, so a phone can
-- never forge a server message (round_start / draw / round_result) on the match channel.
do $$
begin
  if exists (select 1 from information_schema.tables where table_schema = 'realtime' and table_name = 'messages') then
    execute $p$drop policy if exists pn_live_listen on realtime.messages$p$;
    execute $p$drop policy if exists pn_live_presence on realtime.messages$p$;
    execute $p$create policy pn_live_listen on realtime.messages for select to authenticated
               using (realtime.messages.extension in ('broadcast', 'presence') and public.pn_live_topic_member((select realtime.topic())))$p$;
    execute $p$create policy pn_live_presence on realtime.messages for insert to authenticated
               with check (realtime.messages.extension = 'presence' and public.pn_live_topic_member((select realtime.topic())))$p$;
  end if;
end $$;

-- ---------------------------------------------------------------------------------------- helpers
-- live.ts `cancel()` for rows the sweep closes without the Edge Function: same JSON shape.
create or replace function public.pn_live_cancelled_state(p_state jsonb, p_reason text)
returns jsonb
language sql immutable set search_path = '' as $$
  select p_state || jsonb_build_object(
    'phase', 'cancelled', 'round', null, 'deadlineMs', 9007199254740991,
    'result', jsonb_build_object('winner', -1, 'endReason', p_reason, 'forfeitedBy', -1,
                                 'hp', coalesce(p_state -> 'hp', '[0,0]'::jsonb), 'roundWins', '[0,0]'::jsonb));
$$;

-- live.ts `finish(-1, 'abandoned')` for the sweep.
create or replace function public.pn_live_abandoned_state(p_state jsonb)
returns jsonb
language sql immutable set search_path = '' as $$
  select p_state || jsonb_build_object(
    'phase', 'finished', 'round', null, 'deadlineMs', 9007199254740991,
    'result', jsonb_build_object('winner', -1, 'endReason', 'abandoned', 'forfeitedBy', -1,
                                 'hp', coalesce(p_state -> 'hp', '[0,0]'::jsonb),
                                 'roundWins', coalesce(p_state -> 'roundWins', '[0,0]'::jsonb)));
$$;

-- state.phase -> live_matches.status
create or replace function public.pn_live_status_of(p_state jsonb)
returns text
language sql immutable set search_path = '' as $$
  select case p_state ->> 'phase'
           when 'lobby' then 'lobby'
           when 'cancelled' then 'cancelled'
           when 'finished' then case when p_state -> 'result' ->> 'endReason' = 'abandoned' then 'abandoned' else 'finished' end
           else 'playing' end;
$$;

create or replace function public.pn_live_insert_rounds(p_match uuid, p_attempts jsonb)
returns void
language plpgsql security definer set search_path = '' as $$
declare a jsonb;
begin
  for a in select * from jsonb_array_elements(coalesce(p_attempts, '[]'::jsonb)) loop
    insert into public.live_rounds (live_match_id, attempt, round_id, round_number, hold_ns, start_at_ms, cue_sent_at_ms,
                                    resolved_at_ms, reports, inputs, flags, view)
    values (p_match, (a ->> 'attempt')::smallint, (a ->> 'roundId')::integer, (a ->> 'roundNumber')::smallint,
            (a ->> 'holdNs')::bigint, (a ->> 'startAtMs')::bigint, (a ->> 'cueSentAtMs')::bigint, (a ->> 'resolvedAtMs')::bigint,
            coalesce(a -> 'reports', '[]'::jsonb), coalesce(a -> 'inputs', '[]'::jsonb), coalesce(a -> 'flags', '[]'::jsonb),
            coalesce(a -> 'view', '{}'::jsonb))
    on conflict (live_match_id, attempt) do nothing;
  end loop;
end $$;

-- ---------------------------------------------------------------------------------------- create / pair / join
-- Opens a lobby for p_host (state = live.ts createLive). A friend lobby takes the first free code of p_codes; a quick
-- lobby has no code. The host's other waiting lobbies are cancelled, and lobbies past their expiry are closed.
-- Returns { status: created, id, code, version } | { status: busy } (every candidate code taken).
create or replace function public.pn_live_create(
  p_host uuid, p_kind text, p_mode text, p_codes text[], p_seed integer, p_state jsonb, p_ttl_ms integer,
  p_mmr integer, p_shadow boolean, p_season integer)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  c text;
  v_id uuid;
begin
  update public.live_matches
     set status = 'cancelled', state = public.pn_live_cancelled_state(state, 'lobby_expired'), version = version + 1, updated_at = now()
   where status = 'lobby' and expires_at <= now();
  update public.live_matches
     set status = 'cancelled', state = public.pn_live_cancelled_state(state, 'host_left'), version = version + 1, updated_at = now()
   where host_id = p_host and status = 'lobby';

  if p_kind = 'quick' then
    insert into public.live_matches (kind, mode, host_id, seed, host_mmr, host_shadow, state, expires_at, season)
    values ('quick', p_mode, p_host, p_seed, p_mmr, coalesce(p_shadow, false), p_state,
            now() + make_interval(secs => p_ttl_ms / 1000.0), p_season)
    returning id into v_id;
    return jsonb_build_object('status', 'created', 'id', v_id, 'code', null, 'version', 0);
  end if;

  foreach c in array coalesce(p_codes, '{}') loop
    begin
      insert into public.live_matches (kind, mode, code, host_id, seed, host_mmr, host_shadow, state, expires_at, season)
      values ('friend', p_mode, c, p_host, p_seed, p_mmr, coalesce(p_shadow, false), p_state,
              now() + make_interval(secs => p_ttl_ms / 1000.0), p_season)
      returning id into v_id;
      return jsonb_build_object('status', 'created', 'id', v_id, 'code', c, 'version', 0);
    exception when unique_violation then
      -- code is live in another lobby: next candidate
    end;
  end loop;
  return jsonb_build_object('status', 'busy');
end $$;

-- Quick match: claims the waiting quick lobby closest in MMR (same mode, same pool: shadow with shadow), with a band
-- that widens with the lobby's age (p_band + p_band_per_sec * age). Row-locked (skip locked), so two callers never
-- claim one lobby. Returns { status: joined, id, live (the LiveState), version } | { status: none } (the caller then hosts one).
create or replace function public.pn_live_quick(
  p_user uuid, p_mode text, p_mmr integer, p_shadow boolean, p_band integer, p_band_per_sec integer)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  m public.live_matches;
begin
  select * into m
    from public.live_matches l
   where l.status = 'lobby' and l.kind = 'quick' and l.mode = p_mode and l.guest_id is null
     and l.host_id <> p_user and l.expires_at > now() and l.host_shadow = coalesce(p_shadow, false)
     and abs(l.host_mmr - p_mmr) <= p_band + p_band_per_sec * extract(epoch from now() - l.created_at)
   order by abs(l.host_mmr - p_mmr), l.created_at
   limit 1
   for update skip locked;
  if not found then return jsonb_build_object('status', 'none'); end if;
  update public.live_matches set guest_id = p_user, updated_at = now() where id = m.id;
  return jsonb_build_object('status', 'joined', 'id', m.id, 'live', m.state, 'version', m.version);
end $$;

-- Friend: joins the waiting lobby with this code (row-locked). The caller then applies live.ts joinLive and saves.
--   joined  the caller holds the guest slot: { id, live (the LiveState), version }
--   rejoin  the caller is already this code's guest (lost answer / reconnect): { id }
--   not_found / expired / used / own_room / taken
create or replace function public.pn_live_join(p_guest uuid, p_code text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  m public.live_matches;
begin
  select * into m from public.live_matches where code = p_code and status = 'lobby' for update;
  if not found then
    select * into m from public.live_matches where code = p_code order by created_at desc limit 1;
    if not found then return jsonb_build_object('state', 'not_found'); end if;
    if m.guest_id = p_guest and m.status in ('playing', 'finished') then
      return jsonb_build_object('state', 'rejoin', 'id', m.id);
    end if;
    if m.status in ('playing', 'finished') then return jsonb_build_object('state', 'used'); end if;
    return jsonb_build_object('state', 'expired');
  end if;
  if m.expires_at <= now() then
    update public.live_matches
       set status = 'cancelled', state = public.pn_live_cancelled_state(state, 'lobby_expired'), version = version + 1, updated_at = now()
     where id = m.id;
    return jsonb_build_object('state', 'expired');
  end if;
  if m.host_id = p_guest then return jsonb_build_object('state', 'own_room'); end if;
  if m.guest_id is not null and m.guest_id <> p_guest then return jsonb_build_object('state', 'taken'); end if;
  update public.live_matches set guest_id = p_guest, updated_at = now() where id = m.id;
  return jsonb_build_object('state', 'joined', 'id', m.id, 'live', m.state, 'version', m.version);
end $$;

-- ---------------------------------------------------------------------------------------- CAS save
-- Writes a new state when the stored version is still p_expect (optimistic concurrency between the two phones'
-- calls and the round driver). p_attempts = newly resolved attempts -> live_rounds. A finished match with a result
-- must go through pn_live_finish (it writes ratings, matches and recordings with it); only `abandoned` ends here.
-- Returns { status: ok, version } | { status: conflict, version }.
create or replace function public.pn_live_save(p_id uuid, p_expect integer, p_state jsonb, p_attempts jsonb)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  v_status text := public.pn_live_status_of(p_state);
  v_version integer;
begin
  if v_status = 'finished' then raise exception 'pn_live_save: a finished match goes through pn_live_finish'; end if;
  update public.live_matches
     set state = p_state,
         version = version + 1,
         status = v_status,
         started_at = case when v_status <> 'lobby' then coalesce(started_at, now()) else started_at end,
         finished_at = case when v_status in ('cancelled', 'abandoned') then now() else finished_at end,
         end_reason = p_state -> 'result' ->> 'endReason',
         winner_slot = (p_state -> 'result' ->> 'winner')::smallint,
         result = p_state -> 'result',
         updated_at = now()
   where id = p_id and version = p_expect and status in ('lobby', 'playing')
  returning version into v_version;
  if not found then
    select version into v_version from public.live_matches where id = p_id;
    return jsonb_build_object('status', 'conflict', 'version', v_version);
  end if;
  perform public.pn_live_insert_rounds(p_id, p_attempts);
  return jsonb_build_object('status', 'ok', 'version', v_version);
end $$;

-- ---------------------------------------------------------------------------------------- finish (one transaction)
-- Everything a finished live match writes (live_finish.ts planLiveFinish computes it), in ONE transaction guarded by
-- the state version:
--   p_finish { status, endReason, winnerSlot, season, finishedAt, players: [P0, P1] }
--   P = { userId, rating: null | { expectMatches, mmr, rd, matches, wins, losses, draws },
--         match: { winner: player|opponent|draw, result, opponentSnapshot, mmrBefore, mmrAfter, opponentMmrBefore, opponentMmrAfter },
--         recording: null | { seed, mode, character, gun, displayName, mmr, attempts, audit, bestReactionNs, expiresAt, quarantineLeft, held },
--         stats: { won, bestNs|null, lightning }, plaus: { clean, shadow, stats|null, log: [{check, action, detail}] } }
-- Ratings are optimistic (expectMatches, as in pn_complete_match): a changed row answers `conflict` and NOTHING is
-- written (the caller re-reads and recomputes). Returns { status: completed, version, matchIds, recordingIds }
--   | { status: conflict, who } | { status: not_playing, matchStatus }.
create or replace function public.pn_live_finish(p_id uuid, p_expect integer, p_state jsonb, p_attempts jsonb, p_finish jsonb)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  lm public.live_matches;
  v_season integer := (p_finish ->> 'season')::integer;
  pl jsonb;
  rt jsonb;
  rc jsonb;
  st jsonb;
  pz jsonb;
  v_log jsonb;
  i integer;
  v_n integer;
  v_users uuid[] := array[null, null]::uuid[];
  v_rec uuid[] := array[null, null]::uuid[];
  v_match uuid[] := array[null, null]::uuid[];
  v_tmp uuid;
  v_now timestamptz := now();
  v_version integer;
begin
  select * into lm from public.live_matches where id = p_id for update;
  if not found then return jsonb_build_object('status', 'not_playing', 'matchStatus', null); end if;
  if lm.status not in ('lobby', 'playing') then return jsonb_build_object('status', 'not_playing', 'matchStatus', lm.status); end if;
  if lm.version <> p_expect then return jsonb_build_object('status', 'conflict', 'who', 'version'); end if;
  if public.pn_live_status_of(p_state) <> 'finished' then raise exception 'pn_live_finish: state is not finished'; end if;

  begin
    for i in 0 .. 1 loop
      pl := p_finish -> 'players' -> i;
      v_users[i + 1] := (pl ->> 'userId')::uuid;
      if v_users[i + 1] is distinct from (case when i = 0 then lm.host_id else lm.guest_id end) then
        raise exception 'pn_live_finish: player % is not slot %', v_users[i + 1], i;
      end if;
    end loop;

    -- ---- ratings (ranked): both, version-checked
    for i in 0 .. 1 loop
      rt := p_finish -> 'players' -> i -> 'rating';
      if rt is not null and jsonb_typeof(rt) = 'object' then
        if (rt ->> 'expectMatches')::integer < 0 then
          insert into public.ratings (user_id, season, mmr, rd, matches, wins, losses, draws, last_played_at)
          values (v_users[i + 1], v_season, (rt ->> 'mmr')::float8, (rt ->> 'rd')::float8, (rt ->> 'matches')::integer,
                  (rt ->> 'wins')::integer, (rt ->> 'losses')::integer, (rt ->> 'draws')::integer, v_now)
          on conflict (user_id, season) do nothing;
        else
          update public.ratings
             set mmr = (rt ->> 'mmr')::float8, rd = (rt ->> 'rd')::float8, matches = (rt ->> 'matches')::integer,
                 wins = (rt ->> 'wins')::integer, losses = (rt ->> 'losses')::integer, draws = (rt ->> 'draws')::integer,
                 last_played_at = v_now
           where user_id = v_users[i + 1] and season = v_season and matches = (rt ->> 'expectMatches')::integer;
        end if;
        get diagnostics v_n = row_count;
        if v_n = 0 then raise exception using errcode = 'PN409', message = 'player' || i; end if;
      end if;
    end loop;

    for i in 0 .. 1 loop
      pl := p_finish -> 'players' -> i;
      pz := pl -> 'plaus';
      -- ---- quarantine (C10): a clean match moves the player's earlier recordings one step closer to release
      if coalesce((pz ->> 'clean')::boolean, false) then
        update public.recordings set quarantine_left = quarantine_left - 1
         where owner_id = v_users[i + 1] and quarantine_left > 0 and not held;
      end if;
      -- ---- the player's live recording joins the pool (a match played to the end only)
      rc := pl -> 'recording';
      if rc is not null and jsonb_typeof(rc) = 'object' then
        insert into public.recordings (owner_id, is_house, seed, difficulty, mode, character, gun, display_name, mmr,
                                       attempts, audit, best_reaction_ns, expires_at, quarantine_left, held)
        values (v_users[i + 1], false, (rc ->> 'seed')::integer, 'hard', lm.mode, rc ->> 'character',
                coalesce(rc ->> 'gun', 'standard'), rc ->> 'displayName', (rc ->> 'mmr')::integer, rc -> 'attempts',
                coalesce(rc -> 'audit', '{}'::jsonb), (rc ->> 'bestReactionNs')::bigint, (rc ->> 'expiresAt')::timestamptz,
                coalesce((rc ->> 'quarantineLeft')::smallint, 0), coalesce((rc ->> 'held')::boolean, false))
        returning id into v_tmp;
        v_rec[i + 1] := v_tmp;
      end if;
    end loop;

    -- ---- one ordinary matches row per player (the other player as the opponent)
    for i in 0 .. 1 loop
      pl := p_finish -> 'players' -> i -> 'match';
      insert into public.matches (player_id, opponent_recording_id, opponent_owner_id, opponent_is_house, opponent_snapshot,
                                  mode, season, seed, rounds, max_attempts, status, issued_at, deadline_at, submitted_at,
                                  player_recording_id, winner, result, player_mmr_before, player_mmr_after,
                                  opponent_mmr_before, opponent_mmr_after, live_match_id)
      values (v_users[i + 1], v_rec[2 - i], v_users[2 - i], false,
              (pl -> 'opponentSnapshot') || jsonb_build_object('recordingId', v_rec[2 - i]),
              lm.mode, v_season, lm.seed, (p_state ->> 'rounds')::smallint, (p_state ->> 'maxAttempts')::smallint, 'completed',
              coalesce(lm.started_at, lm.created_at), v_now, v_now, v_rec[i + 1], pl ->> 'winner', pl -> 'result',
              (pl ->> 'mmrBefore')::integer, (pl ->> 'mmrAfter')::integer, (pl ->> 'opponentMmrBefore')::integer,
              (pl ->> 'opponentMmrAfter')::integer, lm.id)
      returning id into v_tmp;
      v_match[i + 1] := v_tmp;
      if v_rec[i + 1] is not null then
        update public.recordings set source_match_id = v_match[i + 1] where id = v_rec[i + 1];
      end if;
    end loop;

    for i in 0 .. 1 loop
      pl := p_finish -> 'players' -> i;
      st := pl -> 'stats';
      pz := pl -> 'plaus';
      -- ---- profile stats (bestNs is null when the match tripped a check)
      update public.profiles p
         set matches_played   = p.matches_played + 1,
             wins             = p.wins + case when coalesce((st ->> 'won')::boolean, false) then 1 else 0 end,
             best_reaction_ns = case when (st ->> 'bestNs') is null then p.best_reaction_ns
                                     when p.best_reaction_ns is null then (st ->> 'bestNs')::bigint
                                     else least(p.best_reaction_ns, (st ->> 'bestNs')::bigint) end,
             lightning_count  = p.lightning_count + greatest(coalesce((st ->> 'lightning')::integer, 0), 0),
             shadow           = p.shadow or coalesce((pz ->> 'shadow')::boolean, false)
       where p.id = v_users[i + 1];
      -- ---- rolling plausibility window + log
      if pz -> 'stats' is not null and jsonb_typeof(pz -> 'stats') = 'object' then
        insert into public.account_stats as a (user_id, shots, won_shots, narrow_wins, matches, trips, updated_at)
        values (v_users[i + 1], coalesce(pz -> 'stats' -> 'shots', '[]'::jsonb),
                coalesce((pz -> 'stats' ->> 'wonShots')::integer, 0), coalesce((pz -> 'stats' ->> 'narrowWins')::integer, 0),
                coalesce((pz -> 'stats' ->> 'matches')::integer, 0),
                coalesce(array(select jsonb_array_elements_text(pz -> 'stats' -> 'trips')), '{}'), v_now)
        on conflict (user_id) do update
           set shots = excluded.shots, won_shots = excluded.won_shots, narrow_wins = excluded.narrow_wins,
               matches = excluded.matches, trips = excluded.trips, updated_at = excluded.updated_at;
      end if;
      for v_log in select * from jsonb_array_elements(coalesce(pz -> 'log', '[]'::jsonb)) loop
        insert into public.plausibility_log (user_id, match_id, check_name, action, detail)
        values (v_users[i + 1], v_match[i + 1], v_log ->> 'check', v_log ->> 'action', coalesce(v_log -> 'detail', '{}'::jsonb));
      end loop;
    end loop;

    -- ---- the live match itself
    update public.live_matches
       set state = p_state, version = version + 1, status = 'finished', updated_at = v_now,
           started_at = coalesce(started_at, v_now), finished_at = v_now,
           end_reason = p_finish ->> 'endReason', winner_slot = (p_finish ->> 'winnerSlot')::smallint,
           result = p_state -> 'result', season = v_season,
           host_match_id = v_match[1], guest_match_id = v_match[2]
     where id = p_id
    returning version into v_version;
    perform public.pn_live_insert_rounds(p_id, p_attempts);
  exception when sqlstate 'PN409' then
    -- a rating row changed under us: everything in this block is rolled back; the caller re-reads and retries
    return jsonb_build_object('status', 'conflict', 'who', sqlerrm);
  end;

  return jsonb_build_object('status', 'completed', 'version', v_version,
                            'matchIds', jsonb_build_array(v_match[1], v_match[2]),
                            'recordingIds', jsonb_build_array(v_rec[1], v_rec[2]));
end $$;

-- ---------------------------------------------------------------------------------------- sweep
-- Closes what nobody will touch again: lobbies past their expiry (cancelled) and playing matches nobody has called
-- for p_idle_sec (both phones gone: abandoned, nothing rated). A match where ONE player is still there is decided by
-- the state machine (that player's live-round call applies the ready deadline: the other forfeits), never here.
-- Safe to run from pg_cron every minute (Docs/LIVE_DUELS.md). Returns the number of rows closed.
create or replace function public.pn_live_sweep(p_idle_sec integer default 600)
returns integer
language plpgsql security definer set search_path = '' as $$
declare
  n1 integer;
  n2 integer;
begin
  update public.live_matches
     set status = 'cancelled', state = public.pn_live_cancelled_state(state, 'lobby_expired'), version = version + 1,
         updated_at = now(), finished_at = now(), end_reason = 'lobby_expired', winner_slot = -1,
         result = public.pn_live_cancelled_state(state, 'lobby_expired') -> 'result'
   where status = 'lobby' and expires_at <= now();
  get diagnostics n1 = row_count;
  update public.live_matches
     set status = 'abandoned', state = public.pn_live_abandoned_state(state), version = version + 1,
         updated_at = now(), finished_at = now(), end_reason = 'abandoned', winner_slot = -1,
         result = public.pn_live_abandoned_state(state) -> 'result'
   where status = 'playing' and updated_at < now() - make_interval(secs => greatest(p_idle_sec, 60));
  get diagnostics n2 = row_count;
  return n1 + n2;
end $$;

-- ---------------------------------------------------------------------------------------- grants
revoke all on function public.pn_live_cancelled_state(jsonb, text) from public, anon, authenticated;
revoke all on function public.pn_live_abandoned_state(jsonb) from public, anon, authenticated;
revoke all on function public.pn_live_status_of(jsonb) from public, anon, authenticated;
revoke all on function public.pn_live_insert_rounds(uuid, jsonb) from public, anon, authenticated;
revoke all on function public.pn_live_create(uuid, text, text, text[], integer, jsonb, integer, integer, boolean, integer) from public, anon, authenticated;
revoke all on function public.pn_live_quick(uuid, text, integer, boolean, integer, integer) from public, anon, authenticated;
revoke all on function public.pn_live_join(uuid, text) from public, anon, authenticated;
revoke all on function public.pn_live_save(uuid, integer, jsonb, jsonb) from public, anon, authenticated;
revoke all on function public.pn_live_finish(uuid, integer, jsonb, jsonb, jsonb) from public, anon, authenticated;
revoke all on function public.pn_live_sweep(integer) from public, anon, authenticated;
grant execute on function public.pn_live_cancelled_state(jsonb, text) to service_role;
grant execute on function public.pn_live_abandoned_state(jsonb) to service_role;
grant execute on function public.pn_live_status_of(jsonb) to service_role;
grant execute on function public.pn_live_insert_rounds(uuid, jsonb) to service_role;
grant execute on function public.pn_live_create(uuid, text, text, text[], integer, jsonb, integer, integer, boolean, integer) to service_role;
grant execute on function public.pn_live_quick(uuid, text, integer, boolean, integer, integer) to service_role;
grant execute on function public.pn_live_join(uuid, text) to service_role;
grant execute on function public.pn_live_save(uuid, integer, jsonb, jsonb) to service_role;
grant execute on function public.pn_live_finish(uuid, integer, jsonb, jsonb, jsonb) to service_role;
grant execute on function public.pn_live_sweep(integer) to service_role;
