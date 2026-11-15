# Glebosaurus – kontekst dla pracy nad projektem

Bezobsługowy monitor czujników gleby Tuya BLE (ATLO SMS1) dla Windows 11 i Raspberry Pi OS (.NET 10). Odczyty do lokalnej
bazy SQLite, alerty „czas podlać” i raporty mailem przez Gmail; później chmura, zaszyfrowana konfiguracja i interfejs WWW.

Przeczytaj najpierw: [README.md](README.md) (użycie), [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md) (projekt i fakty zmierzone
na sprzęcie), [docs/ROADMAP.md](docs/ROADMAP.md) (kolejne fazy i decyzje do podjęcia).

## Komendy

```powershell
dotnet build Glebosaurus.slnx -c Release
dotnet test --solution Glebosaurus.slnx -c Release        # xunit.v3 na Microsoft Testing Platform (global.json)
dotnet run --project src/Glebosaurus.Agent -f net10.0-windows10.0.19041.0 -- --help   # Windows; Linux: -f net10.0
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
- `tests/` – protokół i sesja na atrapie czujnika; alerty, raporty, magazyn i silnik na atrapach z ręcznie sterowanym czasem.
- `local/` – **poza repozytorium** (`.gitignore`): robocza konfiguracja, klucze czujników, baza, dzienniki.

## Konwencje

- Komunikaty, komentarze, dokumentacja i maile po polsku; identyfikatory w kodzie po angielsku.
- **NuGet: zawsze najnowsze stabilne wersje** (bez beta/preview/rc); wersję sprawdzać w API NuGet, nie z pamięci.
- `TreatWarningsAsErrors`; `TargetFramework` ustawiany per projekt; Agent ma dwa TFM (Windows: WinRT, Linux: BlueZ).
- Czas przez `TimeProvider`, radio/baza/poczta przez interfejsy – dzięki temu silnik testuje się bez sprzętu.
- Każda zmiana zachowania dostaje test; sprawdzenia na sprzęcie opisuje się w ARCHITEKTURA.md z datą.
- Wersja (obecnie 0.1.1) jest w jednym miejscu: `Directory.Build.props`; widać ją w stopce każdego maila (`AppInfo.Version`).
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
  `local/glebosaurus-web.password` (skrót hasła panelu) i `local/glebosaurus-web.pfx` (klucz certyfikatu panelu).
- **Claude działa z pakietu MSIX**: moje polecenia piszące do `%LOCALAPPDATA%` (poza `Temp`) trafiają do
  `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local`, której Harmonogram nie widzi – `install-windows.ps1` przerwie wtedy z
  wyjaśnieniem. Prawdziwą usługę instaluje/aktualizuje użytkownik w zwykłym oknie PowerShell albo tymczasowe zadanie
  Harmonogramu (procesy z Harmonogramu działają poza pakietem); próby w scratchpadzie (Temp) działają normalnie. Usługa działa
  na bestia5 od 2026-11-15 (`%LOCALAPPDATA%\Glebosaurus`) – próbne instalacje nie mogą jej ruszyć obok (jeden monitor na konto).
- Tag `v*` wypchnięty na GitHuba publikuje wydanie, które zainstalowane usługi pobiorą same – tylko na prośbę użytkownika.
  `install-windows.ps1` na prawdziwym `%LOCALAPPDATA%\Glebosaurus` (albo z `-ImportFrom local`) rejestruje zadanie i uruchamia
  prawdziwy monitor z mailami – próby rób na katalogu tymczasowym z `-TaskName Glebosaurus-test` i udawanym API (`-ApiBase`).
  Nie używaj `Stop-ScheduledTask` ani zabijania procesu na działającej usłudze – zatrzymuje się `glebosaurus stop`.
- `run` z sekcją `web` słucha na wszystkich interfejsach – na Windows pierwsze uruchomienie może pokazać okno Zapory; reguły
  zapory i zaufanie do certyfikatu zmienia użytkownik, nie Claude. Panel do prób: program podglądu na loopbacku albo testy.

## Stan

Zrobione: spike sprzętowy i faza 1 (lokalny monitor, parowanie), szablony maili pod telefon z konfigurowalnymi odbiorcami
(konto nadawcy i odbiorca są w `local/glebosaurus.config.json`); 2026-11-13 wysłano 3 prawdziwe maile próbne i Gmail je
przyjął. Układ i kolorystykę maili (kwadratowe, pastelowe) zatwierdził użytkownik na telefonie – nie zmieniaj ich bez prośby. Log techniczny dla administratora (osobny adres, pierwszy przebieg dnia wysyła
dziennik z poprzedniego dnia z załącznikiem, retencja dzienników 7 dni) jest w kodzie i testach; 2026-11-13 `send-log` wysłał prawdziwy dziennik (Gmail przyjął),
2026-11-14 `run` wysłał go sam w pierwszym przebiegu dnia (restart nie powtórzył). Niesprawdzone: czyszczenie starych plików na żywo.
Panel WWW (decyzje użytkownika 2026-11-14: HTTPS, hasło, na razie bez edycji konfiguracji; odczyt na żądanie dodany tego samego dnia) działa na żywo:
2026-11-14 hasło ustawione (`local/glebosaurus-web.password`, sekcja `web` w `local/glebosaurus.config.json`), `run` z panelem,
użytkownik zalogował się w przeglądarce i zlecał odczyty na żądanie. Raport na żądanie z panelu (2026-11-14) sprawdzony w testach,
w podglądzie i na żywo: raport zlecony z panelu doszedł (czekał ok. 78 s na odczyty cyklu). Nasłuch BLE na przemian z odczytem i okno skanu 60 s
sprawdzone na żywo tego dnia. Wykresy i strona czujnika (2026-11-14) sprawdzone w testach i w przeglądarce na programie podglądu z danymi próbnymi; na
prawdziwym monitorze jeszcze nie. 2026-11-14 `run` sam wysłał prawdziwy mail o alercie (słaba bateria). Prototyp wykresu w raporcie mailowym
(`MailChart`, sam HTML, kolumny po 2 godz., temperatura jako blady zakres, budżet 90 KB) jest od 2026-11-14 w każdym raporcie (`SnapshotBuilder` dociąga odczyty z 75 h); pierwszą
wersję użytkownik obejrzał na telefonie i poprawił (2 godz., temperatura jako zakres). Niesprawdzone:
wejście z telefonu (reguła Zapory), prawdziwy raport z `run`,
Raspberry Pi, kalibracja wilgotności. Następne kroki: patrz [docs/ROADMAP.md](docs/ROADMAP.md).

Usługa Windows (2026-11-15): wydanie **0.1.0** opublikowane przez Actions (repo publiczne), a na bestia5 zainstalowane z
GitHuba skryptem z `-ImportFrom local -NoStart`: program w `%LOCALAPPDATA%\Glebosaurus\app`, **kopia** danych z `local/` w
`…\Glebosaurus\data`, zadanie „Glebosaurus” przy zalogowaniu. Od tej chwili prawdziwym monitorem jest usługa; `local/` zostaje
do prób, a `run` z niego nie wystartuje obok usługi (kod 4). Niesprawdzone na żywo: pierwszy start prawdziwej usługi (okno
Zapory dla nowej ścieżki, maile), strażnik co godzinę, uśpienie i wybudzenie, wylogowanie w trakcie odczytu, aktualizacja z
prawdziwego wydania (na udawanym API działa).
