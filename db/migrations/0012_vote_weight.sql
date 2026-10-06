-- How much a vote counts when the ranking learns: 1 for a plain vote, up to 3 for a strong signal, below 1 for a weak one.
-- Rows written before this keep weight 1, so the ranking they train is unchanged.
alter table votes add column weight real not null default 1 check (weight > 0 and weight <= 3);
