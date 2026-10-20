-- Paw Noon review pass 2 (Docs/REVIEW_2026-10-20_pass2.md). Additive on top of 0001..0060.
-- Numbered 0061: every new migration must sort after the last one already applied (0060), or `supabase db push`
-- refuses it (supabase/scripts/check_migrations.sh enforces the order; new files: `supabase migration new <name>`).
--
--   1. NOT VALID checks that blocked ordinary UPDATEs of old rows (any UPDATE re-checks the whole row, so a
--      profile with a pre-0060 name failed pn_complete_match's stats update, daily-claim, purchases...): repair the
--      old rows, then VALIDATE the constraints.
--   2. Character ids: the server persona id is `pawtruth` (was `blackpaw`); old rows are renamed, the API keeps
--      accepting `blackpaw` as an alias (recording.ts).
--   3. Per-user rate limits for the functions that had none (room-join guesses, profile-update, purchase-validate,
--      daily-claim, leaderboard-read, profile-read, loadout-equip, account-delete): pn_rate_take().
--   4. Purchases: pn_grant_entitlement only moves a purchase from another account when the caller proved it owns
--      the store purchase (device receipt / signed transaction / matching appAccountToken); a bare transaction id is
--      no longer enough (review pass 2, PAYMENTS.md §6).

-- ======================================================================================== 1. display names
-- The shape of 0060's profiles_display_name_shape, as a function (same expression).
create or replace function public.pn_display_name_ok(p text) returns boolean
language sql immutable set search_path = '' as $$
  select p is not null and (
    (char_length(p) between 3 and 14
      and p ~ '^[A-Z0-9]([A-Z0-9 ]*[A-Z0-9])?$'
      and position('  ' in p) = 0)
    or p ~ '^Stranger #[0-9]{4}$')
$$;

-- functions/_shared/display_name.ts reserved names (house ghosts, staff, the game)
create or replace function public.pn_display_name_reserved(p text) returns boolean
language sql immutable set search_path = '' as $$
  select p in ('DUSTY', 'MARSHAL MITTENS', 'OLD TOM')
      or p ~ '^(STRANGER|ADMIN|MODERATOR|PAWNOON|PAW NOON|SUPPORT|SYSTEM|HOUSE GHOST)'
$$;

-- What an old name becomes: its normalised form (trim, collapse spaces, upper-case) when that is a valid,
-- unreserved name, else a fresh guest name.
create or replace function public.pn_display_name_repair(p text) returns text
language plpgsql volatile set search_path = '' as $$
declare
  n text := upper(regexp_replace(btrim(coalesce(p, '')), '\s+', ' ', 'g'));
begin
  if public.pn_display_name_ok(p) then return p; end if;
  if public.pn_display_name_ok(n) and not public.pn_display_name_reserved(n) then return n; end if;
  return 'Stranger #' || lpad((floor(random() * 10000))::int::text, 4, '0');
end $$;

-- Repairs every profile row the NOT VALID checks of 0002 / 0007 / 0060 would refuse on its next UPDATE, and the
-- recordings' name snapshots of the renamed players. Idempotent; returns the number of profiles changed.
create or replace function public.pn_repair_profiles() returns integer
language plpgsql security definer set search_path = '' as $$
declare
  v_n integer := 0;
  v_k integer;
begin
  with fixed as (
    update public.profiles p
       set display_name = public.pn_display_name_repair(p.display_name)
     where not public.pn_display_name_ok(p.display_name)
    returning p.id, p.display_name
  ), ghosts as (
    update public.recordings r set display_name = f.display_name from fixed f where r.owner_id = f.id returning 1
  )
  select count(*) into v_n from fixed;
  update public.profiles p set settings = '{}'::jsonb where pg_column_size(p.settings) > 4096;
  get diagnostics v_k = row_count;
  v_n := v_n + v_k;
  update public.profiles p set equipped_gun = 'standard'
   where not (p.equipped_gun = 'standard' or p.equipped_gun = any (p.owned_items));
  get diagnostics v_k = row_count;
  v_n := v_n + v_k;
  update public.profiles p set character = case when p.character = 'blackpaw' then 'pawtruth' else 'whiskers' end
   where p.character not in ('whiskers', 'pawtruth');
  get diagnostics v_k = row_count;
  v_n := v_n + v_k;
  return v_n;
end $$;

-- ======================================================================================== 2. character ids
update public.recordings set character = 'pawtruth' where character = 'blackpaw';

select public.pn_repair_profiles();

alter table public.profiles
  add constraint profiles_character_known check (character in ('whiskers', 'pawtruth')) not valid;

-- every check is now true for every row: validate, so "NOT VALID" no longer hides a broken old row
alter table public.profiles validate constraint profiles_display_name_clean;
alter table public.profiles validate constraint profiles_settings_small;
alter table public.profiles validate constraint profiles_equipped_gun_owned;
alter table public.profiles validate constraint profiles_display_name_shape;
alter table public.profiles validate constraint profiles_character_known;

-- ======================================================================================== 3. rate limits
-- One row per counted call. Written by pn_rate_take only (service role); never readable by clients.
create table public.rate_events (
  user_id  uuid not null references auth.users (id) on delete cascade,
  kind     text not null check (char_length(kind) between 1 and 32),
  at       timestamptz not null default now()
);
create index rate_events_user_kind_idx on public.rate_events (user_id, kind, at desc);
alter table public.rate_events enable row level security;
revoke all on public.rate_events from anon, authenticated;

-- true = allowed (and counted); false = p_max calls of p_kind in the last p_window_sec already (nothing recorded).
-- Serialised per (user, kind) with an advisory transaction lock, so parallel calls cannot both slip through.
-- Old rows of this user and kind are pruned on the way (a row is only needed for its window).
create or replace function public.pn_rate_take(p_user uuid, p_kind text, p_window_sec integer, p_max integer)
returns boolean
language plpgsql security definer set search_path = '' as $$
declare
  v_n integer;
begin
  perform pg_advisory_xact_lock(hashtextextended(p_user::text || '/' || p_kind, 0));
  delete from public.rate_events e
   where e.user_id = p_user and e.kind = p_kind and e.at < now() - make_interval(secs => greatest(p_window_sec, 1));
  select count(*) into v_n from public.rate_events e where e.user_id = p_user and e.kind = p_kind;
  if v_n >= p_max then return false; end if;
  insert into public.rate_events (user_id, kind) values (p_user, p_kind);
  return true;
end $$;

-- How many p_kind events p_user has in the window (for limits that only count failures, e.g. room-join misses).
create or replace function public.pn_rate_count(p_user uuid, p_kind text, p_window_sec integer)
returns integer
language sql stable security definer set search_path = '' as $$
  select count(*)::integer from public.rate_events e
   where e.user_id = p_user and e.kind = p_kind and e.at >= now() - make_interval(secs => greatest(p_window_sec, 1));
$$;

-- Records one event without a limit check (the failure counters above).
create or replace function public.pn_rate_note(p_user uuid, p_kind text)
returns void
language sql security definer set search_path = '' as $$
  insert into public.rate_events (user_id, kind) values (p_user, p_kind);
$$;

-- ======================================================================================== 4. purchases
-- Same as 0007, plus p_allow_transfer: when false, a purchase that belongs to ANOTHER account is left where it is
-- and the outcome is `owned_elsewhere` (nothing changes). purchase-validate passes true only with proof that the
-- caller's device owns the store purchase (see functions/_shared/payments/binding.ts).
drop function if exists public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean);

create or replace function public.pn_grant_entitlement(
  p_user uuid, p_store text, p_product text, p_item text, p_txn text, p_orig_txn text,
  p_env text, p_status text, p_purchased_at timestamptz, p_raw jsonb, p_equip boolean, p_allow_transfer boolean)
returns table (outcome text, owned_items text[], coins integer, equipped_gun text, previous_owner uuid)
language plpgsql security definer set search_path = '' as $$
declare
  v_row public.purchases;
  v_prev uuid := null;
  v_outcome text := null;
begin
  select * into v_row from public.purchases pu where pu.store = p_store and pu.transaction_id = p_txn for update;
  if not found then
    insert into public.purchases (user_id, store, product_id, item, transaction_id, original_transaction_id,
                                  environment, status, purchased_at, raw)
    values (p_user, p_store, p_product, p_item, p_txn, p_orig_txn, p_env, p_status, p_purchased_at, coalesce(p_raw, '{}'::jsonb))
    on conflict (store, transaction_id) do nothing;
    if found then
      v_outcome := 'granted';
    else
      select * into v_row from public.purchases pu where pu.store = p_store and pu.transaction_id = p_txn for update;
    end if;
  end if;

  if v_outcome is null then
    if v_row.user_id = p_user then
      v_outcome := 'already_owned';
    elsif not coalesce(p_allow_transfer, false) then
      -- someone else's purchase and no proof that this caller owns it: nothing moves
      return query
        select 'owned_elsewhere'::text, p.owned_items, p.coins, p.equipped_gun, null::uuid
          from public.profiles p where p.id = p_user;
      return;
    else
      v_prev := v_row.user_id;
      v_outcome := 'transferred';
    end if;
    update public.purchases pu
       set user_id = p_user,
           product_id = p_product,
           item = p_item,
           original_transaction_id = coalesce(p_orig_txn, pu.original_transaction_id),
           environment = p_env,
           status = p_status,
           purchased_at = coalesce(p_purchased_at, pu.purchased_at),
           validated_at = now(),
           raw = coalesce(p_raw, pu.raw)
     where pu.id = v_row.id;
  end if;

  if v_prev is not null then
    perform public.pn_recompute_item(v_prev, p_item);
  end if;
  perform public.pn_recompute_item(p_user, p_item);

  if p_equip and p_status = 'valid' then
    update public.profiles p set equipped_gun = p_item where p.id = p_user and p_item = any (p.owned_items);
  end if;

  return query
    select v_outcome, p.owned_items, p.coins, p.equipped_gun, v_prev
      from public.profiles p where p.id = p_user;
end $$;

-- ======================================================================================== grants
revoke all on function public.pn_display_name_ok(text) from public, anon, authenticated;
revoke all on function public.pn_display_name_reserved(text) from public, anon, authenticated;
revoke all on function public.pn_display_name_repair(text) from public, anon, authenticated;
revoke all on function public.pn_repair_profiles() from public, anon, authenticated;
revoke all on function public.pn_rate_take(uuid, text, integer, integer) from public, anon, authenticated;
revoke all on function public.pn_rate_count(uuid, text, integer) from public, anon, authenticated;
revoke all on function public.pn_rate_note(uuid, text) from public, anon, authenticated;
revoke all on function public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean, boolean) from public, anon, authenticated;
grant execute on function public.pn_repair_profiles() to service_role;
grant execute on function public.pn_rate_take(uuid, text, integer, integer) to service_role;
grant execute on function public.pn_rate_count(uuid, text, integer) to service_role;
grant execute on function public.pn_rate_note(uuid, text) to service_role;
grant execute on function public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean, boolean) to service_role;
