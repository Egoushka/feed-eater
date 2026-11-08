#!/usr/bin/env bash
# Acceptance checks of docs/specs/2026-11-08-fact-gold-set.md.
#   offline  checks 1-3: gold well-formed, scorer self-test, lint (no network, no model calls)
#   live     checks 4-5: runs the extractor on the gold set through Bifrost (tailnet, a few cents) and scores it
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

fail=0
step() { echo "== $1"; }

case "${1:-}" in
  offline)
    step "1 gold set"; python3 -I bench/FactGold/score.py --check-gold || fail=1
    step "2 scorer self-test"; python3 -I bench/FactGold/score.py --self-test || fail=1
    step "3 lint"; ruff check bench/FactGold || fail=1
    ;;
  live)
    model="${2:-openrouter/deepseek/deepseek-v4.1-flash}"
    out="bench/FactGold/results/live-$(date +%Y%m%d-%H%M%S).json"
    step "4 extract with $model"
    python3 -I bench/FactGold/extract.py --model "$model" --tag "$(basename "$out" .json)" || fail=1
    step "5 recall at least 20 of 30"
    python3 -I bench/FactGold/score.py "$out" || fail=1
    python3 -I - "$out" <<'PY' || fail=1
import json, subprocess, sys
r = json.loads(subprocess.check_output(["python3", "-I", "bench/FactGold/score.py", sys.argv[1], "--json"]))
print("PASS" if r["items_recalled"] >= 20 else f"FAIL: {r['items_recalled']}/30")
sys.exit(0 if r["items_recalled"] >= 20 else 1)
PY
    ;;
  *) echo "usage: $0 offline | live [model]"; exit 2 ;;
esac
exit $fail
