#!/usr/bin/env bash
set -e

echo "===================================================="
echo " ⚡ Installazione QuickPing per Linux (X11 & Wayland)"
echo "    Supporto nativo: Zorin OS 18.1 & CachyOS"
echo "===================================================="

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Rimuove eventuali ritorni a capo Windows (CRLF) se copiato da partizione NTFS/USB
sed -i 's/\r$//' "$SCRIPT_DIR/quickping.py" 2>/dev/null || true

# 1. Installa dipendenze in base alla distribuzione (Zorin/Ubuntu vs CachyOS/Arch)
if command -v apt-get >/dev/null 2>&1; then
    echo "-> Rilevato sistema Debian/Ubuntu/Zorin OS (apt)..."
    sudo apt-get update
    sudo apt-get install -y \
        python3 \
        python3-gi \
        python3-gi-cairo \
        gir1.2-gtk-3.0 \
        gir1.2-ayatanaappindicator3-0.1 \
        wl-clipboard \
        xclip \
        iputils-ping \
        traceroute
elif command -v pacman >/dev/null 2>&1; then
    echo "-> Rilevato sistema Arch/CachyOS (pacman)..."
    sudo pacman -S --needed --noconfirm \
        python \
        python-gobject \
        gtk3 \
        libayatana-appindicator \
        wl-clipboard \
        xclip \
        iputils \
        traceroute
else
    echo "-> Gestore pacchetti non riconosciuto automaticamente. Assicurati di avere: python3-gi, gtk3, wl-clipboard, xclip, traceroute."
fi

# 2. Abilita la lettura globale dei click del mouse (/dev/input/mice) per funzionare sia su Wayland che su X11
echo "-> Configurazione permessi input per supporto globale Wayland + X11..."
sudo usermod -aG input "$USER" 2>/dev/null || true
echo 'KERNEL=="mice", MODE="0644"' | sudo tee /etc/udev/rules.d/99-quickping-mice.rules >/dev/null
sudo udevadm control --reload-rules 2>/dev/null || true
sudo chmod a+r /dev/input/mice 2>/dev/null || true

# 3. Copia l'applicazione in ~/.local/bin/quickping
mkdir -p "$HOME/.local/bin"
cp "$SCRIPT_DIR/quickping.py" "$HOME/.local/bin/quickping"
chmod +x "$HOME/.local/bin/quickping"

# 4. Crea voce nel Menu Applicazioni e in Esecuzione Automatica (Autostart)
mkdir -p "$HOME/.local/share/applications"
mkdir -p "$HOME/.config/autostart"

DESKTOP_CONTENT="[Desktop Entry]
Type=Application
Name=QuickPing
Comment=Ping e Tracert rapidi selezionando IP o Hostname (X11 & Wayland)
Exec=/usr/bin/env python3 $HOME/.local/bin/quickping
Icon=network-transmit-receive
Terminal=false
Categories=Network;Utility;
StartupNotify=false
X-GNOME-Autostart-enabled=true
"

echo "$DESKTOP_CONTENT" > "$HOME/.local/share/applications/quickping.desktop"
echo "$DESKTOP_CONTENT" > "$HOME/.config/autostart/quickping.desktop"

# 5. Avvia subito QuickPing in background (chiudendo eventuali istanze precedenti)
pkill -f "$HOME/.local/bin/quickping" 2>/dev/null || true
nohup /usr/bin/env python3 "$HOME/.local/bin/quickping" >/dev/null 2>&1 &

echo ""
echo "✅ Installazione completata!"
echo "   QuickPing è ora attivo nella barra di sistema e partirà automaticamente ad ogni avvio del PC."
echo "   Seleziona qualsiasi IP o Hostname a schermo e premi il Tasto Destro!"
