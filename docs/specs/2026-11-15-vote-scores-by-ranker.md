# Votes and swipes as scores per ranker version (feed-eater/P03) — spec, not built

There is no ranker version in the code today. Weights (`Weights`), the learned term (`/learn` switch) and the profile text all change the order, and nothing records which combination ranked an item. So P03 needs a migration; it cannot merge without the owner.

## Design
- `ranker_id` = short hash of the effective `Weights` + learned on/off + the profile file hash, computed when `SelectAsync` / swipe scoring runs.
- Migration (the number follows feed-eater#71's 0036): `items.ranker text`, set by `SetScoresAsync` with the score.
- A vote or swipe is a score of the item for that ranker: no new table. `eval ranker` joins `votes` and `swipe_events` to `items.ranker` and prints, per ranker: shown, voted, 👍 rate (hit rate), swipe-right rate.
- The same table goes into the weekly report.

## Acceptance (machine-run)
1. Same weights and learn state give the same `ranker_id`; changing one weight changes it.
2. `SetScoresAsync` stores it; a vote on that item is counted under it (DB test).
3. The report lists rankers in time order with hit rate and n; rankers with n < 10 are marked "too few".

## Decision for the owner
Is a hash of weights + learn state + profile the right "version", or a hand-bumped string in config? Recommended: the hash (no step to forget).
