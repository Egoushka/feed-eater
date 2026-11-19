# Adoption board: hand test (2026-11-19)

Question: if cards were built from Yehor's own stack plus this week's signals, would he act on them?
Go rule: he would act on 2 or more of 5. Otherwise stop and report.

Repos used: feed-eater, homelab, chargehand (`personal/orchestrator`). Inventory: csproj, `bootstrap/versions.env`,
Compose images, `repos.toml`. Signals: 7 feed digests, Trendshift (today only), HN Algolia (since 2026-11-12), deps.dev.
OSS Insight trends is UNREACHABLE (API reports the ranking metric unavailable since 2026-03-01).
Caveat: signal titles and dates come from a fetch sweep; I have not read the linked articles yet. Stars were not fetched.
Version bumps are excluded on purpose (e.g. ModelContextProtocol 2.2.0 in feed-eater vs 2.4.0 in chargehand is Renovate's job).

Result: act on all 5 (Yehor, relayed 2026-11-19). Go gate passed, 5/5.

## Card 1: Postgres 19 and CVE-2026-6460: check your exposure
- **Where:** homelab runs `postgres:18` or `18-alpine` in about 12 stacks (pangolin, paperless, langfuse, senses, firefox-sync, monitoring), plus `pgvector:0.8.x-pg18` in feed-eater, chronicle and nytka.
- **Evidence:** PostgreSQL 19 released 2026-11-19; CVE-2026-6460 "arbitrary code execution" published 2026-11-13; point releases 18.7 / 17.12 the same day. https://www.postgresql.org/support/security/CVE-2026-6460/
- **Not known yet:** which versions and which privilege level the CVE needs. UNKNOWN until the advisory is read.
- **Effort:** assess = read the advisory and `lab` the running minor per container (small). Upgrading to 19 is a separate, larger card.
- **Gain:** know whether any box database is exposed; a clear go/no-go on pg19.
- **Radar state if picked:** assess.
- **Rating:** act (Yehor, 2026-11-19)

## Card 2: Tailscale netmap caching for the WARP flap
- **Where:** homelab / workspace access path. Your notes say the tailnet path from the Mac flaps (WARP + Tailscale on `100.64.0.0/10`) and a userspace node is the workaround.
- **Evidence:** Tailscale blog "netmap caching" (keeps the tailnet up on bad links), 2026-11-18. https://tailscale.com/blog/netmap-caching
- **Not known yet:** whether it applies to a self-hosted headscale 0.29.7 control plane, which is what nickel uses. If headscale does not support it, this card is `hold`.
- **Effort:** read the post and headscale release notes (small); trial = bump the client on the Mac and force a flap.
- **Gain:** fewer manual fallbacks to the SOCKS node.
- **Rating:** act (Yehor, 2026-11-19)

## Card 3: wait for PR events in `gh` instead of polling
- **Where:** chargehand and the `ws` tooling. Workspace rules already work around polling cost (`gh run watch -i 60`, `gh pr checks` failing on the devbox, `ws wait`).
- **Evidence:** GitHub changelog 2026-11-18 "Wait for pull request events with GitHub CLI". https://github.blog/changelog/2026-11-18-wait-for-pull-request-events-with-github-cli
- **Not known yet:** exact command and whether it works on the devbox where `gh pr checks` fails.
- **Effort:** small. Trial = swap one wait in `ws wait` on a throwaway branch and compare API calls per minute.
- **Gain:** fewer REST calls and a simpler wait path for every merge.
- **Rating:** act (Yehor, 2026-11-19)

## Card 4: measure your MCP tool-schema size (feed-eater and chargehand)
- **Where:** feed-eater `feed_*` tools (ModelContextProtocol.AspNetCore 2.2.0) and chargehand's MCP server (2.4.0). Both are loaded into every session, so tool schemas cost context on each call.
- **Evidence:** Metabase's rebuild lessons on MCP tool design (2026-11-17, HN 11 pts, https://www.metabase.com/blog/mcp-tool-design-lessons); CtxWise on schema bloat (https://github.com/FramY2/ctxwise, 2026-11-13, one source, low confidence).
- **Effort:** small. Trial = dump `tools/list` for each server, count tokens, trim descriptions on a branch, re-count.
- **Gain:** a measured number of tokens saved per session. Stop result: under 10% saving, `hold`.
- **Rating:** act (Yehor, 2026-11-19)

## Card 5: replace hand-written RSS/Atom parsing with `System.ServiceModel.Syndication`
- **Where:** feed-eater `Sources/FeedParser.cs` (189 lines) plus `SafeXml.cs`. JSON Feed would stay hand-written.
- **Evidence:** inventory only, no signal this week. Included on purpose to test whether a pure-inventory card is worth acting on.
- **Effort:** medium. Trial = swap RSS/Atom parsing, run the existing parser tests and a feed corpus diff.
- **Gain:** less code to own; risk is regression on odd feeds.
- **Rating:** act (Yehor, 2026-11-19)

## Noted and dropped
- Telegram.Bot for `TelegramClient.cs`: the inventory suggests it, but Telegram is being removed (decision 4), so no card.
- Quartz/Hangfire for chargehand's `Scheduler.cs`, Komodo for `homelab-sync.sh`: inventory-only, no signal; kept for later cards.

# Trial results (2026-11-19, run by hand: the trial queue is not built yet)

| Card | Verdict | Measured |
|---|---|---|
| 1 Postgres CVE-2026-6460 | **adopt** the patch; Postgres 19 hold | 13 of 13 running Postgres servers were below the fix (12 on 18.6, 1 on 17.11; fixed in 18.7 / 17.12). Approved by Yehor; shipped in a homelab PR (see its result below). pgvector has no pg19 tag and 19 was RC2 at the time. |
| 2 Tailscale netmap caching | **hold** | Needs the `cache-network-maps` node attribute from the control plane; whether headscale 0.29.7 sends it is UNKNOWN. It would not fix the WARP routing clash. Cheapest next test: devbox `TS_USE_CACHED_NETMAP`, stop headscale 1 min, `tailscale ping`. |
| 3 `gh` wait for PR events | **hold** | `gh event listen` needs gh 2.105+ (installed 2.104.0; newer binary at `scratch/gh-2105`). Live call returned `HTTP 403: Event subscriptions are not enabled for this account` (preview feature). Current `ws wait`: about 3 calls/min. After: not measurable. Retest when the account is enrolled. |
| 4 MCP schema size | **trial** (feed-eater), hold (chargehand) | feed-eater 10 tools: 9122 -> 7355 bytes (-19.4%, about 2280 -> 1838 tokens, chars/4 estimate), descriptions only; builds clean, no test asserts description text; Docker tests unrun (no Docker on the devbox). Branch `trial/adoption-card-4` (bfa33d2), local. chargehand 5 tools: 36002 bytes, about 65% is output schemas; descriptions trim is under 10%. |
| 5 Syndication parser | **hold** | RSS/Atom swap works, LOC net -7, 30 of 31 parser tests pass. Regression: RSS `<description>` with unescaped nested HTML makes the library throw and rejects the whole feed; the old parser reads it. Corpus: 17 feeds, about 250 entries, 0 title/link/guid/date mismatches, 6 feeds differ only by whitespace. Branch `trial/adoption-card-5`, uncommitted, local. |

Radar states from this test: card 1 adopt, card 4 trial, cards 2, 3, 5 hold.
