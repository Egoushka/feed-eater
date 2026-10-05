# Handoff — feed-eater (2026-10-05)

## Goal

A .NET service that reads every Miniflux entry, sends a daily Telegram digest of what matters to Yehor's projects,
learns from 👍/👎/💡, files ideas into Plane Intake, and serves the whole archive over MCP.

## State

- Local repo `~/Projects/personal/feed-eater`, branch `main`, 2 commits (`97258fc` spec, `f25128e` plan). Not pushed;
  no GitHub repo yet; not in `repos.toml` yet.
- Spec: `docs/specs/2026-10-05-feed-eater-design.md` (status: draft, awaiting Yehor's review).
- Plan: `docs/plans/2026-10-05-feed-eater-v1.md`: 18 tasks (0–17), full code in each step, TDD.
- No product code exists. The plan's code has never been compiled; expect small build fixes.
- Template service: `~/Projects/personal/senses` (same stack and versions, same patterns).

## Decided

- Miniflux is the only fetcher; no crawler.
- Cost caps: triage 60 items a day (`gpt-4.1-nano`), read 12 (`claude-haiku-4-5`), embed everything (`text-embedding-3-small`).
- LiteLLM virtual key with a $10/30d budget. Estimate: about $3.3–3.7 a month.
- Vectors are passed as `real[]` and cast in SQL; no Pgvector NuGet package.
- Profile file is JSON.
- feed-eater gets its own Telegram bot; JARVIS long-polls its bot.
- Code is public (Apache-2.0); the archive and MCP stay tailnet-only.
- Ideas go to Plane Intake; projects without a Plane project go to a new `FEED` project.
- Execution method: recommended subagent-driven with Sonnet implementers. **Yehor has not chosen yet.**

## Tried and failed

- `git init` inside the sandbox fails (it can't write `.git/config`). Git writes in this repo need the sandbox off.

## Next step

1. Ask Yehor to confirm the spec and plan and to choose the execution method (subagent-driven or native).
2. Run Task 0 (preflight; read-only ssh to hedzer), then Tasks 1–16 in a worktree under `.claude/worktrees/`.
3. Task 17 (deploy) asks before every externally visible step and runs in a separate homelab-gitops session.

## How to verify

- `dotnet test FeedEater.slnx` is green after each task. Docker must be running for Testcontainers.
- After deploy: `hz deployed <sha> --wait 10m`, `FEED_MCP_TOKEN=... ./selftest.sh http://100.64.0.2:8104`, and the first
  digest in Telegram by 08:00 Kyiv.
