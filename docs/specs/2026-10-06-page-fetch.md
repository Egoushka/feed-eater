# Page fetch: safety spec

feed-eater fetches web pages that feeds link to (Reddit and Hacker News link posts, feed discovery). The URL comes from an
untrusted feed, so the fetcher is the one place that talks to arbitrary hosts. Everything below is enforced in code and has a
test; `Fetch/SafeFetcher.cs` is the only code that opens such a connection.

## Network

1. Only `http` and `https`, only ports 80 and 443. Anything else is refused before any DNS lookup.
2. We resolve the host ourselves, in the connect callback of the `SocketsHttpHandler`, and connect to the address we validated.
   The handler never resolves again, so DNS rebinding between check and connect is not possible.
3. Refused address ranges (IPv4 and IPv6, and IPv4 embedded in IPv6: mapped, NAT64, 6to4): unspecified, loopback, private
   (10/8, 172.16/12, 192.168/16), link-local (169.254/16 including the cloud metadata address, fe80::/10), carrier-grade NAT
   (100.64/10, the tailnet), unique-local (fc00::/7), multicast, reserved, benchmarking and documentation ranges, broadcast.
   If a name resolves to any refused address, the whole fetch is refused (no picking the public one).
4. IP-literal hosts go through the same check. A proxy is never used.
5. Redirects are followed by us, at most 3. Each hop is re-validated (scheme, port, blocked hosts) and gets a fresh connection
   check, so a redirect to an internal address is refused. A redirect from https to http is refused (no downgrade). A malformed
   Location header ends the fetch as a failure, never an exception.

## Request and response

6. Timeout 10 s per request (each redirect hop gets its own), started after the politeness wait so queueing never counts against a host.
7. The body is read as a stream and cut at 1 MB (after decompression). Only `text/html`, `application/xhtml+xml` and
   `text/plain` are read; any other type is dropped without reading the body.
8. No cookies, no credentials, no `Authorization` header, a fixed User-Agent naming feed-eater and its repository.

## Politeness and cost

9. At most one request per second per host (a redirect to the same host counts).
10. A global cap of fetches per UTC day (`FeedEater:Fetch:MaxPerDay`, default 400), counted in the `cursors` table so a restart
    does not reset it.
11. A failure (refused, timeout, error status, wrong type) is remembered per host for 24 hours in memory and per item in the
    database, so nothing is retried in a loop and a restart does not retry an item.
12. `FeedEater:Fetch:BlockedHosts` lists hosts never fetched (login-walled or hostile: social networks, video sites); subdomains match.
13. At most `FeedEater:Fetch:MaxPerPoll` pages per ingest poll (default 40), and only for items published in the last 3 days.

## Parsing

16. Every regular expression applied to fetched or feed text has a 250 ms match timeout, and extraction works on at most the first
    256 KB. A timeout counts as "no text" or "no feed", never as an error, so hostile markup cannot stall the loop.
17. A discovered feed URL longer than 2,000 characters is ignored. Housekeeping tables (host failures, politeness) are pruned.

## Using the text

14. Fetched text is untrusted data. It is cut to a character budget, put between `<untrusted_page>` tags in the prompt (a closing
    tag inside the text is defused), and both prompts tell the model to treat it as information only and to ignore any
    instruction inside it.
15. Fetched text is never executed, rendered as HTML, or written anywhere but the item's `extra_text`.
