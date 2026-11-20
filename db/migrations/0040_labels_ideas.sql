-- Labels and lost ideas (docs/specs/2026-11-20-labels-and-idea-resurfacing.md).

-- One row per swipe or action on a card or idea. Append-only; same keys as wheelhouse's ledger so one learner reads both. No free text.
create table labels (
    id        bigserial primary key,
    ts        timestamptz not null default now(),
    surface   text not null default 'feed-eater',
    item_kind text not null,
    item_id   text not null,
    action    text not null check (action in ('kept', 'dismissed', 'snoozed', 'opened', 'promoted', 'ignored'))
);
-- squawk-ignore require-concurrent-index-creation
create index labels_item_idx on labels (item_kind, item_id, ts);

create function labels_append_only() returns trigger language plpgsql as $$
begin
    raise exception 'labels is append-only';
end $$;
create trigger labels_no_change before update or delete on labels for each row execute function labels_append_only();

-- Ideas that existed only in a transcript, with their FSRS-6 memory state. A swipe is the review.
create table idea_items (
    id             bigserial primary key,
    seed_key       text not null unique,
    body           text not null,                      -- his exact words
    source_session text not null,
    source_date    date not null,
    repo           text,                               -- GitHub owner/name an idea is promoted to; null uses the fallback repo
    status         text not null default 'active' check (status in ('active', 'archived', 'promoted')),
    stability      real not null default 0,            -- 0 with difficulty 0 = never reviewed
    difficulty     real not null default 0,
    reps           int not null default 0,
    lapses         int not null default 0,
    due_at         timestamptz not null default now(),
    last_review_at timestamptz,
    queued_at      timestamptz,                        -- when it last went into a stack; caps the ideas per day
    issue_number   int,
    created_at     timestamptz not null default now()
);

-- squawk-ignore ban-drop-constraint
alter table swipe_events drop constraint swipe_events_action_check;
alter table swipe_events add constraint swipe_events_action_check check (
    action in ('next', 'back', 'like', 'dislike', 'save', 'share', 'idea', 'open', 'good', 'again', 'send', 'dismiss',
               'stop', 'pick_a', 'pick_b', 'keep', 'drop', 'remove', 'read',
               'idea_keep', 'idea_archive', 'idea_later', 'idea_promote')) not valid;
alter table swipe_events validate constraint swipe_events_action_check;
