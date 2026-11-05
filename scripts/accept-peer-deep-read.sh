#!/usr/bin/env bash
# Acceptance checks of docs/specs/2026-11-05-peer-deep-read.md.
#   offline   checks 1-8 (tests; no network, needs Docker for the Postgres test container)
#   live      checks L1-L5 against a real database and chargehand; needs chargehand#683 merged and deployed
# Live reads these from the environment and never prints them:
#   ConnectionStrings__FeedEater, DATABASE_URL (the same database as a libpq URI, for psql),
#   FeedEater__PeerHarvest__ChargehandUrl, FeedEater__PeerHarvest__ChargehandKey, FeedEater__PeerHarvest__SetUrl.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

fail=0
check() { # name, ok
  if [ "$2" = "1" ]; then echo "PASS $1"; else echo "FAIL $1"; fail=1; fi
}

offline() {
  dotnet test tests/FeedEater.Tests --filter 'FullyQualifiedName~PeerDeepRead|FullyQualifiedName~ConfigDocTests'
}

live() {
  for v in ConnectionStrings__FeedEater DATABASE_URL FeedEater__PeerHarvest__ChargehandUrl FeedEater__PeerHarvest__ChargehandKey FeedEater__PeerHarvest__SetUrl; do
    if [ -z "${!v:-}" ]; then echo "missing environment variable $v"; exit 2; fi
  done
  for tool in psql jq dotnet; do
    command -v "$tool" >/dev/null || { echo "missing tool $tool"; exit 2; }
  done

  local own="${OWN_REPO:-Egoushka/feed-eater}"
  local app=(dotnet run --project src/FeedEater --)
  local episodes="select count(*) from facts.episodes where namespace = 'prior-art'"

  echo "== first peer-harvest run (spends chargehand budget, capped by PeerHarvest:SetSpendCapUsd)"
  local first
  first=$("${app[@]}" peer-harvest) || { echo "FAIL peer-harvest"; exit 1; }
  echo "$first"

  local per_tool
  per_tool=$(psql "$DATABASE_URL" -At -F ' ' -c "
    select t.external_ref, count(distinct f.id)
    from facts.facts f join facts.entities t on t.id = f.subject_id
      join facts.fact_sources fs on fs.fact_id = f.id join facts.episodes e on e.id = fs.episode_id
    where f.namespace = 'prior-art' and f.predicate = 'has_feature' and f.retired_at is null and e.source_kind = 'repo-read'
    group by 1 order by 1")
  echo "$per_tool"
  check "L1 8 tools, each with >= 5 has_feature facts" "$(echo "$per_tool" | awk 'NF==2 && $2>=5 {ok++} END {print (ok==8) ? 1 : 0}')"

  local verify
  verify=$("${app[@]}" prior-art verify --sample 10 | tail -n 1)
  echo "$verify"
  check "L2 >= 9 of 10 citations resolve" "$(echo "$verify" | awk '{print ($1>=9 && $3==10) ? 1 : 0}')"

  local gaps
  gaps=$("${app[@]}" prior-art gaps --repo "$own")
  check "L3 >= 5 gap rows, each with >= 2 peers and evidence" "$(echo "$gaps" | jq '[.gaps[] | select((.peers | length) >= 2 and all(.peers[]; .evidence.path != null))] | length >= 5 | if . then 1 else 0 end')"

  local before after
  before=$(psql "$DATABASE_URL" -At -c "$episodes")
  "${app[@]}" peer-harvest > /dev/null || { echo "FAIL second peer-harvest"; exit 1; }
  after=$(psql "$DATABASE_URL" -At -c "$episodes")
  check "L4 second run adds no episode ($before -> $after)" "$([ "$before" = "$after" ] && echo 1 || echo 0)"

  local spent
  spent=$(echo "$first" | grep -o 'deep read: [0-9]* facts, \$[0-9.]*' | sed 's/.*\$//' | awk '{s+=$1} END {printf "%.2f", s}')
  check "L5 first run spent \$$spent <= \$5.00" "$(awk -v s="$spent" 'BEGIN {print (s <= 5.00) ? 1 : 0}')"
}

case "${1:-}" in
  offline) offline || fail=1 ;;
  live) live ;;
  *) echo "usage: $0 offline|live"; exit 2 ;;
esac

exit $fail
