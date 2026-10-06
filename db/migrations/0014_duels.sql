-- A pair of unvoted items sent as one message; the tap on one of them (or skip) answers it. winner is null while open and after a skip.
create table duels (
    id          bigserial primary key,
    a           bigint not null references items (id),
    b           bigint not null references items (id),
    sent_at     timestamptz not null default now(),
    winner      bigint references items (id),
    answered_at timestamptz,
    check (a <> b),
    check (winner is null or winner in (a, b))
);
create index duels_sent_at_idx on duels (sent_at);
