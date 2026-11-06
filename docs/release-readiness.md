# Release Readiness — WinBrew 1.0.0 (+ v1.1 installer additions)

**Date:** 2026-11-06 · **Commit basis:** Phase F (after `a20f384`) ·
**Verdict: NOT READY TO RELEASE.** Everything that runs on the Linux dev host
passes; the Windows CI matrix has not run yet (the workflows are committed and
run on the first push), signing and release infrastructure are missing, and
the HTTPS end-to-end suite is not built. Each blocker is listed below.

Evidence key — **dev host:** Linux x64, .NET SDK 10.0.403, `dotnet test`
(release + test-hooks builds) and `docker build --target test`, all green:
unit 597/597; integration 220 passed / 22 skipped (test-hooks), 74 passed /
93 skipped (release); E2E 1 passed / 5 skipped. **CI:** `.github/workflows/ci.yml`
(not yet run).

| # | Criterion (A19) | Implementation | Tests | Evidence | Status | Blocker |
|---|---|---|---|---|---|---|
| 1 | Clean machine runs the CLI from the zip, nothing extracted to %TEMP% | Single-file bundle without native self-extraction; `e_sqlite3.dll` beside `wbrew.exe` (ADR-0001); Linux-hosted publish of both RIDs produced exactly the manifest set | `ReleaseBinaryTests.FromTheExtractedZip_…` (asserts an empty fresh `%TEMP%`) | dev host: publish output checked; Windows: NOT RUN | BLOCKED | Windows CI run (x64 + ARM64) |
| 2 | Standard user self-installs, installs/removes portable packages without elevation | `self install`, portable lifecycle never elevates; only the v1.1 helper elevates | `SelfInstallTests`, `LifecycleCliTests`, `ReleaseBinaryTests.SelfInstall_…` | dev host PASS (test host); Windows NOT RUN | BLOCKED | Windows CI run |
| 3 | Commands run correctly through shims (args, cwd, exit codes, Ctrl+C), incl. x86/AnyCPU | Native AOT shim, MSVC `argv[0]` rules, job object, Ctrl+C forwarding (ADR-0007); x86/`0x014C` accepted (ADR-0019) | `ShimRulesTests` (≥ 35-case quoting corpus), `WindowsOnlyTests` (real shim process) | dev host: rules PASS; process tests NOT RUN | BLOCKED | Windows CI run (AOT shim builds only on Windows) |
| 4 | Dependency conflicts fail with explanations before any change | PubGrub with derivation-tree explanations (ADR-0010) | `ResolverTests`, `DependencyCliTests.Conflict_…` (whole-root snapshot, no HTTP request) | dev host PASS | PASS | |
| 5 | Corrupt downloads, malicious archives, tampered/expired/replayed metadata rejected | A13 HTTP + archive defenses; D6 trust chain (ADR-0009, ADR-0011) | `LifecycleCliTests` (corrupt artifact), `MaliciousArchiveTests`, `TrustChainTests`, `TrustCliTests` (11-attack matrix) | dev host PASS | PASS | Windows-only extraction re-check (reparse walk) runs in CI |
| 6 | A crash at any registered checkpoint recovers or reports RecoveryFailed | Journal, compensation, roll-forward; 24 registered checkpoints (ADR-0008) | `CrashRecoveryTests` (coverage table = registry; every scenario) | dev host PASS except `path.updated` (needs HKCU) | PASS (dev) / `path.updated` BLOCKED | Windows CI run |
| 7 | Upgrade (incl. while a shim runs), failed-upgrade recovery, uninstall, autoremove tested and documented | `.old.<guid>` protocol for running shims (D4), compensation, D8 | `LifecycleCliTests`, `CrashRecoveryTests` (Upgrade, MultiPackageUpgrade), `DependencyCliTests` (autoremove), `WindowsOnlyTests` (running shim); `recovery-troubleshooting.md` | dev host PASS; upgrade-while-running NOT RUN | BLOCKED | Windows CI run |
| 8 | Locked bundles reproduce exact selections or fail explicitly | `bundle install --locked` never resolves (ADR-0011) | `BundleCliTests` (exact versions/digests, 5 defect cases, offline restore) | dev host PASS | PASS | |
| 9 | JSON output and exit codes match their specifications | One mapping (`ExitCodes.cs`), versioned envelope schema | `EnvelopeSchemaTests`, `ExitCodesTests`; `cli-reference.md` (full table incl. 13 and the 130/`0xC000013A` note) | dev host PASS | PASS | |
| 10 | CI passes on the declared support matrix, or blockers are listed | `ci.yml` (windows-latest x64, windows-11-arm ARM64, Linux Docker), `release.yml` | — | workflows committed, **never run** | BLOCKED | First CI run after push; ARM64 runner availability |
| 11 | No critical path contains placeholders or fake success | Every stub of earlier phases replaced; no `NotImplementedException`/TODO in `src/`; tests that cannot run skip with a reason | source scan (this report), `NoShellCommandStringTests` | dev host PASS | PASS | |
| 12 | Known limitations and external prerequisites disclosed | `known-limitations.md`, `release-checklist.md`, this report | — | documents present | PASS | |

### v1.1 additions

| Criterion | Implementation | Tests | Status | Blocker |
|---|---|---|---|---|
| MSI and EXE lifecycles tested on disposable machines | ADR-0012; WiX fixture MSI + fixture EXE (`tests/fixtures/installers`, `scripts/build-installer-fixtures.ps1`) | `InstallerWindowsTests.Fixture*` (per-user MSI, per-machine MSI, EXE); `InstallerServiceTests` (fakes) PASS on dev host | BLOCKED | Windows disposable runner + fixtures built in CI |
| Elevated-helper refusal (pre-created plain dir, junction, hard link) | `ProtectedDirectories`, `DirectoryTrustRules` | `DirectoryTrustRulesTests` PASS; `InstallerWindowsTests.PreCreated*` + ACL snapshots NOT RUN | BLOCKED | Windows disposable runner |
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
| Windows ARM64 runners | ARM64 E2E | Unverified — `ci.yml` uses `windows-11-arm` |
| Production repository hosting | Public use | Missing |
| Production root keys (offline) | Trust bootstrap | Missing |
| Self-update feed URL + release root chain | self update | Missing — builds exit 3 "self-update not configured for this build" |

## Measured non-functional results (A17)

| Metric | x64 | ARM64 | Target |
|---|---|---|---|
| `--version` warm start (post-self-install, through the shim) | NOT RUN (`ReleaseBinaryTests`) | NOT RUN | < 300 ms |
| `--version` first run (fresh profile) | NOT RUN | NOT RUN | report only |
| `list` (200 packages) | dev host only: median **557 ms** (test-host process, framework-dependent, Linux; `MeasurementTests`) — not the target measurement | NOT RUN | < 500 ms |
| `search` (10k generated catalog, median of 10) | dev host only: median **463 ms** (min 398, max 549; `TrustCliTests`) — the target is defined on the CI runner | NOT RUN | < 1 s |
| Shim overhead / size | NOT RUN (AOT shim builds only on Windows) | NOT RUN | < 30 ms / < 3 MB |
| Extraction assertion (no %TEMP%\.net\) | NOT RUN (`ReleaseBinaryTests`) | NOT RUN | must pass |
| `wbrew.exe` bundle size | 105,570,571 bytes (Linux-hosted publish) | 114,569,497 bytes | report only |
| `WinBrew.ElevatedHelper.exe` size (self-contained single file) | 100,925,244 bytes | 109,924,168 bytes | report only |
| Shipped zip size | NOT RUN (needs the AOT shim) | NOT RUN | report only |

## Known limitations

See `known-limitations.md`. Release-relevant: unsigned artifacts until a
signing route exists; no public repository or self-update feed; Windows-only
behavior unverified until CI runs; the helper roughly doubles the zip size
(trimming or Native AOT for the helper should be evaluated on Windows).
