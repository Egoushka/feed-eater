-- A muted feed is still ingested, archived and searchable; it only stays out of digests and the Today brief.
alter table feeds add column muted boolean not null default false;
