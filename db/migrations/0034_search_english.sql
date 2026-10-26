-- GitHub Egoushka/feed-eater#31: stem word forms ("running" finds "run") instead of matching 'simple' tokens verbatim.
drop index items_search_idx;
alter table items drop column search;
alter table items add column search tsvector generated always as (
    setweight(to_tsvector('english', title), 'A') ||
    setweight(to_tsvector('english', left(content, 100000)), 'B')) stored;
create index items_search_idx on items using gin (search);
