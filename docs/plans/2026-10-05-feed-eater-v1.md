# feed-eater v1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A .NET service that reads every Miniflux entry, ranks it against Yehor's projects and taste, sends a daily Telegram digest of the top items with concrete suggestions, learns from 👍/👎/💡, files accepted ideas into Plane Intake, and serves the whole archive over MCP.

**Architecture:** One ASP.NET Core process with its own Postgres 18 + pgvector. Background loops (`PollingLoop` subclasses) ingest Miniflux entries and embed them; scheduled jobs (`ScheduledJob`: one run per key, keyed by local date) rebuild profiles, collect Karakeep/GitHub-star signals, run the 07:30 digest and the weekly Hindsight retain. Ranking is vector math in C# (`Scorer`); only the top 60 candidates reach a small model and the top 12 a stronger one, both through LiteLLM. A Telegram long-poller turns button presses into votes and Plane Intake items. MCP tools read the archive.

**Tech Stack:** .NET 10, ASP.NET Core, Dapper 2.1.89, Npgsql 10.0.3, dbup-postgresql 7.0.1, ModelContextProtocol.AspNetCore 2.2.0, Serilog.AspNetCore 10.0.0, Serilog.Formatting.Compact 3.0.0, xUnit 2.9.3, Testcontainers.PostgreSql 4.15.0, Microsoft.AspNetCore.Mvc.Testing 10.0.12, Microsoft.Extensions.TimeProvider.Testing 10.10.0. Versions copied from `personal/senses` so the services move together. Postgres image `pgvector/pgvector:0.8.7-pg18-trixie`.

**Spec:** `docs/specs/2026-10-05-feed-eater-design.md`

## Global Constraints

- Target `net10.0`, `Nullable` enable, `TreatWarningsAsErrors` true, `InvariantGlobalization` true (`Directory.Build.props` copied from senses).
- No Pgvector NuGet package (spec D8): vectors go to SQL as `float[]` parameters cast `@v::real[]::vector` and come back as `embedding::real[]`.
- Embeddings are `text-embedding-3-small`, 1536 dimensions, unit length (`Vectors.Normalize` on receipt), so cosine similarity is a dot product.
- Models through LiteLLM `http://100.64.0.2:4000/`: embed `text-embedding-3-small`, triage `gpt-4.1-nano`, read `claude-haiku-4-5`.
- Caps (config `FeedEater:Caps`): triage 60, read 12, candidate window 3 days, min relevance 2, short content < 1,500 chars, triage text 2,000 chars, read text 24,000 chars, embed text 8,000 chars, centroid over last 500.
- Weights (config `FeedEater:Weights`): taste 0.5, feed prior 0.2, taste needs ≥ 10 positives, feed prior needs ≥ 5 votes on that feed.
- Timezone `Europe/Kyiv`. Digest window 07:30 to 12:00 local; profiles daily 03:00; signals daily 02:00; Hindsight Sundays 18:00.
- Miniflux read/unread state is never written. feed-eater never crawls; full text comes only from Miniflux `fetch-content`.
- Secrets only in env. The Telegram token sits in the HttpClient base URL, so `System.Net.Http.HttpClient` logging stays at `Warning` (appsettings.json) and request URIs are never logged.
- MCP endpoint `/mcp`: bearer token required, any `Origin` header refused, fail closed when the token is unset (copied from senses).
- Code style: match senses: `sealed` classes, primary constructors, file-scoped namespaces, raw SQL through Dapper `CommandDefinition` with the cancellation token, timestamps passed as `.UtcDateTime`, short `///` summaries only where the why is not obvious. DB rows map onto `sealed record` types with `init` properties (Dapper property mapping with `MatchNamesWithUnderscores`); SQL casts each column to the property's type (`::int`, `::float8`, `::text`).
- Every commit message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **First start backfills 10,752 old entries**: they must land in the archive but must not flood the first digest. Candidates are bounded by `published_at` within the window, not only by `ingested_at`. Test in Task 11.
2. **Empty or one-word titles** (Miniflux has entries with `title: ""`): they must never be marked duplicates of each other. `TitleHash.Of` returns `""` below 8 normalised characters and `""` never matches. Test in Tasks 3 and 5.
3. **Telegram fails halfway through a digest**: a retry must send only the messages not yet sent and must not pay for triage or reads again. Test in Task 11.
4. **💡 tapped twice, or on a digest from last week**: at most one Plane Intake item per item. Test in Task 12.
5. **LiteLLM budget runs out mid-run**: the digest goes out with what was read, with a "budget reached" line, instead of failing and retrying until noon. Test in Task 11.

---

## File Structure

```
feed-eater/
├── Directory.Build.props            copied from senses
├── FeedEater.slnx
├── Dockerfile                       senses' Dockerfile, names changed
├── LICENSE                          Apache-2.0
├── README.md
├── profile.example.json             shape of the profile file; the real one lives in homelab-gitops
├── selftest.sh                      calls every MCP tool against a running service
├── .github/workflows/ci.yml
├── db/migrations/0001_init.sql
├── docs/specs/2026-10-05-feed-eater-design.md
├── docs/plans/2026-10-05-feed-eater-v1.md
├── src/FeedEater/
│   ├── FeedEater.csproj
│   ├── Program.cs                   host, migrations, /healthz, /mcp
│   ├── ServiceRegistration.cs       all DI wiring
│   ├── FeedEaterOptions.cs          every setting with its default
│   ├── Json.cs                      shared JSON options and parse helper
│   ├── appsettings.json
│   ├── Storage/                     FeedDb, DatabaseMigrator, one store per table group
│   ├── Loops/                       LoopHealth, PollingLoop, ScheduledJob, Schedule
│   ├── Text/                        HtmlText, UrlCanonicalizer, TitleHash
│   ├── Ranking/                     Vectors, Taste, Scorer
│   ├── Llm/                         LiteLlmClient
│   ├── Ingest/                      MinifluxClient, Ingestor
│   ├── Profiles/                    ProfileFile, ProfileBuilder
│   ├── Plane/                       PlaneClient, IdeaFiler
│   ├── Digest/                      Prompts, LlmJson, DigestFormatter, DigestRun, DigestJob
│   ├── Telegram/                    TelegramClient, CallbackData, CallbackHandler, TelegramPoller
│   ├── Signals/                     KarakeepClient, GitHubStarsClient, SignalJob
│   ├── Memory/                      HindsightClient, WeeklyRetain
│   └── Mcp/                         McpExtensions, FeedTools
└── tests/FeedEater.Tests/
    ├── FeedEater.Tests.csproj
    ├── PostgresFixture.cs, StubHandler.cs, TestVectors.cs, Seed.cs
    ├── Fixtures/miniflux-entries.json, profile.json
    └── one *Tests.cs per component
```

Task order follows dependencies: storage and loops first, pure logic next, then the clients, then the digest that joins them, then the feedback loop, signals, MCP, packaging and deployment.

Spec phases 1 and 2 ship together as `v0.1.0` (Tasks 1–17). Phase 3 (source growth, tuning) is Task 17 Steps 9–10 and later config changes, not code.

---

### Task 0: Preflight (Yehor + agent, before code)

Settles the spec's open questions. Nothing here changes the box.

- [ ] **Step 1: Q2, Reddit `top` feed from the box's IP.** Raw ssh, because `hz` has no HTTP verb:

```bash
ssh hedzer 'curl -s -o /dev/null -w "%{http_code} %{content_type}\n" -A "feed-eater-preflight/0.1" "https://www.reddit.com/r/selfhosted/top/.rss?t=day"'
```

Expected: `200 application/atom+xml...`. A `429` means retry once after 2 minutes; a `403` means the Reddit cleanup in Task 17 keeps the current `/.rss` URLs and only slows them down.

- [ ] **Step 2: Q3, LiteLLM cost header.** The master key is read on the box and never printed:

```bash
ssh hedzer 'K=$(grep ^LITELLM_MASTER_KEY= /opt/stacks/litellm/.env | cut -d= -f2-); curl -s -D - -o /dev/null http://127.0.0.1:4000/v1/embeddings -H "Authorization: Bearer $K" -H "Content-Type: application/json" -d "{\"model\":\"text-embedding-3-small\",\"input\":[\"ping\"]}" | grep -i "^x-litellm-response-cost"'
```

Expected: one `x-litellm-response-cost: ...` line. If it is missing, `LiteLlmClient` still works (cost recorded as 0) and the digest header's spend line reads $0.00; note it in `docs/specs/2026-10-05-preflight.md`.

- [ ] **Step 3: Q4 is answered.** `host/opt-homelab/plane-sync.py:129` posts `{"issue": {"name", "description_html", "priority"}}` to `intake-issues/` in production. Nothing to run.

- [ ] **Step 4 (optional): real Miniflux sample.** Replaces the synthetic fixture of Task 5 with three real entries (public article text only):

```bash
ssh hedzer 'T=$(grep ^MINIFLUX_TOKEN= /opt/homelab/.ingest-secrets | cut -d= -f2-); curl -s -H "X-Auth-Token: $T" "http://127.0.0.1:8092/v1/entries?after_entry_id=0&order=id&direction=asc&limit=3"' > tests/FeedEater.Tests/Fixtures/miniflux-entries.real.json
```

Keep it only if `jq '.entries | length'` prints 3; the tests in Task 5 still use the synthetic file, which pins the duplicate cases.

- [ ] **Step 5: Record the answers** in `docs/specs/2026-10-05-preflight.md` (one table row per question: question, command, answer), then commit:

```bash
git add docs/specs/2026-10-05-preflight.md
git commit -m "docs: preflight answers"
```

---

### Task 1: Scaffold, options, schema

**Files:**
- Create: `Directory.Build.props`, `FeedEater.slnx`, `src/FeedEater/FeedEater.csproj`, `src/FeedEater/Program.cs`, `src/FeedEater/ServiceRegistration.cs`, `src/FeedEater/FeedEaterOptions.cs`, `src/FeedEater/Json.cs`, `src/FeedEater/appsettings.json`, `src/FeedEater/Storage/FeedDb.cs`, `src/FeedEater/Storage/DatabaseMigrator.cs`, `db/migrations/0001_init.sql`
- Test: `tests/FeedEater.Tests/FeedEater.Tests.csproj`, `tests/FeedEater.Tests/PostgresFixture.cs`, `tests/FeedEater.Tests/TestVectors.cs`, `tests/FeedEater.Tests/OptionsTests.cs`, `tests/FeedEater.Tests/MigrationTests.cs`

**Interfaces:**
- Produces: `FeedEaterOptions` (all nested option classes below), `FeedDb(NpgsqlDataSource)` with `DataSource` and `PingAsync(ct)`, `DatabaseMigrator(string, ILogger<DatabaseMigrator>).Run()`, `Json.Options`, `Json.Parse(string)`, `ServiceRegistration.AddFeedEater(IServiceCollection, IConfiguration)`, test `PostgresFixture` (`Db`, `ConnectionString`, `ResetAsync()`), `TestVectors.Dims`, `TestVectors.OneHot(int)`.

- [ ] **Step 1: Copy the build props and write the solution and project files**

```bash
cp ../senses/Directory.Build.props .
mkdir -p src/FeedEater/Storage db/migrations tests/FeedEater.Tests/Fixtures
```

`FeedEater.slnx`:

```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/FeedEater/FeedEater.csproj" />
  </Folder>
  <Folder Name="/tests/">
    <Project Path="tests/FeedEater.Tests/FeedEater.Tests.csproj" />
  </Folder>
</Solution>
```

`src/FeedEater/FeedEater.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">

  <ItemGroup>
    <PackageReference Include="Dapper" Version="2.1.89" />
    <PackageReference Include="dbup-postgresql" Version="7.0.1" />
    <PackageReference Include="ModelContextProtocol.AspNetCore" Version="2.2.0" />
    <PackageReference Include="Npgsql" Version="10.0.3" />
    <PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
    <PackageReference Include="Serilog.Formatting.Compact" Version="3.0.0" />
  </ItemGroup>

  <ItemGroup>
    <!-- Embedded as FeedEater.Migrations.<file>; DatabaseMigrator filters on ".Migrations." -->
    <EmbeddedResource Include="../../db/migrations/*.sql" LinkBase="Migrations" />
    <InternalsVisibleTo Include="FeedEater.Tests" />
  </ItemGroup>

</Project>
```

`tests/FeedEater.Tests/FeedEater.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.12" />
    <PackageReference Include="Microsoft.Extensions.TimeProvider.Testing" Version="10.10.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="Testcontainers.PostgreSql" Version="4.15.0" />
    <PackageReference Include="coverlet.collector" Version="10.1.0" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="../../src/FeedEater/FeedEater.csproj" />
    <None Include="Fixtures/**" CopyToOutputDirectory="PreserveNewest" />
    <Using Include="Xunit" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the failing tests**

`tests/FeedEater.Tests/TestVectors.cs`:

```csharp
namespace FeedEater.Tests;

public static class TestVectors
{
    public const int Dims = 1536;

    public static float[] OneHot(int dim)
    {
        var v = new float[Dims];
        v[dim] = 1;
        return v;
    }
}
```

`tests/FeedEater.Tests/PostgresFixture.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using FeedEater.Storage;
using Testcontainers.PostgreSql;

namespace FeedEater.Tests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("pgvector/pgvector:0.8.7-pg18-trixie").Build();

    public FeedDb Db { get; private set; } = null!;
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        new DatabaseMigrator(ConnectionString, NullLogger<DatabaseMigrator>.Instance).Run();
        Db = new FeedDb(NpgsqlDataSource.Create(ConnectionString));
    }

    public async Task ResetAsync()
    {
        await using var c = await Db.DataSource.OpenConnectionAsync();
        await using var cmd = new NpgsqlCommand(
            "truncate llm_usage, cursors, profiles, signals, ideas, votes, digests, reads, triage, items, feeds restart identity cascade", c);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        await Db.DataSource.DisposeAsync();
        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
```

`tests/FeedEater.Tests/OptionsTests.cs`:

```csharp
using Microsoft.Extensions.Configuration;

namespace FeedEater.Tests;

public sealed class OptionsTests
{
    [Fact]
    public void Defaults_match_the_spec()
    {
        var o = new FeedEaterOptions();

        Assert.Equal(new TimeSpan(7, 30, 0), o.DigestAt);
        Assert.Equal(new TimeSpan(12, 0, 0), o.DigestGiveUpAt);
        Assert.Equal(60, o.Caps.Triage);
        Assert.Equal(12, o.Caps.Read);
        Assert.Equal(3, o.Caps.CandidateDays);
        Assert.Equal(2, o.Caps.MinRelevance);
        Assert.Equal("gpt-4.1-nano", o.Llm.TriageModel);
        Assert.Equal("claude-haiku-4-5", o.Llm.ReadModel);
        Assert.Equal("text-embedding-3-small", o.Llm.EmbedModel);
        Assert.Equal(0.5, o.Weights.Taste);
        Assert.Equal(0.2, o.Weights.Prior);
        Assert.Equal("FEED", o.Plane.FallbackProject);
        Assert.Equal("Europe/Kyiv", o.Zone.Id);
    }

    [Fact]
    public void Binds_nested_settings_from_configuration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FeedEater:DigestAt"] = "06:45:00",
            ["FeedEater:Caps:Read"] = "8",
            ["FeedEater:Telegram:AllowedUserId"] = "123456789",
        }).Build();

        var o = config.GetSection(FeedEaterOptions.Section).Get<FeedEaterOptions>()!;

        Assert.Equal(new TimeSpan(6, 45, 0), o.DigestAt);
        Assert.Equal(8, o.Caps.Read);
        Assert.Equal(123456789, o.Telegram.AllowedUserId);
    }
}
```

`tests/FeedEater.Tests/MigrationTests.cs`:

```csharp
using Dapper;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MigrationTests(PostgresFixture pg)
{
    [Fact]
    public async Task Schema_has_pgvector_and_round_trips_an_embedding_as_a_real_array()
    {
        await pg.ResetAsync();
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var v = TestVectors.OneHot(3);
        await c.ExecuteAsync("insert into feeds (id, title) values (1, 'f')");

        var id = await c.ExecuteScalarAsync<long>(
            """
            insert into items (feed_id, url, canonical_url, title_hash, title, published_at, embedding)
            values (1, 'https://a.example/x', 'https://a.example/x', '', 'Title', now(), @v::real[]::vector)
            returning id
            """, new { v });
        var back = await c.ExecuteScalarAsync<float[]>("select embedding::real[] from items where id = @id", new { id });

        Assert.Equal(v, back);
        Assert.True(await pg.Db.PingAsync(default));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx`
Expected: build FAILS with `The type or namespace name 'FeedEaterOptions' could not be found` (and `FeedDb`, `DatabaseMigrator`).

- [ ] **Step 4: Write the options, JSON helper and appsettings**

`src/FeedEater/FeedEaterOptions.cs`:

```csharp
namespace FeedEater;

public sealed class FeedEaterOptions
{
    public const string Section = "FeedEater";

    public string TimeZone { get; set; } = "Europe/Kyiv";

    /// <summary>False in tests that boot the host, so nothing polls real services.</summary>
    public bool RunJobs { get; set; } = true;

    public string ProfilePath { get; set; } = "/config/profile.json";
    public TimeSpan DigestAt { get; set; } = new(7, 30, 0);
    public TimeSpan DigestGiveUpAt { get; set; } = new(12, 0, 0);

    public MinifluxOptions Miniflux { get; set; } = new();
    public LlmOptions Llm { get; set; } = new();
    public CapsOptions Caps { get; set; } = new();
    public WeightsOptions Weights { get; set; } = new();
    public TelegramOptions Telegram { get; set; } = new();
    public PlaneOptions Plane { get; set; } = new();
    public KarakeepOptions Karakeep { get; set; } = new();
    public GitHubOptions GitHub { get; set; } = new();
    public HindsightOptions Hindsight { get; set; } = new();

    public TimeZoneInfo Zone => TimeZoneInfo.FindSystemTimeZoneById(TimeZone);
}

public sealed class MinifluxOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:8092/";
    public string Token { get; set; } = "";
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(30);
    public int PageSize { get; set; } = 100;
}

public sealed class LlmOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:4000/";
    public string ApiKey { get; set; } = "";
    public string EmbedModel { get; set; } = "text-embedding-3-small";
    public string TriageModel { get; set; } = "gpt-4.1-nano";
    public string ReadModel { get; set; } = "claude-haiku-4-5";
    public int EmbedBatch { get; set; } = 64;
}

public sealed class CapsOptions
{
    public int Triage { get; set; } = 60;
    public int Read { get; set; } = 12;
    public int CandidateDays { get; set; } = 3;
    public int MinRelevance { get; set; } = 2;
    public int ShortContentChars { get; set; } = 1500;
    public int TriageChars { get; set; } = 2000;
    public int ReadChars { get; set; } = 24000;
    public int EmbedChars { get; set; } = 8000;
    public int Centroid { get; set; } = 500;
}

public sealed class WeightsOptions
{
    public double Taste { get; set; } = 0.5;
    public double Prior { get; set; } = 0.2;
    public int MinPositives { get; set; } = 10;
    public int MinFeedVotes { get; set; } = 5;
}

public sealed class TelegramOptions
{
    public string BaseUrl { get; set; } = "https://api.telegram.org/";
    public string Token { get; set; } = "";

    /// <summary>Yehor's Telegram user id: the only sender whose button presses count, and the chat digests go to.</summary>
    public long AllowedUserId { get; set; }
}

public sealed class PlaneOptions
{
    public string BaseUrl { get; set; } = "http://plane-proxy/";
    public string Token { get; set; } = "";
    public string Workspace { get; set; } = "homelab";
    public string FallbackProject { get; set; } = "FEED";
}

public sealed class KarakeepOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:3009/";
    public string Token { get; set; } = "";
}

public sealed class GitHubOptions
{
    public string BaseUrl { get; set; } = "https://api.github.com/";
    public string User { get; set; } = "Egoushka";
}

public sealed class HindsightOptions
{
    public string BaseUrl { get; set; } = "http://100.64.0.2:8888/";
    public string Token { get; set; } = "";
    public string Bank { get; set; } = "learning";
}
```

`src/FeedEater/Json.cs`:

```csharp
using System.Text.Json;

namespace FeedEater;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonElement Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}
```

`src/FeedEater/appsettings.json`:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": { "Microsoft.AspNetCore": "Warning", "System.Net.Http.HttpClient": "Warning" }
    }
  }
}
```

- [ ] **Step 5: Write the schema**

`db/migrations/0001_init.sql`:

```sql
create extension if not exists vector;

create table feeds (
    id       bigint primary key,          -- Miniflux feed id
    title    text not null,
    category text,
    site_url text
);

create table items (
    id                bigserial primary key,
    miniflux_entry_id bigint unique,
    feed_id           bigint references feeds (id),
    url               text not null,
    canonical_url     text not null,
    title_hash        text not null,      -- '' when the title is too short to compare
    title             text not null,
    published_at      timestamptz not null,
    ingested_at       timestamptz not null default now(),
    content           text not null default '',   -- plain text, HTML stripped
    embedding         vector(1536),
    score             real,
    profile_key       text,
    duplicate_of      bigint references items (id),
    search            tsvector generated always as (
        setweight(to_tsvector('simple', title), 'A') ||
        setweight(to_tsvector('simple', left(content, 100000)), 'B')) stored
);
create index items_canonical_url_idx on items (canonical_url);
create index items_title_hash_idx on items (title_hash, published_at);
create index items_ingested_at_idx on items (ingested_at);
create index items_unembedded_idx on items (id) where embedding is null;
create index items_embedding_idx on items using hnsw (embedding vector_cosine_ops);
create index items_search_idx on items using gin (search);

create table triage (
    item_id   bigint primary key references items (id),
    relevance smallint not null,
    project   text,
    kind      text not null,
    reason    text not null default '',
    model     text not null,
    at        timestamptz not null default now()
);

create table reads (
    item_id    bigint primary key references items (id),
    summary    text not null,
    why        text not null,
    kind       text not null,
    project    text,
    suggestion text,
    model      text not null,
    at         timestamptz not null default now()
);

create table digests (
    local_date date primary key,
    status     text not null check (status in ('building', 'sent', 'failed')),
    candidates int not null default 0,
    triaged    int not null default 0,
    item_ids   bigint[] not null default '{}',
    sent_count int not null default 0,     -- messages already delivered; a retry resumes here
    note       text,                       -- shown in the header: budget reached, Miniflux down
    error      text,                       -- last failure; never shown in the header
    sent_at    timestamptz
);

create table votes (
    item_id bigint primary key references items (id),
    value   smallint not null check (value in (-1, 1)),
    at      timestamptz not null default now()
);

create table ideas (
    item_id        bigint primary key references items (id),
    plane_project  text not null,
    plane_issue_id text not null,
    title          text not null,
    at             timestamptz not null default now()
);

create table signals (
    id          bigserial primary key,
    source      text not null check (source in ('karakeep', 'github_star')),
    external_id text not null,
    url         text,
    title       text not null,
    embedding   vector(1536),
    polarity    smallint not null default 1,
    at          timestamptz not null,
    unique (source, external_id)
);

create table profiles (
    key              text primary key,
    kind             text not null check (kind in ('project', 'topic')),
    plane_identifier text,
    description      text not null,
    embedding        vector(1536) not null,
    built_at         timestamptz not null
);

create table cursors (
    name  text primary key,
    value text not null
);

create table llm_usage (
    id            bigserial primary key,
    at            timestamptz not null default now(),
    purpose       text not null,
    model         text not null,
    input_tokens  int not null,
    output_tokens int not null,
    cost          numeric(12, 6) not null
);
create index llm_usage_at_idx on llm_usage (at);
```

- [ ] **Step 6: Write the database plumbing and host**

`src/FeedEater/Storage/FeedDb.cs`:

```csharp
using Dapper;
using Npgsql;

namespace FeedEater.Storage;

public sealed class FeedDb(NpgsqlDataSource dataSource)
{
    static FeedDb() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    public NpgsqlDataSource DataSource => dataSource;

    public async Task<bool> PingAsync(CancellationToken ct)
    {
        try
        {
            await using var c = await dataSource.OpenConnectionAsync(ct);
            return await c.ExecuteScalarAsync<int>(new CommandDefinition("select 1", cancellationToken: ct)) == 1;
        }
        catch (NpgsqlException)
        {
            return false;
        }
    }
}
```

`src/FeedEater/Storage/DatabaseMigrator.cs`:

```csharp
using System.Reflection;
using DbUp;
using DbUp.Engine.Output;
using Npgsql;

namespace FeedEater.Storage;

/// <summary>Applies <c>db/migrations/*.sql</c> in name order, once each, journalled in <c>schemaversions</c>.</summary>
public sealed class DatabaseMigrator(string connectionString, ILogger<DatabaseMigrator> logger)
{
    public void Run()
    {
        EnsureDatabase.For.PostgresqlDatabase(
            new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);

        var result = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(
                Assembly.GetExecutingAssembly(),
                name => name.Contains(".Migrations.", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .LogTo(new MigrationLog(logger))
            .Build()
            .PerformUpgrade();

        if (!result.Successful)
        {
            throw new InvalidOperationException(
                $"Database migration failed on '{result.ErrorScript?.Name ?? "(unknown script)"}'.", result.Error);
        }

        logger.LogInformation("Database schema is up to date ({Applied} script(s) applied).", result.Scripts.Count());
    }

    private sealed class MigrationLog(ILogger logger) : IUpgradeLog
    {
        public void LogTrace(string format, params object[] args) { }
        public void LogDebug(string format, params object[] args) { }
        public void LogInformation(string format, params object[] args) => logger.LogDebug("{Message}", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
        public void LogWarning(string format, params object[] args) => logger.LogWarning("{Message}", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
        public void LogError(string format, params object[] args) => logger.LogError("{Message}", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
        public void LogError(Exception ex, string format, params object[] args) => logger.LogError(ex, "{Message}", string.Format(System.Globalization.CultureInfo.InvariantCulture, format, args));
    }
}
```

`src/FeedEater/ServiceRegistration.cs` (Task 14 replaces it with the full wiring):

```csharp
using Npgsql;
using FeedEater.Storage;

namespace FeedEater;

public static class ServiceRegistration
{
    public static IServiceCollection AddFeedEater(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FeedEater")
            ?? throw new InvalidOperationException("ConnectionStrings:FeedEater is not set.");

        services.Configure<FeedEaterOptions>(configuration.GetSection(FeedEaterOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DatabaseMigrator(connectionString, sp.GetRequiredService<ILogger<DatabaseMigrator>>()));
        services.AddSingleton(new FeedDb(NpgsqlDataSource.Create(connectionString)));
        return services;
    }
}
```

`src/FeedEater/Program.cs` (Task 14 adds `/mcp`):

```csharp
using FeedEater;
using FeedEater.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddFeedEater(builder.Configuration);

var app = builder.Build();
app.Services.GetRequiredService<DatabaseMigrator>().Run();

app.MapGet("/healthz", async (FeedDb db, CancellationToken ct) => await db.PingAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.Run();

public partial class Program;
```

- [ ] **Step 7: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS (3 tests). Docker must be running for Testcontainers.

- [ ] **Step 8: Commit**

```bash
git add Directory.Build.props FeedEater.slnx src db tests
git commit -m "feat: scaffold, options and schema"
```

---

### Task 2: Loops, schedule and cursors

**Files:**
- Create: `src/FeedEater/Loops/LoopHealth.cs`, `src/FeedEater/Loops/PollingLoop.cs`, `src/FeedEater/Loops/Schedule.cs`, `src/FeedEater/Loops/ScheduledJob.cs`, `src/FeedEater/Storage/CursorStore.cs`
- Test: `tests/FeedEater.Tests/ScheduleTests.cs`, `tests/FeedEater.Tests/ScheduledJobTests.cs`

**Interfaces:**
- Consumes: `FeedDb`, `FeedEaterOptions.Zone`.
- Produces: `LoopHealth(TimeProvider)` with `Succeeded(string)`, `Failed(string, string)`, `IsDown(string)`, `Snapshot()`; abstract `PollingLoop(LoopHealth, TimeProvider, ILogger)` with `Name`, `Interval`, `PollAsync(ct)`, protected `Time`, `Logger`; `Schedule.Local(DateTimeOffset, TimeZoneInfo)`, `Schedule.Key(DateTime)`, `Schedule.LatestDaily(DateTime, TimeSpan)`, `Schedule.Window(DateTime, TimeSpan, TimeSpan)`, `Schedule.LatestWeekly(DateTime, DayOfWeek, TimeSpan)`; abstract `ScheduledJob(CursorStore, IOptions<FeedEaterOptions>, LoopHealth, TimeProvider, ILogger)` with `DueKey(DateTime)`, `RunAsync(string, ct)`, protected `Settings`, internal `TickAsync(ct)`; `CursorStore(FeedDb)` with `GetAsync(name, ct)`, `SetAsync(name, value, ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/ScheduleTests.cs`:

```csharp
using FeedEater.Loops;

namespace FeedEater.Tests;

public sealed class ScheduleTests
{
    private static readonly TimeZoneInfo Kyiv = TimeZoneInfo.FindSystemTimeZoneById("Europe/Kyiv");
    private static readonly TimeSpan At0730 = new(7, 30, 0);
    private static readonly TimeSpan Noon = new(12, 0, 0);

    [Theory]
    [InlineData("2026-10-05T07:29:00", null)]
    [InlineData("2026-10-05T07:30:00", "2026-10-05")]
    [InlineData("2026-10-05T11:59:00", "2026-10-05")]
    [InlineData("2026-10-05T12:00:00", null)]
    public void Window_is_open_from_start_until_end(string local, string? expected) =>
        Assert.Equal(expected, Schedule.Window(DateTime.Parse(local, System.Globalization.CultureInfo.InvariantCulture), At0730, Noon));

    [Fact]
    public void Local_time_follows_the_october_dst_change()
    {
        // Kyiv is UTC+3 until 2026-10-25 04:00 local, then UTC+2.
        Assert.Equal(new DateTime(2026, 10, 24, 7, 30, 0), Schedule.Local(new DateTimeOffset(2026, 10, 24, 4, 30, 0, TimeSpan.Zero), Kyiv));
        Assert.Equal(new DateTime(2026, 10, 25, 7, 30, 0), Schedule.Local(new DateTimeOffset(2026, 10, 25, 5, 30, 0, TimeSpan.Zero), Kyiv));
    }

    [Fact]
    public void Latest_daily_points_at_yesterday_before_the_hour()
    {
        Assert.Equal("2026-10-04", Schedule.LatestDaily(new DateTime(2026, 10, 5, 1, 0, 0), new TimeSpan(3, 0, 0)));
        Assert.Equal("2026-10-05", Schedule.LatestDaily(new DateTime(2026, 10, 5, 3, 0, 0), new TimeSpan(3, 0, 0)));
    }

    [Fact]
    public void Latest_weekly_points_at_the_last_sunday_evening()
    {
        var at = new TimeSpan(18, 0, 0);
        Assert.Equal("2026-09-27", Schedule.LatestWeekly(new DateTime(2026, 10, 4, 17, 59, 0), DayOfWeek.Sunday, at));
        Assert.Equal("2026-10-04", Schedule.LatestWeekly(new DateTime(2026, 10, 4, 18, 0, 0), DayOfWeek.Sunday, at));
        Assert.Equal("2026-10-04", Schedule.LatestWeekly(new DateTime(2026, 10, 5, 9, 0, 0), DayOfWeek.Sunday, at));
    }
}
```

`tests/FeedEater.Tests/ScheduledJobTests.cs`:

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduledJobTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private sealed class CountingJob(CursorStore cursors, TimeProvider time, bool fail)
        : ScheduledJob(cursors, Options.Create(new FeedEaterOptions()), new LoopHealth(time), time, NullLogger.Instance)
    {
        public List<string> Runs { get; } = [];
        protected override string Name => "counting";
        protected override string? DueKey(DateTime localNow) => Schedule.Window(localNow, new TimeSpan(7, 30, 0), new TimeSpan(12, 0, 0));

        protected override Task RunAsync(string key, CancellationToken ct)
        {
            Runs.Add(key);
            return fail ? throw new InvalidOperationException("boom") : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Runs_once_per_key_and_again_the_next_day()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 4, 40, 0, TimeSpan.Zero)); // 07:40 Kyiv
        var job = new CountingJob(new CursorStore(pg.Db), time, fail: false);

        await job.TickAsync(default);
        await job.TickAsync(default);
        time.Advance(TimeSpan.FromDays(1));
        await job.TickAsync(default);

        Assert.Equal(["2026-10-05", "2026-10-06"], job.Runs);
    }

    [Fact]
    public async Task Does_nothing_outside_the_window_and_retries_after_a_failure()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 3, 0, 0, TimeSpan.Zero)); // 06:00 Kyiv
        var cursors = new CursorStore(pg.Db);
        var job = new CountingJob(cursors, time, fail: true);

        await job.TickAsync(default);
        Assert.Empty(job.Runs);

        time.Advance(TimeSpan.FromHours(2)); // 08:00 Kyiv
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => job.TickAsync(default));

        Assert.Equal(["2026-10-05", "2026-10-05"], job.Runs);
        Assert.Null(await cursors.GetAsync("job:counting", default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~Schedule"`
Expected: build FAILS with `The type or namespace name 'Loops' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Loops/LoopHealth.cs`:

```csharp
using System.Collections.Concurrent;

namespace FeedEater.Loops;

public sealed record LoopStatus(string Name, bool Up, DateTimeOffset? LastSuccess, string? LastError);

public sealed class LoopHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, LoopStatus> _status = new();

    public void Succeeded(string name) => _status[name] = new LoopStatus(name, true, time.GetUtcNow(), null);

    public void Failed(string name, string error) => _status.AddOrUpdate(
        name, n => new LoopStatus(n, false, null, error), (_, old) => old with { Up = false, LastError = error });

    public bool IsDown(string name) => _status.TryGetValue(name, out var s) && !s.Up;

    public IReadOnlyList<LoopStatus> Snapshot() => _status.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
}
```

`src/FeedEater/Loops/PollingLoop.cs`:

```csharp
namespace FeedEater.Loops;

/// <summary>Runs <see cref="PollAsync"/> forever, records health, backs off on failure up to 5 minutes.</summary>
public abstract class PollingLoop(LoopHealth health, TimeProvider time, ILogger logger) : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    protected abstract string Name { get; }
    protected abstract TimeSpan Interval { get; }
    protected abstract Task PollAsync(CancellationToken ct);

    // Subclasses use these instead of their own copies: capturing a parameter that is also passed to the base is CS9107.
    protected TimeProvider Time => time;
    protected ILogger Logger => logger;

    public static TimeSpan Backoff(TimeSpan interval, int failures) =>
        TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * Math.Pow(2, failures - 1), MaxBackoff.TotalSeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollAsync(stoppingToken);
                failures = 0;
                health.Succeeded(Name);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                failures++;
                logger.LogWarning(ex, "{Loop} failed ({Failures} in a row)", Name, failures);
                health.Failed(Name, ex.Message);
            }

            await Task.Delay(failures == 0 ? Interval : Backoff(Interval, failures), time, stoppingToken);
        }
    }
}
```

`src/FeedEater/Loops/Schedule.cs`:

```csharp
using System.Globalization;

namespace FeedEater.Loops;

/// <summary>Run keys for scheduled jobs. A key is a local date; a job runs once per key.</summary>
public static class Schedule
{
    public static DateTime Local(DateTimeOffset utc, TimeZoneInfo zone) => TimeZoneInfo.ConvertTime(utc, zone).DateTime;

    public static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The most recent day on which <paramref name="at"/> has passed: a job missed while down runs at start.</summary>
    public static string LatestDaily(DateTime local, TimeSpan at) => Key(local.TimeOfDay >= at ? local.Date : local.Date.AddDays(-1));

    /// <summary>Today's key while inside [from, until), otherwise none: a missed window is skipped, not caught up.</summary>
    public static string? Window(DateTime local, TimeSpan from, TimeSpan until) =>
        local.TimeOfDay >= from && local.TimeOfDay < until ? Key(local.Date) : null;

    public static string LatestWeekly(DateTime local, DayOfWeek day, TimeSpan at)
    {
        var back = ((int)local.DayOfWeek - (int)day + 7) % 7;
        var candidate = local.Date.AddDays(-back);
        if (back == 0 && local.TimeOfDay < at)
        {
            candidate = candidate.AddDays(-7);
        }

        return Key(candidate);
    }
}
```

`src/FeedEater/Storage/CursorStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed class CursorStore(FeedDb db)
{
    public async Task<string?> GetAsync(string name, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<string?>(new CommandDefinition(
            "select value from cursors where name = @name", new { name }, cancellationToken: ct));
    }

    public async Task SetAsync(string name, string value, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "insert into cursors (name, value) values (@name, @value) on conflict (name) do update set value = excluded.value",
            new { name, value }, cancellationToken: ct));
    }
}
```

`src/FeedEater/Loops/ScheduledJob.cs`:

```csharp
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Loops;

/// <summary>
/// Checks every minute whether a run is due; runs at most once per key and stores the key only after success,
/// so a failed run is retried with the polling backoff.
/// </summary>
public abstract class ScheduledJob(CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger logger)
    : PollingLoop(health, time, logger)
{
    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);
    protected FeedEaterOptions Settings => options.Value;

    /// <summary>The run key for this moment, or null when nothing is due.</summary>
    protected abstract string? DueKey(DateTime localNow);

    protected abstract Task RunAsync(string key, CancellationToken ct);

    protected override async Task PollAsync(CancellationToken ct)
    {
        var key = DueKey(Schedule.Local(Time.GetUtcNow(), options.Value.Zone));
        if (key is null || await cursors.GetAsync($"job:{Name}", ct) == key)
        {
            return;
        }

        await RunAsync(key, ct);
        await cursors.SetAsync($"job:{Name}", key, ct);
    }

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS (all tests so far).

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: polling loops, schedule keys and cursors"
```

---
### Task 3: Text and vector helpers

**Files:**
- Create: `src/FeedEater/Text/HtmlText.cs`, `src/FeedEater/Text/UrlCanonicalizer.cs`, `src/FeedEater/Text/TitleHash.cs`, `src/FeedEater/Ranking/Vectors.cs`
- Modify: `tests/FeedEater.Tests/TestVectors.cs` (add `Blend`)
- Test: `tests/FeedEater.Tests/TextTests.cs`, `tests/FeedEater.Tests/VectorsTests.cs`

**Interfaces:**
- Produces: `HtmlText.ToPlain(string?) -> string`, `UrlCanonicalizer.Canonical(string) -> string`, `TitleHash.Of(string) -> string` (16 hex chars, or `""` below 8 normalised characters), `Vectors.Dot(float[], float[]) -> double`, `Vectors.Normalize(float[]) -> float[]`, `Vectors.Centroid(IReadOnlyList<float[]>) -> float[]?` (normalised mean, null when empty), test `TestVectors.Blend(int a, int b, float weightB)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/TextTests.cs`:

```csharp
using FeedEater.Text;

namespace FeedEater.Tests;

public sealed class TextTests
{
    [Fact]
    public void Html_becomes_plain_text_without_scripts()
    {
        Assert.Equal("Hello world\nTwo", HtmlText.ToPlain("<p>Hello&nbsp;<b>world</b></p><script>x()</script><p>Two</p>"));
        Assert.Equal("a & b <c>", HtmlText.ToPlain("a &amp; b &lt;c&gt;"));
        Assert.Equal("a\n\nb", HtmlText.ToPlain("<p>a</p><p></p><p></p><p>b</p>"));
        Assert.Equal("", HtmlText.ToPlain(null));
        Assert.Equal("", HtmlText.ToPlain("   "));
    }

    [Theory]
    [InlineData("HTTP://WWW.Example.com/a/b/?utm_source=x&b=2&a=1#frag", "https://example.com/a/b?a=1&b=2")]
    [InlineData("https://example.com/", "https://example.com")]
    [InlineData("http://example.com:8080/x?ref=hn&fbclid=1", "https://example.com:8080/x")]
    [InlineData("not a url", "not a url")]
    public void Urls_are_canonicalised(string url, string expected) => Assert.Equal(expected, UrlCanonicalizer.Canonical(url));

    [Fact]
    public void Title_hash_ignores_case_and_punctuation()
    {
        Assert.Equal(TitleHash.Of("Postgres 18.1 released"), TitleHash.Of("Postgres 18.1 Released!"));
        Assert.NotEqual(TitleHash.Of("Postgres 18.1 released"), TitleHash.Of("Postgres 18.2 released"));
        Assert.Equal(16, TitleHash.Of("Postgres 18.1 released").Length);
        Assert.NotEqual("", TitleHash.Of("Привіт, світ — новини"));
    }

    [Fact]
    public void Short_or_empty_titles_get_no_hash_so_they_never_match()
    {
        Assert.Equal("", TitleHash.Of(""));
        Assert.Equal("", TitleHash.Of("News"));
        Assert.Equal("", TitleHash.Of("!!! ???"));
    }
}
```

`tests/FeedEater.Tests/VectorsTests.cs`:

```csharp
using FeedEater.Ranking;

namespace FeedEater.Tests;

public sealed class VectorsTests
{
    [Fact]
    public void Normalize_gives_unit_length_and_dot_is_cosine()
    {
        var v = Vectors.Normalize([3f, 4f]);

        Assert.Equal(0.6f, v[0], 5);
        Assert.Equal(0.8f, v[1], 5);
        Assert.Equal(1.0, Vectors.Dot(v, v), 5);
        Assert.Equal([0f, 0f], Vectors.Normalize([0f, 0f]));
    }

    [Fact]
    public void Centroid_is_the_normalised_mean()
    {
        var c = Vectors.Centroid([TestVectors.OneHot(0), TestVectors.OneHot(1)])!;

        Assert.Equal(Math.Sqrt(0.5), c[0], 5);
        Assert.Equal(Math.Sqrt(0.5), c[1], 5);
        Assert.Null(Vectors.Centroid([]));
    }

    [Fact]
    public void Dot_refuses_vectors_of_different_length() =>
        Assert.Throws<ArgumentException>(() => Vectors.Dot([1f], [1f, 0f]));
}
```

Add to `tests/FeedEater.Tests/TestVectors.cs` (inside the class; add `using FeedEater.Ranking;` at the top):

```csharp
    /// <summary>A unit vector mostly along <paramref name="a"/>, partly along <paramref name="b"/>.</summary>
    public static float[] Blend(int a, int b, float weightB)
    {
        var v = new float[Dims];
        v[a] = 1 - weightB;
        v[b] = weightB;
        return Vectors.Normalize(v);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~TextTests|FullyQualifiedName~VectorsTests"`
Expected: build FAILS with `The type or namespace name 'Text' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Text/HtmlText.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;

namespace FeedEater.Text;

public static partial class HtmlText
{
    public static string ToPlain(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return "";
        }

        var s = ScriptOrStyle().Replace(html, " ");
        s = BlockEnd().Replace(s, "\n");
        s = Tag().Replace(s, " ");
        s = WebUtility.HtmlDecode(s);
        s = Spaces().Replace(s, " ");
        s = LineEdges().Replace(s, "\n");
        s = BlankLines().Replace(s, "\n\n");
        return s.Trim();
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"<(br|/p|/div|/li|/tr|/h[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v ]+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@" *\n *")]
    private static partial Regex LineEdges();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
```

`src/FeedEater/Text/UrlCanonicalizer.cs`:

```csharp
namespace FeedEater.Text;

/// <summary>One form per article: https, no www, no fragment, no tracking parameters, sorted query, no trailing slash.</summary>
public static class UrlCanonicalizer
{
    public static string Canonical(string url)
    {
        var trimmed = url.Trim();
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return trimmed;
        }

        var host = uri.Host.StartsWith("www.", StringComparison.Ordinal) ? uri.Host[4..] : uri.Host;
        var port = uri.IsDefaultPort ? "" : $":{uri.Port}";
        var path = uri.AbsolutePath.Length > 1 ? uri.AbsolutePath.TrimEnd('/') : "";
        var query = string.Join('&', uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !IsTracking(p.Split('=')[0]))
            .Order(StringComparer.Ordinal));
        return $"https://{host}{port}{path}{(query.Length > 0 ? "?" + query : "")}";
    }

    private static bool IsTracking(string key) =>
        key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || key is "ref" or "fbclid" or "gclid" or "mc_cid" or "mc_eid";
}
```

`src/FeedEater/Text/TitleHash.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace FeedEater.Text;

public static class TitleHash
{
    private const int MinLetters = 8;

    /// <summary>Lowercase letters and digits only, hashed. Empty below 8 of them: "News" must not match every other "News".</summary>
    public static string Of(string title)
    {
        var letters = new StringBuilder(title.Length);
        foreach (var ch in title.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                letters.Append(ch);
            }
        }

        return letters.Length < MinLetters
            ? ""
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(letters.ToString())))[..16];
    }
}
```

`src/FeedEater/Ranking/Vectors.cs`:

```csharp
namespace FeedEater.Ranking;

public static class Vectors
{
    public static double Dot(float[] a, float[] b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException($"vector lengths differ: {a.Length} and {b.Length}");
        }

        var sum = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * (double)b[i];
        }

        return sum;
    }

    public static float[] Normalize(float[] v)
    {
        var norm = Math.Sqrt(Dot(v, v));
        return norm == 0 ? v : v.Select(x => (float)(x / norm)).ToArray();
    }

    public static float[]? Centroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return null;
        }

        var sum = new float[vectors[0].Length];
        foreach (var v in vectors)
        {
            for (var i = 0; i < sum.Length; i++)
            {
                sum[i] += v[i];
            }
        }

        return Normalize(sum);
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: html, url, title and vector helpers"
```

---

### Task 4: LiteLLM client and usage

**Files:**
- Create: `src/FeedEater/Llm/LiteLlmClient.cs`, `src/FeedEater/Storage/UsageStore.cs`
- Modify: `tests/FeedEater.Tests/TestVectors.cs` (add `EmbeddingResponse`, `InputCount`)
- Test: `tests/FeedEater.Tests/StubHandler.cs`, `tests/FeedEater.Tests/LiteLlmClientTests.cs`

**Interfaces:**
- Consumes: `Vectors.Normalize`, `Json.Parse`, `FeedEaterOptions.Llm`.
- Produces: `IUsageSink.AddAsync(purpose, model, inputTokens, outputTokens, cost, ct)`; `UsageStore(FeedDb) : IUsageSink` with `SpendSinceAsync(DateTimeOffset, ct) -> decimal`; `LiteLlmClient(HttpClient, IUsageSink, IOptions<FeedEaterOptions>)` with `EmbedAsync(IReadOnlyList<string>, string purpose, ct) -> IReadOnlyList<float[]>` (unit vectors, input order) and `ChatAsync(string model, string system, string user, int maxTokens, string purpose, ct) -> ChatResult(Content, InputTokens, OutputTokens, Cost)`; internal static `LiteLlmClient.RetryDelay`; `BudgetExceededException`; test `StubHandler`.

- [ ] **Step 1: Write the test helpers and failing tests**

`tests/FeedEater.Tests/StubHandler.cs`:

```csharp
using System.Net;
using System.Text;

namespace FeedEater.Tests;

/// <summary>Answers every request with <paramref name="respond"/>(request, body) and records the calls.</summary>
public sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Uri, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        lock (Calls)
        {
            Calls.Add((request.Method, request.RequestUri!.ToString(), body));
        }

        return respond(request, body);
    }

    public HttpClient Client(string baseUrl) => new(this) { BaseAddress = new Uri(baseUrl) };

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
```

Add to `tests/FeedEater.Tests/TestVectors.cs` (add `using System.Text.Json;` at the top):

```csharp
    /// <summary>An OpenAI-style embeddings response with <paramref name="count"/> vectors.</summary>
    public static string EmbeddingResponse(int count, Func<int, float[]>? vector = null)
    {
        var data = Enumerable.Range(0, count).Select(i => new { index = i, embedding = (vector ?? (_ => OneHot(0)))(i) });
        return JsonSerializer.Serialize(new { data, usage = new { prompt_tokens = 10 * count, total_tokens = 10 * count } });
    }

    public static int InputCount(string requestBody)
    {
        using var doc = JsonDocument.Parse(requestBody);
        return doc.RootElement.GetProperty("input").GetArrayLength();
    }
```

`tests/FeedEater.Tests/LiteLlmClientTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Llm;

namespace FeedEater.Tests;

public sealed class LiteLlmClientTests
{
    public LiteLlmClientTests() => LiteLlmClient.RetryDelay = TimeSpan.Zero;

    private sealed class Sink : IUsageSink
    {
        public List<(string Purpose, string Model, int In, int Out, decimal Cost)> Rows { get; } = [];

        public Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct)
        {
            Rows.Add((purpose, model, inputTokens, outputTokens, cost));
            return Task.CompletedTask;
        }
    }

    private static LiteLlmClient Client(StubHandler handler, Sink sink) =>
        new(handler.Client("http://llm/"), sink, Options.Create(new FeedEaterOptions()));

    [Fact]
    public async Task Embed_returns_unit_vectors_in_input_order_and_records_cost()
    {
        var v0 = TestVectors.OneHot(0).Select(x => x * 2).ToArray();
        var v1 = TestVectors.OneHot(1).Select(x => x * 3).ToArray();
        var json = JsonSerializer.Serialize(new
        {
            data = new[] { new { index = 1, embedding = v1 }, new { index = 0, embedding = v0 } },
            usage = new { prompt_tokens = 20 },
        });
        var handler = new StubHandler((_, _) =>
        {
            var r = StubHandler.Json(json);
            r.Headers.Add("x-litellm-response-cost", "0.0001");
            return r;
        });
        var sink = new Sink();

        var vectors = await Client(handler, sink).EmbedAsync(["a", "b"], "embed", default);

        Assert.Equal(1f, vectors[0][0]);
        Assert.Equal(1f, vectors[1][1]);
        Assert.Equal(("embed", "text-embedding-3-small", 20, 0, 0.0001m), sink.Rows.Single());
        Assert.Equal("http://llm/v1/embeddings", handler.Calls.Single().Uri);
    }

    [Fact]
    public async Task Chat_returns_content_and_tokens()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"choices":[{"message":{"content":"{\"relevance\":2}"}}],"usage":{"prompt_tokens":100,"completion_tokens":7}}"""));
        var sink = new Sink();

        var result = await Client(handler, sink).ChatAsync("gpt-4.1-nano", "sys", "user", 200, "triage", default);

        Assert.Equal("{\"relevance\":2}", result.Content);
        Assert.Equal(("triage", "gpt-4.1-nano", 100, 7, 0m), sink.Rows.Single());
        Assert.Contains("\"max_tokens\":200", handler.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_spent_budget_becomes_BudgetExceededException()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(
            """{"error":{"message":"Authentication Error, ExceededTokenBudget: Current spend for token: 10.01; Max Budget for Token: 10.0"}}""",
            HttpStatusCode.Unauthorized));

        await Assert.ThrowsAsync<BudgetExceededException>(() => Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default));
        Assert.Single(handler.Calls);
    }

    [Fact]
    public async Task Server_errors_are_retried_twice()
    {
        var calls = 0;
        var handler = new StubHandler((_, _) => ++calls < 3
            ? StubHandler.Json("{}", HttpStatusCode.BadGateway)
            : StubHandler.Json("""{"choices":[{"message":{"content":"ok"}}]}"""));

        var result = await Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default);

        Assert.Equal("ok", result.Content);
        Assert.Equal(3, handler.Calls.Count);
    }

    [Fact]
    public async Task Gives_up_after_the_second_retry()
    {
        var handler = new StubHandler((_, _) => StubHandler.Json("{}", HttpStatusCode.BadGateway));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Client(handler, new Sink()).ChatAsync("m", "s", "u", 10, "read", default));

        Assert.Equal(HttpStatusCode.BadGateway, error.StatusCode);
        Assert.Equal(3, handler.Calls.Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~LiteLlmClientTests"`
Expected: build FAILS with `The type or namespace name 'Llm' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Llm/LiteLlmClient.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using FeedEater.Ranking;

namespace FeedEater.Llm;

public sealed record ChatResult(string Content, int InputTokens, int OutputTokens, decimal Cost);

public sealed class BudgetExceededException(string message) : Exception(message);

public interface IUsageSink
{
    Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct);
}

/// <summary>OpenAI-compatible calls through LiteLLM. Every call's tokens and cost go to the usage sink.</summary>
public sealed class LiteLlmClient(HttpClient http, IUsageSink usage, IOptions<FeedEaterOptions> options)
{
    private const int Retries = 2;

    internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> inputs, string purpose, CancellationToken ct)
    {
        var model = options.Value.Llm.EmbedModel;
        using var response = await PostAsync("v1/embeddings", new { model, input = inputs }, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        var vectors = body.GetProperty("data").EnumerateArray()
            .OrderBy(d => d.GetProperty("index").GetInt32())
            .Select(d => Vectors.Normalize(d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray()))
            .ToList();
        if (vectors.Count != inputs.Count)
        {
            throw new InvalidOperationException($"asked for {inputs.Count} embeddings, got {vectors.Count}");
        }

        await usage.AddAsync(purpose, model, Tokens(body, "prompt_tokens"), 0, Cost(response), ct);
        return vectors;
    }

    public async Task<ChatResult> ChatAsync(string model, string system, string user, int maxTokens, string purpose, CancellationToken ct)
    {
        using var response = await PostAsync("v1/chat/completions", new
        {
            model,
            max_tokens = maxTokens,
            temperature = 0.2,
            messages = new object[] { new { role = "system", content = system }, new { role = "user", content = user } },
        }, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        var content = body.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        var result = new ChatResult(content, Tokens(body, "prompt_tokens"), Tokens(body, "completion_tokens"), Cost(response));
        await usage.AddAsync(purpose, model, result.InputTokens, result.OutputTokens, result.Cost, ct);
        return result;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, object payload, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                response = await http.PostAsJsonAsync(path, payload, ct);
            }
            catch (HttpRequestException) when (attempt < Retries)
            {
                await Task.Delay(RetryDelay * (attempt + 1), ct);
                continue;
            }

            if ((int)response.StatusCode >= 500 && attempt < Retries)
            {
                response.Dispose();
                await Task.Delay(RetryDelay * (attempt + 1), ct);
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            var status = response.StatusCode;
            var text = await response.Content.ReadAsStringAsync(ct);
            response.Dispose();
            if (text.Contains("ExceededTokenBudget", StringComparison.Ordinal) || text.Contains("ExceededBudget", StringComparison.Ordinal))
            {
                throw new BudgetExceededException("the LiteLLM budget for this key is spent");
            }

            throw new HttpRequestException($"LiteLLM {(int)status}: {text[..Math.Min(300, text.Length)]}", null, status);
        }
    }

    private static int Tokens(JsonElement body, string name) =>
        body.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;

    private static decimal Cost(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-litellm-response-cost", out var values)
        && decimal.TryParse(values.First(), NumberStyles.Float, CultureInfo.InvariantCulture, out var cost) ? cost : 0m;
}
```

`src/FeedEater/Storage/UsageStore.cs`:

```csharp
using Dapper;
using FeedEater.Llm;

namespace FeedEater.Storage;

public sealed class UsageStore(FeedDb db) : IUsageSink
{
    public async Task AddAsync(string purpose, string model, int inputTokens, int outputTokens, decimal cost, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into llm_usage (purpose, model, input_tokens, output_tokens, cost)
            values (@purpose, @model, @inputTokens, @outputTokens, @cost)
            """,
            new { purpose, model, inputTokens, outputTokens, cost }, cancellationToken: ct));
    }

    public async Task<decimal> SpendSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<decimal>(new CommandDefinition(
            "select coalesce(sum(cost), 0) from llm_usage where at >= @since",
            new { since = since.UtcDateTime }, cancellationToken: ct));
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: LiteLLM client with retries, budget error and usage log"
```

---

### Task 5: Miniflux ingest and embedding

**Files:**
- Create: `src/FeedEater/Ingest/MinifluxClient.cs`, `src/FeedEater/Ingest/Ingestor.cs`, `src/FeedEater/Storage/ItemStore.cs`
- Test: `tests/FeedEater.Tests/Fixtures/miniflux-entries.json`, `tests/FeedEater.Tests/IngestorTests.cs`

**Interfaces:**
- Consumes: `PollingLoop`, `LoopHealth`, `LiteLlmClient.EmbedAsync`, `HtmlText`, `UrlCanonicalizer`, `TitleHash`.
- Produces: `MinifluxEntry(Id, FeedId, FeedTitle, Category, SiteUrl, Url, Title, Content, PublishedAt)`; `MinifluxClient(HttpClient)` with `EntriesAfterAsync(long afterId, int limit, ct)` and `FetchContentAsync(long entryId, ct) -> string` (HTML); `Feed(Id, Title, Category, SiteUrl)`; `NewItem(EntryId, FeedId, Url, CanonicalUrl, TitleHash, Title, PublishedAt, Content)`; `PendingEmbed { Id, Title, Content, Url }`; `ItemStore(FeedDb)` with `UpsertFeedAsync(Feed, ct)`, `InsertAsync(NewItem, ct) -> long?`, `MaxEntryIdAsync(ct) -> long`, `UnembeddedAsync(int, ct)`, `SetEmbeddingsAsync(IReadOnlyList<(long Id, float[] Vector)>, ct)`; `Ingestor` (hosted loop, `Ingestor.LoopName = "miniflux"`) with internal `IngestAsync(ct) -> int`, `EmbedPendingAsync(ct) -> int`, static `EmbedText(title, content, url, maxChars)`.

- [ ] **Step 1: Write the fixture and failing tests**

`tests/FeedEater.Tests/Fixtures/miniflux-entries.json` (shape from miniflux.app/docs/api.html; ids chosen to pin both duplicate rules and the empty-title case):

```json
{
  "total": 5,
  "entries": [
    {
      "id": 101, "feed_id": 7, "status": "unread", "title": "Postgres 18.1 released",
      "url": "https://www.postgresql.org/about/news/postgresql-181-released/?utm_source=rss",
      "published_at": "2026-10-04T09:00:00Z",
      "content": "<p>Fixes for <b>async I/O</b>.</p>",
      "feed": { "id": 7, "title": "PostgreSQL News", "site_url": "https://www.postgresql.org/", "category": { "id": 2, "title": "Databases" } }
    },
    {
      "id": 102, "feed_id": 8, "status": "unread", "title": "Postgres 18.1 Released!",
      "url": "https://news.ycombinator.com/item?id=1",
      "published_at": "2026-10-04T10:00:00Z",
      "content": "<a href=\"https://news.ycombinator.com/item?id=1\">Comments</a>",
      "feed": { "id": 8, "title": "Hacker News", "site_url": "https://news.ycombinator.com/", "category": { "id": 3, "title": "Software Engineering" } }
    },
    {
      "id": 103, "feed_id": 7, "status": "read", "title": "",
      "url": "https://www.postgresql.org/about/news/pgconf/",
      "published_at": "2026-10-04T11:00:00Z",
      "content": "",
      "feed": { "id": 7, "title": "PostgreSQL News", "site_url": "https://www.postgresql.org/", "category": { "id": 2, "title": "Databases" } }
    },
    {
      "id": 104, "feed_id": 8, "status": "unread", "title": "Release notes",
      "url": "https://postgresql.org/about/news/postgresql-181-released",
      "published_at": "2026-10-04T12:00:00Z",
      "content": "<p>Same article, other link.</p>",
      "feed": { "id": 8, "title": "Hacker News", "site_url": "https://news.ycombinator.com/", "category": { "id": 3, "title": "Software Engineering" } }
    },
    {
      "id": 105, "feed_id": 9, "status": "unread", "title": "News",
      "url": "https://ain.ua/2026/10/04/news/",
      "published_at": "2026-10-04T13:00:00Z",
      "content": "<p>Новини.</p>",
      "feed": { "id": 9, "title": "AIN.UA", "site_url": "https://ain.ua/", "category": null }
    }
  ]
}
```

`tests/FeedEater.Tests/IngestorTests.cs`:

```csharp
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IngestorTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Ingestor Build(StubHandler miniflux, StubHandler llm)
    {
        var options = Options.Create(new FeedEaterOptions());
        return new Ingestor(
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new ItemStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            options, new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<Ingestor>.Instance);
    }

    private static StubHandler Miniflux() => new((request, _) => StubHandler.Json(
        request.RequestUri!.Query.Contains("after_entry_id=0", StringComparison.Ordinal)
            ? File.ReadAllText("Fixtures/miniflux-entries.json")
            : """{"total":0,"entries":[]}"""));

    private static StubHandler Llm() => new((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));

    private sealed record Row
    {
        public long MinifluxEntryId { get; init; }
        public string CanonicalUrl { get; init; } = "";
        public string Content { get; init; } = "";
        public long? DuplicateOf { get; init; }
        public bool Embedded { get; init; }
    }

    [Fact]
    public async Task Stores_entries_marks_duplicates_and_embeds_everything()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var ingestor = Build(Miniflux(), Llm());

        Assert.Equal(5, await ingestor.IngestAsync(default));
        Assert.Equal(5, await ingestor.EmbedPendingAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var rows = (await c.QueryAsync<Row>(
            """
            select i.miniflux_entry_id, i.canonical_url, i.content, d.miniflux_entry_id as duplicate_of, i.embedding is not null as embedded
            from items i left join items d on d.id = i.duplicate_of order by i.miniflux_entry_id
            """)).ToDictionary(r => r.MinifluxEntryId);

        Assert.Equal("https://postgresql.org/about/news/postgresql-181-released", rows[101].CanonicalUrl);
        Assert.Equal("Fixes for async I/O .", rows[101].Content);
        Assert.Null(rows[101].DuplicateOf);
        Assert.Equal(101, rows[102].DuplicateOf);   // same title, other feed
        Assert.Null(rows[103].DuplicateOf);         // empty title never matches
        Assert.Equal(101, rows[104].DuplicateOf);   // same canonical URL
        Assert.Null(rows[105].DuplicateOf);         // short title never matches the empty one
        Assert.All(rows.Values, r => Assert.True(r.Embedded));
        Assert.Equal(3, await c.ExecuteScalarAsync<int>("select count(*)::int from feeds"));
    }

    [Fact]
    public async Task A_second_poll_starts_after_the_last_entry_and_adds_nothing()
    {
        var miniflux = Miniflux();
        var ingestor = Build(miniflux, Llm());

        await ingestor.IngestAsync(default);
        Assert.Equal(0, await ingestor.IngestAsync(default));

        Assert.Contains(miniflux.Calls, call => call.Uri.Contains("after_entry_id=105", StringComparison.Ordinal));
    }

    [Fact]
    public void Embed_text_falls_back_to_the_url_and_is_capped()
    {
        Assert.Equal("https://x.example/", Ingestor.EmbedText("", "", "https://x.example/", 8000));
        Assert.Equal(8 + 1 + 5, Ingestor.EmbedText("Headline", new string('a', 50), "u", 5).Length);
    }
}
```

The expected content `"Fixes for async I/O ."` follows from `HtmlText.ToPlain`: `</b>` becomes a space before the period.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~IngestorTests"`
Expected: build FAILS with `The type or namespace name 'Ingest' does not exist`.

- [ ] **Step 3: Write the Miniflux client**

`src/FeedEater/Ingest/MinifluxClient.cs`:

```csharp
using System.Text.Json;

namespace FeedEater.Ingest;

public sealed record MinifluxEntry(
    long Id, long FeedId, string FeedTitle, string? Category, string? SiteUrl,
    string Url, string Title, string Content, DateTimeOffset PublishedAt);

/// <summary>Read-only: entries by id and the full-text fetch. Never changes read/unread state.</summary>
public sealed class MinifluxClient(HttpClient http)
{
    public async Task<IReadOnlyList<MinifluxEntry>> EntriesAfterAsync(long afterId, int limit, CancellationToken ct)
    {
        var body = Json.Parse(await http.GetStringAsync($"v1/entries?after_entry_id={afterId}&order=id&direction=asc&limit={limit}", ct));
        return body.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            ? entries.EnumerateArray().Select(Parse).ToList()
            : [];
    }

    /// <summary>The original article's HTML, scraped by Miniflux; not written back into Miniflux.</summary>
    public async Task<string> FetchContentAsync(long entryId, CancellationToken ct)
    {
        var body = Json.Parse(await http.GetStringAsync($"v1/entries/{entryId}/fetch-content?update_content=false", ct));
        return body.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "";
    }

    private static MinifluxEntry Parse(JsonElement e)
    {
        var feed = e.GetProperty("feed");
        return new MinifluxEntry(
            e.GetProperty("id").GetInt64(),
            e.GetProperty("feed_id").GetInt64(),
            Str(feed, "title") ?? "",
            feed.TryGetProperty("category", out var category) ? Str(category, "title") : null,
            Str(feed, "site_url"),
            Str(e, "url") ?? "",
            Str(e, "title") ?? "",
            Str(e, "content") ?? "",
            e.GetProperty("published_at").GetDateTimeOffset());
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 4: Write the item store (ingest half; Tasks 11, 12 and 14 add queries)**

`src/FeedEater/Storage/ItemStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed record Feed(long Id, string Title, string? Category, string? SiteUrl);

public sealed record NewItem(
    long EntryId, long FeedId, string Url, string CanonicalUrl, string TitleHash, string Title, DateTime PublishedAt, string Content);

public sealed record PendingEmbed
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Content { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class ItemStore(FeedDb db)
{
    public async Task UpsertFeedAsync(Feed feed, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into feeds (id, title, category, site_url) values (@Id, @Title, @Category, @SiteUrl)
            on conflict (id) do update set title = excluded.title, category = excluded.category, site_url = excluded.site_url
            """, feed, cancellationToken: ct));
    }

    /// <summary>
    /// Inserts one Miniflux entry; null when it is already stored. A row whose canonical URL, or whose title hash within
    /// 7 days, matches an earlier row points at it through duplicate_of. An empty hash never matches.
    /// </summary>
    public async Task<long?> InsertAsync(NewItem item, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long?>(new CommandDefinition(
            """
            insert into items (miniflux_entry_id, feed_id, url, canonical_url, title_hash, title, published_at, content, duplicate_of)
            values (@EntryId, @FeedId, @Url, @CanonicalUrl, @TitleHash, @Title, @PublishedAt, @Content,
                    coalesce(
                        (select id from items where canonical_url = @CanonicalUrl order by id limit 1),
                        (select id from items
                         where @TitleHash <> '' and title_hash = @TitleHash
                           and published_at between @PublishedAt - interval '7 days' and @PublishedAt + interval '7 days'
                         order by id limit 1)))
            on conflict (miniflux_entry_id) do nothing
            returning id
            """, item, cancellationToken: ct));
    }

    public async Task<long> MaxEntryIdAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<long>(new CommandDefinition(
            "select coalesce(max(miniflux_entry_id), 0) from items", cancellationToken: ct));
    }

    public async Task<IReadOnlyList<PendingEmbed>> UnembeddedAsync(int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<PendingEmbed>(new CommandDefinition(
            "select id, title, content, url from items where embedding is null order by id limit @limit",
            new { limit }, cancellationToken: ct))).ToList();
    }

    public async Task SetEmbeddingsAsync(IReadOnlyList<(long Id, float[] Vector)> rows, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        foreach (var (id, vector) in rows)
        {
            await c.ExecuteAsync(new CommandDefinition(
                "update items set embedding = @vector::real[]::vector where id = @id", new { id, vector }, cancellationToken: ct));
        }
    }
}
```

- [ ] **Step 5: Write the ingestor**

`src/FeedEater/Ingest/Ingestor.cs`:

```csharp
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Text;

namespace FeedEater.Ingest;

/// <summary>Copies new Miniflux entries into the archive, then embeds every row that has no vector yet.</summary>
public sealed class Ingestor(
    MinifluxClient miniflux, ItemStore items, LiteLlmClient llm, IOptions<FeedEaterOptions> options,
    LoopHealth health, TimeProvider time, ILogger<Ingestor> logger)
    : PollingLoop(health, time, logger)
{
    public const string LoopName = "miniflux";

    protected override string Name => LoopName;
    protected override TimeSpan Interval => options.Value.Miniflux.PollInterval;

    protected override async Task PollAsync(CancellationToken ct)
    {
        var added = await IngestAsync(ct);
        var embedded = await EmbedPendingAsync(ct);
        Logger.LogInformation("Ingested {Added} entries, embedded {Embedded}", added, embedded);
    }

    internal async Task<int> IngestAsync(CancellationToken ct)
    {
        var pageSize = options.Value.Miniflux.PageSize;
        var added = 0;
        while (true)
        {
            var page = await miniflux.EntriesAfterAsync(await items.MaxEntryIdAsync(ct), pageSize, ct);
            foreach (var e in page)
            {
                await items.UpsertFeedAsync(new Feed(e.FeedId, e.FeedTitle, e.Category, e.SiteUrl), ct);
                if (await items.InsertAsync(ToNewItem(e), ct) is not null)
                {
                    added++;
                }
            }

            if (page.Count < pageSize)
            {
                return added;
            }
        }
    }

    internal async Task<int> EmbedPendingAsync(CancellationToken ct)
    {
        var o = options.Value;
        var done = 0;
        while (true)
        {
            var batch = await items.UnembeddedAsync(o.Llm.EmbedBatch, ct);
            if (batch.Count == 0)
            {
                return done;
            }

            var vectors = await llm.EmbedAsync(batch.Select(b => EmbedText(b.Title, b.Content, b.Url, o.Caps.EmbedChars)).ToList(), "embed", ct);
            await items.SetEmbeddingsAsync(batch.Select((b, i) => (b.Id, vectors[i])).ToList(), ct);
            done += batch.Count;
        }
    }

    /// <summary>Title and the start of the text; the URL when both are empty, because the API rejects empty input.</summary>
    internal static string EmbedText(string title, string content, string url, int maxChars)
    {
        var text = $"{title}\n{(content.Length <= maxChars ? content : content[..maxChars])}".Trim();
        return text.Length > 0 ? text : url;
    }

    private static NewItem ToNewItem(MinifluxEntry e) => new(
        e.Id, e.FeedId, e.Url, UrlCanonicalizer.Canonical(e.Url), TitleHash.Of(e.Title), e.Title,
        e.PublishedAt.UtcDateTime, HtmlText.ToPlain(e.Content));
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: ingest Miniflux entries with duplicate marking and embeddings"
```

---

### Task 6: Profiles and the Plane client

**Files:**
- Create: `src/FeedEater/Profiles/ProfileFile.cs`, `src/FeedEater/Profiles/ProfileBuilder.cs`, `src/FeedEater/Plane/PlaneClient.cs`, `src/FeedEater/Storage/ProfileStore.cs`
- Test: `tests/FeedEater.Tests/Fixtures/profile.json`, `tests/FeedEater.Tests/ProfileTests.cs`, `tests/FeedEater.Tests/PlaneClientTests.cs`

**Interfaces:**
- Consumes: `ScheduledJob`, `Schedule.LatestDaily`, `LiteLlmClient.EmbedAsync`.
- Produces: `ProfileEntry(Key, Description, Plane?)`, `ProfileFile(About, Projects, Topics)` with static `Load(path)`, `Parse(json)`, `OneLiner(description)`; `Profile { Key, Kind, PlaneIdentifier, Description, Embedding }`; `ProfileStore(FeedDb)` with `ReplaceAllAsync(IReadOnlyList<Profile>, DateTimeOffset, ct)`, `AllAsync(ct)`; `PlaneClient(HttpClient, IOptions<FeedEaterOptions>)` with `ProjectIdAsync(identifier, ct)`, `OpenItemTitlesAsync(identifier, ct)`, `CreateIntakeAsync(identifier, name, descriptionHtml, ct) -> string`; `ProfileBuilder` (scheduled job, daily 03:00).

- [ ] **Step 1: Write the fixture and failing tests**

`tests/FeedEater.Tests/Fixtures/profile.json`:

```json
{
  "about": "Backend .NET developer in Kyiv who builds self-hosted AI tooling on a Hetzner homelab.",
  "projects": [
    { "key": "chargehand", "plane": "CHARGEHAND", "description": "Agent orchestrator. Runs read-only coding agents and returns cited claims." },
    { "key": "homelab", "plane": "LAB", "description": "Hetzner VPS with about 120 containers, deployed by GitOps." },
    { "key": "trader", "description": "Algorithmic crypto trading experiments." }
  ],
  "topics": [
    { "key": "postgres", "description": "PostgreSQL internals, releases, extensions and performance." },
    { "key": "llm-engineering", "description": "Building with LLMs: evals, agents, retrieval, cost." }
  ]
}
```

`tests/FeedEater.Tests/ProfileTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProfileTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Parses_the_profile_file()
    {
        var file = ProfileFile.Load("Fixtures/profile.json");

        Assert.Equal(3, file.Projects.Count);
        Assert.Equal(2, file.Topics.Count);
        Assert.Equal("LAB", file.Projects[1].Plane);
        Assert.Null(file.Projects[2].Plane);
        Assert.Equal("Agent orchestrator.", ProfileFile.OneLiner(file.Projects[0].Description));
    }

    [Fact]
    public void Rejects_duplicate_keys_missing_sections_and_empty_descriptions()
    {
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse(
            """{"about":"a","projects":[{"key":"x","description":"d"}],"topics":[{"key":"x","description":"d"}]}"""));
        Assert.Throws<JsonException>(() => ProfileFile.Parse("""{"about":"a","projects":[]}"""));
        Assert.Throws<InvalidDataException>(() => ProfileFile.Parse(
            """{"about":"a","projects":[{"key":"x","description":" "}],"topics":[]}"""));
    }

    [Fact]
    public async Task Builder_embeds_descriptions_with_open_plane_work_and_survives_a_plane_error()
    {
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        var plane = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/homelab/projects/" => StubHandler.Json(
                """{"results":[{"id":"p-ch","identifier":"CHARGEHAND"},{"id":"p-lab","identifier":"LAB"}]}"""),
            "/api/v1/workspaces/homelab/projects/p-lab/states/" => StubHandler.Json(
                """{"results":[{"id":"s-open","group":"started"},{"id":"s-done","group":"completed"}]}"""),
            "/api/v1/workspaces/homelab/projects/p-lab/work-items/" => StubHandler.Json(
                """{"results":[{"name":"Rotate backups","state":"s-open"},{"name":"Old thing","state":"s-done"}]}"""),
            _ => StubHandler.Json("{}", System.Net.HttpStatusCode.InternalServerError),
        });
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json" });
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 5, 1, 0, 0, TimeSpan.Zero)); // 04:00 Kyiv
        var profiles = new ProfileStore(pg.Db);
        var builder = new ProfileBuilder(
            new PlaneClient(plane.Client("http://plane/"), options), profiles,
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(time), time, NullLogger<ProfileBuilder>.Instance);

        await builder.TickAsync(default);

        var stored = await profiles.AllAsync(default);
        Assert.Equal(5, stored.Count);
        Assert.Equal(3, stored.Count(p => p.Kind == "project"));
        Assert.Equal(TestVectors.Dims, stored[0].Embedding.Length);
        var input = llm.Calls.Single().Body;
        Assert.Contains("Open work: Rotate backups", input, StringComparison.Ordinal);
        Assert.DoesNotContain("Old thing", input, StringComparison.Ordinal);
        Assert.Contains("Agent orchestrator. Runs read-only coding agents and returns cited claims.\"", input, StringComparison.Ordinal);
    }
}
```

`tests/FeedEater.Tests/PlaneClientTests.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Plane;

namespace FeedEater.Tests;

public sealed class PlaneClientTests
{
    private static PlaneClient Client(StubHandler handler) => new(handler.Client("http://plane/"), Options.Create(new FeedEaterOptions()));

    private static HttpResponseMessage Projects() => StubHandler.Json("""{"results":[{"id":"p1","identifier":"FEED"}]}""");

    [Fact]
    public async Task Falls_back_to_the_issues_path_when_work_items_is_missing()
    {
        var handler = new StubHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/v1/workspaces/homelab/projects/" => Projects(),
            "/api/v1/workspaces/homelab/projects/p1/states/" => StubHandler.Json("""{"results":[{"id":"s1","group":"backlog"}]}"""),
            "/api/v1/workspaces/homelab/projects/p1/work-items/" => StubHandler.Json("{}", HttpStatusCode.NotFound),
            "/api/v1/workspaces/homelab/projects/p1/issues/" => StubHandler.Json("""{"results":[{"name":"A","state":"s1"}]}"""),
            _ => StubHandler.Json("{}", HttpStatusCode.InternalServerError),
        });

        Assert.Equal(["A"], await Client(handler).OpenItemTitlesAsync("FEED", default));
    }

    [Fact]
    public async Task Creates_an_intake_item_and_returns_its_id()
    {
        var handler = new StubHandler((request, _) => request.Method == HttpMethod.Post
            ? StubHandler.Json("""{"id":"intake-1","issue":{"id":"issue-9"}}""", HttpStatusCode.Created)
            : Projects());

        var id = await Client(handler).CreateIntakeAsync("FEED", "Try X", "<p>Try X</p>", default);

        Assert.Equal("issue-9", id);
        var post = handler.Calls.Single(c => c.Method == HttpMethod.Post);
        Assert.Equal("http://plane/api/v1/workspaces/homelab/projects/p1/intake-issues/", post.Uri);
        Assert.Contains("\"description_html\":\"\\u003Cp\\u003ETry X\\u003C/p\\u003E\"", post.Body, StringComparison.Ordinal);
        Assert.Contains("\"priority\":\"none\"", post.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_project_identifier_is_an_error()
    {
        var handler = new StubHandler((_, _) => Projects());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Client(handler).ProjectIdAsync("NOPE", default));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~ProfileTests|FullyQualifiedName~PlaneClientTests"`
Expected: build FAILS with `The type or namespace name 'Profiles' does not exist`.

- [ ] **Step 3: Write the profile file and store**

`src/FeedEater/Profiles/ProfileFile.cs`:

```csharp
using System.Text.Json;

namespace FeedEater.Profiles;

public sealed record ProfileEntry(string Key, string Description, string? Plane = null);

/// <summary>Hand-written description of Yehor, his projects (with Plane identifiers) and his topics.</summary>
public sealed record ProfileFile(string About, IReadOnlyList<ProfileEntry> Projects, IReadOnlyList<ProfileEntry> Topics)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
    };

    public static ProfileFile Load(string path) => Parse(File.ReadAllText(path));

    public static ProfileFile Parse(string json)
    {
        var file = JsonSerializer.Deserialize<ProfileFile>(json, Options) ?? throw new InvalidDataException("the profile file is empty");
        var entries = file.Projects.Concat(file.Topics).ToList();
        if (entries.Any(e => string.IsNullOrWhiteSpace(e.Key) || string.IsNullOrWhiteSpace(e.Description)))
        {
            throw new InvalidDataException("every profile entry needs a key and a description");
        }

        var duplicate = entries.GroupBy(e => e.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return duplicate is null ? file : throw new InvalidDataException($"profile key '{duplicate.Key}' appears twice");
    }

    /// <summary>The first sentence, at most 160 characters: what prompts list for each key.</summary>
    public static string OneLiner(string description)
    {
        var end = description.IndexOf(". ", StringComparison.Ordinal);
        var sentence = end < 0 ? description : description[..(end + 1)];
        return sentence.Length <= 160 ? sentence : sentence[..157] + "...";
    }
}
```

`src/FeedEater/Storage/ProfileStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed record Profile
{
    public string Key { get; init; } = "";
    public string Kind { get; init; } = "";
    public string? PlaneIdentifier { get; init; }
    public string Description { get; init; } = "";
    public float[] Embedding { get; init; } = [];
}

public sealed class ProfileStore(FeedDb db)
{
    /// <summary>Replaces the whole set, so keys removed from the profile file stop counting.</summary>
    public async Task ReplaceAllAsync(IReadOnlyList<Profile> profiles, DateTimeOffset builtAt, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await using var tx = await c.BeginTransactionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("delete from profiles", transaction: tx, cancellationToken: ct));
        foreach (var p in profiles)
        {
            await c.ExecuteAsync(new CommandDefinition(
                """
                insert into profiles (key, kind, plane_identifier, description, embedding, built_at)
                values (@Key, @Kind, @PlaneIdentifier, @Description, @Embedding::real[]::vector, @builtAt)
                """,
                new { p.Key, p.Kind, p.PlaneIdentifier, p.Description, p.Embedding, builtAt = builtAt.UtcDateTime },
                tx, cancellationToken: ct));
        }

        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<Profile>> AllAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Profile>(new CommandDefinition(
            "select key, kind, plane_identifier, description, embedding::real[] as embedding from profiles order by kind, key",
            cancellationToken: ct))).ToList();
    }
}
```

- [ ] **Step 4: Write the Plane client**

`src/FeedEater/Plane/PlaneClient.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Plane;

public sealed class PlaneClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    private static readonly string[] OpenGroups = ["backlog", "unstarted", "started"];
    private Dictionary<string, string>? _projectIds;

    private string Workspace => $"api/v1/workspaces/{options.Value.Plane.Workspace}";

    public async Task<string> ProjectIdAsync(string identifier, CancellationToken ct)
    {
        _projectIds ??= (await GetAsync($"{Workspace}/projects/", ct)).GetProperty("results").EnumerateArray()
            .ToDictionary(p => p.GetProperty("identifier").GetString()!, p => p.GetProperty("id").GetString()!, StringComparer.Ordinal);
        return _projectIds.TryGetValue(identifier, out var id) ? id : throw new InvalidOperationException($"Plane project {identifier} does not exist");
    }

    public async Task<IReadOnlyList<string>> OpenItemTitlesAsync(string identifier, CancellationToken ct)
    {
        var project = await ProjectIdAsync(identifier, ct);
        var open = (await GetAsync($"{Workspace}/projects/{project}/states/", ct)).GetProperty("results").EnumerateArray()
            .Where(s => OpenGroups.Contains(s.GetProperty("group").GetString()))
            .Select(s => s.GetProperty("id").GetString())
            .ToHashSet(StringComparer.Ordinal);

        JsonElement page;
        try
        {
            page = await GetAsync($"{Workspace}/projects/{project}/work-items/?per_page=100", ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Plane renamed issues to work-items; v1.4 may only have the old path (as plane-sync.py notes).
            page = await GetAsync($"{Workspace}/projects/{project}/issues/?per_page=100", ct);
        }

        return page.GetProperty("results").EnumerateArray()
            .Where(i => open.Contains(i.GetProperty("state").GetString()))
            .Select(i => i.GetProperty("name").GetString() ?? "")
            .Where(name => name.Length > 0)
            .ToList();
    }

    public async Task<string> CreateIntakeAsync(string identifier, string name, string descriptionHtml, CancellationToken ct)
    {
        var project = await ProjectIdAsync(identifier, ct);
        using var response = await http.PostAsJsonAsync(
            $"{Workspace}/projects/{project}/intake-issues/",
            new { issue = new { name, description_html = descriptionHtml, priority = "none" } }, ct);
        var issue = (await ReadAsync(response, ct)).GetProperty("issue");
        return issue.ValueKind == JsonValueKind.Object ? issue.GetProperty("id").GetString()! : issue.GetString()!;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await ReadAsync(response, ct);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Plane {(int)response.StatusCode}: {text[..Math.Min(300, text.Length)]}", null, response.StatusCode);
        }

        return Json.Parse(text);
    }
}
```

- [ ] **Step 5: Write the profile builder**

`src/FeedEater/Profiles/ProfileBuilder.cs`:

```csharp
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.Profiles;

/// <summary>Daily at 03:00 (and at start when today's build is missing): one vector per project and topic.</summary>
public sealed class ProfileBuilder(
    PlaneClient plane, ProfileStore profiles, LiteLlmClient llm,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<ProfileBuilder> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxTitles = 30;

    protected override string Name => "profiles";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(3, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var file = ProfileFile.Load(Settings.ProfilePath);
        var entries = new List<(ProfileEntry Entry, string Kind, string Text)>();
        foreach (var p in file.Projects)
        {
            entries.Add((p, "project", await ProjectTextAsync(p, ct)));
        }

        entries.AddRange(file.Topics.Select(t => (t, "topic", t.Description)));

        var vectors = await llm.EmbedAsync(entries.Select(e => e.Text).ToList(), "profile", ct);
        await profiles.ReplaceAllAsync(entries.Select((e, i) => new Profile
        {
            Key = e.Entry.Key,
            Kind = e.Kind,
            PlaneIdentifier = e.Entry.Plane,
            Description = e.Entry.Description,
            Embedding = vectors[i].ToArray(),
        }).ToList(), Time.GetUtcNow(), ct);
        Logger.LogInformation("Profiles rebuilt: {Count}", entries.Count);
    }

    private async Task<string> ProjectTextAsync(ProfileEntry project, CancellationToken ct)
    {
        if (project.Plane is null)
        {
            return project.Description;
        }

        try
        {
            var titles = await plane.OpenItemTitlesAsync(project.Plane, ct);
            return titles.Count == 0 ? project.Description : $"{project.Description}\nOpen work: {string.Join("; ", titles.Take(MaxTitles))}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            Logger.LogWarning(ex, "Plane work items for {Project} unavailable; using the description only", project.Plane);
            return project.Description;
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: profiles from the profile file and open Plane work"
```

---
### Task 7: Ranking and the feedback store

**Files:**
- Create: `src/FeedEater/Ranking/Taste.cs`, `src/FeedEater/Ranking/Scorer.cs`, `src/FeedEater/Storage/FeedbackStore.cs`
- Test: `tests/FeedEater.Tests/Seed.cs`, `tests/FeedEater.Tests/ScorerTests.cs`, `tests/FeedEater.Tests/FeedbackStoreTests.cs`

**Interfaces:**
- Consumes: `Vectors`, `Profile`, `WeightsOptions`.
- Produces: `Taste(float[]? Positive, float[]? Negative, IReadOnlyDictionary<long, double> FeedUpRates)` with static `Build(positives, negatives, feedVotes, WeightsOptions)` and `Empty`; `Scored(long ItemId, double Score, string? ProfileKey)`; `Scorer.Score(long itemId, long? feedId, float[] x, IReadOnlyList<Profile>, Taste, WeightsOptions) -> Scored`; `FeedVotes { FeedId, Up, Down }`, `VoteCounts { Up, Down }`, `Idea { ItemId, PlaneProject, PlaneIssueId, Title, At }`; `FeedbackStore(FeedDb)` with `SetVoteAsync(long, short, ct)`, `PositiveVectorsAsync(int, ct)`, `NegativeVectorsAsync(int, ct)`, `FeedVotesAsync(ct)`, `VotesSinceAsync(DateTimeOffset, ct) -> VoteCounts`, `GetIdeaAsync(long, ct) -> Idea?`, `AddIdeaAsync(Idea, ct)`, `IdeasAsync(string? planeProject, int limit, ct)`; test `Seed.ItemAsync(...)`.

- [ ] **Step 1: Write the seed helper and failing tests**

`tests/FeedEater.Tests/Seed.cs`:

```csharp
using Dapper;

namespace FeedEater.Tests;

public static class Seed
{
    public static async Task<long> ItemAsync(
        PostgresFixture pg, long feedId, string title, float[] embedding,
        DateTime? publishedAt = null, DateTime? ingestedAt = null, string content = "", long? entryId = null)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync("insert into feeds (id, title) values (@feedId, @name) on conflict do nothing",
            new { feedId, name = $"Feed {feedId}" });
        return await c.ExecuteScalarAsync<long>(
            """
            insert into items (miniflux_entry_id, feed_id, url, canonical_url, title_hash, title, published_at, ingested_at, content, embedding)
            values (@entryId, @feedId, @url, @url, '', @title, @publishedAt, @ingestedAt, @content, @embedding::real[]::vector)
            returning id
            """,
            new
            {
                entryId, feedId, url = $"https://example.com/{Guid.NewGuid():N}", title,
                publishedAt = publishedAt ?? DateTime.UtcNow, ingestedAt = ingestedAt ?? DateTime.UtcNow, content, embedding,
            });
    }

    public static async Task SignalAsync(PostgresFixture pg, string externalId, float[] embedding)
    {
        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        await c.ExecuteAsync(
            """
            insert into signals (source, external_id, title, embedding, at)
            values ('karakeep', @externalId, 'saved', @embedding::real[]::vector, now())
            """, new { externalId, embedding });
    }
}
```

`tests/FeedEater.Tests/ScorerTests.cs`:

```csharp
using FeedEater.Ranking;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class ScorerTests
{
    private static readonly WeightsOptions W = new();

    private static Profile P(string key, int dim) => new() { Key = key, Kind = "project", Description = key, Embedding = TestVectors.OneHot(dim) };

    [Fact]
    public void Picks_the_closest_profile()
    {
        var s = Scorer.Score(1, null, TestVectors.Blend(1, 0, 0.2f), [P("a", 0), P("b", 1)], Taste.Empty, W);

        Assert.Equal("b", s.ProfileKey);
        Assert.True(s.Score > 0.9);
    }

    [Fact]
    public void No_profiles_means_no_key_and_zero_fit()
    {
        var s = Scorer.Score(1, null, TestVectors.OneHot(0), [], Taste.Empty, W);

        Assert.Null(s.ProfileKey);
        Assert.Equal(0, s.Score);
    }

    [Fact]
    public void Taste_lifts_items_like_past_positives_and_lowers_items_like_negatives()
    {
        var taste = new Taste(TestVectors.OneHot(5), TestVectors.OneHot(6), new Dictionary<long, double>());
        var profiles = new[] { P("a", 0) };

        var liked = Scorer.Score(1, null, TestVectors.Blend(0, 5, 0.5f), profiles, taste, W);
        var neutral = Scorer.Score(2, null, TestVectors.Blend(0, 7, 0.5f), profiles, taste, W);
        var disliked = Scorer.Score(3, null, TestVectors.Blend(0, 6, 0.5f), profiles, taste, W);

        Assert.True(liked.Score > neutral.Score);
        Assert.True(neutral.Score > disliked.Score);
    }

    [Fact]
    public void Feed_prior_adds_weight_times_rate_minus_half()
    {
        var taste = new Taste(null, null, new Dictionary<long, double> { [7] = 0.9 });
        var x = TestVectors.OneHot(0);

        var withPrior = Scorer.Score(1, 7, x, [P("a", 0)], taste, W);
        var without = Scorer.Score(2, 8, x, [P("a", 0)], taste, W);

        Assert.Equal(0.2 * 0.4, withPrior.Score - without.Score, 6);
    }

    [Fact]
    public void Taste_needs_enough_positives_and_feed_votes()
    {
        var nine = Enumerable.Range(0, 9).Select(TestVectors.OneHot).ToList();
        var ten = Enumerable.Range(0, 10).Select(TestVectors.OneHot).ToList();
        var feeds = new[] { new FeedVotes { FeedId = 1, Up = 3, Down = 1 }, new FeedVotes { FeedId = 2, Up = 4, Down = 1 } };

        var few = Taste.Build(nine, [TestVectors.OneHot(20)], feeds, W);
        var enough = Taste.Build(ten, [TestVectors.OneHot(20)], feeds, W);

        Assert.Null(few.Positive);
        Assert.Null(few.Negative);
        Assert.NotNull(enough.Positive);
        Assert.NotNull(enough.Negative);
        Assert.False(enough.FeedUpRates.ContainsKey(1));
        Assert.Equal(0.8, enough.FeedUpRates[2], 6);
    }
}
```

`tests/FeedEater.Tests/FeedbackStoreTests.cs`:

```csharp
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedbackStoreTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_last_vote_wins_and_counts_by_time()
    {
        var store = new FeedbackStore(pg.Db);
        var id = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0));

        await store.SetVoteAsync(id, 1, default);
        await store.SetVoteAsync(id, -1, default);

        var counts = await store.VotesSinceAsync(DateTimeOffset.UtcNow.AddMinutes(-1), default);
        Assert.Equal(0, counts.Up);
        Assert.Equal(1, counts.Down);
    }

    [Fact]
    public async Task Positives_come_from_up_votes_ideas_and_signals_and_negatives_from_down_votes()
    {
        var store = new FeedbackStore(pg.Db);
        var up = await Seed.ItemAsync(pg, 1, "up", TestVectors.OneHot(1));
        var down = await Seed.ItemAsync(pg, 1, "down", TestVectors.OneHot(2));
        var filed = await Seed.ItemAsync(pg, 2, "filed", TestVectors.OneHot(3));
        await Seed.SignalAsync(pg, "k1", TestVectors.OneHot(4));
        await store.SetVoteAsync(up, 1, default);
        await store.SetVoteAsync(down, -1, default);
        await store.AddIdeaAsync(new Idea { ItemId = filed, PlaneProject = "FEED", PlaneIssueId = "i1", Title = "t", At = DateTime.UtcNow }, default);

        var positives = await store.PositiveVectorsAsync(500, default);
        var negatives = await store.NegativeVectorsAsync(500, default);
        var feeds = await store.FeedVotesAsync(default);

        Assert.Equal(new[] { 1, 3, 4 }, positives.Select(v => Array.IndexOf(v, 1f)).Order());
        Assert.Equal(2, Array.IndexOf(negatives.Single(), 1f));
        var feed1 = feeds.Single(f => f.FeedId == 1);
        Assert.Equal((1, 1), (feed1.Up, feed1.Down));
    }

    [Fact]
    public async Task An_idea_is_stored_once_and_listed_by_project()
    {
        var store = new FeedbackStore(pg.Db);
        var a = await Seed.ItemAsync(pg, 1, "a", TestVectors.OneHot(0));
        var b = await Seed.ItemAsync(pg, 1, "b", TestVectors.OneHot(1));

        await store.AddIdeaAsync(new Idea { ItemId = a, PlaneProject = "SKAR", PlaneIssueId = "first", Title = "x", At = DateTime.UtcNow }, default);
        await store.AddIdeaAsync(new Idea { ItemId = a, PlaneProject = "SKAR", PlaneIssueId = "second", Title = "x", At = DateTime.UtcNow }, default);
        await store.AddIdeaAsync(new Idea { ItemId = b, PlaneProject = "FEED", PlaneIssueId = "third", Title = "y", At = DateTime.UtcNow }, default);

        Assert.Equal("first", (await store.GetIdeaAsync(a, default))!.PlaneIssueId);
        Assert.Equal(["third"], (await store.IdeasAsync("FEED", 10, default)).Select(i => i.PlaneIssueId));
        Assert.Equal(2, (await store.IdeasAsync(null, 10, default)).Count);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~ScorerTests|FullyQualifiedName~FeedbackStoreTests"`
Expected: build FAILS with `The type or namespace name 'Taste' could not be found`.

- [ ] **Step 3: Write the ranking code**

`src/FeedEater/Ranking/Taste.cs`:

```csharp
using FeedEater.Storage;

namespace FeedEater.Ranking;

/// <summary>What Yehor's reactions say so far: centroids of liked and disliked items, and each feed's 👍 rate.</summary>
public sealed record Taste(float[]? Positive, float[]? Negative, IReadOnlyDictionary<long, double> FeedUpRates)
{
    public static Taste Empty { get; } = new(null, null, new Dictionary<long, double>());

    public static Taste Build(
        IReadOnlyList<float[]> positives, IReadOnlyList<float[]> negatives, IReadOnlyList<FeedVotes> feeds, WeightsOptions w)
    {
        var enough = positives.Count >= w.MinPositives;
        return new Taste(
            enough ? Vectors.Centroid(positives) : null,
            enough ? Vectors.Centroid(negatives) : null,
            feeds.Where(f => f.Up + f.Down >= w.MinFeedVotes).ToDictionary(f => f.FeedId, f => (double)f.Up / (f.Up + f.Down)));
    }
}
```

`src/FeedEater/Ranking/Scorer.cs`:

```csharp
using FeedEater.Storage;

namespace FeedEater.Ranking;

public sealed record Scored(long ItemId, double Score, string? ProfileKey);

/// <summary>score = best profile fit + Taste * (cos C+ - cos C-) + Prior * (feed 👍 rate - 0.5). Vectors are unit length.</summary>
public static class Scorer
{
    public static Scored Score(long itemId, long? feedId, float[] x, IReadOnlyList<Profile> profiles, Taste taste, WeightsOptions w)
    {
        string? key = null;
        var fit = 0d;
        foreach (var p in profiles)
        {
            var s = Vectors.Dot(x, p.Embedding);
            if (key is null || s > fit)
            {
                fit = s;
                key = p.Key;
            }
        }

        var liking = taste.Positive is null ? 0 : Vectors.Dot(x, taste.Positive) - (taste.Negative is null ? 0 : Vectors.Dot(x, taste.Negative));
        var prior = feedId is { } f && taste.FeedUpRates.TryGetValue(f, out var rate) ? rate - 0.5 : 0;
        return new Scored(itemId, fit + (w.Taste * liking) + (w.Prior * prior), key);
    }
}
```

- [ ] **Step 4: Write the feedback store**

`src/FeedEater/Storage/FeedbackStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed record FeedVotes
{
    public long FeedId { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
}

public sealed record VoteCounts
{
    public int Up { get; init; }
    public int Down { get; init; }
}

public sealed record Idea
{
    public long ItemId { get; init; }
    public string PlaneProject { get; init; } = "";
    public string PlaneIssueId { get; init; } = "";
    public string Title { get; init; } = "";
    public DateTime At { get; init; }
}

public sealed class FeedbackStore(FeedDb db)
{
    public async Task SetVoteAsync(long itemId, short value, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into votes (item_id, value, at) values (@itemId, @value, now())
            on conflict (item_id) do update set value = excluded.value, at = excluded.at
            """, new { itemId, value }, cancellationToken: ct));
    }

    /// <summary>Newest first: 👍 items, items filed as ideas, and positive signals (Karakeep saves, GitHub stars).</summary>
    public Task<IReadOnlyList<float[]>> PositiveVectorsAsync(int limit, CancellationToken ct) => VectorsAsync(
        """
        select embedding from (
            select i.embedding::real[] as embedding, v.at from votes v join items i on i.id = v.item_id
            where v.value = 1 and i.embedding is not null
            union all
            select i.embedding::real[], d.at from ideas d join items i on i.id = d.item_id where i.embedding is not null
            union all
            select embedding::real[], at from signals where polarity = 1 and embedding is not null
        ) p
        order by at desc limit @limit
        """, limit, ct);

    public Task<IReadOnlyList<float[]>> NegativeVectorsAsync(int limit, CancellationToken ct) => VectorsAsync(
        """
        select i.embedding::real[] as embedding from votes v join items i on i.id = v.item_id
        where v.value = -1 and i.embedding is not null
        order by v.at desc limit @limit
        """, limit, ct);

    public async Task<IReadOnlyList<FeedVotes>> FeedVotesAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<FeedVotes>(new CommandDefinition(
            """
            select i.feed_id, (count(*) filter (where v.value = 1))::int as up, (count(*) filter (where v.value = -1))::int as down
            from votes v join items i on i.id = v.item_id
            where i.feed_id is not null
            group by i.feed_id
            """, cancellationToken: ct))).ToList();
    }

    public async Task<VoteCounts> VotesSinceAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleAsync<VoteCounts>(new CommandDefinition(
            """
            select (count(*) filter (where value = 1))::int as up, (count(*) filter (where value = -1))::int as down
            from votes where at >= @since
            """, new { since = since.UtcDateTime }, cancellationToken: ct));
    }

    public async Task<Idea?> GetIdeaAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<Idea>(new CommandDefinition(
            "select item_id, plane_project, plane_issue_id, title, at from ideas where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    /// <summary>The first filing wins; a second one for the same item is ignored.</summary>
    public async Task AddIdeaAsync(Idea idea, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into ideas (item_id, plane_project, plane_issue_id, title, at)
            values (@ItemId, @PlaneProject, @PlaneIssueId, @Title, @At)
            on conflict (item_id) do nothing
            """, idea, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<Idea>> IdeasAsync(string? planeProject, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Idea>(new CommandDefinition(
            """
            select item_id, plane_project, plane_issue_id, title, at from ideas
            where @planeProject::text is null or plane_project = @planeProject
            order by at desc limit @limit
            """, new { planeProject, limit }, cancellationToken: ct))).ToList();
    }

    private async Task<IReadOnlyList<float[]>> VectorsAsync(string sql, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var rows = await c.QueryAsync<VectorRow>(new CommandDefinition(sql, new { limit }, cancellationToken: ct));
        return rows.Select(r => r.Embedding).ToList();
    }

    // Dapper maps arrays only as properties, not as a bare single-column result.
    private sealed record VectorRow
    {
        public float[] Embedding { get; init; } = [];
    }
}
```

The `At` passed to `AddIdeaAsync` must be a UTC `DateTime` (`DateTime.UtcNow` or `time.GetUtcNow().UtcDateTime`); Npgsql refuses a local `DateTime` for `timestamptz`.

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: scoring by profile fit, taste and feed prior; feedback store"
```

---

### Task 8: Prompts and model-output parsing

**Files:**
- Create: `src/FeedEater/Digest/Prompts.cs`, `src/FeedEater/Digest/LlmJson.cs`
- Test: `tests/FeedEater.Tests/LlmJsonTests.cs`, `tests/FeedEater.Tests/PromptsTests.cs`

**Interfaces:**
- Consumes: `Profile`, `ProfileFile.OneLiner`.
- Produces: `TriageResult { Relevance, Project, Kind, Reason }`, `ReadResult { Summary, Why, Kind, Project, Suggestion }`; `LlmJson.Triage(string, IReadOnlySet<string>) -> TriageResult` (never throws), `LlmJson.Read(string, IReadOnlySet<string>) -> ReadResult?`; `Prompts.Triage(about, profiles, title, feed, text, maxChars) -> (string System, string User)`, `Prompts.Read(about, profiles, Profile? match, title, url, feed, text, maxChars) -> (string System, string User)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/LlmJsonTests.cs`:

```csharp
using FeedEater.Digest;

namespace FeedEater.Tests;

public sealed class LlmJsonTests
{
    private static readonly HashSet<string> Keys = ["homelab", "postgres"];

    [Fact]
    public void Triage_reads_fenced_json()
    {
        var t = LlmJson.Triage("```json\n{\"relevance\": 3, \"project\": \"homelab\", \"kind\": \"improve\", \"reason\": \"new backup tool\"}\n```", Keys);

        Assert.Equal(3, t.Relevance);
        Assert.Equal("homelab", t.Project);
        Assert.Equal("improve", t.Kind);
        Assert.Equal("new backup tool", t.Reason);
    }

    [Fact]
    public void Triage_clamps_and_cleans_bad_fields()
    {
        var t = LlmJson.Triage("""{"relevance": 7, "project": "unknown", "kind": "urgent"}""", Keys);
        var s = LlmJson.Triage("""{"relevance": "2", "project": null, "kind": "new"}""", Keys);

        Assert.Equal(3, t.Relevance);
        Assert.Null(t.Project);
        Assert.Equal("fyi", t.Kind);
        Assert.Equal(2, s.Relevance);
        Assert.Equal("new", s.Kind);
    }

    [Fact]
    public void Triage_of_garbage_is_marginal_not_an_error()
    {
        var t = LlmJson.Triage("I think this is relevant!", Keys);

        Assert.Equal(1, t.Relevance);
        Assert.Equal("fyi", t.Kind);
        Assert.Equal("unparseable model output", t.Reason);
    }

    [Fact]
    public void Read_keeps_a_suggestion_only_for_improve_or_new()
    {
        var improve = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"improve","project":"postgres","suggestion":"Do X."}""", Keys)!;
        var fyi = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"fyi","project":"postgres","suggestion":"Do X."}""", Keys)!;
        var nullText = LlmJson.Read("""{"summary":"S.","why":"W.","kind":"new","suggestion":"null"}""", Keys)!;

        Assert.Equal("Do X.", improve.Suggestion);
        Assert.Equal("postgres", improve.Project);
        Assert.Null(fyi.Suggestion);
        Assert.Null(nullText.Suggestion);
    }

    [Fact]
    public void Read_without_a_summary_is_unusable()
    {
        Assert.Null(LlmJson.Read("""{"why":"W.","kind":"fyi"}""", Keys));
        Assert.Null(LlmJson.Read("no json here", Keys));
    }
}
```

`tests/FeedEater.Tests/PromptsTests.cs`:

```csharp
using FeedEater.Digest;
using FeedEater.Storage;

namespace FeedEater.Tests;

public sealed class PromptsTests
{
    private static readonly Profile Homelab = new() { Key = "homelab", Kind = "project", Description = "Hetzner VPS. About 120 containers." };
    private static readonly Profile Postgres = new() { Key = "postgres", Kind = "topic", Description = "PostgreSQL internals." };

    [Fact]
    public void Triage_lists_keys_with_one_liners_and_clips_the_text()
    {
        var (system, user) = Prompts.Triage("Backend dev.", [Homelab, Postgres], "Title", "Feed", new string('x', 5000), 2000);

        Assert.Contains("Backend dev.", system, StringComparison.Ordinal);
        Assert.Contains("\"relevance\"", system, StringComparison.Ordinal);
        Assert.Contains("- homelab (project): Hetzner VPS.", user, StringComparison.Ordinal);
        Assert.DoesNotContain("About 120 containers", user, StringComparison.Ordinal);
        Assert.Contains(new string('x', 2000), user, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 2001), user, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_gives_the_matched_project_in_full()
    {
        var (system, user) = Prompts.Read("Backend dev.", [Homelab, Postgres], Homelab, "T", "https://u", "F", "body", 24000);

        Assert.Contains("English", system, StringComparison.Ordinal);
        Assert.Contains("Best match: homelab: Hetzner VPS. About 120 containers.", user, StringComparison.Ordinal);
        Assert.Contains("URL: https://u", user, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~LlmJsonTests|FullyQualifiedName~PromptsTests"`
Expected: build FAILS with `The type or namespace name 'Digest' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Digest/LlmJson.cs`:

```csharp
using System.Globalization;
using System.Text.Json;

namespace FeedEater.Digest;

public sealed record TriageResult
{
    public int Relevance { get; init; }
    public string? Project { get; init; }
    public string Kind { get; init; } = "fyi";
    public string Reason { get; init; } = "";
}

public sealed record ReadResult
{
    public string Summary { get; init; } = "";
    public string Why { get; init; } = "";
    public string Kind { get; init; } = "fyi";
    public string? Project { get; init; }
    public string? Suggestion { get; init; }
}

/// <summary>Lenient parsing of model replies: the first {...} in the text, unknown keys and kinds dropped.</summary>
public static class LlmJson
{
    private static readonly string[] Kinds = ["improve", "new", "fyi"];

    public static TriageResult Triage(string text, IReadOnlySet<string> keys)
    {
        if (ExtractObject(text) is not { } e)
        {
            return new TriageResult { Relevance = 1, Reason = "unparseable model output" };
        }

        return new TriageResult
        {
            Relevance = Math.Clamp(Relevance(e) ?? 1, 0, 3),
            Project = Key(e, keys),
            Kind = Kind(e),
            Reason = Str(e, "reason") ?? "",
        };
    }

    public static ReadResult? Read(string text, IReadOnlySet<string> keys)
    {
        if (ExtractObject(text) is not { } e || Str(e, "summary") is not { } summary)
        {
            return null;
        }

        var kind = Kind(e);
        return new ReadResult
        {
            Summary = summary,
            Why = Str(e, "why") ?? "",
            Kind = kind,
            Project = Key(e, keys),
            Suggestion = kind == "fyi" ? null : Str(e, "suggestion"),
        };
    }

    private static JsonElement? ExtractObject(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var element = Json.Parse(text[start..(end + 1)]);
            return element.ValueKind == JsonValueKind.Object ? element : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? Relevance(JsonElement e)
    {
        if (!e.TryGetProperty("relevance", out var r))
        {
            return null;
        }

        if (r.ValueKind == JsonValueKind.Number && r.TryGetInt32(out var n))
        {
            return n;
        }

        return r.ValueKind == JsonValueKind.String && int.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { } s
        && !string.IsNullOrWhiteSpace(s) && s.Trim() != "null" ? s.Trim() : null;

    private static string Kind(JsonElement e) => Str(e, "kind") is { } k && Kinds.Contains(k) ? k : "fyi";

    private static string? Key(JsonElement e, IReadOnlySet<string> keys) => Str(e, "project") is { } k && keys.Contains(k) ? k : null;
}
```

`src/FeedEater/Digest/Prompts.cs`:

```csharp
using FeedEater.Profiles;
using FeedEater.Storage;

namespace FeedEater.Digest;

public static class Prompts
{
    public static (string System, string User) Triage(
        string about, IReadOnlyList<Profile> profiles, string title, string feed, string text, int maxChars) => (
        $$"""
        You rank news items for one reader. About him: {{about}}
        Reply with one JSON object and nothing else:
        {"relevance": 0-3, "project": "<key>" or null, "kind": "improve" | "new" | "fyi", "reason": "<at most 20 words>"}
        relevance: 3 = he should act on it this week, 2 = worth reading today, 1 = marginal, 0 = noise.
        kind: improve = changes one of his projects; new = something he could build or adopt separately; fyi = worth knowing only.
        project: one of the keys listed, or null.
        """,
        $"""
        Projects and topics:
        {List(profiles)}

        Item
        Title: {title}
        Feed: {feed}
        Text: {Clip(text, maxChars)}
        """);

    public static (string System, string User) Read(
        string about, IReadOnlyList<Profile> profiles, Profile? match, string title, string url, string feed, string text, int maxChars) => (
        $$"""
        You read one article for one reader and tell him what matters. About him: {{about}}
        Reply with one JSON object and nothing else:
        {"summary": "<at most 2 sentences: what is new>", "why": "<1 sentence: why it matters to him>", "kind": "improve" | "new" | "fyi", "project": "<key>" or null, "suggestion": "<at most 2 sentences: one concrete action>" or null}
        Write in English whatever the article's language. Plain words, no hype; if it is a minor release, say so.
        Give a suggestion only when kind is improve (an action in that project) or new (something to build or adopt).
        project: one of the keys listed, or null.
        """,
        $"""
        Best match: {(match is null ? "none" : $"{match.Key}: {match.Description}")}

        Projects and topics:
        {List(profiles)}

        Article
        Title: {title}
        URL: {url}
        Feed: {feed}

        {Clip(text, maxChars)}
        """);

    private static string List(IReadOnlyList<Profile> profiles) =>
        string.Join('\n', profiles.Select(p => $"- {p.Key} ({p.Kind}): {ProfileFile.OneLiner(p.Description)}"));

    private static string Clip(string text, int maxChars) => text.Length <= maxChars ? text : text[..maxChars];
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: triage and read prompts with lenient JSON parsing"
```

---

### Task 9: Digest formatting and button data

**Files:**
- Create: `src/FeedEater/Telegram/Messages.cs`, `src/FeedEater/Telegram/CallbackData.cs`, `src/FeedEater/Digest/DigestFormatter.cs`
- Test: `tests/FeedEater.Tests/DigestFormatterTests.cs`, `tests/FeedEater.Tests/CallbackDataTests.cs`

**Interfaces:**
- Produces: `Button(string Text, string Data)`, `OutMessage(string Html, IReadOnlyList<IReadOnlyList<Button>>? Keyboard = null)`; `CallbackData` with static `Parse(string?) -> CallbackData?`, `Vote(long, short)`, `Idea(long)`, const `Noop`, and subtypes `VoteCallback(long ItemId, short Value)`, `IdeaCallback(long ItemId)`, `NoopCallback`; `DigestItem { Id, Title, Url, Feed, Project, Kind, Summary, Why, Suggestion }`; `DigestHeader(DateOnly Date, int Shown, int Candidates, IReadOnlyList<(string Key, int Count)> ByProject, int VotesUp, int VotesDown, decimal MonthSpend, IReadOnlyList<string> Notes)`; `DigestFormatter.Header(DigestHeader) -> OutMessage`, `DigestFormatter.Item(DigestItem, short? vote, string? filedIn) -> OutMessage`, `DigestFormatter.Buttons(long id, bool hasSuggestion, short? vote, string? filedIn)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/CallbackDataTests.cs`:

```csharp
using System.Text;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class CallbackDataTests
{
    [Fact]
    public void Round_trips_votes_ideas_and_noop()
    {
        Assert.Equal(new VoteCallback(5, 1), CallbackData.Parse(CallbackData.Vote(5, 1)));
        Assert.Equal(new VoteCallback(5, -1), CallbackData.Parse(CallbackData.Vote(5, -1)));
        Assert.Equal(new IdeaCallback(9), CallbackData.Parse(CallbackData.Idea(9)));
        Assert.IsType<NoopCallback>(CallbackData.Parse(CallbackData.Noop));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("x:1")]
    [InlineData("v:abc:u")]
    [InlineData("v:1:z")]
    [InlineData("i:-3")]
    public void Rejects_anything_else(string? data) => Assert.Null(CallbackData.Parse(data));

    [Fact]
    public void Fits_telegrams_64_byte_limit_for_the_largest_id() =>
        Assert.True(Encoding.UTF8.GetByteCount(CallbackData.Vote(long.MaxValue, -1)) <= 64);
}
```

`tests/FeedEater.Tests/DigestFormatterTests.cs`:

```csharp
using System.Net;
using System.Text.RegularExpressions;
using FeedEater.Digest;
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class DigestFormatterTests
{
    private static DigestItem Item(string? suggestion = "Add pg_stat_io to the dashboard.", string url = "https://example.com/a?x=1&y=2") => new()
    {
        Id = 42, Title = "Postgres <18.1> & friends", Url = url, Feed = "PostgreSQL News", Project = "homelab",
        Kind = suggestion is null ? "fyi" : "improve", Summary = "Async I/O fixes.", Why = "Your box runs PG 18.", Suggestion = suggestion,
    };

    private static int VisibleLength(string html) => WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")).Length;

    [Fact]
    public void Item_escapes_text_and_shows_the_suggestion()
    {
        var m = DigestFormatter.Item(Item(), null, null);

        Assert.StartsWith("💡 <b><a href=\"https://example.com/a?x=1&amp;y=2\">Postgres &lt;18.1&gt; &amp; friends</a></b>", m.Html, StringComparison.Ordinal);
        Assert.Contains("<i>PostgreSQL News · homelab · improve</i>", m.Html, StringComparison.Ordinal);
        Assert.Contains("💡 Add pg_stat_io to the dashboard.", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Buttons_follow_vote_and_filing_state()
    {
        var fresh = DigestFormatter.Buttons(42, true, null, null);
        var voted = DigestFormatter.Buttons(42, true, 1, null);
        var filed = DigestFormatter.Buttons(42, true, 1, "SKAR");
        var plain = DigestFormatter.Buttons(42, false, null, null);

        Assert.Equal(["👍", "👎"], fresh[0].Select(b => b.Text));
        Assert.Equal(["v:42:u", "v:42:d"], fresh[0].Select(b => b.Data));
        Assert.Equal("i:42", fresh[1].Single().Data);
        Assert.Equal("👍 ✓", voted[0][0].Text);
        Assert.Equal(("✓ Filed in SKAR", "n"), (filed[1].Single().Text, filed[1].Single().Data));
        Assert.Single(plain);
    }

    [Fact]
    public void Huge_fields_stay_under_telegrams_limit_and_a_huge_url_drops_the_link()
    {
        var big = new string('&', 10_000);
        var m = DigestFormatter.Item(Item(big, "https://example.com/" + new string('a', 2000)) with { Title = big, Summary = big, Why = big, Feed = big }, null, null);

        Assert.True(VisibleLength(m.Html) <= DigestFormatter.MaxLength);
        Assert.DoesNotContain("<a href", m.Html, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_has_counts_votes_spend_and_notes()
    {
        var h = DigestFormatter.Header(new DigestHeader(
            new DateOnly(2026, 10, 5), 12, 412, [("homelab", 5), ("postgres", 3)], 7, 2, 1.234m, ["Miniflux was unreachable"]));

        Assert.Contains("<b>Feed digest · 5 Oct</b>", h.Html, StringComparison.Ordinal);
        Assert.Contains("12 of 412 new items", h.Html, StringComparison.Ordinal);
        Assert.Contains("homelab 5 · postgres 3", h.Html, StringComparison.Ordinal);
        Assert.Contains("Yesterday 👍 7 · 👎 2 · 30-day spend $1.23", h.Html, StringComparison.Ordinal);
        Assert.Contains("⚠️ Miniflux was unreachable", h.Html, StringComparison.Ordinal);
        Assert.Null(h.Keyboard);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~DigestFormatterTests|FullyQualifiedName~CallbackDataTests"`
Expected: build FAILS with `The type or namespace name 'Telegram' does not exist`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Telegram/Messages.cs`:

```csharp
namespace FeedEater.Telegram;

public sealed record Button(string Text, string Data);

public sealed record OutMessage(string Html, IReadOnlyList<IReadOnlyList<Button>>? Keyboard = null);
```

`src/FeedEater/Telegram/CallbackData.cs`:

```csharp
using System.Globalization;

namespace FeedEater.Telegram;

/// <summary>Button payloads: <c>v:{id}:u</c>, <c>v:{id}:d</c>, <c>i:{id}</c>, <c>n</c>. Telegram allows 64 bytes.</summary>
public abstract record CallbackData
{
    public const string Noop = "n";

    public static string Vote(long itemId, short value) => string.Create(CultureInfo.InvariantCulture, $"v:{itemId}:{(value > 0 ? "u" : "d")}");

    public static string Idea(long itemId) => string.Create(CultureInfo.InvariantCulture, $"i:{itemId}");

    public static CallbackData? Parse(string? data) => data?.Split(':') switch
    {
        ["v", var id, "u"] when Id(id) is { } i => new VoteCallback(i, 1),
        ["v", var id, "d"] when Id(id) is { } i => new VoteCallback(i, -1),
        ["i", var id] when Id(id) is { } i => new IdeaCallback(i),
        [Noop] => new NoopCallback(),
        _ => null,
    };

    private static long? Id(string text) => long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
}

public sealed record VoteCallback(long ItemId, short Value) : CallbackData;

public sealed record IdeaCallback(long ItemId) : CallbackData;

public sealed record NoopCallback : CallbackData;
```

`src/FeedEater/Digest/DigestFormatter.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text;
using FeedEater.Telegram;

namespace FeedEater.Digest;

public sealed record DigestItem
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public string? Project { get; init; }
    public string Kind { get; init; } = "fyi";
    public string Summary { get; init; } = "";
    public string Why { get; init; } = "";
    public string? Suggestion { get; init; }
}

public sealed record DigestHeader(
    DateOnly Date, int Shown, int Candidates, IReadOnlyList<(string Key, int Count)> ByProject,
    int VotesUp, int VotesDown, decimal MonthSpend, IReadOnlyList<string> Notes);

/// <summary>
/// Telegram HTML. Every field is escaped and clipped so the visible text stays under 4,096 characters
/// (Telegram counts text after entity parsing, so escapes and the href do not count).
/// </summary>
public static class DigestFormatter
{
    public const int MaxLength = 4096;
    private const int MaxLinkedUrl = 1000;

    public static OutMessage Header(DigestHeader h)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"<b>Feed digest · {h.Date.ToString("d MMM", CultureInfo.InvariantCulture)}</b>\n");
        sb.Append(CultureInfo.InvariantCulture, $"{h.Shown} of {h.Candidates} new items");
        if (h.ByProject.Count > 0)
        {
            sb.Append('\n').Append(string.Join(" · ", h.ByProject.Select(p => string.Create(CultureInfo.InvariantCulture, $"{E(p.Key)} {p.Count}"))));
        }

        sb.Append(CultureInfo.InvariantCulture, $"\nYesterday 👍 {h.VotesUp} · 👎 {h.VotesDown} · 30-day spend ${h.MonthSpend.ToString("0.00", CultureInfo.InvariantCulture)}");
        foreach (var note in h.Notes)
        {
            sb.Append("\n⚠️ ").Append(E(Clip(note, 300)));
        }

        return new OutMessage(sb.ToString());
    }

    public static OutMessage Item(DigestItem i, short? vote, string? filedIn)
    {
        var title = E(Clip(i.Title, 300));
        var link = i.Url.Length <= MaxLinkedUrl ? $"<a href=\"{E(i.Url)}\">{title}</a>" : title;
        var meta = string.Join(" · ", new[] { Clip(i.Feed, 80), i.Project, i.Kind }.Where(s => !string.IsNullOrEmpty(s)).Select(s => E(s!)));
        var html = new StringBuilder()
            .Append(i.Suggestion is null ? "📰" : "💡").Append(" <b>").Append(link).Append("</b>\n")
            .Append("<i>").Append(meta).Append("</i>\n\n")
            .Append(E(Clip(i.Summary, 1200))).Append('\n')
            .Append(E(Clip(i.Why, 600)));
        if (i.Suggestion is not null)
        {
            html.Append("\n\n💡 ").Append(E(Clip(i.Suggestion, 800)));
        }

        return new OutMessage(html.ToString(), Buttons(i.Id, i.Suggestion is not null, vote, filedIn));
    }

    public static IReadOnlyList<IReadOnlyList<Button>> Buttons(long id, bool hasSuggestion, short? vote, string? filedIn)
    {
        var rows = new List<IReadOnlyList<Button>>
        {
            new[]
            {
                new Button(vote == 1 ? "👍 ✓" : "👍", CallbackData.Vote(id, 1)),
                new Button(vote == -1 ? "👎 ✓" : "👎", CallbackData.Vote(id, -1)),
            },
        };
        if (filedIn is not null)
        {
            rows.Add(new[] { new Button($"✓ Filed in {filedIn}", CallbackData.Noop) });
        }
        else if (hasSuggestion)
        {
            rows.Add(new[] { new Button("💡 To Plane", CallbackData.Idea(id)) });
        }

        return rows;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);

    private static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: digest messages, buttons and callback data"
```

---

### Task 10: Telegram client

**Files:**
- Create: `src/FeedEater/Telegram/TelegramClient.cs`
- Test: `tests/FeedEater.Tests/TelegramClientTests.cs`

**Interfaces:**
- Consumes: `OutMessage`, `Button`.
- Produces: `TgCallback(string Id, long FromId, long ChatId, long MessageId, string? Data)`, `TgUpdate(long UpdateId, TgCallback? Callback)`, `TelegramException`; `TelegramClient(HttpClient)` (base address `https://api.telegram.org/bot{token}/`) with `SendAsync(long chatId, OutMessage, ct) -> long messageId`, `EditButtonsAsync(chatId, messageId, keyboard, ct)`, `AnswerAsync(callbackId, string? text, ct)`, `GetUpdatesAsync(long offset, int timeoutSeconds, ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/TelegramClientTests.cs`:

```csharp
using FeedEater.Telegram;

namespace FeedEater.Tests;

public sealed class TelegramClientTests
{
    private static (TelegramClient Client, StubHandler Handler) Build(string response)
    {
        var handler = new StubHandler((_, _) => StubHandler.Json(response));
        return (new TelegramClient(handler.Client("http://tg/botT/")), handler);
    }

    [Fact]
    public async Task Send_posts_html_with_inline_buttons_and_returns_the_message_id()
    {
        var (client, handler) = Build("""{"ok":true,"result":{"message_id":77}}""");

        var id = await client.SendAsync(42, new OutMessage("<b>hi</b>", [[new Button("👍", "v:1:u")]]), default);

        Assert.Equal(77, id);
        var call = handler.Calls.Single();
        Assert.Equal("http://tg/botT/sendMessage", call.Uri);
        Assert.Contains("\"parse_mode\":\"HTML\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"inline_keyboard\":[[{\"text\":", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"callback_data\":\"v:1:u\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"is_disabled\":true", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Send_without_buttons_has_no_markup_and_answer_without_text_has_no_text()
    {
        var (client, handler) = Build("""{"ok":true,"result":{"message_id":1}}""");

        await client.SendAsync(42, new OutMessage("plain"), default);
        var (answerClient, answerHandler) = Build("""{"ok":true,"result":true}""");
        await answerClient.AnswerAsync("cb1", null, default);

        Assert.DoesNotContain("reply_markup", handler.Calls.Single().Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\"", answerHandler.Calls.Single().Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Not_ok_becomes_TelegramException_with_the_description()
    {
        var (client, _) = Build("""{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""");

        var error = await Assert.ThrowsAsync<TelegramException>(() => client.EditButtonsAsync(42, 7, [[new Button("x", "n")]], default));

        Assert.Contains("message is not modified", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Get_updates_parses_callbacks_and_skips_other_updates()
    {
        var (client, handler) = Build(
            """
            {"ok":true,"result":[
              {"update_id":10,"callback_query":{"id":"cb1","from":{"id":42},"message":{"message_id":7,"chat":{"id":42}},"data":"v:5:u"}},
              {"update_id":11,"message":{"message_id":8,"text":"hi"}}
            ]}
            """);

        var updates = await client.GetUpdatesAsync(10, 50, default);

        Assert.Equal(new TgCallback("cb1", 42, 42, 7, "v:5:u"), updates[0].Callback);
        Assert.Null(updates[1].Callback);
        Assert.Contains("\"allowed_updates\":[\"callback_query\"]", handler.Calls.Single().Body, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~TelegramClientTests"`
Expected: build FAILS with `The type or namespace name 'TelegramClient' could not be found`.

- [ ] **Step 3: Write the implementation**

`src/FeedEater/Telegram/TelegramClient.cs`:

```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace FeedEater.Telegram;

public sealed record TgCallback(string Id, long FromId, long ChatId, long MessageId, string? Data);

public sealed record TgUpdate(long UpdateId, TgCallback? Callback);

public sealed class TelegramException(string message) : Exception(message);

/// <summary>The four Bot API calls feed-eater needs. The token is in the base address, so URIs are never logged.</summary>
public sealed class TelegramClient(HttpClient http)
{
    public async Task<long> SendAsync(long chatId, OutMessage message, CancellationToken ct)
    {
        var result = await CallAsync("sendMessage", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["text"] = message.Html,
            ["parse_mode"] = "HTML",
            ["link_preview_options"] = new { is_disabled = true },
            ["reply_markup"] = message.Keyboard is null ? null : Markup(message.Keyboard),
        }, ct);
        return result.GetProperty("message_id").GetInt64();
    }

    public Task EditButtonsAsync(long chatId, long messageId, IReadOnlyList<IReadOnlyList<Button>> keyboard, CancellationToken ct) =>
        CallAsync("editMessageReplyMarkup", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_id"] = messageId,
            ["reply_markup"] = Markup(keyboard),
        }, ct);

    public Task AnswerAsync(string callbackId, string? text, CancellationToken ct) =>
        CallAsync("answerCallbackQuery", new Dictionary<string, object?>
        {
            ["callback_query_id"] = callbackId,
            ["text"] = text,
        }, ct);

    public async Task<IReadOnlyList<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, CancellationToken ct)
    {
        var result = await CallAsync("getUpdates", new Dictionary<string, object?>
        {
            ["offset"] = offset,
            ["timeout"] = timeoutSeconds,
            ["allowed_updates"] = new[] { "callback_query" },
        }, ct);
        return result.EnumerateArray()
            .Select(u => new TgUpdate(
                u.GetProperty("update_id").GetInt64(),
                u.TryGetProperty("callback_query", out var c) ? Callback(c) : null))
            .ToList();
    }

    private static TgCallback Callback(JsonElement c)
    {
        var hasMessage = c.TryGetProperty("message", out var m);
        return new TgCallback(
            c.GetProperty("id").GetString()!,
            c.GetProperty("from").GetProperty("id").GetInt64(),
            hasMessage ? m.GetProperty("chat").GetProperty("id").GetInt64() : 0,
            hasMessage ? m.GetProperty("message_id").GetInt64() : 0,
            c.TryGetProperty("data", out var d) ? d.GetString() : null);
    }

    private static object Markup(IReadOnlyList<IReadOnlyList<Button>> keyboard) =>
        new { inline_keyboard = keyboard.Select(row => row.Select(b => new { text = b.Text, callback_data = b.Data })) };

    private async Task<JsonElement> CallAsync(string method, Dictionary<string, object?> payload, CancellationToken ct)
    {
        foreach (var key in payload.Where(p => p.Value is null).Select(p => p.Key).ToList())
        {
            payload.Remove(key);
        }

        using var response = await http.PostAsJsonAsync(method, payload, ct);
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        if (!body.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            var description = body.TryGetProperty("description", out var d) ? d.GetString() : response.StatusCode.ToString();
            throw new TelegramException($"Telegram {method}: {description}");
        }

        return body.GetProperty("result");
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat: Telegram Bot API client"
```

---
### Task 11: The digest run

**Files:**
- Create: `src/FeedEater/Storage/AnalysisStore.cs`, `src/FeedEater/Storage/DigestStore.cs`, `src/FeedEater/Digest/DigestRun.cs`, `src/FeedEater/Digest/DigestJob.cs`
- Modify: `src/FeedEater/Storage/ItemStore.cs` (add `Candidate`, `CandidatesAsync`, `SetScoresAsync`, `SetContentAsync`, `DigestItemsAsync`)
- Test: `tests/FeedEater.Tests/DigestRunTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2–10.
- Produces: `Candidate { Id, MinifluxEntryId, FeedId, Title, Url, FeedTitle, Content, Embedding }`; `ItemStore.CandidatesAsync(DateTimeOffset ingestedAfter, DateTimeOffset publishedAfter, ct)`, `SetScoresAsync(IReadOnlyList<Scored>, ct)`, `SetContentAsync(long, string, ct)`, `DigestItemsAsync(long[], ct) -> IReadOnlyList<DigestItem>` (in the given order, read items only); `AnalysisStore(FeedDb)` with `GetTriageAsync`, `SaveTriageAsync(long, TriageResult, string model, ct)`, `GetReadAsync`, `SaveReadAsync(long, ReadResult, string model, ct)`; `DigestRow { LocalDate, Status, Candidates, Triaged, ItemIds, SentCount, Note, Error, SentAt }`; `DigestStore(FeedDb)` with `GetAsync(date)`, `CreateAsync(date)`, `SetSelectionAsync(date, candidates, triaged, long[], note)`, `SetSentCountAsync(date, n)`, `MarkSentAsync(date, at)`, `MarkFailedAsync(date, error)`, `LastSentAtAsync()`, `ListAsync(before, limit)` (dates are `yyyy-MM-dd` strings); `Selection(Candidates, Triaged, ItemIds, Notes)`; `DigestRun.RunAsync(string date, ct)`, internal `SelectAsync(ct)`; `DigestJob` (scheduled, window 07:30–12:00).

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/DigestRunTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class DigestRunTests(PostgresFixture pg) : IAsyncLifetime
{
    private const string Today = "2026-10-05";
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 4, 30, 0, TimeSpan.Zero); // 07:30 Kyiv
    private static readonly string LongText = string.Join(' ', Enumerable.Repeat("Postgres async I/O details.", 80));

    private readonly List<string> _sent = [];
    private int _telegramCalls;
    private int _failTelegramAt;
    private int _chatCalls;
    private bool _budgetOnB;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string Chat(string content) => JsonSerializer.Serialize(new
    {
        choices = new[] { new { message = new { content } } },
        usage = new { prompt_tokens = 100, completion_tokens = 20 },
    });

    private HttpResponseMessage Llm(string body)
    {
        Interlocked.Increment(ref _chatCalls);
        if (body.Contains("\"model\":\"gpt-4.1-nano\"", StringComparison.Ordinal))
        {
            var keep = body.Contains("Title: Keep", StringComparison.Ordinal);
            return StubHandler.Json(Chat(keep ? """{"relevance":3,"project":"homelab","kind":"improve","reason":"r"}""" : """{"relevance":0,"kind":"fyi"}"""));
        }

        if (body.Contains("Title: Keep A", StringComparison.Ordinal))
        {
            return StubHandler.Json(Chat("""{"summary":"A is new.","why":"Box runs it.","kind":"improve","project":"homelab","suggestion":"Turn A on."}"""));
        }

        return _budgetOnB
            ? StubHandler.Json("""{"error":{"message":"ExceededTokenBudget"}}""", HttpStatusCode.Unauthorized)
            : StubHandler.Json(Chat("""{"summary":"B is out.","why":"Context.","kind":"fyi","project":null,"suggestion":null}"""));
    }

    private HttpResponseMessage Telegram(string body)
    {
        var n = Interlocked.Increment(ref _telegramCalls);
        if (n == _failTelegramAt)
        {
            return StubHandler.Json("""{"ok":false,"description":"Too Many Requests"}""");
        }

        _sent.Add(body);
        return StubHandler.Json($$"""{"ok":true,"result":{"message_id":{{n}}}}""");
    }

    private (DigestRun Run, DigestStore Digests, StubHandler Miniflux) Build()
    {
        var options = Options.Create(new FeedEaterOptions { ProfilePath = "Fixtures/profile.json", Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var time = new FakeTimeProvider(Now);
        var miniflux = new StubHandler((_, _) => StubHandler.Json(JsonSerializer.Serialize(new { content = $"<p>{LongText}</p>" })));
        var llm = new StubHandler((_, body) => body.Contains("\"input\"", StringComparison.Ordinal)
            ? StubHandler.Json(TestVectors.EmbeddingResponse(1))
            : Llm(body));
        var telegram = new StubHandler((_, body) => Telegram(body));
        var digests = new DigestStore(pg.Db);
        var run = new DigestRun(
            new ItemStore(pg.Db), new ProfileStore(pg.Db), new FeedbackStore(pg.Db), new AnalysisStore(pg.Db), digests, new UsageStore(pg.Db),
            new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new MinifluxClient(miniflux.Client("http://miniflux/")),
            new TelegramClient(telegram.Client("http://tg/botT/")),
            new LoopHealth(time), options, time, NullLogger<DigestRun>.Instance);
        return (run, digests, miniflux);
    }

    private async Task SeedAsync()
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "Hetzner VPS.", Embedding = TestVectors.OneHot(0) }],
            Now, default);
        var hourAgo = Now.UtcDateTime.AddHours(-1);
        await Seed.ItemAsync(pg, 1, "Keep A", TestVectors.OneHot(0), hourAgo, hourAgo, LongText, 201);
        await Seed.ItemAsync(pg, 1, "Keep B", TestVectors.Blend(0, 1, 0.3f), hourAgo, hourAgo, "short", 202);
        await Seed.ItemAsync(pg, 2, "Drop C", TestVectors.Blend(0, 2, 0.6f), hourAgo, hourAgo, LongText, 203);
        // Backfilled today but published a month ago: archived, never a candidate.
        await Seed.ItemAsync(pg, 2, "Keep Old", TestVectors.OneHot(0), Now.UtcDateTime.AddDays(-30), hourAgo, LongText, 204);
    }

    [Fact]
    public async Task Sends_the_header_then_ideas_first_and_runs_only_once()
    {
        await SeedAsync();
        var (run, digests, miniflux) = Build();

        await run.RunAsync(Today, default);
        var chatCalls = _chatCalls;
        await run.RunAsync(Today, default);

        Assert.Equal(3, _sent.Count);
        Assert.Contains("2 of 3 new items", _sent[0], StringComparison.Ordinal);
        Assert.Contains("Keep A", _sent[1], StringComparison.Ordinal);
        Assert.Contains("Turn A on.", _sent[1], StringComparison.Ordinal);
        Assert.Contains("Keep B", _sent[2], StringComparison.Ordinal);
        Assert.DoesNotContain(_sent, s => s.Contains("Keep Old", StringComparison.Ordinal));
        Assert.Equal(chatCalls, _chatCalls);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
        Assert.Single(miniflux.Calls, c => c.Uri.Contains("/v1/entries/202/fetch-content", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Resumes_after_a_telegram_failure_without_paying_again()
    {
        await SeedAsync();
        var (run, digests, _) = Build();
        _failTelegramAt = 2;

        await Assert.ThrowsAsync<TelegramException>(() => run.RunAsync(Today, default));
        var afterFirst = _chatCalls;
        Assert.Equal(1, (await digests.GetAsync(Today, default))!.SentCount);

        await run.RunAsync(Today, default);

        Assert.Equal(afterFirst, _chatCalls);
        Assert.Equal(3, _sent.Count);
        Assert.Single(_sent, s => s.Contains("Feed digest", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spent_budget_still_sends_what_was_read_with_a_note()
    {
        await SeedAsync();
        _budgetOnB = true;
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Equal(2, _sent.Count);
        Assert.Contains("budget reached", _sent[0], StringComparison.Ordinal);
        Assert.Equal("sent", (await digests.GetAsync(Today, default))!.Status);
    }

    [Fact]
    public async Task No_candidates_sends_one_explanation_and_returns()
    {
        var (run, digests, _) = Build();

        await run.RunAsync(Today, default);

        Assert.Contains("No digest today", Assert.Single(_sent), StringComparison.Ordinal);
        Assert.Equal("failed", (await digests.GetAsync(Today, default))!.Status);
    }
}
```

"2 of 3": the header counts shown items against candidates. A and B are shown; A, B and C are candidates; the month-old item is not.

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~DigestRunTests"`
Expected: build FAILS with `The type or namespace name 'DigestRun' could not be found`.

- [ ] **Step 3: Write the analysis and digest stores**

`src/FeedEater/Storage/AnalysisStore.cs`:

```csharp
using Dapper;
using FeedEater.Digest;

namespace FeedEater.Storage;

/// <summary>Model results per item, kept so a rerun never pays for the same item twice.</summary>
public sealed class AnalysisStore(FeedDb db)
{
    public async Task<TriageResult?> GetTriageAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<TriageResult>(new CommandDefinition(
            "select relevance::int as relevance, project, kind, reason from triage where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    public async Task SaveTriageAsync(long itemId, TriageResult t, string model, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into triage (item_id, relevance, project, kind, reason, model) values (@itemId, @Relevance, @Project, @Kind, @Reason, @model)
            on conflict (item_id) do update set relevance = excluded.relevance, project = excluded.project, kind = excluded.kind,
                                                reason = excluded.reason, model = excluded.model, at = now()
            """, new { itemId, t.Relevance, t.Project, t.Kind, t.Reason, model }, cancellationToken: ct));
    }

    public async Task<ReadResult?> GetReadAsync(long itemId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ReadResult>(new CommandDefinition(
            "select summary, why, kind, project, suggestion from reads where item_id = @itemId",
            new { itemId }, cancellationToken: ct));
    }

    public async Task SaveReadAsync(long itemId, ReadResult r, string model, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into reads (item_id, summary, why, kind, project, suggestion, model)
            values (@itemId, @Summary, @Why, @Kind, @Project, @Suggestion, @model)
            on conflict (item_id) do update set summary = excluded.summary, why = excluded.why, kind = excluded.kind,
                                                project = excluded.project, suggestion = excluded.suggestion, model = excluded.model, at = now()
            """, new { itemId, r.Summary, r.Why, r.Kind, r.Project, r.Suggestion, model }, cancellationToken: ct));
    }
}
```

`src/FeedEater/Storage/DigestStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed record DigestRow
{
    public string LocalDate { get; init; } = "";
    public string Status { get; init; } = "";
    public int Candidates { get; init; }
    public int Triaged { get; init; }
    public long[] ItemIds { get; init; } = [];
    public int SentCount { get; init; }
    public string? Note { get; init; }
    public string? Error { get; init; }
    public DateTime? SentAt { get; init; }
}

/// <summary>One row per local date (yyyy-MM-dd). A sent digest is never changed again.</summary>
public sealed class DigestStore(FeedDb db)
{
    private const string Columns = "local_date::text as local_date, status, candidates, triaged, item_ids, sent_count, note, error, sent_at";

    public async Task<DigestRow?> GetAsync(string date, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<DigestRow>(new CommandDefinition(
            $"select {Columns} from digests where local_date = @date::date", new { date }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<DigestRow>> ListAsync(string? before, int limit, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DigestRow>(new CommandDefinition(
            $"select {Columns} from digests where @before::date is null or local_date < @before::date order by local_date desc limit @limit",
            new { before, limit }, cancellationToken: ct))).ToList();
    }

    public Task CreateAsync(string date, CancellationToken ct) => ExecuteAsync(
        "insert into digests (local_date, status) values (@date::date, 'building') on conflict (local_date) do nothing", new { date }, ct);

    public Task SetSelectionAsync(string date, int candidates, int triaged, long[] itemIds, string? note, CancellationToken ct) => ExecuteAsync(
        """
        update digests set candidates = @candidates, triaged = @triaged, item_ids = @itemIds, note = @note, sent_count = 0
        where local_date = @date::date and status <> 'sent'
        """, new { date, candidates, triaged, itemIds, note }, ct);

    public Task SetSentCountAsync(string date, int sentCount, CancellationToken ct) => ExecuteAsync(
        "update digests set sent_count = @sentCount where local_date = @date::date", new { date, sentCount }, ct);

    public Task MarkSentAsync(string date, DateTimeOffset at, CancellationToken ct) => ExecuteAsync(
        "update digests set status = 'sent', sent_at = @at, error = null where local_date = @date::date",
        new { date, at = at.UtcDateTime }, ct);

    public Task MarkFailedAsync(string date, string error, CancellationToken ct) => ExecuteAsync(
        """
        insert into digests (local_date, status, error) values (@date::date, 'failed', @error)
        on conflict (local_date) do update set status = 'failed', error = excluded.error where digests.status <> 'sent'
        """, new { date, error }, ct);

    public async Task<DateTime?> LastSentAtAsync(CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<DateTime?>(new CommandDefinition(
            "select max(sent_at) from digests where status = 'sent'", cancellationToken: ct));
    }

    private async Task ExecuteAsync(string sql, object args, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(sql, args, cancellationToken: ct));
    }
}
```

- [ ] **Step 4: Add the digest queries to the item store**

Add `using FeedEater.Digest;` and `using FeedEater.Ranking;` to `src/FeedEater/Storage/ItemStore.cs`, this record next to `PendingEmbed`:

```csharp
public sealed record Candidate
{
    public long Id { get; init; }
    public long? MinifluxEntryId { get; init; }
    public long? FeedId { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string FeedTitle { get; init; } = "";
    public string Content { get; init; } = "";
    public float[] Embedding { get; init; } = [];
}
```

and these methods inside `ItemStore`:

```csharp
    /// <summary>
    /// Items new since the last digest. Both bounds matter: ingested_at alone would hand the first digest the whole
    /// backfill, published_at alone would repeat yesterday's items.
    /// </summary>
    public async Task<IReadOnlyList<Candidate>> CandidatesAsync(DateTimeOffset ingestedAfter, DateTimeOffset publishedAfter, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<Candidate>(new CommandDefinition(
            """
            select i.id, i.miniflux_entry_id, i.feed_id, i.title, i.url, coalesce(f.title, '') as feed_title, i.content,
                   i.embedding::real[] as embedding
            from items i left join feeds f on f.id = i.feed_id
            where i.ingested_at > @ingestedAfter and i.published_at > @publishedAfter
              and i.duplicate_of is null and i.embedding is not null
            """,
            new { ingestedAfter = ingestedAfter.UtcDateTime, publishedAfter = publishedAfter.UtcDateTime }, cancellationToken: ct))).ToList();
    }

    public async Task SetScoresAsync(IReadOnlyList<Scored> scores, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            update items i set score = s.score, profile_key = s.key
            from unnest(@ids, @values, @keys) as s(id, score, key)
            where i.id = s.id
            """,
            new
            {
                ids = scores.Select(s => s.ItemId).ToArray(),
                values = scores.Select(s => (float)s.Score).ToArray(),
                keys = scores.Select(s => s.ProfileKey).ToArray(),
            }, cancellationToken: ct));
    }

    public async Task SetContentAsync(long id, string content, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            "update items set content = @content where id = @id", new { id, content }, cancellationToken: ct));
    }

    /// <summary>The digest's items in its order; items without a read result are skipped.</summary>
    public async Task<IReadOnlyList<DigestItem>> DigestItemsAsync(long[] ids, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<DigestItem>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, r.project, r.kind, r.summary, r.why, r.suggestion
            from unnest(@ids::bigint[]) with ordinality as x(id, ord)
            join items i on i.id = x.id
            join reads r on r.item_id = i.id
            left join feeds f on f.id = i.feed_id
            order by x.ord
            """, new { ids }, cancellationToken: ct))).ToList();
    }
```

- [ ] **Step 5: Write the digest run**

`src/FeedEater/Digest/DigestRun.cs`:

```csharp
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Profiles;
using FeedEater.Ranking;
using FeedEater.Storage;
using FeedEater.Telegram;
using FeedEater.Text;

namespace FeedEater.Digest;

public sealed record Selection(int Candidates, int Triaged, IReadOnlyList<long> ItemIds, IReadOnlyList<string> Notes);

/// <summary>
/// One digest: rank candidates, triage the top N with the small model, read the top M with the strong one, send.
/// Every step is stored, so a rerun resumes: results per item in triage/reads, delivered messages in sent_count.
/// </summary>
public sealed class DigestRun(
    ItemStore items, ProfileStore profiles, FeedbackStore feedback, AnalysisStore analysis, DigestStore digests, UsageStore usage,
    LiteLlmClient llm, MinifluxClient miniflux, TelegramClient telegram, LoopHealth health,
    IOptions<FeedEaterOptions> options, TimeProvider time, ILogger<DigestRun> logger)
{
    private const int TriageMaxTokens = 200;
    private const int ReadMaxTokens = 600;

    public async Task RunAsync(string date, CancellationToken ct)
    {
        var digest = await digests.GetAsync(date, ct);
        if (digest?.Status == "sent")
        {
            return;
        }

        if (digest is null || digest.ItemIds.Length == 0)
        {
            await digests.CreateAsync(date, ct);
            var selection = await SelectAsync(ct);
            if (selection.ItemIds.Count == 0)
            {
                var why = selection.Notes.Count > 0 ? string.Join("; ", selection.Notes) : $"none of {selection.Candidates} new items passed triage";
                await telegram.SendAsync(options.Value.Telegram.AllowedUserId, new OutMessage($"No digest today: {WebUtility.HtmlEncode(why)}"), ct);
                await digests.MarkFailedAsync(date, why, ct);
                return;
            }

            await digests.SetSelectionAsync(date, selection.Candidates, selection.Triaged, [.. selection.ItemIds],
                selection.Notes.Count == 0 ? null : string.Join('\n', selection.Notes), ct);
            digest = (await digests.GetAsync(date, ct))!;
        }

        await SendAsync(digest, ct);
    }

    internal async Task<Selection> SelectAsync(CancellationToken ct)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var notes = new List<string>();
        if (health.IsDown(Ingestor.LoopName))
        {
            notes.Add("Miniflux was unreachable at the last poll; some items may be missing");
        }

        var floor = now - TimeSpan.FromDays(o.Caps.CandidateDays);
        var lastSent = await digests.LastSentAtAsync(ct);
        var since = lastSent is { } l && new DateTimeOffset(l, TimeSpan.Zero) > floor ? new DateTimeOffset(l, TimeSpan.Zero) : floor;
        var candidates = await items.CandidatesAsync(since, floor, ct);
        var profileList = await profiles.AllAsync(ct);
        var taste = Taste.Build(
            await feedback.PositiveVectorsAsync(o.Caps.Centroid, ct), await feedback.NegativeVectorsAsync(o.Caps.Centroid, ct),
            await feedback.FeedVotesAsync(ct), o.Weights);
        var scored = candidates.Select(c => Scorer.Score(c.Id, c.FeedId, c.Embedding, profileList, taste, o.Weights)).ToList();
        await items.SetScoresAsync(scored, ct);

        var byId = candidates.ToDictionary(c => c.Id);
        var keys = profileList.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        var about = ProfileFile.Load(o.ProfilePath).About;

        var triaged = new List<(Scored Score, TriageResult Triage)>();
        foreach (var s in scored.OrderByDescending(s => s.Score).Take(o.Caps.Triage))
        {
            var t = await analysis.GetTriageAsync(s.ItemId, ct);
            if (t is null)
            {
                var c = byId[s.ItemId];
                var (system, user) = Prompts.Triage(about, profileList, c.Title, c.FeedTitle, c.Content, o.Caps.TriageChars);
                try
                {
                    t = LlmJson.Triage((await llm.ChatAsync(o.Llm.TriageModel, system, user, TriageMaxTokens, "triage", ct)).Content, keys);
                    await analysis.SaveTriageAsync(s.ItemId, t, o.Llm.TriageModel, ct);
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached during triage");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "Triage of item {Item} failed; counted as marginal", s.ItemId);
                    t = new TriageResult { Relevance = 1, Reason = "triage failed" };
                }
            }

            triaged.Add((s, t));
        }

        var picked = triaged
            .Where(x => x.Triage.Relevance >= o.Caps.MinRelevance)
            .OrderByDescending(x => x.Triage.Relevance).ThenByDescending(x => x.Score.Score)
            .Take(o.Caps.Read)
            .ToList();

        var read = new List<(Scored Score, TriageResult Triage, ReadResult Read)>();
        foreach (var (s, t) in picked)
        {
            var r = await analysis.GetReadAsync(s.ItemId, ct);
            if (r is null)
            {
                var c = byId[s.ItemId];
                try
                {
                    var text = await FullTextAsync(c, ct);
                    var match = profileList.FirstOrDefault(p => p.Key == (t.Project ?? s.ProfileKey));
                    var (system, user) = Prompts.Read(about, profileList, match, c.Title, c.Url, c.FeedTitle, text, o.Caps.ReadChars);
                    r = LlmJson.Read((await llm.ChatAsync(o.Llm.ReadModel, system, user, ReadMaxTokens, "read", ct)).Content, keys);
                    if (r is not null)
                    {
                        await analysis.SaveReadAsync(s.ItemId, r, o.Llm.ReadModel, ct);
                    }
                }
                catch (BudgetExceededException)
                {
                    notes.Add("LLM budget reached; fewer highlights today");
                    break;
                }
                catch (HttpRequestException ex)
                {
                    logger.LogWarning(ex, "Reading item {Item} failed; left out of the digest", s.ItemId);
                }
            }

            if (r is not null)
            {
                read.Add((s, t, r));
            }
        }

        var ordered = read
            .OrderBy(x => x.Read.Suggestion is null ? 1 : 0)
            .ThenByDescending(x => x.Triage.Relevance)
            .ThenByDescending(x => x.Score.Score)
            .Select(x => x.Score.ItemId)
            .ToList();
        return new Selection(candidates.Count, triaged.Count, ordered, notes);
    }

    private async Task<string> FullTextAsync(Candidate c, CancellationToken ct)
    {
        if (c.Content.Length >= options.Value.Caps.ShortContentChars || c.MinifluxEntryId is not { } entryId)
        {
            return c.Content;
        }

        try
        {
            var text = HtmlText.ToPlain(await miniflux.FetchContentAsync(entryId, ct));
            if (text.Length <= c.Content.Length)
            {
                return c.Content;
            }

            await items.SetContentAsync(c.Id, text, ct);
            return text;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Full text for item {Item} unavailable; using the feed text", c.Id);
            return c.Content;
        }
    }

    private async Task SendAsync(DigestRow digest, CancellationToken ct)
    {
        var chat = options.Value.Telegram.AllowedUserId;
        var messages = await BuildMessagesAsync(digest, ct);
        for (var i = digest.SentCount; i < messages.Count; i++)
        {
            await telegram.SendAsync(chat, messages[i], ct);
            await digests.SetSentCountAsync(digest.LocalDate, i + 1, ct);
        }

        await digests.MarkSentAsync(digest.LocalDate, time.GetUtcNow(), ct);
    }

    private async Task<List<OutMessage>> BuildMessagesAsync(DigestRow d, CancellationToken ct)
    {
        var shown = await items.DigestItemsAsync(d.ItemIds, ct);
        var now = time.GetUtcNow();
        var votes = await feedback.VotesSinceAsync(now.AddDays(-1), ct);
        var spend = await usage.SpendSinceAsync(now.AddDays(-30), ct);
        var byProject = shown
            .GroupBy(v => v.Project ?? "other")
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.Count()))
            .ToList();
        var header = DigestFormatter.Header(new DigestHeader(
            DateOnly.ParseExact(d.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture), shown.Count, d.Candidates, byProject,
            votes.Up, votes.Down, spend, d.Note is null ? [] : d.Note.Split('\n')));
        return [header, .. shown.Select(v => DigestFormatter.Item(v, null, null))];
    }
}
```

`src/FeedEater/Digest/DigestJob.cs`:

```csharp
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Digest;

/// <summary>Runs the digest inside the 07:30–12:00 window; a failure is reported once on Telegram and retried with backoff.</summary>
public sealed class DigestJob(
    DigestRun run, DigestStore digests, TelegramClient telegram,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<DigestJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    protected override string Name => "digest";
    protected override string? DueKey(DateTime localNow) => Schedule.Window(localNow, Settings.DigestAt, Settings.DigestGiveUpAt);

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        try
        {
            await run.RunAsync(key, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var firstFailure = (await digests.GetAsync(key, ct))?.Error is null;
            await digests.MarkFailedAsync(key, ex.Message, ct);
            if (firstFailure)
            {
                await ReportAsync(key, ex.Message, ct);
            }

            throw;
        }
    }

    private async Task ReportAsync(string key, string error, CancellationToken ct)
    {
        var until = Settings.DigestGiveUpAt.ToString(@"hh\:mm", CultureInfo.InvariantCulture);
        try
        {
            await telegram.SendAsync(Settings.Telegram.AllowedUserId,
                new OutMessage($"Digest {key} failed: {WebUtility.HtmlEncode(error)}. Retrying until {until}."), ct);
        }
        catch (Exception notify) when (notify is HttpRequestException or TelegramException)
        {
            Logger.LogWarning(notify, "Could not report the digest failure on Telegram");
        }
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: daily digest with staged triage and reads, resumable sending"
```

---

### Task 12: Votes, ideas and the Telegram poller

**Files:**
- Create: `src/FeedEater/Plane/IdeaFiler.cs`, `src/FeedEater/Telegram/CallbackHandler.cs`, `src/FeedEater/Telegram/TelegramPoller.cs`
- Modify: `src/FeedEater/Storage/ItemStore.cs` (add `ItemView`, `GetAsync`)
- Test: `tests/FeedEater.Tests/FeedbackLoopTests.cs`

**Interfaces:**
- Consumes: `FeedbackStore`, `ProfileStore`, `PlaneClient.CreateIntakeAsync`, `TelegramClient`, `DigestFormatter.Buttons`, `CallbackData`.
- Produces: `ItemView { Id, Title, Url, Feed, PublishedAt, Content, ProfileKey, Relevance, Reason, Summary, Why, Kind, Project, Suggestion, Vote, FiledIn }`; `ItemStore.GetAsync(long, ct) -> ItemView?`; `IdeaFiler.FileAsync(long itemId, ct) -> string planeProject` (idempotent); `CallbackHandler.HandleAsync(TgCallback, ct)`; `TelegramPoller` (hosted loop) with internal `TickAsync(ct)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/FeedbackLoopTests.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Loops;
using FeedEater.Plane;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class FeedbackLoopTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<(string Method, string Body)> _telegram = [];
    private string _updates = """{"ok":true,"result":[]}""";
    private bool _editNotModified;

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static string Callback(long updateId, long from, string data) =>
        $$"""{"update_id":{{updateId}},"callback_query":{"id":"cb{{updateId}}","from":{"id":{{from}}},"message":{"message_id":7,"chat":{"id":42}},"data":"{{data}}"}}""";

    private (TelegramPoller Poller, StubHandler Plane) Build()
    {
        var options = Options.Create(new FeedEaterOptions { Telegram = new TelegramOptions { AllowedUserId = 42 } });
        var telegramStub = new StubHandler((request, body) =>
        {
            var method = request.RequestUri!.Segments[^1];
            _telegram.Add((method, body));
            return method switch
            {
                "getUpdates" => StubHandler.Json(Interlocked.Exchange(ref _updates, """{"ok":true,"result":[]}""")),
                "editMessageReplyMarkup" when _editNotModified => StubHandler.Json("""{"ok":false,"description":"Bad Request: message is not modified"}"""),
                _ => StubHandler.Json("""{"ok":true,"result":true}"""),
            };
        });
        var planeStub = new StubHandler((request, _) => request.Method == HttpMethod.Post
            ? StubHandler.Json("""{"issue":{"id":"issue-1"}}""", System.Net.HttpStatusCode.Created)
            : StubHandler.Json("""{"results":[{"id":"p-lab","identifier":"LAB"},{"id":"p-feed","identifier":"FEED"}]}"""));
        var telegram = new TelegramClient(telegramStub.Client("http://tg/botT/"));
        var items = new ItemStore(pg.Db);
        var feedback = new FeedbackStore(pg.Db);
        var filer = new IdeaFiler(items, feedback, new ProfileStore(pg.Db), new PlaneClient(planeStub.Client("http://plane/"), options), options, TimeProvider.System);
        var handler = new CallbackHandler(telegram, feedback, filer, items, options, NullLogger<CallbackHandler>.Instance);
        var poller = new TelegramPoller(telegram, handler, new CursorStore(pg.Db), new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<TelegramPoller>.Instance);
        return (poller, planeStub);
    }

    private async Task<long> SeedReadItemAsync(string? suggestion)
    {
        await new ProfileStore(pg.Db).ReplaceAllAsync(
            [new Profile { Key = "homelab", Kind = "project", PlaneIdentifier = "LAB", Description = "VPS.", Embedding = TestVectors.OneHot(0) }],
            DateTimeOffset.UtcNow, default);
        var id = await Seed.ItemAsync(pg, 1, "Backup tool", TestVectors.OneHot(0));
        await new AnalysisStore(pg.Db).SaveReadAsync(id, new ReadResult
        {
            Summary = "S.", Why = "W.", Kind = suggestion is null ? "fyi" : "improve", Project = "homelab", Suggestion = suggestion,
        }, "m", default);
        return id;
    }

    private string Answers() => string.Join('\n', _telegram.Where(t => t.Method == "answerCallbackQuery").Select(t => t.Body));

    [Fact]
    public async Task A_vote_is_stored_marked_on_the_buttons_and_the_offset_advances()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"v:{id}:u")}}]}""";

        await poller.TickAsync(default);

        Assert.Equal(1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        var markup = JsonSerializer.Deserialize<JsonElement>(_telegram.Single(t => t.Method == "editMessageReplyMarkup").Body);
        Assert.Equal("👍 ✓", markup.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("text").GetString());
        Assert.Equal("11", await new CursorStore(pg.Db).GetAsync("telegram:offset", default));
    }

    [Fact]
    public async Task Someone_else_pressing_a_button_changes_nothing()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, _) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 999, $"v:{id}:u")}}]}""";

        await poller.TickAsync(default);

        Assert.Null((await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
        Assert.Contains("Not for you.", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_taps_on_the_idea_button_file_one_plane_item_in_the_matched_project()
    {
        var id = await SeedReadItemAsync("Try it.");
        var (poller, plane) = Build();
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}},{{Callback(11, 42, $"i:{id}")}}]}""";

        await poller.TickAsync(default);

        var post = Assert.Single(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("/projects/p-lab/intake-issues/", post.Uri, StringComparison.Ordinal);
        var item = (await new ItemStore(pg.Db).GetAsync(id, default))!;
        Assert.Equal("LAB", item.FiledIn);
        Assert.Equal(1, item.Vote);
        Assert.Contains("Filed in LAB", Answers(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_item_without_a_suggestion_is_not_filed_and_an_unchanged_keyboard_is_fine()
    {
        var id = await SeedReadItemAsync(null);
        var (poller, plane) = Build();
        _editNotModified = true;
        _updates = $$"""{"ok":true,"result":[{{Callback(10, 42, $"i:{id}")}},{{Callback(11, 42, $"v:{id}:d")}}]}""";

        await poller.TickAsync(default);

        Assert.DoesNotContain(plane.Calls, c => c.Method == HttpMethod.Post);
        Assert.Contains("Not filed: this item has no suggestion to file", Answers(), StringComparison.Ordinal);
        Assert.Equal(-1, (await new ItemStore(pg.Db).GetAsync(id, default))!.Vote);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~FeedbackLoopTests"`
Expected: build FAILS with `The type or namespace name 'IdeaFiler' could not be found`.

- [ ] **Step 3: Add the item view to the item store**

Add next to `Candidate` in `src/FeedEater/Storage/ItemStore.cs`:

```csharp
public sealed record ItemView
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string Content { get; init; } = "";
    public string? ProfileKey { get; init; }
    public int? Relevance { get; init; }
    public string? Reason { get; init; }
    public string? Summary { get; init; }
    public string? Why { get; init; }
    public string? Kind { get; init; }
    public string? Project { get; init; }
    public string? Suggestion { get; init; }
    public int? Vote { get; init; }
    public string? FiledIn { get; init; }
}
```

and inside `ItemStore`:

```csharp
    public async Task<ItemView?> GetAsync(long id, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ItemView>(new CommandDefinition(
            """
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, i.published_at, i.content, i.profile_key,
                   t.relevance::int as relevance, t.reason, r.summary, r.why, coalesce(r.kind, t.kind) as kind,
                   coalesce(r.project, t.project) as project, r.suggestion, v.value::int as vote, d.plane_project as filed_in
            from items i
            left join feeds f on f.id = i.feed_id
            left join triage t on t.item_id = i.id
            left join reads r on r.item_id = i.id
            left join votes v on v.item_id = i.id
            left join ideas d on d.item_id = i.id
            where i.id = @id
            """, new { id }, cancellationToken: ct));
    }
```

- [ ] **Step 4: Write the idea filer, callback handler and poller**

`src/FeedEater/Plane/IdeaFiler.cs`:

```csharp
using System.Net;
using Microsoft.Extensions.Options;
using FeedEater.Storage;

namespace FeedEater.Plane;

/// <summary>Turns an item's suggestion into a Plane Intake item in the matched project's Plane project, else FEED. Once per item.</summary>
public sealed class IdeaFiler(
    ItemStore items, FeedbackStore feedback, ProfileStore profiles, PlaneClient plane, IOptions<FeedEaterOptions> options, TimeProvider time)
{
    private const int MaxTitle = 80;

    public async Task<string> FileAsync(long itemId, CancellationToken ct)
    {
        if (await feedback.GetIdeaAsync(itemId, ct) is { } existing)
        {
            return existing.PlaneProject;
        }

        var item = await items.GetAsync(itemId, ct) ?? throw new InvalidOperationException($"item {itemId} does not exist");
        if (item.Suggestion is null)
        {
            throw new InvalidOperationException("this item has no suggestion to file");
        }

        var project = (await profiles.AllAsync(ct)).FirstOrDefault(p => p.Key == item.Project)?.PlaneIdentifier
            ?? options.Value.Plane.FallbackProject;
        var title = item.Suggestion.Length <= MaxTitle ? item.Suggestion : item.Suggestion[..(MaxTitle - 1)] + "…";
        var html = $"<p>{E(item.Suggestion)}</p><p>{E(item.Why ?? "")}</p>"
            + $"<p>Source: <a href=\"{E(item.Url)}\">{E(item.Title)}</a> ({E(item.Feed)}, feed-eater item {item.Id})</p>";
        var issueId = await plane.CreateIntakeAsync(project, title, html, ct);
        await feedback.AddIdeaAsync(new Idea { ItemId = itemId, PlaneProject = project, PlaneIssueId = issueId, Title = title, At = time.GetUtcNow().UtcDateTime }, ct);
        return project;
    }

    private static string E(string text) => WebUtility.HtmlEncode(text);
}
```

`src/FeedEater/Telegram/CallbackHandler.cs`:

```csharp
using Microsoft.Extensions.Options;
using FeedEater.Digest;
using FeedEater.Plane;
using FeedEater.Storage;

namespace FeedEater.Telegram;

public sealed class CallbackHandler(
    TelegramClient telegram, FeedbackStore feedback, IdeaFiler ideas, ItemStore items,
    IOptions<FeedEaterOptions> options, ILogger<CallbackHandler> logger)
{
    public async Task HandleAsync(TgCallback callback, CancellationToken ct)
    {
        if (callback.FromId != options.Value.Telegram.AllowedUserId)
        {
            await telegram.AnswerAsync(callback.Id, "Not for you.", ct);
            return;
        }

        switch (CallbackData.Parse(callback.Data))
        {
            case VoteCallback vote:
                await feedback.SetVoteAsync(vote.ItemId, vote.Value, ct);
                await RefreshButtonsAsync(callback, vote.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, vote.Value > 0 ? "👍 saved" : "👎 saved", ct);
                break;

            case IdeaCallback idea:
                string project;
                try
                {
                    project = await ideas.FileAsync(idea.ItemId, ct);
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Filing item {Item} failed", idea.ItemId);
                    await telegram.AnswerAsync(callback.Id, Clip($"Not filed: {ex.Message}"), ct);
                    return;
                }

                await feedback.SetVoteAsync(idea.ItemId, 1, ct);
                await RefreshButtonsAsync(callback, idea.ItemId, ct);
                await telegram.AnswerAsync(callback.Id, $"Filed in {project}", ct);
                break;

            default:
                await telegram.AnswerAsync(callback.Id, null, ct);
                break;
        }
    }

    private async Task RefreshButtonsAsync(TgCallback callback, long itemId, CancellationToken ct)
    {
        if (await items.GetAsync(itemId, ct) is not { } item)
        {
            return;
        }

        try
        {
            await telegram.EditButtonsAsync(callback.ChatId, callback.MessageId,
                DigestFormatter.Buttons(itemId, item.Suggestion is not null, (short?)item.Vote, item.FiledIn), ct);
        }
        catch (TelegramException ex) when (ex.Message.Contains("not modified", StringComparison.OrdinalIgnoreCase))
        {
            // Same vote pressed twice: the keyboard already shows it.
        }
    }

    private static string Clip(string text) => text.Length <= 190 ? text : text[..189] + "…";
}
```

`src/FeedEater/Telegram/TelegramPoller.cs`:

```csharp
using System.Globalization;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Telegram;

/// <summary>
/// Long-polls button presses. Updates are handled one at a time, so a double tap on 💡 sees the first filing.
/// The offset advances past a failing update so one bad callback cannot block the rest.
/// </summary>
public sealed class TelegramPoller(
    TelegramClient telegram, CallbackHandler handler, CursorStore cursors, LoopHealth health, TimeProvider time, ILogger<TelegramPoller> logger)
    : PollingLoop(health, time, logger)
{
    private const string Cursor = "telegram:offset";
    private const int LongPollSeconds = 50;

    protected override string Name => "telegram";
    protected override TimeSpan Interval => TimeSpan.FromSeconds(1);

    protected override async Task PollAsync(CancellationToken ct)
    {
        var offset = long.TryParse(await cursors.GetAsync(Cursor, ct), NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;
        foreach (var update in await telegram.GetUpdatesAsync(offset, LongPollSeconds, ct))
        {
            if (update.Callback is { } callback)
            {
                try
                {
                    await handler.HandleAsync(callback, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    Logger.LogError(ex, "Callback {Data} failed", callback.Data);
                }
            }

            await cursors.SetAsync(Cursor, (update.UpdateId + 1).ToString(CultureInfo.InvariantCulture), ct);
        }
    }

    internal Task TickAsync(CancellationToken ct) => PollAsync(ct);
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: votes and Plane ideas from Telegram buttons"
```

---

### Task 13: Karakeep and GitHub-star signals

**Files:**
- Create: `src/FeedEater/Signals/KarakeepClient.cs`, `src/FeedEater/Signals/GitHubStarsClient.cs`, `src/FeedEater/Signals/SignalJob.cs`, `src/FeedEater/Storage/SignalStore.cs`
- Test: `tests/FeedEater.Tests/SignalJobTests.cs`

**Interfaces:**
- Consumes: `ScheduledJob`, `Schedule.LatestDaily`, `LiteLlmClient.EmbedAsync`.
- Produces: `Bookmark(Id, CreatedAt, Url, Title, Description)`, `KarakeepClient(HttpClient).PageAsync(string? cursor, ct) -> (IReadOnlyList<Bookmark> Items, string? Next)`; `Star(FullName, Url, Description, Topics, StarredAt)`, `GitHubStarsClient(HttpClient, IOptions<FeedEaterOptions>).PageAsync(int page, ct)`; `NewSignal(Source, ExternalId, Url, Title, At)`, `SignalStore(FeedDb)` with `ExistsAsync(source, externalId, ct)`, `AddAsync(NewSignal, float[], ct)`; `SignalJob` (scheduled daily 02:00) with internal `CollectAsync(ct) -> int`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/SignalJobTests.cs`:

```csharp
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Signals;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SignalJobTests(PostgresFixture pg) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static StubHandler Karakeep() => new((request, _) => StubHandler.Json(request.RequestUri!.Query.Contains("cursor=c2", StringComparison.Ordinal)
        ? """{"bookmarks":[{"id":"b3","createdAt":"2026-10-01T10:00:00Z","title":null,"content":{"type":"link","url":"https://c.example","title":"Third","description":"desc"}}],"nextCursor":null}"""
        : """
          {"bookmarks":[
            {"id":"b1","createdAt":"2026-10-04T10:00:00Z","title":"First","content":{"type":"link","url":"https://a.example","title":"First","description":null}},
            {"id":"b2","createdAt":"2026-10-03T10:00:00Z","title":"A note","content":{"type":"text","text":"just text"}}
          ],"nextCursor":"c2"}
          """));

    private static StubHandler GitHub() => new((request, _) => StubHandler.Json(request.RequestUri!.Query.EndsWith("&page=1", StringComparison.Ordinal)
        ? """[{"starred_at":"2026-10-02T08:00:00Z","repo":{"full_name":"pgvector/pgvector","html_url":"https://github.com/pgvector/pgvector","description":"Vector search for Postgres","topics":["postgres","vectors"]}}]"""
        : "[]"));

    private SignalJob Build(StubHandler karakeep, StubHandler github, StubHandler llm)
    {
        var options = Options.Create(new FeedEaterOptions { Karakeep = new KarakeepOptions { Token = "k" } });
        return new SignalJob(
            new KarakeepClient(karakeep.Client("http://karakeep/")), new GitHubStarsClient(github.Client("http://github/"), options),
            new SignalStore(pg.Db), new LiteLlmClient(llm.Client("http://llm/"), new UsageStore(pg.Db), options),
            new CursorStore(pg.Db), options, new LoopHealth(TimeProvider.System), TimeProvider.System, NullLogger<SignalJob>.Instance);
    }

    [Fact]
    public async Task Collects_link_bookmarks_and_stars_then_stops_at_known_ones()
    {
        var llm = new StubHandler((_, body) => StubHandler.Json(TestVectors.EmbeddingResponse(TestVectors.InputCount(body))));
        var karakeep = Karakeep();
        var job = Build(karakeep, GitHub(), llm);

        Assert.Equal(3, await job.CollectAsync(default));
        var callsAfterFirst = karakeep.Calls.Count;
        Assert.Equal(0, await job.CollectAsync(default));

        await using var c = await pg.Db.DataSource.OpenConnectionAsync();
        var ids = (await c.QueryAsync<string>("select external_id from signals order by external_id")).ToList();
        Assert.Equal(["b1", "b3", "pgvector/pgvector"], ids);
        Assert.Equal(callsAfterFirst + 1, karakeep.Calls.Count);   // second run stops on the first page at b1
        Assert.Contains("pgvector/pgvector: Vector search for Postgres postgres vectors", llm.Calls[0].Body, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~SignalJobTests"`
Expected: build FAILS with `The type or namespace name 'Signals' does not exist`.

- [ ] **Step 3: Write the clients and store**

`src/FeedEater/Signals/KarakeepClient.cs`:

```csharp
using System.Text.Json;

namespace FeedEater.Signals;

public sealed record Bookmark(string Id, DateTimeOffset CreatedAt, string? Url, string Title, string? Description);

public sealed class KarakeepClient(HttpClient http)
{
    public async Task<(IReadOnlyList<Bookmark> Items, string? Next)> PageAsync(string? cursor, CancellationToken ct)
    {
        var query = cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor);
        var body = Json.Parse(await http.GetStringAsync($"api/v1/bookmarks?limit=100&sortOrder=desc{query}", ct));
        var items = body.GetProperty("bookmarks").EnumerateArray().Select(b =>
        {
            var content = b.TryGetProperty("content", out var c) ? c : default;
            var url = Str(content, "url");
            return new Bookmark(
                b.GetProperty("id").GetString()!,
                b.GetProperty("createdAt").GetDateTimeOffset(),
                Str(content, "type") == "link" ? url : null,
                Str(b, "title") ?? Str(content, "title") ?? url ?? "",
                Str(content, "description"));
        }).ToList();
        var next = body.TryGetProperty("nextCursor", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        return (items, next);
    }

    private static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

`src/FeedEater/Signals/GitHubStarsClient.cs`:

```csharp
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FeedEater.Signals;

public sealed record Star(string FullName, string Url, string? Description, IReadOnlyList<string> Topics, DateTimeOffset StarredAt);

/// <summary>Public stars, unauthenticated (60 requests an hour is plenty for one run a day).</summary>
public sealed class GitHubStarsClient(HttpClient http, IOptions<FeedEaterOptions> options)
{
    public async Task<IReadOnlyList<Star>> PageAsync(int page, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"users/{options.Value.GitHub.User}/starred?sort=created&direction=desc&per_page=100&page={page}");
        request.Headers.Accept.ParseAdd("application/vnd.github.star+json");
        using var response = await http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = Json.Parse(await response.Content.ReadAsStringAsync(ct));
        return body.EnumerateArray().Select(s =>
        {
            var repo = s.GetProperty("repo");
            return new Star(
                repo.GetProperty("full_name").GetString()!,
                repo.GetProperty("html_url").GetString()!,
                repo.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null,
                repo.TryGetProperty("topics", out var t) && t.ValueKind == JsonValueKind.Array ? t.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
                s.GetProperty("starred_at").GetDateTimeOffset());
        }).ToList();
    }
}
```

`src/FeedEater/Storage/SignalStore.cs`:

```csharp
using Dapper;

namespace FeedEater.Storage;

public sealed record NewSignal(string Source, string ExternalId, string? Url, string Title, DateTime At);

public sealed class SignalStore(FeedDb db)
{
    public async Task<bool> ExistsAsync(string source, string externalId, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return await c.ExecuteScalarAsync<bool>(new CommandDefinition(
            "select exists (select 1 from signals where source = @source and external_id = @externalId)",
            new { source, externalId }, cancellationToken: ct));
    }

    public async Task AddAsync(NewSignal signal, float[] embedding, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        await c.ExecuteAsync(new CommandDefinition(
            """
            insert into signals (source, external_id, url, title, embedding, polarity, at)
            values (@Source, @ExternalId, @Url, @Title, @embedding::real[]::vector, 1, @At)
            on conflict (source, external_id) do nothing
            """,
            new { signal.Source, signal.ExternalId, signal.Url, signal.Title, embedding, signal.At }, cancellationToken: ct));
    }
}
```

- [ ] **Step 4: Write the job**

`src/FeedEater/Signals/SignalJob.cs`:

```csharp
using Microsoft.Extensions.Options;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Signals;

/// <summary>Daily at 02:00: new Karakeep link saves and GitHub stars become positive examples for ranking.</summary>
public sealed class SignalJob(
    KarakeepClient karakeep, GitHubStarsClient github, SignalStore signals, LiteLlmClient llm,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<SignalJob> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private const int MaxPages = 5;

    protected override string Name => "signals";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestDaily(localNow, new TimeSpan(2, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct) =>
        Logger.LogInformation("Collected {Count} new signals", await CollectAsync(ct));

    internal async Task<int> CollectAsync(CancellationToken ct)
    {
        var fresh = new List<(NewSignal Signal, string Text)>();
        if (Settings.Karakeep.Token.Length > 0)
        {
            fresh.AddRange(await NewBookmarksAsync(ct));
        }

        fresh.AddRange(await NewStarsAsync(ct));
        foreach (var chunk in fresh.Chunk(Settings.Llm.EmbedBatch))
        {
            var vectors = await llm.EmbedAsync(chunk.Select(c => c.Text).ToList(), "signal", ct);
            for (var i = 0; i < chunk.Length; i++)
            {
                await signals.AddAsync(chunk[i].Signal, vectors[i], ct);
            }
        }

        return fresh.Count;
    }

    /// <summary>Newest first until a stored one; notes and images have no URL and are skipped.</summary>
    private async Task<List<(NewSignal, string)>> NewBookmarksAsync(CancellationToken ct)
    {
        var found = new List<(NewSignal, string)>();
        string? cursor = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var (items, next) = await karakeep.PageAsync(cursor, ct);
            foreach (var b in items)
            {
                if (await signals.ExistsAsync("karakeep", b.Id, ct))
                {
                    return found;
                }

                if (b.Url is not null)
                {
                    found.Add((new NewSignal("karakeep", b.Id, b.Url, b.Title, b.CreatedAt.UtcDateTime), $"{b.Title}\n{b.Description}".Trim()));
                }
            }

            if (next is null)
            {
                return found;
            }

            cursor = next;
        }

        return found;
    }

    private async Task<List<(NewSignal, string)>> NewStarsAsync(CancellationToken ct)
    {
        var found = new List<(NewSignal, string)>();
        for (var page = 1; page <= MaxPages; page++)
        {
            var stars = await github.PageAsync(page, ct);
            foreach (var s in stars)
            {
                if (await signals.ExistsAsync("github_star", s.FullName, ct))
                {
                    return found;
                }

                found.Add((new NewSignal("github_star", s.FullName, s.Url, s.FullName, s.StarredAt.UtcDateTime),
                    $"{s.FullName}: {s.Description} {string.Join(' ', s.Topics)}".Trim()));
            }

            if (stars.Count < 100)
            {
                return found;
            }
        }

        return found;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: Karakeep saves and GitHub stars as positive signals"
```

---
### Task 14: MCP tools, search and composition

**Files:**
- Create: `src/FeedEater/Mcp/McpExtensions.cs`, `src/FeedEater/Mcp/FeedTools.cs`
- Modify: `src/FeedEater/Storage/ItemStore.cs` (add `SearchHit`, `SearchAsync`), `src/FeedEater/ServiceRegistration.cs` (replace), `src/FeedEater/Program.cs` (replace)
- Test: `tests/FeedEater.Tests/SearchTests.cs`, `tests/FeedEater.Tests/McpTests.cs`

**Interfaces:**
- Consumes: every store and client above.
- Produces: `SearchHit { Id, Title, Url, Feed, PublishedAt, Summary, Project, Kind, Vote, Rank }`; `ItemStore.SearchAsync(float[]? vector, string text, string? project, string? kind, DateTimeOffset? from, DateTimeOffset? to, int limit, ct)`; MCP tools `feed_search`, `feed_read`, `feed_digests`, `feed_digest`, `feed_ideas`; `AddFeedEaterMcp()`, `MapFeedEaterMcp(token)`; the final `AddFeedEater` wiring.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/SearchTests.cs`:

```csharp
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class SearchTests(PostgresFixture pg) : IAsyncLifetime
{
    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Hybrid_search_returns_meaning_matches_and_keyword_matches()
    {
        var store = new ItemStore(pg.Db);
        var byMeaning = await Seed.ItemAsync(pg, 1, "Tuning approximate nearest neighbour indexes", TestVectors.OneHot(1));
        var byWord = await Seed.ItemAsync(pg, 1, "HNSW in pgvector 0.8", TestVectors.OneHot(2));
        await Seed.ItemAsync(pg, 1, "Unrelated", TestVectors.OneHot(3));

        var both = await store.SearchAsync(TestVectors.OneHot(1), "hnsw", null, null, null, null, 2, default);
        var wordsOnly = await store.SearchAsync(null, "hnsw", null, null, null, null, 10, default);

        Assert.Equal([byMeaning, byWord], both.Select(h => h.Id).Order());
        Assert.Equal([byWord], wordsOnly.Select(h => h.Id));
    }

    [Fact]
    public async Task Filters_by_publication_range()
    {
        var store = new ItemStore(pg.Db);
        await Seed.ItemAsync(pg, 1, "hnsw old", TestVectors.OneHot(1), new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var recent = await Seed.ItemAsync(pg, 1, "hnsw new", TestVectors.OneHot(1), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));

        var hits = await store.SearchAsync(null, "hnsw", null, null, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), null, 10, default);

        Assert.Equal([recent], hits.Select(h => h.Id));
    }
}
```

`tests/FeedEater.Tests/McpTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using FeedEater.Llm;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class McpTests(PostgresFixture pg) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _app = null!;

    public async Task InitializeAsync()
    {
        await pg.ResetAsync();
        LiteLlmClient.RetryDelay = TimeSpan.Zero;
        _app = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("ConnectionStrings:FeedEater", pg.ConnectionString)
            .UseSetting("Mcp:Token", "test-token")
            .UseSetting("FeedEater:RunJobs", "false")
            .UseSetting("FeedEater:Llm:BaseUrl", "http://127.0.0.1:9/"));   // nothing listens: search must fall back to keywords
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    private static HttpRequestMessage Rpc(string method, string paramsJson, string? token, string? origin = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent($$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{paramsJson}}}""", Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (origin is not null)
        {
            request.Headers.Add("Origin", origin);
        }

        return request;
    }

    private async Task<string> CallAsync(string tool, string args) =>
        await (await _app.CreateClient().SendAsync(Rpc("tools/call", $$"""{"name":"{{tool}}","arguments":{{args}}}""", "test-token"))).Content.ReadAsStringAsync();

    [Fact]
    public async Task Rejects_missing_token_wrong_token_and_browser_origin()
    {
        var http = _app.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", null))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(Rpc("tools/list", "{}", "nope"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(Rpc("tools/list", "{}", "test-token", "https://evil.example"))).StatusCode);
    }

    [Fact]
    public async Task Lists_the_five_tools()
    {
        var body = await (await _app.CreateClient().SendAsync(Rpc("tools/list", "{}", "test-token"))).Content.ReadAsStringAsync();

        foreach (var tool in new[] { "feed_search", "feed_read", "feed_digests", "feed_digest", "feed_ideas" })
        {
            Assert.Contains(tool, body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Search_answers_from_keywords_when_embeddings_are_unavailable_and_read_returns_the_text()
    {
        var id = await Seed.ItemAsync(pg, 1, "pgvector HNSW tuning", TestVectors.OneHot(1), content: "Set ef_search higher.");

        var search = await CallAsync("feed_search", """{"query":"hnsw"}""");
        var read = await CallAsync("feed_read", $$"""{"id":{{id}}}""");

        Assert.Contains("pgvector HNSW tuning", search, StringComparison.Ordinal);
        Assert.Contains("Set ef_search higher.", read, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bad_arguments_are_explained()
    {
        Assert.Contains("no item", await CallAsync("feed_read", """{"id":999}"""), StringComparison.Ordinal);
        Assert.Contains("yyyy-MM-dd", await CallAsync("feed_digest", """{"date":"5 Oct"}"""), StringComparison.Ordinal);
        Assert.Contains("query is empty", await CallAsync("feed_search", """{"query":" "}"""), StringComparison.Ordinal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~SearchTests|FullyQualifiedName~McpTests"`
Expected: build FAILS with `'ItemStore' does not contain a definition for 'SearchAsync'`.

- [ ] **Step 3: Add search to the item store**

Add next to `ItemView` in `src/FeedEater/Storage/ItemStore.cs`:

```csharp
public sealed record SearchHit
{
    public long Id { get; init; }
    public string Title { get; init; } = "";
    public string Url { get; init; } = "";
    public string Feed { get; init; } = "";
    public DateTime PublishedAt { get; init; }
    public string? Summary { get; init; }
    public string? Project { get; init; }
    public string? Kind { get; init; }
    public int? Vote { get; init; }
    public double Rank { get; init; }
}
```

and inside `ItemStore`:

```csharp
    /// <summary>
    /// Top 50 by vector distance and top 50 by full-text rank, merged by reciprocal rank fusion (k = 60).
    /// A null vector searches keywords only.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        float[]? vector, string text, string? project, string? kind, DateTimeOffset? from, DateTimeOffset? to, int limit, CancellationToken ct)
    {
        const string Filter =
            """
            i.duplicate_of is null
            and (@project::text is null or i.profile_key = @project
                 or exists (select 1 from reads r where r.item_id = i.id and r.project = @project))
            and (@kind::text is null or exists (select 1 from reads r where r.item_id = i.id and r.kind = @kind))
            and (@from::timestamptz is null or i.published_at >= @from)
            and (@to::timestamptz is null or i.published_at < @to)
            """;
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        return (await c.QueryAsync<SearchHit>(new CommandDefinition(
            $"""
            with v as (
                select id, row_number() over (order by dist) as pos from (
                    select i.id, i.embedding <=> @vector::real[]::vector as dist from items i
                    where @vector::real[] is not null and i.embedding is not null and {Filter}
                    order by dist limit 50) a
            ),
            t as (
                select id, row_number() over (order by score desc) as pos from (
                    select i.id, ts_rank(i.search, q) as score from items i, websearch_to_tsquery('simple', @text) q
                    where i.search @@ q and {Filter}
                    order by score desc limit 50) b
            ),
            fused as (
                select id, sum(1.0 / (60 + pos))::float8 as rrf from (select * from v union all select * from t) u group by id
            )
            select i.id, i.title, i.url, coalesce(f.title, '') as feed, i.published_at, r.summary,
                   coalesce(r.project, i.profile_key) as project, r.kind, vt.value::int as vote, fused.rrf as rank
            from fused
            join items i on i.id = fused.id
            left join feeds f on f.id = i.feed_id
            left join reads r on r.item_id = i.id
            left join votes vt on vt.item_id = i.id
            order by fused.rrf desc
            limit @limit
            """,
            new { vector, text, project, kind, from = from?.UtcDateTime, to = to?.UtcDateTime, limit }, cancellationToken: ct))).ToList();
    }
```

- [ ] **Step 4: Write the MCP layer**

`src/FeedEater/Mcp/McpExtensions.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;

namespace FeedEater.Mcp;

public static class McpExtensions
{
    public static IServiceCollection AddFeedEaterMcp(this IServiceCollection services)
    {
        // Stateless: no session, no GET stream.
        services.AddMcpServer().WithHttpTransport(options => options.Stateless = true).WithTools<FeedTools>();
        return services;
    }

    /// <summary>
    /// Bearer token on every request, fail closed when none is configured, and no Origin header: a browser always
    /// sends one, so this is the DNS-rebinding guard.
    /// </summary>
    public static IEndpointRouteBuilder MapFeedEaterMcp(this IEndpointRouteBuilder app, string? token)
    {
        var expected = Encoding.UTF8.GetBytes($"Bearer {token}");
        app.MapMcp("/mcp").Add(builder =>
        {
            var next = builder.RequestDelegate!;
            builder.RequestDelegate = context =>
            {
                if (context.Request.Headers.ContainsKey("Origin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }

                var given = Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString());
                if (string.IsNullOrEmpty(token) || !CryptographicOperations.FixedTimeEquals(given, expected))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                }

                return next(context);
            };
        });
        return app;
    }
}
```

`src/FeedEater/Mcp/FeedTools.cs`:

```csharp
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using FeedEater.Llm;
using FeedEater.Storage;

namespace FeedEater.Mcp;

[McpServerToolType]
public sealed class FeedTools(ItemStore items, DigestStore digests, FeedbackStore feedback, LiteLlmClient llm)
{
    [McpServerTool(Name = "feed_search", ReadOnly = true)]
    [Description("Search Yehor's research archive: every item from his feeds (tech news, blogs, releases, Ukrainian tech) since 2023, with an AI summary where one was written. Matches by meaning and by keywords. Optional filters: project or topic key, kind (improve, new, fyi), published range. Returns id, title, url, feed, date, summary, project, kind, his vote.")]
    public async Task<string> SearchAsync(
        [Description("What to look for, in words.")] string query,
        [Description("Project or topic key, e.g. homelab, chargehand, postgres.")] string? project = null,
        [Description("improve, new or fyi.")] string? kind = null,
        [Description("Published at or after, ISO 8601.")] DateTimeOffset? from = null,
        [Description("Published before, ISO 8601.")] DateTimeOffset? to = null,
        [Description("At most this many results, 1 to 50; default 20.")] int limit = 20,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            throw new McpProtocolException("query is empty.", McpErrorCode.InvalidParams);
        }

        float[]? vector = null;
        try
        {
            vector = (await llm.EmbedAsync([query], "search", ct))[0];
        }
        catch (Exception ex) when (ex is HttpRequestException or BudgetExceededException)
        {
            // Keyword search still answers.
        }

        return Json(await items.SearchAsync(vector, query, project, kind, from, to, Math.Clamp(limit, 1, 50), ct));
    }

    [McpServerTool(Name = "feed_read", ReadOnly = true)]
    [Description("One archived item in full: text, triage verdict, AI summary and suggestion, Yehor's vote, and the Plane project it was filed in.")]
    public async Task<string> ReadAsync([Description("Item id from feed_search or feed_digest.")] long id, CancellationToken ct = default) =>
        Json(await items.GetAsync(id, ct) ?? throw new McpProtocolException($"no item with id {id}.", McpErrorCode.InvalidParams));

    [McpServerTool(Name = "feed_digests", ReadOnly = true)]
    [Description("Daily digests, newest first: date, status, how many items were candidates, triaged and shown, with item ids.")]
    public async Task<string> DigestsAsync(
        [Description("Only digests before this date, yyyy-MM-dd.")] string? before = null,
        [Description("At most this many, 1 to 30; default 10.")] int limit = 10,
        CancellationToken ct = default) =>
        Json(await digests.ListAsync(before is null ? null : Date(before), Math.Clamp(limit, 1, 30), ct));

    [McpServerTool(Name = "feed_digest", ReadOnly = true)]
    [Description("One day's digest with its highlights in order: title, url, feed, project, kind, summary, why, suggestion.")]
    public async Task<string> DigestAsync([Description("yyyy-MM-dd")] string date, CancellationToken ct = default)
    {
        var digest = await digests.GetAsync(Date(date), ct) ?? throw new McpProtocolException($"no digest for {date}.", McpErrorCode.InvalidParams);
        return Json(new { digest, items = await items.DigestItemsAsync(digest.ItemIds, ct) });
    }

    [McpServerTool(Name = "feed_ideas", ReadOnly = true)]
    [Description("Ideas Yehor filed from digests into Plane Intake, newest first, with the Plane project and the source item id.")]
    public async Task<string> IdeasAsync(
        [Description("Plane project identifier, e.g. LAB, SKAR, FEED.")] string? planeProject = null,
        [Description("At most this many, 1 to 50; default 20.")] int limit = 20,
        CancellationToken ct = default) =>
        Json(await feedback.IdeasAsync(planeProject, Math.Clamp(limit, 1, 50), ct));

    private static string Date(string text) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : throw new McpProtocolException("dates are yyyy-MM-dd.", McpErrorCode.InvalidParams);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, FeedEater.Json.Options);
}
```

- [ ] **Step 5: Replace the composition root**

`src/FeedEater/ServiceRegistration.cs`:

```csharp
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using Npgsql;
using FeedEater.Digest;
using FeedEater.Ingest;
using FeedEater.Llm;
using FeedEater.Loops;
using FeedEater.Mcp;
using FeedEater.Plane;
using FeedEater.Profiles;
using FeedEater.Signals;
using FeedEater.Storage;
using FeedEater.Telegram;

namespace FeedEater;

public static class ServiceRegistration
{
    public static IServiceCollection AddFeedEater(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("FeedEater")
            ?? throw new InvalidOperationException("ConnectionStrings:FeedEater is not set.");
        var section = configuration.GetSection(FeedEaterOptions.Section);
        var settings = section.Get<FeedEaterOptions>() ?? new FeedEaterOptions();

        services.Configure<FeedEaterOptions>(section);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new DatabaseMigrator(connectionString, sp.GetRequiredService<ILogger<DatabaseMigrator>>()));
        services.AddSingleton(new FeedDb(NpgsqlDataSource.Create(connectionString)));
        services.AddSingleton<CursorStore>();
        services.AddSingleton<ItemStore>();
        services.AddSingleton<ProfileStore>();
        services.AddSingleton<FeedbackStore>();
        services.AddSingleton<AnalysisStore>();
        services.AddSingleton<DigestStore>();
        services.AddSingleton<SignalStore>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<IUsageSink>(sp => sp.GetRequiredService<UsageStore>());
        services.AddSingleton<LoopHealth>();

        services.AddHttpClient<LiteLlmClient>((sp, http) =>
        {
            var o = Settings(sp).Llm;
            http.BaseAddress = new Uri(o.BaseUrl);
            http.Timeout = TimeSpan.FromSeconds(120);
            Bearer(http, o.ApiKey);
        });
        services.AddHttpClient<MinifluxClient>((sp, http) =>
        {
            var o = Settings(sp).Miniflux;
            http.BaseAddress = new Uri(o.BaseUrl);
            http.DefaultRequestHeaders.Add("X-Auth-Token", o.Token);
        });
        services.AddHttpClient<TelegramClient>((sp, http) =>
        {
            var o = Settings(sp).Telegram;
            http.BaseAddress = new Uri($"{o.BaseUrl}bot{o.Token}/");
            http.Timeout = TimeSpan.FromSeconds(70);   // above the 50 s long poll
        });
        services.AddHttpClient<PlaneClient>((sp, http) =>
        {
            var o = Settings(sp).Plane;
            http.BaseAddress = new Uri(o.BaseUrl);
            http.DefaultRequestHeaders.Add("X-API-Key", o.Token);
        });
        services.AddHttpClient<KarakeepClient>((sp, http) =>
        {
            var o = Settings(sp).Karakeep;
            http.BaseAddress = new Uri(o.BaseUrl);
            Bearer(http, o.Token);
        });
        services.AddHttpClient<GitHubStarsClient>((sp, http) =>
        {
            http.BaseAddress = new Uri(Settings(sp).GitHub.BaseUrl);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("feed-eater/0.1");
        });

        services.AddSingleton<DigestRun>();
        services.AddSingleton<IdeaFiler>();
        services.AddSingleton<CallbackHandler>();

        if (settings.RunJobs)
        {
            services.AddHostedService<Ingestor>();
            services.AddHostedService<ProfileBuilder>();
            services.AddHostedService<SignalJob>();
            services.AddHostedService<DigestJob>();
            if (settings.Telegram.Token.Length > 0)
            {
                services.AddHostedService<TelegramPoller>();
            }
        }

        services.AddFeedEaterMcp();
        return services;
    }

    private static FeedEaterOptions Settings(IServiceProvider sp) => sp.GetRequiredService<IOptions<FeedEaterOptions>>().Value;

    private static void Bearer(HttpClient http, string token)
    {
        if (token.Length > 0)
        {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
    }
}
```

`src/FeedEater/Program.cs`:

```csharp
using FeedEater;
using FeedEater.Mcp;
using FeedEater.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((context, logging) => logging
    .ReadFrom.Configuration(context.Configuration)
    .WriteTo.Console(new RenderedCompactJsonFormatter()));
builder.Services.AddFeedEater(builder.Configuration);

var app = builder.Build();
app.Services.GetRequiredService<DatabaseMigrator>().Run();

var token = app.Configuration["Mcp:Token"];
if (string.IsNullOrEmpty(token))
{
    app.Logger.LogWarning("Mcp:Token is not set; /mcp refuses every request");
}

app.MapGet("/healthz", async (FeedDb db, CancellationToken ct) => await db.PingAsync(ct) ? Results.Ok() : Results.StatusCode(503));
app.MapFeedEaterMcp(token);
app.Run();

public partial class Program;
```

`DigestRun`, `IdeaFiler` and `CallbackHandler` are singletons that hold typed `HttpClient`s; senses does the same with its senses. The handler rotation that `IHttpClientFactory` gives up here does not matter for five fixed hosts.

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS (all tests).

- [ ] **Step 7: Commit**

```bash
git add src tests
git commit -m "feat: MCP tools over the archive with hybrid search; full wiring"
```

---

### Task 15: Weekly retain to Hindsight

**Files:**
- Create: `src/FeedEater/Memory/HindsightClient.cs`, `src/FeedEater/Memory/WeeklyRetain.cs`
- Modify: `src/FeedEater/Storage/FeedbackStore.cs` (add `WeekReport`, `WeekReportAsync`), `src/FeedEater/ServiceRegistration.cs` (register the client and the job)
- Test: `tests/FeedEater.Tests/WeeklyRetainTests.cs`

**Interfaces:**
- Consumes: `ScheduledJob`, `Schedule.LatestWeekly`, `FeedbackStore`.
- Produces: `WeekReport { Digests, Highlights, Up, Down, Liked, Ideas }`, `FeedbackStore.WeekReportAsync(DateTimeOffset since, ct)`; `HindsightClient(HttpClient).RetainAsync(bank, content, context, DateTimeOffset timestamp, documentId, tags, ct)`; `WeeklyRetain` (scheduled, Sundays 18:00) with static `Text(string week, WeekReport)`.

- [ ] **Step 1: Write the failing tests**

`tests/FeedEater.Tests/WeeklyRetainTests.cs`:

```csharp
using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using FeedEater.Loops;
using FeedEater.Memory;
using FeedEater.Storage;

namespace FeedEater.Tests;

[Collection(PostgresCollection.Name)]
public sealed class WeeklyRetainTests(PostgresFixture pg) : IAsyncLifetime
{
    private static readonly DateTimeOffset SundayEvening = new(2026, 10, 4, 15, 30, 0, TimeSpan.Zero); // 18:30 Kyiv

    public Task InitializeAsync() => pg.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private WeeklyRetain Build(StubHandler hindsight, TimeProvider time) => new(
        new HindsightClient(hindsight.Client("http://hindsight/")), new FeedbackStore(pg.Db),
        new CursorStore(pg.Db), Options.Create(new FeedEaterOptions()), new LoopHealth(time), time, NullLogger<WeeklyRetain>.Instance);

    [Fact]
    public async Task Retains_one_weekly_summary_to_the_learning_bank()
    {
        var liked = await Seed.ItemAsync(pg, 1, "pgvector 0.8.7 released", TestVectors.OneHot(0));
        var feedback = new FeedbackStore(pg.Db);
        await feedback.SetVoteAsync(liked, 1, default);
        await feedback.AddIdeaAsync(new Idea { ItemId = liked, PlaneProject = "LAB", PlaneIssueId = "i", Title = "Upgrade pgvector", At = DateTime.UtcNow }, default);
        await using (var c = await pg.Db.DataSource.OpenConnectionAsync())
        {
            await c.ExecuteAsync("insert into digests (local_date, status, item_ids, sent_at) values ('2026-10-04', 'sent', @ids, now())", new { ids = new[] { liked } });
        }

        var hindsight = new StubHandler((_, _) => StubHandler.Json("""{"operation_id":"op1"}"""));
        var time = new FakeTimeProvider(SundayEvening);

        await Build(hindsight, time).TickAsync(default);

        var call = hindsight.Calls.Single();
        Assert.Equal("http://hindsight/v1/default/banks/learning/memories", call.Uri);
        Assert.Contains("\"document_id\":\"feed-eater-2026-W40\"", call.Body, StringComparison.Ordinal);
        Assert.Contains("signal:feed-eater", call.Body, StringComparison.Ordinal);
        Assert.Contains("pgvector 0.8.7 released", call.Body, StringComparison.Ordinal);
        Assert.Contains("Upgrade pgvector (LAB)", call.Body, StringComparison.Ordinal);
        Assert.Contains("\"timestamp\":\"2026-10-04T18:30:00+03:00\"", call.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_week_without_digests_retains_nothing()
    {
        var hindsight = new StubHandler((_, _) => StubHandler.Json("{}"));

        await Build(hindsight, new FakeTimeProvider(SundayEvening)).TickAsync(default);

        Assert.Empty(hindsight.Calls);
    }
}
```

The `+03:00` in the expected timestamp is Kyiv summer time on 2026-10-04; the JSON body escapes `+` as `+` with the default encoder, so `HindsightClient` must serialise with `JavaScriptEncoder.UnsafeRelaxedJsonEscaping` (it is plain JSON to a trusted API, never HTML).

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test FeedEater.slnx --filter "FullyQualifiedName~WeeklyRetainTests"`
Expected: build FAILS with `The type or namespace name 'Memory' does not exist`.

- [ ] **Step 3: Add the week report to the feedback store**

Add next to `Idea` in `src/FeedEater/Storage/FeedbackStore.cs`:

```csharp
public sealed record WeekReport
{
    public int Digests { get; init; }
    public int Highlights { get; init; }
    public int Up { get; init; }
    public int Down { get; init; }
    public IReadOnlyList<string> Liked { get; init; } = [];
    public IReadOnlyList<Idea> Ideas { get; init; } = [];
}
```

and inside `FeedbackStore`:

```csharp
    public async Task<WeekReport> WeekReportAsync(DateTimeOffset since, CancellationToken ct)
    {
        await using var c = await db.DataSource.OpenConnectionAsync(ct);
        var args = new { since = since.UtcDateTime };
        var digests = await c.QuerySingleAsync<WeekReport>(new CommandDefinition(
            """
            select count(*)::int as digests, coalesce(sum(cardinality(item_ids)), 0)::int as highlights
            from digests where status = 'sent' and sent_at >= @since
            """, args, cancellationToken: ct));
        var votes = await VotesSinceAsync(since, ct);
        var liked = await c.QueryAsync<string>(new CommandDefinition(
            """
            select i.title from votes v join items i on i.id = v.item_id
            where v.value = 1 and v.at >= @since order by v.at desc limit 10
            """, args, cancellationToken: ct));
        var ideas = await c.QueryAsync<Idea>(new CommandDefinition(
            "select item_id, plane_project, plane_issue_id, title, at from ideas where at >= @since order by at",
            args, cancellationToken: ct));
        return digests with { Up = votes.Up, Down = votes.Down, Liked = liked.ToList(), Ideas = ideas.ToList() };
    }
```

- [ ] **Step 4: Write the client and the job**

`src/FeedEater/Memory/HindsightClient.cs`:

```csharp
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FeedEater.Memory;

/// <summary>Hindsight's retain API, the same body host/opt-homelab/ingest.py sends.</summary>
public sealed class HindsightClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task RetainAsync(
        string bank, string content, string context, DateTimeOffset timestamp, string documentId, IReadOnlyList<string> tags, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"v1/default/banks/{bank}/memories", new
        {
            @async = true,
            items = new[]
            {
                new
                {
                    content,
                    context,
                    timestamp = timestamp.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                    document_id = documentId,
                    tags,
                },
            },
        }, Options, ct);
        response.EnsureSuccessStatusCode();
    }
}
```

`src/FeedEater/Memory/WeeklyRetain.cs`:

```csharp
using System.Globalization;
using Microsoft.Extensions.Options;
using FeedEater.Loops;
using FeedEater.Storage;

namespace FeedEater.Memory;

/// <summary>Sundays at 18:00: one plain summary of the week's reading to the learning bank. No model call; Hindsight extracts facts itself.</summary>
public sealed class WeeklyRetain(
    HindsightClient hindsight, FeedbackStore feedback,
    CursorStore cursors, IOptions<FeedEaterOptions> options, LoopHealth health, TimeProvider time, ILogger<WeeklyRetain> logger)
    : ScheduledJob(cursors, options, health, time, logger)
{
    private static readonly string[] Tags = ["signal:feed-eater", "trust:agent", "type:weekly-log"];

    protected override string Name => "weekly-retain";
    protected override string? DueKey(DateTime localNow) => Schedule.LatestWeekly(localNow, DayOfWeek.Sunday, new TimeSpan(18, 0, 0));

    protected override async Task RunAsync(string key, CancellationToken ct)
    {
        var now = Time.GetUtcNow();
        var report = await feedback.WeekReportAsync(now.AddDays(-7), ct);
        if (report.Digests == 0)
        {
            Logger.LogInformation("No digests this week; nothing to retain");
            return;
        }

        var sunday = DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var week = string.Create(CultureInfo.InvariantCulture, $"{ISOWeek.GetYear(sunday)}-W{ISOWeek.GetWeekOfYear(sunday):00}");
        await hindsight.RetainAsync(Settings.Hindsight.Bank, Text(week, report), "feed-eater weekly reading",
            TimeZoneInfo.ConvertTime(now, Settings.Zone), $"feed-eater-{week}", Tags, ct);
    }

    internal static string Text(string week, WeekReport r)
    {
        var text = string.Create(CultureInfo.InvariantCulture,
            $"feed-eater week {week}: {r.Digests} digests with {r.Highlights} highlights. Yehor voted up {r.Up}, down {r.Down}.");
        if (r.Liked.Count > 0)
        {
            text += $" Liked: {string.Join("; ", r.Liked)}.";
        }

        if (r.Ideas.Count > 0)
        {
            text += $" Ideas filed in Plane: {string.Join("; ", r.Ideas.Select(i => $"{i.Title} ({i.PlaneProject})"))}.";
        }

        return text;
    }
}
```

In `src/FeedEater/ServiceRegistration.cs`, add `using FeedEater.Memory;`, register the client next to the others:

```csharp
        services.AddHttpClient<HindsightClient>((sp, http) =>
        {
            var o = Settings(sp).Hindsight;
            http.BaseAddress = new Uri(o.BaseUrl);
            Bearer(http, o.Token);
        });
```

and the job inside `if (settings.RunJobs)`:

```csharp
            services.AddHostedService<WeeklyRetain>();
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test FeedEater.slnx`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src tests
git commit -m "feat: weekly reading summary to Hindsight"
```

---

### Task 16: Container, CI, self-test and README

**Files:**
- Create: `Dockerfile`, `.github/workflows/ci.yml`, `selftest.sh`, `README.md`, `profile.example.json`, `LICENSE`

**Interfaces:**
- Consumes: the built service.
- Produces: image `ghcr.io/egoushka/feed-eater:<version>` on a `v*` tag; `selftest.sh <base-url>` with `FEED_MCP_TOKEN` set.

- [ ] **Step 1: Dockerfile** (senses' file with names changed):

```dockerfile
# --platform=$BUILDPLATFORM keeps the SDK native; TARGETARCH picks the output.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-noble AS build
ARG TARGETARCH
ARG VERSION=0.0.0-dev
WORKDIR /src

COPY Directory.Build.props ./
COPY src/FeedEater/*.csproj src/FeedEater/
RUN dotnet restore src/FeedEater/FeedEater.csproj -a "${TARGETARCH}"

COPY src/ src/
# Migrations are embedded into FeedEater from here.
COPY db/ db/
RUN dotnet publish src/FeedEater/FeedEater.csproj -c Release -a "${TARGETARCH}" -p:Version="${VERSION}" -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble AS final
WORKDIR /app
# curl for the compose healthcheck; tzdata for Europe/Kyiv.
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl tzdata \
    && rm -rf /var/lib/apt/lists/*
USER app
COPY --from=build --chown=app:app /app .
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0
EXPOSE 8080
ENTRYPOINT ["dotnet", "FeedEater.dll"]
```

- [ ] **Step 2: CI** — copy senses' workflow and rename:

```bash
mkdir -p .github/workflows
sed -e 's/Senses.slnx/FeedEater.slnx/' -e 's#ghcr.io/egoushka/senses#ghcr.io/egoushka/feed-eater#' ../senses/.github/workflows/ci.yml > .github/workflows/ci.yml
grep -nE 'FeedEater.slnx|feed-eater' .github/workflows/ci.yml
```

Expected: two lines, the `dotnet test FeedEater.slnx` step and the image tag.

- [ ] **Step 3: Self-test script** `selftest.sh` (then `chmod +x selftest.sh`):

```bash
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
call feed_search '{"query":"postgres"}' | grep -q '"result"' && echo "feed_search ok"
call feed_digests '{}' | grep -q '"result"' && echo "feed_digests ok"
call feed_ideas '{}' | grep -q '"result"' && echo "feed_ideas ok"
```

- [ ] **Step 4: `profile.example.json`** — the shape only; the real file is in homelab-gitops:

```json
{
  "about": "Backend developer who runs a homelab and builds his own AI tooling.",
  "projects": [
    { "key": "homelab", "plane": "LAB", "description": "VPS with about 100 containers deployed by GitOps. Docker Compose, Postgres, Prometheus." },
    { "key": "side-project", "description": "A project without a Plane project; ideas for it go to the fallback project." }
  ],
  "topics": [
    { "key": "postgres", "description": "PostgreSQL internals, releases, extensions and performance." },
    { "key": "llm-engineering", "description": "Building with LLMs: evals, agents, retrieval, cost control." }
  ]
}
```

- [ ] **Step 5: LICENSE and README**

```bash
curl -fsSL https://www.apache.org/licenses/LICENSE-2.0.txt -o LICENSE
head -3 LICENSE
```

Expected: `Apache License` / `Version 2.0, January 2004`.

`README.md`:

````markdown
# feed-eater

Reads every entry Miniflux collects, keeps them all in a searchable archive, and each morning sends the few that
matter to Telegram with a concrete suggestion where one exists. Button presses teach the ranking; 💡 files the
suggestion into Plane Intake. Any MCP client can search the archive.

Design: [docs/specs/2026-10-05-feed-eater-design.md](docs/specs/2026-10-05-feed-eater-design.md).

## How an item travels

1. Miniflux fetches the feed. feed-eater copies the entry (every 30 min), strips HTML, marks duplicates by
   canonical URL or title, and embeds it.
2. At 07:30 Europe/Kyiv the new items are ranked by vector math: fit to the projects and topics in
   `profile.json`, similarity to what was liked or disliked, and the feed's 👍 rate.
3. The top 60 get a one-line verdict from a small model; the top 12 of those that matter are read in full by a
   stronger one, which writes a summary, why it matters, and a suggestion.
4. Telegram gets a header and one message per item with 👍 👎 and 💡.

## Configuration

Every key is under `FeedEater:` (environment: `FeedEater__Section__Key`). Defaults are in
[src/FeedEater/FeedEaterOptions.cs](src/FeedEater/FeedEaterOptions.cs).

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings:FeedEater` | yes | Postgres 18 with pgvector |
| `Mcp:Token` | yes | empty: `/mcp` refuses every request |
| `FeedEater:Miniflux:Token` | yes | Miniflux API key; read-only use |
| `FeedEater:Llm:ApiKey` | yes | LiteLLM virtual key with a budget |
| `FeedEater:Telegram:Token`, `AllowedUserId` | yes | its own bot; the poller only starts with a token |
| `FeedEater:Plane:Token` | for 💡 | Plane API key |
| `FeedEater:Karakeep:Token` | no | without it Karakeep saves are not used |
| `FeedEater:Hindsight:Token` | no | weekly summary to the `learning` bank |
| `FeedEater:ProfilePath` | no | default `/config/profile.json`; shape in `profile.example.json` |

## MCP tools

`feed_search`, `feed_read`, `feed_digests`, `feed_digest`, `feed_ideas`. All read-only. `./selftest.sh` calls them.

## Develop

```bash
dotnet test FeedEater.slnx
```

Docker must be running (Testcontainers starts `pgvector/pgvector:0.8.7-pg18-trixie`).

## License

Apache-2.0.
````

- [ ] **Step 6: Build the image locally and run the whole suite**

```bash
docker build -t feed-eater:dev .
dotnet test FeedEater.slnx -c Release
```

Expected: the image builds; all tests PASS.

- [ ] **Step 7: Commit**

```bash
git add Dockerfile .github selftest.sh profile.example.json LICENSE README.md
git commit -m "chore: container, CI, self-test, README"
```

---

### Task 17: Deploy (Yehor's go-ahead for each externally visible step)

**Files (in `personal/homelab-gitops`, its own session and worktree, one PR):**
- Create: `feed-eater/compose.yaml`, `feed-eater/config/profile.json`, `changelog.d/<YYYYMMDD>-feed-eater.md`
- Modify: `NETWORKS.md`, `PINS.md`, `agentgateway/config/config.yaml`, `switchboard/config/config.yaml`, `gatus/config/config.yaml`, `host/opt-homelab/ingest.py`, `host/cron.d/ingest-signals`

- [ ] **Step 1 (ask first): public GitHub repo.**

```bash
gh repo create Egoushka/feed-eater --public --source ~/Projects/personal/feed-eater --push
```

Then add to `~/Projects/repos.toml` (check `ListAgents` first; it is a shared control file):

```toml
[[repo]]
name = "feed-eater"
path = "personal/feed-eater"
kind = "personal"
status = "active"
remote = "https://github.com/Egoushka/feed-eater.git"
sweep = true
note = "reads Miniflux, daily Telegram digest with project suggestions, ideas to Plane Intake, archive over MCP"
```

- [ ] **Step 2 (ask first): release `v0.1.0`.** `git tag v0.1.0 && git push origin v0.1.0`; `gh run watch` for the `image` job; pin with homelab-gitops' `scripts/pin-image.sh ghcr.io/egoushka/feed-eater:0.1.0` and `scripts/pin-image.sh pgvector/pgvector:0.8.7-pg18-trixie`.

- [ ] **Step 3 (Yehor): accounts and keys.** None of these values pass through the chat; each goes into `feed-eater/.env.enc` with `make secret-set STACK=feed-eater KEY=<k>` as SECRETS.md describes.
  - Telegram: create a bot with BotFather, then send it `/start` from your account (a bot cannot message a user first). `TELEGRAM_BOT_TOKEN`; `TELEGRAM_USER_ID` is the same id as JARVIS's `TELEGRAM_ALLOWED_USER_IDS`.
  - Miniflux: Settings → API Keys → "feed-eater" → `MINIFLUX_TOKEN`.
  - LiteLLM key with its own budget, generated on the box so the master key never leaves it; pipe the printed key straight into the secret:

    ```bash
    ssh hedzer 'K=$(grep ^LITELLM_MASTER_KEY= /opt/stacks/litellm/.env | cut -d= -f2-); curl -s http://127.0.0.1:4000/key/generate -H "Authorization: Bearer $K" -H "Content-Type: application/json" -d "{\"key_alias\":\"feed-eater\",\"models\":[\"text-embedding-3-small\",\"gpt-4.1-nano\",\"claude-haiku-4-5\"],\"max_budget\":10,\"budget_duration\":\"30d\"}" | jq -r .key'
    ```

    → `LITELLM_KEY`.
  - `PLANE_TOKEN`: the token plane-sync uses (`PLANE_BOT_TOKEN`), or a new Plane API key.
  - `KARAKEEP_API_KEY`: a new Karakeep API key. `HINDSIGHT_API_KEY`: the existing one from `host/opt-homelab/.env`.
  - `DB_PASSWORD` and `FEED_MCP_TOKEN`: `openssl rand -hex 32` each.

- [ ] **Step 4 (ask first): Plane project `FEED`** through the Plane MCP: `project create` with name "Feed", identifier `FEED`; then `project update_features` with `intakes: true` for FEED and every project named in `profile.json` (LAB already has Intake: plane-sync files alerts there). Check each with `project get_features` first.

- [ ] **Step 5: Stack.** `feed-eater/compose.yaml`:

```yaml
## feed-eater — reads every Miniflux entry, sends a daily Telegram digest of what matters to Yehor's projects,
## files accepted ideas into Plane Intake, and serves the archive over MCP. Code: github.com/Egoushka/feed-eater.
## Deploy: /opt/stacks/feed-eater
## Reached via: agentgateway route /mcp/feed -> 100.64.0.2:8104 (tailnet only, bearer token). Telegram is long-polled.
## Depends on: miniflux, litellm, karakeep, hindsight (tailnet addresses), plane (plane-proxy on `edge`).

services:
  feed-eater:
    image: ghcr.io/egoushka/feed-eater:0.1.0@sha256:<digest printed by scripts/pin-image.sh in Step 2>
    container_name: feed-eater
    restart: unless-stopped
    x-homelab:
      tier: 2
      exposure: tailnet
      backup: none
      rollback: forward
      probe: http://100.64.0.2:8104/healthz
    ports: ["100.64.0.2:8104:8080"]   # agentgateway reaches MCP backends by tailnet address
    environment:
      - ConnectionStrings__FeedEater=Host=feed-eater-db;Username=feedeater;Password=${DB_PASSWORD:?set DB_PASSWORD in feed-eater/.env};Database=feedeater
      - FeedEater__Miniflux__Token=${MINIFLUX_TOKEN:?}
      - FeedEater__Llm__ApiKey=${LITELLM_KEY:?}
      - FeedEater__Telegram__Token=${TELEGRAM_BOT_TOKEN:?}
      - FeedEater__Telegram__AllowedUserId=${TELEGRAM_USER_ID:?}
      - FeedEater__Plane__Token=${PLANE_TOKEN:?}
      - FeedEater__Karakeep__Token=${KARAKEEP_API_KEY:-}
      - FeedEater__Hindsight__Token=${HINDSIGHT_API_KEY:-}
      - Mcp__Token=${FEED_MCP_TOKEN:?}
    volumes: ["./config/profile.json:/config/profile.json:ro"]
    depends_on:
      feed-eater-db: {condition: service_healthy}
    mem_limit: 384m
    memswap_limit: 384m
    security_opt: [no-new-privileges:true]
    healthcheck:
      test: ["CMD", "curl", "-fsS", "http://127.0.0.1:8080/healthz"]
      interval: 30s
      timeout: 5s
      retries: 3
      start_period: 60s
    labels:
      - autoheal=true
    networks: [default, edge]

  feed-eater-db:
    image: pgvector/pgvector:0.8.7-pg18-trixie@sha256:<digest printed by scripts/pin-image.sh in Step 2>
    container_name: feed-eater-db
    restart: unless-stopped
    x-homelab: {tier: 2, exposure: internal, backup: pgdump}   # the research archive is the product: dump it
    environment:
      - POSTGRES_PASSWORD=${DB_PASSWORD:?set DB_PASSWORD in feed-eater/.env}
      - POSTGRES_USER=feedeater
      - POSTGRES_DB=feedeater
    volumes: [feed_eater_pg18:/var/lib/postgresql]
    mem_limit: 512m
    memswap_limit: 512m
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U feedeater"]
      interval: 10s
      timeout: 5s
      retries: 5
      start_period: 30s
    security_opt: [no-new-privileges:true]

networks:
  default:
    ipam:
      config:
        - subnet: 10.211.35.0/24
  edge:
    external: true
    name: headscale_headscale_net

volumes:
  feed_eater_pg18:
```

`feed-eater/config/profile.json` (descriptions from `repos.toml` notes and Plane project descriptions; edit freely, the builder reloads it nightly):

```json
{
  "about": "Yehor, backend-leaning full-stack developer in Kyiv (.NET/C#, ASP.NET Core, EF Core, CQRS, SQL Server, Angular). Runs a self-hosted homelab on Hetzner and builds his own AI tooling. Long-term goal: passive income.",
  "projects": [
    { "key": "chargehand", "plane": "CHARGEHAND", "description": "Agent orchestrator. Answers codebase questions with read-only coding-agent workers and returns claims with evidence; .NET, runs on the VPS and the Mac." },
    { "key": "whetstone", "plane": "WHET", "description": "Prompt enhancer behind two MCP tools, enhance and feedback. First client is chargehand." },
    { "key": "senses", "description": "Perception layer for the AI. Turns Wi-Fi presence, NetFlow, Wakapi coding heartbeats and Oura sleep into states (home, on a call, deep work, asleep) served over MCP; .NET and Postgres." },
    { "key": "nytka", "plane": "NYTKA", "description": "Omi pendant without Omi's cloud. Android app plus a self-hosted ASP.NET Core and Postgres server that records, transcribes and summarises conversations." },
    { "key": "chronicle", "plane": "CHRON", "description": "Self-hosted memory over the Telegram chat archive and homelab streams." },
    { "key": "synapse", "description": "Memory control plane. Decides which claims stored in Hindsight are still true, superseded, or should stop being recalled." },
    { "key": "jarvis", "plane": "JARVIS", "description": "Personal assistant harness in TypeScript. Own agent loop, gateway tools, Claude Code and chargehand as sub-agents, web app and Telegram." },
    { "key": "content-engine", "description": "Plainsight. Self-hosted engine that keeps a developer's public presence consistent across GitHub, website and LinkedIn and drafts content from real work." },
    { "key": "touchstone", "plane": "TOUCH", "description": "SQL Server to PostgreSQL behaviour check. Replays captured stored-procedure calls on both engines and diffs results and table state." },
    { "key": "skarbnyk", "plane": "SKAR", "description": "Ukrainian personal tax on crypto. Turns exchange history into the tax declaration XML." },
    { "key": "homelab", "plane": "LAB", "description": "Hetzner VPS with about 120 containers deployed by GitOps. Docker Compose, Pangolin, Headscale, agentgateway, LiteLLM, Hindsight, Prometheus, Postgres." },
    { "key": "personal-website", "description": "hrabovskyi.online. Personal site and blog." },
    { "key": "trader", "description": "Algorithmic crypto trading experiments." },
    { "key": "feed-eater", "plane": "FEED", "description": "This service. Reads feeds, ranks them against projects, sends a daily Telegram digest and serves the archive over MCP." }
  ],
  "topics": [
    { "key": "dotnet-backend", "description": ".NET and ASP.NET Core releases, EF Core, performance, architecture, testing." },
    { "key": "llm-engineering", "description": "Building with LLMs: evals, prompting, retrieval, cost control, model releases." },
    { "key": "agents-and-mcp", "description": "Coding agents, agent orchestration, Model Context Protocol servers and clients." },
    { "key": "memory-systems", "description": "Long-term memory for AI agents: retrieval, forgetting, knowledge graphs." },
    { "key": "self-hosting", "description": "Self-hosted software, homelab hardware, Docker, networking, backups, observability." },
    { "key": "postgres", "description": "PostgreSQL internals, releases, extensions such as pgvector, migration from SQL Server." },
    { "key": "passive-income", "description": "Passive income for a Ukrainian resident: dividend ETFs, REITs, bonds, crypto yield, and their taxes in Ukraine." },
    { "key": "embedded", "description": "Embedded development: ESP32, microcontrollers, sensors, home hardware." },
    { "key": "ukraine-tech", "description": "Ukrainian tech industry, laws and taxes for IT specialists, the DOU and AIN community." }
  ]
}
```

- [ ] **Step 6: Register.**
  - `NETWORKS.md`: row `| 10.211.35.0/24 | feed-eater | feed-eater/compose.yaml |` (grep that it is still free).
  - `PINS.md`: rows for both digests.
  - `agentgateway/config/config.yaml`, next to the `senses` route; then add `"feed"` to the allow-list at `:85` and `FEED_MCP_TOKEN` to `agentgateway/.env.enc`:

    ```yaml
        - name: feed
          matches: [{path: {exact: /mcp/feed}}]
          policies:
            apiKey:
              mode: strict
              keys: [{keyHash: "sha256:<sha256 of a new client key, generated as for the senses route>", metadata: {client: owner}}]
          backends:
          - mcp:
              prefixMode: always
              failureMode: failOpen
              targets:
              - name: feed
                mcp: {host: "http://100.64.0.2:8104/mcp"}
                policies: {backendAuth: {key: "${FEED_MCP_TOKEN}"}}
    ```
  - `switchboard/config/config.yaml` under `servers:`:

    ```yaml
      feed:          {about: "feed-eater: Yehor's research archive (every feed item since 2023, AI summaries, daily digests, ideas filed to Plane)", trust_annotations: true}
    ```

  - `gatus/config/config.yaml`, next to `senses`:

    ```yaml
      - name: feed-eater
        group: mcp
        url: "http://100.64.0.2:8104/healthz"
        interval: 60s
        conditions: ["[STATUS] < 400"]
        alerts: [{type: ntfy}]
    ```

- [ ] **Step 7: Retire the dead Miniflux signal.** In `host/opt-homelab/ingest.py` delete `f_miniflux` (lines 82–92) and the `"miniflux": dict(...)` entry in `SIGNALS`; in `host/cron.d/ingest-signals` delete the `ingest.py miniflux` line. feed-eater's weekly retain replaces it.

- [ ] **Step 8:** `changelog.d/<YYYYMMDD>-feed-eater.md`: one paragraph (what the stack does; that it reads Miniflux and never changes it; that the miniflux ingest signal is removed). `make validate` passes; open the PR (ask first); after merge `hz deployed <merge sha> --wait 10m`; then `hz logs feed-eater --since 15m` shows `Ingested ... entries` lines and the backfill finishing, and `FEED_MCP_TOKEN=... ./selftest.sh http://100.64.0.2:8104` prints four `ok` lines.

- [ ] **Step 9 (Yehor approves each): Miniflux source cleanup.**
  - Hacker News: change the feed URL to `https://hnrss.org/frontpage?points=100`.
  - r/selfhosted and r/homelab: `https://www.reddit.com/r/<sub>/top/.rss?t=day` if Task 0 Step 1 returned 200; otherwise leave them.

- [ ] **Step 10: Four-week validation** (spec success criteria). Each Monday, record in `docs/specs/2026-10-05-validation.md` of feed-eater: digests delivered by 08:00 that week, 👍 and 👎 counts and the rate, 30-day spend from the digest header, one `feed_search` that found something useful. After four weeks: if the 👍 rate is under 60%, tune `FeedEater__Weights__*` and `FeedEater__Caps__*` in the compose environment, not code.
