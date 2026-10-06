-- A story seen in several feeds: members point at the first item of the story. The original rows are never changed.
alter table items add column cluster_of bigint references items (id);
alter table items add column clustered boolean not null default false;   -- considered for clustering; false rows are checked on the next poll
create index items_cluster_of_idx on items (cluster_of) where cluster_of is not null;
-- Only the last few days can cluster, so older rows are done; the rest are picked up by the first poll after the deploy.
update items set clustered = true where published_at < now() - interval '3 days';
