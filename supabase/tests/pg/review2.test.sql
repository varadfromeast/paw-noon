-- Review pass 2 SQL tests (migrations/0061_review2.sql): old rows vs NOT VALID checks, character ids, rate limits,
-- purchase transfers. Run by supabase/tests/pg/run.sh.
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
  update public.profiles set display_name = upper(p_name) where id = u;
  return u;
end $$;

-- --------------------------------------------------------------------------- every profile check is validated
do $$
declare c record;
begin
  for c in select conname, convalidated from pg_constraint
            where conrelid = 'public.profiles'::regclass and contype = 'c' loop
    perform t.eq(c.convalidated, true, 'constraint validated: ' || c.conname);
  end loop;
end $$;

-- --------------------------------------------------------------------------- the bug: an old row blocked every UPDATE
-- Rebuild the pre-0061 situation inside a transaction that is rolled back: a profile named before 0060 (lower case,
-- 1-2 chars or too long), then the 0060 check added NOT VALID, as on a database that had rows before 0060.
begin;
do $$
declare
  u uuid;
  failed boolean := false;
  n integer;
begin
  alter table public.profiles drop constraint profiles_display_name_shape;
  alter table public.profiles drop constraint profiles_character_known;
  insert into auth.users (is_anonymous) values (true) returning id into u;
  update public.profiles set display_name = 'billy  the kid', character = 'blackpaw' where id = u;
  insert into public.recordings (owner_id, seed, mode, character, display_name, mmr, attempts, expires_at)
  values (u, 1, 'casual', 'whiskers', 'billy  the kid', 1200, '[]', now() + interval '1 day');
  alter table public.profiles add constraint profiles_display_name_shape
    check (public.pn_display_name_ok(display_name)) not valid;

  -- what every validated match did to this player before the fix: the stats update re-checks the old name
  begin
    perform public.pn_record_match_stats(u, true, 230000000, 0);
  exception when check_violation then failed := true;
  end;
  perform t.eq(failed, true, 'old name blocks an unrelated UPDATE under a NOT VALID check');

  n := public.pn_repair_profiles();
  perform t.eq(n >= 2, true, 'repair touched the name and the character');
  perform t.eq((select display_name from public.profiles where id = u), 'BILLY THE KID', 'name normalised');
  perform t.eq((select character from public.profiles where id = u), 'pawtruth', 'blackpaw -> pawtruth');
  perform t.eq((select display_name from public.recordings where owner_id = u), 'BILLY THE KID', 'ghost name follows');
  alter table public.profiles validate constraint profiles_display_name_shape;
  perform public.pn_record_match_stats(u, true, 230000000, 0);
  perform t.eq((select matches_played from public.profiles where id = u), 1, 'stats update works after the repair');
  perform t.eq(public.pn_repair_profiles(), 0, 'repair is idempotent');
end $$;
rollback;

-- --------------------------------------------------------------------------- name repair rules
do $$
begin
  perform t.eq(public.pn_display_name_repair('BILLY'), 'BILLY', 'valid name kept');
  perform t.eq(public.pn_display_name_repair('Stranger #0042'), 'Stranger #0042', 'guest name kept');
  perform t.eq(public.pn_display_name_repair('  slim   jim '), 'SLIM JIM', 'normalised');
  perform t.eq(public.pn_display_name_repair('old tom') ~ '^Stranger #[0-9]{4}$', true, 'reserved -> guest name');
  perform t.eq(public.pn_display_name_repair('admin cat') ~ '^Stranger #[0-9]{4}$', true, 'reserved prefix -> guest name');
  perform t.eq(public.pn_display_name_repair('x') ~ '^Stranger #[0-9]{4}$', true, 'too short -> guest name');
  perform t.eq(public.pn_display_name_repair('THE FASTEST GUN ALIVE') ~ '^Stranger #[0-9]{4}$', true, 'too long -> guest name');
  perform t.eq(public.pn_display_name_repair(E'evil\nname'), 'EVIL NAME', 'a newline is just white space');
  perform t.eq(public.pn_display_name_repair(E'evil\u0007') ~ '^Stranger #[0-9]{4}$', true, 'control chars -> guest name');
  perform t.eq(public.pn_display_name_repair('café') ~ '^Stranger #[0-9]{4}$', true, 'non-ASCII -> guest name');
end $$;

-- --------------------------------------------------------------------------- character ids
do $$
declare
  u uuid := t.user('Chars');
  failed boolean := false;
begin
  perform t.eq((select count(*)::int from public.recordings where character = 'blackpaw'), 0, 'no blackpaw recordings left');
  perform t.eq((select character from public.recordings where id = '00000000-0000-4000-8000-000000000002'), 'pawtruth', 'house ghost is pawtruth');
  update public.profiles set character = 'pawtruth' where id = u;
  begin
    update public.profiles set character = 'tiger' where id = u;
  exception when check_violation then failed := true;
  end;
  perform t.eq(failed, true, 'unknown character refused');
end $$;

-- --------------------------------------------------------------------------- rate limits
do $$
declare
  u uuid := t.user('Rate');
  v uuid := t.user('Rate2');
  i integer;
begin
  for i in 1..3 loop
    perform t.eq(public.pn_rate_take(u, 'room_join', 60, 3), true, 'within the limit ' || i);
  end loop;
  perform t.eq(public.pn_rate_take(u, 'room_join', 60, 3), false, 'fourth call refused');
  perform t.eq(public.pn_rate_count(u, 'room_join', 60), 3, 'a refused call is not recorded');
  perform t.eq(public.pn_rate_take(v, 'room_join', 60, 3), true, 'per user');
  perform t.eq(public.pn_rate_take(u, 'profile_update', 60, 3), true, 'per kind');
  -- an expired window frees the slots (and prunes the old rows)
  update public.rate_events set at = now() - interval '2 minutes' where user_id = u and kind = 'room_join';
  perform t.eq(public.pn_rate_take(u, 'room_join', 60, 3), true, 'window expired');
  perform t.eq((select count(*)::int from public.rate_events where user_id = u and kind = 'room_join'), 1, 'old rows pruned');
  perform public.pn_rate_note(u, 'room_miss');
  perform public.pn_rate_note(u, 'room_miss');
  perform t.eq(public.pn_rate_count(u, 'room_miss', 600), 2, 'failures noted');
end $$;

-- clients cannot touch the table or the functions
do $$
begin
  perform t.eq(has_table_privilege('authenticated', 'public.rate_events', 'select'), false, 'rate_events not readable');
  perform t.eq(has_function_privilege('authenticated', 'public.pn_rate_take(uuid, text, integer, integer)', 'execute'), false, 'pn_rate_take service only');
  perform t.eq(has_function_privilege('authenticated',
    'public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean, boolean)', 'execute'),
    false, 'pn_grant_entitlement service only');
  perform t.eq((select relrowsecurity from pg_class where oid = 'public.rate_events'::regclass), true, 'rate_events has RLS');
end $$;

-- column grants: no shadow-ban tell, no other player's auth id
do $$
begin
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'shadow', 'select'), false, 'shadow hidden');
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'flagged', 'select'), false, 'flagged hidden');
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'rejections', 'select'), false, 'rejections hidden');
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'display_name', 'select'), true, 'name readable');
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'id', 'select'), true, 'id readable (RLS needs it)');
  perform t.eq(has_column_privilege('authenticated', 'public.matches', 'opponent_owner_id', 'select'), false, 'owner id hidden');
  perform t.eq(has_column_privilege('authenticated', 'public.matches', 'opponent_snapshot', 'select'), false, 'snapshot hidden');
  perform t.eq(has_column_privilege('authenticated', 'public.matches', 'result', 'select'), true, 'own result readable');
  perform t.eq(has_column_privilege('authenticated', 'public.profiles', 'display_name', 'update'), false, 'names only via profile-update');
end $$;

-- every public table has RLS on
do $$
declare r record;
begin
  for r in select c.relname, c.relrowsecurity from pg_class c join pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public' and c.relkind = 'r' loop
    perform t.eq(r.relrowsecurity, true, 'RLS on public.' || r.relname);
  end loop;
end $$;

-- --------------------------------------------------------------------------- purchases: no transfer without proof
do $$
declare
  a uuid := t.user('Buyer');
  b uuid := t.user('Thief');
  r record;
begin
  select * into r from public.pn_grant_entitlement(a, 'app_store', 'com.pawnoon.pixel.peacemaker', 'peacemaker', '2000000000000001',
    '2000000000000001', 'sandbox', 'valid', now(), '{}', true, false);
  perform t.eq(r.outcome, 'granted', 'first validation grants (no proof needed for a new purchase)');
  perform t.eq('peacemaker' = any (r.owned_items), true, 'buyer owns it');

  -- another account with only the transaction id: nothing moves
  select * into r from public.pn_grant_entitlement(b, 'app_store', 'com.pawnoon.pixel.peacemaker', 'peacemaker', '2000000000000001',
    '2000000000000001', 'sandbox', 'valid', now(), '{}', true, false);
  perform t.eq(r.outcome, 'owned_elsewhere', 'transfer refused without proof');
  perform t.eq(coalesce('peacemaker' = any (r.owned_items), false), false, 'caller did not get it');
  perform t.eq((select 'peacemaker' = any (owned_items) from public.profiles where id = a), true, 'buyer keeps it');
  perform t.eq((select equipped_gun from public.profiles where id = a), 'peacemaker', 'buyer still equipped');

  -- the same account again: idempotent
  select * into r from public.pn_grant_entitlement(a, 'app_store', 'com.pawnoon.pixel.peacemaker', 'peacemaker', '2000000000000001',
    '2000000000000001', 'sandbox', 'valid', now(), '{}', false, false);
  perform t.eq(r.outcome, 'already_owned', 'same account: already owned');

  -- with proof (a reinstall restoring on a new guest): it moves, and the old account loses it
  select * into r from public.pn_grant_entitlement(b, 'app_store', 'com.pawnoon.pixel.peacemaker', 'peacemaker', '2000000000000001',
    '2000000000000001', 'sandbox', 'valid', now(), '{}', false, true);
  perform t.eq(r.outcome, 'transferred', 'transfer with proof');
  perform t.eq(r.previous_owner, a, 'previous owner reported');
  perform t.eq((select 'peacemaker' = any (owned_items) from public.profiles where id = a), false, 'old account lost it');
  perform t.eq((select equipped_gun from public.profiles where id = a), 'standard', 'old account unequipped');
end $$;

drop schema t cascade;
select 'review2 sql tests passed' as result;
