# examples/

Illustrative files. **Nothing here points at a real repository**: URLs use the
reserved `.invalid` domain, and digests and fingerprints are placeholders.

- `bundles/dev.v1.json` + `bundles/dev.v1.lock.json` — a versioned example
  bundle (bundle.v1) and its lockfile (lockfile.v1). `bundle install --file
  dev.v1.json --locked` reads the lock from `dev.v1.lock.json` beside it.
  `BundleAndReleaseFileTests` parses both, so they stay valid.
- Signed fixture repositories are generated, never committed:
  `scripts/new-fixture-repo.ps1` (TEST ONLY keys) and
  `WinBrew.TestFixtures.RepositorySigner`.
- Manifest examples: `docs/manifest-spec.md`.
