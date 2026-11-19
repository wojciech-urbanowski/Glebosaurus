#!/usr/bin/env bash
# Glebosaurus – instalacja, aktualizacja i odinstalowanie agenta na Linuksie (Raspberry Pi OS 64-bit albo inny Debian/Ubuntu
# arm64) jako usługi systemd. Ten sam skrypt instaluje od zera i aktualizuje – także sam, z timera (ARCHITEKTURA.md, zasada 7).
#
# Wymaga: ASP.NET Core Runtime 10 (paczka jest zależna od frameworka – runtime instalujesz i aktualizujesz sam, opis niżej
# i w README), curl, tar, sha256sum, systemd. Uruchamiać jako root:
#
#   curl -fsSLO https://github.com/wojciech-urbanowski/Glebosaurus/releases/latest/download/install-linux.sh
#   sudo bash install-linux.sh
#
# Układ (domyślnie /opt/glebosaurus):
#   app/              program (właściciel root – usługa nie może go zmienić); app.old/ – poprzednia wersja
#   data/             konfiguracja agenta, token, kopia konfiguracji z chmury, baza, dzienniki, sekrety panelu i kopie
#                     chmury – należy do użytkownika usługi (glebosaurus, prawa 700); aktualizacja niczego w nim nie zmienia
#   updates/          pobrane pliki, dziennik ostatniej aktualizacji bez nadzoru, ślad nieudanej próby
#   install.conf      ustawienia instalacji (repozytorium, dotnet, autoaktualizacje); kolejne uruchomienia używają tych samych
#   install-linux.sh  kopia skryptu dla timera aktualizacji i odinstalowania
# Usługa glebosaurus.service uruchamia „glebosaurus run” jako użytkownik glebosaurus i podnosi go po awarii; timer
# glebosaurus-update.timer przy starcie systemu i codziennie o 03:30 sprawdza wydania na GitHubie – nowszą wersję instaluje
# skrypt z tego wydania (sprawdzony z SHA256SUMS). Gdy po aktualizacji usługa nie działa, wraca poprzednia wersja, a ta sama
# wersja czeka dobę. Polecenia agenta: sudo glebosaurus <polecenie> (np. enroll, web-password) – jako użytkownik usługi,
# w katalogu data/ (tam leży glebosaurus.config.json).
#
# Plik musi mieć końce linii LF (.gitattributes) i zostać bez BOM.

set -Eeuo pipefail
umask 022

readonly DEFAULT_REPOSITORY='wojciech-urbanowski/Glebosaurus'
readonly DEFAULT_PREFIX='/opt/glebosaurus'
readonly SERVICE='glebosaurus'
readonly DEFAULT_SERVICE_USER='glebosaurus'
readonly UNIT_DIR='/etc/systemd/system'
readonly WRAPPER='/usr/local/bin/glebosaurus'
readonly DAILY_CHECK='03:30'
readonly RETRY_HOURS=24          # nieudana wersja czeka tyle, zanim timer spróbuje jej ponownie
readonly HEALTH_SECONDS=45       # tyle po aktualizacji usługa musi działać bez restartu, inaczej wraca poprzednia wersja

usage() {
    cat <<'EOF'
Glebosaurus – instalacja i aktualizacja agenta na Linuksie (usługa systemd). Uruchamiaj jako root (sudo).

  sudo bash install-linux.sh [opcje]                 instalacja albo aktualizacja do najnowszego wydania
  sudo bash install-linux.sh --uninstall [--remove-data]

Opcje:
  --version X          konkretna wersja (np. 0.2.4) – także starsza (powrót)
  --package PLIK       lokalna paczka Glebosaurus-<wersja>-<runtime>.tar.gz (obok może leżeć SHA256SUMS)
  --dotnet ŚCIEŻKA     program dotnet z ASP.NET Core Runtime (domyślnie: znaleziony w PATH, /usr/share/dotnet, /opt/dotnet …)
  --auto-updates on|off  samoaktualizacja z timera (domyślnie on; zapamiętywane)
  --no-start           nie uruchamiaj usługi po instalacji
  --force              zainstaluj ponownie, nawet gdy ta wersja już jest
  --uninstall          usuń program, usługę i timer; dane zostają (z --remove-data – także dane i użytkownik glebosaurus)
Do prób i CI:
  --prefix KATALOG     katalog instalacji (domyślnie /opt/glebosaurus)
  --no-systemd         tylko pliki: bez użytkownika usługi, jednostek systemd i polecenia glebosaurus (bez roota)
  --runtime RID        linux-arm64 (domyślnie z uname -m), linux-x64, linux-arm
  --repository WŁ/NAZWA, --release-base URL   skąd brać wydania (np. file:///… z udawanym wydaniem)
Wewnętrzne: --auto-update (timer: sprawdza wydanie i przekazuje instalację skryptowi nowej wersji), --unattended.
EOF
}

say() { printf 'Glebosaurus: %s\n' "$*"; }
warn() { printf 'Glebosaurus: UWAGA: %s\n' "$*" >&2; }
failure_reason=''
die() {
    failure_reason="$*"
    printf 'Glebosaurus: BŁĄD: %s\n' "$*" >&2
    exit 1
}
trap 'failure_reason="${failure_reason:-nieoczekiwany błąd w wierszu $LINENO ($BASH_COMMAND)}"' ERR

# ---- parametry -------------------------------------------------------------------------------------------------------

opt_version='' opt_package='' opt_prefix='' opt_dotnet='' opt_runtime='' opt_repository='' opt_release_base='' opt_auto_updates=''
no_systemd=0 no_start=0 force=0 uninstall=0 remove_data=0 auto_update=0 unattended=0

value() { [ $# -ge 2 ] && [ -n "$2" ] || die "$1: brak wartości (pomoc: --help)"; printf '%s' "$2"; }
while [ $# -gt 0 ]; do
    case "$1" in
        --version) opt_version="$(value "$@")"; shift 2 ;;
        --package) opt_package="$(value "$@")"; shift 2 ;;
        --prefix) opt_prefix="$(value "$@")"; shift 2 ;;
        --dotnet) opt_dotnet="$(value "$@")"; shift 2 ;;
        --runtime) opt_runtime="$(value "$@")"; shift 2 ;;
        --repository) opt_repository="$(value "$@")"; shift 2 ;;
        --release-base) opt_release_base="$(value "$@")"; shift 2 ;;
        --auto-updates) opt_auto_updates="$(value "$@")"; shift 2 ;;
        --no-systemd) no_systemd=1; shift ;;
        --no-start) no_start=1; shift ;;
        --force) force=1; shift ;;
        --uninstall) uninstall=1; shift ;;
        --remove-data) remove_data=1; shift ;;
        --auto-update) auto_update=1; shift ;;
        --unattended) unattended=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "nieznany parametr $1 (pomoc: --help)" ;;
    esac
done

PREFIX="${opt_prefix:-$DEFAULT_PREFIX}"
PREFIX="${PREFIX%/}"
APP="$PREFIX/app"
OLD="$PREFIX/app.old"
FAILED="$PREFIX/app.failed"
DATA="$PREFIX/data"
UPDATES="$PREFIX/updates"
CONF="$PREFIX/install.conf"
SELF_COPY="$PREFIX/install-linux.sh"
ATTEMPT="$UPDATES/update-attempt"
LOG="$UPDATES/install-last.log"
CONFIG="$DATA/glebosaurus.config.json"

# ---- ustawienia instalacji: parametr > install.conf > domyślne -------------------------------------------------------

# Odczyt jednej wartości z install.conf (KLUCZ='wartość') – bez wykonywania pliku.
conf_get() {
    [ -f "$CONF" ] || return 0
    sed -n "s/^$1='\\(.*\\)'\$/\\1/p" "$CONF" | tail -n 1
}

detect_runtime() {
    case "$(uname -m)" in
        aarch64|arm64) echo 'linux-arm64' ;;
        armv7l|armv7*|armv6l|armhf) echo 'linux-arm' ;;
        x86_64|amd64) echo 'linux-x64' ;;
        *) echo "linux-$(uname -m)" ;;
    esac
}

REPOSITORY="${opt_repository:-$(conf_get REPOSITORY)}"
REPOSITORY="${REPOSITORY:-$DEFAULT_REPOSITORY}"
if [ -n "$opt_release_base" ]; then RELEASE_BASE="$opt_release_base"
elif [ -n "$opt_repository" ]; then RELEASE_BASE="https://github.com/$REPOSITORY/releases"
else RELEASE_BASE="$(conf_get RELEASE_BASE)"; RELEASE_BASE="${RELEASE_BASE:-https://github.com/$REPOSITORY/releases}"
fi
RELEASE_BASE="${RELEASE_BASE%/}"
AUTO_UPDATE="${opt_auto_updates:-$(conf_get AUTO_UPDATE)}"
AUTO_UPDATE="${AUTO_UPDATE:-on}"
case "$AUTO_UPDATE" in on|off) ;; *) die "--auto-updates: on albo off" ;; esac
if [ "$no_systemd" = 1 ]; then SYSTEMD='off'; else SYSTEMD="$(conf_get SYSTEMD)"; SYSTEMD="${SYSTEMD:-on}"; fi
SERVICE_USER="$(conf_get SERVICE_USER)"
if [ "$SYSTEMD" = on ]; then SERVICE_USER="${SERVICE_USER:-$DEFAULT_SERVICE_USER}"; else SERVICE_USER="$(id -un)"; fi
RUNTIME="${opt_runtime:-$(conf_get RUNTIME)}"
RUNTIME="${RUNTIME:-$(detect_runtime)}"
if [ -n "$opt_dotnet" ] && [ ! -x "$opt_dotnet" ]; then die "--dotnet: $opt_dotnet nie jest programem"; fi
DOTNET=''

# ---- pomocnicze ------------------------------------------------------------------------------------------------------

# Polecenie jako użytkownik usługi (np. sprawdzenie wersji nowego programu) – nie jako root.
as_service_user() {
    if [ "$(id -un)" = "$SERVICE_USER" ]; then (cd / && "$@")
    elif command -v runuser >/dev/null 2>&1; then (cd / && runuser -u "$SERVICE_USER" -- "$@")
    else (cd / && sudo -u "$SERVICE_USER" -- "$@")
    fi
}

runtime_help() {
    local major="$1"
    cat >&2 <<EOF
Glebosaurus potrzebuje ASP.NET Core Runtime $major (sam .NET Runtime nie wystarczy – agent ma panel WWW). Instalacja do
/opt/dotnet (poza /home – usługa nie widzi katalogów domowych):
  curl -fsSLO https://dot.net/v1/dotnet-install.sh
  sudo bash dotnet-install.sh --channel $major.0 --runtime aspnetcore --install-dir /opt/dotnet
  sudo ln -sf /opt/dotnet/dotnet /usr/local/bin/dotnet
Potem uruchom ten skrypt ponownie. Poprawki bezpieczeństwa .NET: te same polecenia co jakiś czas, potem
sudo systemctl restart glebosaurus.
EOF
}

# Program dotnet: parametr, install.conf, PATH, typowe katalogi. Dowiązanie (np. /usr/local/bin/dotnet) – prawdziwa ścieżka,
# bo obok niej leżą runtime'y.
find_dotnet() {
    local candidate
    for candidate in "$opt_dotnet" "$(conf_get DOTNET)" "$(command -v dotnet 2>/dev/null || true)" "${DOTNET_ROOT:+$DOTNET_ROOT/dotnet}" \
                     /usr/share/dotnet/dotnet /usr/lib/dotnet/dotnet /opt/dotnet/dotnet /usr/local/share/dotnet/dotnet; do
        if [ -n "$candidate" ] && [ -x "$candidate" ]; then readlink -f "$candidate"; return 0; fi
    done
    return 1
}

require_dotnet() {
    local major="$1"
    if [ -z "$DOTNET" ]; then
        DOTNET="$(find_dotnet)" || { runtime_help "$major"; die "nie znaleziono programu dotnet"; }
    fi
    if [ "$SYSTEMD" = on ]; then
        case "$DOTNET" in
            /home/*|/root/*) runtime_help "$major"; die "dotnet leży w katalogu domowym ($DOTNET) – usługa (ProtectHome) go nie zobaczy" ;;
        esac
    fi
    if ! "$DOTNET" --list-runtimes 2>/dev/null | tr -d '\r' | grep -q "^Microsoft\\.AspNetCore\\.App $major\\."; then
        runtime_help "$major"
        die "$DOTNET nie ma ASP.NET Core Runtime $major"
    fi
}

fetch() {
    local url="$1" file="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL --retry 3 --retry-delay 5 --connect-timeout 30 -A 'Glebosaurus-install' -o "$file" "$url" \
            || die "nie udało się pobrać $url"
    elif command -v wget >/dev/null 2>&1; then
        wget -q -U 'Glebosaurus-install' -O "$file" "$url" || die "nie udało się pobrać $url"
    else
        die "brak curl (sudo apt install curl)"
    fi
}

# Skrót z pliku SHA256SUMS (format sha256sum: 64 znaki, spacja, spacja albo gwiazdka, nazwa) – bez awk (mawk w Debianie).
expected_hash() {
    local sums="$1" name="$2" line
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        [ "${#line}" -gt 66 ] && [ "${line:66}" = "$name" ] || continue
        case "${line:64:2}" in '  '|' *') ;; *) continue ;; esac
        if [[ "${line:0:64}" =~ ^[0-9a-fA-F]{64}$ ]]; then
            printf '%s\n' "${line:0:64}" | tr 'A-F' 'a-f'
            return 0
        fi
    done < "$sums"
    return 1
}

verify_hash() {
    local sums="$1" file="$2" name expected actual
    name="$(basename "$file")"
    expected="$(expected_hash "$sums" "$name")" || die "SHA256SUMS nie zawiera pliku $name"
    actual="$(sha256sum "$file" | cut -d ' ' -f 1)"
    actual="${actual#\\}"
    [ "$expected" = "$actual" ] || die "skrót SHA-256 pliku $name się nie zgadza (oczekiwano $expected, jest $actual) – uszkodzone pobranie?"
}

# Nazwa paczki dla tego systemu w SHA256SUMS wydania.
package_in_sums() {
    local line name
    while IFS= read -r line || [ -n "$line" ]; do
        line="${line%$'\r'}"
        name="${line:66}"
        if [[ "$name" =~ ^Glebosaurus-[0-9]+\.[0-9]+\.[0-9]+-${RUNTIME}\.tar\.gz$ ]]; then printf '%s\n' "$name"; return 0; fi
    done < "$1"
    return 1
}

no_package_for_runtime() {
    if [ "$RUNTIME" = linux-arm ]; then
        die "32-bitowy system ($(uname -m)): paczki linux-arm jeszcze nie ma – dojdzie razem z czytaniem czujników na Pi (ROADMAP 2.3b). Zainstaluj 64-bitowy Raspberry Pi OS albo zbuduj paczkę sam: deploy/package-linux.ps1 -Runtime linux-arm"
    fi
    die "wydanie nie ma paczki dla $RUNTIME (są tylko: $(grep -o 'linux-[a-z0-9]*\.tar\.gz' "$1" | sed 's/\.tar\.gz//' | tr '\n' ' '))"
}

version_gt() { [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -n 1)" = "$1" ]; }

# Zainstalowana wersja (z samego programu – to też sprawdza, że runtime działa); pusto, gdy nie ma albo nie działa.
installed_version() {
    local version=''
    [ -f "$APP/glebosaurus.dll" ] && [ -n "$DOTNET" ] || return 0
    version="$(as_service_user "$DOTNET" "$APP/glebosaurus.dll" --version 2>/dev/null | tr -d '\r' | head -n 1)" || true
    if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then printf '%s\n' "$version"; fi
    return 0
}

write_attempt() {
    mkdir -p "$UPDATES"
    printf 'VERSION=%s\nAT=%s\nREASON=%s\n' "$1" "$(date +%s)" "$(printf '%s' "$2" | tr '\n' ' ')" > "$ATTEMPT"
}

# Nieudana (albo przerwana) próba tej wersji w ciągu ostatniej doby – timer jej nie ponawia.
attempt_postponed() {
    local version at
    [ -f "$ATTEMPT" ] || return 1
    version="$(sed -n 's/^VERSION=//p' "$ATTEMPT")"
    at="$(sed -n 's/^AT=//p' "$ATTEMPT")"
    [ "$version" = "$1" ] && [[ "$at" =~ ^[0-9]+$ ]] && [ $(( $(date +%s) - at )) -lt $(( RETRY_HOURS * 3600 )) ]
}

# Jedna instalacja albo aktualizacja naraz; false – inna właśnie trwa.
take_lock() {
    mkdir -p "$UPDATES"
    command -v flock >/dev/null 2>&1 || return 0
    exec 9> "$UPDATES/.lock"
    flock -n 9
}

service_active() { [ "$SYSTEMD" = on ] && systemctl is-active --quiet "$SERVICE"; }

# Po aktualizacji: usługa ma działać przez HEALTH_SECONDS bez żadnego restartu.
service_healthy() {
    local waited=0 state restarts
    while [ "$waited" -lt "$HEALTH_SECONDS" ]; do
        sleep 3
        waited=$((waited + 3))
        state="$(systemctl show -p ActiveState --value "$SERVICE")"
        restarts="$(systemctl show -p NRestarts --value "$SERVICE" 2>/dev/null || true)"
        if [ "$state" != active ] || [ "${restarts:-0}" != 0 ]; then return 1; fi
    done
    return 0
}

# Plik tokenu agenta (enroll): obok konfiguracji, chyba że cloud.tokenFile mówi inaczej (zgrubny odczyt bez parsera JSON –
# niejasny przypadek traktujemy jak zarejestrowanego agenta; sam „run” powie, czego brakuje).
agent_enrolled() {
    local token
    token="$(grep -v '^[[:space:]]*//' "$CONFIG" 2>/dev/null | sed -n 's/.*"tokenFile"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)" || true
    token="${token:-glebosaurus-cloud.token}"
    case "$token" in /*) ;; *) token="$DATA/$token" ;; esac
    [ -f "$token" ]
}

check_value() { case "$2" in *"'"*|*$'\n'*) die "$1: wartość nie może zawierać apostrofu ani nowego wiersza ($2)" ;; esac; }

write_conf() {
    local key tmp="$CONF.tmp"
    for key in REPOSITORY RELEASE_BASE AUTO_UPDATE SYSTEMD SERVICE_USER DOTNET RUNTIME; do check_value "$key" "${!key}"; done
    {
        printf '# Glebosaurus – ustawienia instalacji (install-linux.sh); aktualizacje używają tych samych.\n'
        printf '# Zmiana: uruchom skrypt z parametrem (np. --auto-updates off, --dotnet ŚCIEŻKA), nie edytuj ręcznie.\n'
        for key in REPOSITORY RELEASE_BASE AUTO_UPDATE SYSTEMD SERVICE_USER DOTNET RUNTIME; do printf "%s='%s'\n" "$key" "${!key}"; done
    } > "$tmp"
    mv -f "$tmp" "$CONF"
}

# Kopia skryptu dla timera i odinstalowania. Przez zmianę nazwy (nowy i-węzeł), bo bash czyta działający skrypt po kawałku.
save_self() {
    local source
    source="$(readlink -f "${BASH_SOURCE[0]}" 2>/dev/null || true)"
    if [ -z "$source" ] || [ ! -f "$source" ]; then
        warn "nie da się skopiować skryptu (uruchomiony z potoku?) – pobierz install-linux.sh do pliku i uruchom go z pliku."
        return 0
    fi
    [ "$source" = "$(readlink -f "$SELF_COPY" 2>/dev/null || true)" ] && return 0
    cp -f "$source" "$SELF_COPY.tmp"
    chmod 755 "$SELF_COPY.tmp"
    mv -f "$SELF_COPY.tmp" "$SELF_COPY"
}

ensure_user() {
    [ "$SYSTEMD" = on ] || return 0
    id -u "$SERVICE_USER" >/dev/null 2>&1 && return 0
    [ "$SERVICE_USER" = "$DEFAULT_SERVICE_USER" ] || die "użytkownik usługi $SERVICE_USER nie istnieje"
    useradd --system --user-group --home-dir "$DATA" --no-create-home --shell /usr/sbin/nologin \
        --comment 'Glebosaurus - agent czujnikow gleby' "$SERVICE_USER"
    say "utworzono użytkownika systemowego $SERVICE_USER (bez logowania)."
}

prepare_data() {
    mkdir -p "$DATA"
    if [ -f "$APP/glebosaurus.config.example.json" ]; then cp -f "$APP/glebosaurus.config.example.json" "$DATA/"; fi
    if [ "$SYSTEMD" = on ]; then
        # Dane należą do usługi – także pliki skopiowane tu przez roota (np. plik administratora do kopii chmury).
        chown -R "$SERVICE_USER:$(id -gn "$SERVICE_USER")" "$DATA"
        chmod 700 "$DATA"
    fi
}

write_units() {
    local group
    group="$(id -gn "$SERVICE_USER")"
    cat > "$UNIT_DIR/$SERVICE.service" <<EOF
# Glebosaurus – agent czujników gleby. Zapisane przez install-linux.sh – każda instalacja i aktualizacja je nadpisuje;
# własne zmiany: sudo systemctl edit glebosaurus.
[Unit]
Description=Glebosaurus - agent czujnikow gleby
Documentation=https://github.com/$REPOSITORY
Wants=network-online.target
After=network-online.target bluetooth.target
# Bez konfiguracji agenta usługa nie startuje (pierwsza instalacja: plik z sekcją "cloud", potem glebosaurus enroll).
ConditionPathExists=$CONFIG
StartLimitIntervalSec=0

[Service]
Type=simple
User=$SERVICE_USER
Group=$group
WorkingDirectory=$DATA
Environment=HOME=$DATA
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
ExecStart="$DOTNET" "$APP/glebosaurus.dll" run --config "$CONFIG" --quiet
# SIGTERM (systemctl stop, aktualizacja) = czyste zatrzymanie: agent kończy bieżący odczyt czujnika i oddaje dzierżawy.
# Zabicie w trakcie rozłączania potrafi zawiesić następny odczyt – stąd długi limit.
TimeoutStopSec=300
Restart=on-failure
RestartSec=60
NoNewPrivileges=yes
ProtectSystem=full
ProtectHome=yes
PrivateTmp=yes

[Install]
WantedBy=multi-user.target
EOF
    cat > "$UNIT_DIR/$SERVICE-update.service" <<EOF
# Glebosaurus – sprawdzenie wydań i samoaktualizacja (install-linux.sh --auto-update). Zapisane przez install-linux.sh.
[Unit]
Description=Glebosaurus - sprawdzenie i instalacja nowego wydania
Wants=network-online.target
After=network-online.target

[Service]
Type=oneshot
ExecStart=/bin/bash "$SELF_COPY" --auto-update --prefix "$PREFIX"
TimeoutStartSec=30min
# Brak sieci albo GitHub niedostępny: kolejna próba za 30 min. Nieudana instalacja tej samej wersji czeka dobę (ślad w updates/).
Restart=on-failure
RestartSec=30min
EOF
    cat > "$UNIT_DIR/$SERVICE-update.timer" <<EOF
# Glebosaurus – sprawdzanie wydań przy starcie systemu i codziennie o $DAILY_CHECK. Zapisane przez install-linux.sh.
[Unit]
Description=Glebosaurus - sprawdzanie wydan przy starcie i codziennie o $DAILY_CHECK

[Timer]
OnBootSec=5min
OnCalendar=*-*-* $DAILY_CHECK:00
Persistent=true

[Install]
WantedBy=timers.target
EOF
    cat > "$WRAPPER.tmp" <<EOF
#!/bin/sh
# Glebosaurus (zapisane przez install-linux.sh): polecenia agenta jako użytkownik usługi, w katalogu danych – tam leży
# glebosaurus.config.json, więc np. sudo glebosaurus enroll albo sudo glebosaurus web-password trafiają w dane usługi.
if [ "\$(id -un)" != '$SERVICE_USER' ]; then exec sudo -u '$SERVICE_USER' -- '$WRAPPER' "\$@"; fi
cd '$DATA' || exit 1
exec '$DOTNET' '$APP/glebosaurus.dll' "\$@"
EOF
    chmod 755 "$WRAPPER.tmp"
    mv -f "$WRAPPER.tmp" "$WRAPPER"
    systemctl daemon-reload
    systemctl --quiet enable "$SERVICE.service"
    if [ "$AUTO_UPDATE" = on ]; then systemctl --quiet enable --now "$SERVICE-update.timer"
    else systemctl --quiet disable --now "$SERVICE-update.timer" 2>/dev/null || true
    fi
}

write_summary() {
    local version
    version="$(installed_version)"
    [ -n "$version" ] || return 0
    echo
    echo "Glebosaurus $version w $PREFIX"
    echo "  program:      $APP  (dotnet: $DOTNET)"
    echo "  dane:         $DATA  (dzienniki: $DATA/logs; użytkownik usługi: $SERVICE_USER)"
    if [ "$SYSTEMD" = on ]; then
        echo "  usługa:       $SERVICE.service – stan: systemctl status $SERVICE; dziennik: journalctl -u $SERVICE -f"
        if [ "$AUTO_UPDATE" = on ]; then echo "  aktualizacje: przy starcie i codziennie o $DAILY_CHECK ($REPOSITORY; journalctl -u $SERVICE-update)"
        else echo '  aktualizacje: wyłączone (sudo bash install-linux.sh --auto-updates on włącza)'
        fi
        echo "  polecenia:    sudo glebosaurus <polecenie> (np. enroll, web-password) – jako $SERVICE_USER, w $DATA"
        echo "  zatrzymanie:  sudo systemctl stop $SERVICE   (start: sudo systemctl start $SERVICE)"
    fi
}

# ---- odinstalowanie --------------------------------------------------------------------------------------------------

do_uninstall() {
    if [ "$SYSTEMD" = on ]; then
        [ "$(id -u)" -eq 0 ] || die "odinstalowanie wymaga roota: sudo bash $0 --uninstall"
        systemctl --quiet disable --now "$SERVICE-update.timer" 2>/dev/null || true
        systemctl stop "$SERVICE-update.service" 2>/dev/null || true
        if systemctl is-active --quiet "$SERVICE"; then say 'zatrzymuję agenta (kończy bieżący odczyt)...'; fi
        systemctl --quiet disable --now "$SERVICE.service" 2>/dev/null || true
        rm -f "$UNIT_DIR/$SERVICE.service" "$UNIT_DIR/$SERVICE-update.service" "$UNIT_DIR/$SERVICE-update.timer" "$WRAPPER"
        systemctl daemon-reload
        say 'usunięto usługę, timer aktualizacji i polecenie glebosaurus.'
    fi
    rm -rf "$APP" "$OLD" "$FAILED" "$UPDATES" "$CONF" "$SELF_COPY"
    if [ "$remove_data" = 1 ]; then
        rm -rf "$DATA"
        rmdir "$PREFIX" 2>/dev/null || true
        if [ "$SYSTEMD" = on ] && [ "$SERVICE_USER" = "$DEFAULT_SERVICE_USER" ] && id -u "$SERVICE_USER" >/dev/null 2>&1; then
            userdel "$SERVICE_USER" 2>/dev/null || warn "nie udało się usunąć użytkownika $SERVICE_USER"
        fi
        say 'odinstalowano razem z danymi.'
    else
        say "odinstalowano; dane zostały w $DATA (usuń je ręcznie albo uruchom z --uninstall --remove-data)."
    fi
}

# ---- samoaktualizacja (timer) ----------------------------------------------------------------------------------------

do_auto_update() {
    [ -f "$CONF" ] || die "brak $CONF – Glebosaurus nie jest zainstalowany tym skryptem"
    if [ "$AUTO_UPDATE" != on ]; then say 'samoaktualizacja wyłączona (install.conf).'; return 0; fi
    # Np. timer włączony przez instalację, która jeszcze się kończy – sprawdzenie przy następnej okazji, bez błędu.
    take_lock || { say 'trwa instalacja albo aktualizacja – sprawdzę wydania następnym razem.'; return 0; }
    DOTNET="$(find_dotnet || true)"
    local work="$UPDATES/check" name version installed
    rm -rf "$work"
    mkdir -p "$work"
    fetch "$RELEASE_BASE/latest/download/SHA256SUMS" "$work/SHA256SUMS"
    name="$(package_in_sums "$work/SHA256SUMS")" || { say "najnowsze wydanie nie ma paczki $RUNTIME – nic do zrobienia."; return 0; }
    [[ "$name" =~ ^Glebosaurus-([0-9]+\.[0-9]+\.[0-9]+)- ]] && version="${BASH_REMATCH[1]}"
    installed="$(installed_version)"
    if [ -z "$installed" ]; then
        warn "nie da się odczytać zainstalowanej wersji ($APP/glebosaurus.dll przez ${DOTNET:-dotnet}) – samoaktualizacja wstrzymana; zainstaluj ręcznie: sudo bash $SELF_COPY"
        return 0
    fi
    if ! version_gt "$version" "$installed"; then say "zainstalowana wersja $installed jest aktualna."; return 0; fi
    if attempt_postponed "$version"; then
        say "wersja $version niedawno się nie zainstalowała ($(sed -n 's/^REASON=//p' "$ATTEMPT")) – kolejna próba po dobie."
        return 0
    fi

    # Skrypt nowego wydania (może wiedzieć więcej niż ten), sprawdzony z SHA256SUMS tego samego wydania.
    fetch "$RELEASE_BASE/download/v$version/SHA256SUMS" "$work/SHA256SUMS.v$version"
    fetch "$RELEASE_BASE/download/v$version/install-linux.sh" "$work/install-linux.sh"
    verify_hash "$work/SHA256SUMS.v$version" "$work/install-linux.sh"
    # Ślad przed przekazaniem: gdyby nowy skrypt w ogóle nie ruszył, ta wersja i tak poczeka dobę (bez pętli).
    write_attempt "$version" 'aktualizacja przerwana przed końcem'
    say "nowa wersja $version (jest $installed) – instaluje ją skrypt z wydania."
    exec 9>&-
    exec bash "$work/install-linux.sh" --unattended --version "$version" --prefix "$PREFIX"
}

# ---- instalacja i aktualizacja ---------------------------------------------------------------------------------------

target_version=''
was_stopped=0

on_exit() {
    local status=$?
    [ "$status" -ne 0 ] || return 0
    if [ "$unattended" = 1 ] && [ -n "$target_version" ]; then write_attempt "$target_version" "${failure_reason:-kod $status}"; fi
    # Przerwana podmiana programu – wraca poprzednia wersja.
    if [ ! -d "$APP" ] && [ -d "$OLD" ]; then mv "$OLD" "$APP" || true; fi
    # Zatrzymany na czas aktualizacji agent wraca do pracy (w wersji, która jest w app/).
    if [ "$was_stopped" = 1 ]; then systemctl start "$SERVICE" || true; fi
}

do_install() {
    if [ "$SYSTEMD" = on ]; then
        [ "$(id -u)" -eq 0 ] || die "instalacja usługi wymaga roota: sudo bash $0"
        command -v systemctl >/dev/null 2>&1 || die "brak systemd (systemctl) – do prób bez usługi: --no-systemd"
    fi
    mkdir -p "$PREFIX" "$UPDATES"
    take_lock || die "inna instalacja albo aktualizacja Glebosaurusa właśnie trwa – spróbuj za chwilę"
    trap on_exit EXIT

    # 1. Która wersja i skąd.
    local work package sums name package_runtime installed up_to_date=0 extracted major reported was_active=0
    if [ -n "$opt_package" ]; then
        package="$(readlink -f "$opt_package")"
        [ -f "$package" ] || die "nie ma pliku $opt_package"
        name="$(basename "$package")"
        [[ "$name" =~ ^Glebosaurus-([0-9]+\.[0-9]+\.[0-9]+)-(linux-[a-z0-9]+)\.tar\.gz$ ]] \
            || die "nazwa paczki $name nie ma postaci Glebosaurus-<wersja>-<runtime>.tar.gz"
        target_version="${BASH_REMATCH[1]}"
        package_runtime="${BASH_REMATCH[2]}"
        [ "$package_runtime" = "$RUNTIME" ] || die "paczka jest dla $package_runtime, a ten system to $RUNTIME (--runtime $package_runtime wymusza)"
    else
        work="$UPDATES/release"
        rm -rf "$work"
        mkdir -p "$work"
        if [ -n "$opt_version" ]; then
            [[ "$opt_version" =~ ^v?([0-9]+\.[0-9]+\.[0-9]+)$ ]] || die "--version: $opt_version nie jest numerem wersji (oczekiwano np. 0.2.4)"
            fetch "$RELEASE_BASE/download/v${BASH_REMATCH[1]}/SHA256SUMS" "$work/SHA256SUMS"
        else
            fetch "$RELEASE_BASE/latest/download/SHA256SUMS" "$work/SHA256SUMS"
        fi
        name="$(package_in_sums "$work/SHA256SUMS")" || no_package_for_runtime "$work/SHA256SUMS"
        [[ "$name" =~ ^Glebosaurus-([0-9]+\.[0-9]+\.[0-9]+)- ]] && target_version="${BASH_REMATCH[1]}"
    fi
    ensure_user
    DOTNET="$(find_dotnet || true)"
    installed="$(installed_version)"

    if [ -n "$installed" ] && [ "$force" = 0 ]; then
        if [ "$installed" = "$target_version" ]; then say "zainstalowana wersja $installed jest aktualna."; up_to_date=1
        elif version_gt "$installed" "$target_version" && [ -z "$opt_version" ] && [ -z "$opt_package" ]; then
            say "zainstalowana wersja $installed jest nowsza niż wydanie $target_version – nic nie zmieniam."; up_to_date=1
        fi
    fi

    # 2. Pobranie, sprawdzenie i podmiana programu.
    if [ "$up_to_date" = 0 ]; then
        work="$UPDATES/$target_version"
        rm -rf "$work"
        mkdir -p "$work"
        if [ -n "$opt_package" ]; then
            sums="$(dirname "$package")/SHA256SUMS"
            if [ -f "$sums" ]; then verify_hash "$sums" "$package"; say 'skrót SHA-256 paczki się zgadza.'
            else warn 'obok paczki nie ma SHA256SUMS – instaluję bez sprawdzenia skrótu.'
            fi
        else
            say "pobieram wersję $target_version..."
            mv "$UPDATES/release/SHA256SUMS" "$work/SHA256SUMS"
            package="$work/$name"
            fetch "$RELEASE_BASE/download/v$target_version/$name" "$package"
            verify_hash "$work/SHA256SUMS" "$package"
            say 'skrót SHA-256 paczki się zgadza.'
        fi
        extracted="$work/app"
        mkdir -p "$extracted"
        tar -xzf "$package" -C "$extracted" --no-same-owner
        [ -f "$extracted/glebosaurus.dll" ] && [ -f "$extracted/glebosaurus.runtimeconfig.json" ] || die "paczka nie zawiera glebosaurus.dll"
        chmod -R u=rwX,go=rX "$extracted"
        [ -f "$extracted/glebosaurus" ] && chmod 755 "$extracted/glebosaurus"

        # Wymagany runtime (paczka zależna od frameworka) i próba uruchomienia nowego programu – przed zatrzymaniem agenta.
        major="$(sed -n 's/.*"tfm"[[:space:]]*:[[:space:]]*"net\([0-9][0-9]*\)\..*/\1/p' "$extracted/glebosaurus.runtimeconfig.json" | head -n 1)"
        [ -n "$major" ] || die "nie da się odczytać wersji .NET z glebosaurus.runtimeconfig.json"
        require_dotnet "$major"
        reported="$(as_service_user "$DOTNET" "$extracted/glebosaurus.dll" --version 2>&1 | tr -d '\r' | head -n 1)" || true
        [ "$reported" = "$target_version" ] || die "nowy program nie działa ($DOTNET glebosaurus.dll --version: ${reported:-brak odpowiedzi}, oczekiwano $target_version)"

        if service_active; then
            was_active=1
            say 'zatrzymuję agenta (kończy bieżący odczyt, najwyżej 5 min)...'
            was_stopped=1
            systemctl stop "$SERVICE"
        fi
        rm -rf "$OLD" "$FAILED"
        if [ -d "$APP" ]; then mv "$APP" "$OLD"; fi
        mv "$extracted" "$APP"
        if [ "$(id -u)" -eq 0 ]; then chown -R root:root "$APP"; fi
        if [ -n "$installed" ]; then say "zaktualizowano $installed → $target_version (poprzednia wersja w $OLD)."
        else say "zainstalowano wersję $target_version w $APP."
        fi
    else
        major="$(sed -n 's/.*"tfm"[[:space:]]*:[[:space:]]*"net\([0-9][0-9]*\)\..*/\1/p' "$APP/glebosaurus.runtimeconfig.json" | head -n 1)"
        require_dotnet "${major:-10}"
    fi

    # 3. Dane, ustawienia, usługa.
    prepare_data
    write_conf
    save_self
    if [ "$SYSTEMD" = on ]; then
        write_units
        say "usługa $SERVICE.service zarejestrowana (start przy uruchomieniu systemu, jako $SERVICE_USER)."
    fi
    rm -f "$ATTEMPT"
    find "$UPDATES" -mindepth 1 -maxdepth 1 ! -name "$(basename "$LOG")" ! -name '.lock' -exec rm -rf {} + 2>/dev/null || true

    # 4. Start (bez konfiguracji albo bez rejestracji – tylko wskazówki, jak w install-windows.ps1).
    if [ ! -f "$CONFIG" ]; then
        if [ "$SYSTEMD" = on ]; then
            warn "brak konfiguracji agenta – usługa nie zostanie uruchomiona. Dalej:"
            cat >&2 <<EOF
  1. sudo cp $DATA/glebosaurus.config.example.json $CONFIG && sudo nano $CONFIG
     – adres projektu Supabase i klucz publikowalny; Raspberry Pi daleko od czujników (sam nadawca maili): "bluetooth": false
  2. właściciel chmury tworzy agenta (na swoim komputerze): glebosaurus cloud agent --admin PLIK --name NAZWA --sender-priority 1 --confirm
  3. sudo glebosaurus enroll          (jednorazowy kod z punktu 2)
  4. sudo systemctl start $SERVICE    (dziennik: journalctl -u $SERVICE -f)
EOF
        fi
    elif ! agent_enrolled; then
        if [ "$unattended" = 0 ]; then
            warn "agent nie jest jeszcze zarejestrowany w chmurze – usługa nie zostanie uruchomiona. Zarejestruj go: sudo glebosaurus enroll (kod od właściciela chmury), potem: sudo systemctl start $SERVICE"
        fi
    elif [ "$SYSTEMD" = off ]; then
        :
    elif [ "$no_start" = 1 ] && [ "$unattended" = 0 ] && [ "$was_stopped" = 0 ]; then
        say "usługa nie została uruchomiona (--no-start); wystartuje przy następnym uruchomieniu systemu albo: sudo systemctl start $SERVICE"
    else
        systemctl start "$SERVICE"
        was_stopped=0
        if [ "$was_active" = 1 ] && [ -d "$OLD" ] && [ -n "$installed" ]; then
            say "sprawdzam, czy nowa wersja działa ($HEALTH_SECONDS s)..."
            if ! service_healthy; then
                warn "wersja $target_version nie działa po aktualizacji – wracam do $installed."
                systemctl stop "$SERVICE" || true
                rm -rf "$FAILED"
                mv "$APP" "$FAILED"
                mv "$OLD" "$APP"
                systemctl start "$SERVICE" || true
                die "wersja $target_version nie działała po aktualizacji (zob. journalctl -u $SERVICE); przywrócono $installed, a nieudana wersja leży w $FAILED"
            fi
        fi
        if systemctl is-active --quiet "$SERVICE"; then say 'usługa działa.'
        else warn "usługa nie wystartowała – zajrzyj do: journalctl -u $SERVICE -n 50"
        fi
    fi

    write_summary
}

# ---- przebieg --------------------------------------------------------------------------------------------------------

if [ "$unattended" = 1 ]; then
    mkdir -p "$UPDATES"
    # Dziennik ostatniej aktualizacji bez nadzoru (i tak trafia też do journalctl -u glebosaurus-update).
    exec > >(tee "$LOG") 2>&1
    say "aktualizacja bez nadzoru $(date '+%Y-%m-%d %H:%M:%S')"
fi

if [ "$uninstall" = 1 ]; then do_uninstall
elif [ "$auto_update" = 1 ]; then do_auto_update
else do_install
fi
