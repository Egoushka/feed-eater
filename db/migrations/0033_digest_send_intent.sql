-- digest crash safety (DigestRun.cs SendAsync): a crash between the Telegram send and recording it must not
-- resend a part. One slot per message part, in send order; 0 means a send was attempted but never confirmed
-- (Telegram's real message ids are always positive).
alter table digests add column sent_message_ids bigint[] not null default '{}';
