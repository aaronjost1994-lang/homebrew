# ADR-0009: HTTP client, archive defenses, artifact selection, Phase B ingest (A10, A13, D6)

**Status:** accepted (Phase B src sub-session, 2026-11-06)

## Decisions

1. **HTTP (A13).** One named `HttpClient` from `IHttpClientFactory`
   (`AllowAutoRedirect=false`, no cookies, no automatic decompression,
   infinite client timeout, factory logging handlers removed so no URL is
   ever logged unredacted). `SecureHttpFetcher` follows redirects itself:
   at most 5; any non-HTTPS target is refused (also HTTPS → loopback http);
   credentials are dropped on the first origin change and never re-added.
   The localhost exception (`localhost`, `127.0.0.1`, `[::1]`) needs a
   test-hooks build **and** `testing.allowInsecureLocalhost=true` **and**
   `WINBREW_TEST_MODE=1`; release builds compile it out.
2. **Limits and retries.** `ExpectedSize` (manifest `size`, or the
   index's manifest size) must match exactly; `download.maxBytes` /
   `trust.maxMetadataBytes` apply on top. Both are checked against
   `Content-Length` and, authoritatively, against the bytes actually
   streamed (larger aborts immediately, shorter is "truncated"). Size and
   hash violations are exit 5 and never retried. Transient failures
   (connection errors, interrupted bodies, 408/429/5xx, per-fetch timeout)
   retry `download.retries` times with exponential backoff and full jitter
   (≈0.5 s, 1 s, 2 s … ≤ 8 s); exhaustion is exit 4. Disk full is exit 12.
3. **Credentials.** Windows Credential Manager generic credential
   `WinBrew:<scheme>://<host>:<port>` (lowercase). Empty user name or
   `Bearer` → `Authorization: Bearer <password>`; any other user name →
   HTTP Basic. Sent only while the request is still on its original origin.
4. **Archive defenses (A13).** A full pre-scan rejects, before anything is
   written: more than `archive.maxEntries`; absolute, drive-letter, UNC and
   `.`/`..` paths; empty segments (separator ambiguity, checked after
   `\`→`/` normalization); reserved device names in any segment (with or
   without extensions, trailing spaces stripped); `:`; trailing dots/spaces;
   Windows-invalid characters; Unix symlink mode bits; encrypted entries;
   case-insensitive duplicates including file-vs-directory clashes. The
   extraction then enforces `archive.maxTotalBytes` and the per-entry and
   total `archive.maxCompressionRatio` on **actual** decompressed bytes,
   checks each final path's containment, and walks the tree for reparse
   points. Any violation deletes the partial output; the `defense` detail
   names the rule.
5. **Artifact selection (A10).** Host = `IsWow64Process2` native machine,
   falling back to `RuntimeInformation.OSArchitecture` — never
   `ProcessArchitecture`. Order: x64 → `x64, x86`; arm64 → `arm64, x64,
   x86`. **x86 is selected only when no native (and, on ARM64, no x64)
   artifact exists**; the plan and the JSON warnings say so. PE validation:
   x64 accepts AMD64 + I386; arm64 accepts ARM64 + AMD64 + I386; ARM32 and
   unknown machines are always refused; subsystems other than console/GUI
   are refused.
6. **Phase B metadata ingest.** `IngestIndexUseCase` is the FINAL ingest
   path (strict `repository-index.v1` parse, expiry with
   `trust.maxClockSkewSeconds`, one transaction for catalog rows and the
   index high-water mark, lower versions rejected as rollback). Signature
   verification sits behind `IMetadataVerifier`: test-hooks builds register
   `TestHooksUnsignedVerifier` (also requires `WINBREW_TEST_MODE=1`);
   release builds register `UnavailableMetadataVerifier`, which refuses all
   metadata with `NotAvailableInThisBuild` until Phase D. The hidden
   `wbrew test-ingest <repo> <url>` command exists only in test-hooks
   builds. Phase D's `repo add`/`update` call the same use case after the
   root → timestamp → index chain.
7. **Manifests** are parsed strictly (unknown members rejected), validated
   (A9), and must match the catalog entry exactly — id, version,
   architectures and dependencies (A10) — else exit 5. The verified
   manifest is kept at `cache\metadata\<repo>\manifests\<id>\<version>.json`
   for `info` (writesToInstallDir) and `--offline`.

## Scope notes

Packages whose catalog entry has dependencies are refused with
`NotAvailableInThisBuild` (Phase C resolver). MSI/EXE artifacts are refused
(Phase E). Expired-metadata auto-refresh (A8) is Phase D.
