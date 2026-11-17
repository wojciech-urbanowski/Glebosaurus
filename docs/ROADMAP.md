# Glebosaurus – plan dalszych prac

Stan na 2026-11-17. Ten plik mówi, **co jest zrobione, co zostało i w jakiej kolejności**. Jak to działa i kiedy co
sprawdzono na sprzęcie – [ARCHITEKTURA.md](ARCHITEKTURA.md) („Stan wdrożenia” i zasady 1–15); użycie – [README](../README.md).
Faza 2 (chmura, wielu agentów, Android) jest uzgodniona z użytkownikiem 2026-11-16; fazy 3–5 to propozycje do
doprecyzowania tuż przed startem.

Wielkości: **S** – dzień lub mniej, **M** – kilka dni, **L** – tydzień lub więcej (przy pracy w wolnych chwilach).
Stan: ✔ zrobione · ◐ w toku albo częściowo · ○ do zrobienia.

## Stan w skrócie

### Działa na co dzień (wersja 0.1.1, tryb samodzielny)

- **Usługa na bestia5** (Windows 11, od 2026-11-15, `%LOCALAPPDATA%\Glebosaurus`): 5 czujników (Cyprysek, Cisy, Zioła,
  Wiszący wejście, Laurowiśnia wejście) co 15 min do SQLite, alerty i raporty o 07:00 i 19:00 mailem przez Gmail, log
  administratora za poprzedni dzień, panel WWW w sieci lokalnej; start przy zalogowaniu, samoaktualizacja z wydań GitHuba
  (pierwsza na żywo 2026-11-16: 0.1.0 → 0.1.1).
- **Protokół Tuya BLE i lokalne parowanie** bez konta Tuya (`pair`), zaległe pomiary z pamięci czujnika, nasłuch na
  przemian z odczytem.
- **Maile pod telefon** (wygląd zatwierdzony 2026-11-13) z wykresem 3 dób w raporcie (2026-11-14). Doszły już prawdziwe
  maile wszystkich rodzajów: próbne, alert, log administratora, raport z harmonogramu (pierwszy 2026-11-16 o 19:00).
- **Repozytorium** publiczne na GitHubie, CI na każdy push, wydania z tagu `v*`.

### Gotowe w kodzie i testach, jeszcze nie w użyciu – tryb chmurowy (faza 2.1)

- Supabase: prawdziwy i próbny projekt, healthchecks.io (2.1.1); schemat i funkcje SQL zielone na próbnym projekcie (2.1.2).
- Core: protokół, dzierżawy, synchronizacja z buforem, konfiguracja z chmury, zbieracz, nadawca (2.1.3 – całość).
- Agent: sekcja `cloud`, `enroll`, `run` w trybie chmurowym (w testach), `cloud migrate`/`import`/`agent` (na próbnym
  projekcie) – większość 2.1.4.
- **Prawdziwy projekt Supabase jest pusty** (bez schematu), a usługa na bestia5 pracuje w trybie samodzielnym. Wydanie
  0.1.1 trybu chmurowego jeszcze nie zawiera.

### Następne kroki (w tej kolejności)

1. **Dokończyć 2.1.4:** `cloud backup` (codzienny zrzut bazy – darmowy plan nie robi kopii) i panel WWW w trybie
   chmurowym (dziś wyłączony z ostrzeżeniem w dzienniku).
2. **Przejście bestia5 na chmurę (2.1.5) – tylko na prośbę i z udziałem użytkownika:** `cloud migrate`, `cloud import` i
   `cloud agent` na prawdziwym projekcie, wydanie z tagiem, sekcja `cloud` i `enroll` w danych usługi, potem tydzień pracy
   (kryterium 2.1). Przy okazji pierwszy prawdziwy mail w trybie chmurowym i pierwszy sygnał do healthchecks.io.
3. **2.2 – Android:** najpierw próba (spike) na Mi 11 – największa niewiadoma fazy 2; potem agent. Czujniki przy wejściu
   bestia5 prawie nie słyszy (niżej), więc drugi zbieracz ma konkretny cel.
4. **2.3 – Raspberry Pi 24/7** jako główny nadawca.
5. **Faza 3** – interfejs WWW w chmurze (może ruszyć zaraz po 2.1).

**W tle, niezależnie od kolejności:** pomiar baterii (1b.4 – dane zbiera usługa od 2026-11-15), kalibracja wilgotności
(1b.5), parowanie pozostałych czujników (1b.6 – najlepiej po 2.1.5, wtedy klucze trafią od razu do Vault), licencja (1b.8).

### Do zbadania – z dzienników usługi na bestia5 (2026-11-15…17)

- ✔ **Fałszywe alerty „brak odczytów” po wyłączeniu komputera** – poprawione w kodzie 2026-11-17, na bestia5 dopiero z
  następnym wydaniem. 2026-11-17 po ok. 18 h przerwy monitor ok. 2 min po starcie, po jednym oknie nasłuchu, otworzył
  alerty dla trzech czujników (mail z 5 alertami), bo cisza liczy się od ostatniego odczytu w bazie, także za czas
  przestoju monitora. Teraz po starcie i po uśpieniu jest 30 min okresu ochronnego – także u nadawcy w trybie chmurowym
  (ARCHITEKTURA.md, zasada 9).
- **Czujniki przy wejściu prawie poza zasięgiem bestia5.** Laurowiśnia wejście: 1 udany odczyt od 2026-11-14, Wiszący
  wejście: 8; 2026-11-16 (ok. 7 h pracy) – 0 i 2. Pozostałe czujniki też często „nie znaleziono w reklamach” (Cyprysek
  10 udanych na ok. 26 cykli), ale luki uzupełniają zaległe pomiary z czujnika. Do wyboru: przestawienie, drugi agent
  (2.2/2.3), pomiar sygnału (ARCHITEKTURA.md, „Zasięg Cisy”).
- **„Czujnik nie potwierdził włączenia powiadomień”** – 7 razy w 3 dni (Cisy, Zioła), następny cykl zwykle udany. Niski
  priorytet; obserwować, czy nie rośnie.

### Czeka na użytkownika

- Obejrzeć na telefonie: pierwszy prawdziwy raport z wykresem (2026-11-16, 19:00), log administratora, nowy mail „agent
  milczy” (w `mail-preview`).
- Zgoda na zmiany w prawdziwym projekcie Supabase i przejście usługi na chmurę (krok 2 wyżej), w tym tag wydania.
- Kiedy wydanie z poprawką alertu „brak odczytów” (wersja i tag – tylko na prośbę): osobno teraz albo razem z 2.1.5.
- Decyzje: licencja repozytorium, raport przy wielu czujnikach (1b.7b).
- Reguła Zapory, jeśli panel ma być dostępny z telefonu.
- Fizycznie: parowanie kolejnych czujników (reset przyciskiem, baterie w oknie 20 s), pomiary do kalibracji.

### Niesprawdzone na żywo

| Obszar | Co zostało |
|---|---|
| Poczta | dzienny limit Gmaila, zachowanie przy błędnym haśle |
| Usługa Windows | strażnik co godzinę; aktualizacja o 03:30 (komputer był wtedy dotąd wyłączony); uśpienie i wybudzenie; wylogowanie w trakcie odczytu; utrata adaptera BLE; 7 dni ciągłej pracy; usuwanie dzienników starszych niż 7 dni |
| Panel WWW | wejście z telefonu w sieci domowej; wykresy na prawdziwym monitorze |
| Tryb chmurowy | wszystko na prawdziwym projekcie, prawdziwy `run`, pełny nasłuch w radiu Shiny |
| Raspberry Pi | wszystko (paczka tylko zbudowana) |
| Czujnik | kalibracja wilgotności; profil `ojzlzzsw`; czy niesparowany czujnik milczy po oknie 20 s |

---

## Przegląd faz

| Faza | Cel | Wielkość | Stan |
|---|---|---|---|
| 0, 1 | Spike sprzętowy, lokalny monitor (SQLite, alerty, raporty, parowanie) | – | ✔ 2026-11-13 |
| **1b** | Domknięcie lokalnego monitora: prawdziwy mail, praca w tle na Windows, panel, baterie, kalibracja, parowanie | M | ◐ mail, usługa, panel i repozytorium ✔; baterie, kalibracja, parowanie ○ |
| **2.1** | Chmura Supabase (darmowa) i pierwszy agent – bestia5 | L | ◐ 2.1.1–2.1.3 ✔, 2.1.4 w toku, 2.1.5 ○ |
| **2.2** | Agent na Androida (Xiaomi Mi 11 Lite 5G) – tylko zbieranie | L | ○ |
| **2.3** | Agent na Raspberry Pi 24/7 – główny nadawca maili | M | ○ |
| **3** | Interfejs webowy w chmurze: pulpit, historia, alerty, raporty, konfiguracja, parowanie zdalne | L | ○ (lokalny panel diagnostyczny jest z 1b) |
| **4** | Wdrożenie i utwardzanie: aktualizacje, monitoring, bezpieczeństwo, CI, runbook | M | ◐ aktualizacje Windows i CI ✔ |
| **5** | Rozszerzenia (opcjonalne): mądrzejsze podlewanie, inne kanały, inne czujniki, zasięg | – | ○ |

Kolejność ma zmniejszać ryzyko i dawać użyteczny produkt jak najwcześniej: **już teraz aplikacja jest w pełni użyteczna
bez chmury** (jeden lub kilka niezależnych monitorów, każdy ze swoim plikiem konfiguracji). Chmura i WWW to wygoda i
koordynacja, nie warunek działania. Zadania wewnątrz faz są w większości niezależne, więc nadają się do równoległej pracy
w osobnych sesjach (każda zmienia inne pliki); wyjątki są zaznaczone.

---

## Faza 1b – domknięcie lokalnego monitora

Cel: monitor pracuje w tle bez nadzoru i wysyła prawdziwe maile. Szczegóły zrobionych zadań i daty sprawdzeń:
ARCHITEKTURA.md („Stan wdrożenia”, zasady 7–11).

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Prawdziwy mail.** Konto Gmail z hasłem aplikacji, szablony pod telefon, konfigurowalni odbiorcy alertów i raportów, `mail-preview`, `mail-test --samples`. | ✔ 2026-11-13…16: wygląd zatwierdzony na telefonie, prawdziwe maile próbne, alert (11-14) i raport z harmonogramu (11-16). ○ Dzienny limit Gmaila, zachowanie przy błędnym haśle. | S |
| 2 | **Raspberry Pi** | → przeniesione do 2.3.1 (decyzja użytkownika 2026-11-16). | – |
| 3 | **Windows w tle.** Zadanie Harmonogramu przy zalogowaniu, bez okna, skrypt instalacji, aktualizacje, zachowanie po uśpieniu i utracie adaptera BLE. | ✔ 2026-11-15 (zasada 7): usługa `glebosaurus-service`, `install-windows.ps1`, samoaktualizacja z GitHuba, jedna instancja, `stop`; na żywo na bestia5. ○ Uśpienie i wybudzenie, utrata adaptera BLE, wylogowanie w trakcie odczytu, 7 dni pracy. | M |
| 4 | **Baterie i interwał.** Kilka dni pracy co 15 min na kilku czujnikach, wykres wskazania baterii w czasie, wybór domyślnego interwału (15/30/60 min). Rozważyć interwał adaptacyjny: częściej blisko progu podlania, rzadziej gdy gleba jest wilgotna i nocą. | ◐ dane zbiera usługa od 2026-11-15; analiza ○ | S + czas |
| 5 | **Kalibracja wilgotności.** Pomiary: powietrze, woda, mokra i sucha ziemia doniczkowa; kalibracja per czujnik (punkty „sucho”/„mokro” albo offset i skala) w konfiguracji; sensowne progi domyślne i opis w README. Tani czujnik nie podaje procentów porównywalnych między glebami. | ○ | M |
| 6 | **Parowanie pozostałych czujników** (większość widać w skanie jako sparowane ze Smart Life – każdy trzeba zresetować przyciskiem, co usuwa go z aplikacji, i sparować po jednym w oknie 20 s) i procedura kopii zapasowej kluczy (do 2.1.5 pliki `*.credentials.json` to jedyne miejsce, gdzie one są; potem Supabase Vault). | ◐ sparowanych 5; reszta i procedura kopii ○ | S |
| 7 | **Operacje.** Log administratora, nasłuch BLE na przemian z odczytem, lokalny panel diagnostyczny, `status --json`, przeładowanie konfiguracji bez restartu (opcjonalnie). | ◐ ✔ 2026-11-13…14: log administratora z retencją i `send-log`, wersja w stopce maili, nasłuch na przemian z odczytem i okno skanu 60 s (zasada 8), panel WWW (zasada 11; na żywo z logowaniem). ○ `status --json`, przeładowanie konfiguracji; skrypt aktualizacji na Pi → 2.3.1. | S |
| 7a | **Panel WWW – dalsze etapy (opcjonalnie).** | ◐ ✔ 2026-11-14: odczyt i raport na żądanie, wykresy (pulpit – 3 doby, strona czujnika – cała historia). ○ „Wyślij log teraz”, przeładowanie konfiguracji między cyklami. Edycja konfiguracji – **odłożona decyzją użytkownika (2026-11-14)**, pełny edytor w 3c. | S–M |
| 7b | **Wykres w raporcie mailowym** (życzenie użytkownika z 2026-11-14): ostatnie 3 doby przy każdym czujniku, sam HTML (Gmail nie pokazuje SVG, grafiki rastrowej użytkownik nie chce). | ✔ 2026-11-14 (`MailChart`; kolumny po 2 godz., temperatura jako blady zakres – po uwagach użytkownika z telefonu). ○ Obejrzenie prawdziwego raportu na telefonie; decyzja przy wielu czujnikach (budżet 90 KB: 2 godz. do ok. 9 czujników, przy 20 – 8 godz.; szersze kolumny albo kilka maili). | S–M |
| 8 | **Higiena repozytorium.** | ◐ ✔ 2026-11-15: GitHub (publiczne od 0.1.0, historia bez sekretów), CI (`ci.yml`), wydania z tagu (`release.yml`). ○ Licencja (bez niej kod jest publiczny, ale „wszelkie prawa zastrzeżone”), analizatory; paczka linux-arm64 w wydaniu → 2.3.1. | S |

**Kryterium ukończenia:** 7 dni ciągłej pracy na Windows bez ingerencji i bez zawieszeń (Pi – w 2.3); co najmniej jeden
prawdziwy alert i jeden raport w skrzynce (✔); udokumentowana prognoza życia baterii; wszystkie czujniki sparowane,
klucze zarchiwizowane.

**Ryzyka:** zasięg BLE (czujniki przy wejściu – „Do zbadania” wyżej), zużycie baterii czujników (4).

---

## Faza 2 – chmura i wiele agentów

Propozycja Claude z 2026-11-15, decyzje użytkownika 2026-11-16. Zastępuje wcześniejszy plan 2a–2c (chmura bez sekretów,
zaszyfrowana koperta konfiguracji, maile z „claimami” na monitorach). Projekt: ARCHITEKTURA.md, zasady 1–4, 10 i 12–15.

**Decyzje (2026-11-16):**
- **Supabase, darmowy plan** – „celem jest darmowość”. Tylko Postgres z funkcjami SQL (operacje atomowe) i Vault
  (Storage nieużywany – logi agentów są w tabeli, zasada 13); bez Edge Functions i bez własnego serwera; region UE.
- **Konfiguracja i wszystkie sekrety w Supabase:** czujniki (nazwy, MAC, progi), harmonogramy, odbiorcy, role agentów,
  a w Vault klucze Tuya i hasło aplikacji Gmail. Na urządzeniu zostaje adres chmury, token agenta i ustawienia lokalnego
  panelu.
- **Wielu agentów zbiera, maile wysyła agent z blokadą „nadaję teraz”.** Kandydatów na nadawcę może być wielu (rola i
  priorytet w tabeli agentów), ale w danej chwili wysyła tylko jeden. Android nie wysyła maili. Log administratora –
  osobny mail od każdego agenta.
- **Dzierżawa czujnika 30 min** – po nagłym zniknięciu agenta jego czujniki przejmuje inny po ok. 32 min.
- **Kolejność:** 2.1 – bestia5 rozmawia z Supabase, 2.2 – agent na Androida, 2.3 – Raspberry Pi 24/7 (główny nadawca).
- **Tryb samodzielny zostaje** (bez sekcji `cloud`, plik konfiguracji i własna poczta – jak dziś).

### 2.1 – Supabase i pierwszy agent (bestia5)

Dzierżawy czujników i blokada nadawcy powstają od razu, choć agent jest jeden (zawsze je dostaje) – po dojściu Androida
protokół się nie zmienia, a działanie przy kilku agentach sprawdza się w 2.2.

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Przygotowanie (użytkownik – kont Claude nie zakłada):** konto Supabase z logowaniem dwuetapowym, projekt na planie Free we Frankfurcie, klucz publikowalny, osobny klucz tajny (tylko import i migracje, nigdy na agentach) i hasło bazy w `local/cloud-admin.json`; próbny projekt `glebosaurus-test` tak samo (`local/cloud-admin.test.json`); healthchecks.io ze sprawdzeniem nadawcy. Konto Gmail zostaje (już jest osobne). | ✔ 2026-11-16 (zasady 2 i 10): oba projekty sprawdzone z bestia5; sprawdzenie „Glebosaurus – nadawca” (Period 5 min, Grace 25 min) w stanie „New” do pierwszego prawdziwego sygnału. | S |
| 2 | **Schemat i funkcje SQL** jako migracje w `supabase/migrations/`: agenci, czujniki, odczyty, słyszenia, dzierżawy, blokada nadawcy, kolejka maili, sekrety poczty (Vault), wspólny stan alertów, polecenia, dzienne logi agentów; RLS na wszystkim, agenci wołają tylko funkcje `public.*`. | ✔ 2026-11-16 (zasada 13): dwie migracje, 53 testy na `glebosaurus-test`. Prawdziwy projekt bez schematu – dostanie go `cloud migrate` w 2.1.5. | M |
| 3 | **Core:** tryb chmurowy obok niezmienionego `MonitorEngine`, w kawałkach: (a) protokół, klient HTTP, księga dzierżaw i zasady przydziału; (b) bufor odczytów w SQLite i pętla synchronizacji; (c) konfiguracja z chmury z chronioną ostatnią dobrą kopią; (d) zbieracz; (e) nadawca (alerty, raporty, log administratora, „agent milczy”, kolejka maili, strażnik). | ✔ 2026-11-16 (zasada 14), testy na ręcznym czasie i na próbnym projekcie. Niesprawdzone: pełny nasłuch w radiu Shiny na sprzęcie, prawdziwy mail w trybie chmurowym. | L |
| 4 | **Agent:** sekcja `cloud` (z `mail` albo `sensors` naraz – błąd startu), `enroll`, `run` w trybie chmurowym, narzędzia właściciela `cloud migrate`/`import`/`agent`, `cloud backup` (darmowy plan nie robi kopii – zasada 10), panel WWW w trybie chmurowym. | ◐ ✔ 2026-11-16 (zasada 15): sekcja `cloud`, chroniony token, `enroll` (CLI na próbnym projekcie), `CloudRuntime` i `run` (testy z atrapą radia), `cloud migrate`/`import`/`agent` (próbny projekt, także CLI), wzór `examples/glebosaurus.cloud.config.json`. ○ `cloud backup`, panel w trybie chmurowym. | M |
| 5 | **Na żywo:** prawdziwy projekt (`cloud migrate`, `cloud import`, `cloud agent`), usługa na bestia5 przechodzi na tryb chmurowy przez wydanie i samoaktualizację (tag tylko na prośbę użytkownika), sekcja `cloud` i `enroll` w danych usługi; tydzień pracy. Historii odczytów `cloud import` nie przenosi – wysyła ją agent ze swojej bazy, więc najpierw import, potem agent. | ○ – na prośbę i z udziałem użytkownika | S + czas |

**Kryterium:** bestia5 przez tydzień czyta czujniki, wysyła odczyty do Supabase i maile przez blokadę i kolejkę; godzinna
przerwa sieci kończy się dosłaniem bez strat; restarty nie dublują maili; unieważnienie tokenu odcina agenta. Maile wychodzą
tylko wtedy, gdy bestia5 działa – tak jak dziś.

### 2.2 – agent na Androida (Xiaomi Mi 11 Lite 5G)

Tylko zbieranie, bez roli nadawcy; telefon na Wi-Fi, stale na ładowarce. .NET for Android z tym samym `Glebosaurus.Tuya`,
zbieraniem z Core i Shiny.BluetoothLE – szczegóły i znane ograniczenia Androida/MIUI w ARCHITEKTURA.md, zasada 12.

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Próba (spike):** Shiny na Mi 11 – skan z filtrem, odczyt jednego czujnika, noc przy zgaszonym ekranie na ładowarce. Największa niewiadoma fazy 2 – jak kiedyś spike na Windows. | ○ | S–M |
| 2 | **Agent:** usługa pierwszoplanowa ze stałym powiadomieniem, start po restarcie telefonu, rejestracja kodem QR, synchronizacja, bufor odczytów, ekran stanu i skanu z sygnałem (wybór miejsca na telefon), stan telefonu w sygnale życia (bateria, temperatura, ładowanie, Wi-Fi); instrukcja ustawień MIUI w README. | ○ | L |
| 3 | **Wydania:** APK budowany w Actions, podpisany jednym kluczem z sekretów repozytorium (kopia klucza – jego utrata oznacza instalację od nowa); aktualizacja przez powiadomienie o nowej wersji albo Obtainium. | ○ | S |
| 4 | **Praca obok bestia5 na tych samych czujnikach** – prawdziwa próba dzierżaw: przejęcie czujników po wyłączeniu bestia5, oddanie, gdy bestia5 słyszy czujnik lepiej, żadnych równoczesnych połączeń. | ○ | S + czas |

**Kryterium:** telefon tydzień pracuje bez dotykania; zero konfliktów z bestia5. Luka do 2.3: gdy bestia5 jest wyłączona,
Android zbiera odczyty, ale maile nie wychodzą (po powrocie bestia5 alerty są liczone na nowo, a raport nadrobiony w granicach
`catchUpHours`).

### 2.3 – Raspberry Pi 24/7

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Pi jako agent** (dawne 1b.2): `scan`, `read`, `run` na Pi 64-bit z BlueZ (potrzebny ASP.NET Core Runtime 10 – panel WWW – albo paczka samodzielna): dopasowanie po MAC z identyfikatora BlueZ (nieprzetestowane; zapasowo po UUID Tuya), uprawnienia D-Bus i grupa `bluetooth`, stabilność połączeń (Wi-Fi/BLE na jednym układzie), unit systemd z `Restart`, paczka linux-arm64 w wydaniu, skrypt aktualizacji (jak `aktualizuj-rpi.sh` w LibrusNotifier). Ustalić minimalny wspierany sprzęt. Jeśli Shiny na Linuksie zawiedzie: zapasowo SimpleSharpBLE (licencja BUSL-1.1, tylko użytek niekomercyjny) albo bezpośrednio Linux.Bluetooth za interfejsem `IGattLink`. | ○ (paczka linux-arm64 tylko zbudowana) | M |
| 2 | **Główny nadawca:** rola nadawcy z priorytetem 1, bestia5 przechodzi na priorytet 2 (zapasowy); próba przejęcia blokady w obie strony. Strażnik i codzienny `cloud backup` przechodzą z bestia5 na Pi. Nadawca nie potrzebuje radia, więc Pi może wysyłać maile nawet wtedy, gdyby BLE na nim zawiodło. | ○ | S |

**Kryterium:** Pi wysyła maile 24/7; wyłączenie Pi przenosi wysyłkę na działającą bestia5 w ok. 7 min; brak nadawcy zgłasza
healthchecks.io.

---

## Faza 3 – interfejs webowy

Hipoteza (2026-11-16, po wyborze Supabase): statyczna strona (np. GitHub Pages) czytająca Supabase wprost przez RLS,
logowanie Supabase Auth tylko na konto właściciela, wykresy z `charts.js` lokalnego panelu, polecenia jako wiersze w tabeli
poleceń; konfiguracja jest jawna dla właściciela (bez odszyfrowywania), sekrety tylko do ustawienia, nigdy do odczytu.
Lokalny panel diagnostyczny monitora (1b.7) zostaje obok niej jako narzędzie serwisowe bez chmury; jego model statusu
(`MonitorStatus`) i układ kart mogą posłużyć za punkt wyjścia dla 3a.

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

- **Aktualizacje:** ✔ Windows (2026-11-15, samoaktualizacja z wydań GitHuba, zasada 7); ○ podpis wydań (np.
  Sigstore/minisign, żeby przejęte konto nie wystarczyło), mechanizm dla Pi (paczka z weryfikacją skrótu – 2.3.1);
  wersjonowanie protokołu agent–chmura (od 2.1 jest `v1`; stare monitory nie mogą zepsuć nowych danych); migracje
  schematu SQLite (jest `user_version`).
- **Obserwowalność:** ✔ sygnał (RSSI) w dzienniku przy każdym odczycie (2026-11-14); ◐ „agent milczy” i strażnik
  healthchecks.io – w kodzie trybu chmurowego (2.1.3), na żywo od 2.1.5; ○ liczniki (odczyty, błędy BLE, czasy połączeń), sygnał w bazie (kolumna w
  `readings`), trend w raporcie i alert „sygnał słaby” przy poziomie bliskim granicy (ok. −90 dBm).
- **Bezpieczeństwo:** przegląd modelu zagrożeń (wyciek tokenu – zwłaszcza nadawcy, który może pobrać hasło Gmaila;
  kradzież urządzenia; przejęcie konta Supabase), rotacja tokenów, limity zapytań, kopia zapasowa i próba odtworzenia z
  `cloud backup`, retencja i agregacja (np. odczyty surowe 90 dni, potem średnie godzinowe – limit 500 MB darmowego planu).
- **CI/CD:** ✔ build i testy na każdy push, wydanie win-x64 z tagu (2026-11-15); ○ paczka linux-arm64 (2.3.1), APK
  (2.2.3); testy sprzętowe jako ręczna lista kontrolna.
- **Runbook:** nowy monitor, nowy czujnik, reset czujnika, utrata hasła, utrata tokenu, przeniesienie na inny komputer.
- **Wspólna biblioteka poczty:** `GmailSender` istnieje dziś w dwóch projektach (tu i w LibrusNotifier); wyciągnąć do
  wspólnego pakietu NuGet albo świadomie zostawić kopię.

---

## Faza 5 – rozszerzenia (opcjonalne, w dowolnej kolejności)

- **Mądrzejsze podlewanie:** tempo wysychania (regresja z historii), prognoza „podlej za ~2 dni”, profile roślin i sezonowość,
  cisza nocna, poranny digest zamiast pojedynczych maili.
- **Inne kanały:** Telegram/ntfy/Web Push, webhooki, Home Assistant (MQTT).
- **Inne czujniki:** profile dla kolejnych produktów Tuya BLE (profil `ojzlzzsw` jest w kodzie, ale niesprawdzony na sprzęcie);
  Zigbee jako alternatywa, gdy zasięg BLE nie wystarcza.
- **Zasięg:** własne bramki ESP32 BLE→Wi-Fi (klucze Tuya wymagają własnej sesji, więc zwykły „Bluetooth proxy” nie wystarczy)
  albo po prostu więcej monitorów na Raspberry Pi.

---

## Decyzje

### Do podjęcia

| Decyzja | Kiedy | Uwagi |
|---|---|---|
| Raport przy wielu czujnikach | przy ok. 10 czujnikach | 1b.7b: szersze kolumny wykresu albo kilka maili. |
| Licencja repozytorium | 1b.8 | LibrusNotifier jest na GPL-3.0; kod protokołu Tuya wywodzi się z projektów MIT. |
| Technologia UI (strona statyczna: TypeScript / Blazor WASM) | start 3a | Strona czyta Supabase wprost. |
| Dostawca logowania do UI | start 3a | Hipoteza: Supabase Auth, tylko konto właściciela. |

### Rozstrzygnięte

- **2026-11-14** – panel WWW: HTTPS z certyfikatem z podpisem własnym, hasło, na razie bez edycji konfiguracji.
- **2026-11-15** – Windows: zadanie Harmonogramu przy zalogowaniu na koncie użytkownika, bez administratora; repozytorium
  publiczne od 0.1.0 (instalacja i aktualizacje bez tokenu); aktualizacje przy starcie i raz na dobę; dane usługi to kopia
  `local/`.
- **2026-11-16** – chmura: Supabase, darmowy plan („celem jest darmowość”); konfiguracja i wszystkie sekrety w Supabase
  (Vault), zaszyfrowana koperta odpada (a z nią problem odzyskiwania hasła); maile wysyła agent z blokadą (Pi – główny,
  bestia5 – zapasowy, Android nie wysyła); bez blokady, a więc bez Supabase, żaden agent nie wysyła maili, a brak nadawcy
  zgłasza healthchecks.io; kolejność bestia5 → Android → Raspberry Pi; dzierżawa czujnika 30 min; funkcje SQL testowane
  na próbnym projekcie `glebosaurus-test`, bez Dockera; log administratora – osobny mail od każdego agenta.
- **2026-11-17** – alert „brak odczytów” (użytkownik: „Tak, popraw”): cisza dalej od ostatniego odczytu, ale 30 min okresu
  ochronnego po starcie i uśpieniu; liczenie ciszy od startu odrzucone (martwy czujnik przy krótkiej pracy komputera).

## Główne ryzyka

- **Shiny/BlueZ na Pi** – dopóki nie sprawdzone, plan zapasowy to SimpleSharpBLE albo Linux.Bluetooth (zmiana tylko za `IGattLink`).
- **Zasięg BLE i zużycie baterii** – liczba agentów zależy od rozkładu czujników (czujniki przy wejściu już dziś są poza
  zasięgiem bestia5); każdy odczyt to ok. 11–14 s połączenia.
- **Okno parowania ok. 20 s** – zdalne parowanie (3d) musi być bardzo szybkie i dobrze opisane użytkownikowi.
- **Utrata kluczy czujników** – do 2.1.5 istnieją tylko w plikach `*.credentials.json`; utrata oznacza reset czujnika. Potem
  są w Vault, a kopię (zrzut bazy + lokalna kopia kluczy) robi `cloud backup`.
- **Android/MIUI** (2.2) – zabijanie aplikacji w tle, ograniczenia skanu BLE, akumulator telefonu stale na ładowarce.
- **Darmowy Supabase** – 500 MB (potem tylko odczyt), uśpienie po 7 dniach bez ruchu, brak kopii zapasowych; konto
  Supabase jest kluczem do wszystkich sekretów (logowanie dwuetapowe).
- **Dryf wskazań wilgotności** – bez kalibracji progi nie są porównywalne między doniczkami.
