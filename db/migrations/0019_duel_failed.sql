-- Set when the send of a duel threw. The row stays, because Telegram may have delivered the message after all and its buttons name this id;
-- the pair is never picked again and the duel does not count toward the daily cap.
alter table duels add column send_failed_at timestamptz;
