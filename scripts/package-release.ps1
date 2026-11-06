<#
.SYNOPSIS
  Publishes and packages a WinBrew release (A14/A18) on a Windows runner.

.DESCRIPTION
  For each RID (win-x64, win-arm64):
    - one solution-level locked restore (the per-RID lock sections, A4);
    - publishes wbrew.exe (self-contained managed single file), the Native AOT
      wbrew-shim.exe and the v1.1 WinBrew.ElevatedHelper.exe;
    - copies EXACTLY the scripts/publish-files.txt set into <Output>/win-<arch>/
      (fails on any missing or extra file);
    - zips it as <Output>/wbrew-<Version>-win-<arch>.zip.
  Then writes <Output>/SHA256SUMS.txt covering every shipped file: the loose
  files of both architectures (win-<arch>/<file>) and the zips.

  Signing is a separate step (release.yml, protected environment). This script
  never signs and never claims anything is signed.

.EXAMPLE
  pwsh scripts/package-release.ps1 -Version 1.0.0 -Output out/release
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Output,
    [string[]] $Rids = @('win-x64', 'win-arm64'),
    # publish: build win-<arch>/ folders; package: zip them + SHA256SUMS.txt.
    # release.yml signs the loose binaries BETWEEN the two steps.
    [ValidateSet('all', 'publish', 'package')] [string] $Step = 'all'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$set = Get-Content (Join-Path $repo 'scripts/publish-files.txt') |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -and -not $_.StartsWith('#') }

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed ($LASTEXITCODE)" }
}

New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = Resolve-Path $Output

if ($Step -in 'all', 'publish') {
    # One solution-level locked restore covers every published RID (the RID
    # sections live in the Cli/Shim/Helper locks, ADR-0002); publish uses --no-restore.
    Invoke-Dotnet @('restore', (Join-Path $repo 'WinBrew.sln'), '--locked-mode')
}

foreach ($rid in $(if ($Step -in 'all', 'publish') { $Rids } else { @() })) {
    $stage = Join-Path $Output "publish-$rid"
    $dest = Join-Path $Output $rid
    Remove-Item -Recurse -Force $stage, $dest -ErrorAction SilentlyContinue

    Invoke-Dotnet @('publish', (Join-Path $repo 'src/WinBrew.Cli'), '-c', 'Release', '-r', $rid, '--self-contained', 'true',
        '--no-restore', "-p:Version=$Version", '-o', $stage)
    Invoke-Dotnet @('publish', (Join-Path $repo 'src/WinBrew.Shim'), '-c', 'Release', '-r', $rid,
        '--no-restore', "-p:Version=$Version", '-o', (Join-Path $stage 'shim'))
    Invoke-Dotnet @('publish', (Join-Path $repo 'src/WinBrew.ElevatedHelper'), '-c', 'Release', '-r', $rid, '--self-contained', 'true',
        '--no-restore', '-p:PublishSingleFile=true', "-p:Version=$Version", '-o', (Join-Path $stage 'helper'))

    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $sources = @{
        'wbrew.exe'                  = Join-Path $stage 'wbrew.exe'
        'e_sqlite3.dll'              = Join-Path $stage 'e_sqlite3.dll'
        'wbrew-shim.exe'             = Join-Path $stage 'shim/wbrew-shim.exe'
        'WinBrew.ElevatedHelper.exe' = Join-Path $stage 'helper/WinBrew.ElevatedHelper.exe'
    }
    foreach ($file in $set) {
        if (-not $sources.ContainsKey($file)) { throw "publish-files.txt lists '$file', which this script does not know how to produce." }
        if (-not (Test-Path $sources[$file])) { throw "'$file' was not produced for $rid ($($sources[$file]))." }
        Copy-Item $sources[$file] (Join-Path $dest $file)
    }

    # The CLI publish output itself must match the recorded manifest (ADR-0001): nothing
    # besides wbrew.exe, e_sqlite3.dll and the *.pdb files.
    $unexpected = Get-ChildItem $stage -File | Where-Object { $_.Name -notin 'wbrew.exe', 'e_sqlite3.dll' -and $_.Extension -ne '.pdb' }
    if ($unexpected) { throw "Unexpected CLI publish output for ${rid}: $($unexpected.Name -join ', ')" }

    Remove-Item -Recurse -Force $stage
    Write-Host "Published $dest"
}

if ($Step -eq 'publish') { return }

foreach ($rid in $Rids) {
    $dest = Join-Path $Output $rid
    $present = Get-ChildItem $dest -File | ForEach-Object Name | Sort-Object
    if (Compare-Object $present ($set | Sort-Object)) { throw "$dest does not hold exactly the publish-files.txt set." }
    $zip = Join-Path $Output "wbrew-$Version-$rid.zip"
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath $zip
    Write-Host "Packaged $zip"
}

# SHA256SUMS.txt — every shipped file (loose per-arch files and the zips), sha256sum format.
$lines = Get-ChildItem $Output -Recurse -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' -and $_.Extension -ne '.json' } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = [IO.Path]::GetRelativePath($Output, $_.FullName).Replace('\', '/')
        "$((Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant())  $relative"
    }
Set-Content -Path (Join-Path $Output 'SHA256SUMS.txt') -Value $lines -Encoding ascii
Write-Host "Wrote SHA256SUMS.txt ($($lines.Count) files)"
