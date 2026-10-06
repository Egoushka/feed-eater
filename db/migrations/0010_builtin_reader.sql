-- Fetch state of the built-in reader, kept per feed.
alter table feeds
    add column etag            text,
    add column last_modified   text,
    add column last_fetched_at timestamptz,
    add column next_fetch_at   timestamptz,
    add column fail_count      int not null default 0,
    add column last_error      text;

-- Feeds added here take ids above anything Miniflux has handed out, so the two never collide.
create sequence feed_id_seq start with 1000000001;
alter table feeds alter column id set default nextval('feed_id_seq');

-- One key per entry across both source kinds: 'mf:<entry id>' from Miniflux, '<feed id>:<sha256 of guid or url>' from the built-in reader.
-- Existing rows keep a null key and keep deduping on miniflux_entry_id: rewriting them would rewrite every vector index entry at startup.
alter table items add column source_key text;
create unique index items_source_key_idx on items (source_key);
