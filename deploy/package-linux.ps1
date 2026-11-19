<#
.SYNOPSIS
    Buduje pliki wydania Glebosaurusa dla Linuksa (Raspberry Pi OS 64-bit): paczkę zależną od frameworka
    Glebosaurus-<wersja>-linux-arm64.tar.gz (na urządzeniu potrzebny ASP.NET Core Runtime 10), skrypt install-linux.sh i ich
    wiersze w SHA256SUMS. Używa go workflow wydania (.github/workflows/release.yml), CI (próba instalacji na arm64) i próby
    lokalne.

.DESCRIPTION
    SHA256SUMS jest wspólny dla całego wydania: gdy w katalogu wyjściowym już jest (np. z deploy/package-windows.ps1),
    skrypt podmienia w nim tylko własne wiersze. Uprawnienia plików w paczce nie mają znaczenia – install-linux.sh ustawia
    je sam (program należy do roota, glebosaurus.dll uruchamia dotnet).

    Wymaga PowerShell 7 (paczka tar.gz przez System.Formats.Tar – ten sam wynik na Windows i Linuksie).

.EXAMPLE
    ./deploy/package-linux.ps1
    Wersja z Directory.Build.props, wynik w publish/release.

.EXAMPLE
    ./deploy/package-linux.ps1 -Version 0.0.2 -OutputDir publish/proba-linux
    Paczka z innym numerem wersji – do prób samoaktualizacji (repozytorium się nie zmienia).
#>
#Requires -Version 7
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    # linux-arm64 – Raspberry Pi OS 64-bit (wydania); linux-arm (32-bit) dojdzie z czytaniem czujników na Pi (ROADMAP 2.3b).
    [ValidateSet('linux-arm64', 'linux-arm', 'linux-x64')]
    [string]$Runtime = 'linux-arm64'
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
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$stage = Join-Path $OutputDir "stage-$Runtime"
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Agent (CLI + run; BLE przez BlueZ) zależny od frameworka: ASP.NET Core Runtime instaluje i aktualizuje właściciel urządzenia.
dotnet publish (Join-Path $repo 'src/Glebosaurus.Agent/Glebosaurus.Agent.csproj') -c Release -f net10.0 -r $Runtime --self-contained false `
    "-p:Version=$Version" --nologo -v quiet -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish zakończył się kodem $LASTEXITCODE." }

Copy-Item -LiteralPath (Join-Path $repo 'examples/glebosaurus.config.json') -Destination (Join-Path $stage 'glebosaurus.config.example.json')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'NOTICE.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repo 'licenses') -Destination (Join-Path $stage 'licenses') -Recurse

$packageName = "Glebosaurus-$Version-$Runtime.tar.gz"
$package = Join-Path $OutputDir $packageName
if (Test-Path -LiteralPath $package) { Remove-Item -LiteralPath $package -Force }
$file = [IO.File]::Create($package)
try {
    $gzip = New-Object IO.Compression.GZipStream($file, [IO.Compression.CompressionLevel]::Optimal)
    try { [System.Formats.Tar.TarFile]::CreateFromDirectory($stage, $gzip, $false) }
    finally { $gzip.Dispose() }
}
finally { $file.Dispose() }
Remove-Item -LiteralPath $stage -Recurse -Force

# Skrypt instalacji z końcami linii LF (bash nie znosi CR) – niezależnie od tego, jak git zapisał go na dysku.
$installer = Join-Path $OutputDir 'install-linux.sh'
$script = [IO.File]::ReadAllText((Join-Path $repo 'deploy/install-linux.sh')).Replace("`r`n", "`n")
[IO.File]::WriteAllText($installer, $script, (New-Object Text.UTF8Encoding($false)))

# SHA256SUMS w formacie sha256sum (LF, bez BOM): własne wiersze podmienione, cudze (paczka Windows, APK) zostają.
$sumsPath = Join-Path $OutputDir 'SHA256SUMS'
$mine = @($packageName, 'install-linux.sh')
$lines = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $sumsPath) {
    foreach ($line in [IO.File]::ReadAllLines($sumsPath)) {
        if ($line.Length -gt 66 -and $mine -contains $line.Substring(66)) { continue }
        if ($line.Trim()) { $lines.Add($line) }
    }
}
foreach ($path in @($package, $installer)) {
    $lines.Add(('{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant(), (Split-Path -Leaf $path)))
}
[IO.File]::WriteAllText($sumsPath, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))

Write-Host "Pliki wydania $Version ($Runtime) w ${OutputDir}:"
foreach ($path in @($package, $installer, $sumsPath)) {
    Write-Host ("  {0,-40} {1,8:N1} MB" -f (Split-Path -Leaf $path), ((Get-Item -LiteralPath $path).Length / 1MB))
}
