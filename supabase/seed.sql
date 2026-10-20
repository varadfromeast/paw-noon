-- Three house ghosts (SPEC_V2 §4.0 step 4): the opponent when the pool has nobody suitable.
-- Shown to players with the "house ghost" label (match-start returns opponent.isHouse = true).
--
-- House ghosts never jump the gun (no "early" attempts), so their reactions do not depend on hold
-- lengths and match-start replays them on a FRESH random seed every match. That starts a new seed
-- lineage for each player recording instead of every recording in the pool inheriting one of three
-- seeds. The `seed` column below is therefore informational only.
-- Reactions are plausible human values in ns; each ghost plays 3 clean rounds (no fouls -> no voids).

insert into public.recordings
  (id, owner_id, is_house, seed, difficulty, mode, character, gun, display_name, mmr, attempts, audit, best_reaction_ns, expires_at)
values
  ('00000000-0000-4000-8000-000000000001', null, true, 1001, 'hard', 'ranked', 'whiskers', 'standard',
   'Dusty', 1000,
   '[{"roundNumber":1,"kind":"shot","reactionNs":412500000},
     {"roundNumber":2,"kind":"shot","reactionNs":388000000},
     {"roundNumber":3,"kind":"shot","reactionNs":455250000}]'::jsonb,
   '{"source":"house"}'::jsonb, 388000000, null),

  ('00000000-0000-4000-8000-000000000002', null, true, 1002, 'hard', 'ranked', 'blackpaw', 'standard',
   'Marshal Mittens', 1200,
   '[{"roundNumber":1,"kind":"shot","reactionNs":268400000},
     {"roundNumber":2,"kind":"shot","reactionNs":301100000},
     {"roundNumber":3,"kind":"shot","reactionNs":247900000}]'::jsonb,
   '{"source":"house"}'::jsonb, 247900000, null),

  ('00000000-0000-4000-8000-000000000003', null, true, 1003, 'hard', 'ranked', 'blackpaw', 'standard',
   'Old Tom', 1400,
   '[{"roundNumber":1,"kind":"shot","reactionNs":221300000},
     {"roundNumber":2,"kind":"shot","reactionNs":236800000},
     {"roundNumber":3,"kind":"shot","reactionNs":209600000}]'::jsonb,
   '{"source":"house"}'::jsonb, 209600000, null)
on conflict (id) do update
  set attempts = excluded.attempts, display_name = excluded.display_name, mmr = excluded.mmr,
      character = excluded.character, best_reaction_ns = excluded.best_reaction_ns;
