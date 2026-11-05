# Security Policy

WinBrew's security model (repository trust, artifact verification,
archive defenses, the elevated helper) is documented in
`docs/security-model.md` and `docs/threat-model.md` as those documents
are produced during the implementation phases.

## Reporting a vulnerability

Until a public disclosure channel is set up, contact the repository
owner privately (via a GitHub security advisory on this repository).

Please include: affected version/commit, environment, reproduction
steps, and impact. Do not open a public issue for an unpatched
vulnerability.

## Scope notes

- WinBrew makes network requests only to configured repository URLs,
  artifact URLs and the self-update feed. It has no telemetry (D11).
- WinBrew never runs install scripts or hooks; portable packages are
  ZIP archives whose declared `.exe` entry points run through WinBrew's
  shim, and MSI/EXE installers (v1.1) run only with explicit consent.
