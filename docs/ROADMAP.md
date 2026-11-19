# Glebosaurus – plan dalszych prac

Ten plik mówi, **co jest zrobione, co zostało i w jakiej kolejności**. Jak to działa – [ARCHITEKTURA.md](ARCHITEKTURA.md);
użycie – [README](../README.md); kiedy co zrobiono i sprawdzono na sprzęcie – [HISTORIA.md](HISTORIA.md).

Wielkości: **S** – dzień lub mniej, **M** – kilka dni, **L** – tydzień lub więcej (przy pracy w wolnych chwilach).
Stan: ✔ zrobione · ◐ w toku albo częściowo · ○ do zrobienia.

## Stan w skrócie (wersja 0.2.4)

**Działa i pracuje na co dzień** (u autora: komputer z Windows 11 jako nadawca i telefon z Androidem jako drugi
zbieracz, 9 czujników):

- protokół Tuya BLE, lokalne parowanie bez konta Tuya (`pair`), czujniki zostawione w Smart Life (klucze z chmury Tuya),
  zaległe pomiary z pamięci czujnika, nasłuch na przemian z odczytem;
- chmura Supabase na darmowym planie: konfiguracja i sekrety w chmurze, wielu agentów z dzierżawami czujników, blokada
  nadawcy i kolejka maili bez duplikatów, strażnik healthchecks.io, codzienna kopia zapasowa;
- maile pod telefon: alerty, raporty z wykresem 3 dób, log administratora, „agent milczy”;
- agent na Windows jako usługa bez okna z samoaktualizacją z wydań GitHuba; lokalny panel WWW (HTTPS, hasło) z wykresami,
  odczytem i raportem na żądanie;
- agent na Androida (tylko zbieranie) z rejestracją kodem QR i podglądem zasięgu; stan telefonu (bateria, temperatura,
  Wi-Fi) w panelu WWW komputera;
- CI na każdy push, wydania z tagu `v*`.

**Niesprawdzone na żywo:**

| Obszar | Co zostało |
|---|---|
| Chmura | tydzień pracy bez dotykania; godzinna przerwa sieci i dosłanie; restart nadawcy bez dubli maili; uśpienie i nocne wyłączenia nadawcy; unieważnienie tokenu na prawdziwym agencie; polecenia z panelu (odczyt i raport na żądanie) |
| Poczta | dzienny limit Gmaila, zachowanie przy błędnym haśle |
| Usługa Windows | strażnik co godzinę; aktualizacja o 03:30; uśpienie i wybudzenie; wylogowanie w trakcie odczytu; utrata adaptera BLE; usuwanie starych dzienników |
| Panel WWW | wejście z telefonu w sieci domowej (reguła Zapory) |
| Android | noc i tydzień pracy; przejęcie czujników po wyłączeniu komputera; błąd GATT 133; APK z CI |
| Raspberry Pi | 2.3a (nadawca bez Bluetooth): instalacja na Pi, tydzień pracy 24/7, przejęcie blokady w obie strony, samoaktualizacja z prawdziwego wydania, powrót poprzedniej wersji przy zepsutym wydaniu; 2.3b (czujniki) – wszystko |
| Czujnik | kalibracja wilgotności; profil `ojzlzzsw`; czy niesparowany czujnik zawsze milknie po oknie 20 s |

### Następne kroki (w tej kolejności)

1. **Tydzień próby chmury** (kryterium fazy 2.1) i **tydzień telefonu obok komputera** (2.2.4).
2. **2.3a – Raspberry Pi 24/7 jako nadawca maili** (bez Bluetooth; komputer z Windows zostaje zapasowym): w kodzie, z próbą
   skryptu w CI na arm64; od 2026-11-19 nadawcą jest Pi – zostało przejęcie w drugą stronę i tydzień pracy.
3. **Faza 3** – interfejs WWW w chmurze, przede wszystkim edytor konfiguracji (dziś plik konfiguracji chmury i `cloud config`).
4. **Odtwarzanie z kopii zapasowej** (faza 4) – kopia jest, odtwarzania i jego próby jeszcze nie ma.
5. **2.3b – czujniki na Raspberry Pi**, gdy Pi stanie w ich zasięgu (albo drugie, 32-bitowe Pi jako zbieracz).

**W tle, niezależnie od kolejności:** pomiar baterii i analiza interwału (1b.4), kalibracja wilgotności (1b.5), analizatory
(1b.8).

---

## Przegląd faz

| Faza | Cel | Wielkość | Stan |
|---|---|---|---|
| 0, 1, 1b | Spike sprzętowy, protokół i parowanie, maile, praca w tle na Windows, panel, repozytorium | – | ✔ (baterie, kalibracja ○) |
| **2.1** | Chmura Supabase (darmowa) i pierwszy agent – komputer z Windows | L | ◐ wszystko zrobione, tydzień próby w toku |
| **2.2** | Agent na Androida – tylko zbieranie | L | ◐ agent pracuje; tydzień obok komputera ○ |
| **2.3a** | Raspberry Pi 24/7 – główny nadawca maili, bez Bluetooth | S–M | ◐ w kodzie (próba skryptu w CI na arm64); na Pi ○ |
| **2.3b** | Raspberry Pi czyta czujniki (BlueZ), także 32-bitowe Pi | M | ○ |
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
wysyła agent z blokadą „nadaję teraz” (Android nigdy; agent bez Bluetooth może być samym nadawcą); dzierżawa czujnika
30 min; log administratora osobno od każdego agenta.

### 2.1 – Supabase i pierwszy agent

✔ Schemat i funkcje SQL (zasada 13), części agenta w Core (zasada 14), agent i narzędzia właściciela (zasada 15), panel WWW
z kopią odczytów wszystkich agentów, kopia zapasowa chmury, wydanie 0.2.0 i praca na żywo.

**Kryterium:** komputer przez tydzień czyta czujniki, wysyła odczyty do Supabase i maile przez blokadę i kolejkę;
godzinna przerwa sieci kończy się dosłaniem bez strat; restarty nie dublują maili; unieważnienie tokenu odcina agenta.

### 2.2 – agent na Androida

Tylko zbieranie, telefon na Wi-Fi (zasada 12; użycie – README, „Android”).

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Próba na telefonie:** instalacja, rejestracja QR, uprawnienia i ustawienia MIUI, skan i odczyty, zgaszony ekran, restart telefonu. | ◐ wszystko poza pracą przez całą noc | S–M |
| 2 | **Agent:** usługa w tle, start po restarcie, rejestracja QR, synchronizacja, ekran stanu i zasięgu, stan telefonu w sygnale życia i w panelu WWW komputera (bateria, temperatura, stan cieplny, sieć). | ✔ ○ alert mailowy o przegrzaniu (nadawca ma już stan agentów z `agent_sync`); start bez odblokowania (Direct Boot) | L |
| 3 | **Wydania:** APK z Actions podpisany kluczem wydań z sekretów repozytorium; powiadomienie o nowej wersji albo Obtainium. | ◐ workflowy gotowe, czekają na sekrety klucza | S |
| 4 | **Praca obok komputera na tych samych czujnikach:** przejęcie czujników po wyłączeniu komputera, oddanie przy lepszym sygnale, żadnych równoczesnych połączeń. | ◐ pierwszy dzień bez kolizji | S + czas |

**Kryterium:** telefon tydzień pracuje bez dotykania; zero konfliktów z innymi agentami. Luka do 2.3a: gdy nadawca jest
wyłączony, telefon zbiera odczyty, ale maile nie wychodzą (po powrocie nadawcy alerty są liczone na nowo, a raport
nadrobiony w granicach `catchUpHours`).

### 2.3 – Raspberry Pi 24/7

Podział (decyzja użytkownika 2026-11-19): Raspberry Pi autora stoi daleko od czujników, więc nie da się na nim sprawdzić
odczytów, a jako nadawca maili przyda się od razu – pracuje bez przerwy. Najpierw **2.3a – Pi jako sam nadawca**, czytanie
czujników później (**2.3b**).

#### 2.3a – Pi jako nadawca maili (bez Bluetooth)

Projekt: ARCHITEKTURA.md, zasady 7 (instalacja i aktualizacje na Linuksie), 14d (zbieracz bez radia) i 15 (plik agenta);
użycie – README, „Raspberry Pi: nadawca maili”.

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Agent bez Bluetooth:** `"bluetooth": false` w pliku agenta – `run` nie otwiera BLE (BlueZ i grupa `bluetooth` niepotrzebne), nie słucha, nie ubiega się o czujniki i ich nie czyta; synchronizacja, dziennik do chmury, `send_log`, maile z rolą nadawcy, panel WWW i codzienna kopia chmury – jak zwykle. Na Linuksie jeden agent na plik konfiguracji (blokada pliku – usługa systemd i terminal to różne sesje). | ✔ 2026-11-19 (testy) | S |
| 2 | **Instalacja i aktualizacje:** paczka `linux-arm64` zależna od frameworka w wydaniu (ASP.NET Core Runtime 10 instaluje i aktualizuje właściciel), `install-linux.sh` – instalacja = aktualizacja: użytkownik `glebosaurus`, `/opt/glebosaurus`, usługa systemd z `Restart`, polecenie `glebosaurus`, samoaktualizacja z timera (przy starcie i o 03:30; skrypt nowego wydania sprawdzony z `SHA256SUMS`, próba nowego programu przed zatrzymaniem agenta, powrót poprzedniej wersji, gdy usługa po aktualizacji nie działa, nieudana wersja czeka dobę). CI: próba na `ubuntu-24.04-arm` z systemd przy każdym pushu, w wydaniu próba instalacji na arm64. | ◐ zrobione 2026-11-19, sprawdzone w Git Bash i w CI na arm64 z systemd; na Pi ○ | M |
| 3 | **Wdrożenie:** agent z priorytetem 1 (`cloud agent --sender-priority 1`), komputer z Windows z priorytetem 2; próba przejęcia blokady w obie strony (wyłączenie Pi → komputer po ok. 7 min; powrót Pi → oddanie po ok. 2 min); strażnik healthchecks.io wysyła ten, kto ma blokadę; codzienna kopia chmury – na jednym agencie (przenieść na Pi albo zostawić). | ◐ 2026-11-19: agent na Pi, przejęcie blokady od komputera (ok. 6 min od pierwszej synchronizacji); w drugą stronę i kopia chmury ○ | S |

**Kryterium:** Pi tydzień wysyła maile 24/7 bez dotykania; wyłączenie Pi przenosi wysyłkę na komputer w ok. 7 min; brak
nadawcy zgłasza healthchecks.io; aktualizacja z wydania przechodzi sama.

#### 2.3b – Pi czyta czujniki

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **BLE na Pi:** `scan`, `read`, `run` z BlueZ (Shiny.BluetoothLE.Linux): dopasowanie po MAC z identyfikatora BlueZ (zapasowo UUID Tuya), uprawnienia D-Bus i grupa `bluetooth` dla użytkownika `glebosaurus` (`install-linux.sh`), utwardzenie usługi a D-Bus, stabilność (Wi-Fi i BLE na jednym układzie). Jeśli Shiny na Linuksie zawiedzie: SimpleSharpBLE (BUSL-1.1, tylko niekomercyjnie) albo Linux.Bluetooth za `IGattLink`. | ○ | M |
| 2 | **32-bitowe Pi jako zbieracz:** paczka `linux-arm` w wydaniu (`package-linux.ps1 -Runtime linux-arm` już umie, `install-linux.sh` rozpoznaje `armv7l`), próba na starym Pi. | ○ | S |

**Kryterium:** Pi tydzień czyta czujniki w swoim zasięgu obok innych agentów, bez kolizji.

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

- **Aktualizacje:** ✔ Windows (samoaktualizacja z wydań GitHuba, zasada 7); ✔ Linux/Pi (`install-linux.sh` z timerem
  systemd, 2.3a); ○ podpis wydań (np. Sigstore/minisign, żeby przejęte konto nie wystarczyło); wersjonowanie protokołu
  agent–chmura (jest `v1`); migracje schematu SQLite (jest `user_version`).
- **Obserwowalność:** ✔ sygnał (RSSI) w dzienniku, „agent milczy”, strażnik healthchecks.io; ○ liczniki (odczyty, błędy
  BLE, czasy połączeń), sygnał w bazie i w raporcie, alert „sygnał słaby” przy poziomie bliskim granicy (ok. −90 dBm).
- **Bezpieczeństwo:** przegląd modelu zagrożeń (wyciek tokenu nadawcy, kradzież urządzenia, przejęcie konta Supabase),
  rotacja tokenów, limity zapytań; ✔ kopia zapasowa (`cloud backup`), ○ **odtwarzanie** (nowy projekt, migracje do wersji
  z kopii, tabele z CSV, sekrety do Vault) i jego próba; ○ szyfrowanie kopii kluczem publicznym (kopia bez sekretu na
  urządzeniu, klucz prywatny tylko u właściciela); retencja i agregacja (np. odczyty surowe 90 dni, potem średnie
  godzinowe – limit 500 MB darmowego planu).
- **CI/CD:** ✔ build i testy na każdy push, wydanie win-x64 z tagu, APK (czeka na sekrety klucza), paczka linux-arm64 z
  próbą instalacji na arm64 (2.3a); ○ testy sprzętowe jako ręczna lista kontrolna.
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
- Raspberry Pi (2026-11-19): najpierw sam nadawca bez Bluetooth (2.3a), czujniki w 2.3b; przełącznik `"bluetooth": false`
  w pliku agenta (ustawienie urządzenia – nie rola w chmurze, bez migracji); `install-linux.sh` z samoaktualizacją jak na
  Windows; paczka zależna od frameworka (ASP.NET Core Runtime 10 instaluje i aktualizuje właściciel); 32-bit (`linux-arm`)
  dopiero w 2.3b.

## Główne ryzyka

- **Shiny/BlueZ na Pi** (2.3b) – dopóki nie sprawdzone, plan zapasowy to SimpleSharpBLE albo Linux.Bluetooth (zmiana
  tylko za `IGattLink`).
- **.NET na Pi aktualizuje właściciel** (paczka zależna od frameworka) – bez tego poprawki bezpieczeństwa .NET nie
  dochodzą; wydanie na nowszym .NET (np. 11) nie zainstaluje się samo, dopóki właściciel nie doinstaluje runtime'u
  (`install-linux.sh` mówi, co zrobić, a usługa zostaje na starej wersji).
- **Zasięg BLE i zużycie baterii** – liczba agentów zależy od rozkładu czujników; każdy odczyt to kilkanaście sekund
  połączenia.
- **Okno parowania ok. 20 s** – zdalne parowanie (3d) musi być bardzo szybkie i dobrze opisane użytkownikowi.
- **Utrata kluczy czujników** – są w Vault i w plikach `*.credentials.json` właściciela, a codzienna kopia chmury zawiera je
  odszyfrowane; utrata wszystkich kopii oznacza reset czujnika.
- **Android/MIUI** – zabijanie aplikacji w tle, ograniczenia skanu BLE, telefon na zewnątrz (mróz, upał, zużycie baterii).
- **Darmowy Supabase** – 500 MB (potem tylko odczyt), uśpienie po 7 dniach bez ruchu, brak kopii zapasowych; konto Supabase
  jest kluczem do wszystkich sekretów (logowanie dwuetapowe).
- **Dryf wskazań wilgotności** – bez kalibracji progi nie są porównywalne między doniczkami.
