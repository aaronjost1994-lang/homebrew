# ADR-0018: No telemetry (D11)

**Status:** accepted (Phase F, 2026-11-06)

## Decision
WinBrew collects and sends no telemetry. It makes network requests only to
configured repository URLs (metadata and manifests), artifact URLs named by
verified manifests, and the self-update feed compiled into the build.
`IHttpClientFactory` logging handlers are removed and URLs are redacted in
logs (A13).

## Consequences
No usage data informs development; issues must be reported by users.
`security-model.md` lists every network destination.
