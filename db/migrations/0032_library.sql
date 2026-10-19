-- v1.0 library (docs/specs/2026-10-19-v1.0-app.md): saves live in feed-eater; Karakeep is an optional mirror.
alter table saved
    alter column karakeep_id drop not null,
    add column source text not null default 'telegram'
        check (source in ('swipe', 'telegram', 'ui', 'api', 'share', 'import')),
    add column read_at timestamptz,
    add column note text,
    add column enrich_attempts int not null default 0,
    add column enriched_at timestamptz,
    add column full_text boolean not null default false;   -- the article page was fetched, not just the shared text
create index saved_unread_idx on saved (at) where read_at is null;

-- Undo removes a library row only if the act created it.
alter table swipe_acts add column previously_saved boolean;

-- Resurface cards: remove and read are swipe acts; undo of a remove re-adds the row with the source it had.
alter table swipe_events drop constraint swipe_events_action_check;
alter table swipe_events add constraint swipe_events_action_check check (
    action in ('next', 'back', 'like', 'dislike', 'save', 'share', 'idea', 'open', 'good', 'again', 'send', 'dismiss',
               'stop', 'pick_a', 'pick_b', 'keep', 'drop', 'remove', 'read'));
alter table swipe_acts add column previous_source text;
