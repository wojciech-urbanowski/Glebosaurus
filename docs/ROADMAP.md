# Glebosaurus – plan dalszych prac

Stan na 2026-11-13: fazy 0 i 1 (spike sprzętowy oraz lokalny monitor) są zrobione i sprawdzone na żywo na Windows.
Poniżej propozycja kolejnych faz. To plan do akceptacji i korygowania, nie zobowiązanie; szczegóły faz 2–3 trzeba
doprecyzować tuż przed ich startem (zwłaszcza wybór chmury). Opisy projektowe: [ARCHITEKTURA.md](ARCHITEKTURA.md).

Wielkości: **S** – dzień lub mniej, **M** – kilka dni, **L** – tydzień lub więcej (przy pracy w wolnych chwilach).

## Przegląd

| Faza | Cel | Wielkość | Zależy od |
|---|---|---|---|
| 0, 1 | Spike sprzętowy, lokalny monitor (SQLite, alerty, raporty, parowanie) | ✔ zrobione | – |
| **1b** | Domknięcie lokalnego monitora: prawdziwy mail, Raspberry Pi, praca w tle na Windows, baterie, kalibracja | M | 1 |
| **2a** | Chmura: wybór dostawcy, model danych, rejestracja monitorów, synchronizacja odczytów | L | 1b (częściowo) |
| **2b** | Zaszyfrowana konfiguracja w chmurze (klucz tylko na urządzeniach i w przeglądarce) | L | 2a |
| **2c** | Wiele monitorów naraz: dzierżawy czujników, „claimy” alertów i raportów | L | 2a, 2b |
| **3** | Interfejs webowy: pulpit, historia, alerty, raporty, konfiguracja, parowanie zdalne | L | 2a (3a), 2b (3c) |
| **4** | Wdrożenie i utwardzanie: aktualizacje, monitoring monitorów, bezpieczeństwo, CI, runbook | M | 2, 3 |
| **5** | Rozszerzenia (opcjonalne): mądrzejsze podlewanie, inne kanały, inne czujniki, zasięg | – | 1b |

Kolejność ma zmniejszać ryzyko i dawać użyteczny produkt jak najwcześniej: **po fazie 1b aplikacja jest już w pełni użyteczna
bez chmury** (jeden lub kilka niezależnych monitorów, każdy ze swoim plikiem konfiguracji). Chmura i WWW to wygoda i
koordynacja, nie warunek działania.

Zadania wewnątrz faz są w większości niezależne, więc nadają się do równoległej pracy w osobnych sesjach (każda zmienia inne
pliki); wyjątki są zaznaczone.

---

## Faza 1b – domknięcie lokalnego monitora

Cel: monitor pracuje w tle na obu systemach, bez nadzoru, i wysyła prawdziwe maile.

| # | Zadanie | Wielkość |
|---|---|---|
| 1 | **Prawdziwy mail.** Konto Gmail z hasłem aplikacji, `mail-test`, alert i raport na prawdziwej skrzynce (też w telefonie); sprawdzić wygląd HTML w Gmailu, dzienny limit wysyłki Gmaila (zweryfikować aktualną wartość) i zachowanie przy błędzie logowania. *Zrobione 2026-11-13: szablony przepisane pod wąski ekran, konfigurowalni odbiorcy alertów i raportów, `mail-preview` i `mail-test --samples`, konto nadawcy w `local/`. Pierwsza prawdziwa wysyłka (`mail-test --samples`, 3 maile) udana, SMTP przyjął wszystkie. Układ i kolorystykę na telefonie w Gmailu zatwierdził użytkownik. 2026-11-14 `run` wysłał pierwszy prawdziwy mail o alercie (słaba bateria), SMTP przyjął. Zostaje: limit dzienny, zachowanie przy błędnym haśle, prawdziwy raport z `run`.* | S |
| 2 | **Raspberry Pi.** `scan`, `read`, `run` na Pi 64-bit z BlueZ (od 2026-11-14 potrzebny ASP.NET Core Runtime 10 – panel WWW – albo paczka samodzielna): dopasowanie po MAC z identyfikatora BlueZ (dziś nieprzetestowane, jest zapasowe dopasowanie po UUID Tuya), uprawnienia D-Bus i grupa `bluetooth`, stabilność połączeń (Wi-Fi/BLE na jednym układzie), unit systemd z `Restart`. Ustalić minimalny wspierany sprzęt. Jeśli Shiny na Linuxie zawiedzie: zapasowo SimpleSharpBLE (licencja BUSL-1.1, tylko użytek niekomercyjny) albo bezpośrednio Linux.Bluetooth za interfejsem `IGattLink`. | M |
| 3 | **Windows w tle.** Zadanie Harmonogramu uruchamiane przy logowaniu, bez okna konsoli (launcher albo podsystem okienkowy), skrypt `deploy/install-windows.ps1` (rejestracja i wyrejestrowanie), zachowanie po uśpieniu/wybudzeniu i po utracie adaptera BLE. Sprawdzić, czy WinRT BLE faktycznie nie działa w usłudze (sesja 0) – jeśli działa, usługa jest prostsza. | M |
| 4 | **Baterie i interwał.** Kilka dni pracy co 15 min na 2–3 czujnikach, wykres wskazania baterii w czasie, wybór domyślnego interwału (15/30/60 min). Rozważyć interwał adaptacyjny: częściej blisko progu podlania, rzadziej gdy gleba jest wilgotna i nocą. | S + czas |
| 5 | **Kalibracja wilgotności.** Pomiary: powietrze, woda, mokra i sucha ziemia doniczkowa; kalibracja per czujnik (punkty „sucho”/„mokro” albo offset i skala) w konfiguracji; sensowne progi domyślne i opis w README. Tani czujnik nie podaje procentów porównywalnych między glebami. | M |
| 6 | **Parowanie pozostałych czujników** (ok. 10; większość widać już w skanie jako sparowane ze Smart Life – każdy trzeba zresetować, co usuwa go z aplikacji, i sparować po jednym w oknie 20 s) i procedura kopii zapasowej kluczy (do czasu fazy 2b to jedyne miejsce, gdzie one są). | S |
| 7 | **Operacje.** `status --json`, skrypt aktualizacji paczki na Pi (jak `aktualizuj-rpi.sh` w LibrusNotifier), przeładowanie konfiguracji bez restartu (opcjonalnie). *Zrobione 2026-11-13: codzienny log techniczny do administratora (`mail.adminRecipients`, załącznik z dziennikiem z poprzedniego dnia) z retencją dzienników (7 dni) i polecenie `send-log`; wersja Glebosaurusa w stopce maili. Zrobione 2026-11-14: nasłuch BLE na przemian z odczytem (czujnik poza zasięgiem nie opóźnia pozostałych) i okno skanu domyślnie 60 s (ARCHITEKTURA.md, zasada 8); także lokalny panel diagnostyczny WWW, podgląd stanu i odczyt czujników na żądanie, bez zmian konfiguracji (HTTPS z certyfikatem z podpisem własnym, tylko sieć lokalna, hasło przez `web-password`; patrz ARCHITEKTURA.md, zasada 11); 2026-11-14 sprawdzony na żywo z `run` na Windows (logowanie w przeglądarce); zostaje wejście z telefonu w sieci domowej i Pi.* | S |
| 7a | **Panel WWW – dalsze etapy (opcjonalnie).** Etap 2 – akcje: *zrobione 2026-11-14: „odczytaj teraz” dla czujnika i dla wszystkich (kolejka silnika, budzenie pętli, najwyżej raz na minutę na czujnik, nagłówek akcji przeciw CSRF; ARCHITEKTURA.md, zasada 11); tego samego dnia „wyślij raport teraz” z potwierdzeniem (prawdziwy mail do odbiorców raportów, kolejka silnika, najwyżej raz na minutę; harmonogram bez zmian).* Zrobione 2026-11-14: wykres wilgotności i temperatury (SVG, dwie osie) – na pulpicie ostatnie 3 doby przy każdym czujniku, a na nowej stronie czujnika pełna historia do przeglądania (zakresy, przesuwanie, powiększanie, pasek przeglądu) razem ze wszystkimi szczegółami czujnika (ARCHITEKTURA.md, zasada 11). Zostaje: „wyślij log teraz”, przeładowanie konfiguracji między cyklami, sprawdzenie wykresów na prawdziwym monitorze i na telefonie. Etap 3 – edycja konfiguracji bez sekretów: **odłożony decyzją użytkownika (2026-11-14)**, pełny edytor przewiduje faza 3c. | S–M |
| 7b | **Wykres w raporcie mailowym** (życzenie użytkownika z 2026-11-14). Ostatnie 3 doby przy każdym czujniku w raporcie. Gmail nie pokazuje SVG w treści (caniemail, 09.2026: obrazek SVG zamienia na PNG i potrzebuje adresu w internecie albo załącznika; `data:` nie działa), a grafiki rastrowej użytkownik nie chce – propozycja: wykres z samego HTML (tabela ok. 36 kolumn po 2 godz. w szerokości 480 px, słupki wilgotności w kolorach progów, temperatura jako znacznik w tej samej kolumnie na własnej skali; Gmail obsługuje wysokości bloków, pozycjonowania nie). Dane z `ReadingSeriesBuilder` (Core). Najpierw `mail-preview`, potem akceptacja układu na telefonie – wygląd maili jest zatwierdzony i zmienia się tylko za zgodą. *Prototyp 2026-11-14 (`MailChart`, ARCHITEKTURA.md, zasada o mailach): kolumny po 2 godz., w każdej niebieska kreska średniej wilgotności i pod nią blady pasek temperatury od najniższej do najwyższej (dwie skale), różowe tło poniżej progu, budżet 90 KB (2 godz. do ok. 9 czujników, przy 20 – 8 godz.), linijka `▁…█` w wersji tekstowej; widać go w `mail-preview` (`raport.html`, `raport-20.html`). 2026-11-14 o 18:50 `mail-test --samples` wysłał pierwszą wersję (kreski po 3 godz.); użytkownik obejrzał ją na telefonie („wygląda OK”) i poprosił o 2 godz. i temperaturę jako stonowany zakres – zrobione tego samego dnia. Tego samego dnia historia podpięta w `SnapshotBuilder` – wykres mają wszystkie raporty z monitora. Zostaje: obejrzenie poprawionej wersji na telefonie (pierwszy prawdziwy raport z wykresem) i ewentualnie decyzja co do wielu czujników (szersze kolumny / kilka maili).* | S–M |
| 8 | **Higiena repozytorium.** Decyzja o licencji, `git init` i zdalne repozytorium, skan sekretów przed pierwszym pushem, CI (build + testy na push, publikacja paczek jako artefaktów), analizatory/formatowanie. | S |

**Kryterium ukończenia:** 7 dni ciągłej pracy na Windows i na Pi bez ingerencji i bez zawieszeń; co najmniej jeden prawdziwy
alert i jeden raport w skrzynce; udokumentowana prognoza życia baterii; wszystkie czujniki sparowane, klucze zarchiwizowane.

**Ryzyka:** Shiny/BlueZ na Pi (zadanie 2 – największe), BLE w tle na Windows (3), zużycie baterii czujników (4).

---

## Faza 2 – chmura i wiele monitorów

Zasada nadrzędna z architektury: **chmura nie zna sekretów** (klucze Tuya, hasło Gmaila, nazwy czujników). Dlatego mail i
reguły alertów zostają na monitorach, a chmura jest „głupim” magazynem z uwierzytelnianiem i atomowymi operacjami.

### 2a – dostawca, model danych, synchronizacja odczytów

1. **Decyzja o dostawcy** (patrz „Decyzje”). Przed wyborem sprawdzić aktualne limity darmowych planów, region UE, atomowe
   operacje (potrzebne do „claimów”), eksport danych i brak własnego serwera do utrzymania. Domyślna hipoteza: Supabase
   (Postgres, logowanie, reguły dostępu na poziomie wierszy, funkcje SQL); alternatywa: Azure (.NET od początku do końca).
2. **Model danych:** `monitors` (id, nazwa, hash tokenu, ostatni kontakt, wersja, stan adaptera BLE), `readings`
   (czujnik, czas, wilgotność, temperatura, bateria, stan baterii, monitor), `alerts`, `report_runs`/claimy, `leases`,
   `config` (koperta zaszyfrowana, wersja), `commands` (kolejka poleceń do monitorów – potrzebna w 3d). Identyfikatory
   czujników losowe i nic nie zdradzające.
3. **Rejestracja monitora:** jednorazowy kod z UI/CLI → odwoływalny token urządzenia; polecenie `glebosaurus enroll`;
   token w DPAPI (Windows) albo pliku z prawami 600 (Pi).
4. **Synchronizacja:** dekorator na `IMonitorStore` z lokalnym outboxem (SQLite), wysyłka wsadowa idempotentna (upsert po
   czujniku i czasie), backoff, praca offline; heartbeat monitora.

**Kryterium:** odczyty z dwóch monitorów widoczne w chmurze bez duplikatów; godzinna przerwa sieci kończy się dosłaniem bez
strat; unieważnienie tokenu odcina monitor.

### 2b – zaszyfrowana konfiguracja

1. **Koperta:** AES-256-GCM, klucz z hasła przez PBKDF2-SHA256 (≥ 600 tys. iteracji), sól i parametry w kopercie, wersja
   w danych uwierzytelnianych (ochrona przed podmianą na starszą wersję). Wspólne wektory testowe dla .NET i JS
   (przeglądarka użyje WebCrypto), żeby oba światy na pewno się rozumiały.
2. **Konfiguracja z koperty:** ten sam model `AgentConfig`, w nim czujniki z kluczami Tuya, progi, harmonogramy, odbiorcy,
   dane Gmaila. Pobieranie po wersji, przeładowanie na żywo, ostatnia dobra konfiguracja zapamiętana lokalnie.
3. **Migracja:** `glebosaurus config import` przenosi dzisiejszy plik konfiguracji i pliki `*.credentials.json` do koperty
   – od tej chwili klucze czujników mają kopię zapasową w chmurze (zaszyfrowaną).
4. **Hasło:** rejestracja monitora pyta o nie raz; lokalnie chronione DPAPI/plikiem 600. Zdecydować o odzyskiwaniu
   (eksport zaszyfrowanej kopii, hasło odzyskiwania) – utrata hasła oznacza utratę konfiguracji.

**Kryterium:** zrzut bazy chmury nie zawiera żadnego sekretu ani nazwy; zła koperta albo błędne hasło → monitor odrzuca ją i
pracuje na ostatniej dobrej konfiguracji.

### 2c – koordynacja wielu monitorów

1. **Dzierżawa czujnika** (TTL ok. 2 min) przed połączeniem, z jitterem; czujnik przyjmuje jedno połączenie naraz, a kilka
   monitorów może go widzieć. Brak statycznego przydziału – monitor próbuje czujniki, których reklamy widzi.
2. **Claimy** alertów i raportów (unikalny klucz: raport + termin, alert + stan), wygasające, żeby inny monitor mógł je
   przejąć. Lokalne znaczniki z fazy 1 (`report_runs`, `last_notified_at`) zastępuje atomowa operacja w bazie.
3. **„Monitor milczy”:** alert wysyłany przez inny monitor (z claimem).
4. **Tryb bez chmury:** zdecydować o polityce (patrz „Decyzje”): monitor offline nadal wysyła własne alerty (ryzyko
   duplikatów po powrocie sieci) czy czeka.

**Kryterium:** dwa monitory w zasięgu tego samego czujnika → brak równoczesnych połączeń i dokładnie jeden mail na alert i na
raport; zatrzymanie monitora w trakcie claimu → przejęcie przez drugi. Najpierw testy na atrapie chmury, potem Windows + Pi.

---

## Faza 3 – interfejs webowy

Aplikacja przeglądarkowa (hipoteza: Blazor WASM z cienkim interopem do WebCrypto; alternatywa: TypeScript), logowanie OAuth
z listą dozwolonych kont, hosting statyczny. Lokalny panel diagnostyczny monitora (1b.7, 2026-11-14) zostaje obok niej jako
narzędzie serwisowe bez chmury; jego model statusu (`MonitorStatus`) i układ kart mogą posłużyć za punkt wyjścia dla 3a.

| # | Zakres | Wielkość |
|---|---|---|
| 3a | Szkielet, logowanie, **pulpit** (kafelki czujników: wilgotność, temperatura, bateria, ostatni odczyt, który monitor), stan monitorów, **historia** (wykresy 24 h/7 d/30 d). Może ruszyć po 2a. | M |
| 3b | **Alerty i raporty:** lista alertów (otwarte/zamknięte), potwierdzenie i wyciszenie (polecenie do monitorów), historia raportów z podglądem HTML. | M |
| 3c | **Konfiguracja:** odszyfrowanie hasłem w przeglądarce, edytor czujników, progów, harmonogramów, odbiorców i Gmaila; te same reguły walidacji co `AgentConfigLoader` (rozważyć wspólny schemat i testy kontraktowe), zapis z kontrolą wersji i obsługą konfliktu. Wymaga 2b. | L |
| 3d | **Parowanie zdalne:** UI tworzy „żądanie parowania”, wybrany monitor w zasięgu uruchamia `pair` (skan rusza pierwszy), UI mówi „włóż baterie teraz”, klucz trafia od razu do zaszyfrowanej konfiguracji, nigdy jawnie do chmury. Wymaga kolejki poleceń z 2a i krótkiego odpytywania w trybie parowania (okno czujnika ok. 20 s). | M |
| 3e | Widok mobilny, motyw jasny/ciemny, polski interfejs. | S |

**Kryterium:** cały scenariusz z przeglądarki, bez dotykania plików: dodać czujnik (parowanie zdalne), ustawić próg, zobaczyć
alert, zmienić odbiorców.

---

## Faza 4 – wdrożenie i utwardzanie

- **Aktualizacje:** skrypt/mechanizm dla Pi (paczka z weryfikacją skrótu) i dla Windows; wersjonowanie protokołu monitor–chmura
  (stare monitory nie mogą zepsuć nowych danych); migracje schematu SQLite (jest `user_version`).
- **Obserwowalność:** heartbeat, alert „monitor milczy”, liczniki (odczyty, błędy BLE, czasy połączeń); sygnał (RSSI) jest od
  2026-11-14 w dzienniku przy każdym odczycie, błędzie i „nie znaleziono” (ARCHITEKTURA.md, „Zasięg Cisy”); zostaje zapis w
  bazie (kolumna w `readings`), trend w raporcie i alert „sygnał słaby” przy poziomie bliskim granicy (ok. −90 dBm).
- **Bezpieczeństwo:** przegląd modelu zagrożeń (wyciek tokenu, kradzież urządzenia, podmiana koperty), rotacja tokenów,
  limity zapytań, kopia zapasowa i odtwarzanie, retencja i agregacja (np. odczyty surowe 90 dni, potem średnie godzinowe).
- **CI/CD:** build i testy na każdy push, publikacja paczek win-x64 i linux-arm64; testy sprzętowe jako ręczna lista kontrolna.
- **Runbook:** nowy monitor, nowy czujnik, reset czujnika, utrata hasła, utrata tokenu, przeniesienie na inny komputer.
- **Wspólna biblioteka poczty:** `GmailSender` istnieje dziś w dwóch projektach (tu i w LibrusNotifier); wyciągnąć do
  wspólnego pakietu NuGet albo świadomie zostawić kopię.

---

## Faza 5 – rozszerzenia (opcjonalne, w dowolnej kolejności po 1b)

- **Mądrzejsze podlewanie:** tempo wysychania (regresja z historii), prognoza „podlej za ~2 dni”, profile roślin i sezonowość,
  cisza nocna, poranny digest zamiast pojedynczych maili.
- **Inne kanały:** Telegram/ntfy/Web Push, webhooki, Home Assistant (MQTT).
- **Inne czujniki:** profile dla kolejnych produktów Tuya BLE (profil `ojzlzzsw` jest w kodzie, ale niesprawdzony na sprzęcie);
  Zigbee jako alternatywa, gdy zasięg BLE nie wystarcza.
- **Zasięg:** własne bramki ESP32 BLE→Wi-Fi (klucze Tuya wymagają własnej sesji, więc zwykły „Bluetooth proxy” nie wystarczy)
  albo po prostu więcej monitorów na Raspberry Pi.

---

## Decyzje do podjęcia

| Decyzja | Kiedy | Uwagi |
|---|---|---|
| Dostawca chmury (Supabase / Azure / inny) | start 2a | Sprawdzić aktualne limity i region UE; wymagane atomowe operacje. |
| Technologia UI (Blazor WASM / TypeScript) | start 3a | Blazor = jeden język, ale WebCrypto i tak przez JS. |
| Dostawca logowania do UI | start 3a | Lista dozwolonych kont (jeden właściciel lub kilku). |
| Polityka offline monitora | 2c | Wysyłać alerty bez chmury (duplikaty) czy czekać. |
| Odzyskiwanie hasła do konfiguracji | 2b | Bez hasła konfiguracji nie da się odszyfrować; klucze czujników odtworzy tylko reset i ponowne parowanie. |
| Licencja i widoczność repozytorium | 1b | LibrusNotifier jest na GPL-3.0; kod protokołu Tuya wywodzi się z projektów MIT. |
| Usługa czy zadanie Harmonogramu na Windows | 1b, zadanie 3 | Zależy od wyniku testu BLE w sesji 0. |

## Główne ryzyka

- **Shiny/BlueZ na Pi** – dopóki nie sprawdzone, plan zapasowy to SimpleSharpBLE albo Linux.Bluetooth (zmiana tylko za `IGattLink`).
- **Zasięg BLE i zużycie baterii** – liczba monitorów zależy od rozkładu czujników; każdy odczyt to ok. 11–14 s połączenia.
- **Okno parowania ok. 20 s** – zdalne parowanie (3d) musi być bardzo szybkie i dobrze opisane użytkownikowi.
- **Utrata kluczy czujników** – do 2b istnieją tylko w plikach `*.credentials.json`; utrata oznacza reset czujnika.
- **Dryf wskazań wilgotności** – bez kalibracji progi nie są porównywalne między doniczkami.

## Sugerowany następny krok

Zadania **1b.1 (mail), 1b.2 (Pi) i 1b.3 (Windows w tle)** – to one zamieniają projekt w używany produkt, a jednocześnie
rozstrzygają największe niewiadome. Pomiar baterii (1b.4) uruchomić od razu w tle, bo potrzebuje kilku dni. Wybór chmury
(2a.1) można zacząć równolegle, ponieważ jest to głównie research, a nie kod.
