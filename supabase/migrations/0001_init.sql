-- Paw Noon M3: schema for recorded ("ghost") PvP duels, accounts, ratings, purchases, notifications.
-- SPEC_V2 §4.5, §5, §6, §7.
--
-- Access model
--   * Clients (anon / authenticated roles, through PostgREST) may only READ their own rows, and may
--     only UPDATE a few harmless columns of their own profile / notifications. They never insert,
--     never delete, and never touch recordings at all.
--   * Everything that changes game state (matches, ratings, coins, entitlements, the recording
--     pool) goes through Edge Functions using the service role, which bypasses RLS.
--   * pn_* SQL functions are SECURITY DEFINER helpers callable by service_role only.

-- ---------------------------------------------------------------------------------------- profiles

create table public.profiles (
  id                uuid primary key references auth.users (id) on delete cascade,
  display_name      text not null default ('Stranger #' || lpad((floor(random() * 10000))::int::text, 4, '0'))
                      check (char_length(display_name) between 1 and 24),
  character         text not null default 'whiskers' check (char_length(character) <= 32),
  owned_items       text[] not null default '{}',          -- e.g. {peacemaker}; written by purchase-validate only
  settings          jsonb not null default '{}'::jsonb,
  coins             integer not null default 0 check (coins >= 0),
  -- stats (server-validated matches only)
  matches_played    integer not null default 0,
  wins              integer not null default 0,
  best_reaction_ns  bigint,
  lightning_count   integer not null default 0,
  -- daily bounty
  daily_streak      integer not null default 0,
  daily_last_claim  date,
  -- anti-cheat (§4.3)
  rejections        integer not null default 0,
  flagged           boolean not null default false,
  created_at        timestamptz not null default now(),
  updated_at        timestamptz not null default now()
);

comment on table public.profiles is 'One row per auth user (guest or linked). Created by trigger on auth.users.';

-- ---------------------------------------------------------------------------------------- recordings

create table public.recordings (
  id                uuid primary key default gen_random_uuid(),
  owner_id          uuid references auth.users (id) on delete cascade,   -- null only for house ghosts
  is_house          boolean not null default false,
  source_match_id   uuid,                                                -- the match this was played in
  seed              integer not null,
  difficulty        text not null default 'hard' check (difficulty in ('hard')),
  mode              text not null check (mode in ('casual', 'ranked')),
  character         text not null,
  gun               text not null default 'standard',
  display_name      text not null,
  mmr               integer not null default 1200,                       -- owner's MMR when recorded
  attempts          jsonb not null,           -- [{roundNumber, kind, reactionNs, inputOffsetNs?}] (what opponents replay)
  audit             jsonb not null default '{}'::jsonb,                  -- full submitted recording
  best_reaction_ns  bigint,
  uses              integer not null default 0,
  created_at        timestamptz not null default now(),
  expires_at        timestamptz,                                         -- null = never (house ghosts)
  constraint recordings_owner_or_house check (is_house or owner_id is not null),
  constraint recordings_attempts_array check (jsonb_typeof(attempts) = 'array')
);

comment on table public.recordings is 'Ghost pool. Never readable by clients: served by match-start only.';
comment on column public.recordings.attempts is 'House ghosts must not contain "early" attempts: they are replayed on a fresh seed every match.';

create index recordings_pool_idx on public.recordings (mode, mmr) where not is_house;
create index recordings_owner_idx on public.recordings (owner_id);

-- ---------------------------------------------------------------------------------------- matches

create table public.matches (
  id                     uuid primary key default gen_random_uuid(),
  player_id              uuid not null references auth.users (id) on delete cascade,
  opponent_recording_id  uuid references public.recordings (id) on delete set null,
  opponent_owner_id      uuid references auth.users (id) on delete set null,
  opponent_is_house      boolean not null default false,
  opponent_snapshot      jsonb not null,   -- {displayName, character, gun, mmr, rd, attempts}: what was issued
  mode                   text not null check (mode in ('casual', 'ranked')),
  season                 integer,
  seed                   integer not null,
  rounds                 smallint not null default 3,
  max_attempts           smallint not null default 9,
  status                 text not null default 'issued'
                           check (status in ('issued', 'submitting', 'completed', 'void', 'expired')),
  issued_at              timestamptz not null default now(),
  deadline_at            timestamptz not null,
  submitted_at           timestamptz,
  void_reason            text,
  void_detail            text,
  player_recording_id    uuid references public.recordings (id) on delete set null,
  winner                 text check (winner in ('player', 'opponent', 'draw')),
  result                 jsonb,
  player_mmr_before      integer,
  player_mmr_after       integer,
  opponent_mmr_before    integer,
  opponent_mmr_after     integer
);

alter table public.recordings
  add constraint recordings_source_match_fk foreign key (source_match_id) references public.matches (id) on delete set null;

create index matches_player_idx on public.matches (player_id, issued_at desc);
create index matches_player_opponent_idx on public.matches (player_id, opponent_recording_id);
create index matches_open_idx on public.matches (player_id) where status = 'issued';
create index matches_wins_day_idx on public.matches (player_id, submitted_at) where status = 'completed' and winner = 'player';

-- ---------------------------------------------------------------------------------------- ratings

create table public.ratings (
  user_id         uuid not null references auth.users (id) on delete cascade,
  season          integer not null,
  mmr             double precision not null default 1200,
  rd              double precision not null default 350,
  matches         integer not null default 0,
  wins            integer not null default 0,
  losses          integer not null default 0,
  draws           integer not null default 0,
  last_played_at  timestamptz,
  primary key (user_id, season)
);

create index ratings_leaderboard_idx on public.ratings (season, mmr desc);

-- ---------------------------------------------------------------------------------------- purchases

create table public.purchases (
  id                       uuid primary key default gen_random_uuid(),
  user_id                  uuid not null references auth.users (id) on delete cascade,
  store                    text not null check (store in ('app_store', 'google_play')),
  product_id               text not null,
  transaction_id           text not null,
  original_transaction_id  text,
  environment              text not null check (environment in ('sandbox', 'production')),
  status                   text not null check (status in ('valid', 'pending', 'refunded', 'revoked')),
  purchased_at             timestamptz,
  validated_at             timestamptz not null default now(),
  raw                      jsonb not null default '{}'::jsonb,
  unique (store, transaction_id)
);

create index purchases_user_idx on public.purchases (user_id);

-- ---------------------------------------------------------------------------------------- notifications

create table public.notifications (
  id          bigint generated always as identity primary key,
  user_id     uuid not null references auth.users (id) on delete cascade,
  kind        text not null,                -- 'ghost_result'
  payload     jsonb not null,
  created_at  timestamptz not null default now(),
  read_at     timestamptz
);

create index notifications_user_idx on public.notifications (user_id, created_at desc);
create index notifications_unread_idx on public.notifications (user_id) where read_at is null;

-- ---------------------------------------------------------------------------------------- RLS + grants

alter table public.profiles      enable row level security;
alter table public.recordings    enable row level security;
alter table public.matches       enable row level security;
alter table public.ratings       enable row level security;
alter table public.purchases     enable row level security;
alter table public.notifications enable row level security;

-- Start from nothing, then grant exactly what clients need.
revoke all on public.profiles, public.recordings, public.matches, public.ratings,
              public.purchases, public.notifications from anon, authenticated;

grant select on public.profiles, public.matches, public.ratings, public.purchases, public.notifications to authenticated;
grant update (display_name, character, settings) on public.profiles to authenticated;
grant update (read_at) on public.notifications to authenticated;
-- recordings: no grant at all. Read through match-start (service role) only.

create policy profiles_select_own on public.profiles
  for select to authenticated using (id = (select auth.uid()));
create policy profiles_update_own on public.profiles
  for update to authenticated using (id = (select auth.uid())) with check (id = (select auth.uid()));

create policy matches_select_own on public.matches
  for select to authenticated using (player_id = (select auth.uid()));

create policy ratings_select_own on public.ratings
  for select to authenticated using (user_id = (select auth.uid()));

create policy purchases_select_own on public.purchases
  for select to authenticated using (user_id = (select auth.uid()));

create policy notifications_select_own on public.notifications
  for select to authenticated using (user_id = (select auth.uid()));
create policy notifications_update_own on public.notifications
  for update to authenticated using (user_id = (select auth.uid())) with check (user_id = (select auth.uid()));

-- ---------------------------------------------------------------------------------------- triggers

create or replace function public.pn_touch_updated_at() returns trigger
language plpgsql set search_path = '' as $$
begin
  new.updated_at := now();
  return new;
end $$;

create trigger profiles_touch before update on public.profiles
  for each row execute function public.pn_touch_updated_at();

-- Every auth user (guest included) gets a profile.
create or replace function public.pn_handle_new_user() returns trigger
language plpgsql security definer set search_path = '' as $$
begin
  insert into public.profiles (id) values (new.id) on conflict (id) do nothing;
  return new;
end $$;

create trigger on_auth_user_created after insert on auth.users
  for each row execute function public.pn_handle_new_user();

-- ---------------------------------------------------------------------------------------- service helpers

-- Picks a ghost for p_user (§4.4): closest MMR in widening bands, never own, never one this player
-- has already faced, pool rules (30 days / 20 uses). Ranked only draws ranked recordings.
-- Falls back to the house ghost this player has met least. Counts the use.
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

-- Rejected recording (§4.3): count it; three = flagged (ranked disabled).
create or replace function public.pn_register_rejection(p_user uuid, p_flag_at integer)
returns table (rejections integer, flagged boolean)
language sql security definer set search_path = '' as $$
  update public.profiles p
     set rejections = p.rejections + 1,
         flagged    = p.flagged or (p.rejections + 1) >= p_flag_at
   where p.id = p_user
  returning p.rejections, p.flagged;
$$;

-- Stats after a server-validated match.
create or replace function public.pn_record_match_stats(p_user uuid, p_won boolean, p_best_ns bigint, p_lightning integer)
returns void
language sql security definer set search_path = '' as $$
  update public.profiles p
     set matches_played   = p.matches_played + 1,
         wins             = p.wins + case when p_won then 1 else 0 end,
         best_reaction_ns = case when p_best_ns is null then p.best_reaction_ns
                                 when p.best_reaction_ns is null then p_best_ns
                                 else least(p.best_reaction_ns, p_best_ns) end,
         lightning_count  = p.lightning_count + greatest(p_lightning, 0)
   where p.id = p_user;
$$;

-- Daily bounty (§7): p_required server-validated wins on p_day (in p_tz) -> p_coins, once per day.
-- Streak continues when yesterday was claimed. Row-locked, so concurrent calls cannot double-pay.
create or replace function public.pn_claim_daily(p_user uuid, p_tz text, p_required integer, p_coins integer)
returns table (claimed boolean, already_claimed boolean, wins_today integer, coins integer, streak integer, day date)
language plpgsql security definer set search_path = '' as $$
declare
  v_day   date := (now() at time zone p_tz)::date;
  v_prof  public.profiles;
  v_wins  integer;
begin
  select * into v_prof from public.profiles where id = p_user for update;
  if not found then
    raise exception 'no profile for %', p_user;
  end if;

  select count(*) into v_wins
    from public.matches m
   where m.player_id = p_user and m.status = 'completed' and m.winner = 'player'
     and (m.submitted_at at time zone p_tz)::date = v_day;

  if v_prof.daily_last_claim = v_day then
    return query select false, true, v_wins, v_prof.coins, v_prof.daily_streak, v_day;
    return;
  end if;
  if v_wins < p_required then
    return query select false, false, v_wins, v_prof.coins, v_prof.daily_streak, v_day;
    return;
  end if;

  update public.profiles p
     set coins            = p.coins + p_coins,
         daily_streak     = case when p.daily_last_claim = v_day - 1 then p.daily_streak + 1 else 1 end,
         daily_last_claim = v_day
   where p.id = p_user
  returning p.coins, p.daily_streak into v_prof.coins, v_prof.daily_streak;

  return query select true, false, v_wins, v_prof.coins, v_prof.daily_streak, v_day;
end $$;

revoke all on function public.pn_pick_opponent(uuid, integer, text) from public, anon, authenticated;
revoke all on function public.pn_register_rejection(uuid, integer) from public, anon, authenticated;
revoke all on function public.pn_record_match_stats(uuid, boolean, bigint, integer) from public, anon, authenticated;
revoke all on function public.pn_claim_daily(uuid, text, integer, integer) from public, anon, authenticated;
revoke all on function public.pn_handle_new_user() from public, anon, authenticated;
grant execute on function public.pn_pick_opponent(uuid, integer, text) to service_role;
grant execute on function public.pn_register_rejection(uuid, integer) to service_role;
grant execute on function public.pn_record_match_stats(uuid, boolean, bigint, integer) to service_role;
grant execute on function public.pn_claim_daily(uuid, text, integer, integer) to service_role;
