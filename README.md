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
| `FeedEater:Miniflux:Token` | yes | Miniflux API key; read-only use |
| `FeedEater:Llm:ApiKey` | yes | LiteLLM virtual key with a budget |
| `FeedEater:Telegram:Token`, `AllowedUserId` | yes | its own bot; the poller only starts with a token |
| `FeedEater:Plane:Token` | for 💡 | Plane API key |
| `FeedEater:Karakeep:Token` | no | without it Karakeep saves are not used |
| `FeedEater:Hindsight:Token` | no | weekly summary to the `learning` bank |
| `FeedEater:ProfilePath` | no | default `/config/profile.json`; shape in `profile.example.json` |

## Web UI

`/ui` is a server-rendered reading UI over the archive: today's digest with 👍 👎 💡 buttons, search, one item, the digest
history, per-feed stats for the last 30 days (to decide which Miniflux feeds to prune), filed ideas and LLM spend.
It needs no JavaScript and loads nothing from other hosts (CSP `default-src 'none'`).

Sign in at `/ui/login` with the same value as `Mcp:Token`. The session is an HttpOnly, SameSite=Strict cookie signed with a key
derived from that token (30 days; changing the token signs everyone out). Every POST also checks that Origin or Referer
names the request's own host and carries a per-session anti-forgery value. Logins are limited to 5 a minute. With no
`Mcp:Token` every `/ui` route answers 503. `FeedEater:Llm:MonthlyBudget` (USD, default 0 = none) adds a budget line to `/ui/usage`.
`/mcp` and `/healthz` are unchanged.

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
