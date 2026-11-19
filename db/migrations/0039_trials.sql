-- Adoption board trials: one row per run of a card on a throwaway branch of its repo.
-- One new table, nothing existing is touched; it is only written when Trials:Enabled and the owner queues a card.
create table trial (
    id           bigserial primary key,
    card_id      bigint not null references card (id),
    status       text not null default 'queued' check (status in ('queued', 'running', 'passed', 'failed', 'blocked', 'cancelled')),
    branch       text not null,                  -- the throwaway branch the brief asks for: trial/<card id>-<slug>
    repo         text not null,
    requested_at timestamptz not null default now(),
    started_at   timestamptz,
    finished_at  timestamptz,
    tests        text not null default '',
    benchmark    text not null default '',
    diff_stat    text not null default '',
    result_md    text not null default ''        -- summary, blocked or failed reason, and the state suggestion; it never changes the card
);

-- squawk-ignore require-concurrent-index-creation
create index trial_card_id_idx on trial (card_id, id desc);

-- Both indexes are on the table created above, still empty and unseen by any reader, so there is nothing to block.
-- A card has at most one trial waiting or running.
-- squawk-ignore require-concurrent-index-creation
create unique index trial_one_active_per_card on trial (card_id) where status in ('queued', 'running');
