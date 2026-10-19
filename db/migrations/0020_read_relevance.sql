-- The reader model's verdict after reading the whole item (0-3). Null on older rows and on replies without it; null is never dropped.
alter table reads add column relevance int;
