# Configuration

Generated from `FeedEaterOptions` by `ConfigDocTests`. Do not edit by hand: change the `[Description]` in `src/FeedEater/FeedEaterOptions.cs`, then run `UPDATE_CONFIG_DOC=1 dotnet test --filter ConfigDocTests` and commit the result.

Settings are keys of the `FeedEater` section. In the environment write `__` for `:`, so `FeedEater:Llm:BaseUrl` is `FeedEater__Llm__BaseUrl`. `dotnet FeedEater.dll doctor --print-config` prints every effective value with secrets masked.

## Outside the `FeedEater` section

| Key | Description |
|---|---|
| `ConnectionStrings:FeedEater` | Required. Npgsql connection string for Postgres 18 with the pgvector extension. |
| `Mcp:Token` | Bearer token for `/mcp` and the password for `/ui/login`. Empty: both refuse every request. |

## Integrations

An integration is off until its required settings are filled. An off integration registers no background job, shows no button and makes no call.

| Integration | On when | Off means |
|---|---|---|
| Miniflux | `Miniflux:BaseUrl` and `Miniflux:Token` | Nothing is copied from Miniflux and no full text is fetched through it. |
| Plane | `Plane:BaseUrl`, `Plane:Token` and `Plane:Workspace` | Ideas go to the local list (see `Ideas:Sink`); open Plane work is not read into project vectors. |
| Karakeep | `Karakeep:BaseUrl` and `Karakeep:Token` | No 📌 button and no bookmark import. |
| GitHub stars | `GitHub:User` | No stars import. |
| Hindsight | `Hindsight:BaseUrl` | No weekly summary is sent. |
| Release watch | `Watch:Source` or `Watch:FallbackPath` | No release watch and no release alerts. |
| Linked pages | `Fetch:MaxPerDay` above 0 | No linked page or Hacker News comment is fetched. |

## General

| Key | Default | Description |
|---|---|---|
| `FeedEater:DigestAt` | `07:30:00` | Local time of day the scheduled digest starts. |
| `FeedEater:DigestGiveUpAt` | `12:00:00` | Local time of day after which a digest that has not gone out is given up for the day. |
| `FeedEater:ProfilePath` | `/config/profile.json` | Your profile: who you are, your projects and topics. A missing file means the built-in example interests are used and the digest says so. |
| `FeedEater:RunJobs` | `true` | False stops every background job (ingest, digest, polling); the web UI and /mcp still run. |
| `FeedEater:TimeZone` | `UTC` | IANA time zone for the digest time, quiet hours and the dates shown in the UI and Telegram, e.g. Europe/Berlin. |

## Caps

| Key | Default | Description |
|---|---|---|
| `FeedEater:Caps:CandidateDays` | `3` | Days back an item can still be a digest candidate. |
| `FeedEater:Caps:Centroid` | `500` | Most recent liked and disliked items used for the taste centroids. |
| `FeedEater:Caps:EmbedChars` | `8000` | Characters of an item embedded. |
| `FeedEater:Caps:MinRelevance` | `2` | Lowest triage relevance (0 to 3) that can reach the digest. |
| `FeedEater:Caps:Read` | `12` | Items the stronger model reads in full per digest. |
| `FeedEater:Caps:ReadChars` | `24000` | Characters of an item given to the read. |
| `FeedEater:Caps:ShortContentChars` | `1500` | Items with less text than this many characters get their full text fetched before reading. |
| `FeedEater:Caps:Triage` | `60` | Candidates the small model triages per digest. |
| `FeedEater:Caps:TriageChars` | `2000` | Characters of an item given to triage. |

## Cluster

| Key | Default | Description |
|---|---|---|
| `FeedEater:Cluster:Threshold` | `0.84` | Cosine similarity at or above which items from different feeds count as one story. Raise it if unrelated items merge, lower it (0.82) if one story shows up twice. |
| `FeedEater:Cluster:WindowDays` | `3` | Items published within this many days of each other can join one story. |

## Fetch

| Key | Default | Description |
|---|---|---|
| `FeedEater:Fetch:BlockedHosts` | `facebook.com,instagram.com,x.com,twitter.com,linkedin.com,tiktok.com,youtube.com,youtu.be,t.me,discord.com,medium.com,nytimes.com,wsj.com,bloomberg.com,ft.com` | Hosts never fetched (login-walled or hostile); subdomains match. |
| `FeedEater:Fetch:Comments` | `5` | Top Hacker News comments added to an HN item. |
| `FeedEater:Fetch:HnApiBase` | `https://hn.algolia.com/api/v1/` | Base URL of the Hacker News Algolia API. |
| `FeedEater:Fetch:MaxPerDay` | `400` | Page fetches per UTC day across the whole service; 0 turns linked-page fetching off. |
| `FeedEater:Fetch:MaxPerPoll` | `40` | Linked pages fetched per ingest poll, newest items first. |
| `FeedEater:Fetch:PageChars` | `6000` | Characters of readable page text kept per item. |
| `FeedEater:Fetch:ShortChars` | `800` | Only items whose own text is shorter than this many characters get their linked page fetched. |

## GitHub

| Key | Default | Description |
|---|---|---|
| `FeedEater:GitHub:BaseUrl` | `https://api.github.com/` | GitHub API URL. |
| `FeedEater:GitHub:User` | (empty) | GitHub user whose public stars count as liked items. Empty means no stars import. |

## Hindsight

| Key | Default | Description |
|---|---|---|
| `FeedEater:Hindsight:Bank` | `feed-eater` | Hindsight bank the weekly summary goes to. |
| `FeedEater:Hindsight:BaseUrl` | (empty) | Hindsight server URL for the weekly reading summary. Empty means no Hindsight. |
| `FeedEater:Hindsight:Token` | (empty) | Hindsight API key; empty when the server needs none. |

## Ideas

| Key | Default | Description |
|---|---|---|
| `FeedEater:Ideas:Sink` | `auto` | Where 💡 ideas go: plane, local, or auto (plane when Plane is configured, else local). Local keeps them on /ui/ideas and in the feed_ideas MCP tool. |

## Karakeep

| Key | Default | Description |
|---|---|---|
| `FeedEater:Karakeep:BaseUrl` | (empty) | Karakeep server URL. Empty means no Karakeep: no 📌 button, no bookmark import. |
| `FeedEater:Karakeep:Token` | (empty) | Karakeep API key. |

## Llm

| Key | Default | Description |
|---|---|---|
| `FeedEater:Llm:ApiKey` | (empty) | API key for the endpoint. |
| `FeedEater:Llm:BaseUrl` | `https://api.openai.com/v1/` | OpenAI-compatible endpoint, including the version path (/v1/). Chat completions and embeddings are called under it. |
| `FeedEater:Llm:EmbedBatch` | `64` | Texts embedded per request. |
| `FeedEater:Llm:EmbedModel` | `text-embedding-3-small` | Embedding model. The database column is fixed at 1536 dimensions. |
| `FeedEater:Llm:MonthlyBudget` | `0` | USD per month shown against the spend on /ui/usage; 0 shows no budget. |
| `FeedEater:Llm:Prices:<name>:Input` | `0` | Price of one model (<name> is the model name sent to the API): USD per million input tokens. Used only when the gateway sends no x-litellm-response-cost header; with neither, a call's cost is unknown. 0 marks a free local model. |
| `FeedEater:Llm:Prices:<name>:Output` | `0` | USD per million output tokens, for the same model. |
| `FeedEater:Llm:ReadModel` | `gpt-4.1-mini` | Stronger model: reads the top items in full, answers questions about the archive. |
| `FeedEater:Llm:TriageModel` | `gpt-4.1-nano` | Small model: the one-line verdict on each candidate, and the intent of a reply. |

## Miniflux

| Key | Default | Description |
|---|---|---|
| `FeedEater:Miniflux:BaseUrl` | (empty) | Miniflux server URL, e.g. http://miniflux:8080/. Empty means no Miniflux. |
| `FeedEater:Miniflux:PageSize` | `100` | Entries per Miniflux request. |
| `FeedEater:Miniflux:PollInterval` | `00:30:00` | How often new entries are copied. |
| `FeedEater:Miniflux:Token` | (empty) | Miniflux API key; only read from. |

## Plane

| Key | Default | Description |
|---|---|---|
| `FeedEater:Plane:BaseUrl` | (empty) | Plane server URL. Empty means no Plane. |
| `FeedEater:Plane:FallbackProject` | (empty) | Plane project identifier for ideas whose item matches no project of its own. |
| `FeedEater:Plane:Token` | (empty) | Plane API key. |
| `FeedEater:Plane:Workspace` | (empty) | Plane workspace slug. |

## Quiet

| Key | Default | Description |
|---|---|---|
| `FeedEater:Quiet:From` | (empty) | Local time at which quiet hours start; both From and To must be set. A window may cross midnight. Empty means no quiet hours. |
| `FeedEater:Quiet:ManualHours` | `12` | Hours a manual /quiet on lasts. |
| `FeedEater:Quiet:To` | (empty) | Local time at which quiet hours end. |

## Taste

| Key | Default | Description |
|---|---|---|
| `FeedEater:Taste:Learn` | `false` | Adds a learned term to the ranking. With fewer than MinVotes votes it refuses to switch on. /learn on\|off overrides this. |
| `FeedEater:Taste:MinVotes` | `100` | Votes needed (and 10 of each kind) before the learned term can be switched on. |

## Telegram

| Key | Default | Description |
|---|---|---|
| `FeedEater:Telegram:AllowedUserId` | `0` | Your numeric Telegram user id: the only sender the bot answers, and the chat digests go to. |
| `FeedEater:Telegram:BaseUrl` | `https://api.telegram.org/` | Telegram Bot API URL. |
| `FeedEater:Telegram:Token` | (empty) | Bot token from @BotFather. Without it the digest and the Telegram poller are off. |

## Watch

| Key | Default | Description |
|---|---|---|
| `FeedEater:Watch:FallbackPath` | (empty) | JSON list of what you run (see config/watch.example.json), used when Source is empty or fails. Release watch is off while both are empty. |
| `FeedEater:Watch:MapPath` | (empty) | JSON map from container image to upstream GitHub repo; empty uses config/watch-map.json beside the app. |
| `FeedEater:Watch:Source` | (empty) | Release watch: what you run, as a PINS.md file path or an https URL (for a private GitHub repo, the contents API URL plus Watch:Token). Empty uses FallbackPath. |
| `FeedEater:Watch:Token` | (empty) | Bearer token for Watch:Source; empty for a public URL. |

## Weights

| Key | Default | Description |
|---|---|---|
| `FeedEater:Weights:Learned` | `0.3` | Weight of the learned term when Taste:Learn is on: (probability - 0.5) times this. |
| `FeedEater:Weights:MinFeedVotes` | `5` | Votes a feed needs before its 👍 rate counts. |
| `FeedEater:Weights:MinPositives` | `10` | Liked items needed before the taste term counts. |
| `FeedEater:Weights:Prior` | `0.2` | Weight of a feed's 👍 rate. |
| `FeedEater:Weights:Taste` | `0.5` | Weight of the similarity to what was liked and disliked. |
