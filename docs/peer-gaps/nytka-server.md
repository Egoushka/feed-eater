# Peer gaps: nytka-app/server

Harvested 2026-11-01. Peers are the 7 closest public open-source projects; every gap cites a page that shows the peer has the feature, and "absent here" was checked in this repo by reading or grepping it. Judged by Claude, not yet by Yehor (the stop rule's reviewer).

**Gaps:** 15. Useful 1, not useful 10, unknown 4.

**What it is:** Self-hosted server for the Omi AI necklace (companion to the nytka-app/android app). Takes Opus chunks, drops silence with Silero VAD, transcribes speech through any OpenAI-compatible endpoint, groups it into conversations, then adds titles, summaries, tasks and memories with a language model you choose. Postgres storage, full-text (Ukrainian and English) plus optional pgvector search, Ask, webhooks, MCP. Apache-2.0, .NET 10. Status: Parked.

**Note from the harvest:** Exactly 7 peers. Nytka covers far more than most peers, so gaps are few and mostly about surfaces (web UI, live stream, CLI, email, scheduling) rather than pipeline. SimpleOpenSoftware/chronicle is not Yehor's own 'chronicle' repo (different owner). screenpipe/screenpipe was considered and dropped: its licence is source-available/non-commercial, not permissive or copyleft. Meetily was considered and dropped: its extra features (PDF/DOCX export, custom templates) are paid-only, and Nytka already has templates. Gaps already on Nytka's ROADMAP.md are marked 'on roadmap' in why_it_matters.

## BasedHardware/omi

https://github.com/BasedHardware/omi · MIT · 13,870 stars · last push 2026-11-01. Similar because: The official app and backend for the same Omi pendant; Nytka replaces its cloud pipeline and even emits Omi-format webhooks.

- **Live transcript and raw-audio push to integrations** (not useful). Omi POSTs transcript segments (text, speaker, is_user, start, end) to a developer URL while a conversation unfolds, and can stream raw PCM16 audio every N seconds.  
  Source: https://docs.omi.me/doc/developer/apps/Integrations  
  Absent here, checked: Read docs/reference/api.md webhook table: only segment.created exists, opt-in, every 30 to 90 s, ids and times only, never text; grep -rIliE 'websocket|text/event-stream|SignalR|MapHub' src docs found no live channel; ROADMAP.md Stage 3 says live transcripts 'neither is in the code yet'.  
  Worth doing? Not useful: Weeks of work, already on ROADMAP.md; the server is parked. Cost: weeks: needs a streaming channel (WebSocket or SSE) fed from the transcription batch writer, plus auth and back-pressure

- **Third-party tools callable from Ask (chat tools)** (not useful). Omi apps publish a JSON manifest of tools (name, description, endpoint, JSON-schema parameters) that Omi's chat AI can call, for example to send a Slack message.  
  Source: https://docs.omi.me/doc/developer/apps/ChatTools  
  Absent here, checked: Read src/Nytka.Server/Ask/AskTools.cs and AskService: Ask only retrieves conversations and memories and has no tool-calling loop; src/Nytka.Server/Ask/AskEndpoints.cs takes only a question string.  
  Worth doing? Not useful: Weeks; turns Ask into an action runner, a different product. Cost: weeks: a tool-calling loop in Ask, a manifest registry table, and safety rules for write actions

## SimpleOpenSoftware/chronicle

https://github.com/SimpleOpenSoftware/chronicle · MIT · 76 stars · last push 2026-09-22. Similar because: Self-hosted backend plus mobile app for Omi pendants: transcription, speaker recognition, memory extraction, local-model options.

- **Non-phone audio sources (ESP32 relay)** (not useful). Besides Omi devices, microphones and uploads, Chronicle ingests audio from an ESP32 bridge (HAVPE relay) with no phone in the path.  
  Source: https://raw.githubusercontent.com/SimpleOpenSoftware/chronicle/main/docs/overview.md  
  Absent here, checked: Read docs/reference/api.md: POST /api/v1/chunks needs the app's Opus frame format (application/vnd.nytka.frames.v1) and import/audio is file-based; grep for esp32 or relay in src and docs found nothing; ROADMAP.md lists only Limitless, Plaud, Fieldy via the Android app.  
  Worth doing? Not useful: Needs hardware Yehor does not run. Cost: about a week: a small relay/adapter script that wraps a stream into the chunk format, plus docs

- **Continuous Markdown vault sync** (unknown). Chronicle keeps memories as Markdown notes in an Obsidian-style vault and syncs it to a Mac through a menu-bar client and Syncthing.  
  Source: https://raw.githubusercontent.com/SimpleOpenSoftware/chronicle/main/docs/overview.md  
  Absent here, checked: Read docs/explanation/features.md Export section: GET /api/v1/export/obsidian is a one-shot ZIP; docs/specs/export.md line 175 lists 'Incremental export (?since=)' among things not done; no sync client in the repo.  
  Worth doing? Unknown: UNKNOWN whether Yehor keeps notes in an Obsidian vault. Cost: days for ?since= on the Obsidian export, plus a small script that unzips on a schedule

## thecloudcowboy/locus-tether

https://github.com/thecloudcowboy/locus-tether · MIT · 17 stars · last push 2026-09-01. Similar because: Self-hosted replacement for Omi's cloud pipeline: pendant audio to local transcription, diarization, voice enrollment, LLM analysis and a daily digest.

- **Speaking-style coaching** (not useful). Deterministic metrics (words per minute, pause locations and lengths, filler-word counts) plus LLM feedback with quoted examples, for the wearer's lines only.  
  Source: https://raw.githubusercontent.com/thecloudcowboy/locus-tether/main/README.md  
  Absent here, checked: grep -rIliE 'filler|wordsPerMinute|words per minute|speaking style' src docs: only TaskMatch.cs and features.md:507 (stop-words for duplicate tasks), no speech metrics. Nytka does know which lines are the wearer's (isUser), so the input exists.  
  Worth doing? Not useful: A week for a feature off Nytka's core. Cost: about a week: a metrics function over user segments, one AI job, one endpoint and a digest section

- **Daily digest by email with PDF** (not useful). The digest is sent as an HTML email with checkboxes and a PDF attachment through SMTP, with a Markdown copy kept on disk.  
  Source: https://raw.githubusercontent.com/thecloudcowboy/locus-tether/main/README.md  
  Absent here, checked: grep -rIliE 'smtp|MailKit|SmtpClient|pdf' src docs scripts: no hits; docs/explanation/features.md Daily digest section delivers only through GET /api/v1/digests, the digest.ready webhook and the list_digests MCP tool.  
  Worth doing? Not useful: A webhook to n8n already delivers the digest; email adds little. Cost: about two days for plain HTML email via SMTP; PDF adds a library and a day

- **Merged-conversation warning** (unknown). The analyzer detects when one recording clearly holds two separate conversations and shows a warning banner instead of silently summarizing them as one.  
  Source: https://raw.githubusercontent.com/thecloudcowboy/locus-tether/main/README.md  
  Absent here, checked: grep -rniE 'merged conversation|possible merge|looks merged' src docs/explanation: the only 'merged conversation' hits are in ProcessSessionHandler.cs:211 and ConversationStore.cs:206, which handle a late chunk joining a conversation, not detecting two topics; ROADMAP.md Stage 3 plans merge and split by hand only.  
  Worth doing? Unknown: Low confidence: Nytka already merges late chunks, which is a different behaviour. Cost: a day or two: one extra field in the summary prompt output and a flag on the conversation

## Vexa-ai/vexa

https://github.com/Vexa-ai/vexa · Apache-2.0 · 2,938 stars · last push 2026-11-01. Similar because: Self-hosted transcription service with speaker attribution, an HTTP API, webhooks-style events, an MCP endpoint and agents that read a growing knowledge base (meeting bots instead of a pendant).

- **User-defined scheduled routines** (unknown). You create a cron-scheduled agent routine (POST /agent/routines) that runs a prompt on a schedule and writes its result into the workspace.  
  Source: https://raw.githubusercontent.com/Vexa-ai/vexa/main/README.md  
  Absent here, checked: Read docs/explanation/features.md (Daily digest, Summaries over time, Insights) and grep -rIliE 'cron|schedule' src: schedules are fixed built-in queues (InsightQueue, QualityQueue, CalendarQueue), none takes a user prompt or time; no routines route in docs/reference/api.md.  
  Worth doing? Unknown: Plausible ('what did I promise this week') but no value test yet; a week. Cost: about a week: a routines table, a scheduler job that runs Ask with a saved prompt, delivery by webhook

- **Kubernetes deployment (Helm chart)** (not useful). Vexa ships a Helm chart in deploy/helm and a k8s runtime backend alongside the Docker one.  
  Source: https://raw.githubusercontent.com/Vexa-ai/vexa/main/README.md  
  Absent here, checked: grep -rIliE 'helm|kubernetes' src docs scripts docker-compose.yml README.md ROADMAP.md: no hits; install is docker-compose.yml plus scripts/install.sh only.  
  Worth doing? Not useful: Only matters if strangers run it on a cluster. Cost: days for a basic chart (server, Postgres, STT); more for GPU speech-to-text

## fastrepl/anarlog

https://github.com/fastrepl/anarlog · MIT · 9,734 stars · last push 2026-11-01. Similar because: Local-first open-source AI note taker (formerly Hyprnote): transcription, summaries, Markdown export, bring-your-own model, MCP server; formerly sold as a Granola alternative.

- **CLI plus a published agent skill** (useful). The repo ships a local CLI and MCP server (apps/cli) and a published agent skill (skills/anarlog) so coding agents learn how to drive them.  
  Source: https://github.com/fastrepl/anarlog  
  Absent here, checked: ls src: Nytka.Replay is a test uploader, not a user CLI; no skills/ directory in the repo; docs/how-to/backfill-with-claude.md and docs/guide/mcp.md are prose guides only.  
  Worth doing? Useful: A day to wrap the existing MCP tools in a skill so agents call them correctly. Cost: about a day for a skill file wrapping the existing MCP tools; days for a CLI

## rishikanthc/Scriberr

https://github.com/rishikanthc/Scriberr · MIT · 3,127 stars · last push 2026-09-20. Similar because: Self-hosted, offline audio transcription with diarization, summaries, chat over transcripts and an API, deployed with Docker.

- **Folder watcher** (unknown). Scriberr automatically transcribes any new audio file dropped into a watched folder.  
  Source: https://github.com/rishikanthc/Scriberr  
  Absent here, checked: grep -rIliE 'FileSystemWatcher|folder watch|watched folder' src docs scripts: no hits; audio gets in only through POST /api/v1/import/audio (docs/reference/api.md line 36) or the app.  
  Worth doing? Unknown: UNKNOWN whether Yehor has recordings from sources other than the necklace. Cost: about a day: a background service on a mounted directory that calls the existing import-audio handler

- **Built-in web UI as installable PWA** (not useful). Scriberr serves its own responsive web UI (light/dark, mobile layout) that installs as a Progressive Web App.  
  Source: https://github.com/rishikanthc/Scriberr  
  Absent here, checked: grep -rniE 'wwwroot|UseStaticFiles|MapFallbackToFile' src: none; README.md says Nytka's interface is the Android app, and ROADMAP.md 'More screens' lists a web client as later.  
  Worth doing? Not useful: Weeks; the server is parked and the Android app is the client. Cost: weeks: a client over the existing API (the API has 82 routes), plus a static host

## khoj-ai/khoj

https://github.com/khoj-ai/khoj · AGPL-3.0 · 37,929 stars · last push 2026-08-02. Similar because: Self-hostable personal AI over your own data: semantic search, chat with sources, bring-your-own model, scheduled digests; the same job as Nytka's Search and Ask but for documents instead of speech.

- **User-defined scheduled queries delivered by email** (not useful). An automation saves a query, runs it at a chosen time and frequency in your time zone and emails the research result.  
  Source: https://docs.khoj.dev/features/automations  
  Absent here, checked: Read docs/reference/api.md routes and features.md Ask section: POST /api/v1/ask is one-shot with no saved or scheduled queries; the only scheduled output is the fixed daily digest; grep for smtp found nothing.  
  Worth doing? Not useful: Same as the routines gap, seen from the user side. Cost: about a week together with the routines item above: a saved-query table, scheduler job, email or webhook delivery

- **Custom Ask personas (agents with their own prompt)** (not useful). Khoj lets you set up agents with a custom system prompt (personality field), private or public on the server.  
  Source: https://docs.khoj.dev/features/agents  
  Absent here, checked: Read src/Nytka.Server/Ask/AskPrompt.cs: the summary line says the Ask messages are 'Server code, not a setting'; Summary templates (features.md) change only summaries, not Ask.  
  Worth doing? Not useful: Low value for one user. Cost: about a day: a setting or table of named prompts and a parameter on POST /api/v1/ask

- **Indexing documents next to recordings** (not useful). Khoj reads PDFs, Markdown, Notion, Word, org-mode and image files into the same semantic search.  
  Source: https://raw.githubusercontent.com/khoj-ai/khoj/master/README.md  
  Absent here, checked: Read docs/reference/api.md lines 35-36: imports accept only SRT, VTT, TXT transcripts and WAV or Ogg Opus audio; no document type or parser in src/Nytka.Server/Import.  
  Worth doing? Not useful: Weeks of parsers for a different product. Cost: weeks: parsers, a new source type in search and Ask, and chunking
