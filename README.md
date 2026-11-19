# Glebosaurus

Bezobsługowy monitor czujników gleby Tuya BLE (ATLO SMS1 i zgodne): agenci na Windows 11, Androidzie i Raspberry Pi
czytają czujniki przez Bluetooth, odczyty trafiają do chmury Supabase (darmowy plan), a jeden z agentów wysyła mailem
przez Gmail alerty „czas podlać”, „słaba bateria”, „brak odczytów” i raporty o stałych porach – z wykresem ostatnich 3 dób,
pisane pod ekran telefonu. Bez konta Tuya (czujniki paruje się lokalnie), bez własnego serwera i bez opłat.

Dokumenty: [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md) – jak to jest zbudowane i dlaczego ·
[docs/ROADMAP.md](docs/ROADMAP.md) – stan i plan · [docs/HISTORIA.md](docs/HISTORIA.md) – dziennik prób na sprzęcie i
decyzji · [CLAUDE.md](CLAUDE.md) – kontekst dla pracy nad kodem z asystentem AI.

## Jak to działa

```
czujniki BLE ──► agenci (Windows / Android / Raspberry Pi) ──► Supabase ──► nadawca maili ──► Gmail ──► telefon
                 każdy czyta czujniki, które słyszy              konfiguracja, odczyty, alerty, sekrety (Vault)
```

- **Agent** to program `glebosaurus run` (na Windows – usługa bez okna, na telefonie – aplikacja w tle). Czyta czujniki,
  które słyszy, i wysyła odczyty do chmury; lokalna baza SQLite jest buforem na czas bez sieci. Czujnik przyjmuje jedno
  połączenie naraz, więc agenci dzielą się czujnikami przez dzierżawy w chmurze.
- **Nadawca** – jeden z agentów z rolą nadawcy (np. komputer stale włączony) – liczy alerty z odczytów wszystkich agentów
  i wysyła maile, każdy najwyżej raz. Kandydatów na nadawcę może być kilku (priorytety); maili nie wysyła telefon.
- **Właściciel** konfiguruje chmurę ze swojego komputera: plik konfiguracji chmury (czujniki, progi, raporty, poczta)
  zapisuje w chmurze polecenie `glebosaurus cloud config`, a agentów rejestruje się jednorazowymi kodami. Agenci przejmują
  zmiany sami, w ciągu ok. minuty.
- **Panel WWW** (opcjonalnie, na agencie w sieci domowej): stan wszystkich czujników, wykresy całej historii, odczyt i raport
  na żądanie.

Stan: wersja 0.2.2; agent pracuje na co dzień na Windows 11 i Androidzie; **niesprawdzone:** Raspberry Pi, kalibracja
wilgotności, dłuższa praca telefonu – szczegóły w [ROADMAP](docs/ROADMAP.md).

## Czego potrzeba

- **Czujniki gleby Tuya BLE** – sprawdzone na ATLO SMS1 (produkt Tuya `gvygg3m8`, „SGS01”); w kodzie jest też profil
  `ojzlzzsw` (niesprawdzony).
- **Agent z Bluetooth LE** w zasięgu czujników (zwykle kilka metrów, zależy od ułożenia czujnika): komputer z Windows 11,
  telefon z Androidem 10+ albo Raspberry Pi z 64-bitowym Raspberry Pi OS. Kilku agentów pokrywa większy teren.
- **Konto Supabase** (darmowy plan wystarcza) – jeden projekt na dane; drugi, próbny, przydaje się do testów.
- **Konto Gmail** do wysyłki, z weryfikacją dwuetapową i [hasłem aplikacji](https://myaccount.google.com/apppasswords) –
  najlepiej osobne konto tylko do tego (hasło aplikacji daje pełny dostęp do skrzynki).
- Opcjonalnie [healthchecks.io](https://healthchecks.io) (darmowe) – mail, gdy nadawca zamilknie.

## Pierwsze uruchomienie

Polecenia zakładają program `glebosaurus` (na Windows z instalacji – `%LOCALAPPDATA%\Glebosaurus\app\glebosaurus.exe`;
z kodu źródłowego: `dotnet run --project src/Glebosaurus.Agent -f net10.0-windows10.0.19041.0 -- <polecenie> …`, na
Linuksie `-f net10.0`). Pliki właściciela – plik administratora, plik konfiguracji chmury i klucze czujników – trzymaj w
jednym prywatnym katalogu (w repozytorium: `local/`, wykluczony z gita).

### 1. Projekt Supabase i plik administratora

1. Załóż projekt na [supabase.com](https://supabase.com) (plan Free, region najbliżej Ciebie); zapamiętaj hasło bazy.
2. Skopiuj [examples/cloud-admin.json](examples/cloud-admin.json) jako `cloud-admin.json` i wpisz adres projektu
   (`https://<id>.supabase.co`), klucz publikowalny (`sb_publishable_…`), hasło bazy i – gdy region jest inny niż Central EU
   (Frankfurt) – adres poolera sesji (Connect → Session pooler). Opcjonalnie adres sygnału healthchecks.io (sprawdzenie
   „Simple”: Period 5 min, Grace 25 min).
3. Załóż schemat bazy (każde polecenie `cloud …` bez `--confirm` tylko pokazuje plan):

   ```powershell
   glebosaurus cloud migrate --admin cloud-admin.json --confirm
   ```

Plik administratora to sekret: hasło bazy otwiera wszystkie dane i sekrety w chmurze. Nie kopiuj go na agentów (wyjątek:
jeden agent z kopią zapasową – niżej).

### 2. Czujniki i plik konfiguracji chmury

1. Sparuj każdy czujnik (szczegóły – „Nowy czujnik” niżej); powstanie plik klucza `<id>.credentials.json` obok pliku
   konfiguracji chmury:

   ```powershell
   glebosaurus pair --id monstera --name "Monstera w salonie" --file cloud-config.json --confirm-bind
   # ... po komunikacie „Czekam na czujnik” włóż baterie do czujnika
   ```

2. Skopiuj [examples/cloud-config.json](examples/cloud-config.json) jako `cloud-config.json`: strefa czasowa, czujniki
   (wpis podaje `pair`), progi, pory raportów, konto Gmail z hasłem aplikacji i odbiorcy.
3. Sprawdź pocztę (prawdziwy mail; z `--samples` także przykładowy alert, raport i log – do obejrzenia na telefonie):

   ```powershell
   glebosaurus mail-test --file cloud-config.json --samples
   ```

4. Zapisz konfigurację w chmurze (plan, potem zapis):

   ```powershell
   glebosaurus cloud config --admin cloud-admin.json --file cloud-config.json
   glebosaurus cloud config --admin cloud-admin.json --file cloud-config.json --confirm
   ```

### 3. Agent

1. Utwórz agenta w chmurze i weź jednorazowy kod rejestracji (ważny 24 h). Agent, który ma wysyłać maile, dostaje
   `--sender-priority` (1 – najważniejszy); bez niego tylko zbiera odczyty:

   ```powershell
   glebosaurus cloud agent --admin cloud-admin.json --name dom --sender-priority 1 --confirm
   ```

2. Na urządzeniu agenta: plik `glebosaurus.config.json` z [examples/glebosaurus.config.json](examples/glebosaurus.config.json)
   (adres projektu i klucz publikowalny – bez hasła bazy), rejestracja i praca:

   ```powershell
   glebosaurus enroll --config glebosaurus.config.json     # bez --code pyta o kod, nie pokazując go
   glebosaurus run --config glebosaurus.config.json
   ```

   Na Windows zamiast `run` z konsoli zainstaluj usługę (niżej), na telefonie – aplikację (niżej, „Android”).

Pierwsze odczyty pojawiają się, gdy agent usłyszy czujnik (nasłuch co 3 min), pierwszy raport – o najbliższej porze z
`reports.times`.

## Windows: instalacja jako usługa

Wydania są na GitHubie: paczka samodzielna `Glebosaurus-<wersja>-win-x64.zip` (z .NET – nic więcej nie trzeba
instalować), skrypt `install-windows.ps1` i `SHA256SUMS`. Instalacja działa na koncie użytkownika, bez administratora
(Windows PowerShell 5.1 albo PowerShell 7):

```powershell
$s = "$env:TEMP\glebosaurus-install.ps1"
Invoke-WebRequest https://github.com/wojciech-urbanowski/Glebosaurus/releases/latest/download/install-windows.ps1 -OutFile $s -UseBasicParsing
powershell -NoProfile -ExecutionPolicy Bypass -File $s
```

Uruchamiaj go w zwykłym oknie PowerShell (menu Start: Windows PowerShell albo Terminal), nie w terminalu wbudowanym w
aplikację z pakietu Windows (np. z Microsoft Store): Windows przekierowuje jej zapisy w `%LOCALAPPDATA%` do prywatnej kopii
pakietu, której Harmonogram nie widzi – skrypt to wykrywa i przerywa.

Skrypt:

- pobiera najnowsze wydanie, sprawdza skrót SHA-256 i rozpakowuje program do `%LOCALAPPDATA%\Glebosaurus\app`;
- przy pierwszej instalacji zostawia w `%LOCALAPPDATA%\Glebosaurus\data` wzór `glebosaurus.config.example.json` – skopiuj
  go jako `glebosaurus.config.json`, uzupełnij sekcję `cloud`, zarejestruj agenta (`enroll --config …` – skrypt wypisuje
  gotowe polecenie) i uruchom usługę: `Start-ScheduledTask Glebosaurus`. Bez konfiguracji albo bez rejestracji usługa nie
  startuje;
- rejestruje zadanie Harmonogramu „Glebosaurus”: przy zalogowaniu (i co godzinę, gdyby usługa nie działała) uruchamia
  `app\glebosaurus-service.exe` – bez okna, także na baterii, bez limitu czasu pracy;
- usługa uruchamia agenta (`glebosaurus run` z konfiguracją z `data\`), podnosi go po awarii (po 1, 2, 5, potem co 15 min) i
  przy starcie oraz codziennie o 03:30 sprawdza wydania. Nowszą wersję instaluje ten sam skrypt z nowego wydania: czyste
  zatrzymanie agenta (po bieżącym odczycie), podmiana `app\` (poprzednia wersja zostaje w `app.old\`), start nowej.
  Nieudana aktualizacja nie zostawia agenta wyłączonego – wraca dotychczasowa wersja, a ta sama nowa wersja czeka dobę na
  kolejną próbę.

Ponowne uruchomienie skryptu aktualizuje usługę do najnowszego wydania albo naprawia instalację (rejestracja zadania, start).
`-Version 0.2.1` instaluje konkretną wersję (także starszą), `-NoStart` nie uruchamia usługi od razu, `-Uninstall`
odinstalowuje (dane zostają; z `-RemoveData` znikają też one). Ustawienia (repozytorium, nazwa zadania, pora sprawdzania,
`autoUpdate`) skrypt zapamiętuje w `%LOCALAPPDATA%\Glebosaurus\install.json`.

| Co | Gdzie / jak |
|---|---|
| Dziennik agenta | `data\logs\glebosaurus-RRRRMMDD.log` (ten sam, który idzie mailem do administratora) |
| Dziennik usługi: starty, awarie, aktualizacje | `data\logs\service-RRRRMMDD.log`; ostatnia aktualizacja: `updates\install-last.log` |
| Czyste zatrzymanie | `& "$env:LOCALAPPDATA\Glebosaurus\app\glebosaurus.exe" stop` – usługa wraca przy logowaniu, najpóźniej po godzinie (strażnik w zadaniu) albo od razu: `Start-ScheduledTask Glebosaurus` |
| Wyłączenie na dłużej | `Disable-ScheduledTask Glebosaurus`, potem `stop`; powrót: `Enable-ScheduledTask Glebosaurus` |

Na jednym koncie działa naraz jeden agent: `run` z konsoli obok usługi od razu się kończy (kod 4), a usługa ponawia start co
5 min, dopóki działa inny. Nie zatrzymuj usługi przez `Stop-ScheduledTask` ani zabijając proces – zabicie w trakcie
rozłączania z czujnikiem potrafi zawiesić następny odczyt; do tego jest `glebosaurus stop`.

## Nowy czujnik: parowanie lokalne

Bez konta i aplikacji Tuya: `pair` sam wymyśla `deviceId` i 6-bajtowy klucz, zapisuje je w czujniku przez BLE i sprawdza na
nowym połączeniu. Po włożeniu baterii czujnik nadaje jako niesparowany tylko ok. 20 s, dlatego **skan rusza pierwszy** –
uruchom polecenie, a baterie włóż dopiero po komunikacie „Czekam na czujnik”:

```powershell
glebosaurus pair --id monstera --name "Monstera w salonie" --file cloud-config.json --confirm-bind
```

Polecenie zapisuje klucz w `monstera.credentials.json` obok pliku konfiguracji chmury (zrób jego kopię zapasową – bez niej
czujnik trzeba resetować; po `cloud config` klucz jest też w chmurze i w jej kopii zapasowej) i podaje gotowy wpis do listy
`sensors`: `{ "id": "monstera", "name": "Monstera w salonie", "tuyaFile": "monstera.credentials.json" }`. Potem
`cloud config … --confirm` – agenci przejmą czujnik sami. Identyfikator: małe litery, cyfry, „_” i „-”.

Przerwane parowanie wznawia się tym samym poleceniem (ten sam klucz). Gdy w zasięgu jest kilka niesparowanych czujników,
polecenie nie zgaduje – wymaga `--mac`. Sparowany czujnik znika z aplikacji Smart Life; reset przyciskiem przywraca stan
fabryczny. Tuż po przypisaniu czujnik reklamuje się rzadko, więc weryfikacja potrafi potrwać do ok. 30 s (limit 90 s).

**Wymiana baterii nie kasuje przypisania** – wystarczy włożyć nowe baterie, agenci czytają czujnik dalej. Przypisanie
kasuje dopiero **reset przyciskiem**: wtedy uruchom `pair --id <to samo id> --file … --confirm-bind` (skan rusza pierwszy),
zresetuj czujnik i włóż baterie – polecenie użyje zapisanego klucza. Agent, który trafi na niesparowany czujnik, zapisuje w
dzienniku dokładnie to polecenie.

**Czujnik zostawiony w Smart Life** też da się czytać – kluczem z chmury Tuya (`deviceId`, `localKey`, `uuid`, np. z
narzędzi Tuya IoT), wpisanym wprost w `tuya` w pliku konfiguracji chmury. Adres MAC ustalisz poleceniem `glebosaurus scan
--verbose` (po UUID Tuya z reklamy). Uwaga: nasze odczyty kasują zapisane w czujniku rekordy, więc Smart Life traci od tej
pory ich historię, a telefon z otwartą aplikacją blokuje odczyt.

## Konfiguracja

| Plik | Kto i gdzie | Zawiera | Sekret? |
|---|---|---|---|
| `cloud-admin.json` ([wzór](examples/cloud-admin.json)) | właściciel, jego komputer | adres projektu, klucz publikowalny, hasło bazy, adres healthchecks.io | **tak** – klucz do całej chmury |
| `cloud-config.json` ([wzór](examples/cloud-config.json)) | właściciel, obok pliku administratora | strefa, odstęp odczytów, czujniki (`tuyaFile` / `tuya`), progi, raporty, poczta z hasłem aplikacji | **tak** (hasło Gmaila, klucze) |
| `*.credentials.json` (z `pair`) | właściciel | klucz lokalny czujnika | **tak** |
| `glebosaurus.config.json` ([wzór](examples/glebosaurus.config.json)) | każdy agent | adres projektu i klucz publikowalny, baza, retencja, dziennik, panel WWW, kopia zapasowa | nie |
| `glebosaurus-cloud.token`, `glebosaurus-cloud-config.bin` | agent, obok jego konfiguracji | token agenta, kopia konfiguracji z chmury (z kluczami Tuya) – chronione DPAPI (Windows) / prawami 600 (Linux) | **tak** – nie kopiuj na inne urządzenia |

Plik agenta odrzuca ustawienia, które należą do chmury (`sensors`, `mail`, `reports`, `alerts`, `readIntervalMinutes`,
`scanWindowSeconds`) – zamiast pomijać je po cichu. Zmiany w pliku konfiguracji chmury trafiają do agentów dopiero po
`cloud config … --confirm` (czujnik usunięty z pliku zostaje w chmurze). Sam odstęp odczytów zmienisz bez pliku:

```powershell
glebosaurus cloud settings --admin cloud-admin.json                                       # wspólne ustawienia
glebosaurus cloud settings --admin cloud-admin.json --read-interval 125 --confirm         # odczyt co 2 h 5 min
```

### Agenci

```powershell
glebosaurus cloud agent --admin cloud-admin.json                                          # lista agentów
glebosaurus cloud agent --admin cloud-admin.json --name dom --sender-priority 2 --confirm # zmiana roli (bez ponownej rejestracji)
glebosaurus cloud agent --admin cloud-admin.json --name dom --revoke --confirm            # odcięcie (zgubione urządzenie, wyciek tokenu)
```

Zmianę roli działający agent przejmuje w ciągu ok. minuty. Odcięcie unieważnia token od razu i kasuje niewykorzystane kody
tego agenta; jego czujniki przejmą inni po wygaśnięciu dzierżaw (najwyżej ok. 32 min). Z powrotem: nowy kod, `enroll`,
restart agenta. Bez sieci agent startuje z kopii konfiguracji i czyta czujniki do końca dzierżaw; odczyty czekają w
lokalnej bazie i trafiają do chmury po powrocie sieci.

## Polecenia

```powershell
glebosaurus run --config glebosaurus.config.json        # praca ciągła; dziennik w logs/ obok pliku konfiguracji
glebosaurus enroll --config glebosaurus.config.json     # rejestracja agenta kodem od właściciela
glebosaurus stop                                        # czysto zatrzymuje działającego agenta i usługę (Windows)
glebosaurus web-password --config glebosaurus.config.json  # hasło i certyfikat panelu WWW (panel działa w ramach run)
glebosaurus pair --id ID --file cloud-config.json --confirm-bind   # nowy czujnik (skan rusza pierwszy)
glebosaurus mail-test --file cloud-config.json [--samples]          # PRAWDZIWY mail z konta z pliku konfiguracji chmury
glebosaurus mail-preview --out mail-preview             # przykładowe maile (HTML i tekst) do obejrzenia, niczego nie wysyła
glebosaurus cloud migrate|config|settings|agent|backup --admin cloud-admin.json …   # właściciel chmury
glebosaurus scan --seconds 15 --verbose                 # diagnostyka: urządzenia Tuya w zasięgu, z mocą sygnału
glebosaurus read --credentials monstera.credentials.json             # diagnostyka: jednorazowy odczyt jednego czujnika
glebosaurus listen --credentials monstera.credentials.json --out listen.log  # diagnostyka: wszystkie ramki czujnika
```

Pełny opis: `glebosaurus --help`. Kody wyjścia: 0 sukces, 1 błąd, 2 nie znaleziono czujnika, 3 czujnik niesparowany albo
brak danych, 4 agent już działa, 130 przerwano. `read` i `listen` łączą się z czujnikiem – zatrzymaj wcześniej agenta, który
może go czytać, i zamknij Smart Life (czujnik przyjmuje jedno połączenie naraz).

## Odczyty, alerty i maile

**Odczyty.** ATLO SMS1 zapisuje pomiar co godzinę i przy każdym połączeniu, a przy połączeniu oddaje całą zaległą serię i ją
kasuje. Agent zapisuje te zaległe pomiary z czasem czujnika, a najnowszy traktuje jako stan bieżący – dlatego domyślny
odstęp odczytów to **2 h 5 min** (`readIntervalMinutes`): wykres i alerty mają dane godzinowe, a bateria – mniej połączeń.
Agent nasłuchuje reklam BLE i każdy należny czujnik, który się pojawi, czyta od razu (czujnik poza zasięgiem nie opóźnia
pozostałych); `scanWindowSeconds` (domyślnie 60 s) to łączny czas nasłuchu. Czujnik, który nie odpowiada, jest ponawiany z
rosnącym odstępem (1, 2, 4, 8, max 10 min). Połączenie trwa ok. 14–17 s plus ok. 1,1 s na każdy zaległy rekord.

**Zasięg.** W dzienniku przy każdym odczycie jest poziom sygnału („sygnał -77 dBm”). Odczyty udają się zwykle przy
−75…−81 dBm (na Androidzie nawet ok. −93 dBm); przy ok. −94 dBm czujnika nie słychać. Poziom zależy od ułożenia czujnika
(nawet 14 dB różnicy w tej samej doniczce), nie od ziemi; `glebosaurus scan` pokazuje średnią i zakres dla każdego
urządzenia, a telefon – podgląd na żywo. Każdy agent czyta tylko czujniki, które słyszy; czujnik słyszany przez kilku
agentów czyta ten, który słyszy go najlepiej.

**Alerty:** „czas podlać” (wilgotność poniżej progu przez kilka odczytów z rzędu, wygasa dopiero powyżej progu wygaszenia),
„słaba bateria”, „brak odczytów” (żadnego odczytu dłużej niż `silentAfterHours`, domyślnie 6 h – liczone też za czas, gdy
nadawca był wyłączony; po jego starcie i po uśpieniu jest jednak 30 min na odczyty, zanim taki alert powstanie). Mail o
nowych alertach idzie od razu (kilka naraz w jednej wiadomości), przypomnienie co `reminderHours`. Raporty o porach z
`reports.times`; spóźniony raport (np. nadawca był wyłączony) jest nadrabiany do `catchUpHours`. Wilgotność z tanich
czujników wymaga kalibracji – domyślne progi (25% / 35%) są tylko punktem wyjścia.

**Maile** są pisane z myślą o telefonie: HTML to jedna wąska kolumna (do 480 px) z kartą na czujnik – nazwa, plakietka
stanu, trzy duże liczby (wilgotność, temperatura gleby, bateria) i pasek wilgotności – a wersja tekstowa ma krótkie wiersze.
Elementy są kwadratowe, w pastelowych kolorach (czerwień – podlej, żółć – słaba bateria, zieleń – OK, szarość – brak
odczytów). Tytuł zaczyna się od tego, co ważne („Monstera – czas podlać”, „Poranny raport – do podlania: Monstera, Fikus
+1”), a w podglądzie na liście wiadomości widać wilgotność wszystkich roślin. Każdy raport ma przy czujniku wykres ostatnich
3 dób z samego HTML (kolumny po 2 godz.: niebieska kreska – średnia wilgotność, blady pasek – temperatura od najniższej do
najwyższej, różowe tło – poniżej progu podlania; przy wielu czujnikach kolumny są szersze, żeby Gmail nie przyciął maila),
a wersja tekstowa linijkę `▁…█`. W stopce każdego maila jest wersja Glebosaurusa. Odbiorcy: `mail.alertRecipients` (alerty
i wiadomość próbna), `mail.reportRecipients` (raporty; puste = ci sami co alertów), `mail.adminRecipients` (log, „agent
milczy”). Wygląd obejrzysz bez wysyłania: `glebosaurus mail-preview` (w przeglądarce użyj widoku telefonu).

**Log techniczny dla administratora.** Agent trzyma dziennik w plikach dziennych `logs/glebosaurus-RRRRMMDD.log` obok pliku
konfiguracji i usuwa starsze niż `logs.retentionDays`. Wczorajszy dziennik każdy agent wysyła do chmury, a nadawca po 01:00
wysyła pod `mail.adminRecipients` osobny mail z dziennika każdego agenta: streszczenie (liczba wpisów, ostatnie ostrzeżenia i
błędy) i pełny dziennik jako załącznik `.log`. Dziennik nie zawiera kluczy czujników ani hasła Gmaila. Agent, który nie
synchronizuje się z chmurą dłużej niż 30 min, daje mail „agent milczy”.

## Panel WWW (diagnostyka w przeglądarce)

Agent z sekcją `web` pokazuje w sieci lokalnej stan **wszystkich** czujników i agentów – trzyma lokalną kopię odczytów
wszystkich agentów (dociąga ją z chmury co 2 min). Wygląd jak w mailach: kwadratowe, pastelowe karty, jedna kolumna na
telefonie; strony odświeżają się same.

- **Pulpit:** karta każdego czujnika (alerty, wilgotność, temperatura, bateria, wiek odczytu, który agent go czyta, problem
  z ostatnią próbą) z wykresem ostatnich 3 dób; otwarte alerty, chmura i agenci (synchronizacja, bufor, nadawca), poczta,
  najbliższy raport, końcówka dziennika, ustawienia.
- **Strona czujnika:** stan teraz z sygnałem (tu i u najlepiej słyszącego innego agenta), przeglądalny wykres całej
  historii (3 doby … rok, przesuwanie przeciąganiem, Ctrl + kółko, dwa palce), ostatnie odczyty, diagnostyka, ustawienia
  i wpisy dziennika o tym czujniku.
- **Odczyt na żądanie** („Odczytaj teraz”) idzie poleceniem przez chmurę do agenta, który czyta czujnik; wynik zwykle w
  ciągu 1–2 min. Ten sam czujnik najwyżej raz na minutę (bateria).
- **Raport na żądanie** („Wyślij raport teraz…”) wysyła **prawdziwy mail** przez obecnego nadawcę – panel najpierw pyta,
  pokazując odbiorców. Zaplanowane raporty idą dalej o swoich porach.

Konfiguracji panel nie zmienia – ta jest w chmurze. Włączenie:

1. Dodaj do pliku agenta sekcję `"web": { "port": 8470 }` (wzór w [examples/glebosaurus.config.json](examples/glebosaurus.config.json)).
2. Na urządzeniu agenta uruchom `glebosaurus web-password --config glebosaurus.config.json` i podaj hasło (co najmniej 12
   znaków, dwa razy). Polecenie zapisuje tylko **skrót** hasła w `glebosaurus-web.password`, tworzy certyfikat HTTPS
   `glebosaurus-web.pfx` i wypisuje adresy panelu oraz **odcisk SHA-256 certyfikatu**.
3. Uruchom agenta ponownie i otwórz np. `https://<nazwa-komputera>:8470/`. Przeglądarka ostrzeże, że certyfikat nie
   pochodzi z zaufanego urzędu – to oczekiwane (podpis własny); porównaj odcisk z tym z `web-password` i przejdź dalej.
4. Windows: dostęp z innych urządzeń wymaga zgody Zapory (przy pierwszym starcie usługi Windows pyta – zezwól w sieci
   prywatnej) albo reguły (PowerShell jako administrator; `web-password` wypisuje ją z właściwym portem):

```powershell
New-NetFirewallRule -DisplayName "Glebosaurus panel" -Direction Inbound -Protocol TCP -LocalPort 8470 -Profile Private -RemoteAddress LocalSubnet -Action Allow
```

Zabezpieczenia: połączenia spoza loopbacku i sieci prywatnych są odrzucane (węższą listę ustawia `web.allowedNetworks`);
HTTPS z certyfikatem z podpisem własnym (ECDSA P-256, odnawiany automatycznie); na dysku tylko skrót hasła (PBKDF2-SHA256),
ciasteczko sesji `Secure`, `HttpOnly`, `SameSite=Strict`, blokada adresu po 5 błędnych hasłach w 15 min; panel nie pokazuje
kluczy czujników ani hasła Gmaila; ścisłe nagłówki (CSP bez skryptów inline), akcje tylko z samego panelu. Kłopot z panelem
kończy się wpisem w dzienniku – agent pracuje dalej. Pliki `glebosaurus-web.password` i `glebosaurus-web.pfx` są sekretami.

## Kopia zapasowa chmury

Darmowy plan Supabase kopii nie robi, więc robi ją Glebosaurus:

```powershell
glebosaurus cloud backup --admin cloud-admin.json            # teraz; domyślnie do backups/ obok pliku administratora, 14 najnowszych
```

Kopia to jeden plik ZIP z jednej chwili (transakcja tylko do odczytu): wszystkie tabele jako CSV (bez dzienników agentów),
lista migracji i **odszyfrowane** sekrety z Vault (klucze Tuya, hasło Gmaila, adres healthchecks.io) – w zrzucie bazy są
zaszyfrowane kluczem projektu, który przepada razem z projektem. Plik jest więc sekretem jak plik administratora; poza
komputer – tylko w zaszyfrowane miejsce. Codziennie robi go agent z sekcją `cloud.backup` w pliku agenta (raz na dzień, przy
pierwszej okazji): potrzebuje pliku administratora z adresem projektu i hasłem bazy – bez klucza tajnego – więc tylko jeden
zaufany agent, najlepiej ten, który pracuje stale. Odtwarzania z kopii jeszcze nie ma (ROADMAP, faza 4).

## Android: agent zbierający

Telefon (Android 10+; sprawdzone na Xiaomi 11 Lite 5G NE z Androidem 14 i HyperOS) na Wi-Fi i **stale na ładowarce** zbiera
odczyty jak każdy agent, ale nie wysyła maili. Przydaje się tam, gdzie komputer nie słyszy czujników. Projekt:
[ARCHITEKTURA](docs/ARCHITEKTURA.md), zasada 12.

1. **Agent w chmurze i kod QR** – właściciel na komputerze (plik administratora musi mieć `publishableKey`):

   ```powershell
   glebosaurus cloud agent --admin cloud-admin.json --name telefon --confirm --qr telefon-qr.png
   ```

   Kod QR zawiera adres projektu, klucz publikowalny i jednorazowy kod rejestracji (24 h) – po rejestracji usuń plik. Bez
   `--qr` kod QR jest w konsoli.
2. **Instalacja:** `Glebosaurus-<wersja>-android.apk` z wydania na GitHubie (albo `./deploy/build-android.ps1 -Install`).
   Android zapyta o zgodę na instalację z tego źródła. Aktualizacja = nowszy APK zainstalowany na starym (dane i
   rejestracja zostają); APK podpisany innym kluczem trzeba najpierw odinstalować.
3. **Rejestracja:** w aplikacji „Zeskanuj kod QR” (skaner z Usług Google Play) albo „Wklej kod”, jeśli inny skaner pokazał
   tekst `glebosaurus://enroll?…`. Agent rusza sam, gdy są uprawnienia.
4. **Ustawienia telefonu** (sekcja „Przygotowanie telefonu” na ekranie pokazuje, czego brakuje):
   - uprawnienia: „Urządzenia w pobliżu” (Android 12+; na 10–11 – lokalizacja) i powiadomienia;
   - „Bateria” → bez optymalizacji baterii;
   - MIUI/HyperOS: „Autostart” → włącz Glebosaurusa; Oszczędzanie baterii → **Bez ograniczeń**; w ostatnich aplikacjach
     przytrzymaj Glebosaurusa i zablokuj (kłódka);
   - jeśli system ma ochronę akumulatora (ładowanie do 80%) – włącz ją: telefon wisi na ładowarce bez przerwy;
   - nie instaluj na tym telefonie Smart Life (walczyłaby o połączenia z czujnikami);
   - **blokada ekranu:** po restarcie telefonu agent rusza dopiero po pierwszym odblokowaniu (wcześniej Android nie
     udostępnia aplikacji jej danych); bez odblokowania po 30 min przyjdzie mail „agent milczy”.
5. **Miejsce na telefon:** „Zasięg czujników” – podgląd na żywo (3 min) mocy sygnału każdego czujnika.

Ekran pokazuje to samo co panel WWW (który agent czyta który czujnik, ostatnie odczyty wszystkich agentów, synchronizacja,
alerty, agenci), stan telefonu (bateria, temperatura akumulatora, ładowarka, Wi-Fi – idą też w sygnale życia do chmury) i
ostatnie wpisy dziennika. Agent pracuje jako usługa ze stałym powiadomieniem; „Zatrzymaj agenta” kończy bieżący odczyt i
oddaje czujniki innym. Token i kopia konfiguracji z kluczami Tuya są zaszyfrowane kluczem z Android Keystore (odinstalowanie
aplikacji je kasuje – potem nowa rejestracja). Dziennik: `adb logcat -s Glebosaurus`.

## Raspberry Pi (linux-arm64) – niesprawdzone

Paczka zależna od frameworka (wymaga **ASP.NET Core Runtime 10**, np. `./dotnet-install.sh --channel 10.0 --runtime
aspnetcore`):

```bash
dotnet publish src/Glebosaurus.Agent -c Release -f net10.0 -r linux-arm64 --self-contained false -o publish/linux-arm64
sudo mkdir -p /opt/glebosaurus && sudo cp -r publish/linux-arm64/* /opt/glebosaurus/
systemctl status bluetooth          # BlueZ musi działać
dotnet /opt/glebosaurus/glebosaurus.dll scan --seconds 15 --verbose
```

Plik agenta z sekcją `cloud` i rejestracja (`enroll`) jak wyżej. Jako usługa: [deploy/glebosaurus.service](deploy/glebosaurus.service)
(wzór). Jeśli `scan` zgłasza brak dostępu do Bluetooth: użytkownik powinien być w grupie `bluetooth`, a polityka D-Bus dla
BlueZ musi mu na to pozwalać (uwagi w dokumentacji Shiny dla Linuxa). Dopasowanie po adresie MAC opiera się na
identyfikatorze urządzenia z BlueZ (nieprzetestowane); zapasowo działa UUID Tuya z reklamy.

## Budowanie i testy

```powershell
dotnet build Glebosaurus.slnx -c Release
dotnet test --solution Glebosaurus.slnx -c Release
```

`global.json` włącza Microsoft Testing Platform (wymagane przez xunit.v3 na SDK 10+). Testy schematu i funkcji SQL działają
na **próbnym** projekcie Supabase (plik administratora w `local/cloud-admin.test.json`) i tylko na żądanie – każdy przebieg
czyści próbną bazę i stosuje migracje od zera; bez zmiennej są pomijane:

```powershell
$env:GLEBOSAURUS_CLOUD_TESTS = '1'; dotnet test --project tests/Glebosaurus.Cloud.Tests -c Release
```

| Projekt | Rola |
|---|---|
| `Glebosaurus.Tuya` | protokół Tuya BLE 3.x, lokalne przypisanie, sesja, profil czujnika |
| `Glebosaurus.Core` | konfiguracja, alerty, harmonogram raportów, szablony maili, wykresy, części agenta (synchronizacja, zbieracz, nadawca, panel) |
| `Glebosaurus.Data` | lokalna baza SQLite (bufor odczytów, kopia odczytów innych agentów) |
| `Glebosaurus.Mail` | wysyłka przez Gmail (MailKit, hasło aplikacji) |
| `Glebosaurus.Web` | panel diagnostyczny WWW wbudowany w `run` (Kestrel, HTTPS, hasło) |
| `Glebosaurus.Cloud` | klient chmury dla agentów: funkcje SQL Supabase przez Data API, ochrona tokenu i kopii konfiguracji, healthchecks.io |
| `Glebosaurus.Cloud.Admin` | narzędzia właściciela: połączenie z bazą, migracje z `supabase/migrations/`, zapis konfiguracji, agenci, kopia zapasowa |
| `Glebosaurus.Agent` | CLI i agent na Windows/Linux (Shiny.BluetoothLE) |
| `Glebosaurus.Service` | usługa Windows bez okna (`glebosaurus-service.exe`): uruchamia i pilnuje agenta, sprawdza wydania |
| `Glebosaurus.Android` | agent na Androida – poza `Glebosaurus.slnx` (wymaga obciążenia `android`) |

Pliki wydania dla Windows (paczka samodzielna, skrypt instalacji, `SHA256SUMS`) buduje `./deploy/package-windows.ps1`
(wynik w `publish/release`). Wydanie: wersja w `Directory.Build.props`, potem tag `v<wersja>` – workflow
[release.yml](.github/workflows/release.yml) uruchamia testy, buduje paczkę i APK, próbuje zainstalować paczkę skryptem i
publikuje wydanie; zainstalowane usługi znajdą je same w ciągu doby. [ci.yml](.github/workflows/ci.yml) buduje i testuje
każdy push do `main` i każdy PR (także APK).

Agent Android potrzebuje obciążenia `android` (`dotnet workload install android`), JDK 17+ i Android SDK. Buduje go skrypt
(APK w `publish/android`, podpisany kluczem wydań z `local/`, jeśli jest; `-Install` instaluje na podłączonym telefonie
przez `adb`):

```powershell
./deploy/build-android.ps1 -Toolchain D:\narzedzia\android-toolchain -Install   # katalog z dotnet, jdk i android-sdk (bez administratora)
./deploy/build-android.ps1 -CreateKeystore                                    # jednorazowo: klucz wydań w local/
```

## Licencja

Glebosaurus to wolne oprogramowanie na licencji **GNU Affero General Public License w wersji 3 lub późniejszej**
(AGPL-3.0-or-later, pełny tekst: [LICENSE](LICENSE)). Wolno go używać – także zarobkowo – zmieniać i rozpowszechniać, ale
każda zmieniona wersja musi być dostępna z pełnym kodem źródłowym na tej samej licencji, również wtedy, gdy udostępnia się
ją innym tylko przez sieć (np. panel WWW albo usługa zbudowana na Glebosaurusie). Program jest dostarczany bez żadnej
gwarancji. Nota prawna i dodatkowe zezwolenie dla APK (biblioteka Google Play services): [NOTICE.md](NOTICE.md).

Kod protokołu Tuya powstał na podstawie projektów MIT – [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Kod wysyłki Gmail
pochodzi z LibrusNotifier (ten sam autor).
