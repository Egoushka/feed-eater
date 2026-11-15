-- The signals behind an item's score (profile fit, taste, feed prior, learned), shown as the digest's reason line.
alter table items add column rank_signals jsonb;
