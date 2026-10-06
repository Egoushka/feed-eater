-- Releases of products the owner runs, seen in release feeds. One row per release; announced once.
create table releases (
    repo        text not null,              -- upstream GitHub repo, lower case
    tag         text not null,
    version     text not null,
    title       text not null,
    url         text not null,
    item_id     bigint references items (id),
    detected_at timestamptz not null default now(),
    newer       boolean not null,           -- newer than the running version when seen; false rows only feed "latest seen"
    summary     text,
    breaking    text,                       -- yes | no | unknown
    evidence    text,
    urgent      boolean not null default false,   -- notes mention security, CVE, vulnerability or breaking
    announced_at timestamptz,               -- sent on Telegram on its own
    digest_date date,                       -- or listed in this day's digest
    primary key (repo, tag)
);
