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

## MCP tools

`feed_search`, `feed_read`, `feed_digests`, `feed_digest`, `feed_ideas`. All read-only. `./selftest.sh` calls them.

## Develop

```bash
dotnet test FeedEater.slnx
```

Docker must be running (Testcontainers starts `pgvector/pgvector:0.8.7-pg18-trixie`).

## License

Apache-2.0.
