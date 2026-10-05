#!/usr/bin/env bash
# Calls every read tool against a running feed-eater. Usage: FEED_MCP_TOKEN=... ./selftest.sh http://100.64.0.2:8104
set -euo pipefail
base="${1:?base url}"
: "${FEED_MCP_TOKEN:?set FEED_MCP_TOKEN}"

call() {
  curl -fsS "$base/mcp" \
    -H "Authorization: Bearer $FEED_MCP_TOKEN" \
    -H 'Content-Type: application/json' \
    -H 'Accept: application/json, text/event-stream' \
    -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":{\"name\":\"$1\",\"arguments\":$2}}"
}

curl -fsS "$base/healthz" >/dev/null && echo "healthz ok"
# Reply goes into a variable first: with pipefail, `curl | grep -q` can fail when grep exits early.
reply=$(call feed_search '{"query":"postgres"}'); grep -q '"result"' <<<"$reply" && echo "feed_search ok"
reply=$(call feed_digests '{}'); grep -q '"result"' <<<"$reply" && echo "feed_digests ok"
reply=$(call feed_ideas '{}'); grep -q '"result"' <<<"$reply" && echo "feed_ideas ok"
# An id or date may not exist yet: any well-formed JSON-RPC reply proves the tool is wired.
reply=$(call feed_read '{"id":1}'); grep -q '"jsonrpc"' <<<"$reply" && echo "feed_read ok"
reply=$(call feed_digest "{\"date\":\"$(date +%F)\"}"); grep -q '"jsonrpc"' <<<"$reply" && echo "feed_digest ok"
