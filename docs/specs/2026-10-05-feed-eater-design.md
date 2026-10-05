# feed-eater — design

Date: 2026-10-05 · Status: draft, awaiting review · Plan: [docs/plans/2026-10-05-feed-eater-v1.md](../plans/2026-10-05-feed-eater-v1.md)

## Goal

Yehor stops researching by hand. A service reads everything his sources publish, picks the few items that
matter to his projects and interests, sends them to Telegram each morning with a concrete suggestion where one
exists, learns from his reactions, and keeps every item in an archive any AI can search over MCP.

Success for v1, measured after 4 weeks of daily digests:

- The digest arrives by 08:00 Europe/Kyiv on at least 26 of 28 days.
- At least 60% of voted highlights get 👍.
- LLM spend at most $10 per 30 days (enforced by a LiteLLM key budget, reported in the digest).
- Any MCP client can find an archived item by topic and cite it (title, URL, date, summary).

## What exists (checked 2026-10-05)

| Piece | State | Evidence |
|---|---|---|
| Miniflux 2.3.3 | 17 feeds in 5 categories; 10,752 entries, all `unread`; ~89 new a day, avg 5,338 chars | `hz sql miniflux-db`; `homelab-gitops/miniflux/compose.yaml` |
| Miniflux signal in Hindsight | Dead: asks for `status=read`, and nothing is ever read | `host/opt-homelab/ingest.py:82` |
| Noise | r/selfhosted (269/week) and Hacker News (161/week) are 70% of volume | same query |
| Failing feeds | r/homelab: "too many requests" (Reddit allows logged-out RSS ~1 req/min/IP since 2026-06); HN `news.ycombinator.com/rss`: unavailable | `feeds.parsing_error_msg` |
| Full-text fetch | Miniflux `GET /v1/entries/{id}/fetch-content` returns `{"content": "..."}` | miniflux.app/docs/api.html |
| Plane Intake | `POST /api/v1/workspaces/homelab/projects/{id}/intake-issues/` with `X-API-Key`, already used | `host/opt-homelab/plane-sync.py:128` |
| Plane projects | SKAR, TOUCH, WHET, NYTKA, CHRON, JARVIS, CHARGEHAND, LAB, HOME | Plane MCP `project list` |
| LiteLLM | `text-embedding-3-small`, `gpt-4.1-nano`, `claude-haiku-4-5` routed; per-key `max_budget` + `budget_duration` precedent (`langfuse-evals`) | `litellm/config.yaml`, `langfuse/README.md:290` |
| Telegram | JARVIS long-polls its own bot, so a second poller on that token would conflict | `jarvis/compose.yaml:4` |
| Template | senses: .NET 10, Dapper, Npgsql, DbUp, `ModelContextProtocol.AspNetCore`, Testcontainers | `personal/senses` |

## Decisions

| # | Decision | Why |
|---|---|---|
| D1 | Miniflux is the only fetcher. feed-eater never crawls. Pages without a feed reach Miniflux through changedetection's RSS output. | Fetching is free and already runs; one input contract. A crawler adds anti-bot and parsing work, not signal. |
| D2 | Cost is bounded by fixed daily caps, not by volume: embed everything, triage the top 60, deep-read the top 12. | Model spend no longer grows with the number of feeds; only embeddings do, at $0.02 per 1M tokens. |
| D3 | Ranking is vector math with hand-set weights, no trained model. | Explainable, testable, needs no labelled data on day one. |
| D4 | Own Telegram bot, long polling, no inbound port. | JARVIS owns its token; long polling matches JARVIS and needs no exposure. |
| D5 | Code public (`Egoushka/feed-eater`, Apache-2.0); archive and MCP tailnet-only. | Full text of third-party articles must not be served publicly; same rule as `refs`. |
| D6 | Accepted ideas become Plane Intake items; projects without a Plane project go to a new `FEED` project. | Ideas join the existing roadmap flow; `FEED` also holds feed-eater's own roadmap. |
| D7 | .NET 10, cloned from senses' structure and package versions. | Yehor's stack; senses and nytka move together on the same versions. |
| D8 | Vectors cross the wire as `real[]` and are cast to `vector` in SQL; no Pgvector NuGet package. | `Pgvector` 0.3.2 states only an Npgsql >= 8.0.5 floor, untested on Npgsql 10; arrays need no package. |

## Architecture

One process, `feed-eater`, with its own Postgres 18 + pgvector.

```
Miniflux API ──(30 min)──► Ingestor ──► items (+ embedding, stage-1 score)
Karakeep, GitHub stars ──(daily)──► SignalCollector ──► signals (+ embedding)
Plane open items ──(nightly)──► ProfileBuilder ──► profiles (+ embedding)

07:30 Kyiv ─► DigestRun: rank ─► triage (nano) ─► fetch full text ─► deep-read (haiku) ─► digest
                                                                              │
Telegram ◄── DigestSender ◄───────────────────────────────────────────────────┘
Telegram ──► CallbackHandler ──► votes / ideas ──► Plane Intake
any MCP client ──► agentgateway /mcp/feed ──► McpTools (read-only)
```

### Ingestor (every 30 min)

1. `GET /v1/entries?after_entry_id={cursor}&order=id&direction=asc&limit=100`, paged until empty. The cursor is
   the highest Miniflux entry id stored. Miniflux read/unread state is never changed.
2. Canonical URL: lowercase host, drop fragment, `utm_*`, `ref`, `fbclid`, trailing slash. A canonical URL already
   stored marks the new row `duplicate_of` the first one. A normalised-title hash seen in the last 7 days does
   the same (catches the same story from two feeds).
3. Embed `title + "\n" + text(content)[:8000 chars]` in batches of 64 through LiteLLM `/v1/embeddings`.
4. The stage-1 score (below) is computed at digest time for the candidate window, because profiles and
   centroids change between runs.

First start backfills all 10,752 existing entries into the archive (embeddings only, ~16M tokens, ~$0.32 once).
Only items ingested since the last sent digest are digest candidates.

### Ranking (stage 1, no LLM)

For item vector `x` (all vectors unit length, so cosine = dot product):

```
fit      = max over profiles p of  cos(x, p)                    projects and topics
taste    = cos(x, C+) - cos(x, C-)    when |positives| >= 10, else 0
prior    = feed_up_rate - 0.5         when the feed has >= 5 votes, else 0
score    = fit + 0.5 * taste + 0.2 * prior                      weights in config
```

`C+` is the mean of the last 500 positives: 👍 items, items filed as ideas, Karakeep saves, GitHub stars.
`C-` is the mean of the last 500 👎 items. The matched profile (argmax of `fit`) is kept as the item's
candidate project.

### Profiles (nightly, 03:00)

A config file `profile.json` (mounted from homelab-gitops; JSON so no YAML dependency) lists:

- **projects**: key, Plane identifier (or none), 3–5 sentence description. Initial set from `repos.toml` notes:
  chargehand, whetstone, senses, nytka, chronicle, synapse, jarvis, content-engine, touchstone, skarbnyk,
  homelab, personal-website, trader.
- **topics**: key and a short description, e.g. dotnet-backend, llm-engineering, agents-and-mcp, memory-systems,
  self-hosting, postgres, passive-income, embedded, ukraine-tech.

The builder embeds each description, plus, for projects with a Plane identifier, the titles of its open work
items (Plane API, `per_page=100`). Descriptions are written by hand; no README fetching in v1.

### DigestRun (07:30 Europe/Kyiv, DST-aware)

1. Candidates: non-duplicate items ingested after the previous sent digest, at most 3 days back.
2. Rank by stage-1 score; take the top `Caps:Triage` (60).
3. **Triage** each with `gpt-4.1-nano`: input title, feed, first 2,000 chars, project and topic one-liners. Output
   JSON `{relevance 0-3, project|null, kind improve|new|fyi, reason ≤ 20 words}`. Unparseable output keeps the
   item with relevance 1.
4. Keep relevance ≥ 2, order by (relevance, stage-1 score), take the top `Caps:Read` (12).
5. Full text: if stored content is under 1,500 chars, call Miniflux `fetch-content` (not stored back into Miniflux).
6. **Deep-read** each with `claude-haiku-4-5`, one call per item: input full text (≤ 24,000 chars), the matched
   project's description, all one-liners. Output JSON `{summary ≤ 2 sentences, why ≤ 1 sentence,
   kind, project|null, suggestion ≤ 2 sentences|null}`. English, whatever the source language.
7. Write the digest row and send.

Triage and read results are stored per item, so a rerun after a crash pays nothing twice. One digest row per
local date; a date already `sent` is never re-sent.

### Telegram

Messages use `parse_mode=HTML`, escaped, each under 4,096 characters.

1. Header: date, `12 of 412 items`, counts per project/topic, yesterday's votes, spend this month.
2. **Ideas** (items with a suggestion), then **Highlights**, one message each:
   `<b><a href=URL>title</a></b>`, `feed · project · kind`, summary, why, `💡 suggestion`.
   Buttons: `👍` `👎`, and `💡 To Plane` when there is a suggestion. `callback_data` is `v:{id}:u`, `v:{id}:d`,
   `i:{id}` (well under the 64-byte limit).

The poller calls `getUpdates` with `timeout=50`, `allowed_updates=["callback_query"]`, stores the offset, and
ignores anyone but `Telegram:AllowedUserId`. Every callback is answered (`answerCallbackQuery`). A vote upserts
(last one wins) and edits the button row to show it. `💡` files the idea (below), counts as 👍, and replaces the
button with `✓ Filed in <Plane project>`.

If a digest run fails, the bot says so once, with the error, and the run retries with backoff until 12:00 local. Delivered messages are counted, so a retry sends only the rest.

### Plane

`💡` → `POST /api/v1/workspaces/homelab/projects/{id}/intake-issues/` with
`{"issue": {"name": suggestion as a title ≤ 80 chars, "description_html": suggestion, why, source link, item id,
"priority": "none"}}`. The project comes from the item's matched project's Plane identifier, else `FEED`. Project
ids are resolved by identifier from `GET /projects/` and cached. Filing is idempotent per item.

### Signals (daily)

- **Karakeep**: `GET /api/v1/bookmarks?limit=100&sortOrder=desc&cursor=...` until a known id. Link bookmarks
  embed `title + description`; polarity +1.
- **GitHub stars**: `GET /users/Egoushka/starred?sort=created&direction=desc&per_page=100` with
  `Accept: application/vnd.github.star+json`, until a known repo. Embed `full_name: description + topics`;
  polarity +1. Unauthenticated (60 requests/hour is plenty).

### MCP (read-only, bearer token, no `Origin`, fail closed; as senses)

| Tool | Returns |
|---|---|
| `feed_search(query, project?, kind?, from?, to?, limit ≤ 50)` | Items by hybrid search: vector top 50 and full-text top 50 merged by reciprocal rank fusion; each with id, title, URL, feed, date, summary if read, votes |
| `feed_read(id)` | One item: full text, triage, read result, votes, idea |
| `feed_digests(before?, limit ≤ 30)` | Digests newest first: date, counts, item ids |
| `feed_digest(date)` | One digest with its items |
| `feed_ideas(project?, limit ≤ 50)` | Filed ideas with Plane ids and source items |

Full-text search uses a generated `tsvector` with the `simple` configuration (sources are English and Ukrainian).

### Hindsight (weekly, Sunday 18:00)

One retain to the `learning` bank: what was read and liked this week, ideas filed. Tags
`signal:feed-eater`, `trust:agent`, `type:weekly-log`. The dead `miniflux` signal is removed from `ingest.py`.

## Data model (Postgres 18 + pgvector 0.8.7)

- `feeds(id PK = Miniflux feed id, title, category, site_url)`
- `items(id bigserial PK, miniflux_entry_id UNIQUE, feed_id, url, canonical_url, title_hash, title, published_at,
  ingested_at, content, embedding vector(1536), score real, profile_key, duplicate_of NULL, search tsvector
  GENERATED)`; HNSW index on `embedding` (cosine), GIN on `search`, index on `canonical_url`.
- `triage(item_id PK, relevance, project, kind, reason, model, at)`
- `reads(item_id PK, summary, why, kind, project, suggestion, model, at)`
- `digests(local_date PK, status building|sent|failed, candidates, triaged, item_ids bigint[], sent_count, note, error,
  sent_at)`: `note` is shown in the header (budget reached, Miniflux down), `error` is the last failure.
- `votes(item_id PK, value smallint ±1, at)`
- `ideas(item_id PK, plane_project, plane_issue_id, title, at)`
- `signals(id PK, source karakeep|github_star, external_id, url, title, embedding, polarity, at,
  UNIQUE(source, external_id))`
- `profiles(key PK, kind project|topic, plane_identifier, description, embedding, built_at)`
- `cursors(name PK, value)`: the Telegram update offset (`telegram:offset`) and one run key per scheduled job
  (`job:<name>`: digest, profiles, signals, weekly-retain), so a job runs once per window.
- `llm_usage(id, at, purpose embed|triage|read|profile|signal|search, model, input_tokens, output_tokens, cost numeric(16,10))`. Cost comes from
  LiteLLM's `x-litellm-response-cost` header.

Retention: everything is kept. ~1 MB a day at today's volume.

## Cost (prices checked 2026-10-05)

text-embedding-3-small $0.02/M; gpt-4.1-nano $0.10/M in, $0.40/M out; Haiku 4.5 $1/M in, $5/M out.

| Stage | Per day | Per 30 days |
|---|---|---|
| Embed (89 items now, 500 at 100 feeds) | 134k–750k tokens | $0.08–0.45 |
| Triage 60 | 90k in, 5k out | $0.33 |
| Deep-read 12 | 72k in, 5k out | $2.90 |
| **Total** | | **~$3.3–3.7** |

Guard: a LiteLLM virtual key `feed-eater` limited to those three models, `max_budget 10`, `budget_duration 30d`.
Over budget LiteLLM returns 401 `ExceededTokenBudget`; DigestRun then sends what it has with "budget reached".

## Sources

Phase 1 cleanup in Miniflux (Yehor approves each):

- Hacker News → `https://hnrss.org/frontpage?points=100` (the current feed is failing).
- r/selfhosted and r/homelab → `/top/.rss?t=day`. Reddit allows ~1 logged-out request per minute per IP; two
  feeds polled by Miniflux's scheduler stay under it. Verify the `top` URL form works first.

Phase 3 grows to ~100 feeds over the topics in `profile.json`, with GitHub release `.atom` feeds for libraries
in use and changedetection for pages without a feed.

## Deployment

New stack `homelab-gitops/feed-eater/`: `feed-eater` + `feed-eater-db` (`pgvector/pgvector:0.8.7-pg18-trixie`,
pinned by digest), subnet `10.211.35.0/24`, port `100.64.0.2:8104` (both free on 2026-10-05; grep again),
joins `edge` for plane-proxy. Reaches Miniflux, Karakeep and LiteLLM at their tailnet addresses.
Secrets in `feed-eater/.env.enc`: DB password, Miniflux API token (new, its own), Karakeep API key, LiteLLM key,
Telegram bot token and user id, Plane token, MCP token, Hindsight key. Registered in agentgateway (`/mcp/feed`),
switchboard and gatus like senses. Image from CI on tag, `ghcr.io/egoushka/feed-eater`.

Yehor's steps: create the bot with BotFather; create Plane project `FEED`; approve the GitHub repo, the release
tag and the homelab-gitops PR.

## Error handling

- Ingestor, signal collectors, profile builder and Telegram poller each run in a loop that backs off up to
  5 minutes on failure (senses' `PollingSense`), and record their health.
- LLM call failure: retried twice; triage failure drops to relevance 1, read failure drops the item from the
  digest. A digest with zero reads is not sent; the bot says why.
- Miniflux down: no new items; the digest goes out with what arrived, and the header says Miniflux was down.
- Telegram send failure: retried until 12:00 local, then `failed` with a note.
- Restart: cursors and per-item results are in Postgres; nothing is fetched or paid for twice.

## Testing

- Unit: URL canonicalisation, title hash, scoring (fixed vectors), triage/read JSON parsing including malformed
  output, digest formatting (HTML escaping, 4,096 limit, button layout), callback parsing, Kyiv 07:30 across the
  2026-10-25 DST change.
- Integration (Testcontainers `pgvector/pgvector:0.8.7-pg18-trixie`): ingest paging and dedupe against recorded
  Miniflux JSON, digest idempotency with stubbed LiteLLM and Telegram, hybrid search ranking, MCP tools through
  `WebApplicationFactory`.
- `selftest.sh` calls every MCP tool against the running stack.
- Live metric: the 👍 rate, shown weekly in the header.

## Phases

1. Ingest + backfill, ranking, profiles, DigestRun, Telegram with votes, LiteLLM key, Miniflux source cleanup.
2. MCP tools, Plane Intake (`💡`), Karakeep and GitHub-star signals, weekly Hindsight retain replacing the dead
   `ingest.py` signal.
3. Source growth to ~100 feeds; tune caps and weights from 4 weeks of votes.

## Open questions to settle first in the plan

- **Q1** Answered by D8: no Pgvector package.
- **Q2** Does `reddit.com/r/selfhosted/top/.rss?t=day` return entries from the box's IP?
- **Q3** Does LiteLLM return `x-litellm-response-cost` on non-streaming chat and embedding calls through this
  proxy version?
- **Q4** Answered: self-hosted Plane accepts `description_html` on intake; `host/opt-homelab/plane-sync.py:129` sends
  it in production.

## Out of scope

Crawling, a web UI, multi-user, summarising whole days of feeds, posting anywhere public, trained ranking models,
audio or video sources.
