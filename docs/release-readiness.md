# Release Readiness — WinBrew 1.0.0 (+ v1.1 installer additions)

**Date:** 2026-11-07 · **Commit basis:** `4ba60e7` ([ci run 42351461373](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42351461373), all green) ·
**Verdict: NOT READY TO RELEASE.** The Windows CI matrix (x64 and ARM64) is
green, including the real Native AOT shim; signing and release infrastructure
are missing; the HTTPS end-to-end suite is not built; `release.yml` has not
had its dry run. Each blocker is listed below.

Evidence key — **dev host:** Linux x64, .NET SDK 10.0.403, `dotnet test`
(release + test-hooks builds) and `docker build --target test`, all green:
unit 600/600; integration 220 passed / 22 skipped (test-hooks), 74 passed /
93 skipped (release); E2E 1 passed / 5 skipped. **CI:** [ci run 42247232609](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42247232609) on `59fe30c`,
all three jobs green:

| Job | Unit | Integration (release) | Integration (test-hooks) | E2E (published zip) |
|---|---|---|---|---|
| windows (win-x64), `windows-latest` | 600/600 | 78 passed / 89 skipped | 234 passed / 8 skipped | 6/6 |
| windows (win-arm64), `windows-11-arm` | 600/600 | 78 passed / 89 skipped | 226 passed / 16 skipped | 6/6 |
| linux (Docker) | 600/600 | 74 / 93 | 220 / 22 | — |

The 8 x64 test-hooks skips are the 7 real-shim `ShimProcessTests` and
`ExtractionVerificationTests`, which need the published binaries. Since
`b1074f0` a CI step runs them after packaging against `out/<rid>/` and fails
if any is skipped: [ci run 42351461373](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42351461373) — **8/8 passed on x64 and on ARM64**. ARM64
additionally skips the 8 `InstallerWindowsTests` (fixtures are built on the
x64 job only).

Windows-only defects found and fixed by the first CI runs: a ZIP file handle
leaked on corrupt archives (`eda5d9c`); redirected stdout/stderr used the OEM
code page instead of UTF-8 (`ff0bb5c`); every elevated-helper result with a
null member was rejected, so per-machine installs could never succeed
(`59fe30c`).

| # | Criterion (A19) | Implementation | Tests | Evidence | Status | Blocker |
|---|---|---|---|---|---|---|
| 1 | Clean machine runs the CLI from the zip, nothing extracted to %TEMP% | Single-file bundle without native self-extraction; `e_sqlite3.dll` beside `wbrew.exe` (ADR-0001); Linux-hosted publish of both RIDs produced exactly the manifest set | `ReleaseBinaryTests.FromTheExtractedZip_…` (asserts an empty fresh `%TEMP%`) | CI x64 + ARM64: `FromTheExtractedZip_…` PASS ([ci run 42247232609](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42247232609)) | PASS | |
| 2 | Standard user self-installs, installs/removes portable packages without elevation | `self install`, portable lifecycle never elevates; only the v1.1 helper elevates | `SelfInstallTests`, `LifecycleCliTests`, `ReleaseBinaryTests.SelfInstall_…` | CI x64 + ARM64: `SelfInstallTests`, `ReleaseBinaryTests.SelfInstall_…` PASS | PASS | |
| 3 | Commands run correctly through shims (args, cwd, exit codes, Ctrl+C), incl. x86/AnyCPU | Native AOT shim, MSVC `argv[0]` rules, job object, Ctrl+C forwarding (ADR-0007); x86/`0x014C` accepted (ADR-0019) | `ShimRulesTests` (≥ 35-case quoting corpus), `WindowsOnlyTests` (real shim process) | rules PASS everywhere; real AOT shim process tests 8/8 on x64 + ARM64 ([ci run 42351461373](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42351461373)): exit-code forwarding, 9009 containment, 206 overflow, self-shim rule | PASS | Ctrl+C delivery and stdin/stdout piping belong to the unbuilt HTTPS E2E suite |
| 4 | Dependency conflicts fail with explanations before any change | PubGrub with derivation-tree explanations (ADR-0010) | `ResolverTests`, `DependencyCliTests.Conflict_…` (whole-root snapshot, no HTTP request) | dev host PASS | PASS | |
| 5 | Corrupt downloads, malicious archives, tampered/expired/replayed metadata rejected | A13 HTTP + archive defenses; D6 trust chain (ADR-0009, ADR-0011) | `LifecycleCliTests` (corrupt artifact), `MaliciousArchiveTests`, `TrustChainTests`, `TrustCliTests` (11-attack matrix) | dev host PASS | PASS | Windows-only extraction re-check (reparse walk) runs in CI |
| 6 | A crash at any registered checkpoint recovers or reports RecoveryFailed | Journal, compensation, roll-forward; 24 registered checkpoints (ADR-0008) | `CrashRecoveryTests` (coverage table = registry; every scenario) | CI x64 + ARM64 PASS, including `path.updated` (HKCU test key) | PASS | |
| 7 | Upgrade (incl. while a shim runs), failed-upgrade recovery, uninstall, autoremove tested and documented | `.old.<guid>` protocol for running shims (D4), compensation, D8 | `LifecycleCliTests`, `CrashRecoveryTests` (Upgrade, MultiPackageUpgrade), `DependencyCliTests` (autoremove), `WindowsOnlyTests` (running shim); `recovery-troubleshooting.md` | CI PASS, including upgrade-while-running (`ShimProcessTests.ReplacingARunningShim_…`, x64 + ARM64, [ci run 42351461373](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42351461373)) | PASS | |
| 8 | Locked bundles reproduce exact selections or fail explicitly | `bundle install --locked` never resolves (ADR-0011) | `BundleCliTests` (exact versions/digests, 5 defect cases, offline restore) | dev host PASS | PASS | |
| 9 | JSON output and exit codes match their specifications | One mapping (`ExitCodes.cs`), versioned envelope schema | `EnvelopeSchemaTests`, `ExitCodesTests`; `cli-reference.md` (full table incl. 13 and the 130/`0xC000013A` note) | dev host PASS | PASS | |
| 10 | CI passes on the declared support matrix, or blockers are listed | `ci.yml` (windows-latest x64, windows-11-arm ARM64, Linux Docker), `release.yml` | — | [ci run 42247232609](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42247232609): all jobs green; `windows-11-arm` available | PASS | `release.yml` dry run not yet run |
| 11 | No critical path contains placeholders or fake success | Every stub of earlier phases replaced; no `NotImplementedException`/TODO in `src/`; tests that cannot run skip with a reason | source scan (this report), `NoShellCommandStringTests` | dev host PASS | PASS | |
| 12 | Known limitations and external prerequisites disclosed | `known-limitations.md`, `release-checklist.md`, this report | — | documents present | PASS | |

### v1.1 additions

| Criterion | Implementation | Tests | Status | Blocker |
|---|---|---|---|---|
| MSI and EXE lifecycles tested on disposable machines | ADR-0012; WiX fixture MSI + fixture EXE (`tests/fixtures/installers`, `scripts/build-installer-fixtures.ps1`) | `InstallerWindowsTests.Fixture*` (per-user MSI, per-machine MSI, EXE); `InstallerServiceTests` (fakes) PASS on dev host; CI x64: all three fixture lifecycles PASS (per-machine through the helper and UAC) | PASS (x64) | ARM64 fixture run not wired (fixtures built on x64 only) |
| Elevated-helper refusal (pre-created plain dir, junction, hard link) | `ProtectedDirectories`, `DirectoryTrustRules` | `DirectoryTrustRulesTests` PASS; CI x64: `InstallerWindowsTests.PreCreated*` + ACL snapshots PASS | PASS (x64) | |
| Reconciliation documented | `list`/`doctor` drift, never corrected | `InstallerServiceTests.Reconcile_…`, `InstallerCliTests`; `recovery-troubleshooting.md` | PASS | |

### Not built (explicit gap)

The A16 **HTTPS end-to-end lifecycle suite** (per-job test CA in
`LocalMachine\Root`, Kestrel-served fixture repository, throttled
interruption + recover, shim execution corpus, bundle round trip on the
published binary) is **not implemented**. `tests/WinBrew.EndToEndTests`
contains only the release-artifact checks (zip contents, SHA256SUMS, %TEMP%,
self install, localhost refusal, A17 timings). The same behaviors are covered
by integration tests against the real CLI process over the test-hooks
localhost exception, which is not equivalent. **Release blocker.**

## External prerequisites

| Item | Needed for | Status |
|---|---|---|
| Code-signing route: managed service (Trusted Signing / "Artifact Signing") if the owner qualifies, otherwise OV/EV certificate, + owner | Authenticode | **Missing** — owner aaronjost1994-lang to choose; `release.yml` labels artifacts UNSIGNED until the `release-signing` environment has secrets |
| Windows ARM64 runners | ARM64 E2E | **Available** — `windows-11-arm` ran the full ARM64 job ([ci run 42247232609](https://github.com/aaronjost1994-lang/homebrew/actions/runs/42247232609)) |
| Production repository hosting | Public use | Missing |
| Production root keys (offline) | Trust bootstrap | Missing |
| Self-update feed URL + release root chain | self update | Missing — builds exit 3 "self-update not configured for this build" |

## Measured non-functional results (A17)

| Metric | x64 | ARM64 | Target |
|---|---|---|---|
| `--version` warm start (post-self-install, through the shim) | **88 ms** median of 10 (CI) | **87 ms** median of 10 (CI) | < 300 ms ✔ |
| `--version` first run (fresh profile) | **640–1,484 ms** across two CI runs (cold-disk variance) | **1,372–1,622 ms** across two CI runs | report only |
| `list` (200 packages) | dev host: median **557 ms** (test-host process, Linux). CI: ran, passed the < 2 s regression gate; number not captured (console verbosity) | same as x64 | < 500 ms — **target measurement not yet taken** |
| `search` (10k generated catalog, median of 10) | dev host: median **463 ms** (min 398, max 549). CI: ran and passed the < 1 s assertion | CI: passed the < 1 s assertion | < 1 s ✔ |
| Shim overhead / size | size **1,223,680 bytes** (CI); overhead not measured separately | size **1,234,432 bytes** (CI) | < 30 ms / < 3 MB (size ✔) |
| Extraction assertion (no %TEMP%\.net\) | **PASS** (CI, `ReleaseBinaryTests`) | **PASS** (CI) | must pass ✔ |
| `wbrew.exe` bundle size | 105,580,376 bytes (CI publish) | 114,579,302 bytes (CI publish) | report only |
| `WinBrew.ElevatedHelper.exe` size (self-contained single file) | 100,925,244 bytes | 109,924,168 bytes | report only |
| Shipped zip size | **83,830,326 bytes** (CI) | **80,603,317 bytes** (CI) | report only |

## Known limitations

See `known-limitations.md`. Release-relevant: unsigned artifacts until a
signing route exists; no public repository or self-update feed; Windows-only
Ctrl+C and piping through shims not yet tested end to end; the helper roughly doubles the zip size
(trimming or Native AOT for the helper should be evaluated on Windows).
