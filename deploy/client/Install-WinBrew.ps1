<#
.SYNOPSIS
  Installs WinBrew from a release zip and connects it to a WinBrew repository.

.DESCRIPTION
  1. Gets the release zip: -ZipPath (already downloaded) or -Version (downloaded
     from the GitHub release with the gh CLI; the repository is private).
  2. Verifies its SHA-256 against SHA256SUMS.txt from the same release.
  3. Extracts it and runs `wbrew self install --modify-path`.
  4. Optionally trusts the server's CA (-CaCertPath; only for servers that use
     Caddy's local CA: localhost, IP addresses, internal names).
  5. Optionally adds the repository (-RepoUrl + -Fingerprint).

  Windows PowerShell 5.1 or PowerShell 7. No administrator rights needed.

.EXAMPLE
  # Release zip + checksums downloaded by hand into the current folder:
  .\Install-WinBrew.ps1 -ZipPath .\wbrew-0.1.0-UNSIGNED-win-x64.zip `
      -RepoUrl https://192.168.1.50:8443/ -Fingerprint <64 hex chars> -CaCertPath .\winbrew-repo-ca.crt

.EXAMPLE
  # Download release v0.1.0 with gh (gh auth login first):
  .\Install-WinBrew.ps1 -Version 0.1.0 -RepoUrl https://repo.example.com/ -Fingerprint <64 hex chars>
#>
[CmdletBinding(DefaultParameterSetName = 'Zip')]
param(
    [Parameter(ParameterSetName = 'Zip', Mandatory)] [string] $ZipPath,
    [Parameter(ParameterSetName = 'Zip')] [string] $SumsPath,
    [Parameter(ParameterSetName = 'Download', Mandatory)] [string] $Version,
    [Parameter(ParameterSetName = 'Download')] [string] $GitHubRepo = 'aaronjost1994-lang/homebrew',
    [string] $RepoName = 'demo',
    [string] $RepoUrl,
    [string] $Fingerprint,
    [string] $CaCertPath,
    [switch] $NoModifyPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('wbrew-setup-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    # ---- 1. Get the zip ---------------------------------------------------------------
    if ($PSCmdlet.ParameterSetName -eq 'Download') {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
            throw 'The gh CLI is required for -Version (https://cli.github.com). Or download the zip and SHA256SUMS.txt by hand and use -ZipPath.'
        }
        Write-Host "Downloading WinBrew v$Version ($arch) from $GitHubRepo ..."
        gh release download "v$Version" --repo $GitHubRepo --dir $work --pattern "wbrew-*-$arch.zip" --pattern 'SHA256SUMS.txt'
        if ($LASTEXITCODE -ne 0) { throw "gh release download failed (exit $LASTEXITCODE)." }
        $ZipPath = (Get-ChildItem $work -Filter "wbrew-*-$arch.zip" | Select-Object -First 1).FullName
        $SumsPath = Join-Path $work 'SHA256SUMS.txt'
    }
    $ZipPath = (Resolve-Path $ZipPath).Path
    if (-not $SumsPath) { $SumsPath = Join-Path (Split-Path $ZipPath) 'SHA256SUMS.txt' }

    # ---- 2. Verify ----------------------------------------------------------------------
    if (-not (Test-Path $SumsPath)) { throw "SHA256SUMS.txt not found at $SumsPath (download it from the same release)." }
    $zipName = Split-Path $ZipPath -Leaf
    $expected = Get-Content $SumsPath | Where-Object { $_ -match '^([0-9a-f]{64})\s+\*?(.+)$' -and (Split-Path $Matches[2] -Leaf) -eq $zipName } |
        ForEach-Object { ($_ -split '\s+')[0] } | Select-Object -First 1
    if (-not $expected) { throw "$zipName is not listed in $SumsPath." }
    $actual = (Get-FileHash $ZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw "SHA-256 mismatch for ${zipName}: expected $expected, got $actual. Do not use this file." }
    Write-Host "SHA-256 verified: $zipName"
    if ($zipName -match 'UNSIGNED') {
        Write-Warning 'This release is UNSIGNED (no Authenticode signature). Continue only if you trust where it came from.'
    }

    # ---- 3. Extract + self install --------------------------------------------------------
    $extract = Join-Path $work 'wbrew'
    Expand-Archive -Path $ZipPath -DestinationPath $extract
    Get-ChildItem $extract -File | Unblock-File
    $selfArgs = @('self', 'install')
    if (-not $NoModifyPath) { $selfArgs += '--modify-path' }
    & (Join-Path $extract 'wbrew.exe') @selfArgs
    if ($LASTEXITCODE -ne 0) { throw "wbrew self install failed (exit $LASTEXITCODE)." }

    $root = if ($env:WINBREW_ROOT) { $env:WINBREW_ROOT } else { Join-Path $env:LOCALAPPDATA 'WinBrew' }
    $wbrew = Join-Path $root 'bin\wbrew.exe'

    # ---- 4. Trust the server's CA (Caddy local CA only) ----------------------------------
    if ($CaCertPath) {
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 (Resolve-Path $CaCertPath).Path
        Write-Host "Trusting CA '$($cert.Subject)' (thumbprint $($cert.Thumbprint)) for your user account."
        Write-Host 'Windows shows a security warning; check the thumbprint, then choose Yes.'
        Import-Certificate -FilePath (Resolve-Path $CaCertPath).Path -CertStoreLocation Cert:\CurrentUser\Root | Out-Null
    }

    # ---- 5. Add the repository -------------------------------------------------------------
    if ($RepoUrl) {
        if (-not $Fingerprint) { throw '-RepoUrl needs -Fingerprint (the server prints it: docker compose logs repo-builder).' }
        & $wbrew repo add $RepoName $RepoUrl --trust-root $Fingerprint
        if ($LASTEXITCODE -ne 0) { throw "wbrew repo add failed (exit $LASTEXITCODE)." }
    }

    Write-Host ''
    Write-Host 'Done. Open a NEW terminal, then try:'
    Write-Host '  wbrew --version'
    if ($RepoUrl) {
        Write-Host '  wbrew search hello'
        Write-Host '  wbrew install hello@1.0.0 --yes; hello world'
        Write-Host '  wbrew upgrade --yes; hello'
        Write-Host '  wbrew install toolbox --yes; echoargs "a b" c; exitcode 7; $LASTEXITCODE'
    }
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
