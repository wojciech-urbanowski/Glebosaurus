# Glebosaurus

Bezobsługowy monitor czujników gleby Tuya BLE (ATLO SMS1) dla Windows 11 i Raspberry Pi OS: odczyty do lokalnej bazy SQLite,
alerty „czas podlać” i raporty o stałych porach mailem przez Gmail.

Dokumenty: [docs/ARCHITEKTURA.md](docs/ARCHITEKTURA.md) – projekt i fakty zmierzone na sprzęcie ·
[docs/ROADMAP.md](docs/ROADMAP.md) – kolejne fazy i decyzje · [CLAUDE.md](CLAUDE.md) – kontekst dla dalszej pracy nad kodem.

## Stan

Wersja 0.2.1. **Tryb samodzielny działa na co dzień** na Windows 11 z prawdziwymi czujnikami – od 2026-11-15 jako usługa na
koncie użytkownika (zadanie Harmonogramu przy zalogowaniu, bez okna), instalowana skryptem i sama aktualizująca się z wydań
na GitHubie (patrz „Windows: instalacja jako usługa”): protokół Tuya BLE z lokalnym parowaniem bez konta Tuya (`pair`),
odczyty do SQLite, alerty, raporty z wykresem i log administratora mailem przez Gmail (wygląd pod telefon), lokalny panel
diagnostyczny w przeglądarce (HTTPS, sieć lokalna, hasło; wykresy, odczyt i raport na żądanie – patrz „Panel WWW”).
**Tryb chmurowy** (Supabase, wielu agentów – faza 2; z panelem WWW i kopią zapasową chmury) jest w wydaniu od 0.2.0;
od 2026-11-18 pracuje w nim usługa na bestia5 (czujniki, progi, poczta i klucze w prawdziwym projekcie Supabase; 2.1.5 –
tydzień próby).
**Niesprawdzone:** Raspberry Pi, kalibracja wilgotności. Co zrobione, co zostało i w jakiej kolejności:
[ROADMAP](docs/ROADMAP.md), „Stan w skrócie”.

| Projekt | Rola |
|---|---|
| `Glebosaurus.Tuya` | protokół Tuya BLE 3.x, lokalne przypisanie, sesja, profil czujnika |
| `Glebosaurus.Core` | konfiguracja, reguły alertów, harmonogram raportów, szablony maili, dane do wykresów, silnik monitora |
| `Glebosaurus.Data` | lokalny magazyn SQLite |
| `Glebosaurus.Mail` | wysyłka przez Gmail (MailKit, hasło aplikacji) |
| `Glebosaurus.Web` | panel diagnostyczny WWW wbudowany w `run` (Kestrel, HTTPS, hasło; pulpit z wykresami, strona czujnika, odczyt i raport na żądanie) |
| `Glebosaurus.Agent` | CLI: `run`, `stop`, `enroll`, `pair`, `status`, `report`, `mail-test`, `mail-preview`, `send-log`, `web-password`, `scan`, `read`, `listen` |
| `Glebosaurus.Service` | usługa Windows bez okna (`glebosaurus-service.exe`): uruchamia i pilnuje monitora, sprawdza wydania i przekazuje aktualizację skryptowi |
| `Glebosaurus.Cloud` | klient chmury dla agentów (faza 2): funkcje SQL Supabase przez Data API, ochrona tokenu i kopii konfiguracji, sygnał dla healthchecks.io |
| `Glebosaurus.Cloud.Admin` | narzędzia właściciela chmury (faza 2): połączenie z bazą Supabase, migracje schematu z `supabase/migrations/`, import, agenci, kopia zapasowa |

## Budowanie i testy

```powershell
dotnet build Glebosaurus.slnx -c Release
dotnet test --solution Glebosaurus.slnx -c Release
```

`global.json` włącza Microsoft Testing Platform (wymagane przez xunit.v3 na SDK 10+).

Testy schematu i funkcji SQL chmury działają na **próbnym** projekcie Supabase (`local/cloud-admin.test.json`) i tylko na
żądanie – każdy przebieg czyści próbną bazę i stosuje migracje od zera (ok. pół minuty); bez zmiennej są pomijane:

```powershell
$env:GLEBOSAURUS_CLOUD_TESTS = '1'; dotnet test --project tests/Glebosaurus.Cloud.Tests -c Release
```

Pliki wydania dla Windows (paczka samodzielna, skrypt instalacji, `SHA256SUMS`) buduje `./deploy/package-windows.ps1`
(wynik w `publish/release`; `-Version 0.1.1 -OutputDir …` – paczka próbna z innym numerem). Wydanie na GitHubie: wersja w
`Directory.Build.props`, potem tag `v<wersja>` – workflow [release.yml](.github/workflows/release.yml) uruchamia testy, buduje
paczkę, próbuje ją zainstalować skryptem i publikuje wydanie; zainstalowane usługi znajdą je same w ciągu doby.
[ci.yml](.github/workflows/ci.yml) buduje i testuje każdy push do `main` i każdy PR.

## Szybki start

Robocze pliki (konfiguracja, klucze czujników, baza, dzienniki) trzymaj w katalogu `local/` – jest poza repozytorium.

1. Sparuj czujnik (patrz niżej) – powstanie plik klucza `<id>.credentials.json`.
2. Skopiuj [examples/glebosaurus.config.json](examples/glebosaurus.config.json) do `local/glebosaurus.config.json` i wpisz
   czujnik z `tuyaFile` (polecenie `pair` podaje gotowy wpis). Sekcję `mail` dodaj, gdy chcesz maile.
3. Uruchom `glebosaurus run --config local/glebosaurus.config.json`.

## Windows: instalacja jako usługa

Wydania (od 0.1.0) są na GitHubie: paczka samodzielna `Glebosaurus-<wersja>-win-x64.zip` (z .NET – nic więcej nie trzeba
instalować), skrypt `install-windows.ps1` i `SHA256SUMS`. Instalacja działa na koncie użytkownika, bez administratora
(Windows PowerShell 5.1 albo PowerShell 7):

```powershell
$s = "$env:TEMP\glebosaurus-install.ps1"
Invoke-WebRequest https://github.com/wojciech-urbanowski/Glebosaurus/releases/latest/download/install-windows.ps1 -OutFile $s -UseBasicParsing
powershell -NoProfile -ExecutionPolicy Bypass -File $s -ImportFrom D:\sciezka\do\local   # -ImportFrom tylko przy przejmowaniu danych
```

Uruchamiaj go w zwykłym oknie PowerShell (menu Start: Windows PowerShell albo Terminal), nie w terminalu wbudowanym w
aplikację z pakietu Windows (np. w aplikacji Claude): Windows przekierowuje jej zapisy w `%LOCALAPPDATA%` do prywatnej kopii
pakietu, której Harmonogram nie widzi – skrypt to wykrywa i przerywa.

Skrypt:

- pobiera najnowsze wydanie, sprawdza skrót SHA-256 i rozpakowuje program do `%LOCALAPPDATA%\Glebosaurus\app`;
- z `-ImportFrom KATALOG` kopiuje do `%LOCALAPPDATA%\Glebosaurus\data` konfigurację, klucze czujników (`tuyaFile` i
  `*.credentials.json`), bazę, dzienniki oraz hasło i certyfikat panelu WWW. Niczego nie nadpisuje, a monitor korzystający z tamtego
  katalogu nie może wtedy działać (spójna kopia bazy). Bez konfiguracji zostawia w `data\` wzór `glebosaurus.config.example.json`
  i usługi nie uruchamia;
- rejestruje zadanie Harmonogramu „Glebosaurus”: przy zalogowaniu (i co godzinę, gdyby usługa nie działała) uruchamia
  `app\glebosaurus-service.exe` – bez okna, także na baterii, bez limitu czasu pracy;
- uruchamia usługę. Ta uruchamia monitor (`glebosaurus run` z konfiguracją z `data\`), podnosi go po awarii (po 1, 2, 5, potem co
  15 min) i przy starcie oraz codziennie o 03:30 sprawdza wydania. Nowszą wersję instaluje ten sam skrypt z nowego wydania:
  czyste zatrzymanie monitora (po bieżącym odczycie), podmiana `app\` (poprzednia wersja zostaje w `app.old\`), start nowej.
  Nieudana aktualizacja nie zostawia monitora wyłączonego – wraca dotychczasowa wersja, a ta sama nowa wersja czeka dobę na
  kolejną próbę.

Ponowne uruchomienie skryptu aktualizuje usługę do najnowszego wydania albo naprawia instalację (rejestracja zadania, start).
`-Version 0.1.0` instaluje konkretną wersję (także starszą), `-NoStart` nie uruchamia usługi od razu (wystartuje przy następnym
zalogowaniu), `-Uninstall` odinstalowuje (dane zostają; z `-RemoveData` znikają też one). Ustawienia (repozytorium, nazwa
zadania, pora sprawdzania, `autoUpdate`) skrypt zapamiętuje w `%LOCALAPPDATA%\Glebosaurus\install.json`.

| Co | Gdzie / jak |
|---|---|
| Dziennik monitora | `data\logs\glebosaurus-RRRRMMDD.log` (ten sam, który idzie mailem do administratora) |
| Dziennik usługi: starty, awarie, aktualizacje | `data\logs\service-RRRRMMDD.log`; ostatnia aktualizacja: `updates\install-last.log` |
| Czyste zatrzymanie | `& "$env:LOCALAPPDATA\Glebosaurus\app\glebosaurus.exe" stop` – usługa wraca przy logowaniu, najpóźniej po godzinie (strażnik w zadaniu) albo od razu: `Start-ScheduledTask Glebosaurus` |
| Wyłączenie na dłużej | `Disable-ScheduledTask Glebosaurus`, potem `stop`; powrót: `Enable-ScheduledTask Glebosaurus` |
| Polecenia na danych usługi | `& "$env:LOCALAPPDATA\Glebosaurus\app\glebosaurus.exe" status --config "$env:LOCALAPPDATA\Glebosaurus\data\glebosaurus.config.json"` |

Na jednym koncie działa naraz jeden monitor: `run` z konsoli obok usługi od razu się kończy (kod 4), a usługa ponawia start co
5 min, dopóki działa inny monitor. Panel WWW: ścieżka programu (`app\glebosaurus.exe`) jest stała, więc Zapora Windows pyta tylko
przy pierwszym starcie – zezwól w sieci prywatnej (albo jednorazowo reguła na port, patrz „Panel WWW”).

## Nowy czujnik: parowanie lokalne

Bez konta i aplikacji Tuya: agent sam wymyśla `deviceId` i 6-bajtowy klucz, zapisuje je w czujniku przez BLE i sprawdza na
nowym połączeniu. Po włożeniu baterii czujnik nadaje jako niesparowany tylko ok. 20 s, dlatego skan rusza pierwszy:

```powershell
glebosaurus pair --id monstera --name "Monstera w salonie" --config local/glebosaurus.config.json --confirm-bind
# ... po komunikacie „Czekam na czujnik” włóż baterie do czujnika
```

Polecenie zapisuje klucz w `monstera.credentials.json` obok konfiguracji (to jedyna kopia – zrób jej kopię zapasową; bez niej
czujnik trzeba resetować) i podaje gotowy wpis do listy `sensors`:
`{ "id": "monstera", "name": "Monstera w salonie", "tuyaFile": "monstera.credentials.json" }`.
Przerwane parowanie wznawia się tym samym poleceniem (ten sam klucz). Gdy w zasięgu jest kilka niesparowanych czujników,
polecenie nie zgaduje – wymaga `--mac`, żeby nie sparować niewłaściwego. Sparowany czujnik znika z aplikacji Smart Life; reset
przyciskiem przywraca stan fabryczny. Tuż po przypisaniu czujnik reklamuje się rzadko, więc weryfikacja potrafi potrwać
do ok. 30 s (limit 90 s).

### Wymiana baterii i reset

**Wymiana baterii nie kasuje przypisania** (sprawdzone: minuta bez zasilania, bez przycisku reset – po włożeniu baterii ten
sam klucz od razu uwierzytelnił sesję). Wystarczy włożyć nowe baterie, monitor czyta czujnik dalej.

Przypisanie kasuje dopiero **reset przyciskiem**. Wtedy uruchom `glebosaurus pair --id <to samo id> --config … --confirm-bind`
(skan rusza pierwszy) i dopiero potem zresetuj czujnik i włóż baterie. Polecenie użyje zapisanego klucza – zwiąże czujnik
ponownie albo, jeśli nadal jest sparowany, tylko sprawdzi klucz. Nie tworzy drugiego klucza i nie ruszy czujnika o innym
UUID. Monitor, który trafi na niesparowany czujnik, zapisuje w dzienniku dokładnie to polecenie.

## Konfiguracja

Wzór z komentarzami: [examples/glebosaurus.config.json](examples/glebosaurus.config.json). Plik konfiguracji zawiera sekrety
(hasło aplikacji Gmail), a pliki `*.credentials.json` klucze czujników – żaden z nich nie trafia do repozytorium. Bez sekcji
`mail` alerty i raporty trafiają tylko do dziennika. Hasło aplikacji Gmail tworzysz na
https://myaccount.google.com/apppasswords (wymaga weryfikacji dwuetapowej).

## Użycie (Windows)

Polecenia poniżej zakładają opublikowany `glebosaurus.exe`; z kodu źródłowego:
`dotnet run --project src/Glebosaurus.Agent -f net10.0-windows10.0.19041.0 -- <polecenie> …`.

```powershell
glebosaurus run --config local/glebosaurus.config.json        # praca ciągła; dziennik w logs/ obok pliku konfiguracji
glebosaurus stop                                              # czysto zatrzymuje działający monitor i usługę (Windows)
glebosaurus status --config local/glebosaurus.config.json     # ostatnie odczyty i alerty z bazy
glebosaurus report --config local/glebosaurus.config.json --out raport.html
glebosaurus mail-test --config local/glebosaurus.config.json  # wysyła PRAWDZIWĄ wiadomość próbną do odbiorców alertów
glebosaurus mail-test --samples --config local/glebosaurus.config.json  # j.w. + przykładowy alert i raport ([PRZYKŁAD]) na telefon
glebosaurus mail-preview --out mail-preview                   # zapisuje przykładowe maile (HTML i tekst), niczego nie wysyła
glebosaurus send-log --config local/glebosaurus.config.json   # wysyła PRAWDZIWY log z wczoraj do administratora (--date RRRR-MM-DD: inny dzień)
glebosaurus web-password --config local/glebosaurus.config.json  # hasło i certyfikat panelu WWW (panel działa w ramach run)
glebosaurus scan --seconds 15 --verbose                       # diagnostyka: urządzenia Tuya w zasięgu
glebosaurus read --credentials local/monstera.credentials.json  # diagnostyka: jednorazowy odczyt jednego czujnika
glebosaurus listen --credentials local/monstera.credentials.json --out local/listen.log  # diagnostyka: wszystkie ramki czujnika
```

### Czy czujnik oddaje zaległe pomiary?

Tak: ATLO SMS1 zapisuje pomiar co godzinę i przy każdym połączeniu, a przy połączeniu oddaje całą zaległą serię i ją kasuje.
`run` zapisuje te zaległe pomiary w bazie z czasem czujnika, a najnowszy z nich traktuje jako stan bieżący. Do podglądu
surowych ramek (np. po zmianie sprzętu) służy `listen`:

1. Zatrzymaj monitor (`run`) i zamknij Smart Life na telefonie – czujnik przyjmuje jedno połączenie naraz.
2. Zostaw czujnik na kilka godzin bez żadnych odczytów (najlepiej na noc).
3. Raz uruchom `glebosaurus listen --credentials … --out local/listen.log`. Polecenie zostaje połączone, aż czujnik zamilknie na
   `--linger` sekund (domyślnie 20, najdłużej 5 min), i wypisuje każdą ramkę: komendę, punkty danych, czas czujnika i surowe bajty.
   Do czujnika nie wysyła nic poza zwykłym uwierzytelnieniem i prośbą o stan; w wyniku nie ma kluczy.
4. Ramki `0x8003` z różnymi czasami to historia. Same `0x8001` (jedna ramka) znaczyłyby, że czujnik oddaje tylko bieżący stan.
   Na ATLO SMS1 sprawdzone 2026-11-13: czujnik oddaje historię w `0x8003` i **kasuje ją po odebraniu** – `listen` zużywa
   zaległe rekordy, więc zapisuj wynik przez `--out` (szczegóły: docs/ARCHITEKTURA.md).

Jak to działa: co 30 s monitor sprawdza, które czujniki czekają na odczyt (odstęp `readIntervalMinutes`, domyślnie 15 min).
Dla nich nasłuchuje reklam BLE i każdy czujnik, który się pojawi, czyta od razu, a potem słucha dalej po pozostałe – czujnik
poza zasięgiem nie opóźnia więc tych w zasięgu. `scanWindowSeconds` (domyślnie 60 s) to łączny czas nasłuchu; czujnik, którego
nie było przez całe okno, jest „nie znaleziony”. Potem monitor zapisuje odczyty, przelicza alerty i wysyła maile.
Czujnik, który nie odpowiada, jest ponawiany z rosnącym odstępem (1, 2, 4, 8, max 10 min). Połączenie trwa ok. 14–17 s plus
ok. 1,1 s na każdy zaległy rekord (czujnik oddaje je od najstarszego, a stan z chwili połączenia jest ostatni, więc nie
kończymy na pierwszym komplecie, tylko po 3 s ciszy). Każdy odczyt kosztuje baterię czujnika.

Zasięg: w dzienniku przy każdym odczycie jest poziom sygnału („sygnał -77 dBm”), a przy „nie znaleziono w reklamach” – ostatni
usłyszany poziom i jak dawno. Z pomiarów na Windows (kilka prób): przy −75…−81 dBm odczyty się udawały, przy ok. −94 dBm czujnika nie było w reklamach. Poziom
zależy od ułożenia czujnika (ok. 14 dB różnicy w tej samej skrzynce), nie od ziemi; `glebosaurus scan` pokazuje średnią i zakres
dla każdego urządzenia, więc pozycję czujnika można dobrać na żywo (patrz docs/ARCHITEKTURA.md, „Zasięg Cisy”).

Alerty: „czas podlać” (wilgotność poniżej progu przez kilka odczytów z rzędu, wygasa dopiero powyżej progu wygaszenia),
„słaba bateria”, „brak odczytów” (żadnego udanego odczytu dłużej niż `silentAfterHours`, domyślnie 6 h – liczone też za czas,
gdy komputer był wyłączony; po starcie i po uśpieniu monitor ma jednak 30 min na odczyty, zanim taki alert otworzy). Mail o
nowych alertach idzie od razu (kilka naraz w jednej wiadomości), przypomnienie co `reminderHours`. Raporty o porach z `reports.times`; spóźniony raport jest nadrabiany do `catchUpHours`.
Wilgotność z tańszych czujników wymaga kalibracji – domyślne progi (25% / 35%) są tylko punktem wyjścia.

### Maile (alerty i raporty)

Maile są pisane z myślą o telefonie: HTML to jedna wąska kolumna (do 480 px) z kartą na czujnik – nazwa, plakietka stanu,
trzy duże liczby (wilgotność, temperatura gleby, bateria) i pasek wilgotności – a wersja tekstowa ma krótkie wiersze.
Elementy są kwadratowe, w pastelowych kolorach (czerwień – podlej, żółć – słaba bateria, zieleń – OK, szarość – brak odczytów).
Tytuł zaczyna się od tego, co ważne („Monstera – czas podlać”, „Poranny raport – do podlania: Monstera, Fikus +1”), a
w podglądzie na liście wiadomości widać wilgotność wszystkich roślin. Raporty idą o porach z `reports.times` (domyślnie
07:00 i 19:00: „Poranny” i „Wieczorny”), alerty od razu po otwarciu, przypomnienia co `reminderHours`.

Odbiorców ustawiasz w sekcji `mail`: `alertRecipients` (alerty i wiadomość próbna) oraz `reportRecipients` (raporty; puste =
ci sami co alertów). Można podać kilka adresów; adresy są walidowane przy starcie. Wygląd obejrzysz bez wysyłania:
`glebosaurus mail-preview` zapisuje przykładowe maile jako pliki HTML (w przeglądarce użyj widoku telefonu), a
`glebosaurus mail-test --samples` wysyła je prawdziwie na telefon (tytuły z „[PRZYKŁAD]”, dane zmyślone). Każdy raport ma przy
czujniku wykres ostatnich 3 dób z samego HTML (kolumny po 2 godz.: niebieska kreska – średnia wilgotność, blady pasek –
temperatura od najniższej do najwyższej, różowe tło – poniżej progu podlania; przy wielu czujnikach kolumny są szersze,
żeby Gmail nie przyciął maila), a wersja tekstowa linijkę `▁…█`. `mail-preview` zapisuje też `raport-20.html` z 20 czujnikami. W stopce każdego
maila jest wersja Glebosaurusa („Wiadomość automatyczna · Glebosaurus 0.2.1”); numer wersji jest w jednym miejscu,
w `Directory.Build.props`.

### Log techniczny dla administratora

Agent trzyma własny dziennik w plikach dziennych `logs/glebosaurus-RRRRMMDD.log` obok pliku konfiguracji (czas lokalny) i
usuwa pliki starsze niż `logs.retentionDays` (domyślnie 7 dni; wczorajszy plik zawsze zostaje). **W pierwszym przebiegu
każdego dnia** (po północy albo przy pierwszym starcie danego dnia) wysyła pod adres z `mail.adminRecipients` – osobny
od odbiorców alertów i raportów – dziennik z **całego poprzedniego dnia**: w treści streszczenie (liczba wpisów, ostatnie
ostrzeżenia i błędy, w tym samym wąskim, pastelowym stylu co reszta), a pełny dziennik jako załącznik `.log` (nazwa
`glebosaurus-<agent>-RRRRMMDD.log`, do 5 mln znaków; dłuższy jest obcinany od początku). Puste `adminRecipients` wyłącza wysyłkę.

- Wysłanie jest odnotowane w bazie, więc restart agenta w ciągu dnia nie powtarza maila, a nieudana wysyłka jest ponawiana
  po 5 minutach (niezależnie od alertów – kłopot z tym mailem nie opóźnia „czas podlać”).
- Jeśli agent poprzedniego dnia nie działał (brak pliku), nie ma czego wysyłać; dni starsze niż wczorajszy nie są
  dosyłane wstecz. Brak maila z logiem pierwszego dnia po północy to też sygnał, że agent wczoraj nie pracował.
- Na żądanie: `glebosaurus send-log [--date RRRR-MM-DD]` wysyła prawdziwy dziennik z wybranego dnia (nie zmienia znacznika
  automatycznej wysyłki); `mail-test --samples` wysyła też przykładowy log, a `mail-preview` zapisuje go do obejrzenia.
- Dziennik zawiera m.in. nazwy czujników, odczyty i ścieżki plików; agent nie zapisuje w nim kluczy czujników ani hasła
  Gmaila (zasada projektu), więc załącznik można bezpiecznie wysyłać mailem.

## Panel WWW (diagnostyka w przeglądarce)

Pracujący monitor (`run`) może pokazywać swój stan w przeglądarce, w sieci lokalnej. Wygląd jak w mailach: kwadratowe,
pastelowe karty, jedna kolumna na telefonie. Strony odświeżają się same co 30 s (co 3 s, gdy trwa albo czeka odczyt lub raport).

**Pulpit** (strona główna) – to, co najważniejsze: karta każdego czujnika (alerty, wilgotność, temperatura, bateria, wiek
odczytu, ewentualny problem z ostatnią próbą) z **wykresem ostatnich 3 dób**, a pod kartami otwarte alerty, stan poczty
(ostatnia wysyłka, ostatni błąd, wstrzymanie), najbliższy raport, końcówka dziennika (dziś lub wcześniej, wszystko albo same
ostrzeżenia i błędy) i ustawienia monitora.

**Strona czujnika** (nazwa na karcie albo „Szczegóły i pełna historia”) – wszystko o jednym czujniku: stan teraz (z
sygnałem), **przeglądalny wykres całej historii**, jego otwarte alerty, ostatnie odczyty w tabeli („Pokaż więcej” do 1000),
diagnostyka (ostatnia próba i jej wynik, ponowienia, sygnał, najstarszy odczyt w bazie), ustawienia (progi, MAC, produkt) i
wpisy dziennika o tym czujniku. Lista „Inny czujnik” przełącza między czujnikami.

**Wykresy:** wilgotność (lewa oś, %) i temperatura gleby (prawa oś, °C) na jednym wykresie; każda oś obejmuje zakres
wartości z widocznego okresu (wilgotność co najmniej 10 punktów procentowych, temperatura 2 °C – żeby szum nie wyglądał jak
skoki), cienka czerwona linia to próg podlania (gdy mieści się w zakresie). Przerwa w linii = brak odczytów dłużej niż
kilka interwałów. Najechanie, dotknięcie albo strzałki na klawiaturze pokazują wartości w danym miejscu. Przy dłuższych
okresach monitor uśrednia odczyty (np. z 2 godz. albo z doby) – wtedy za linią widać blady pas od najmniejszej do
największej wartości, a dymek podaje średnią, zakres i liczbę odczytów. Na stronie czujnika: zakresy 3 doby / 7 dni / 30 dni
/ 90 dni / rok / całość, przesuwanie przeciąganiem (też palcem) i strzałkami ◀ ▶, powiększanie Ctrl + kółkiem myszy,
dwoma palcami albo przyciskami − +, „Teraz” wraca do bieżącego czasu, a pasek pod wykresem pokazuje całą historię z
prostokątem widocznego okresu (można go przeciągać). Wykresy to SVG rysowany w przeglądarce, bez dodatkowych bibliotek.

**Odczyt na żądanie:** przycisk „Odczytaj teraz” na karcie czujnika i „Odczytaj wszystkie teraz” nad kartami zlecają odczyt
wcześniej, niż przewiduje harmonogram (także w trakcie odstępu po nieudanych próbach). Zlecenie trafia do kolejki monitora,
który budzi się od razu i czyta po kolei – nigdy równolegle z trwającym połączeniem (czujnik przyjmuje jedno naraz). Karta
pokazuje „czeka na odczyt…”, potem „odczyt trwa…”, a na końcu nowy wynik (albo przyczynę porażki). Każde połączenie kosztuje
baterię, dlatego ten sam czujnik można wymusić najwyżej **raz na minutę** od ostatniej próby (przycisk pokazuje, od kiedy).
Zlecenia trafiają do dziennika (kto i co). Konfiguracji panel nie zmienia – to robi się w pliku i restartem monitora.

**Raport na żądanie:** „Wyślij raport teraz…” w sekcji „Poczta i raporty” wysyła **prawdziwy mail** – ten sam raport co z
harmonogramu, do tych samych odbiorców (`mail.reportRecipients`, a gdy pusta – odbiorcy alertów) – dlatego panel najpierw
pyta, pokazując adresy. Monitor wysyła go w najbliższym cyklu (budzi się od razu; jeśli trwa odczyt czujników, raport idzie
zaraz po nim, więc pokazuje świeże dane). Zaplanowane raporty idą dalej o swoich porach. Wynik (wysłany albo błąd poczty)
pojawia się w notce i w karcie raportów; kolejny raport na żądanie najwcześniej **minutę** po poprzedniej próbie. Przycisk
jest nieaktywny, gdy poczta nie jest skonfigurowana.

Włączenie:

1. Dodaj do konfiguracji sekcję `"web": { "port": 8470 }` (wzór w [examples/glebosaurus.config.json](examples/glebosaurus.config.json)).
2. Na komputerze monitora uruchom `glebosaurus web-password --config local/glebosaurus.config.json` i podaj hasło (co
   najmniej 12 znaków, dwa razy). Polecenie zapisuje tylko **skrót** hasła (PBKDF2-SHA256, 600 tys. iteracji) w
   `glebosaurus-web.password` obok konfiguracji, tworzy certyfikat HTTPS `glebosaurus-web.pfx` i wypisuje adresy panelu oraz
   **odcisk SHA-256 certyfikatu**.
3. Uruchom monitor ponownie i otwórz np. `https://<nazwa-komputera>:8470/`. Przeglądarka ostrzeże, że certyfikat nie pochodzi
   z zaufanego urzędu – to oczekiwane (certyfikat z podpisem własnym); porównaj odcisk w szczegółach certyfikatu z tym z
   `web-password` albo z dziennika monitora i przejdź dalej.
4. Windows: dostęp z innych urządzeń wymaga reguły Zapory (PowerShell jako administrator; `web-password` wypisuje ją z
   właściwym portem). Reguła wpuszcza tylko profil Prywatny i lokalną podsieć:

```powershell
New-NetFirewallRule -DisplayName "Glebosaurus panel" -Direction Inbound -Protocol TCP -LocalPort 8470 -Profile Private -RemoteAddress LocalSubnet -Action Allow
```

Zabezpieczenia (kilka warstw):

- **Tylko sieć lokalna:** połączenia spoza loopbacku i sieci prywatnych (10/8, 172.16/12, 192.168/16, link-local, IPv6 ULA) są
  odrzucane, nawet gdy router przekieruje port na zewnątrz. Węższą listę ustawia `web.allowedNetworks` (np.
  `["192.168.1.0/24"]`); loopback jest dozwolony zawsze.
- **HTTPS** z certyfikatem z podpisem własnym (ECDSA P-256, ważny 825 dni, odnawiany automatycznie 30 dni przed końcem –
  wtedy zmienia się odcisk).
- **Hasło:** na dysku tylko skrót; logowanie daje ciasteczko sesji `Secure`, `HttpOnly`, `SameSite=Strict`, ważne
  `web.sessionHours` (domyślnie 12 godz.); restart monitora wylogowuje. Po 5 błędnych hasłach z jednego adresu w ciągu
  15 min adres jest blokowany na 15 min.
- **Sekrety nie wychodzą:** panel nie pokazuje kluczy czujników (`localKey`, `deviceId`) ani hasła aplikacji Gmail (testy
  sprawdzają, że ich nie ma w odpowiedziach). Ścisłe nagłówki (CSP bez skryptów inline, zakaz osadzania w ramkach,
  `no-store`), POST tylko z samego panelu (`Sec-Fetch-Site`), a akcje dodatkowo wymagają własnego nagłówka, którego formularz
  z obcej strony nie umie wysłać. Odrzucone żądania trafiają do dziennika.
- **Monitor ważniejszy niż panel:** brak hasła, zajęty port czy inny kłopot z panelem kończy się wpisem w dzienniku – monitor
  pracuje dalej bez panelu.

Pliki `glebosaurus-web.password` i `glebosaurus-web.pfx` (z kluczem prywatnym) są sekretami jak klucze czujników – leżą w
`local/` (poza repozytorium); na Linuksie powstają z prawami 600. Nowe hasło: ponownie `web-password` i restart monitora.

## Tryb chmurowy (faza 2) – od 0.2.0, na bestia5 od 2026-11-18

Wielu agentów (Windows, Raspberry Pi, później Android) pracuje na wspólnej chmurze Supabase: czujniki, progi, harmonogramy,
odbiorcy, klucze Tuya i hasło Gmaila są w chmurze, agenci dzielą się czujnikami przez dzierżawy (żaden czujnik nie ma dwóch
połączeń naraz), a maile wysyła jeden agent z blokadą nadawcy – każdy najwyżej raz. Projekt: [ARCHITEKTURA](docs/ARCHITEKTURA.md),
zasady 1–4 i 13–15; stan: [ROADMAP](docs/ROADMAP.md), faza 2.1.

Przejście z trybu samodzielnego – właściciel, z plikiem administratora projektu (`local/cloud-admin.json`, z hasłem bazy;
każde polecenie bez `--confirm` tylko pokazuje plan):

```powershell
glebosaurus cloud migrate --admin local/cloud-admin.json --confirm                 # schemat bazy w chmurze
glebosaurus cloud import --admin local/cloud-admin.json --config local/glebosaurus.config.json --confirm
glebosaurus cloud agent --admin local/cloud-admin.json --name bestia5 --sender-priority 2 --confirm   # wypisze kod
```

Ten sam `cloud agent` z nazwą istniejącego agenta zmienia jego rolę (np. `--sender-priority 1` – główny nadawca; bez
`--sender-priority` – tylko zbiera); działający agent przejmuje ją w ciągu ok. minuty, bez ponownej rejestracji. Zgubione
urządzenie albo wyciek tokenu – odcięcie agenta (token przestaje działać od razu, jego niewykorzystane kody przepadają;
czujniki przejmą inni po wygaśnięciu dzierżaw, najwyżej ok. 32 min):

```powershell
glebosaurus cloud agent --admin local/cloud-admin.json --name bestia5 --revoke --confirm   # z powrotem: nowy kod, enroll, restart
```

`cloud import` przenosi ustawienia, odbiorców, czujniki z kluczami Tuya, hasło Gmaila i adres sygnału healthchecks.io
(sekretów nie wypisuje; nic w chmurze nie usuwa, powtórzony – nadpisuje). Historii odczytów nie przenosi: wysyła ją sam agent
w trybie chmurowym ze swojej bazy. Na urządzeniu agenta plik konfiguracji ma sekcję `cloud` zamiast `sensors` i `mail` (wzór:
[examples/glebosaurus.cloud.config.json](examples/glebosaurus.cloud.config.json)), potem rejestracja kodem i zwykła praca:

```powershell
glebosaurus enroll --config glebosaurus.config.json     # bez --code pyta o kod, nie pokazując go
glebosaurus run --config glebosaurus.config.json
```

Zainstalowana usługa Windows (tak przeszła bestia5 2026-11-18): najpierw zwykła aktualizacja do 0.2.0 lub nowszej skryptem
instalacji, potem w zwykłym oknie PowerShell `Disable-ScheduledTask Glebosaurus`, `glebosaurus stop`, w
`%LOCALAPPDATA%\Glebosaurus\data` konfiguracja z sekcją `cloud` zamiast dotychczasowej (tę zachowaj obok, np. jako
`glebosaurus.standalone.config.json` – to droga powrotu), `cloud agent … --confirm` i `enroll --config <ten plik>`, na koniec
`Enable-ScheduledTask Glebosaurus` i `Start-ScheduledTask Glebosaurus`. Agent przy pierwszej synchronizacji wysyła całą
historię odczytów z bazy usługi (paczkami po 500).

Token agenta (`glebosaurus-cloud.token`) i ostatnia dobra kopia konfiguracji z chmury (`glebosaurus-cloud-config.bin`, z
kluczami Tuya) leżą obok konfiguracji, chronione DPAPI (Windows) albo prawami 600 (Linux) – to sekrety, nie kopiuj ich na
inne urządzenia. Bez sieci agent startuje z kopii i czyta czujniki do końca dzierżaw; odczyty czekają w lokalnej bazie i
trafiają do chmury po powrocie sieci.

**Panel WWW w trybie chmurowym** (sekcja `web`, hasło jak zwykle przez `web-password`): ten sam pulpit i strona czujnika,
ale z odczytami **wszystkich** agentów – agent z panelem trzyma ich lokalną kopię (dociąga z chmury co 2 min to, co przyjęła,
także odczyty dosłane z opóźnieniem; nie odsyła ich z powrotem). Przy każdym czujniku widać, który agent go czyta (ma
dzierżawę) i kto poza tym agentem słyszy go najlepiej; karta „Chmura i agenci” pokazuje synchronizację, bufor, nadawcę
maili, kopię odczytów i wszystkich agentów. „Odczytaj teraz” i „Wyślij raport teraz” idą poleceniem przez chmurę – odczyt do
agenta z dzierżawą czujnika (może nim być ten), raport do obecnego nadawcy (zwykle w ciągu 1–2 min). Alerty są ze wspólnego
stanu w chmurze. Konfiguracji panel nie zmienia – czujniki, progi i poczta są w chmurze.

**Kopia zapasowa chmury** (darmowy plan Supabase kopii nie robi):

```powershell
glebosaurus cloud backup --admin local/cloud-admin.json            # teraz; domyślnie do local/backups, 14 najnowszych
```

Kopia to jeden plik ZIP z jednej chwili (transakcja tylko do odczytu): wszystkie tabele schematu `glebosaurus` jako CSV (bez
dzienników agentów), lista migracji i **odszyfrowane** sekrety z Vault (klucze Tuya, hasło Gmaila, adres sygnału
healthchecks.io) – w zrzucie bazy są zaszyfrowane kluczem projektu, który przepada razem z projektem. Plik jest więc sekretem
jak `cloud-admin.json` (na Linuksie prawa 600); poza ten komputer – tylko w zaszyfrowane miejsce. Robi go sam program (bez
`pg_dump`). Codziennie robi go agent z sekcją `cloud.backup` w konfiguracji (wzór w
[examples/glebosaurus.cloud.config.json](examples/glebosaurus.cloud.config.json)): raz na dzień kalendarzowy, przy pierwszej
okazji (komputer bywa w nocy wyłączony), co najmniej 10 min po starcie; potrzebuje pliku administratora z adresem projektu i
hasłem bazy – bez klucza tajnego – więc tylko jeden zaufany agent (dziś bestia5, docelowo Raspberry Pi). Odtwarzania z kopii
jeszcze nie ma (ROADMAP, faza 4).

## Raspberry Pi (linux-arm64) – niesprawdzone

Paczka: `publish/Glebosaurus-<wersja>-linux-arm64.tar.gz` (wersja pochodzi z `Directory.Build.props`, obecnie 0.2.1, i jest widoczna
w stopce maili; `publish/` jest poza repozytorium; zależna od frameworka – od 2026-11-14
(panel WWW) wymaga **ASP.NET Core Runtime 10** (zawiera .NET Runtime; sam .NET Runtime już nie wystarczy), np.
`./dotnet-install.sh --channel 10.0 --runtime aspnetcore`; budowanie:
`dotnet publish src/Glebosaurus.Agent -c Release -f net10.0 -r linux-arm64 --self-contained false`).

```bash
sudo mkdir -p /opt/glebosaurus && sudo tar -xzf Glebosaurus-<wersja>-linux-arm64.tar.gz -C /opt/glebosaurus
systemctl status bluetooth          # BlueZ musi działać
dotnet /opt/glebosaurus/glebosaurus.dll scan --seconds 15 --verbose
```

Plik konfiguracji i pliki kluczy skopiuj osobno, nie w paczce. Jako usługa: [deploy/glebosaurus.service](deploy/glebosaurus.service)
(wzór). Czujnik przyjmuje jedno połączenie naraz – telefon ani drugi monitor nie mogą go wtedy trzymać. Jeśli `scan` zgłasza brak
dostępu do Bluetooth: użytkownik powinien być w grupie `bluetooth`, a polityka D-Bus dla BlueZ musi mu na to pozwalać (uwagi w
dokumentacji Shiny dla Linuxa). Na Linuxie dopasowanie po adresie MAC opiera się na identyfikatorze urządzenia z BlueZ
(nieprzetestowane); zapasowo działa UUID Tuya z reklamy.

## Licencje

Kod protokołu Tuya powstał na podstawie projektów MIT – [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Kod wysyłki Gmail
pochodzi z LibrusNotifier (ten sam autor). Licencja samego projektu: do decyzji (patrz ROADMAP).
