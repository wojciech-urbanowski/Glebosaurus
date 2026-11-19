# Glebosaurus – kontekst dla pracy nad projektem

Bezobsługowy monitor czujników gleby Tuya BLE (ATLO SMS1) na .NET 10: agenci na Windows 11, Androidzie i Raspberry Pi OS
czytają czujniki i wysyłają odczyty do chmury Supabase (darmowy plan); jeden z agentów (nadawca z blokadą) wysyła alerty
„czas podlać” i raporty mailem przez Gmail. Tryb samodzielny (bez chmury) usunięto 2026-11-19 – nie przywracaj go.

Przeczytaj najpierw: [README.md](README.md) (użycie), [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md) (projekt, zasady 1–15 –
kod odwołuje się do ich numerów, i fakty o czujniku), [docs/ROADMAP.md](docs/ROADMAP.md) (stan i kolejne fazy),
[docs/HISTORIA.md](docs/HISTORIA.md) (dziennik prób na sprzęcie z datami, wdrożenie autora).

## Komendy

```powershell
dotnet build Glebosaurus.slnx -c Release
dotnet test --solution Glebosaurus.slnx -c Release        # xunit.v3 na Microsoft Testing Platform (global.json)
$env:GLEBOSAURUS_CLOUD_TESTS='1'; dotnet test --project tests/Glebosaurus.Cloud.Tests -c Release   # SQL na PRÓBNYM Supabase (czyści go)
dotnet run --project src/Glebosaurus.Agent -f net10.0-windows10.0.19041.0 -- --help   # Windows; Linux: -f net10.0
# Agent: plik z sekcją "cloud" (wzór examples/glebosaurus.config.json), potem enroll --code KOD i run
# Właściciel: cloud migrate|config|settings|agent|backup --admin local/cloud-admin.test.json (bez --confirm tylko plan; prawdziwy projekt – tylko na prośbę)
# cloud config --file PLIK – plik konfiguracji chmury (wzór examples/cloud-config.json: czujniki, progi, raporty, poczta)
# cloud settings --read-interval MIN – sam odstęp odczytów w chmurze (admin_settings_update), bez pełnego pliku
# Kopia chmury: cloud backup --admin local/cloud-admin.test.json --out <scratchpad> (tylko czyta bazę; plik ZIP z odszyfrowanymi sekretami)
dotnet publish src/Glebosaurus.Agent -c Release -f net10.0 -r linux-arm64 --self-contained false -o publish/linux-arm64
./deploy/package-windows.ps1                               # pliki wydania Windows (zip samodzielny, install-windows.ps1, SHA256SUMS) w publish/release
./deploy/build-android.ps1 -Toolchain <katalog> [-Install]   # APK agenta Android (poza slnx), podpis kluczem z local/, -Install = adb; katalog w pamięci Claude
# Kod QR rejestracji telefonu: cloud agent --admin … --name NAZWA --confirm --qr <scratchpad>\kod.png (zawiera kod rejestracji)
```

## Mapa

- `src/Glebosaurus.Tuya` – protokół Tuya BLE 3.x (ramki, AES, DP), sesja (`TuyaSession`), lokalne parowanie, poświadczenia,
  profile czujników. **Nie zna biblioteki BLE** – rozmawia przez `IGattLink`.
- `src/Glebosaurus.Core` – konfiguracja (`AgentConfig` + `AgentConfigLoader` – plik agenta; `CloudSetup` +
  `CloudSetupLoader` – plik konfiguracji chmury właściciela), reguły alertów (`AlertEvaluator`, `SilenceGrace`),
  harmonogram raportów, szablony maili (`MailTemplates`, `MailChart`), dane do wykresów (`Core/Charts`), interfejsy brzegowe
  (`IMonitorStore`, `ISensorReader`/`ISensorRadio`/`ISensorListener`, `IMailGateway`, `ILogArchive`; pliki dzienne w
  `DailyLogFiles`). Kolejność radia „nasłuch → odczyt znalezionego → nasłuch po resztę” to `ListenThenReadSensorReader`.
  `Core/Updates` (wersje, opis wydania, `SHA256SUMS`, `UpdatePolicy`, harmonogram, `install.json`, ślad próby) i
  `Core/Hosting` (muteksy jednej instancji, zdarzenie zatrzymania – nazwy wpisane też w skrypt, kody wyjścia,
  `RestartBackoff`) – logika usługi Windows z testami.
- **Agent w Core** (`Core/Cloud`, ARCHITEKTURA.md, zasada 14): protokół 1:1 z funkcjami SQL i `ICloudApi`, `LeaseLedger`
  (ważność od wysłania prośby, zegar monotoniczny), `SensorLeasePolicy` i `NotifierLockPolicy`, `CloudSync` (pętla
  `agent_sync`, bufor `IReadingOutbox` = kolumna `sync_state` w SQLite; `user_version` = 1 – nie podnoś bez potrzeby),
  `CloudConfigSource` (`get_config` → `AgentConfig` przez `CloudConfigMapper`; ostatnia dobra kopia z kluczami Tuya w
  `CloudConfigCache`, chroniona DPAPI / plikiem 600 – to sekret, nie wypisuj jej), `CollectorEngine` (odczyt tylko z
  dzierżawą z zapasem, nasłuch wszystkich czujników co 3 min, polecenia `read_now`/`release`/`send_log`, dziennik dnia
  poprzedniego do chmury), `NotifierEngine` (tylko z blokadą i hasłem na tę epokę; każdy mail: `mail_claim` → SMTP →
  `mail_sent`, pewna porażka SMTP oddaje rezerwację, niepewna nie – najwyżej raz; alerty, raporty, log administratora po
  jednym mailu na agenta, „agent milczy”, strażnik healthchecks.io), `CloudPanel` (status lokalnego panelu z konfiguracji,
  migawki zbieracza, obrazu ostatniej synchronizacji i alertów z chmury; odczyt/raport na żądanie jako `command_create`;
  wspólna część statusu – `MonitorStatusBuilder`, pole po polu, bez sekretów) i `CloudReadingMirror` (kopia odczytów
  wszystkich agentów przez `readings_feed`, tabela `cloud_feed`, `sync_state` = 1). `CloudRuntime` spina części.
- `src/Glebosaurus.Data` (SQLite: odczyty, bufor, kopia; stare bazy mogą mieć nieużywane tabele `alerts`/`report_runs`),
  `src/Glebosaurus.Mail` (Gmail/MailKit), `src/Glebosaurus.Cloud` (`SupabaseCloudApi`, sam HttpClient – także dla
  Androida; ochrona tokenu i kopii konfiguracji; `HealthchecksHeartbeat`).
- `src/Glebosaurus.Agent` – CLI (Shiny.BluetoothLE): `run` (`CloudAgent.RunAsync`), `enroll`, `stop`, `web-password`, `pair`
  (klucz obok pliku konfiguracji chmury, `--file`), `mail-test --file`, `mail-preview`, `cloud …` (`CloudAdminCommands`),
  diagnostyka `scan`/`read`/`listen`; radio Shiny to tylko `ISensorRadio` (`ShinySensorRadio`).
- `src/Glebosaurus.Cloud.Admin` – połączenie z bazą (Npgsql, pooler sesji, zawsze `VerifyFull` z wbudowanym certyfikatem
  głównym Supabase `supabase/prod-ca-2021.crt`, ważnym do 2031-04-26), `MigrationRunner` z migracjami wbudowanymi,
  `CloudSetupWriter` (plan i zapis pliku konfiguracji chmury), `CloudAgents`, `CloudSettingsAdmin` i `CloudBackup` (zrzut
  bez `pg_dump`: tabele CSV z jednej migawki + odszyfrowane sekrety z Vault, ZIP; `DailyCloudBackup` – raz dziennie w `run`
  z sekcją `cloud.backup`).
- `src/Glebosaurus.Service` – usługa Windows bez okna (`glebosaurus-service.exe`, zadanie Harmonogramu przy zalogowaniu):
  `Supervisor` uruchamia `glebosaurus run` (CreateNoWindow), podnosi go po awarii, sprawdza wydania GitHuba przy starcie i
  codziennie o 03:30 i przekazuje aktualizację skryptowi `deploy/install-windows.ps1` (ten sam instaluje od zera; bez
  konfiguracji albo bez tokenu agenta usługi nie startuje). Wydania: `deploy/package-windows.ps1` +
  `.github/workflows/release.yml` (tag `v*`); CI: `ci.yml`.
- `src/Glebosaurus.Web` – lokalny panel diagnostyczny w procesie `run` (Kestrel, HTTPS z certyfikatem z podpisem własnym,
  tylko sieć lokalna, hasło; **pulpit** `/` z wykresem ostatnich 3 dób przy każdym czujniku, **strona czujnika**
  `/sensor?id=` z przeglądalnym wykresem całej historii – SVG w `wwwroot/charts.js`, bez bibliotek, dane z
  `/api/sensors/{id}/series` uśredniane w Core przez `ReadingSeriesBuilder`; akcje przez chmurę; konfiguracji nie zmienia).
  Strona: `wwwroot/` wbudowane w bibliotekę, dane wstawiane tylko jako tekst (CSP bez skryptów inline).
- `supabase/migrations/` – schemat i funkcje SQL chmury (ARCHITEKTURA.md, zasada 13): tabele w prywatnym schemacie
  `glebosaurus` z RLS, agenci wołają tylko funkcje `public.*` (rola `anon`), administracja (`glebosaurus.admin_*`) tylko
  przez połączenie z bazą. **Zastosowanej migracji nie zmieniaj** – zmiana schematu to nowa migracja (suma kontrolna).
  Zmiana funkcji SQL = zmiana typów protokołu i testów `CloudProtocolTests`/`CloudApiClientTests`.
- `src/Glebosaurus.Android` – agent na Androida (ARCHITEKTURA.md, zasada 12), **poza `Glebosaurus.slnx`** (obciążenie
  `android`; narzędzia bez administratora w jednym katalogu poza repozytorium – dotnet z obciążeniem, jdk, android-sdk z
  adb; ścieżka w pamięci Claude). Tylko zbieracz: `AgentHost` składa `CloudRuntime` bez nadawcy (`notifier: null`,
  `NotifierLockOptions.CollectorOnly`) z kopią odczytów; radio wprost na API Androida (`AndroidRadio` – skan zawsze z
  filtrem po MAC, `ScanStartLimiter` z Core; `GattConnection` – LE, ponowienia 133, zawsze `close()`), usługa
  pierwszoplanowa `connectedDevice` + blokada uśpienia, start po `BOOT_COMPLETED`/`MY_PACKAGE_REPLACED`, sekrety przez
  `KeystoreProtector`, ekran z `AgentScreenText` (Core), rejestracja `EnrollmentLink` z kodu QR. Logika i testy w Core – w
  projekcie Android tylko klej z API systemu. Nazwa `Android` w przestrzeni `Glebosaurus.Android` zasłania globalną – w
  kodzie `global::Android…`. Po zmianach w Core zbuduj też APK (`build-android.ps1` bez `-Install`).
- `tests/` – protokół i sesja na atrapie czujnika; Core na atrapach z ręcznie sterowanym czasem; `ExamplesTests` pilnuje,
  że wzory z `examples/` się wczytują; `Glebosaurus.Cloud.Tests` – funkcje SQL na próbnym projekcie (zegar przesuwany
  `glebosaurus.test_now`, tylko w bazie ze znacznikiem próbnej).
- `examples/` – wzory: `glebosaurus.config.json` (plik agenta), `cloud-config.json` (plik konfiguracji chmury),
  `cloud-admin.json` (plik administratora).
- `local/` – **poza repozytorium** (`.gitignore`): pliki właściciela i próby (pliki administratora, konfiguracja chmury,
  klucze czujników, klucz wydań Androida, robocze bazy i dzienniki).

## Konwencje

- Komunikaty, komentarze, dokumentacja i maile po polsku; identyfikatory w kodzie po angielsku.
- Licencja AGPL-3.0-or-later (`LICENSE`; nota i dodatkowe zezwolenie z §7 na Google Play services w APK – `NOTICE.md`).
  Kod obcy opisują `THIRD-PARTY-NOTICES.md` i `licenses/` – nowa zależność albo zapożyczony kod musi mieć licencję zgodną
  z AGPL (MIT, Apache-2.0, BSD, PostgreSQL itp.); niezgodną da się dodać tylko z nowym zezwoleniem w `NOTICE.md`.
- **NuGet: zawsze najnowsze stabilne wersje** (bez beta/preview/rc); wersję sprawdzać w API NuGet, nie z pamięci.
- `TreatWarningsAsErrors`; `TargetFramework` ustawiany per projekt; Agent ma dwa TFM (Windows: WinRT, Linux: BlueZ).
- Czas przez `TimeProvider`, radio/baza/poczta/chmura przez interfejsy – dzięki temu silniki testują się bez sprzętu.
- Każda zmiana zachowania dostaje test; sprawdzenia na sprzęcie i na żywo dopisuje się z datą do docs/HISTORIA.md, a
  ARCHITEKTURA.md opisuje stan obecny (bez dziennika).
- Wersja (obecnie 0.2.2) jest w jednym miejscu: `Directory.Build.props`; widać ją w stopce każdego maila (`AppInfo.Version`).
  Podbijaj ją tylko na prośbę użytkownika (przy wydaniu paczki). Wydanie = tag `v<wersja>` (musi zgadzać się z
  `Directory.Build.props`) → Actions publikuje je, a zainstalowane usługi same się zaktualizują w ciągu doby.
- Skrypty `.ps1` zapisuj w UTF-8 **z BOM** (Windows PowerShell 5.1) i nie wstawiaj „” do napisów w podwójnych cudzysłowach –
  PowerShell traktuje je jak zwykły cudzysłów; tam poprzedzaj je odwrotnym apostrofem albo użyj napisu w pojedynczych
  cudzysłowach. Konfigurację w skrypcie czytaj tylko przez `Read-ConfigJson` (komentarze `//`): komunikat wyjątku
  `ConvertFrom-Json` z Windows PowerShell 5.1 zawiera **cały plik** – nigdy go nie wypisuj ani nie loguj.

## Sprzęt – zasady pracy na żywo (kosztowne odkrycia)

- Czujnik po włożeniu baterii nadaje jako niesparowany **tylko ok. 20 s**. Zawsze najpierw uruchom skan/`pair` w tle,
  dopiero potem poproś użytkownika o baterie – nigdy odwrotnie.
- Sparowany czujnik reklamuje się rzadko (ok. 1 pakiet na kilka–kilkanaście sekund); dane usługi i producenta przychodzą w
  osobnych pakietach. Dopasowanie: po MAC, zapasowo po UUID Tuya.
- Czujnik przyjmuje jedno połączenie naraz. Odczyt = kilkanaście sekund połączenia i kosztuje baterię. Czujnik z dużą
  zaległą historią (np. tygodnie w Smart Life) oddaje ją na raty – ok. 110 rekordów na 180-sekundową sesję; sesja urwana
  limitem bez raportu stanu nie ma stanu bieżącego (`MapSession`). **Przy DP14 = 0 (niska bateria) nie rób ręcznych
  odczytów** – czujnik potrafi po takiej sesji zamilknąć.
- Czujniki zostawione w Smart Life czytamy kluczem z chmury Tuya (bez resetu); MAC ustala `scan` po UUID Tuya z reklamy.
  Nasze odczyty kasują ich rekordy w czujniku (Smart Life traci historię).
- **Nie zabijaj procesu w trakcie rozłączania** (następny odczyt może zawisnąć). Do prób: `run --minutes N`.
- W zasięgu bywają inne urządzenia Tuya (DC:23:51:*): część to czujniki użytkownika wciąż sparowane ze Smart Life, reszta
  może być cudza. Łącz się tylko z czujnikami z konfiguracji; `pair` wybiera tylko niesparowany czujnik znanego typu (czyli
  po resecie przyciskiem – ten usuwa czujnik ze Smart Life) i przy wielu kandydatach wymaga `--mac`. RSSI ze Shiny ma −127
  („brak wartości”) w ok. 1/3 pakietów – `SignalStats` liczy tylko ważne próbki.
- Shiny na Windows potrafi przerwać skan `COMException 0x80004005` – nasłuch jest wtedy wznawiany
  (`ListenThenReadSensorReader`, najwyżej 3 razy z rzędu), a cykl idzie dalej (ARCHITEKTURA.md, zasada 8).
- Wymiana baterii nie kasuje parowania; kasuje je dopiero reset przyciskiem (wtedy `pair --id <to samo id>`).
- Wskazanie baterii (DP15) to szacunek z napięcia – zaniżany pod obciążeniem radia; DP14 to stan baterii (0 niski / 1 średni /
  2 wysoki, „niski” od ok. 20%); wilgotność trzeba kalibrować.
- Protokół zgadza się bajt w bajt z roquerodrigo/tuya-ble-sdk (ha-tuya-ble, ten sam `gvygg3m8`) – ARCHITEKTURA.md, koniec
  sekcji o czujniku.

## Czego nie robić bez zgody użytkownika

- `pair` zmienia stan czujnika (wymaga `--confirm-bind` i fizycznej obecności użytkownika przy baterii).
- `mail-test` (też z `--samples`) i prawdziwy `run` nadawcy wysyłają prawdziwe maile; w panelu WWW prawdziwy mail wysyła
  też „Wyślij raport teraz” (`POST /api/report`). Wyglądu maili sprawdzaj przez `mail-preview` (tylko pliki) – maile są
  projektowane pod wąski ekran telefonu, nie psuj tego (testy pilnują układu).
- Nie drukuj i nie loguj sekretów i nie commituj `local/`. Sekretami są: pliki `*.credentials.json` (klucze czujników),
  hasło aplikacji Gmail (w pliku konfiguracji chmury), `glebosaurus-web.password` (skrót hasła panelu),
  `glebosaurus-web.pfx` (klucz certyfikatu panelu), pliki administratora (`local/cloud-admin.json`,
  `local/cloud-admin.test.json` – hasło bazy, adres sygnału healthchecks.io), kopie chmury `glebosaurus-backup-*.zip`
  (odszyfrowane klucze Tuya i hasło Gmaila), `glebosaurus-cloud.token` (token agenta) i `glebosaurus-cloud-config.bin`
  (kopia konfiguracji z kluczami Tuya) obok konfiguracji agenta, jednorazowe kody rejestracji (i pliki PNG z kodem QR z
  `cloud agent --qr`) oraz klucz wydań Androida `local/glebosaurus-android.keystore` z hasłem w `….keystore.password` –
  jedyny klucz aktualizacji APK: nie wypisuj hasła, nie twórz nowego klucza (`-CreateKeystore` odmawia, gdy plik jest),
  sekrety repozytorium tylko na prośbę. Skrypty czytają te pliki bez wypisywania wartości. Adresów projektów Supabase nie
  wpisuj do repozytorium, bo jest publiczne.
- Migracje i testy SQL tylko na próbnym projekcie (`local/cloud-admin.test.json`). Prawdziwy projekt
  (`local/cloud-admin.json`) zmienia się wyłącznie na prośbę użytkownika – także migracja „gotowa i sprawdzona”,
  `cloud config --confirm`, `cloud settings --confirm` i `cloud agent --confirm`. Do prób `cloud config` nie używaj
  prawdziwego pliku konfiguracji chmury (ma prawdziwe hasło Gmaila i klucze Tuya) – zmyślony plik w scratchpadzie.
- **Prawdziwą usługę Windows instaluje/aktualizuje/przełącza użytkownik** w zwykłym oknie PowerShell. Procesy narzędzi Claude
  bywają w pakiecie MSIX (zapisy w `%LOCALAPPDATA%` poza `Temp` trafiają wtedy do `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local`,
  której Harmonogram nie widzi – `install-windows.ps1` przerywa z wyjaśnieniem), a bywają poza nim – nie zakładaj ani
  jednego, ani drugiego. Próby w scratchpadzie (Temp) działają normalnie: katalog tymczasowy z `-TaskName Glebosaurus-test`
  i udawanym API (`-ApiBase`); na jednym koncie działa jeden agent, więc próbne instalacje nie mogą ruszyć działającej
  usługi. Nie używaj `Stop-ScheduledTask` ani zabijania procesu na działającej usłudze – zatrzymuje się `glebosaurus stop`.
- Tag `v*` wypchnięty na GitHuba publikuje wydanie, które zainstalowane usługi pobiorą same – tylko na prośbę użytkownika.
- `run` z sekcją `web` słucha na wszystkich interfejsach – na Windows pierwsze uruchomienie może pokazać okno Zapory; reguły
  zapory i zaufanie do certyfikatu zmienia użytkownik, nie Claude. Panel do prób: program podglądu na loopbacku albo testy.
- Agent na telefonie: nie przerywaj procesu (instalacja, `am force-stop`) w trakcie odczytu – sprawdź dziennik
  (`adb logcat -s Glebosaurus`); `build-android.ps1 -Install` aktualizuje APK bez utraty rejestracji.

## Stan

Co działa, co zostało i w jakiej kolejności – [docs/ROADMAP.md](docs/ROADMAP.md), „Stan w skrócie”. Wdrożenie autora (która
maszyna jest prawdziwym nadawcą, telefon, czujniki, prawdziwy projekt Supabase) – [docs/HISTORIA.md](docs/HISTORIA.md),
„Wdrożenie autora”, i pamięć Claude. Zatwierdzone przez użytkownika – nie zmieniaj bez prośby: układ i kolorystyka maili
(kwadratowe, pastelowe, pod telefon), wykres w raporcie (`MailChart`: kolumny po 2 godz., temperatura jako blady zakres),
panel WWW bez edycji konfiguracji (pełny edytor – faza 3c), log administratora jako osobny mail od każdego agenta.
