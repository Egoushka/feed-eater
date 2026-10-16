# Swipe mode and the Instagram self-profile

Status: DRAFT for approval. No code until approved.

Goal: the owner's scrolling habit (Instagram memes, mostly) should leave him better off, by his own definition, and give him a private map of where his
attention goes and how it left him, like the one Meta and Google already hold. "Better off" is not work or income: life is wider than that. Laughing,
sharing something with a friend, sport, music, cooking, places, art, language and reflection count the same as learning or a work idea, and humour is a
legitimate thing to want more of, not a habit to eliminate. What counts is **owner-defined** (see Categories); this spec does not fix the list. Two parts,
built and shipped separately:

- **Part 1, swipe mode:** a phone web app inside feed-eater, one card per screen, endless, with a hard session cap, ranked for yield (saves, recalls,
  ideas), never for time on screen.
- **Part 2, Instagram import:** read Instagram's "Download your information" JSON export into tables and show one report, "where my attention went",
  next to what Meta inferred about him and what `profile.json` says he wants.

## Decisions

| # | Decision | Why |
|---|---|---|
| D1 | Stay inside feed-eater; no new repo. | Needs `items`, embeddings, `votes`, the scorer, `ideas`, auth and the migrator. A new repo would copy all of them. Strong reason to split would be a second user or a native app; neither exists. |
| D2 | Web app (PWA) under `/swipe`, small hand-written JS file, no framework, no build step. | The existing UI forbids scripts by CSP. Swipe needs touch events. One 300-line file served from the same host keeps the "loads nothing from other hosts" promise. |
| D3 | The objective is predicted **yield**; dwell time is logged and never read by ranking. | The point is to be the opposite of Instagram's objective. Enforced by a test (see Guards). |
| D4 | The swipe path calls no LLM and no embedding API. | Every card is precomputed by jobs. Keeps latency under 100 ms and the cost at zero per swipe. |
| D5 | Swipe votes go into the existing `votes` table. | Telegram, `/ui/feedback`, centroids, the feed 👍 rate and the learned model then work unchanged. |
| D7 | What counts as yield is a list of **categories the owner defines** (config file), not code. Work is one category among many. | The first draft leaned on tech and income because that is what the archive holds today. The ranker must not push the whole life toward work. |
| D8 | The self-map joins three kinds of signal: what was done (views, saves, dwell), how it felt (one tap at session end), and the body and life signals feed-eater's owner already records (Oura sleep, Last.fm). | Clicks show what happened, not how it left him. Felt is the only signal that tells a good hour from a bad one. |
| D6 | Part 2 cannot be coded until its parser is checked against a real export (see Part 2, gate G1). | The file names in public write-ups conflict and are third-party; see "Export format, what is known". |

## Facts this rests on (read from `origin/main` fa27084, 2026-10-16)

- `Scorer.Score` = best profile fit + `Taste` × (cos C+ − cos C−) + `Prior` × (feed 👍 rate − 0.5) + `Learned` × (p − 0.5)
  (`Ranking/Scorer.cs`). `LogisticModel` trains on `LabeledVector(X, Liked, Weight)` and needs 100 votes with 10 of each kind (`WeightsOptions.MinPositives`
  is 10 for the centroids).
- `votes(item_id pk, value ±1, weight real 0..3, at)`; `SetVoteAsync(itemId, value, ct, weight)` overwrites (`Storage/FeedbackStore.cs`).
- `ideas`, `IdeaFiler` and `IIdeaSink` (Plane Intake, else local list) already file a 💡 (`Plane/IdeaSinks.cs`).
- `saved(item_id, karakeep_id)` backs the 📌 button.
- UI: server-rendered, cookie session, CSP `default-src 'none'; style-src 'self'`, POSTs need same-origin plus an anti-forgery value (`Ui/UiEndpoints.cs`).
- Migrations: `origin/main` has up to 0020; the v0.9 spec reserves 0020 to 0023 (`docs/specs/2026-10-07-v0.9-hooks.md:83`). Swipe takes the next free
  numbers when it is built (named below by table, not by number).
- Items carry plain text only (HTML stripped), no images. A card for an archive item is therefore text: title, feed, the stored read (`reads.why`/`summary`)
  or the first 280 characters, and a link out.
- **UNKNOWN, checked at plan time:** how the feed-eater host is reached from a phone (tailnet name, HTTPS). A PWA needs HTTPS to install and to keep the
  cookie `Secure`. If the host is tailnet-only, the phone needs Tailscale on; that is acceptable and also keeps Instagram-style distribution out of it.

## Part 1. Swipe mode

### Cards
Four kinds, one screen each:

1. **item** — an archive item, ranked (below). Shows title, feed, why-it-matters line, source link. Never shown twice within 30 days; items already voted on
   are not shown as item cards (they come back as recall cards if liked).
2. **recall** — a spaced-repetition card: a question on the front, answer on tap. Sources: (a) items the owner liked or saved, turned into 1 to 3 question/answer
   pairs by the small model at like time (batch job, not on the swipe path); (b) the owner's notes (Nytka `list_notes`; shape UNKNOWN, spike S3, optional,
   off when absent). Scheduling: a simple Leitner/SM-2 style ladder (intervals 1, 3, 7, 16, 35 days; "again" resets to 1). FSRS is a later swap behind the
   same table. Due cards come first, at most `Swipe:MaxRecallPerSession` (default 12), so a backlog never eats the session.
3. **seed** (optional; only categories flagged `seed: true`, work-like ones by default) — "post seed": a one-line angle for a post, drafted from a liked/saved item plus a profile project ("You saved X; angle: Y"). Two buttons: send, dismiss.
   Send files it through the existing `IdeaFiler` as an idea titled `[post seed] …` (Plane Intake in FEED, else the local list). Direct handoff to Plainsight
   is spike S4: Plainsight has "manual notes" as a signal source; whether it accepts a remote write is UNKNOWN. Until S4 is answered, Plane Intake is the bridge.
   At most `Swipe:MaxSeedsPerSession` (default 2).
4. **summary** — the last card of every session (below).

### Categories (owner-defined)
A category is `{key, description, share, events, seed}` in a file next to `profile.json` (`categories.json`, same loader and the same
"missing file means a neutral example plus a notice" rule). `description` is embedded like a profile entry; an item or an Instagram author belongs to
its nearest category by cosine, with a floor below which it is "uncategorised". `share` is the owner's wanted share of cards (they must not have to sum to
1; the rest goes to the model). `events` says which events count as yield for that category (default: all in the table below), because yield is not the
same thing everywhere: for humour it is *laughed* and *shared*, for sport it may be *saved to do*, for reflection a recall passed. Categories are labels on
top of `profile.json`, which keeps describing projects and topics for the digest.

**The category list is an open question for the owner (Q1), deliberately not invented here.** Candidates he named: laughing and sharing with friends,
body and sport, music, cooking, places, art, language, reflection, work. Until he sets them the swipe feed runs with one category, "everything", and the
cards show no category label.

**Supply caveat (Q2):** the archive is built from RSS feeds the owner chose for work, so today it contains almost no humour, sport or music items. A category
without items cannot be served. Either he adds feeds for them (OPML import exists), or Part 1 stays work-heavy by supply and the report shows it.

**Selection is per category, not global.** A single yield model would learn that work saves outweigh laughs and drown everything else. So: slots are
split by `share`; inside a category the model only ranks. Slots without a `share` go to the model's global ranking.

### Gestures and the log
Swipe up = next (neutral, it says nothing about the card). Swipe right = save. Swipe left = dislike. Swipe down = back one card. Double-tap = like.
Buttons for like, dislike, save, share (the phone's share sheet: a friend gets the link; this is the "laughing and sharing" signal), 💡, open sit on the card (so nothing depends on a gesture, and accessibility works). Recall cards: tap reveals, then
right = good, left = again.

Table `swipe_sessions(id, started_at, ended_at, end_reason, card_cap, time_cap_s, cards_shown, felt, felt_at)` and
`swipe_events(id, session_id, kind, ref_id, category, position, shown_at, dwell_ms, direction, action, rank_source, predicted, created_at)` — one row per card
shown, finished when the card is left. `action` ∈ next, back, like, dislike, save, share, idea, open, good, again, send, dismiss. `rank_source` ∈ exploit, explore,
due, seed. `predicted` is the model's yield estimate at selection time (for calibration, section Ranking). The client sends the row on leave and
`navigator.sendBeacon` on page hide, so a killed tab still logs. A reload mid-card writes one row with `action = next`.

A like writes `votes(+1, weight 0.5)`, a dislike `votes(-1, weight 0.5)`, a save `votes(+1, weight 1)` plus the existing 📌 path when Karakeep is
configured, 💡 the existing idea flow. A manual Telegram vote overwrites a swipe vote (existing rule: your own deliberate vote wins). Weights are 0.5 because a
thumb flick is cheaper than a Telegram tap.

### Ranking for yield
**Yield event**, the label the ranker learns from, counts only things that cost effort or show lasting value. These weights are starting values and a
category's `events` may narrow them:

| Event | Weight in the yield label |
|---|---|
| idea (💡 or seed sent) | 1.0 |
| share (sent to someone) | 0.8 |
| save (right swipe, 📌) | 0.7 |
| recall passed on a review at least 1 day later (the card was remembered) | 0.7 |
| like | 0.3 |
| open (link tapped) | 0.1 |
| dislike | −0.5 |
| dwell, next, back | 0 |

**Model.** The existing `LogisticModel` is trained a second time with the yield label (positive when the weighted sum is at least 0.5) over the
embeddings of shown item cards. It is called `YieldModel`. Until it has `Swipe:YieldMinEvents` (default 30) positive events and 30 negatives, the
score is the existing `Scorer` score. After that: `score = Yield × (p_yield − 0.5) + (existing Scorer score)`, `Yield` default 1.0, under `WeightsOptions`
so `eval` can compare it with today's ranking on the same votes (existing `EvalRunner`).

**Selection.** Per slot (inside the category the slot was split to): with probability `1 − ε` take the highest score among unseen candidates published in the last `Swipe:CandidateDays` (default 14);
with probability ε (default 0.2) take an explore item: a random candidate from outside the top 30% whose embedding is least similar to the last 20 cards
shown. Explore rows are logged `rank_source = explore`, so their yield rate is measurable against exploit rows; this is the only honest test of whether the
model beats chance. Recall cards and seeds fill their quota first, items take the rest. Default mix per session: due recall (up to 12), seeds (up to 2),
items for the remainder of the 40 cards.

**Guards.**
- `dwell_ms` is never selected by the training queries, the ranker or the report's "yield" numbers. A test greps the SQL of the two training queries and
  fails if `dwell` appears; another test shuffles dwell values and checks the ranking output is identical.
- Fast swipes carry no penalty: a quick up-swipe is `next`, which is label 0. Only an explicit left swipe or the dislike button is negative.
- The Part 1 report (below) shows dwell next to yield on purpose, so the owner can see whether time and value diverge.

### Session cap (hard)
Server-side, not a client timer. `POST /swipe/next` returns the **summary** card instead of a new card once `cards_shown ≥ Swipe:Cards` (default 40) or
`now − started_at ≥ Swipe:Minutes` (default 15). After the summary the endpoint answers `closed` until `Swipe:CooldownMinutes` (default 180) have passed,
and at most `Swipe:SessionsPerDay` (default 3) sessions start per local day. A reload cannot restart the clock because state is in `swipe_sessions`.
This stops the app, not the owner: Instagram stays one tap away. The cap exists to make the swipe mode a bounded dose; the claim that it reduces Instagram time
is not tested here and is a question for the weekly report (Instagram minutes before and after, once Part 2 or the owner's own screen-time log gives it).

Summary card: cards seen, minutes, saved, shared, ideas filed, recall passed/failed, the session's yield per card against the 7-session average, and the
three categories most seen. Then **one question, one tap: "How did this leave me?"** with three options (lighter / same / drained; wording and the choice
of three are Q3 for the owner), stored as `swipe_sessions.felt` (+1, 0, −1). Skipping it is allowed and stored as null. No streaks, no "come back" copy.
`felt` is session-level, so it is not a training label for single cards (too coarse, and it would reward whatever ended a session well). It feeds the
report below and one rule: after three `drained` sessions in a row the summary card says so in plain words and suggests a shorter cap; it changes nothing by itself.

### Serving and security
- Routes: `GET /swipe` (shell), `GET /swipe/app.js`, `/swipe/app.css`, `/swipe/manifest.webmanifest`, `/swipe/sw.js`, `POST /swipe/next`,
  `POST /swipe/event`, `POST /swipe/end`. Same cookie session, same-origin check and anti-forgery value as `/ui`; the value is embedded in the shell
  and sent as a header.
- CSP on `/swipe*` only: `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; manifest-src 'self'; form-action 'self';
  frame-ancestors 'none'; base-uri 'none'`. The `/ui` policy is not loosened. No images, no fonts, no third-party host.
- Service worker caches the shell and JS only. Cards are never cached, so there is no offline mode and nothing stale to leak.
- Card text comes from feeds and is untrusted: rendered with `textContent`, never `innerHTML`; links open with `rel="noopener noreferrer"` and only `http(s)`.
- All new routes answer 503 without `Mcp:Token`, like `/ui`.

### Part 1 report page: `/ui/swipe`
Server-rendered like the rest. Per week and per category: sessions, cards, minutes, yield events per 100 cards, exploit vs explore yield rate, recall pass rate, shares, share of
cards against the owner's wanted `share`, dwell per card (shown, not used), and the felt tap per session. This is how we find out whether the idea works at all.

### Config
`Swipe:Enabled` (default false), `Swipe:CategoriesPath` (default `/config/categories.json`), `Cards`, `Minutes`, `CooldownMinutes`, `SessionsPerDay`, `Epsilon`, `CandidateDays`, `YieldMinEvents`, `Yield`,
`MaxRecallPerSession`, `MaxSeedsPerSession`, `Recall:Enabled`, `Seeds:Enabled`. Documented in `docs/CONFIG.md`; `doctor` gets one `swipe` line
(enabled, enough candidates, HTTPS or not, recall source reachable).

### Jobs
`RecallGenerator` (after a like/save, within 10 min, small model, capped at `Swipe:RecallPerDay`, default 10, counted in `llm_usage`), `SeedGenerator` (daily,
from items saved or liked in the last 3 days, capped 3 a day), `YieldTrainer` (retrains in the morning job's slot, like `LearnedTaste`).
Tables: `recall_cards`, `recall_reviews`, `post_seeds`, `swipe_sessions`, `swipe_events`.

## Part 2. Instagram import and the attention report

### Export format, what is known (all UNVERIFIED against a real export)
No export is on this Mac or in the repo (searched `~/Downloads`, `~/Projects`, Spotlight, 2026-10-16). The public descriptions are third-party blogs, not Meta
documentation, and they disagree on folder names across export versions:

- JSON is a selectable format; "all time" must be chosen or older data is silently cut ([Stashr](https://stashr.me/blog/export-instagram-data),
  [Marqly](https://www.marqly.com/blog/export-instagram-saved-posts)).
- Likes: `your_instagram_activity/…/liked_posts.json`; saves: `saved_posts.json` and `saved_collections.json`, holding account, link, Unix timestamp and
  nothing else.
- Posts viewed and videos watched carry **author and timestamp only, no link, no caption**
  ([Data Transfer Initiative note](https://portmap.dtinit.org/articles/watch-history1.md/)).
- Ads: `ads_interests.json` (the interest categories Meta assigned), and "Topics Instagram associated with the account".
- Which folder holds viewed/watched/topics (`ads_and_topics/`, `ads_information/…` or elsewhere) differs between sources.

Consequence for the design: the largest signal (posts viewed) has **no content**, so "topics over time" cannot come from captions. It comes from per-author
aggregates plus a small owner-confirmed author → topic map (below). That is a limit of the data, not a choice.

### Gate G1 (owner action, then one tool run)
1. Owner requests the export: Instagram → Accounts Center → Your information and permissions → Export your information → Create export → format **JSON**,
   date range **All time**, categories: Your Instagram activity, Ads information, Preferences (leave media off; it keeps the zip small). Meta says it can take up
   to 48 hours. I do not do this: it needs his login, which I do not enter.
2. `dotnet FeedEater.dll import-instagram <zip|folder> --inspect` (built first, in the Part 2 PR): lists every JSON file by path, size, top-level shape
   and first-record keys; reads nothing into the database. Paste its output into Appendix A of this spec.
3. Only then are the parsers fixed to real names and shapes. The importer finds files by **basename and shape**, not by full path, so a renamed folder does not
   break it; an unrecognised file is reported, not guessed.

### Importer
CLI `import-instagram <zip|folder> [--inspect] [--dry-run]`, plus `/ui/instagram` upload for archives under `Instagram:MaxUploadMb` (default 200; the CLI has
no limit). Reads the zip as a stream, parses only `*.json`, never extracts or stores media or the raw files. Zip-slip safe (entries are read, not written),
caps on entries (50 000) and uncompressed size (2 GB). Idempotent: a row's key is a hash of (kind, timestamp, author, link), so re-importing a newer export adds
only the new rows.

Tables (the "self-profile"):
- `ig_events(id, kind, ts, author, url, source_file, key unique)`; `kind` ∈ like, save, post_viewed, video_watched, ad_viewed, ad_clicked (the set is fixed by G1).
- `ig_interests(id, label, kind, snapshot_date, embedding)`; `kind` ∈ ad_interest, topic. One snapshot per import; labels embedded with the existing embedder
  (labels only, a few hundred short strings, cents).
- `ig_authors(handle, category_key null, set_by)`; `set_by` ∈ owner, suggested. The category is one of the owner's categories (Q1), not a profile key.
- `ig_imports(id, at, archive_sha256, files_read, rows_added)`.

Privacy: data stays in the owner's database. The only things that leave the host are interest labels (to the embedding API, as today for profile text) and,
only when `Instagram:SuggestTopics` is true (default false), author handles to the small model to suggest a topic each, shown for the owner to confirm. No
caption or media is ever read.

### Report: `/ui/attention`
One server-rendered page, inline SVG like the Taste map (no script):
1. **Volume over time:** posts viewed, videos watched, likes, saves per week (the swipe-mode events appear as a second series after the first sessions).
2. **Who:** top 25 authors by views per quarter, with like/save rate per author (views are the only attention measure Meta exports, and they have no dwell
   time either, so this is count, not minutes).
3. **Categories over time:** weekly share of views per owner category, using `ig_authors.category_key`. A meme account tagged *humour* is a result, not noise. The 50 most-viewed authors cover most views (to be measured on the
   real export, not assumed); the page shows how many views are untagged and offers the tagging form for the top untagged authors.
4. **What Meta thinks vs what I say:** each `ig_interests` label placed at its nearest `profile.json` key or owner category (cosine from embeddings, same vectors the scorer
   uses). Three lists: Meta interests with a close profile key (agreement), Meta interests far from every key (what Instagram made of me that I did not
   choose), and profile keys no Meta interest comes near (what I want that Instagram does not see). Each shown with the similarity number and the nearest key.

5. **Alongside (the felt and body join):** per day and per week, put side by side: Instagram views and swipe minutes, the felt taps, the night's Oura sleep score
   after a heavy scroll day, and Last.fm listening (a stand-in for the *music* category). Descriptive only: each pair shows n, and with fewer than 20 days
   it says "too few days". No correlation is presented as a cause. How feed-eater reads Oura and Last.fm is Q4/spike S5; both are optional and the section
   hides without them.

No score, no verdict. Read-only; deleting all `ig_*` rows is one button (`POST /ui/instagram/purge`, confirm step).

## Build order and pull requests
1. **PR A: swipe core** — migrations, `/swipe` shell, cards of kind item and summary, session cap, event log, `votes` writes, `/ui/swipe` report, tests. Usable alone.
2. **PR B: yield ranking** — `YieldModel`, selection with ε, calibration columns, the dwell guard tests, `eval` comparison.
3. **PR C: recall and seeds** — generators, SR scheduling, seed → `IdeaFiler`. Spikes S3 and S4 first.
4. **PR D (blocked on G1): Instagram import and `/ui/attention`.** Starts with `--inspect` only, so G1 can run while A to C are built.

## Open questions for the owner
- **Q1.** The category list, each one's wanted `share`, and which events count for it. Not invented in this spec.
- **Q2.** Will he add feeds for non-work categories (humour, sport, music, …), or accept that Part 1 serves only what the archive holds?
- **Q3.** The wording of the felt tap and whether three options are the right number.
- **Q4.** Whether Oura and Last.fm go in the report at all (privacy: sleep and listening next to Instagram habits in one page).

## Spikes (each ≤ 1 hour, answer written into this spec before the PR that needs it)
- S1: how the feed-eater host is reached from the phone and whether it is HTTPS (decides PWA install and `Secure` cookie).
- S2: touch behaviour on iOS Safari: vertical swipe vs pull-to-refresh and the browser's back gesture on a standalone PWA; `touch-action` and `overscroll-behavior` settings.
- S3: Nytka `list_notes` shape and a stable note id, for recall cards from notes.
- S5: how feed-eater reads Oura and Last.fm (both live behind the homelab gateway as MCP tools; whether they have a plain HTTP API feed-eater can call with a token is UNKNOWN). Fallback: a daily push of two numbers into a small `body_daily` table by a separate job.
- S4: whether Plainsight accepts a manual note over its MCP/CLI from outside; if not, Plane Intake stays the bridge.

## Risks
- **The cap is self-enforced.** Mitigated only by friction (cooldown, daily limit). It is a decision aid, not a lock.
- **Cold start.** With 10 up and 1 down votes (handoff 2026-10-07) the yield model has no data for weeks; the first sessions run on the existing score plus ε.
  The report shows exploit vs explore so a model that does not beat random is visible.
- **Work swallowing everything.** Work items are the ones the archive has and the ones with the clearest saves. Per-category shares are the guard; the report shows share of cards against wanted share.
- **Felt tap fatigue.** One tap, skippable, once per session; if it is skipped most of the time the join is empty and the report says so.
- **Gaming:** saves are cheap; a save flick counts 0.7 but recall passing a day later counts equally, which is the check against saving what is never used.
- **Feed content is text-only:** a swipe feed of titles is a weaker hook than video. That is intended, and it is also why the owner may drift back to Instagram;
  the Part 2 volume chart (Instagram views per week before and after) is the measurement.
- **Meta changes the export** between versions; hence shape-based detection and `--inspect`.
- **Hot spots shared with v0.9:** `ServiceRegistration.cs`, `FeedEaterOptions.cs`, `Program.cs`, migrations 0020 to 0023. Rebase and renumber at build time.

## Out of scope
Scraping Instagram or any logged-in session; showing Instagram content in the feed; push notifications; video or images; a second user; a native app;
anything that rewards time in the app.

## Built differently (PR A)
- Migration is `0024_swipe.sql` (past the v0.9 reservation); tables `categories`, `swipe_sessions`, `swipe_events`. `swipe_events` has an `acts` array next to `action`
  (all acts on a card; `action` is the strongest) and one row per card, created by the first act or the leave event.
- The CSP on `/swipe` adds `img-src 'self'` for the icon; `/ui` is unchanged.
- The session queue is fixed at start (`queue_items`, `queue_categories`), so the client names only a position and the server derives every item id.
- A like or dislike by swipe is a vote of weight 0.5; a save is weight 1 plus the 📌 Karakeep bookmark when configured.
- Not in PR A: the `doctor` swipe line (comes with PR B), recall cards, seeds, the yield model and epsilon exploration.
- The `/ui` nav always lists Swipe.

## Built differently (PR B)
- Migration `0025_swipe_ranking.sql`: `swipe_sessions.queue_sources` and `queue_predicted` (NaN = no model yet), `swipe_events.predicted`; `rank_source` is now `exploit` or `explore`.
- No `YieldTrainer` job: the model is trained from the event log when a session starts (a few hundred rows train in well under a second). Its inputs are the
  acts of every card shown, labelled by `YieldLabels` (idea 1.0, share 0.8, save 0.7, like 0.3, open 0.1, dislike −0.5; positive from 0.5); the `recall` weight waits for PR C.
- The weights `Yield`, `Epsilon` and `YieldMinEvents` live in `Swipe:`, not `Weights:`.
- Explore: each slot is explore with probability ε; of ten random candidates from outside the top 30% by score, the one least similar to the cards already picked.
- "Compare with today's ranking" is the held-out agreement (5-fold AUC) of the yield model against centroids on the same log, shown on `/ui/swipe`, next to exploit vs
  explore good-act rates and the model's mean expected rate. `EvalRunner` (which re-runs the LLM prompts) is not involved.
- The `doctor` line `swipe` exists: off, or unseen item count and category count; an invalid `categories.json` fails it.

## Appendix A: real export file list
_Empty until G1. Paste the `--inspect` output here._
