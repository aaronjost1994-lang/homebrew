<#
.SYNOPSIS
  Builds the Phase E installer fixtures on a Windows CI runner (A16):
  the WiX fixture MSI and the minimal fixture EXE installer.
.DESCRIPTION
  Requires the WiX Toolset v4+ CLI (`dotnet tool install --global wix`) and
  the .NET SDK. Outputs to -Output and prints SHA-256 and size of each file,
  ready for a fixture repository manifest. Set WINBREW_INSTALLER_FIXTURES to
  -Output to enable the Windows installer integration tests.
#>
[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Output)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$here = Join-Path $PSScriptRoot '..\tests\fixtures\installers'
New-Item -ItemType Directory -Force -Path $Output | Out-Null

Push-Location $here
try {
    wix build FixtureMsi.wxs -o (Join-Path $Output 'winbrew-fixture.msi')
    if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }
} finally { Pop-Location }

dotnet publish (Join-Path $here 'FixtureExe\FixtureExe.csproj') -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -o (Join-Path $Output 'exe')
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
Copy-Item (Join-Path $Output 'exe\winbrew-fixture-setup.exe') (Join-Path $Output 'winbrew-fixture-setup.exe') -Force

foreach ($file in 'winbrew-fixture.msi', 'winbrew-fixture-setup.exe') {
    $path = Join-Path $Output $file
    $hash = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLowerInvariant()
    Write-Host "$file  sha256=$hash  size=$((Get-Item $path).Length)"
}
