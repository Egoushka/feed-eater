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
| GitHub issues | `GitHub:Token` and `GitHub:FallbackRepo` | Ideas go to the local list (see `Ideas:Sink`); open issues are not read into project vectors and there is no weekly ideas shortlist. |
| Karakeep | `Karakeep:BaseUrl` and `Karakeep:Token` | No 📌 button and no bookmark import. |
| GitHub stars | `GitHub:User` | No stars import. |
| Hype autopsy | `GitHub:User` and `Telegram:Token` | No repo snapshots and no monthly autopsy; `GitHub:Token` is optional and lifts the 60 requests an hour limit. |
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

## Api

| Key | Default | Description |
|---|---|---|
| `FeedEater:Api:BaseUrl` | (empty) | The address the phone app uses; the pairing QR carries it. Empty: the address the setup page was opened on. |

## Caps

| Key | Default | Description |
|---|---|---|
| `FeedEater:Caps:CandidateDays` | `3` | Days back an item can still be a digest candidate. |
| `FeedEater:Caps:Centroid` | `500` | Most recent liked and disliked items used for the taste centroids. |
| `FeedEater:Caps:EmbedChars` | `8000` | Characters of an item embedded. |
| `FeedEater:Caps:MinReadRelevance` | `1` | Lowest relevance (0 to 3) the reader model can give an item after reading it and keep it in the digest. Items below MinRelevance but at or above this go last. |
| `FeedEater:Caps:MinRelevance` | `2` | Lowest triage relevance (0 to 3) that can reach the digest. |
| `FeedEater:Caps:Read` | `12` | Items the stronger model reads in full per digest. |
| `FeedEater:Caps:ReadChars` | `24000` | Characters of an item given to the read. |
| `FeedEater:Caps:ShortContentChars` | `1500` | Items with less text than this many characters get their full text fetched before reading. |
| `FeedEater:Caps:Triage` | `400` | Candidates the small model triages per digest. |
| `FeedEater:Caps:TriageChars` | `2000` | Characters of an item given to triage. |

## Cluster

| Key | Default | Description |
|---|---|---|
| `FeedEater:Cluster:Threshold` | `0.84` | Cosine similarity at or above which items from different feeds count as one story. Raise it if unrelated items merge, lower it (0.82) if one story shows up twice. |
| `FeedEater:Cluster:WindowDays` | `3` | Items published within this many days of each other can join one story. |

## Duel

| Key | Default | Description |
|---|---|---|
| `FeedEater:Duel:Enabled` | `true` | False turns the twice-daily duel off: two unvoted items in one message, one tap picks the one you would rather read. Needs the Telegram bot. |
| `FeedEater:Duel:PerDay` | `2` | Most duels sent per local day; later slots of the day are skipped. |
| `FeedEater:Duel:Times` | `12:30,20:30` | Local times a duel is sent, comma separated (HH:mm). A slot missed while the service was down is sent only within 2 hours of its time. |

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

## Follow

| Key | Default | Description |
|---|---|---|
| `FeedEater:Follow:Days` | `14` | Days a story is followed before it is closed with a summary. |
| `FeedEater:Follow:Enabled` | `true` | Follow this story: a 🧵 button on items that sends later items on the same story as replies to one message. False removes the button and the job. |
| `FeedEater:Follow:MaxActive` | `5` | Stories followed at once. |
| `FeedEater:Follow:MaxMessages` | `50` | Messages sent per followed story; the follow closes when it is reached. |

## GitHub

| Key | Default | Description |
|---|---|---|
| `FeedEater:GitHub:BaseUrl` | `https://api.github.com/` | GitHub API URL. |
| `FeedEater:GitHub:FallbackRepo` | (empty) | Repo (owner/name) for ideas whose item matches no project with a repo of its own. With GitHub:Token it turns GitHub issues on: ideas are filed as issues labelled triage. |
| `FeedEater:GitHub:OwnerTokens` | (empty) | Tokens for owners that GitHub:Token cannot reach (a fine-grained token covers one owner), as owner=token pairs separated by commas, e.g. some-org=ghp_x. A repo of a listed owner is called with that token instead of GitHub:Token. |
| `FeedEater:GitHub:Token` | (empty) | GitHub token, sent on every GitHub call. It lifts the 60 requests an hour limit of anonymous calls (the hype autopsy snapshots stop at 40 requests a run without it); a token with no scopes is enough for that. Filing ideas as issues needs one that can write issues in every repo of the profile (classic: repo, or public_repo when all are public). |
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
| `FeedEater:Ideas:Sink` | `auto` | Where 💡 ideas go: github, local, or auto (github when GitHub issues are configured, else local). GitHub opens an issue labelled triage in the project's repo; local keeps them on /ui/ideas and in the feed_ideas MCP tool. |

## Karakeep

| Key | Default | Description |
|---|---|---|
| `FeedEater:Karakeep:BaseUrl` | (empty) | Karakeep server URL. Empty means no Karakeep: no mirror of saves, no bookmark import. |
| `FeedEater:Karakeep:Mirror` | `false` | Copy each new save to Karakeep as a bookmark. The library works without it. |
| `FeedEater:Karakeep:Token` | (empty) | Karakeep API key. |

## Library

| Key | Default | Description |
|---|---|---|
| `FeedEater:Library:EnrichBatch` | `5` | Saved items the background enrichment (full text, read, embedding) handles per pass. |
| `FeedEater:Library:EnrichIdle` | `00:05:00` | How long the enrichment loop waits after a pass that found nothing to do. After a pass that did work it waits 30 seconds. |
| `FeedEater:Library:ResurfaceDays` | `14` | A saved item still unread after this many days is a candidate for the weekly resurface. |
| `FeedEater:Library:ResurfacePerWeek` | `3` | At most this many saved items are resurfaced in one week. |

## Llm

| Key | Default | Description |
|---|---|---|
| `FeedEater:Llm:ApiKey` | (empty) | API key for the endpoint. |
| `FeedEater:Llm:BaseUrl` | `https://api.openai.com/v1/` | OpenAI-compatible endpoint, including the version path (/v1/). Chat completions and embeddings are called under it. |
| `FeedEater:Llm:EmbedBatch` | `64` | Texts embedded per request. |
| `FeedEater:Llm:EmbedDimensions` | `0` | Sent as the dimensions parameter of embedding requests; 0 leaves it out. Models that can shorten their output (text-embedding-3-large) need 1536 here. |
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

## PeerHarvest

| Key | Default | Description |
|---|---|---|
| `FeedEater:PeerHarvest:CardMaxTokens` | `700` | Tokens the card model may answer with. |
| `FeedEater:PeerHarvest:CardModel` | `gpt-4.1-nano` | Small model: writes the card (addresses, features, uses) from a peer's README and file tree, one call per repo. |
| `FeedEater:PeerHarvest:ReadmeMaxBytes` | `32768` | Bytes of a peer's README kept for the card step; the rest is cut before the model sees it. |
| `FeedEater:PeerHarvest:SetToken` | (empty) | GitHub token that can read the peer set file; empty for a public repository. |
| `FeedEater:PeerHarvest:SetUrl` | (empty) | Peer harvester: GitHub contents API URL of the peer set file (one own repo plus its public peers, YAML), to fingerprint and card each repo into the prior-art namespace. Empty turns peer-harvest off for the whole configured set (a single repo can still be run by name). |

## PriorArt

| Key | Default | Description |
|---|---|---|
| `FeedEater:PriorArt:HitMinFacts` | `3` | Prior-art search: coverage is hit when at least this many relevant facts come from at least PriorArt:HitMinTools tools; some relevant facts but fewer is thin; none is miss. |
| `FeedEater:PriorArt:HitMinTools` | `2` | Prior-art search: the number of different tools the relevant facts of a hit must come from. |
| `FeedEater:PriorArt:MinSimilarity` | `0.35` | Prior-art search: cosine similarity at or above which a fact counts as relevant to the question. A fact below it is still listed, flagged not relevant, and does not count toward coverage. |
| `FeedEater:PriorArt:StaleAfterDays` | `180` | Prior-art search: a claim recorded more than this many days ago is flagged stale. |

## Quiet

| Key | Default | Description |
|---|---|---|
| `FeedEater:Quiet:From` | (empty) | Local time at which quiet hours start; both From and To must be set. A window may cross midnight. Empty means no quiet hours. |
| `FeedEater:Quiet:ManualHours` | `12` | Hours a manual /quiet on lasts. |
| `FeedEater:Quiet:To` | (empty) | Local time at which quiet hours end. |

## Source

| Key | Default | Description |
|---|---|---|
| `FeedEater:Source:AllowedHosts` | (empty) | Exact host names a configured feed URL may point at although they are private (self-hosted RSSHub, Nitter); any port. Applies to feed URLs only, never to article or linked-page fetches. |
| `FeedEater:Source:BackfillDays` | `14` | The first fetch of a feed keeps only entries published this many days back, so a new instance does not embed whole histories. With Miniflux, older entries are archived and searchable but not embedded, so they never become digest candidates. |
| `FeedEater:Source:FeedInterval` | `00:30:00` | How often each feed is fetched; repeated failures double the wait up to MaxBackoff. |
| `FeedEater:Source:Kind` | `builtin` | Where entries come from: builtin (feed-eater reads the feeds itself) or miniflux (copies entries from Miniflux). Switching on a populated database is unsupported. |
| `FeedEater:Source:MaxBackoff` | `1.00:00:00` | The longest wait between fetches of a feed that keeps failing. |
| `FeedEater:Source:PollInterval` | `00:01:00` | How often the ingest loop looks for feeds that are due. |

## Swipe

| Key | Default | Description |
|---|---|---|
| `FeedEater:Swipe:CandidateDays` | `14` | Items published in the last this many days can be shown. |
| `FeedEater:Swipe:Cards` | `40` | Cards in one session; the summary card follows the last one. |
| `FeedEater:Swipe:CategoriesPath` | `/config/categories.json` | Your categories (what counts as a good use of attention: shares, events, seed flag); shape in categories.example.json. A missing file means one category, everything. |
| `FeedEater:Swipe:CategoryFloor` | `0.2` | Cosine similarity below which an item belongs to no category. |
| `FeedEater:Swipe:CooldownMinutes` | `180` | Minutes after a session ends before the next one can start. |
| `FeedEater:Swipe:Enabled` | `false` | Swipe mode: a phone page at /swipe showing one card per screen, with a hard cap per session. Off by default; needs Mcp:Token for the sign-in. |
| `FeedEater:Swipe:Epsilon` | `0.2` | Share of cards chosen for exploration: a random item from outside the best-ranked part that is unlike the rest of the session. Its yield is the only honest test of the ranking. |
| `FeedEater:Swipe:Minutes` | `15` | Minutes after which a session ends with the summary card, whatever the card count. |
| `FeedEater:Swipe:Notes:BaseUrl` | (empty) | Server that lists your notes at GET /api/v1/notes (items with id, topic, points, createdAt), for recall cards. Empty means no notes. |
| `FeedEater:Swipe:Notes:Token` | (empty) | Bearer token for the notes server. |
| `FeedEater:Swipe:Recall:Enabled` | `true` | Recall cards: questions made from the items you liked or saved (and from your notes) come back at growing intervals. False turns generating and showing them off. |
| `FeedEater:Swipe:Recall:PerDay` | `10` | Most liked or saved items turned into questions per local day (one small-model call each). |
| `FeedEater:Swipe:Recall:PerSession` | `12` | Most recall cards in one session; due cards come first, so a backlog never takes the whole session. |
| `FeedEater:Swipe:RepeatDays` | `30` | An item shown in swipe mode is not shown again for this many days. |
| `FeedEater:Swipe:Seeds:Enabled` | `true` | Post seeds: a one-line angle for a post, drafted from an item you liked or saved, with a send button that files it as an idea. False turns generating and showing them off. |
| `FeedEater:Swipe:Seeds:PerDay` | `3` | Most seeds drafted per local day (one small-model call each). |
| `FeedEater:Swipe:Seeds:PerSession` | `2` | Most seeds in one session. |
| `FeedEater:Swipe:SessionsPerDay` | `3` | Most sessions that can start per local day. |
| `FeedEater:Swipe:Yield` | `1` | Weight of the yield model's (probability - 0.5) next to the taste score, once it has enough examples. |
| `FeedEater:Swipe:YieldMinEvents` | `30` | Good and not-good examples (each) the yield model needs before it adds to the ranking. |

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
| `FeedEater:Telegram:Token` | (empty) | Bot token from @BotFather. Without it the Telegram poller is off; the digest and the inbox jobs (duels, follows, the weekly review, ideas, the autopsy) still run when Swipe:Enabled is true, and wait in the swipe stack. |

## Watch

| Key | Default | Description |
|---|---|---|
| `FeedEater:Watch:FallbackPath` | (empty) | JSON list of what you run (see config/watch.example.json), used when Source is empty or fails. Release watch is off while both are empty. |
| `FeedEater:Watch:MapPath` | (empty) | JSON map from container image to upstream GitHub repo; empty uses config/watch-map.json beside the app. |
| `FeedEater:Watch:Source` | (empty) | Release watch: what you run, as a PINS.md file path or an https URL (for a private GitHub repo, the contents API URL plus Watch:Token). Empty uses FallbackPath. |
| `FeedEater:Watch:Token` | (empty) | Bearer token for Watch:Source; empty for a public URL. |

## WeeklyIdeas

| Key | Default | Description |
|---|---|---|
| `FeedEater:WeeklyIdeas:At` | `09:00:00` | Local time of day on Monday the shortlist runs. A run missed while the service was down happens at start. |
| `FeedEater:WeeklyIdeas:BacklogToken` | (empty) | GitHub token that can read the backlog file; empty for a public repository. |
| `FeedEater:WeeklyIdeas:BacklogUrl` | (empty) | GitHub contents API URL of the backlog file, to keep the shortlist from repeating a row. Empty means the backlog is not checked; GitHub issues and earlier shortlists still are. |
| `FeedEater:WeeklyIdeas:Enabled` | `true` | Mondays: the week's improve items and what you liked or saved, ranked into a shortlist, with the top few on Telegram. Ideas are filed as GitHub issues labelled triage only after Keep (Telegram button, swipe card). Needs GitHub issues (GitHub:Token and GitHub:FallbackRepo), and Telegram or Swipe. False turns the job off. |
| `FeedEater:WeeklyIdeas:MaxFiled` | `5` | Most ideas filed as GitHub issues per week. |
| `FeedEater:WeeklyIdeas:TopInMessage` | `3` | Ideas listed in the Telegram message, best first. |

## Weights

| Key | Default | Description |
|---|---|---|
| `FeedEater:Weights:Learned` | `0.3` | Weight of the learned term when Taste:Learn is on: (probability - 0.5) times this. |
| `FeedEater:Weights:MinFeedVotes` | `5` | Votes a feed needs before its 👍 rate counts. |
| `FeedEater:Weights:MinPositives` | `10` | Liked items needed before the taste term counts. |
| `FeedEater:Weights:Prior` | `0.2` | Weight of a feed's 👍 rate. |
| `FeedEater:Weights:Taste` | `0.5` | Weight of the similarity to what was liked and disliked. |
