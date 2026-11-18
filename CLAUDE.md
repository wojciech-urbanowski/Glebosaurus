# Glebosaurus – kontekst dla pracy nad projektem

Bezobsługowy monitor czujników gleby Tuya BLE (ATLO SMS1) dla Windows 11 i Raspberry Pi OS (.NET 10). Odczyty do lokalnej
bazy SQLite, alerty „czas podlać” i raporty mailem przez Gmail; później chmura Supabase z wieloma agentami (także Android) i interfejs WWW – plan: ROADMAP, faza 2.

Przeczytaj najpierw: [README.md](README.md) (użycie), [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md) (projekt i fakty zmierzone
na sprzęcie), [docs/ROADMAP.md](docs/ROADMAP.md) (kolejne fazy i decyzje do podjęcia).

## Komendy

```powershell
dotnet build Glebosaurus.slnx -c Release
dotnet test --solution Glebosaurus.slnx -c Release        # xunit.v3 na Microsoft Testing Platform (global.json)
$env:GLEBOSAURUS_CLOUD_TESTS='1'; dotnet test --project tests/Glebosaurus.Cloud.Tests -c Release   # SQL na PRÓBNYM Supabase (czyści go)
dotnet run --project src/Glebosaurus.Agent -f net10.0-windows10.0.19041.0 -- --help   # Windows; Linux: -f net10.0
# Tryb chmurowy: sekcja "cloud" w konfiguracji (wzór examples/glebosaurus.cloud.config.json), potem enroll --code KOD i run
# Właściciel: cloud migrate|import|settings|agent --admin local/cloud-admin.test.json (bez --confirm tylko plan; prawdziwy projekt – tylko na prośbę)
# cloud settings --read-interval MIN – sam odstęp odczytów w chmurze (admin_settings_update), bez pełnego importu
# Kopia chmury: cloud backup --admin local/cloud-admin.test.json --out <scratchpad> (tylko czyta bazę; plik ZIP z odszyfrowanymi sekretami)
dotnet publish src/Glebosaurus.Agent -c Release -f net10.0 -r linux-arm64 --self-contained false -o publish/linux-arm64
./deploy/package-windows.ps1                               # pliki wydania Windows (zip samodzielny, install-windows.ps1, SHA256SUMS) w publish/release
```

## Mapa

- `src/Glebosaurus.Tuya` – protokół Tuya BLE 3.x (ramki, AES, DP), sesja (`TuyaSession`), lokalne parowanie, poświadczenia,
  profile czujników. **Nie zna biblioteki BLE** – rozmawia przez `IGattLink`.
- `src/Glebosaurus.Core` – konfiguracja, reguły alertów (`AlertEvaluator`), harmonogram raportów, szablony maili, silnik
  (`MonitorEngine`) i interfejsy brzegowe: `ISensorReader` (radio), `IMonitorStore` (baza), `IMailGateway` (poczta), `ILogArchive` (dziennik; pliki dzienne w `DailyLogFiles`).
  Kolejność radia „nasłuch → odczyt znalezionego → nasłuch po resztę” to `ListenThenReadSensorReader` (Core, testy na atrapie);
  w agencie radio Shiny to tylko `ISensorRadio` (`ShinySensorRadio`: słuchaj / przeczytaj jeden).
- `src/Glebosaurus.Data` (SQLite), `src/Glebosaurus.Mail` (Gmail/MailKit), `src/Glebosaurus.Agent` (CLI, Shiny.BluetoothLE).
- `src/Glebosaurus.Service` – usługa Windows bez okna (`glebosaurus-service.exe`, zadanie Harmonogramu przy zalogowaniu):
  `Supervisor` uruchamia `glebosaurus run` (CreateNoWindow), podnosi go po awarii, sprawdza wydania GitHuba przy starcie i
  codziennie o 03:30 i przekazuje aktualizację skryptowi `deploy/install-windows.ps1` (ten sam instaluje od zera). Decyzje
  w Core z testami: `Core/Updates` (wersje, opis wydania, `SHA256SUMS`, `UpdatePolicy`, harmonogram, `install.json`,
  ślad próby) i `Core/Hosting` (muteksy jednej instancji, zdarzenie zatrzymania – nazwy wpisane też w skrypt, kody wyjścia,
  `RestartBackoff`). Wydania: `deploy/package-windows.ps1` + `.github/workflows/release.yml` (tag `v*`); CI: `ci.yml`.
- `src/Glebosaurus.Web` – lokalny panel diagnostyczny w procesie `run` (Kestrel, HTTPS z certyfikatem z podpisem własnym,
  tylko sieć lokalna, hasło; **pulpit** `/` z wykresem ostatnich 3 dób przy każdym czujniku, **strona czujnika** `/sensor?id=`
  ze wszystkimi szczegółami i przeglądalnym wykresem całej historii – SVG w `wwwroot/charts.js`, bez bibliotek, dane z
  `/api/sensors/{id}/series` uśredniane w Core przez `ReadingSeriesBuilder` (`Core/Charts`); **odczyt na żądanie** przez `MonitorEngine.RequestRead` – kolejka silnika, najwyżej raz na
  minutę na czujnik – i **raport mailowy na żądanie** przez `MonitorEngine.RequestReport` – ta sama kolejka, po odczytach cyklu,
  najwyżej raz na minutę, bez zmiany harmonogramu; konfiguracji nie zmienia); dane z `MonitorEngine.State` i `MonitorStatusBuilder` (status składany pole po
  pole, bez sekretów). Strona: `wwwroot/` wbudowane w bibliotekę, dane wstawiane tylko jako tekst (CSP bez skryptów inline).
- `supabase/migrations/` – schemat i funkcje SQL chmury (ARCHITEKTURA.md, zasada 13): tabele w prywatnym schemacie
  `glebosaurus` z RLS, agenci wołają tylko funkcje `public.*` (rola `anon`), administracja (`glebosaurus.admin_*`) tylko
  przez połączenie z bazą. Zastosowanej migracji nie zmieniaj – zmiana schematu to nowa migracja (suma kontrolna).
  `src/Glebosaurus.Cloud.Admin` – połączenie z bazą (Npgsql, pooler sesji, zawsze `VerifyFull` z wbudowanym certyfikatem
  głównym Supabase `supabase/prod-ca-2021.crt`, ważnym do 2031-04-26), `MigrationRunner` z migracjami wbudowanymi, import,
  agenci i `CloudBackup` (zrzut bez `pg_dump`: tabele CSV z jednej migawki + odszyfrowane sekrety z Vault, ZIP;
  `DailyCloudBackup` – raz dziennie w `run` z sekcją `cloud.backup`).
- Tryb chmurowy w Core (`Core/Cloud`, ARCHITEKTURA.md, zasada 14): protokół 1:1 z funkcjami SQL i `ICloudApi`,
  `LeaseLedger` (ważność od wysłania prośby, zegar monotoniczny), `SensorLeasePolicy` i `NotifierLockPolicy`, `CloudSync`
  (pętla `agent_sync`, bufor `IReadingOutbox` = kolumna `sync_state` w SQLite – dodawana zgodnie wstecz, `user_version`
  zostaje 1, żeby 0.1.x po powrocie dalej otwierała bazę; nie podnoś jej bez potrzeby), `CloudConfigSource`
  (`get_config` → `AgentConfig` przez `CloudConfigMapper`; ostatnia dobra kopia z kluczami Tuya w `CloudConfigCache`,
  chroniona DPAPI / plikiem 600 – to sekret, nie wypisuj jej), `CollectorEngine` (odczyt tylko z dzierżawą z zapasem,
  nasłuch wszystkich czujników co 3 min przez `ISensorListener`, polecenia `read_now`/`release`/`send_log`, dziennik dnia
  poprzedniego do chmury), `NotifierEngine` (tylko z blokadą i hasłem na tę epokę; każdy mail: `mail_claim` → SMTP →
  `mail_sent`, pewna porażka SMTP oddaje rezerwację, niepewna nie – najwyżej raz; alerty, raporty, log administratora po
  jednym mailu na agenta, „agent milczy”, strażnik healthchecks.io), lokalny panel w trybie chmurowym: `CloudPanel` (status
  z konfiguracji z chmury, migawki zbieracza `CollectorEngine.State`, obrazu ostatniej synchronizacji `CloudSyncState.View`
  i alertów z chmury; odczyt/raport na żądanie jako `command_create`) i `CloudReadingMirror` (kopia odczytów wszystkich
  agentów w SQLite przez `readings_feed` – numer kolejny z barierą „ustalonych”, tabela `cloud_feed`, `sync_state` = 1,
  więc nie wracają do wysyłki). Klient HTTP:
  `src/Glebosaurus.Cloud` (`SupabaseCloudApi`, sam HttpClient – także dla Androida). Zmiana funkcji SQL = zmiana typów
  protokołu i testów `CloudProtocolTests`/`CloudApiClientTests`. `MonitorEngine` (tryb samodzielny) zostaje bez zmian.
- `tests/` – protokół i sesja na atrapie czujnika; alerty, raporty, magazyn i silnik na atrapach z ręcznie sterowanym czasem;
  `Glebosaurus.Cloud.Tests` – funkcje SQL na próbnym projekcie (zegar przesuwany `glebosaurus.test_now`, tylko w bazie ze
  znacznikiem próbnej).
- `local/` – **poza repozytorium** (`.gitignore`): robocza konfiguracja, klucze czujników, baza, dzienniki.

## Konwencje

- Komunikaty, komentarze, dokumentacja i maile po polsku; identyfikatory w kodzie po angielsku.
- **NuGet: zawsze najnowsze stabilne wersje** (bez beta/preview/rc); wersję sprawdzać w API NuGet, nie z pamięci.
- `TreatWarningsAsErrors`; `TargetFramework` ustawiany per projekt; Agent ma dwa TFM (Windows: WinRT, Linux: BlueZ).
- Czas przez `TimeProvider`, radio/baza/poczta przez interfejsy – dzięki temu silnik testuje się bez sprzętu.
- Każda zmiana zachowania dostaje test; sprawdzenia na sprzęcie opisuje się w ARCHITEKTURA.md z datą.
- Wersja (obecnie 0.2.1) jest w jednym miejscu: `Directory.Build.props`; widać ją w stopce każdego maila (`AppInfo.Version`).
  Podbijaj ją tylko na prośbę użytkownika (przy wydaniu paczki). Wydanie = tag `v<wersja>` (musi zgadzać się z
  `Directory.Build.props`) → Actions publikuje je, a zainstalowane usługi same się zaktualizują w ciągu doby.
- Skrypty `.ps1` zapisuj w UTF-8 **z BOM** (Windows PowerShell 5.1) i nie wstawiaj „” do napisów w podwójnych cudzysłowach –
  PowerShell traktuje je jak zwykły cudzysłów; tam poprzedzaj je odwrotnym apostrofem albo użyj napisu w pojedynczych cudzysłowach.
  Konfigurację w skrypcie czytaj tylko przez `Read-ConfigJson` (komentarze `//`): komunikat wyjątku `ConvertFrom-Json` z
  Windows PowerShell 5.1 zawiera **cały plik, razem z hasłem Gmaila** – nigdy go nie wypisuj ani nie loguj.

## Sprzęt – zasady pracy na żywo (kosztowne odkrycia)

- Czujnik po włożeniu baterii nadaje jako niesparowany **tylko ok. 20 s**. Zawsze najpierw uruchom skan/`pair` w tle,
  dopiero potem poproś użytkownika o baterie – nigdy odwrotnie.
- Sparowany czujnik reklamuje się rzadko (ok. 1 pakiet na kilka–kilkanaście sekund); dane usługi i producenta przychodzą w
  osobnych pakietach. Dopasowanie: po MAC z konfiguracji, zapasowo po UUID Tuya.
- Czujnik przyjmuje jedno połączenie naraz. Odczyt = ok. 11–14 s połączenia i kosztuje baterię, więc interwał ≥ 15 min.
  Czujnik z dużą zaległą historią (np. tygodnie w Smart Life) oddaje ją na raty – ok. 110 rekordów na 180-sekundową sesję;
  sesja urwana limitem bez raportu stanu nie ma stanu bieżącego (`MapSession`, od 0.2.1 – wcześniej przesuwała historię
  „na teraz”, Alpinarium 2026-11-18). **Przy DP14 = 0 (niska bateria) nie rób ręcznych odczytów**: 2026-11-18 Hortensje
  niskie (5%) zamilkły po takiej sesji.
- Czujniki zostawione w Smart Life czytamy kluczem z chmury Tuya (bez resetu); MAC ustala `scan` po UUID Tuya z reklamy.
  Nasze odczyty kasują ich rekordy w czujniku (Smart Life traci historię).
- **Nie zabijaj procesu w trakcie rozłączania** (następny odczyt może zawisnąć). Do prób: `run --minutes N`.
- W zasięgu jest wiele innych urządzeń Tuya (DC:23:51:*): część to **własne czujniki użytkownika**, wciąż sparowane ze Smart
  Life (`Cisy` było wśród nich), reszta może być cudza. Łącz się tylko z czujnikami z konfiguracji; `pair` wybiera tylko niesparowany czujnik
  znanego typu (czyli po resecie przyciskiem – ten usuwa czujnik ze Smart Life) i przy wielu kandydatach wymaga `--mac`. RSSI ze Shiny ma −127 („brak wartości”) w ok. 1/3 pakietów – `SignalStats` liczy tylko ważne próbki; `scan` i dziennik monitora pokazują sygnał, a do dokładnych pomiarów służy surowy WinRT (ARCHITEKTURA.md, „Zasięg Cisy”; odczyty udawały się przy −75…−81 dBm, przy ok. −94 dBm czujnika nie było w reklamach).
- Shiny na Windows potrafi przerwać skan `COMException 0x80004005` (2026-11-14: 1 na 22 cykle) – nasłuch jest wtedy wznawiany
  (`ListenThenReadSensorReader`, najwyżej 3 razy z rzędu), a cykl monitora idzie dalej; szczegóły w ARCHITEKTURA.md, zasada 8.
- Wymiana baterii nie kasuje parowania; kasuje je dopiero reset przyciskiem (wtedy `pair --id <to samo id>`).
- Wskazanie baterii (DP15) to szacunek z napięcia – zaniżany pod obciążeniem radia; DP14 to stan baterii (0 niski / 1 średni /
  2 wysoki, „niski” od ok. 20%); wilgotność trzeba kalibrować.
- Protokół zgadza się bajt w bajt z roquerodrigo/tuya-ble-sdk (ha-tuya-ble, ten sam `gvygg3m8`) – porównanie i różnice w
  ARCHITEKTURA.md, „Porównanie z ha-tuya-ble”.

## Czego nie robić bez zgody użytkownika

- `pair` zmienia stan czujnika (wymaga `--confirm-bind` i fizycznej obecności użytkownika przy baterii).
- `mail-test` (też z `--samples`), `send-log` i prawdziwy `run` z sekcją `mail` wysyłają prawdziwe maile (w tym log z
  załącznikiem do `mail.adminRecipients`); w panelu WWW prawdziwy mail wysyła też „Wyślij raport teraz” (`POST /api/report`). Wyglądu maili sprawdzaj przez
  `mail-preview` (tylko pliki) – maile są projektowane pod wąski ekran telefonu, nie psuj tego (testy pilnują układu).
- Nie drukuj i nie loguj kluczy z `*.credentials.json` ani hasła aplikacji Gmail; nie commituj `local/`. Sekretami są też
  `local/glebosaurus-web.password` (skrót hasła panelu), `local/glebosaurus-web.pfx` (klucz certyfikatu panelu) i
  `local/cloud-admin.json` (klucz tajny Supabase, hasło bazy, adres sygnału healthchecks.io) oraz
  `local/cloud-admin.test.json` (to samo dla próbnego projektu) oraz kopie chmury `glebosaurus-backup-*.zip` (np.
  `local/backups/` – odszyfrowane klucze Tuya i hasło Gmaila) – skrypty czytają je bez wypisywania wartości; adresów
  projektów Supabase też nie wpisuj do repozytorium, bo jest publiczne. W trybie chmurowym sekretami są też
  `glebosaurus-cloud.token` (token agenta) i `glebosaurus-cloud-config.bin` (kopia konfiguracji z kluczami Tuya) obok
  konfiguracji agenta, a także jednorazowe kody rejestracji.
- Migracje i testy SQL tylko na próbnym projekcie `glebosaurus-test` (`local/cloud-admin.test.json`). Prawdziwy projekt
  (`local/cloud-admin.json`) zmienia się wyłącznie na prośbę użytkownika – także migracja „gotowa i sprawdzona”, `cloud
  import` i `cloud agent --confirm`. Do prób importu nie używaj prawdziwej konfiguracji (`local/glebosaurus.config.json` ma
  prawdziwe hasło Gmaila i klucze Tuya) – zmyślona konfiguracja w scratchpadzie.
- **Claude bywa w pakiecie MSIX**: 2026-11-15 moje polecenia piszące do `%LOCALAPPDATA%` (poza `Temp`) trafiały do
  `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local`, której Harmonogram nie widzi – `install-windows.ps1` przerywa wtedy z
  wyjaśnieniem; 2026-11-18 procesy narzędzi działały już poza pakietem (bez przekierowania – nie zakładaj ani jednego, ani
  drugiego). Zasada zostaje: prawdziwą usługę instaluje/aktualizuje/przełącza użytkownik w zwykłym oknie PowerShell; próby
  w scratchpadzie (Temp) działają normalnie. Usługa działa na bestia5 od 2026-11-15 (`%LOCALAPPDATA%\Glebosaurus`) – próbne
  instalacje nie mogą jej ruszyć obok (jeden monitor na konto).
- Tag `v*` wypchnięty na GitHuba publikuje wydanie, które zainstalowane usługi pobiorą same – tylko na prośbę użytkownika.
  `install-windows.ps1` na prawdziwym `%LOCALAPPDATA%\Glebosaurus` (albo z `-ImportFrom local`) rejestruje zadanie i uruchamia
  prawdziwy monitor z mailami – próby rób na katalogu tymczasowym z `-TaskName Glebosaurus-test` i udawanym API (`-ApiBase`).
  Nie używaj `Stop-ScheduledTask` ani zabijania procesu na działającej usłudze – zatrzymuje się `glebosaurus stop`.
- `run` z sekcją `web` słucha na wszystkich interfejsach – na Windows pierwsze uruchomienie może pokazać okno Zapory; reguły
  zapory i zaufanie do certyfikatu zmienia użytkownik, nie Claude. Panel do prób: program podglądu na loopbacku albo testy.

## Stan

Pełny obraz – co działa, co zostało, w jakiej kolejności, co niesprawdzone i co czeka na użytkownika – jest w
[docs/ROADMAP.md](docs/ROADMAP.md), „Stan w skrócie”; daty i szczegóły sprawdzeń – w ARCHITEKTURA.md. Tu tylko to, co
trzeba wiedzieć przed każdą pracą:

- **Prawdziwym monitorem jest usługa na bestia5** (od 2026-11-15, wersja 0.2.1 od 2026-11-18 20:20): program w
  `%LOCALAPPDATA%\Glebosaurus\app`, dane w `…\Glebosaurus\data`, zadanie „Glebosaurus” przy zalogowaniu, Zapora
  zatwierdzona dla stałej ścieżki `app\glebosaurus.exe`, samoaktualizacja z GitHuba sprawdzona (0.1.0 → 0.1.1 → 0.2.0 → 0.2.1).
  **Od 2026-11-18 18:06 pracuje w trybie chmurowym** (2.1.5): agent `bestia5`, nadawca z priorytetem 2, konfiguracja,
  czujniki, klucze Tuya i hasło Gmaila w prawdziwym projekcie Supabase; w `data\` sekcja `cloud`, token, kopia
  konfiguracji, `cloud-admin.json` bez klucza tajnego (codzienna kopia do `data\backups`) i poprzednia konfiguracja
  `glebosaurus.standalone.config.json`. Zmiany czujników, progów i poczty idą teraz przez chmurę (`cloud import` nadpisuje
  – tylko na prośbę), nie przez plik usługi. `local/` zostaje do prób (jego `glebosaurus.config.json` to tryb samodzielny,
  z tymi samymi 9 czujnikami co chmura – z niego szedł `cloud import`), a `run` z niego nie wystartuje obok usługi (kod 4).
- **Zatwierdzone przez użytkownika – nie zmieniaj bez prośby:** układ i kolorystyka maili (kwadratowe, pastelowe, pod
  telefon), wykres w raporcie (`MailChart`: kolumny po 2 godz., temperatura jako blady zakres), panel WWW bez edycji
  konfiguracji (pełny edytor – faza 3c), log administratora w trybie chmurowym jako osobny mail od każdego agenta.
- **Faza 2.1 (chmura):** 2.1.1–2.1.4 skończone; 2.1.5 od 2026-11-18: prawdziwy projekt ma schemat (trzy migracje) i
  konfigurację (od 2026-11-18 19:07 9 czujników: 5 sparowanych lokalnie i 4 zostawione w Smart Life; od 21:32 odczyt co
  2 h 5 min – decyzja użytkownika, `cloud settings`), wydanie 0.2.0, usługa w trybie chmurowym – teraz tydzień próby (kryterium 2.1 w
  ROADMAP). Zmiany na prawdziwym projekcie (migracje, `cloud import`, `cloud agent --confirm`) dalej tylko na prośbę.
- **Z pracy usługi (2026-11-17):** czujniki przy wejściu prawie poza zasięgiem bestia5 (ROADMAP, „Do zbadania”); fałszywe
  alerty „brak odczytów” po wyłączeniu komputera poprawione (`SilenceGrace`, ARCHITEKTURA.md, zasada 9) – na bestia5 od
  0.2.0.
