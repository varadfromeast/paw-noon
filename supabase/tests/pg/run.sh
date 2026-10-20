#!/usr/bin/env bash
# SQL tests for the migrations against a REAL Postgres (no Docker, no Supabase CLI needed).
#
#   supabase/tests/pg/run.sh                 # throwaway cluster: initdb in a temp dir (Postgres 15+ binaries)
#   PN_PG_URL=postgres://... supabase/tests/pg/run.sh   # an existing EMPTY database you can drop objects in
#
# Loads auth_stub.sql (auth.users, auth.uid(), roles), every migration in order, seed.sql, then each
# tests/pg/*.test.sql in one psql session with ON_ERROR_STOP. A failing assertion raises and exits non-zero.
# Against `supabase start` use the local db URL (postgresql://postgres:postgres@127.0.0.1:54322/postgres)
# only after `supabase db reset`, and pass PN_PG_SKIP_SCHEMA=1 (the stack already has auth + migrations).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sb="$(cd "$here/../.." && pwd)"

cleanup() { :; }
if [[ -z "${PN_PG_URL:-}" ]]; then
  bin="${PN_PG_BIN:-}"
  if [[ -z "$bin" ]]; then
    for d in /usr/lib/postgresql/*/bin /opt/homebrew/opt/postgresql@*/bin /usr/local/opt/postgresql@*/bin; do
      [[ -x "$d/initdb" ]] && bin="$d"
    done
  fi
  if [[ -z "$bin" ]] && command -v initdb >/dev/null; then bin="$(dirname "$(command -v initdb)")"; fi
  [[ -n "$bin" ]] || { echo "no initdb found: install Postgres 15+ or set PN_PG_URL / PN_PG_BIN" >&2; exit 2; }
  tmp="$(mktemp -d)"
  port="${PN_PG_PORT:-54329}"
  as=()
  if [[ "$(id -u)" == 0 ]]; then chown postgres "$tmp"; as=(runuser -u postgres --); fi
  "${as[@]}" "$bin/initdb" -D "$tmp/data" -U postgres -A trust >/dev/null
  "${as[@]}" "$bin/pg_ctl" -D "$tmp/data" -o "-p $port -k $tmp -c listen_addresses=''" -l "$tmp/log" -w start >/dev/null
  cleanup() { "${as[@]}" "$bin/pg_ctl" -D "$tmp/data" -m immediate stop >/dev/null 2>&1 || true; rm -rf "$tmp"; }
  trap cleanup EXIT
  PN_PG_URL="postgresql://postgres@/postgres?host=$tmp&port=$port"
fi

run() { psql "$PN_PG_URL" -X -q -v ON_ERROR_STOP=1 "$@"; }

if [[ -z "${PN_PG_SKIP_SCHEMA:-}" ]]; then
  run -f "$here/auth_stub.sql"
  for f in "$sb"/migrations/*.sql; do run -f "$f"; done
  run -f "$sb/seed.sql"
fi
fail=0
for t in "$here"/*.test.sql; do
  if run -f "$t"; then echo "ok    $(basename "$t")"; else echo "FAIL  $(basename "$t")"; fail=1; fi
done
exit $fail
