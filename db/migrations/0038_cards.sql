-- Adoption board: what the owner's own repos run (inventory_item) and the proposal cards built from it (card).
-- Two new tables, nothing existing is touched; the app only reads them when the cards job or page is used.
create table inventory_item (
    id          bigserial primary key,
    kind        text not null check (kind in ('nuget', 'npm', 'image', 'mcp', 'skill')),
    name        text not null,
    version     text not null default '',
    repo        text not null,
    source_path text not null,                 -- file the item was read from, relative to the repo
    seen_at     timestamptz not null default now(),
    unique (repo, kind, name, source_path)
);

create table card (
    id                bigserial primary key,
    term              text not null,                    -- the inventory term the card is about, lower case; one card per repo and term, so a held or dropped term never comes back
    title             text not null,
    what              text not null,
    repo              text not null,
    where_in_stack    text not null check (length(btrim(where_in_stack)) > 0),
    evidence          jsonb not null check (jsonb_typeof(evidence) = 'array' and jsonb_array_length(evidence) > 0),
    evidence_checked  boolean not null default false,   -- false: the signal titles were fetched, the pages were not read
    effort            text not null default '',
    gain              text not null default '',
    state             text not null default 'proposed' check (state in ('proposed', 'assess', 'trial', 'adopt', 'hold', 'dropped')),
    state_reason      text not null default '',
    created_at        timestamptz not null default now(),
    state_changed_at  timestamptz,
    source_signal_ids bigint[] not null default '{}',   -- items.id of the feed items that backed the card
    unique (repo, term)
);
