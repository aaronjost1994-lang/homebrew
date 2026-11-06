<#
.SYNOPSIS
  Generates a SIGNED WinBrew fixture repository (D6) for tests and benchmarks.

.DESCRIPTION
  Writes root.json (+ root/1.json), timestamp.json, index.json and
  manifests/<id>/<version>.json under -Output, signed with freshly generated
  ECDSA P-256 keys (IEEE P1363 signatures over the exact payload bytes; key
  IDs = lowercase hex SHA-256 of the DER SubjectPublicKeyInfo). The private
  keys are written to <Output>/keys/ as PKCS#8 base64 — TEST KEYS ONLY, never
  use them for a real repository.

  Artifacts are NOT generated: manifests point at <BaseUrl>/artifacts/<id>-<version>.zip
  with a placeholder digest, which is enough for catalog, search and
  resolver benchmarks (A17: the 10,000-package search catalog). Tests that
  install packages build real fixtures with WinBrew.TestFixtures
  (FixtureRepository + RepositorySigner), which follows the same format.

  The root fingerprints to pass to `wbrew repo add --trust-root` are printed
  and written to <Output>/keys/fingerprints.txt.

.EXAMPLE
  pwsh scripts/new-fixture-repo.ps1 -Output out/fixture -BaseUrl https://localhost:8443/ -Packages 10000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Output,
    [Parameter(Mandatory)] [string] $BaseUrl,
    [int] $Packages = 3,
    [int] $RootKeys = 1,
    [int] $RootThreshold = 1,
    [int] $IndexVersion = 1,
    [int] $ExpiresDays = 6
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($RootThreshold -lt 1 -or $RootThreshold -gt $RootKeys) { throw "RootThreshold must be between 1 and RootKeys." }
if ($ExpiresDays -gt 7) { throw "timestamp.json may not live longer than 7 days (D6)." }

$utf8 = [System.Text.UTF8Encoding]::new($false)
$base = $BaseUrl.TrimEnd('/') + '/'
$expires = [DateTimeOffset]::UtcNow.AddDays($ExpiresDays).ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)
$rootExpires = [DateTimeOffset]::UtcNow.AddDays(300).ToString("yyyy-MM-ddTHH:mm:ssZ", [Globalization.CultureInfo]::InvariantCulture)

function New-Key {
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    $spki = $ecdsa.ExportSubjectPublicKeyInfo()
    $id = ([System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($spki))).ToLowerInvariant()
    [pscustomobject]@{ Ecdsa = $ecdsa; Spki = $spki; Id = $id }
}

function Write-Envelope([string] $Path, [string] $PayloadType, [string] $Json, [object[]] $Keys) {
    $payload = $utf8.GetBytes($Json)
    $signatures = foreach ($k in $Keys) {
        $sig = $k.Ecdsa.SignData($payload, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
        if ($sig.Length -ne 64) { throw "unexpected signature length $($sig.Length)" }
        [ordered]@{ keyid = $k.Id; sig = [System.Convert]::ToBase64String($sig) }
    }
    $envelope = [ordered]@{
        payload     = [System.Convert]::ToBase64String($payload)
        payloadType = $PayloadType
        signatures  = @($signatures)
    }
    $bytes = $utf8.GetBytes(($envelope | ConvertTo-Json -Depth 5 -Compress))
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    [System.IO.File]::WriteAllBytes($Path, $bytes)
    return $bytes
}

function Get-Sha256Hex([byte[]] $Bytes) {
    ([System.Convert]::ToHexString([System.Security.Cryptography.SHA256]::HashData($Bytes))).ToLowerInvariant()
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$rootKeys = @(1..$RootKeys | ForEach-Object { New-Key })
$timestampKey = New-Key
$indexKey = New-Key

# ---- manifests + index (sparse index, D6) ------------------------------------
$packagesNode = [ordered]@{}
for ($i = 0; $i -lt $Packages; $i++) {
    $id = 'fixture-{0:D5}' -f $i
    $version = '1.0.0'
    $manifest = [ordered]@{
        schemaVersion      = '1'
        id                 = $id
        version            = $version
        displayName        = "Fixture $i"
        description        = "Generated fixture package number $i"
        homepage           = 'https://example.org/'
        license            = 'MIT'
        scope              = @('user')
        writesToInstallDir = $false
        artifacts          = @([ordered]@{
            architecture = 'x64'
            url          = "${base}artifacts/$id-$version.zip"
            sha256       = ('0' * 64)
            size         = 1
            format       = 'zip'
            installType  = 'portable'
        })
        dependencies       = @()
        executables        = @()
    }
    $manifestBytes = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 6 -Compress))
    $manifestPath = Join-Path $Output "manifests/$id/$version.json"
    New-Item -ItemType Directory -Force -Path (Split-Path $manifestPath) | Out-Null
    [System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)

    $packagesNode[$id] = [ordered]@{
        displayName = "Fixture $i"
        description = "Generated fixture package number $i"
        versions    = [ordered]@{
            $version = [ordered]@{
                sha256        = Get-Sha256Hex $manifestBytes
                size          = $manifestBytes.Length
                architectures = @('x64')
                dependencies  = @()
            }
        }
    }
}

$index = [ordered]@{ version = $IndexVersion; expires = $expires; packages = $packagesNode }
$indexBytes = Write-Envelope (Join-Path $Output 'index.json') 'application/vnd.winbrew.index+json;version=1' `
    ($index | ConvertTo-Json -Depth 8 -Compress) @($indexKey)

# ---- timestamp (binds index.json by digest and length) -------------------------
$timestamp = [ordered]@{
    version = $IndexVersion
    expires = $expires
    meta    = [ordered]@{ 'index.json' = [ordered]@{ sha256 = Get-Sha256Hex $indexBytes; length = $indexBytes.Length } }
}
Write-Envelope (Join-Path $Output 'timestamp.json') 'application/vnd.winbrew.timestamp+json;version=1' `
    ($timestamp | ConvertTo-Json -Depth 5 -Compress) @($timestampKey) | Out-Null

# ---- root (signed by a threshold of its own root keys) ------------------------------
$keysNode = [ordered]@{}
foreach ($k in @($rootKeys + $timestampKey + $indexKey)) {
    $keysNode[$k.Id] = [ordered]@{ keytype = 'ecdsa'; scheme = 'ecdsa-sha256-nistp256'; public = [System.Convert]::ToBase64String($k.Spki) }
}
$root = [ordered]@{
    version = 1
    expires = $rootExpires
    keys    = $keysNode
    roles   = [ordered]@{
        root      = [ordered]@{ threshold = $RootThreshold; keyids = @($rootKeys | ForEach-Object Id) }
        timestamp = [ordered]@{ threshold = 1; keyids = @($timestampKey.Id) }
        index     = [ordered]@{ threshold = 1; keyids = @($indexKey.Id) }
    }
}
$rootJson = $root | ConvertTo-Json -Depth 6 -Compress
$rootBytes = Write-Envelope (Join-Path $Output 'root.json') 'application/vnd.winbrew.root+json;version=1' $rootJson @($rootKeys[0..($RootThreshold - 1)])
New-Item -ItemType Directory -Force -Path (Join-Path $Output 'root') | Out-Null
[System.IO.File]::WriteAllBytes((Join-Path $Output 'root/1.json'), $rootBytes)

# ---- test keys + fingerprints ---------------------------------------------------------
$keysDir = Join-Path $Output 'keys'
New-Item -ItemType Directory -Force -Path $keysDir | Out-Null
$i = 0
foreach ($k in $rootKeys) {
    [System.IO.File]::WriteAllText((Join-Path $keysDir "root-$i.pkcs8"), [System.Convert]::ToBase64String($k.Ecdsa.ExportPkcs8PrivateKey()))
    $i++
}
[System.IO.File]::WriteAllText((Join-Path $keysDir 'timestamp.pkcs8'), [System.Convert]::ToBase64String($timestampKey.Ecdsa.ExportPkcs8PrivateKey()))
[System.IO.File]::WriteAllText((Join-Path $keysDir 'index.pkcs8'), [System.Convert]::ToBase64String($indexKey.Ecdsa.ExportPkcs8PrivateKey()))
$fingerprints = @($rootKeys | ForEach-Object Id)
[System.IO.File]::WriteAllLines((Join-Path $keysDir 'fingerprints.txt'), $fingerprints)

Write-Host "Fixture repository written to $Output ($Packages package(s), index v$IndexVersion)."
Write-Host "Add it with:"
Write-Host ("  wbrew repo add <name> $base " + (($fingerprints | ForEach-Object { "--trust-root $_" }) -join ' '))
