-- The feed's own URL, from Miniflux: the release watch trusts only GitHub's releases.atom feeds, not any item that links a release.
alter table feeds add column feed_url text;
