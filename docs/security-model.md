# Security model

## What WinBrew protects

- **Authenticity of metadata** — D6 signatures (ECDSA P-256) over root,
  timestamp and index; manifests are bound to the index by SHA-256.
- **Integrity of artifacts** — SHA-256 and size from the verified manifest,
  checked while streaming; the cache is content-addressed and re-verified on
  use.
- **Freshness** — version high-water marks reject rollback and replay;
  expiry (with `trust.maxClockSkewSeconds`) rejects frozen metadata online.
- **The file system** — archive defenses (A13) before and after extraction;
  shim containment (D4); no ACL is ever changed on anything WinBrew did not
  create.
- **The machine** — WinBrew runs unelevated. Only the v1.1 elevated helper
  runs elevated, once per per-machine installer operation, with a typed
  request (ADR-0012).

## Trust decisions the user makes

| Decision | How it is expressed |
|---|---|
| Trust a repository's root keys | `repo add` shows the fingerprints; approve interactively or with `--trust-root` (never implied by `--yes`) |
| Run an MSI/EXE installer (executable code chosen by the repository) | `--allow-installer` |
| Run it per-machine | `--scope machine` + the UAC prompt |
| Change the user PATH | `--modify-path` or an interactive yes |

## Network

HTTPS only for repositories and downloads. The localhost exception exists
only in test-hooks builds AND with `testing.allowInsecureLocalhost=true` AND
`WINBREW_TEST_MODE=1`; it is compiled out of release builds. Redirects are
followed manually (≤ 5, never to non-HTTPS); credentials (Windows Credential
Manager, `WinBrew:<origin>`) are sent only to their exact origin and dropped
on an origin change. Logs redact `Authorization`, cookies, URL userinfo and
every query string.

**WinBrew contacts only:** configured repository URLs, artifact URLs named
by verified manifests, and the self-update feed compiled into the build.
There is **no telemetry** (D11).

## Mark-of-the-Web (D7)

WinBrew adds no `Zone.Identifier` to downloaded or extracted files;
authenticity comes from signatures and checksums. Consequence: **SmartScreen
reputation checks do not apply to WinBrew-installed binaries.**

## Self-update

The release feed uses the same envelope, roles and key model; the release
root compiled into the binary is only the initial root and rotates like any
repository root. The running executable is never overwritten.

## Code signing

Release binaries (`wbrew.exe`, `wbrew-shim.exe`, `WinBrew.ElevatedHelper.exe`)
are Authenticode-signed from a protected GitHub environment when a signing
route is available; otherwise they are published labeled **UNSIGNED**
(`release-readiness.md`).

## Limits of the model

- A process running as the same user can modify `packages\`, `bin\`, the
  state database and staging; containment checks are defense in depth only.
- Malware already running as the same user can influence what the user
  approves at a UAC prompt, and can tamper with an elevated request before
  the helper starts (the helper then still runs only the hash-verified
  artifact or a ProductCode uninstall).
- Installer declarations (v1.1) are executable-code trust decisions: trusting
  a repository and passing `--allow-installer` means trusting its installers.

See `threat-model.md` for the STRIDE analysis.
