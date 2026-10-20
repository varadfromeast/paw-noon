-- Paw Noon M6: accounts (SPEC_V2 §5). Additive on top of 0001/0002. Numbered 0060 so it cannot collide with
-- migrations other milestones add in parallel (it depends on nothing after 0002).

-- ---------------------------------------------------------------------------------------- display names
-- Names change only through the profile-update function (server-side rules, reserved names, ghost rename).
-- 0001 let clients update display_name directly under RLS; take that column back (character / settings stay).
revoke update (display_name) on public.profiles from authenticated;

-- Shape of every new write: a player-chosen name (3-14 of A-Z 0-9 space, no edge or double spaces; the same rules
-- as functions/_shared/display_name.ts) or the generated guest name. NOT VALID: existing rows are left alone.
alter table public.profiles
  add constraint profiles_display_name_shape
  check (
    (char_length(display_name) between 3 and 14
      and display_name ~ '^[A-Z0-9]([A-Z0-9 ]*[A-Z0-9])?$'
      and position('  ' in display_name) = 0)
    or display_name ~ '^Stranger #[0-9]{4}$'
  ) not valid;

-- ---------------------------------------------------------------------------------------- after-link hook
-- Linking Apple / Google to a guest (POST /auth/v1/token?grant_type=id_token with link_identity, the guest's JWT)
-- adds a row to auth.identities for the SAME auth.users row and flips is_anonymous to false. The user id does not
-- change, so the profile, matches, recordings, ratings and purchases (all keyed by that id) simply stay: there is no
-- guest-to-account migration to run. This hook only stamps when it happened (analytics, and so the guest cleanup
-- below can never touch a linked player).
alter table public.profiles add column if not exists linked_at timestamptz;

create or replace function public.pn_handle_user_linked() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  update public.profiles set linked_at = coalesce(linked_at, now()) where id = new.id;
  return new;
end $$;

drop trigger if exists on_auth_user_linked on auth.users;
create trigger on_auth_user_linked after update of is_anonymous on auth.users
  for each row when (old.is_anonymous is true and new.is_anonymous is false)
  execute function public.pn_handle_user_linked();

revoke all on function public.pn_handle_user_linked() from public, anon, authenticated;

-- ---------------------------------------------------------------------------------------- guest cleanup (manual)
-- Guests who never played online pile up (every fresh install that opens the profile or PVP signs up). Not
-- scheduled; run by hand or from pg_cron when the table grows (Docs/ACCOUNTS.md):
--   delete from auth.users u
--    where u.is_anonymous and u.created_at < now() - interval '30 days'
--      and not exists (select 1 from public.matches m where m.player_id = u.id);
