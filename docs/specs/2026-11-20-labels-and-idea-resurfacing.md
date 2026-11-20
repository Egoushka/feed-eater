# Spec: label rows for every swipe, and FSRS resurfacing of lost ideas (2026-11-20)

Status: approved by Yehor 2026-11-20 (brief from session c41d07ea). Sources: `scratch/wh-rethink/lab/ANALYSIS.md` (steps 2 and 5),
`r3/3-human.md` row 4 (FSRS resurfacing inside the swipe stack, never as a due count), `t1-ideas.md` (9 ideas that exist only in a transcript).

## Why
Yehor said on 2026-11-03: "all my ideas and stuff just dissapear at the end". The idea extraction over three weeks found 9 ideas with no trace
outside a transcript. The swipe stack is the one surface he opens every day, so the ideas come back there, scheduled by memory, not by a list.
A second need: one learner should read every surface's yes/no decisions. Wheelhouse keeps a ledger with the same row shape.
Value hypothesis (for him): an idea that returns at the moment it is about to be forgotten gets kept or promoted more often than one left in a note.
Cheapest test: after 3 weeks, at least 3 of the 9 seeded ideas are kept or promoted (starting value, not sourced). Stop result: all 9 dismissed or snoozed three times means the seed list was noise, not the mechanism.

## Decisions
- **L1 Labels.** Table `labels`, append-only (a trigger rejects update and delete). Columns: `ts`, `surface` (`feed-eater`), `item_kind`, `item_id` (text), `action`.
  `action` is one of `kept | dismissed | snoozed | opened | promoted | ignored`. No free text, nothing else.
- **L2 Mapping.** Every swipe act becomes one label (table in `LabelActions`): like, save, good, keep, pick_a, pick_b are `kept`; dislike, dismiss, drop, stop, remove,
  read are `dismissed`; again and later are `snoozed`; open is `opened`; idea, send, share, promote are `promoted`. A card left with no act writes `ignored` once, on its first
  event. Undo does not delete a label (append-only); the vote it undid is restored as before.
- **L3 Ideas.** Table `idea_items`: his exact words, source session, source date, repo guess, status (`active | archived | promoted`) and the FSRS memory state.
  The 9 lost ideas live in `docs/seeds/lost-ideas.json` (not in `src/`: nothing shipped in the image names the owner, `OwnerTermsTests`). `Swipe:Ideas:SeedPath` points at the file; startup seeds by `seed_key` (insert, do nothing on conflict), so a restart or a second run adds nothing. An empty path seeds nothing.
- **L4 FSRS.** A C# port of the FSRS-6 memory model (default weights, request retention 0.9, no fuzz, no intra-day learning steps), checked against
  ts-fsrs 6.0.0 (MIT) with golden values in the tests. A .NET NuGet port was not used: none is maintained by the FSRS project, and the model is about 60 lines.
  `ts-fsrs` itself is TypeScript, and this repo is .NET.
- **L5 Resurfacing.** A new queue kind `lost_idea`. Ideas whose retrievability is at or below 0.9 (due) and that are active come in lowest retrievability first, at most
  `Swipe:Ideas:PerSession` (default 1) per session and `Swipe:Ideas:PerDay` (default 2) per local day, shuffled in with the ranked items. Never a push, never a count.
- **L6 Swipe is the review.** Acts: `idea_keep` (right swipe; FSRS Good), `idea_archive` (left swipe; status archived, never returns), `idea_later` (FSRS Again; back within a day or so),
  `idea_promote` (files a GitHub issue in the idea's repo, else the fallback repo, with `Value hypothesis` and `Cheapest test` sections left empty for him; status promoted, never returns).
  A promote that cannot file (issues not configured) fails and changes nothing.

## Machine-run acceptance checks (tests)
1. Seeding twice leaves exactly 9 `idea_items`; each holds the exact words from `t1-ideas.md`.
2. One swipe on an idea writes exactly one `labels` row (surface, kind `lost_idea`, id, mapped action) and updates the schedule (due date moves, reps +1).
3. No idea appears twice in one stack; a session queue holds each `idea_items` id at most once.
4. Archived and promoted ideas never resurface, whatever their due date.
5. No count of ideas, due or otherwise, is in any card, summary, page or API response (the response bodies are scanned).
6. `labels` rejects update and delete. Every act any card kind offers maps to a label action.
7. The FSRS port matches ts-fsrs 6.0.0 on the golden values: first-review stability and difficulty per grade, retrievability, and the next stability and difficulty after Good and Again.
8. Per-session and per-day caps hold.

## Migration
`0040_labels_ideas.sql`: new tables only, plus a wider check on `swipe_events.action`. No data is rewritten.
