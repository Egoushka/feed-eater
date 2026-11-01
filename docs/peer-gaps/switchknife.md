# Peer gaps: Egoushka/switchknife

Harvested 2026-11-01. Peers are the 7 closest public open-source projects; every gap cites a page that shows the peer has the feature, and "absent here" was checked in this repo by reading or grepping it. Judged by Claude, not yet by Yehor (the stop rule's reviewer).

**Gaps:** 21. Useful 6, not useful 6, unknown 9.

**What it is:** Public MIT set of small .NET 10 libraries, one NuGet package per job, all Native-AOT compatible (IsAotCompatible, no reflection JSON, scripts/aot-smoke.sh), versioned per package by tag, 0.x. Consumers: chargehand and the owner's other services. Checked at local checkout /Users/yehorhrabovskyi/Projects/personal/switchknife (README.md, CLAUDE.md, docs/adr/0001-0005, src/*/README.md).

**Note from the harvest:** Exactly 7 peers, chosen one per area so the whole set is spanned as far as 7 allow: Facts, Journal, McpGuard, Migrations, Pace, Hosting, Testing.Postgres. No peer was compared for Testing.Http (e.g. RichardSzalay/MockHttp, WireMock.Net), Facts.Testing, Journal.Postgres (covered via the Journal peer) or Knowledge. For Facts the closest bi-temporal fact store with LLM extraction is Python (getzep/graphiti); no .NET equivalent with comparable scope was found. For Pace there is no open-source .NET pacing engine for AI-subscription allowances; ccusage (TypeScript/Rust) is the closest by purpose. DbUp is the library Switchknife.Migrations wraps, so its gaps are DbUp features Switchknife does not expose. All gaps are listed without judging usefulness.

## getzep/graphiti

https://github.com/getzep/graphiti · Apache-2.0 · 32,411 stars · last push 2026-11-01. Similar because: Temporal knowledge graph with explicit bi-temporal facts, automatic invalidation, episodes as provenance, hybrid search and an LLM extraction pipeline; the closest open-source analogue to Switchknife.Facts (Python, Neo4j/FalkorDB).

- **Graph-aware search: rerank/expand by graph distance and BFS** (unknown). Search recipes combine full-text, vector and BFS over edges and nodes and rerank edges by node distance from a focal entity.  
  Source: https://github.com/getzep/graphiti/blob/main/graphiti_core/search/search_config_recipes.py#L126  
  Absent here, checked: Read src/Switchknife.Facts/README.md search section and FactsStore.Search.cs: only full-text + nearest-neighbour fused by reciprocal rank; grep -E 'traversal|neighbo|hop' in src/Switchknife.Facts found only the vector-neighbour comment, no graph walk.  
  Worth doing? Unknown: UNKNOWN whether any consumer asks relationship questions. Cost: about 2-4 days: a recursive CTE over facts whose ObjectEntity links entities, plus a third ranked list in the fusion and a benchmark

- **Community clustering with summaries** (not useful). Builds communities by label propagation over the entity graph and keeps a model-written summary per community, updated as episodes arrive.  
  Source: https://github.com/getzep/graphiti/blob/main/graphiti_core/utils/maintenance/community_operations.py  
  Absent here, checked: grep -i -E 'communit|ontology|dedup' over src/Switchknife.Facts and src/Switchknife.Knowledge (cs, md, sql): no matches.  
  Worth doing? Not useful: Weeks; useful only for stores with thousands of facts. Cost: weeks: a clustering pass, a summary store and a model call path in the extraction kit

- **Entity resolution beyond exact name (alias, fuzzy, model-assisted dedupe)** (unknown). A dedupe prompt/pipeline merges duplicate entity nodes and resolves edges, with configurable thresholds.  
  Source: https://github.com/getzep/graphiti/blob/main/graphiti_core/prompts/dedupe_nodes.py  
  Absent here, checked: Read src/Switchknife.Facts/Extraction/ExactNameEntityResolver.cs: exact trimmed case-insensitive name only; its comment says proper resolution (aliases, phonetic keys, embeddings) comes in F4. Table facts.entity_aliases with phonetic/skeleton keys exists in Migrations/0001_facts.sql but nothing uses it for resolution yet.  
  Worth doing? Unknown: Already planned as F4; the source adds design evidence, not a new decision. Cost: about 1-2 weeks: alias lookup first (days), then embedding and optional model judgement behind IEntityResolver

## microsoft/durabletask-dotnet

https://github.com/microsoft/durabletask-dotnet · MIT · 198 stars · last push 2026-11-01. Similar because: Durable execution for .NET: activities are recorded and replayed after a restart, the same job as Switchknife.Journal steps, with a richer workflow layer.

- **Declarative retry policy per step (attempts, first interval, backoff coefficient, max interval, timeout, exception filter)** (useful). RetryPolicy configures an activity call so the framework retries it with exponential backoff without caller code.  
  Source: https://github.com/microsoft/durabletask-dotnet/blob/main/src/Abstractions/RetryPolicy.cs  
  Absent here, checked: Read src/Switchknife.Journal/StepJournal.cs and IStepStore.cs; grep -i -E 'retry|backoff|timer|WaitFor|signal|external' in src/Switchknife.Journal: no match. A thrown step is recorded failed and the next RunAsync is a fresh attempt by the caller.  
  Worth doing? Useful: Each caller of RunAsync writes its own retry loop; a policy recorded in the journal is 1-2 days. Cost: about 1-2 days: an options object on RunAsync, a loop with TimeProvider delay and tests

- **Durable timers (sleep that survives a restart)** (unknown). CreateTimer(fireAt) persists the wake-up time so an orchestration resumes at the right moment after a crash.  
  Source: https://github.com/microsoft/durabletask-dotnet/blob/main/src/Abstractions/TaskOrchestrationContext.cs#L195  
  Absent here, checked: Same grep over src/Switchknife.Journal and src/Switchknife.Journal.Postgres: no timer, delay (apart from a lease backoff in PgStepStore.cs) or wake-at column in the schema.  
  Worth doing? Unknown: No flow waits for hours yet. Cost: about 3-5 days: a wake_at column and a step kind in both stores plus a scan on start

- **Waiting for an external event inside a flow** (unknown). WaitForExternalEvent<T>(name) suspends a flow until another process raises an event with that name.  
  Source: https://github.com/microsoft/durabletask-dotnet/blob/main/src/Abstractions/TaskOrchestrationContext.cs#L238  
  Absent here, checked: Journal has AppendEventAsync/ReadEventsAsync (src/Switchknife.Journal/StepJournal.cs) but they are a replay log for readers; no API suspends a step until a named event arrives (grep 'external|signal' none).  
  Worth doing? Unknown: Fits human approval, but a week with no consumer asking. Cost: about 1 week: a wait step stored as pending, an event-match on append, and a resume path in both stores

## modelcontextprotocol/csharp-sdk

https://github.com/modelcontextprotocol/csharp-sdk · Apache-2.0 (GitHub reports NOASSERTION: licence is moving from MIT to Apache-2.0, LICENSE file says so) · 4,611 stars · last push 2026-10-30. Similar because: The official .NET MCP SDK; its ASP.NET Core package and docs cover what Switchknife.McpGuard guards (streamable HTTP endpoint security).

- **Host header allow-list (AllowedHosts) as the DNS-rebinding defence** (useful). Docs tell local servers to accept only loopback host names via AllowedHosts, because Kestrel does not validate Host by default.  
  Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md#L239  
  Absent here, checked: Read src/Switchknife.McpGuard/README.md and McpGuardExtensions.cs; grep -i host over src/Switchknife.McpGuard *.cs: no match. McpGuard refuses any Origin and checks a token but does not check Host.  
  Worth doing? Useful: Half a day; closes the case of a rebinding request that omits Origin. Cost: about half a day: an optional allowed-hosts list checked before the Origin check, with tests

- **Per-tool authorization with [Authorize] and filtered tool lists** (not useful). AddAuthorizationFilters lets [Authorize] and roles on tools, prompts and resources decide access, and removes unauthorized items from list results.  
  Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/identity/identity.md#L90  
  Absent here, checked: McpGuard README Limits: one static token, scopes or per-client tokens left to an authentication scheme; grep -i -E 'scope|authorize' in src/Switchknife.McpGuard: only a 401 status line.  
  Worth doing? Not useful: Needs a dependency on the MCP SDK that McpGuard avoids on purpose. Cost: weeks, and it would need a dependency on the MCP SDK that the package avoids on purpose

- **OAuth 2 protected-resource metadata with JWT bearer in a ready sample** (unknown). A ProtectedMcpServer sample shows JWT authentication, OAuth resource metadata and protected tools for clients that discover the authorization server.  
  Source: https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/identity/identity.md#L193  
  Absent here, checked: grep -i -E 'oauth|metadata' in src/Switchknife.McpGuard *.cs and README.md: none; the guard is a static bearer token only.  
  Worth doing? Unknown: UNKNOWN whether any MCP client Yehor uses needs OAuth discovery. Cost: about 1-2 weeks as a separate package: metadata endpoint, JWT validation, tests against a real MCP client

## DbUp/DbUp

https://github.com/DbUp/DbUp · MIT · 2,632 stars · last push 2026-02-23. Similar because: The migration engine Switchknife.Migrations wraps; its gaps are DbUp features the wrapper does not expose.

- **Script variables ($Name$ substitution)** (unknown). WithVariable/WithVariables replaces $Name$ in scripts and fails on a missing variable outside comments.  
  Source: https://github.com/DbUp/DbUp/blob/main/docs/more-info/variable-substitution.md  
  Absent here, checked: Read src/Switchknife.Migrations/MigrationOptions.cs and DatabaseMigrator.cs; grep -i -E 'variable|RunAlways|ScriptType|RunGroup|HtmlReport|JournalTo' over src/Switchknife.Migrations *.cs: no match. README Limits says script variables are not exposed.  
  Worth doing? Unknown: Half a day, but no script needs a per-environment name yet. Cost: about half a day: a Variables dictionary on MigrationOptions and a test

- **Run-always scripts and run group order** (unknown). ScriptType.RunAlways with RunGroupOrder reruns idempotent scripts (views, grants) before or after the run-once scripts.  
  Source: https://github.com/DbUp/DbUp/blob/main/docs/more-info/run-group-order.md  
  Absent here, checked: Same grep over src/Switchknife.Migrations: no ScriptType or RunGroupOrder; DatabaseMigrator.cs builds one WithScriptsEmbeddedInAssembly call with a single filter.  
  Worth doing? Unknown: No view or grant is reapplied on start yet. Cost: about 1 day: a second filter and group option in MigrationOptions and tests of ordering

- **Preview of pending scripts (HTML report / dry run)** (useful). GenerateUpgradeHtmlReport writes which scripts would run without running them, for an approver to review.  
  Source: https://github.com/DbUp/DbUp/blob/main/docs/more-info/html-report.md  
  Absent here, checked: grep -i -E 'dry|preview|pending|GetScriptsToExecute|HtmlReport' over src/Switchknife.Migrations: none; Run() always performs the upgrade.  
  Worth doing? Useful: Pairs with the migration gate (workspace #868); a day to list pending scripts. Cost: about 1 day: a Pending() method returning script names (HTML optional)

- **Configurable journal table (name and schema)** (useful). JournalToSqlTable moves DbUp's journal to another schema/table, or a custom IJournal can be plugged in.  
  Source: https://github.com/DbUp/DbUp/blob/main/docs/more-info/journaling.md  
  Absent here, checked: README fixes the journal to schemaversions, and src/Switchknife.Testing.Postgres/PostgresDatabase.cs has const Journal = "schemaversions" relying on it; no option in MigrationOptions.cs.  
  Worth doing? Useful: Two migrators in one database (an app and Facts) share DbUp's default journal; half a day. Cost: about half a day: one option, plus Testing.Postgres reading it

## ccusage/ccusage

https://github.com/ccusage/ccusage · MIT (README; GitHub reports NOASSERTION) · 19,287 stars · last push 2026-11-01. Similar because: Tracks AI-subscription usage per day, session and 5-hour billing window across agent tools; same domain as Switchknife.Pace, which paces that allowance (TypeScript/Rust, no pacing or admission).

- **Burn rate and projected total for the active window** (useful). The blocks report shows tokens per minute and the projected total (and cost) if the current rate continues, with a token-limit warning.  
  Source: https://github.com/ccusage/ccusage/blob/main/docs/guide/blocks-reports.md  
  Absent here, checked: Read src/Switchknife.Pace/README.md and Types.cs; grep -i -E 'projected|burn|eta|TimeToLimit|exhaust' over src/Switchknife.Pace *.cs: only a 'burn' word in a PaceLine comment. Status carries pace line and remaining but no time-to-limit or projected end of week.  
  Worth doing? Useful: Whether the current pace hits the limit before reset is Pace's core question; a pure function, one day. Cost: about 1 day: a pure function of readings over time and a field on the status, with tests under FakeTimeProvider

- **Token-to-dollar pricing with cache tokens and offline price overrides** (not useful). Computes cost from input, output, cache-creation and cache-read tokens per model using a bundled price catalog, overridable per model, usable offline.  
  Source: https://github.com/ccusage/ccusage/blob/main/apps/ccusage/README.md#features  
  Absent here, checked: Types.cs documents CostUsd and EstimateUsd as caller-supplied dollars at list price; grep -i -E 'pricing|price|token|cache' over src/Switchknife.Pace *.cs finds only those doc comments.  
  Worth doing? Not useful: Needs price upkeep; consumers already hold their own prices. Cost: several days plus upkeep as prices change, best as a separate package so Pace stays no-I/O and dependency-free

- **Reading usage from the agents' own log files across many tools** (not useful). One CLI reads local usage data of Claude Code, Codex, OpenCode, Gemini CLI and others and reports it uniformly.  
  Source: https://github.com/ccusage/ccusage/blob/main/apps/ccusage/README.md#features  
  Absent here, checked: Pace has no I/O by design (ADR 0004, src/Switchknife.Pace/README.md); grep -i -E 'jsonl|ReadAll|File\.' over src/Switchknife.Pace *.cs: none.  
  Worth doing? Not useful: Breaks Pace's no-I/O rule; weeks per format. Cost: weeks per tool format, and a conscious break from the no-I/O rule, so only as a separate adapter package

## testcontainers/testcontainers-dotnet

https://github.com/testcontainers/testcontainers-dotnet · MIT · 4,372 stars · last push 2026-10-29. Similar because: Switchknife.Testing.Postgres is built on its Testcontainers.PostgreSql module; features of the module and base library show what the wrapper leaves out.

- **Container reuse across test runs (WithReuse)** (not useful). With reuse on, the container is kept after the run and matched by a configuration hash on the next run, skipping the start.  
  Source: https://github.com/testcontainers/testcontainers-dotnet/blob/main/docs/api/resource_reuse.md  
  Absent here, checked: Read src/Switchknife.Testing.Postgres/PostgresDatabase.cs: StartAsync builds PostgreSqlBuilder(_image).Build() with no reuse and DisposeAsync removes it; grep -i -E 'WithReuse|WithSsl|Ssl' over the package: none.  
  Worth doing? Not useful: Saves seconds on a 1m45s test run; unbenchmarked. Cost: about half a day: an opt-in flag with a label, but the docs mark reuse as experimental and it disables the reaper

- **TLS-enabled PostgreSQL server (WithSsl)** (not useful). The module can start Postgres with server certificates so tests can use SslMode connections.  
  Source: https://github.com/testcontainers/testcontainers-dotnet/blob/main/docs/modules/postgres.md  
  Absent here, checked: grep -i ssl over src/Switchknife.Testing.Postgres: no match; the constructor takes only image and migrate callback.  
  Worth doing? Not useful: No consumer needs verify-full tests. Cost: about half a day: an option passed through to the builder and a test with certificates

## NCronJob-Dev/NCronJob

https://github.com/NCronJob-Dev/NCronJob · MIT · 217 stars · last push 2026-11-01. Similar because: In-process IHostedService job scheduler with retries and TimeProvider-driven delays; the nearest maintained library to Switchknife.Hosting's PollingService (a fixed-interval poller).

- **Cron schedules with time zones** (useful). Jobs run on cron expressions, with a TimeZoneInfo per job, UTC by default.  
  Source: https://github.com/NCronJob-Dev/NCronJob#features  
  Absent here, checked: Read src/Switchknife.Hosting/README.md and PollingService.cs; grep -i -E 'cron|TimeZone|Trigger|jitter|scope' over src/Switchknife.Hosting *.cs: no match. Only Interval and backoff exist, with no initial delay or jitter.  
  Worth doing? Useful: 'Nightly at 03:00 local' cannot be an interval without drift; two days. Cost: about 2 days: a schedule abstraction beside Interval, with a cron parser dependency checked for AOT

- **Trigger a run on demand** (unknown). Instant jobs let code (an API request, for example) start a job now, with parameters, without waiting for the next schedule.  
  Source: https://docs.ncronjob.dev/features/instant-jobs/  
  Absent here, checked: Same grep over src/Switchknife.Hosting: no trigger or wake-up member; PollingService waits Interval only.  
  Worth doing? Unknown: Half a day, but no webhook needs to nudge a poller yet. Cost: about half a day: a TriggerNow() that cancels the wait, plus tests under FakeTimeProvider

- **Bounded retries with a fixed or exponential policy and attempt count in context** (unknown). A RetryPolicy attribute gives N attempts with exponential or fixed delay, and the job sees context.Attempts; delays use TimeProvider.  
  Source: https://docs.ncronjob.dev/features/retry-support/  
  Absent here, checked: README of src/Switchknife.Hosting: it retries forever with a doubled interval up to MaxBackoff and has no attempt cap or attempt number passed to the poll.  
  Worth doing? Unknown: PollingService already backs off; a failure cap is unrequested. Cost: about 1 day: an optional max-failures and an attempt property, plus a stop-or-continue rule
