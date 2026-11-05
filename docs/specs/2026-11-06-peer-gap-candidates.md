# Weekly ideas: the week's top peer gaps as candidates

Issue: Egoushka/feed-eater#54 (peer harvester pilot, issue 6 of 7). Builds on #53 / PR #61 (`prior_art_gaps`, on main). Does not touch open PR #55.
Feeds Egoushka/research#15 (the gate), which needs the gap candidates to arrive in the same inbox with the same Keep / Drop outcomes as every other idea.

## What it adds

A candidate source `peer-gap` for `WeeklyIdeasJob`. No new filer, no migration, no new schedule.

1. **Selection.** For the peer set file (`PeerHarvest:SetUrl`), `PriorArtGaps.QueryAsync(set.Repo, minPeers 2)` gives the gap rows. They are ranked by
   `peer count × mean confidence` (ties: feature name), URLs already offered are skipped *before* the cut, and the top 2 become leads. Two is the
   per-set cap; weekly ideas file at most 5 per run, so gaps take at most 2 of them. Skipping before the cut means the next run offers the next gaps.
2. **Source URL** `feed-eater://gaps/<owner>/<repo>/<feature-slug>` (repo = the peer set's own repo). It goes through the same "a source URL is offered once" rule:
   - filed or dropped URLs (`weekly_ideas`) are skipped, as for any lead;
   - a gap URL given to the ranking is remembered (cursor `peer-gap-offered:<url>`, set once the ranking answered), so a later run does not offer it again whether
     the ranking kept it or not.
3. **Candidate text** names the feature, the peers that have it and one evidence link each (`https://github.com/<repo>/blob/<commit>/<path>#L<a>-L<b>`; the
   evidence `url` when it has one; else the peer's repo page). The prompt gets a short form; the issue body gets the full list.
4. **Target repo** is the peer set's own repo, whatever project the ranking names: the job overwrites it for a `peer-gap` lead.
5. **Ranked like the rest.** The leads join the week's other leads; the model ranks, dedupes against issues, backlog and earlier ideas, and Keep / Drop works as
   before. Gap leads are never links in Telegram (the `feed-eater://` scheme is not a web link).
6. **Run log** names the gap leads offered to the ranking and the gap ideas staged, by URL.
7. A gap source that fails (peer set file unreachable, prior-art store down, no deep read yet) is logged and skipped; the week's other leads still run.

## Acceptance checks

Offline (`scripts/accept-peer-gap-candidates.sh offline`, needs Docker for the Postgres container):

| # | Check | Pass |
|---|---|---|
| 1 | Fixture of 4 gap rows | the source offers exactly 2, the ones with the highest `peers × mean confidence`; a second call with the first call's URLs skipped offers the other 2; a third offers none |
| 2 | URL and text | URL is `feed-eater://gaps/<owner>/<repo>/<slug>`; text names the feature and every peer with one evidence link each; project is the set's repo |
| 3 | Job: at most 2 | with 4 gap rows and other leads, the ranking prompt has 2 `peer-gap` leads and the total stays within the lead cap |
| 4 | Job: second run | a second weekly run puts none of the first run's gap URLs in its prompt, also when the first ranking kept none of them |
| 5 | Job: filed or dropped | a gap URL in `weekly_ideas` is not offered |
| 6 | Job: staging | a kept-by-ranking gap lead is staged in the inbox with the set's repo as project even when the model names another, a body with the peers and links, and no `feed-eater://` link in the Telegram message |
| 7 | Job: log | the run log names the offered gap URLs |
| 8 | Job: failing source | the run still ranks the other leads and stages them |
| 9 | Existing weekly ideas tests | unchanged and green |

Live (after the first weekly ideas run with the feed-eater peer set; needs the deep reads of #53 stored, which wait for chargehand#683, PR chargehand#692):

| # | Check | Pass |
|---|---|---|
| L1 | the run log of that run | at most 2 `peer-gap` URLs under "Peer-gap candidates offered"; they are the ones in the prompt |
| L2 | `select name from cursors where name like 'peer-gap-offered:%'` | the same URLs |

## Choices

- Offered-once memory lives in the existing `cursors` table: no migration, so the PR needs no extra approval. A `weekly_ideas` row cannot hold it (a row means filed or dropped).
- Gaps are computed when the weekly run starts, from live facts, not stored between runs.
- One set today (`PeerHarvest:SetUrl`); the source takes the set's repo, so a second set would be a loop, not a redesign.

## Not in this change

No change to the gaps tool, the peer harvest, the Telegram buttons or the Keep path. The labelling gate (research#15) is not started.
