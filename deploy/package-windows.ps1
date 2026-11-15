<#
.SYNOPSIS
    Buduje pliki wydania Glebosaurusa dla Windows x64: paczkę samodzielną (z .NET, bez instalowania czegokolwiek),
    skrypt install-windows.ps1 i SHA256SUMS. Używa go workflow wydania (.github/workflows/release.yml) i próby lokalne.

.EXAMPLE
    ./deploy/package-windows.ps1
    Wersja z Directory.Build.props, wynik w publish/release.

.EXAMPLE
    ./deploy/package-windows.ps1 -Version 0.1.1 -OutputDir publish/proba-0.1.1
    Paczka z innym numerem wersji – do prób aktualizacji (repozytorium się nie zmienia).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Wersja `„$Version`” nie ma postaci major.minor.patch." }
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'publish/release' }
$OutputDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDir)
$stage = Join-Path $OutputDir 'stage'
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Agent (CLI + monitor, WinRT BLE) i usługa (bez okna) do jednego katalogu – ten sam .NET, wspólne biblioteki.
$projects = @(
    @{ Path = 'src/Glebosaurus.Agent/Glebosaurus.Agent.csproj'; Framework = 'net10.0-windows10.0.19041.0' },
    @{ Path = 'src/Glebosaurus.Service/Glebosaurus.Service.csproj'; Framework = 'net10.0-windows' }
)
foreach ($project in $projects) {
    dotnet publish (Join-Path $repo $project.Path) -c Release -f $project.Framework -r win-x64 --self-contained true `
        "-p:Version=$Version" --nologo -v quiet -o $stage
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $($project.Path) zakończył się kodem $LASTEXITCODE." }
}

Copy-Item -LiteralPath (Join-Path $repo 'examples/glebosaurus.config.json') -Destination (Join-Path $stage 'glebosaurus.config.example.json')
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse

$zipName = "Glebosaurus-$Version-win-x64.zip"
$zip = Join-Path $OutputDir $zipName
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item -LiteralPath $stage -Recurse -Force

$installer = Join-Path $OutputDir 'install-windows.ps1'
Copy-Item -LiteralPath (Join-Path $repo 'deploy/install-windows.ps1') -Destination $installer -Force

# Format sha256sum (LF, bez BOM) – czyta go usługa (Sha256Sums) i install-windows.ps1.
$lines = foreach ($file in @($zip, $installer)) {
    '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash.ToLowerInvariant(), (Split-Path -Leaf $file)
}
[IO.File]::WriteAllText((Join-Path $OutputDir 'SHA256SUMS'), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

Write-Host "Pliki wydania $Version w ${OutputDir}:"
Get-ChildItem -LiteralPath $OutputDir -File | ForEach-Object { Write-Host ("  {0,-36} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }
