-- A story the owner follows for a while: its root message in Telegram, the later items sent as replies to it, and how many.
create table follows (
    id              bigserial primary key,
    root_item_id    bigint not null references items (id),
    started_at      timestamptz not null,
    ends_at         timestamptz not null,
    root_message_id bigint,
    last_item_id    bigint references items (id),
    status          text not null default 'active' check (status in ('active', 'closed')),
    sent            int not null default 0
);
create index follows_active_idx on follows (id) where status = 'active';

-- Items already sent for a follow, so none repeats and a new item can be compared with them.
create table follow_items (
    follow_id bigint not null references follows (id) on delete cascade,
    item_id   bigint not null references items (id),
    at        timestamptz not null default now(),
    primary key (follow_id, item_id)
);
