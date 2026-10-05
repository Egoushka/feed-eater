# feed-eater preflight answers (2026-10-05)

Run from the Mac over `ssh hedzer`. Nothing on the box changed.

| Question | Command | Answer |
|---|---|---|
| Q2: does Reddit serve `top` feeds to the box's IP? | `curl -A "feed-eater-preflight/0.1" https://www.reddit.com/r/selfhosted/top/.rss?t=day` | `200 application/atom+xml; charset=UTF-8`. The Task 17 cleanup can switch to `top/.rss?t=day`. |
| Q3: does LiteLLM return a cost header? | `POST /v1/embeddings` (`text-embedding-3-small`, input `ping`) | Yes: `x-litellm-response-cost: 2e-08`. LiteLLM listens on `100.64.0.2:4000` only, not `127.0.0.1`. |
| Q4: Plane Intake payload | none; `host/opt-homelab/plane-sync.py:129` | `{"issue": {"name", "description_html", "priority"}}` to `intake-issues/`. |
| Step 4: real Miniflux sample | skipped | Optional; the repo is public and Task 5 tests use the synthetic fixture. |
