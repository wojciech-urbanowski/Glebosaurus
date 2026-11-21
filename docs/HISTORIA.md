# Glebosaurus – historia

Dziennik projektu: kiedy co zrobiono, co i jak sprawdzono na sprzęcie, jakie decyzje podjęto i dlaczego. Opis tego, jak
system działa **dziś**, jest w [ARCHITEKTURA.md](ARCHITEKTURA.md), plan – w [ROADMAP.md](ROADMAP.md). Ten plik dopisuje
się na bieżąco: sprawdzenia na sprzęcie trafiają tu z datą.

Nazwy z wdrożenia autora: **bestia5** – komputer z Windows 11 (usługa, nadawca maili), **mi11** – telefon Xiaomi 11 Lite
5G NE (agent Android), nazwy czujników to rośliny (Cyprysek, Cisy, Zioła, Wiszący wejście, Laurowiśnia wejście,
Laurowiśnia szafka, Hortensje niskie, Trawa, Alpinarium). „Monitor” w starszych wpisach to proces `glebosaurus run`
(dziś: agent), „tryb samodzielny” – usunięty sposób pracy bez chmury (niżej).

## Wdrożenie autora – stan na 2026-11-19

- **bestia5** (Windows 11, usługa w `%LOCALAPPDATA%\Glebosaurus` od 2026-11-15, wersja 0.2.3 od 2026-11-19 16:54): agent
  `bestia5`, nadawca z priorytetem 2, panel WWW w sieci lokalnej, codzienna kopia chmury do `data\backups`. Konfiguracja
  trybu samodzielnego sprzed przejścia na chmurę (`data\glebosaurus.standalone.config.json`, z hasłem Gmaila i kluczami
  Tuya) i jej pliki kluczy czujników (`data\*.credentials.json` – klucze są w chmurze) usunięte 2026-11-19.
- **mi11** (Android 14, HyperOS) od 2026-11-19 10:45: agent `mi11`, tylko zbieracz, na razie przy bestia5; APK 0.2.3 od
  2026-11-19 16:48 (instalacja przez adb).
- **rasp3** (Raspberry Pi, 64-bit, daleko od czujników) od 2026-11-19 ok. 21:27: agent `rasp3`, **główny nadawca**
  (priorytet 1), bez Bluetooth (faza 2.3a); wersja 0.2.4 z `install-linux.sh` (usługa systemd, samoaktualizacja z timera).
  bestia5 zostaje nadawcą zapasowym.
- **Prawdziwy projekt Supabase**: 5 migracji (ostatnia `20261120120000_agent_cpu_temperature` od 2026-11-20 08:07), 9 czujników (5 sparowanych lokalnie: Cyprysek, Cisy, Zioła, Wiszący wejście,
  Laurowiśnia wejście; 4 zostawione w Smart Life i czytane kluczami z chmury Tuya: Laurowiśnia szafka, Hortensje niskie,
  Trawa, Alpinarium), odczyt co 2 h 5 min (od 2026-11-18 21:32), raporty o 07:00 i 19:00, strażnik healthchecks.io.

Stan z planu prac z tego dnia (przed usunięciem trybu samodzielnego):

### Działa na co dzień (wersja 0.2.1, tryb chmurowy od 2026-11-18)

- **Usługa na bestia5** (Windows 11, od 2026-11-15, `%LOCALAPPDATA%\Glebosaurus`), **od 2026-11-18 18:06 w trybie
  chmurowym** (2.1.5): agent `bestia5` (nadawca, priorytet 2) czyta co 2 h 5 min (od 2026-11-18 21:32, wcześniej co 15 min) 9 czujników: 5 sparowanych lokalnie
  (Cyprysek, Cisy, Zioła, Wiszący wejście, Laurowiśnia wejście) i od 2026-11-18 19:07 4 zostawione w Smart Life
  (Laurowiśnia szafka, Hortensje niskie, Trawa, Alpinarium – klucze z chmury Tuya; Hortensje czekają na baterię), odczyty idą do Supabase (lokalna baza to bufor i kopia), alerty i raporty o 07:00 i 19:00
  wysyła przez blokadę nadawcy i kolejkę maili w chmurze, log administratora za poprzedni dzień, panel WWW w sieci
  lokalnej, codzienna kopia chmury do `data\backups`; start przy zalogowaniu, samoaktualizacja z GitHuba (0.1.0 →
  0.1.1 → 0.2.0 → 0.2.1). Konfiguracja (czujniki, progi, poczta, klucze Tuya) jest w chmurze – plik usługi ma tylko sekcję
  `cloud`; poprzednia leży obok jako `glebosaurus.standalone.config.json` (droga powrotu).
- **Prawdziwy projekt Supabase** (od 2026-11-18): schemat (3 migracje), konfiguracja z usługi (`cloud import`; od 19:07 9
  czujników), sekrety w Vault; pierwszy raport z harmonogramu z chmury o 19:00; nadawca wysyła sygnał do healthchecks.io co 5 min (bez ostrzeżeń w dzienniku; stan sprawdzenia – w panelu
  healthchecks.io).
- **Protokół Tuya BLE i lokalne parowanie** bez konta Tuya (`pair`), zaległe pomiary z pamięci czujnika, nasłuch na
  przemian z odczytem.
- **Maile pod telefon** (wygląd zatwierdzony 2026-11-13) z wykresem 3 dób w raporcie (2026-11-14). Doszły już prawdziwe
  maile wszystkich rodzajów: próbne, alert, log administratora, raport z harmonogramu (pierwszy 2026-11-16 o 19:00);
  w trybie chmurowym pierwsze 2026-11-18 o 18:09 (alert i log administratora).
- **Telefon jako drugi zbieracz** (od 2026-11-19 10:45, faza 2.2): agent `mi11` na Xiaomi 11 Lite 5G NE (Android 14,
  HyperOS) – usługa w tle na ładowarce, czyta czujniki, których nie trzyma bestia5 (pierwszego dnia Alpinarium, obie
  Laurowiśnie, Hortensje), maili nie wysyła. Na razie leży przy bestia5 – docelowe miejsce do wybrania.
- **Tryb samodzielny** (bez sekcji `cloud`) zostaje i działa jak dotąd – do prób z `local/` i dla monitorów bez chmury.
- **Repozytorium** publiczne na GitHubie, CI na każdy push, wydania z tagu `v*`.

### Następne kroki (w tej kolejności)

1. **Tydzień próby trybu chmurowego na bestia5 (2.1.5, od 2026-11-18)** – kryterium 2.1 niżej: odczyty w chmurze bez
   strat, maile bez dubli po restartach, dosłanie po przerwie sieci, unieważnienie tokenu odcina agenta (to ostatnie –
   tylko na prośbę; na prawdziwym agencie oznacza ponowny `enroll`). Do obejrzenia po drodze: raporty o 07:00 i 19:00 z
   chmury, codzienna kopia, nocne wyłączenia komputera (healthchecks.io, „brak odczytów” po starcie).
2. **2.2 – Android:** agent pracuje na telefonie od 2026-11-19 10:45 jako `mi11` w prawdziwym projekcie, obok bestia5
   (ARCHITEKTURA.md, zasada 12 – wyniki próby). Teraz: telefon na docelowe miejsce (podgląd zasięgu), noc i tydzień pracy
   (2.2.4). Pierwszego dnia przeczytał czujniki, których bestia5 nie trzymała – także Laurowiśnię wejście (−93 dBm).
3. **2.3 – Raspberry Pi 24/7** jako główny nadawca (priorytet 1; bestia5 zostaje zapasowym).
4. **Faza 3** – interfejs WWW w chmurze (może ruszyć zaraz po 2.1).
5. **Odtwarzanie z kopii zapasowej** (faza 4) – kopia jest od 2026-11-17 (na bestia5 codziennie od 2026-11-18),
   odtwarzania i jego próby jeszcze nie ma.

**W tle, niezależnie od kolejności:** pomiar baterii (1b.4 – dane zbiera usługa od 2026-11-15), kalibracja wilgotności
(1b.5), parowanie pozostałych czujników (1b.6 – nowy czujnik po `pair` trafia do chmury przez `cloud import`), licencja
(1b.8).

### Do zbadania – z dzienników usługi na bestia5 (2026-11-15…17)

- ✔ **Fałszywe alerty „brak odczytów” po wyłączeniu komputera** – poprawione w kodzie 2026-11-17, na bestia5 od 0.2.0
  (2026-11-18; tego dnia o 17:28, jeszcze na 0.1.1, zdarzyło się raz jeszcze). 2026-11-17 po ok. 18 h przerwy monitor ok. 2 min po starcie, po jednym oknie nasłuchu, otworzył
  alerty dla trzech czujników (mail z 5 alertami), bo cisza liczy się od ostatniego odczytu w bazie, także za czas
  przestoju monitora. Teraz po starcie i po uśpieniu jest 30 min okresu ochronnego – także u nadawcy w trybie chmurowym
  (ARCHITEKTURA.md, zasada 9).
- **Czujniki przy wejściu prawie poza zasięgiem bestia5.** Laurowiśnia wejście: 1 udany odczyt od 2026-11-14, Wiszący
  wejście: 8; 2026-11-16 (ok. 7 h pracy) – 0 i 2. Pozostałe czujniki też często „nie znaleziono w reklamach” (Cyprysek
  10 udanych na ok. 26 cykli), ale luki uzupełniają zaległe pomiary z czujnika. Do wyboru: przestawienie, drugi agent
  (2.2/2.3), pomiar sygnału (niżej, „Zasięg Cisy”). Według użytkownika (2026-11-18) w zasięgu bestia5 jest
  obecnie najwyżej część czujników, może mniejszość; w pierwszych oknach nasłuchu w trybie chmurowym (18:06–18:16) agent
  słyszał tylko Cisy (i trzy razy z rzędu nie znalazł go przy odczycie), a o 18:29 usłyszał i przeczytał 4 pozostałe
  (−61…−65 dBm – dużo lepiej niż zwykle w trybie samodzielnym). W trybie chmurowym agent bierze dzierżawę tylko na czujnik, który słyszy – reszta czeka na
  kolejnych agentów (2.2/2.3), a do tego czasu jej dotyczą alerty „brak odczytów”, jak dotąd.
- ✔ **4 czujniki ze Smart Life bez MAC** (Laurowiśnia szafka, Hortensje niskie, Trawa, Alpinarium – klucze z chmury Tuya,
  dopisane do `local/` 2026-11-16) – 2026-11-18: MAC ustalony skanem po UUID z reklamy, próbny `read` Hortensji udany,
  `cloud import` o 19:07 (na prośbę użytkownika), agent czyta Alpinarium, Trawę i Laurowiśnię szafkę; Hortensje milczą od
  próbnego odczytu (bateria 5% – niżej, „Czujnik ATLO SMS1”). Czujniki zostają w Smart Life.
- ✔ **„Migotanie” wykresu Alpinarium** (2026-11-18): pierwsza sesja z ok. 230 rekordami historii urwała się na limicie
  180 s, a ostatni odebrany rekord (sprzed 3 dni) został zakotwiczony na chwili połączenia – 152 rekordy przesunięte o 3 d
  6 h przeplatały się z prawdziwymi. Poprawka w 0.2.1 (ARCHITEKTURA.md, zasada 8), dane przesunięte z powrotem w chmurze i
  w bazie usługi (na prośbę użytkownika).
- **Niesparowany czujnik `gvygg3m8` (DC:23:51:…)** (2026-11-18): nadawał jako niesparowany ponad 2 min, po przypisaniu klucza
  zamilkł; na prośbę użytkownika porzucony, klucz usunięty – do ewentualnego użycia tylko po resecie przyciskiem.
- **„Czujnik nie potwierdził włączenia powiadomień”** – 7 razy w 3 dni (Cisy, Zioła), następny cykl zwykle udany. Niski
  priorytet; obserwować, czy nie rośnie.

### Czeka na użytkownika

- **Strona w chmurze (faza 3, etapy 1–5 w kodzie)** – na próbnym projekcie logowanie na żywo działa (2026-11-21: konto
  właściciela z TOTP, strona na GitHub Pages i z tego komputera – `dotnet run tools/web-preview.cs -- static`); można tam
  przeklikać edytor i alerty na danych pokazowych. `seed` i testy SQL czyszczą próbny projekt razem z listą właścicieli
  – konto w Auth zostaje, wystarczy znów `cloud owner`. Dalej prawdziwy projekt (migracje tylko na prośbę): w jego
  panelu Supabase wyłączyć zakładanie kont, Add user, Site URL https://wojciech-urbanowski.github.io/Glebosaurus/;
  logowanie z TOTP, `cloud owner` (README, „Strona w chmurze”) – zrobione 2026-11-21, wydanie 0.2.8 też. Zostało:
  przeklikać edytor na prawdziwych danych (zapisy ustawień, odbiorców, progów, „Wyślij próbny mail” po aktualizacji
  rasp3 do 0.2.8), sprawdzić `pair` na sprzęcie po przeniesieniu kroków BLE do `ShinySensorPairer` i parowanie zdalne
  przy czujniku.

- Obejrzeć na telefonie: pierwszy prawdziwy raport z wykresem (2026-11-16, 19:00), log administratora, nowy mail „agent
  milczy” (w `mail-preview`).
- Obejrzeć pierwsze maile z trybu chmurowego (2026-11-18 18:09: alert „słaba bateria” Cypryska i log administratora –
  oba to jednorazowe powtórki po przejściu) i pierwszy raport z chmury (19:00).
- Wymiana baterii (przypisanie i klucze zostają): **Hortensje niskie** (5%, od 2026-11-18 18:41 milczą – bez nowej baterii
  nie będzie odczytów), Trawa (8%), Laurowiśnia szafka (12–14%), Cyprysek (12% od 2026-11-16). Alerty „słaba bateria”
  Laurowiśni szafki i Trawy wyszły 2026-11-18 o 19:23 i 19:33.
- healthchecks.io: alarm „nadawca milczy” przy każdym wyłączeniu bestia5 na dłużej niż ok. 30 min – jeśli męczy, wyłączyć
  powiadomienia tego sprawdzenia w panelu healthchecks.io (do czasu Raspberry Pi).
- **Android (2.2):** telefon na docelowe miejsce (przycisk „Zasięg czujników”), stale na ładowarce; decyzja o blokadzie
  ekranu (z PIN-em agent po restarcie telefonu czeka na odblokowanie); kopia klucza wydań (`local/glebosaurus-android.keystore`
  i `.password`) poza komputerem; zgoda na sekrety klucza w repozytorium (APK w wydaniach).
- Decyzje: licencja repozytorium, raport przy wielu czujnikach (1b.7b).
- Reguła Zapory, jeśli panel ma być dostępny z telefonu.
- Fizycznie: parowanie kolejnych czujników (reset przyciskiem, baterie w oknie 20 s), pomiary do kalibracji.

### Niesprawdzone na żywo

| Obszar | Co zostało |
|---|---|
| Poczta | dzienny limit Gmaila, zachowanie przy błędnym haśle |
| Usługa Windows | strażnik co godzinę; aktualizacja o 03:30 (komputer był wtedy dotąd wyłączony); uśpienie i wybudzenie; wylogowanie w trakcie odczytu; utrata adaptera BLE; 7 dni ciągłej pracy; usuwanie dzienników starszych niż 7 dni |
| Panel WWW | wejście z telefonu w sieci domowej; wykresy na prawdziwym monitorze |
| Tryb chmurowy | (na żywo od 2026-11-18: historia, blokada nadawcy, maile przez kolejkę, nasłuch i dzierżawy, codzienna kopia w `run`, panel – logowanie, raport z harmonogramu, nowe czujniki z `cloud import` bez restartu) tydzień pracy, przerwa sieci i dosłanie, restart bez dubli maili, uśpienie i nocne wyłączenia w trybie chmurowym, unieważnienie tokenu na prawdziwym agencie, polecenia z panelu (odczyt i raport na żądanie) |
| Android | noc i tydzień pracy, docelowe miejsce, wyłączenie bestia5 (przejęcie czujników), błąd 133, APK z CI |
| Raspberry Pi | wszystko (paczka tylko zbudowana) |
| Czujnik | kalibracja wilgotności; profil `ojzlzzsw`; czy niesparowany czujnik milczy po oknie 20 s (2026-11-18 jeden nadawał ponad 2 min – ARCHITEKTURA.md) |

## Kalendarium

- **2026-11-13** – spike sprzętowy i lokalny monitor (faza 0–1): protokół Tuya BLE, lokalne parowanie bez konta Tuya
  (`pair`), odczyty do SQLite, alerty, raporty. `listen` pokazał, że czujnik oddaje zaległe pomiary i kasuje je po
  odebraniu – poprawka sesji (czytanie do ciszy, kolejność odbioru). Pomiar zasięgu Cisy (ziemia nie jest przyczyną). Pierwsze
  prawdziwe maile (`mail-test --samples`); wygląd maili pod telefon zatwierdzony przez użytkownika. Log administratora.
- **2026-11-14** – nasłuch na przemian z odczytem, okno nasłuchu 60 s, sygnał (RSSI) w dzienniku; panel WWW (HTTPS, hasło,
  sieć lokalna) na żywo, odczyt i raport na żądanie, wykresy (pulpit i strona czujnika); wykres w raporcie mailowym
  (kolumny po 2 godz., temperatura jako blady zakres – po uwagach użytkownika z telefonu); pierwszy alert z `run`.
- **2026-11-15** – usługa Windows (zadanie Harmonogramu), `install-windows.ps1`, samoaktualizacja z wydań GitHuba, CI i
  wydania z tagu; repozytorium publiczne, wydanie 0.1.0; usługa na bestia5 z danymi z `local/`. Porównanie protokołu z
  ha-tuya-ble (bajt w bajt; znaczenie DP14).
- **2026-11-16** – decyzje fazy 2 (Supabase, darmowy plan; konfiguracja i sekrety w Vault; blokada nadawcy; dzierżawa 30 min;
  kolejność bestia5 → Android → Pi); projekty Supabase (prawdziwy i próbny) i healthchecks.io; schemat i funkcje SQL;
  części trybu chmurowego w Core (a–e); agent (`enroll`, `run`, `cloud migrate`/`import`/`agent`). Samoaktualizacja
  0.1.0 → 0.1.1; pierwszy raport z harmonogramu.
- **2026-11-17** – okres ochronny 30 min dla „brak odczytów” po starcie i uśpieniu (`SilenceGrace`) po fałszywych alertach
  po nocnym wyłączeniu komputera; panel WWW w trybie chmurowym (kopia odczytów wszystkich agentów, `readings_feed`);
  `cloud backup`; zmiana roli agenta bez restartu; `cloud agent --revoke`.
- **2026-11-18** – wydanie 0.2.0; na prawdziwym projekcie `cloud migrate` i `cloud import` (5 czujników z konfiguracji
  usługi); 18:06 usługa na bestia5 przełączona na tryb chmurowy (625 odczytów historii przyjętych bez strat); 19:07
  `cloud import` 9 czujników (4 ze Smart Life, MAC ustalony skanem po UUID); błąd historii urwanej limitem sesji
  (Alpinarium) – poprawka w 0.2.1, na bestia5 od 20:20; 21:32 odstęp odczytów 2 h 5 min (`cloud settings`); agent
  Android w kodzie.
- **2026-11-19** – telefon jako agent `mi11` w prawdziwym projekcie (10:45). **Usunięcie trybu samodzielnego i narzędzi
  przejścia na chmurę** (niżej); `cloud import` zastąpiło `cloud config` z plikiem konfiguracji chmury; dokumentacja pod
  publikację (ten plik wydzielony z ARCHITEKTURY i ROADMAP). Shiny.BluetoothLE 6.5.2 → 6.6.0 (reszta pakietów NuGet była
  aktualna); sprawdzone na bestia5 buildem z repozytorium obok działającej usługi 0.2.1: `scan` (4 urządzenia Tuya) i
  `read` Ziół o 13:25 – zaraz po odczycie usługi, żeby nie zabrać rekordów historii – wykrycie, połączenie, sesja Tuya 3.1 i
  DP3/5/15/14 w ok. 20 s, wartości jak w odczycie usługi sprzed 30 s. **Wydanie 0.2.2** (Shiny 6.6.0, pierwsze bez trybu
  samodzielnego; bez APK – repozytorium nie ma sekretów klucza Androida) opublikowane 13:45 z tagu `v0.2.2`; na bestia5
  `stop` (1 s) i start zadania o 13:45:40 – usługa znalazła wydanie i skryptem w tle przeszła 0.2.1 → 0.2.2 w ok. 15 s.
  Plik agenta usługi (tylko `cloud`, `logs`, `monitorName`, `web`) przyjęty przez nowy loader; konfiguracja z chmury
  (wersja 60, 9 czujników), panel WWW i kopia odczytów od razu, blokada nadawcy (epoka 3) po 3,5 min, log administratora
  za 18.11 nie powtórzony, bez ostrzeżeń w dzienniku. Telefon mi11 został na APK zbudowanym z repozytorium przed 0.2.2.
  Stare pliki trybu samodzielnego (`glebosaurus.standalone.config.json` i 5 plików `*.credentials.json` w `data\` usługi,
  `glebosaurus.collect.config.json` w `local/`) usunięte do Kosza; usługa pracuje dalej bez zmian. **Licencja:
  AGPL-3.0-or-later** (`LICENSE`, nota i dodatkowe zezwolenie z §7 na bibliotekę Google Play services w APK – `NOTICE.md`;
  link do kodu w stopce panelu i w `--help`). Rozważane były też licencje z zakazem użytku komercyjnego (CC BY-NC-SA 4.0,
  AGPL + Commons Clause, PolyForm Noncommercial) – odrzucone: nie są open source, a żadna nie łączy spójnie zakazu
  zarabiania z obowiązkiem publikacji kodu; AGPL wybrana zamiast GPL, bo obejmuje też udostępnianie przez sieć.
  **Stan telefonu w panelu WWW (0.2.3)** – telefon niczego nie wystawia (prośba użytkownika), stan jedzie w `agent_sync`,
  a chmura oddaje go innym agentom. Migracja `20261119120000_agent_device_status` na prawdziwym projekcie o 16:46: usługa
  0.2.2 na bestia5 synchronizowała się dalej bez ostrzeżeń (nowe pole w liście agentów pomija). APK 0.2.3 na mi11 o 16:48
  przez adb, w przerwie między odczytami: agent wstał sam po `MY_PACKAGE_REPLACED` (konfiguracja wersja 60, 9 czujników).
  Minutę później w chmurze (`agent_status_view`, zapytanie tylko do odczytu) sygnał życia mi11: Wi-Fi −63 dBm, na
  ładowarce, bateria 97%, akumulator 15,0 °C, stan cieplny `none`; bestia5 – wersja, platforma i Bluetooth. Wydanie 0.2.3
  (bez APK – nadal bez sekretów klucza Androida) opublikowane 16:50 z tagu `v0.2.3`; na bestia5 `stop` (1 s) i start
  zadania o 16:54:15 – usługa przeszła 0.2.2 → 0.2.3 w ok. 15 s, konfiguracja z chmury (wersja 60), panel WWW i kopia
  odczytów od razu, bez ostrzeżeń w dzienniku; w chmurze obaj agenci z wersją 0.2.3 w sygnale życia.
  **Podział fazy 2.3** (prośba użytkownika): Raspberry Pi autora stoi daleko od czujników – nie da się na nim sprawdzić
  odczytów – a jako nadawca maili przyda się od razu, bo pracuje 24/7; więc najpierw **2.3a – Pi jako sam nadawca bez
  Bluetooth**, czytanie czujników w **2.3b**. Decyzje użytkownika: przełącznik `"bluetooth": false` w pliku agenta (nie rola
  w chmurze – bez migracji, zależy od sprzętu i miejsca; nie parametr `run`); `install-linux.sh` z samoaktualizacją z timera
  systemd (jak na Windows, nie ręcznie); paczka **zależna od frameworka** (ASP.NET Core Runtime 10 instaluje i aktualizuje
  właściciel), nie samodzielna; Pi autora jest 64-bitowe, a starsze 32-bitowe może kiedyś zostać zbieraczem – paczka
  `linux-arm` dopiero w 2.3b. Zrobione: zbieracz bez radia (`CollectorEngine` z `null` zamiast odczytu i nasłuchu), `run`
  bez BLE, na Linuksie jedna instancja przez blokadę pliku (muteks „Local\” obejmuje tylko sesję, a usługa systemd i
  terminal to różne sesje), `package-linux.ps1`, `install-linux.sh`, `test-install-linux.sh`, zadanie CI na
  `ubuntu-24.04-arm` i paczka linux-arm64 w wydaniu (z próbą instalacji na arm64). Stary wzór `deploy/glebosaurus.service`
  usunięty – jednostki zapisuje skrypt. **Sprawdzone 2026-11-19 w Git Bash na Windows** (paczki `linux-x64` 0.0.1 i
  0.0.2, bez systemd): instalacja z paczki, samoaktualizacja z udawanego wydania (`file://`) skryptem z wydania, zepsute
  wydanie (paczka z inną wersją w środku – wykryte przed podmianą, ślad próby, kolejna próba odłożona o dobę), jawny powrót
  do 0.0.1, odinstalowanie z danymi; paczka linux-arm64 na x64 słusznie odrzucona przed podmianą („architecture is not
  compatible”). APK zbudowany po zmianach w Core (bez instalacji na telefonie). **CI na `ubuntu-24.04-arm`** (po pushu
  ok. 19:50): pierwszy przebieg zatrzymał shellcheck (SC1111 – „” tuż przy zmiennych w napisach), po poprawce cała próba
  zielona – bez roota jak w Git Bash, ale na arm64 z prawdziwą paczką, oraz jako root z systemd: użytkownik
  `glebosaurus`, jednostki przechodzą `systemd-analyze verify`, `data/` 700, program należy do roota, usługa bez
  konfiguracji nie startuje, timer wyłączony przy `--auto-updates off`, aktualizacja usługi 0.0.1 → 0.0.2, polecenie
  `sudo glebosaurus`, odinstalowanie z danymi i użytkownikiem. W prawdziwym projekcie agent **`rasp3`** (nadawca,
  priorytet 1; kod rejestracji w `local/`) – do pierwszej synchronizacji Pi blokadę trzyma bestia5. **Wydanie 0.2.4**
  (pierwsze z paczką linux-arm64 i `install-linux.sh`) opublikowane 20:05 z tagu `v0.2.4` – próba instalacji paczki na
  arm64 w workflowie zielona; `install-linux.sh` w Git Bash pobrał z prawdziwego wydania (`latest/download`) wersję 0.2.4 i
  zgodny skrót, a uruchomienia paczki arm64 na x64 słusznie odmówił przed podmianą. **Na żywo na Pi (rasp3, 64-bit):**
  użytkownik zainstalował 0.2.4 skryptem z wydania; pierwszy `enroll` padł na „Request headers must contain only ASCII
  characters” – w `cloud.publishableKey` wkleił się nadmiarowy znak (odczyt konfiguracji sprawdza tylko przedrostek
  `sb_publishable_`); po poprawce `enroll` przeszedł tym samym kodem. Pierwsza synchronizacja `rasp3` ok. 21:27; bestia5
  oddał blokadę nadawcy ważniejszemu kandydatowi ok. 21:31 (po 2 min jego obecności; bestia5 nie zapisuje oddania w
  dzienniku – widać je tylko w chmurze), a `rasp3` wziął ją o 21:33:13 (epoka 5, odnawiana co minutę) – sprawdzone
  zapytaniem tylko do odczytu w tabeli dzierżaw. **Niesprawdzone:**
  samoaktualizacja Pi z prawdziwego wydania, powrót poprzedniej wersji po nieudanym starcie, przejęcie wysyłki w drugą
  stronę (Pi wyłączone → bestia5). **Ostrzeżenia o telefonie (0.2.5)** – decyzje użytkownika: praca telefonu na baterii jest normalna
  (nie zakładamy już, że stoi stale na ładowarce), a telefon może stać na tarasie (mróz, upał). Ostrzeżenie dopiero przy
  baterii poniżej 30% bez ładowarki; obok gorącego akumulatora (od 45 °C) i przegrzewania według systemu doszedł
  zmarznięty akumulator (poniżej 0 °C). Ostrzeżenia widać na ekranie aplikacji i w panelu WWW, bez osobnego maila –
  użytkownikowi to na razie wystarcza; mail przychodzi dopiero, gdy telefon zamilknie na 30 min („agent milczy”). Testy
  zielone, APK 0.2.5 zbudowany (bez instalacji na mi11); wydanie 0.2.5 z tagu `v0.2.5`.
- **2026-11-20** – dwie luki z wdrożenia `rasp3`: (1) odczyt pliku agenta sprawdza teraz cały klucz publikowalny (tylko
  litery, cyfry, „_” i „-” po `sb_publishable_`) – niewypełniony wzór i wklejony nadmiar dają błąd ze wskazaniem znaku i
  pozycji zamiast „Request headers must contain only ASCII characters” przy `enroll`; prawdziwe klucze (plik
  administratora, próbny projekt, usługa na bestia5) sprawdzone wcześniej bez wypisywania – spełniają regułę; (2) nadawca,
  który oddaje blokadę, zapisuje to w dzienniku z przyczyną (ważniejszy kandydat z nazwą i priorytetem, odebrana rola,
  zatrzymanie). Testy zielone, APK zbudowany (bez instalacji); w wydaniu – przy następnym.
- **2026-11-20** – **stan urządzeń w raporcie** (prośba użytkownika po porannym raporcie z rasp3; projekt zaakceptowany):
  pod czujnikami sekcja „Urządzenia” – telefon (bateria, ładowarka, temperatura akumulatora) i Raspberry Pi (temperatura
  procesora, nowe pole sygnału życia `cpuTemperatureCelsius`, migracja `20261120120000_agent_cpu_temperature`), ostrzeżenia
  jak w panelu, gorący procesor od 75 °C; urządzenie z ostrzeżeniem albo milczące liczy się do „Uwag”. Bez wykrywania zbyt
  niskiego napięcia zasilacza Pi (decyzja: odczyt raz na minutę gubiłby krótkie spadki). Testy zielone (także SQL na
  próbnym projekcie), wygląd sprawdzony w `mail-preview` przy 375 px, APK zbudowany (bez instalacji – telefon wysyła baterię
  i temperaturę od 0.2.3). Na prośbę użytkownika migracja `20261120120000_agent_cpu_temperature` na prawdziwym projekcie
  o 08:07 (plan: jedna czekająca; po niej bestia5, mi11 i rasp3 synchronizują się dalej) i **wydanie 0.2.6** z tagu
  `v0.2.6` – usługi na rasp3 i bestia5 pobiorą je same (przy starcie albo o 03:30). Pierwszy prawdziwy raport z sekcją ○.
- **2026-11-20** – **rzadsze odpytywanie** (prośba użytkownika: mniej baterii czujników i telefonu, nic nie wyłączać;
  lista odstępów z zależnościami zaakceptowana, nasłuch co 4 min zamiast proponowanych 5 – przy pętli 60 s dwa nasłuchy co
  5 min nie mieściłyby się w świeżości słyszenia 10 min). Pętla zbieracza 60 s, nasłuch co 4 min, ponowienia 2, 5, 10 min,
  sesja urwana – po 2 min, synchronizacja 90 s, obecność kandydata 4 min, nadawca: pętla 60 s i alerty co 5 min, zlecony
  odczyt czeka w panelu 6 min; strażnik, dzierżawy i okresy ochronne bez zmian (ARCHITEKTURA.md, „Zależności czasów”,
  pilnuje `TimingDependenciesTests`). Nowe: przejęty czujnik – ostatni odczyt z chmury przed pierwszym połączeniem;
  powiadomienie telefonu bez bazy i bez `alert_list` (dotąd ok. 1440 zapytań na dobę); godzinne podsumowanie nasłuchu w
  dzienniku. Tryb skanu Balanced dla nasłuchu na telefonie – dopiero po 1–2 dniach podsumowań z obecnym trybem (punkt
  odniesienia). Testy zielone, APK zbudowany (bez instalacji). Na prośbę użytkownika **wydanie 0.2.7** z tagu `v0.2.7`
  (bez migracji) – usługi na rasp3 i bestia5 pobiorą je same. Na prośbę użytkownika APK 0.2.7 na mi11 o 15:36
  (`adb install -r` w przerwie między nasłuchami, ostatnie połączenie z czujnikiem zamknięte o 14:43): agent wstał sam po
  `MY_PACKAGE_REPLACED` z tą samą rejestracją, wziął konfigurację i kopię odczytów, nasłuchy idą co 4–5 min. Jak sprawdzić
  efekt – niżej, „Rzadsze odpytywanie – jak sprawdzić”.
- **2026-11-21** – **faza 3** (interfejs w chmurze): decyzje użytkownika (12, wszystkie zalecane) – kolejność etapów
  (szkielet, 3c, 3a, 3b, 3d; 3e w każdym), zwykły JS bez budowania, dane tylko przez `public.owner_*` dla zalogowanego
  właściciela z TOTP, GitHub Pages z adresem projektu z odnośnika, plik konfiguracji chmury do usunięcia po sprawdzeniu
  edytora, alerty z potwierdzeniem i wyciszeniami, historia maili 90 dni, pełne zarządzanie agentami, parowanie zdalne
  w tej fazie (ROADMAP, faza 3 i „Rozstrzygnięte”). **Etap 1 w kodzie:** migracja `20261121120000_owner_web` (tabela
  `owners`, `require_owner()`, `owner_info`, `owner_dashboard`, `admin_owner_*`), `cloud owner` i `cloud web-link`, strona
  `web/` (połączenie z odnośnika, logowanie hasłem i TOTP z dodaniem TOTP przy pierwszym logowaniu, odświeżanie sesji
  pod blokadą kart, wylogowanie, pulpit, motyw ciemny, telefon), `pages.yml`; `format.js` wydzielony z lokalnego panelu
  jako plik wspólny. Testy SQL na próbnym projekcie zielone (78, w tym odmowa dla obcego konta, bez `aal2` i dla `anon`,
  dodanie właściciela z kontem w `auth.users` w wycofywanej transakcji); strona przeklikana na atrapie Supabase na
  loopbacku (zły i dobry e-mail/hasło, dodanie TOTP z kodem QR, zły i dobry kod, pulpit, sesja po przeładowaniu,
  wylogowanie, 375 px bez przewijania w bok, motyw ciemny; nazwa czujnika z `<b>` pokazana jako tekst). Na żywo ○ –
  czeka na kroki użytkownika (niżej, „Czeka na użytkownika”).
  **Etap 2 (3c) w kodzie:** migracja `20261121140000_owner_config` (archiwum czujników, polecenie `mail_test` i klucz
  `mail-test|…`, funkcje edytora z kontrolą `config_version`), próbny mail w `NotifierEngine`, strona `config.html` z
  sekcjami odczytów i raportów, poczty, czujników, agentów i strażnika; biblioteka qrcode-generator 2.0.4 (MIT, z rejestru
  npm, suma SHA-512 zgodna z rejestrem, przejrzana – bez sieci i bez DOM) do kodu QR rejestracji telefonu – orientacja
  sprawdzona po informacji o formacie (poziom M, obie kopie zgodne). Testy SQL zielone (163, w tym kontrakt z
  `CloudSetupLoader`: 38 przypadków po obu stronach, archiwum, konflikt wersji, sekrety, agenci, `mail_test` do nadawcy);
  edytor przeklikany na atrapie (błąd i zapis ustawień, hasło, próbny mail z błędem SMTP, nowy agent z kodem i QR,
  archiwum; 375 px bez przewijania w bok, motyw ciemny). Usunięcie pliku konfiguracji chmury i `cloud config` – po
  sprawdzeniu edytora na żywo i zgodzie użytkownika.
  **Etap 3 (3a) w kodzie:** migracja `20261121160000_owner_history` (`owner_series` z uśrednianiem w bazie,
  `owner_sensor`, `owner_readings`, pulpit z alertami i odbiorcami raportu), `charts.js` wspólny z lokalnym panelem,
  pulpit z wykresami 3 dób, odczytem i raportem na żądanie, strona czujnika z przeglądalnym wykresem. Testy SQL zielone
  (170, w tym kontrakt serii z `ReadingSeriesBuilder` w 5 scenariuszach: pojedyncze odczyty, przedziały z dziurą, koniec
  czasu letniego, dni, tygodnie od poniedziałku); pulpit i strona czujnika przeklikane na atrapie (stan odczytu na
  żądanie, „Całość”, 375 px bez przewijania w bok po poprawce nagłówka strony czujnika, motyw ciemny).
  **Etap 4 (3b) w kodzie:** migracja `20261121180000_owner_alerts` (potwierdzenie, ręczne zamknięcie, wyciszenie czujnika
  i wszystkich alertów, tabela `mail_archive` z `mail_archive_put` dla nadawcy, funkcje strony), nadawca: bez maili o
  potwierdzonych i wyciszonych alertach, notka i etykieta wyciszenia w raporcie, archiwum maila po wysyłce; strony
  `alerts.html` i `mail.html`. Testy zielone (SQL 173; Core – potwierdzenie, wyciszenie czujnika z mailem po końcu,
  urlop z raportem, archiwum i jego błąd); strona alertów przeklikana na atrapie (potwierdzenie, urlop na 3 dni, podgląd
  maila w ramce sandbox ze stylami). Wygląd raportu z wyciszeniem (notka o urlopie pod paskiem, szara etykieta
  „alerty wyciszone do …” przy czujniku; `mail-preview` → `raport-wyciszenie.html`) zaakceptowany przez użytkownika
  2026-11-21 o 06:59.
  **Etap 5 (3d) w kodzie:** migracja `20261121200000_owner_pairing` (postęp poleceń, `pair_key_get|put`,
  `pair_complete`, `owner_pair_start`), `PairingProcedure` i `RemotePairing` w Core, `ShinySensorPairer` w agencie (te same
  kroki BLE co `pair`, który teraz go używa), `RadioGate` w zbieraczu, panel „Sparuj czujnik zdalnie” na stronie. Testy
  zielone (SQL 176 – od polecenia do nowego czujnika, ponowne parowanie istniejącego, odmowy; Core – kolejność kroków z
  kluczem przed wysłaniem, kilku kandydatów, wznowienie, zbieracz bez radia w trakcie); panel przeklikany na atrapie
  („włóż baterie” dopiero po starcie skanu, kilku kandydatów z adresami w polu MAC, sukces). Na sprzęcie ○ – `pair` po
  przeniesieniu kroków BLE i parowanie zdalne czekają na próbę z użytkownikiem przy czujniku.
  **Przegląd bezpieczeństwa** (niezależny przegląd migracji fazy 3 i logowania strony): bez uwag wysokiej wagi;
  poprawione w migracji `20261121220000_owner_hardening` i na stronie – jedno czekające parowanie na czujnik, ponowna
  próba jako nowe polecenie, porzucanie i automatyczne kasowanie nieaktualnego klucza parowania, właściciel tylko z
  potwierdzonym TOTP (kolejność kroków w README: najpierw logowanie z TOTP, potem `cloud owner`), archiwum tylko
  wysłanych maili z limitami długości i limitem rozpakowania w przeglądarce, pytanie przed przełączeniem projektu z
  odnośnika, odświeżanie sesji raz na kartę i bez wylogowania przy 429, klasa `html` nie koliduje już ze stylem `.top`.
  Testy SQL 177 i jednostkowe zielone; zmiany strony sprawdzone na atrapie.
  **Podgląd na prawdziwych funkcjach SQL** (`tools/web-preview.cs` zamiast atrapy w Pythonie): `seed` wgrywa na próbny
  projekt dane pokazowe (5 czujników, 3 agentów, 2 tygodnie odczytów co godzinę, 4 alerty, 9 maili w archiwum z szablonów
  `MailSamples`), `serve` udaje tylko Supabase Auth, a funkcje `owner_*` woła w bazie jak PostgREST (rola `authenticated`
  z żądaniami tokenu, dopasowanie po nazwach argumentów, transakcja tylko do odczytu dla `STABLE`, kody HTTP błędów).
  Przeklikane: logowanie z kodem i z dodaniem TOTP, pulpit z wykresami 3 dób, strona czujnika (7 dni, odczyty, kto
  słyszy), alerty (potwierdzenie, wyciszenie czujnika na 3 dni), podgląd maila z archiwum, edytor (odbiorcy, próg
  czujnika, konflikt wersji z drugiej karty, nowy agent z kodem i QR), odczyt na żądanie (czeka na agenta), 375 px bez
  przewijania w bok – bez niezgodności strony z funkcjami SQL. Poprawione: pulpit nie powtarza otwartego alertu drugą
  etykietą („Słaba bateria” i „słaba bateria”), system agenta w edytorze nazwany jak na pulpicie, komunikat
  `cloud web-link` (konto zakłada się w panelu Supabase; Site URL tylko dla strony https). Tryb `static` serwuje samo
  `web/` na 127.0.0.1:8473 – prawdziwe logowanie na próbnym projekcie bez GitHub Pages (Supabase odpowiada na CORS `*`;
  sprawdzone zapytaniem wstępnym dla Auth i RPC). Na próbnym projekcie zakładanie kont jest jeszcze włączone.
  **Pierwsze prawdziwe logowanie** (użytkownik, próbny projekt, strona z `static`): hasło przeszło, ale kod QR do
  dodania TOTP się nie wyświetlił – obraz SVG od Supabase (`totp.qr_code`, data: URL) przeglądarka pokazuje jako pusty;
  strona rysuje teraz kod sama z `totp.uri` (otpauth://) tą samą biblioteką co kod QR rejestracji telefonu. Po
  poprawce: dodanie TOTP z kodu QR, ekran „Brak dostępu” przed nadaniem właściciela, `cloud owner`, pulpit z danymi
  pokazowymi przez prawdziwe Supabase Auth i Data API – **logowanie na żywo działa na próbnym projekcie** (zakładanie
  kont wyłączone). **GitHub Pages** (repozytorium wypchnięte, CI zielone): Pages ze źródłem „GitHub Actions”, workflow
  „Strona w chmurze” uruchomiony ręcznie – strona pod https://wojciech-urbanowski.github.io/Glebosaurus/ (wszystkie pliki
  z właściwymi typami); logowanie z TOTP na próbnym projekcie i pulpit z danymi pokazowymi także stamtąd.
  **Zgodność agentów 0.2.7 z migracjami fazy 3** (przed migracjami na prawdziwym projekcie): przegląd zmian w funkcjach,
  które woła 0.2.7 – `get_config` i `alert_json` tylko dokładają pola (0.2.7 czyta JSON bez ścisłości, nieznane pola
  pomija) i pomijają czujniki z archiwum, `mail_claim`, `command_create` i `commands_for` tylko przyjmują `mail_test`;
  polecenia `mail_test` i `pair` agent 0.2.7 zostawia z jednym ostrzeżeniem w dzienniku („wygaśnie samo”), a archiwizacja
  czujnika zamyka jego alerty w bazie. Próba: testy chmury z tagu `v0.2.7` (kod agenta 0.2.7 od końca do końca –
  synchronizacja, nadawca, zbieracz, kopia odczytów, konfiguracja, kopia zapasowa) na próbnym projekcie z bieżącymi
  migracjami – 69 z 70 zielonych; jedyny błąd to test dokładnej listy funkcji API (nowe funkcje – zamierzone). Wniosek:
  migracje można zastosować przed nowym wydaniem; do wydania strona pokazuje próbny mail, wyciszenia, potwierdzenia,
  archiwum maili i parowanie zdalne, ale agenci 0.2.7 ich nie wykonują (przypomnienia o potwierdzonych i maile o
  wyciszonych alertach idą dalej).
  **Migracje fazy 3 na prawdziwym projekcie** (na prośbę użytkownika, ok. 11:54): sześć migracji od `20261121120000_owner_web`
  do `20261121220000_owner_hardening` zastosowanych, schemat aktualny. Agenci 0.2.7 (bestia5, rasp3, mi11) synchronizują
  się dalej bez błędów (sprawdzone w ciągu kilku minut po migracji). Potem użytkownik: zakładanie kont wyłączone, konto właściciela,
  pierwsze logowanie z dodaniem TOTP na stronie z GitHub Pages (Firefox); `cloud owner` na prawdziwym projekcie (12:06).
  **Błąd na prawdziwej stronie:** „Wycisz czujnik → 3 dni” kończył się komunikatem „UPDATE requires a WHERE clause”.
  Supabase ładuje dla roli `authenticator` (Data API) rozszerzenie `pg_safeupdate`, które odrzuca UPDATE/DELETE bez WHERE
  także wewnątrz funkcji; wyzwalacz `config_changed` (każda zmiana czujnika) i pięć funkcji strony i agenta
  (`owner_settings_update`, `owner_sensor_add`, `owner_sensor_credentials`, `owner_alerts_mute`, `pair_complete`)
  zmieniały jedyny wiersz `settings` bez WHERE. Testy SQL i podgląd łączą się z bazą wprost – bez tego rozszerzenia
  (załadować go tam nie można: „access to library "safeupdate" is not allowed”). Poprawka: migracja
  `20261121230000_safe_update` (te same funkcje z `where id`, także dwie `admin_*`), test statyczny `SqlSafeUpdateTests`
  (bez migracji wskazuje dokładnie te 8 funkcji), `REQUIRED_MIGRATION` strony podbite. Testy SQL 177 zielone na
  próbnym projekcie. Agenci 0.2.7 nie byli dotknięci (ich funkcje nie zmieniają czujników ani ustawień). Migracja na
  prawdziwym projekcie na prośbę użytkownika (12:29); agenci synchronizują się dalej, a wyciszenie czujnika na 3 dni ze
  strony (Firefox) przechodzi.
  **Wydanie 0.2.8 przygotowane** (wersja podbita na prośbę użytkownika, bez tagu): w agencie od 0.2.7 – nadawca wysyła
  próbny mail na polecenie ze strony, pomija maile i przypomnienia o potwierdzonych i wyciszonych alertach (czujnik,
  urlop), pokazuje wyciszenia w raporcie i zapisuje wysłane maile do archiwum strony; zbieracz na Windows paruje czujnik
  zdalnie (`pair` z wiersza poleceń na tych samych krokach BLE); `cloud owner`, `cloud web-link`. Wymaga migracji do
  `20261121230000_safe_update` (prawdziwy projekt już ją ma). Skrypty instalacji, usługa Windows i workflow wydania bez
  zmian od 0.2.7. Testy zielone (686, SQL 177), paczki win-x64 i linux-arm64 oraz APK zbudowane lokalnie. Na prośbę
  użytkownika **wydanie 0.2.8** z tagu `v0.2.8` (12:41; workflow „Wydanie” zielony z próbą instalacji na arm64) – usługi
  na bestia5 i rasp3 pobiorą je same; APK na mi11 niepotrzebny (nowości są dla nadawcy i zbieracza na Windows). Strona
  z tagu nie poszła: środowisko `github-pages` domyślnie przyjmuje wdrożenia tylko z `main` („Tag "v0.2.8" is not allowed
  to deploy to github-pages due to environment protection rules”); treść strony i tak aktualna z ręcznego uruchomienia.
  Na prośbę użytkownika reguła tagu `v*` w środowisku `github-pages` (obok `main`) – ponowione wdrożenie z `v0.2.8`
  przeszło; opis dla własnej kopii repozytorium w README, „Strona w chmurze”.
  **Plik konfiguracji chmury i `cloud config` usunięte** (na prośbę użytkownika; konfiguracja zmienia się już tylko na
  stronie w chmurze): `CloudSetup`/`CloudSetupLoader`, `CloudSetupWriter`, `cloud config`, wzór
  `examples/cloud-config.json`, pole `tuyaFile`, sekcja `healthchecks` pliku administratora (strażnika ustawia strona) i
  `mail-test` (próbny mail z pliku – zastępuje go „Wyślij próbny mail” na stronie; wygląd maili – `mail-preview` i podgląd
  w historii maili). `pair` zapisuje klucz w bieżącym katalogu (albo `--credentials`) i odsyła do „Dodaj czujnik z pliku
  klucza”; identyfikator czujnika sprawdza `SensorIds`. `ConfigContractTests` – te same przypadki już tylko na funkcjach
  SQL. Funkcje `admin_*` w bazie zostają (testy, `cloud settings`, `tools/web-preview.cs`); komentarze w zastosowanych
  migracjach wspominają `CloudSetupLoader` – zostają (suma kontrolna). Testy zielone (632, SQL 138).
  Lista alertów: przy wyciszonym czujniku albo urlopie karta mówi „maile wstrzymane do …” (dłuższe z wyciszeń) zamiast
  „przypomnienie co 24 godz.” – sprawdzone na podglądzie z próbną bazą (czujnik 3 dni, urlop 1 dzień).
  **Funkcje strony przez prawdziwe Supabase Auth i Data API w testach SQL** (`DataApiOwnerTests`, po błędzie
  `pg_safeupdate`): przebieg zakłada w Auth próbnego projektu konto testowe (admin API kluczem tajnym), loguje się
  hasłem, dodaje TOTP z kodem liczonym w teście (token `aal2`), nadaje właściciela przez `admin_owner_add` i woła każdą
  funkcję `owner_*` przez `/rest/v1/rpc` jak strona; konto usuwa na końcu (sprawdzone: w Auth nie zostaje). Dowód, że ta
  droga łapie błąd z rana: z definicjami ośmiu funkcji sprzed migracji `safe_update` (przywróconymi na chwilę w próbnej
  bazie) przejście kończy się „400 21000: UPDATE requires a WHERE clause”, po przywróceniu poprawki przechodzi; stały
  test robi to samo z wyzwalaczem `config_changed` (przez Data API 21000, wprost przechodzi). **Nowy błąd:** konflikt
  wersji konfiguracji (`require_config_version`, SQLSTATE 40001) przez Data API nie wraca – PostgREST 14.18 ponawia
  transakcję po 40001: ok. 1000 ponowień na sekundę przez ok. 50 s (ok. 45 tys. wycofanych transakcji na jedno żądanie,
  także po zerwaniu połączenia przez klienta po 30 s). Dotyczy każdego zapisu konfiguracji ze strony z nieaktualną
  wersją; przez połączenie bezpośrednie konflikt przechodzi od razu, więc dotąd nikt tego nie widział. Test
  oczekiwanego zachowania czekał pominięty (143 zielone, 1 pominięty). **Poprawka** (na prośbę użytkownika): migracja
  `20261121233000_config_conflict` – `require_config_version` zgłasza konflikt kodem PT409 (PostgREST oddaje PTxyz jako
  status HTTP xyz, bez ponawiania), treść „Glebosaurus: conflict: …” bez zmian, więc strona działa z migracją i bez niej
  (`REQUIRED_MIGRATION` bez zmian); test statyczny `SqlDataApiRulesTests` (żadna funkcja nie zgłasza kodu z klasy 40),
  podgląd (`web-preview.cs`) oddaje PTxyz jako status. Testy SQL zielone (144, konflikt przez Data API wraca od razu
  jako 409); potem dane pokazowe (`web-preview.cs -- seed`) i właściciel próbnego projektu (`cloud owner`) przywrócone.
  Migracja na prawdziwym projekcie na prośbę użytkownika (13:36): `require_config_version` ma tam PT409, agenci
  (bestia5, mi11, rasp3) synchronizują się dalej (sprawdzone w ciągu 2 min po migracji). Zapisu z konfliktem na
  prawdziwej stronie nie próbowano.

## Tryb samodzielny i przejście na chmurę (0.1.0–0.2.1)

Do wersji 0.2.1 agent miał drugi sposób pracy: **monitor samodzielny** bez chmury. Plik konfiguracji zawierał czujniki
(`sensors` z `tuyaFile`), pocztę z hasłem Gmaila (`mail`), raporty i progi; `MonitorEngine` czytał czujniki, liczył alerty
i wysyłał maile sam, a stan alertów i znaczniki wysłanych raportów trzymał w SQLite (tabele `alerts` i `report_runs`).
Były też polecenia `status`, `report` i `send-log`, a skrypt instalacji umiał przejąć katalog `local/` (`-ImportFrom`).

Przejście na chmurę (bestia5, 2026-11-18): `cloud migrate` → `cloud import` (konfiguracja trybu samodzielnego: ustawienia,
odbiorcy, czujniki z kluczami Tuya, hasło Gmaila do Vault) → aktualizacja usługi do 0.2.0 → jednorazowy skrypt
`przejscie-na-chmure.ps1` (zatrzymanie usługi, poprzednia konfiguracja obok jako `glebosaurus.standalone.config.json`,
plik z sekcją `cloud`, `cloud agent bestia5 --sender-priority 2`, `enroll`, start; przy błędzie powrót do trybu
samodzielnego). Historii odczytów nie przenosił import: agent wysłał ją sam ze swojej bazy przy pierwszych
synchronizacjach (kolumna `sync_state` dodana do starej bazy, `user_version` bez zmian, żeby 0.1.x po ewentualnym powrocie
dalej ją otwierała). Chmura nie znała stanu alertów i wysyłek trybu samodzielnego, więc pierwszy alert i log
administratora przyszły jeszcze raz.

**2026-11-19 tryb samodzielny usunięto** (wszystkie instalacje pracowały już w chmurze, a dwa sposoby pracy podwajały
kod, testy i dokumentację): `MonitorEngine`, `SnapshotBuilder`, alerty i raporty w SQLite, polecenia `status`, `report`,
`send-log`, `-ImportFrom`, gałęzie trybu samodzielnego w panelu. `cloud import` (przeniesienie konfiguracji trybu
samodzielnego) zastąpiło `cloud config` – zapis **pliku konfiguracji chmury** właściciela (ten sam format czujników, poczty
i raportów, bez ustawień urządzenia); plik agenta odrzuca teraz ustawienia, które należą do chmury. Stare bazy SQLite
zachowują nieużywane tabele `alerts` i `report_runs`. Ostatni commit z trybem samodzielnym: `ecd7983`.

## Próby na sprzęcie i na żywo

Wpisy przeniesione z ARCHITEKTURY (stan na 2026-11-19). W starszych wpisach „monitor” i polecenia `status`, `report`,
`send-log`, `cloud import` dotyczą wersji sprzed usunięcia trybu samodzielnego.

### Stan wdrożenia obszarów

| Obszar | Stan |
|---|---|
| Protokół Tuya BLE, lokalne parowanie, sesja, odczyt DP | zrobione, sprawdzone na sprzęcie |
| Monitor: odczyty, SQLite, alerty, raporty, mail (szablony) | zrobione, sprawdzone na żywo na Windows |
| Wysyłka prawdziwego maila przez Gmail | zrobione: 2026-11-13 `mail-test --samples` wysłał 3 maile (próbny, alert, raport) z konta nadawcy na skrzynkę użytkownika, SMTP przyjął wszystkie (logowanie hasłem aplikacji działa); wygląd i kolorystyka obejrzane i zatwierdzone przez użytkownika na telefonie; 2026-11-14 `run` sam otworzył prawdziwy alert (słaba bateria Cypryska, 19% przy progu 20%) i wysłał o nim mail – SMTP przyjął (18:06); 2026-11-16 19:00 usługa na bestia5 wysłała pierwszy prawdziwy raport z harmonogramu (z wykresem 3 dób; SMTP przyjął); **niesprawdzone**: dzienny limit, zachowanie przy błędnym haśle, wygląd prawdziwego raportu z wykresem na telefonie |
| Log techniczny dla administratora (dziennik dzienny, retencja, mail z załącznikiem) | zrobione w kodzie i testach (2026-11-13), podgląd obejrzany w widoku telefonu; 2026-11-13 `send-log` wysłał prawdziwy dziennik z załącznikiem na skrzynkę administratora, Gmail przyjął; 2026-11-14 `run` w pierwszym przebiegu dnia sam wysłał dziennik z 13.11 (ok. 3 s po starcie), a restart monitora kilka minut później go nie powtórzył (znacznik w bazie); **niesprawdzone**: wygląd tego maila na telefonie, czyszczenie starych plików przez `FileLoggerProvider` na żywo |
| Raspberry Pi (BlueZ przez Shiny) | **niesprawdzone** – faza 2.3b |
| Raspberry Pi jako nadawca bez Bluetooth (2.3a) | zrobione 2026-11-19: `"bluetooth": false`, `install-linux.sh` z samoaktualizacją, paczka linux-arm64 w wydaniu; skrypt sprawdzony w Git Bash (bez systemd), CI na arm64 z systemd; **niesprawdzone na Pi** |
| Praca w tle na Windows (zadanie Harmonogramu), instalacja i samoaktualizacja z wydań GitHuba | zrobione 2026-11-15 (zasada 7): usługa `glebosaurus-service` bez okna, skrypt `install-windows.ps1`, workflowy CI i wydania; testy logiki w Core. **2026-11-15 na żywo (Windows, katalog próbny, udawane API wydań na loopbacku, monitor z atrapą czujnika):** instalacja z przejęciem konfiguracji, `stop` (1 s), aktualizacja o codziennej porze (monitor zatrzymany czysto, skrypt w tle, nowa usługa wystartowana przez Harmonogram), aktualizacja przy starcie (po wymuszonym powrocie do starszej wersji), zepsute wydanie (zły skrót – stara wersja wraca, próba odłożona o dobę, bez pętli), blokada drugiego monitora (kod 4), odinstalowanie. **2026-11-15 wydanie 0.1.0** opublikowane przez `release.yml` (testy, paczka, próba instalacji w Windows PowerShell 5.1 na runnerze – wszystko zielone; API wydań odpowiada bez logowania) i zainstalowane na bestia5 skryptem pobranym z wydania, z przejęciem `local/` i bez startu. Próba na sucho przejęcia prawdziwych danych wykryła, że `ConvertFrom-Json` z PowerShell 5.1 nie zna komentarzy `//`, a jego komunikat błędu zawiera cały plik z hasłem Gmaila – skrypt czyta teraz konfigurację przez `Read-ConfigJson` i nie wypisuje treści wyjątku. **Na żywo z prawdziwymi danymi:** 2026-11-15 23:56 usługa wystartowana przez Harmonogram (panel WWW ze stałej ścieżki – reguła Zapory zatwierdzona raz; o północy sama wysłała log administratora), a 2026-11-16 00:06 pierwsza samoaktualizacja z GitHuba 0.1.0 → 0.1.1 przy starcie usługi (ok. 15 s: sprawdzenie wydania, skrypt w tle, podmiana `app\`, nowa usługa z Harmonogramu; log administratora się nie powtórzył, Zapora nie pytała ponownie). **Dzienniki usługi 2026-11-16…17:** komputer dwa razy wyłączony w trakcie pracy monitora (nie w trakcie połączenia z czujnikiem; bez wpisu o zatrzymaniu) – po zalogowaniu usługa wystartowała sama, sprawdziła wydania, a pierwszy odczyt dociągnął zaległe pomiary z pamięci czujnika (Zioła: 19 od 22:32 poprzedniego dnia); log administratora za poprzedni dzień wyszedł przy pierwszym przebiegu. Po przerwie ok. 18 h monitor ok. 2 min po starcie otworzył alerty „brak odczytów” – cisza liczy się od ostatniego odczytu w bazie, także za czas przestoju monitora; od 2026-11-17 w kodzie okres ochronny 30 min po starcie i uśpieniu (zasada 9; na bestia5 od 0.2.0). 2026-11-18 o 17:28 (0.1.1, po ok. 19 h przerwy) błąd zdarzył się jeszcze raz (dwa alerty „brak odczytów” w mailu z 5 alertami, zamknięte po ok. 20 min); o 18:06 aktualizacja 0.1.1 → 0.2.0 skryptem instalacji uruchomionym przez użytkownika (zatrzymanie monitora po bieżącym kroku, start nowej wersji w ok. 4 s), a zaraz potem przejście na tryb chmurowy (wiersz „Chmura Supabase”). 2026-11-18 20:19 aktualizacja 0.2.0 → 0.2.1 już w trybie chmurowym: `stop` i start zadania, usługa przy starcie znalazła wydanie, skrypt w tle podmienił `app\` i o 20:20:13 wystartowała nowa wersja (ok. 20 s); konfiguracja z kopii w chmurze, odczyty i blokada nadawcy (nowa epoka) wróciły w ciągu kilku minut, bez powtórzonych maili. **Niesprawdzone**: strażnik co godzinę, aktualizacja o 03:30 na prawdziwym wydaniu (o 03:30 komputer był dotąd wyłączony), zachowanie po uśpieniu i wybudzeniu, wylogowanie w trakcie odczytu, 7 dni ciągłej pracy |
| Lokalny panel diagnostyczny WWW (HTTPS, sieć lokalna, hasło; podgląd, odczyt i raport na żądanie) | zrobione w kodzie i testach (2026-11-14, w tym testy na prawdziwym Kestrelu z TLS); `web-password` uruchomione na żywo, a strona obejrzana na prawdziwych danych (kopia bazy, dzienniki, konfiguracja) przez program podglądu na loopbacku, także w widoku telefonu 375 px. **2026-11-14 na żywo (Windows, `run` z prawdziwą konfiguracją):** panel startuje z monitorem, odcisk certyfikatu z dziennika zgadza się z `web-password` i z serwerem (127.0.0.1 i adres LAN), bez logowania 302/401; użytkownik zalogował się w przeglądarce (połączenie przyszło z adresu IPv6 link-local – filtr sieci go przepuścił). Próba wykryła błąd: przy `Referrer-Policy: no-referrer` przeglądarka wysyła z formularza `Origin: null`, a filtr POST odrzucał je jako obcą stronę (403, bez wpisu w dzienniku) – teraz rozstrzyga `Sec-Fetch-Site`, polityka to `same-origin`, odrzucenie trafia do dziennika (testy odtwarzają nagłówki przeglądarki). Wykresy (pulpit: 3 doby; strona czujnika: cała historia) – 2026-11-14 w testach i w przeglądarce na programie podglądu z danymi próbnymi (zasada 11). **Niesprawdzone**: wejście z telefonu w sieci domowej (reguła Zapory), wykresy na prawdziwym monitorze, Raspberry Pi |
| Chmura Supabase, wielu agentów (dzierżawy czujników, blokada nadawcy) | faza 2 uzgodniona z użytkownikiem 2026-11-16 (zasady 1–4, 10, 12); 2026-11-16: projekty Supabase (prawdziwy i próbny) i healthchecks.io założone; schemat i funkcje SQL (zasada 13), Core w trybie chmurowym (zasada 14) i agent (zasada 15: sekcja `cloud`, `enroll`, `run`, `cloud migrate`/`import`/`agent`) zrobione i sprawdzone w testach i na próbnym projekcie; 2026-11-17: panel WWW w trybie chmurowym z kopią odczytów wszystkich agentów (`readings_feed`) i kopia zapasowa chmury (`cloud backup`, także codziennie w `run`) – w testach, na próbnym projekcie i (panel) w przeglądarce na programie podglądu; zmiana roli agenta bez restartu (poprawka) i odcięcie agenta (`cloud agent --revoke`) – w testach i na próbnym projekcie. **2026-11-18 (2.1.5) na żywo:** prawdziwy projekt – `cloud migrate` (3 migracje), `cloud import` z konfiguracji usługi (5 czujników z kluczami Tuya, hasło Gmaila i adres healthchecks.io w Vault; 4 czujniki ze Smart Life z `local/` bez MAC pominięte – chmura rozpoznaje czujnik po MAC), adres i klucz publikowalny sprawdzone wywołaniem `enroll` z błędnym kodem (odmowa 28000 z funkcji, nie z bramy); wydanie 0.2.0 (pierwsze z trybem chmurowym), usługa zaktualizowana skryptem instalacji i o 18:06 przełączona skryptem użytkownika (zatrzymanie, poprzednia konfiguracja obok jako `glebosaurus.standalone.config.json`, sekcja `cloud` z `backup`, `cloud agent` bestia5 z priorytetem 2, `enroll`, start). Pierwszy prawdziwy `run` w trybie chmurowym: konfiguracja z chmury (wersja 22), panel WWW w trybie chmurowym (logowanie użytkownika 18:07), wczorajszy dziennik w chmurze; cała historia z bazy usługi – 625 odczytów od 2026-11-13 – przyjęta przy pierwszych synchronizacjach (wszystkie `sync_state` = 1, bez odrzuceń); blokada nadawcy (epoka 1) po ok. 2,5 min i pierwsze maile w trybie chmurowym o 18:09 – alert „słaba bateria” Cypryska i log administratora za 17.11, oba to jednorazowe powtórki (chmura nie ma stanu alertów ani śladu wysyłek trybu samodzielnego; kolejka maili: każdy z rezerwacją i wysłaniem raz). Nasłuch w radiu Shiny na sprzęcie: o 18:13 usłyszany Cisy (−80 dBm) i dzierżawa, trzy próby odczytu Cisy „nie znaleziono w reklamach” (w trybie samodzielnym 2026-11-17: 6 udanych na 31); o 18:29 dzierżawy i udane odczyty 4 czujników (−61…−65 dBm) – w bazie usługi 674 odczyty, wszystkie przyjęte przez chmurę. Pierwsza codzienna kopia w `run` o 18:16 (10 min po starcie): 11 KB, 625 odczytów, 7 sekretów. Teraz tydzień próby (kryterium 2.1 w ROADMAP) |
| Agent Android (tylko zbieranie) | 2026-11-18 w kodzie (zasada 12): usługa w tle, radio BLE na API Androida, ekran stanu i zasięgu, rejestracja kodem QR, APK podpisany kluczem wydań. **2026-11-19 na telefonie** (Xiaomi 11 Lite 5G NE, Android 14, HyperOS): rejestracja QR, skan przy zgaszonym ekranie, odczyty 4 czujników (także Laurowiśnia wejście przy −93 dBm), start po restarcie (po odblokowaniu) i po aktualizacji; od 10:45 agent `mi11` w prawdziwym projekcie obok bestia5. Niesprawdzone: noc i tydzień pracy, docelowe miejsce |
| Interfejs WWW w chmurze | zaprojektowane (zasada 10), niezrobione – faza 3 |

### Czujnik ATLO SMS1 (Tuya `gvygg3m8`)

**Reklamy i okno parowania**
- Czujnik wystawia reklamy BLE ze znacznikami Tuya (usługa 0xA201, dane producenta 0x07D0). Bez baterii milczy; po
  włożeniu baterii nadaje jako niesparowany tylko ok. 20 s (dioda mruga tyle samo).
- Sparowany czujnik reklamuje się rzadko (rzędu jednego pakietu na kilka–kilkanaście sekund), a dane usługi i dane
  producenta przychodzą w osobnych pakietach – pełna tożsamość Tuya składa się dopiero po kilku sekundach. Dlatego monitor
  dopasowuje czujnik po adresie MAC z konfiguracji (od pierwszego pakietu), a UUID Tuya jest drogą zapasową.

**Parowanie i sesja**
- **Lokalne parowanie bez konta Tuya działa**: własny `deviceId` + 6-bajtowy klucz lokalny, zapis przez BLE, ponowne
  połączenie, szyfrowana sesja AES (protokół Tuya BLE 3.1, firmware 9.3), odbiór punktów danych.
- `glebosaurus pair` sprawdzone: nowy klucz, wznowienie przerwanego parowania, ponowne przypisanie tym samym kluczem. Tuż po
  przypisaniu czujnik bywa cichy (ok. 1 pakiet na 12 s), więc weryfikacja czeka do 90 s (30 s okazało się za mało).
- **Wymiana baterii nie kasuje przypisania** (≥ 60 s bez zasilania, bez przycisku reset – ten sam klucz od razu uwierzytelnił
  sesję; DP6 w pierwszym raporcie potwierdza restart). Przypisanie kasuje **reset przyciskiem**; wtedy
  `pair --id <to samo id>` przypisze ten sam klucz jeszcze raz.
- **Czujniki zostawione w Smart Life też czytamy** (2026-11-18): klucze z chmury Tuya (`deviceId`, `localKey`, `uuid`, bez
  MAC) – MAC 4 czujników ustalony skanem po UUID Tuya z reklamy (`scan`; ta sama metoda dała dla 5 znanych czujników dokładnie
  ich MAC), `read` z takim kluczem działa (protokół 3.1, firmware 9.3, jak przy lokalnym parowaniu), a po `cloud import`
  agent czyta je w tym samym rytmie co pozostałe. Skutki: odebrane przez nas rekordy znikają z czujnika, więc Smart Life traci od
  tej pory ich historię; połączenie aplikacji w telefonie blokuje nasz odczyt w danym cyklu. Zegary tych czujników były
  dobre (Alpinarium, Trawa, Laurowiśnia szafka – godzinne rekordy z prawdziwym dobowym rytmem temperatury); wcześniejsze
  „Hortensje ok. 17 h 46 min do tyłu” było skutkiem błędu niżej, nie zegara.
- **Historia dłuższa niż jedna sesja** (2026-11-18): czujnik oddaje ok. 110 rekordów na 180 s sesji (MTU danych 20), od
  najstarszego, a zwykły raport stanu na końcu; czujnik ze Smart Life trzyma tygodnie rekordów godzinnych (Alpinarium: od
  11-09, ok. 230). Gdy limit sesji urwie historię, ostatni odebrany rekord bywa sprzed dni – a `MapAll` kotwiczył serię na
  chwili połączenia (przy rozjechanym zegarze), więc **Alpinarium dostało 152 rekordy z 11-09…11-15 przesunięte o 3 d 6 h**
  (przeplot z prawdziwą serią z następnej sesji, „migotanie” na wykresie) i najstarszy rekord jako „stan teraz” o 19:14;
  ten sam błąd dał wyświetlenie historii Hortensji w `read` o 17 h za późno. Laurowiśnia szafka też doszła do limitu, ale
  po raporcie stanu – bez szkody. Poprawka 0.2.1 (zasada 8): sesja przerwana limitem bez raportu stanu nie ma stanu
  bieżącego, rekordy zostają z czasem czujnika (te z przyszłości odpadają), silnik zapisuje je od razu (czujnik już je
  skasował) i wraca po resztę po 1 min, bez porażki w dzierżawie. Dane Alpinarium naprawione jednorazowo w chmurze i w
  bazie usługi: przesunięcie z rytmu rekordów (sesja 2 zaczęła się dokładnie tam, gdzie urwała się 1) – 3 d 5 h 57 min
  44,6 s, dokładność ok. ±15 s.
- **Długa sesja na słabej baterii** (2026-11-18): Hortensje niskie z baterią 5% (DP14 = niski także w rekordach bez
  obciążenia radia) po pełnej 180-sekundowej sesji `read` (urwanej w trakcie historii – ok. 18 najnowszych rekordów zostało w
  czujniku) przestały się reklamować w ogóle – najpewniej wyczerpana bateria. Przy DP14 = 0 nie robić ręcznych odczytów z
  długą historią; najpierw wymiana baterii.
- **Niesparowany czujnik nadający dłużej niż 20 s** (2026-11-18): jeden czujnik `gvygg3m8` nadawał jako niesparowany
  przez całe 2 min skanu (42 pakiety) – może pętla restartów na słabej baterii. `pair` przypisał mu klucz (sesja bez błędu),
  ale potem czujnik zamilkł (ponad 15 min, także dwa wznowienia `pair`); na prośbę użytkownika klucz usunięto – jeśli
  przypisanie się udało, czujnik wymaga resetu przyciskiem.

**Dane**
- Punkty danych profilu `sgs01`: DP3 wilgotność gleby %, DP5 temperatura gleby ×10 °C, DP15 bateria %, DP14 enum
  **stan baterii: 0 niski, 1 średni, 2 wysoki**, DP9 enum (prawdopodobnie jednostka temperatury), DP6 wartość 0 (nieznane; widać je w części
  rekordów – godzinowych i pierwszym po starcie – a nie w rekordach z chwili połączenia).
- DP14 (2026-11-15): znaczenie wg [roquerodrigo/ha-tuya-ble](https://github.com/roquerodrigo/ha-tuya-ble) (ten sam produkt
  `gvygg3m8`, „SGS01”); nasza baza się z nim zgadza – 0 tylko przy DP15 16–19% (Cyprysek, 16 rekordów), 1 przy 21–56% (120
  rekordów, wszystkie czujniki), 2 jeszcze nie wystąpiło. Próg „niski” wypada więc ok. 20%. `SoilSensorProfiles.BatteryStateName`
  podaje nazwę stanu; `read` ją pokazuje, alerty wciąż patrzą na DP15.
- Wskazanie baterii rośnie po odpoczynku (27% → 34% → 37%): to szacunek z napięcia, zaniżany pod obciążeniem radia, więc do
  alertu nadaje się tylko trend albo wiele odczytów.

**Porównanie z ha-tuya-ble (2026-11-15)**
- Integracja Home Assistant [roquerodrigo/ha-tuya-ble](https://github.com/roquerodrigo/ha-tuya-ble) 1.2.1 obsługuje ten sam
  czujnik (`gvygg3m8`); protokół ma w bibliotece [tuya-ble-sdk](https://github.com/roquerodrigo/tuya-ble-sdk) 0.1.4, na której
  wzorowany jest `Glebosaurus.Tuya`. Nasze ramki (Device Info, parowanie sesji, prośba o stan) zgadzają się z ich wektorami
  bajt w bajt (`ProtocolTests.FramesMatchReferenceImplementationByteForByte`); odczyt reklamy, klucze, fragmentacja, punkty danych,
  potwierdzanie raportów, odpowiedzi na prośby o czas i nieobsługiwane raporty podpisane (`0x8004`/`0x8005`) – tak samo.
- Różnice celowe: oni biorą `device_id` i `local_key` z konta Tuya (czujnik zostaje w Smart Life), my parujemy lokalnie (zasada 5;
  sesja przyjęłaby też klucze z chmury). Sesja SDK kończy się po 1,5 s ciszy i po 60 s wyrzuca wszystko, co zebrała, a czujnik oddaje
  historię co ok. 1,1 s – my czekamy 3 s ciszy, limit to 3 min, a po nim oddajemy to, co przyszło (patrz „Zaległe pomiary”).
- Przejęte od SDK: **nieczytelna ramka z punktami danych nie przerywa odczytu** (wcześniej `InvalidDataException` wywracał całą
  sesję i przepadało to, co już odebrane). Sesja pomija taką ramkę, ale – inaczej niż SDK – ją potwierdza: czujnik oddaje
  historię od najstarszego rekordu, więc bez potwierdzenia ten sam rekord mógłby wracać na początku każdej sesji albo wstrzymać
  resztę serii. Liczba pominiętych ramek i surowe bajty pierwszej trafiają do uwag sesji, a `run` zapisuje uwagi sesji w
  dzienniku jako ostrzeżenia. Na sprzęcie nieczytelnej ramki jeszcze nie było (dzienniki z 13–14.11 czyste).

**Czas i bateria**
- Jeden odczyt (Windows, Shiny): wykrycie ok. 1–2 s, połączenie ok. 1 s, włączenie powiadomień ok. 2 s, pierwsze punkty danych
  po 5–8 s od startu sesji; razem ok. 11–14 s połączenia. To pomiar sprzed 2026-11-13: sesja kończyła się wtedy na pierwszym
  komplecie DP3/5/15, czyli na najstarszym rekordzie historii (patrz „Zaległe pomiary” niżej); teraz czeka na ciszę po serii.
- Odczyt co minutę (próba) zbił wskazanie baterii z 33% do 27% w ok. 40 minut – artefakt próby, ale pokazuje, że
  częstotliwość odczytów decyduje o baterii.
- **Odstęp odczytów: 2 h 5 min** (decyzja użytkownika 2026-11-18, wcześniej 15 min): wilgotność gleby zmienia się wolno, a
  czujnik sam zapisuje rekord co godzinę i oddaje go przy połączeniu (niżej, „Zaległe pomiary”), więc rzadsze połączenia
  nie gubią przebiegu – baza dostaje te same punkty godzinowe plus stan z chwili odczytu; sesja jest dłuższa tylko o 2 ramki
  (ok. 2 s). Alert „czas podlać” (`consecutiveReadings` kolejnych odczytów poniżej progu) liczy też rekordy z historii, a
  „brak odczytów” (domyślnie 6 h) musi być dłuższy niż odstęp – `cloud settings` ostrzega, gdy nie jest. W trybie chmurowym
  odstęp jest ustawieniem w chmurze (`cloud settings --read-interval`), w samodzielnym – `readIntervalMinutes` w pliku.
  Prawdziwy projekt: 125 min od 2026-11-18 21:32 (wersja konfiguracji 59 → 60); usługa na bestia5 (0.2.1) pobrała ją sama
  o 21:32:55, bez nowego wydania.

**Pułapki**
- Wymuszone zabicie procesu w trakcie rozłączania powoduje, że następny odczyt wisi na włączaniu powiadomień (limit 12 s to
  teraz wyłapuje); do prób służy `run --minutes N`, które kończy pracę czysto.
- Shiny na Windows: `Peripheral.Uuid` ma postać `00000000-0000-0000-0000-<MAC>`, MTU danych 20. RSSI ze Shiny ma −127 („brak
  wartości”) w ok. 1/3 pakietów (`scan` 2026-11-14: 5 z 15, 5 z 15, 4 z 10, 1 z 3 – przyczyny nie badano), więc `SeenSensor` liczy
  tylko ważne próbki (`SignalStats`), a nie ostatnią wartość. Surowy WinRT podaje ważny RSSI w każdym pakiecie (patrz „Zasięg Cisy”).
- W zasięgu są inne urządzenia Tuya (DC:23:51:*): część to własne czujniki użytkownika wciąż sparowane ze Smart Life
  (`Cisy` było wśród nich), reszta może być cudza. Łączymy się wyłącznie z czujnikami z konfiguracji albo (przy parowaniu) z jedynym
  niesparowanym kandydatem znanego typu.

**Nie sprawdzone:** kalibracja wilgotności w glebie, działanie na Raspberry Pi, dzienny limit Gmaila i zachowanie przy błędnym haśle (wysyłka przez Gmail i wygląd maili na telefonie sprawdzone – 2026-11-13), czy
niesparowany czujnik po minięciu 20 s okna milczy do następnego restartu (resetu), ile rekordów czujnik w ogóle mieści oraz
działanie poprawionego monitora (`run`) na żywo.

**Zaległe pomiary (sprawdzone 2026-11-13, `glebosaurus listen`, Cyprysek; zapis w `local/listen-cyprysek.log`)**
- **Czujnik trzyma historię i oddaje ją przy połączeniu**, w ramkach `0x8003` (DP ze znacznikiem czasu czujnika), **od
  najstarszego**, jedna ramka na ok. 1,1 s. Po uwierzytelnieniu czujnik dwa razy prosi o czas (`0x8011`, odpowiadamy), a rekordy
  zaczynają płynąć po ok. 8–10 s.
- Rekord powstaje **co godzinę** (14:13:47, 15:13:47) i **przy każdym połączeniu** (ostatnia ramka serii ma czas połączenia, czyli
  to jest „stan bieżący”). Zdarzają się rekordy częściowe (13:13:23: tylko DP15 i DP14).
- **Po pełnym odebraniu serii (z naszym potwierdzeniem każdej ramki) czujnik kasuje rekordy**: drugie połączenie 6 min później
  dało już tylko jeden, nowy rekord. Przerwanie sesji po pierwszej ramce niczego nie kasuje – 13:08:44 wróciło po godzinach.
- **Błąd monitora (stan sprzed poprawki):** sesja kończyła się na pierwszym kompletnym zestawie DP3/5/15, czyli na
  **najstarszym** rekordzie historii, a nie na bieżącym. Monitor o 14:15 zapisał jako „teraz” 18%, 27,0 °C, 37% (rekord z
  13:08:44), choć czujnik w glebie mierzył wtedy 79%, 16,0 °C, 30%. Skutek: fałszywe „czas podlać” i nieaktualne raporty.
- **Zegar czujnika po utracie zasilania (Cisy, 2026-11-13, po wymianie baterii; `local/listen-cisy.log`):** czujnik liczy czas od
  nowa, więc rekordy po restarcie mają godziny z przeszłości. `listen` o 15:48:50 dostał kolejno: 13:54:55, 14:16:55,
  15:14:52 (w ziemi, 61%, 10,2 °C – prawdziwy, sprzed wymiany), 15:14:06 i 15:24:42 (już po wymianie, 16%, 16,0 °C, bateria
  56% – czas spóźniony o ok. 24 min, restart faktycznie ok. 15:38). Czasy nie rosną monotonicznie, a **ostatni rekord w
  kolejności odbioru jest zawsze stanem z połączenia** – tylko tej kolejności można ufać. Nawet rekord z chwili połączenia miał
  zły czas, więc odpowiedź na prośby o czas (`0x8011`) go nie poprawiła; czy poprawia zegar na przyszłość, sprawdzi następny
  odczyt Cisy (jeśli tak, kolejne rekordy będą miały dobre godziny).
- **Poprawka (2026-11-13; testy i odczyt `listen` na obu czujnikach, ale poprawiony `run` na żywo jeszcze nie sprawdzony):**
  sesja czyta serię do ciszy 3 s (limit sesji 3 min, po nim zwraca to, co odebrała) i nie scala punktów o tym samym numerze.
  `SoilSensorProfiles.MapAll` składa rekordy w **kolejności odbioru** (zwykły raport bez czasu idzie na koniec), pomija rekordy
  bez wilgotności i temperatury (np. same DP15) i, gdy czas ostatniego rekordu odbiega od chwili połączenia o więcej niż 5 min,
  przesuwa o tę różnicę końcówkę serii od ostatniego cofnięcia czasu (restart Cisy wypada wtedy o 15:38:14). Silnik zapisuje
  zaległe rekordy z czasem czujnika (klucz (czujnik, czas) odrzuca duplikaty, rekord z przyszłości jest pomijany), a najnowszy
  z chwilą odbioru – od tego zależy rytm odczytów. Połączenie trwa ok. 14–17 s plus ok. 1,1 s na każdy zaległy rekord.
- Dwa wiersze z 14:15–14:17 w bazie (Cyprysek 18%/27,0 °C, Cisy 15%/17,7 °C) pochodzą sprzed poprawki. Cyprysek na pewno
  jest najstarszym rekordem historii (z 13:08:44), nie stanem z tej chwili. U Cisy rekordy z 13:54:55 i 14:16:55 mają te same
  wartości, więc ten wiersz jest zgodny z rzeczywistością.

**Zasięg Cisy (sprawdzone 2026-11-13, komputer `bestia5`: adapter Intel Wireless Bluetooth, sterownik 24.70.0.4, kilka innych
sparowanych urządzeń BLE/audio (myszy, klawiatura, słuchawki); pomiar surowym WinRT `BluetoothLEAdvertisementWatcher`, jednorazowym narzędziem poza repozytorium)**
- Adapter podaje **ważny RSSI w każdym pakiecie** (reklama i odpowiedź na skan). Zasięg da się więc mierzyć; −127 pojawia się
  dopiero w Shiny (ok. 1/3 pakietów), dlatego `scan` i monitor liczą średnią z ważnych próbek.
- Cisy obok komputera: ok. −52 dBm. Na skrzynce z ziemią (ok. 3 m), **leżący w powietrzu**: −92…−98 dBm (średnio −94), 9
  pakietów w 60 s, `read` nie znalazł go w reklamach przez 45 s. **Wbity w ziemię** w tej samej skrzynce kilka minut później:
  −78…−83 dBm (średnio −81) przez 4,5 min (59 pakietów, najdłuższa przerwa 27 s), `read` udany (ok. 11 s, bateria 56%). Cyprysek
  w tym czasie stałe ok. −77 dBm.
- **Wniosek: ziemia nie jest przyczyną.** Sygnał zależy od ułożenia i miejsca czujnika (różnica ok. 14 dB między dwoma pozycjami
  w tej samej skrzynce), a margines jest cienki: odczyty udawały się przy −75…−81 dBm (kilka prób), a przy −94 dBm (blisko czułości
  odbiornika) czujnika nie było w reklamach. Wcześniejszej utraty zasięgu w ziemi nie odtworzono; w tamtej próbie baterie miały wg DP15 21% (po wymianie 56%).
- Adapter dostarcza pakiety seriami: odstępy między odebranymi reklamami sięgają 27 s (czujnik w seriach nadaje co ok. 2 s),
  a okno skanu monitora (`scanWindowSeconds`) miało domyślnie 30 s – niewiele zapasu przy słabym sygnale. Od 2026-11-14
  domyślnie 60 s, a znaleziony czujnik jest czytany od razu (zasada 8), więc dłuższe okno kosztuje tylko przy czujniku poza zasięgiem.
- Odczyt w ziemi dał 16% i 16 °C, czyli tyle co czujnik w pokoju w powietrzu (suchy substrat o temperaturze pokojowej albo
  sonda nie w ziemi) – do sprawdzenia przed kalibracją wilgotności.
- **Zapis sygnału (2026-11-14, w kodzie i testach; sprawdzone na żywo `scan` i `run`).** `scan` pokazuje dla każdego urządzenia
  średnią, zakres i liczbę próbek (z osobną liczbą próbek bez wartości). Monitor dopisuje do dziennika „sygnał -77 dBm”
  przy każdym odczycie i przy błędzie połączenia, a przy „nie znaleziono w reklamach” podaje ostatni usłyszany poziom i jak
  dawno (`ostatnio słyszany: -90 dBm, 10 min temu`; po restarcie „od startu monitora jeszcze nie słyszany”). Poziom pochodzi z
  jednego–dwóch pakietów sprzed połączenia (skan czeka na pierwszą próbkę z ważnym RSSI), więc to wskazówka, nie pomiar;
  do dokładnych pomiarów służy surowy WinRT. Sygnał nie trafia do bazy ani do maili (do rozważenia: kolumna w `readings` i
  trend w raporcie). Próba `run` 2026-11-14: Cyprysek -75 dBm, Cisy -77 dBm, oba odczyty udane.

### Usługa Windows (2026-11-15)

- **Sprawdzone 2026-11-15 na bestia5** (Windows 11 Pro 26300, konto Microsoft w grupie Administratorzy, bez podniesienia
uprawnień): zadanie z wyzwalaczem przy logowaniu **własnego** użytkownika rejestruje się bez administratora (wyzwalacz „dowolny
użytkownik” – odmowa dostępu); powtórka co godzinę zapisuje się jako `PT1H` bez końca; usługa i monitor nie mają okien
(`MainWindowHandle` 0, bez procesów Windows Terminal), choć domyślny terminal to „niech Windows zdecyduje”; skrypt uruchomiony
przez usługę, która zaraz potem się kończy, działa dalej, a `Start-ScheduledTask` z tego skryptu uruchamia nową kopię zadania
(rodzicem nowej usługi jest Harmonogram – zapasowy start wprost nie był potrzebny); reguły Zapory panelu są per ścieżka
programu (były dwie: `bin\Release\…` i `publish\win\…`). Paczka samodzielna win-x64: ok. 54 MB zip, 142 MB po rozpakowaniu
(największy jest `Microsoft.Windows.SDK.NET.dll`, 25 MB – projekcja WinRT).
- **Instalacja z aplikacji z pakietu (2026-11-15, bestia5).** Pierwsza prawdziwa instalacja, uruchomiona z terminala
aplikacji Claude (pakiet MSIX `Claude_…`), trafiła w całości do `%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Local\Glebosaurus`:
procesy aplikacji widziały ją pod zwykłą ścieżką, ale Harmonogram nie – zadanie kończyło się 0x80070002 (brak pliku), a
nawet `cmd.exe` z tym katalogiem roboczym 0x8007010B. `GetCurrentPackageFullName` w tych procesach nie zwraca pakietu, więc
skrypt wykrywa sam objaw: zapisuje plik próbny w katalogu nadrzędnym instalacji i szuka go w `Packages\*\LocalCache\Local\…`
– jeśli tam jest, przerywa z wyjaśnieniem (ok. 60 ms). `%LOCALAPPDATA%\Temp` nie jest przekierowywany (stąd udane próby w
katalogu tymczasowym). Prawdziwą instalację wykonało tymczasowe zadanie Harmonogramu (procesy z Harmonogramu działają poza
pakietem), z przejęciem danych z kopii pakietu; kopię potem usunięto.
- **Zatrzymanie przed podmianą dotyczy tylko własnej instalacji**: skrypt zatrzymuje usługę i monitor, których plik programu
leży w jego `app\` (po ścieżce procesu). Muteksy są wspólne dla sesji, więc wcześniej instalacja próbna w innym katalogu
zatrzymała prawdziwą usługę (2026-11-15, czysto; usługa uruchomiona ponownie po ok. 30 s), a każda instalacja zatrzymałaby
też deweloperskie `run`. Druga instalacja nie wystartuje obok działającej (jeden monitor na konto) – skrypt to zgłasza.

### Projekty Supabase (2026-11-16)

**Projekt założony 2026-11-16** (użytkownik; dane w `local/cloud-admin.json`): Central EU (Frankfurt), Postgres 17.12,
Vault (`supabase_vault` 0.3.2), automatyczne RLS na nowych tabelach (wyzwalacz zdarzeń `ensure_rls`). Sprawdzone z
bestia5 tego dnia: klucz publikowalny przyjmowany przez Data API (nieistniejąca funkcja → 404 PGRST202, zmyślony klucz
→ 401), a opis schematu dla niego zamknięty (401); klucz tajny czyta schemat i Storage; hasło bazy działa przez pooler
sesji `aws-1-eu-central-1.pooler.supabase.com:5432` (użytkownik `postgres.<id projektu>`) – `aws-0` nie zna projektu,
a połączenie bezpośrednie `db.<id projektu>.supabase.co` ma tylko adres IPv6, którego bestia5 nie rozwiązuje.
Próbny projekt `glebosaurus-test` (dane w `local/cloud-admin.test.json`) założony i sprawdzony tego samego dnia z tym
samym wynikiem (region, wersje, automatyczne RLS, zachowanie kluczy, pooler `aws-1`).

### Agent Android

- **Sprawdzone 2026-11-18 (bez telefonu):** testy części w Core (odnośnik rejestracji, starty skanu, złożenie reklam z
  prawdziwej reklamy czujnika, dziennik, ekran, tryb tylko-zbieracz z pełnym biegiem `CloudRuntime`), budowanie APK
  bez ostrzeżeń, podpis kluczem wydań (`apksigner`), manifest (uprawnienia, minSdk 29, targetSdk 36, `libe_sqlite3.so` w
  APK), kod QR z `cloud agent --qr` na próbnym projekcie.
- **Sprawdzone 2026-11-19 na telefonie** (Xiaomi 11 Lite 5G NE, model 2109119DG „lisa”, Android 14, HyperOS 1.0;
  instalacja przez `adb` – HyperOS wymaga „Instaluj przez USB” w opcjach programisty i potwierdzenia na telefonie):
  - **Rejestracja kodem QR** skanerem Usług Google Play – najpierw próbny projekt (agent `telefon-proba`), potem na
    prośbę użytkownika prawdziwy (o 10:45 agent `mi11`, tylko zbieracz; nowy token zastąpił próbny, kopia
    konfiguracji z innego projektu skasowana). Token i kopia konfiguracji w Keystore, konfiguracja z chmury (9
    czujników), kopia odczytów wszystkich agentów (1305), usługa pierwszoplanowa `connectedDevice` i blokada uśpienia
    (`dumpsys`). Uprawnienia, wyjątek z optymalizacji baterii i Autostart ustawione przyciskami z ekranu.
  - **Skan:** przy bestia5 telefon słyszy wszystkie 9 czujników (−72…−89 dBm średnio; Zioła −72, Cyprysek −74, Hortensje
    −81, Trawa −81, Laurowiśnia szafka −86, Wiszący wejście −86, Laurowiśnia wejście −87, Cisy −88, Alpinarium −89) –
    moc w **każdym** pakiecie i ok. 20 pakietów na czujnik w 30 s (Windows z Shiny: −127 w co trzecim pakiecie i
    pakiety co kilka–kilkanaście sekund). Wśród 10 urządzeń Tuya także porzucony CB:F7:7D (−91 dBm).
  - **Odczyty:** telefon wziął dzierżawy tylko na czujniki bez właściciela (Alpinarium, Laurowiśnia szafka, Laurowiśnia
    wejście – 10:48:59; Hortensje niskie – 11:26) i przeczytał je: Laurowiśnia szafka 12 s (−83 dBm), **Laurowiśnia
    wejście przy −93 dBm** (pierwsza próba – rozłączenie ze statusem GATT 62, druga po 1,5 s udana; bestia5 przeczytała
    ten czujnik od 11-14 raz), Alpinarium (−89 dBm, z zaległą historią), Hortensje 7 s (−82 dBm). Odczyty w chmurze
    przy najbliższej synchronizacji. Pozostałe czujniki zostały u bestia5 – bez kolizji.
  - **Zgaszony ekran:** nasłuch co 3 min z filtrem po MAC działa – skany 30 s przy zgaszonym ekranie dały 166 i 164
    wyniki, tyle co przy włączonym (156–176; `dumpsys bluetooth_manager`), liczniki czasu nie stają.
  - **Restart telefonu:** agent startuje sam (`BOOT_COMPLETED`, 15 s po odblokowaniu), ale **dopiero po pierwszym
    odblokowaniu PIN-em** – wcześniej system wysyła tylko `LOCKED_BOOT_COMPLETED`, a dane aplikacji (token, baza) są
    zaszyfrowane do odblokowania. Telefon z blokadą ekranu po restarcie (aktualizacja systemu, rozładowanie) czeka więc
    na człowieka; po 30 min ciszy nadawca wyśle „agent milczy”. Do wyboru: telefon bez blokady ekranu albo praca w
    Direct Boot (dane w pamięci urządzenia, nie użytkownika – zmiana projektu, nie zrobiona).
  - **Aktualizacja na działającej aplikacji** (`adb install -r`, ten sam klucz): `MY_PACKAGE_REPLACED`, agent wrócił sam
    w ok. 5 s, rejestracja i token zostały.
  - Poprawione po próbie: podwójny tytuł (pasek akcji motywu), białe ikony paska stanu na jasnym tle, komunikat po
    akcji wiszący bez końca (znika po minucie).
  - **Niesprawdzone:** noc i tydzień pracy, docelowe miejsce telefonu, błąd 133 (był 62), dłuższe zachowanie HyperOS
    wobec aplikacji w tle, zadanie `android` w CI i APK w wydaniu (do pierwszego wypchnięcia).

### Rzadsze odpytywanie – jak sprawdzić (2026-11-20)

Po wdrożeniu (usługi bestia5 i rasp3 z wydania – przy starcie albo o 03:30; APK 0.2.7 na mi11 od 2026-11-20 15:36).
Punkt odniesienia jest w chmurze wstecz: odczyty mają chwilę przyjęcia (`received_at`) i agenta od 2026-11-17.

**Połączenia, opóźnienia i przejęcia – z odczytów w chmurze** (edytor SQL Supabase, samo czytanie). Jedno udane
połączenie to jedna paczka odczytów czujnika z tą samą chwilą przyjęcia: zaległe rekordy i stan bieżący idą w jednej
synchronizacji (po przerwie sieci kilka połączeń może się zlać w jedno). Nieudane próby nie zostawiają odczytów – te są w
logu administratora.

```sql
with polaczenia as (
    select distinct sensor_id, received_at, agent_id
      from glebosaurus.readings
     where received_at > now() - interval '14 days'
), z_poprzednim as (
    select *, lag(received_at) over w as poprzednie, lag(agent_id) over w as poprzedni_agent
      from polaczenia window w as (partition by sensor_id order by received_at)
)
select (received_at at time zone 'Europe/Warsaw')::date as dzien,
       count(*) as polaczenia,
       round(count(*)::numeric / count(distinct sensor_id), 1) as na_czujnik,
       count(*) filter (where received_at - poprzednie < interval '115 minutes') as przed_odstepem,
       count(*) filter (where received_at - poprzednie > interval '140 minutes') as opoznione,
       count(*) filter (where poprzednie is not null and agent_id is distinct from poprzedni_agent) as zmiany_agenta
  from z_poprzednim
 group by 1 order by 1;
```

- `na_czujnik` – przy odstępie 2 h 5 min ok. 11–12 na dobę; więcej to dodatkowe połączenia.
- `przed_odstepem` – połączenia wcześniej niż po odstępie: dosłanie po urwanej sesji, odczyt na żądanie, odczyt zaraz po
  przejęciu. Powinno spaść (sprawdzanie ostatniego odczytu po przejęciu), a na pewno nie rosnąć.
- `opoznione` – przerwa ponad 140 min: porażki, czujnik bez właściciela, przerwa sieci. Nie powinno rosnąć.
- `zmiany_agenta` – kolejny odczyt czujnika zrobił inny agent (przejęcie dzierżawy). Nie powinno rosnąć.

**Telefon (mi11) po kilku dniach:**
- w logu administratora od mi11 co godzinę „Nasłuch za ostatnie … min” – ilu czujników średnio słychać i ile pakietów na
  czujnik; 1–2 dni tych wpisów to punkt odniesienia przed trybem skanu Balanced;
- bateria: dobowy spadek bez ładowarki (albo czas na ładowarce) i udział Glebosaurusa w ustawieniach Androida (Bateria →
  zużycie przez aplikacje) w porównaniu z dniami przed wdrożeniem; temperatura akumulatora w raporcie (sekcja
  „Urządzenia”);
- powiadomienie „Czyta N z 9 czujników · chmura OK” – godzina tylko przy kłopocie z chmurą;
- żadnych maili „agent milczy” o mi11; w dzienniku wpisy „czujnik przejęty – w chmurze jest odczyt sprzed …” to
  zaoszczędzone połączenia.

**Chmura:** w panelu Supabase (Usage) liczba zapytań API po kilku dniach – synchronizacje ok. 960 na agenta na dobę
zamiast 1440, przeliczanie alertów u nadawcy ok. 860 zamiast 4320, telefon bez ok. 1440 zapytań `alert_list`.

## Przebieg faz 1b i 2 – stan zadań na 2026-11-19

Tabele z planu prac sprzed usunięcia trybu samodzielnego, ze szczegółami i datami (aktualny plan: [ROADMAP.md](ROADMAP.md)).

### Faza 1b – domknięcie lokalnego monitora

Cel: monitor pracuje w tle bez nadzoru i wysyła prawdziwe maile. Szczegóły zrobionych zadań i daty sprawdzeń:
ARCHITEKTURA.md („Stan wdrożenia”, zasady 7–11).

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Prawdziwy mail.** Konto Gmail z hasłem aplikacji, szablony pod telefon, konfigurowalni odbiorcy alertów i raportów, `mail-preview`, `mail-test --samples`. | ✔ 2026-11-13…16: wygląd zatwierdzony na telefonie, prawdziwe maile próbne, alert (11-14) i raport z harmonogramu (11-16). ○ Dzienny limit Gmaila, zachowanie przy błędnym haśle. | S |
| 2 | **Raspberry Pi** | → przeniesione do 2.3.1 (decyzja użytkownika 2026-11-16). | – |
| 3 | **Windows w tle.** Zadanie Harmonogramu przy zalogowaniu, bez okna, skrypt instalacji, aktualizacje, zachowanie po uśpieniu i utracie adaptera BLE. | ✔ 2026-11-15 (zasada 7): usługa `glebosaurus-service`, `install-windows.ps1`, samoaktualizacja z GitHuba, jedna instancja, `stop`; na żywo na bestia5. ○ Uśpienie i wybudzenie, utrata adaptera BLE, wylogowanie w trakcie odczytu, 7 dni pracy. | M |
| 4 | **Baterie i interwał.** Kilka dni pracy co 15 min na kilku czujnikach, wykres wskazania baterii w czasie, wybór domyślnego interwału (15/30/60 min). Rozważyć interwał adaptacyjny: częściej blisko progu podlania, rzadziej gdy gleba jest wilgotna i nocą. | ◐ interwał wybrany 2026-11-18: **2 h 5 min** (decyzja użytkownika; czujnik i tak zapisuje rekord co godzinę – ARCHITEKTURA.md, „Czas i bateria”), w chmurze przez `cloud settings` (prawdziwy projekt od 2026-11-18 21:32); dane o baterii zbiera usługa od 2026-11-15 (do 11-18 co 15 min); analiza ○ | S + czas |
| 5 | **Kalibracja wilgotności.** Pomiary: powietrze, woda, mokra i sucha ziemia doniczkowa; kalibracja per czujnik (punkty „sucho”/„mokro” albo offset i skala) w konfiguracji; sensowne progi domyślne i opis w README. Tani czujnik nie podaje procentów porównywalnych między glebami. | ○ | M |
| 6 | **Parowanie pozostałych czujników** (większość widać w skanie jako sparowane ze Smart Life – każdy trzeba zresetować przyciskiem, co usuwa go z aplikacji, i sparować po jednym w oknie 20 s) i procedura kopii zapasowej kluczy (od 2026-11-18 klucze 5 czujników są w Supabase Vault i w codziennej kopii chmury; nowy czujnik po `pair` trafia do chmury przez `cloud import`). | ◐ sparowanych 5; reszta ○ | S |
| 7 | **Operacje.** Log administratora, nasłuch BLE na przemian z odczytem, lokalny panel diagnostyczny, `status --json`, przeładowanie konfiguracji bez restartu (opcjonalnie). | ◐ ✔ 2026-11-13…14: log administratora z retencją i `send-log`, wersja w stopce maili, nasłuch na przemian z odczytem i okno skanu 60 s (zasada 8), panel WWW (zasada 11; na żywo z logowaniem). ○ `status --json`, przeładowanie konfiguracji; skrypt aktualizacji na Pi → 2.3.1. | S |
| 7a | **Panel WWW – dalsze etapy (opcjonalnie).** | ◐ ✔ 2026-11-14: odczyt i raport na żądanie, wykresy (pulpit – 3 doby, strona czujnika – cała historia). ○ „Wyślij log teraz”, przeładowanie konfiguracji między cyklami. Edycja konfiguracji – **odłożona decyzją użytkownika (2026-11-14)**, pełny edytor w 3c. | S–M |
| 7b | **Wykres w raporcie mailowym** (życzenie użytkownika z 2026-11-14): ostatnie 3 doby przy każdym czujniku, sam HTML (Gmail nie pokazuje SVG, grafiki rastrowej użytkownik nie chce). | ✔ 2026-11-14 (`MailChart`; kolumny po 2 godz., temperatura jako blady zakres – po uwagach użytkownika z telefonu). ○ Obejrzenie prawdziwego raportu na telefonie; decyzja przy wielu czujnikach (budżet 90 KB: 2 godz. do ok. 9 czujników, przy 20 – 8 godz.; szersze kolumny albo kilka maili). | S–M |
| 8 | **Higiena repozytorium.** | ◐ ✔ 2026-11-15: GitHub (publiczne od 0.1.0, historia bez sekretów), CI (`ci.yml`), wydania z tagu (`release.yml`). ○ Licencja (bez niej kod jest publiczny, ale „wszelkie prawa zastrzeżone”), analizatory; paczka linux-arm64 w wydaniu → 2.3.1. | S |

**Kryterium ukończenia:** 7 dni ciągłej pracy na Windows bez ingerencji i bez zawieszeń (Pi – w 2.3); co najmniej jeden
prawdziwy alert i jeden raport w skrzynce (✔); udokumentowana prognoza życia baterii; wszystkie czujniki sparowane,
klucze zarchiwizowane.

**Ryzyka:** zasięg BLE (czujniki przy wejściu – „Do zbadania” wyżej), zużycie baterii czujników (4).

---

### Faza 2 – chmura i wiele agentów

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

#### 2.1 – Supabase i pierwszy agent (bestia5)

Dzierżawy czujników i blokada nadawcy powstają od razu, choć agent jest jeden (zawsze je dostaje) – po dojściu Androida
protokół się nie zmienia, a działanie przy kilku agentach sprawdza się w 2.2.

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Przygotowanie (użytkownik – kont Claude nie zakłada):** konto Supabase z logowaniem dwuetapowym, projekt na planie Free we Frankfurcie, klucz publikowalny, osobny klucz tajny (tylko import i migracje, nigdy na agentach) i hasło bazy w `local/cloud-admin.json`; próbny projekt `glebosaurus-test` tak samo (`local/cloud-admin.test.json`); healthchecks.io ze sprawdzeniem nadawcy. Konto Gmail zostaje (już jest osobne). | ✔ 2026-11-16 (zasady 2 i 10): oba projekty sprawdzone z bestia5; sprawdzenie „Glebosaurus – nadawca” (Period 5 min, Grace 25 min) w stanie „New” do pierwszego prawdziwego sygnału. | S |
| 2 | **Schemat i funkcje SQL** jako migracje w `supabase/migrations/`: agenci, czujniki, odczyty, słyszenia, dzierżawy, blokada nadawcy, kolejka maili, sekrety poczty (Vault), wspólny stan alertów, polecenia, dzienne logi agentów; RLS na wszystkim, agenci wołają tylko funkcje `public.*`. | ✔ 2026-11-16 (zasada 13): dwie migracje, 53 testy na `glebosaurus-test`. Prawdziwy projekt bez schematu – dostanie go `cloud migrate` w 2.1.5. | M |
| 3 | **Core:** tryb chmurowy obok niezmienionego `MonitorEngine`, w kawałkach: (a) protokół, klient HTTP, księga dzierżaw i zasady przydziału; (b) bufor odczytów w SQLite i pętla synchronizacji; (c) konfiguracja z chmury z chronioną ostatnią dobrą kopią; (d) zbieracz; (e) nadawca (alerty, raporty, log administratora, „agent milczy”, kolejka maili, strażnik). | ✔ 2026-11-16 (zasada 14), testy na ręcznym czasie i na próbnym projekcie. Niesprawdzone: pełny nasłuch w radiu Shiny na sprzęcie, prawdziwy mail w trybie chmurowym. | L |
| 4 | **Agent:** sekcja `cloud` (z `mail` albo `sensors` naraz – błąd startu), `enroll`, `run` w trybie chmurowym, narzędzia właściciela `cloud migrate`/`import`/`agent`, `cloud backup` (darmowy plan nie robi kopii – zasada 10), panel WWW w trybie chmurowym. | ✔ 2026-11-16 (zasada 15): sekcja `cloud`, chroniony token, `enroll` (CLI na próbnym projekcie), `CloudRuntime` i `run` (testy z atrapą radia), `cloud migrate`/`import`/`agent` (próbny projekt, także CLI), wzór `examples/glebosaurus.cloud.config.json`. ✔ 2026-11-17: panel WWW w trybie chmurowym – kopia odczytów wszystkich agentów (nowa funkcja `readings_feed`, trzecia migracja), kto czyta który czujnik, nadawca, agenci, odczyt i raport na żądanie przez polecenia w chmurze (testy, próbny projekt, przeglądarka na programie podglądu); `cloud backup` – tabele i odszyfrowane sekrety w ZIP bez `pg_dump`, ręcznie i codziennie w `run` (`cloud.backup`; próbny projekt, także CLI); zmiana roli agenta bez restartu (poprawka `CloudConfigSource`) i `cloud agent --revoke` (próbny projekt, także CLI). | M |
| 5 | **Na żywo:** prawdziwy projekt (`cloud migrate`, `cloud import`, `cloud agent`), usługa na bestia5 przechodzi na tryb chmurowy przez wydanie i samoaktualizację (tag tylko na prośbę użytkownika), sekcja `cloud` (z `backup` – kopia pliku administratora bez klucza tajnego w danych usługi) i `enroll` w danych usługi; tydzień pracy. Historii odczytów `cloud import` nie przenosi – wysyła ją agent ze swojej bazy, więc najpierw import, potem agent. | ◐ 2026-11-18 (na prośbę użytkownika): `cloud migrate` i `cloud import` (5 czujników z konfiguracji usługi) na prawdziwym projekcie, wydanie 0.2.0, przejście usługi skryptem uruchomionym przez użytkownika (niżej, „Stan wdrożenia obszarów”); od 18:06 tryb chmurowy: 625 odczytów historii przyjętych bez strat (tyle samo w pierwszej codziennej kopii o 18:16), blokada nadawcy, pierwsze maile przez kolejkę (każdy raz), nasłuch w radiu Shiny słyszy czujniki i bierze dzierżawy, pierwsze odczyty w trybie chmurowym o 18:30 (4 czujniki, w chmurze razem 674 odczyty). Teraz tydzień próby. | S + czas |

**Kryterium:** bestia5 przez tydzień czyta czujniki, wysyła odczyty do Supabase i maile przez blokadę i kolejkę; godzinna
przerwa sieci kończy się dosłaniem bez strat; restarty nie dublują maili; unieważnienie tokenu odcina agenta. Maile wychodzą
tylko wtedy, gdy bestia5 działa – tak jak dziś.

#### 2.2 – agent na Androida (Xiaomi Mi 11 Lite 5G)

Tylko zbieranie, bez roli nadawcy; telefon na Wi-Fi, stale na ładowarce. .NET for Android z tym samym `Glebosaurus.Tuya` i
trybem chmurowym z Core; radio BLE wprost na API Androida (Shiny nie filtruje tam skanu po MAC – decyzja przy implementacji
2026-11-18) – szczegóły i znane ograniczenia Androida/MIUI w ARCHITEKTURA.md, zasada 12; użycie – README, „Android”.

| # | Zadanie | Stan | Wielkość |
|---|---|---|---|
| 1 | **Próba na Mi 11:** instalacja APK, rejestracja kodem QR (najpierw próbny projekt), uprawnienia i ustawienia MIUI, podgląd zasięgu, skan z filtrem po MAC i odczyt jednego czujnika, noc przy zgaszonym ekranie na ładowarce, restart telefonu. Największa niewiadoma fazy 2 – jak kiedyś spike na Windows. Agenta zrobiono przed próbą (2026-11-18), bo telefon był niedostępny; próba sprawdziła agenta od razu. | ◐ 2026-11-19 (ARCHITEKTURA.md, zasada 12): wszystko poza nocą – rejestracja, uprawnienia, skan (także przy zgaszonym ekranie), odczyty 4 czujników, start po restarcie (po odblokowaniu PIN-em) i po aktualizacji. ○ Noc na ładowarce. | S–M |
| 2 | **Agent:** usługa pierwszoplanowa ze stałym powiadomieniem, start po restarcie telefonu, rejestracja kodem QR, synchronizacja, bufor odczytów, ekran stanu i skanu z sygnałem (wybór miejsca na telefon), stan telefonu w sygnale życia (bateria, temperatura, ładowanie, Wi-Fi); instrukcja ustawień MIUI w README. | ◐ 2026-11-18 w kodzie (zasada 12): wszystko z listy; części w Core z testami, APK zbudowany i podpisany; 2026-11-19 na telefonie (3 drobne poprawki ekranu po próbie). ○ Alert mailowy o przegrzaniu (wymaga stanu agentów u nadawcy – nowa funkcja SQL); start bez odblokowania po restarcie (Direct Boot) – jeśli telefon ma mieć blokadę ekranu. | L |
| 3 | **Wydania:** APK budowany w Actions, podpisany jednym kluczem z sekretów repozytorium (kopia klucza – jego utrata oznacza instalację od nowa); aktualizacja przez powiadomienie o nowej wersji albo Obtainium. | ◐ 2026-11-18: klucz wydań w `local/` (kopia poza komputerem – użytkownik), `deploy/build-android.ps1`, zadanie `android` w CI, APK w `release.yml` przy sekretach `ANDROID_KEYSTORE_BASE64`/`ANDROID_KEYSTORE_PASSWORD`. ○ Sekrety w repozytorium (użytkownik albo Claude na prośbę), pierwszy przebieg CI, powiadomienie o nowej wersji. | S |
| 4 | **Praca obok bestia5 na tych samych czujnikach** – prawdziwa próba dzierżaw: przejęcie czujników po wyłączeniu bestia5, oddanie, gdy bestia5 słyszy czujnik lepiej, żadnych równoczesnych połączeń. | ◐ od 2026-11-19 10:45: telefon wziął 4 czujniki bez właściciela, reszta przy bestia5, bez kolizji. ○ Tydzień, wyłączenie bestia5, oddawanie przy lepszym sygnale. | S + czas |

**Kryterium:** telefon tydzień pracuje bez dotykania; zero konfliktów z bestia5. Luka do 2.3: gdy bestia5 jest wyłączona,
Android zbiera odczyty, ale maile nie wychodzą (po powrocie bestia5 alerty są liczone na nowo, a raport nadrobiony w granicach
`catchUpHours`).
