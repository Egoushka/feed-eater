-- How many daily runs GitHub failed (not the rate limit) for the repo of a 👍 item that has no snapshot yet.
-- After the third the job stores an empty snapshot, so one repo that always fails stops being the oldest candidate.
create table snapshot_failures (
    item_id  bigint primary key references items (id),
    failures int not null
);
