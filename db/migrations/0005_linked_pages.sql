-- Reddit and Hacker News link posts: where the post points, and the page text and top comments fetched for it.
alter table items add column link_url text;
alter table items add column hn_id bigint;
alter table items add column extra_text text;
alter table items add column extra_fetched_at timestamptz;   -- set after one attempt, whether or not it produced text
create index items_extra_pending_idx on items (published_at) where extra_fetched_at is null and (link_url is not null or hn_id is not null);
