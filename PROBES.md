# WinBrew — Phase 0 External Probe Pre-Work

**Companion:** owner-only reference, like `CHANGES.md` and `README.md`;
never sent to implementation sessions. `phases/phase-0.md` still
requires Phase 0 to record each default decision as adopted with
evidence — this file only front-runs the three network-dependent
probes so the planning session starts from facts. Answers gathered
**2026-11-04**; re-verify anything time-sensitive at Phase 0 time.

## 1. System.CommandLine 2.0 stable version (default decision 9)

A stable 2.0 line **exists**: nuget.org lists `2.0.0` through
`2.0.11` — latest stable patch **2.0.11** — plus 3.0.0 previews
(off-limits: A4 anchors to the 2.0 stable surface).
Source: `https://api.nuget.org/v3-flatcontainer/system.commandline/index.json`.
The "no stable release exists" branch of the probe is moot; Phase 0
records **2.0.11** with this evidence.

## 2. Windows ARM64 hosted runners (default decision 3)

A GitHub-hosted Windows ARM64 runner **exists**: label
`windows-11-arm` (4 vCPU / 16 GB RAM / 14 GB SSD), **public
preview**, listed for **public repositories only** — the
private-repository standard table has no Windows ARM64 entry.
Source: GitHub Docs, "About GitHub-hosted runners" (fetched
2026-11-04). This planning repository (`aaronjost1994-lang/homebrew`) is
**private** (checked via `gh` on 2026-11-04), so today the answer
is: **no hosted ARM64 runner** → any ARM64 E2E job reports **BLOCKED
per A16** (in the readiness report — never skipped silently, never
counted as passing): a legitimate, spec-sanctioned outcome. Two
forward-looking notes:

- A1 positions WinBrew as a published, installable product; once the
  implementation repo exists as **public**, `windows-11-arm` becomes
  available for its CI (still public preview — re-verify when Phase
  F runs, and treat losing the runner as an A16 BLOCKED case, not a
  spec violation).
- Alternative for a private phase: a **self-hosted** ARM64 Windows
  runner. `release-checklist.md` already lists runner ownership as
  an external prerequisite the owner supplies.

## 3. Signing route: name and eligibility (default decision 4)

The official Microsoft Learn overview (fetched 2026-11-04) still
titles the service **"Trusted Signing"**
(`learn.microsoft.com/azure/trusted-signing/overview`) and nowhere
uses the name "Artifact Signing". CHANGES.md (v2.3, fix 4.7) and the
A18 parenthetical claim a late-2026 repackage to "Artifact Signing" —
**that rename could not be confirmed from the public docs today.**
A18 already hedges ("verify its current name"), so the prompt needs
no change; Phase 0 should record: *name per public docs = Trusted
Signing as of 2026-11-04; re-check inside the Azure portal during
sign-up, where a marketing rename would certainly appear.*

Identity validation remains a real, sometimes slow eligibility gate:
whether the project owner qualifies (individual vs organization
identity, regional availability) can only be confirmed by starting
that process — not probeable from here.

---

Not probeable from here, still on the owner: the A1 trademark/name
check for "WinBrew" before publishing.
