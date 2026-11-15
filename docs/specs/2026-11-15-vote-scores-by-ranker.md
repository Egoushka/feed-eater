# Votes and swipes as scores per ranker version (feed-eater/P03)

Ranker id = first 8 hex of SHA-256 over the `Weights` values, the learned on/off switch and the profile keys + descriptions (decided by the owner 2026-11-15).

- Migration 0037: `items.ranker text`, set by the digest's `SetScoresAsync` with the scores.
- A vote or swipe like/dislike on an item counts under the item's ranker; no new table. `dotnet FeedEater.dll ranker` prints, per ranker in time order: votes, 👍 hit rate, swipes, swipe like rate; fewer than 10 votes is marked "too few".
- Known limit: swipe queues score items without writing them, so a swipe counts under the ranker of the item's last digest scoring, not the swipe queue's ranking.

## Acceptance (machine-run, `RankerTests`)
1. Same inputs give the same id (profile order does not matter); changing a weight, the learned switch or a profile text changes it.
2. A vote on an item scored by ranker r1 is counted under r1 (DB test).
3. The report lists rankers in time order with hit rate and n, "too few" under 10 votes.

Not in this PR: the weekly report line, and stamping swipe queues.
