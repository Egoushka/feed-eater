# Fact collector, step 1: a gold set and a measured extractor

Decision D-113 (`~/Projects/context/ai-setup/decisions.md`): the fact collector measures extraction quality before any store or UI is built. This change is that measurement, nothing else.

## What it adds

All under `bench/FactGold/`; no service code, no migration, nothing deployed.

1. **Gold set** `gold.json`: 34 items, each with its source URL, date and a text snapshot (at most 6000 characters, as the extractor sees it).
   - 30 positive items, each with 1 to 4 expected facts: `kind`, `subject`, `claim`, and where the text gives them `amount`, `date`, `deadline`.
   - 4 negative items that carry nothing collectable (opinion, a milestone post, a question, a personal story). Any fact emitted for one is a false positive.
   - Coverage: opportunities (paid .NET/Angular/agent gigs and bounties, free API credits, free GPU hours, cheap VPS deals, hackathons with prizes), provider price and policy changes, new models with benchmark numbers, code approaches worth stealing, ideas.
   - Sources: 19 posts from the feed-eater archive, 11 public web pages fetched 2026-11-08 (LowEndBox, We Work Remotely, Devpost, GitHub issues, vendor docs and pricing).
2. **Extractor** `prompt.md` (v1) and `prompt-v2.md`, run by `extract.py` through Bifrost. Output is the `Switchknife.Facts` wire format v1 (camelCase): kind in `predicate`, claim in `text`, subject in `subject.name`, amount in `value`, date in `validFrom`, deadline in `validTo`, confidence in `salience`.
3. **Scorer** `score.py`: matches extracted facts to gold, prints recall and precision.
4. **Acceptance script** `scripts/accept-fact-gold.sh` and the results of the runs in `bench/FactGold/results/`.

## Acceptance checks

| # | Check | Pass |
|---|---|---|
| 1 | `scripts/accept-fact-gold.sh offline`, gold | 30 positive items; every fact has a known kind; every alternative group of every `match` has a member that occurs in the item's snapshot; negatives have no facts |
| 2 | `scripts/accept-fact-gold.sh offline`, scorer | the gold's own claims score 30/30 items and precision 1.0; an empty result scores 0/30 |
| 3 | `scripts/accept-fact-gold.sh offline`, lint | `ruff check` clean |
| 4 | `scripts/accept-fact-gold.sh live`, extract | the extractor answers for all 34 items through Bifrost with a cheap model |
| 5 | `scripts/accept-fact-gold.sh live`, recall | at least 20 of the 30 positive items recalled |

Stop rule: if after two prompt or model changes recall is still under 10 of 30, the bet failed; stop and report the misses.

## Choices

- **Match rule, frozen before any model output was seen.** An expected fact matches an emitted fact of the same item when every group of its `match` list has an alternative that is a substring of the normalised subject + text + value + object (lower case, `$` and `,` removed). One emitted fact satisfies at most one expected fact. An item is recalled when at least half (rounded up) of its expected facts match. The anchors are text-grounded: check 1 fails if an anchor does not occur in the snapshot.
- **Kind, deadline and amount are scored separately.** A fact that says the right thing under the wrong kind still counts for recall; kind accuracy and deadline accuracy are reported next to it.
- **Precision is strict and a lower bound.** The gold lists the headline facts of an item, not every true statement, so an extra correct fact counts against precision. Negatives are reported separately as "clean" items.
- **Honest sampling.** Archive items were picked in `md5(id)` order within each category, web items in md5-of-title order from the feeds named above; no item was swapped for being easy. Four distractors are included for precision.
- **Cheap models only**, through Bifrost (`opencode-mac` key). Public web content may go to training providers (D-113); no employer data, no one's private words, no secrets.
- **Python standard library** for the runner and scorer: no new dependency, no .NET change.

## Left out on purpose

- ClankerNation/OpenAgents#143 was dropped as a bounty example: its body addressed automated readers and asked them to paste their own platform instructions into a header. That is text unsafe to feed an extractor unfiltered; the collector needs an input guard for it (follow-up), and the item is not in the gold.
- `onyx-dot-app/onyx#2281` was dropped: the snapshot has no bounty or amount, only a feature request, so no honest expected fact exists.

## Not in this change

No fact store, no UI, no wiring into the archive or any job, no `Switchknife.Facts` dependency (the shape is matched, not imported). Gemini Flash-Lite could not be measured: Bifrost answers 502 `upstream_credentials_exhausted` for provider `gemini` (see `bench/FactGold/README.md`).
