-- Which ranker (a hash of weights, learned on/off and profiles) last scored the item, so votes and swipes can be counted per ranker.
alter table items add column ranker text;
