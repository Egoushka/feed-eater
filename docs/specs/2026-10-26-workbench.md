# Workbench: a research app on top of the archive

Goal: a desktop-first web app, served by feed-eater at `/app`, where the owner dives into the archive (search, a real reader, tags,
related items, stories, topics over time) and grows ideas from it (highlights, notes, Ask with citations, promote to Plane or a GitHub
issue). Telegram and the swipe stack stay what they are for: a few minutes of triage a day. The workbench is for the hour at the desk.

Owner's words (2026-10-26): "make it as app and improve it so I can really dive into news, tech etc. Tags, some interconnections, a
reader, that allow to interact with information in a way that allows research efficiently, basically a tool so people can born ideas
there (Telegram is small for this idea)."

Decided with the owner 2026-10-26:
- **Web app, fastest option, not the owner's usual stack:** Svelte 5 (SvelteKit, static adapter, SPA mode) + TypeScript + Vite, built to
  static files at image build time and served by the same .NET process. No Node at runtime, no new container.
- **Order:** v1.0 step 5 (push and doctor) ships first; then the workbench milestones; the Android app (v1.0 steps 6 and 7) after. The
  `/swipe` PWA covers phone triage meanwhile.
- **Notes and ideas live in feed-eater**, not in Nytka or in Markdown files. Nytka stays for voice and life notes.
- **Entities wait for the switchknife facts library.** No entity extraction in this spec; tags, meaning (embeddings), story clusters and
  notes carry the connections. The seam for entities is named in [Later](#later-entities-from-the-facts-library).
- Spec and plan reviewed in one docs PR; steps are filed as chargehand issues only after the owner approves it.

## Problem

The archive holds ~15,200 items (ids up to 15,172 on 2026-10-23), about 150 new a day. It is reachable three ways, and none supports
research:
- the digest: 9 to 12 items a day, chosen for the owner, read once;
- search: `/ui` (no script, one result list), Telegram `/search` (5 results) and MCP `feed_search` (20 results) — a list of titles with no
  way to open several, compare, follow a thread, or keep what was found;
- Ask: one answer in Telegram, gone in the chat history.

What is missing, checked against `main` at 96e0590:
- **A reader.** `items.content` is plain text with the HTML stripped (`0001_init.sql`; `ReadableText.Extract` flattens a page to text).
  Headings, lists, code blocks and links are gone, so a technical article reads badly.
- **Connections.** Story clusters exist (`items.cluster_of`, same event within 3 days, cosine 0.84) and show as "also in". Nothing shows
  items related by meaning, a story developing over weeks, or what the owner already wrote about a subject.
- **Tags.** `items.profile_key` is the nearest `profile.json` entry by embedding, always set, often wrong ("What Zig felt like, coming from
  Rust", item 8926, is filed under `postgres`). It cannot be corrected and is not a tag.
- **A place for ideas.** Weekly ideas go to Plane after a swipe; a 💡 files one item. There is no note, highlight, or idea that collects
  several sources and grows over days.
- **Keyword search does not stem.** `items.search` uses `to_tsvector('simple', …)` and the query `websearch_to_tsquery('simple', …)`
  (`ItemStore.cs:563`), so "databases" misses "database". Switching to `'english'` costs one table rewrite and no new dependency (plan step
  W0, shippable before anything else); BM25 is the larger step after it.
- **Summaries are rare.** Only digest reads and library saves get a summary (`reads`); most search hits have `summary: null`. The workbench
  must be useful on title and text alone and must not start reading 150 items a day to fill this gap (the cost invariant below).

## Users

1. **The owner, at the desk**, 30 to 90 minutes, a few times a week: chasing a subject for a project (a library choice, a homelab change, a
   blog post), or browsing a topic to see what moved. Keyboard first, two monitors, wants every action under 100 ms.
2. **The owner, triaging on the phone** in the morning: unchanged (swipe stack, later the Android app). Topic updates reach the phone as
   stack cards, so the workbench never needs visiting to stay useful (the lesson of swipe mode, README "Lessons").
3. **Someone self-hosting feed-eater**: the workbench ships inside the same image, works with the stock `pgvector/pgvector:pg18` database,
   and needs no new setting to turn on. Single user, as everything else.

Not users: a team, the public, a hosted service.

## Core loops

**Dive.** Search (or a topic, or today's digest) → open an item in the reader → the side panel shows related items, the same story in other
feeds and its later developments, tags, and notes that cite it → open the next one with one key → highlight what matters → write it into a
note.

**Grow an idea.** An idea note pulls in highlights and items as links (`[[item:123]]`, `[[hl:45]]`). Ask the archive from inside the note,
scoped to its sources, a tag or a topic; the answer comes back with citations and can be inserted. When the idea is ready, **Promote** files a
Plane intake item or a GitHub issue whose body carries the note and its sources. A GitHub issue with the `chargehand` label becomes work for
the owner's coding-agent orchestrator.

**Watch a subject.** A topic is a saved search plus tags. Its page shows a weekly timeline, the items, the stories inside it and the notes
linked to it. Once a week each topic with new items adds one `topic` card to the swipe stack ("pgvector: 6 new"); opening it opens the topic in
the workbench.

## Screens

Desktop layout: three panes (list, reader, context), resizable, the context pane collapsible. Under 1100 px wide the context pane becomes a tab
of the reader; under 700 px the list and reader stack (usable on a phone, not designed for it). Dark and light follow the system.

```
┌─ feed-eater ─────────────────────────────────────────── Ctrl-K ─┐
│ Home  Search  Topics  Notes  Ask                         ◐  ⚙   │
├───────────────┬────────────────────────────────┬────────────────┤
│ ▸ pgvector 0.8.7 released    │ pgvector 0.8.7 Released         │ Tags            │
│   PostgreSQL news · Oct 5    │ PostgreSQL news · 5 Oct 2026    │ [postgres] [+]  │
│ ▸ pgx-bm25 1.0: BM25 ranked… │ ───────────────────────────────  │ ┆security?┆ ✓ ✗ │
│   PostgreSQL news · Oct 7    │ Summary: fixes a buffer overflow│                 │
│ ▸ Hybrid Search Patterns…    │ (CVE-2026-103484) in IVFFlat…   │ Same story (2)  │
│   CrunchyData · Jul 30       │                                 │  · LWN · Oct 6  │
│   …                          │ ## What changed                 │ Developments    │
│                              │ The IVFFlat build …█████        │  · pgvector 0.8.8 │
│ facets                       │   (highlighted passage)         │ Related         │
│  kind: improve 12 · fyi 30   │                                 │  · pgvectorscale│
│  feed: PostgreSQL news 9 …   │ ```sql                          │  · Turbovec …   │
│  month: Oct 21 · Sep 14 …    │ REINDEX INDEX …                 │ Notes citing it │
│  tag: postgres 40 …          │ ```                             │  · BM25 for fe… │
├───────────────┴────────────────────────────────┴────────────────┤
│ j/k move · Enter open · h highlight · n note · s save · v 👍 · t tag │
└────────────────────────────────────────────────────────────────┘
```

- **Home.** Today's digest (from `digests.item_ids`, with summary, why, suggestion); topic updates since the last visit; continue reading
  (opened, not finished, last 14 days); open ideas (notes of kind `idea`, status `open`, most recently edited); saved and unread (the library).
- **Search.** One box. Keyword results appear as you type; results by meaning merge in when the query embedding arrives, without moving the
  selected row. Facets on the left (kind, feed, month, tag, saved, has note), each with a count. Results are items and notes. URL holds the
  state (`/app/search?q=…&tag=…`), so a search can be bookmarked or linked from a note.
- **Reader.** Sanitized article HTML (headings, lists, code, quotes, links; images shown as links), title, feed, date, "also in", the read
  (summary, why, suggestion) when one exists, vote, save, follow, tags. Reading progress is kept. Select text → `h` highlights, `n` sends the
  selection to a note. Context pane as in the mockup.
- **Story.** One story: the cluster (same event, other feeds) and its developments (later items on the same story within 30 days), on a
  timeline.
- **Topic.** Name, definition (query, tags, filters; editable), a weekly bar chart of matching items (last 52 weeks), the items (by date or by
  relevance), the stories inside it, the notes linked to it, Ask scoped to it, Export.
- **Notes.** List (filter: kind, status, tag), and the editor: Markdown with a live preview, `[[` autocompletes items, highlights, notes,
  tags and topics from search; backlinks below; Ask panel; Promote. Autosaves.
- **Ask.** A question, an optional scope (all, topic, tag, note's sources, selected items), a streamed answer with numbered citations that
  open in the reader; Save as note.
- **Command palette (Ctrl-K).** Jump to any item, note, topic or tag by title; run any action on the selection (tag, note, save, follow,
  promote, export). Every screen action has a key, listed with `?`.

## Architecture

```mermaid
flowchart LR
  subgraph image[feed-eater image]
    spa[web/ Svelte build at /app] -->|fetch JSON, cookie + X-CSRF| appapi[/app/api]
    appapi --> svc[services: search, reader, tags, topics, notes, ask, promote]
    ui[/ui no-JS admin] --> svc
    api1[/api/v1 bearer, phone] --> svc
    mcp[/mcp] --> svc
    loops[loops: body prefetch, tag suggester, note embedder, topic digest] --> svc
  end
  svc --> pg[(Postgres 18: pgvector + optional BM25 extension)]
  svc --> llm[LLM: embeddings, Ask]
  svc --> plane[Plane] & gh[GitHub issues]
  loops --> inbox[(inbox → swipe stack)]
```

- `web/` is a new top-level folder: SvelteKit with `adapter-static` (SPA fallback `index.html`), `ssr = false`, `paths.base = '/app'`,
  TypeScript strict. Libraries kept small: CodeMirror 6 (note editor), markdown-it with `html: false` (note preview). No UI kit, no state
  library: Svelte runes and a ~200-line fetch cache (`web/src/lib/api.ts`: request dedupe, in-memory LRU of 500 responses, `AbortController`
  per screen, prefetch).
- The Dockerfile gains a `node:24` build stage that runs `npm ci && npm run build` in `web/` and copies the output to the app's `wwwroot/app`.
  Hashed assets are served `Cache-Control: public, max-age=31536000, immutable`; `index.html` is `no-store`.
- `/app` responses carry `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src
  'self'; font-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'` — no inline script or style, nothing from other hosts.
- **Auth.** The same `fe_session` cookie as `/ui` (login at `/ui/login`, which gains a `return=/app/...` parameter limited to same-site paths).
  `GET /app/api/me` returns `{csrf, version, capabilities}`; the SPA sends the csrf value as `X-CSRF` on every non-GET request. Every
  `/app/api` call checks cookie, csrf header (non-GET) and the same-origin rule already in `UiGuard.SameOrigin`; with no `Mcp:Token` it answers
  503 like `/ui`. `/api/v1` keeps refusing browsers (`ApiAuth`); its rules do not change.
- **Handlers stay thin and portable.** Each feature maps its routes with an extension method on `RouteGroupBuilder` (as `MapLibraryApi` does
  today), so the same handlers can later be mounted under `/api/v1` for the phone. Problem details for errors, as `/api/v1`.

## Data model

Migrations continue after the last one on `main` when the step starts (0032 is the last today; v1.0 step 5 may take 0033). Every new table goes
into `PostgresFixture.ResetAsync`'s list and `CombinationTests`.

```sql
-- Reader: sanitized article HTML, fetched once. Plain text stays in items.content for search and models.
create table item_bodies (
    item_id    bigint primary key references items (id),
    html       text not null,              -- allowlist-sanitized; empty when status <> 'ok'
    source     text not null check (source in ('page', 'content')),   -- fetched page, or items.content wrapped in <p>
    status     text not null check (status in ('ok', 'no_text', 'failed')),
    attempts   int not null default 1,
    fetched_at timestamptz not null default now()
);

create table reading_state (
    item_id     bigint primary key references items (id),
    opened_at   timestamptz not null,
    progress    real not null default 0 check (progress between 0 and 1),
    finished_at timestamptz
);

-- Query embeddings are the slow part of hybrid search; the same text is embedded once per model.
create table query_embeddings (
    hash      bytea primary key,            -- sha256(model || '\n' || normalized query)
    embedding vector(1536) not null,
    last_used timestamptz not null default now()
);

create table tags (
    id          bigserial primary key,
    slug        text not null unique,       -- lowercase, [a-z0-9-]
    name        text not null,
    description text not null default '',   -- embedded; what the tag is about, in words
    embedding   vector(1536),               -- of the description
    profile_key text unique,                -- seeded from profiles; null for tags the owner made
    suggest     boolean not null default true,
    created_at  timestamptz not null default now()
);

create table item_tags (
    item_id  bigint not null references items (id),
    tag_id   bigint not null references tags (id) on delete cascade,
    state    text not null check (state in ('confirmed', 'suggested', 'rejected')),
    score    real,                          -- cosine to the tag prototype when suggested
    at       timestamptz not null default now(),
    primary key (item_id, tag_id)
);
create index item_tags_tag_idx on item_tags (tag_id, state);

create table topics (
    id              bigserial primary key,
    name            text not null,
    query           text not null default '',
    tag_ids         bigint[] not null default '{}',
    filters         jsonb not null default '{}',   -- feed ids, kinds, saved; same names as the search API
    notify          boolean not null default true,
    last_checked_at timestamptz,
    created_at      timestamptz not null default now()
);

-- W3C text-quote anchoring: the quote plus up to 32 characters each side, re-found in the rendered body.
create table highlights (
    id         bigserial primary key,
    item_id    bigint not null references items (id),
    exact      text not null check (length(exact) between 1 and 4000),
    prefix     text not null default '',
    suffix     text not null default '',
    start_hint int,                          -- character offset in the body text when made; a hint, not the anchor
    comment    text,
    created_at timestamptz not null default now()
);
create index highlights_item_idx on highlights (item_id);

create table notes (
    id           bigserial primary key,
    kind         text not null check (kind in ('note', 'idea', 'answer')),
    title        text not null,
    body         text not null default '',   -- Markdown, at most 200,000 characters
    status       text not null default 'open' check (status in ('open', 'promoted', 'dropped')),
    promoted_url text,
    embedding    vector(1536),               -- filled by a loop after edits settle
    embedded_at  timestamptz,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now(),
    search       tsvector generated always as (
        setweight(to_tsvector('english', title), 'A') || setweight(to_tsvector('english', left(body, 100000)), 'B')) stored
);
create index notes_search_idx on notes using gin (search);

-- Rebuilt from the body on every save: what the note points at, for backlinks.
create table note_links (
    note_id     bigint not null references notes (id) on delete cascade,
    target_kind text not null check (target_kind in ('item', 'highlight', 'note', 'tag', 'topic')),
    target_id   bigint not null,
    primary key (note_id, target_kind, target_id)
);
create index note_links_target_idx on note_links (target_kind, target_id);

-- One filing per note and target, so a retried Promote files once.
create table promotions (
    note_id    bigint not null references notes (id),
    target     text not null check (target in ('plane', 'github')),
    url        text not null,
    at         timestamptz not null default now(),
    primary key (note_id, target)
);
```

`inbox.kind` gains `topic` (the check constraint is replaced as `0032_library.sql` did for `swipe_events`).

Not stored, computed per request (cheap at this size; see [Performance](#performance)): related items (vector index), story developments,
topic membership and timeline, facets.

## Features

### Reader body
On first open, `ReaderBody` builds `item_bodies`: fetch the item's URL once through `SafeFetcher` (all page-fetch rules, the daily cap
`Fetch:MaxPerDay`), take the main region the way `ReadableText` does, and sanitize with an allowlist (the HtmlSanitizer package, MIT): `p h1–h6
ul ol li pre code blockquote em strong a table thead tbody tr th td hr br figure figcaption`; `a` keeps `href` only if `http(s)`, gains
`rel="noopener noreferrer nofollow"` and `target="_blank"`; `img` becomes a link to its source labelled with its alt text; every attribute
other than `href` is dropped. A 403, paywall, non-HTML or a body under 400 characters of text falls back to `items.content` split into
paragraphs (`source = content`). A failed fetch is retried on a later open at most 3 times. A loop prefetches bodies for each new digest's items
and new library saves (at most 50 a day, inside the fetch cap), so the morning's reading opens instantly.

### Search
- **API:** `GET /app/api/search?q=&mode=lexical|hybrid&tag=&feed=&kind=&from=&to=&saved=&notes=&cursor=&limit=` → `{hits, facets, cursor}`.
  Hits are items and notes in one ranked list (`type: item|note`), with a highlighted snippet. Facets: counts by kind, feed, month, tag,
  saved, over the full candidate set (not the page).
- **Lexical:** behind `ILexicalSearch`, two implementations. `Bm25Search` uses a BM25 index extension when installed; `TsvectorSearch` is
  `items.search` with the `'english'` configuration (from W0) and `ts_rank_cd`, used when the extension is absent. Which extension is decided by a benchmark step on the owner's
  archive (plan step W3a) between **pgx-bm25** (1.0 released 2026-10-07, PostgreSQL licence, BM25F, phrases, prefix, snippets; item 12969 in
  the archive) and **pg_textsearch** (Tiger Data, PostgreSQL licence, 1.0 early 2026, phrase queries since 1.5.0). ParadeDB `pg_search` is out:
  AGPL and replicas only in its paid edition. Exactly one extension is installed; the code depends only on `ILexicalSearch`.
- **Database image:** the chosen extension ships in `ghcr.io/egoushka/feed-eater-db:pg18` (the pgvector image plus the extension, built in this
  repository's CI). `compose.example.yaml` moves to it; the stock pgvector image keeps working with `TsvectorSearch`. `doctor` prints which
  lexical engine is active.
- **By meaning:** today's HNSW scan with `hnsw.iterative_scan = relaxed_order`, filters inside the scan.
- **Fusion:** reciprocal rank fusion, k = 60, as today, over the top 50 of each; then a small boost for a 👍 vote, a save and a confirmed tag
  that is in the filter (weights fixed in code, covered by tests). Notes join the lexical and vector lists with the same rules.
- **Speed:** `mode=lexical` never calls the embedder. The SPA asks `lexical` on each keystroke (debounced 120 ms, earlier request aborted)
  and `hybrid` after 400 ms without typing or on Enter. Query embeddings are cached in `query_embeddings` (rows unused for 90 days deleted
  weekly). A hybrid request whose embedding takes over 1.5 s returns lexical results with `partial: true`, and the SPA retries once in the
  background (today's timeout is 5 s and blocks the answer).
- **Eval:** `dotnet FeedEater.dll search-eval [--queries file] [--n 200]` measures known-item retrieval on the live archive: for `n` items
  with a read, the first sentence of the summary is the query and the item the target; prints MRR@10, recall@10, p50 and p95 latency per
  engine and mode. Calls no chat model; embeds at most `n` queries (cached).

### Related and story
- **Related:** `GET /app/api/items/{id}/related?k=10`: nearest items by embedding, excluding duplicates and the item's own cluster, cosine
  ≥ 0.55, at most 2 from one feed.
- **Story:** `GET /app/api/items/{id}/story`: the cluster (`cluster_of` root and members) plus developments: items published after the root
  within `Story:Days` (30), not in the cluster, with cosine ≥ `Cluster.Threshold − 0.04` to the root or to any member already in the list
  (the rule `StoryFollower` uses), version-name guard reused from the clusterer, at most 50, in date order.

### Tags
- **Seeding:** each profile (`profiles` table: key, description, embedding) becomes a tag once, `profile_key` set; later profile edits update
  name, description and embedding of their seeded tag. `profile_key` on items is left alone (ranking uses it).
- **Owner tags:** create with a name and a description (embedded on save). Tags can be renamed, merged (rows move, duplicates collapse to the
  strongest state) and deleted.
- **States per item:** `confirmed` (the owner said yes, or added it), `suggested` (the suggester), `rejected` (the owner said no; never
  suggested again for that item).
- **Suggester** (a loop, hourly, vector math only): prototype = normalize(description embedding + mean of confirmed item embeddings) and,
  when there are ≥ 5 rejections, minus 0.3 × the mean of rejected ones. An item from the last 90 days (and every item the reader opens) is
  suggested when its cosine to the prototype is at least the tag's 97th percentile over the last 90 days' items and at least 0.35. At most
  20 new suggestions per tag a run. No model call.
- Search filter `tag=` matches confirmed and suggested; the facet shows both counts. The reader shows confirmed tags solid and suggested ones
  dashed with ✓ and ✗.

### Topics
- A topic = `query` + `tag_ids` + `filters`, evaluated through the search service (hybrid when `query` is set, tag filter otherwise).
- **Page:** `GET /app/api/topics/{id}` (definition, counts), `/items?sort=date|relevance&cursor=`, `/timeline` (weekly counts for 52 weeks,
  `date_bin('7 days', published_at, …)`), `/stories` (clusters with ≥ 2 members among the topic's items, newest first), and its notes
  (`note_links` with `target_kind = topic`, plus notes linking any of its top 200 items).
- **Topic digest** (weekly, Sunday 17:00 local, quiet hours respected): for each topic with `notify`, items published since
  `last_checked_at` → one inbox entry `topic` keyed `topic:{id}:{iso week}` with the count and the top 3 titles; none when the count is 0.
  The swipe card shows the name, count and titles (rendered with `textContent`); acts: `open` (deep link to `/app/topics/{id}`), `mute`
  (sets `notify = false`), next = later. With Telegram on, the entry is rendered as one text message with the link.

### Highlights
Select text in the reader → `POST /app/api/items/{id}/highlights {exact, prefix, suffix, startHint, comment?}`. On load the SPA re-anchors
each highlight in the rendered body: exact + prefix + suffix match, else exact match nearest `startHint`, else listed under "Couldn't place"
with its quote (never lost). Highlights appear in the reader, in search (as `has note` facet), and in exports.

### Notes
- `POST /app/api/notes`, `GET/PUT/DELETE /app/api/notes/{id}`; `PUT` carries the `updatedAt` it was based on and gets 409 with the current
  note when someone (another tab) saved in between.
- Links in the body: `[[item:123]]`, `[[hl:45]]`, `[[note:6]]`, `[[topic:2]]`, and `#tag-slug`. On save the server parses them into
  `note_links` (unknown ids are kept as text and not linked). Backlinks: `GET /app/api/backlinks?kind=item&id=123`.
- Preview: markdown-it with `html: false` and `linkify`; `[[…]]` rendered as chips that open the target; external links `rel="noopener
  noreferrer"`. Raw HTML in a note shows as text.
- "Send selection to note" (`n` in the reader) appends a quote block with a `[[hl:…]]` link to the chosen note (new or recent).
- A loop embeds notes 60 s after their last edit, so they join search by meaning and related items (a note's related panel shows items).

### Ask
- `POST /app/api/ask {question, scope}` with `scope` one of `{}` (archive), `{topicId}`, `{tagId}`, `{noteId}` (the note's linked items and
  highlights' items), `{itemIds: [...]}` (at most 30). Answers stream as server-sent events: `token` events, then one `citations` event
  (`[{n, itemId, title, url, highlightId?}]`), then `done`. Retrieval runs inside the scope (top 8 by hybrid search within it; for `itemIds`
  and `noteId`, those items ranked by the question). The prompt, citation checking and "the sources don't answer it" behaviour are
  `ArchiveAnswer`'s, refactored to take a retrieval set; Telegram `/ask` and `POST /api/v1/ask` keep their behaviour.
- **Save as note** creates a note of kind `answer` with the question as title, the answer with citations as `[[item:…]]` links.
- One Ask is one read-model call, as today; there is no other model call in the workbench besides embeddings.

### Promote
- `POST /app/api/notes/{id}/promote {target: plane|github, project?, repo?, labels?}`.
- Body: the note's Markdown with `[[item:n]]` resolved to "title (feed, date) url", highlights quoted with their item link, and a footer
  "From feed-eater note {id}". Title: the note's title.
- Plane: an Intake item through the existing `PlaneClient` in `project` (default `Plane:FallbackProject`). GitHub: an issue through the REST
  API with `FeedEater:GitHub:Token`, only in repos listed in `FeedEater:Promote:GitHubRepos`, labels from the request limited to
  `FeedEater:Promote:GitHubLabels` (e.g. `chargehand`).
- Idempotent: an existing `promotions` row returns its URL and files nothing. The body carries a hidden marker
  `<!-- feed-eater:note:{id} -->`; before creating a GitHub issue the repo is searched for it, so a request that timed out after GitHub created
  the issue adopts that issue instead of filing a second one. On success the note's status becomes `promoted` and the URL is
  shown on the note. Unconfigured targets are absent from `capabilities` and the button is hidden.

### Export
`GET /app/api/notes/{id}.md` (resolved links, highlights), `/app/api/topics/{id}.md` (definition, the top 100 items with title, feed, date,
URL, summary when present, highlights, linked notes), `/app/api/export/notes.zip` (every note as Markdown plus `index.json`). The existing OPML
export stays on `/ui/sources`.

### MCP
`feed_search` gains `tag`; new read-only tools `feed_notes` (search notes, return one with its links) and `feed_topic` (a topic's definition,
recent items and notes). AI clients see the owner's research, not only the raw archive.

## Performance

Budgets, measured on the owner's instance (~15k items; CI checks the server side on a seeded 20,000-item database):

| Path | Budget (p95) | How |
|---|---|---|
| `/app/api/search?mode=lexical` | 50 ms server | BM25 index or GIN; facets in the same statement |
| `/app/api/search?mode=hybrid`, embedding cached | 150 ms server | HNSW + RRF; one transaction |
| keystroke → results painted (LAN) | 150 ms | debounce 120 ms + lexical + no layout shift |
| `/app/api/items/{id}` with body present | 30 ms | one query; body prefetched for digest and saves |
| `/related`, `/story` | 40 ms each | HNSW, k ≤ 50 |
| First load of `/app` (cold cache) | 1 s on LAN, JS ≤ 150 KB gzip at first paint | code split per route; CodeMirror loaded only in the editor |
| Moving the selection (j/k) | next frame | reader data for the selected row and the next 2 prefetched |

`PerfTests` (trait `Category=Perf`, part of the normal suite) seed 20,000 items with random embeddings and text and assert the server budgets
with 3× headroom for CI runners. The SPA keeps a bundle-size check in CI (`npm run size`).

The cost invariant stays: model spend is capped by fixed counts, not by archive size. The workbench adds Ask calls (only when asked) and
embeddings for queries (cached) and notes. Tag suggestions, related items, stories, topics and timelines are vector math and SQL.

## Errors
- Embedder down or slow: search answers lexically with `partial: true`; the SPA shows "keyword results only" and retries once.
- Page fetch refused, 403 or paywall: the reader shows the plain text and says "full article not available"; the original link is one key away.
- Model down during Ask: the stream ends with an `error` event and the retrieved sources are still listed, so the dive continues.
- Plane or GitHub down on Promote: 502 problem details; nothing is recorded, so a retry is safe; the note stays `open`.
- Session expired: any 401 sends the SPA to `/ui/login?return=<current path>`; an unsaved note is kept in `sessionStorage` and restored.
- Conflicting note edit: 409 with both versions; the editor shows the server version and keeps the local text in a side panel.
- Hostile feed text: bodies are sanitized on the server and rendered under a CSP with no inline script; notes render no raw HTML; titles and
  snippets are text. Snippet highlighting returns ranges, not HTML.

## Testing
- **Server** (existing style: xUnit, `WebApplicationFactory<Program>`, Testcontainers Postgres, one host per test class): auth (no cookie 401,
  missing csrf 403, cross-origin 403, no `Mcp:Token` 503); search modes (lexical never calls the embedder; cache hit embeds once); facets equal
  to a direct count; both `ILexicalSearch` implementations pass the same relevance tests; sanitizer (scripts, event handlers, `javascript:`
  links, `style`, `iframe`, `svg` removed; code and headings kept); body fetched once and fallback; related and story rules; tag seeding,
  suggestion, rejection memory, merge; topic evaluation, timeline buckets, weekly entry once per key and none at 0; note link parsing,
  backlinks, 409; Ask scope (retrieved ids ⊆ scope), SSE order, citations; Promote idempotency per target, allow-lists, unconfigured target
  absent; exports; MCP tools; `PerfTests`.
- **Web** (`web/`): `svelte-check` with TypeScript strict; Vitest unit tests (fetch cache dedupe and abort, search store debounce, highlight
  anchoring including a body whose offsets moved, link chips, markdown preview escapes HTML); Playwright end-to-end tests against the built
  SPA with the API mocked by route handlers from fixtures in `tests/contract/app/` (written by the server tests, as `tests/contract/` is for
  `/api/v1`), so the browser tests need no database.
- `OwnerTermsTests` scans `web/src`.
- Before the 0.12 release: a second agent's risk review, as for earlier releases.

## Out of scope
- **Entity extraction** (projects, libraries, companies, people) and entity pages: waits for the facts library (below).
- **A graph view.** The related pane, stories, backlinks and topic pages carry the connections; a force-directed graph of 15k nodes looks good
  and answers little. Reconsider after entities exist.
- **Offline use of the web app**, a service worker, IndexedDB caches: it is used at a desk on the same network.
- **Images in the reader:** shown as links; showing them needs an image proxy through `SafeFetcher` with its own limits (a candidate later).
- **Summaries for every item:** the cost invariant; the reader offers "Read this" (one read-model call, as a library save does) on demand.
- **Multiple users, sharing, public notes, comments.**
- **Annotating PDFs, videos, podcasts.**
- **Changing the digest, ranking, swipe stack** beyond the new `topic` card.

## Later: entities from the facts library
When switchknife ships its temporal facts library, a step adds an `IEntityGraph` adapter: items as sources of facts (subject, relation,
object, valid from/to), entity pages built like topic pages (timeline, items, stories, notes), and entity chips in the reader next to tags.
Nothing in this spec stores entities, so there is nothing to migrate; topics gain an `entity_ids` filter then. Until the library's design
exists, this spec does not guess its interface.

## Milestones and versions
The weekly cycle (`~/Projects/context/cycle.md`, from 2026-10-26): every merged step is a patch, the Friday release cuts the next patch for
what merged that week, and a closed milestone is marked by an empty commit with `Release-As: 0.N.0`. Milestones are named goals in Plane; the
version numbers below are expectations. feed-eater tags releases by hand today (`v0.8.6`, the image builds on `v*` tags); adopting
release-please with `bump-patch-for-minor-pre-major` is an open decision for the owner.

| Milestone (Plane name) | Releases | Contents | Done when |
|---|---|---|---|
| Server for the app | 0.9.0 | v1.0 steps 1–5 (inbox, one stack, `/api/v1`, library, push and doctor) | released and deployed; planned in `docs/plans/2026-10-19-v1.0-app.md` |
| M1 Reader and search | 0.9.x patches, closed by 0.10.0 | English stemming (W0), app shell, search, BM25, reader, related and story, home and palette | the owner reads a day's digest and runs ten searches in `/app` without opening `/ui` |
| M2 Tags and topics | 0.10.x patches, closed by 0.11.0 | Tags, topics, topic digest card, topic and story pages | five topics exist and one weekly `topic` card reached the stack |
| M3 Notes and ideas | 0.11.x patches, closed by 0.12.0 | Highlights, notes, Ask in the app, Promote, exports, MCP additions; risk review | one idea went from highlights to a promoted GitHub issue with its sources |
| Android | 1.0.0 | Android app (v1.0 steps 6–7), Telegram retired | as `docs/specs/2026-10-19-v1.0-app.md` |

## Owner side (not in this repository)
- homelab-gitops: switch the feed-eater database image to `feed-eater-db` once W3b is released (a `REINDEX`-free change: the extension
  builds its index on first start; check the migration time on a copy first). `/app` is reached on the same HTTPS tailnet name as `/ui`.
- chargehand: add feed-eater to the repositories it reads (`/srv/chargehand/repos`) and to its intake, so issues labelled `chargehand` on
  `Egoushka/feed-eater` become driven sessions.
- `FeedEater:Promote:GitHubRepos` and `GitHubLabels` for the owner's instance.

## Acceptance
1. Every plan step's acceptance checks pass in CI on its PR.
2. On the owner's instance: lexical search p95 ≤ 50 ms and hybrid (cached) ≤ 150 ms by `search-eval`, and BM25 MRR@10 at least equal to the
   tsvector baseline.
3. A day's digest read entirely in `/app`, keyboard only.
4. A topic card in the swipe stack opens the topic in `/app`.
5. One idea note with at least three highlights from two items, an Ask answer inserted with citations, promoted to a GitHub issue that
   chargehand picks up.
6. The stock `pgvector/pgvector:pg18` database still runs everything (lexical search falls back to tsvector; `doctor` says so).
