# Workbench: master plan

> **For agentic workers:** each step below is one GitHub issue and one PR, implemented by a chargehand worker (label `chargehand`) or by
> superpowers:subagent-driven-development. A step is self-contained: its section, the Global constraints and the spec are the whole brief.
> Write code test-first: the named tests fail before the code exists. A step is done when every command under **Acceptance** passes on the PR.
> Checkboxes (`- [ ]`) track the tests and deliverables.

**Goal:** a desktop-first research web app at `/app` (search, reader, related items, stories, tags, topics, highlights, notes, Ask, Promote)
on top of feed-eater's archive (spec: `docs/specs/2026-10-26-workbench.md`).

**Architecture:** a SvelteKit SPA in `web/`, built into the image and served by the existing .NET process at `/app`. It talks JSON to a
cookie-authenticated `/app/api` whose handlers are thin route-group extensions over services in `src/FeedEater/Workbench/`. New tables for
bodies, tags, topics, highlights, notes; everything relational (related items, stories, topic membership, timelines, facets) is computed per
request from the vector and text indexes. Lexical search goes behind `ILexicalSearch` with a BM25 implementation and today's tsvector one.

**Tech stack:** .NET 10 minimal APIs, Dapper + Npgsql, Postgres 18 with pgvector and an optional BM25 extension, xUnit + Testcontainers +
`WebApplicationFactory`; Svelte 5, SvelteKit (`adapter-static`), TypeScript strict, Vite, Vitest, Playwright, CodeMirror 6, markdown-it;
Node 24 at build time only.

**Spec:** `docs/specs/2026-10-26-workbench.md`. Depends on v1.0 step 5 (`docs/plans/2026-10-19-v1.0-app.md`) being merged and 0.9.0 released.

## How this plan is cut (rulings)
- **R1. Code-level detail is written per step, against `main` as it is then** (the repository's practice since v1.0: "each step gets its own
  detailed plan written against `main` as it is when the step starts"). This file fixes what each step delivers, the names other steps rely
  on, the tests that prove it and the commands that must pass. A worker reads the files named in its step before writing code.
- **R2. One step = one issue = one PR**, merged when its acceptance checks and CI pass (the weekly cycle, `~/Projects/context/cycle.md`).
  Releases follow the cycle: every merged step is a patch-level change, the Friday release cuts the next patch for what merged that week,
  and the step that closes a milestone is followed by an empty commit with the footer `Release-As: 0.N.0`. Milestones are named goals in
  Plane ("Reader and search", "Tags and topics", "Notes and ideas"); the version numbers here are expectations, not names.
- **R3. Each step is end to end** (migration, service, endpoint, SPA screen, tests) when it is small enough to review in one sitting;
  otherwise the server half comes first and the screen follows as its own step.
- **R4. Server code lives in `src/FeedEater/Workbench/<Feature>/`** (service, store, `Map<Feature>Api(RouteGroupBuilder)`), stores in the
  same folder rather than `Storage/`, so a feature can be read in one place. Shared pieces that already exist (`ItemStore`, `ArchiveSearch`,
  `SafeFetcher`, `PlaneClient`, `IInbox`) are reused, not copied.
- **R5. Browser tests never need a database.** Server tests write JSON fixtures to `tests/contract/app/` (as `ContractTests` does for
  `/api/v1`, regenerated with `UPDATE_CONTRACT=1`); Playwright serves the built SPA and answers `/app/api/*` from those fixtures.

## Global constraints
- Every integration stays optional. An instance that never opens `/app` behaves as `main` does; the stock `pgvector/pgvector:pg18` image keeps working.
- `/ui`, `/swipe`, `/mcp` and `/api/v1` keep their auth rules unchanged. `/api/v1` keeps refusing requests with an `Origin` header (`ApiAuth`).
- `/app/api`: `fe_session` cookie required; non-GET requests need header `X-CSRF` equal to `UiSession.AntiForgery(cookie)` and pass
  `UiGuard.SameOrigin`; no `Mcp:Token` → 503. Errors are RFC 7807 problem details.
- `/app` CSP exactly: `default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'`.
- Feed-derived text is never inserted as HTML in the SPA except `item_bodies.html`, which is sanitized on the server. Notes render with
  markdown-it `html: false`.
- The swipe path calls no LLM; dwell never feeds ranking (swipe mode's guards). The workbench's only model calls: embeddings (queries, notes,
  tag descriptions) and Ask.
- Migrations continue after the highest number on `main` when the step starts, one file per step that needs one, applied by `DatabaseMigrator`.
  Every new table goes into `PostgresFixture.ResetAsync`'s truncate list and into `CombinationTests`' migration lists.
- New options get a `[Description]` in `FeedEaterOptions.cs`; regenerate `docs/CONFIG.md` with `UPDATE_CONFIG_DOC=1 dotnet test --filter ConfigDocTests`.
- Endpoint test classes share one host per class (see `ApiTests`/`ApiHost`); building a host per test exhausts Postgres connections.
- `OwnerTermsTests` stays green and, from W1, scans `web/src`.
- Server checks: `dotnet format FeedEater.slnx --verify-no-changes` and `dotnet test FeedEater.slnx` (needs Docker).
  Web checks (from W1): `cd web && npm ci && npm run check && npm test && npm run e2e && npm run build && npm run size`.
- **Where checks run.** chargehand's driven sessions have no Docker client, Node or Playwright (session image; ADR 0078 in chargehand). A
  worker writes the tests first, verifies in the session with `dotnet build FeedEater.slnx -c Release` and `dotnet format FeedEater.slnx
  --verify-no-changes`, and leaves the Testcontainers suite and the web checks to the PR's CI, which runs them all. A step's **Acceptance**
  commands are what CI runs; the PR is done when CI is green. When the session image gains Node (the planned mise image), the web checks
  move into the session too.
- **Contract fixtures without Docker.** From W1, when `ContractTests` fail in CI, the job reruns them with `UPDATE_CONTRACT=1` and uploads
  `tests/contract/` as the artifact `contract-fixtures`; the worker downloads it (`gh run download <run> -n contract-fixtures -D tests/contract`),
  reviews the diff and commits it.
- Comments: few, and only about why. No secrets, env dumps or process listings in logs, tests or transcripts.
- If `dotnet restore` returns 401, a user-level NuGet config is adding a private feed: pass `--source https://api.nuget.org/v3/index.json`.

## Review focus (failure modes no single step's tests cover)
1. **Hostile article HTML reaches the browser.** A feed links a page with `<script>`, `onerror=`, `javascript:` links, SVG with script,
   `<style>` hiding text, `<meta http-equiv=refresh>`. Expected: sanitized away, and the CSP blocks anything missed. Owned by W4 (sanitizer
   corpus test `ReaderBodyTests.Hostile_markup_is_removed` with each of these) and W1 (CSP header test).
2. **A slow or dead embedder makes search feel broken.** Expected: keyword results within the lexical budget, `partial: true`, no spinner
   past 1.5 s. Owned by W2 (`AppSearchTests.Hybrid_returns_partial_lexical_when_embedder_is_slow` with a fake embedder that waits 10 s).
3. **Two tabs edit the same note.** Expected: the second save gets 409 with the server version, nothing is silently overwritten. Owned by
   W12 (`NotesApiTests.Stale_update_is_409`) and W13 (Playwright `note-conflict.spec.ts`).
4. **Promote clicked twice, or retried after a timeout.** Expected: one Plane item / one GitHub issue. Owned by W15
   (`PromoteTests.Second_promote_returns_first_url_and_files_nothing`, also with the first call timing out after the remote created the issue:
   the timeout is recorded as failure only if no issue with the note's marker exists — see W15).
5. **The archive grows 4× (60k items, a year from now).** Expected: budgets still hold. Owned by W2/W5 (`PerfTests` seed 20,000 items and
   assert 3× headroom; W3a's benchmark also runs at 60,000 synthetic rows).

## Steps

Each step: **Release**, **Depends on**, what it delivers, **Files**, **Produces** (names later steps use), **Tests**, **Acceptance**, **Not here**.

---

### W0 — English stemming for keyword search (can be filed now)
**Release:** patch · **Depends on:** nothing (independent of the workbench; improves `/ui`, Telegram, MCP and `/api/v1` search today).

Today `items.search` is built with `to_tsvector('simple', …)` (`0001_init.sql`) and queried with `websearch_to_tsquery('simple', @text)`
(`src/FeedEater/Storage/ItemStore.cs:563`, the only query site), so English words are not stemmed: "databases" does not find "database".
Switch both to `'english'`. The archive is mostly English; non-English items lose nothing they have today except exact-stopword matches.

**Files:**
- Migration `00NN_english_search.sql`: `drop index items_search_idx; alter table items drop column search; alter table items add column search tsvector generated always as (setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('english', left(content, 100000)), 'B')) stored; create index items_search_idx on items using gin (search);`
  (rewrites the table once: seconds at 15k rows; note the rewrite in `docs/SETUP.md`'s upgrade section).
- `src/FeedEater/Storage/ItemStore.cs:563`: `websearch_to_tsquery('english', @text)`.

**Tests:**
- [ ] `SearchTests.English_plural_finds_singular` ("databases" finds an item titled "Database migrations"; fails on `main`).
- [ ] `SearchTests.Stemmed_verb_forms_match` ("indexing" finds "indexes").
- [ ] `SearchTests.Exact_title_still_ranks_first` (no regression on a known-item query).
- [ ] `CombinationTests` lists include the migration.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~SearchTests|FullyQualifiedName~CombinationTests"
```
**Not here:** BM25 (W3b).

---

### Milestone M1 — Reader and search (0.9.x patches, closed by 0.10.0)

### W1 — App shell, `/app` hosting and `/app/api` auth
**Release:** patch · **Depends on:** 0.9.0 released.

Delivers: `web/` SvelteKit project (Svelte 5, TypeScript strict, `adapter-static` with `fallback: 'index.html'`, `ssr = false`,
`paths.base = '/app'`), an empty three-pane layout with the top navigation (Home, Search, Topics, Notes, Ask) and a theme that follows
the system; the Dockerfile builds it and the app serves it; `/app/api` with its auth filter and `GET /app/api/me`; `/ui/login?return=`; CI.

**Files:**
- Create `web/` (`package.json` with scripts `dev`, `build`, `check` = `svelte-check --fail-on-warnings`, `test` = `vitest run`,
  `e2e` = `playwright test`, `size` = bundle-size check failing above 150 KB gzip for the entry chunk; `svelte.config.js`, `vite.config.ts`,
  `tsconfig.json`, `playwright.config.ts`, `src/app.html`, `src/routes/+layout.svelte`, `src/routes/+layout.ts` (`export const ssr = false`),
  `src/lib/api.ts`, `src/lib/session.ts`, `tests/e2e/shell.spec.ts`, `tests/e2e/fixtures.ts`).
- Create `src/FeedEater/Workbench/AppHosting.cs` (`MapFeedEaterApp`: static files from `wwwroot/app` under `/app` with the cache rules
  of the spec, SPA fallback for `/app/{**path}` that is not `/app/api`, CSP and `X-Content-Type-Options: nosniff` on every `/app` response;
  unauthenticated page requests → 302 to `/ui/login?return=<path>`).
- Create `src/FeedEater/Workbench/AppAuth.cs` (endpoint filter per Global constraints) and `src/FeedEater/Workbench/AppApi.cs`
  (`MapFeedEaterAppApi`: the `/app/api` group, `GET /me` → `AppMe(string Csrf, string Version, AppCapabilities Capabilities)` with
  `AppCapabilities(bool Plane, bool GitHub, bool Bm25)`).
- Modify `src/FeedEater/Program.cs` (map both after `MapFeedEaterUi`), `src/FeedEater/Ui/UiHandlers.cs` (login keeps `return` if it is a
  path starting with `/app` or `/ui` and redirects there after login), `Dockerfile` (stage `FROM node:24-bookworm-slim AS web`, `npm ci && npm run build`,
  copy `web/build` to `/app/wwwroot/app` in the final stage), `.github/workflows/ci.yml` (job `web`: setup-node 24, the web checks, Playwright
  browsers installed with `npx playwright install --with-deps chromium`; in job `test`, on failure of `ContractTests` a step reruns them with
  `UPDATE_CONTRACT=1` and uploads `tests/contract/` as artifact `contract-fixtures`, per Global constraints), `tests/FeedEater.Tests/OwnerTermsTests.cs` (scan `web/src`),
  `.gitignore` (`web/node_modules`, `web/build`, `web/.svelte-kit`, `src/FeedEater/wwwroot/app`).
- `src/lib/api.ts`: `get<T>(path, {signal})`, `send<T>(method, path, body)` adding `X-CSRF` from `session.ts`; in-flight dedupe by URL;
  LRU of 500 GET responses with `invalidate(prefix)`; `prefetch(path)`; on 401 → `location.assign('/ui/login?return=' + current path)`.

**Produces:** `MapFeedEaterAppApi` group builder that later steps extend with `group.Map<Feature>Api()`; `AppCapabilities`; `web/src/lib/api.ts`
(`get`, `send`, `prefetch`, `invalidate`); the layout slots `list`, `reader`, `context`; `tests/contract/app/` folder and the
`AppContract.Write(name, object)` test helper.

**Tests:**
- [ ] `AppShellTests.Page_without_cookie_redirects_to_login_with_return` (GET `/app/search` → 302 `/ui/login?return=%2Fapp%2Fsearch`).
- [ ] `AppShellTests.Page_with_cookie_serves_index_with_csp` (200, exact CSP string, `nosniff`, `Cache-Control: no-store`).
- [ ] `AppShellTests.Hashed_asset_is_immutable` (a file under `/app/_app/immutable/` has `max-age=31536000, immutable`).
- [ ] `AppShellTests.Api_without_cookie_is_401_problem`, `…Post_without_csrf_is_403`, `…Post_cross_origin_is_403`, `…No_mcp_token_is_503`.
- [ ] `AppShellTests.Me_returns_csrf_that_post_accepts`.
- [ ] `AppShellTests.Api_v1_still_refuses_browsers` (Origin header → 403, unchanged).
- [ ] `LoginReturnTests.Return_outside_app_or_ui_is_ignored` (`return=https://evil.example` and `//evil.example` → redirect to `/ui`).
- [ ] Vitest `api.test.ts`: two concurrent `get` of one URL make one fetch; `send` sets `X-CSRF`; 401 navigates to login with return.
- [ ] Playwright `shell.spec.ts`: layout renders three panes at 1400 px, list+reader stacked at 600 px; no console errors; no request to another origin.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~AppShellTests|FullyQualifiedName~LoginReturnTests|FullyQualifiedName~OwnerTermsTests"
cd web && npm ci && npm run check && npm test && npm run e2e && npm run build && npm run size
docker build -t feed-eater:w1 .
```
**Not here:** any feature screen.

---

### W2 — Search API with modes, facets and the query-embedding cache; Search screen
**Release:** patch · **Depends on:** W1.

Delivers: `GET /app/api/search` per the spec (items only; notes join in W12), `query_embeddings`, the 1.5 s partial rule, and the Search
screen (box, instant results, facets, keyboard, URL state).

**Files:**
- Migration `00NN_workbench_search.sql`: `query_embeddings` (spec), plus `create index items_published_month_idx on items (date_trunc('month', published_at at time zone 'UTC'))` only if `PerfTests` show the month facet needs it.
- Create `src/FeedEater/Workbench/Search/AppSearch.cs` (`SearchAsync(AppSearchQuery q, CancellationToken ct) → AppSearchResult`),
  `QueryEmbeddingCache.cs` (`GetOrEmbedAsync(string text, TimeSpan wait, CancellationToken ct) → float[]?`; normalizes whitespace and case,
  hashes model + text; `null` when `wait` passes, while the embedding keeps going in the background and is stored for the next call),
  `AppSearchStore.cs` (one SQL statement: candidates, RRF, boosts, page, facets), `SearchApi.cs` (`MapSearchApi`).
- Records: `AppSearchQuery(string Q, string Mode, long[] Tags, long[] Feeds, string[] Kinds, DateTimeOffset? From, DateTimeOffset? To, bool? Saved, bool? Notes, string? Cursor, int Limit)`;
  `AppHit(string Type, long Id, string Title, string? Url, string? Feed, DateTimeOffset At, string? Summary, string Snippet, int[] Marks, int? Vote, bool Saved, int Also, double Rank)`
  (`Marks` are start/length pairs into `Snippet`, never HTML); `Facet(string Key, string Label, int Count)`;
  `AppSearchResult(IReadOnlyList<AppHit> Hits, IReadOnlyDictionary<string, IReadOnlyList<Facet>> Facets, string? Cursor, bool Partial)`.
- Boosts (constants in `AppSearchStore`, each covered by a test): vote 👍 +0.002, saved +0.002, in-filter confirmed tag +0.001 on the RRF score.
- Modify `src/FeedEater/Storage/ItemStore.cs` only to extract the shared filter SQL if needed; `ArchiveSearch` keeps its behaviour for MCP,
  Telegram and `/api/v1`.
- Weekly cleanup of `query_embeddings` rows unused for 90 days (in an existing weekly job or a new `ScheduledJob`).
- Web: `src/routes/search/+page.svelte`, `src/lib/search.svelte.ts` (state: query, filters, results; lexical request on input debounced
  120 ms, earlier one aborted; hybrid after 400 ms idle or Enter; merging keeps the selected id selected), `src/lib/components/ResultList.svelte`,
  `Facets.svelte`, `Snippet.svelte` (renders `Marks` as `<mark>` around text nodes). Keys: `/` focus box, `j`/`k` move, `Enter` open
  (navigates to `/app/items/{id}`, a placeholder until W4), `Esc` back to the box. State in the URL query.

**Produces:** `AppSearch.SearchAsync`, `AppSearchQuery`, `AppHit`, `QueryEmbeddingCache.GetOrEmbedAsync` (used by topics, Ask, notes);
`web/src/lib/search.svelte.ts`, `ResultList.svelte`, `Snippet.svelte`.

**Tests:**
- [ ] `AppSearchTests.Lexical_never_calls_the_embedder` (fake `LiteLlmClient` counting calls → 0).
- [ ] `AppSearchTests.Hybrid_embeds_a_query_once` (two identical hybrid searches, differing only in case and spaces → 1 embed call).
- [ ] `AppSearchTests.Hybrid_returns_partial_lexical_when_embedder_is_slow` (fake waits 10 s → response under 2 s, `Partial = true`, then the
  next call within the test finds the cached vector once the fake completes).
- [ ] `AppSearchTests.Facet_counts_equal_direct_counts` (seeded items over 3 feeds, 2 kinds, 3 months; counts over the full candidate set, not the page).
- [ ] `AppSearchTests.Filters_combine` (feed + kind + from/to + saved).
- [ ] `AppSearchTests.Boosts_order_ties` (equal RRF → voted 👍 first, then saved).
- [ ] `AppSearchTests.Cursor_pages_without_repeats`.
- [ ] `AppSearchTests.Snippet_marks_are_ranges_not_html` (a title containing `<b>` stays text; marks point at the query words).
- [ ] `PerfTests.Search_lexical_p95_under_150ms_at_20k` and `PerfTests.Search_hybrid_cached_p95_under_450ms_at_20k` (50 runs, warm; 3× the spec budgets).
- [ ] Contract fixtures `search-lexical.json`, `search-hybrid.json`, `search-partial.json`.
- [ ] Vitest `search.test.ts`: typing "pgv" then "pgvector" within 100 ms sends one lexical request; hybrid results replace lexical
  ones and keep the selected id; an aborted request never overwrites newer results.
- [ ] Playwright `search.spec.ts`: type, see results, `j` `j` `Enter` navigates to the second hit; facet click filters and updates the URL;
  reload restores the same state; `partial` shows "keyword results only".

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~AppSearchTests|FullyQualifiedName~PerfTests|FullyQualifiedName~ContractTests|FullyQualifiedName~CombinationTests"
cd web && npm run check && npm test && npm run e2e && npm run size
```
**Not here:** BM25 (W3b), tag facet (W8), notes in results (W12).

---

### W3a — Benchmark: pgx-bm25 vs pg_textsearch vs tsvector (decision step)
**Release:** none (docs and scripts only) · **Depends on:** W2 (uses its query shape). Can run in parallel with W4.

Delivers: a reproducible benchmark and a written decision. This is a research step: its output is numbers and a recommendation, not product code.

**Files:**
- Create `bench/search/` with `Dockerfile.pgx-bm25` and `Dockerfile.pg_textsearch` (each `FROM pgvector/pgvector:pg18`, building the
  extension at a pinned release tag), `load.sql` (schema of `items` plus each engine's index over `title` and `content`), `queries.tsv`
  (200 known-item queries generated by `bench/search/make-queries.sh`: for public items with a read, first sentence of the summary → item
  id; generated on the owner's instance with `docker compose exec` and committed without item text), `run.sh` (loads a dump or the
  synthetic corpus, runs every query per engine, writes `results.json`), and a synthetic corpus generator for 60,000 rows.
- Create `docs/reference/search-bench.md`: setup, MRR@10, recall@10, p50/p95 per engine at 15k (owner's archive) and 60k (synthetic), index
  build time and size, licence, maintenance signals (release cadence, open issues), query features (phrase, prefix, snippets), and
  **the decision** with its reason.

**Acceptance:**
```bash
bench/search/run.sh --synthetic 60000 --engines tsvector-simple,tsvector-english,pgx-bm25,pg_textsearch
jq -e '[.engines[] | select(.mrr10 != null and .p95_ms != null)] | length == 4' bench/search/results.json
grep -q '^## Decision' docs/reference/search-bench.md
```
The owner (or the Control desk) runs `bench/search/run.sh --dump <owner dump>` once on the real archive and pastes the table into the doc
before W3b starts; a chargehand worker cannot reach the owner's database.

**Not here:** application code.

---

### W3b — `ILexicalSearch`, BM25 engine, database image, `search-eval`
**Release:** patch · **Depends on:** W3a's decision merged.

Delivers: lexical search behind an interface with the chosen BM25 engine and today's tsvector engine; `ghcr.io/egoushka/feed-eater-db:pg18`;
detection at start; `doctor` line; `search-eval` command.

**Files:**
- Create `src/FeedEater/Workbench/Search/ILexicalSearch.cs`: `Task<IReadOnlyList<LexicalHit>> SearchAsync(NpgsqlConnection c, NpgsqlTransaction tx, string text, string filterSql, object filterArgs, int limit, CancellationToken ct)`,
  `LexicalHit(long Id, double Score)`, plus `string Name`; `TsvectorSearch.cs` (`ts_rank_cd` on `items.search`); `Bm25Search.cs` (the chosen extension);
  `LexicalEngine.cs` (at start: `select extname from pg_extension` → picks BM25 when present, else tsvector; logs the choice once).
- Migration: `create extension if not exists <chosen>` and its index, **wrapped so a database without the extension available skips both**
  (`do $$ begin if exists (select 1 from pg_available_extensions where name = '<chosen>') then … end if; end $$;`).
- `db/Dockerfile` (the image), CI job `db-image` building it on PRs and pushing `ghcr.io/egoushka/feed-eater-db:pg18-<version>` on tags.
- Modify `AppSearchStore` and `ItemStore.SearchAsync` to take lexical candidates from `ILexicalSearch` (MCP, Telegram and `/api/v1` search
  gain BM25 too); `doctor` prints `lexical search: bm25 (<ext> <version>)` or `lexical search: tsvector (install feed-eater-db for BM25)`;
  `AppCapabilities.Bm25`.
- Create `src/FeedEater/Workbench/Search/SearchEvalCommand.cs` (`search-eval [--n 200] [--queries file]`; output per the spec).
- `compose.example.yaml` → `image: ghcr.io/egoushka/feed-eater-db:pg18-<version>` with a comment that the stock pgvector image also works;
  `docs/SETUP.md` upgrade note.
- `PostgresFixture`: image from env `FEEDEATER_TEST_DB_IMAGE` (default stock pgvector); CI runs the suite once more with the built db image
  for the `LexicalSearchTests` class.

**Produces:** `ILexicalSearch`, `LexicalEngine.Current`, `search-eval`.

**Tests:**
- [ ] `LexicalSearchTests` as a theory over both engines (BM25 cases skip with a reason when the extension is absent): rare term beats a
  repeated common term; title match beats body match; `websearch` syntax (`"exact phrase"`, `-word`) behaves the same for both on the
  shared fixture; filters apply inside the candidate set.
- [ ] `LexicalEngineTests.Falls_back_to_tsvector_without_extension` and `…Picks_bm25_when_installed`.
- [ ] `MigrationTests.Bm25_migration_is_noop_on_stock_image`.
- [ ] `DoctorTests.Prints_lexical_engine`.
- [ ] `SearchEvalTests.Reports_mrr_and_latency_on_seeded_items` (fake embedder; output has both engines' rows when available).

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
docker build -t feed-eater-db:test db/ && FEEDEATER_TEST_DB_IMAGE=feed-eater-db:test dotnet test FeedEater.slnx --filter "FullyQualifiedName~LexicalSearchTests|FullyQualifiedName~PerfTests"
```
Owner gate (not CI): `docker compose run --rm feed-eater search-eval` on the owner's instance shows BM25 MRR@10 ≥ tsvector and lexical p95 ≤ 50 ms.

**Not here:** changing fusion weights.

---

### W4 — Reader body, item API, reading state; Reader screen
**Release:** patch · **Depends on:** W1 (can run in parallel with W2/W3).

**Files:**
- Migration: `item_bodies`, `reading_state` (spec).
- Create `src/FeedEater/Workbench/Reader/ReaderBody.cs` (`Build(string html, string url) → ReaderHtml(string Html, int TextLength)`:
  main region as `ReadableText` picks it, then `HtmlSanitizer` with the spec's allowlist; `img` → `<a href=src>image: alt</a>`;
  `a` gets `rel`/`target`; relative links resolved against `url`; non-http(s) links unwrapped to text),
  `ReaderBodyService.cs` (`GetOrFetchAsync(long itemId, CancellationToken ct) → ItemBody`: stored row, else one `SafeFetcher` fetch, else
  fallback from `items.content` split on blank lines into `<p>`, HTML-encoded; retries a `failed` row on open at most 3 attempts),
  `BodyPrefetchJob.cs` (after each digest is stored and for new library saves: at most 50 a day, inside `Fetch:MaxPerDay`),
  `ReaderStore.cs`, `ItemsAppApi.cs` (`MapItemsAppApi`: `GET /items/{id}` → `AppItem`, `POST /items/{id}/vote {value: 1|-1|0}`,
  `POST /items/{id}/save`, `DELETE /items/{id}/save`, `POST /items/{id}/follow` and `DELETE /items/{id}/follow` (through
  `StoryFollower.StartAsync` / `CloseAsync`), `POST /items/{id}/progress {progress, finished}`, `POST /items/{id}/read-now`
  (one read via `ItemReader`, as a library save does)).
- `AppItem(long Id, string Title, string Url, string? Feed, DateTimeOffset PublishedAt, string BodyHtml, string BodySource, string BodyStatus, ReadView? Read, int? Vote, bool Saved, bool Following, IReadOnlyList<AlsoIn> AlsoIn, double Progress)`,
  `ReadView(string Summary, string Why, string? Suggestion, string Kind, string? Project)` (from `reads`), `AlsoIn(long Id, string Feed, string Url)` (cluster members).
- Package `HtmlSanitizer` (MIT), pinned.
- Votes, saves and follows call the existing services (`FeedbackStore`/vote path used by `/ui`, `LibraryService.SaveAsync` with source
  `ui`), so ranking learns from the workbench exactly as from `/ui`.
- Web: `src/routes/items/[id]/+page.svelte`, `src/lib/components/Reader.svelte` (typography for long reading: 68ch measure, code blocks
  scroll horizontally, system fonts), progress reported on scroll (throttled 2 s) and on leave; keys `v` 👍, `V` 👎, `s` save, `o` open original,
  `r` read now; prefetch the selected search hit's item and the next two via `api.prefetch`.

**Produces:** `ReaderBodyService.GetOrFetchAsync`, `ItemBody`, `AppItem`, `Reader.svelte` (with a `{@html}` slot used only for `BodyHtml`),
`reading_state` (Home's "continue reading" in W6).

**Tests:**
- [ ] `ReaderBodyTests.Hostile_markup_is_removed` (script, `onerror`, `javascript:` and `data:` links, `<svg><script>`, `<style>`, `<iframe>`,
  `<object>`, `<meta http-equiv=refresh>`, `<form>`, `style=` attributes — each absent from the output).
- [ ] `ReaderBodyTests.Structure_is_kept` (h2, ul/li, pre/code with `<` inside, blockquote, table).
- [ ] `ReaderBodyTests.Images_become_links` and `…Relative_links_are_resolved`.
- [ ] `ReaderBodyServiceTests.Fetches_once` (second call → no fetch), `…Falls_back_to_content_on_403`, `…Falls_back_when_text_is_short`,
  `…Retries_failed_at_most_3_times`, `…Respects_daily_fetch_cap`.
- [ ] `BodyPrefetchTests.Prefetches_digest_items_up_to_50`.
- [ ] `ItemsAppApiTests`: get, vote (writes `votes` like `/ui`), save/unsave (library row, source `ui`), progress, read-now (one read call, fake LLM).
- [ ] `PerfTests.Item_with_body_p95_under_90ms_at_20k`.
- [ ] Contract fixtures `item-page.json`, `item-content-fallback.json`.
- [ ] Playwright `reader.spec.ts`: opens from search, `v` marks 👍, `s` saves; body with `<script>` in the fixture does not execute (window flag stays unset).

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~ReaderBody|FullyQualifiedName~BodyPrefetch|FullyQualifiedName~ItemsAppApiTests|FullyQualifiedName~PerfTests|FullyQualifiedName~ContractTests|FullyQualifiedName~CombinationTests"
cd web && npm run check && npm test && npm run e2e && npm run size
```
**Not here:** highlights (W11), tags (W8), context pane content (W5).

---

### W5 — Related items and story; context pane
**Release:** patch · **Depends on:** W4.

**Files:**
- Option `FeedEater:Story:Days` (30) with description.
- Create `src/FeedEater/Workbench/Connections/ConnectionsStore.cs` (`RelatedAsync(long itemId, int k, CancellationToken ct)`,
  `StoryAsync(long itemId, CancellationToken ct) → Story(StoryItem Root, IReadOnlyList<StoryItem> SameEvent, IReadOnlyList<StoryItem> Developments)`),
  rules exactly as the spec (cosine ≥ 0.55, ≤ 2 per feed, excluding duplicates and the cluster; developments within `Story:Days`, threshold
  `Cluster.Threshold − 0.04`, the version guard taken from `StoryClusterer` — extract it to a shared static if it is private), `ConnectionsApi.cs`.
- Web: `src/lib/components/ContextPane.svelte` with sections Same story, Developments, Related (Tags and Notes sections are added by W8 and
  W12); `src/routes/stories/[id]/+page.svelte` is W10.

**Produces:** `ConnectionsStore.RelatedAsync`, `ConnectionsStore.StoryAsync`, `ContextPane.svelte` with named section slots.

**Tests:**
- [ ] `ConnectionsTests.Related_excludes_cluster_and_duplicates`, `…Related_caps_two_per_feed`, `…Related_respects_min_similarity`
  (seeded unit vectors at known angles).
- [ ] `ConnectionsTests.Developments_follow_the_story_within_window` (a chain A→B→C where C is close to B but not to A is included;
  an item after 30 days is not), `…Different_versions_do_not_join` ("pgvector 0.8.7" vs "pgvector 0.9.0" titles).
- [ ] `PerfTests.Related_and_story_p95_under_120ms_at_20k`.
- [ ] Contract fixtures `related.json`, `story.json`.
- [ ] Playwright `context.spec.ts`: sections render; clicking a related item opens it in the reader and keeps the list selection.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~ConnectionsTests|FullyQualifiedName~PerfTests|FullyQualifiedName~ContractTests|FullyQualifiedName~ConfigDocTests"
cd web && npm run check && npm test && npm run e2e
```

---

### W6 — Home, command palette, keyboard map (*milestone M1*)
**Release:** closes M1 "Reader and search" (`Release-As: 0.10.0`) · **Depends on:** W2, W4, W5.

**Files:**
- Create `src/FeedEater/Workbench/Home/HomeApi.cs`: `GET /app/api/home` → today's digest items (with read), continue reading (opened, not
  finished, last 14 days, 10), library unread (10), and empty arrays for topic updates and open ideas until W9/W12 fill them (the record
  fields exist from now: `Home(IReadOnlyList<DigestCard> Digest, IReadOnlyList<AppHit> Continue, IReadOnlyList<AppHit> Unread, IReadOnlyList<TopicUpdate> Topics, IReadOnlyList<NoteRef> Ideas)`,
  `DigestCard(AppHit Hit, ReadView? Read)`, `NoteRef(long Id, string Title, string Kind, DateTimeOffset UpdatedAt)`, and `TopicUpdate` as W9 defines it, declared here empty-capable).
  `GET /app/api/jump?q=` → up to 10 titles of items (and later notes, topics, tags) by prefix/trigram for the palette (lexical only, no embedder).
- Web: `src/routes/+page.svelte` (Home), `src/lib/components/Palette.svelte` (Ctrl-K / Cmd-K: jump to entity, run an action on the
  current selection from a registry), `src/lib/keys.ts` (one registry of keys and actions; `?` shows the list), `src/lib/actions.ts`.

**Produces:** the action registry `registerAction({id, title, key?, run, when})` later steps add to; `Home` record fields for W9/W12.

**Tests:**
- [ ] `HomeApiTests.Digest_continue_unread` (seeded digest, reading_state, saved rows).
- [ ] `HomeApiTests.Jump_never_calls_the_embedder`.
- [ ] Contract `home.json`, `jump.json`.
- [ ] Vitest `keys.test.ts`: no two actions share a key; keys are ignored while typing in an input or the editor.
- [ ] Playwright `home.spec.ts`: digest renders; Ctrl-K, type, Enter opens the item; `?` lists keys.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
cd web && npm ci && npm run check && npm test && npm run e2e && npm run build && npm run size
docker build -t feed-eater:m1 .
```
Release gate: the owner reads a day's digest and runs ten searches in `/app` without opening `/ui` (spec, Milestones).

---

### Milestone M2 — Tags and topics (0.10.x patches, closed by 0.11.0)

### W7 — Tags: schema, seeding from profiles, suggester, API
**Release:** patch · **Depends on:** M1.

**Files:**
- Migration: `tags`, `item_tags` (spec).
- Create `src/FeedEater/Workbench/Tags/TagStore.cs`, `TagService.cs` (`CreateAsync(name, description)`, `RenameAsync`, `MergeAsync(from, into)`,
  `DeleteAsync`, `SetAsync(itemId, tagId, TagAction action)` with `TagAction = Add | Confirm | Reject | Remove`), `TagSeeder.cs` (on start and after
  `ProfileBuilder` rebuilds: one tag per profile, keyed by `profile_key`, updating name/description/embedding of seeded tags; never deletes),
  `TagSuggester.cs` (hourly loop; the spec's prototype and percentile rule; tags with `suggest = false` skipped; ≤ 20 new suggestions per tag a run;
  also suggests on demand for one item via `SuggestForItemAsync(itemId)` which the reader calls on open), `TagsApi.cs`:
  `GET /tags` (with counts confirmed/suggested), `POST /tags`, `PATCH /tags/{id}`, `POST /tags/{id}/merge {into}`, `DELETE /tags/{id}`,
  `POST /items/{id}/tags {tagId, action}`, `GET /items/{id}/tags` (runs `SuggestForItemAsync` first, so an opened item always has its
  suggestions; vector math only).
- Tag description embedding through `LiteLlmClient.EmbedAsync` with purpose `tag`.
- `AppSearchQuery.Tags` filter and the `tag` facet in `AppSearchStore` (confirmed + suggested; facet shows both counts).

**Produces:** `TagService`, `TagSuggester.SuggestForItemAsync`, `AppTag(long Id, string Slug, string Name, string State, double? Score)`, the `tag` facet.

**Tests:**
- [ ] `TagSeederTests.Seeds_once_per_profile_and_updates_on_rebuild`.
- [ ] `TagSuggesterTests.Suggests_above_percentile_and_floor` (synthetic vectors), `…Rejected_is_never_suggested_again`,
  `…Confirmed_items_move_the_prototype`, `…Five_rejections_push_it_away`, `…Caps_twenty_per_run`, `…Calls_no_chat_model`.
- [ ] `TagServiceTests.Merge_collapses_to_strongest_state` (confirmed > suggested > rejected), `…Slug_is_unique_and_normalized`.
- [ ] `AppSearchTests.Tag_filter_and_facet`.
- [ ] Contracts `tags.json`, `item-tags.json`.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~Tag|FullyQualifiedName~AppSearchTests|FullyQualifiedName~ContractTests|FullyQualifiedName~CombinationTests"
```
**Not here:** screens (W8).

---

### W8 — Tags in the app
**Release:** patch · **Depends on:** W7.

**Files (web):** `src/lib/components/TagChips.svelte` (confirmed solid, suggested dashed with ✓ ✗; `t` opens the tag picker),
`TagPicker.svelte` (palette mode: search tags, Enter adds, "create tag 'x'" when none matches — asks for a one-line description),
the Tags section of `ContextPane.svelte`, the tag facet in `Facets.svelte`, `src/routes/tags/+page.svelte` (manage: rename, merge,
delete, toggle `suggest`, counts), palette actions `tag.add`, `tag.confirmAll` (confirm every suggestion on the current item).

**Tests:**
- [ ] Vitest `tagchips.test.ts`: ✓ sends `confirm`, ✗ sends `reject` and hides the chip; optimistic update rolled back on error.
- [ ] Playwright `tags.spec.ts`: add a tag with `t`, confirm a suggestion, filter search by the tag, merge two tags on `/app/tags`.

**Acceptance:**
```bash
cd web && npm run check && npm test && npm run e2e && npm run size
```

---

### W9 — Topics: schema, evaluation, timeline, weekly `topic` card
**Release:** patch · **Depends on:** W7.

**Files:**
- Migration: `topics` (spec); replace `inbox.kind` check to add `topic`; add `open`/`mute` to whatever check lists swipe acts if needed
  (`0032_library.sql` shows the pattern).
- Create `src/FeedEater/Workbench/Topics/TopicStore.cs`, `TopicService.cs` (`ItemsAsync(topicId, sort, cursor)` through `AppSearch`;
  `TimelineAsync(topicId) → IReadOnlyList<WeekCount>` 52 weeks with zeros; `StoriesAsync(topicId)`; `NewSinceAsync(topicId, DateTimeOffset)`),
  `TopicDigestJob.cs` (Sunday 17:00 local via `ScheduledJob`, quiet hours held; key `topic:{id}:{isoYear}-W{isoWeek}`; payload `{name, count, titles[3], topicId}`;
  updates `last_checked_at`; none when count is 0), `TopicsApi.cs` (`GET/POST /topics`, `GET/PATCH/DELETE /topics/{id}`, `/items`, `/timeline`, `/stories`).
- Swipe: `InboxKinds.Topic`; `CardView.Detail` for `topic`; acts `open` (returns the deep link `/app/topics/{id}`) and `mute`; the PWA
  (`swipe.js`) renders the card with `textContent`; Telegram renderer: one text message with the link (only while `Telegram:Token` is set).
- `Home.Topics` filled: topics with entries in the last 7 days.

**Produces:** `TopicService`, `TopicUpdate(long TopicId, string Name, int Count, IReadOnlyList<string> Titles)`, inbox kind `topic`.

**Tests:**
- [ ] `TopicServiceTests.Items_equal_search_with_definition`, `…Timeline_has_52_weeks_with_zeros`, `…Stories_need_two_members`.
- [ ] `TopicDigestTests.One_entry_per_topic_per_week` (run twice → one row), `…None_when_nothing_new`, `…Muted_topic_is_skipped`,
  `…Quiet_hours_hold_it`.
- [ ] `SwipeTopicCardTests.Topic_card_queues_after_digest_and_mute_sets_notify_false`.
- [ ] `TelegramRendererTests.Topic_entry_renders_text_with_link` (Telegram on), `…No_send_when_telegram_off`.
- [ ] `SwipePageTests` (existing style) asserts the topic card's text is set via `textContent` (no `innerHTML` for new kinds).
- [ ] Contracts `topic.json`, `topic-timeline.json`, `topic-stories.json`, `home.json` regenerated.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
```

---

### W10 — Topic and story pages (*milestone M2*)
**Release:** closes M2 "Tags and topics" (`Release-As: 0.11.0`) · **Depends on:** W9, W5.

**Files (web):** `src/routes/topics/+page.svelte` (list, "new topic from this search" also as a palette action on the Search screen),
`src/routes/topics/[id]/+page.svelte` (definition editor, timeline as inline SVG bars — no chart library —, items with sort, stories, notes
placeholder filled by W12), `src/routes/stories/[id]/+page.svelte` (root, same event, developments on a vertical timeline),
`src/lib/components/Timeline.svelte` (accessible: each bar has a title with week and count; table fallback toggle).

**Tests:**
- [ ] Vitest `timeline.test.ts`: 52 bars, zero weeks rendered as empty bars, bar heights scale to the max.
- [ ] Playwright `topics.spec.ts`: create a topic from a search, open it, change sort, open a story from it; the swipe deep link
  `/app/topics/{id}` opens the page after login redirect.

**Acceptance:**
```bash
cd web && npm ci && npm run check && npm test && npm run e2e && npm run build && npm run size
dotnet test FeedEater.slnx
docker build -t feed-eater:m2 .
```
Release gate: five topics exist on the owner's instance and one weekly `topic` card reached the stack.

---

### Milestone M3 — Notes and ideas (0.11.x patches, closed by 0.12.0)

### W11 — Highlights
**Release:** patch · **Depends on:** M1 (W4).

**Files:**
- Migration: `highlights` (spec).
- Create `src/FeedEater/Workbench/Highlights/HighlightStore.cs`, `HighlightsApi.cs` (`GET/POST /items/{id}/highlights`, `PATCH/DELETE /highlights/{id}`);
  `AppItem` gains `Highlights`.
- Web: `src/lib/anchoring.ts` (`describe(range, root) → {exact, prefix, suffix, startHint}`, `locate(selector, root) → Range | null`: exact +
  prefix/suffix match, else exact nearest `startHint`, else null), `src/lib/components/Highlights.svelte` (wraps located ranges in
  `<mark>` without touching other markup; "Couldn't place" list), keys `h` (highlight selection), `c` (comment on a highlight).

**Produces:** `AppHighlight(long Id, long ItemId, string Exact, string Prefix, string Suffix, int? StartHint, string? Comment, DateTimeOffset CreatedAt)`,
`anchoring.ts` (`describe`, `locate`).

**Tests:**
- [ ] `HighlightsApiTests`: create, list, delete; `exact` over 4,000 characters → 400.
- [ ] Vitest `anchoring.test.ts`: round trip on a body; same quote twice in the body → prefix/suffix pick the right one; body re-fetched
  with an extra paragraph before → still found; quote edited away → `null`.
- [ ] Playwright `highlights.spec.ts`: select text, `h`, reload, mark is there; a fixture body that changed shows the quote under "Couldn't place".

**Acceptance:**
```bash
dotnet test FeedEater.slnx --filter "FullyQualifiedName~Highlight|FullyQualifiedName~ContractTests|FullyQualifiedName~CombinationTests"
cd web && npm run check && npm test && npm run e2e
```

---

### W12 — Notes: schema, links, backlinks, search, embedder
**Release:** patch · **Depends on:** W11, W7, W9.

**Files:**
- Migration: `notes`, `note_links` (spec).
- Create `src/FeedEater/Workbench/Notes/NoteLinks.cs` (`Parse(string body) → IReadOnlyList<NoteLink>`: `[[item:N]]`, `[[hl:N]]`, `[[note:N]]`,
  `[[topic:N]]`, `#slug` outside code spans and fences), `NoteStore.cs`, `NoteService.cs` (`CreateAsync`, `UpdateAsync(id, title, body, kind, status, DateTimeOffset basedOn)`
  → `NoteSaved | NoteConflict(Note Current)`; rebuilds links in the same transaction; unknown ids dropped from `note_links`, kept in text),
  `NoteEmbedder.cs` (loop: notes with `updated_at > embedded_at` and `updated_at < now() − 60 s`, embed title + body truncated to the
  model limit), `NotesApi.cs` (`GET /notes?kind=&status=&tag=`, `POST /notes`, `GET/PUT/DELETE /notes/{id}`, `GET /backlinks?kind=&id=`,
  `POST /notes/{id}/append {markdown}` for "send selection to note").
- `AppSearchStore`: notes join both lists (lexical on `notes.search` — tsvector even when BM25 is active, documented —, vector on
  `notes.embedding`), `type = note`, facet `notes` (has-note) and kind; `Home.Ideas` filled.
- `jump` includes notes, topics and tags.

**Produces:** `NoteService`, `NoteLinks.Parse` → `NoteLink(string TargetKind, long TargetId)`,
`AppNote(long Id, string Kind, string Title, string Body, string Status, string? PromotedUrl, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, IReadOnlyList<NoteLink> Links)`,
`NoteSaved(AppNote Note)`, `NoteConflict(AppNote Current)`, `/backlinks`.

**Tests:**
- [ ] `NoteLinksTests`: every token type; tokens in code spans/fences ignored; `#tag` needs a known slug; `[[item:abc]]` ignored.
- [ ] `NotesApiTests.Stale_update_is_409` (with the current note in the body), `…Links_rebuild_on_save`, `…Delete_removes_links`,
  `…Body_over_200000_chars_is_400`, `…Append_adds_a_quote_block`.
- [ ] `BacklinksTests.Item_highlight_tag_topic_backlinks`.
- [ ] `NoteEmbedderTests.Waits_60s_after_last_edit_and_embeds_once`.
- [ ] `AppSearchTests.Notes_appear_with_type_note_and_facet`.
- [ ] Contracts `note.json`, `note-conflict.json`, `notes.json`, `backlinks.json`, `home.json` regenerated.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
```

---

### W13 — Notes in the app: editor, preview, backlinks
**Release:** patch · **Depends on:** W12.

**Files (web):** `src/routes/notes/+page.svelte` (list with filters), `src/routes/notes/[id]/+page.svelte`, `src/lib/editor/` (CodeMirror 6
loaded by dynamic `import()` only on this route; Markdown mode; `[[` completion from `/app/api/jump`; `#` completion from tags; autosave
1 s after the last keystroke with `basedOn`; on 409 a side panel with the server text and "use mine"/"use theirs"; unsaved text mirrored
to `sessionStorage` and restored after a login redirect), `src/lib/markdown.ts` (markdown-it `html: false`, `linkify: true`, a plugin turning
`[[kind:N]]` into chips that navigate), Backlinks and Notes sections in `ContextPane.svelte`, topic page notes section, reader key `n`
(send selection: choose a recent note or create one; calls `/append` with a quote and `[[hl:N]]` after creating the highlight).

**Tests:**
- [ ] Vitest `markdown.test.ts`: `<script>`, `<img onerror>` and raw HTML render as text; `[[item:5]]` becomes a chip linking `/app/items/5`;
  `javascript:` links are not linked.
- [ ] Vitest `autosave.test.ts`: debounce, `basedOn` updated after each save, conflict state on 409.
- [ ] Playwright `notes.spec.ts`: create an idea, `[[` completes an item, preview shows the chip, reload keeps text; `note-conflict.spec.ts`:
  a mocked 409 shows both versions and nothing is lost; `n` in the reader appends the selection to a note.
- [ ] `npm run size`: the entry chunk stays under 150 KB gzip (the editor is a separate chunk).

**Acceptance:**
```bash
cd web && npm run check && npm test && npm run e2e && npm run build && npm run size
```

---

### W14 — Ask in the workbench: scopes, streaming, save as note
**Release:** patch · **Depends on:** W12, W9.

**Files:**
- Modify `src/FeedEater/Search/ArchiveAnswer.cs`: extract `AnswerAsync(string question, IReadOnlyList<SearchHit> sources, ct)` and a
  streaming variant `StreamAsync(question, sources, ct) → IAsyncEnumerable<AskEvent>` (`AskToken(string Text)`, `AskCitations(IReadOnlyList<Citation>)`,
  `AskError(string Message)`); the existing `AnswerAsync(question, ct)` keeps retrieving its own top 8 and calls the new one, so Telegram
  and `/api/v1/ask` behave as before. Streaming needs `LiteLlmClient` to support `stream: true` (SSE from the OpenAI-compatible endpoint);
  add it there with a test against a stub handler.
- Create `src/FeedEater/Workbench/Ask/AskScope.cs` (`ResolveAsync(scope, question, ct) → IReadOnlyList<SearchHit>`: archive = `AppSearch` hybrid top 8;
  topic = topic's definition top 8; tag = tag filter top 8; note = linked items and highlights' items ranked by the question, top 8;
  itemIds ≤ 30 ranked by the question, top 8), `AskApi.cs` (`POST /ask` → `text/event-stream` with events `token`, `citations`, `done`,
  `error`; `POST /ask/save {question, answer, citations}` → note of kind `answer`).
- Citations include `highlightId` when the cited item has a highlight in the note scope.
- Web: `src/routes/ask/+page.svelte`, `src/lib/components/AskPanel.svelte` (also embedded in the note editor and the topic page, with the
  scope preset), streaming via `fetch` + `ReadableStream` (not `EventSource`, so `X-CSRF` can be sent); citations `[n]` open the reader.

**Tests:**
- [ ] `ArchiveAnswerTests` (existing) still pass unchanged.
- [ ] `AskScopeTests`: retrieved ids ⊆ scope for topic, tag, note, itemIds; itemIds > 30 → 400.
- [ ] `AskApiTests.Stream_order_is_tokens_citations_done`, `…Model_down_sends_error_with_sources`, `…Unknown_markers_are_dropped`
  (as `ArchiveAnswer` does), `…Save_creates_answer_note_with_item_links`.
- [ ] `LiteLlmClientTests.Streams_chat_completion_chunks` (stub handler).
- [ ] Playwright `ask.spec.ts` with a mocked stream: tokens appear progressively; citation opens the reader; Save as note opens the new note.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
cd web && npm run check && npm test && npm run e2e
```

---

### W15 — Promote an idea to Plane or a GitHub issue
**Release:** patch · **Depends on:** W12, W13.

**Files:**
- Migration: `promotions` (spec).
- Options `FeedEater:Promote:GitHubRepos` (string list, `owner/name`) and `FeedEater:Promote:GitHubLabels` (allowed labels), with descriptions.
- Create `src/FeedEater/Workbench/Promote/PromoteBody.cs` (`Render(AppNote note, IReadOnlyList<ResolvedLink> links) → (string Title, string Markdown)`,
  `ResolvedLink(string Kind, long Id, string Title, string? Feed, DateTimeOffset? At, string? Url, string? Quote)` loaded from the note's `Links`:
  `[[item:N]]` → "Title (Feed, yyyy-MM-dd) URL"; `[[hl:N]]` → a quote block plus its item link; footer `From feed-eater note {id}` and a
  hidden marker `<!-- feed-eater:note:{id} -->`), `GitHubIssues.cs` (create issue; find an open or closed issue containing the marker via
  the search API), `PromoteService.cs` (`PromoteAsync(noteId, target, project?, repo?, labels?) → PromoteResult(string Url, bool Created)`):
  an existing `promotions` row returns its URL; GitHub: before creating, search for the marker in that repo and adopt a found issue (covers
  a timeout after the remote created it); Plane: the existing `PlaneClient` intake call, with the marker in the description and the same
  adopt-if-found check if Plane's API can search, otherwise the `promotions` row alone; on success sets the note's status and URL),
  `PromoteApi.cs` (`POST /notes/{id}/promote`).
- `AppCapabilities.Plane` = Plane configured; `AppCapabilities.GitHub` = token set and at least one repo listed.
- Web: Promote dialog on the note page (target, project or repo from capabilities, labels as checkboxes from the allowed list); the note
  shows its promoted URL.

**Tests:**
- [ ] `PromoteBodyTests.Resolves_items_and_highlights_and_adds_marker`.
- [ ] `PromoteTests.Second_promote_returns_first_url_and_files_nothing` (Plane and GitHub, stub HTTP).
- [ ] `PromoteTests.Timeout_after_remote_created_adopts_existing_issue` (stub: create times out, search finds the marker → one issue, row written).
- [ ] `PromoteTests.Repo_not_listed_is_400`, `…Label_not_allowed_is_400`, `…Unconfigured_target_absent_from_capabilities`,
  `…Remote_down_is_502_and_records_nothing`.
- [ ] Playwright `promote.spec.ts` (mocked): dialog lists only allowed repos and labels; promoted URL shows on the note.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx --filter "FullyQualifiedName~Promote|FullyQualifiedName~ConfigDocTests|FullyQualifiedName~CombinationTests|FullyQualifiedName~ContractTests"
cd web && npm run check && npm test && npm run e2e
```

---

### W16 — Exports and MCP additions (*milestone M3*)
**Release:** closes M3 "Notes and ideas" (`Release-As: 0.12.0`) · **Depends on:** W12, W14, W15.

**Files:**
- Create `src/FeedEater/Workbench/Export/ExportApi.cs`: `GET /notes/{id}.md`, `GET /topics/{id}.md`, `GET /export/notes.zip` (each note as
  `notes/{id}-{slug}.md` with resolved links and highlights, plus `index.json` with id, title, kind, status, dates, links), content
  disposition attachment, streamed (no whole-archive buffering).
- Modify `src/FeedEater/Mcp/FeedTools.cs`: `feed_search` gains `tag` (slug); new read-only `feed_notes(query?, id?)` and `feed_topic(id or name)`;
  `selftest.sh` calls them.
- Web: Export actions on note and topic pages and in the palette.

**Tests:**
- [ ] `ExportTests.Note_markdown_resolves_links`, `…Topic_markdown_lists_top_100_with_highlights`, `…Zip_has_every_note_and_index`.
- [ ] `McpToolsTests` (existing style): `feed_search` with `tag`; `feed_notes` by query and id; `feed_topic` by name; all read-only.

**Acceptance:**
```bash
dotnet format FeedEater.slnx --verify-no-changes
dotnet test FeedEater.slnx
cd web && npm ci && npm run check && npm test && npm run e2e && npm run build && npm run size
docker build -t feed-eater:m3 .
```
Release gates for 0.12.0: a second agent's risk review of M1–M3 (findings fixed with tests that failed first), and the spec's acceptance
item 5 (one idea promoted to an issue chargehand picks up).

---

## Order and parallelism

```
W0 (any time)
0.9.0 ─ W1 ─┬─ W2 ─ W3a ─ W3b ─┐
            └─ W4 ─ W5 ─────────┴─ W6 (0.10.0) ─ W7 ─┬─ W8
                                                     └─ W9 ─ W10 (0.11.0) ─ W11 ─ W12 ─┬─ W13 ─ W15 ─┐
                                                                                        └─ W14 ───────┴─ W16 (0.12.0)
```
- W2 and W4 can run at the same time after W1 (different files; both add routes to the group in `AppApi.cs`, so the second to merge
  rebases one line). W3a needs only W2's query shape and can overlap W4/W5.
- W8 and W9 can run at the same time after W7. W11 can start once M1 is out, alongside M2.
- One chargehand worker per step; a step starts only when its **Depends on** steps are merged.

## Filing as issues

When the owner approves this plan, each step becomes one issue on `Egoushka/feed-eater`:
- Title: `workbench W<n>: <step title>`; labels `chargehand`, `workbench`, `m1`/`m2`/`m3`.
- Body: the step's section verbatim, then "Global constraints, rulings and Review focus: `docs/plans/2026-10-26-workbench.md`; spec:
  `docs/specs/2026-10-26-workbench.md`", then the **Depends on** issues as links.
- Only steps whose dependencies are merged carry the `chargehand` label, so the orchestrator never starts a step on a stale base.
- Work-in-progress limit: at most 3 open implementation PRs in feed-eater (the weekly cycle); label the next issue only when one merges.
- Each milestone is a Plane item in the week's cycle; the steps picked for the week are listed on it.
- W3a's owner run (the real-archive benchmark) and every **Release gate** are tasks for the owner or the Control desk, not chargehand.
