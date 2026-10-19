-- Swipe mode (docs/specs/2026-10-16-swipe-mode.md). Numbered past 0023 because the v0.9 spec reserves 0020 to 0023.

-- The owner's categories from categories.json, one embedded description each, rebuilt daily like profiles.
create table categories (
    key         text primary key,
    description text not null,
    share       real check (share >= 0 and share <= 1),   -- wanted share of cards; null = left to the ranking
    events      text[] not null default '{}',             -- which events count as yield here; empty = the defaults
    seed        boolean not null default false,
    embedding   vector(1536) not null,
    built_at    timestamptz not null
);

-- One sitting. The queue is fixed when it starts, so a reload resumes at the same card and the cap cannot be reset from the client.
create table swipe_sessions (
    id               bigserial primary key,
    started_at       timestamptz not null default now(),
    ended_at         timestamptz,
    end_reason       text check (end_reason in ('cards', 'time', 'exhausted', 'user')),
    card_cap         int not null,
    time_cap_s       int not null,
    cards_shown      int not null default 0,                -- cards handed out so far; the highest position handed out plus one
    queue_items      bigint[] not null default '{}',
    queue_categories text[] not null default '{}',          -- parallel to queue_items; '' = no category
    felt             smallint check (felt in (-1, 0, 1)),   -- "how did this leave me": drained, same, lighter
    felt_at          timestamptz
);
create index swipe_sessions_started_idx on swipe_sessions (started_at);

-- One row per card, created by the first thing that happens to it. dwell_ms is logged for the report and is never read by ranking.
create table swipe_events (
    id          bigserial primary key,
    session_id  bigint not null references swipe_sessions (id) on delete cascade,
    position    int not null,
    kind        text not null,
    ref_id      bigint not null references items (id),
    category    text,
    shown_at    timestamptz,
    dwell_ms    int,
    direction   text check (direction in ('up', 'down', 'left', 'right', 'none')),
    action      text check (action in ('next', 'back', 'like', 'dislike', 'save', 'share', 'idea', 'open')),
    acts        text[] not null default '{}',
    rank_source text not null default 'ranked',
    created_at  timestamptz not null default now(),
    unique (session_id, position)
);
create index swipe_events_ref_idx on swipe_events (ref_id, created_at);
