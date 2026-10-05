# feed-eater v1 — four-week validation

Started 2026-10-07 (first scheduled digest, 07:30 Kyiv). Fill one row each Monday for the week just ended.

How to read each number:
- **Digests by 08:00**: days that week (of 7) with a digest in Telegram by 08:00 Kyiv. `hz sql feed-eater-db "select local_date, status, sent_at from digests order by 1 desc limit 7"`.
- **👍 / 👎**: button presses that week. The digest header shows the 7-day 👍 rate.
- **Rate**: 👍 / (👍 + 👎).
- **Spend**: "spend this month" from the latest digest header (budget: $10 per 30 days on the LiteLLM key `feed-eater`).
- **Useful `feed_search`**: one query run that week through the `feed` MCP target that found something worth having, and what it found.

| Week ending | Digests by 08:00 | 👍 | 👎 | Rate | Spend | Useful `feed_search` | Notes |
|---|---|---|---|---|---|---|---|
| 2026-10-12 | /7 | | | | | | |
| 2026-10-19 | /7 | | | | | | |
| 2026-10-26 | /7 | | | | | | |
| 2026-11-02 | /7 | | | | | | |

## Decision after week 4

- 👍 rate under 60%: tune `FeedEater__Weights__*` and `FeedEater__Caps__*` in `feed-eater/compose.yaml` (homelab-gitops, by PR), not in code. Change one group at a time and watch a week.
- 👍 rate 60% or more, 7/7 digests on time, spend within budget: v1 passes; plan v0.2 from the parked findings in `docs/HANDOFF.md`.

## Log

Free-form notes: digests that looked wrong, items that should have been ranked higher, ideas filed to Plane that were useless.
