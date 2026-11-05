#!/usr/bin/env bash
# Offline acceptance checks of docs/specs/2026-11-06-peer-gap-candidates.md (checks 1-9; tests, no network, needs Docker for the Postgres test container).
# The live checks L1-L2 need the first weekly ideas run after the deep reads exist; the spec says how to read them.
set -uo pipefail
cd "$(dirname "$0")/.." || exit 2

case "${1:-}" in
  offline) dotnet test tests/FeedEater.Tests --filter 'FullyQualifiedName~PeerGapLeadsTests|FullyQualifiedName~WeeklyIdeas|FullyQualifiedName~IdeaShortlist|FullyQualifiedName~ConfigDocTests' ;;
  *) echo "usage: $0 offline"; exit 2 ;;
esac
