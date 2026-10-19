-- v1.0 library (docs/specs/2026-10-19-v1.0-app.md): saves live in feed-eater; Karakeep is an optional mirror.
alter table saved
    alter column karakeep_id drop not null,
    add column source text not null default 'telegram'
        check (source in ('swipe', 'telegram', 'ui', 'api', 'share', 'import')),
    add column read_at timestamptz,
    add column note text,
    add column enrich_attempts int not null default 0,
    add column enriched_at timestamptz;
create index saved_unread_idx on saved (at) where read_at is null;

-- Undo removes a library row only if the act created it.
alter table swipe_acts add column previously_saved boolean;
