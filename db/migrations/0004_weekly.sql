-- One row per Sunday review: the figures as JSON (the UI renders from them) and the message that went to Telegram.
create table weekly (
    week_of  date primary key,            -- the Sunday the review ran
    built_at timestamptz not null,
    sent_at  timestamptz,
    report   jsonb not null,
    message  text not null
);
