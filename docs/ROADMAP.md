# Glebosaurus – plan dalszych prac

Stan na 2026-11-13: fazy 0 i 1 (spike sprzętowy oraz lokalny monitor) są zrobione i sprawdzone na żywo na Windows.
Poniżej propozycja kolejnych faz. To plan do akceptacji i korygowania, nie zobowiązanie. **Faza 2 (chmura, wielu agentów,
Android) jest uzgodniona z użytkownikiem 2026-11-16** – decyzje i kolejność kroków niżej; szczegóły fazy 3 trzeba
doprecyzować tuż przed jej startem. Opisy projektowe: [ARCHITEKTURA.md](ARCHITEKTURA.md).

Wielkości: **S** – dzień lub mniej, **M** – kilka dni, **L** – tydzień lub więcej (przy pracy w wolnych chwilach).

## Przegląd

| Faza | Cel | Wielkość | Zależy od |
|---|---|---|---|
| 0, 1 | Spike sprzętowy, lokalny monitor (SQLite, alerty, raporty, parowanie) | ✔ zrobione | – |
| **1b** | Domknięcie lokalnego monitora: prawdziwy mail, praca w tle na Windows, baterie, kalibracja (Raspberry Pi – przeniesione do 2.3) | M | 1 |
| **2.1** | Chmura Supabase (darmowa) i pierwszy agent – bestia5: konfiguracja i sekrety w chmurze, odczyty, dzierżawy czujników, blokada nadawcy, kolejka maili | L | 1b (częściowo) |
| **2.2** | Agent na Androida (Xiaomi Mi 11 Lite 5G) – tylko zbieranie | L | 2.1 |
| **2.3** | Agent na Raspberry Pi 24/7 – główny nadawca maili | M | 2.1 |
| **3** | Interfejs webowy: pulpit, historia, alerty, raporty, konfiguracja, parowanie zdalne | L | 2.1 |
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
| 2 | **Raspberry Pi** – *przeniesione do fazy 2.3 (decyzja użytkownika 2026-11-16: kolejność bestia5 → Android → Pi; Pi pracuje 24/7 i będzie głównym nadawcą maili); zakres poniżej bez zmian.* `scan`, `read`, `run` na Pi 64-bit z BlueZ (od 2026-11-14 potrzebny ASP.NET Core Runtime 10 – panel WWW – albo paczka samodzielna): dopasowanie po MAC z identyfikatora BlueZ (dziś nieprzetestowane, jest zapasowe dopasowanie po UUID Tuya), uprawnienia D-Bus i grupa `bluetooth`, stabilność połączeń (Wi-Fi/BLE na jednym układzie), unit systemd z `Restart`. Ustalić minimalny wspierany sprzęt. Jeśli Shiny na Linuxie zawiedzie: zapasowo SimpleSharpBLE (licencja BUSL-1.1, tylko użytek niekomercyjny) albo bezpośrednio Linux.Bluetooth za interfejsem `IGattLink`. | M |
| 3 | **Windows w tle.** Zadanie Harmonogramu uruchamiane przy logowaniu, bez okna konsoli (launcher albo podsystem okienkowy), skrypt `deploy/install-windows.ps1` (rejestracja i wyrejestrowanie), zachowanie po uśpieniu/wybudzeniu i po utracie adaptera BLE. Sprawdzić, czy WinRT BLE faktycznie nie działa w usłudze (sesja 0) – jeśli działa, usługa jest prostsza. *Zrobione 2026-11-15 (ARCHITEKTURA.md, zasada 7): usługa `glebosaurus-service` (bez okna) z zadania Harmonogramu przy zalogowaniu, na koncie użytkownika, ze strażnikiem co godzinę; `install-windows.ps1` instaluje, przejmuje dane (`-ImportFrom`), aktualizuje i odinstalowuje; usługa sama aktualizuje się z wydań GitHuba przy starcie i codziennie o 03:30 (przez ten sam skrypt); jedna instancja monitora i czyste zatrzymanie (`stop`). Sprawdzone na żywo na katalogu próbnym z udawanym wydaniem. Zostaje: uśpienie/wybudzenie, utrata adaptera BLE, wylogowanie w trakcie odczytu, 7 dni pracy; sesja 0 – niepotrzebna po decyzji o koncie użytkownika.* | M |
| 4 | **Baterie i interwał.** Kilka dni pracy co 15 min na 2–3 czujnikach, wykres wskazania baterii w czasie, wybór domyślnego interwału (15/30/60 min). Rozważyć interwał adaptacyjny: częściej blisko progu podlania, rzadziej gdy gleba jest wilgotna i nocą. | S + czas |
| 5 | **Kalibracja wilgotności.** Pomiary: powietrze, woda, mokra i sucha ziemia doniczkowa; kalibracja per czujnik (punkty „sucho”/„mokro” albo offset i skala) w konfiguracji; sensowne progi domyślne i opis w README. Tani czujnik nie podaje procentów porównywalnych między glebami. | M |
| 6 | **Parowanie pozostałych czujników** (ok. 10; większość widać już w skanie jako sparowane ze Smart Life – każdy trzeba zresetować, co usuwa go z aplikacji, i sparować po jednym w oknie 20 s) i procedura kopii zapasowej kluczy (do kroku 2.1 pliki `*.credentials.json` to jedyne miejsce, gdzie one są; potem Supabase Vault). | S |
| 7 | **Operacje.** `status --json`, skrypt aktualizacji paczki na Pi (jak `aktualizuj-rpi.sh` w LibrusNotifier), przeładowanie konfiguracji bez restartu (opcjonalnie). *Zrobione 2026-11-13: codzienny log techniczny do administratora (`mail.adminRecipients`, załącznik z dziennikiem z poprzedniego dnia) z retencją dzienników (7 dni) i polecenie `send-log`; wersja Glebosaurusa w stopce maili. Zrobione 2026-11-14: nasłuch BLE na przemian z odczytem (czujnik poza zasięgiem nie opóźnia pozostałych) i okno skanu domyślnie 60 s (ARCHITEKTURA.md, zasada 8); także lokalny panel diagnostyczny WWW, podgląd stanu i odczyt czujników na żądanie, bez zmian konfiguracji (HTTPS z certyfikatem z podpisem własnym, tylko sieć lokalna, hasło przez `web-password`; patrz ARCHITEKTURA.md, zasada 11); 2026-11-14 sprawdzony na żywo z `run` na Windows (logowanie w przeglądarce); zostaje wejście z telefonu w sieci domowej i Pi.* | S |
| 7a | **Panel WWW – dalsze etapy (opcjonalnie).** Etap 2 – akcje: *zrobione 2026-11-14: „odczytaj teraz” dla czujnika i dla wszystkich (kolejka silnika, budzenie pętli, najwyżej raz na minutę na czujnik, nagłówek akcji przeciw CSRF; ARCHITEKTURA.md, zasada 11); tego samego dnia „wyślij raport teraz” z potwierdzeniem (prawdziwy mail do odbiorców raportów, kolejka silnika, najwyżej raz na minutę; harmonogram bez zmian).* Zrobione 2026-11-14: wykres wilgotności i temperatury (SVG, dwie osie) – na pulpicie ostatnie 3 doby przy każdym czujniku, a na nowej stronie czujnika pełna historia do przeglądania (zakresy, przesuwanie, powiększanie, pasek przeglądu) razem ze wszystkimi szczegółami czujnika (ARCHITEKTURA.md, zasada 11). Zostaje: „wyślij log teraz”, przeładowanie konfiguracji między cyklami, sprawdzenie wykresów na prawdziwym monitorze i na telefonie. Etap 3 – edycja konfiguracji bez sekretów: **odłożony decyzją użytkownika (2026-11-14)**, pełny edytor przewiduje faza 3c. | S–M |
| 7b | **Wykres w raporcie mailowym** (życzenie użytkownika z 2026-11-14). Ostatnie 3 doby przy każdym czujniku w raporcie. Gmail nie pokazuje SVG w treści (caniemail, 09.2026: obrazek SVG zamienia na PNG i potrzebuje adresu w internecie albo załącznika; `data:` nie działa), a grafiki rastrowej użytkownik nie chce – propozycja: wykres z samego HTML (tabela ok. 36 kolumn po 2 godz. w szerokości 480 px, słupki wilgotności w kolorach progów, temperatura jako znacznik w tej samej kolumnie na własnej skali; Gmail obsługuje wysokości bloków, pozycjonowania nie). Dane z `ReadingSeriesBuilder` (Core). Najpierw `mail-preview`, potem akceptacja układu na telefonie – wygląd maili jest zatwierdzony i zmienia się tylko za zgodą. *Prototyp 2026-11-14 (`MailChart`, ARCHITEKTURA.md, zasada o mailach): kolumny po 2 godz., w każdej niebieska kreska średniej wilgotności i pod nią blady pasek temperatury od najniższej do najwyższej (dwie skale), różowe tło poniżej progu, budżet 90 KB (2 godz. do ok. 9 czujników, przy 20 – 8 godz.), linijka `▁…█` w wersji tekstowej; widać go w `mail-preview` (`raport.html`, `raport-20.html`). 2026-11-14 o 18:50 `mail-test --samples` wysłał pierwszą wersję (kreski po 3 godz.); użytkownik obejrzał ją na telefonie („wygląda OK”) i poprosił o 2 godz. i temperaturę jako stonowany zakres – zrobione tego samego dnia. Tego samego dnia historia podpięta w `SnapshotBuilder` – wykres mają wszystkie raporty z monitora. Zostaje: obejrzenie poprawionej wersji na telefonie (pierwszy prawdziwy raport z wykresem) i ewentualnie decyzja co do wielu czujników (szersze kolumny / kilka maili).* | S–M |
| 8 | **Higiena repozytorium.** Decyzja o licencji, `git init` i zdalne repozytorium, skan sekretów przed pierwszym pushem, CI (build + testy na push, publikacja paczek jako artefaktów), analizatory/formatowanie. *Zrobione 2026-11-15: repozytorium na GitHubie (publiczne od wydania 0.1.0 – decyzja użytkownika; historia sprawdzona: bez kluczy i haseł), CI na push/PR (`ci.yml`), wydania z tagu `v*` (`release.yml`: testy, paczka win-x64, próba instalacji skryptem). Zostaje: licencja (bez niej kod jest publiczny, ale „wszelkie prawa zastrzeżone”), paczka linux-arm64 w wydaniu, analizatory.* | S |

**Kryterium ukończenia:** 7 dni ciągłej pracy na Windows bez ingerencji i bez zawieszeń (Pi – w 2.3); co najmniej jeden
prawdziwy alert i jeden raport w skrzynce; udokumentowana prognoza życia baterii; wszystkie czujniki sparowane, klucze
zarchiwizowane.

**Ryzyka:** Shiny/BlueZ na Pi (zadanie 2, teraz 2.3 – największe), BLE w tle na Windows (3), zużycie baterii czujników (4).

---

## Faza 2 – chmura i wiele agentów

Propozycja Claude z 2026-11-15, decyzje użytkownika 2026-11-16. Zastępuje wcześniejszy plan 2a–2c (chmura bez sekretów,
zaszyfrowana koperta konfiguracji, maile z „claimami” na monitorach). Projekt: ARCHITEKTURA.md, zasady 1–4, 10 i 12.

**Decyzje (2026-11-16):**
- **Supabase, darmowy plan** – „celem jest darmowość”. Tylko Postgres z funkcjami SQL (operacje atomowe) i Vault
  (Storage ostatecznie nieużywany – logi agentów są w tabeli, ARCHITEKTURA.md, zasada 13); bez Edge Functions i bez
  własnego serwera; region UE.
- **Konfiguracja i wszystkie sekrety w Supabase:** czujniki (nazwy, MAC, progi), harmonogramy, odbiorcy, role agentów,
  a w Vault klucze Tuya i hasło aplikacji Gmail. Na urządzeniu zostaje adres chmury, token agenta i ustawienia lokalnego
  panelu. Zaszyfrowana koperta (dawne 2b) odpada razem z problemem odzyskiwania hasła.
- **Wielu agentów zbiera, maile wysyła agent z blokadą „nadaję teraz”.** Kandydatów na nadawcę może być wielu (rola i
  priorytet w tabeli agentów), ale w danej chwili wysyła tylko jeden. Android nie wysyła maili.
- **Dzierżawa czujnika 30 min** – po nagłym zniknięciu agenta jego czujniki przejmuje inny po ok. 32 min.
- **Kolejność:** 2.1 – bestia5 rozmawia z Supabase, 2.2 – agent na Androida, 2.3 – Raspberry Pi 24/7 (główny nadawca).
- **Tryb samodzielny zostaje** (bez sekcji `cloud`, plik konfiguracji i własna poczta – jak dziś).

### 2.1 – Supabase i pierwszy agent (bestia5)

Dzierżawy czujników i blokada nadawcy powstają od razu, choć agent jest jeden (zawsze je dostaje) – po dojściu Androida
protokół się nie zmienia, a działanie przy kilku agentach sprawdza się w 2.2.

| # | Zadanie | Wielkość |
|---|---|---|
| 1 | **Przygotowanie (użytkownik – kont Claude nie zakłada):** konto Supabase z logowaniem dwuetapowym (TOTP, kody odzyskiwania w menedżerze haseł), organizacja na planie Free i projekt w regionie Central EU (Frankfurt) z wygenerowanym hasłem bazy; adres projektu, klucz publikowalny (`sb_publishable_…`), osobny klucz tajny (`sb_secret_…` – tylko import i migracje, nigdy na agentach) i hasło bazy w `local/cloud-admin.json` (poza repozytorium); konto healthchecks.io z jednym sprawdzeniem nadawcy (adres sygnału też w `local/cloud-admin.json`); drugi, próbny projekt Supabase `glebosaurus-test` do testów funkcji SQL (tak samo ustawiony, dane w `local/cloud-admin.test.json`). Konto Gmail zostaje: obecny nadawca to już konto tylko do takich zastosowań (użytkownik, 2026-11-16). Limity darmowego planu sprawdzone 2026-11-16 (ARCHITEKTURA.md, zasada 10). **Zrobione 2026-11-16:** projekt Supabase założony i sprawdzony z bestia5 (klucze, hasło bazy przez pooler sesji – ARCHITEKTURA.md, zasada 10); próbny `glebosaurus-test` ustawiony tak samo i sprawdzony tym samym skryptem (inne adres, klucze i hasło); konto healthchecks.io ze sprawdzeniem „Glebosaurus – nadawca” (Period 5 min, Grace 25 min), adres sygnału sprawdzony wpisem `/log` – stan „New” bez alarmów aż do pierwszego prawdziwego sygnału. | S |
| 2 | **Schemat i funkcje SQL** jako migracje w repozytorium (`supabase/migrations/`). Tabele: agenci (skrót tokenu, rola nadawcy, priorytet, sygnał życia), czujniki (konfiguracja), odczyty, słyszenia (jeden wiersz na agenta i czujnik), dzierżawy, alerty, kolejka maili, polecenia, ustawienia (odbiorcy, harmonogramy); Vault na klucze Tuya i hasło Gmaila; dzienne logi agentów (7 dni). Funkcje: `enroll`, `agent_sync`, `lease_claim`/`lease_release`, `mail_claim`/`mail_sent`/`mail_credentials`, `get_config`. RLS zamyka wszystkie tabele; agenci wołają tylko funkcje `security definer`. Testy funkcji: dwóch agentów, uśpienie z dzierżawą, utrata sieci, restart w trakcie wysyłki. **Zrobione 2026-11-16** i sprawdzone na `glebosaurus-test` (ARCHITEKTURA.md, zasada 13; dwie migracje, 53 testy): agenci i rejestracja, konfiguracja z Vault, odczyty, słyszenia, dzierżawy, blokada nadawcy, kolejka maili, sekrety poczty, wspólny stan alertów, polecenia i dzienne logi agentów (tabela z gzipem zamiast Storage). Prawdziwy projekt jest jeszcze bez schematu – dostanie go poleceniem migracji agenta (2.1.4), tylko na prośbę użytkownika. | M |
| 3 | **Core:** podział silnika na zbieranie odczytów i wysyłanie (alerty, raporty, log administratora); konfiguracja z chmury z ostatnią dobrą kopią lokalnie; magazyn z buforem SQLite (pełna lokalna kopia odczytów agenta, wysyłka wsadowa idempotentna po `(sensor_id, at)`, praca offline); zasady przydziału czujników i blokady nadawcy (priorytety, oddawanie, epoki) z testami na ręcznie sterowanym czasie; wspólny stan alertów i kolejka maili zamiast lokalnych `report_runs`/`last_notified_at`. Tryb samodzielny (`MonitorEngine`) zostaje bez zmian; tryb chmurowy powstaje obok z tych samych części (reguły alertów, harmonogram, szablony maili) – w kawałkach (ARCHITEKTURA.md, zasada 14): **(a)** protokół w Core (`ICloudApi`), klient HTTP, księga dzierżaw i zasady przydziału – **zrobione 2026-11-16** (testy na ręcznym czasie i klient przez Data API próbnego projektu); **(b)** bufor odczytów w SQLite (stan wysłania, schemat v2) i pętla synchronizacji (paczki, słyszenia, dzierżawy, wersja konfiguracji); **(c)** konfiguracja z chmury → `AgentConfig` z ostatnią dobrą kopią lokalnie; **(d)** zbieracz: odczyt tylko czujników z dzierżawą, nasłuch wszystkich, polecenia `read_now`/`release`/`pair`; **(e)** nadawca: alerty, raporty z odczytów wszystkich agentów, log administratora z logów wszystkich agentów, kolejka maili, strażnik. | L |
| 4 | **Agent:** `enroll`, `run` w trybie chmurowym (sekcja `cloud`; z sekcją `mail` naraz – błąd startu), `cloud import` (jednorazowo konfiguracja, klucze z `*.credentials.json`, hasło Gmaila i historia odczytów z `…\Glebosaurus\data` – bez wypisywania sekretów), `cloud backup` (codzienny zrzut bazy, bo darmowy plan nie robi kopii – ARCHITEKTURA.md, zasada 10). Sygnał nadawcy do healthchecks.io. | M |
| 5 | **Na żywo:** usługa na bestia5 przechodzi na tryb chmurowy przez wydanie i samoaktualizację (tag tylko na prośbę użytkownika); tydzień pracy. | S + czas |

**Kryterium:** bestia5 przez tydzień czyta czujniki, wysyła odczyty do Supabase i maile przez blokadę i kolejkę; godzinna
przerwa sieci kończy się dosłaniem bez strat; restarty nie dublują maili; unieważnienie tokenu odcina agenta. Maile wychodzą
tylko wtedy, gdy bestia5 działa – tak jak dziś.

### 2.2 – agent na Androida (Xiaomi Mi 11 Lite 5G)

Tylko zbieranie, bez roli nadawcy; telefon na Wi-Fi, stale na ładowarce. .NET for Android z tym samym `Glebosaurus.Tuya`,
zbieraniem z Core i Shiny.BluetoothLE – szczegóły i znane ograniczenia Androida/MIUI w ARCHITEKTURA.md, zasada 12.

| # | Zadanie | Wielkość |
|---|---|---|
| 1 | **Próba (spike):** Shiny na Mi 11 – skan z filtrem, odczyt jednego czujnika, noc przy zgaszonym ekranie na ładowarce. Największa niewiadoma fazy 2 – jak kiedyś spike na Windows. | S–M |
| 2 | **Agent:** usługa pierwszoplanowa ze stałym powiadomieniem, start po restarcie telefonu, rejestracja kodem QR, synchronizacja, bufor odczytów, ekran stanu i skanu z sygnałem (wybór miejsca na telefon), stan telefonu w sygnale życia (bateria, temperatura, ładowanie, Wi-Fi); instrukcja ustawień MIUI w README. | L |
| 3 | **Wydania:** APK budowany w Actions, podpisany jednym kluczem z sekretów repozytorium (kopia klucza – jego utrata oznacza instalację od nowa); aktualizacja przez powiadomienie o nowej wersji albo Obtainium. | S |
| 4 | **Praca obok bestia5 na tych samych czujnikach** – prawdziwa próba dzierżaw: przejęcie czujników po wyłączeniu bestia5, oddanie, gdy bestia5 słyszy czujnik lepiej, żadnych równoczesnych połączeń. | S + czas |

**Kryterium:** telefon tydzień pracuje bez dotykania; zero konfliktów z bestia5. Luka do 2.3: gdy bestia5 jest wyłączona,
Android zbiera odczyty, ale maile nie wychodzą (po powrocie bestia5 alerty są liczone na nowo, a raport nadrobiony w granicach
`catchUpHours`).

### 2.3 – Raspberry Pi 24/7

| # | Zadanie | Wielkość |
|---|---|---|
| 1 | **Pi jako agent:** zakres dawnego 1b.2 (BlueZ przez Shiny, D-Bus i grupa `bluetooth`, systemd z `Restart`, paczka linux-arm64 w wydaniu, aktualizacje na Pi). | M |
| 2 | **Główny nadawca:** rola nadawcy z priorytetem 1, bestia5 przechodzi na priorytet 2 (zapasowy); próba przejęcia blokady w obie strony. Strażnik i codzienny `cloud backup` przechodzą z bestia5 na Pi. Nadawca nie potrzebuje radia, więc Pi może wysyłać maile nawet wtedy, gdyby BLE na nim zawiodło. | S |

**Kryterium:** Pi wysyła maile 24/7; wyłączenie Pi przenosi wysyłkę na działającą bestia5 w ok. 7 min; brak nadawcy zgłasza
healthchecks.io.

---

## Faza 3 – interfejs webowy

Hipoteza (2026-11-16, po wyborze Supabase): statyczna strona (np. GitHub Pages) czytająca Supabase wprost przez RLS,
logowanie Supabase Auth tylko na konto właściciela, wykresy z `charts.js` lokalnego panelu, polecenia jako wiersze w tabeli
poleceń; konfiguracja jest jawna dla właściciela (bez odszyfrowywania), sekrety tylko do ustawienia, nigdy do odczytu. Lokalny panel diagnostyczny monitora (1b.7, 2026-11-14) zostaje obok niej jako
narzędzie serwisowe bez chmury; jego model statusu (`MonitorStatus`) i układ kart mogą posłużyć za punkt wyjścia dla 3a.

| # | Zakres | Wielkość |
|---|---|---|
| 3a | Szkielet, logowanie, **pulpit** (kafelki czujników: wilgotność, temperatura, bateria, ostatni odczyt, który agent), stan agentów (sygnał życia, przydział czujników, kto nadaje), **historia** (wykresy 24 h/7 d/30 d). Może ruszyć po 2.1. | M |
| 3b | **Alerty i raporty:** lista alertów (otwarte/zamknięte), potwierdzenie i wyciszenie (polecenie do monitorów), historia raportów z podglądem HTML. | M |
| 3c | **Konfiguracja:** edytor czujników, progów, harmonogramów, odbiorców i ról agentów (kto może nadawać, priorytet); sekrety (hasło Gmaila, klucze Tuya) tylko do ustawienia; te same reguły walidacji co `AgentConfigLoader` (rozważyć wspólny schemat i testy kontraktowe), zapis z kontrolą wersji i obsługą konfliktu. Wymaga 2.1. | M–L |
| 3d | **Parowanie zdalne:** UI tworzy „żądanie parowania”, wybrany agent w zasięgu uruchamia `pair` (skan rusza pierwszy), UI mówi „włóż baterie teraz”, klucz trafia od razu do Vault. Wymaga tabeli poleceń z 2.1 i krótkiego odpytywania w trybie parowania (okno czujnika ok. 20 s). | M |
| 3e | Widok mobilny, motyw jasny/ciemny, polski interfejs. | S |

**Kryterium:** cały scenariusz z przeglądarki, bez dotykania plików: dodać czujnik (parowanie zdalne), ustawić próg, zobaczyć
alert, zmienić odbiorców.

---

## Faza 4 – wdrożenie i utwardzanie

- **Aktualizacje:** skrypt/mechanizm dla Pi (paczka z weryfikacją skrótu); Windows – zrobione 2026-11-15 (samoaktualizacja z
  wydań GitHuba, ARCHITEKTURA.md zasada 7; zostaje podpis wydań, np. Sigstore/minisign, żeby przejęte konto nie wystarczyło);
  wersjonowanie protokołu agent–chmura (od 2.1 jest `v1`)
  (stare monitory nie mogą zepsuć nowych danych); migracje schematu SQLite (jest `user_version`).
- **Obserwowalność:** heartbeat, alert „monitor milczy”, liczniki (odczyty, błędy BLE, czasy połączeń); sygnał (RSSI) jest od
  2026-11-14 w dzienniku przy każdym odczycie, błędzie i „nie znaleziono” (ARCHITEKTURA.md, „Zasięg Cisy”); zostaje zapis w
  bazie (kolumna w `readings`), trend w raporcie i alert „sygnał słaby” przy poziomie bliskim granicy (ok. −90 dBm).
- **Bezpieczeństwo:** przegląd modelu zagrożeń (wyciek tokenu – zwłaszcza nadawcy, który może pobrać hasło Gmaila;
  kradzież urządzenia; przejęcie konta Supabase), rotacja tokenów, limity zapytań, kopia zapasowa i próba odtworzenia z
  `cloud backup`, retencja i agregacja (np. odczyty surowe 90 dni, potem średnie godzinowe – limit 500 MB darmowego planu).
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
| ~~Dostawca chmury (Supabase / Azure / inny)~~ | start 2 | Rozstrzygnięte 2026-11-16 (użytkownik): Supabase, darmowy plan – „celem jest darmowość”. |
| ~~Gdzie konfiguracja i sekrety~~ | start 2 | Rozstrzygnięte 2026-11-16 (użytkownik): czujniki z nazwami i progami, klucze Tuya i hasło Gmaila w Supabase (Vault); koperta 2b odpada. |
| ~~Kto wysyła maile~~ | start 2 | Rozstrzygnięte 2026-11-16 (użytkownik): agent z blokadą „nadaję teraz”; kandydatów może być wielu (Pi – główny, bestia5 – zapasowy); Android nie wysyła. |
| ~~Kolejność kroków fazy 2~~ | start 2 | Rozstrzygnięte 2026-11-16 (użytkownik): bestia5 z chmurą → Android → Raspberry Pi. Czas dzierżawy czujnika: 30 min. |
| ~~Lokalne testy funkcji SQL~~ | 2.1 | Rozstrzygnięte 2026-11-16 (użytkownik): drugi, próbny projekt Supabase `glebosaurus-test` (dane w `local/cloud-admin.test.json`), bez Dockera. |
| Technologia UI (strona statyczna: TypeScript / Blazor WASM) | start 3a | Strona czyta Supabase wprost; WebCrypto już niepotrzebne. |
| Dostawca logowania do UI | start 3a | Hipoteza: Supabase Auth, tylko konto właściciela. |
| ~~Polityka offline monitora~~ | 2 | Rozstrzygnięte 2026-11-16: bez blokady nadawcy (a więc bez Supabase) żaden agent nie wysyła maili; brak nadawcy zgłasza healthchecks.io. |
| ~~Odzyskiwanie hasła do konfiguracji~~ | – | Nieaktualne od 2026-11-16 – bez zaszyfrowanej koperty. |
| Licencja repozytorium | 1b | LibrusNotifier jest na GPL-3.0; kod protokołu Tuya wywodzi się z projektów MIT. Widoczność rozstrzygnięta 2026-11-15: publiczne od wydania 0.1.0 (instalacja i aktualizacje bez tokenu). |
| ~~Usługa czy zadanie Harmonogramu na Windows~~ | 1b, zadanie 3 | Rozstrzygnięte 2026-11-15 (użytkownik): zadanie Harmonogramu przy zalogowaniu, na koncie użytkownika, bez administratora. |

## Główne ryzyka

- **Shiny/BlueZ na Pi** – dopóki nie sprawdzone, plan zapasowy to SimpleSharpBLE albo Linux.Bluetooth (zmiana tylko za `IGattLink`).
- **Zasięg BLE i zużycie baterii** – liczba monitorów zależy od rozkładu czujników; każdy odczyt to ok. 11–14 s połączenia.
- **Okno parowania ok. 20 s** – zdalne parowanie (3d) musi być bardzo szybkie i dobrze opisane użytkownikowi.
- **Utrata kluczy czujników** – do 2.1 istnieją tylko w plikach `*.credentials.json`; utrata oznacza reset czujnika. Potem
  są w Vault, a kopię (zrzut bazy + lokalna kopia kluczy) robi `cloud backup`.
- **Android/MIUI** (2.2) – zabijanie aplikacji w tle, ograniczenia skanu BLE, akumulator telefonu stale na ładowarce.
- **Darmowy Supabase** – 500 MB (potem tylko odczyt), uśpienie po 7 dniach bez ruchu, brak kopii zapasowych; konto
  Supabase jest kluczem do wszystkich sekretów (logowanie dwuetapowe).
- **Dryf wskazań wilgotności** – bez kalibracji progi nie są porównywalne między doniczkami.

## Sugerowany następny krok

Faza **2.1** (decyzja użytkownika 2026-11-16). Konta i oba projekty są gotowe (2.1.1), schemat i funkcje SQL też (2.1.2,
oba 2026-11-16); w Core (2.1.3) zrobiony kawałek (a) – protokół, klient HTTP i zasady dzierżaw; dalej (b) bufor odczytów i
pętla synchronizacji. Z fazy 1b
w tle: pomiar baterii (1b.4), kalibracja (1b.5) i parowanie pozostałych czujników (1b.6 – po 2.1 klucze trafią od razu do
Vault).
