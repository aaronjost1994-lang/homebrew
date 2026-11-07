# Try WinBrew for real: repository server + Windows client

This folder sets up both sides of a working WinBrew installation:

| Side | What | Where |
|---|---|---|
| **Server** | A signed package repository, served over HTTPS by Caddy, built and re-signed by `winbrew-repo` | `deploy/server/` (Docker Compose) |
| **Client** | WinBrew from a GitHub release zip, installed and connected to that repository | `deploy/client/Install-WinBrew.ps1` |

```
 Windows PC (client)                         Docker host (server)
 ┌───────────────────────────┐   HTTPS      ┌──────────────────────────────────────┐
 │ wbrew.exe                 │ ───────────▶ │ caddy  :8443  (TLS, static files)    │
 │  repo add demo <url>      │              │   └── volume "repo" (read-only)      │
 │  --trust-root <fp>        │              │ repo-builder (winbrew-repo)          │
 │ bin\hello.exe (shim)      │              │   signs root/timestamp/index,        │
 └───────────────────────────┘              │   packs artifacts, re-signs daily    │
                                            │   └── volume "keys" (never served)   │
                                            └──────────────────────────────────────┘
```

The demo repository holds five package versions, all real Windows x64
programs cross-compiled from `server/demo/src/*.c`:

| Package | Shows |
|---|---|
| `hello` 1.0.0 and 2.0.0 | install, then `wbrew upgrade` |
| `toolbox` 1.0.0 | dependency resolution: it pulls in `echoargs` and `exitcode` |
| `echoargs` 1.0.0 | how arguments and quoting pass through a shim |
| `exitcode` 1.0.0 | exit codes forwarded through a shim |

ARM64 Windows runs these x64 builds through emulation; WinBrew selects them automatically.

---

## 1. Server (any machine with Docker)

```bash
cd deploy/server
cp .env.example .env      # set PUBLIC_HOST, see below
docker compose up -d --build
docker compose logs repo-builder
```

The log prints the **root fingerprint**. Clients need it, so write it down.
It is also in the keys volume:
`docker compose exec repo-builder cat /keys/fingerprint.txt`.

**`PUBLIC_HOST`** is how the Windows client reaches the server, and it is
written into every manifest:

| Setup | `.env` | Certificate |
|---|---|---|
| Docker Desktop on the same Windows PC | `PUBLIC_HOST=localhost` | Caddy local CA (step 2b) |
| Server on your LAN | `PUBLIC_HOST=192.168.1.50` (its IP) | Caddy local CA (step 2b) |
| Public server with a DNS name | `PUBLIC_HOST=repo.example.com`, `HTTPS_PORT=443`, `HTTP_PORT=80` | Let's Encrypt, automatic |

If you change `PUBLIC_HOST`, restart the builder so the manifests are rewritten:
`docker compose up -d --force-recreate repo-builder`.

Check that it works from the server:
```bash
docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ./winbrew-repo-ca.crt
curl --cacert winbrew-repo-ca.crt https://localhost:8443/root.json
```

### Keys and re-signing

- `repo-builder` keeps its signing keys in the `keys` volume and signs the
  repository on start and then every `RESIGN_EVERY_HOURS` (default 24).
  `timestamp.json` expires after 6 days, so a stopped builder makes the
  repository unusable for clients within a week. That is intended: it stops
  replay of stale metadata.
- **Back up the `keys` volume.** Losing `root.pem` means every client has to
  run `repo remove` and `repo add` again with a new fingerprint.
- The root expires after 360 days; key rotation is not automated yet.
- These keys are online, which is fine for a demo or a private team
  repository. A public production repository should keep the root key
  offline (`docs/repository-trust-spec.md`).

### Your own packages

Mount a folder over `/packages` (see the comment in `compose.yaml`) laid out like this:

```
my-packages/
  mytool/
    1.0.0/
      package.json
      files/            ← exactly what gets installed, e.g. bin/mytool.exe
```

`package.json`:
```json
{
  "displayName": "My Tool",
  "description": "What it does, in one sentence.",
  "homepage": "https://example.com/mytool",
  "license": "MIT",
  "architecture": "x64",
  "dependencies": [ { "id": "otherpkg", "range": "[1.0.0,2.0.0)" } ],
  "executables": [ { "name": "mytool", "target": "bin/mytool.exe" } ]
}
```

`architecture` is `x64`, `arm64` or `x86`, and `dependencies` is optional.
Each `executables` entry becomes a command on the client's `PATH`.
Field rules: `docs/manifest-spec.md`.

---

## 2. Client (Windows 10/11, x64 or ARM64)

### 2a. Get a release

Releases are built by `.github/workflows/release.yml` when a version tag is
pushed:

```bash
git tag v0.1.0
git push origin v0.1.0
```

The workflow creates a **draft** GitHub release containing
`wbrew-0.1.0-UNSIGNED-win-x64.zip`, the `-win-arm64` zip, `SHA256SUMS.txt`,
an SBOM and `SIGNING.txt`. Open it under **Releases**, check it, then
**Publish**. "UNSIGNED" stays in the names until a code-signing route is set
up (`docs/release-checklist.md`).

Without a release, the zip from any green CI run works too: the run page,
**Artifacts**, `wbrew-win-x64-unsigned`.

### 2b. Trust the server's certificate (Caddy local CA only)

Skip this step for a public server with Let's Encrypt.

Copy the CA certificate from the server:
`docker compose cp caddy:/data/caddy/pki/authorities/local/root.crt ./winbrew-repo-ca.crt`.
Bring it to the Windows PC and pass it to the installer as `-CaCertPath`.
The installer adds it to your user's trusted roots, and Windows asks for
confirmation. Remove it later with `certmgr.msc`, under Trusted Root
Certification Authorities, entry "Caddy Local Authority".

### 2c. Install and connect

In PowerShell (no administrator rights needed), in a folder that holds the
zip, `SHA256SUMS.txt` and `Install-WinBrew.ps1`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-WinBrew.ps1 -ZipPath .\wbrew-0.1.0-UNSIGNED-win-x64.zip `
    -RepoUrl https://192.168.1.50:8443/ `
    -Fingerprint <fingerprint from the server log> `
    -CaCertPath .\winbrew-repo-ca.crt
```

Or let the script download the published release itself (no login needed,
the repository is public):
`.\Install-WinBrew.ps1 -Version 0.1.0 -RepoUrl ... -Fingerprint ... -CaCertPath ...`

The script checks the zip against `SHA256SUMS.txt`, runs
`wbrew self install --modify-path`, trusts the CA, and runs
`wbrew repo add demo <url> --trust-root <fingerprint>`.

### 2d. Use it (in a new terminal)

```powershell
wbrew search hello
wbrew install hello@1.0.0 --yes
hello world                       # runs through the shim in %LOCALAPPDATA%\WinBrew\bin
wbrew outdated
wbrew upgrade --yes ; hello       # now 2.0.0
wbrew install toolbox --yes       # also installs echoargs and exitcode
echoargs "a b" 'c d' e
exitcode 7 ; $LASTEXITCODE        # 7
wbrew list
wbrew uninstall toolbox --yes ; wbrew autoremove --yes
wbrew doctor
wbrew self uninstall              # removes everything again
```

---

## What has been verified

- The stack was brought up with Docker Compose (`PUBLIC_HOST=localhost`).
  A release build of the client (HTTPS only, no test hooks), trusting only
  Caddy's local CA, then ran `repo add` with the fingerprint, `search`,
  `info`, `install toolbox` (which installed both dependencies),
  `install hello@1.0.0`, `upgrade` to 2.0.0, a re-sign followed by `update`
  (artifacts unchanged byte for byte), `uninstall` and `autoremove`.
  This was done on the Linux dev host, so the `.exe`s were installed but not run.
- `tests/WinBrew.IntegrationTests/RepoToolTests.cs` repeats the publish,
  install, re-sign, update and upgrade cycle in CI on every push, on Windows
  x64, ARM64 and Linux.
- **Not yet verified:** the PowerShell installer script and running the demo
  programs through shims on a Windows PC. That is what this guide is for.
