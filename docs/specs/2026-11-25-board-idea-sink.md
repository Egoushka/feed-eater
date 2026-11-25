# Ideas go to the wheelhouse board (A8, issue #85)

An idea (a 💡 on an item, a promoted swipe idea) becomes a wheelhouse board item: `wh board add --kind task --work feature --source feed:<itemId>`. The GitHub sink stays, chosen with `FeedEater:Ideas:Sink=github`, for kind fix. `board` is the default (`auto` and unknown values).

## Acceptance checks (machine-run, `dotnet test --filter`)
- `BoardIdeaSinkTests`: the wh call has `--kind task --work feature --source feed:<id>`; research uses `--work research`; no item id means no `--source`; a non-zero exit throws.
- `ConfigTests` (idea sink default is `board`; `github` and `local` still selectable) and `ConfigDocTests` (CONFIG.md current).
- `CompositionTests`: default resolves `BoardIdeaSink`; `github` and `local` resolve their sinks.
- `FeedbackLoopTests`, `SwipeIdeaTests` still pass.
