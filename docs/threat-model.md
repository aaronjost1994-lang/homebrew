# Threat model (STRIDE)

Assets: the user's account and files, the machine (v1.1 per-machine), the
integrity of installed software, the trust roots. Adversaries: a network
attacker (MITM, malicious mirror), a compromised or malicious repository, a
malicious package author, another local user (multi-user machine), malware
running as the same user.

| # | Category | Threat | Mitigation | Evidence |
|---|---|---|---|---|
| S1 | Spoofing | MITM serves fake metadata or artifacts | HTTPS only; D6 signatures; SHA-256 per manifest and artifact | `TrustCliTests`, `HttpFetcherTests` |
| S2 | Spoofing | Attacker key presented as a repository root | Fingerprint approval on `repo add`; no implicit trust; `--yes` never implies it | `RepoAdd_*` tests |
| S3 | Spoofing | Wrong-curve / explicit-parameter / compressed keys accepted by lax parsing | SPKI parsed with ASN.1 before import, `KeySize == 256` | `TrustChainTests.Spki_*` |
| S4 | Spoofing | Credentials sent to another origin | Exact-origin Credential Manager lookup; stripped on origin change | `HttpFetcherTests` |
| T1 | Tampering | Modified index/manifest/artifact | Signatures, digests, sizes; index length enforced while streaming | `Update_RejectsAttack_*`, `ManifestDigestMismatch_*` |
| T2 | Tampering | Malicious ZIP (traversal, device names, ADS, symlinks, bombs) | A13 archive defenses on actual bytes; post-extraction containment + reparse walk | `MaliciousArchiveTests` |
| T3 | Tampering | Installer swapped after verification (TOCTOU) | Copy into a protected `<op>` dir, open with `FileShare.Read`, hash from that handle, keep it open while running | `InstallerServiceTests`, helper code (Windows runner tests) |
| T4 | Tampering | Pre-created `%ProgramData%\WinBrew` (plain dir, junction, hard link) by a standard user | Owner + DACL + reparse checks on handles; never adopt/repair; create with final descriptor; re-check on `ERROR_ALREADY_EXISTS` | `DirectoryTrustRulesTests`, `InstallerWindowsTests` (Windows runner) |
| T5 | Tampering | Sidecar redirected outside the root | Containment to `bin\..\packages\` (self-shim: `app\`), reparse refusal, exit 9009 | `ShimRulesTests` |
| R1 | Repudiation | No record of what changed | Operation journal with intent/outcome per step; logs; installer logs kept | `CrashRecoveryTests` |
| I1 | Information disclosure | Secrets in logs (signed URLs, tokens) | Redaction of `Authorization`, cookies, userinfo, query strings; no HTTP logging handlers | `HttpFetcherTests` |
| I2 | Information disclosure | Telemetry | None (D11) | ADR-0018 |
| D1 | Denial of service | Freeze attack (stale but valid metadata) | Expiry; online commands refresh, offline use warns with age | `FreezeAttack`, `ExpiredMetadata_*` |
| D2 | Denial of service | Rollback to vulnerable versions | High-water marks for root/timestamp/index | `RolledBackIndex`, `ReplayedOldTimestamp` |
| D3 | Denial of service | Huge metadata / artifacts / archives | `trust.maxMetadataBytes`, `download.maxBytes`, archive limits | `OversizedMetadata_*`, `MaliciousArchiveTests` |
| D4 | Denial of service | Crash mid-operation leaves a broken install | Journal + compensation/roll-forward at every checkpoint; `RecoveryFailed` reported honestly | `CrashRecoveryTests` (every checkpoint) |
| E1 | Elevation of privilege | Elevated code runs attacker-chosen commands | The helper accepts four typed operations only; no command lines; no shell anywhere (enforced by a source scan) | `ElevatedRequest` tests, `NoShellCommandStringTests` |
| E2 | Elevation of privilege | Elevated install runs a user-writable file | The helper runs only its own protected copy | helper code, Windows runner tests |
| E3 | Elevation of privilege | Repository key compromise | Threshold signatures, cross-signed rotation, offline root keys (operator duty) | `Rotation_*`, release checklist |

## Accepted risks

- Malware running as the same user (see `security-model.md`, "Limits").
- A trusted repository that publishes a malicious package or installer: trust
  in a repository is trust in its content.
- SmartScreen does not apply (no Mark-of-the-Web, D7).
