---
name: homebrew-env-tooling
description: "Environment facts for /data/homebrew work — artifact publishing unavailable (ANTHROPIC_AUTH_TOKEN), headless Chrome + node available for verification"
metadata:
  node_type: memory
  type: project
  originSessionId: 68b6dad0-b4b5-42db-b00b-1c8a9bb0641b
  modified: 2026-11-04T22:40:31.680Z
---

Environment facts for the /data/homebrew (WinBrew) workspace, learned 2026-11-04:

- **Artifact publishing is refused** in this session setup: it authenticates with `ANTHROPIC_AUTH_TOKEN`, not a claude.ai login, and every Artifact tool call errors until the user unsets that var and runs `/login`. Don't retry; the fallback is pointing the user at the self-contained file (e.g. `mindmap.html` opens directly in a browser).
- **Verification tooling that works well here**: `google-chrome --headless=new --disable-gpu --no-sandbox --screenshot=… --virtual-time-budget=6000 file://…` for visual checks (dark mode via a temp copy setting `data-theme="dark"`, mobile via `--window-size=390,844`, interactions via appended `<script>` injections in /tmp copies). `node` (at /root/.local/bin/node) for JS syntax checks via `new Function(scriptBody)`.
- Headless Chrome launches take 10–30s each; four in one foreground command can exceed the 120s default Bash timeout and get backgrounded. Run one per call or in background.
- The Bash safety classifier is intermittently unavailable ("GLM-5.3-Flash temporarily unavailable") — read-only tools (Read) still work when it fails; retry Bash rather than switching approach.
- `.NET SDK at /root/.dotnet` (NOT on PATH — use `/root/.dotnet/dotnet` or export DOTNET_ROOT for apphosts). `dotnet-install.sh` copies in /tmp can lose their exec bit — run via `bash /tmp/dotnet-install.sh`, not directly, or the install silently no-ops (exit 0, "Permission denied" in output). Apphost-built probe binaries need `DOTNET_ROOT=/root/.dotnet` to find the runtime.

**Why:** these cost real time to discover and one (artifact auth) produces a hard refusal.
**How to apply:** verify HTML work with the headless-Chrome recipe; don't attempt Artifact publishes or suggest them without the login caveat.
