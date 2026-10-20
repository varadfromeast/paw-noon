#!/usr/bin/env bash
# Migration order guard (review pass 2, integrator concern "0003 / 0007 / 0060 vs supabase db push").
#
# `supabase db push` applies local migrations whose version is not in the remote history, but REFUSES a local
# migration whose version sorts before the newest one already applied ("Found local migration files to be inserted
# before the last migration on remote database"; `--include-all` overrides). The milestones numbered theirs
# 0003 (M5), 0007 (M7) and 0060 (M6) to avoid collisions while working in parallel; that is fine as long as
# (1) no applied migration is ever renamed, and (2) every NEW migration sorts after all of them.
#
# migrations.lock lists the migrations in the order they are applied. This script fails when a locked file is
# missing or renamed, when versions repeat, when the version sort and the file sort disagree, or when a file not
# in the lock sorts before the last locked one. After adding a migration (`supabase migration new <name>` makes
# a timestamped one, which always sorts last), run with --update and commit the lock.
#   supabase/scripts/check_migrations.sh            check (CI)
#   supabase/scripts/check_migrations.sh --update   append new migrations to the lock (after the check passes)
set -euo pipefail
dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
lock="$dir/scripts/migrations.lock"
cd "$dir/migrations"
mapfile -t files < <(ls -1 *.sql | LC_ALL=C sort)
mapfile -t locked < <(grep -v '^#' "$lock" | sed '/^$/d')
fail=0
declare -A seen=()
for f in "${files[@]}"; do
  if [[ ! "$f" =~ ^([0-9]+)_[a-z0-9_]+\.sql$ ]]; then echo "bad migration name: $f (want <digits>_<snake_case>.sql)" >&2; fail=1; continue; fi
  v="${BASH_REMATCH[1]}"
  key="$((10#$v))"
  if [[ -n "${seen[$key]:-}" ]]; then echo "duplicate migration version $v: $f and ${seen[$key]}" >&2; fail=1; fi
  seen[$key]="$f"
done
# file order (what the CLI applies) must equal numeric version order
num_sorted="$(printf '%s\n' "${files[@]}" | sort -t_ -k1,1n)"
if [[ "$num_sorted" != "$(printf '%s\n' "${files[@]}")" ]]; then echo "file order and numeric version order differ (pad versions to the same width)" >&2; fail=1; fi
# every locked migration still exists, in the locked order
for i in "${!locked[@]}"; do
  if [[ "${files[$i]:-}" != "${locked[$i]}" ]]; then
    echo "applied migration ${locked[$i]} is missing, renamed or reordered (found ${files[$i]:-nothing} at position $((i+1))); never rename an applied migration" >&2
    fail=1
    break
  fi
done
last="${locked[${#locked[@]}-1]}"
new=()
for f in "${files[@]}"; do
  in_lock=0
  for l in "${locked[@]}"; do [[ "$f" == "$l" ]] && in_lock=1; done
  if [[ $in_lock == 0 ]]; then
    if [[ "$f" < "$last" ]]; then echo "new migration $f sorts before $last: \`supabase db push\` would refuse it; renumber it after $last" >&2; fail=1; fi
    new+=("$f")
  fi
done
[[ $fail == 0 ]] || exit 1
if [[ "${1:-}" == "--update" ]]; then
  for f in "${new[@]}"; do echo "$f" >> "$lock"; done
  echo "lock updated (${#new[@]} added)"
else
  [[ ${#new[@]} -eq 0 ]] || echo "new (not yet in migrations.lock): ${new[*]}"
  echo "migrations in order (${#files[@]})"
fi
