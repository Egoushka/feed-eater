-- What a repo looked like when its item got a 👍, so a later autopsy can say what the project did since.
-- One row per 👍 item. repo is null when the item links no GitHub repo (counted, never scored); stars is null when the repo was already gone.
-- autopsy_month is the key of the autopsy that scored the row, so a row is scored once.
create table repo_snapshots (
    item_id       bigint primary key references items (id),
    repo          text,
    taken_at      timestamptz not null,
    stars         int,
    pushed_at     timestamptz,
    release_tag   text,
    created_at    timestamptz,
    autopsy_month date
);
create index repo_snapshots_pending_idx on repo_snapshots (taken_at) where autopsy_month is null;

-- One row per monthly autopsy: the figures as JSON (the UI renders from them) and the message that went to Telegram.
create table autopsy (
    month    date primary key,            -- the first of the month the autopsy ran
    built_at timestamptz not null,
    sent_at  timestamptz,
    report   jsonb not null,
    message  text not null
);
