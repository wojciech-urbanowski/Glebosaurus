# Glebosaurus – architektura

Projekt zaakceptowany 2026-11-13; chmura i wielu agentów (faza 2) przeprojektowane z użytkownikiem 2026-11-16 (zasady 1–4,
10, 12). Plan dalszych prac: [ROADMAP.md](ROADMAP.md). Użycie: [../README.md](../README.md).

Cel: bezobsługowa aplikacja .NET 10 (Windows 11, Raspberry Pi OS 64-bit, Android), która po cichu zbiera dane z kilkunastu
czujników gleby Tuya BLE (wilgotność, temperatura gleby, bateria), zapisuje je lokalnie i w chmurze, wysyła alerty o
podlewaniu i raporty o stałych porach mailem (Gmail), a konfigurację, alerty i raporty udostępnia w przeglądarce.

## Stan wdrożenia

| Obszar | Stan |
|---|---|
| Protokół Tuya BLE, lokalne parowanie, sesja, odczyt DP | zrobione, sprawdzone na sprzęcie |
| Monitor: odczyty, SQLite, alerty, raporty, mail (szablony) | zrobione, sprawdzone na żywo na Windows |
| Wysyłka prawdziwego maila przez Gmail | zrobione: 2026-11-13 `mail-test --samples` wysłał 3 maile (próbny, alert, raport) z konta nadawcy na skrzynkę użytkownika, SMTP przyjął wszystkie (logowanie hasłem aplikacji działa); wygląd i kolorystyka obejrzane i zatwierdzone przez użytkownika na telefonie; 2026-11-14 `run` sam otworzył prawdziwy alert (słaba bateria Cypryska, 19% przy progu 20%) i wysłał o nim mail – SMTP przyjął (18:06); **niesprawdzone**: dzienny limit, zachowanie przy błędnym haśle, prawdziwy raport z `run` |
| Log techniczny dla administratora (dziennik dzienny, retencja, mail z załącznikiem) | zrobione w kodzie i testach (2026-11-13), podgląd obejrzany w widoku telefonu; 2026-11-13 `send-log` wysłał prawdziwy dziennik z załącznikiem na skrzynkę administratora, Gmail przyjął; 2026-11-14 `run` w pierwszym przebiegu dnia sam wysłał dziennik z 13.11 (ok. 3 s po starcie), a restart monitora kilka minut później go nie powtórzył (znacznik w bazie); **niesprawdzone**: wygląd tego maila na telefonie, czyszczenie starych plików przez `FileLoggerProvider` na żywo |
| Raspberry Pi (BlueZ przez Shiny) | paczka zbudowana, **niesprawdzone** |
| Praca w tle na Windows (zadanie Harmonogramu), instalacja i samoaktualizacja z wydań GitHuba | zrobione 2026-11-15 (zasada 7): usługa `glebosaurus-service` bez okna, skrypt `install-windows.ps1`, workflowy CI i wydania; testy logiki w Core. **2026-11-15 na żywo (Windows, katalog próbny, udawane API wydań na loopbacku, monitor z atrapą czujnika):** instalacja z przejęciem konfiguracji, `stop` (1 s), aktualizacja o codziennej porze (monitor zatrzymany czysto, skrypt w tle, nowa usługa wystartowana przez Harmonogram), aktualizacja przy starcie (po wymuszonym powrocie do starszej wersji), zepsute wydanie (zły skrót – stara wersja wraca, próba odłożona o dobę, bez pętli), blokada drugiego monitora (kod 4), odinstalowanie. **2026-11-15 wydanie 0.1.0** opublikowane przez `release.yml` (testy, paczka, próba instalacji w Windows PowerShell 5.1 na runnerze – wszystko zielone; API wydań odpowiada bez logowania) i zainstalowane na bestia5 skryptem pobranym z wydania, z przejęciem `local/` i bez startu. Próba na sucho przejęcia prawdziwych danych wykryła, że `ConvertFrom-Json` z PowerShell 5.1 nie zna komentarzy `//`, a jego komunikat błędu zawiera cały plik z hasłem Gmaila – skrypt czyta teraz konfigurację przez `Read-ConfigJson` i nie wypisuje treści wyjątku. **Na żywo z prawdziwymi danymi:** 2026-11-15 23:56 usługa wystartowana przez Harmonogram (panel WWW ze stałej ścieżki – reguła Zapory zatwierdzona raz; o północy sama wysłała log administratora), a 2026-11-16 00:06 pierwsza samoaktualizacja z GitHuba 0.1.0 → 0.1.1 przy starcie usługi (ok. 15 s: sprawdzenie wydania, skrypt w tle, podmiana `app\`, nowa usługa z Harmonogramu; log administratora się nie powtórzył, Zapora nie pytała ponownie). **Niesprawdzone**: strażnik co godzinę, aktualizacja o 03:30 na prawdziwym wydaniu, zachowanie po uśpieniu i wybudzeniu, wylogowanie w trakcie odczytu, 7 dni ciągłej pracy |
| Lokalny panel diagnostyczny WWW (HTTPS, sieć lokalna, hasło; podgląd, odczyt i raport na żądanie) | zrobione w kodzie i testach (2026-11-14, w tym testy na prawdziwym Kestrelu z TLS); `web-password` uruchomione na żywo, a strona obejrzana na prawdziwych danych (kopia bazy, dzienniki, konfiguracja) przez program podglądu na loopbacku, także w widoku telefonu 375 px. **2026-11-14 na żywo (Windows, `run` z prawdziwą konfiguracją):** panel startuje z monitorem, odcisk certyfikatu z dziennika zgadza się z `web-password` i z serwerem (127.0.0.1 i adres LAN), bez logowania 302/401; użytkownik zalogował się w przeglądarce (połączenie przyszło z adresu IPv6 link-local – filtr sieci go przepuścił). Próba wykryła błąd: przy `Referrer-Policy: no-referrer` przeglądarka wysyła z formularza `Origin: null`, a filtr POST odrzucał je jako obcą stronę (403, bez wpisu w dzienniku) – teraz rozstrzyga `Sec-Fetch-Site`, polityka to `same-origin`, odrzucenie trafia do dziennika (testy odtwarzają nagłówki przeglądarki). Wykresy (pulpit: 3 doby; strona czujnika: cała historia) – 2026-11-14 w testach i w przeglądarce na programie podglądu z danymi próbnymi (zasada 11). **Niesprawdzone**: wejście z telefonu w sieci domowej (reguła Zapory), wykresy na prawdziwym monitorze, Raspberry Pi |
| Chmura Supabase, wielu agentów (dzierżawy czujników, blokada nadawcy), agent Android, interfejs WWW | zaprojektowane (faza 2 uzgodniona z użytkownikiem 2026-11-16, zasady 1–4, 10, 12); 2026-11-16 projekty Supabase (prawdziwy i próbny) założone, a rdzeń schematu i funkcji SQL zrobiony i sprawdzony testami na próbnym projekcie (zasada 13); prawdziwy projekt jeszcze bez schematu; agenci, Android i interfejs – niezrobione |

## Zweryfikowane na sprzęcie (ATLO SMS1, Tuya `gvygg3m8`)

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
  częstotliwość odczytów decyduje o baterii. Domyślnie 15 min; docelowy odstęp po pomiarach przez kilka dni.

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

## Komponenty

Dziś (faza 1) działa jeden samodzielny monitor: BLE → Tuya → odczyty → SQLite, reguły alertów, raporty, Gmail SMTP
(MailKit), lokalny panel WWW. Tryb samodzielny zostaje. Docelowo (faza 2, uzgodniona 2026-11-16, zasady 1–4, 10 i 12):

```
 Czujniki Tuya BLE (~12)
        │ BLE: GATT 0x1910 (notify 0x2B10, write 0x2B11), AES, klucz lokalny
        │ łączy się tylko agent z dzierżawą czujnika; słuchać mogą wszyscy
 ┌──────┴────────────┐ ┌───────────────────┐ ┌──────────────────────┐
 │ bestia5 (Win11)   │ │ Android (Mi 11)   │ │ Raspberry Pi 24/7    │  agenci .NET 10, bufor SQLite
 │ zbieranie         │ │ zbieranie         │ │ zbieranie            │  panel lokalny (Win/Pi)
 │ nadawca (prior. 2)│ │ bez maili         │ │ nadawca (prior. 1)   │
 └──────┬────────────┘ └─────────┬─────────┘ └──────────┬───────────┘
        └─── HTTPS, token agenta ┴ funkcje SQL ─────────┘  tylko ruch wychodzący
 ┌──────┴───────────────────────────────────────────────────────────┐
 │ SUPABASE (darmowy plan, region UE) – funkcje SQL, RLS            │
 │ konfiguracja, odczyty, dzierżawy, blokada nadawcy, kolejka maili │
 │ Vault: klucze Tuya, hasło Gmaila · dzienne logi agentów (gzip)   │
 └──────┬───────────────────────────────────────────────────────────┘
        │ HTTPS, Supabase Auth – tylko konto właściciela                (faza 3)
 ┌──────┴──────────────────────────┐
 │ PRZEGLĄDARKA (strona statyczna) │
 └─────────────────────────────────┘
 Poczta: nadawca z blokadą → Gmail SMTP (MailKit). Strażnik: nadawca → healthchecks.io.
```

## Zasady projektowe

1. **Konfiguracja i sekrety w Supabase** (decyzje użytkownika 2026-11-16; zastępuje wcześniejszą zasadę „chmura nie zna
   sekretów” z zaszyfrowaną paczką konfiguracji). Supabase trzyma całą konfigurację – czujniki (nazwy, MAC, progi,
   kalibracja), harmonogramy raportów, odbiorców, role agentów – a w Vault (szyfrowanie kluczem trzymanym poza bazą) klucze
   Tuya i hasło aplikacji Gmail. Na urządzeniu zostaje tylko adres chmury, własny token agenta i ustawienia lokalnego
   panelu; konfiguracja z chmury ma lokalnie ostatnią dobrą kopię. Każdy agent zbiera odczyty; reguły alertów, raporty i
   maile robi agent, który trzyma blokadę nadawcy (zasada 2). Klucze Tuya dostaje każdy zarejestrowany agent (każdy może
   przejąć każdy czujnik) i trzyma ich lokalną kopię (DPAPI / Android Keystore / plik 600) do pracy bez sieci; `pair` wysyła
   nowe klucze od razu do Vault. Hasło Gmaila dostaje tylko obecny nadawca (`mail_credentials`, ta sama kontrola epoki co
   `mail_claim`) i trzyma je wyłącznie w pamięci, nigdy na dysku. Konsekwencje: konto Supabase jest kluczem do wszystkiego
   (logowanie dwuetapowe; klucz tajny `sb_secret_…` nigdy na agentach – tylko do importu i migracji, w
   `local/cloud-admin.json`); hasło aplikacji Gmaila daje pełny dostęp do skrzynki (też IMAP), więc nadawcą jest osobne
   konto Gmail tylko do takich zastosowań (obecne konto nadawcy już takie jest – użytkownik, 2026-11-16).
   Tryb samodzielny (bez sekcji `cloud`) działa jak dotąd z plikiem konfiguracji i własną pocztą.
2. **Koordynacja wielu agentów** (2026-11-16). Czujnik przyjmuje jedno połączenie naraz, a kilka agentów może go słyszeć;
   każdy mail ma wyjść raz. Jedna funkcja SQL `lease_claim(zasób, agent, ttl)` atomowo daje dzierżawę, gdy zasób jest
   wolny, wygasł (plus 2 min zapasu) albo już należy do pytającego, i zwraca numer epoki oraz czas ważności;
   `lease_release` ją oddaje. Czas liczy baza (`now()`), a agent liczy ważność na własnym zegarze monotonicznym od chwili
   **wysłania** prośby – wychodzi mu krócej niż bazie, więc rozjechane zegary nie szkodzą.
   - **Dzierżawa czujnika: 30 min** (decyzja użytkownika), odnawiana przy każdej synchronizacji (ok. 60 s). Bez dzierżawy
     agent się nie łączy; agent nie zaczyna odczytu, gdy zostały mniej niż 2 min. Bez sieci agent czyta swoje czujniki do
     końca dzierżawy i przestaje, a baza oddaje czujnik innemu dopiero po wygaśnięciu i zapasie – przy podziale sieci nie
     ma dwóch właścicieli naraz. Koszt: nagłe zniknięcie agenta to ok. 32 min bez odczytów jego czujników. Zasady przydziału
     (Core, testy na `TimeProvider`): agent ubiega się tylko o czujnik, który słyszy; agent ze słabszym sygnałem czeka
     dłużej, zanim weźmie wolny czujnik; właściciel oddaje czujnik po 3 porażkach z rzędu albo gdy inny agent słyszy go
     dłuższy czas co najmniej o 6 dB lepiej (histereza – bez przerzucania); przekazanie planowe (np. `stop`): właściciel
     kończy trwający odczyt i oddaje (sekundy, nie 30 min). Słyszenia (RSSI, kiedy, wyniki prób) to jeden wiersz na parę
     agent–czujnik, nadpisywany – bez historii. Odczyt na żądanie trafia do właściciela; `pair` bierze dzierżawę na swój
     identyfikator czujnika.
   - **Blokada nadawcy „nadaję teraz”** (zasób `notifier`; decyzja użytkownika: nadawców może być wielu): 5 min, odnawiana
     co ok. 60 s, jedna na całą pocztę (alerty, raporty, log administratora – wspólny stan alertów). Kandydaci mają w
     tabeli agentów `can_send_mail` i `sender_priority` (Pi 1, bestia5 2; Android nie jest kandydatem). Przy wolnej blokadzie
     kandydat z priorytetem N czeka N × 1 min, zanim po nią sięgnie; gdy wraca ważniejszy kandydat (widać go po sygnale
     życia), obecny nadawca oddaje blokadę dobrowolnie w bezpiecznej chwili (między cyklami, nic w trakcie wysyłki). Po
     nagłym zniknięciu nadawcy przejęcie po ok. 7 min. Bez blokady agent w trybie chmurowym nie wysyła żadnego maila –
     także przy niedostępnym Supabase.
   - **Kolejka maili bez duplikatów** – trzy niezależne zabezpieczenia: (1) blokada z epoką – dwóch nadawców nie pracuje
     naraz; (2) `mail_claim(klucz, agent, epoka)` udaje się tylko, gdy według zegara bazy agent wciąż trzyma blokadę z tą
     epoką – np. bestia5 uśpiona z blokadą po przebudzeniu już nic nie wyśle; (3) unikalny klucz maila (`insert … on
     conflict do nothing` – „tak” tylko raz w historii): `report|2026-11-17T07:00`, `alert|<czujnik>|<rodzaj>|<początek
     epizodu>`, `log|<dzień>`, `agent-silent|<agent>|<od>`, `report-request|<id zlecenia>`. Potem SMTP i `mail_sent`. Mail
     zarezerwowany przez nadawcę, który zamarł przed SMTP, nie jest ponawiany przez innego – najgorzej opóźnienie albo
     utrata jednego maila (ślad w logu administratora), nigdy duplikat. Message-ID wyliczany z klucza. Wspólny stan w
     Supabase (otwarte alerty unikalne dla czujnika i rodzaju, klucze kolejki, znacznik logu), więc nowy nadawca zaczyna
     tam, gdzie skończył poprzedni; raport z przerwy nadrabia `catchUpHours`. W trybie chmurowym zastępuje to lokalne
     `report_runs` i `last_notified_at`.
   - **Strażnik:** sygnał do healthchecks.io (darmowy) wysyła tylko obecny nadawca i tylko, gdy wszystko działa (blokada,
     Supabase, SMTP); cisza ponad 30 min → mail od healthchecks.io (padł nadawca, nie ma sieci, Supabase uśpiony lub
     niedostępny). Jeden strażnik niezależnie od liczby kandydatów. Sygnał co 5 min (limit serwisu: 5 na minutę na
     sprawdzenie; 100 wpisów historii to ok. 8 godz.), sprawdzenie „Simple”: Period 5 min, Grace 25 min – założone
     2026-11-16 (adres sygnału w `local/cloud-admin.json`, to sekret). Nowe sprawdzenie nie alarmuje do pierwszego
     sygnału, a `/log` dopisuje do historii bez zmiany stanu – tak sprawdzono adres. Dopóki nadawcą jest sama bestia5
     (2.1–2.2), jej wyłączenie albo uśpienie na noc też wywoła alarm.
   Testy: zasady w Core na atrapie bazy z ręcznie sterowanym czasem; funkcje SQL na drugim, próbnym projekcie Supabase
   (`glebosaurus-test`, decyzja użytkownika 2026-11-16 – bez Dockera) – dwóch agentów, uśpienie z dzierżawą, utrata sieci,
   restart w trakcie wysyłki. Testy na chmurze uruchamia się na żądanie (`GLEBOSAURUS_CLOUD_TESTS=1`; bez tego albo bez
   pliku `local/cloud-admin.test.json` są pomijane) i nigdy na prawdziwym projekcie: odmawiają, gdy id projektu jest takie jak w `local/cloud-admin.json` albo gdy
   baza ma tabele Glebosaurusa bez znacznika bazy próbnej.
3. **Synchronizacja agent ↔ Supabase** (2026-11-16). Tylko ruch wychodzący: HTTPS do funkcji SQL (`/rest/v1/rpc/…`)
   zwykłym `HttpClient` z kluczem publikowalnym projektu (`sb_publishable_…` w nagłówku `apikey`, rola `anon`; dawnych
   kluczy `anon`/`service_role` nie używamy – Supabase wycofuje je do końca 2026) i tokenem agenta – bez SDK i bez hasła
   bazy na urządzeniach (wyjątek:
   agent robiący kopię zapasową, zasada 10). Główne wywołanie `agent_sync` co ok. 60 s: sygnał życia (wersja, stan BLE;
   telefon także bateria, temperatura, ładowanie, Wi-Fi), słyszenia, paczka odczytów z lokalnego bufora, odnowienie
   dzierżaw → w odpowiedzi przydziały z epoką i czasem ważności, wersja konfiguracji i polecenia (czytaj teraz, oddaj,
   paruj, wyślij log). Odczyty idempotentne po `(sensor_id, at)` – ten sam klucz co lokalnie; lokalny SQLite zostaje
   buforem i pełną kopią odczytów danego agenta, więc przerwa sieci kończy się dosłaniem bez strat. Dzienne logi agentów
   trafiają spakowane do tabeli w bazie (7 dni; nie do Storage – zasada 13), a nadawca wysyła z nich jeden log administratora z załącznikami wszystkich
   agentów. Protokół jest wersjonowany (`v1`), żeby stare agenty nie psuły nowych danych.
4. **Uwierzytelnianie.** Agent: jednorazowy kod rejestracji (z panelu albo SQL; na Androidzie kod QR) → `enroll` →
   własny token, w bazie tylko jego skrót, odwoływalny w każdej chwili; lokalnie DPAPI (Windows), Android Keystore albo
   plik 600 (Pi). Zasady dostępu do wierszy (RLS) zamykają wszystkie tabele; agenci wołają wyłącznie funkcje
   `security definer`, które sprawdzają token i rolę. Od 2026 Supabase nie wystawia nowych tabel przez Data API bez
   jawnego `grant` (w projekcie domyślne uprawnienia `public` dają `anon` przy tabelach tylko `Dxtm`, bez odczytu i
   zapisu), ale funkcje nadal dostają `execute` dla `PUBLIC` z ogólnej zasady Postgresa (dokumentacja i domyślne
   uprawnienia projektu, sprawdzone 2026-11-16) – migracje odbierają je (`revoke execute … from public`, też jako
   domyślne uprawnienie dla przyszłych funkcji) i nadają `anon` tylko funkcje agentów. Najcenniejszy jest token nadawcy (może zdobyć blokadę, a z nią hasło
   Gmaila), więc rolę nadawcy mają tylko 1–2 zaufane urządzenia. Właściciel (panel, faza 3): Supabase Auth, tylko jego
   konto; sekrety w panelu można ustawić, nigdy odczytać.
5. **Klucze Tuya: lokalne parowanie własnym kluczem** (bez Smart Life i Tuya IoT). Sparowany czujnik nie jest w aplikacji
   Smart Life; reset go przywraca. Do kroku 2.1 klucze żyją wyłącznie w plikach `*.credentials.json` (kopia zapasowa na
   użytkowniku); potem w Supabase Vault (zasada 1), a pliki przenosi tam `cloud import`.
6. **Warstwa BLE: Shiny.BluetoothLE (+ `.Linux`, BlueZ)** za własnym wąskim interfejsem (`IGattLink`); protokół Tuya
   jest czysto zarządzany (`Glebosaurus.Tuya`) i nie zna biblioteki BLE. Zapasowo: SimpleSharpBLE (licencja BUSL-1.1,
   tylko użytek niekomercyjny) albo bezpośrednio WinRT + Linux.Bluetooth. Ta sama biblioteka obsłuży Androida (zasada 12):
   Shiny.BluetoothLE 6.5.2 ma `net10.0-android36.0` (sprawdzone w NuGet 2026-11-15).
7. **Uruchamianie, instalacja i aktualizacje.** Raspberry Pi: usługa systemd (wzór w `deploy/`). Windows (2026-11-15, decyzje
   użytkownika: konto użytkownika zamiast administratora, start przy zalogowaniu, aktualizacja przy starcie i raz na dobę,
   wydania budowane przez GitHub Actions, repozytorium publiczne): zadanie Harmonogramu „Glebosaurus” uruchamia przy
   zalogowaniu `glebosaurus-service.exe` (projekt `Glebosaurus.Service`, podsystem okienkowy – bez okna konsoli). Nie usługa
   Windows: ta wymaga administratora, startuje przed zalogowaniem (BLE przez WinRT w sesji 0 – niesprawdzone) i potrzebowałaby
   hasła konta.
   - **Układ** `%LOCALAPPDATA%\Glebosaurus`: `app\` (program; ścieżka stała, bo reguły Zapory są per ścieżka programu – wersje w
     osobnych katalogach pytałyby o Zaporę po każdej aktualizacji), `app.old\` (poprzednia wersja), `data\` (konfiguracja,
     klucze, baza, dzienniki, sekrety panelu – aktualizacja ich nie dotyka), `updates\` (pobrane pliki, dziennik ostatniej
     aktualizacji, ślad próby), `install.json` (ustawienia skryptu: repozytorium, adres API, nazwa zadania, pora, `autoUpdate`).
   - **Usługa** (`Supervisor`) uruchamia `glebosaurus.exe run --quiet` z `CreateNoWindow` (bez okna także wtedy, gdy domyślnym
     terminalem jest Windows Terminal – `-WindowStyle Hidden` tego nie gwarantuje) i z prywatnym zdarzeniem zatrzymania;
     wyjście monitora trafia do `service-RRRRMMDD.log`. Po zakończeniu monitora wznawia go po 1, 2, 5, potem co 15 min (licznik
     zeruje godzina spokojnej pracy; kod 4 „monitor już działa” – co 5 min, `RestartBackoff`). Wydania sprawdza przy starcie,
     potem codziennie o 03:30 czasu lokalnego, po błędzie sieci po 30 min (`UpdateCheckSchedule`); decyzję podejmuje
     `UpdatePolicy` (tylko nowsza, kompletna, nie robocza wersja; po nieudanej próbie ta sama wersja czeka dobę).
   - **Aktualizacja = ten sam skrypt co instalacja.** Usługa pobiera `install-windows.ps1` z nowego wydania, sprawdza go z
     `SHA256SUMS`, zapisuje ślad próby (`updates\update-attempt.json`), zatrzymuje czysto monitor, uruchamia skrypt
     (`-Unattended`, bez okna) i kończy pracę – jej plików nie da się podmienić w trakcie działania. Skrypt czeka na koniec
     usługi, pobiera paczkę, sprawdza skrót, wyłącza zadanie na czas podmiany, przenosi `app\` do `app.old\`, rozpakowaną paczkę do
     `app\`, rejestruje zadanie od nowa, usuwa ślad próby i uruchamia zadanie (zapasowo usługę wprost). Przy porażce zapisuje
     przyczynę w śladzie i uruchamia dotychczasową wersję – bez śladu zepsute wydanie albo skrypt, który w ogóle nie ruszył,
     dawałyby pętlę „przekaż – porażka – start – przekaż” przy stojącym monitorze.
   - **Jedna instancja i czyste zatrzymanie** (`Glebosaurus.Core/Hosting`): muteks `Local\Glebosaurus.Monitor` trzyma każdy
     `run` (drugi kończy się kodem 4 – deweloperski `run` obok usługi walczyłby o czujniki i wysyłał maile podwójnie),
     `Local\Glebosaurus.Service` – usługa. Muteksem włada osobny wątek (zwalnia go tylko właściciel, a kod asynchroniczny wraca na
     dowolnym wątku). Zdarzenie `Local\Glebosaurus.Stop` (polecenie `stop`, skrypt) zatrzymuje monitor i usługę; nikt nie zabija
     procesu, bo zabicie w trakcie rozłączania potrafi zawiesić następny odczyt. `Stop-ScheduledTask` zabija – nie używać.
   - **Zadanie Harmonogramu**: wyzwalacz przy logowaniu własnego użytkownika z powtórką co godzinę (strażnik: przy działającej
     usłudze nowa kopia jest pomijana – `MultipleInstances IgnoreNew`), `LogonType Interactive`, `RunLevel Limited`, start i
     praca na baterii, bez limitu czasu (domyślne 72 godz. zatrzymałoby monitor), 3 ponowienia po minucie.
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
   - **Bezpieczeństwo aktualizacji:** HTTPS do GitHuba i skrót SHA-256 z tego samego wydania chronią przed uszkodzonym
     pobraniem, nie przed przejęciem konta GitHub (podpis wydań – ROADMAP, faza 4). Skrypt nie potrzebuje administratora i
     nie zmienia Zapory.
8. **Odczyty.** Monitor słucha reklam i łączy się, gdy czujnik jest widoczny, a odczyt należny; domyślny interwał
   15 min. **Nasłuch na przemian z odczytem** (2026-11-14, `ListenThenReadSensorReader` w Core, radio Shiny za
   `ISensorRadio`): gdy w reklamach pojawi się którykolwiek z należnych czujników, nasłuch się kończy, czujnik jest czytany
   od razu, potem nasłuch wraca po pozostałe. Radio nigdy nie słucha i nie łączy się jednocześnie (na Pi Wi-Fi i BLE dzielą
   układ). Okno `scanWindowSeconds` (domyślnie 60 s) liczy tylko nasłuch, łącznie ze wszystkich rund; czujnik, którego nie
   było przez całe okno, dostaje „nie znaleziono”. Wcześniej skan czekał całe okno na wszystkich, więc czujnik poza
   zasięgiem opóźniał pozostałe. Sprawdzone na żywo 2026-11-14: Cisy same – 60 s nasłuchu bez skutku; grupa Cyprysek + Cisy
   – Cisy pojawiły się pierwsze i od razu poszły do odczytu (połączenie padło po 20 s, „Unreachable”, −80 dBm), potem nowa
   runda nasłuchu i Cyprysek przeczytany. Ponowienia po porażce tylko odsuwają odczyt (nigdy nie przyspieszają go przed
   zwykłym interwałem), a odczyt na żądanie pomija i jedno, i drugie. **Błąd nasłuchu** (2026-11-14): na żywo skan Shiny na
   Windows raz przerwał się `COMException (0x80004005) Nieokreślony błąd` (1 na 22 cykle tego dnia). Shiny przy skanie pyta
   system o każde słyszane urządzenie (`BluetoothLEDevice.FromBluetoothAddressAsync`, także cudze urządzenia Tuya w pobliżu)
   bez obsługi błędu, więc jedna nieudana próba kończy cały skan – wniosek ze źródeł Shiny (gałąź master; wersji 6.5.2 nie
   udało się porównać). Wyjątek przerywał wtedy cały cykl monitora: bez odczytu reszty czujników, alertów, raportów i
   poczty, a przy trwałej awarii adaptera monitor nie wysłałby nawet alertu „brak odczytów”. Teraz `ListenThenReadSensorReader`
   wznawia nieudany nasłuch po 1 s w tym samym oknie (nieudana próba zużywa swój czas), najwyżej 3 razy z rzędu (udany nasłuch
   zeruje licznik), a potem czekające czujniki dostają „błąd odczytu: nasłuch BLE przerwany błędem: …” – działa zwykły odstęp
   ponowień, a cykl idzie dalej (testy na atrapie radia i silniku). Ostrzeżenia w dzienniku; ślad stosu tylko przy pierwszej
   porażce z serii. Parowanie to osobna, interaktywna komenda uruchamiana w zasięgu czujnika (okno ok. 20 s – skan musi już działać,
   gdy użytkownik wkłada baterie). Przy kilku niesparowanych kandydatach parowanie wymaga `--mac`.
9. **Alerty i raporty.** Progi per czujnik z histerezą, kilka kolejnych odczytów i cooldown; „czujnik milczy”,
   niska bateria (docelowo trend albo DP14 – stan baterii, 0 = niski – zamiast pojedynczego DP15). Raporty HTML o stałych porach, nadrabiane do
   `catchUpHours`. Wysyłka: Gmail SMTP (MailKit, smtp.gmail.com:465, hasło aplikacji) – kod z LibrusNotifier.
   Odbiorców alertów i raportów podaje się osobnymi listami w `mail.alertRecipients` i `mail.reportRecipients` (puste =
   jak alerty); adresy są walidowane, przycinane i pozbawiane powtórzeń przy wczytaniu konfiguracji.
   **Maile są projektowane pod telefon** (2026-11-13): HTML to jedna kolumna do 480 px (karta na czujnik, trzy duże
   liczby: wilgotność, temperatura, bateria, pasek wilgotności względem progów), style wyłącznie inline, bez tabel
   wielokolumnowych i sztywnych szerokości. Styl: elementy kwadratowe (żadnych zaokrągleń) i pastelowe kolory – jasne
   tło plakietek i paska podsumowania, średni odcień na ramkach i paskach, ciemny tekst (kontrast ≥ 5:1), nigdy biały
   tekst na nasyconym tle; paleta to klasa `Tone` w `MailLayout.cs`, a testy pilnują braku `border-radius` i `color:#fff`; wersja tekstowa ma wiersze ≤ 40 znaków. Tytuł zaczyna się od tego, co
   ważne (obcinany w powiadomieniu), a nazwę „Glebosaurus” niesie nadawca, nie tytuł: „Monstera – czas podlać”,
   „Poranny raport – do podlania: Monstera, Fikus +1”. Pora dnia w tytule raportu („Poranny”/„Wieczorny”) wynika z
   zaplanowanej godziny, nie z chwili wysyłki (raport nadrobiony po godzinach zostaje tym samym raportem). Ukryty tekst
   podglądu (preheader) pokazuje na liście wiadomości wilgotność każdej rośliny. Raport obejrzano w widoku telefonu
   375 px, a na 320 px sprawdzono brak poziomego przewijania (wygląd w Gmailu na telefonie – układ i kolorystykę – zatwierdził użytkownik 2026-11-13);
   `glebosaurus mail-preview` zapisuje przykładowe maile do obejrzenia w przeglądarce, a
   `mail-test --samples` wysyła je na telefon.
   **Wykres w raporcie – prototyp (2026-11-14; o 18:50 `mail-test --samples` wysłał pierwszą wersję na skrzynkę użytkownika,
   SMTP przyjął; użytkownik obejrzał ją w Gmailu na telefonie – „wygląda OK” – i poprosił o przedziały po 2 godz. i
   stonowaną temperaturę pokazaną jako zakres; poprawione tego samego dnia, czeka na ponowne obejrzenie).** Gmail nie pokazuje SVG w treści, obrazek
   SVG zamienia na PNG i potrzebuje adresu w internecie, `data:` nie działa (caniemail, 09.2026), a grafiki rastrowej
   użytkownik nie chce – dlatego wykres jest z samego HTML (`MailChart`): tabela z kolumnami po 2 godz. (12 na dobę; czas
   lokalny, granice jak `ReadingSeriesBuilder.LocalBucketStart`). W kolumnie niebieska kreska 3 px na wysokości średniej
   wilgotności (skala z lewej, zawsze 0–100% – życzenie użytkownika z 2026-11-15; na wierzchu, bo ważniejsza) i pod nią blady pasek temperatury od najniższej do
   najwyższej w przedziale (z prawej, min. 2 °C, pasek co najmniej 2 px) – pomarańcz panelu o kryciu 35%, wyliczony z góry
   jako zwykły kolor osobno dla białego i dla różowego tła (`rgba` z przecinkami Gmail obsługuje, ale tak jest krócej i bez
   ryzyka). Położenie bez pozycjonowania (Gmail go nie obsługuje): `valign=bottom`, dopełnienie komórki od dołu i bloki o
   zadanej wysokości ułożone jeden pod drugim. Różowe tło kolumny = średnia poniżej progu podlania, pusta kolumna = brak
   odczytów, pod spodem podpisy dni; objaśnienie raz na raport. Gmail przycina HTML powyżej ok. 102 KB, więc raport ma budżet
   90 KB; kolumna to ok. 180 B, wykres ok. 7,3 KB, więc przedziały po 2 godz. mieszczą się do ok. 9 czujników, a przy większej
   liczbie rosną (2 → 3 → 4 → 6 → 8 → 12 godz.; 20 czujników → 8 godz., ok. 86 KB); gdy nic się nie mieści – raport bez
   wykresów (testy).
   Wersja tekstowa: 12 znaków `▁…█` (średnie z 6 godz., ta sama skala 0–100%) i zmiana, np. „3 doby: ▃▃▃▃▃▃▃▃▂▂▂▂ 31→18%”. Dane: `SensorSnapshot.History` –
   od 2026-11-14 (po uwadze użytkownika, że raport na żądanie z panelu nie miał wykresów) `SnapshotBuilder` dociąga odczyty z
   ostatnich 75 h (`GetRangeAsync`), więc wykres mają raporty zaplanowane, na żądanie i polecenie `report`; przykłady
   (`mail-preview`, też `raport-20.html`) mają zmyśloną historię. `report --out` z prawdziwej bazy dał 3 wykresy (18 KB).
   Stopka każdego maila (HTML) i ostatni wiersz wersji tekstowej podają wersję Glebosaurusa (`AppInfo.Version`, czytana z
   metadanych zestawu; jedno źródło numeru: `Directory.Build.props`, obecnie 0.1.1).
   **Log techniczny dla administratora** (2026-11-13): dziennik agenta to pliki dzienne `glebosaurus-RRRRMMDD.log` (czas
   lokalny; wspólna klasa `DailyLogFiles` zna nazwy, zapisuje do nich `FileLoggerProvider`, a silnik czyta je przez
   `ILogArchive`). Retencja po dacie z nazwy pliku: `logs.retentionDays` (domyślnie 7; zostaje dzisiejszy i tyle dni
   wstecz), liczona przy pierwszym zapisie każdego dnia. W pierwszym cyklu dnia `MonitorEngine` wysyła pod
   `mail.adminRecipients` (osobna lista od alertów i raportów; pusta = wyłączone) dziennik z poprzedniego dnia: streszczenie
   (liczniki, ostatnie 12 ostrzeżeń i błędów, wąski układ jak reszta maili) i pełny plik jako załącznik (limit 5 mln znaków,
   dłuższy jest obcinany od początku). Znacznik wysłania to klucz `log|RRRR-MM-DD` w tabeli `report_runs` (restart nie
   powtarza maila); nieudana wysyłka ma własny odstęp ponowień (5 min), więc nie opóźnia alertów. Świadome ograniczenie:
   tylko „wczoraj” – jeśli agent wczoraj nie działał albo dzień starszy nie został wysłany, nie jest dosyłany wstecz.
10. **Chmura i UI.** **Supabase, darmowy plan** (decyzja użytkownika 2026-11-16: „celem jest darmowość”): Postgres z
    funkcjami SQL (operacje atomowe) i Vault (Storage nieużywany – zasada 13); bez Edge Functions i bez własnego serwera; region Central EU
    (Frankfurt). Rozważone wcześniej (2026-11-15): własny serwer ASP.NET Core na VPS (ok. 4–7 €/mies.) i Azure (ok.
    15–30 USD/mies.) – odrzucone ze względu na koszt. Limity darmowego planu (supabase.com/pricing, sprawdzone
    2026-11-16; poza wymienionymi niżej: Storage 1 GB – nieużywany, logi agentów są w bazie (zasada 13); logi API i bazy
    trzymane tylko 1 dzień):
    - baza 500 MB, po przekroczeniu tylko do odczytu – odczyty to ok. 0,4 mln wierszy rocznie, szacunkowo 50–80 MB, więc
      starczy na kilka lat, potem uśrednianie starych odczytów (ROADMAP, faza 4); słyszenia bez historii (zasada 2); rozmiar
      bazy w logu administratora;
    - transfer 5 GB/mies. – synchronizacja 3 agentów co 60 s to szacunkowo setki MB;
    - uśpienie po 7 dniach bez ruchu – agenci wołają bazę co minutę; po tygodniowej awarii trzeba ręcznie wznowić projekt
      (dane zostają), a ciszę zgłosi strażnik (zasada 2);
    - brak kopii zapasowych – polecenie `cloud backup` codziennie robi zrzut bazy (`pg_dump` przez pooler sesji, IPv4; hasło
      bazy tylko na agencie robiącym kopię: najpierw bestia5, docelowo Pi), trzyma 14 ostatnich i dokłada lokalną kopię
      kluczy Tuya, bo w zrzucie sekrety z Vault są zaszyfrowane kluczem projektu (do potwierdzenia przy implementacji);
    - 2 aktywne projekty – drugi, `glebosaurus-test`, do testów funkcji SQL (zasada 2); też usypia po tygodniu bez ruchu,
      więc przed testami po przerwie trzeba go wznowić (Restore project).
    **Projekt założony 2026-11-16** (użytkownik; dane w `local/cloud-admin.json`): Central EU (Frankfurt), Postgres 17.12,
    Vault (`supabase_vault` 0.3.2), automatyczne RLS na nowych tabelach (wyzwalacz zdarzeń `ensure_rls`). Sprawdzone z
    bestia5 tego dnia: klucz publikowalny przyjmowany przez Data API (nieistniejąca funkcja → 404 PGRST202, zmyślony klucz
    → 401), a opis schematu dla niego zamknięty (401); klucz tajny czyta schemat i Storage; hasło bazy działa przez pooler
    sesji `aws-1-eu-central-1.pooler.supabase.com:5432` (użytkownik `postgres.<id projektu>`) – `aws-0` nie zna projektu,
    a połączenie bezpośrednie `db.<id projektu>.supabase.co` ma tylko adres IPv6, którego bestia5 nie rozwiązuje.
    Próbny projekt `glebosaurus-test` (dane w `local/cloud-admin.test.json`) założony i sprawdzony tego samego dnia z tym
    samym wynikiem (region, wersje, automatyczne RLS, zachowanie kluczy, pooler `aws-1`).
    Schemat jako migracje SQL w repozytorium (`supabase/migrations/`). UI (faza 3, hipoteza 2026-11-16): statyczna strona
    (np. GitHub Pages) czytająca Supabase przez RLS, logowanie Supabase Auth tylko na konto właściciela, wykresy z
    `charts.js` lokalnego panelu, polecenia („odczytaj teraz”, „wyślij raport teraz”) jako wiersze w tabeli poleceń;
    konfiguracja jawna dla właściciela, sekrety tylko do ustawienia.
11. **Lokalny panel diagnostyczny** (2026-11-14; podgląd, odczyt i raport na żądanie, bez zmian konfiguracji). Kestrel w procesie `run` (projekt
    `Glebosaurus.Web`, tylko framework ASP.NET Core, bez pakietów), włączany sekcją `web` (port domyślnie 8470). Warstwy:
    filtr adresów źródłowych (loopback + sieci prywatne albo `web.allowedNetworks`) przed wszystkim innym; HTTPS z
    certyfikatem z podpisem własnym (ECDSA P-256, 825 dni, odnawiany 30 dni przed końcem, odcisk SHA-256 w dzienniku i w
    `web-password`); hasło jako skrót PBKDF2-SHA256 (600 tys. iteracji) w `glebosaurus-web.password` obok konfiguracji, nie
    w niej – zapis z programu zgubiłby komentarze w pliku JSON; sesje tylko w pamięci (losowy token 256 bit w ciasteczku
    `__Host-glebosaurus`, `Secure`/`HttpOnly`/`SameSite=Strict`), limit 5 błędnych haseł na adres w 15 min i jedno liczenie
    PBKDF2 naraz; POST tylko z tego samego źródła – rozstrzyga `Sec-Fetch-Site`, a `Origin` tylko bez niego (przy
    restrykcyjnej polityce referrera przeglądarka wysyła `Origin: null` także z tej samej strony, stąd `Referrer-Policy:
    same-origin`), odrzucenie trafia do dziennika; CSP bez skryptów inline, `no-store`. Dane:
    silnik publikuje po każdym kroku niezmienną migawkę stanu (`MonitorEngine.State`: próby, ponowienia, sygnał, poczta,
    trwający odczyt), a `MonitorStatusBuilder` składa z niej i z bazy status pole po pole – nigdy przez serializację
    `AgentConfig`, więc klucze czujników i hasło Gmaila nie mają którędy wyjść (test). Strona to statyczny HTML + JS
    wbudowany w bibliotekę, dane z JSON API (`/api/status`, `/api/sensors/{id}/readings`, `/api/log`), wstawiane wyłącznie
    jako tekst. Panel nigdy nie zatrzymuje monitora: brak hasła czy zajęty port to tylko wpis w dzienniku.
    **Odczyt na żądanie** (2026-11-14): `POST /api/sensors/{id}/read` i `/api/read-all` (wymagany nagłówek
    `X-Glebosaurus-Action: 1` – formularz z obcej strony go nie doda, a skrypt musiałby przejść CORS) wołają
    `MonitorEngine.RequestRead`. Silnik dopisuje czujniki do kolejki pod blokadą i budzi pętlę (sygnał zamiast czekania
    `TickInterval`); w najbliższym cyklu czyta je bez względu na odstęp i ponowienia po porażce, po kolei z resztą – radio
    nadal robi jedno połączenie naraz. Ochrona baterii: ten sam czujnik najwyżej raz na `ForcedReadCooldown` (1 min) od
    ostatniej próby; odpowiedź mówi per czujnik: zlecony / już czekał / właśnie trwa / za wcześnie (od kiedy) / nieznany.
    Sprawdzone 2026-11-14 w podglądzie z prawdziwym silnikiem i atrapą radia: kliknięcie → pętla budzi się od razu →
    „odczyt trwa…” → wynik po ok. 6 s; przycisk sam się odblokowuje po końcu odstępu. Na żywo tego samego dnia (`run`,
    Windows, logowanie z sieci lokalnej): „Odczytaj wszystkie” o 13:29:01 – Cyprysek i Cisy zlecone, Zioła „za wcześnie” (czytane
    54 s wcześniej), pętla ruszyła w tej samej sekundzie; Cyprysek przeczytany o 13:29:43, Cisy nie znalezione (na granicy
    zasięgu). Wtedy skan czekał całe okno na wszystkie zlecone czujniki, więc Cisy opóźniły Cypryska o ok. 30 s – poprawione
    tego samego dnia nasłuchem na przemian z odczytem (zasada 8).
    **Raport na żądanie** (2026-11-14): `POST /api/report` (ten sam nagłówek akcji) woła `MonitorEngine.RequestReport`.
    Zlecenie czeka pod tą samą blokadą co odczyty i budzi pętlę; raport idzie w najbliższym cyklu **po** odczytach tego cyklu
    (świeże dane), tym samym szablonem i do tych samych odbiorców co zaplanowany, ale bez wpisu w `report_runs` – harmonogram
    się nie zmienia. To świadoma akcja, więc wstrzymanie wysyłki po błędzie (`MailRetryDelay`) go nie blokuje ani nie
    przedłuża; przed zasypaniem skrzynek chroni `ReportOnRequestCooldown` (1 min od poprzedniej próby, udanej lub nie).
    Odpowiedź: zlecony / już czeka / za wcześnie (od kiedy) / poczta nieskonfigurowana; wynik (wysłany albo błąd) silnik
    publikuje w migawce (`ReportOnRequest`), a panel pokazuje go po zakończeniu. Cykl przerwany błędem kończy czekający raport
    porażką z tą przyczyną – inaczej zlecenie zostałoby w kolejce i pętla ruszałaby od razu po każdym nieudanym cyklu (test).
    Strona pyta o potwierdzenie z listą odbiorców (prawdziwy mail). Sprawdzone 2026-11-14 w podglądzie na loopbacku
    (prawdziwy silnik, atrapy radia i poczty, przez API): zlecony → już czeka → wysłany do odbiorców raportów → za wcześnie;
    przy trwającym odczycie dwóch czujników raport poszedł zaraz po nich, a błąd poczty trafił do stanu. Sama strona
    sprawdzona w przeglądarce na atrapie API (widok 375 px): potwierdzenie, notka z wynikiem, odblokowanie po odstępie,
    błąd. Przeglądarka w aplikacji Claude nie otworzyła panelu (certyfikat z podpisem własnym). **Na żywo 2026-11-14** (`run`,
    Windows, panel z sieci lokalnej): zlecenie o 14:15:49 obudziło pętlę, która najpierw czytała zaległe Zioła (błąd
    powiadomień) i Cisy (nie znalezione w oknie 60 s), a raport poszedł o 14:17:07 (1 odbiorca) i dotarł do skrzynki –
    czekanie na odczyty cyklu trwało ok. 78 s.
    **Wykresy i strona czujnika** (2026-11-14, decyzje użytkownika: bez grafiki rastrowej, wilgotność i temperatura na jednym
    wykresie z dwiema osiami pionowymi, pulpit z tym, co najważniejsze, a szczegóły na stronie czujnika). Pulpit (`/`) ma przy
    każdej karcie wykres ostatnich 72 h; strona czujnika (`/sensor?id=…`) zbiera wszystko o jednym czujniku, z wykresem całej
    historii do przeglądania. Dane: `GET /api/sensors/{id}/series?from&to&points` (domyślnie ostatnie 72 h, okno najwyżej 20 lat,
    `points` 10–2000) → `ReadingSeriesBuilder` w Core (żeby raport mailowy mógł go kiedyś użyć): dopóki odczytów jest nie więcej
    niż `points`, zwraca je wprost, inaczej uśrednia w przedziałach z drabinki 30 min … 28 dni (średnia, min, max, liczba
    odczytów, średni czas odczytów – punkt nigdy nie wypada poza okno). Przedziały liczone w czasie lokalnym, więc dobowe
    zaczynają się o północy, a tygodniowe w poniedziałek; dziura (`gapBefore`) = odstęp dłuższy niż przedział + 3 interwały
    odczytu czujnika. Magazyn: `GetRangeAsync` (odczyty z przedziału, klucz główny `(sensor_id, at)` wystarcza – schemat bez
    zmian) i `GetReadingBoundsAsync` (najstarszy i najnowszy odczyt). Rysowanie: `wwwroot/charts.js`, czysty SVG przez
    `createElementNS`, bez bibliotek (rozważone: uPlot – canvas, ECharts – ok. 1 MB w DLL, Chart.js – canvas i wtyczki),
    kolory w `app.css` (CSP bez stylów inline): wilgotność niebieska `#2a78d6`, temperatura pomarańczowa `#eb6834` (para z
    walidowanej palety, rozróżnialna przy zaburzeniach widzenia barw), tekst w kolorach tekstu, jednostki osi z kreską w kolorze
    serii. Obie osie mają wspólną liczbę przedziałów (jedna siatka) i „okrągłe” kroki. Oś wilgotności jest zawsze 0–100%
    (życzenie użytkownika z 2026-11-15; wykresy czujników i okresów są porównywalne, próg podlania zawsze widać), więc siatka
    ma 4 albo 5 przedziałów (co 25 / co 20 p.p.); temperatura dopasowuje się do danych, z minimalną rozpiętością 2 °C. Pasek
    przeglądu pod wykresem rysuje wilgotność w tej samej skali 0–100%.
    Pulpit pobiera dane wykresu tylko po nowym odczycie (albo co 15 min) i trzyma obiekt wykresu między odświeżeniami, więc nic
    nie miga. Strona czujnika pobiera okno z zapasem jednej szerokości po obu stronach (płynne przesuwanie), ok. 1 punkt na 2 px,
    a po geście dociąga dane, gdy okno wyjdzie poza pobrany zakres albo zmieni rozpiętość o więcej niż ~1,6×; w trakcie
    wczytywania poprzedni obraz zostaje, przygaszony. Kółko myszy powiększa tylko z Ctrl (zwykłe przewija stronę). Dziennik na
    stronie czujnika: `/api/log?contains=<nazwa>` (filtr treści, wielkość liter ma znaczenie). Skrypty to moduły ES
    (`common.js` wspólny dla obu stron). Sprawdzone 2026-11-14 w przeglądarce na programie podglądu (loopback, prawdziwy Core,
    baza z 12 czujnikami próbnymi o historii 0–400 dni z dziurami): pulpit 1200 px i 375 px, przeciąganie, Ctrl+kółko, przyciski,
    zakresy, klawiatura, czujnik bez odczytów i nieznany identyfikator, brak błędów CSP; na prawdziwym monitorze jeszcze nie.
    Dalsze etapy – ROADMAP 1b.7a.
12. **Agent na Androida** (plan, faza 2.2; telefon Xiaomi Mi 11 Lite 5G na Wi-Fi, stale na ładowarce). Tylko zbieranie,
    bez roli nadawcy. .NET for Android (`net10.0-android`, bez MAUI – jedna aktywność ze stanem i usługa) z
    `Glebosaurus.Tuya`, zbieraniem z Core, buforem SQLite (`Glebosaurus.Data`) i Shiny.BluetoothLE (zasada 6) – protokół nie
    powstaje drugi raz (Kotlin odrzucony: drugi raz ramki, AES i DP). Usługa pierwszoplanowa typu `connectedDevice`
    (Android 14 wymaga typu i uprawnienia) ze stałym powiadomieniem, start po restarcie telefonu, uprawnienia
    `BLUETOOTH_SCAN`/`BLUETOOTH_CONNECT` i powiadomień, wyjątek z optymalizacji baterii; rejestracja kodem QR; ekran stanu i
    skanu z sygnałem (wybór miejsca na telefon). Znane ograniczenia do obsłużenia:
    - MIUI/HyperOS zabija aplikacje w tle – ręcznie: Autostart, oszczędzanie baterii „Bez ograniczeń”, blokada w ostatnich
      aplikacjach (instrukcja w README); gdy mimo to zginie, chmura wyśle alert „agent milczy”;
    - skan BLE bez filtra jest wstrzymywany przy zgaszonym ekranie – filtr po MAC-ach (albo UUID Tuya); najwyżej 5 startów
      skanu na 30 s – wznawianie nasłuchu po błędzie co 1 s (`ListenThenReadSensorReader`, zasada 8) trzeba tam wydłużyć,
      inaczej skan po cichu nic nie zwróci; skan dłuższy niż 30 min system degraduje;
    - GATT: częsty błąd 133 – zawsze `close()`, ponowienia z odstępem; Android podaje prawdziwy MAC, więc dopasowanie po
      MAC działa;
    - na ładowarce Doze się nie włącza, ale stary akumulator stale na 100% może puchnąć – sprawdzić limit ładowania w
      systemie; temperatura akumulatora w sygnale życia i alert przy przegrzaniu;
    - na tym telefonie nie instalować Smart Life (dodatkowy klient walczący o połączenia).
    Wydania: APK z Actions podpisany jednym kluczem (sekret repozytorium; jego utrata = instalacja od nowa); Android i tak
    wymaga stuknięcia „Zainstaluj”, więc aktualizacja to powiadomienie o nowej wersji (albo Obtainium).
13. **Schemat chmury** (krok 2.1.2, 2026-11-16; `supabase/migrations/20261116200000_core.sql` – rdzeń,
    `20261116220000_alerts_commands_logs.sql` – alerty, polecenia, logi). Realizacja zasad 1–4:
    - **Schemat `glebosaurus`** (Data API go nie wystawia, RLS na każdej tabeli bez żadnych zasad): `agents` (skrót
      tokenu, `can_send_mail`, `sender_priority`, sygnał życia), `enrollment_codes`, `settings` (jeden wiersz; czas lokalny
      raportów – `time_zone`, domyślnie Europe/Warsaw; `config_version` rośnie przy każdej zmianie ustawień i czujników),
      `sensors` (progi jak `SensorConfig`; dane Tuya w Vault jako `glebosaurus:sensor:<id>`), `readings` (klucz
      `(sensor_id, at)`, temperatura w dziesiątych częściach – jak w SQLite, plus agent, który odczytał), `sightings`,
      `leases` (wiersz zostaje po oddaniu, więc epoka tylko rośnie), `mail_outbox`, `alerts` (jeden otwarty na czujnik i
      rodzaj, jak w SQLite; początek epizodu liczy baza), `commands`, `agent_logs`, `environment` (znacznik bazy próbnej)
      i `schema_migrations`.
    - **API agentów** – 18 funkcji `public.*` `security definer` z pustym `search_path`, wykonywalnych tylko przez `anon`:
      `enroll`, `get_config`, `agent_sync`, `lease_claim`, `lease_release`, `mail_claim`, `mail_sent`, `mail_abandon`,
      `mail_credentials`, `readings_range`, `alert_list`, `alert_open`, `alert_resolve`, `alert_notified`,
      `command_create`, `command_done`, `log_put`, `log_get`. Nieznany albo unieważniony token → SQLSTATE 28000 (Data API:
      403); czynność zastrzeżona dla obecnego nadawcy bez blokady z aktualną epoką → 42501; błędne dane → 22023 (400). Czasy z agenta są względne (`heardSecondsAgo`), bo jego zegar może się rozjechać z bazą; odczyty mają
      własny czas, a baza odrzuca te z przyszłości (ponad 10 min), sprzed 2020 i spoza zakresów – z powodem w odpowiedzi,
      żeby agent ich nie ponawiał. `agent_sync` najpierw oddaje, potem bierze dzierżawy w stałej kolejności (bez
      zakleszczeń) i zwraca dla każdego czujnika właściciela oraz najlepsze słyszenie innego agenta z ostatnich 15 min,
      a także polecenia skierowane teraz do agenta.
    - **Alerty:** czytać może każdy agent (lokalny panel), otwierać, zamykać i oznaczać jako wysłane – tylko obecny nadawca
      (blokada z epoką). Otwarcie już otwartego zwraca istniejący alert, więc restart nadawcy nie tworzy drugiego epizodu,
      a nowy nadawca po przejęciu widzi ten sam stan (także `lastNotifiedAt` do przypomnień). Cisza agenta nie potrzebuje
      tabeli: klucz `agent-silent|<agent>|<od>` wyliczony z `lastSeenSecondsAgo` dubluje tylko kolejka maili.
    - **Polecenia** (`read_now`, `report_now`, `release`, `pair`, `send_log`): bez wskazanego agenta trafiają tam, gdzie
      jest władza – odczyt i oddanie do właściciela dzierżawy czujnika (po przejęciu – do nowego), raport do obecnego
      nadawcy; `pair` i `send_log` zawsze do wskazanego agenta. Wracają w każdej synchronizacji, dopóki agent nie zgłosi
      wyniku (`command_done`) albo nie wygasną (10 min; parowanie 15 min, log 1 godz.). To samo polecenie najwyżej raz na
      minutę i nigdy dwa czekające naraz (blokada doradcza na czas tworzenia). Agent (lokalny panel) może zlecić tylko
      `read_now` i `report_now`; resztę właściciel (`glebosaurus.admin_command`, w fazie 3 panel). Raport na żądanie ma
      klucz maila `report-request|<id polecenia>`.
    - **Dzienne logi agentów:** tabela zamiast Supabase Storage (decyzja przy implementacji 2026-11-16 – Storage wymaga
      zalogowanego użytkownika albo klucza tajnego, a tak log idzie tą samą drogą co reszta). Agent pakuje log gzipem
      (`log_put`, base64, najwyżej 4 MB po spakowaniu – sprawdzone przez Data API 2026-11-16: 3,9 MB w 1,3 s; ponowne
      wysłanie dnia zastępuje log), a czyta go tylko obecny nadawca (`log_get` – logi wszystkich agentów z danego dnia do
      jednego maila administratora). Retencja `settings.log_retention_days` (7 dni, w dniu strefy `time_zone`). Logi
      liczą się do 500 MB bazy: zwykle setki KB dziennie, najgorzej 3 agentów × 8 dni × 4 MB ≈ 100 MB.
    - **Administracja** (`glebosaurus.admin_*`: agent i kod rejestracji, unieważnienie, czujnik, dane Tuya, sekrety poczty,
      ustawienia) – tylko przez połączenie z bazą hasłem bazy (`Glebosaurus.Cloud.Admin`, pooler sesji), nigdy przez Data
      API. Klucz tajny `sb_secret_…` nie jest więc dziś do niczego potrzebny – do rozstrzygnięcia przy `cloud import`
      (2.1.4). Migracje stosuje `MigrationRunner` (blokada doradcza, suma kontrolna – zastosowanej migracji nie wolno
      zmieniać), wbudowane w bibliotekę z `supabase/migrations/`.
    - **Czas:** wszystko liczy `glebosaurus.clock()` = `now()`; tylko w bazie ze znacznikiem próbnej testy przesuwają zegar
      ustawieniem `glebosaurus.test_now` we własnej transakcji (Data API nie daje klientom ustawiać takich zmiennych).
    - **Połączenie z bazą:** serwer ma certyfikat od własnego CA Supabase, którego Windows nie zna (z systemowym magazynem
      `VerifyFull` odrzucony, 2026-11-16). Certyfikat główny „Supabase Root 2021 CA” (publiczny, ważny do 2031-04-26)
      pobrał użytkownik z panelu projektu do `supabase/prod-ca-2021.crt`; jest wbudowany w `Glebosaurus.Cloud.Admin` z
      odciskiem SHA-256 zapisanym w kodzie, a połączenie jest zawsze `VerifyFull` – bez cichego przejścia na samo
      szyfrowanie, bo idzie nim hasło bazy. Sprawdzone 2026-11-16 na obu projektach (tylko `select 1` na prawdziwym), a
      kontrolnie z obcym CA połączenie zostaje odrzucone. Test bez sieci przypomni o nowym certyfikacie pół roku przed końcem.
    - **Testy** (`tests/Glebosaurus.Cloud.Tests`, 53 przypadki, z czego 6 bez sieci – także w CI; reszta uruchamiana
      świadomie – `GLEBOSAURUS_CLOUD_TESTS=1` i
      `local/cloud-admin.test.json`): każdy przebieg czyści próbną bazę i stosuje migracje od zera; przypadki z ROADMAP
      (dwóch agentów, uśpienie z dzierżawą, utrata sieci, restart w trakcie wysyłki), równoczesne prośby o jedną dzierżawę
      (dokładnie jeden zwycięzca), rejestracja i unieważnienie, alerty po przejęciu nadawcy, droga poleceń za dzierżawą,
      logi (gzip, tylko dla nadawcy, retencja), uprawnienia `anon` (dokładnie lista funkcji API, nic w `glebosaurus`, RLS
      wszędzie) i prawdziwa droga przez Data API z kluczem publikowalnym. **2026-11-16: wszystkie zielone na
      `glebosaurus-test`, także drugi przebieg na istniejącym schemacie i przebieg z `VerifyFull`.**
    - Krok 2.1.2 jest skończony; prawdziwy projekt dostanie schemat poleceniem migracji agenta (2.1.4) – na prośbę
      użytkownika.

## Struktura repozytorium

```
src/Glebosaurus.Tuya      protokół Tuya BLE, poświadczenia, profile czujników, sesja, reguły parowania (bez zależności od BLE)
src/Glebosaurus.Core      konfiguracja, reguły alertów, harmonogram raportów, szablony maili, dane do wykresów, silnik monitora (interfejsy)
src/Glebosaurus.Data      lokalny magazyn SQLite (Microsoft.Data.Sqlite)
src/Glebosaurus.Mail      GmailSender (MailKit)
src/Glebosaurus.Web       lokalny panel diagnostyczny (Kestrel, HTTPS, hasło; pulpit z wykresami, strona czujnika, odczyt i raport na żądanie; strony w wwwroot/ wbudowane w bibliotekę)
src/Glebosaurus.Agent     CLI/monitor: run, stop, pair, status, report, mail-test, mail-preview, send-log, web-password, scan, read, listen (Shiny; net10.0 + net10.0-windows10.0.19041.0)
src/Glebosaurus.Service   usługa Windows bez okna (glebosaurus-service.exe): nadzór monitora, sprawdzanie wydań, przekazanie aktualizacji skryptowi
src/Glebosaurus.Cloud.Admin  narzędzia właściciela chmury: połączenie z bazą Supabase (Npgsql, pooler sesji, VerifyFull), migracje
supabase/migrations/      schemat i funkcje SQL chmury (zasada 13), wbudowane w Glebosaurus.Cloud.Admin
supabase/prod-ca-2021.crt certyfikat główny Supabase (publiczny, do 2031-04-26), wbudowany w Glebosaurus.Cloud.Admin
tests/                    testy jednostkowe: protokół i sesja na atrapie czujnika; alerty, raporty, magazyn, silnik na atrapach;
                          Glebosaurus.Cloud.Tests – funkcje SQL na próbnym projekcie Supabase (na żądanie)
examples/                 wzór konfiguracji            deploy/    install-windows.ps1, package-windows.ps1, wzór unitu systemd
.github/workflows/        ci.yml (build + testy), release.yml (tag v* → testy, paczka, próba instalacji, wydanie)
docs/                     dokumentacja                 publish/   gotowe paczki (win-x64, linux-arm64) – wynik budowania, poza repozytorium
local/                    robocze pliki użytkownika (konfiguracja, klucze, baza, dzienniki) – poza repozytorium
```

Interfejsy brzegowe, za którymi fazy 2–3 podmieniają implementacje: `IGattLink` (BLE), `ISensorReader` (radio),
`IMonitorStore` (baza – faza 2 doda magazyn z buforem lokalnym i Supabase, dzierżawy i kolejkę maili), `IMailGateway`
(poczta); konfiguracja przez `AgentConfig` (w fazie 2 z chmury). Faza 2 dzieli też silnik na zbieranie odczytów i
wysyłanie (alerty, raporty, log administratora) – tryb samodzielny ma oba, agent chmurowy zbieranie, a nadawca z blokadą
także wysyłanie.

## Ryzyka

- Shiny na prawdziwej Raspberry Pi (BlueZ, uprawnienia D-Bus) i w trybie bez MAUI.
- BLE w usłudze Windows (sesja 0) – dlatego zadanie Harmonogramu.
- Samoaktualizacja ufa kontu GitHub (skrót chroni przed uszkodzeniem, nie przed podmianą wydania); zepsute wydanie wstrzymuje
  się samo (stara wersja wraca, kolejna próba po dobie), ale wymaga nowego wydania z poprawką.
- Wylogowanie i zamknięcie systemu kończą monitor bez pytania (kilka sekund) – może to wypaść w trakcie odczytu (niesprawdzone).
- Koszt baterii przy częstych odczytach (decyduje o interwale); dryf wskazań wilgotności bez kalibracji.
- Zasięg BLE określa liczbę monitorów; wiele innych urządzeń Tuya w eterze (część to czujniki użytkownika do sparowania
  lokalnie – każdy wymaga resetu, który usuwa go ze Smart Life).
- Do kroku 2.1 klucze czujników istnieją tylko w plikach lokalnych.
- Chmura (faza 2): konto Supabase jest kluczem do wszystkich sekretów; limity darmowego planu (500 MB, uśpienie po 7
  dniach, brak kopii zapasowych – zasada 10); bez Supabase nie wychodzi żaden mail (zgłasza to strażnik). Certyfikat główny
  Supabase wygasa 2031-04-26 (albo Supabase zmieni CA wcześniej) – połączenia właściciela z bazą przestaną wtedy działać,
  dopóki nie wbudujemy nowego (test przypomina pół roku wcześniej).
- Android (faza 2.2): MIUI/HyperOS zabija aplikacje w tle, ograniczenia skanu BLE, akumulator telefonu stale na ładowarce.
