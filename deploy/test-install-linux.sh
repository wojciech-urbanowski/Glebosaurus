#!/usr/bin/env bash
# Próba deploy/install-linux.sh bez Raspberry Pi – w CI na ubuntu-24.04-arm (.github/workflows/ci.yml), ręcznie na Linuksie
# albo w Git Bash na Windows (paczki linux-x64, tylko część bez systemd):
#   1. bez roota (--no-systemd): instalacja z paczki, samoaktualizacja z udawanego wydania (file://), zepsute wydanie
#      (ślad próby i odłożenie o dobę), jawny powrót do starszej wersji, odinstalowanie;
#   2. z --systemd, jako root (sudo): pełna instalacja usługi – użytkownik glebosaurus, jednostki systemd, polecenie
#      glebosaurus – bez startu (nie ma konfiguracji agenta), sprawdzenie jednostek i praw, odinstalowanie z danymi.
#      Tylko na maszynie do prób: zakłada i usuwa /opt/glebosaurus i użytkownika glebosaurus.
# Użycie: deploy/test-install-linux.sh KATALOG_STAREJ KATALOG_NOWEJ [--systemd]
#   (katalogi = wyjście deploy/package-linux.ps1 dla dwóch wersji: paczka, install-linux.sh, SHA256SUMS)

set -Eeuo pipefail

[ $# -ge 2 ] || { echo "Użycie: $0 KATALOG_STAREJ KATALOG_NOWEJ [--systemd]" >&2; exit 2; }
old_dir="$(readlink -f "$1")"
new_dir="$(readlink -f "$2")"
with_systemd=0
[ "${3:-}" = '--systemd' ] && with_systemd=1
installer="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/install-linux.sh"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

step() { printf '\n=== %s\n' "$*"; }
fail() { printf 'PRÓBA NIEUDANA: %s\n' "$*" >&2; exit 1; }
package_of() { local found; found="$(ls "$1"/Glebosaurus-*-linux-*.tar.gz)"; printf '%s\n' "$found"; }
version_of() { basename "$1" | sed -E 's/^Glebosaurus-([0-9]+\.[0-9]+\.[0-9]+)-.*/\1/'; }
expect_version() {
    local actual
    actual="$(dotnet "$2/glebosaurus.dll" --version | tr -d '\r')"
    [ "$actual" = "$1" ] || fail "$2: oczekiwano wersji $1, jest $actual"
    echo "ok: $2 → $1"
}
# Adres file:// – w Git Bash curl chce ścieżki Windows (C:/…).
file_url() { if command -v cygpath >/dev/null 2>&1; then printf 'file:///%s\n' "$(cygpath -m "$1")"; else printf 'file://%s\n' "$1"; fi; }

old_package="$(package_of "$old_dir")"
new_package="$(package_of "$new_dir")"
v1="$(version_of "$old_package")"
v2="$(version_of "$new_package")"
runtime="$(basename "$old_package" | sed -E 's/^Glebosaurus-[0-9.]+-(linux-[a-z0-9]+)\.tar\.gz$/\1/')"
echo "Paczki: $v1 i $v2 ($runtime); skrypt: $installer"

# Udawane wydania w układzie GitHuba: latest/download/<plik> i download/v<wersja>/<plik>.
release="$work/release"
mkdir -p "$release/latest/download" "$release/download/v$v1" "$release/download/v$v2"
cp "$old_dir"/* "$release/download/v$v1/"
cp "$new_dir"/* "$release/download/v$v2/"
cp "$old_dir/SHA256SUMS" "$release/latest/download/SHA256SUMS"
prefix="$work/glebosaurus"

step "1. Instalacja $v1 z paczki (bez systemd)"
bash "$installer" --no-systemd --prefix "$prefix" --package "$old_package" --runtime "$runtime" --release-base "$(file_url "$release")"
expect_version "$v1" "$prefix/app"
[ -f "$prefix/data/glebosaurus.config.example.json" ] || fail 'brak wzoru konfiguracji w data/'
[ -f "$prefix/install-linux.sh" ] || fail 'brak kopii skryptu'
grep -q "^RUNTIME='$runtime'\$" "$prefix/install.conf" || fail 'install.conf bez runtime'

step "2. Samoaktualizacja – najnowsze wydanie to wciąż $v1"
bash "$prefix/install-linux.sh" --auto-update --prefix "$prefix" | tee "$work/out.txt"
grep -q 'jest aktualna' "$work/out.txt" || fail 'oczekiwano „aktualna”'
expect_version "$v1" "$prefix/app"

step "3. Wydanie $v2 – samoaktualizacja skryptem z wydania"
cp "$new_dir/SHA256SUMS" "$release/latest/download/SHA256SUMS"
bash "$prefix/install-linux.sh" --auto-update --prefix "$prefix"
expect_version "$v2" "$prefix/app"
expect_version "$v1" "$prefix/app.old"
[ -f "$prefix/updates/install-last.log" ] || fail 'brak dziennika aktualizacji bez nadzoru'
[ ! -f "$prefix/updates/update-attempt" ] || fail 'ślad próby został po udanej aktualizacji'

step "4. Zepsute wydanie 9.9.9 (w środku $v2) – ślad próby, odłożenie, program bez zmian"
broken="$release/download/v9.9.9"
mkdir -p "$broken"
cp "$new_package" "$broken/Glebosaurus-9.9.9-$runtime.tar.gz"
cp "$new_dir/install-linux.sh" "$broken/"
(cd "$broken" && sha256sum "Glebosaurus-9.9.9-$runtime.tar.gz" install-linux.sh > SHA256SUMS)
cp "$broken/SHA256SUMS" "$release/latest/download/SHA256SUMS"
if bash "$prefix/install-linux.sh" --auto-update --prefix "$prefix"; then fail 'zepsute wydanie się zainstalowało'; fi
grep -q '^VERSION=9.9.9$' "$prefix/updates/update-attempt" || fail 'brak śladu nieudanej próby'
expect_version "$v2" "$prefix/app"
bash "$prefix/install-linux.sh" --auto-update --prefix "$prefix" | tee "$work/out.txt"
grep -q 'kolejna próba po dobie' "$work/out.txt" || fail 'nieudana wersja nie została odłożona'

step "5. Jawny powrót do $v1 z wydania"
bash "$prefix/install-linux.sh" --no-systemd --prefix "$prefix" --version "$v1"
expect_version "$v1" "$prefix/app"

step "6. Odinstalowanie z danymi"
bash "$prefix/install-linux.sh" --no-systemd --prefix "$prefix" --uninstall --remove-data
[ ! -e "$prefix" ] || fail "zostało $prefix"

if [ "$with_systemd" = 1 ]; then
    step "7. Usługa systemd (root): instalacja $v1 bez konfiguracji – usługa zarejestrowana, ale nie startuje"
    sudo bash "$installer" --package "$old_package" --runtime "$runtime" --auto-updates off
    id glebosaurus
    [ "$(sudo glebosaurus --version | tr -d '\r')" = "$v1" ] || fail 'polecenie glebosaurus nie działa'
    [ "$(sudo stat -c '%U %a' /opt/glebosaurus/data)" = 'glebosaurus 700' ] || fail 'złe prawa data/'
    [ "$(stat -c '%U' /opt/glebosaurus/app/glebosaurus.dll)" = 'root' ] || fail 'program nie należy do roota'
    systemctl is-enabled glebosaurus.service
    if systemctl is-active --quiet glebosaurus.service; then fail 'usługa wystartowała bez konfiguracji'; fi
    if systemctl is-enabled --quiet glebosaurus-update.timer; then fail 'timer włączony mimo --auto-updates off'; fi
    sudo systemd-analyze verify /etc/systemd/system/glebosaurus.service /etc/systemd/system/glebosaurus-update.service \
        /etc/systemd/system/glebosaurus-update.timer

    # Timer zostaje wyłączony: włączony sięgnąłby od razu po prawdziwe wydania z GitHuba.
    step "8. Aktualizacja usługi do $v2 (ustawienia z install.conf)"
    sudo bash /opt/glebosaurus/install-linux.sh --package "$new_package"
    [ "$(sudo glebosaurus --version | tr -d '\r')" = "$v2" ] || fail 'aktualizacja usługi nie podmieniła programu'
    [ "$(sudo glebosaurus --version --config nieistotne | tr -d '\r')" = "$v2" ] || fail 'polecenie glebosaurus nie przekazuje argumentów'
    if systemctl is-enabled --quiet glebosaurus-update.timer; then fail 'install.conf nie zapamiętał --auto-updates off'; fi

    step "9. Odinstalowanie usługi z danymi"
    sudo bash /opt/glebosaurus/install-linux.sh --uninstall --remove-data
    if id glebosaurus >/dev/null 2>&1; then fail 'został użytkownik glebosaurus'; fi
    [ ! -e /opt/glebosaurus ] && [ ! -e /etc/systemd/system/glebosaurus.service ] && [ ! -e /usr/local/bin/glebosaurus ] \
        || fail 'zostały pliki usługi'
fi

echo
echo 'Próba install-linux.sh: wszystko w porządku.'
