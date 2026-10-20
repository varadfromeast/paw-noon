-- Paw Noon: review hardening (Docs/REVIEW_2026-10-20.md). Additive on top of 0001_init.sql.

-- ---------------------------------------------------------------------------------------- profiles
-- Clients may update display_name / settings directly (RLS: own row). display_name is shown to other
-- players (opponent card, ghost notifications): no control characters (newline / bidi tricks in a
-- notification), no leading or trailing blanks. settings is free-form jsonb: cap its size so a client
-- cannot park megabytes in its own row. NOT VALID: enforced for every new write, existing rows untouched.
alter table public.profiles
  add constraint profiles_display_name_clean
  check (display_name !~ '[[:cntrl:]]' and display_name = btrim(display_name)) not valid;

alter table public.profiles
  add constraint profiles_settings_small
  check (pg_column_size(settings) <= 4096) not valid;

-- ---------------------------------------------------------------------------------------- matches
-- match-start voids this player's matches stuck in `submitting` (the submit function died mid-way).
create index if not exists matches_submitting_idx on public.matches (player_id, submitted_at) where status = 'submitting';

-- ---------------------------------------------------------------------------------------- pool
-- Same as 0001, plus: recordings of flagged accounts (three rejected recordings, §4.3) leave the pool
-- immediately instead of being served for up to 30 days / 20 uses.
create or replace function public.pn_pick_opponent(p_user uuid, p_mmr integer, p_mode text)
returns public.recordings
language plpgsql security definer set search_path = '' as $$
declare
  r public.recordings;
  band integer;
begin
  foreach band in array array[150, 300, 600, 1200, 1000000] loop
    select rec.* into r
      from public.recordings rec
     where not rec.is_house
       and rec.owner_id <> p_user
       and rec.difficulty = 'hard'
       and (p_mode = 'casual' or rec.mode = 'ranked')
       and rec.uses < 20
       and rec.expires_at > now()
       and abs(rec.mmr - p_mmr) <= band
       and not exists (select 1 from public.profiles p where p.id = rec.owner_id and p.flagged)
       and not exists (select 1 from public.matches m
                        where m.player_id = p_user and m.opponent_recording_id = rec.id)
     order by abs(rec.mmr - p_mmr), random()
     limit 1
     for update skip locked;
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

revoke all on function public.pn_pick_opponent(uuid, integer, text) from public, anon, authenticated;
grant execute on function public.pn_pick_opponent(uuid, integer, text) to service_role;
