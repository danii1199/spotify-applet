#!/usr/bin/env bash
# Instala la miniaplicación de Spotify para Cinnamon.
#   ./install.sh            copia la miniaplicación y la añade al panel
#   ./install.sh --link     enlaza en vez de copiar (los cambios del repo se aplican al recargar)
#   ./install.sh --uninstall
set -euo pipefail

UUID="spotify-applet"
SRC="$(cd "$(dirname "$0")" && pwd)/$UUID"
DEST="$HOME/.local/share/cinnamon/applets/$UUID"

reload() {
    gdbus call --session --dest org.Cinnamon --object-path /org/Cinnamon \
        --method org.Cinnamon.ReloadXlet "$UUID" APPLET >/dev/null 2>&1 || true
}

if [[ "${1:-}" == "--uninstall" ]]; then
    current="$(gsettings get org.cinnamon enabled-applets)"
    gsettings set org.cinnamon enabled-applets "$(python3 -c "
import ast, sys
l = ast.literal_eval(sys.argv[1])
print([a for a in l if ':$UUID:' not in a])" "$current")"
    rm -rf "$DEST"
    echo "Desinstalada. Tu cuenta sigue en ~/.config/spotify-applet (bórrala si quieres)."
    exit 0
fi

missing=()
for cmd in python3 curl wget xdg-open; do
    command -v "$cmd" >/dev/null || missing+=("$cmd")
done
if (( ${#missing[@]} )); then
    echo "Faltan programas: ${missing[*]}  (sudo apt install ${missing[*]/xdg-open/xdg-utils})"
    exit 1
fi
[[ "${XDG_CURRENT_DESKTOP:-}" == *Cinnamon* ]] || echo "Aviso: no parece que estés en Cinnamon; la miniaplicación solo funciona ahí."

mkdir -p "$(dirname "$DEST")"
rm -rf "$DEST"
if [[ "${1:-}" == "--link" ]]; then
    ln -s "$SRC" "$DEST"
else
    cp -r "$SRC" "$DEST"
fi
chmod +x "$DEST/spotify-lists.py"

current="$(gsettings get org.cinnamon enabled-applets)"
if [[ "$current" != *":$UUID:"* ]]; then
    gsettings set org.cinnamon enabled-applets "$(python3 -c "
import ast, sys
l = ast.literal_eval(sys.argv[1])
ids = [int(a.split(':')[-1]) for a in l if a.split(':')[-1].isdigit()]
l.append('panel1:right:0:$UUID:%d' % (max(ids, default=0) + 1))
print(l)" "$current")"
    echo "Instalada y añadida al panel (a la derecha). Puedes moverla en modo edición del panel."
else
    reload
    echo "Actualizada y recargada."
fi
