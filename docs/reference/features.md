# Features in detail

The behaviour of each feature, its settings and its limits. Moved here from the README on 2026-10-20; every setting with its default is also in [CONFIG.md](../CONFIG.md).

## Configuration

Every key is under `FeedEater:` (environment: `FeedEater__Section__Key`). Every setting, its default and what it does is in
[docs/CONFIG.md](../CONFIG.md); `dotnet FeedEater.dll doctor` checks them against the real services, and `doctor --print-config`
prints the effective values with secrets masked.

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings:FeedEater` | yes | Postgres 18 with pgvector |
| `Mcp:Token` | yes | empty: `/mcp` refuses every request |
| `FeedEater:Miniflux:BaseUrl`, `Token` | with `Source:Kind=miniflux` | Miniflux and its API key; read-only use |
| `FeedEater:Llm:BaseUrl`, `ApiKey` | yes | an OpenAI-compatible endpoint with its version path (default `https://api.openai.com/v1/`) and key |
| `FeedEater:Telegram:Token`, `AllowedUserId` | yes | its own bot; the poller only starts with a token. While `AllowedUserId` is 0 the bot answers only `/start`, with your id |
| `FeedEater:Miniflux:BaseUrl`, `Token` | no | use Miniflux as the feed source instead of the built-in reader; read-only use |
| `FeedEater:TimeZone` | no | default `UTC`; set an IANA name such as `Europe/Berlin` for the digest time, quiet hours and shown dates |
| `FeedEater:GitHub:Token`, `FallbackRepo` | no | ideas become issues labelled `triage`; without them 💡 saves ideas to the local list (`/ui/ideas`) |
| `FeedEater:Karakeep:BaseUrl`, `Token` | no | without them there is no Karakeep mirror and no bookmark import |
| `FeedEater:Hindsight:BaseUrl` | no | weekly summary to the `feed-eater` bank; off without it |
| `FeedEater:GitHub:User` | no | stars of this user count as liked items; off without it. With a Telegram token it also turns on the hype autopsy (`/ui/autopsy`) |
| `FeedEater:GitHub:Token` | no | sent on every GitHub call; lifts the 60 requests an hour limit (the autopsy snapshots stop at 40 requests a run without it) |
| `FeedEater:ProfilePath` | no | default `/config/profile.json`; shape in `profile.example.json`; a missing file means the example interests and a notice |

## Built-in feed reader

By default feed-eater fetches the feeds itself (RSS 2.0, Atom, JSON Feed); no Miniflux is needed. Add feeds on `/ui/sources` (a feed
or a site address; the feed link is looked up on the page), import or export OPML there (folders become categories), send
`/feeds` or `/feeds add <url>` to the bot, or run `dotnet FeedEater.dll import-opml feeds.opml` (adds the feeds; the running service
fetches them at its next poll). Removing a feed keeps its items in the archive, now without a feed.

Each feed is fetched with a conditional GET (ETag, Last-Modified) every `FeedInterval`; every failure doubles the wait up to
`MaxBackoff`, and a good fetch resets it. The error shows on `/ui/sources`, and a feed that failed 3 times in a row is named in the digest
header. All fetches go through the same guarded fetcher as page fetch: public addresses only, ports 80 and 443, at most 3 redirects, 10 s,
one request a second per host, answers up to 5 MB. A feed's first fetch keeps only entries from the last `BackfillDays`, so a new
instance embeds days, not whole histories. Items without a date count as published when first seen. An item is stored once per
feed and entry (`items.source_key`); the same article from another feed is marked a duplicate by URL or title as before. Short items
get their article text from the page itself when the digest reads them.

`FeedEater:Source:*`:

| Key | Default | Notes |
|---|---|---|
| `Kind` | `builtin` | `builtin` or `miniflux` (copy entries from Miniflux, as before; full text through Miniflux). Any other value stops startup and fails `doctor`. Switching on a populated database is unsupported |
| `AllowedHosts` | none | Private hosts a feed URL may point at, e.g. a self-hosted RSSHub: exact names, any port (`FeedEater__Source__AllowedHosts__0=rsshub`). Feed URLs only: article and linked-page fetches never reach private hosts |
| `BackfillDays` | 14 | Age limit for the first fetch of a feed |
| `FeedInterval` | 30 min | Per feed |
| `MaxBackoff` | 24 h | Longest wait after repeated failures |
| `PollInterval` | 1 min | How often the ingest loop looks for feeds that are due |

## Web UI

`/ui` is a server-rendered reading UI over the archive: today's digest with 👍 👎 💡 buttons, search, one item, the digest
history, per-feed stats for the last 30 days (to decide which Miniflux feeds to prune), filed ideas and LLM spend (tokens always; a call
whose cost is in neither the gateway's `x-litellm-response-cost` header nor `FeedEater:Llm:Prices` shows as "cost unknown", not as free).
It needs no JavaScript and loads nothing from other hosts (CSP `default-src 'none'`).

`/ui/setup` shows the same checks as `doctor`. Sign in at `/ui/login` with the same value as `Mcp:Token`. The session is an HttpOnly, SameSite=Strict cookie signed with a key
derived from that token (30 days; changing the token signs everyone out). Every POST also checks that Origin or Referer
names the request's own host and carries a per-session anti-forgery value. Logins are limited to 5 a minute. With no
`Mcp:Token` every `/ui` route answers 503. `FeedEater:Llm:MonthlyBudget` (USD, default 0 = none) adds a budget line to `/ui/usage`.
`/mcp` and `/healthz` are unchanged.

`/ui/posts` is the stream of recent posts beyond the digest (filters: category, feed, project, kind, unrated, with AI summary;
keyset paging). `/ui/feedback` lists everything already rated, per 👍, 👎 and 💡, with the 7- and 30-day 👍 rate; a vote can be
changed or cleared there. The top of Today has "the day in brief", built from the digest's stored reads with no extra model call.

### Taste map

`/ui/map` draws every voted item and a seeded sample of up to 1500 other embedded items from the last 90 days as points on a plane
(PCA on the embeddings, computed on the server, cached 10 minutes), as one inline `<svg>` with no script. 👍 is a green circle, 👎 a red
cross (shape and colour), no vote a small grey dot; a heavier vote is a bigger mark. Each profile key is written at the mean of its 10 nearest
points; a dashed square marks a region with items where no digest ever showed one. The month links above the plot (`?month=YYYY-MM`) show the
votes up to the end of that month while every item stays in place, so you can watch the taste form. A table below the plot gives the counts per
vote kind and per profile for use without the picture. It needs at least 3 embedded items and no setting.

## v0.4 additions

- **Story clustering.** An item whose embedding is within `FeedEater:Cluster:Threshold` (default 0.84, cosine) of an earlier
  item from another feed published within `WindowDays` (3) is linked to it (`items.cluster_of`); nothing is hidden. The digest takes
  the earliest unmuted item of each story and skips a story any member of which was triaged on an earlier day. Cards, the Telegram
  digest and search show "also in" and the count. Two titles that name different versions never merge. 0.84 was picked from the
  real archive on 2026-10-06 (same stories 0.80 to 0.86, related ones below 0.80, few samples): raise it if unrelated items merge,
  lower it to 0.82 if the same story shows up twice.
- **Mute a feed.** `/ui/sources` has Mute/Unmute per feed and a posts/week column. A muted feed is still ingested, archived and
  searchable; it stays out of digest candidates and the Today brief, and `/ui/posts` hides it unless "Show muted feeds" is ticked.
- **Telegram search.** `/search words`, or any plain message from the allowed user, returns up to 5 results, one message each,
  with 👍 👎 📌.
- **Weekly review.** Sundays 18:30 in `FeedEater:TimeZone` (only with a Telegram token): counts, top 5 👍, ratings per project, ideas filed, the 3
  feeds that earned most 👍 and 3 mute candidates. Stored in `weekly` and shown at `/ui/weekly`. No model call.

## v0.5 additions

- **Linked pages.** Reddit and Hacker News link posts are teasers, so for recent ones with a short text feed-eater fetches the page they
  link to (and the top 5 HN comments) and gives it to triage and read, fenced as untrusted data. The fetcher refuses private, loopback,
  link-local, tailnet and metadata addresses, pins the connection to the address it checked, follows at most 3 redirects, reads at most
  1 MB of html or text in 10 s, and keeps to 1 request a second per host and `FeedEater:Fetch:MaxPerDay` (400; 0 turns page fetching off). Every rule is in
  [docs/specs/2026-10-06-page-fetch.md](../specs/2026-10-06-page-fetch.md). Tune with `FeedEater:Fetch:*` (`MaxPerPoll`, `BlockedHosts`).
- **Release watch.** Off until `FeedEater:Watch:Source` or `FeedEater:Watch:FallbackPath` is set. What you run comes from the source: a PINS.md
  file path or an https URL (for a private repo, the GitHub contents API URL plus `FeedEater:Watch:Token`); unset or failing, it uses the
  static JSON list at `FeedEater:Watch:FallbackPath` (the format is in `config/watch.example.json`). `config/watch-map.json`
  maps images to upstream GitHub repos (add rows for more). A release newer than the pinned version is read by the model (what changed,
  breaking yes/no/unknown with a quote); one that mentions security, a CVE, a vulnerability or breaking goes to Telegram on its own,
  the rest in the digest header. `/ui/releases` lists products and releases. Each release is announced once; nothing is applied.
- **📌 Save** (Telegram, Today, Posts, Feedback, item page) saves the item to the library; it always shows. With Karakeep set it also mirrors the bookmark there, and Karakeep being down never fails the save.
- **Suggested feeds** on `/ui/sources`: domains behind your 👍 items that no subscribed feed covers, with the feed URL found on the
  homepage (3 domains per poll, rechecked after 30 days). Subscribe in Miniflux yourself.
- **Prompt eval.** `dotnet FeedEater.dll eval --max-usd 0.25 [--max-items 40] [--reads] [--out report.md]` re-runs the current prompts
  over your voted items and prints how relevance agrees with the votes and how often "new" appears with no version or date. It says when
  there are too few votes (under 10 up and 10 down in the sample) and stops at the spend limit. It refuses to run when a call comes back
  with no cost (no `x-litellm-response-cost` header and no `FeedEater:Llm:Prices` entry), because the limit could not be enforced.
- **Taste learning, guarded.** `dotnet FeedEater.dll taste` is an offline report (held-out agreement of a learned model against the
  current ranking). `FeedEater:Taste:Learn` (default false) adds a bounded learned term; with under 100 votes (10 of each kind) it
  refuses and the digest header says so. The default ranking is unchanged.
- **Quiet hours.** `FeedEater:Quiet:From` and `To` (local time, default none) hold the scheduled digest, duels, followed stories, the weekly review,
  the hype autopsy and release alerts. `/quiet` (or `/quiet on|off|status`) and a button on Today toggle it by hand; `/digest` still works. Driving it from the
  senses service is a follow-up.

## v0.6 additions

- **Ask the archive.** `/ask question`, or any plain message ending in `?`, searches the archive (top 8), and the read model answers
  from those items only, citing each claim as `[n]`. Markers that name no source are dropped, and the cited items follow as links. When the
  sources do not answer it, the reply says so. Other plain messages are still a search.
- **Reply to an item.** Reply to a digest item or a search result with free text. The small model turns it into one action: 👍, 👎,
  file an idea (in a project you name, in your words: "idea for <project>: …"), save to Karakeep, mute the feed, or answer a question
  from the item's own text. A bare 👍 or 👎 needs no model call. The item is found from the buttons on the message you replied to, so
  nothing new is stored. A project name that is not a known project (a repo, or its bare name, when GitHub is the idea sink) files nothing and lists the known ones.
- **`/learn`.** `/learn status` gives the vote count and, with enough votes, the held-out agreement of the current and learned rankings.
  `/learn on|off` switches the learned term from the next digest and wins over `FeedEater:Taste:Learn`. The weekly review adds a
  "Ranking" line when the learned model is ahead by the required margin and still off.

## Vote weight

Every vote has a weight (`votes.weight`, above 0 and up to 3, default 1). The ranking learns from weighted votes: the 👍 and 👎
centroids are weighted means, the learned model counts each vote by its weight, a feed's 👍 rate sums weights, and the taste map draws a
heavier vote as a bigger mark. Only the duel writes a weight below 1 today (its 👎 counts half). A vote cast by hand replaces the earlier
one with weight 1, and votes from before the weights keep 1, so the default ranking is unchanged. The thresholds that need "enough
votes" (the learned term, the taste centroids) count votes, not weights; the weekly review and the feedback page count votes too.

## Duel

Twice a day (`FeedEater:Duel:Times`, default 12:30 and 20:30 local, at most `PerDay` 2) the bot sends one message with two items you have
not voted on and asks which you would rather read. They come from the middle of the ranking, where it is least sure (items from the last
7 days, embedded, scored, relevant, one per story, not in a muted feed; the pair that is least alike wins, and a pair is never sent
twice). One tap on ◀ or ▶ records a 👍 for the pick (weight 1) and a 👎 for the other (weight 0.5) and rewrites the message to "You
picked …"; skip records nothing. An item you voted on yourself is never overwritten. Needs six eligible items and the Telegram bot;
`FeedEater:Duel:Enabled=false` turns it off. A slot missed while the service was down is sent only within 2 hours of its time, and quiet
hours hold it. A reply to a duel message is an ordinary message (a search, or a question when it ends in "?").

## Follow this story

🧵 on a digest item or a search hit follows its story for `FeedEater:Follow:Days` (14): the bot sends a root message with a ⏹ button, then
every later item on the same story as a reply to it, checked every 10 minutes (same cluster, or as similar as the cluster threshold minus
0.04 to the root or to an item already sent; muted feeds are skipped). The story closes at the end, at `MaxMessages` (50) or on ⏹ with
the read model's summary of it, or just the links when the model fails. `MaxActive` (5) stories can be followed at once; `/follows` lists them with stop
buttons and `/unfollow [id]` stops one. A reply to an update acts on that item like a reply to a digest item; a reply to the root
message gets the help text. Quiet hours hold the run, and `FeedEater:Follow:Enabled=false` removes the button and the job. Needs the
Telegram bot.

## Hype autopsy

With `FeedEater:GitHub:User` and a Telegram token set, a daily job (05:00) records the GitHub repo of every 👍 item (stars, last push,
latest release). On the 1st of each month (09:00) the autopsy scores each snapshot that is at least 87 days old, by first match: gone
(404 or archived), grew (stars up 20% or more), alive (pushed in the last 30 days), quiet. It splits the result by the model's relevance
and by feed, shows it at `/ui/autopsy` and sends it to Telegram; it calls no model, and quiet hours hold the message. Without
`FeedEater:GitHub:Token` the snapshot job stops at 40 requests a run and the next day carries on. A duel pick is a 👍 too, so it is
snapshotted like any other.

## Weekly ideas

With GitHub issues and a Telegram token set, Mondays at `FeedEater:WeeklyIdeas:At` (09:00 in `FeedEater:TimeZone`) a job collects the week's items
the model marked `improve`, the items you 👍 or 📌 saved, and your new Karakeep bookmarks. One model call ranks them into at most
`MaxFiled` (5) ideas, each with the project it belongs to, the repo or workflow it changes, an effort (S under an hour, M up to a day, L
longer) and a reason. Every idea waits for Keep or Drop; Keep files it as an issue labelled `triage` in that project's repo (`FeedEater:GitHub:FallbackRepo` when none fits), and the
top `TopInMessage` (3) go to Telegram. The model sees the latest 100 issues of each repo (closed ones too, pull requests left out), the ideas earlier runs filed and, with
`FeedEater:WeeklyIdeas:BacklogUrl` (a GitHub contents API URL, token in `BacklogToken`), a backlog file, and leaves out whatever repeats
them; a source URL that was filed once is never offered again (`weekly_ideas`). A configured backlog that cannot be read fails the run,
which is retried with backoff, so nothing is filed without the check. Quiet hours hold it; a run missed while down happens at start; `/ideas` on Telegram runs it now.
There is no 💡 button on digest items: the machinery behind it (the `ideas` list, the web UI, the swipe page) is unchanged, and an old
message's 💡 still works.

## Running a digest on demand

The scheduled digest runs once a day inside 07:30 to 12:00 local time (`FeedEater:TimeZone`). To run one outside that:

- Telegram: `/digest` (only from `FeedEater:Telegram:AllowedUserId`). `/digest resend` sends today's digest again.
- UI: "Run digest now" on `/ui`; "Send today's digest again" sits behind a confirm step.

Both write a cursor, `digest:force` (`run:<date>` or `resend:<date>`), that the digest job picks up within a minute. It ignores
the window and the done-check for that day only, and is cleared after the run, failed or not (a failure is reported on Telegram
and in `/ui`). A stale request from an earlier day is dropped. If today's digest was already sent, a plain run says so and
sends nothing; only `resend` sends the same messages again. Without a Telegram token the digest job is not registered.

## MCP tools

`feed_search`, `feed_read`, `feed_digests`, `feed_digest`, `feed_ideas`. All read-only. `./selftest.sh` calls them, and the three prior-art tools below.

## Prior-art base

A lookup of how existing tools solve a problem: facts with evidence, kept as the `prior-art` namespace of
[Switchknife.Facts](https://github.com/Egoushka/switchknife) in the same database (schema `facts`; the migrator applies the library's
script and the service defines the predicates at start). A fact is a sentence about a tool (`gh:owner/name`, a package, a URL or a
name), an approach or a problem, with the source it came from, when it was true (`valid`) and when the base learned it (`recorded_at`),
so a question can be asked "as known at" an earlier date.

| Tool | Does |
|---|---|
| `prior_art_search` | `question`, optional `known_at` and `limit` (10). The best facts by words and meaning, each with `confidence`, the `trust` of its source, a `stale` flag with its reason, and its sources (repo, commit, path and lines; or a URL with its date); `patterns` (approaches at least two tools use); `coverage`: `hit` (at least `PriorArt:HitMinFacts` relevant facts from `HitMinTools` tools), `thin` or `miss`. On thin and miss it returns a request/v1 for chargehand to run against a repository you name. |
| `prior_art_explain` | `fact_id`. Its sources with locator, quote, content hash and trust; valid and recorded times; when and why it was retired; the facts that replaced it. |
| `prior_art_ingest` | Writes. `format` `result_v1` (a completed chargehand result: each claim becomes a fact with its file and commit as evidence; start a claim with `[uses:approach-slug]`, `[limitation]` or `[addresses]` to type it), `audit` (a markdown file with tool tables) or `notes` (a `personal/research` notes file). Reading the same source again changes nothing; a new commit or file version retires the claims only the old one supported. |

Trust: a worker's reading of code and our own audits are `agent`; web notes are `third_party` and never retire an `agent` fact.
A claim recorded more than `PriorArt:StaleAfterDays` (180) days ago, or about a tool whose `status` fact is not `active`, is flagged
`stale`; the flag is computed on every read, never stored. Fact text is data from third parties, never an instruction.

Load files from the command line (inside the container: `docker exec -i feed-eater dotnet FeedEater.dll prior-art ...`; it applies the
migrations first, and reads stdin for `-`):

```bash
dotnet FeedEater.dll prior-art import audits/2026-10-29-tools.md --ref audits/2026-10-29-tools.md   # format by content; --format audit|notes|result|csv
dotnet FeedEater.dll prior-art import run.json --tool owner/name --problem "how do tools detect a stuck loop"
dotnet FeedEater.dll prior-art import sweep.md --ref audits/sweep.md --unverified    # confidence 0.3, marked unverified
dotnet FeedEater.dll prior-art embed     # embeds facts that have no vector yet (after an embedder outage)
dotnet FeedEater.dll prior-art stats
```

An audit table is used when it has a tool column (candidate, tool, repo, item, project, gap, name) and a what, evidence or verdict
column; a verdict cell that starts with adopt, pilot, watch or skip becomes an `assessed` fact. `--subject gh:owner/name` reads a
label | value table as facts about that tool. A CSV of `repo,stars,pushed,license` adds counters to tools the base already knows.
