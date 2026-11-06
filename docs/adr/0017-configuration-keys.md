# ADR-0017: Configuration keys (D10)

**Status:** accepted (Phase F, 2026-11-06; implemented in Phase A)

## Decision
`config.json` (with `schemaVersion`, `schemas/config.v1.schema.json`) holds
only the D10 keys: `color`, `interactive`, `cache.maxBytes`,
`download.timeoutSeconds`, `download.maxBytes`, `download.retries` (0–5),
`retainVersions` (0–5), `lock.timeoutSeconds`, `trust.maxClockSkewSeconds`,
`trust.maxMetadataBytes`, `archive.maxEntries`, `archive.maxTotalBytes`,
`archive.maxCompressionRatio`, `path.managed`, `testing.allowInsecureLocalhost`.
- `config set` validates type and range; unknown keys exit 2; release builds
  reject `testing.*` with exit 2 and `doctor` reports such a key if it is
  found in the file.
- Credentials are never stored in `config.json` (Windows Credential Manager,
  `WinBrew:<origin>`, A13).
- `path.managed` is set only by WinBrew after the user consented to a PATH
  change.

## Evidence
`ConfigDefinitionsTests`, `JsonConfigStoreTests`, `ReleaseBinaryTests` (release rejects `testing.*`).
