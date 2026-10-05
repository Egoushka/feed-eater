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
create index items_published_at_idx on items (published_at);
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
    cost          numeric(16, 10) not null
);
create index llm_usage_at_idx on llm_usage (at);
