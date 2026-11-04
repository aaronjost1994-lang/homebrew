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

## 4. Local build environment probe (Linux, 2026-11-04)

Validates PLAN.md §4's TFM table empirically with a scratch
TFM-matrix solution **outside this repository** (SDK **10.0.301**,
linux-x64, at `/root/.dotnet`, not on PATH):

- `net10.0` and `net10.0-windows` classlibs **build clean** on Linux;
  `Microsoft.Win32.Registry` compiles from a `net10.0-windows` project
  **with no extra package** (the TFM targeting pack provides it).
- `net10.0` xunit tests pass (NuGetVersion `ToNormalizedString()`
  per D3); a `net10.0-windows` **test** project builds and runs on
  Linux: SQLite in-memory + **WAL file db** work with
  `Microsoft.Data.Sqlite`'s bundled e_sqlite3 — the A6 storage stack
  is viable cross-plat.
- Off-Windows runtime: `Registry.GetValue` throws
  **PlatformNotSupportedException** ("Registry is not supported on
  this platform.") — observed, and the CA1416 platform analyzer
  flags such call sites correctly.
- **Native AOT (the D4 shim):** publishing `win-x64` from Linux is
  refused with `error : Cross-OS native compilation is not supported`
  — **shim binaries require Windows** (or Windows CI runners, A16).
  The control (`linux-x64` AOT) publishes fine, so the toolchain
  itself works; only cross-OS is blocked.

Security findings from restore (NU1903, high severity):
- `SQLitePCLRaw.lib.e_sqlite3` **2.1.10** — GHSA-2m69-gcr7-jv3q.
  Phase A must pin a patched bundle; note the **FTS5 probe (§2 item
  2 / default decision 2) runs against whatever bundle is pinned**,
  so the probe must be re-run on the pinned version, not assumed
  from any other build.
- `Microsoft.DotNet.ILCompiler` **10.0.9** — GHSA-p3jp-9v83-76w9.
  Pin a patched ILCompiler before the shim's release build (Phase F).
  **Follow-up (2026-11-05, during Phase A build):** the advisory is
  CVE-2026-111497 (.NET Brotli decoder OOB write, CWE-787, HIGH);
  patched in **10.0.14** for the 10.0.x line — same patch level the
  runtime packs need (`Microsoft.NETCore.App.Runtime.*` 10.0.9 is in
  the same vulnerable range, and the per-RID locked restores would
  trip the identical NU1903). Central package management **cannot**
  pin SDK-injected packages (NU1009: `Microsoft.DotNet.ILCompiler` is
  implicitly referenced by the PublishAot targets from
  `MicrosoftNETCoreSdk.BundledVersions.props`, `ILCompilerPackVersion`
  = the SDK's bundled version). The only clean fix is a newer SDK:
  Phase A bumps the build from SDK **10.0.301** to the latest 10.0.x
  (bundling ILCompiler/runtime ≥ 10.0.14) and updates global.json
  accordingly. Windows runners must use an SDK ≥ that patch level too.

Consequence for PLAN.md: everything in §4 is verifiable on this
Linux box except **shim AOT binaries and Windows-runtime suites**
(registry, ACL, Restart Manager, E2E) — those need Windows 11 or
Windows runners, exactly as the plan's disclosure already states.

---

Not probeable from here, still on the owner: the A1 trademark/name
check for "WinBrew" before publishing.
