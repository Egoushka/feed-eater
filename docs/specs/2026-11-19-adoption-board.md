# Spec: feed-eater becomes a tech adoption board (2026-11-19)

Status: proposed, decisions D1 to D5 from Yehor (2026-11-19). Hand test passed 5 of 5 (see `2026-11-19-adoption-board-handtest.md`).

## Why
The digest went out on each of the last 7 days (4 to 11 items); `feed_feed_ideas` is empty. Reading is not the bottleneck, acting is.
Yehor has ADHD: no caps or time budgets, novelty is the fuel, every new thing leaves a visible trace. His goal: grow into the
engineer he wants to be and own things that earn; once a month something goes out into the world.
Value hypothesis: a card built from his own stack ("what, where in my stack, evidence, effort, gain") gets acted on more than a news item.
Measured so far: 5 of 5 hand-built cards rated act. Cheapest next test: the first 4 weeks of generated cards, acted-on rate >= 40% (starting value, not sourced).
Stop result: under 20% of generated cards picked for a trial after 4 weeks means no new card sources.

## Decisions (Yehor, 2026-11-19)
- **D1 Start from his stack, not the news.** Inventory = repos in `/home/node/work/repos.toml`, their lockfiles (NuGet, npm),
  container images in `personal/homelab` Compose files, skills and MCP servers in `context/ai-setup/inventory.toml`.
  Signals = his feeds plus Trendshift, OSS Insight, deps.dev, ecosyste.ms, HN Algolia (`scratch/research-repo-discovery/REPORT.md`).
  Output = a proposal card: what, where in his stack, evidence, effort, gain. Not version bumps (Renovate/Dependabot do those).
- **D2 Trials picked by hand.** He selects cards; chargehand runs each on a throwaway branch of the matching repo and reports tests,
  benchmark, diff. Nothing runs unless he picked it.
- **D3 Radar.** Card states (assess, trial, adopt, hold) are his tech radar, exported as `radar.json` by PR to
  `personal/personal-website` (its ADR 0003: content stays in git; ADR 0001: the site is never the app). No employer data.
- **D4 Telegram goes.** The digest moves to an ntfy push (ntfy runs in homelab) linking to an inbox page in feed-eater's own web UI.
  Own small PR, first.
- **D5 Project picture.** Per active project, which cards touch it and what is next; read `ws board`, do not duplicate it.

## Findings from the hand test that shape the build
- Cards need an `evidence_checked` flag: signal titles were fetched but not all read; a card says what is UNKNOWN.
- Trial verdicts differ from first impressions: 3 of 5 hand cards ended `hold` after a measured trial. The radar must record the trial result, not only the proposal.
- Inventory-only cards (no signal) are valid; card 5 was one and its trial produced a real regression finding.
- External access can block a trial (card 3: preview feature, HTTP 403). A trial result `blocked` is a state, not a failure.

## Steps (one PR each, in order)

### S1 Telegram off, ntfy on
Remove `src/FeedEater/Telegram/*`, `TelegramOptions`, the poller and the Telegram send path from `DigestRun`, duels, follows, weekly and ideas.
Votes, swipes, duels and approvals already have web routes (`/vote`, `/swipe`); they stay and become the only path.
Add `Push:NtfyUrl`, `Push:Topic`, `Push:Token` options. After the digest is ready, post one ntfy message
"Today: N items" with a click URL to the inbox page (`/` Today page or `/inbox`), held by quiet hours, at most one retry an hour.
Needs no migration. Delete the Telegram tests and docs; keep behavior tests, rewritten against the web path.

Acceptance checks:
- A1.1 `grep -rIi telegram src tests config compose.example.yaml .env.example README.md docs/*.md | grep -v '^docs/\(specs\|plans\|HANDOFF\)'` prints nothing.
- A1.2 `dotnet build -warnaserror` and `dotnet test` pass (CI; the devbox has no Docker).
- A1.3 test `Push_posts_one_ntfy_message_per_digest`: fake ntfy server receives one POST with the inbox URL in `Click`, and none inside quiet hours.
- A1.4 test `Push_retries_at_most_once_an_hour`.
- A1.5 live, after deploy: `lab logs feed-eater` shows `ntfy ok` for the next digest and no `telegram` lines; the homelab Compose and SOPS env drop `FeedEater__Telegram__*` (separate homelab PR, by Yehor's merge policy for the box).

### S2 Cards (needs a migration: waits for Yehor's merge)
Tables (migration `db/migrations/<next>_cards.sql`, squawk-clean, additive only): `inventory_item(kind, name, version, repo, source_path, seen_at)`,
`card(id, title, what, repo, where_in_stack, evidence jsonb, effort, gain, state, state_reason, created_at, source_signal_ids)`;
`state` in `proposed | assess | trial | adopt | hold | dropped`.
Inventory collector reads the repos in D1 from a mounted checkout or the GitHub API (security skill: read-only token, no secret in logs).
Card generator joins inventory to ranked items and external signals (HN Algolia, deps.dev, Trendshift) and writes cards; a version bump never becomes a card.
Inbox page `/cards` (loads the `ui-design` skill, screenshot gate): list by state, card detail with evidence links, buttons "run trial" and "hold".
MCP tools `feed_cards` (list) and `feed_card_set_state`.

Acceptance checks:
- A2.1 `dotnet test --filter Cards` passes: inventory parses a sample csproj, `package.json`, Compose file and `inventory.toml`; a version-bump-only candidate yields no card; a card without `where_in_stack` is rejected.
- A2.2 `squawk db/migrations/<next>_cards.sql` exits 0 and the migration test applies it on a fresh Postgres 18 container.
- A2.3 `GET /ui/cards` returns 200 with one row per card, and Playwright screenshot at 390 px and 1280 px passes the `ui-design` gate (recorded in the PR).
- A2.4 live: `feed_cards` returns at least 5 cards for the 3 repos of the hand test within one day of deploy, each with at least one evidence URL.
- A2.5 `feed_card_set_state` rejects an unknown state and writes a `state_reason`.

### S3 Trials (needs a migration; waits for Yehor's merge)
`trial(id, card_id, status, branch, repo, requested_at, finished_at, tests, benchmark, diff_stat, result_md)`; status
`queued | running | passed | failed | blocked | cancelled`. Only a card with state `trial` set by Yehor (button or `feed_card_set_state`) can be queued.
A runner hands the job to chargehand (`orchestrate`), which works on a throwaway branch of the matching repo and returns tests, benchmark and diff; the result is stored and shown on the card. The runner never pushes to a default branch and never opens a PR itself.

Acceptance checks:
- A3.1 test `A_card_not_picked_is_never_queued`: generated cards in state `proposed` produce zero trial rows after a runner pass.
- A3.2 test `Trial_result_is_stored_with_tests_and_diff` against a fake chargehand.
- A3.3 test `Blocked_trial_keeps_its_reason` (HTTP 403 style failure leaves status `blocked`, not `failed`).
- A3.4 live: queue the hand-test card 4 trial; `trial.status = passed` and `diff_stat` non-empty on the row.
- A3.5 `grep -rn "git push" src` finds no push in the runner code.

### S4 Radar on the site
Export `radar.json` (fields: name, quadrant, ring = state, since, one-line reason, trial result summary, repo-kind `personal` only) and open a PR to
`personal/personal-website` under `content/` (check its `CLAUDE.md` for the path and the schema before coding). A repo whose `kind` is `work` is never exported.
The site renders it; the site stays read-only (ADR 0001).

Acceptance checks:
- A4.1 test `Radar_never_exports_work_repos`: inventory containing a `kind = work` repo yields no row in `radar.json`.
- A4.2 `radar.json` validates against the JSON schema checked into the site repo; the site's own CI passes on the PR.
- A4.3 the export fails if any row names a term from the workspace work-terms denylist (the same list leak-guard uses); test `Radar_export_rejects_denylisted_terms`.
- A4.4 live: the site build renders the radar page and `curl`ing it returns each `adopt` card name.

### S5 Project picture (after S4; separate small PR)
Per active project from `ws board`, list the cards that touch it and the next trial; reads `ws board --json` if it exists, otherwise parses `repos.toml`; does not duplicate board fields.
Acceptance: test `Project_picture_lists_cards_per_repo`; `/cards?repo=<name>` returns only that repo's cards.

## Out of scope
Version bumps (Renovate), any chat bot, auto-running trials, merging or deploying a trial branch, employer data, a public API.

## Open risks
- Card quality depends on signals; OSS Insight trends is UNREACHABLE (2026-11-19); ecosyste.ms not yet tested.
- The devbox has no Docker, so DB tests run only in CI.
- Removing Telegram also removes the phone path for votes until the Android app or the web UI covers it; web vote works today.
