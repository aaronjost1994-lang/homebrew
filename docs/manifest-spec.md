# Package manifest specification (manifest v1)

Schema: `schemas/manifest.v1.schema.json`. The typed validator
(`ManifestValidator`) enforces the rules beyond the schema. Manifests are
served at `<repository>/manifests/<id>/<version>.json`, bound to `index.json`
by SHA-256 and size, and parsed strictly (unknown members are rejected). Any
problem is a verification failure (exit 5).

## Fields

| Field | Required | Rules |
|---|---|---|
| `schemaVersion` | yes | `"1"` |
| `id` | yes | `^[a-z0-9](?:[a-z0-9.-]{0,62}[a-z0-9])?$`, no `..`, no reserved device names (`CON`, `NUL`, `COM1` …) |
| `version` | yes | A NuGet version already in normalized form (`1.2.0`, `2.0.0-beta.1`) — D3 |
| `vendorVersion` | no | Free text, display only, ≤ 64 characters |
| `displayName`, `description` | yes | ≤ 256 / ≤ 2048 characters |
| `homepage` | yes | HTTPS URL |
| `license` | yes | SPDX expression |
| `scope` | yes | `["user"]` for portable packages; MSI/EXE may declare `user` and/or `machine` (v1.1) |
| `writesToInstallDir` | no (default `false`) | `true` when the application stores configuration or state inside its installation directory; `wbrew info` and `install` then warn that this state is not kept across upgrades |
| `artifacts[]` | yes | One per architecture (`x64`, `arm64`, `x86`): `url` (HTTPS), `sha256` (64 lowercase hex), `size` (bytes), `format` (`zip`/`msi`/`exe`), `installType` (`portable`/`msi`/`exe`). `zip` pairs with `portable`. All artifacts share one install type. |
| `dependencies[]` | no | `{ "id": "<id>" or "<repo>/<id>", "range": "<NuGet interval>" }` — must equal the index entry exactly. Unqualified ids refer to the declaring repository only; a qualified id needs that repository configured (else exit 3). Installer packages have none. |
| `executables[]` | portable only | `{ "name": "<shim name>", "target": "<relative path to an .exe inside the package>" }`; the PE machine type must run on the host (A10) |
| `installer` | MSI/EXE only (v1.1) | See below |

Package identity is `<repo>/<id>`. An unqualified id that exists in more than
one repository is a usage error (exit 2) listing the candidates; there is no
repository priority.

## `installer` (v1.1)

Installer declarations are **executable-code trust decisions, not safe data**:
they decide what runs, with which arguments, possibly elevated.

```json
"installer": {
  "type": "msi",
  "msi": {
    "productCode": "{6F1C2D3E-4A5B-4C6D-8E7F-90A1B2C3D4E5}",
    "upgradeCode": "{0B1C2D3E-4F5A-4B6C-9D7E-8F90A1B2C3D4}",
    "properties": { "ADDLOCAL": "Main" },
    "userProperties": { "ALLUSERS": "2", "MSIINSTALLPERUSER": "1" }
  }
}
```

- `productCode` / `upgradeCode`: uppercase braced GUIDs.
- `properties`: public properties passed on every install. `ALLUSERS`,
  `MSIINSTALLPERUSER`, `REBOOT`, `REBOOTPROMPT` are reserved. Values use the
  safe alphabet `A–Z a–z 0–9 . _ : \ / { } @ + , = -` (no spaces, quotes or
  control characters).
- `userProperties`: the property set the MSI was **authored** for per-user
  installs. Required when `scope` contains `user`.

```json
"installer": {
  "type": "exe",
  "exe": {
    "silentInstallArgs": ["/S", "/install"],
    "silentUninstall": { "path": "{LocalAppData}/Vendor/uninstall.exe", "args": ["/S"] },
    "detection": {
      "uninstallKey": "VendorApp",
      "file": { "path": "{LocalAppData}/Vendor/app.exe", "version": "1.0.0.0" }
    }
  }
}
```

- Switches are never guessed: `silentInstallArgs` and `silentUninstall` are
  explicit; registry `UninstallString` values are never run.
- Path patterns start with `{ProgramFiles}`, `{ProgramFilesX86}`,
  `{ProgramData}`, `{LocalAppData}` or `{AppData}`, followed by `/`-separated
  segments without `..`, `:`, device names or trailing dots/spaces. A
  machine-only package must use a machine root.
- `detection`: at least one rule. After the installer runs, all declared rules
  must match; anything else is state `Unknown` (exit 8).

## Example (portable)

```json
{
  "schemaVersion": "1",
  "id": "hello",
  "version": "2.0.0",
  "displayName": "Hello",
  "description": "Prints hello.",
  "homepage": "https://example.org/hello",
  "license": "MIT",
  "scope": ["user"],
  "artifacts": [
    { "architecture": "x64", "url": "https://repo.example/artifacts/hello-2.0.0-x64.zip",
      "sha256": "<64 lowercase hex>", "size": 12345, "format": "zip", "installType": "portable" }
  ],
  "dependencies": [ { "id": "libhello", "range": "[1.0.0,2.0.0)" } ],
  "executables": [ { "name": "hello", "target": "bin/hello.exe" } ]
}
```
