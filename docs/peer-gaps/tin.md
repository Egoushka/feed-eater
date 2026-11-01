# Peer gaps: Egoushka/tin

Harvested 2026-11-01. Peers are the 7 closest public open-source projects; every gap cites a page that shows the peer has the feature, and "absent here" was checked in this repo by reading or grepping it. Judged by Claude, not yet by Yehor (the stop rule's reviewer).

**Gaps:** 18. Useful 6, not useful 9, unknown 3.

**What it is:** tin lets an AI agent on a computer call an Android 16+ phone's apps through MCP. A Rust binary running as the shell user under Shizuku turns every app's AppFunctions into typed MCP tools, denies everything by default via a policy file, never runs money functions, serves only the shell uid over an abstract unix socket (reached with adb forward), and holds state-changing calls until the owner taps Run in the approvals app.

**Note from the harvest:** The local checkout of tin main is 2 commits behind origin (the system tools and approval gate, #5, are on origin/main and in .claude/worktrees/speech on branch feat/speech along with speech). own_features and every own_check below were read from the checkout and from /Users/yehorhrabovskyi/Projects/personal/tin/.claude/worktrees/speech (the superset). Originally planned peer alex4dev/appfunctions-caller (the only other direct AppFunctions caller found) was dropped because the repo has no licence; minhalvp/android-mcp-server (Apache-2.0, 810 stars, last push 2025-05-28) took the 7th slot. Few projects do the same thing as tin (AppFunctions over MCP with a policy layer); the other peers are the nearest 'agent drives an Android phone through MCP' projects, which is why several gaps are about the UI-automation side.

## mobile-next/mobile-mcp

https://github.com/mobile-next/mobile-mcp · Apache-2.0 · 10,199 stars · last push 2026-10-30. Similar because: The largest MCP server that lets an agent operate real Android phones (and iOS); same user: a developer with an agent and adb.

- **Screenshot tool** (unknown). mobile_take_screenshot (and mobile_save_screenshot) return the screen as an image so a vision model can see what the accessibility text misses.  
  Source: https://github.com/mobile-next/mobile-mcp/blob/main/README.md#-available-mcp-tools  
  Absent here, checked: grep -rniE 'screencap|screenshot' core/src android/app/src/main scripts in .claude/worktrees/speech: no hits. system.rs VERBS has only screen_text (uiautomator text), no image.  
  Worth doing? Unknown: Depends on whether the phone layer drives screens at all; tin's thesis is typed functions, not pixels. Cost: about a day: one read verb running `screencap -p` as shell, returned as MCP image content, behind the screen_text banking-app refusal.

- **Device logs and crash reports** (useful). mobile_get_device_logs (logcat), mobile_list_crashes and mobile_get_crash let the agent read logs and full crash reports.  
  Source: https://github.com/mobile-next/mobile-mcp/blob/main/README.md#-available-mcp-tools  
  Absent here, checked: grep -rniE 'logcat|crash' core/src: no hits; VERBS list in core/src/system.rs has no log verb.  
  Worth doing? Useful: Failed calls (code 1000 on a locked phone) are opaque today; a bounded logcat read is cheap. Cost: a few hours: read verb around `logcat -d -t N` with a package filter; crash list needs `dumpsys dropbox`.

- **Batch several tool calls in one request** (not useful). mobile_batch_commands runs multiple tools in sequence (click, type, click) in one call, optionally listing screen elements at the end.  
  Source: https://github.com/mobile-next/mobile-mcp/blob/main/README.md#-available-mcp-tools  
  Absent here, checked: grep -rniE 'batch' core/src: no hits. Every tin call is one round trip of about 1.5 s over adb forward (README Numbers).  
  Worth doing? Not useful: One batch means one approval for many writes; it weakens the per-call gate. Cost: about a day for a batch tool; the hard part is that each step still needs its own approval ticket in gate.rs.

- **App lifecycle tools: list, terminate, install, uninstall, clear data** (not useful). mobile_list_apps, mobile_terminate_app, mobile_install_app, mobile_uninstall_app and mobile_clear_app_data manage apps, not just launch them.  
  Source: https://github.com/mobile-next/mobile-mcp/blob/main/README.md#-available-mcp-tools  
  Absent here, checked: grep -rniE 'pm install|uninstall|pm clear|force-stop|list-packages|pm list' core/src: no hits. system.rs has only `launch` (monkey) for apps.  
  Worth doing? Not useful: Install, uninstall and clear-data are destructive writes; the function list already says which apps matter. Cost: a day: write verbs (force-stop, pm clear, pm uninstall) all held for approval, plus a read verb `pm list packages`.

## us-all/android-mcp-server

https://github.com/us-all/android-mcp-server · MIT · 15 stars · last push 2026-07-10. Similar because: Android MCP server with the same safety idea as tin: read-only by default, writes and shell behind separate explicit flags, category allow/deny lists.

- **Secret redaction in error output** (useful). API keys, tokens and passwords are redacted from all error outputs before they reach the model.  
  Source: https://github.com/us-all/android-mcp-server#security  
  Absent here, checked: grep -rniE 'redact|sanitiz' core/src: only `dumpsys notification --noredact` in system.rs (it asks for unredacted data); no scrubbing of results or errors in mcp.rs tool_error / call_tool.  
  Worth doing? Useful: Notifications and screen text can carry one-time codes that would go straight to the model. Cost: a few hours: a regex scrub pass over tool results and errors in mcp.rs, with tests.

- **search-tools meta tool for runtime tool discovery** (not useful). An always-on search-tools tool lets the client find tools by keyword, so the tool list can stay narrow; the README measures 9,200 schema tokens at 76 tools down to 2,500 with category filters.  
  Source: https://github.com/us-all/android-mcp-server#token-efficiency  
  Absent here, checked: grep -rniE 'search' core/src/mcp.rs: no hits; tools/list in mcp.rs returns every allowed tool with its full schema (61 AppFunctions plus 12 system tools before policy).  
  Worth doing? Not useful: Claude Code already defers and searches tools on the client; 61 functions fit. Cost: about a day: one tool that searches names and descriptions of the allowed set, plus a 'lazy' mode where tools/list returns only a few.

- **MCP prompts and resources** (not useful). Ships 5 prompts/list workflow templates and read-only resources such as android://devices and android://app/{package}/activities.  
  Source: https://github.com/us-all/android-mcp-server#mcp-prompts-5  
  Absent here, checked: grep -rniE 'prompts/list|resources/list' core/src: no hits; mcp.rs handles initialize, tools/list, tools/call only.  
  Worth doing? Not useful: Low value for one client and one user. Cost: a day: add two JSON-RPC methods; the content (which prompts, which resources) is the real work.

## giftiar/MCP-bridge

https://github.com/giftiar/MCP-bridge · GPL-3.0 · 0 stars · last push 2026-10-25. Similar because: Android MCP server that combines an on-phone app, local HTTP MCP endpoint and Shizuku for shell-level powers, with its own guardrails.

- **Emergency stop (kill switch)** (useful). A floating overlay ball; one tap pauses the agent and every write operation is blocked immediately until tapped again.  
  Source: https://raw.githubusercontent.com/giftiar/MCP-bridge/main/README.md  
  Absent here, checked: grep -rniE 'emergency|panic|kill|pause' core/src android/app/src/main in .claude/worktrees/speech: no hits. The only brake is the approval tap per call and deleting the policy file.  
  Worth doing? Useful: A looping agent is stopped by one tap, not one approval at a time. Cost: a day or two: an overlay or notification action in the Kotlin app that flips a shared flag tin checks before every call.

- **Virtual display mode** (not useful). Apps run on a separate virtual display created through Shizuku, so the agent works without touching the user's own screen.  
  Source: https://raw.githubusercontent.com/giftiar/MCP-bridge/main/README.md  
  Absent here, checked: grep -rniE 'virtual.?display|vd_' core/src android/app/src: no hits. tin's tap/swipe/launch act on the main display.  
  Worth doing? Not useful: Weeks of work for a screen-driving mode that is outside tin's scope. Cost: weeks: needs a display-capable Shizuku user service, input routing per display and per-display screenshots/OCR.

- **Offline OCR fallback when the UI tree is empty** (not useful). When the accessibility node tree cannot be read, it recognises text from a screenshot with on-device ML Kit OCR; nothing is uploaded.  
  Source: https://raw.githubusercontent.com/giftiar/MCP-bridge/main/README.md  
  Absent here, checked: grep -rniE 'ocr' core/src android/app/src: no hits. screen_text in core/src/system.rs depends only on a uiautomator dump.  
  Worth doing? Not useful: Needs screenshots first and a week of work; outside tin's scope. Cost: about a week: needs a screenshot verb plus a recogniser; tin already ships sherpa-onnx for speech, so an on-device ONNX OCR model is the route that fits the Rust side.

- **Element refs that expire when the screen changes** (useful). A snapshot returns element refs (@7) with a revision; acting on a ref after the screen changed is refused, so a stale tap cannot hit the wrong button.  
  Source: https://raw.githubusercontent.com/giftiar/MCP-bridge/main/README.md  
  Absent here, checked: core/src/system.rs: tap takes raw x, y pixels (MAX_COORD check only); screen_text returns bounds and clickable but no ref or revision; grep -rn 'rev' finds no screen revision.  
  Worth doing? Useful: Approvals add minutes between seeing the screen and the tap; stale refs are a real misfire risk. Cost: about two days: hash the last uiautomator dump, hand out refs, and make tap accept ref and reject if the hash changed.

## qazmko72/android-mcp-server

https://github.com/qazmko72/android-mcp-server · MIT · 3 stars · last push 2026-10-09. Similar because: A native Android app that serves MCP on the phone, built around phone-side security; optional Shizuku for app management.

- **Bearer token for non-local callers, stored in Android Keystore** (not useful). Remote (LAN) requests need an exact Authorization Bearer token: 256-bit random, encrypted with a Keystore AES-GCM key, resettable to invalidate at once.  
  Source: https://raw.githubusercontent.com/qazmko72/android-mcp-server/main/README.md  
  Absent here, checked: grep -rniE 'bearer|authorization' core/src android/app/src/main in .claude/worktrees/speech: no hits in the MCP transport (transport.rs relies on SO_PEERCRED uid 0 or 2000 only; gate.rs signs approval decisions with HMAC but that is another channel).  
  Worth doing? Not useful: tin is reachable only through adb forward; revisit if it ever listens on a network. Cost: two to three days: token store in the Kotlin app, a TCP listener in tin, header check.

- **Rate limit and brute-force lockout** (not useful). 60 requests per IP per minute for remote access; five failed authentications block that IP for five minutes.  
  Source: https://raw.githubusercontent.com/qazmko72/android-mcp-server/main/README.md  
  Absent here, checked: grep -rniE 'rate.?limit|per minute|lockout|block' core/src: no hits. transport.rs enforces only MAX_BODY (1 MiB) and the peer uid.  
  Worth doing? Not useful: Same reason: only matters for network callers; approvals already gate writes. Cost: about a day: a token bucket in transport.rs or in call_tool; only meaningful together with a network listener.

## shizzgar/shizuku-mcp

https://github.com/shizzgar/shizuku-mcp · Apache-2.0 · 9 stars · last push 2026-03-29. Similar because: MCP server for an Android phone whose privileged commands run through Shizuku, served over streamable HTTP.

- **doctor self-check tool** (useful). A doctor system tool reports whether the setup works (Shizuku/rish reachable, prerequisites in place) so the agent or user can diagnose a broken install.  
  Source: https://raw.githubusercontent.com/shizzgar/shizuku-mcp/master/README.md  
  Absent here, checked: grep -rniE 'doctor' core/src android scripts in .claude/worktrees/speech: no hits. Only `tin probe` (GET /health) and scripts/peer-check.sh exist.  
  Worth doing? Useful: Policy missing means everything is denied and the agent cannot tell why; a doctor tool says so. Cost: a few hours: a read tool returning policy path and rule counts, function count from cmd, Shizuku service state, approvals server reachability, last call error.

- **Long-running calls with poll and cancel** (unknown). One-shot commands that outlive the sync limit continue in the background and can be polled or cancelled by the client.  
  Source: https://raw.githubusercontent.com/shizzgar/shizuku-mcp/master/README.md  
  Absent here, checked: core/src/backend.rs sets a fixed `--timeout-duration` (timeout_secs: 30 in main.rs) and call_tool in mcp.rs blocks; grep -rniE 'poll|cancel' core/src finds nothing relevant to tool calls.  
  Worth doing? Unknown: Unknown whether an approval-held call really outlives the client timeout; not measured. Cost: two to three days: job table, async execute, poll/cancel tools; interacts with approval tickets in gate.rs.

## andreban/webmcp-appfunctions

https://github.com/andreban/webmcp-appfunctions · Apache-2.0 · 3 stars · last push 2026-10-26. Similar because: The only other project found that turns Android 16 AppFunctions into agent tools (WebMCP in the browser) by calling `cmd app_function` over adb.

- **Per-function enable/disable at OS level** (useful). Uses `cmd app_function set-enabled --package P --function ID --state enable|disable|default` to switch a function on or off in the system itself.  
  Source: https://raw.githubusercontent.com/andreban/webmcp-appfunctions/main/README.md  
  Absent here, checked: grep -rniE 'set-enabled|set_enabled' core/src backend: no hits. backend.rs only runs list-app-functions and execute-app-function.  
  Worth doing? Useful: Disabling money functions in the OS is a second lock under policy.toml; unverified on Android 17. Cost: about a day: a small `tin disable <pkg> <fn>` CLI command and a backend method; policy deny stays the main control.

- **Browser-side client over WebUSB and WebMCP** (not useful). A client-side web app talks to the phone over WebUSB (in-browser ADB) and registers each function as a WebMCP tool, with an interactive tester that builds argument forms from the schema.  
  Source: https://github.com/andreban/webmcp-appfunctions#-features  
  Absent here, checked: ls of the tin repo: no web client; the only client is scripts/accept.py and README tells users to run adb forward and point an MCP client at 127.0.0.1:7381.  
  Worth doing? Not useful: Weeks for a browser client nobody asked for. Cost: weeks: a web app plus a WebUSB ADB stack, and tin would need an ADB-reachable stdio or socket entry the browser can open.

## minhalvp/android-mcp-server

https://github.com/minhalvp/android-mcp-server · Apache-2.0 · 810 stars · last push 2025-05-28. Similar because: Popular, simple adb-based MCP server giving an agent control of a real Android phone: packages, UI layout, screenshots, commands.

- **Per-package intent action discovery** (unknown). get_package_action_intents returns a package's non-data actions from the Activity Resolver Table, so the agent can find what an app can be asked to do even without AppFunctions.  
  Source: https://raw.githubusercontent.com/minhalvp/android-mcp-server/master/README.md  
  Absent here, checked: grep -rniE 'resolver|resolve-activity|query-intent|intent' core/src in .claude/worktrees/speech: no hits. tin sees apps only through AppFunctions metadata and `launch` via monkey.  
  Worth doing? Unknown: Only 11 apps publish AppFunctions, but whether intents are worth exposing is undecided. Cost: a day: read verb parsing `dumpsys package <pkg>` (Activity Resolver Table), plus a write verb to fire an intent behind approval.
