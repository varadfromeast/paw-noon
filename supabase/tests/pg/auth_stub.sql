-- Minimal stand-in for what the Supabase stack provides before migrations run, so the migrations and
-- the SQL tests can run on a plain Postgres 15+/16 (no Docker): the auth schema with auth.users and
-- auth.uid(), and the anon / authenticated / service_role roles. Never applied to a real project.
do $$ begin
  if not exists (select 1 from pg_roles where rolname = 'anon') then create role anon nologin; end if;
  if not exists (select 1 from pg_roles where rolname = 'authenticated') then create role authenticated nologin; end if;
  if not exists (select 1 from pg_roles where rolname = 'service_role') then create role service_role nologin bypassrls; end if;
end $$;

create schema if not exists auth;
create table if not exists auth.users (
  id            uuid primary key default gen_random_uuid(),
  is_anonymous  boolean not null default true,
  created_at    timestamptz not null default now()
);
create or replace function auth.uid() returns uuid language sql stable as $$
  select nullif(current_setting('request.jwt.claim.sub', true), '')::uuid
$$;
grant usage on schema auth to anon, authenticated, service_role;
grant usage on schema public to anon, authenticated, service_role;
grant select on auth.users to service_role;
