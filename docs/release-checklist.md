# Release checklist

## External infrastructure (owner action required)

| Item | Needed for | Owner | Notes |
|---|---|---|---|
| Code-signing route | Authenticode for `wbrew.exe`, `wbrew-shim.exe`, `WinBrew.ElevatedHelper.exe` | project owner (aaronjost1994-lang) | Preferred: Microsoft's managed code-signing service (Trusted Signing, renamed "Artifact Signing" — **verify the current name, identity-validation and regional eligibility rules** before applying). Otherwise an OV or EV certificate from a public CA (identity validation required). Configure the `release-signing` GitHub environment (required reviewers, tag-only) with `SIGNING_ENDPOINT`, `SIGNING_ACCOUNT`, `SIGNING_CERTIFICATE_PROFILE`, `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (OIDC federated credential). Never commit credentials or keys. |
| Windows ARM64 runners | ARM64 CI and native E2E | project owner | `ci.yml` uses the GitHub-hosted label `windows-11-arm`; verify availability for the repository's plan |
| Production repository hosting | Public use | project owner | HTTPS static hosting that keeps every historical manifest |
| Production root keys | Trust bootstrap | project owner | Generate and store **offline** (≥ 2 root keys, threshold ≥ 2 recommended); online timestamp/index keys; documented rotation procedure |
| Self-update feed URL + release root chain | `self update` | project owner | Build with `-p:WinBrewReleaseFeedUrl=…` and `-p:WinBrewReleaseRoot=<base64 root.json>` |
| Action pinning | Supply chain | maintainer | Pin every `uses:` in `.github/workflows/` by commit SHA |
| Tool versions | SBOM, fixtures | maintainer | Verify the pinned CycloneDX (5.5.0) and WiX (5.0.2) tool versions exist and are current |

## Per release

1. CHANGELOG: move `[Unreleased]` to the version; update README status.
2. `ci.yml` green on `windows-latest` (x64) and `windows-11-arm` (ARM64),
   including the release-binary checks and the installer/directory tests on
   the disposable runners.
3. Tag `v<version>`; `release.yml` runs publish → sign (approval in
   `release-signing`) → package.
4. Check the draft release: both zips, `SHA256SUMS.txt`, `sbom.cdx.json`,
   `SIGNING.txt` (`SIGNED`, or `UNSIGNED` in the title and file names).
5. Download each zip on a clean machine; verify `SHA256SUMS.txt`; run
   `wbrew --version` and `wbrew self install`.
6. Update `release-readiness.md` with the CI run links and measured A17
   numbers; publish the release only if every blocker is resolved or
   explicitly accepted.
