# Handoff — feed-eater, current state (2026-10-07, v0.6.0 live, v0.8.0 released)

"continue" means: read this section; the older v0.1 handoff below is history.

- `main` = 2118fc8 (v0.8; tags v0.2.0, v0.3.0, v0.5.0, v0.6.0, v0.7.0, v0.8.0; #3 closed unmerged).
- Next scheduled digest: 2026-10-07 07:30 Kyiv. Check it landed and read the 7-day 👍 rate in its header.
- Validation table (`docs/specs/2026-10-05-validation.md`): first row due Monday 2026-10-12, still empty.
- The parked v1 review findings (M1 to M3 in the old section below) were fixed in v0.2.
- v0.6.0 is deployed (feed-eater#5 merged as 73edb31, tag v0.6.0, homelab-gitops#658 merged as 483cbd8, live at 17:08Z, healthy,
  0 migrations). It adds /ask with checked citations, free-text replies to items, and the /learn switch.
- Open check: the first reply to a digest item. The item id is read from the buttons of the replied-to message (no stored
  map), and the Bot API docs don't say whether `reply_to_message` carries `reply_markup`. If a reply to an item falls through to
  search, add a sent-message to item table instead.
- Votes so far: 10 up, 1 down; the learned ranking needs 100 with 10 of each.

## v0.7 and v0.8 released, not deployed (2026-10-07)
Specs: `docs/specs/2026-10-06-v0.7-portable.md`, `docs/specs/2026-10-06-v0.8-learns-faster.md` (each ends with what was built differently).
Decided with the owner: build all 14 buildable ideas across v0.7 (portable), v0.8 (learns faster), v0.9 (hooks to the owner's setup); no social
network or public publishing; one PR per version; every integration optional.
- **Released:** v0.7.0 (feed-eater#6, 03b70b9) and v0.8.0 (feed-eater#7, 2118fc8); images `ghcr.io/egoushka/feed-eater:0.7.0` and `:0.8.0`
  for amd64 and arm64. Each had a read-only risk review by a second agent; v0.7's found 8 defects, v0.8's found 3 medium (job stalls) and 6 low; all
  fixed with tests that failed first. 1122 tests green.
- **NOT deployed:** the owner's stack still runs 0.6.0 (live, healthy). Deploy order (do not skip): 1) merge homelab-gitops#682 (states every value that used
  to be a code default; no behaviour change on 0.6.0; recreates the container for seconds); 2) `scripts/pin-image.sh --pr feed-eater 0.8.0` in homelab-gitops
  (migrations 0010 to 0012 and 0014 to 0019 are all cheap: new tables and columns); 3) after deploy, `doctor` in the container must show no FAIL and the
  feed source must read Miniflux; 4) optionally `FeedEater__GitHub__Token` for Hype autopsy (secret via `secret.sh`).
- **v0.8 contents:** vote weight, Duel, Follow this story, Hype autopsy, Taste map. Not built: Shipped-it (0013) and Backlog bloodhound (0017).
  Spike S1 (does the stored Plane intake id resolve as a work item with a state group?) is answered by Plane's tool contract (`issue` field of an
  intake record is the work item id) but not confirmed live: the `ideas` table and the FEED intake queue are empty, so press 💡 once, then check.
  Spike S2 (comment endpoint) writes a throwaway issue and comment in the owner's Plane and needs the owner's yes. Shipped-it must treat a failed lookup
  as "unknown", never as an error.
- **Still to verify live:** the first real digest and `/start` with a real key and bot; reply-to-item in Telegram (Bot API docs do not say whether
  `reply_to_message` carries the buttons); the Hindsight ping path `GET v1/default/banks`; Duel's first pairs; the first autopsy needs 87 days.
- **v0.9:** not started; outline and integration facts at the end of the v0.7 spec. Write its own spec first, as for v0.7 and v0.8.
- **Follow-ups:** `config/watch.example.json` is still the owner's list (replace with a neutral 5-row example); doctor shows Miniflux twice in Miniflux
  mode; a single poison entry mid-feed can still block newer entries of that feed (no per-entry catch); a 403 with `Retry-After` but no
  `X-RateLimit-Remaining: 0` is treated as an ordinary GitHub failure; a Telegram outage burns Duel pairs from a small pool.

---

# Handoff — feed-eater (2026-10-05, Tasks 0–16 merged, Task 17 deployed except Telegram token and Miniflux cleanup)

## Goal

A .NET service that reads every Miniflux entry, sends a daily Telegram digest of what matters to Yehor's projects,
learns from 👍/👎/💡, files ideas into Plane Intake, and serves the whole archive over MCP.

## State

- `main` = `f0026b8` (squash of `feat/v1`), pushed to the public repo https://github.com/Egoushka/feed-eater. CI is green.
- Tag `v0.1.0` pushed; image `ghcr.io/egoushka/feed-eater:0.1.0` is published,
  digest `sha256:65da5b60b72f1113e57808a8ef3e82e89a187469eb36ca0c30028f01a56c5286`.
- The `repos.toml` entry is in PR https://github.com/Egoushka/workspace/pull/63 (open, not merged).
- Tasks 0–16 of `docs/plans/2026-10-05-feed-eater-v1.md` are done. Each was reviewed against its plan section and
  for code quality, then the whole branch had a final review. The final review's three Important findings are fixed
  and re-reviewed.
- `dotnet test FeedEater.slnx`: 127/127 passing (Testcontainers, pgvector 0.8.7-pg18). `docker build` succeeded locally.
- Preflight answers: `docs/specs/2026-10-05-preflight.md`. LiteLLM listens on `100.64.0.2:4000` only; Reddit `top`
  feeds return 200 from the box.
- Plane project `FEED` (id `c0eb0fc1-ef92-4559-947e-ceb29598dfaa`) exists. Intake is on for FEED, CHARGEHAND, WHET,
  NYTKA, CHRON, JARVIS, TOUCH, SKAR and LAB; the FEED `intake-issues/` endpoint answers 200. The Plane MCP's
  `get_features`/`update_features` return 404 on this self-hosted Plane, so Intake was enabled with
  `PATCH /api/v1/workspaces/homelab/projects/<id>/ {"intake_view": true}` from the box, using plane-sync's token.
- Task 17 Steps 1, 2 and 4 are done; Step 3 and Steps 5–10 remain. Every step is externally visible and needs Yehor's go-ahead.

## Decisions taken during execution (deviations from the plan)

The spec won over the plan wherever they disagreed.

- The digest header says "spend this month" (counted from the 1st, Kyiv time) and adds a "7-day 👍 rate" line.
  If you want a 30-day window to match the LiteLLM budget, it is a one-line change.
- The `selftest.sh` script calls all 5 MCP tools.
- Ideas go to the read's project, else the ranking's matched profile, else `FEED`.
- If handling a button press fails, it is still answered.
- Candidates are embedded, non-duplicate items published in the last 3 days and not triaged before today. This
  replaces the plan's `ingested_at > last sent_at` bound, which lost items after a late resend and the whole
  first-start backlog. Same-day retries reuse the stored triage results.
- A LiteLLM outage (every triage call, or every read, failing on transport) makes the run throw. The job then retries
  until 12:00 instead of sending "No digest today". An exhausted budget still sends what was read.
- A digest that never sends is marked `failed` with a note when the next day's run starts.
- The embedder skips an input only on 400/413/422. It retries that input once at 2,000 chars, then skips it until
  restart. Other errors rethrow so the loop backs off.
- `llm_usage.cost` is `numeric(16,10)`; it was edited in `0001_init.sql` in place because the migration is not deployed.
  Indexes were added on `items.published_at`. HNSW iterative scan is on for filtered search.
- LiteLLM budget errors are recognised by `Budget has been exceeded`, `budget_exceeded` and `ExceededBudget`. This
  was checked against the LiteLLM source running on the box.
- Accepted risks:
  - If the process dies between the Plane POST and the ideas insert, the item can be filed twice.
  - An unparseable deep read is paid for again on rerun.
- The rulings ledger, task reports and reviews are in `~/Projects/personal/feed-eater/.superpowers/sdd/2026-10-05-feed-eater-v1/` (git-ignored).
  `final-review.md` lists the parked minor findings for v0.2:
  - the "Miniflux unreachable" note also fires on embed failures;
  - backfilled items have no `profile_key`, so they miss the `feed_search` project filter;
  - a GitHub failure blocks Karakeep signals;
  - the digest job runs without a Telegram token.

## Tried and failed

- `dotnet` (MSBuild socket), Testcontainers and `git commit` all fail inside the Bash sandbox, so run them with the
  sandbox off.

## Next step

1. Merge workspace PR #63.
2. Task 17 Step 3 (Yehor only, secrets never pass through chat): create the Telegram bot, the Miniflux API key, the LiteLLM
   virtual key, and the Plane, Karakeep and Hindsight tokens, plus `DB_PASSWORD` and `FEED_MCP_TOKEN`. Store each with
   `make secret-set STACK=feed-eater KEY=<k>`.
   **The plan's LiteLLM key command is wrong:** it calls `127.0.0.1:4000`, but LiteLLM listens only on `100.64.0.2:4000`
   (see preflight). Use `http://100.64.0.2:4000/key/generate`.
3. Steps 5–10 happen in a separate `homelab-gitops` session and worktree, as one PR: compose stack, pins (image digest
   above, plus `pgvector/pgvector:0.8.7-pg18-trixie`), agentgateway route `/mcp/feed`, gatus, ingest. The plan's Task 17
   lists each step.

## How to verify

- Branch: `dotnet test FeedEater.slnx` is green (127/127).
- After deploy: `hz deployed <sha> --wait 10m`, then `FEED_MCP_TOKEN=... ./selftest.sh http://100.64.0.2:8104`, then
  the first digest in Telegram by 08:00 Kyiv.

## Task 17 deploy (homelab-gitops, 2026-10-05, later session)

- Deployed: PRs Egoushka/homelab-gitops#572 (stack), #573 (TELEGRAM_USER_ID was a JSON list `[id]`; now bare), #574
  (Miniflux base URL `http://miniflux:8080/`, because Miniflux publishes only 127.0.0.1:8092 on the host).
  `feed-eater` is healthy at `100.64.0.2:8104`; `selftest.sh` prints ok for all 5 tools; ingest is running (5,214 items after ~6 min).
- Subnet is `10.211.36.0/24` (the plan's 10.211.35 belongs to router-actions). MCP is a target `feed` on `/mcp/homelab`, no
  separate `/mcp/feed` route. Also needed in the PR: `policy/budget.yaml` (896m), a `policy/baseline/homepage-tile.txt` line,
  `FEED_MCP_TOKEN=ci` in `.github/workflows/ci.yml`, and a regenerated `db-dump.targets`.
- Secrets: `feed-eater/.env.enc` holds everything except `TELEGRAM_BOT_TOKEN`. LITELLM_KEY (alias feed-eater, $10/30d) was
  generated against `100.64.0.2:4000`. The Mac has no age key; set secrets in `/root/gitops-work` on hedzer, `scp` the `.enc` back, PR it.
- `miniflux` ingest signal removed from `ingest.py` and cron.

## Still open

1. Step 10: four-week validation, one row each Monday in `docs/specs/2026-10-05-validation.md`.
2. Watch the first scheduled digest on 2026-10-07 at 07:30 Kyiv and the 👍/👎 rate in its header.
3. Parked v0.2 findings: see `final-review.md` listed above.

## Done since the deploy

- `TELEGRAM_BOT_TOKEN` is deployed (homelab-gitops#575); `@feed_hrabovsky_bot` accepts it. Ingest finished (10,776 items) and all were embedded.
- Step 9 done: Miniflux feeds 13 (Hacker News, `hnrss.org/frontpage?points=100`), 17 and 16 (r/homelab, r/selfhosted `top/.rss?t=day`) updated; no parse errors.
- The first digest was sent early on 2026-10-06 at 00:20 Kyiv (13 items from 140 candidates, 60 triaged) by setting
  `FeedEater__DigestAt` to 00:20 for one run (homelab-gitops#576), reverted in #578. That day's digest key is consumed,
  so the next one is 2026-10-07 at 07:30. There is no manual trigger: to run one off-schedule, repeat that temporary PR.
- Yehor tried it in Telegram and reported it works.
