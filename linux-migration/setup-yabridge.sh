#!/usr/bin/env bash
# =============================================================================
#  Перенос Windows VST3-плагинов в REAPER под Linux через Wine + yabridge.
#
#  Использование:
#     chmod +x setup-yabridge.sh
#     ./setup-yabridge.sh /путь/к/папке/plugins
#
#  Что делает скрипт:
#    1. Проверяет окружение (не root, нативный REAPER, Wine Staging, yabridge).
#    2. Ставит yabridge в ~/.local/share/yabridge, если его нет.
#    3. Создаёт отдельный Wine-префикс для аудиоплагинов (~/.wine-audio).
#    4. Копирует туда ТОЛЬКО разрешённые .vst3 и пропускает файлы, изменённые
#       после выпуска производителем.
#    5. Регистрирует плагины через yabridgectl -> они появляются в ~/.vst3/yabridge,
#       откуда REAPER видит их как обычные Linux-плагины.
#
#  Скрипт не изменяет сами плагины: Windows-бинарник нельзя "перекомпилировать"
#  в Linux-формат без исходников. Он запускается внутри Wine, а yabridge
#  прозрачно связывает его с REAPER.
# =============================================================================
set -euo pipefail

YABRIDGE_VERSION="5.1.1"
WINE_RECOMMENDED="9.21"          # yabridge 5.1.1 не работает с Wine 9.22+ / 10.x
export WINEPREFIX="${WINEPREFIX:-$HOME/.wine-audio}"
VST3_DIR="$WINEPREFIX/drive_c/Program Files/Common Files/VST3"
YABRIDGE_HOME="$HOME/.local/share/yabridge"

# Плагины, для которых нужен перенос через yabridge (у них нет Linux-версии).
ALLOWED=(
  "Refire.vst3"         # Outobugi, бесплатный, только Windows
  "PitchMonster.vst3"   # Devious Machines, только Windows/macOS, онлайн-лицензия
)

# Плагины, которые этот скрипт сознательно НЕ устанавливает (см. README.md).
declare -A SKIP_REASON=(
  ["Speakers.vst3"]="у AudioThing есть НАТИВНАЯ Linux-версия Speakers — скачайте её из личного кабинета audiothing.net, Wine не нужен."
)

c_red=$'\e[31m'; c_grn=$'\e[32m'; c_ylw=$'\e[33m'; c_off=$'\e[0m'
info() { printf '%s[*]%s %s\n' "$c_grn" "$c_off" "$*"; }
warn() { printf '%s[!]%s %s\n' "$c_ylw" "$c_off" "$*"; }
die()  { printf '%s[x]%s %s\n' "$c_red" "$c_off" "$*" >&2; exit 1; }

SRC="${1:-}"
[[ -n "$SRC" && -d "$SRC" ]] || die "Укажите папку с плагинами: $0 /путь/к/plugins"
[[ $EUID -ne 0 ]] || die "Не запускайте скрипт от root: Wine-префикс должен принадлежать вашему пользователю."

# --- 1. REAPER --------------------------------------------------------------
if command -v flatpak >/dev/null 2>&1 && flatpak list 2>/dev/null | grep -qi reaper; then
  warn "Найден REAPER из Flatpak. yabridge с Flatpak НЕ работает — установите REAPER с reaper.fm (tar.xz, install-reaper.sh)."
fi

# --- 2. Wine Staging ----------------------------------------------------------
if ! command -v wine >/dev/null 2>&1; then
  cat <<EOF
${c_red}Wine не установлен.${c_off} Установите Wine Staging ${WINE_RECOMMENDED}:

  Ubuntu / Debian / Mint (после подключения репозитория WineHQ, https://wiki.winehq.org/Download):
    version=${WINE_RECOMMENDED}; variant=staging
    codename=\$(shopt -s nullglob; awk '/^deb https:\/\/dl\.winehq\.org/ { print \$3; exit 0 } END { exit 1 }' /etc/apt/sources.list /etc/apt/sources.list.d/*.list || awk '/^Suites:/ { print \$2; exit }' /etc/apt/sources.list /etc/apt/sources.list.d/wine*.sources)
    sudo apt install --install-recommends {"winehq-\$variant","wine-\$variant","wine-\$variant-amd64","wine-\$variant-i386"}="\$version~\$codename-1"
    sudo apt-mark hold winehq-staging

  Arch / Manjaro:  sudo pacman -S wine-staging yabridge yabridgectl
  Fedora:          COPR patrickl/wine-tkg (там же собран yabridge)

Затем запустите этот скрипт ещё раз.
EOF
  exit 1
fi

WINE_VER="$(wine --version 2>/dev/null | sed -E 's/^wine-([0-9]+\.[0-9]+).*/\1/')"
info "Wine: $(wine --version)"
if [[ "$(printf '%s\n%s\n' "$WINE_VER" "9.22" | sort -V | head -n1)" == "9.22" ]]; then
  warn "Wine $WINE_VER новее 9.21: релизный yabridge $YABRIDGE_VERSION с ним не работает."
  warn "Либо откатитесь на Wine Staging $WINE_RECOMMENDED, либо поставьте dev-сборку yabridge (см. README.md)."
fi

# --- 3. yabridge ----------------------------------------------------------------
if command -v yabridgectl >/dev/null 2>&1; then
  YCTL="$(command -v yabridgectl)"
elif [[ -x "$YABRIDGE_HOME/yabridgectl" ]]; then
  YCTL="$YABRIDGE_HOME/yabridgectl"
else
  info "Скачиваю yabridge $YABRIDGE_VERSION в $YABRIDGE_HOME ..."
  tmp="$(mktemp -d)"
  url="https://github.com/robbert-vdh/yabridge/releases/download/${YABRIDGE_VERSION}/yabridge-${YABRIDGE_VERSION}.tar.gz"
  curl -fL --retry 3 -o "$tmp/yabridge.tar.gz" "$url" || die "Не удалось скачать $url"
  mkdir -p "$HOME/.local/share"
  tar -C "$HOME/.local/share" -xzf "$tmp/yabridge.tar.gz"
  rm -rf "$tmp"
  YCTL="$YABRIDGE_HOME/yabridgectl"
  [[ -x "$YCTL" ]] || die "После распаковки не найден $YCTL"
  if ! grep -q '.local/share/yabridge' "$HOME/.bashrc" 2>/dev/null; then
    echo 'export PATH="$PATH:$HOME/.local/share/yabridge"' >> "$HOME/.bashrc"
  fi
fi
info "yabridgectl: $YCTL"

# --- 4. Wine-префикс --------------------------------------------------------------
if [[ ! -d "$WINEPREFIX/drive_c" ]]; then
  info "Создаю 64-битный Wine-префикс $WINEPREFIX ..."
  WINEARCH=win64 WINEDEBUG=-all wineboot --init >/dev/null 2>&1 || die "wineboot не смог создать префикс"
fi
mkdir -p "$VST3_DIR"

# --- 5. Копирование плагинов с проверкой -----------------------------------------
is_tampered() {
  # Посторонние секции, которые добавляют сторонние патчеры, а не производители.
  LC_ALL=C grep -aqF '.R2R64' "$1"
}

installed=0
shopt -s nullglob
for f in "$SRC"/*.vst3; do
  name="$(basename "$f")"
  if [[ -n "${SKIP_REASON[$name]:-}" ]]; then
    warn "Пропускаю $name: ${SKIP_REASON[$name]}"
    continue
  fi
  if is_tampered "$f"; then
    warn "Пропускаю $name: файл изменён после выпуска производителем. Установите его официальным установщиком."
    continue
  fi
  allowed=0
  for a in "${ALLOWED[@]}"; do [[ "$a" == "$name" ]] && allowed=1; done
  if [[ $allowed -eq 0 ]]; then
    warn "Пропускаю $name: его нет в списке ALLOWED. Если это лицензионный плагин, добавьте его туда."
    continue
  fi
  cp -f -- "$f" "$VST3_DIR/"
  info "Установлен: $name"
  installed=$((installed + 1))
done

[[ $installed -gt 0 ]] || die "Не установлено ни одного плагина."

# --- 6. Регистрация в yabridge ------------------------------------------------------
"$YCTL" add "$VST3_DIR" 2>/dev/null || true   # если путь уже добавлен, это не ошибка
"$YCTL" sync
"$YCTL" status || true

cat <<EOF

${c_grn}Готово.${c_off} Мосты для плагинов созданы в ~/.vst3/yabridge

В REAPER:
  Options -> Preferences -> Plug-ins -> VST
    * проверьте, что в "VST plug-in paths" есть  ~/.vst3
    * нажмите "Re-scan" -> "Clear cache/re-scan".

Плагины, которые требуют активации (Pitch Monster), активируйте прямо из
интерфейса плагина — так же, как в Windows. Плагины с защитой PACE/iLok
в Wine не работают: для них нужна Windows или нативная замена (ReaFIR, ReaXcomp).

Если плагин не грузится, запустите REAPER так и пришлите лог:
  YABRIDGE_DEBUG_FILE=/tmp/yabridge.log YABRIDGE_DEBUG_LEVEL=1 reaper
EOF
