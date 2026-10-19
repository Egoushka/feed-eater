-- v1.0 one stack (docs/specs/2026-10-19-v1.0-app.md): inbox entries and digest items ride in swipe sessions; acts are idempotent and undoable.
alter table inbox add column shown_count int not null default 0;

alter table swipe_events drop constraint swipe_events_action_check;
alter table swipe_events add constraint swipe_events_action_check check (
    action in ('next', 'back', 'like', 'dislike', 'save', 'share', 'idea', 'open', 'good', 'again', 'send', 'dismiss',
               'stop', 'pick_a', 'pick_b', 'keep', 'drop'));

-- One row per act the client sent with an id. A repeat returns this row's result; undo restores previous_*.
create table swipe_acts (
    client_id       uuid primary key,
    session_id      bigint not null references swipe_sessions (id) on delete cascade,
    position        int not null,
    act             text not null,
    ok              boolean,                 -- null while the first call is still running
    message         text,
    previous_value  smallint,                -- the vote before the act; null = there was none
    previous_weight real,
    at              timestamptz not null default now(),
    undone_at       timestamptz
);
create index swipe_acts_card_idx on swipe_acts (session_id, position);
