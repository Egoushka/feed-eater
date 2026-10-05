# Handoff — feed-eater (2026-10-05, after Tasks 0–16)

## Goal

A .NET service that reads every Miniflux entry, sends a daily Telegram digest of what matters to Yehor's projects,
learns from 👍/👎/💡, files ideas into Plane Intake, and serves the whole archive over MCP.

## State

- Branch `feat/v1` in worktree `.claude/worktrees/v1`, 32 commits on top of `main` (`b5d8745`). Not pushed; no GitHub repo
  yet; not in `~/Projects/repos.toml` yet.
- Tasks 0–16 of `docs/plans/2026-10-05-feed-eater-v1.md` are done. Each was reviewed against its plan section and
  for code quality, then the whole branch had a final review. The final review's three Important findings are fixed
  and re-reviewed.
- `dotnet test FeedEater.slnx`: 127/127 passing (Testcontainers, pgvector 0.8.7-pg18). `docker build` succeeded locally.
- Preflight answers: `docs/specs/2026-10-05-preflight.md`. LiteLLM listens on `100.64.0.2:4000` only; Reddit `top`
  feeds return 200 from the box.
- Task 17 (deploy) has not started. Every step in it is externally visible and needs Yehor's go-ahead.

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
- The rulings ledger, task reports and reviews are in `.superpowers/sdd/2026-10-05-feed-eater-v1/` (git-ignored).
  `final-review.md` lists the parked minor findings for v0.2:
  - the "Miniflux unreachable" note also fires on embed failures;
  - backfilled items have no `profile_key`, so they miss the `feed_search` project filter;
  - a GitHub failure blocks Karakeep signals;
  - the digest job runs without a Telegram token.

## Tried and failed

- `dotnet` (MSBuild socket), Testcontainers and `git commit` all fail inside the Bash sandbox, so run them with the
  sandbox off.

## Next step

1. Yehor approves the branch. Then merge `feat/v1` to `main` (squash or merge, Yehor's choice) and remove the worktree.
2. Task 17 (deploy): create the public GitHub repo, push, tag `v0.1.0` for the image, add the stack in
   `homelab-gitops` from a separate session, create the Telegram bot, the LiteLLM virtual key ($10/30d), the profile
   file and the secrets. Ask before each step.

## How to verify

- Branch: `dotnet test FeedEater.slnx` is green (127/127).
- After deploy: `hz deployed <sha> --wait 10m`, then `FEED_MCP_TOKEN=... ./selftest.sh http://100.64.0.2:8104`, then
  the first digest in Telegram by 08:00 Kyiv.
