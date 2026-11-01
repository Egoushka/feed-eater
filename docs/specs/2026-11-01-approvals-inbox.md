# Telegram out, approvals inbox in

Goal: feed-eater stops talking to Telegram. The daily digest and the notices that used to be Telegram messages arrive as items in the approvals
phone shell's inbox; everything else already waits in the swipe stack.

Why: Yehor's standing decision (2026-10-31) is that no messenger bots are built or kept; his own apps are the interface, and the approvals app
is becoming the shell for all of them (`approvals/docs/specs/2026-10-31-shell-design.md`). The v1.0 spec
([2026-10-19-v1.0-app.md](2026-10-19-v1.0-app.md)) already planned "Telegram goes"; its Android client is superseded by the shell (decision 8
of the shell spec), so the parity table there now maps to the stack and the API, not to a feed-eater APK.

## What exists on main (checked at 870bffc)

- Every job that spoke to the owner writes an `inbox` row through `IInbox.AddAsync`; the swipe stack and `/api/v1` read the rows. Telegram is
  an extra send made inside `InboxService` while `Telegram:Token` is set.
- The digest with `Telegram:Token` empty already works: `DigestRun.SendAsync` marks the digest sent and the stack shows its items
  (`SwipeStore`: latest `digests` row with `status = 'sent'`).
- The Telegram bot also takes commands (`/ask /digest /feeds /follows /ideas /learn /quiet /search /unfollow`), button presses and free-text
  replies. The API (`ApiEndpoints`, `ManageApi`) and the settings pages cover the same actions (parity table in the v1.0 spec).

## Design

**Approvals sender.** A typed `ApprovalsClient` does `PUT {Approvals:BaseUrl}/items/{id}` with `Authorization: Bearer {Approvals:Token}`
(approvals server README, "Routes"). Source is `feed-eater`; the id must start with `feed-eater:`. Both settings empty means off: nothing is
sent and nothing fails. The item is `kind: info` (no actions, no callback, so no route to allow-list on the approvals side).

| Moment | Item id | Title | Link |
|---|---|---|---|
| Digest marked sent | `feed-eater:digest:<date>` | `Digest <date>: N items` | the stack (`Api:BaseUrl` + `/swipe`) |
| A `notice` inbox row (no digest today, digest failed, feeds failing) | `feed-eater:<inbox key>` | the notice text | the stack |

The `PUT` is idempotent (200 on update), so a forced resend of a digest rewrites the same item. The notification is a nudge, not the product:
it is sent after the digest is marked sent, and a failure is logged and not retried; the stack already holds the digest. Inbox kinds other than
`notice` (duel, follow, release, idea, review, autopsy, resurface) do not notify; they wait in the stack, as the v1.0 spec says ("default: none").

**Deck.** The swipe `Deck` widget the shell spec names (feed-eater's stack, a "fourth consumer to design when that app is built") does not
exist yet, so the card is a plain inbox card with a link. Follow-up in approvals: a swipe `Deck` screen component.

**Removal (second PR).** Delete `Telegram/` (client, poller, command, callback and reply handlers), the `Telegram` options, the `TelegramCheck`,
the message formatters, the `OutMessage` parameter of `IInbox.AddAsync`, and the Telegram branches of `DigestRun`, `StoryFollower` and the UI
pages. Tests that exercised only Telegram go with it; tests of the behaviour behind it (votes, saves, follows) stay, driven through the service
the callback used.

**Left for later.** The columns `digests.sent_message_ids` and `inbox`'s Telegram bookkeeping become dead. Dropping them is a migration, so it is
a separate PR that waits for Yehor (D-73).

## Acceptance checks

1. `dotnet test FeedEater.slnx` is green after each PR.
2. With `Approvals:BaseUrl` and `Approvals:Token` set, a digest run issues one `PUT /items/feed-eater:digest:<date>` with a valid `info` body
   (title, summary, a link, `expires_at`), and a second forced run issues the same id. With either empty, no request is made.
3. A `notice` inbox row issues one `PUT`; a `duel` row issues none. An approvals outage (5xx, timeout) does not fail the digest or the job.
4. After PR 2: `grep -rIil telegram src tests docs .env.example` finds only this spec and the migration history.

## Yehor's steps (outside this repo)

- approvals stack: add `feed-eater=<token>` to `APPROVALS_SOURCE_TOKENS` (token at least 32 characters).
- feed-eater stack (homelab-gitops): set `FeedEater__Approvals__BaseUrl` and `FeedEater__Approvals__Token`; after PR 2 deploys, remove
  `FeedEater__Telegram__Token` and `FeedEater__Telegram__AllowedUserId` from the stack and delete the Telegram bot token secret from the box.
