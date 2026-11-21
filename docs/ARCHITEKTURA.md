# Glebosaurus – architektura

Jak zbudowany jest Glebosaurus i dlaczego tak. Użycie: [README](../README.md); plan dalszych prac: [ROADMAP.md](ROADMAP.md);
dziennik prób na sprzęcie i decyzji z datami: [HISTORIA.md](HISTORIA.md).

Cel: bezobsługowa aplikacja .NET 10 (Windows 11, Raspberry Pi OS 64-bit, Android), która po cichu zbiera dane z kilkunastu
czujników gleby Tuya BLE (wilgotność, temperatura gleby, bateria), zapisuje je w chmurze, wysyła alerty o podlewaniu i
raporty o stałych porach mailem (Gmail), a stan pokazuje w przeglądarce – za darmo (darmowy plan Supabase, bez własnego
serwera).

## Komponenty

```
 Czujniki Tuya BLE (np. ATLO SMS1)
        │ BLE: GATT 0x1910 (notify 0x2B10, write 0x2B11), AES, klucz lokalny
        │ łączy się tylko agent z dzierżawą czujnika; słuchać mogą wszyscy
 ┌──────┴────────────┐ ┌───────────────────┐ ┌──────────────────────┐
 │ Windows 11        │ │ Android           │ │ Raspberry Pi 24/7    │  agenci .NET 10, bufor SQLite,
 │ zbieranie         │ │ zbieranie         │ │ nadawca (prior. 1)   │  panel lokalny (Windows/Pi)
 │ nadawca (prior. 2)│ │ bez maili         │ │ czujniki – faza 2.3b │
 └──────┬────────────┘ └─────────┬─────────┘ └──────────┬───────────┘
        └─── HTTPS, token agenta ┴ funkcje SQL ─────────┘  tylko ruch wychodzący
 ┌──────┴───────────────────────────────────────────────────────────┐
 │ SUPABASE (darmowy plan) – funkcje SQL, RLS                       │
 │ konfiguracja, odczyty, dzierżawy, blokada nadawcy, kolejka maili │
 │ Vault: klucze Tuya, hasło Gmaila · dzienne logi agentów (gzip)   │
 └──────┬───────────────────────────────────────────────────────────┘
        │ połączenie z bazą hasłem bazy – tylko właściciel (CLI: cloud migrate|config|agent|backup)
        │ HTTPS, Supabase Auth z TOTP – tylko konto właściciela, funkcje owner_*  (faza 3: strona na GitHub Pages)
 Poczta: nadawca z blokadą → Gmail SMTP (MailKit). Strażnik: nadawca → healthchecks.io.
```

Agentów może być dowolnie wielu; każdy zbiera odczyty z czujników, które słyszy, a maile wysyła tylko ten, który trzyma
blokadę nadawcy. Agent bez Bluetooth (`"bluetooth": false`, np. Raspberry Pi stojące daleko od czujników) jest samym
nadawcą – czujniki czytają inni. Właściciel konfiguruje chmurę na stronie w chmurze (czujniki, progi, raporty, poczta,
agenci – zasada 10), a z własnego komputera zakłada schemat i nadaje dostęp poleceniami `glebosaurus cloud …` z plikiem
administratora (hasło bazy).

## Struktura repozytorium

```
src/Glebosaurus.Tuya        protokół Tuya BLE 3.x, poświadczenia, profile czujników, sesja, reguły parowania (bez zależności od BLE)
src/Glebosaurus.Core        konfiguracja, alerty, harmonogram raportów, szablony maili, dane do wykresów, części agenta
                            (Core/Cloud: protokół, synchronizacja, zbieracz, nadawca, panel), aktualizacje i hosting (logika z testami)
src/Glebosaurus.Data        lokalna baza SQLite (bufor odczytów, kopia odczytów innych agentów)
src/Glebosaurus.Mail        GmailSender (MailKit)
src/Glebosaurus.Web         lokalny panel diagnostyczny (Kestrel, HTTPS, hasło; strony w wwwroot/ wbudowane w bibliotekę)
src/Glebosaurus.Cloud       klient chmury dla agentów: funkcje SQL przez Data API (sam HttpClient) – ICloudApi z Core
src/Glebosaurus.Cloud.Admin narzędzia właściciela: połączenie z bazą (Npgsql, VerifyFull), migracje, zapis konfiguracji, agenci, kopia
src/Glebosaurus.Agent       CLI i agent na Windows/Linux (Shiny.BluetoothLE; net10.0 + net10.0-windows10.0.19041.0)
src/Glebosaurus.Service     usługa Windows bez okna (glebosaurus-service.exe): nadzór agenta, sprawdzanie wydań, aktualizacja
src/Glebosaurus.Android     agent na Androida (zasada 12) – poza Glebosaurus.slnx (obciążenie android)
web/                        strona właściciela w chmurze (zasada 10): moduły ES bez budowania, GitHub Pages
supabase/migrations/        schemat i funkcje SQL chmury (zasada 13), wbudowane w Glebosaurus.Cloud.Admin
supabase/prod-ca-2021.crt   certyfikat główny Supabase (publiczny, do 2031-04-26), wbudowany w Glebosaurus.Cloud.Admin
tests/                      protokół i sesja na atrapie czujnika; Core na atrapach z ręcznie sterowanym czasem;
                            Glebosaurus.Cloud.Tests – funkcje SQL na próbnym projekcie Supabase (na żądanie)
examples/                   wzory: plik agenta, plik administratora
deploy/                     install-windows.ps1, package-windows.ps1 (Windows); install-linux.sh, package-linux.ps1,
                            test-install-linux.sh (Linux/Raspberry Pi, zasada 7); build-android.ps1
.github/workflows/          ci.yml (build + testy, APK), release.yml (tag v* → testy, paczka, APK, próba instalacji, wydanie),
                            pages.yml (tag v* i ręcznie → web/ na GitHub Pages)
```

Interfejsy brzegowe, dzięki którym silniki testuje się bez sprzętu: `IGattLink` (BLE dla protokołu), `ISensorRadio` /
`ISensorReader` / `ISensorListener` (radio), `IMonitorStore` + `IReadingOutbox` + `IReadingMirrorStore` (SQLite),
`ICloudApi` (chmura), `IMailGateway` (poczta), `ILogArchive` (dziennik), a czas zawsze przez `TimeProvider`.

## Zasady projektowe

1. **Konfiguracja i sekrety w Supabase.** Supabase trzyma całą konfigurację – czujniki (nazwy, MAC, progi), harmonogramy
   raportów, odbiorców, role agentów – a w Vault (szyfrowanie kluczem trzymanym poza bazą) klucze Tuya i hasło aplikacji
   Gmail. Na urządzeniu agenta zostaje tylko adres chmury, własny token i ustawienia urządzenia (baza, retencja, dziennik,
   panel); konfiguracja z chmury ma lokalnie ostatnią dobrą kopię, więc agent startuje i bez sieci. Klucze Tuya dostaje
   każdy zarejestrowany agent (każdy może przejąć każdy czujnik) i trzyma ich kopię chronioną (DPAPI / Android Keystore /
   plik 600). Hasło Gmaila dostaje tylko obecny nadawca (`mail_credentials`, ta sama kontrola epoki co `mail_claim`) i
   trzyma je wyłącznie w pamięci. Konfigurację zmienia właściciel na stronie w chmurze (zasada 10, sekrety tylko do
   ustawienia); pliku konfiguracji chmury i `cloud config` od 2026-11-21 nie ma. Konsekwencje: konto Supabase i hasło bazy są kluczem do wszystkiego (logowanie
   dwuetapowe; hasło bazy nigdy na agentach – wyjątek: jeden agent z kopią zapasową, zasada 10); hasło aplikacji Gmail
   daje pełny dostęp do skrzynki, więc nadawcą powinno być osobne konto Gmail tylko do tego.
2. **Koordynacja wielu agentów.** Czujnik przyjmuje jedno połączenie naraz, a kilka agentów może go słyszeć; każdy mail
   ma wyjść raz. Funkcja SQL `lease_claim(zasób, agent, ttl)` atomowo daje dzierżawę, gdy zasób jest wolny, wygasł (plus
   2 min zapasu) albo już należy do pytającego, i zwraca numer epoki i czas ważności; `lease_release` ją oddaje. Czas
   liczy baza, a agent liczy ważność na własnym zegarze monotonicznym od chwili **wysłania** prośby – wychodzi mu krócej
   niż bazie, więc rozjechane zegary nie szkodzą.
   - **Dzierżawa czujnika: 30 min**, odnawiana przy każdej synchronizacji (ok. 90 s). Bez dzierżawy agent się nie łączy i
     nie zaczyna odczytu, gdy zostały mniej niż 2 min. Bez sieci agent czyta swoje czujniki do końca dzierżawy, a baza
     oddaje czujnik innemu dopiero po wygaśnięciu i zapasie – przy podziale sieci nie ma dwóch właścicieli naraz. Koszt:
     nagłe zniknięcie agenta to ok. 32 min bez odczytów jego czujników. Agent ubiega się tylko o czujnik, który słyszy;
     słabszy sygnał czeka dłużej; właściciel oddaje czujnik po 3 porażkach z rzędu albo gdy inny agent słyszy go dłużej
     co najmniej o 6 dB lepiej (histereza); przy zatrzymaniu (np. `stop`) kończy trwający odczyt i oddaje od razu.
     Słyszenia to jeden wiersz na parę agent–czujnik, nadpisywany – bez historii.
   - **Blokada nadawcy** (zasób `notifier`): 5 min, odnawiana co ok. 90 s, jedna na całą pocztę (alerty, raporty, log
     administratora). Kandydaci mają w tabeli agentów `can_send_mail` i `sender_priority`; przy wolnej blokadzie kandydat
     z priorytetem N czeka N × 1 min. Gdy wraca ważniejszy kandydat, obecny nadawca oddaje blokadę w bezpiecznej chwili
     (nic w trakcie wysyłki). Po nagłym zniknięciu nadawcy przejęcie po ok. 7 min. Bez blokady agent nie wysyła żadnego
     maila – także przy niedostępnym Supabase. Oddający zapisuje w dzienniku przyczynę (ważniejszy kandydat z nazwą i
     priorytetem, odebrana rola, zatrzymanie), a nowy nadawca – „mam blokadę”.
   - **Kolejka maili bez duplikatów** – trzy niezależne zabezpieczenia: (1) blokada z epoką – dwóch nadawców nie pracuje
     naraz; (2) `mail_claim(klucz, agent, epoka)` udaje się tylko, gdy według zegara bazy agent wciąż trzyma blokadę z tą
     epoką – uśpiony nadawca po przebudzeniu niczego nie wyśle; (3) unikalny klucz maila („tak” tylko raz w historii):
     `report|HH:mm|data`, `alert|<czujnik>|<rodzaj>|<początek epizodu>|n`, `log|<dzień>|<agent>`,
     `agent-silent|<agent>|<od>`, `report-request|<id polecenia>`. Potem SMTP i `mail_sent`. Mail zarezerwowany przez
     nadawcę, który zamarł przed SMTP, nie jest ponawiany – najgorzej opóźnienie albo utrata jednego maila, nigdy duplikat.
     Message-ID wyliczany z klucza. Wspólny stan w Supabase (otwarte alerty, klucze kolejki), więc nowy nadawca zaczyna
     tam, gdzie skończył poprzedni; raport z przerwy nadrabia `catchUpHours`.
   - **Strażnik:** sygnał do healthchecks.io (darmowy) wysyła tylko obecny nadawca i tylko, gdy wszystko działa
     (blokada, Supabase, SMTP), co 5 min; cisza ponad 30 min (sprawdzenie „Simple”: Period 5 min, Grace 25 min) → mail od
     healthchecks.io (padł nadawca, nie ma sieci, Supabase uśpiony).
   Testy: zasady w Core na atrapie bazy z ręcznie sterowanym czasem; funkcje SQL na drugim, próbnym projekcie Supabase –
   dwóch agentów, uśpienie z dzierżawą, utrata sieci, restart w trakcie wysyłki. Testy na chmurze uruchamia się na
   żądanie (`GLEBOSAURUS_CLOUD_TESTS=1` i plik `local/cloud-admin.test.json`; bez nich są pomijane) i nigdy na prawdziwym
   projekcie: odmawiają, gdy id projektu jest takie jak w `local/cloud-admin.json` albo gdy baza ma tabele Glebosaurusa bez
   znacznika bazy próbnej. Funkcje SQL wołają przez połączenie bezpośrednie (rola i żądania tokenu ustawione w
   transakcji, zegar przesuwany), a funkcje agentów także przez prawdziwe Data API (`SupabaseCloudApi`). Funkcje strony
   właściciela idą dodatkowo drogą `web/supabase.js` (`DataApiOwnerTests`, zasada 13 – „Środowisko Data API”): konto w
   Supabase Auth próbnego projektu zakłada admin API kluczem tajnym (`secretKey` w pliku próbnego projektu; zakładanie
   kont jest wyłączone), logowanie hasłem, składnik TOTP z kodem liczonym w teście (RFC 6238) → token `aal2`, wpis przez
   `admin_owner_add`; potem każda funkcja `owner_*` (zmieniające dane – każda) przez `/rest/v1/rpc` z kluczem
   publikowalnym, kody HTTP błędów i dowód na wyzwalaczu sprzed poprawki `safe_update` (przez Data API 21000, wprost
   przechodzi). Jedno konto na przebieg (adres `glebosaurus-data-api-…@example.com`, hasło tylko w pamięci), usuwane na
   końcu; wspólne ustawienia wracają na miejsce, bo Data API nie da się wycofać.
3. **Synchronizacja agent ↔ Supabase.** Tylko ruch wychodzący: HTTPS do funkcji SQL (`/rest/v1/rpc/…`) zwykłym
   `HttpClient` z kluczem publikowalnym projektu (`sb_publishable_…` w nagłówku `apikey`, rola `anon`) i tokenem agenta –
   bez SDK i bez hasła bazy na urządzeniach. Główne wywołanie `agent_sync` co ok. 90 s: sygnał życia (wersja, stan BLE;
   telefon także bateria, ładowarka, temperatura akumulatora, stan cieplny, sieć i siła Wi-Fi; agent na Linuksie –
   temperatura procesora ze stref cieplnych jądra, `LinuxThermal`), słyszenia, paczka odczytów
   z lokalnego bufora, odnowienie dzierżaw → w odpowiedzi przydziały z epoką i czasem ważności, wersja konfiguracji,
   polecenia i lista agentów z ich ostatnim sygnałem życia. Odczyty są idempotentne po `(sensor_id, at)` – ten sam klucz
   co lokalnie – a lokalny SQLite jest buforem i pełną kopią odczytów agenta, więc przerwa sieci kończy się dosłaniem bez
   strat. Dzienne logi agentów trafiają spakowane do tabeli w bazie, a nadawca
   wysyła z nich log administratora – osobny mail z dziennika każdego agenta. Protokół jest wersjonowany (`v1`).
4. **Uwierzytelnianie.** Agent: jednorazowy kod rejestracji od właściciela (`cloud agent`; na Androidzie kod QR) →
   `enroll` → własny token, w bazie tylko jego skrót, odwoływalny w każdej chwili (`cloud agent --revoke`); lokalnie DPAPI
   (Windows), Android Keystore albo plik 600 (Linux). RLS zamyka wszystkie tabele; agenci wołają wyłącznie funkcje
   `security definer`, które sprawdzają token i rolę. Funkcje Postgresa domyślnie dostają `execute` dla `PUBLIC`, więc
   migracje to odbierają (także jako domyślne uprawnienie dla przyszłych funkcji) i nadają `anon` tylko funkcje agentów.
   Najcenniejszy jest token nadawcy (może zdobyć blokadę, a z nią hasło Gmaila), więc rolę nadawcy dostają tylko 1–2
   zaufane urządzenia. **Właściciel w przeglądarce** (faza 3, zasada 10): konto Supabase Auth (e-mail i hasło) z
   obowiązkowym drugim składnikiem TOTP. Konto zakłada właściciel w panelu Supabase (zakładanie kont wyłączone), a
   właścicielem Glebosaurusa czyni je dopiero wpis w tabeli `glebosaurus.owners` (`cloud owner --email`, połączenie z
   bazą) – samo konto w Auth nie daje niczego. Właścicielem może zostać tylko konto z już potwierdzonym TOTP (dodaje go
   strona przy pierwszym logowaniu, zanim konto jest właścicielem) – inaczej ktoś z samym hasłem dodałby własny drugi
   składnik. Strona woła wyłącznie funkcje `public.owner_*` (`security definer`, puste
   `search_path`, `execute` tylko dla roli `authenticated`), a każda zaczyna od `glebosaurus.require_owner()`:
   `auth.uid()` musi być w `owners`, a token mieć `aal2` (zalogowanie z TOTP) – inaczej SQLSTATE 42501. Tabele zostają
   zamknięte (RLS bez zasad), więc właściciel widzi i zmienia tylko to, co dają funkcje. Sekrety (hasło Gmaila, klucze
   Tuya, adres strażnika) można w panelu ustawić, nigdy odczytać – funkcje zwracają tylko „ustawione” i datę.
5. **Klucze Tuya: lokalne parowanie własnym kluczem** (bez Smart Life i Tuya IoT). `pair` wymyśla `deviceId` i 6-bajtowy
   klucz lokalny, zapisuje go w pliku **przed** wysłaniem do czujnika (przerwane parowanie wznawia się tym samym kluczem)
   i sprawdza na nowym połączeniu. Sparowany czujnik znika z aplikacji Smart Life; reset przyciskiem go przywraca. Czujnik
   zostawiony w Smart Life też da się czytać kluczem pobranym z chmury Tuya (MAC ustala `scan` po UUID Tuya z reklamy) –
   wtedy jednak nasze odczyty kasują jego rekordy i Smart Life traci historię. Klucze trafiają do Vault przez stronę w
   chmurze (plik klucza z `pair` albo ręcznie zapisany klucz z chmury Tuya, wgrany w edytorze) albo przez parowanie
   zdalne. **Parowanie zdalne** (faza 3d): kroki BLE w `ShinySensorPairer` (`ISensorPairer` z Core –
   skan kandydatów z oknem 1,5 s na drugiego, przypisanie w sesji Tuya, weryfikacja odczytem; to samo radio ma `pair`),
   przebieg w `PairingProcedure` (Core, testy na atrapie): skan rusza pierwszy i dopiero wtedy postęp „wkładaj baterie”,
   kilku kandydatów – odmowa z ich adresami, nowy klucz zapisany przed wysłaniem (`IPairingKeyStore`: plik albo Vault),
   chwila spokoju, weryfikacja. `RemotePairing` wykonuje polecenie `pair` w zbieraczu: czeka na wolne radio (`RadioGate` –
   zbieracz pomija nasłuch i odczyty, gdy radio trzyma parowanie), zgłasza postęp (`command_progress`), klucz trzyma w
   Vault (`pair_key_put` – tylko agent wskazany w czekającym poleceniu; `pair_key_get` oddaje klucz przerwanego parowania
   albo istniejącego czujnika o tym id), a po weryfikacji woła `pair_complete` – baza zakłada czujnik (albo podmienia klucz
   i MAC istniejącego), przenosi klucz z `glebosaurus:pairing:<id>` do klucza czujnika i kończy polecenie. Jedno
   czekające parowanie na czujnik i na agenta (nie dwa agenty na jeden zapisany klucz); ponowna próba po wyniku to nowe
   polecenie; zapisany klucz parowania znika, gdy czujnik o tym id powstaje, znika albo dostaje nowy MAC (wyzwalacz), a
   właściciel może go porzucić (`owner_pair_forget` – pod tym id ma być inny czujnik). Na razie tylko
   agent na Windows; inny agent odpowiada na `pair` od razu odmową (`CollectorEngine.NoPairingError`).
6. **Warstwa BLE: Shiny.BluetoothLE (+ `.Linux`, BlueZ)** za własnym wąskim interfejsem (`IGattLink`); protokół Tuya jest
   czysto zarządzany (`Glebosaurus.Tuya`) i nie zna biblioteki BLE. Zapasowo: SimpleSharpBLE (licencja BUSL-1.1, tylko
   użytek niekomercyjny) albo bezpośrednio WinRT + Linux.Bluetooth. Na Androidzie radio jest wprost na API systemu
   (Shiny nie filtruje tam skanu po MAC – zasada 12), za tymi samymi interfejsami.
7. **Uruchamianie, instalacja i aktualizacje.** Raspberry Pi i inny Linux: usługa systemd (niżej, „Linux”). Windows: zadanie
   Harmonogramu „Glebosaurus” uruchamia przy zalogowaniu `glebosaurus-service.exe` (projekt `Glebosaurus.Service`,
   podsystem okienkowy – bez okna konsoli), na koncie użytkownika, bez administratora. Nie usługa Windows: ta wymaga
   administratora, startuje przed zalogowaniem (BLE przez WinRT w sesji 0 – niesprawdzone) i potrzebowałaby hasła konta.
   - **Układ** `%LOCALAPPDATA%\Glebosaurus`: `app\` (program; ścieżka stała, bo reguły Zapory są per ścieżka programu),
     `app.old\` (poprzednia wersja), `data\` (konfiguracja agenta, token, kopia konfiguracji z chmury, baza, dzienniki,
     sekrety panelu – aktualizacja ich nie dotyka), `updates\` (pobrane pliki, dziennik ostatniej aktualizacji, ślad
     próby), `install.json` (repozytorium, adres API, nazwa zadania, pora, `autoUpdate`).
   - **Usługa** (`Supervisor`) uruchamia `glebosaurus.exe run --quiet` z `CreateNoWindow` i z prywatnym zdarzeniem
     zatrzymania; wyjście agenta trafia do `service-RRRRMMDD.log`. Po zakończeniu agenta wznawia go po 1, 2, 5, potem co
     15 min (licznik zeruje godzina spokojnej pracy; kod 4 „agent już działa” – co 5 min, `RestartBackoff`). Wydania
     sprawdza przy starcie, potem codziennie o 03:30, po błędzie sieci po 30 min (`UpdateCheckSchedule`); decyzję podejmuje
     `UpdatePolicy` (tylko nowsza, kompletna, nie robocza wersja; po nieudanej próbie ta sama wersja czeka dobę).
   - **Aktualizacja = ten sam skrypt co instalacja.** Usługa pobiera `install-windows.ps1` z nowego wydania, sprawdza go z
     `SHA256SUMS`, zapisuje ślad próby, zatrzymuje czysto agenta, uruchamia skrypt (`-Unattended`, bez okna) i kończy
     pracę. Skrypt czeka na koniec usługi, pobiera paczkę, sprawdza skrót, wyłącza zadanie na czas podmiany, przenosi
     `app\` do `app.old\`, rozpakowaną paczkę do `app\`, rejestruje zadanie od nowa i uruchamia je. Przy porażce zapisuje
     przyczynę w śladzie i uruchamia dotychczasową wersję – bez śladu zepsute wydanie dawałoby pętlę „przekaż – porażka –
     start – przekaż”.
   - **Jedna instancja i czyste zatrzymanie** (`Glebosaurus.Core/Hosting`): muteks `Local\Glebosaurus.Monitor` trzyma
     każdy `run` (drugi kończy się kodem 4), `Local\Glebosaurus.Service` – usługa. Zdarzenie `Local\Glebosaurus.Stop`
     (polecenie `stop`, skrypt) zatrzymuje agenta i usługę; nikt nie zabija procesu, bo zabicie w trakcie rozłączania
     potrafi zawiesić następny odczyt. `Stop-ScheduledTask` zabija – nie używać.
   - **Zadanie Harmonogramu**: wyzwalacz przy logowaniu własnego użytkownika (wyzwalacz „dowolny użytkownik” wymaga
     administratora) z powtórką co godzinę (strażnik; przy działającej usłudze nowa kopia jest pomijana –
     `MultipleInstances IgnoreNew`), `LogonType Interactive`, `RunLevel Limited`, praca na baterii, bez limitu czasu.
   - **Terminal aplikacji z pakietu MSIX** (np. aplikacji z Microsoft Store) bywa przekierowany: zapisy w `%LOCALAPPDATA%`
     trafiają do prywatnej kopii pakietu, której Harmonogram nie widzi. Skrypt zapisuje plik próbny i szuka go w
     `Packages\*\LocalCache\Local\…` – jeśli tam jest, przerywa z wyjaśnieniem. Zatrzymanie przed podmianą dotyczy tylko
     agenta i usługi uruchomionych z własnego `app\` (po ścieżce procesu) – muteksy są wspólne dla sesji.
   - **Bezpieczeństwo aktualizacji:** HTTPS do GitHuba i skrót SHA-256 z tego samego wydania chronią przed uszkodzonym
     pobraniem, nie przed przejęciem konta GitHub (podpis wydań – ROADMAP, faza 4). Skrypt nie zmienia Zapory.
   - **Linux (Raspberry Pi OS 64-bit, inny Debian/Ubuntu arm64)** – `deploy/install-linux.sh` (bash, uruchamiany przez
     sudo), ten sam podział ról co na Windows: skrypt instaluje i aktualizuje, a samoaktualizację uruchamia timer.
     Paczka `Glebosaurus-<wersja>-linux-arm64.tar.gz` jest **zależna od frameworka** (decyzja użytkownika 2026-11-19:
     mała paczka, ASP.NET Core Runtime 10 instaluje i aktualizuje właściciel – `dotnet-install.sh` do `/opt/dotnet`,
     poza `/home`, bo usługa go nie widzi). Paczkę buduje `package-linux.ps1` (PowerShell 7, `System.Formats.Tar`), także
     na Windows; uprawnienia plików ustawia skrypt instalacji.
     - **Układ** `/opt/glebosaurus`: `app/` (program, właściciel root – usługa nie może go zmienić), `app.old/`, `data/`
       (konfiguracja agenta, token, kopia konfiguracji z chmury, baza, dzienniki, sekrety panelu, kopie chmury; należy do
       systemowego użytkownika `glebosaurus` bez logowania, prawa 700; aktualizacja nic w nim nie zmienia), `updates/`
       (pobrane pliki, `install-last.log`, ślad próby `update-attempt`), `install.conf` (repozytorium, dotnet, runtime,
       autoaktualizacje – odczytywany bez wykonywania), kopia skryptu.
     - **Jednostki** (zapisuje je skrypt, każda aktualizacja nadpisuje; własne zmiany – `systemctl edit`):
       `glebosaurus.service` – `dotnet app/glebosaurus.dll run --quiet` jako `glebosaurus`, `Restart=on-failure` co 60 s
       bez limitu prób, `TimeoutStopSec=300` (SIGTERM = czyste zatrzymanie po bieżącym odczycie), `ConditionPathExists`
       na konfigurację, utwardzenie `NoNewPrivileges`, `ProtectSystem=full`, `ProtectHome`, `PrivateTmp`;
       `glebosaurus-update.timer` (5 min po starcie systemu i codziennie o 03:30, `Persistent`) →
       `glebosaurus-update.service` (`install-linux.sh --auto-update`; brak sieci – ponowienie po 30 min). Polecenie
       `/usr/local/bin/glebosaurus` uruchamia agenta jako `glebosaurus` w `data/` (`sudo glebosaurus enroll`).
     - **Samoaktualizacja:** `SHA256SUMS` najnowszego wydania (`releases/latest/download`) mówi, jaka jest wersja paczki
       dla tego systemu (bez API GitHuba i parsowania JSON w bashu); nowsza → skrypt **z tego wydania**, sprawdzony z jego
       `SHA256SUMS`, ślad próby i przekazanie (`--unattended`, dziennik w `updates/install-last.log` i w journalctl).
       Skrypt pobiera paczkę, sprawdza skrót, rozpakowuje, sprawdza wymagany runtime (`tfm` z `runtimeconfig.json`) i
       uruchamia nowy program (`--version` jako `glebosaurus`) – wszystko **przed** zatrzymaniem agenta; potem
       `systemctl stop`, podmiana `app/` (stara do `app.old/`), start. Gdy agent działał przed aktualizacją, nowa wersja
       musi przepracować 45 s bez restartu – inaczej wraca poprzednia (nieudana zostaje w `app.failed/`). Każda porażka
       zapisuje ślad: ta wersja czeka dobę (bez pętli), a agent wraca do pracy w dotychczasowej wersji.
     - **Start:** bez konfiguracji albo bez tokenu agenta skrypt usługi nie uruchamia, tylko podaje kolejne kroki.
     - **Jedna instancja:** nazwany muteks .NET z „Local\” obejmuje na Linuksie jedną sesję, a usługa i terminal to
       różne sesje – dlatego `run` na Linuksie bierze blokadę pliku `glebosaurus.run.lock` obok konfiguracji
       (`InstanceLockFile`, `FileShare.None` = `flock`); drugi agent z tą samą konfiguracją kończy się kodem 4.
     - **Próby:** `deploy/test-install-linux.sh` – instalacja z paczki, samoaktualizacja z udawanego wydania (`file://`),
       zepsute wydanie, powrót do starszej wersji, odinstalowanie; z `--systemd` (root) pełna usługa bez startu. CI
       uruchamia go przy każdym pushu na `ubuntu-24.04-arm`, a wydanie instaluje swoją paczkę na arm64 przed publikacją.
8. **Odczyty.** Agent słucha reklam i łączy się, gdy czujnik jest widoczny, a odczyt należny (odstęp od ostatniego
   odczytu w bazie – własnego albo z kopii odczytów innych agentów). Przy nowej epoce dzierżawy (przejęcie czujnika, start
   agenta) zbieracz przed pierwszym zwykłym odczytem raz pyta chmurę o ostatni odczyt czujnika (`readings_range`) – odczyt
   poprzedniego właściciela mógł jeszcze nie dojść przez kopię odczytów, a agent bez panelu kopii nie ma; bez sieci czyta
   według lokalnej bazy. **Nasłuch na przemian z odczytem**
   (`ListenThenReadSensorReader` w Core, radio za `ISensorRadio`): gdy w reklamach pojawi się którykolwiek z należnych
   czujników, nasłuch się kończy, czujnik jest czytany od razu, potem nasłuch wraca po pozostałe. Radio nigdy nie słucha i
   nie łączy się jednocześnie (na Pi Wi-Fi i BLE dzielą układ). Okno `scanWindowSeconds` (domyślnie 60 s) liczy tylko
   nasłuch, łącznie ze wszystkich rund; czujnik, którego nie było przez całe okno, dostaje „nie znaleziono”. Ponowienia
   po porażce rosną (2, 5, potem co 10 min, nigdy dłużej niż odstęp odczytów) i tylko odsuwają odczyt, a odczyt na żądanie pomija i odstęp, i
   ponowienia. **Błąd nasłuchu:** skan Shiny na Windows potrafi przerwać się `COMException 0x80004005` (Shiny pyta system o
   każde słyszane urządzenie bez obsługi błędu), więc nieudany nasłuch jest wznawiany po 1 s w tym samym oknie, najwyżej 3
   razy z rzędu, a potem czekające czujniki dostają „błąd odczytu” i działa zwykły odstęp ponowień. **Sesja dłuższa niż
   limit:** sesja trwa najwyżej 180 s, a czujnik z długą historią oddaje ją na raty (ok. 110 rekordów na sesję).
   `TuyaReadResult.Truncated` mówi, że sesję urwał limit; bez zwykłego raportu stanu (`SoilSensorProfiles.MapSession`)
   nie ma wtedy stanu bieżącego, a rekordy zostają z czasem czujnika. Radio zwraca `SensorReadFailure.Incomplete`;
   zbieracz zapisuje rekordy od razu (czujnik już je skasował), wraca po resztę po 2 min (czujnik odpoczywa po sesji do 3 min) i nie liczy tego jako porażki
   dzierżawy. Parowanie to osobna, interaktywna komenda (okno czujnika ok. 20 s – skan musi już działać, gdy użytkownik
   wkłada baterie); przy kilku niesparowanych kandydatach wymaga `--mac`.
9. **Alerty i raporty.** Liczy je nadawca z odczytów wszystkich agentów (`AlertEvaluator`): progi per czujnik z
   histerezą i kilkoma kolejnymi odczytami („czas podlać”), niska bateria (DP15 – docelowo trend albo DP14), „brak
   odczytów”. **Cisza** liczy się od ostatniego odczytu (czujnik bez odczytów – od startu), także za czas, gdy nadawca nie
   działał; po starcie nadawcy i po przerwie w przeliczaniu alertów dłuższej niż 10 min (uśpienie, długo bez blokady) jest
   jednak 30 min okresu ochronnego (`SilenceGrace`) – nowy alert o ciszy nie powstaje, a otwarty trwa. Bez tego komputer
   włączony po nocy wysyłałby alerty, zanim zdąży cokolwiek odczytać; liczenie ciszy dopiero od startu odrzucone – martwy
   czujnik przy komputerze włączanym na kilka godzin dziennie nie dałby alertu nigdy. Raporty HTML o stałych porach (w
   strefie z konfiguracji chmury), nadrabiane do `catchUpHours`. Wysyłka: Gmail SMTP (MailKit, smtp.gmail.com:465, hasło
   aplikacji). Odbiorcy alertów i raportów to osobne listy; adresy są walidowane, przycinane i pozbawiane powtórzeń.
   **Maile są projektowane pod telefon:** HTML to jedna kolumna do 480 px (karta na czujnik, trzy duże liczby: wilgotność,
   temperatura, bateria, pasek wilgotności względem progów), style wyłącznie inline, bez sztywnych szerokości. Elementy
   kwadratowe (żadnych zaokrągleń), pastelowe kolory, ciemny tekst (kontrast ≥ 5:1), nigdy biały tekst na nasyconym tle;
   paleta to `Tone` w `MailLayout.cs`, a testy pilnują braku `border-radius` i `color:#fff`; wersja tekstowa ma wiersze
   ≤ 40 znaków. Tytuł zaczyna się od tego, co ważne („Monstera – czas podlać”, „Poranny raport – do podlania: Monstera,
   Fikus +1”); pora dnia w tytule raportu wynika z zaplanowanej godziny. **Sekcja „Urządzenia”** pod czujnikami: karta
   każdego agenta-telefonu (`android`: bateria, temperatura akumulatora, ładowarka) i agenta-Raspberry Pi (`linux`:
   temperatura procesora) z listy agentów ostatniej synchronizacji (`AgentDevice.ForReport`), w stylu karty czujnika;
   ostrzeżenia te same co w panelu (`AgentDevice.Warnings`), a „milczy” – ten sam próg 30 min co mail „agent milczy”
   (wartości z ostatniego kontaktu). Urządzenie z ostrzeżeniem albo milczące liczy się do „Uwag” w pasku i tytule;
   „do podlania” zostaje na pierwszym miejscu. Komputer z Windows nie zgłasza baterii ani temperatury – nie ma karty. Ukryty tekst podglądu pokazuje wilgotność każdej
   rośliny. `glebosaurus mail-preview` zapisuje przykładowe maile do obejrzenia; prawdziwy próbny mail wysyła nadawca na
   polecenie ze strony (`mail_test`).
   **Wykres w raporcie** z samego HTML (`MailChart`) – Gmail nie pokazuje SVG, a `data:` nie działa: tabela z kolumnami
   po 2 godz. (czas lokalny), w kolumnie niebieska kreska na wysokości średniej wilgotności (skala 0–100%) i pod nią blady
   pasek temperatury od najniższej do najwyższej, różowe tło = średnia poniżej progu podlania, pusta kolumna = brak
   odczytów. Położenie bez pozycjonowania (`valign=bottom`, dopełnienie od dołu, bloki o zadanej wysokości). Gmail
   przycina HTML powyżej ok. 102 KB, więc raport ma budżet 90 KB: przedziały po 2 godz. mieszczą się do ok. 9 czujników,
   przy większej liczbie rosną (3 → 4 → 6 → 8 → 12 godz.), a gdy nic się nie mieści – raport bez wykresów. Wersja
   tekstowa: linijka `▁…█` ze średnich z 6 godz. Stopka podaje wersję (`AppInfo.Version`, jedno źródło:
   `Directory.Build.props`).
   **Log administratora:** dziennik agenta to pliki dzienne `glebosaurus-RRRRMMDD.log` (`DailyLogFiles`,
   `FileLoggerProvider`, retencja `logs.retentionDays`). Zbieracz wysyła wczorajszy dziennik do chmury w pierwszym cyklu
   dnia, a nadawca od 01:00 wysyła pod `mail.adminRecipients` osobny mail z dziennika każdego agenta: streszczenie
   (liczniki, ostatnie ostrzeżenia i błędy) i pełny plik jako załącznik (limit 5 mln znaków, obcinany od początku).
   „**Agent milczy**”: agent bez synchronizacji ponad 30 min → mail do administratora, raz na epizod ciszy.
10. **Chmura i UI.** **Supabase, darmowy plan**: Postgres z funkcjami SQL (operacje atomowe) i Vault; bez Edge Functions,
    bez Storage i bez własnego serwera. Własny serwer (VPS) i Azure odrzucone ze względu na koszt. Limity darmowego planu:
    - baza 500 MB, po przekroczeniu tylko do odczytu – odczyty to ok. 0,4 mln wierszy rocznie przy kilkunastu czujnikach,
      szacunkowo 50–80 MB, więc starczy na kilka lat (potem uśrednianie starych odczytów – ROADMAP, faza 4);
    - transfer 5 GB/mies. – synchronizacja kilku agentów co 90 s to setki MB;
    - uśpienie po 7 dniach bez ruchu – agenci wołają bazę co półtorej minuty; po tygodniowej awarii trzeba ręcznie wznowić projekt
      (dane zostają), a ciszę zgłosi strażnik (zasada 2);
    - **brak kopii zapasowych** – robi je `cloud backup` (`CloudBackup` w `Glebosaurus.Cloud.Admin`): bez `pg_dump`, jedna
      transakcja `REPEATABLE READ READ ONLY`, każda tabela schematu `glebosaurus` przez `COPY … TO STDOUT` jako CSV (bez
      `agent_logs`), `manifest.json` (projekt, czas, wersja, migracje z sumami, liczby wierszy) i `secrets.json` z
      **odszyfrowanymi** sekretami z `vault.decrypted_secrets` (w surowym zrzucie są zaszyfrowane kluczem projektu, który
      przepada z projektem). Jeden plik ZIP, zapis atomowy, na Linuksie prawa 600, zostaje `keep` najnowszych. Ręcznie
      (`cloud backup`) albo codziennie w `run` jednego zaufanego agenta z sekcją `cloud.backup` (`DailyCloudBackup`: raz na
      dzień kalendarzowy, przy pierwszej okazji, co najmniej 10 min po starcie) – ten agent ma plik administratora z
      hasłem bazy, bez klucza tajnego. Kopia jest sekretem jak plik administratora. Odtwarzania z kopii jeszcze nie ma
      (ROADMAP, faza 4);
    - 2 aktywne projekty – drugi przydaje się do testów funkcji SQL (zasada 2); też usypia po tygodniu bez ruchu.
    Połączenie właściciela z bazą idzie przez pooler sesji (IPv4; bezpośrednie `db.<id>.supabase.co` ma tylko IPv6) jako
    `postgres.<id projektu>`.
    **Interfejs w chmurze** (faza 3, w budowie – ROADMAP, etapy): statyczna strona w katalogu `web/`, publikowana na
    GitHub Pages (`.github/workflows/pages.yml`: z wydaniem i ręcznie). Zwykły JS (moduły ES) bez kroku budowania i bez
    npm – jak lokalny panel, z którym dzieli `charts.js` i część `common.js`; Supabase Auth i funkcje SQL przez zwykły
    `fetch` (bez supabase-js).
    - **Adres projektu nie jest w kodzie** (repozytorium jest publiczne): strona dostaje go z linku
      `…#url=<adres projektu>&key=<klucz publikowalny>` (`cloud web-link [--qr]`; fragment nie trafia do serwera) i
      zapamiętuje w przeglądarce. Klucz publikowalny nie jest sekretem – chroni logowanie i `require_owner()` (zasada 4).
    - **Sesja:** hasło → token `aal1` → kod TOTP (przy pierwszym logowaniu strona dodaje TOTP i pokazuje jego kod QR z
      odpowiedzi Supabase) → token `aal2`; token odświeżania w `localStorage` do wylogowania, token dostępu tylko w pamięci
      i odświeżany 5 min przed wygaśnięciem. Supabase wymienia token odświeżania przy każdym użyciu, więc karty
      przeglądarki odświeżają pod wspólną blokadą (Web Locks) i zawsze ostatnim zapisanym tokenem, a równoległe
      wywołania w jednej karcie czekają na jedno odświeżenie; limit zapytań (429) nie kończy sesji. Odnośnik do innego
      projektu niż zapamiętany strona przełącza dopiero po potwierdzeniu (przełączenie kończy sesję). Ograniczenie: strony
      `<konto>.github.io` jednego konta GitHub dzielą źródło (origin), a więc i `localStorage` – na tym koncie nie publikuje
      się stron z obcym kodem (README).
    - **Dane:** tylko `public.owner_*`; `owner_info` zwraca wersję schematu, więc strona nowsza niż baza mówi, której
      migracji brakuje. Zapisy niosą `config_version` (kontrola współbieżności – druga karta nie nadpisze cudzej zmiany).
      Zmiany protokołu agentów tylko przez dodawanie pól i funkcji, więc starsi agenci działają dalej. Odświeżanie co 60 s
      (bez Realtime), czas lokalny przeglądarki.
    - **Strona:** CSP w `<meta>` (GitHub Pages nie ustawia nagłówków) bez skryptów inline i z `connect-src` do Supabase,
      wyjście z ramki w JS (brak `frame-ancestors` w `<meta>`; dopóki skrypt nie potwierdzi, że strona jest na wierzchu,
      `app.css` jej nie pokazuje), dane wstawiane wyłącznie jako tekst, motyw jasny i ciemny (według systemu), układ pod
      telefon, manifest PWA bez trybu offline; podgląd maili (etap 4) w izolowanej ramce `sandbox` z osobną stroną i własną
      CSP. Pliki: `shell.js` (ramka, projekt z odnośnika, logowanie, TOTP, nagłówek, odświeżanie co 60 s przy widocznej
      karcie), `supabase.js` (Auth i RPC; błąd z funkcji – rodzaj `invalid` z polskim powodem, konflikt wersji –
      `conflict`), `ui.js` (formularze, potwierdzenia w miejscu zamiast okien przeglądarki), strony (`index.html` +
      `dashboard.js` – pulpit z alertami, wykresem 3 dób przy każdym czujniku i odczytem/raportem na żądanie,
      `sensor.html` + `sensor.js` – strona czujnika z przeglądalnym wykresem całej historii, `config.html` + `config.js`
      – edytor konfiguracji, `alerts.html` + `alerts.js` – alerty, wyciszenia i historia maili, `mail.html` + `mail.js` –
      podgląd maila: HTML z archiwum rozpakowany w przeglądarce (`DecompressionStream`) i pokazany w ramce `sandbox` bez
      żadnych uprawnień przez `srcdoc`; ta jedna strona ma w CSP `style-src 'unsafe-inline'`, bo maile mają style w
      atrybutach – skrypty nadal tylko z `'self'`, a ramka i tak ich nie uruchamia), `commands.js` (zlecenie przez `owner_command` i śledzenie wyniku co 5 s), `charts.js`
      (ten sam plik co w lokalnym panelu), `qr.js` (kod QR rejestracji telefonu
      jako SVG z biblioteki qrcode-generator, MIT, `vendor/qrcode.js` bez zmian – test pilnuje sumy), `format.js` – ten
      sam plik co w lokalnym panelu.
    - **Edytor konfiguracji** (3c): sekcje odczytów i raportów, poczty (adresy, hasło aplikacji, próbny mail – polecenie
      `mail_test` do obecnego nadawcy, który pobiera hasło na nowo i bez przerwy po wcześniejszym błędzie, a błąd SMTP
      zwraca w wyniku polecenia; strona śledzi wynik co 5 s), czujników (dodanie z pliku klucza z `pair` czytanego w
      przeglądarce, ustawienia, nowy klucz, archiwum i przywrócenie, usunięcie tylko bez odczytów), agentów (nowy agent z
      kodem rejestracji i QR odnośnika `glebosaurus://enroll` jak `EnrollmentLink`, rola i priorytet, wyłączenie,
      odcięcie) i strażnika. Po zapisie strona wczytuje konfigurację na nowo i pokazuje notkę przy zmienionym miejscu. Testy (`OwnerWebTests`) pilnują CSP i braku kodu inline, braku wstawiania HTML, zgodności kopii
      wspólnych plików z panelem i tego, że migracja wymagana przez stronę (`REQUIRED_MIGRATION`) definiuje każdą wołaną
      funkcję `owner_*`. Funkcje strony sprawdzają testy SQL (`OwnerTests`) z żądaniami tokenu ustawionymi jak przez Data
      API (`request.jwt.claims`): obce konto, brak `aal2` i rola `anon` – odmowa.
11. **Lokalny panel diagnostyczny** (podgląd, odczyt i raport na żądanie, bez zmian konfiguracji). Kestrel w procesie `run`
    (projekt `Glebosaurus.Web`, tylko framework ASP.NET Core), włączany sekcją `web` (port domyślnie 8470). Warstwy: filtr
    adresów źródłowych (loopback + sieci prywatne albo `web.allowedNetworks`) przed wszystkim innym; HTTPS z certyfikatem z
    podpisem własnym (ECDSA P-256, 825 dni, odnawiany 30 dni przed końcem, odcisk SHA-256 w dzienniku i w `web-password`);
    hasło jako skrót PBKDF2-SHA256 (600 tys. iteracji) w `glebosaurus-web.password` obok konfiguracji; sesje tylko w
    pamięci (losowy token 256 bit w ciasteczku `__Host-glebosaurus`, `Secure`/`HttpOnly`/`SameSite=Strict`), limit 5
    błędnych haseł na adres w 15 min; POST tylko z tego samego źródła – rozstrzyga `Sec-Fetch-Site`, a `Origin` tylko bez
    niego (przy restrykcyjnej polityce referrera przeglądarka wysyła `Origin: null` także z tej samej strony, stąd
    `Referrer-Policy: same-origin`), akcje dodatkowo wymagają nagłówka `X-Glebosaurus-Action`; CSP bez skryptów inline,
    `no-store`. Panel nigdy nie zatrzymuje agenta: brak hasła czy zajęty port to tylko wpis w dzienniku.
    **Dane:** `CloudPanel` składa status z konfiguracji z chmury, lokalnej bazy (własne odczyty i kopia odczytów innych
    agentów), migawki zbieracza (`CollectorEngine.State`: próby, ponowienia, sygnał, trwający odczyt), obrazu ostatniej
    synchronizacji (kto ma dzierżawę każdego czujnika, kto poza tym agentem słyszy go najlepiej, kto jest nadawcą, agenci z
    urządzeniem z ich sygnału życia – `AgentDevice`: program, bateria, temperatura, sieć i ostrzeżenia, które pokazuje też
    baner strony), stanu poczty nadawcy i alertów ze wspólnego stanu w chmurze (`alert_list`, najwyżej co 30 s). Wspólną część (czujniki
    i ich odczyty) składa `MonitorStatusBuilder` – pole po polu, nigdy przez serializację `AgentConfig`, więc klucze
    czujników i hasło Gmaila nie mają którędy wyjść (test). **Kopia odczytów wszystkich agentów** (`CloudReadingMirror`,
    tylko z sekcją `web`): co 2 min odczyty przyjęte przez chmurę po ostatnim znanym numerze (`readings_feed`, paczki po
    5000), zapisane jako już przyjęte (`sync_state` = 1 – nie wracają do wysyłki), z numerem w tabeli `cloud_feed`. Numer
    kolejny, nie czas pomiaru, bo odczyty przychodzą z opóźnieniem; `readings_feed` zwraca tylko wiersze „ustalone”
    (przyjęte co najmniej 30 s temu i poniżej najniższego niewidocznego jeszcze wiersza – bariera dla transakcji w toku).
    **Akcje przez chmurę:** „Odczytaj teraz” → `command_create('read_now')` dla każdego czujnika – chmura kieruje je do
    właściciela dzierżawy; „Wyślij raport teraz” (prawdziwy mail – strona pyta o potwierdzenie z listą odbiorców) →
    `report_now` do obecnego nadawcy. Ten sam czujnik i raport najwyżej raz na minutę; zlecony odczyt „czeka” w panelu,
    dopóki nie przyjdzie nowszy odczyt, najwyżej 6 min (odczyt innego agenta dochodzi przez jego synchronizację i kopię
    odczytów). **Strony:** statyczny HTML + JS wbudowany w bibliotekę, dane z
    JSON API, wstawiane wyłącznie jako tekst. Pulpit (`/`) ma przy każdym czujniku wykres ostatnich 72 h; strona czujnika
    (`/sensor?id=…`) – wszystko o jednym czujniku z przeglądalnym wykresem całej historii. Dane wykresów:
    `GET /api/sensors/{id}/series?from&to&points` → `ReadingSeriesBuilder` (Core): dopóki odczytów jest nie więcej niż
    `points`, zwraca je wprost, inaczej uśrednia w przedziałach z drabinki 30 min … 28 dni (średnia, min, max, liczba) w
    czasie lokalnym; dziura = odstęp dłuższy niż przedział + 3 interwały odczytu. Rysowanie: `wwwroot/charts.js`, czysty
    SVG, bez bibliotek; wilgotność `#2a78d6` (oś zawsze 0–100%), temperatura `#eb6834` (oś dopasowana, min. 2 °C).
12. **Agent na Androida** – tylko zbieranie, bez roli nadawcy; telefon na Wi-Fi. .NET for Android
    (`src/Glebosaurus.Android`, `net10.0-android`, Android 10+, bez MAUI – jedna aktywność i usługa) z `Glebosaurus.Tuya`,
    częściami agenta z Core i buforem SQLite (`Glebosaurus.Data`) – protokół nie powstaje drugi raz. Projekt jest poza
    `Glebosaurus.slnx` (wymaga obciążenia `android`, JDK i Android SDK); buduje go `deploy/build-android.ps1`, w CI osobne
    zadanie `android`.
    - **Radio na API Androida, nie Shiny:** Shiny.BluetoothLE na Androidzie filtruje skan tylko po UUID usług, a skan bez
      filtra system wstrzymuje przy zgaszonym ekranie; Shiny prosi też o uprawnienia przez aktywność, a agent pracuje w
      usłudze od startu telefonu. `AndroidRadio` (`ISensorRadio`, `ISensorListener`) skanuje zawsze z filtrem po MAC
      czujników z konfiguracji (`ScanFilter.setDeviceAddress`), `GattConnection` (`IGattLink`) łączy się tylko po LE bez
      automatycznego ponawiania, czeka na potwierdzenie każdej operacji GATT, ponawia błąd połączenia (np. 133) do 3 razy i
      zawsze kończy `disconnect()` + `close()`. Złożenie reklam niezależne od biblioteki: `AdvertisedDevice` w Core.
    - **Starty skanu:** Android po cichu „głuchnie” aplikacji, która uruchomi skan więcej niż 5 razy w 30 s, więc
      `ScanStartLimiter` (Core) przepuszcza najwyżej 4 starty na 32 s, a nasłuch po błędzie skanu wraca po 8 s.
    - **Tylko zbieracz:** `CloudRuntime` bez nadawcy i `NotifierLockOptions.CollectorOnly` – telefon nie sięga po blokadę
      nadawcy nawet z rolą nadawcy nadaną w chmurze przez pomyłkę. Kopia odczytów wszystkich agentów jest włączona.
    - **Usługa pierwszoplanowa** `connectedDevice` ze stałym powiadomieniem i blokadą uśpienia procesora (procesor nie
      zasypia, więc o baterii telefonu decydują radia: skan BLE, połączenia, Wi-Fi). Tekst powiadomienia
      (`AgentScreenText.Notification`) powstaje ze stanu w pamięci – bez bazy i bez zapytań do chmury – i trafia do systemu
      tylko przy zmianie: liczba czytanych czujników, kłopot z chmurą (wtedy z godziną); `AgentHost`
      podnosi przebieg zakończony błędem po 1, 2, 5, potem co 15 min. Start po restarcie telefonu i po aktualizacji
      aplikacji (`BOOT_COMPLETED`, `MY_PACKAGE_REPLACED`) – z blokadą ekranu dopiero po pierwszym odblokowaniu (wcześniej
      dane aplikacji są zaszyfrowane; Direct Boot – niezrobione).
    - **Uprawnienia:** Android 12+ `BLUETOOTH_SCAN` (`neverForLocation`) i `BLUETOOTH_CONNECT`, Android 10–11 lokalizacja;
      powiadomienia (13+); wyjątek z optymalizacji baterii; Autostart MIUI – skrót do listy. Bez kopii w chmurze Google.
    - **Sekrety:** token i kopia konfiguracji – `ProtectedFile` z `KeystoreProtector` (AES-GCM, klucz w Android Keystore);
      odinstalowanie aplikacji je kasuje – potem nowa rejestracja.
    - **Rejestracja kodem QR:** `cloud agent … --confirm --qr PLIK.png` koduje `EnrollmentLink` –
      `glebosaurus://enroll?url=…&key=…&code=…` (adres projektu i klucz publikowalny z pliku administratora, kod
      jednorazowy). Telefon czyta go skanerem z Usług Google Play (aplikacja nie ma uprawnienia do aparatu), z wklejonego
      tekstu albo z odnośnika.
    - **Ekran:** ten sam `MonitorStatus` co w panelu, jako tekst (`AgentScreenText`): ostrzeżenia, agent, telefon,
      czujniki, alerty, agenci, „Przygotowanie telefonu” i ostatnie wpisy dziennika. **Podgląd zasięgu**: 3 min skanu bez
      filtra z mocą sygnału na żywo – do wyboru miejsca na telefon.
    - **Sygnał życia telefonu** (`PhoneState`): bateria, ładowarka, temperatura akumulatora, stan cieplny systemu
      (`PowerManager.getCurrentThermalStatus`), rodzaj sieci i siła Wi-Fi, stan Bluetooth. Telefon niczego nie wystawia –
      dane jadą w każdym `agent_sync`, a chmura oddaje je innym agentom, więc panel WWW komputera pokazuje stan telefonu z
      jego ostatniej synchronizacji. Opis i ostrzeżenia wspólne dla ekranu i panelu (`AgentDevice`; telefon może stać na
      zewnątrz): akumulator od 45 °C albo poniżej 0 °C (w mrozie się nie ładuje, telefon potrafi się wyłączyć), system
      ograniczający wydajność z powodu temperatury (od `moderate`), bateria poniżej 30% bez ładowarki (sama praca na baterii
      nie jest ostrzeżeniem; na ładowarce słaba bateria się ładuje), sieć komórkowa zamiast Wi-Fi. Osobnego alertu
      mailowego o temperaturze telefonu nie ma – stan telefonu z ostrzeżeniami jest w każdym raporcie (sekcja
      „Urządzenia”, zasada 9).
    - **Wydania:** APK podpisany jednym kluczem wydań (jego utrata = aktualizacja tylko przez odinstalowanie i nową
      rejestrację); w wydaniu z tagu, gdy repozytorium ma sekrety `ANDROID_KEYSTORE_BASE64` i `ANDROID_KEYSTORE_PASSWORD`.
      Kod wersji z `Directory.Build.props` (0.2.1 → 201).
    - **Ograniczenia Androida/MIUI** (README): zabijanie aplikacji w tle (Autostart, oszczędzanie baterii „Bez ograniczeń”,
      blokada w ostatnich aplikacjach), Smart Life na tym samym telefonie walczyłby o połączenia.
13. **Schemat chmury** (`supabase/migrations/`; zastosowanej migracji nie wolno zmieniać – suma kontrolna – zmiana
    schematu to nowa migracja):
    - **Schemat `glebosaurus`** (Data API go nie wystawia, RLS na każdej tabeli bez żadnych zasad): `agents` (skrót tokenu,
      `can_send_mail`, `sender_priority`, sygnał życia), `enrollment_codes`, `settings` (jeden wiersz; strefa raportów,
      `config_version` rośnie przy każdej zmianie ustawień i czujników), `sensors` (progi jak `SensorConfig`; dane Tuya w
      Vault jako `glebosaurus:sensor:<id>`), `readings` (klucz `(sensor_id, at)`, temperatura w dziesiątych częściach, agent,
      numer kolejny i chwila przyjęcia), `sightings`, `leases` (wiersz zostaje po oddaniu, więc epoka tylko rośnie),
      `mail_outbox`, `alerts` (jeden otwarty na czujnik i rodzaj), `commands`, `agent_logs`, `environment` (znacznik bazy
      próbnej), `owners` (właściciele strony w chmurze – faza 3) i `schema_migrations`.
    - **API agentów** – funkcje `public.*` `security definer` z pustym `search_path`, wykonywalne tylko przez `anon`:
      `enroll`, `get_config`, `agent_sync`, `lease_claim`, `lease_release`, `mail_claim`, `mail_sent`, `mail_abandon`,
      `mail_credentials`, `readings_range`, `readings_feed`, `alert_list`, `alert_open`, `alert_resolve`, `alert_notified`,
      `command_create`, `command_done`, `log_put`, `log_get`. Nieznany albo unieważniony token → SQLSTATE 28000 (Data API:
      403); czynność nadawcy bez blokady z aktualną epoką → 42501; błędne dane → 22023 (400). Czasy z agenta są względne
      (`heardSecondsAgo`), bo jego zegar może się rozjechać z bazą; odczyty z przyszłości (ponad 10 min), sprzed 2020 i spoza
      zakresów baza odrzuca z powodem, żeby agent ich nie ponawiał. `agent_sync` najpierw oddaje, potem bierze dzierżawy w
      stałej kolejności (bez zakleszczeń).
    - **Lista agentów** w odpowiedzi `agent_sync` niesie ich ostatni sygnał życia przez `glebosaurus.agent_status_view`:
      tylko znane pola o właściwym typie, w zakresie i o ograniczonej długości (bez wersji i platformy – nic), bo status
      zapisuje każdy agent dowolnym obiektem, a pole złego typu nie może zepsuć synchronizacji innym. Nowe pole sygnału
      życia = nowa migracja.
    - **Alerty:** czytać może każdy agent, otwierać, zamykać i oznaczać – tylko obecny nadawca. Otwarcie już otwartego
      zwraca istniejący alert, więc restart nadawcy nie tworzy drugiego epizodu.
    - **Polecenia** (`read_now`, `report_now`, `release`, `pair`, `send_log`): bez wskazanego agenta trafiają tam, gdzie jest
      władza – odczyt i oddanie do właściciela dzierżawy, raport do obecnego nadawcy. Wracają w każdej synchronizacji, dopóki
      agent nie zgłosi wyniku albo nie wygasną (10 min; parowanie 15 min, log 1 godz.). To samo polecenie najwyżej raz na
      minutę. Agent może zlecić tylko `read_now` i `report_now`; resztę właściciel (`glebosaurus.admin_command`).
    - **Dzienne logi agentów:** tabela zamiast Storage (Storage wymaga zalogowanego użytkownika albo klucza tajnego).
      Agent pakuje log gzipem (`log_put`, najwyżej 4 MB po spakowaniu), czyta go tylko obecny nadawca (`log_get`).
      Retencja `settings.log_retention_days`.
    - **API strony właściciela** (faza 3, zasada 4) – funkcje `public.owner_*` `security definer` z pustym `search_path`,
      wykonywalne tylko przez `authenticated`, każda zaczyna od `glebosaurus.require_owner()` (konto z
      `glebosaurus.owners` i token z `aal2`, inaczej 42501 z powodem `mfa_required` albo `not_owner`): `owner_info`
      (konto, czas bazy, lista migracji), `owner_dashboard` (czujniki spoza archiwum z ostatnim odczytem i dzierżawcą,
      agenci z sygnałem życia przez `agent_status_view`, nadawca), `owner_config` (cała konfiguracja; sekrety tylko jako
      data ustawienia z `vault.secrets`), zapisy z `config_version` (`owner_settings_update`, `owner_sensor_add|update|
      credentials|archive|delete`; inna wersja w bazie → PT409 „conflict”, przez Data API HTTP 409; niżej „Środowisko
      Data API”), `owner_secret_set` (hasło Gmaila, adres
      strażnika; null – usunięcie), `owner_agent_save|code|revoke`, `owner_command` (`read_now`, `report_now`,
      `mail_test`, `send_log`) i `owner_command_status`, `owner_series` (dane wykresu: uśrednianie w bazie jak
      `ReadingSeriesBuilder` – ta sama drabinka, `date_bin` w czasie lokalnym od poniedziałku 0001-01-01, ta sama reguła
      dziur; strefa przeglądarki, nieznana – strefa raportów; pilnuje `SeriesContractTests` na tych samych odczytach co
      C#, także przy zmianie czasu), `owner_sensor` (strona czujnika: ustawienia, ostatni odczyt, dzierżawa, słyszenia,
      alerty, produkt Tuya bez kluczy) i `owner_readings`; alerty (3b): `owner_alerts` (otwarte, zamknięte z 30 dni,
      wyciszenia), `owner_alert_acknowledge`, `owner_alert_close` (`closed_manually`; gdy stan trwa, nadawca otwiera nowy
      epizod), `owner_alerts_mute` (czujnik albo wszystkie, najwyżej 90 dni; zmiana podnosi wersję konfiguracji),
      `owner_mail_history` i `owner_mail_body`; parowanie zdalne (3d): `owner_pair_start` (agent zarejestrowany i
      włączony, id czujnika, opcjonalnie MAC; jedno parowanie naraz na agenta), `owner_command_status` z postępem. **Agenci (3b):** `get_config` niesie `alertsMutedUntil` (ustawienia i
      czujniki), `alert_list` – `acknowledgedAt`; nadawca nie wysyła maili o potwierdzonych alertach i o wyciszonych
      czujnikach (alert żyje dalej, mail wychodzi po końcu wyciszenia), w urlopie – żadnych maili o alertach, a raport ma
      notkę o wyciszeniu i szarą etykietę przy wyciszonym czujniku. **Archiwum maili** (`mail_archive`, 90 dni): nadawca po
      udanej wysyłce raportu, alertu, maila próbnego i „agent milczy” zapisuje HTML spakowany gzipem (`mail_archive_put` –
      tylko obecny nadawca i tylko mail, który sam zarezerwował i odnotował jako wysłany; błąd archiwum nie dotyczy
      maila; strona rozpakowuje najwyżej 5 MB HTML). Walidacja tylko w bazie (strefa IANA z
      `pg_timezone_names`, odstęp ≥ 0,5 min, okno skanu 5–120 s, progi 0–100 z „koniec” > „podlać”, 1–10 odczytów z rzędu,
      adresy bez imion i z kropką w domenie – ujednolicone i bez powtórzeń, plik klucza: MAC, UUID 16 znaków, deviceId,
      klucz 6 bajtów, znany productId, MAC i UUID niepowtarzalne); pilnuje tego `ConfigContractTests` – zestaw przypadków
      dobrych i złych na funkcjach strony (do 2026-11-21 ten sam zestaw sprawdzał też plik konfiguracji chmury). **Archiwum czujnika** (`archived_at`, zawsze wyłączony):
      `get_config` go pomija (agent traktuje to jak usunięcie), pulpit też, otwarte alerty zamykają się; odczyty i klucz
      zostają, przywrócony wraca wyłączony. Tabela `owners` bez klucza obcego do `auth.users` (schemat Supabase; kopia
      i testy od niego nie zależą) – usunięte konto i tak się nie zaloguje, a natychmiast odcina `cloud owner --remove`.
    - **Administracja** (`glebosaurus.admin_*`: agent i kod rejestracji, unieważnienie, czujnik, dane Tuya, sekrety poczty,
      ustawienia, właściciele strony) – tylko przez połączenie z bazą hasłem bazy (`Glebosaurus.Cloud.Admin`), nigdy przez Data API; klucz
      tajny `sb_secret_…` nie jest programowi potrzebny (używają go tylko testy na próbnym projekcie – konto testowe w
      Auth). Migracje stosuje `MigrationRunner` (blokada doradcza, suma kontrolna).
    - **Środowisko Data API** różni się od połączenia bezpośredniego, którym idą migracje, administracja i większość testów
      SQL: rola `authenticator` ładuje `pg_safeupdate` (UPDATE/DELETE bez WHERE → 21000, także w wyzwalaczach – stąd
      `where id` przy jedynym wierszu `settings`; pilnuje `SqlSafeUpdateTests`), rola `authenticated` ma limit czasu zapytań
      (funkcja strony dłuższa niż on zawiedzie z 57014), argumenty dopasowuje się po nazwach (zła nazwa – 404 PGRST202), a
      PostgREST ponawia transakcję po SQLSTATE 40001 – w pętli, ok. 1000 razy na sekundę przez ok. 50 s, także po zerwaniu
      połączenia przez klienta. Dlatego żadna funkcja nie zgłasza kodów z klasy 40 (pilnuje `SqlDataApiRulesTests`);
      konflikt wersji ma kod PT409 – PostgREST oddaje kod PTxyz jako status HTTP xyz. Kody HTTP błędów: 22023 → 400,
      42501 → 403 z tokenem użytkownika, 401 bez niego (sam klucz publikowalny), PT409 → 409, zepsuty token → 401. Te różnice łapią testy przez
      prawdziwe Supabase Auth i Data API (`DataApiOwnerTests`, niżej).
    - **Czas:** wszystko liczy `glebosaurus.clock()` = `now()`; tylko w bazie ze znacznikiem próbnej testy przesuwają zegar
      ustawieniem `glebosaurus.test_now`.
    - **Połączenie z bazą:** serwer ma certyfikat od własnego CA Supabase, którego systemowy magazyn nie zna. Certyfikat
      główny „Supabase Root 2021 CA” (publiczny, ważny do 2031-04-26) jest wbudowany w `Glebosaurus.Cloud.Admin` z
      odciskiem SHA-256 zapisanym w kodzie, a połączenie jest zawsze `VerifyFull` – bez cichego przejścia na samo
      szyfrowanie, bo idzie nim hasło bazy. Test przypomni o nowym certyfikacie pół roku przed końcem.
14. **Agent w Core** (`Core/Cloud`, testowany na atrapach z ręcznie sterowanym czasem):
    - **(a) Protokół** (`Protocol.cs`): typy 1:1 z funkcjami SQL, `ICloudApi`; serializacja przez generator źródeł (bez
      refleksji – Android jest przycinany). Odczyt ma czas w pełnych milisekundach UTC (jak SQLite), więc klucz
      `(sensor_id, at)` jest ten sam po obu stronach. Sekrety są poza `ToString`. Błędy mają rodzaj (`CloudErrorKind`):
      `Unauthorized` (agent przestaje), `NotNotifier` (stracił blokadę), `Invalid` (błąd programu, bez ponowień),
      `Unavailable` (sieć, 5xx, 429 – ponowić później), `Protocol` (np. baza bez migracji). Klient HTTP
      (`src/Glebosaurus.Cloud`, `SupabaseCloudApi`) to sam `HttpClient`, ten sam dla Windows, Pi i Androida. Księga
      dzierżaw (`LeaseLedger`) i zasady przydziału (`SensorLeasePolicy`, `NotifierLockPolicy`) – zasada 2.
    - **(b) Bufor odczytów i synchronizacja:** `IReadingOutbox` (w SQLite kolumna `sync_state`: 0 – do wysłania,
      1 – przyjęty, 2 – odrzucony), pętla `CloudSync` – co 90 s jedno `agent_sync` z najwyżej 500 najstarszymi
      niewysłanymi odczytami; zaległy bufor idzie paczkami co 2 s; po porażce odstępy 30 s, 1, 2, 5 min; utrata
      odpowiedzi kończy się ponownym wysłaniem bez dubli; przy zatrzymaniu – ostatnia paczka i oddanie dzierżaw.
    - **(c) Konfiguracja z chmury:** `CloudConfigMapper` składa `AgentConfig` z chmury (czujniki, progi, odstępy, raporty,
      nazwa agenta) i z pliku (baza, retencja, panel); czujnik z błędnymi danymi jest pomijany z ostrzeżeniem. Strefa
      raportów to nazwa IANA – agent działa z `InvariantGlobalization`, więc na Windows zapasowa mapa najczęstszych stref.
      `CloudConfigCache` – ostatnia dobra kopia (z kluczami Tuya) chroniona `ILocalProtector`. `CloudConfigSource` – start
      z chmury, bez niej z kopii, bez obu – czytelny błąd; odświeżanie przy nowej wersji albo zmienionej roli agenta.
    - **(d) Zbieracz** (`CollectorEngine`): cykl co 60 s, czyta tylko czujniki z dzierżawą starczającą na odczyt; co 4 min
      30 s nasłuchu wszystkich czujników (`ISensorListener`) dla przydziału, co godzinę podsumowanie nasłuchu w dzienniku
      (nasłuchy, ilu czujników słychać, pakiety na czujnik – do oceny zmian radia); polecenia `read_now`, `release`, `send_log`;
      dziennik z poprzedniego dnia do chmury. Alertów nie liczy. **Bez radia** (agent z `"bluetooth": false`, np.
      Raspberry Pi jako sam nadawca – ROADMAP 2.3a): zbieracz dostaje `null` zamiast odczytu i nasłuchu – nie słucha i nie
      czyta, a synchronizacja nie przekazuje czujników do przydziału, więc agent o żaden się nie ubiega; zlecony odczyt
      (chmura kieruje go i tak do właściciela dzierżawy) zgłasza jako niewykonalny. Dziennik do chmury, `send_log`,
      `release` i sprzątanie bazy działają jak zwykle, a sygnał życia ma „Bluetooth: wyłączony w konfiguracji”.
    - **(e) Nadawca** (`NotifierEngine`): pracuje tylko z blokadą ważną co najmniej minutę i z hasłem Gmaila na tę epokę;
      pętla co 60 s, alerty przelicza co 5 min (odczyty przychodzą co ok. 2 godz.).
      Każdy mail przez kolejkę: `mail_claim` → SMTP → `mail_sent`; pewna porażka SMTP (`MailTransportException.MaybeDelivered
      = false`) oddaje rezerwację i jest ponawiana po 5 min, niepewna zostawia rezerwację – mail może przepaść, ale nie
      pójdzie drugi raz. Alerty z odczytów wszystkich agentów (`readings_range`), raporty o stałych porach i na żądanie
      (75 h odczytów na wykres; telefony i Raspberry Pi z listy agentów ostatniej synchronizacji – sekcja „Urządzenia”),
      log administratora osobno od każdego agenta, „agent milczy”, strażnik healthchecks.io.
15. **Agent i narzędzia właściciela** (`src/Glebosaurus.Agent`).
    - **Plik agenta** (`AgentConfigLoader`): sekcja `cloud` (`url`, `publishableKey`, opcjonalnie `tokenFile`,
      `configCacheFile`, `backup`) i ustawienia urządzenia (`database`, `retentionDays`, `logs`, `web`, `monitorName`,
      `bluetooth`). `"bluetooth": false` – agent bez radia (zasada 14d): `run` w ogóle nie otwiera BLE (nie potrzeba
      adaptera, BlueZ ani uprawnień), a przy starcie ostrzega, gdy agent nie ma też roli nadawcy (wtedy poza dziennikiem
      nic nie robi). To ustawienie urządzenia, nie rola w chmurze: zależy od tego, gdzie stoi i co potrafi sprzęt
      (decyzja 2026-11-19 – bez migracji i zmian protokołu).
      Ustawienia wspólne dla agentów (`sensors`, `mail`, `reports`, `alerts`, `readIntervalMinutes`, `scanWindowSeconds`)
      są odrzucane z wyjaśnieniem – byłyby po cichu pomijane. Adres musi być projektem Supabase, a klucz publikowalnym:
      `sb_publishable_` i dalej tylko litery, cyfry, „_” i „-” (jedzie w nagłówku HTTP) – niewypełniony wzór
      (`sb_publishable_…`) albo wklejony nadmiar (spacja, cudzysłów, znak spoza ASCII) daje błąd ze wskazaniem znaku i
      pozycji już przy odczycie pliku, także w kodzie QR telefonu.
    - **Rejestracja** (`enroll [--code KOD]`, bez `--code` pyta bez pokazywania): kod → token w
      `glebosaurus-cloud.token` obok konfiguracji (`AgentTokenStore`, `ProtectedFile`: nagłówek, DPAPI / prawa 600, zapis
      atomowy). Ponowna rejestracja zastępuje token.
    - **Praca** (`run`): jeden agent na konto (Linux: na plik konfiguracji – zasada 7), sygnały zatrzymania, BLE (o ile
      nie wyłączone), SQLite, potem `CloudRuntime`: konfiguracja z
      chmury albo z kopii, a obok siebie pętle synchronizacji, zbieracza i nadawcy; po każdej synchronizacji – odświeżenie
      konfiguracji i rozdział poleceń. Z sekcją `web` także panel i kopia odczytów wszystkich agentów; z `cloud.backup` –
      codzienna kopia chmury.
    - **Narzędzia właściciela** (`glebosaurus cloud …`, połączenie z bazą hasłem z pliku administratora – `--admin PLIK`,
      zawsze jawnie; każde polecenie wypisuje id projektu, a bez `--confirm` tylko pokazuje plan):
      - `cloud migrate` – stan migracji, z `--confirm` stosuje czekające; zmieniona albo nieznana migracja – stop z kodem 1;
      - `cloud settings [--read-interval MIN]` – wspólne ustawienia i zmiana samego odstępu odczytów (resztę konfiguracji
        zmienia strona w chmurze; plik konfiguracji chmury i `cloud config` usunięto 2026-11-21 – HISTORIA.md);
      - `cloud agent [--name NAZWA [--sender-priority N] --confirm [--qr PLIK]]` – lista agentów albo nowy agent
        (zbieracz albo kandydat na nadawcę 1–9) z jednorazowym kodem rejestracji (24 h); istniejącemu agentowi zmienia rolę
        bez ponownej rejestracji (agent przejmuje ją przy najbliższej synchronizacji);
      - `cloud agent --name NAZWA --revoke` – odcięcie agenta: token przestaje działać od razu, niewykorzystane kody
        przepadają; dzierżawy wygasają same (czujniki po ok. 32 min, blokada nadawcy po ok. 7 min);
      - `cloud owner [--email ADRES [--remove] --confirm]` (`CloudOwners`) – właściciele strony w chmurze: lista (czy konto
        jest w Supabase Auth, czy ma TOTP), dodanie konta, które już jest w Auth (bez niego – instrukcja, jak je założyć),
        odebranie dostępu;
      - `cloud web-link [--page ADRES] [--qr PLIK]` (`OwnerWebLink`, bez połączenia z bazą) – odnośnik do strony z adresem
        projektu i kluczem publikowalnym we fragmencie i jego kod QR;
      - `cloud backup` – kopia zapasowa (zasada 10).
    - **Pozostałe polecenia:** `pair` (zasada 5; klucz `ID.credentials.json` w bieżącym katalogu albo `--credentials`,
      identyfikator sprawdza `SensorIds` – ta sama reguła co w bazie; czujnik dodaje się potem na stronie z pliku klucza),
      `mail-preview`, `web-password`, `stop`, diagnostyka `scan`, `read`, `listen`.

## Zależności czasów

Baterię czujnika zużywa tylko połączenie: odczyt i ponowienie, które doszło do połączenia. Nasłuch jest bierny (czujnik i
tak się reklamuje), a synchronizacja i pętle czujnika nie dotyczą – od nich zależy bateria telefonu (radio Bluetooth i
Wi-Fi) i ruch do Supabase. Odstępy są dobrane tak, żeby rzadko budzić radio i pytać chmurę, nie łamiąc zależności z
tabeli; pilnuje ich `TimingDependenciesTests` (wartości po stronie chmury czyta z migracji).

| Odstęp | Wartość | Zależność |
|---|---|---|
| Pętla zbieracza | 60 s | `read_now` budzi ją od razu; dokładność nasłuchu i ponowień |
| Nasłuch do przydziału | co 4 min, okno 30 s | rusza w pierwszym obrocie pętli po 4 min, więc co 4,5–5 min; dwa kolejne nasłuchy mieszczą się w świeżości słyszenia 10 min (`HeardWithin`) – jeden niesłyszany nasłuch nie zeruje czekania na wolny czujnik; chmura oddaje słyszenia innych agentów z 15 min |
| Okno nasłuchu przed odczytem | 60 s | adapter Windows dostarcza pakiety seriami z przerwami do ok. 27 s |
| Odstęp odczytów | 125 min (chmura) | główny koszt baterii czujnika |
| Ponowienia po porażce | 2, 5, potem 10 min (nie dłużej niż odstęp odczytów) | trzecia porażka z rzędu (ok. 7 min po pierwszej) oddaje czujnik; okres ochronny 30 min mieści 5 prób |
| Sesja urwana w trakcie historii | 2 min | bez liczenia porażki |
| Synchronizacja | 90 s; od razu po nowych odczytach, oddaniu czujnika i poleceniach z panelu | kandydat na nadawcę jest obecny, gdy jego sygnał życia ma najwyżej 4 min (po jednym chybionym cyklu – do 180 s); blokadzie nadawcy (5 min) po chybionym cyklu zostają 2 min, a do maila trzeba 1 – maile stają dopiero przy trzeciej nieudanej synchronizacji z rzędu; dzierżawa czujnika 30 min; strażnik wymaga udanej synchronizacji w ostatnich 3 min; „agent milczy” po 30 min; polecenia docierają do 90 s, wygasają po 10 min |
| Kopia odczytów | 2 min | zlecony odczyt czeka w panelu do 6 min: synchronizacja właściciela, nasłuch i odczyt, 30 s „ustalenia” wiersza, kopia; nowy właściciel czujnika nie czeka na kopię – raz na epokę dzierżawy pyta chmurę o ostatni odczyt (zasada 8) |
| Pętla nadawcy | 60 s | raporty o stałych porach; raport na żądanie budzi ją od razu |
| Przeliczanie alertów | 5 min (3 zapytania do chmury) | przerwa ponad 10 min to przestój i 30 min okresu ochronnego (`SilenceGrace`); przyrostowe pobieranie odczytów cofa się o 30 min |
| Strażnik healthchecks.io | 5 min (z pętlą 5–6 min) | ustawienie sprawdzenia: Period 5 min, Grace 25 min |
| Dzierżawy w chmurze | czujnik 30 min, blokada 5 min, zapas 2 min | stałe w migracji |

## Czujnik ATLO SMS1 (Tuya `gvygg3m8`) – fakty zmierzone na sprzęcie

Daty, przebieg prób i dane surowe: [HISTORIA.md](HISTORIA.md).

**Reklamy i parowanie**
- Czujnik wystawia reklamy BLE ze znacznikami Tuya (usługa 0xA201, dane producenta 0x07D0). Bez baterii milczy; po
  włożeniu baterii nadaje jako niesparowany zwykle tylko ok. 20 s (dioda mruga tyle samo; raz zaobserwowano dłużej).
- Sparowany czujnik reklamuje się rzadko (rzędu jednego pakietu na kilka–kilkanaście sekund), a dane usługi i dane
  producenta przychodzą w osobnych pakietach – pełna tożsamość Tuya składa się po kilku sekundach. Dlatego agent
  dopasowuje czujnik po adresie MAC (od pierwszego pakietu), a UUID Tuya jest drogą zapasową.
- Lokalne parowanie bez konta Tuya działa (protokół Tuya BLE 3.1, firmware 9.3). Tuż po przypisaniu czujnik bywa cichy
  (ok. 1 pakiet na 12 s), więc weryfikacja czeka do 90 s.
- **Wymiana baterii nie kasuje przypisania** (≥ 60 s bez zasilania – ten sam klucz od razu uwierzytelnia sesję).
  Przypisanie kasuje reset przyciskiem; wtedy `pair --id <to samo id>` przypisze ten sam klucz jeszcze raz.

**Dane**
- Punkty danych: DP3 wilgotność gleby %, DP5 temperatura gleby ×10 °C, DP15 bateria %, DP14 enum **stan baterii: 0 niski,
  1 średni, 2 wysoki** (zgodnie z [roquerodrigo/ha-tuya-ble](https://github.com/roquerodrigo/ha-tuya-ble); „niski” od ok.
  20%), DP9 enum (prawdopodobnie jednostka temperatury), DP6 (nieznane).
- DP15 to szacunek z napięcia, zaniżany pod obciążeniem radia (rośnie po odpoczynku), więc do alertu nadaje się tylko
  trend albo wiele odczytów. Przy DP14 = 0 nie robić ręcznych odczytów z długą historią – po takiej sesji czujnik na
  wyczerpanej baterii przestał się reklamować.
- **Czujnik trzyma historię i oddaje ją przy połączeniu** w ramkach `0x8003` (DP z czasem czujnika), od najstarszego, ok.
  1,1 s na ramkę; rekord powstaje co godzinę i przy każdym połączeniu (ostatnia ramka serii to stan z chwili połączenia).
  Po odebraniu serii z potwierdzeniem każdej ramki czujnik kasuje rekordy. Czujnik ze Smart Life trzyma tygodnie
  rekordów i oddaje ok. 110 na 180-sekundową sesję.
- Po utracie zasilania zegar czujnika liczy od nowa, więc rekordy po restarcie mają godziny z przeszłości i czasy nie
  rosną monotonicznie – ufać można tylko kolejności odbioru. `SoilSensorProfiles.MapAll` składa rekordy w kolejności
  odbioru i, gdy czas ostatniego rekordu odbiega od chwili połączenia o więcej niż 5 min, przesuwa końcówkę serii od
  ostatniego cofnięcia czasu – ale tylko w sesji zakończonej normalnie (zasada 8).
- Wilgotność trzeba kalibrować – tani czujnik nie podaje procentów porównywalnych między glebami.

**Czas, bateria, zasięg**
- Połączenie trwa ok. 14–17 s plus ok. 1,1 s na każdy zaległy rekord. Odczyt co minutę wyraźnie zbija wskazanie
  baterii – częstotliwość odczytów decyduje o baterii. Domyślny odstęp **2 h 5 min**: wilgotność gleby zmienia się wolno,
  a godzinne rekordy czujnika i tak trafiają do bazy, więc rzadsze połączenia nie gubią przebiegu. „Brak odczytów”
  (domyślnie 6 h) musi być dłuższy niż odstęp – `cloud settings` ostrzega, gdy nie jest.
- Odczyty udają się przy −75…−81 dBm (Windows) i nawet −93 dBm (Android); przy ok. −94 dBm czujnika na Windows nie ma w
  reklamach. Sygnał zależy od ułożenia czujnika (ok. 14 dB różnicy w tej samej skrzynce), nie od ziemi. Adapter
  dostarcza pakiety seriami z przerwami do ok. 27 s – stąd domyślne okno nasłuchu 60 s.
- Shiny na Windows: `Peripheral.Uuid` ma postać `00000000-0000-0000-0000-<MAC>`, MTU danych 20; RSSI ma −127 („brak
  wartości”) w ok. 1/3 pakietów, więc `SignalStats` liczy tylko ważne próbki. Android podaje moc w każdym pakiecie.

**Pułapki**
- Wymuszone zabicie procesu w trakcie rozłączania powoduje, że następny odczyt wisi na włączaniu powiadomień – do prób
  służy `run --minutes N`, które kończy pracę czysto.
- W zasięgu bywają inne urządzenia Tuya (DC:23:51:*), także cudze. Agent łączy się wyłącznie z czujnikami z konfiguracji,
  a `pair` – z jedynym niesparowanym kandydatem znanego typu.
- Czujnik przyjmuje jedno połączenie naraz – telefon ze Smart Life w pobliżu blokuje odczyt w danym cyklu.

**Zgodność z ha-tuya-ble:** protokół zgadza się bajt w bajt z [roquerodrigo/tuya-ble-sdk](https://github.com/roquerodrigo/tuya-ble-sdk)
(`ProtocolTests.FramesMatchReferenceImplementationByteForByte`). Różnice celowe: lokalne parowanie zamiast kluczy z konta
Tuya (sesja przyjmie też takie klucze); sesja czeka 3 s ciszy (SDK 1,5 s), limit to 3 min, a po nim oddaje to, co
przyszło; nieczytelna ramka z punktami danych nie przerywa odczytu i jest potwierdzana (inaczej ten sam rekord mógłby
wracać na początku każdej sesji).

## Ryzyka

- Shiny na Raspberry Pi (BlueZ, uprawnienia D-Bus) – niesprawdzone (ROADMAP 2.3b); plan zapasowy za `IGattLink`. Pi jako
  sam nadawca (bez Bluetooth) BlueZ nie używa.
- Raspberry Pi: runtime .NET (paczka zależna od frameworka) aktualizuje właściciel – samoaktualizacja Glebosaurusa tego
  nie robi; wydanie na nowszym .NET czeka, aż właściciel doinstaluje runtime (skrypt to wyjaśnia, agent pracuje dalej).
- BLE w usłudze Windows (sesja 0) – dlatego zadanie Harmonogramu.
- Samoaktualizacja ufa kontu GitHub (skrót chroni przed uszkodzeniem, nie przed podmianą wydania); zepsute wydanie
  wstrzymuje się samo (stara wersja wraca, kolejna próba po dobie), ale wymaga nowego wydania z poprawką.
- Wylogowanie i zamknięcie systemu kończą agenta bez pytania – może to wypaść w trakcie odczytu (niesprawdzone).
- Koszt baterii przy częstych odczytach; dryf wskazań wilgotności bez kalibracji.
- Zasięg BLE określa liczbę agentów.
- Chmura: konto Supabase i hasło bazy są kluczem do wszystkich sekretów; limity darmowego planu (500 MB, uśpienie po 7
  dniach, brak kopii zapasowych – zasada 10); bez Supabase nie wychodzi żaden mail (zgłasza to strażnik). Certyfikat
  główny Supabase wygasa 2031-04-26 (albo Supabase zmieni CA wcześniej) – połączenia właściciela z bazą przestaną wtedy
  działać, dopóki nie wbudujemy nowego (test przypomina pół roku wcześniej).
- Android: MIUI/HyperOS zabija aplikacje w tle, ograniczenia skanu BLE, telefon na zewnątrz (mróz, upał), start po
  restarcie dopiero po odblokowaniu.
