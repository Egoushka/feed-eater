# Reason line on every ranked item (feed-eater/P01)

Each scored item keeps the signals behind its score, and the digest shows them as one line.

## Acceptance (machine-run, `ScorerTests`, `DigestFormatterTests`)
1. `Scorer.Score` returns `Signals` whose parts (fit, taste, feed prior, learned) sum to `Score`.
2. `RankSignals.Line()` names the profile and every non-zero part with its sign, e.g. `fits chargehand 0.62 · liked-like +0.10 · feed 👍 −0.03`; zero parts are left out; no profile and no parts gives an empty line.
3. `DigestFormatter.Item` prints the line in italics under the meta line when `DigestItem.Ranked` is set, escaped; nothing when empty.
4. Migration `0036` adds nullable `items.rank_signals jsonb`; `SetScoresAsync` writes it and `DigestItemsAsync` reads it back (DB test, needs Postgres).

Not in this PR: showing the line in the swipe UI or the web reader.
