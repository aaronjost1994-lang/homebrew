# ADR-0015: No Mark-of-the-Web on downloaded or extracted files (D7)

**Status:** accepted (Phase F, 2026-11-06)

## Decision
WinBrew does not add a `Zone.Identifier` alternate data stream to downloaded
artifacts or extracted files. Authenticity comes from D6 signatures (root →
timestamp → index → manifest digest) and the artifact SHA-256.

## Consequences
- SmartScreen's reputation check does **not** apply to WinBrew-installed
  binaries; neither do Office/Explorer MotW prompts. Documented in
  `security-model.md` and `known-limitations.md`.
- Archive entries named with `:` (alternate data streams) are rejected by the
  A13 archive defenses, so no package can carry its own `Zone.Identifier`.
