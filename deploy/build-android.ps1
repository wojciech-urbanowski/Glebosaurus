<#
.SYNOPSIS
  Buduje APK agenta Android (faza 2.2) i opcjonalnie instaluje go na podłączonym telefonie.

.DESCRIPTION
  Wymaga .NET SDK z obciążeniem "android", JDK 17+ i Android SDK. Bez -Toolchain używa dotnet z PATH, a SDK i JDK z
  -AndroidSdk/-JavaSdk albo z domyślnych lokalizacji; z -Toolchain – katalogu z podkatalogami dotnet, jdk, android-sdk
  (instalacja bez administratora). -InstallDependencies dociąga brakujące składniki Android SDK (akceptuje ich licencje) – CI.
  APK jest podpisany kluczem wydań, jeśli jest (domyślnie local/glebosaurus-android.keystore, hasło w pliku obok z
  rozszerzeniem .password – nigdy nie jest wypisywane). Bez klucza – kluczem testowym Android SDK; takiej aplikacji nie da
  się potem zaktualizować APK z wydania (inny podpis), trzeba ją odinstalować.

.EXAMPLE
  ./deploy/build-android.ps1 -Toolchain D:\narzedzia\android-toolchain -Install
#>
[CmdletBinding()]
param(
    [string]$Toolchain = $env:GLEBOSAURUS_ANDROID_TOOLCHAIN,
    [string]$AndroidSdk,
    [string]$JavaSdk,
    [string]$Keystore = (Join-Path $PSScriptRoot '..\local\glebosaurus-android.keystore'),
    [string]$OutputDir = (Join-Path $PSScriptRoot '..\publish\android'),
    # Tworzy nowy klucz wydań (jednorazowo). Jego utrata = aktualizacje tylko przez odinstalowanie i ponowną rejestrację.
    [switch]$CreateKeystore,
    # Dociąga brakujące składniki Android SDK (platforma, narzędzia) i akceptuje ich licencje – w CI.
    [switch]$InstallDependencies,
    # Instaluje APK na jedynym podłączonym telefonie (adb install -r – dane i rejestracja zostają).
    [switch]$Install
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\Glebosaurus.Android\Glebosaurus.Android.csproj'
$version = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

$dotnet = 'dotnet'
$properties = @()
$adb = 'adb'
$keytool = 'keytool'
if ($Toolchain) {
    $dotnet = Join-Path $Toolchain 'dotnet\dotnet.exe'
    $env:DOTNET_ROOT = Join-Path $Toolchain 'dotnet'
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'
    $AndroidSdk = Join-Path $Toolchain 'android-sdk'
    $JavaSdk = Join-Path $Toolchain 'jdk'
}
if ($AndroidSdk) {
    $properties += "-p:AndroidSdkDirectory=$AndroidSdk"
    $adb = Join-Path $AndroidSdk 'platform-tools\adb.exe'
}
if ($JavaSdk) {
    $properties += "-p:JavaSdkDirectory=$JavaSdk"
    $keytool = Join-Path $JavaSdk 'bin\keytool.exe'
}

$passwordFile = "$Keystore.password"
if ($CreateKeystore) {
    if (Test-Path $Keystore) { throw "Klucz $Keystore już istnieje – nie nadpisuję (to jedyny klucz aktualizacji)." }
    New-Item -ItemType Directory -Force (Split-Path -Parent $Keystore) | Out-Null
    $bytes = New-Object byte[] 24
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $password = [Convert]::ToBase64String($bytes) -replace '[+/=]', 'x'
    [System.IO.File]::WriteAllText($passwordFile, $password)
    $env:GLEBOSAURUS_NEW_PASSWORD = $password
    try {
        & $keytool -genkeypair -keystore $Keystore -storetype PKCS12 -alias glebosaurus -keyalg RSA -keysize 4096 -validity 36500 `
            -dname 'CN=Glebosaurus' -storepass:env GLEBOSAURUS_NEW_PASSWORD -keypass:env GLEBOSAURUS_NEW_PASSWORD 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "keytool: kod $LASTEXITCODE" }
    }
    finally { Remove-Item Env:GLEBOSAURUS_NEW_PASSWORD -ErrorAction SilentlyContinue }
    Write-Host "Utworzono klucz wydań $Keystore (hasło w $passwordFile). Zrób kopię obu plików poza komputerem."
}

if (Test-Path $Keystore) {
    if (-not (Test-Path $passwordFile)) { throw "Brak pliku z hasłem klucza: $passwordFile" }
    $env:GLEBOSAURUS_KEYSTORE_PASSWORD = [System.IO.File]::ReadAllText($passwordFile).Trim()
    $properties += "-p:GlebosaurusKeystore=$((Resolve-Path $Keystore).Path)"
    Write-Host "Podpis: klucz wydań $Keystore"
}
else {
    Write-Warning "Brak klucza wydań ($Keystore) – APK podpisany kluczem testowym Android SDK."
}

if ($InstallDependencies) {
    & $dotnet build $project -t:InstallAndroidDependencies -f net10.0-android -p:AcceptAndroidSDKLicenses=True @properties
    if ($LASTEXITCODE -ne 0) { throw "InstallAndroidDependencies: kod $LASTEXITCODE" }
}

try {
    # Budowa przyrostowa nie podpisuje ponownie APK po zmianie klucza – stare pliki APK precz, pakowanie zawsze od nowa.
    $bin = Join-Path $repo 'src\Glebosaurus.Android\bin\Release'
    if (Test-Path $bin) { Get-ChildItem $bin -Recurse -Filter *.apk | Remove-Item -Force }
    & $dotnet publish $project -c Release -f net10.0-android @properties
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish: kod $LASTEXITCODE" }
}
finally {
    Remove-Item Env:GLEBOSAURUS_KEYSTORE_PASSWORD -ErrorAction SilentlyContinue
}

$signed = Get-ChildItem (Join-Path $repo 'src\Glebosaurus.Android\bin\Release\net10.0-android\publish') -Filter '*-Signed.apk' | Select-Object -First 1
if (-not $signed) { throw 'Nie znaleziono podpisanego APK po publikacji.' }
New-Item -ItemType Directory -Force $OutputDir | Out-Null
$apk = Join-Path $OutputDir "Glebosaurus-$version-android.apk"
Copy-Item $signed.FullName $apk -Force
$hash = (Get-FileHash $apk -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "APK: $apk ($([math]::Round((Get-Item $apk).Length / 1MB, 1)) MB, SHA-256 $hash)"

if ($Install) {
    & $adb install -r $apk
    if ($LASTEXITCODE -ne 0) { throw ("adb install: kod $LASTEXITCODE " + '(telefon podłączony, debugowanie USB i „Instaluj przez USB” włączone?)') }
}
