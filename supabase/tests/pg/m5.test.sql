-- M5 SQL tests: pn_complete_match (D1), quarantine + shadow pool, rooms, leaderboards.
-- Run by supabase/tests/pg/run.sh (each DO block raises on failure; ON_ERROR_STOP stops the file).
set client_min_messages = warning;
create schema if not exists t;

create or replace function t.eq(actual anyelement, expected anyelement, what text) returns void language plpgsql as $$
begin
  if actual is distinct from expected then raise exception 'FAIL %: expected %, got %', what, expected, actual; end if;
end $$;

create or replace function t.user(p_name text) returns uuid language plpgsql as $$
declare u uuid;
begin
  insert into auth.users (is_anonymous) values (false) returning id into u;
  update public.profiles set display_name = p_name where id = u;
  return u;
end $$;

-- a match in `submitting` for p_player against p_rec (issued p_ago ago)
create or replace function t.submitting(p_player uuid, p_rec uuid, p_mode text default 'casual', p_ago interval default '1 minute')
returns uuid language plpgsql as $$
declare m uuid; r public.recordings;
begin
  select * into r from public.recordings where id = p_rec;
  insert into public.matches (player_id, opponent_recording_id, opponent_owner_id, opponent_is_house, opponent_snapshot,
                              mode, season, seed, status, issued_at, deadline_at, submitted_at)
  values (p_player, r.id, r.owner_id, r.is_house,
          jsonb_build_object('recordingId', r.id, 'ownerId', r.owner_id, 'isHouse', r.is_house, 'displayName', r.display_name,
                             'character', r.character, 'gun', r.gun, 'mmr', r.mmr, 'attempts', r.attempts),
          p_mode, case when p_mode = 'ranked' then 202643 end, 77, 'submitting', now() - p_ago, now() + interval '5 minutes', now())
  returning id into m;
  return m;
end $$;

create or replace function t.rec_json(p_seed integer default 77, p_quarantine integer default 2, p_held boolean default false)
returns jsonb language sql as $$
  select jsonb_build_object('seed', p_seed, 'mode', 'casual', 'character', 'whiskers', 'gun', 'standard', 'displayName', 'X',
    'mmr', 1200, 'attempts', '[{"roundNumber":1,"kind":"shot","reactionNs":250000000}]'::jsonb, 'audit', '{}'::jsonb,
    'bestReactionNs', 250000000, 'expiresAt', now() + interval '30 days', 'quarantineLeft', p_quarantine, 'held', p_held)
$$;

create or replace function t.result_json(p_owner uuid, p_won boolean, p_best bigint default 231000000)
returns jsonb language sql as $$
  select jsonb_build_object('winner', case when p_won then 'player' else 'opponent' end,
    'result', '{"endReason":"rounds"}'::jsonb,
    'stats', jsonb_build_object('won', p_won, 'bestNs', p_best, 'lightning', 0),
    'notification', case when p_owner is null then null
                         else jsonb_build_object('userId', p_owner, 'payload', '{"message":"hi"}'::jsonb) end)
$$;

create or replace function t.rating_json(p_expect integer, p_owner uuid default null, p_owner_expect integer default -1)
returns jsonb language sql as $$
  select jsonb_build_object('season', 202643, 'playerBefore', 1200, 'playerAfter', 1262, 'opponentBefore', 1200,
    'player', jsonb_build_object('expectMatches', p_expect, 'mmr', 1262.5, 'rd', 290, 'matches', greatest(p_expect, 0) + 1,
                                 'wins', 1, 'losses', 0, 'draws', 0),
    'owner', case when p_owner is null then null else jsonb_build_object('userId', p_owner, 'expectMatches', p_owner_expect,
             'mmr', 1137.5, 'rd', 290, 'matches', greatest(p_owner_expect, 0) + 1, 'wins', 0, 'losses', 1, 'draws', 0, 'after', 1138) end)
$$;

-- --------------------------------------------------------------------------- pn_complete_match: happy path (casual)
do $$
declare
  owner uuid := t.user('Owner');
  me uuid := t.user('Me');
  ghost uuid;
  m uuid;
  res jsonb;
  rec public.recordings;
begin
  insert into public.recordings (owner_id, seed, mode, character, display_name, mmr, attempts, expires_at)
  values (owner, 77, 'casual', 'whiskers', 'Owner', 1200, '[]', now() + interval '1 day') returning id into ghost;
  m := t.submitting(me, ghost);
  res := public.pn_complete_match(m, t.result_json(owner, true), null, t.rec_json(), '{"clean":true,"shadow":false,"stats":null,"log":[]}');
  perform t.eq(res ->> 'status', 'completed', 'casual completes');
  select * into rec from public.recordings where id = (res ->> 'recordingId')::uuid;
  perform t.eq(rec.owner_id, me, 'recording owner');
  perform t.eq(rec.quarantine_left, 2::smallint, 'new recording quarantined for 2 matches');
  perform t.eq(rec.source_match_id, m, 'recording source match');
  perform t.eq((select status from public.matches where id = m), 'completed', 'match completed');
  perform t.eq((select player_recording_id from public.matches where id = m), rec.id, 'match -> recording');
  perform t.eq((select matches_played from public.profiles where id = me), 1, 'stats: played');
  perform t.eq((select wins from public.profiles where id = me), 1, 'stats: wins');
  perform t.eq((select best_reaction_ns from public.profiles where id = me), 231000000::bigint, 'stats: best');
  perform t.eq((select count(*)::int from public.notifications where user_id = owner), 1, 'owner notified');

  -- second call: guarded by status = submitting, nothing written twice
  res := public.pn_complete_match(m, t.result_json(owner, true), null, t.rec_json(), '{"clean":true}');
  perform t.eq(res ->> 'status', 'not_submitting', 'second completion refused');
  perform t.eq(res ->> 'matchStatus', 'completed', 'second completion sees completed');
  perform t.eq((select count(*)::int from public.recordings where owner_id = me), 1, 'one recording only');
  perform t.eq((select matches_played from public.profiles where id = me), 1, 'stats once');
end $$;

-- --------------------------------------------------------------------------- D1 atomicity: rating conflicts write nothing
do $$
declare
  owner uuid := t.user('Owner2');
  me uuid := t.user('Me2');
  ghost uuid;
  m uuid;
  res jsonb;
begin
  insert into public.recordings (owner_id, seed, mode, character, display_name, mmr, attempts, expires_at)
  values (owner, 77, 'ranked', 'whiskers', 'Owner2', 1200, '[]', now() + interval '1 day') returning id into ghost;
  insert into public.ratings (user_id, season, mmr, rd, matches) values (me, 202643, 1200, 300, 4);
  m := t.submitting(me, ghost, 'ranked');

  -- player row moved on (5 matches now, we computed from 3)
  res := public.pn_complete_match(m, t.result_json(owner, true), t.rating_json(3, owner, -1), t.rec_json(), '{"clean":true}');
  perform t.eq(res ->> 'status', 'conflict', 'stale player rating -> conflict');
  perform t.eq(res ->> 'who', 'player', 'conflict on player');
  perform t.eq((select status from public.matches where id = m), 'submitting', 'still submitting');
  perform t.eq((select count(*)::int from public.recordings where owner_id = me), 0, 'no recording written');
  perform t.eq((select matches_played from public.profiles where id = me), 0, 'no stats written');
  perform t.eq((select count(*)::int from public.notifications where user_id = owner), 0, 'no notification written');

  -- owner row conflict after the player row was written: the player write is rolled back too
  insert into public.ratings (user_id, season, mmr, rd, matches) values (owner, 202643, 1200, 300, 9);
  res := public.pn_complete_match(m, t.result_json(owner, true), t.rating_json(4, owner, 2), t.rec_json(), '{"clean":true}');
  perform t.eq(res ->> 'status', 'conflict', 'stale owner rating -> conflict');
  perform t.eq(res ->> 'who', 'owner', 'conflict on owner');
  perform t.eq((select matches from public.ratings where user_id = me and season = 202643), 4, 'player rating rolled back');
  perform t.eq((select mmr from public.ratings where user_id = me and season = 202643), 1200::float8, 'player mmr rolled back');

  -- fresh values: everything lands at once
  res := public.pn_complete_match(m, t.result_json(owner, true), t.rating_json(4, owner, 9), t.rec_json(), '{"clean":true}');
  perform t.eq(res ->> 'status', 'completed', 'ranked completes');
  perform t.eq((select matches from public.ratings where user_id = me and season = 202643), 5, 'player rated');
  perform t.eq((select matches from public.ratings where user_id = owner and season = 202643), 10, 'owner rated');
  perform t.eq((select opponent_mmr_after from public.matches where id = m), 1138, 'owner mmr after stored');
  perform t.eq((select player_mmr_after from public.matches where id = m), 1262, 'player mmr after stored');
end $$;

-- --------------------------------------------------------------------------- D1 atomicity: a failing write rolls back the rating
do $$
declare
  me uuid := t.user('Me3');
  m uuid;
  res jsonb;
  failed boolean := false;
begin
  m := t.submitting(me, '00000000-0000-4000-8000-000000000002', 'ranked');
  begin
    -- attempts must be a JSON array (recordings_attempts_array): the insert fails after the rating insert
    res := public.pn_complete_match(m, t.result_json(null, true), t.rating_json(-1),
             t.rec_json() || '{"attempts":{"not":"an array"}}'::jsonb, '{"clean":true}');
  exception when check_violation then
    failed := true;
  end;
  perform t.eq(failed, true, 'bad recording raises');
  perform t.eq((select count(*)::int from public.ratings where user_id = me), 0, 'rating insert rolled back with it');
  perform t.eq((select status from public.matches where id = m), 'submitting', 'match untouched');
end $$;

-- --------------------------------------------------------------------------- deleted ghost owner does not fail the match
do $$
declare
  owner uuid := t.user('Gone');
  me uuid := t.user('Me4');
  ghost uuid;
  m uuid;
  res jsonb;
begin
  insert into public.recordings (owner_id, seed, mode, character, display_name, mmr, attempts, expires_at)
  values (owner, 77, 'ranked', 'whiskers', 'Gone', 1200, '[]', now() + interval '1 day') returning id into ghost;
  m := t.submitting(me, ghost, 'ranked');
  delete from auth.users where id = owner;   -- cascades the recording, the match keeps its snapshot
  res := public.pn_complete_match(m, t.result_json(owner, false), t.rating_json(-1, owner, -1), null, '{"clean":true}');
  perform t.eq(res ->> 'status', 'completed', 'completes without the owner');
  perform t.eq((select opponent_mmr_after from public.matches where id = m), null::integer, 'no owner rating');
  perform t.eq((select count(*)::int from public.ratings where user_id = me), 1, 'player still rated');
end $$;

-- --------------------------------------------------------------------------- quarantine + held + shadow pool
do $$
declare
  a uuid := t.user('Alice');
  b uuid := t.user('Bob');
  s uuid := t.user('Shady');
  m uuid;
  res jsonb;
  r1 uuid;
  picked public.recordings;
begin
  update public.recordings set expires_at = now() - interval '1 second' where not is_house;   -- earlier blocks' pool
  -- Alice's first recording: quarantined for 2 of her matches
  m := t.submitting(a, '00000000-0000-4000-8000-000000000002');
  res := public.pn_complete_match(m, t.result_json(null, true), null, t.rec_json(), '{"clean":true}');
  r1 := (res ->> 'recordingId')::uuid;
  picked := public.pn_pick_opponent(b, 1200, 'casual');
  perform t.eq(picked.is_house, true, 'quarantined recording not served');

  -- a tripped match: the new recording is held, the old one does not move
  m := t.submitting(a, '00000000-0000-4000-8000-000000000002');
  res := public.pn_complete_match(m, t.result_json(null, true), null, t.rec_json(77, 2, true),
           '{"clean":false,"shadow":false,"log":[{"check":"margin_vs_ghost","action":"hold","detail":{"narrowWins":3}}]}');
  perform t.eq((select quarantine_left from public.recordings where id = r1), 2::smallint, 'tripped match does not release');
  perform t.eq((select count(*)::int from public.plausibility_log where user_id = a and check_name = 'margin_vs_ghost'), 1, 'trip logged');

  -- two clean matches release the first recording (the held one never)
  for i in 1..2 loop
    m := t.submitting(a, '00000000-0000-4000-8000-000000000002');
    res := public.pn_complete_match(m, t.result_json(null, true), null, null, '{"clean":true}');
  end loop;
  perform t.eq((select quarantine_left from public.recordings where id = r1), 0::smallint, 'released after 2 clean matches');
  perform t.eq((select quarantine_left from public.recordings where owner_id = a and held), 2::smallint, 'held stays');
  picked := public.pn_pick_opponent(b, 1200, 'casual');
  perform t.eq(picked.id, r1, 'released recording is served');

  -- shadow: Shady's recordings only meet shadow players, and a shadow player only meets house + shadow
  m := t.submitting(s, '00000000-0000-4000-8000-000000000002');
  res := public.pn_complete_match(m, t.result_json(null, true), null, t.rec_json(77, 0, false),
           '{"clean":false,"shadow":true,"stats":{"shots":[[120000000,1500000000]],"wonShots":1,"narrowWins":1,"matches":1,"trips":["too_fast"]},"log":[]}');
  perform t.eq((select shadow from public.profiles where id = s), true, 'shadow set');
  perform t.eq((select trips from public.account_stats where user_id = s), array['too_fast'], 'rolling stats stored');
  update public.recordings set held = false where owner_id = s;   -- even unheld, shadow recordings stay apart
  picked := public.pn_pick_opponent(t.user('Carol'), 1200, 'casual');
  perform t.eq(picked.id, r1, 'normal players never get shadow recordings');
  picked := public.pn_pick_opponent(s, 1200, 'casual');
  perform t.eq(picked.is_house, true, 'shadow player does not get normal recordings');
  update public.profiles set flagged = true where id = a;   -- a flagged account is in the shadow pool too
  picked := public.pn_pick_opponent(s, 1200, 'casual');
  perform t.eq(picked.owner_id, a, 'shadow player meets flagged recordings');
end $$;

-- --------------------------------------------------------------------------- rooms
do $$
declare
  host uuid := t.user('Host');
  friend uuid := t.user('Friend');
  other uuid := t.user('Other');
  r public.rooms;
  r2 public.rooms;
  j jsonb;
  m uuid;
  res jsonb;
begin
  select * into r from public.pn_room_create(host, array['ABCD', 'ABCE'], 600);
  perform t.eq(r.code, 'ABCD', 'first free code');
  perform t.eq(r.status, 'open', 'room open');
  -- a second room for another host cannot take a live code
  select * into r2 from public.pn_room_create(other, array['ABCD', 'WXYZ'], 600);
  perform t.eq(r2.code, 'WXYZ', 'live code skipped');
  -- all candidates taken: no row
  perform t.eq((select count(*)::int from public.pn_room_create(t.user('X'), array['ABCD', 'WXYZ'], 600)), 0, 'no free code -> no row');

  perform t.eq(public.pn_room_join(host, 'ABCD') ->> 'state', 'own_room', 'host cannot join own room');
  perform t.eq(public.pn_room_join(friend, 'QQQQ') ->> 'state', 'not_found', 'unknown code');
  j := public.pn_room_join(friend, 'ABCD');
  perform t.eq(j ->> 'state', 'waiting', 'before the host recorded');
  perform t.eq(j ->> 'hostName', 'Host', 'host name for the waiting card');
  perform t.eq(public.pn_room_join(other, 'ABCD') ->> 'state', 'taken', 'friend slot is held');

  -- a match issued BEFORE the room was created is not "the next recording"
  m := t.submitting(host, '00000000-0000-4000-8000-000000000002', 'casual', '1 hour');
  res := public.pn_complete_match(m, t.result_json(null, true), null, t.rec_json(), '{"clean":true}');
  perform t.eq(res ->> 'roomCode', null, 'older match does not fill the room');
  perform t.eq(public.pn_room_join(friend, 'ABCD') ->> 'state', 'waiting', 'still waiting');

  -- the host's next match fills it (even though the recording is quarantined for strangers)
  update public.rooms set created_at = now() - interval '2 minutes' where id = r.id;
  m := t.submitting(host, '00000000-0000-4000-8000-000000000002', 'casual', '1 minute');
  res := public.pn_complete_match(m, t.result_json(null, true), null, t.rec_json(4242), '{"clean":true}');
  perform t.eq(res ->> 'roomCode', 'ABCD', 'next recording reserved');
  perform t.eq((select status from public.rooms where id = r.id), 'recorded', 'room recorded');

  j := public.pn_room_join(friend, 'abcd');
  perform t.eq(j ->> 'state', 'not_found', 'codes are upper case (room-join normalises)');
  j := public.pn_room_join(friend, 'ABCD');
  perform t.eq(j ->> 'state', 'ready', 'friend gets the recording');
  perform t.eq((j -> 'recording' ->> 'seed')::int, 4242, 'the host''s recording');
  perform t.eq((select status from public.rooms where id = r.id), 'used', 'room used');
  perform t.eq(public.pn_room_join(friend, 'ABCD') ->> 'state', 'rejoin', 'same friend again -> rejoin');
  perform t.eq(public.pn_room_join(other, 'ABCD') ->> 'state', 'used', 'anybody else -> used');

  -- unclaim gives it back while no match was attached
  perform public.pn_room_unclaim(r.id, friend);
  perform t.eq((select status from public.rooms where id = r.id), 'recorded', 'unclaimed');

  -- one open room per host; expiry frees the code
  select * into r2 from public.pn_room_create(other, array['MNOP'], 600);
  perform t.eq((select status from public.rooms where code = 'WXYZ'), 'cancelled', 'host''s previous open room cancelled');
  update public.rooms set expires_at = now() - interval '1 second' where id = r2.id;
  perform t.eq(public.pn_room_join(friend, 'MNOP') ->> 'state', 'expired', 'expired room');
  select * into r2 from public.pn_room_create(host, array['MNOP'], 600);
  perform t.eq(r2.code, 'MNOP', 'expired code is free again');
end $$;

-- --------------------------------------------------------------------------- leaderboards
do $$
declare
  u uuid[] := array[]::uuid[];
  me uuid;
  sh uuid;
  lb jsonb;
  i int;
begin
  delete from public.ratings where season = 202699;
  for i in 1..60 loop
    u := u || t.user('P' || i);
    insert into public.ratings (user_id, season, mmr, rd, matches) values (u[i], 202699, 1000 + i * 10, 100, 5);
  end loop;
  insert into public.ratings (user_id, season, mmr, rd, matches) values (u[60], 202698, 9999, 100, 50);  -- other week
  me := t.user('Newbie');
  insert into public.ratings (user_id, season, mmr, rd, matches) values (me, 202699, 2000, 300, 2);   -- not enough matches
  sh := t.user('Shadow');
  update public.profiles set shadow = true where id = sh;
  insert into public.ratings (user_id, season, mmr, rd, matches) values (sh, 202699, 1305, 100, 9);

  lb := public.pn_leaderboard('ranked', 202699, u[11], 50, 5);
  perform t.eq((lb ->> 'total')::int, 60, 'total eligible');
  perform t.eq(jsonb_array_length(lb -> 'top'), 50, 'top 50');
  perform t.eq(lb -> 'top' -> 0 ->> 'displayName', 'P60', 'best first');
  perform t.eq((lb -> 'top' -> 0 ->> 'value')::int, 1600, 'value is rounded mmr');
  perform t.eq((lb -> 'top' -> 49 ->> 'rank')::int, 50, 'rank 50');
  perform t.eq((lb -> 'you' ->> 'rank')::int, 50, 'caller rank (P11 = 1110 -> 50th)');
  perform t.eq((lb -> 'top' -> 49 ->> 'you')::boolean, true, 'caller marked in the list');

  lb := public.pn_leaderboard('ranked', 202699, me, 50, 5);
  perform t.eq((lb -> 'you' ->> 'eligible')::boolean, false, 'too few matches: not ranked');
  perform t.eq(lb -> 'you' -> 'rank', 'null'::jsonb, 'no rank yet');
  perform t.eq((lb -> 'you' ->> 'matches')::int, 2, 'shows the matches so far');

  lb := public.pn_leaderboard('ranked', 202699, sh, 50, 5);
  perform t.eq((lb -> 'you' ->> 'rank')::int, 31, 'shadow sees the rank it would have (1305 -> after 30 better)');
  perform t.eq((select count(*)::int from jsonb_array_elements(lb -> 'top') e where e ->> 'displayName' = 'Shadow'), 0, 'shadow not listed');

  -- ties share a rank
  update public.ratings set mmr = 1600 where user_id = u[59] and season = 202699;
  lb := public.pn_leaderboard('ranked', 202699, u[1], 50, 5);
  perform t.eq((lb -> 'top' -> 1 ->> 'rank')::int, 1, 'tie shares rank 1');
  perform t.eq((lb -> 'top' -> 2 ->> 'rank')::int, 3, 'next rank skips');

  -- fastest shot board
  update public.profiles set best_reaction_ns = null;
  update public.profiles set best_reaction_ns = 180000000 where id = u[1];
  update public.profiles set best_reaction_ns = 175000000 where id = u[2];
  update public.profiles set best_reaction_ns = 150000000 where id = sh;   -- shadow: hidden
  update public.profiles set best_reaction_ns = 200000000 where id = me;
  lb := public.pn_leaderboard('best_reaction_hard', null, me, 50, 0);
  perform t.eq((lb ->> 'total')::int, 3, 'fastest: eligible');
  perform t.eq(lb -> 'top' -> 0 ->> 'displayName', 'P2', 'fastest first');
  perform t.eq((lb -> 'top' -> 0 ->> 'value')::bigint, 175000000::bigint, 'value in ns');
  perform t.eq((lb -> 'you' ->> 'rank')::int, 3, 'caller third');
  lb := public.pn_leaderboard('best_reaction_hard', null, t.user('Nobody'), 50, 0);
  perform t.eq(lb -> 'you', 'null'::jsonb, 'no shot yet: no row');
end $$;

drop schema t cascade;
select 'm5 sql tests passed' as result;
