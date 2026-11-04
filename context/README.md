# context/ — Claude Code agent state, mirrored for other machines

This directory carries the Claude Code configuration and memory that
drive work in this workspace, checked in so a new server can pick up
exactly where this one left off. Copy the files to the same paths on
the next machine before continuing the project there.

| File | Destination on the new server |
| --- | --- |
| `CLAUDE.md` | `~/.claude/CLAUDE.md` (global agent instructions; references `RTK.md` via `@RTK.md`) |
| `RTK.md` | `~/.claude/RTK.md` (rtk CLI proxy usage — token-optimization hook) |
| `memory/MEMORY.md` | `~/.claude/projects/-data-homebrew/memory/MEMORY.md` (memory index — keep this filename; it is loaded each session) |
| `memory/homebrew-env-tooling.md` | same `memory/` directory (environment facts and working recipes) |

The memory directory path encodes the workspace
(`/data/homebrew` → `-data-homebrew`); on a server where the checkout
lives elsewhere, use the corresponding path-derived directory, or keep
`/data/homebrew` as the checkout location to reuse these files as-is.

`../probes/` holds the scratch probe projects referenced as evidence by
PROBES.md §4 and PLAN.md (TFM matrix, registry probe). They are data —
re-run them, don't trust the copies blindly.
