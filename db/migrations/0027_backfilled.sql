-- Old entries from a newly added Miniflux feed are archived but never embedded or offered as candidates.
alter table items add column backfilled boolean not null default false;

update items set backfilled = true where embedding is null and published_at < ingested_at - interval '14 days';
