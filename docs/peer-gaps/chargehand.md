# Peer gaps: Egoushka/chargehand

Harvested 2026-11-01. Peers are the 7 closest public open-source projects; every gap cites a page that shows the peer has the feature, and "absent here" was checked in this repo by reading or grepping it. Judged by Claude, not yet by Yehor (the stop rule's reviewer).

**Gaps:** 12. Useful 4, not useful 6, unknown 2.

**What it is:** chargehand (Egoushka/chargehand, .NET 10, Apache-2.0, pre-1.0, 1 user) is an orchestrator for coding-agent sessions (Claude Code, OpenCode). Read-only questions return result/v1 with citations checked against a pinned commit; driven batches run sandboxed writing sessions in containers and open tested draft pull requests. It is reachable as MCP (stdio/HTTP), HTTP API, CLI and a browser console.

**Note from the harvest:** Peers found: 7 (all public GitHub, none archived, all pushed within ~7 weeks). No peer found that combines checked-citation Q&A with driven writing sessions; all 7 are coding-agent orchestrators/workspaces, so the overlap is the driven-session half. vibe-kanban's README says it is sunsetting but the repo was pushed 2026-10-29. ComposioHQ/agent-orchestrator now lives at OrchestratorInc/agent-orchestrator; steveyegge/gastown at gastownhall/gastown. Stars/pushed_at from `gh api repos/<name>` on 2026-11-01. Not verified: peers' features were read from README/docs pages, not by running the software; gaps in Yehor's repo are 'not found by grep/read', not proven absent from every file.

## OrchestratorInc/agent-orchestrator

https://github.com/OrchestratorInc/agent-orchestrator · Apache-2.0 · 13,985 stars · last push 2026-11-01. Similar because: A main agent plans work and starts one worker per task in its own branch/worktree, watches PR/CI/review state and feeds failures back to the owning worker until a PR merges: the same task-to-merged-PR loop as chargehand's driven batches, integrator and CI fixer.

- **Merge conflicts routed back to the worker that owns the branch** (useful). The SCM observer polls GitHub/GitLab every 30 s and records PR comments, CI failures and merge conflicts; actionable feedback of all three kinds is sent to the owning session as its next turn.  
  Source: https://github.com/OrchestratorInc/agent-orchestrator/blob/main/docs/architecture.md  
  Absent here, checked: Read docs/guide/driven.md line 80 and docs/adr/0046-integrator-and-ci-fixer.md line 30: on a conflict the integrator leaves 'one comment and no merge'; the CI fixer only handles CI failures (at most 3 attempts). docs/guide/github-start.md line 51 routes review comments to a review round, but I found no conflict-resolution round.  
  Worth doing? Useful: A conflicting draft PR stops at a comment and waits for a person today. Cost: about a week: add a conflict signal to the intake poll and start a continue_from round whose goal is 'rebase and resolve', reusing the review-round limit

- **Kanban board with lanes derived from agent, PR, CI and review state** (unknown). Sessions are sorted into lanes computed at read time (working, needs_input, ci_failed, changes_requested, merge_conflict, mergeable, merged).  
  Source: https://github.com/OrchestratorInc/agent-orchestrator#one-board-for-every-session  
  Absent here, checked: Read the screen list in docs/guide/web-ui.md lines 44-60 (Overview, Needs you, Start, Analytics, Learning, Repositories, Runs table, Batch, Task, Research) and grepped web/console/src for kanban/lane/board: only inbox and batch timeline hits, no PR-state board.  
  Worth doing? Unknown: A lane view may help, but approvals is becoming the shell; where it would live is undecided. Cost: a few days: one console screen over data the batch and task views already read (task state, PR, checks)

## BloopAI/vibe-kanban

https://github.com/BloopAI/vibe-kanban · Apache-2.0 · 28,552 stars · last push 2026-10-29. Similar because: Runs 10+ coding agents (Claude Code, Codex, OpenCode, Gemini CLI...) in isolated per-task workspaces with diff review and PR creation, self-hostable; README announces it is sunsetting, so treat as a feature reference, not a live competitor.

- **Inline diff comments in the UI sent straight to the agent** (not useful). The reviewer comments on lines of the workspace diff inside the app and those comments go to the agent as feedback, before any PR exists.  
  Source: https://github.com/BloopAI/vibe-kanban#overview  
  Absent here, checked: Listed web/console/src/screens/code (CodeBlock, DiffView, Highlighted, diff.ts) and grepped it for comment/annotat/feedback: only a CSS token for syntax comments. The console shows the diff (docs/guide/web-ui.md line 56) and answers questions, but a diff line cannot be commented on.  
  Worth doing? Not useful: Review happens on GitHub after the draft PR opens; a week for line-comment UI is not worth it for one user. Cost: about a week: line-comment UI in DiffView plus a continue_from request that carries the comments as the answer

## openai/symphony

https://github.com/openai/symphony · Apache-2.0 · 27,886 stars · last push 2026-09-15. Similar because: A long-running service that polls an issue tracker, gives each issue an isolated workspace and an agent run, retries with backoff, reconciles against tracker state and lands PRs: the same shape as chargehand's GitHub intake plus driven sessions. It is a spec (SPEC.md) with an Elixir reference implementation.

- **Tracker adapter contract (Linear first) instead of GitHub-only intake** (not useful). Intake is a small adapter interface (fetch issues by active/terminal state, refresh by id) with per-tracker config for kind, labels and states; Symphony ships Linear.  
  Source: https://github.com/openai/symphony/blob/main/SPEC.md#11-issue-tracker-integration-contract  
  Absent here, checked: Listed src/Chargehand.Automation (GitHubIntake*.cs, IntakeHost.cs, IntakeSources.cs only) and grepped src, docs and web for linear/jira/asana: no tracker code. docs/adr/0067-intake-from-plane.md is 'superseded; Plane intake removed 2026-10-26'. GitLabForge.cs covers merge requests, not issue intake.  
  Worth doing? Not useful: One user, GitHub only; no second tracker is wanted. Cost: about 3-5 days per tracker once an IIntakeSource interface is cut out of GitHubIntake (Plane did this once and was removed)

- **Workspace lifecycle hooks (after_create, before_run, after_run, before_remove)** (useful). The repository's WORKFLOW.md declares shell hooks that run around the workspace lifecycle, each with its own failure rule and a shared timeout.  
  Source: https://github.com/openai/symphony/blob/main/SPEC.md#94-workspace-hooks  
  Absent here, checked: Grepped src, docs/guide and docs/adr for 'WORKFLOW.md|before_run|after_create|before_remove|lifecycle hook': no hits. The nearest feature is .chargehand/checks (docs/guide/driven.md line 29), which is a post-run gate only; ADR 0078 (repo declares its environment) is status 'proposed'.  
  Worth doing? Useful: Per-repo setup (env, seed DB) is hard-coded today; hooks in the repo declaration fix that in days. Cost: about 2-3 days: add a hooks list to the repository declaration being designed in ADR 0078 and run it in the session container

- **Live reload of workflow config with last-known-good fallback** (not useful). Config and prompt edits are picked up without restarting the service; an invalid edit keeps the previous good config and logs the error.  
  Source: https://github.com/openai/symphony/blob/main/SPEC.md#62-dynamic-reload-semantics  
  Absent here, checked: Grepped src/docs for hot reload / live reload / FileSystemWatcher / reload of profile or preset: only Vite's dev-server reload in docs/guide/web-ui.md line 85. CLAUDE.local.md says an update needs ~/.chargehand/update.sh to restart the agent; prompts/ and presets/ are read from the working tree at start.  
  Worth doing? Not useful: ADR 0043 already survives restarts; a preset change can wait for one. Cost: about 2 days: file watcher on profile/presets/prompts with schema validation before swap

## OpenHands/OpenHands

https://github.com/OpenHands/OpenHands · MIT · 92,422 stars · last push 2026-11-01. Similar because: Self-hostable agent server that runs coding agents (its own, Claude Code, Codex, Gemini, OpenCode, any ACP agent) locally, in Docker sandboxes or on VMs, with a REST API and scheduled/event automations: overlaps chargehand's runtimes, containers, HTTP API and schedules.

- **Webhook-triggered automations with Slack / Linear / Notion connectors** (unknown). An optional Automation Server runs workflows on a schedule or on incoming events and connects them to Slack, GitHub, Linear, Notion and others.  
  Source: https://github.com/OpenHands/OpenHands#readme  
  Absent here, checked: Read docs/guide/github-start.md line 14 ('Webhook: off. chargehand polls') and docs/adr/0051-intake-from-github.md lines 14 and 87: a webhook was a rejected option and 'a reachable endpoint' is a future condition. Grepped src for webhook: only the incident preset reading an Alertmanager/Grafana payload on stdin (docs/guide/reference.md line 112).  
  Worth doing? Unknown: Polling works; a webhook only matters if alerts should start runs, which nobody has asked for. Cost: about a week: signed-delivery endpoint on the server plus reachability guidance (the server is private by design, ADR 0024)

## generalaction/emdash

https://github.com/generalaction/emdash · Apache-2.0 · 6,138 stars · last push 2026-10-30. Similar because: Runs many coding agents in parallel, each in its own git worktree and branch, takes work from issue trackers, and reviews diffs, creates PRs, inspects CI and merges: the same ticket-to-PR flow as chargehand's intake and driven batches, as a desktop app.

- **Ticket intake from about ten trackers** (not useful). Issues from Linear, GitHub, Jira, GitLab, Asana, Featurebase, Monday.com, Forgejo, Plain or YouTrack can be sent to an agent.  
  Source: https://github.com/generalaction/emdash#what-you-can-do  
  Absent here, checked: Same check as the Symphony tracker gap: src/Chargehand.Automation has GitHub intake only, no Linear/Jira/Asana/GitLab-issue code (grep over src, docs, web returned nothing); Plane intake removed (ADR 0067).  
  Worth doing? Not useful: Ten trackers for one user; the full list is weeks. Cost: weeks for the full list; days per tracker behind one adapter interface

- **Agents on remote machines over SSH/SFTP** (not useful). A project can live on a remote host, reached by SSH agent, key or password, with credentials kept in the OS keychain.  
  Source: https://github.com/generalaction/emdash#remote-projects  
  Absent here, checked: Read docs/guide/runners.md (runners are HTTP services each with Docker; a remote runner gets the repository as a bundle) and grepped docs for ssh: only the push-URL rule in reference.md line 182 and a memory tunnel in memory-and-services.md line 277. No SSH transport to a host without a runner service.  
  Worth doing? Not useful: ADR 0089 (elastic cloud runners) already adds machines. Cost: weeks: a new runner kind that drives a session over SSH without the runner service and Docker

## gastownhall/gastown

https://github.com/gastownhall/gastown · MIT · 18,653 stars · last push 2026-10-28. Similar because: A multi-agent workspace manager (20-30 Claude Code, Codex, Gemini... workers) with a scheduler capacity governor, watchdogs, handoff state in git, a web dashboard, OTLP telemetry and a merge path: closest in governance depth to chargehand's governor, scheduler and integrator.

- **Bisecting merge queue with verification gates** (not useful). The per-project Refinery is a Bors-style queue: workers never push to main; batched changes are verified and bisected on failure.  
  Source: https://github.com/gastownhall/gastown#merge-queue-refinery  
  Absent here, checked: Read docs/adr/0046-integrator-and-ci-fixer.md line 20: 'A merge queue. Not available on every plan and repository; not assumed.' The integrator merges one PR at a time through GitHub auto-merge (docs/guide/driven.md line 80); grepped docs for 'merge queue|bisect': that ADR line only.  
  Worth doing? Not useful: Weeks to build; parallel batches have not broken main yet (no incident cited). Cost: weeks: needs a queue state store and speculative batch builds; or days if it just enables the host's own merge queue

- **Severity-routed escalation with ack and stale re-escalation** (useful). Blockers get P0-P2 severity, route to different channels (mail, email, SMS, Slack), and unacknowledged ones re-escalate one level after a threshold, up to a maximum.  
  Source: https://github.com/gastownhall/gastown/blob/main/docs/design/escalation.md  
  Absent here, checked: Read src/Chargehand.Automation/Notify (ApprovalsSink, NtfyQuestionSink, QuestionNotifier) and docs/guide/phone-answers.md; grepped for remind/renotif/re-escalat/unacknowledged: none. Notices go out once with a fixed priority (ApprovalsSink.cs line 59: priority 4); ADR 0064 resolves or dismisses inbox items but does not re-raise them.  
  Worth doing? Useful: An unanswered question holds a batch slot and money; a timed re-send is two days. Cost: about 2 days: a timer over open inbox items that re-sends with higher ntfy priority

## RunMaestro/Maestro

https://github.com/RunMaestro/Maestro · AGPL-3.0 · 3,528 stars · last push 2026-11-01. Similar because: Orchestrates Claude Code, Codex and OpenCode in worktrees with unattended 'Auto Run' playbooks, a headless CLI usable from cron/CI with JSONL output, cost tracking, mobile remote control and an MCP server: overlaps chargehand's schedules, CLI and console.

- **Automatic retry with backoff and resume after availability or quota errors** (useful). Overloaded/5xx/429/network errors retry on a 30 s to 30 min schedule and quota exhaustion is probed at the reset time; an Auto Run batch pauses and resumes itself, re-reading which tasks are already done.  
  Source: https://docs.runmaestro.ai/agent-resilience  
  Absent here, checked: Read docs/guide/driven.md line 232 and docs/guide/reference.md line 167: a session that hits a usage or rate limit is handed over as a [partial] draft PR and 'the task stays rate_limited and retryable' (ROADMAP.md: the scheduler pauses new starts). I found no timer that re-queues the task after the limit resets; grepped for failover/fallback endpoint: none.  
  Worth doing? Useful: Overnight batches on a subscription hit limits; resuming at the reset time saves a manual restart. Cost: about 2-3 days: use the reset time from the limit error to re-queue via continue_from, capped by the existing continuation limits
