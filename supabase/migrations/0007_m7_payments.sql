-- Paw Noon M7: payments (SPEC_V2 §6). Additive on top of 0001_init.sql / 0002_review_hardening.sql.
-- (Numbered 0007 for the milestone so M5 / M6 migrations can take 0003..0006 without a rename.)
--
-- * purchases.item: the item a validated product unlocks ("peacemaker"); entitlements are recomputed from
--   the valid purchases of that item, so a refund or a transfer can take it away again.
-- * profiles.equipped_gun: the loadout. A row check keeps it to "standard" or an owned item, so neither a
--   client update nor a refund can leave an unowned gun equipped.
-- * pn_grant_entitlement: idempotent upsert of one validated store purchase + the profile, row-locked.
--   A store purchase (Apple original transaction / Google order) entitles exactly one account: validating
--   it from another account (new guest after a reinstall, second device without linking) MOVES it there,
--   like the App Store / Play Store do for the store account itself.

-- ---------------------------------------------------------------------------------------- columns

alter table public.purchases add column if not exists item text;
create index if not exists purchases_user_item_idx on public.purchases (user_id, item) where status = 'valid';

alter table public.profiles add column if not exists equipped_gun text not null default 'standard';
alter table public.profiles
  add constraint profiles_equipped_gun_owned
  check (equipped_gun = 'standard' or equipped_gun = any (owned_items)) not valid;

-- clients may equip directly (RLS: own row; the check above enforces ownership); loadout-equip does the same
grant update (equipped_gun) on public.profiles to authenticated;

-- ---------------------------------------------------------------------------------------- entitlements

-- owned_items gains / loses p_item according to the user's valid purchases of it; losing it unequips it.
create or replace function public.pn_recompute_item(p_user uuid, p_item text)
returns void
language plpgsql security definer set search_path = '' as $$
begin
  if exists (select 1 from public.purchases pu where pu.user_id = p_user and pu.item = p_item and pu.status = 'valid') then
    update public.profiles p
       set owned_items = array_append(p.owned_items, p_item)
     where p.id = p_user and not (p_item = any (p.owned_items));
  else
    update public.profiles p
       set owned_items = array_remove(p.owned_items, p_item),
           equipped_gun = case when p.equipped_gun = p_item then 'standard' else p.equipped_gun end
     where p.id = p_user and (p_item = any (p.owned_items) or p.equipped_gun = p_item);
  end if;
end $$;

-- outcome: granted (new purchase row) | already_owned (same account, refreshed) | transferred (moved from
-- previous_owner). The profile columns are returned after the change.
create or replace function public.pn_grant_entitlement(
  p_user uuid, p_store text, p_product text, p_item text, p_txn text, p_orig_txn text,
  p_env text, p_status text, p_purchased_at timestamptz, p_raw jsonb, p_equip boolean)
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
      -- a concurrent call inserted it first: continue as an update under the lock
      select * into v_row from public.purchases pu where pu.store = p_store and pu.transaction_id = p_txn for update;
    end if;
  end if;

  if v_outcome is null then
    if v_row.user_id = p_user then
      v_outcome := 'already_owned';
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

-- Equip a gun (loadout-equip). Returns the equipped gun, or null when the gun is not owned.
create or replace function public.pn_set_equipped_gun(p_user uuid, p_gun text)
returns text
language plpgsql security definer set search_path = '' as $$
declare
  v_gun text;
begin
  update public.profiles p
     set equipped_gun = p_gun
   where p.id = p_user and (p_gun = 'standard' or p_gun = any (p.owned_items))
  returning p.equipped_gun into v_gun;
  return v_gun;
end $$;

revoke all on function public.pn_recompute_item(uuid, text) from public, anon, authenticated;
revoke all on function public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean) from public, anon, authenticated;
revoke all on function public.pn_set_equipped_gun(uuid, text) from public, anon, authenticated;
grant execute on function public.pn_recompute_item(uuid, text) to service_role;
grant execute on function public.pn_grant_entitlement(uuid, text, text, text, text, text, text, text, timestamptz, jsonb, boolean) to service_role;
grant execute on function public.pn_set_equipped_gun(uuid, text) to service_role;
