# Peer gaps

For each project, what its 7 closest open-source peers have that it lacks, each with a source page. Harvested 2026-11-01 for five projects: tin, approvals, chargehand, nytka-server, switchknife. The other active projects in `repos.toml` are not covered yet (rollout is #57).

| Repo | Gaps | Useful | Not useful | Unknown |
|---|---|---|---|---|
| [tin](tin.md) | 18 | 6 | 9 | 3 |
| [approvals](approvals.md) | 15 | 7 | 4 | 4 |
| [chargehand](chargehand.md) | 12 | 4 | 6 | 2 |
| [nytka-server](nytka-server.md) | 15 | 1 | 10 | 4 |
| [switchknife](switchknife.md) | 21 | 6 | 6 | 9 |
| **Total** | **81** | **24 (29.6%)** | **35** | **22** |

The stop rule from Yehor was: fewer than 1 useful gap in 5 (20%) means narrow the harvester to prior-art search only. 29.6% is above it, so the rule did not fire. The 20% figure is a starting value from Yehor, not calibrated.

## What to trust

- **Judged by Claude, not by Yehor.** "Useful" means the gap fits what the project is for and costs less than it gives. Yehor has not reviewed any verdict; treat the ratio as a first estimate.
- **Every gap has a source URL.** All 49 distinct URLs returned HTTP 200 on 2026-11-01. 19 claims were spot-checked against the page text. Anchors (`#...`) were not click-tested. A gap with no source would be marked UNKNOWN and left out; none was.
- **"Absent here" was checked** by reading or grepping the repo; the check is written under each gap.
- **Not produced by the CLI.** The peer-harvest job (`peer-harvest`, PR #56) covers fingerprints and README cards only. The deep read and gap matching (`prior_art_gaps`, #53) is unbuilt and blocked by chargehand#683. These lists came from research subagents given the same task (7 peers, deep read, own-repo check) plus my judgment. The CLI also needs a LiteLLM key, DB, GitHub token and a peer-set file that this session did not have.
- `nytka-server` is `parked` in `repos.toml`; it was included because the task named it.

## Files

- `<repo>.md`: readable list. Per gap: verdict, detail, source URL, the "absent here" check, and a one-line "worth doing?" with cost in plain words.
- `gaps.jsonl`: one JSON object per gap, for the rooms hook below.
- The same 81 gaps are stored as sourced facts in the prior-art base (`prior_art_ingest`, format `notes`, `source_ref` = `feed-eater:docs/peer-gaps/<repo>.md`). Ask `prior_art_search` to read them back.

## Rooms hook (prepared, not wired)

`gaps.jsonl` lines are `{"repo", "peer", "gap", "source_url", "judged"}` with `judged` one of `useful`, `not useful`, `unknown`. In the rooms spec (switchknife PR #52) a project is a room and a gap is an `observation` event, so one line maps to one event. Nothing writes to any room; K4 research rounds (switchknife#51) are not started.

## Redo

Rerun the research per repo, regenerate the files, and re-ingest with the same `source_ref`: reading the same source again changes nothing, and a changed file retires only the claims the old version alone supported.
