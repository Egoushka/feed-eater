# Peer gaps: Egoushka/approvals

Harvested 2026-11-01. Peers are the 7 closest public open-source projects; every gap cites a page that shows the peer has the feature, and "absent here" was checked in this repo by reading or grepping it. Judged by Claude, not yet by Yehor (the stop rule's reviewer).

**Gaps:** 15. Useful 7, not useful 4, unknown 4.

**What it is:** Self-hosted approval inbox for Yehor's setup: a Node 26 / TypeScript / SQLite server on hedzer holds approval items that his n8n workflows PUT in; a native Android app (Kotlin, Compose) reads, edits and decides them. A decision is an HMAC-signed callback to the source's own n8n webhook; the server never performs the side effect. Replaces ntfy approval cards. Server v0.1.0 live 2026-10-19, Android app built and waiting to be paired.

**Note from the harvest:** Checked and excluded as peers: humanlayer/humanlayer (Apache-2.0, 12.7k stars) pivoted to a coding-agent control plane and its README no longer describes approvals; gethandup/handup is the closest product (approval inbox with Android app, relay, allow rules, presence routing, previews) but its application source is proprietary and not in the public repo, so it is not open source; RLASAF12/agent-gate (MIT) is archived and moved to agent-guardrails; Kestra and Activepieces were considered as workflow-engine peers but dropped for weaker similarity. The 7 chosen are uneven: Impri and awaithumans are direct peers, Email-Ops and Happy are partial matches, ntfy/Windmill/Trigger.dev are infrastructure or platform features. Windmill licence is AGPL-3.0 core with enterprise features per its README; ntfy is dual Apache-2.0/GPL-2.0 per its README (GitHub reports Apache-2.0). Star counts and push dates are from gh api on 2026-11-01.

## sekera-radim/impri

https://github.com/sekera-radim/impri · MIT (self-hostable core; hosted cloud is the paid offering) · 6 stars · last push 2026-10-30. Similar because: Self-hostable approval inbox where an agent/workflow proposes an action, a human approves, rejects or edits the draft (also from phone), and the edited text goes back to the source; closest match in problem and user.

- **MCP server and poll/long-poll read-back for agents** (useful). Exposes push_action / await_decision / report_result / inbox_status as MCP tools (and the same as REST), so an agent can submit an item and wait for the answer; sources can also poll GET /v1/actions/:id instead of needing a callback URL.  
  Source: https://github.com/sekera-radim/impri#what-the-agent-can-do  
  Absent here, checked: grep -rniE 'mcp' server/src returns only the senses client; server/src/http.ts has only PUT/DELETE /items/{id} for sources (no GET for a source to read status); spec section 'Build and process' says the items API is not an agent tool in v1.  
  Worth doing? Useful: Lets agents ask for approval directly instead of through n8n; a day over the existing PUT. Cost: about a day for a thin MCP wrapper over the existing PUT plus a source-scoped GET status route; a few days if await/long-poll and a tool registry entry in context/ai-setup are included

- **Rules engine: auto-approve, auto-reject, set expiry, escalate** (useful). Rules match on kind glob, payload field conditions (eq/lt/gt/in...) and target host, run once at creation in priority order, and each firing is logged.  
  Source: https://github.com/sekera-radim/impri/blob/main/docs/rules.md  
  Absent here, checked: grep -rniE 'rule|auto.?approve|auto.?reject|escalat' server/src finds only the callback allowlist and the notify batching rule; no per-item policy exists in store.ts/items.ts.  
  Worth doing? Useful: Routine low-risk items skip the phone; 2-3 days. Needs a reviewed rule list so nothing risky auto-approves. Cost: about 2-3 days: a rules table or config file, a matcher run in upsert, and a decision path that signs and posts without a human tap

- **Retried decision delivery and an expiry event to the source** (useful). Outbound webhook is retried up to 5 times with growing delay then dead-lettered, and an 'expired' status is delivered to the source as well as approve/reject.  
  Source: https://github.com/sekera-radim/impri/blob/main/docs/webhooks.md  
  Absent here, checked: server/src/decide.ts does one fetch with a 10 s timeout and leaves the item 'failed' until a person taps Retry; grep -n 'expir' server/src/*.ts shows expiry only gates decide() (410) and the done view, never a callback to the source.  
  Worth doing? Useful: Outreach never learns an item expired, and a failed callback is lost today. Cost: about a day: a retry timer over 'failed' items with backoff, plus one extra signed callback on expiry

- **Bulk approve/reject of many items** (useful). POST /v1/actions/bulk-decision takes up to 50 ids with one verdict and per-item results; the web inbox has a select mode that uses it (edits are not allowed in bulk).  
  Source: https://github.com/sekera-radim/impri/blob/main/docs/approve-multiple-ai-agent-actions-at-once.md  
  Absent here, checked: grep -niE 'multi|selected|bulk' android/app/src/main/java/com/yehor/approvals/ui/*.kt finds only source FilterChips; http.ts has no bulk route (decide is one item per POST).  
  Worth doing? Useful: Memory-loop sends up to 8 items at once; one tap for all is 1-2 days. Cost: about 1-2 days: a select mode in the queue plus a loop over decide() with per-item result

## awaithumans/awaithumans-human-in-the-loop-ai-agents

https://github.com/awaithumans/awaithumans-human-in-the-loop-ai-agents · Apache-2.0 · 27 stars · last push 2026-10-20. Similar because: Self-hosted human-in-the-loop service: an agent creates a task, a human answers in a dashboard (or Slack/email), the agent resumes with a typed answer; same shape as items + decisions + callbacks.

- **AI verification of the human's answer** (unknown). A verifier (Claude/OpenAI/Gemini/Azure) checks each human submission against instructions and re-sends the task with the reason if it fails, catching 'approved without reading'.  
  Source: https://github.com/awaithumans/awaithumans-human-in-the-loop-ai-agents#ai-verification  
  Absent here, checked: grep -rniE 'verif|llm|claude|openai' server/src returns only the HMAC signing helpers; decide.ts forwards the tap straight to the callback.  
  Worth doing? Unknown: A check on edited emails sounds right, but it adds an LLM call and its false-block rate is unmeasured. Cost: about 2 days: a pre-callback hook that calls an LLM through the existing gateway and a 'needs another look' state in the app

- **Typed response schema and generated forms** (unknown). Responses are validated against a Pydantic/Zod schema and the form is rendered from that schema per channel, so choices, numbers and flags are first-class and not only free text.  
  Source: https://github.com/awaithumans/awaithumans-human-in-the-loop-ai-agents#architecture  
  Absent here, checked: server/src/items.ts accepts editable paths only of the form body.<name> that are strings (regex ^body\.([A-Za-z0-9_]+)$ and typeof === 'string'); no number/boolean/enum fields, no validation of edited values.  
  Worth doing? Unknown: Matters once the 9 migration sources land; not yet clear which need typed fields. Cost: about 3-4 days: extend the contract with field types and constraints, validate in decide(), render in Compose, add fixtures on both sides

- **Client SDK that turns 'ask a human' into one awaitable call** (useful). await_human() / awaitHuman() in Python and TypeScript create the task, wait (long-poll or via Temporal/LangGraph adapters) and return the typed answer.  
  Source: https://github.com/awaithumans/awaithumans-human-in-the-loop-ai-agents#typescript  
  Absent here, checked: ls of the repo shows only contract/, server/, android/, docs/; no client package. Sources talk raw HTTP from n8n Code nodes (spec 'Contract').  
  Worth doing? Useful: Two consumers already exist (n8n snippet, outreach); a small package is two days. Cost: about 2 days for a small TypeScript package over PUT plus the signature verifier (reuses contract/ fixtures); more if long-poll is added

## binwiederhier/ntfy

https://github.com/binwiederhier/ntfy · Apache-2.0 and GPL-2.0 (dual) · 35,215 stars · last push 2026-11-01. Similar because: The notification layer approvals replaces and rides on: self-hosted HTTP pub-sub with an Android app, priorities and action buttons; the old approval cards were ntfy cards.

- **Action buttons on the notification itself** (useful). A notification carries up to three buttons (view, http request, broadcast, copy) so a decision can be sent without opening the app.  
  Source: https://docs.ntfy.sh/publish/#action-buttons  
  Absent here, checked: grep -n 'addAction' -r android/app/src/main returns nothing; Notifier.kt builds only a tap PendingIntent (lines 27-29) and the push is content-free ('changed').  
  Worth doing? Useful: The ntfy cards let you act from the shade in one tap; this restores that. Cost: about 1-2 days for Snooze/Open actions on the Android notification via PendingIntent; approving from the shade needs a safe design (per-item confirm, no edited text) and is a few days

- **Phone-call escalation for top priority** (not useful). A message can trigger a voice call that reads it aloud (X-Call header), for alerts that must not be missed.  
  Source: https://docs.ntfy.sh/publish/#phone-calls  
  Absent here, checked: server/src/notify.ts and push.ts only send Web Push; priority 5 bypasses senses suppression (notify.ts line 15) but nothing escalates beyond a normal push.  
  Worth doing? Not useful: Needs a Twilio account for a rare case. Cost: unclear: ntfy docs require a verified number and a Twilio-backed setup on the server (not checked in his ntfy config); likely a day if already configured, otherwise a new external account

## slopus/happy

https://github.com/slopus/happy · MIT · 24,444 stars · last push 2026-11-01. Similar because: Open-source self-hostable mobile client (with its own relay server and push server) for deciding on and steering coding agents from a phone. Weaker match than the others: it steers sessions rather than queueing approvals.

- **End-to-end encrypted storage: the server cannot read item content** (not useful). Clients encrypt session data, messages and state (AES-256-GCM per-session keys, key wrapped per device); the relay stores opaque blobs and only ids, versions and timestamps in clear.  
  Source: https://github.com/slopus/happy/blob/main/docs/encryption.md  
  Absent here, checked: server/src/db.ts stores each item as a plain 'json text' column in SQLite; store.ts reads it back with JSON.parse. Spec 'Auth' relies on tailnet/internal-only vhost plus hashed device keys, not content encryption.  
  Worth doing? Not useful: The server stays on the trusted tailnet box. Cost: weeks: sources would have to encrypt for the device key, the server could no longer validate or render fields, and edited-value hashing and the senses/batching logic need rework

- **iOS and web clients** (unknown). One codebase ships the mobile app for iOS, Android and a web client, plus a desktop shell.  
  Source: https://github.com/slopus/happy#how-happy-fits-together  
  Absent here, checked: repo has only android/; spec section 'Not in v1' lists 'a web client'; grep -rniE 'ios|iphone|pwa' in docs/specs, server/README.md, android/README.md found nothing relevant.  
  Worth doing? Unknown: A read-only browser view helps when the phone is away; web client is listed 'Not in v1', undecided. Cost: weeks for a second native client; a few days for a read/decide web page served by the existing server (needs a browser auth path beyond the device key)

## windmill-labs/windmill

https://github.com/windmill-labs/windmill · AGPL-3.0 core (Apache-2.0 for clients; some features Enterprise-only) · 18,349 stars · last push 2026-11-01. Similar because: Self-hosted workflow platform (like his n8n) whose flows suspend for human approval and resume on an approval page, a form, Slack or Teams. Included for the approval-step feature set, not as a full peer.

- **Secret resume/cancel URLs usable from any browser** (not useful). A flow step generates one-time resume and cancel URLs that can be put in an email or message; opening one approves without an app account (login can optionally be required).  
  Source: https://www.windmill.dev/docs/flows/flow_approval  
  Absent here, checked: server/src/devices.ts / http.ts: every decision route under /api needs a paired device bearer key; no unauthenticated or link-based decision path exists.  
  Worth doing? Not useful: One-time links add an attack surface for a fallback nobody needs yet. Cost: about 2 days for signed single-use links to a minimal server-rendered page, but it opens an unauthenticated write path on an internal-only vhost; needs a threat review

- **Required number of approvers (quorum)** (not useful). A suspend step can require N approval events (optionally from a specific group) and tracks who approved; self-approval can be blocked. Group and form permission parts are Cloud/Enterprise in Windmill.  
  Source: https://www.windmill.dev/docs/flows/flow_approval  
  Absent here, checked: store.ts beginDecision is a conditional update so only the first decision wins and the spec ('Errors and edge cases') says 'the first decision wins, the second gets 409'; single owner, no approver model.  
  Worth doing? Not useful: Single owner. Cost: weeks for multi-user identity and an approver table; irrelevant until a second person exists

## triggerdotdev/trigger.dev

https://github.com/triggerdotdev/trigger.dev · Apache-2.0 · 16,672 stars · last push 2026-10-31. Similar because: Open-source, self-hostable background-job platform with 'waitpoint tokens': a run pauses until a human or service completes a token with a payload, with timeouts and chat-channel approval buttons. Platform feature rather than a standalone inbox.

- **Tags and filtered listing of pending waits** (unknown). Tokens carry tags and can be listed by status (waiting/completed/timed out), tag, idempotency key and time range; a single token can be retrieved with its output.  
  Source: https://trigger.dev/docs/wait-for-token  
  Absent here, checked: server/src/items.ts Item interface has no tags/labels field; http.ts GET /api/items filters only by view=open|done|feed; no source-facing list or retrieve route exists.  
  Worth doing? Unknown: Grouping helps when 9 sources land; not needed before. Cost: about a day: an optional tags array in the contract, a column or JSON filter, and filter chips beyond the current per-source ones

## Unicorn-Commander/Email-Ops

https://github.com/Unicorn-Commander/Email-Ops · Apache-2.0 · 1 stars · last push 2026-10-28. Similar because: Self-hosted human approval queue for agent-drafted outbound email (the outreach use case that motivated approvals), with MCP access. A mail suite, so the match is on the approval part only; very young project.

- **Per-agent autonomy levels and a pause switch** (useful). Each agent is L0/L1/L2: L0 stages every send, L1 sends internal mail and stages external, L2 may auto-send a narrow class (in-thread reply, trusted recipients, no attachments, at most 5 recipients); one agent or the whole fleet can be paused; unattended sends are audited.  
  Source: https://github.com/Unicorn-Commander/Email-Ops#highlights  
  Absent here, checked: grep -rniE 'pause|autonomy|disable' server/src only finds device revoke (devices.ts) in cli.ts/devices.ts; there is no per-source policy or pause, and every decision item waits for a tap.  
  Worth doing? Useful: Stops one noisy source without revoking its token; two days. Cost: about 2 days for a per-source policy table (staged vs auto) and a 'pause source' toggle that rejects PUTs; the auto path overlaps with the Impri rules-engine gap
