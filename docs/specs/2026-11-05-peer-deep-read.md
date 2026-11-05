# Peer harvester, part 2: deep read, feature matching, gaps

Issue: Egoushka/feed-eater#53. Builds on #52 (fingerprint and card, on main). Independent of PR #55 (#51): this change does not touch
`PriorArtSearch.cs`, `PriorArtTests.cs` or `bench/`. Feeds #54 (weekly ideas from gaps) and Egoushka/research#15 (the gate).

## What it adds

1. **Deep read.** `PeerHarvestJob` asks chargehand (`POST /v1/runs`, `request/v1`, preset `prior-art`, `budget_usd` 0.50) to read each repo
   of the peer set and stores the `result/v1` through the existing importer as a `repo-read` episode: `source_ref = gh:owner/name#peer-template-v1`,
   hash = commit. The question is built from the peer set file's `aspects` and names the claim tags. The own repo is read with the same template.
2. **Fourth claim tag** `[feature:slug]` → `has_feature` (object: the `feature:<slug>` entity). Existing tags are unchanged.
3. **Recheck.** A repo is read again only when its latest release is a new minor or major since the last read (patch releases and no release: no
   rerun). The last read release is the live `latest_release` fact of the tool. `--force` overrides.
4. **Feature resolution.** After a read, embeddings of the repo's new `feature` entities are compared with the existing ones; pairs at cosine
   ≥ `PeerHarvest:FeatureMergeSimilarity` (0.85) go to one cheap call (confirm or reject). Confirmed: `merged_into` and an `entity_aliases` row,
   plus a `same_as` fact (trust agent). Rejected: a `distinct_from` fact, so the pair is not asked twice. `prior-art merges` lists them; `--undo <id>`
   reverses one. No migration: decisions are facts, the merge is the library's own columns.
5. **`prior_art_gaps`** (MCP, read-only; CLI `prior-art gaps`): features that at least `min_peers` (2) peers have and the own repo lacks, each with the peers
   (best evidence and confidence per peer), the approaches those peers use; own-only features; approaches at least `min_peers` peers use. Also
   grouped per peer (`by_peer`). Computed from live facts at `known_at` (default now), following `merged_into`.
6. **`prior-art verify --sample N`**: N random live code-evidence facts (locator with repo, commit, path, lines); `GET repos/<repo>/contents/<path>?ref=<sha>`
   must exist and have the cited lines.
7. **Peer set parser fix.** The real `peers/feed-eater.yaml` lists peers as `- gh: owner/name # comment` and has `template:` and `aspects:`; the #52 parser
   rejects that shape. It reads both shapes and exposes `Template` and `Aspects`.

## Choices

- Spend guard: a set run stops starting reads once the summed `usage.usd` of its reads reaches `PeerHarvest:SetSpendCapUsd` (5.00, the pilot cap). A
  result with `usd: null` counts as its full `budget_usd`.
- A fact whose locator carries `label: marketing` (a README's boast, not code) counts for nothing in the gaps.
- Undoing a merge retracts its `same_as` fact and records a `distinct_from` fact, so the pair is not merged again.
- A read at a commit already stored is a duplicate (the commit is the episode hash), so a rerun without new commits writes nothing.
- Card refresh on a new release follows from the README sha in the card's episode hash; the card hash is not changed.
- Merges are not bi-temporal: `known_at` filters facts, `merged_into` is read as it is now.
- A peer is any other tool with a live `has_feature` fact, narrowed to the peer set file's list when `PeerHarvest:SetUrl` is set or `peers` is passed.

## Acceptance checks

Offline (CI; `scripts/accept-peer-deep-read.sh offline` runs 1-8 and exits non-zero on any failure):

| # | Check | Pass |
|---|---|---|
| 1 | `[feature:slug]` parsing in a `result/v1` import | a fixture claim becomes a `has_feature` fact on the right entity; other tags unchanged |
| 2 | Merge confirm and reject | confirm sets `merged_into`, adds an alias and a `same_as` fact; reject adds `distinct_from` and no merge; a decided pair is not asked again; undo restores |
| 3 | Gap computation over a fixture | expected rows, peers, `min_peers`, own-only rows, shared approaches, merged features counted once, `known_at` hides later facts |
| 4 | Recheck rule | new minor/major reads; patch, same release and no release do not; `--force` reads |
| 5 | Rerun with no new commits | the `prior-art` episode count is unchanged and chargehand is not called a second time |
| 6 | Request shape and spend guard | `request/v1` carries preset `prior-art`, `budget_usd` 0.50, `gh:` path for peers; a set run stops at the cap |
| 7 | Verify sampler | fake GitHub: existing path with the lines resolves; missing path, bad ref and a line past the end do not |
| 8 | Peer set parser | the real file shape parses: 7 peers, template, 7 aspects |

Live pilot (needs chargehand#683 merged and deployed so `gh:owner/name` resolves; run `scripts/accept-peer-deep-read.sh live` with the database url and a
chargehand key in the environment):

| # | Check | Pass |
|---|---|---|
| L1 | per tool, live `has_feature` facts from `repo-read` episodes | 8 tools (7 peers + own), each ≥ 5 |
| L2 | `prior-art verify --sample 10` | ≥ 9 of 10 resolve |
| L3 | `prior_art_gaps(repo="Egoushka/feed-eater")` | ≥ 5 rows, each ≥ 2 peers and evidence |
| L4 | second `peer-harvest` run, no new commits or releases | `prior-art` episode count unchanged |
| L5 | spend of account `peer-harvest` for the pilot | ≤ $5.00 |

## Not in this change

No migration. No schedule. No change to `prior_art_search`. Weekly ideas (#54) and the labelling gate (research#15) are not started.
