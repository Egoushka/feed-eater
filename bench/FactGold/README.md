# FactGold: measuring fact extraction before building the collector

Spec: `docs/specs/2026-11-08-fact-gold-set.md` (decision D-113). Run the checks with `scripts/accept-fact-gold.sh offline|live`.

| File | What |
|---|---|
| `gold.json` | 34 items (30 positive, 4 negative): source URL, date, text snapshot, expected facts with match anchors |
| `prompt.md`, `prompt-v2.md` | the extractor prompts (v2 caps at 3 facts and skips offers the reader cannot act on) |
| `extract.py` | runner: item text in, `Switchknife.Facts` v1 JSON out, through Bifrost; records tokens |
| `score.py` | scorer: `--check-gold`, `--self-test`, or a results file |
| `results/` | raw outputs of the runs below |

```bash
python3 -I bench/FactGold/extract.py --model openrouter/deepseek/deepseek-v4.1-flash --tag mine
python3 -I bench/FactGold/score.py bench/FactGold/results/mine.json
```

## Result (2026-11-08)

Model `openrouter/deepseek/deepseek-v4.1-flash`, temperature 0, one call per item.

| Run | Prompt | Items recalled | Fact recall | Strict precision | Negatives clean | Kind right (of matched) |
|---|---|---|---|---|---|---|
| `run1-deepseek-p1` | v1 | **30/30** | 47/50 | 0.38 (47 of 123 emitted) | 2/4 | 43/47 |
| `run3-deepseek-p2` | v2 | 29/30 | 43/50 | 0.61 (43 of 70 emitted) | 3/4 | 40/43 |

A rerun of v1 through `scripts/accept-fact-gold.sh live` gave 30/30 items, 46/50 facts, precision 0.38 (not bit-identical at temperature 0, but the same picture).

Deadlines: 4/4 right on both runs (matched facts that have a gold deadline). Acceptance (at least 20/30) is met by the first run with no prompt change, so the stop rule never came into play.

`prompt-v2.md` was written after reading the v1 misses on this same set, so v2 is tuned on its test data; v1 is the unbiased number.

Gemini 3.5 Flash-Lite could not be measured: Bifrost answers `502 upstream_credentials_exhausted` ("all configured keys returned permanent per-key errors: provider gemini") for every call. That is a gateway key problem, not a model result.

**Cost:** about $0.06 by token count for the two scored runs (`est_usd` in the results, with starting prices of $0.15/$0.60 per M tokens), plus a crashed first attempt and probes. `lab spend` shows $0.08 for the whole 7-day window of the `opencode-mac` key (170 requests), so the total is far under the $3 limit.

## Top 3 failure patterns

1. **Too many facts, not too few.** v1 emitted 123 facts for 50 expected; the extras are mostly true but minor (a second VPS tier, release-note lines such as OAuth refresh fixes in the MCP SDK changelog) and 5 of the 6 false positives on negatives come from a Cloudflare post on free programs for nonprofits and governments, read as `opportunity` although the reader cannot take them. v2 (cap 3, skip offers the reader cannot act on) lifts precision from 0.38 to 0.61.
2. **Facts that are not the headline get dropped.** The misses are secondary facts: a Terminal-Bench 64% line deep in a benchmark post, one row of a deprecation table, the VS Code agent feature in a long release note. Tightening the cap to 3 trades recall for precision: v2 additionally lost the Beam and MiMo models, the Astra "turned everything off" lesson, the Anthropic Team $500 credit and both Aspire facts.
3. **Kind is fuzzy at the edges.** 4 of 47 matched facts carry another kind than gold: a model launch with a price cut read as `new_model` not `price_change`, a benchmark post tagged `opportunity`, `idea` versus `code_idea`. Recall does not depend on it, but any filter by kind will.

## What this does and does not show

- It shows the extractor finds the headline facts of clean public text (blog posts, changelogs, listings, pricing pages) and gets amounts and deadlines right.
- It does not show precision at the level a store needs (0.38 to 0.61 strict), nor behaviour on noisy input: long threads, scraped HTML with navigation, non-English pages, text that tries to instruct the reader (one such item was dropped, see the spec).
- The gold is small (30) and its anchors are lenient on purpose (two substring groups per fact). They were written before any model output was seen. After the first run, one anchor (`a-spawn`, alternatives for "turning them off") was widened because the scorer self-test showed the gold's own claim did not match it; no score changed.
- Sampling was deterministic by hash, but the categories were chosen by me to cover the brief, so this is a coverage test, not a sample of the feed.
