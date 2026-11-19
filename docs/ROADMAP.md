# Glebosaurus – plan dalszych prac

Ten plik mówi, **co jest zrobione, co zostało i w jakiej kolejności**. Jak to działa – [ARCHITEKTURA.md](ARCHITEKTURA.md);
użycie – [README](../README.md); kiedy co zrobiono i sprawdzono na sprzęcie – [HISTORIA.md](HISTORIA.md).

Wielkości: **S** – dzień lub mniej, **M** – kilka dni, **L** – tydzień lub więcej (przy pracy w wolnych chwilach).
Stan: ✔ zrobione · ◐ w toku albo częściowo · ○ do zrobienia.

## Stan w skrócie (wersja 0.2.2)

**Działa i pracuje na co dzień** (u autora: komputer z Windows 11 jako nadawca i telefon z Androidem jako drugi
zbieracz, 9 czujników):

- protokół Tuya BLE, lokalne parowanie bez konta Tuya (`pair`), czujniki zostawione w Smart Life (klucze z chmury Tuya),
  zaległe pomiary z pamięci czujnika, nasłuch na przemian z odczytem;
- chmura Supabase na darmowym planie: konfiguracja i sekrety w chmurze, wielu agentów z dzierżawami czujników, blokada
  nadawcy i kolejka maili bez duplikatów, strażnik healthchecks.io, codzienna kopia zapasowa;
- maile pod telefon: alerty, raporty z wykresem 3 dób, log administratora, „agent milczy”;
- agent na Windows jako usługa bez okna z samoaktualizacją z wydań GitHuba; lokalny panel WWW (HTTPS, hasło) z wykresami,
  odczytem i raportem na żądanie;
- agent na Androida (tylko zbieranie) z rejestracją kodem QR i podglądem zasięgu;
- CI na każdy push, wydania z tagu `v*`.

**Niesprawdzone na żywo:**

| Obszar | Co zostało |
|---|---|
| Chmura | tydzień pracy bez dotykania; godzinna przerwa sieci i dosłanie; restart nadawcy bez dubli maili; uśpienie i nocne wyłączenia nadawcy; unieważnienie tokenu na prawdziwym agencie; polecenia z panelu (odczyt i raport na żądanie) |
| Poczta | dzienny limit Gmaila, zachowanie przy błędnym haśle |
| Usługa Windows | strażnik co godzinę; aktualizacja o 03:30; uśpienie i wybudzenie; wylogowanie w trakcie odczytu; utrata adaptera BLE; usuwanie starych dzienników |
| Panel WWW | wejście z telefonu w sieci domowej (reguła Zapory) |
| Android | noc i tydzień pracy; przejęcie czujników po wyłączeniu komputera; błąd GATT 133; APK z CI |
| Raspberry Pi | wszystko (paczka linux-arm64 tylko zbudowana) |
| Czujnik | kalibracja wilgotności; profil `ojzlzzsw`; czy niesparowany czujnik zawsze milknie po oknie 20 s |

### Następne kroki (w tej kolejności)

1. **Tydzień próby chmury** (kryterium fazy 2.1) i **tydzień telefonu obok komputera** (2.2.4).
2. **2.3 – Raspberry Pi 24/7** jako główny nadawca (komputer z Windows zostaje zapasowym).
3. **Faza 3** – interfejs WWW w chmurze, przede wszystkim edytor konfiguracji (dziś plik konfiguracji chmury i `cloud config`).
4. **Odtwarzanie z kopii zapasowej** (faza 4) – kopia jest, odtwarzania i jego próby jeszcze nie ma.

**W tle, niezależnie od kolejności:** pomiar baterii i analiza interwału (1b.4), kalibracja wilgotności (1b.5), analizatory
(1b.8).

---

## Przegląd faz

| Faza | Cel | Wielkość | Stan |
|---|---|---|---|
| 0, 1, 1b | Spike sprzętowy, protokół i parowanie, maile, praca w tle na Windows, panel, repozytorium | – | ✔ (baterie, kalibracja ○) |
| **2.1** | Chmura Supabase (darmowa) i pierwszy agent – komputer z Windows | L | ◐ wszystko zrobione, tydzień próby w toku |
| **2.2** | Agent na Androida – tylko zbieranie | L | ◐ agent pracuje; tydzień obok komputera ○ |
| **2.3** | Agent na Raspberry Pi 24/7 – główny nadawca maili | M | ○ |
| **3** | Interfejs webowy w chmurze: pulpit, historia, alerty, konfiguracja, parowanie zdalne | L | ○ (lokalny panel diagnostyczny jest) |
| **4** | Wdrożenie i utwardzanie: aktualizacje, monitoring, bezpieczeństwo, odtwarzanie, runbook | M | ◐ aktualizacje Windows i CI ✔ |
| **5** | Rozszerzenia (opcjonalne): mądrzejsze podlewanie, inne kanały, inne czujniki, zasięg | – | ○ |

Do 0.2.1 Glebosaurus miał też tryb samodzielny (monitor bez chmury, konfiguracja i poczta w pliku); usunięty 2026-11-19 –
HISTORIA.md, „Tryb samodzielny i przejście na chmurę”.

---

## Faza 1b – zostało z lokalnego monitora

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 4 | **Baterie i interwał.** Wykres wskazania baterii w czasie, prognoza życia baterii. Rozważyć interwał adaptacyjny: częściej blisko progu podlania, rzadziej gdy gleba jest wilgotna i nocą. | ◐ domyślny odstęp 2 h 5 min (czujnik i tak zapisuje rekord co godzinę); dane o baterii zbierane, analiza ○ | S + czas |
| 5 | **Kalibracja wilgotności.** Pomiary: powietrze, woda, mokra i sucha ziemia doniczkowa; kalibracja per czujnik (punkty „sucho”/„mokro” albo offset i skala) w konfiguracji chmury; sensowne progi domyślne i opis w README. | ○ | M |
| 7 | **Operacje.** „Wyślij log teraz” w panelu (polecenie `send_log` jest w chmurze), `status --json`. | ○ | S |
| 7b | **Raport przy wielu czujnikach:** budżet 90 KB mieści wykresy po 2 godz. do ok. 9 czujników, przy 20 – po 8 godz.; szersze kolumny (dziś) albo kilka maili. | ◐ | S |
| 8 | **Higiena repozytorium:** ✔ 2026-11-19 licencja AGPL-3.0-or-later ([LICENSE](../LICENSE), [NOTICE.md](../NOTICE.md)); ○ analizatory. | ◐ | S |

---

## Faza 2 – chmura i wiele agentów

Projekt: ARCHITEKTURA.md, zasady 1–4 i 10–15. Decyzje: Supabase na darmowym planie (tylko Postgres z funkcjami SQL i
Vault, bez Edge Functions i bez własnego serwera); konfiguracja i wszystkie sekrety w Supabase; wielu agentów zbiera, maile
wysyła agent z blokadą „nadaję teraz” (Android nigdy); dzierżawa czujnika 30 min; log administratora osobno od każdego
agenta.

### 2.1 – Supabase i pierwszy agent

✔ Schemat i funkcje SQL (zasada 13), części agenta w Core (zasada 14), agent i narzędzia właściciela (zasada 15), panel WWW
z kopią odczytów wszystkich agentów, kopia zapasowa chmury, wydanie 0.2.0 i praca na żywo.

**Kryterium:** komputer przez tydzień czyta czujniki, wysyła odczyty do Supabase i maile przez blokadę i kolejkę;
godzinna przerwa sieci kończy się dosłaniem bez strat; restarty nie dublują maili; unieważnienie tokenu odcina agenta.

### 2.2 – agent na Androida

Tylko zbieranie, telefon na Wi-Fi, stale na ładowarce (zasada 12; użycie – README, „Android”).

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Próba na telefonie:** instalacja, rejestracja QR, uprawnienia i ustawienia MIUI, skan i odczyty, zgaszony ekran, restart telefonu. | ◐ wszystko poza nocą na ładowarce | S–M |
| 2 | **Agent:** usługa w tle, start po restarcie, rejestracja QR, synchronizacja, ekran stanu i zasięgu, stan telefonu w sygnale życia. | ✔ ○ alert mailowy o przegrzaniu (nadawca potrzebuje stanu agentów – nowa funkcja SQL); start bez odblokowania (Direct Boot) | L |
| 3 | **Wydania:** APK z Actions podpisany kluczem wydań z sekretów repozytorium; powiadomienie o nowej wersji albo Obtainium. | ◐ workflowy gotowe, czekają na sekrety klucza | S |
| 4 | **Praca obok komputera na tych samych czujnikach:** przejęcie czujników po wyłączeniu komputera, oddanie przy lepszym sygnale, żadnych równoczesnych połączeń. | ◐ pierwszy dzień bez kolizji | S + czas |

**Kryterium:** telefon tydzień pracuje bez dotykania; zero konfliktów z innymi agentami. Luka do 2.3: gdy nadawca jest
wyłączony, telefon zbiera odczyty, ale maile nie wychodzą (po powrocie nadawcy alerty są liczone na nowo, a raport
nadrobiony w granicach `catchUpHours`).

### 2.3 – Raspberry Pi 24/7

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Pi jako agent:** `scan`, `read`, `run` na Pi 64-bit z BlueZ (ASP.NET Core Runtime 10 albo paczka samodzielna): dopasowanie po MAC z identyfikatora BlueZ (zapasowo UUID Tuya), uprawnienia D-Bus i grupa `bluetooth`, stabilność (Wi-Fi i BLE na jednym układzie), unit systemd z `Restart`, paczka linux-arm64 w wydaniu, skrypt aktualizacji. Jeśli Shiny na Linuksie zawiedzie: SimpleSharpBLE (BUSL-1.1, tylko niekomercyjnie) albo Linux.Bluetooth za `IGattLink`. | ○ (paczka tylko zbudowana) | M |
| 2 | **Główny nadawca:** priorytet 1, komputer z Windows priorytet 2; próba przejęcia blokady w obie strony; strażnik i codzienna kopia chmury przechodzą na Pi. | ○ | S |

**Kryterium:** Pi wysyła maile 24/7; wyłączenie Pi przenosi wysyłkę na komputer w ok. 7 min; brak nadawcy zgłasza
healthchecks.io.

---

## Faza 3 – interfejs webowy w chmurze

Hipoteza: statyczna strona (np. GitHub Pages) czytająca Supabase wprost przez RLS, logowanie Supabase Auth tylko na konto
właściciela, wykresy z `charts.js` lokalnego panelu, polecenia jako wiersze w tabeli poleceń; konfiguracja jawna dla
właściciela, sekrety tylko do ustawienia. Lokalny panel zostaje obok jako narzędzie serwisowe; jego model statusu
(`MonitorStatus`) i układ kart to punkt wyjścia dla 3a.

| # | Zakres | Wielkość |
|---|---|---|
| 3a | Szkielet, logowanie, **pulpit** (czujniki, ostatni odczyt, który agent), stan agentów, **historia** (wykresy). | M |
| 3b | **Alerty i raporty:** lista alertów, potwierdzenie i wyciszenie, historia raportów z podglądem HTML. | M |
| 3c | **Konfiguracja:** edytor czujników, progów, harmonogramów, odbiorców i ról agentów; sekrety tylko do ustawienia; te same reguły walidacji co `CloudSetupLoader` (wspólny schemat, testy kontraktowe), zapis z kontrolą wersji. Zastąpi plik konfiguracji chmury. | M–L |
| 3d | **Parowanie zdalne:** UI tworzy „żądanie parowania”, wybrany agent w zasięgu uruchamia `pair` (skan rusza pierwszy), UI mówi „włóż baterie teraz”, klucz trafia od razu do Vault. Wymaga krótkiego odpytywania w trybie parowania (okno czujnika ok. 20 s). | M |
| 3e | Widok mobilny, motyw jasny/ciemny, polski interfejs. | S |

**Kryterium:** cały scenariusz z przeglądarki, bez dotykania plików: dodać czujnik (parowanie zdalne), ustawić próg,
zobaczyć alert, zmienić odbiorców.

---

## Faza 4 – wdrożenie i utwardzanie

- **Aktualizacje:** ✔ Windows (samoaktualizacja z wydań GitHuba, zasada 7); ○ podpis wydań (np. Sigstore/minisign, żeby
  przejęte konto nie wystarczyło), mechanizm dla Pi (paczka z weryfikacją skrótu – 2.3.1); wersjonowanie protokołu
  agent–chmura (jest `v1`); migracje schematu SQLite (jest `user_version`).
- **Obserwowalność:** ✔ sygnał (RSSI) w dzienniku, „agent milczy”, strażnik healthchecks.io; ○ liczniki (odczyty, błędy
  BLE, czasy połączeń), sygnał w bazie i w raporcie, alert „sygnał słaby” przy poziomie bliskim granicy (ok. −90 dBm).
- **Bezpieczeństwo:** przegląd modelu zagrożeń (wyciek tokenu nadawcy, kradzież urządzenia, przejęcie konta Supabase),
  rotacja tokenów, limity zapytań; ✔ kopia zapasowa (`cloud backup`), ○ **odtwarzanie** (nowy projekt, migracje do wersji
  z kopii, tabele z CSV, sekrety do Vault) i jego próba; ○ szyfrowanie kopii kluczem publicznym (kopia bez sekretu na
  urządzeniu, klucz prywatny tylko u właściciela); retencja i agregacja (np. odczyty surowe 90 dni, potem średnie
  godzinowe – limit 500 MB darmowego planu).
- **CI/CD:** ✔ build i testy na każdy push, wydanie win-x64 z tagu, APK (czeka na sekrety klucza); ○ paczka linux-arm64,
  testy sprzętowe jako ręczna lista kontrolna.
- **Runbook:** nowy agent, nowy czujnik, reset czujnika, utrata hasła, utrata tokenu, przeniesienie na inny komputer.

---

## Faza 5 – rozszerzenia (opcjonalne, w dowolnej kolejności)

- **Mądrzejsze podlewanie:** tempo wysychania (regresja z historii), prognoza „podlej za ~2 dni”, profile roślin i
  sezonowość, cisza nocna, poranny digest zamiast pojedynczych maili.
- **Inne kanały:** Telegram/ntfy/Web Push, webhooki, Home Assistant (MQTT).
- **Inne czujniki:** profile dla kolejnych produktów Tuya BLE (profil `ojzlzzsw` jest w kodzie, ale niesprawdzony na
  sprzęcie); Zigbee jako alternatywa, gdy zasięg BLE nie wystarcza.
- **Zasięg:** własne bramki ESP32 BLE→Wi-Fi (klucze Tuya wymagają własnej sesji, więc zwykły „Bluetooth proxy” nie
  wystarczy) albo po prostu więcej agentów.

---

## Decyzje

### Do podjęcia

| Decyzja | Kiedy | Uwagi |
|---|---|---|
| Raport przy wielu czujnikach | przy ok. 10 czujnikach | 1b.7b: szersze kolumny wykresu albo kilka maili. |
| Szyfrowanie kopii zapasowej chmury | zanim kopie trafią poza komputer | Dziś kopia (z odszyfrowanymi sekretami) leży bez szyfrowania obok pliku administratora, którego hasło bazy i tak otwiera wszystkie sekrety. |
| Technologia UI (TypeScript / Blazor WASM) | start 3a | Strona czyta Supabase wprost. |

### Rozstrzygnięte (daty i uzasadnienia – HISTORIA.md)

- Panel WWW: HTTPS z certyfikatem z podpisem własnym, hasło, bez edycji konfiguracji (pełny edytor – 3c).
- Windows: zadanie Harmonogramu przy zalogowaniu na koncie użytkownika, bez administratora; repozytorium publiczne
  (instalacja i aktualizacje bez tokenu); aktualizacje przy starcie i raz na dobę.
- Chmura: Supabase, darmowy plan („celem jest darmowość”); konfiguracja i wszystkie sekrety w Supabase (Vault); maile
  wysyła agent z blokadą (Pi – główny, komputer z Windows – zapasowy, Android nie wysyła); bez Supabase żaden agent nie
  wysyła maili, a brak nadawcy zgłasza healthchecks.io; dzierżawa czujnika 30 min; funkcje SQL testowane na próbnym
  projekcie, bez Dockera; log administratora – osobny mail od każdego agenta.
- Alert „brak odczytów”: cisza od ostatniego odczytu, ale 30 min okresu ochronnego po starcie i uśpieniu.
- Odstęp odczytów: 2 h 5 min.
- Tryb samodzielny usunięty; konfiguracja chmury z pliku właściciela (`cloud config`) do czasu edytora w fazie 3c.

## Główne ryzyka

- **Shiny/BlueZ na Pi** – dopóki nie sprawdzone, plan zapasowy to SimpleSharpBLE albo Linux.Bluetooth (zmiana tylko za
  `IGattLink`).
- **Zasięg BLE i zużycie baterii** – liczba agentów zależy od rozkładu czujników; każdy odczyt to kilkanaście sekund
  połączenia.
- **Okno parowania ok. 20 s** – zdalne parowanie (3d) musi być bardzo szybkie i dobrze opisane użytkownikowi.
- **Utrata kluczy czujników** – są w Vault i w plikach `*.credentials.json` właściciela, a codzienna kopia chmury zawiera je
  odszyfrowane; utrata wszystkich kopii oznacza reset czujnika.
- **Android/MIUI** – zabijanie aplikacji w tle, ograniczenia skanu BLE, akumulator telefonu stale na ładowarce.
- **Darmowy Supabase** – 500 MB (potem tylko odczyt), uśpienie po 7 dniach bez ruchu, brak kopii zapasowych; konto Supabase
  jest kluczem do wszystkich sekretów (logowanie dwuetapowe).
- **Dryf wskazań wilgotności** – bez kalibracji progi nie są porównywalne między doniczkami.
