# Setup

feed-eater runs as two containers: itself and Postgres with pgvector. It needs three things from outside: an OpenAI-compatible key
(chat and embeddings), a Telegram bot, and feeds (an OPML file or a first feed URL). Nothing else is required. Every setting is
listed in [CONFIG.md](CONFIG.md).

## Quick start (about 10 minutes)

You need Docker with the compose plugin, an [OpenAI API key](https://platform.openai.com/api-keys) with a few dollars of credit, and
a Telegram account.

**1. Get the two files.** In an empty directory:

```bash
curl -fsSL https://raw.githubusercontent.com/Egoushka/feed-eater/main/compose.example.yaml -o compose.yaml
curl -fsSL https://raw.githubusercontent.com/Egoushka/feed-eater/main/.env.example -o .env
mkdir config
```

**2. Create the Telegram bot.** In Telegram open [@BotFather](https://t.me/BotFather), send `/newbot`, pick a name and a username,
and copy the token it gives you (it looks like `123456789:AAH...`).

**3. Fill in `.env`.** Open it and set five values; each has a comment:

| Key | Put here |
|---|---|
| `POSTGRES_PASSWORD` | any password of letters and digits |
| `Mcp__Token` | a password for the web UI, e.g. the output of `openssl rand -hex 32` |
| `FeedEater__Llm__ApiKey` | your OpenAI key |
| `FeedEater__Telegram__Token` | the BotFather token |
| `FeedEater__TimeZone` | your IANA time zone, e.g. `Europe/Berlin` (default `UTC`); the digest goes out at 07:30 in it |

Leave `FeedEater__Telegram__AllowedUserId=0` for now.

**4. Add your feeds.** Export an OPML file from your current reader and save it as `config/feeds.opml`. No file yet? Skip this and
add a first feed at `http://localhost:8080/ui/sources` in step 7.

**5. Start it.**

```bash
docker compose up -d
```

**6. Tell it who you are.** Send `/start` to your bot in Telegram. It answers with your numeric user id and the exact line to
set. Put that value in `.env` (`FeedEater__Telegram__AllowedUserId=<your id>`) and apply it:

```bash
docker compose up -d
```

Until the id is set the bot answers only `/start`, and nothing is ever sent to a chat that is not yours.

**7. Import the feeds** (if you have an OPML file):

```bash
docker compose run --rm feed-eater import-opml /config/feeds.opml
```

To add feeds one by one instead, open `http://localhost:8080/ui/sources`, sign in with the `Mcp__Token` value, and paste a feed URL
or a site address. A new instance embeds only the last 14 days of entries, so the first run costs cents.

**8. Check everything.**

```bash
docker compose run --rm feed-eater doctor
```

`doctor` prints one line per integration: `ok`, `off` (not configured, which is fine for an optional one) or `FAIL` with the reason
and the fix. It exits with code 1 when a required item fails: database, `llm chat`, `llm embeddings`, `telegram` or `feed source`.
`profile` shows `warn` while the example interests are in use (see below); that is not a failure. The same table is at
`http://localhost:8080/ui/setup`.

**9. Get a digest.** Send `/digest` to the bot. The digest starts within a minute and arrives within about 15 minutes of the first start; after that one
arrives every morning. `http://localhost:8080/ui/usage` shows what the first run cost.

### Your interests

Without a profile the digest ranks by example interests and says so in its header. To use your own, save a `config/profile.json`,
best before the first start:

```bash
curl -fsSL https://raw.githubusercontent.com/Egoushka/feed-eater/main/profile.example.json -o config/profile.json
```

`about` is a sentence on who you are; each project and topic has a `key` and a `description` the ranking and the models use. Add a
`"plane"` value to a project to file its ideas into that Plane project (see Plane below). The file is read on each digest, but the
ranking vectors built from it are rebuilt once a day at 03:00 in your time zone, so a change made later fully applies from the next
night.

### Another model provider

`FeedEater__Llm__BaseUrl` takes any OpenAI-compatible endpoint, including its version path (`/v1/`): a gateway such as LiteLLM, or a
local server (for Ollama from a container: `http://host.docker.internal:11434/v1/`). Set `FeedEater__Llm__TriageModel`,
`ReadModel` and `EmbedModel` to names the endpoint knows. If the endpoint sends no cost header, set `FeedEater__Llm__Prices__<model>__Input`
and `__Output` (USD per million tokens) so `/ui/usage` can show a cost; without either it shows tokens and "cost unknown". Use `0`
for a free local model.

**Known limit: embeddings must have 1536 dimensions.** The database column is `vector(1536)` and cannot change without a migration
and re-embedding every item. `text-embedding-3-small` (the default) and `text-embedding-ada-002` fit; a model that returns another
size is reported by `doctor` as `FAIL llm embeddings`. A model that can shorten its output (`text-embedding-3-large`) works when you set
`FeedEater__Llm__EmbedDimensions=1536`, which is sent as the `dimensions` parameter.

### A feed on a private host

A feed URL that points at a private address (a self-hosted RSSHub or Nitter) is refused. List its host name in
`FeedEater__Source__AllowedHosts__0=rsshub` (exact name, any port). This applies to feed URLs only: article and linked-page fetches
never reach private hosts, whatever the list says, because those links come from the feeds' entries.

### Reaching it from elsewhere

The compose file publishes the UI on `127.0.0.1:8080` only. To use it from a phone or another machine, put a reverse proxy with
HTTPS in front (the session cookie is marked `Secure` when the request arrives over HTTPS or with `X-Forwarded-Proto: https`).
Do not publish the port as it is: the only protection is the `Mcp__Token` password.

Any MCP client can search the archive at `/mcp` with `Authorization: Bearer <Mcp__Token>`.

## Optional integrations

Each one is off until its settings are filled: no background work, no button, no call. Add the keys to `.env`, run
`docker compose up -d`, then `doctor` shows the integration as `ok` or `FAIL` instead of `off`.

**Miniflux (instead of the built-in reader).** Set `FeedEater__Miniflux__BaseUrl` and `FeedEater__Miniflux__Token` (an API key; only
read from) and `FeedEater__Source__Kind=miniflux`. feed-eater then copies entries from Miniflux and the built-in reader stays idle.
Switching between the two on a database that already holds entries is not supported.

**Plane (ideas as intake items).** 💡 on an item saves an idea. By default it goes to the local list (`/ui/ideas`, MCP tool
`feed_ideas`). Set `FeedEater__Plane__BaseUrl`, `__Token` (API key) and `__Workspace` (the slug) and ideas go to Plane Intake of the
project named in the profile entry (`"plane": "<identifier>"`), else of `FeedEater__Plane__FallbackProject`. Intake must be enabled
on those projects. `FeedEater__Ideas__Sink=local` keeps ideas local even with Plane configured.

**Karakeep (bookmarks).** Set `FeedEater__Karakeep__BaseUrl` and `__Token` (API key). Items get a 📌 button that bookmarks the link,
and your Karakeep bookmarks count as liked items in the ranking.

**GitHub stars.** Set `FeedEater__GitHub__User`. The public stars of that user count as liked items. A token is optional
(`FeedEater__GitHub__Token`, no scopes needed): without it GitHub allows 60 requests an hour, which is enough for the stars.

**Hype autopsy.** With `FeedEater__GitHub__User` and a Telegram token set, a daily job (05:00) records the GitHub repo of every 👍
item, and on the 1st of each month (09:00) a report says what those repos did in the 87 or more days since: stars up 20% or more
(grew), pushed within 30 days (alive), neither (quiet), 404 or archived (gone). It splits the result by the model's relevance and by
feed, lands on `/ui/autopsy` and goes to Telegram. It calls no model. Without a token the snapshot job stops at 40 requests a run and
carries on the next day; set `FeedEater__GitHub__Token` to lift that.

**Hindsight (memory).** Set `FeedEater__Hindsight__BaseUrl` (and `__Token` if the server needs a key). On Sundays at 18:00 a plain
summary of the week's reading goes to the bank named by `FeedEater__Hindsight__Bank` (default `feed-eater`).

**Release watch.** Tells you when software you run has a new release: what changed, and whether it breaks. Set
`FeedEater__Watch__Source` to a `PINS.md` (a file path inside the container or an https URL; for a private GitHub repo use the
contents API URL plus `FeedEater__Watch__Token`), or `FeedEater__Watch__FallbackPath` to a JSON list of what you run (the format is
`config/watch.example.json` in the repository). Mount the file into the container, for example under `./config`. The map from
container images to their upstream GitHub repos is `config/watch-map.json`; point `FeedEater__Watch__MapPath` at your own copy to add rows.

**Linked pages.** On by default, not an external service: for short link posts from Reddit and Hacker News the page they link to is
fetched (never private addresses). `FeedEater__Fetch__MaxPerDay=0` turns it off.

## Upgrading

1. Read the release notes for the tag you move to.
2. Back up the database: `docker compose exec -T db pg_dump -U feed feed > feed-eater-backup.sql`.
3. Change the tag in `compose.yaml` (`ghcr.io/egoushka/feed-eater:<version>`), then `docker compose pull && docker compose up -d`.
4. Run `docker compose run --rm feed-eater doctor`.

Migrations run when the app starts and only move forward, so a rollback means restoring the backup. A new Postgres major version
needs a dump and restore; changing the image tag is not enough.

### From v0.6

v0.7 moves every setting that was specific to one deployment out of the defaults. A v0.6 install keeps working only if it sets what
it relied on:

- `FeedEater__Source__Kind=miniflux`, with the existing Miniflux URL and key (the default is now the built-in reader).
- `FeedEater__TimeZone` (the default is now `UTC`).
- `FeedEater__Llm__BaseUrl` and the model names, if you do not use the OpenAI defaults (`gpt-4.1-nano` triage, `gpt-4.1-mini` read,
  `text-embedding-3-small` embeddings).
- The URLs and keys of Plane, Karakeep, Hindsight and the GitHub user: each integration is off until its setting is filled.
- `FeedEater__Watch__FallbackPath` (or `Source`): release watch is off until one is set, and the list is no longer built in.

`docker compose run --rm feed-eater doctor --print-config` prints every effective value with secrets masked; run it before and after
upgrading and compare the two outputs to see that nothing moved.
