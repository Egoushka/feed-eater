-- Feed URLs found on the homepages of domains the owner keeps liking; checked once a month at most.
create table feed_suggestions (
    domain     text primary key,
    feed_url   text,
    status     text not null check (status in ('found', 'none', 'failed')),
    checked_at timestamptz not null
);
