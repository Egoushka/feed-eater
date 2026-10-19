-- v1.0 inbox (docs/specs/2026-10-19-v1.0-app.md): what used to be only a Telegram message. Telegram renders it while its token is set; swipe sessions queue it.
create table inbox (
    id            bigserial primary key,
    key           text not null unique,                      -- what makes a retried job write one row: 'duel:12', 'review:2026-10-18'
    kind          text not null check (kind in ('follow', 'duel', 'release', 'idea', 'review', 'autopsy', 'resurface', 'notice')),
    item_id       bigint references items (id),
    ref_id        bigint,                                    -- the duel, follow or other row the entry is about
    payload       jsonb not null default '{}',
    created_at    timestamptz not null default now(),
    expires_at    timestamptz,
    shown_session bigint references swipe_sessions (id) on delete set null,
    done_at       timestamptz
);
create index inbox_open_idx on inbox (created_at) where done_at is null;

-- Ideas wait for Keep or Drop; a dropped idea is remembered so the shortlist does not offer its source again.
alter table weekly_ideas
    add column outcome text not null default 'filed' check (outcome in ('filed', 'dropped')),
    alter column plane_issue_id drop not null;
