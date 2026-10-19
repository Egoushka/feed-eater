-- Swipe mode PR B: where each queued card came from and what the yield model expected of it, so exploit and explore can be compared.
alter table swipe_sessions
    add column queue_sources   text[] not null default '{}',   -- 'exploit' | 'explore', parallel to queue_items
    add column queue_predicted real[] not null default '{}';   -- the yield model's probability at selection; NaN = no model yet
alter table swipe_events add column predicted real;
