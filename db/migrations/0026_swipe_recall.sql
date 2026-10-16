-- Swipe mode PR C: recall cards and post seeds. A queue slot is now (kind, id): an item, a recall card or a seed.

-- Spaced repetition: one row per question. step 0 = new or just forgotten; the ladder (1, 3, 7, 16, 35 days) lives in code.
create table recall_cards (
    id         bigserial primary key,
    source     text not null check (source in ('item', 'note')),
    ref        text not null,                                   -- 'item:123' or 'note:<uuid>': what it was made from
    item_id    bigint references items (id),                    -- set for source 'item'
    front      text not null,
    back       text not null,
    category   text,
    step       int not null default 0,
    due_at     timestamptz not null,
    reps       int not null default 0,
    lapses     int not null default 0,
    suspended  boolean not null default false,
    created_at timestamptz not null default now()
);
create index recall_cards_due_idx on recall_cards (due_at) where not suspended;

create table recall_reviews (
    id          bigserial primary key,
    card_id     bigint not null references recall_cards (id) on delete cascade,
    session_id  bigint references swipe_sessions (id) on delete set null,
    at          timestamptz not null default now(),
    result      text not null check (result in ('good', 'again')),
    step_before int not null                                    -- a good answer at step 1 or more means it was remembered, not just seen
);

-- Sources already turned into cards (or tried), so a model call is never repeated for the same thing.
create table recall_done (
    ref text primary key,
    at  timestamptz not null default now()
);

-- One-line post angles drafted from liked items; "send" files them as an idea.
create table post_seeds (
    id          bigserial primary key,
    item_id     bigint not null unique references items (id),
    angle       text not null,
    category    text,
    status      text not null default 'new' check (status in ('new', 'sent', 'dismissed')),
    sent_to     text,
    created_at  timestamptz not null default now(),
    resolved_at timestamptz
);

alter table swipe_sessions add column queue_kinds text[] not null default '{}';   -- 'item' | 'recall' | 'seed'; empty = all items

-- ref_id now points at an item, a recall card or a seed, by kind.
alter table swipe_events drop constraint swipe_events_ref_id_fkey;
alter table swipe_events drop constraint swipe_events_action_check;
alter table swipe_events add constraint swipe_events_action_check check (
    action in ('next', 'back', 'like', 'dislike', 'save', 'share', 'idea', 'open', 'good', 'again', 'send', 'dismiss'));
