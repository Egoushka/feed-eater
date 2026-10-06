-- Items saved to Karakeep from the 📌 button; one row per item makes the button idempotent.
create table saved (
    item_id     bigint primary key references items (id),
    karakeep_id text not null,
    at          timestamptz not null default now()
);
