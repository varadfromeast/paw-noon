-- Live duels SQL tests (migrations/0070_live.sql): lobbies, codes, quick pairing, CAS save, the one-transaction
-- finish, the sweep, and RLS (a player can never read or act on somebody else's live match, nor read the secret
-- state of their own). Run by supabase/tests/pg/run.sh.
set client_min_messages = warning;
create schema if not exists t;
grant usage on schema t to authenticated, anon;

create or replace function t.eq(actual anyelement, expected anyelement, what text) returns void language plpgsql as $$
begin
  if actual is distinct from expected then raise exception 'FAIL %: expected %, got %', what, expected, actual; end if;
end $$;
grant execute on function t.eq(anyelement, anyelement, text) to authenticated, anon;

create or replace function t.user(p_name text) returns uuid language plpgsql as $$
declare u uuid;
begin
  insert into auth.users (is_anonymous) values (false) returning id into u;
  update public.profiles set display_name = upper(p_name) where id = u;
  return u;
end $$;

-- a live.ts-shaped state (only the fields SQL reads matter: phase, result, rounds, maxAttempts, hp, roundWins)
create or replace function t.live_state(p_phase text, p_result jsonb default null) returns jsonb language sql as $$
  select jsonb_build_object('v', 1, 'kind', 'friend', 'mode', 'casual', 'seed', 4242, 'rounds', 3, 'maxAttempts', 9,
    'phase', p_phase, 'deadlineMs', 0, 'attempt', 0, 'round', null, 'history', '[]'::jsonb,
    'hp', '[100,100]'::jsonb, 'roundWins', '[0,0]'::jsonb, 'result', p_result)
$$;

create or replace function t.finish_player(p_user uuid, p_won boolean, p_rating jsonb, p_best bigint) returns jsonb language sql as $$
  select jsonb_build_object(
    'userId', p_user,
    'rating', p_rating,
    'match', jsonb_build_object('winner', case when p_won then 'player' else 'opponent' end,
               'result', '{"live":true,"endReason":"rounds"}'::jsonb,
               'opponentSnapshot', '{"ownerId":null,"isHouse":false,"live":true,"displayName":"X","character":"whiskers","gun":"standard","mmr":1200,"attempts":[]}'::jsonb,
               'mmrBefore', 1200, 'mmrAfter', case when p_won then 1262 else 1138 end, 'opponentMmrBefore', 1200,
               'opponentMmrAfter', case when p_won then 1138 else 1262 end),
    'recording', jsonb_build_object('seed', 4242, 'mode', 'ranked', 'character', 'whiskers', 'gun', 'standard', 'displayName', 'X',
               'mmr', 1200, 'attempts', '[{"roundNumber":1,"kind":"shot","reactionNs":250000000}]'::jsonb, 'audit', '{"live":true}'::jsonb,
               'bestReactionNs', p_best, 'expiresAt', now() + interval '30 days', 'quarantineLeft', 2, 'held', false),
    'stats', jsonb_build_object('won', p_won, 'bestNs', p_best, 'lightning', 0),
    'plaus', jsonb_build_object('clean', true, 'shadow', false,
               'stats', jsonb_build_object('shots', jsonb_build_array(jsonb_build_array(p_best, 2000000000)), 'wonShots', 0,
                                           'narrowWins', 0, 'matches', 1, 'trips', '[]'::jsonb),
               'log', case when p_won then '[]'::jsonb else '[{"check":"live_rtt","action":"log","detail":{"flaggedRounds":1}}]'::jsonb end))
$$;

create or replace function t.rating(p_expect integer, p_won boolean) returns jsonb language sql as $$
  select jsonb_build_object('expectMatches', p_expect, 'mmr', case when p_won then 1262.5 else 1137.5 end, 'rd', 290,
                            'matches', greatest(p_expect, 0) + 1, 'wins', case when p_won then 1 else 0 end,
                            'losses', case when p_won then 0 else 1 end, 'draws', 0)
$$;

-- a playing (joined) friend match between p_host and p_guest
create or replace function t.playing(p_host uuid, p_guest uuid, p_mode text default 'casual') returns uuid language plpgsql as $$
declare res jsonb; mid uuid;
begin
  res := public.pn_live_create(p_host, 'friend', p_mode, array['TQZX', 'TQZY', 'TQZW', 'TQZV'], 4242, t.live_state('lobby'), 600000, 1200, false,
                               case when p_mode = 'ranked' then 202643 end);
  mid := (res ->> 'id')::uuid;
  res := public.pn_live_join(p_guest, res ->> 'code');
  res := public.pn_live_save(mid, (res ->> 'version')::integer, t.live_state('ready'), '[]'::jsonb);
  return mid;
end $$;

-- --------------------------------------------------------------------------- friend lobby: create, codes, join
do $$
declare
  a uuid := t.user('LIVE HOST');
  b uuid := t.user('LIVE GUEST');
  c uuid := t.user('LIVE THIRD');
  r1 jsonb;
  r2 jsonb;
  j jsonb;
begin
  r1 := public.pn_live_create(a, 'friend', 'casual', array['HOWL', 'DUST'], 1, t.live_state('lobby'), 600000, 1200, false, null);
  perform t.eq(r1 ->> 'status', 'created', 'friend lobby created');
  perform t.eq(r1 ->> 'code', 'HOWL', 'first free code');
  -- the same code is taken while that lobby waits: another host gets the next candidate
  r2 := public.pn_live_create(c, 'friend', 'casual', array['HOWL', 'DUST'], 2, t.live_state('lobby'), 600000, 1200, false, null);
  perform t.eq(r2 ->> 'code', 'DUST', 'live code unique among waiting lobbies');
  perform t.eq(public.pn_live_create(b, 'friend', 'casual', array['HOWL', 'DUST'], 3, t.live_state('lobby'), 600000, 1200, false, null) ->> 'status',
               'busy', 'every candidate taken -> busy');
  -- c opens another lobby: its first one is cancelled (one waiting lobby per host), and DUST is free again
  r2 := public.pn_live_create(c, 'friend', 'casual', array['DUST'], 4, t.live_state('lobby'), 600000, 1200, false, null);
  perform t.eq(r2 ->> 'code', 'DUST', 'own old lobby cancelled, code recycled');
  perform t.eq((select count(*)::int from public.live_matches where host_id = c and status = 'cancelled'), 1, 'old lobby cancelled');
  perform t.eq((select state ->> 'phase' from public.live_matches where host_id = c and status = 'cancelled'), 'cancelled', 'cancelled state shape');

  perform t.eq(public.pn_live_join(a, 'HOWL') ->> 'state', 'own_room', 'host cannot join own lobby');
  perform t.eq(public.pn_live_join(b, 'ZZZZ') ->> 'state', 'not_found', 'unknown code');
  j := public.pn_live_join(b, 'HOWL');
  perform t.eq(j ->> 'state', 'joined', 'guest joins');
  perform t.eq((j ->> 'version')::int, 0, 'join returns the version to CAS on');
  perform t.eq(public.pn_live_join(c, 'HOWL') ->> 'state', 'taken', 'a third player cannot take the slot');
  perform t.eq(public.pn_live_join(b, 'HOWL') ->> 'state', 'joined', 'guest re-joining the waiting lobby is idempotent');
  -- the function applies joinLive and saves: playing
  perform t.eq(public.pn_live_save((j ->> 'id')::uuid, 0, t.live_state('ready'), '[]'::jsonb) ->> 'status', 'ok', 'join saved');
  perform t.eq((select status from public.live_matches where id = (j ->> 'id')::uuid), 'playing', 'status follows the phase');
  perform t.eq(public.pn_live_join(b, 'HOWL') ->> 'state', 'rejoin', 'guest reconnecting by code gets the match back');
  perform t.eq(public.pn_live_join(c, 'HOWL') ->> 'state', 'used', 'others see it used');

  -- an expired lobby
  update public.live_matches set expires_at = now() - interval '1 second' where code = 'DUST' and status = 'lobby';
  perform t.eq(public.pn_live_join(b, 'DUST') ->> 'state', 'expired', 'expired lobby');
  perform t.eq((select status from public.live_matches where code = 'DUST' order by created_at desc limit 1), 'cancelled', 'expired lobby closed');
end $$;

-- --------------------------------------------------------------------------- quick pairing
do $$
declare
  a uuid := t.user('QUICK A');
  b uuid := t.user('QUICK B');
  c uuid := t.user('QUICK C');
  d uuid := t.user('QUICK D');
  e uuid := t.user('QUICK E');
  r jsonb;
  lobby uuid;
begin
  perform t.eq(public.pn_live_quick(a, 'casual', 1200, false, 150, 20) ->> 'status', 'none', 'nobody waiting');
  r := public.pn_live_create(a, 'quick', 'casual', null, 9, t.live_state('lobby'), 30000, 1200, false, null);
  lobby := (r ->> 'id')::uuid;
  perform t.eq(r ->> 'code', null::text, 'quick lobby has no code');
  perform t.eq(public.pn_live_quick(a, 'casual', 1200, false, 150, 20) ->> 'status', 'none', 'never paired with yourself');
  perform t.eq(public.pn_live_quick(e, 'ranked', 1200, false, 150, 20) ->> 'status', 'none', 'other mode: no pairing');
  perform t.eq(public.pn_live_quick(d, 'casual', 1200, true, 150, 20) ->> 'status', 'none', 'a shadow player never meets a clean lobby');
  perform t.eq(public.pn_live_quick(c, 'casual', 1700, false, 150, 20) ->> 'status', 'none', 'too far in MMR for a fresh lobby');
  -- the band widens with the lobby's age: 30 s old -> 150 + 600
  update public.live_matches set created_at = now() - interval '30 seconds' where id = lobby;
  r := public.pn_live_quick(b, 'casual', 1650, false, 150, 20);
  perform t.eq(r ->> 'status', 'joined', 'paired once the band widened');
  perform t.eq((r ->> 'id')::uuid, lobby, 'paired into the waiting lobby');
  perform t.eq((select guest_id from public.live_matches where id = lobby), b, 'guest recorded');
  perform t.eq(public.pn_live_quick(c, 'casual', 1650, false, 150, 20) ->> 'status', 'none', 'a claimed lobby is not offered again');
end $$;

-- --------------------------------------------------------------------------- CAS save
do $$
declare
  a uuid := t.user('CAS A');
  b uuid := t.user('CAS B');
  mid uuid := t.playing(a, b);
  v integer;
  r jsonb;
  failed boolean := false;
begin
  v := (select l.version from public.live_matches l where l.id = mid);
  r := public.pn_live_save(mid, v, t.live_state('countdown'), '[]'::jsonb);
  perform t.eq(r ->> 'status', 'ok', 'save on the current version');
  perform t.eq((r ->> 'version')::int, v + 1, 'version bumped');
  r := public.pn_live_save(mid, v, t.live_state('draw'), '[]'::jsonb);
  perform t.eq(r ->> 'status', 'conflict', 'stale version -> conflict');
  perform t.eq((select state ->> 'phase' from public.live_matches l where l.id = mid), 'countdown', 'conflict wrote nothing');
  -- resolved attempts land in live_rounds (idempotent per attempt)
  r := public.pn_live_save(mid, v + 1, t.live_state('ready'),
         '[{"attempt":0,"roundId":1,"roundNumber":1,"holdNs":1746005752,"startAtMs":1,"cueSentAtMs":2,"resolvedAtMs":3,"reports":[null,null],"inputs":[],"flags":[["excess_latency"],[]],"view":{"void":false}}]');
  perform t.eq((select count(*)::int from public.live_rounds where live_match_id = mid), 1, 'round row written');
  begin
    perform public.pn_live_save(mid, v + 2, t.live_state('finished', '{"winner":0,"endReason":"rounds"}'), '[]'::jsonb);
  exception when others then failed := true;
  end;
  perform t.eq(failed, true, 'a finished result cannot bypass pn_live_finish');
  r := public.pn_live_save(mid, v + 2, t.live_state('finished', '{"winner":-1,"endReason":"abandoned"}'), '[]'::jsonb);
  perform t.eq((select status from public.live_matches l where l.id = mid), 'abandoned', 'abandoned through save');
  perform t.eq(public.pn_live_save(mid, v + 3, t.live_state('ready'), '[]'::jsonb) ->> 'status', 'conflict', 'a closed match never changes again');
end $$;

-- --------------------------------------------------------------------------- finish: one transaction
do $$
declare
  a uuid := t.user('FIN A');
  b uuid := t.user('FIN B');
  mid uuid := t.playing(a, b, 'ranked');
  v integer;
  r jsonb;
  fin jsonb;
  done jsonb := t.live_state('finished', '{"winner":0,"endReason":"rounds","forfeitedBy":-1,"hp":[40,0],"roundWins":[2,1]}');
begin
  v := (select l.version from public.live_matches l where l.id = mid);
  insert into public.ratings (user_id, season, mmr, rd, matches) values (b, 202643, 1200, 300, 4);

  -- B's rating row moved on (we computed from 3 matches, it has 4): conflict, NOTHING written for either player
  fin := jsonb_build_object('status', 'finished', 'endReason', 'rounds', 'winnerSlot', 0, 'season', 202643,
           'players', jsonb_build_array(t.finish_player(a, true, t.rating(-1, true), 231000000),
                                        t.finish_player(b, false, t.rating(3, false), 262000000)));
  r := public.pn_live_finish(mid, v, done, '[]'::jsonb, fin);
  perform t.eq(r ->> 'status', 'conflict', 'stale rating -> conflict');
  perform t.eq((select count(*)::int from public.ratings where user_id = a), 0, 'A rating not written');
  perform t.eq((select count(*)::int from public.matches where live_match_id = mid), 0, 'no match rows');
  perform t.eq((select count(*)::int from public.recordings where owner_id in (a, b)), 0, 'no recordings');
  perform t.eq((select matches_played from public.profiles where id = a), 0, 'no stats');
  perform t.eq((select status from public.live_matches l where l.id = mid), 'playing', 'still playing');
  perform t.eq((select l.version from public.live_matches l where l.id = mid), v, 'version unchanged');

  -- wrong version: conflict too
  perform t.eq(public.pn_live_finish(mid, v + 7, done, '[]'::jsonb, fin) ->> 'status', 'conflict', 'stale state version -> conflict');

  -- recomputed from fresh rows: everything at once
  fin := jsonb_set(fin, '{players,1}', t.finish_player(b, false, t.rating(4, false), 262000000));
  r := public.pn_live_finish(mid, v, done, '[]'::jsonb, fin);
  perform t.eq(r ->> 'status', 'completed', 'finish completes');
  perform t.eq((select status from public.live_matches l where l.id = mid), 'finished', 'live finished');
  perform t.eq((select winner_slot from public.live_matches l where l.id = mid), 0::smallint, 'winner slot');
  perform t.eq((select count(*)::int from public.matches where live_match_id = mid and status = 'completed'), 2, 'two match rows');
  perform t.eq((select winner from public.matches where live_match_id = mid and player_id = a), 'player', 'A won');
  perform t.eq((select winner from public.matches where live_match_id = mid and player_id = b), 'opponent', 'B lost');
  perform t.eq((select opponent_owner_id from public.matches where live_match_id = mid and player_id = a), b, 'A faced B');
  perform t.eq((select opponent_recording_id from public.matches where live_match_id = mid and player_id = a),
               (select id from public.recordings where owner_id = b), 'A''s opponent = B''s pooled recording (never served to A again)');
  perform t.eq((select (opponent_snapshot ->> 'recordingId')::uuid from public.matches where live_match_id = mid and player_id = b),
               (select id from public.recordings where owner_id = a), 'snapshot carries the recording id');
  perform t.eq((select count(*)::int from public.recordings where owner_id in (a, b) and quarantine_left = 2), 2, 'both pooled, quarantined');
  perform t.eq((select source_match_id from public.recordings where owner_id = a),
               (select m.id from public.matches m where m.live_match_id = mid and m.player_id = a), 'recording -> its match');
  perform t.eq((select host_match_id from public.live_matches l where l.id = mid),
               (select m.id from public.matches m where m.live_match_id = mid and m.player_id = a), 'live -> host match');
  perform t.eq((select mmr from public.ratings where user_id = a and season = 202643), 1262.5::float8, 'A rated');
  perform t.eq((select matches from public.ratings where user_id = b and season = 202643), 5, 'B rated once');
  perform t.eq((select wins from public.profiles where id = a), 1, 'A stats');
  perform t.eq((select best_reaction_ns from public.profiles where id = b), 262000000::bigint, 'B best');
  perform t.eq((select count(*)::int from public.account_stats where user_id in (a, b)), 2, 'rolling windows');
  perform t.eq((select count(*)::int from public.plausibility_log where user_id = b and check_name = 'live_rtt'), 1, 'live flag logged');
  -- once finished, nothing moves it again
  perform t.eq(public.pn_live_finish(mid, v + 1, done, '[]'::jsonb, fin) ->> 'status', 'not_playing', 'finish is once');
  perform t.eq((select count(*)::int from public.matches where live_match_id = mid), 2, 'still two rows');
end $$;

-- --------------------------------------------------------------------------- sweep
do $$
declare
  a uuid := t.user('SWEEP A');
  b uuid := t.user('SWEEP B');
  c uuid := t.user('SWEEP C');
  lobby uuid;
  idle uuid;
  fresh uuid;
begin
  idle := t.playing(b, c);
  update public.live_matches set updated_at = now() - interval '20 minutes' where id = idle;
  fresh := t.playing(c, a);
  lobby := (public.pn_live_create(a, 'quick', 'casual', null, 5, t.live_state('lobby'), 30000, 1200, false, null) ->> 'id')::uuid;
  update public.live_matches set expires_at = now() - interval '1 second' where id = lobby;
  perform t.eq(public.pn_live_sweep(600), 2, 'sweep closed the two stale rows');
  perform t.eq((select status from public.live_matches where id = lobby), 'cancelled', 'expired lobby cancelled');
  perform t.eq((select state -> 'result' ->> 'endReason' from public.live_matches where id = lobby), 'lobby_expired', 'reason in state');
  perform t.eq((select status from public.live_matches where id = idle), 'abandoned', 'idle match abandoned');
  perform t.eq((select state ->> 'phase' from public.live_matches where id = idle), 'finished', 'abandoned state shape');
  perform t.eq((select status from public.live_matches where id = fresh), 'playing', 'an active match is left alone');
  perform t.eq((select count(*)::int from public.matches where live_match_id = idle), 0, 'nothing rated for an abandoned match');
end $$;

-- --------------------------------------------------------------------------- RLS: members only, never the secret state
do $$
declare
  a uuid := t.user('RLS A');
  b uuid := t.user('RLS B');
  c uuid := t.user('RLS C');
  mid uuid := t.playing(a, b);
begin
  perform public.pn_live_insert_rounds(mid,
    '[{"attempt":0,"roundId":1,"roundNumber":1,"holdNs":1,"startAtMs":1,"cueSentAtMs":2,"resolvedAtMs":3,"reports":[null,null],"inputs":[],"flags":[[],[]],"view":{"void":true}}]');
  perform set_config('t.a', a::text, false);
  perform set_config('t.b', b::text, false);
  perform set_config('t.c', c::text, false);
  perform set_config('t.id', mid::text, false);
end $$;

-- as A (host)
begin;
select set_config('request.jwt.claim.sub', current_setting('t.a'), true);
set local role authenticated;
do $$
declare
  mid uuid := current_setting('t.id')::uuid;
  denied boolean;
begin
  perform t.eq((select count(*)::int from public.live_matches where live_matches.id = mid), 1, 'host sees own match');
  perform t.eq((select count(*)::int from public.live_matches), 1, 'host sees no foreign match (others exist)');
  perform t.eq((select count(*)::int from public.live_rounds), 1, 'host sees no foreign rounds');
  perform t.eq((select count(*)::int from public.live_rounds where live_match_id = mid), 1, 'host sees own rounds');
  perform t.eq(public.pn_live_topic_member('live:' || mid), true, 'host may listen on the channel');
  perform t.eq(public.pn_live_topic_member('live:not-a-uuid'), false, 'bad topic');
  perform t.eq(public.pn_live_topic_member('room:' || mid), false, 'other topic shapes');
  -- the state (seed, cue time), the version and the auth ids are never readable
  denied := false; begin perform state from public.live_matches limit 1; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'state column denied');
  denied := false; begin perform seed from public.live_matches limit 1; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'seed column denied');
  denied := false; begin perform guest_id from public.live_matches limit 1; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'opponent auth id denied');
  denied := false; begin perform flags from public.live_rounds limit 1; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'soft flags denied');
  denied := false; begin perform reports from public.live_rounds limit 1; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'raw reports denied');
  -- no writes, no service functions
  denied := false; begin update public.live_matches set status = 'finished'; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'update denied');
  denied := false; begin delete from public.live_matches; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'delete denied');
  denied := false; begin insert into public.live_rounds (live_match_id, attempt, round_id, round_number, hold_ns, start_at_ms, resolved_at_ms, reports, inputs, flags, view) values (mid, 5, 6, 1, 1, 1, 1, '[]', '[]', '[]', '{}'); exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'insert round denied');
  denied := false; begin perform public.pn_live_save(mid, 0, '{}'::jsonb, '[]'::jsonb); exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'pn_live_save denied');
  denied := false; begin perform public.pn_live_finish(mid, 0, '{}'::jsonb, '[]'::jsonb, '{}'::jsonb); exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'pn_live_finish denied');
  denied := false; begin perform public.pn_live_join(current_setting('t.a')::uuid, 'TQZX'); exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'pn_live_join denied');
end $$;
rollback;

-- as C (a stranger)
begin;
select set_config('request.jwt.claim.sub', current_setting('t.c'), true);
set local role authenticated;
do $$
declare mid uuid := current_setting('t.id')::uuid;
begin
  perform t.eq((select count(*)::int from public.live_matches where live_matches.id = mid), 0, 'stranger cannot see the match');
  perform t.eq((select count(*)::int from public.live_rounds where live_match_id = mid), 0, 'stranger cannot see its rounds');
  perform t.eq(public.pn_live_is_member(mid), false, 'stranger is not a member');
  perform t.eq(public.pn_live_topic_member('live:' || mid), false, 'stranger may not listen on the channel');
end $$;
rollback;

-- as B (guest) and as nobody (anon)
begin;
select set_config('request.jwt.claim.sub', current_setting('t.b'), true);
set local role authenticated;
do $$
declare mid uuid := current_setting('t.id')::uuid;
begin
  perform t.eq((select count(*)::int from public.live_matches where live_matches.id = mid), 1, 'guest sees the match');
  perform t.eq((select status from public.live_matches where live_matches.id = mid), 'playing', 'guest reads the status');
end $$;
rollback;

begin;
set local role anon;
do $$
declare denied boolean := false;
begin
  begin perform 1 from public.live_matches; exception when insufficient_privilege then denied := true; end;
  perform t.eq(denied, true, 'anon has no access');
end $$;
rollback;

select 'live sql tests passed';
