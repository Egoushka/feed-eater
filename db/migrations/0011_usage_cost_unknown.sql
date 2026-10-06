-- A call whose cost no gateway header and no configured price gave is NULL (unknown), not 0.
-- Rows written before this stay as they are: their 0 may be a real zero or an unknown.
alter table llm_usage alter column cost drop not null;
