# feed-eater

Reads every entry Miniflux collects, keeps them all in a searchable archive, and each morning sends the few that
matter to Telegram with a concrete suggestion where one exists. Button presses teach the ranking; 💡 files the
suggestion into Plane Intake. Any MCP client can search the archive.

Design: [docs/specs/2026-10-05-feed-eater-design.md](docs/specs/2026-10-05-feed-eater-design.md).

## How an item travels

1. Miniflux fetches the feed. feed-eater copies the entry (every 30 min), strips HTML, marks duplicates by
   canonical URL or title, and embeds it.
2. At 07:30 Europe/Kyiv the new items are ranked by vector math: fit to the projects and topics in
   `profile.json`, similarity to what was liked or disliked, and the feed's 👍 rate.
3. The top 60 get a one-line verdict from a small model; the top 12 of those that matter are read in full by a
   stronger one, which writes a summary, why it matters, and a suggestion.
4. Telegram gets a header and one message per item with 👍 👎 and 💡.

## Configuration

Every key is under `FeedEater:` (environment: `FeedEater__Section__Key`). Defaults are in
[src/FeedEater/FeedEaterOptions.cs](src/FeedEater/FeedEaterOptions.cs).

| Key | Required | Notes |
|---|---|---|
| `ConnectionStrings:FeedEater` | yes | Postgres 18 with pgvector |
| `Mcp:Token` | yes | empty: `/mcp` refuses every request |
| `FeedEater:Miniflux:Token` | with `Source:Kind=miniflux` | Miniflux API key; read-only use |
| `FeedEater:Llm:ApiKey` | yes | LiteLLM virtual key with a budget |
| `FeedEater:Telegram:Token`, `AllowedUserId` | yes | its own bot; the poller only starts with a token |
| `FeedEater:Plane:Token` | for 💡 | Plane API key |
| `FeedEater:Karakeep:Token` | no | without it Karakeep saves are not used |
| `FeedEater:Hindsight:Token` | no | weekly summary to the `learning` bank |
| `FeedEater:ProfilePath` | no | default `/config/profile.json`; shape in `profile.example.json` |

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
| `Kind` | `builtin` | `builtin` or `miniflux` (copy entries from Miniflux, as before; full text through Miniflux). Switching on a populated database is unsupported |
| `AllowedHosts` | none | Private hosts a fetch may reach, e.g. a self-hosted RSSHub: exact names, any port (`FeedEater__Source__AllowedHosts__0=rsshub`) |
| `BackfillDays` | 14 | Age limit for the first fetch of a feed |
| `FeedInterval` | 30 min | Per feed |
| `MaxBackoff` | 24 h | Longest wait after repeated failures |
| `PollInterval` | 5 min | How often the ingest loop looks for feeds that are due |

## Web UI

`/ui` is a server-rendered reading UI over the archive: today's digest with 👍 👎 💡 buttons, search, one item, the digest
history, per-feed stats for the last 30 days (to decide which Miniflux feeds to prune), filed ideas and LLM spend.
It needs no JavaScript and loads nothing from other hosts (CSP `default-src 'none'`).

Sign in at `/ui/login` with the same value as `Mcp:Token`. The session is an HttpOnly, SameSite=Strict cookie signed with a key
derived from that token (30 days; changing the token signs everyone out). Every POST also checks that Origin or Referer
names the request's own host and carries a per-session anti-forgery value. Logins are limited to 5 a minute. With no
`Mcp:Token` every `/ui` route answers 503. `FeedEater:Llm:MonthlyBudget` (USD, default 0 = none) adds a budget line to `/ui/usage`.
`/mcp` and `/healthz` are unchanged.

`/ui/posts` is the stream of recent posts beyond the digest (filters: category, feed, project, kind, unrated, with AI summary;
keyset paging). `/ui/feedback` lists everything already rated, per 👍, 👎 and 💡, with the 7- and 30-day 👍 rate; a vote can be
changed or cleared there. The top of Today has "the day in brief", built from the digest's stored reads with no extra model call.

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
  with 👍 👎 💡.
- **Weekly review.** Sundays 18:30 Kyiv (only with a Telegram token): counts, top 5 👍, ratings per project, ideas filed, the 3
  feeds that earned most 👍 and 3 mute candidates. Stored in `weekly` and shown at `/ui/weekly`. No model call.

## v0.5 additions

- **Linked pages.** Reddit and Hacker News link posts are teasers, so for recent ones with a short text feed-eater fetches the page they
  link to (and the top 5 HN comments) and gives it to triage and read, fenced as untrusted data. The fetcher refuses private, loopback,
  link-local, tailnet and metadata addresses, pins the connection to the address it checked, follows at most 3 redirects, reads at most
  1 MB of html or text in 10 s, and keeps to 1 request a second per host and `FeedEater:Fetch:MaxPerDay` (400). Every rule is in
  [docs/specs/2026-10-06-page-fetch.md](docs/specs/2026-10-06-page-fetch.md). Tune with `FeedEater:Fetch:*` (`MaxPerPoll`, `BlockedHosts`).
- **Release watch.** What you run comes from `FeedEater:Watch:Source`: a PINS.md file path or an https URL (for the private repo, the
  GitHub contents API URL plus `FeedEater:Watch:Token`); unset or failing, it uses the static `config/watch.json`. `config/watch-map.json`
  maps images to upstream GitHub repos (add rows for more). A release newer than the pinned version is read by the model (what changed,
  breaking yes/no/unknown with a quote); one that mentions security, a CVE, a vulnerability or breaking goes to Telegram on its own,
  the rest in the digest header. `/ui/releases` lists products and releases. Each release is announced once; nothing is applied.
- **📌 Save** (Telegram, Today, Posts, Feedback, item page) bookmarks the link in Karakeep once. Karakeep being down gives a notice.
- **Suggested feeds** on `/ui/sources`: domains behind your 👍 items that no subscribed feed covers, with the feed URL found on the
  homepage (3 domains per poll, rechecked after 30 days). Subscribe in Miniflux yourself.
- **Prompt eval.** `dotnet FeedEater.dll eval --max-usd 0.25 [--max-items 40] [--reads] [--out report.md]` re-runs the current prompts
  over your voted items and prints how relevance agrees with the votes and how often "new" appears with no version or date. It says when
  there are too few votes (under 10 up and 10 down in the sample) and stops at the spend limit.
- **Taste learning, guarded.** `dotnet FeedEater.dll taste` is an offline report (held-out agreement of a learned model against the
  current ranking). `FeedEater:Taste:Learn` (default false) adds a bounded learned term; with under 100 votes (10 of each kind) it
  refuses and the digest header says so. The default ranking is unchanged.
- **Quiet hours.** `FeedEater:Quiet:From` and `To` (local time, default none) hold the scheduled digest, the weekly review and release
  alerts. `/quiet` (or `/quiet on|off|status`) and a button on Today toggle it by hand; `/digest` still works. Driving it from the
  senses service is a follow-up.

## v0.6 additions

- **Ask the archive.** `/ask question`, or any plain message ending in `?`, searches the archive (top 8), and the read model answers
  from those items only, citing each claim as `[n]`. Markers that name no source are dropped, and the cited items follow as links. When the
  sources do not answer it, the reply says so. Other plain messages are still a search.
- **Reply to an item.** Reply to a digest item or a search result with free text. The small model turns it into one action: 👍, 👎,
  file an idea (in a project you name, in your words: "idea for JARVIS: …"), save to Karakeep, mute the feed, or answer a question
  from the item's own text. A bare 👍 or 👎 needs no model call. The item is found from the buttons on the message you replied to, so
  nothing new is stored. A project name that is not a Plane project files nothing and lists the known ones.
- **`/learn`.** `/learn status` gives the vote count and, with enough votes, the held-out agreement of the current and learned rankings.
  `/learn on|off` switches the learned term from the next digest and wins over `FeedEater:Taste:Learn`. The weekly review adds a
  "Ranking" line when the learned model is ahead by the required margin and still off.

## Running a digest on demand

The scheduled digest runs once a day inside 07:30 to 12:00 Kyiv time. To run one outside that:

- Telegram: `/digest` (only from `FeedEater:Telegram:AllowedUserId`). `/digest resend` sends today's digest again.
- UI: "Run digest now" on `/ui`; "Send today's digest again" sits behind a confirm step.

Both write a cursor, `digest:force` (`run:<date>` or `resend:<date>`), that the digest job picks up within a minute. It ignores
the window and the done-check for that day only, and is cleared after the run, failed or not (a failure is reported on Telegram
and in `/ui`). A stale request from an earlier day is dropped. If today's digest was already sent, a plain run says so and
sends nothing; only `resend` sends the same messages again. Without a Telegram token the digest job is not registered.

## MCP tools

`feed_search`, `feed_read`, `feed_digests`, `feed_digest`, `feed_ideas`. All read-only. `./selftest.sh` calls them.

## Develop

```bash
dotnet test FeedEater.slnx
```

Docker must be running (Testcontainers starts `pgvector/pgvector:0.8.7-pg18-trixie`).

## License

Apache-2.0.
