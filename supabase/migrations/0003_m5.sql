-- Paw Noon M5 (SPEC_V2 §4.3, §4.4, §7; Docs/M5_PLAUSIBILITY.md; review D1). Additive on top of 0001 + 0002.
--
--   * friend duels: `rooms` (4-letter code -> the host's NEXT recording, for one friend, 10 min)
--   * weekly ranked seasons (ratings.season = YYYYWW) and leaderboards: pn_leaderboard()
--   * plausibility containment: profiles.shadow, recordings.quarantine_left / held, account_stats, plausibility_log
--   * D1: pn_complete_match() writes a validated match in ONE transaction (ratings, pool recording, match row,
--     stats, owner notification, room link, plausibility state). The Edge Function only computes.

-- ---------------------------------------------------------------------------------------- profiles
-- C11 shadow flag: set by the rolling plausibility checks. A shadow account keeps playing, but only against
-- house ghosts and other flagged / shadow players, its recordings are never served to anybody else and it is
-- left off the public leaderboards. Nothing in any response says so. Cleared by ops only.
alter table public.profiles add column if not exists shadow boolean not null default false;

create index if not exists profiles_best_reaction_idx on public.profiles (best_reaction_ns)
  where best_reaction_ns is not null and not flagged and not shadow;

-- ---------------------------------------------------------------------------------------- recordings
-- C10 quarantine: a new pooled recording waits until its owner has completed `quarantine_left` further clean
-- matches. `held`: tripped A4 / B5-B9, kept for audit, never served (it simply expires after 30 days).
alter table public.recordings
  add column if not exists quarantine_left smallint not null default 0 check (quarantine_left >= 0),
  add column if not exists held boolean not null default false;

create index if not exists recordings_owner_quarantine_idx on public.recordings (owner_id) where quarantine_left > 0;

-- ---------------------------------------------------------------------------------------- matches
alter table public.matches add column if not exists room_id uuid;

-- ---------------------------------------------------------------------------------------- rooms
create table public.rooms (
  id              uuid primary key default gen_random_uuid(),
  code            text not null check (code ~ '^[A-Z]{4}$'),
  host_id         uuid not null references auth.users (id) on delete cascade,
  -- open: waiting for the host's next recording; recorded: the friend can play it; used: a match was issued
  -- to the friend; expired / cancelled: gone (the code is free again)
  status          text not null default 'open' check (status in ('open', 'recorded', 'used', 'expired', 'cancelled')),
  created_at      timestamptz not null default now(),
  expires_at      timestamptz not null,
  recording_id    uuid references public.recordings (id) on delete set null,
  host_match_id   uuid references public.matches (id) on delete set null,
  recorded_at     timestamptz,
  guest_id        uuid references auth.users (id) on delete set null,
  joined_at       timestamptz,
  guest_match_id  uuid references public.matches (id) on delete set null,
  used_at         timestamptz
);

comment on table public.rooms is 'Friend duels: a code reserves the host''s next recording for one friend. Written by pn_room_* only.';

-- a code is unique among live rooms only; expired / used codes are free again
create unique index rooms_live_code_idx on public.rooms (code) where status in ('open', 'recorded');
create index rooms_code_idx on public.rooms (code, created_at desc);
create index rooms_host_idx on public.rooms (host_id, created_at desc);
create index rooms_live_expiry_idx on public.rooms (expires_at) where status in ('open', 'recorded');

alter table public.matches
  add constraint matches_room_fk foreign key (room_id) references public.rooms (id) on delete set null;

-- ---------------------------------------------------------------------------------------- plausibility
-- B5-B9 rolling window per account (written by pn_complete_match from the function's computation).
create table public.account_stats (
  user_id      uuid primary key references auth.users (id) on delete cascade,
  shots        jsonb not null default '[]'::jsonb check (jsonb_typeof(shots) = 'array'),  -- [[reactionNs, holdNs], ...]
  won_shots    integer not null default 0,
  narrow_wins  integer not null default 0,
  matches      integer not null default 0,
  trips        text[] not null default '{}',
  updated_at   timestamptz not null default now()
);

-- Every tripped check with its data, so thresholds can be tuned against real distributions (never shown).
create table public.plausibility_log (
  id          bigint generated always as identity primary key,
  user_id     uuid references auth.users (id) on delete cascade,
  match_id    uuid references public.matches (id) on delete set null,
  check_name  text not null,
  action      text not null check (action in ('reject', 'hold', 'shadow', 'log')),
  detail      jsonb not null default '{}'::jsonb,
  created_at  timestamptz not null default now()
);
create index plausibility_log_user_idx on public.plausibility_log (user_id, created_at desc);

alter table public.rooms enable row level security;
alter table public.account_stats enable row level security;
alter table public.plausibility_log enable row level security;
revoke all on public.rooms, public.account_stats, public.plausibility_log from anon, authenticated;
-- no grants: rooms are served by room-create / room-join, the plausibility tables are service-only

-- ---------------------------------------------------------------------------------------- pool
-- 0002 plus M5: quarantined and held recordings are not served; flagged / shadow players are a separate pool
-- (C11): they meet house ghosts and each other only, and nobody else ever meets them.
create or replace function public.pn_pick_opponent(p_user uuid, p_mmr integer, p_mode text)
returns public.recordings
language plpgsql security definer set search_path = '' as $$
declare
  r public.recordings;
  band integer;
  v_shadow boolean;
begin
  select coalesce(p.flagged or p.shadow, false) into v_shadow from public.profiles p where p.id = p_user;
  v_shadow := coalesce(v_shadow, false);

  foreach band in array array[150, 300, 600, 1200, 1000000] loop
    select rec.* into r
      from public.recordings rec
      join public.profiles op on op.id = rec.owner_id
     where not rec.is_house
       and rec.owner_id <> p_user
       and rec.difficulty = 'hard'
       and (p_mode = 'casual' or rec.mode = 'ranked')
       and rec.uses < 20
       and rec.expires_at > now()
       and abs(rec.mmr - p_mmr) <= band
       and (op.flagged or op.shadow) = v_shadow
       and (v_shadow or (rec.quarantine_left = 0 and not rec.held))
       and not exists (select 1 from public.matches m
                        where m.player_id = p_user and m.opponent_recording_id = rec.id)
     order by abs(rec.mmr - p_mmr), random()
     limit 1
     for update of rec skip locked;
    if found then
      update public.recordings set uses = uses + 1 where id = r.id;
      return r;
    end if;
  end loop;

  select h.* into r
    from public.recordings h
   where h.is_house
   order by (select count(*) from public.matches m
              where m.player_id = p_user and m.opponent_recording_id = h.id), abs(h.mmr - p_mmr), random()
   limit 1;
  if found then
    update public.recordings set uses = uses + 1 where id = r.id;
  end if;
  return r;
end $$;

-- ---------------------------------------------------------------------------------------- rooms: create
-- Opens a room for p_host with the first free code of p_codes (the function generates candidates without
-- I / O and without blocked words). Expires stale rooms first, and cancels the host's other room that is still
-- waiting for a recording (one open room per host; a `recorded` room stays playable for its friend).
-- Returns the new row, or no row when every candidate was taken.
create or replace function public.pn_room_create(p_host uuid, p_codes text[], p_ttl_seconds integer)
returns setof public.rooms
language plpgsql security definer set search_path = '' as $$
declare
  c text;
  r public.rooms;
begin
  update public.rooms set status = 'expired' where status in ('open', 'recorded') and expires_at <= now();
  update public.rooms set status = 'cancelled' where host_id = p_host and status = 'open';
  foreach c in array p_codes loop
    begin
      insert into public.rooms (code, host_id, expires_at)
      values (c, p_host, now() + make_interval(secs => p_ttl_seconds))
      returning * into r;
      return next r;
      return;
    exception when unique_violation then
      -- code is live in another room: try the next candidate
    end;
  end loop;
  return;
end $$;

-- ---------------------------------------------------------------------------------------- rooms: join
-- One atomic step for room-join (row-locked):
--   waiting   the host has not recorded yet; the caller now holds the friend slot (poll again)
--   ready     the room is now `used` by the caller; `recording` is the host's recording to issue a match against
--   rejoin    the caller already got this room's match (lost answer): `guestMatchId`
--   not_found / expired / used / own_room / taken
create or replace function public.pn_room_join(p_guest uuid, p_code text)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  r public.rooms;
  rec public.recordings;
  host_name text;
begin
  select * into r from public.rooms where code = p_code and status in ('open', 'recorded') for update;
  if not found then
    select * into r from public.rooms where code = p_code order by created_at desc limit 1;
    if not found then return jsonb_build_object('state', 'not_found'); end if;
    if r.status = 'used' and r.guest_id = p_guest then
      return jsonb_build_object('state', 'rejoin', 'roomId', r.id, 'guestMatchId', r.guest_match_id);
    end if;
    if r.status = 'used' then return jsonb_build_object('state', 'used'); end if;
    return jsonb_build_object('state', 'expired');
  end if;

  if r.expires_at <= now() then
    update public.rooms set status = 'expired' where id = r.id;
    return jsonb_build_object('state', 'expired');
  end if;
  if r.host_id = p_guest then return jsonb_build_object('state', 'own_room'); end if;
  if r.guest_id is not null and r.guest_id <> p_guest then return jsonb_build_object('state', 'taken'); end if;

  select p.display_name into host_name from public.profiles p where p.id = r.host_id;

  if r.status = 'open' or r.recording_id is null then
    update public.rooms set guest_id = p_guest, joined_at = coalesce(joined_at, now()) where id = r.id;
    return jsonb_build_object('state', 'waiting', 'roomId', r.id, 'code', r.code, 'expiresAt', r.expires_at,
                              'hostName', coalesce(host_name, 'Stranger'));
  end if;

  select * into rec from public.recordings where id = r.recording_id;
  if not found then
    update public.rooms set status = 'expired' where id = r.id;
    return jsonb_build_object('state', 'expired');
  end if;
  update public.rooms set status = 'used', guest_id = p_guest, joined_at = coalesce(joined_at, now()), used_at = now()
   where id = r.id;
  return jsonb_build_object('state', 'ready', 'roomId', r.id, 'code', r.code, 'hostName', coalesce(host_name, 'Stranger'),
                            'recording', to_jsonb(rec));
end $$;

-- room-join could not issue the match after pn_room_join said `ready`: give the room back to its friend.
create or replace function public.pn_room_unclaim(p_room uuid, p_guest uuid)
returns void
language sql security definer set search_path = '' as $$
  update public.rooms set status = 'recorded', used_at = null
   where id = p_room and guest_id = p_guest and status = 'used' and guest_match_id is null;
$$;

-- ---------------------------------------------------------------------------------------- D1: complete a match
-- Everything a validated match-submit writes, in ONE transaction, guarded by `status = 'submitting'`.
-- The function (match-submit) computes; this only writes.
--
-- p_result     { winner: player|opponent|draw, result: <matches.result>, stats: { won, bestNs|null, lightning },
--                notification: null | { userId, payload } }
-- p_rating     null (casual) | { season, playerBefore, playerAfter, opponentBefore,
--                player: { expectMatches, mmr, rd, matches, wins, losses, draws },
--                owner:  null | { userId, expectMatches, mmr, rd, matches, wins, losses, draws, after } }
--              expectMatches = the rating row's `matches` the values were computed from (-1 = no row yet):
--              optimistic concurrency, a changed row answers `conflict` and NOTHING is written.
-- p_recording  null (not pooled) | { seed, mode, character, gun, displayName, mmr, attempts, audit, bestReactionNs,
--                expiresAt, quarantineLeft, held }
-- p_plaus      { clean, shadow, stats: null | { shots, wonShots, narrowWins, matches, trips }, log: [{check, action, detail}] }
--
-- Returns { status: completed, recordingId, opponentMmrAfter, roomCode }  |  { status: conflict, who }
--       | { status: not_submitting, matchStatus }
-- The ghost owner's rating and notification are best-effort inside the transaction (a deleted owner does not
-- fail the caller's match), as in review F2.
create or replace function public.pn_complete_match(
  p_match_id uuid, p_result jsonb, p_rating jsonb, p_recording jsonb, p_plaus jsonb)
returns jsonb
language plpgsql security definer set search_path = '' as $$
declare
  m public.matches;
  pl jsonb := p_rating -> 'player';
  ow jsonb := p_rating -> 'owner';
  v_season integer := (p_rating ->> 'season')::integer;
  v_rec_id uuid;
  v_room_code text;
  v_opp_after integer;
  v_now timestamptz := now();
  v_n integer;
  v_log jsonb;
begin
  select * into m from public.matches where id = p_match_id for update;
  if not found then return jsonb_build_object('status', 'not_submitting', 'matchStatus', null); end if;
  if m.status <> 'submitting' then return jsonb_build_object('status', 'not_submitting', 'matchStatus', m.status); end if;

  begin
    -- ---- ratings (ranked): player first, version-checked
    if p_rating is not null and jsonb_typeof(p_rating) = 'object' then
      if (pl ->> 'expectMatches')::integer < 0 then
        insert into public.ratings (user_id, season, mmr, rd, matches, wins, losses, draws, last_played_at)
        values (m.player_id, v_season, (pl ->> 'mmr')::float8, (pl ->> 'rd')::float8, (pl ->> 'matches')::integer,
                (pl ->> 'wins')::integer, (pl ->> 'losses')::integer, (pl ->> 'draws')::integer, v_now)
        on conflict (user_id, season) do nothing;
      else
        update public.ratings
           set mmr = (pl ->> 'mmr')::float8, rd = (pl ->> 'rd')::float8, matches = (pl ->> 'matches')::integer,
               wins = (pl ->> 'wins')::integer, losses = (pl ->> 'losses')::integer, draws = (pl ->> 'draws')::integer,
               last_played_at = v_now
         where user_id = m.player_id and season = v_season and matches = (pl ->> 'expectMatches')::integer;
      end if;
      get diagnostics v_n = row_count;
      if v_n = 0 then raise exception using errcode = 'PN409', message = 'player'; end if;

      -- the ghost's owner, rated as if they had played live (best effort: the owner may be gone)
      if ow is not null and jsonb_typeof(ow) = 'object' then
        begin
          if (ow ->> 'expectMatches')::integer < 0 then
            insert into public.ratings (user_id, season, mmr, rd, matches, wins, losses, draws, last_played_at)
            values ((ow ->> 'userId')::uuid, v_season, (ow ->> 'mmr')::float8, (ow ->> 'rd')::float8,
                    (ow ->> 'matches')::integer, (ow ->> 'wins')::integer, (ow ->> 'losses')::integer,
                    (ow ->> 'draws')::integer, v_now)
            on conflict (user_id, season) do nothing;
          else
            update public.ratings
               set mmr = (ow ->> 'mmr')::float8, rd = (ow ->> 'rd')::float8, matches = (ow ->> 'matches')::integer,
                   wins = (ow ->> 'wins')::integer, losses = (ow ->> 'losses')::integer, draws = (ow ->> 'draws')::integer,
                   last_played_at = v_now
             where user_id = (ow ->> 'userId')::uuid and season = v_season and matches = (ow ->> 'expectMatches')::integer;
          end if;
          get diagnostics v_n = row_count;
          if v_n = 0 then raise exception using errcode = 'PN409', message = 'owner'; end if;
          v_opp_after := (ow ->> 'after')::integer;
        exception when foreign_key_violation then
          v_opp_after := null;   -- owner deleted meanwhile: their side is skipped, the match still counts
        end;
      end if;
    end if;

    -- ---- quarantine (C10): a clean match moves the owner's earlier recordings one step closer to release
    if coalesce((p_plaus ->> 'clean')::boolean, true) then
      update public.recordings set quarantine_left = quarantine_left - 1
       where owner_id = m.player_id and quarantine_left > 0 and not held;
    end if;

    -- ---- the player's recording joins the pool
    if p_recording is not null and jsonb_typeof(p_recording) = 'object' then
      insert into public.recordings (owner_id, is_house, source_match_id, seed, difficulty, mode, character, gun,
                                     display_name, mmr, attempts, audit, best_reaction_ns, expires_at,
                                     quarantine_left, held)
      values (m.player_id, false, m.id, (p_recording ->> 'seed')::integer, 'hard', m.mode,
              p_recording ->> 'character', coalesce(p_recording ->> 'gun', 'standard'), p_recording ->> 'displayName',
              (p_recording ->> 'mmr')::integer, p_recording -> 'attempts', coalesce(p_recording -> 'audit', '{}'::jsonb),
              (p_recording ->> 'bestReactionNs')::bigint, (p_recording ->> 'expiresAt')::timestamptz,
              coalesce((p_recording ->> 'quarantineLeft')::smallint, 0), coalesce((p_recording ->> 'held')::boolean, false))
      returning id into v_rec_id;

      -- friend duel: this is the host's NEXT recording after they opened a room -> reserve it for the friend
      -- (a held recording is still a fair recording of this player for a friend who asked for it)
      update public.rooms ro
         set status = 'recorded', recording_id = v_rec_id, host_match_id = m.id, recorded_at = v_now
       where ro.id = (select r2.id from public.rooms r2
                       where r2.host_id = m.player_id and r2.status = 'open' and r2.recording_id is null
                         and r2.expires_at > v_now and r2.created_at <= m.issued_at
                       order by r2.created_at desc limit 1)
      returning ro.code into v_room_code;
    end if;

    -- ---- the match itself
    update public.matches
       set status = 'completed',
           winner = p_result ->> 'winner',
           result = p_result -> 'result',
           player_recording_id = v_rec_id,
           player_mmr_before = (p_rating ->> 'playerBefore')::integer,
           player_mmr_after = (p_rating ->> 'playerAfter')::integer,
           opponent_mmr_before = (p_rating ->> 'opponentBefore')::integer,
           opponent_mmr_after = v_opp_after
     where id = m.id;

    -- ---- profile stats (server-validated matches only; bestNs is null when the match tripped a check)
    update public.profiles p
       set matches_played   = p.matches_played + 1,
           wins             = p.wins + case when coalesce((p_result -> 'stats' ->> 'won')::boolean, false) then 1 else 0 end,
           best_reaction_ns = case when (p_result -> 'stats' ->> 'bestNs') is null then p.best_reaction_ns
                                   when p.best_reaction_ns is null then (p_result -> 'stats' ->> 'bestNs')::bigint
                                   else least(p.best_reaction_ns, (p_result -> 'stats' ->> 'bestNs')::bigint) end,
           lightning_count  = p.lightning_count + greatest(coalesce((p_result -> 'stats' ->> 'lightning')::integer, 0), 0),
           shadow           = p.shadow or coalesce((p_plaus ->> 'shadow')::boolean, false)
     where p.id = m.player_id;

    -- ---- rolling plausibility window + log
    if p_plaus -> 'stats' is not null and jsonb_typeof(p_plaus -> 'stats') = 'object' then
      insert into public.account_stats as a (user_id, shots, won_shots, narrow_wins, matches, trips, updated_at)
      values (m.player_id, coalesce(p_plaus -> 'stats' -> 'shots', '[]'::jsonb),
              coalesce((p_plaus -> 'stats' ->> 'wonShots')::integer, 0), coalesce((p_plaus -> 'stats' ->> 'narrowWins')::integer, 0),
              coalesce((p_plaus -> 'stats' ->> 'matches')::integer, 0),
              coalesce(array(select jsonb_array_elements_text(p_plaus -> 'stats' -> 'trips')), '{}'), v_now)
      on conflict (user_id) do update
         set shots = excluded.shots, won_shots = excluded.won_shots, narrow_wins = excluded.narrow_wins,
             matches = excluded.matches, trips = excluded.trips, updated_at = excluded.updated_at;
    end if;
    for v_log in select * from jsonb_array_elements(coalesce(p_plaus -> 'log', '[]'::jsonb)) loop
      insert into public.plausibility_log (user_id, match_id, check_name, action, detail)
      values (m.player_id, m.id, v_log ->> 'check', v_log ->> 'action', coalesce(v_log -> 'detail', '{}'::jsonb));
    end loop;

    -- ---- the ghost's owner hears about it (best effort)
    if p_result -> 'notification' is not null and jsonb_typeof(p_result -> 'notification') = 'object' then
      begin
        insert into public.notifications (user_id, kind, payload)
        values ((p_result -> 'notification' ->> 'userId')::uuid, 'ghost_result', p_result -> 'notification' -> 'payload');
      exception when foreign_key_violation then
        null;
      end;
    end if;
  exception when sqlstate 'PN409' then
    -- a rating row changed under us: everything in this block is rolled back, the caller re-reads and retries
    return jsonb_build_object('status', 'conflict', 'who', sqlerrm);
  end;

  return jsonb_build_object('status', 'completed', 'recordingId', v_rec_id, 'opponentMmrAfter', v_opp_after,
                            'roomCode', v_room_code);
end $$;

-- ---------------------------------------------------------------------------------------- leaderboards
-- p_board 'ranked' (weekly season p_season, by MMR, at least p_min_matches rated matches that season) or
-- 'best_reaction_hard' (all time, fastest server-validated shot). Flagged and shadow accounts are left off the
-- board; their own `you` row still shows the rank they would have, so nothing gives the flag away.
-- Returns { total, top: [{ rank, displayName, character, value, matches, you }], you: null | { rank, value, matches, eligible } }
create or replace function public.pn_leaderboard(p_board text, p_season integer, p_user uuid, p_limit integer, p_min_matches integer)
returns jsonb
language plpgsql stable security definer set search_path = '' as $$
declare
  v_top jsonb;
  v_total integer;
  v_you jsonb;
  v_mine double precision;
  v_matches integer;
  v_rank integer;
begin
  if p_board = 'ranked' then
    with eligible as (
      select r.user_id, round(r.mmr)::integer as value, r.matches, p.display_name, p.character
        from public.ratings r join public.profiles p on p.id = r.user_id
       where r.season = p_season and r.matches >= p_min_matches and not p.flagged and not p.shadow
    ), ranked as (
      select e.*, rank() over (order by e.value desc) as rk, row_number() over (order by e.value desc, e.matches desc, e.user_id) as rn
        from eligible e
    )
    select coalesce(jsonb_agg(jsonb_build_object('rank', rk, 'displayName', display_name, 'character', character,
                                                 'value', value, 'matches', matches, 'you', user_id = p_user) order by rn), '[]'::jsonb),
           (select count(*) from eligible)
      into v_top, v_total
      from ranked where rn <= p_limit;

    select r.mmr, r.matches into v_mine, v_matches from public.ratings r where r.user_id = p_user and r.season = p_season;
    if found then
      select 1 + count(*) into v_rank
        from public.ratings r join public.profiles p on p.id = r.user_id
       where r.season = p_season and r.matches >= p_min_matches and not p.flagged and not p.shadow
         and round(r.mmr) > round(v_mine);
      v_you := jsonb_build_object('rank', case when v_matches >= p_min_matches then v_rank end,
                                  'value', round(v_mine)::integer, 'matches', v_matches,
                                  'eligible', v_matches >= p_min_matches);
    end if;

  elsif p_board = 'best_reaction_hard' then
    with eligible as (
      select p.id as user_id, p.best_reaction_ns as value, p.matches_played as matches, p.display_name, p.character
        from public.profiles p
       where p.best_reaction_ns is not null and not p.flagged and not p.shadow
    ), ranked as (
      select e.*, rank() over (order by e.value asc) as rk, row_number() over (order by e.value asc, e.user_id) as rn
        from eligible e
    )
    select coalesce(jsonb_agg(jsonb_build_object('rank', rk, 'displayName', display_name, 'character', character,
                                                 'value', value, 'matches', matches, 'you', user_id = p_user) order by rn), '[]'::jsonb),
           (select count(*) from eligible)
      into v_top, v_total
      from ranked where rn <= p_limit;

    select p.best_reaction_ns, p.matches_played into v_mine, v_matches from public.profiles p where p.id = p_user;
    if found and v_mine is not null then
      select 1 + count(*) into v_rank
        from public.profiles p
       where p.best_reaction_ns is not null and not p.flagged and not p.shadow and p.best_reaction_ns < v_mine;
      v_you := jsonb_build_object('rank', v_rank, 'value', v_mine::bigint, 'matches', v_matches, 'eligible', true);
    end if;
  else
    raise exception 'unknown board %', p_board;
  end if;

  return jsonb_build_object('total', v_total, 'top', v_top, 'you', v_you);
end $$;

-- ---------------------------------------------------------------------------------------- grants
revoke all on function public.pn_pick_opponent(uuid, integer, text) from public, anon, authenticated;
revoke all on function public.pn_room_create(uuid, text[], integer) from public, anon, authenticated;
revoke all on function public.pn_room_join(uuid, text) from public, anon, authenticated;
revoke all on function public.pn_room_unclaim(uuid, uuid) from public, anon, authenticated;
revoke all on function public.pn_complete_match(uuid, jsonb, jsonb, jsonb, jsonb) from public, anon, authenticated;
revoke all on function public.pn_leaderboard(text, integer, uuid, integer, integer) from public, anon, authenticated;
grant execute on function public.pn_pick_opponent(uuid, integer, text) to service_role;
grant execute on function public.pn_room_create(uuid, text[], integer) to service_role;
grant execute on function public.pn_room_join(uuid, text) to service_role;
grant execute on function public.pn_room_unclaim(uuid, uuid) to service_role;
grant execute on function public.pn_complete_match(uuid, jsonb, jsonb, jsonb, jsonb) to service_role;
grant execute on function public.pn_leaderboard(text, integer, uuid, integer, integer) to service_role;
