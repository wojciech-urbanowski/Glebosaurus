<#
.SYNOPSIS
    Instaluje albo aktualizuje Glebosaurusa na Windows 11: agenta czujników gleby jako usługę startującą przy zalogowaniu,
    na koncie użytkownika (bez uprawnień administratora).

.DESCRIPTION
    Pobiera wydanie z GitHuba (paczkę samodzielną z .NET – niczego więcej nie trzeba instalować), sprawdza skrót SHA-256,
    rozpakowuje do %LOCALAPPDATA%\Glebosaurus\app i rejestruje zadanie Harmonogramu „Glebosaurus”, które przy każdym
    zalogowaniu (i co godzinę, gdyby usługa nie działała) uruchamia app\glebosaurus-service.exe – bez okna.
    Usługa uruchamia agenta (glebosaurus run), podnosi go po awarii, a przy starcie i codziennie o 03:30 sprawdza nowe
    wydania; nowszą wersję instaluje ten sam skrypt (usługa uruchamia go z parametrem -Unattended).

    Przy pierwszej instalacji w data\ zostaje wzór glebosaurus.config.example.json: skopiuj go jako glebosaurus.config.json,
    wpisz adres projektu Supabase i klucz publikowalny, zarejestruj agenta (glebosaurus enroll) i uruchom usługę
    (Start-ScheduledTask Glebosaurus) – skrypt podaje gotowe polecenia.

    Gdy usługa jest już zainstalowana, skrypt ją aktualizuje: czysto zatrzymuje agenta (czeka na koniec odczytu), podmienia
    app\ (poprzednia wersja zostaje w app.old) i uruchamia usługę ponownie. Dane (konfiguracja agenta, token, kopia
    konfiguracji z chmury, baza, dzienniki, sekrety panelu WWW) leżą w data\ i aktualizacja ich nie dotyka.

    Działa w Windows PowerShell 5.1 i PowerShell 7. Plik musi być w UTF-8 z BOM (polskie znaki w Windows PowerShell).

.EXAMPLE
    $s = "$env:TEMP\glebosaurus-install.ps1"
    Invoke-WebRequest https://github.com/wojciech-urbanowski/Glebosaurus/releases/latest/download/install-windows.ps1 -OutFile $s -UseBasicParsing
    powershell -NoProfile -ExecutionPolicy Bypass -File $s

    Pierwsza instalacja, aktualizacja do najnowszego wydania albo naprawa (ponowna rejestracja zadania i start usługi).

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File $s -Uninstall
    Odinstalowanie (dane w data\ zostają; -RemoveData usuwa także je).
#>
[CmdletBinding()]
param(
    # Wersja do zainstalowania (np. 0.1.0); domyślnie najnowsze wydanie. Starszą wersję (powrót) instaluje się tylko jawnie.
    [string]$Version,
    # Katalog instalacji.
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Glebosaurus'),
    # Lokalna paczka Glebosaurus-<wersja>-win-x64.zip zamiast pobierania (do prób; obok może leżeć SHA256SUMS).
    [string]$PackagePath,
    # Nie uruchamiaj usługi po instalacji (zadanie i tak uruchomi ją przy następnym zalogowaniu).
    [switch]$NoStart,
    # Bez zadania Harmonogramu (tylko pliki – do prób i CI).
    [switch]$NoTask,
    # Zainstaluj ponownie, nawet gdy ta wersja już jest.
    [switch]$Force,
    [switch]$Uninstall,
    # Z -Uninstall: usuń też dane (konfigurację, token, bazę, dzienniki).
    [switch]$RemoveData,
    # Wywołanie z usługi przy samoaktualizacji: dziennik do updates\install-last.log, po porażce powrót starej wersji.
    [switch]$Unattended,
    # Poniższe zapamiętuje install.json (kolejne aktualizacje używają tych samych wartości).
    [string]$TaskName,
    [string]$Repository,
    [string]$ApiBase,
    # Pora codziennego sprawdzania wydań (HH:mm).
    [string]$DailyUpdateCheck,
    [Nullable[bool]]$AutoUpdate
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # pasek postępu Invoke-WebRequest w Windows PowerShell spowalnia pobieranie wielokrotnie
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch { }

# Te same nazwy co w src/Glebosaurus.Core/Hosting/Instances.cs.
$MonitorMutex = 'Local\Glebosaurus.Monitor'
$ServiceMutex = 'Local\Glebosaurus.Service'
$StopEvent = 'Local\Glebosaurus.Stop'
$StopTimeoutSeconds = 300

$InstallDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($InstallDir)
$AppDir = Join-Path $InstallDir 'app'
$OldAppDir = Join-Path $InstallDir 'app.old'
$DataDir = Join-Path $InstallDir 'data'
$UpdatesDir = Join-Path $InstallDir 'updates'
$SettingsPath = Join-Path $InstallDir 'install.json'
$AttemptPath = Join-Path $UpdatesDir 'update-attempt.json'
$ConfigPath = Join-Path $DataDir 'glebosaurus.config.json'
$ServiceExe = Join-Path $AppDir 'glebosaurus-service.exe'
$MonitorExe = Join-Path $AppDir 'glebosaurus.exe'

function Write-Step([string]$Text) { Write-Host "Glebosaurus: $Text" }

# ---- ustawienia instalacji: parametr > install.json > domyślne -------------------------------------------------------

$saved = $null
if (Test-Path -LiteralPath $SettingsPath) {
    try { $saved = Get-Content -LiteralPath $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json }
    catch { Write-Warning "Nieczytelny $SettingsPath ($($_.Exception.Message)) – używam ustawień domyślnych." }
}
$BoundParameters = $PSBoundParameters   # w funkcji $PSBoundParameters to już parametry funkcji
function Get-Setting([string]$Name, $Default) {
    if ($BoundParameters.ContainsKey($Name)) { return $BoundParameters[$Name] }
    if ($saved -and $saved.PSObject.Properties[$Name] -and $null -ne $saved.$Name -and "$($saved.$Name)" -ne '') { return $saved.$Name }
    return $Default
}
$TaskName = Get-Setting 'TaskName' 'Glebosaurus'
$Repository = Get-Setting 'Repository' 'wojciech-urbanowski/Glebosaurus'
$ApiBase = ([string](Get-Setting 'ApiBase' 'https://api.github.com')).TrimEnd('/')
$DailyUpdateCheck = Get-Setting 'DailyUpdateCheck' '03:30'
$AutoUpdate = [bool](Get-Setting 'AutoUpdate' $true)
if ($DailyUpdateCheck -notmatch '^([01]\d|2[0-3]):[0-5]\d$') { throw "DailyUpdateCheck: `„$DailyUpdateCheck`” nie jest godziną HH:mm." }

# ---- pomocnicze ------------------------------------------------------------------------------------------------------

# Czy ktoś trzyma muteks (agent albo usługa) – sprawdza, zajmując go na chwilę.
function Test-Held([string]$Name) {
    $mutex = New-Object System.Threading.Mutex($false, $Name)
    try {
        $acquired = $false
        try { $acquired = $mutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $acquired = $true }
        if ($acquired) { $mutex.ReleaseMutex() }
        return (-not $acquired)
    }
    finally { $mutex.Dispose() }
}

# Usługa i agent uruchomieni z TEGO katalogu app\ (po ścieżce programu). Muteksy są wspólne dla całej sesji, więc same nie
# wystarczą: instalacja próbna w innym katalogu zatrzymałaby prawdziwą usługę (zdarzyło się 2026-11-15), a każda instalacja
# zatrzymywałaby też deweloperskie „run”, choć to nie blokuje żadnego pliku w app\.
function Test-Running {
    $prefix = $AppDir.TrimEnd('\') + '\'
    $own = @(Get-Process -Name glebosaurus, glebosaurus-service -ErrorAction SilentlyContinue | Where-Object {
            $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) })
    return ($own.Count -gt 0)
}

# Czyste zatrzymanie: sygnał „zatrzymaj się” i czekanie, aż agent skończy bieżący odczyt. Nigdy nie zabija procesu –
# zabicie w trakcie rozłączania czujnika potrafi zawiesić następny odczyt.
function Stop-Glebosaurus {
    if (-not (Test-Running)) { return $true }
    $deadline = (Get-Date).AddSeconds($StopTimeoutSeconds)
    $signalled = $false
    while (Test-Running) {
        if (-not $signalled) {
            $handle = $null
            if ([System.Threading.EventWaitHandle]::TryOpenExisting($StopEvent, [ref]$handle)) {
                [void]$handle.Set()
                $handle.Dispose()
                $signalled = $true
                Write-Step 'poproszono działającego agenta o zatrzymanie – czekam na koniec bieżącego odczytu...'
            }
        }
        if ((Get-Date) -ge $deadline) { return $false }
        Start-Sleep -Seconds 2
    }
    Write-Step 'agent i usługa zatrzymani.'
    return $true
}

# Czy zapisy w $Directory trafiają do prywatnej kopii jakiejś aplikacji z pakietu (MSIX)? Tak jest, gdy skrypt działa w
# terminalu wbudowanym w taką aplikację (sprawdzone 2026-11-15 na aplikacji Claude): Windows przekierowuje jej zapisy w
# %LOCALAPPDATA% do %LOCALAPPDATA%\Packages\<pakiet>\LocalCache\Local, a Harmonogram tej kopii nie widzi – usługa nie
# wystartowałaby przy logowaniu (0x80070002). Sprawdzamy objaw wprost: plik próbny i szukanie go w kopiach pakietów.
function Get-RedirectingPackage([string]$Directory) {
    if (-not $Directory -or -not (Test-Path -LiteralPath $Directory -PathType Container)) { return $null }
    $local = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\')
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('\')
    if (-not ($full -eq $local -or $full.StartsWith($local + '\', [StringComparison]::OrdinalIgnoreCase))) { return $null }
    $relative = $full.Substring($local.Length).TrimStart('\')
    $packages = Join-Path $local 'Packages'
    $probe = 'glebosaurus-proba-' + [guid]::NewGuid().ToString('N')
    $path = Join-Path $full $probe
    try {
        [IO.File]::WriteAllText($path, '')
        if (-not (Test-Path -LiteralPath $packages)) { return $null }
        foreach ($package in [IO.Directory]::EnumerateDirectories($packages)) {
            if ([IO.File]::Exists([IO.Path]::Combine($package, 'LocalCache', 'Local', $relative, $probe))) { return (Split-Path -Leaf $package) }
        }
        return $null
    }
    catch { return $null }
    finally { try { [IO.File]::Delete($path) } catch { } }
}

function Invoke-Retry([scriptblock]$Action, [int]$Attempts = 10) {
    for ($i = 1; ; $i++) {
        try { & $Action; return }
        catch { if ($i -ge $Attempts) { throw }; Start-Sleep -Seconds 1 }
    }
}

function Get-InstalledVersion {
    if (-not (Test-Path -LiteralPath $MonitorExe)) { return $null }
    $text = (Get-Item -LiteralPath $MonitorExe).VersionInfo.ProductVersion
    if ($text -match '^(\d+)\.(\d+)\.(\d+)') { return New-Object Version([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) }
    return $null
}

function ConvertTo-ReleaseVersion([string]$Text) {
    if ($Text -match '^[vV]?(\d+)\.(\d+)\.(\d+)$') { return New-Object Version([int]$Matches[1], [int]$Matches[2], [int]$Matches[3]) }
    throw "`„$Text`” nie jest numerem wersji (oczekiwano np. 0.1.0)."
}

function Get-Release {
    $headers = @{ 'User-Agent' = 'Glebosaurus-install'; 'Accept' = 'application/vnd.github+json' }
    if ($Version) { $uri = "$ApiBase/repos/$Repository/releases/tags/v$((ConvertTo-ReleaseVersion $Version).ToString(3))" }
    else { $uri = "$ApiBase/repos/$Repository/releases/latest" }
    try { return Invoke-RestMethod -Uri $uri -Headers $headers -UseBasicParsing }
    catch { throw "Nie udało się pobrać opisu wydania ($uri): $($_.Exception.Message)" }
}

function Save-Url([string]$Url, [string]$Path) {
    try { Invoke-WebRequest -Uri $Url -OutFile $Path -UseBasicParsing -Headers @{ 'User-Agent' = 'Glebosaurus-install' } }
    catch { throw "Nie udało się pobrać $Url : $($_.Exception.Message)" }
}

function Assert-Hash([string]$SumsPath, [string]$File) {
    $name = Split-Path -Leaf $File
    $expected = $null
    foreach ($line in (Get-Content -LiteralPath $SumsPath)) {
        if ($line -match '^([0-9a-fA-F]{64}) [ *](.+)$' -and $Matches[2] -eq $name) { $expected = $Matches[1].ToLowerInvariant(); break }
    }
    if (-not $expected) { throw "SHA256SUMS nie zawiera pliku $name." }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $File).Hash.ToLowerInvariant()
    if ($expected -ne $actual) { throw "Skrót SHA-256 pliku $name się nie zgadza (oczekiwano $expected, jest $actual) – uszkodzone pobranie?" }
}

# Konfiguracja agenta może mieć komentarze // i /* */ oraz przecinki przed } i ] (czyta ją System.Text.Json), a
# ConvertFrom-Json z Windows PowerShell 5.1 ich nie zna – więc najpierw je usuwamy (napisy w cudzysłowach zostają nietknięte).
# Przy błędzie zwraca $null BEZ komunikatu wyjątku: ConvertFrom-Json wkleja do niego cały plik (zasada projektu – plik
# z sekretami nigdy nie trafia na ekran ani do dziennika).
function Read-ConfigJson([string]$Path) {
    try {
        $text = Get-Content -LiteralPath $Path -Raw -Encoding UTF8
        $strings = '("(?:\\.|[^"\\])*")'
        $text = [regex]::Replace($text, $strings + '|//[^\r\n]*|/\*[\s\S]*?\*/',
            [Text.RegularExpressions.MatchEvaluator] { param($m) if ($m.Groups[1].Success) { $m.Value } else { '' } })
        $text = [regex]::Replace($text, $strings + '|,(\s*[}\]])',
            [Text.RegularExpressions.MatchEvaluator] { param($m) if ($m.Groups[1].Success) { $m.Value } else { $m.Groups[2].Value } })
        return ($text | ConvertFrom-Json)
    }
    catch { return $null }
}

function Save-Json($Value, [string]$Path) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    Set-Content -LiteralPath $Path -Value ($Value | ConvertTo-Json) -Encoding UTF8
}

function Install-Package([string]$Extracted) {
    if (Test-Path -LiteralPath $OldAppDir) { Invoke-Retry { Remove-Item -LiteralPath $OldAppDir -Recurse -Force } }
    if (Test-Path -LiteralPath $AppDir) { Invoke-Retry { Rename-Item -LiteralPath $AppDir -NewName (Split-Path -Leaf $OldAppDir) } }
    try { Invoke-Retry { Move-Item -LiteralPath $Extracted -Destination $AppDir } }
    catch {
        if ((Test-Path -LiteralPath $OldAppDir) -and -not (Test-Path -LiteralPath $AppDir)) { Rename-Item -LiteralPath $OldAppDir -NewName (Split-Path -Leaf $AppDir) }
        throw
    }
}

function Register-GlebosaurusTask {
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action = New-ScheduledTaskAction -Execute $ServiceExe -WorkingDirectory $DataDir
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    # Strażnik: co godzinę zadanie próbuje wystartować; gdy usługa działa, nowa kopia jest pomijana (IgnoreNew).
    $trigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Hours 1)).Repetition
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
    # Laptop: także na baterii; bez domyślnego limitu 72 godzin pracy zadania.
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -StartWhenAvailable
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force `
        -Description 'Glebosaurus – agent czujników gleby (usługa bez okna; https://github.com/wojciech-urbanowski/Glebosaurus).' | Out-Null
}

function Start-Glebosaurus {
    if (Test-Running) { return }
    if (Test-Held $ServiceMutex) {
        Write-Warning 'Działa usługa Glebosaurus z innego katalogu instalacji – ta nie wystartuje obok niej (na koncie pracuje jeden agent).'
        return
    }
    if (-not (Test-Path -LiteralPath $ServiceExe)) { Write-Warning "Brak $ServiceExe – nie ma czego uruchomić."; return }
    if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) { Start-ScheduledTask -TaskName $TaskName }
    for ($i = 0; $i -lt 40 -and -not (Test-Held $ServiceMutex); $i++) { Start-Sleep -Milliseconds 500 }
    if (-not (Test-Held $ServiceMutex)) {
        Write-Step 'zadanie Harmonogramu nie uruchomiło usługi – uruchamiam ją bezpośrednio.'
        Start-Process -FilePath $ServiceExe -WorkingDirectory $DataDir
        for ($i = 0; $i -lt 40 -and -not (Test-Held $ServiceMutex); $i++) { Start-Sleep -Milliseconds 500 }
    }
    if (Test-Running) { Write-Step 'usługa działa.' }
    else { Write-Warning "Usługa nie wystartowała – zajrzyj do $(Join-Path $DataDir 'logs')." }
}

function Remove-Leftovers {
    if (-not (Test-Path -LiteralPath $UpdatesDir)) { return }
    Get-ChildItem -LiteralPath $UpdatesDir | Where-Object {
        $_.Name -ne 'install-last.log' -and $_.FullName -ne $PSCommandPath -and $_.Name -ne 'update-attempt.json'
    } | ForEach-Object { try { Remove-Item -LiteralPath $_.FullName -Recurse -Force } catch { } }
}

# Czy agent ma token z rejestracji (enroll). Plik tokenu leży obok konfiguracji, chyba że cloud.tokenFile mówi inaczej;
# nieczytelna konfiguracja – nie zgadujemy i traktujemy agenta jak zarejestrowanego (sam „run” powie, czego brakuje).
function Test-Enrolled {
    $token = 'glebosaurus-cloud.token'
    $config = Read-ConfigJson $ConfigPath
    if (-not $config) { return $true }
    if ($config.PSObject.Properties['cloud'] -and $config.cloud -and $config.cloud.PSObject.Properties['tokenFile'] -and $config.cloud.tokenFile) {
        $token = [string]$config.cloud.tokenFile
    }
    if (-not [IO.Path]::IsPathRooted($token)) { $token = Join-Path $DataDir $token }
    return (Test-Path -LiteralPath $token)
}

function Write-Summary($Installed) {
    if (-not $Installed) { return }
    $panel = $null
    $config = Read-ConfigJson $ConfigPath
    if ($config -and $config.PSObject.Properties['web'] -and $config.web) {
        $port = 8470
        if ($config.web.PSObject.Properties['port'] -and $config.web.port) { $port = [int]$config.web.port }
        $panel = "https://$($env:COMPUTERNAME.ToLowerInvariant()):$port"
    }
    Write-Host ''
    Write-Host "Glebosaurus $($Installed.ToString(3)) w $InstallDir"
    Write-Host "  program:      $AppDir"
    Write-Host "  dane:         $DataDir  (dzienniki: $(Join-Path $DataDir 'logs'))"
    if (-not $NoTask) { Write-Host "  usługa:       zadanie Harmonogramu `„$TaskName`” – przy zalogowaniu, bez okna; co godzinę wraca, jeśli nie działa" }
    if ($AutoUpdate) { Write-Host "  aktualizacje: przy starcie usługi i codziennie o $DailyUpdateCheck ($Repository)" }
    else { Write-Host '  aktualizacje: wyłączone (install.json)' }
    Write-Host "  zatrzymanie:  & '$MonitorExe' stop      (wróci przy logowaniu albo: Start-ScheduledTask $TaskName)"
    if ($panel) {
        Write-Host "  panel WWW:    $panel  – przy pierwszym starcie Windows może zapytać o Zaporę dla $MonitorExe"
        Write-Host '                (zezwól w sieci prywatnej; ścieżka programu jest stała, więc aktualizacje nie pytają ponownie)'
    }
}

# ---- odinstalowanie --------------------------------------------------------------------------------------------------

function Invoke-Uninstall {
    if (-not $NoTask -and (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue)) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Step "usunięto zadanie Harmonogramu `„$TaskName`”."
    }
    if (-not (Stop-Glebosaurus)) { throw "Agent nie zatrzymał się w $StopTimeoutSeconds s – spróbuj ponownie za chwilę." }
    foreach ($path in @($AppDir, $OldAppDir, $UpdatesDir, $SettingsPath)) {
        if (Test-Path -LiteralPath $path) { Invoke-Retry { Remove-Item -LiteralPath $path -Recurse -Force } }
    }
    if ($RemoveData) {
        if (Test-Path -LiteralPath $DataDir) { Invoke-Retry { Remove-Item -LiteralPath $DataDir -Recurse -Force } }
        if ((Test-Path -LiteralPath $InstallDir) -and -not (Get-ChildItem -LiteralPath $InstallDir -Force)) { Remove-Item -LiteralPath $InstallDir -Force }
        Write-Step 'odinstalowano razem z danymi.'
    }
    else {
        Write-Step "odinstalowano; dane zostały w $DataDir (usuń je ręcznie albo uruchom z -Uninstall -RemoveData)."
    }
}

# ---- instalacja i aktualizacja ---------------------------------------------------------------------------------------

$script:TargetVersion = $null
$script:TaskDisabled = $false
$script:Stopped = $false

function Invoke-Install {
    New-Item -ItemType Directory -Force -Path $InstallDir, $DataDir, $UpdatesDir | Out-Null
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    # 1. Która wersja i skąd.
    $packageUrl = $null
    $sumsUrl = $null
    if ($PackagePath) {
        $PackagePath = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
        if ((Split-Path -Leaf $PackagePath) -notmatch '^Glebosaurus-(\d+\.\d+\.\d+)-win-x64\.zip$') { throw "Nazwa paczki $PackagePath nie ma postaci Glebosaurus-<wersja>-win-x64.zip." }
        $script:TargetVersion = ConvertTo-ReleaseVersion $Matches[1]
    }
    else {
        $release = Get-Release
        $script:TargetVersion = ConvertTo-ReleaseVersion ([string]$release.tag_name)
        $packageName = "Glebosaurus-$($script:TargetVersion.ToString(3))-win-x64.zip"
        foreach ($asset in @($release.assets)) {
            if ($asset.name -eq $packageName) { $packageUrl = $asset.browser_download_url }
            if ($asset.name -eq 'SHA256SUMS') { $sumsUrl = $asset.browser_download_url }
        }
        if (-not $packageUrl -or -not $sumsUrl) { throw "Wydanie $($release.tag_name) nie ma pliku $packageName albo SHA256SUMS." }
    }
    $target = $script:TargetVersion
    $installed = Get-InstalledVersion

    $upToDate = $false
    if ($installed -and -not $Force) {
        if ($installed -eq $target) { Write-Step "zainstalowana wersja $($installed.ToString(3)) jest aktualna."; $upToDate = $true }
        elseif ($installed -gt $target -and -not $Version) {
            Write-Step "zainstalowana wersja $($installed.ToString(3)) jest nowsza niż wydanie $($target.ToString(3)) – nic nie zmieniam."
            $upToDate = $true
        }
    }

    # 2. Pobranie, sprawdzenie i podmiana programu.
    if (-not $upToDate) {
        $work = Join-Path $UpdatesDir $target.ToString(3)
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $work | Out-Null
        if ($PackagePath) {
            $zip = $PackagePath
            $sums = Join-Path (Split-Path -Parent $PackagePath) 'SHA256SUMS'
            if (Test-Path -LiteralPath $sums) { Assert-Hash $sums $zip; Write-Step 'skrót SHA-256 paczki się zgadza.' }
            else { Write-Warning 'Obok paczki nie ma SHA256SUMS – instaluję bez sprawdzenia skrótu.' }
        }
        else {
            $zip = Join-Path $work (Split-Path -Leaf ([Uri]$packageUrl).AbsolutePath)
            $sums = Join-Path $work 'SHA256SUMS'
            Write-Step "pobieram wersję $($target.ToString(3))..."
            Save-Url $sumsUrl $sums
            Save-Url $packageUrl $zip
            Assert-Hash $sums $zip
            Write-Step 'skrót SHA-256 paczki się zgadza.'
        }
        $extracted = Join-Path $work 'app'
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extracted)
        foreach ($required in @('glebosaurus.exe', 'glebosaurus-service.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path $extracted $required))) { throw "Paczka nie zawiera $required." }
        }

        $task = $null
        if (-not $NoTask) { $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue }
        if ($task) { Disable-ScheduledTask -TaskName $TaskName | Out-Null; $script:TaskDisabled = $true }   # strażnik nie wystartuje w trakcie
        if (Test-Running) { $script:Stopped = $true }
        if (-not (Stop-Glebosaurus)) { throw "Agent nie zatrzymał się w $StopTimeoutSeconds s – aktualizację przerwano, nic nie zmieniono." }
        Install-Package $extracted
        if ($installed) { Write-Step "zaktualizowano $($installed.ToString(3)) → $($target.ToString(3)) (poprzednia wersja w $OldAppDir)." }
        else { Write-Step "zainstalowano wersję $($target.ToString(3)) w $AppDir." }
    }

    # 3. Dane.
    New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    $hasConfig = Test-Path -LiteralPath $ConfigPath
    if (-not $hasConfig) {
        $example = Join-Path $AppDir 'glebosaurus.config.example.json'
        if (Test-Path -LiteralPath $example) { Copy-Item -LiteralPath $example -Destination (Join-Path $DataDir 'glebosaurus.config.example.json') -Force }
    }

    # 4. Ustawienia i zadanie Harmonogramu.
    Save-Json ([ordered]@{ repository = $Repository; apiBase = $ApiBase; taskName = $TaskName; autoUpdate = $AutoUpdate; dailyUpdateCheck = $DailyUpdateCheck }) $SettingsPath
    if (-not $NoTask) {
        Register-GlebosaurusTask
        $script:TaskDisabled = $false
        Write-Step "zadanie Harmonogramu `„$TaskName`” zarejestrowane (przy zalogowaniu, na Twoim koncie, bez administratora)."
    }
    if (Test-Path -LiteralPath $AttemptPath) { Remove-Item -LiteralPath $AttemptPath -Force }
    Remove-Leftovers

    # 5. Start.
    $enrolled = $hasConfig -and (Test-Enrolled)
    if (-not $hasConfig) {
        Write-Warning ("Brak konfiguracji $ConfigPath – usługa nie zostanie uruchomiona. Skopiuj wzór " +
            "$(Join-Path $DataDir 'glebosaurus.config.example.json') jako glebosaurus.config.json, wpisz adres projektu i klucz publikowalny, " +
            "potem: & '$MonitorExe' enroll --config '$ConfigPath' (kod od właściciela chmury) i Start-ScheduledTask $TaskName.")
    }
    elseif (-not $enrolled -and -not $Unattended) {
        Write-Warning ("Agent nie jest jeszcze zarejestrowany w chmurze – usługa nie zostanie uruchomiona. " +
            "Zarejestruj go: & '$MonitorExe' enroll --config '$ConfigPath' (kod od właściciela chmury), potem: Start-ScheduledTask $TaskName.")
    }
    elseif ($NoTask -and -not $Unattended) { }
    elseif ($NoStart -and -not $Unattended -and -not $script:Stopped) { Write-Step 'usługa nie została uruchomiona (-NoStart); wystartuje przy następnym zalogowaniu.' }
    else { Start-Glebosaurus }

    Write-Summary (Get-InstalledVersion)
}

# ---- przebieg --------------------------------------------------------------------------------------------------------

if ($Unattended) {
    New-Item -ItemType Directory -Force -Path $UpdatesDir | Out-Null
    try { Start-Transcript -Path (Join-Path $UpdatesDir 'install-last.log') -Force | Out-Null } catch { }
    if ($Version) { $script:TargetVersion = ConvertTo-ReleaseVersion $Version }
    # Usługa, która uruchomiła skrypt, właśnie się kończy – jej pliki zwolnią się za chwilę.
    for ($i = 0; $i -lt 60 -and (Test-Running); $i++) { Start-Sleep -Milliseconds 500 }
}

$succeeded = $false
try {
    $package = Get-RedirectingPackage (Split-Path -Parent $InstallDir)
    if ($package) {
        throw ("Ten PowerShell działa wewnątrz aplikacji z pakietu Windows ($package), np. w terminalu wbudowanym w aplikację. " +
            'Windows przekierowuje jej zapisy w %LOCALAPPDATA% do prywatnej kopii pakietu, której Harmonogram zadań nie widzi, ' +
            'więc usługa nie wystartowałaby przy logowaniu. Uruchom skrypt w zwykłym oknie PowerShell (menu Start: Windows PowerShell albo Terminal).')
    }
    if ($Uninstall) { Invoke-Uninstall } else { Invoke-Install }
    $succeeded = $true
}
catch {
    Write-Host "Glebosaurus: BŁĄD: $($_.Exception.Message)" -ForegroundColor Red
    if ($Unattended -and $script:TargetVersion) {
        try { Save-Json ([ordered]@{ version = $script:TargetVersion.ToString(3); at = [DateTimeOffset]::UtcNow.ToString('o'); reason = $_.Exception.Message }) $AttemptPath } catch { }
    }
    throw
}
finally {
    if ($script:TaskDisabled) { try { Enable-ScheduledTask -TaskName $TaskName | Out-Null } catch { } }
    # Po porażce przywróć pracę monitora (w wersji, która jest w app\), jeśli go zatrzymaliśmy albo uruchomiła nas usługa.
    if (-not $succeeded -and -not $Uninstall -and ($Unattended -or $script:Stopped)) {
        try { Start-Glebosaurus } catch { Write-Warning "Nie udało się uruchomić usługi ponownie: $($_.Exception.Message)" }
    }
    if ($Unattended) { try { Stop-Transcript | Out-Null } catch { } }
}
