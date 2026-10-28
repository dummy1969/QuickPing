#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
QuickPing per Linux (Zorin OS / Ubuntu & CachyOS / Arch Linux)
Funziona sia sotto X11 che sotto Wayland.
Riconosce indirizzi IPv4 e Nomi Host / Domini selezionati in qualsiasi applicazione.
"""

import os
import sys
import re
import time
import socket
import struct
import select
import shutil
import signal
import subprocess
import threading
from datetime import datetime
from pathlib import Path

# Usa XWayland (x11) come primo backend se disponibile così GTK permette
# il posizionamento libero del popup e il "Sempre in primo piano" (Pin) anche sotto Wayland,
# con fallback automatico a wayland puro.
if "DISPLAY" in os.environ and "GDK_BACKEND" not in os.environ:
    os.environ["GDK_BACKEND"] = "x11,wayland"

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gtk, Gdk, GLib, Pango

# Supporto System Tray (AppIndicator3 / AyatanaAppIndicator3 per GNOME/KDE, con fallback a Gtk.StatusIcon)
HAS_APPINDICATOR = False
try:
    gi.require_version("AyatanaAppIndicator3", "0.1")
    from gi.repository import AyatanaAppIndicator3 as AppIndicator3
    HAS_APPINDICATOR = True
except (ValueError, ImportError):
    try:
        gi.require_version("AppIndicator3", "0.1")
        from gi.repository import AppIndicator3
        HAS_APPINDICATOR = True
    except (ValueError, ImportError):
        HAS_APPINDICATOR = False


# Regex IPv4 e Hostname (identiche alla versione Windows, escludono telefoni ed email)
IPV4_REGEX = re.compile(
    r"(?<![\d\.])((?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3})(?![\d\.])"
)
HOSTNAME_REGEX = re.compile(
    r"(?<![@\w\.-])(?:https?://)?((?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,24})(?![@\w\.-])",
    re.IGNORECASE,
)


def extract_target_from_text(text: str):
    if not text:
        return None
    cleaned = text.strip()
    if not cleaned or len(cleaned) > 140:
        return None

    m_ip = IPV4_REGEX.search(cleaned)
    if m_ip:
        return m_ip.group(1)

    m_host = HOSTNAME_REGEX.search(cleaned)
    if m_host:
        return m_host.group(1).lower()

    return None


APP_CSS = b"""
window.quickping-dark {
    background-color: #121216;
    color: #f0f0f5;
}
.qp-header {
    background-color: #1c1c22;
    padding: 10px 12px;
    border-bottom: 1px solid #2d2d38;
}
.qp-footer {
    background-color: #1c1c22;
    padding: 6px 10px;
    border-top: 1px solid #2d2d38;
}
.qp-title {
    font-family: monospace;
    font-size: 14px;
    font-weight: bold;
    color: #ffffff;
}
.qp-stats {
    font-family: monospace;
    font-size: 11.5px;
    color: #bec3d2;
}
.qp-badge-wait {
    font-weight: bold;
    color: #facc15;
}
.qp-badge-online {
    font-weight: bold;
    color: #4ade80;
}
.qp-badge-timeout {
    font-weight: bold;
    color: #f87171;
}
.qp-btn {
    background-image: none;
    background-color: #2d2d37;
    color: #ffffff;
    border: 1px solid #3f3f4e;
    border-radius: 4px;
    padding: 4px 10px;
}
.qp-btn:hover {
    background-color: #3f3f4e;
}
.qp-popup-window {
    background-color: #18181b;
    border: 1px solid #22c55e;
    border-radius: 6px;
}
.qp-popup-ping {
    background-image: none;
    background-color: #16a34a;
    color: #ffffff;
    font-weight: bold;
    border: none;
    border-radius: 4px;
    padding: 5px 12px;
}
.qp-popup-ping:hover {
    background-color: #15803d;
}
.qp-popup-tracert {
    background-image: none;
    background-color: #3f3f46;
    color: #ffffff;
    border: none;
    border-radius: 4px;
    padding: 5px 10px;
}
.qp-popup-tracert:hover {
    background-color: #52525b;
}
textview.qp-log, textview.qp-log text {
    background-color: #0e0e12;
    color: #4ade80;
    font-family: monospace;
    font-size: 12px;
}
"""


class TriggerMode:
    POPUP_ON_RIGHT_CLICK = 0   # Seleziona IP/Host + Tasto Destro -> Mostra bottone Ping
    DIRECT_ON_RIGHT_CLICK = 1  # Seleziona IP/Host + Tasto Destro -> Apre subito finestra Ping
    POPUP_ON_SELECTION = 2     # Appena selezioni un IP/Host -> Mostra subito bottone Ping


class FloatingPingPopup(Gtk.Window):
    def __init__(self, app):
        super().__init__(type=Gtk.WindowType.POPUP)
        self.app = app
        self.current_target = None
        self.hide_timer_id = None

        self.set_decorated(False)
        self.set_skip_taskbar_hint(True)
        self.set_skip_pager_hint(True)
        self.set_keep_above(True)
        self.set_accept_focus(False)
        self.set_focus_on_map(False)
        self.get_style_context().add_class("qp-popup-window")

        box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        box.set_margin_top(4)
        box.set_margin_bottom(4)
        box.set_margin_start(4)
        box.set_margin_end(4)

        self.btn_ping = Gtk.Button(label="⚡ Ping")
        self.btn_ping.get_style_context().add_class("qp-popup-ping")
        self.btn_ping.connect("button-press-event", self._on_ping_clicked)
        self.btn_ping.connect("clicked", self._on_ping_clicked)

        self.btn_tracert = Gtk.Button(label="Tracert")
        self.btn_tracert.get_style_context().add_class("qp-popup-tracert")
        self.btn_tracert.connect("button-press-event", self._on_tracert_clicked)
        self.btn_tracert.connect("clicked", self._on_tracert_clicked)

        box.pack_start(self.btn_ping, True, True, 0)
        box.pack_start(self.btn_tracert, False, False, 0)
        self.add(box)

    def _on_ping_clicked(self, *_args):
        target = self.current_target
        self.hide_popup()
        if target:
            self.app.open_ping_window(target, start_tracert=False)
        return True

    def _on_tracert_clicked(self, *_args):
        target = self.current_target
        self.hide_popup()
        if target:
            self.app.open_ping_window(target, start_tracert=True)
        return True

    def show_near_cursor(self, target: str, x: int, y: int):
        self.current_target = target
        self.btn_ping.set_label(f"⚡ Ping {target}")
        self.show_all()

        # Calcola dimensione e posiziona appena sopra il cursore
        alloc = self.get_preferred_size()[1]
        w = max(alloc.width, 220)
        h = max(alloc.height, 38)

        pos_x = max(10, x - 30)
        pos_y = max(10, y - h - 16)
        self.move(pos_x, pos_y)
        self.present_with_time(Gdk.CURRENT_TIME)

        if self.hide_timer_id is not None:
            GLib.source_remove(self.hide_timer_id)
        self.hide_timer_id = GLib.timeout_add(6500, self.hide_popup)

    def hide_popup(self):
        if self.hide_timer_id is not None:
            GLib.source_remove(self.hide_timer_id)
            self.hide_timer_id = None
        if self.get_visible():
            self.hide()
        return False


class PingWindow(Gtk.Window):
    def __init__(self, target: str, start_tracert: bool = False):
        super().__init__(title=f"Ping ➔ {target}")
        self.target = target
        self.is_paused = False
        self.is_closed = False

        self.sent = 0
        self.received = 0
        self.lost = 0
        self.min_rtt = None
        self.max_rtt = 0.0
        self.sum_rtt = 0.0

        self.set_default_size(530, 390)
        self.set_keep_above(True)
        self.get_style_context().add_class("quickping-dark")
        self.connect("destroy", self._on_destroy)

        self._build_ui()

        # Avvia loop di ping ogni 1 secondo in background
        threading.Thread(target=self._ping_Worker, daemon=True).start()
        if start_tracert:
            self._run_tracert()

    def _build_ui(self):
        vbox = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)

        # Header
        header = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        header.get_style_context().add_class("qp-header")

        top_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.lbl_title = Gtk.Label(label=f"TARGET: {self.target}")
        self.lbl_title.get_style_context().add_class("qp-title")
        self.lbl_title.set_xalign(0)

        self.btn_copy = Gtk.Button(label="Copia")
        self.btn_copy.get_style_context().add_class("qp-btn")
        self.btn_copy.connect("clicked", self._on_copy_target)

        self.lbl_badge = Gtk.Label(label="● IN CORSO...")
        self.lbl_badge.get_style_context().add_class("qp-badge-wait")
        self.lbl_badge.set_xalign(1)

        top_row.pack_start(self.lbl_title, False, False, 0)
        top_row.pack_start(self.btn_copy, False, False, 0)
        top_row.pack_end(self.lbl_badge, False, False, 0)

        self.lbl_stats = Gtk.Label(
            label="Inviati: 0 | Ricevuti: 0 | Persi: 0 (0%)  —  Min: - Media: - Max: -"
        )
        self.lbl_stats.get_style_context().add_class("qp-stats")
        self.lbl_stats.set_xalign(0)

        header.pack_start(top_row, False, False, 0)
        header.pack_start(self.lbl_stats, False, False, 0)

        # Area Log centrale
        scrolled = Gtk.ScrolledWindow()
        scrolled.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.AUTOMATIC)
        self.textview = Gtk.TextView()
        self.textview.set_editable(False)
        self.textview.set_cursor_visible(False)
        self.textview.set_left_margin(10)
        self.textview.set_right_margin(10)
        self.textview.set_top_margin(8)
        self.textview.set_bottom_margin(8)
        self.textview.get_style_context().add_class("qp-log")

        self.textbuffer = self.textview.get_buffer()
        self.tag_ok = self.textbuffer.create_tag("ok", foreground="#4ade80")
        self.tag_warn = self.textbuffer.create_tag("warn", foreground="#facc15")
        self.tag_err = self.textbuffer.create_tag("err", foreground="#f87171")
        self.tag_info = self.textbuffer.create_tag("info", foreground="#93c5fd")
        self.tag_muted = self.textbuffer.create_tag("muted", foreground="#9ca3af")

        scrolled.add(self.textview)

        # Footer con pulsanti
        footer = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        footer.get_style_context().add_class("qp-footer")

        self.btn_pause = Gtk.Button(label="⏸ Pausa")
        self.btn_pause.get_style_context().add_class("qp-btn")
        self.btn_pause.connect("clicked", self._on_toggle_pause)

        btn_reset = Gtk.Button(label="🔄 Reset")
        btn_reset.get_style_context().add_class("qp-btn")
        btn_reset.connect("clicked", self._on_reset)

        btn_tracert = Gtk.Button(label="🛣️ Tracert")
        btn_tracert.get_style_context().add_class("qp-btn")
        btn_tracert.connect("clicked", lambda *_: self._run_tracert())

        btn_term = Gtk.Button(label="💻 Terminale")
        btn_term.get_style_context().add_class("qp-btn")
        btn_term.connect("clicked", self._on_open_terminal)

        self.chk_top = Gtk.CheckButton(label="📌 Primo piano")
        self.chk_top.set_active(True)
        self.chk_top.connect("toggled", lambda chk: self.set_keep_above(chk.get_active()))

        footer.pack_start(self.btn_pause, False, False, 0)
        footer.pack_start(btn_reset, False, False, 0)
        footer.pack_start(btn_tracert, False, False, 0)
        footer.pack_start(btn_term, False, False, 0)
        footer.pack_end(self.chk_top, False, False, 0)

        vbox.pack_start(header, False, False, 0)
        vbox.pack_start(scrolled, True, True, 0)
        vbox.pack_start(footer, False, False, 0)

        self.add(vbox)

    def _on_copy_target(self, *_args):
        clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        clip.set_text(self.target, -1)
        self.btn_copy.set_label("OK!")

    def _on_toggle_pause(self, *_args):
        self.is_paused = not self.is_paused
        self.btn_pause.set_label("▶ Riprendi" if self.is_paused else "⏸ Pausa")
        self._append_log(
            "--- Ping in pausa ---" if self.is_paused else "--- Ping ripreso ---",
            self.tag_muted,
        )

    def _on_reset(self, *_args):
        self.sent = self.received = self.lost = 0
        self.min_rtt = None
        self.max_rtt = self.sum_rtt = 0.0
        self.textbuffer.set_text("")
        self._update_header(None)

    def _on_open_terminal(self, *_args):
        cmd_str = f"ping {self.target}"
        terminals = [
            ["gnome-terminal", "--", "bash", "-c", cmd_str],
            ["konsole", "-e", "bash", "-c", cmd_str],
            ["xfce4-terminal", "-e", cmd_str],
            ["kitty", "bash", "-c", cmd_str],
            ["alacritty", "-e", "bash", "-c", cmd_str],
            ["xterm", "-e", cmd_str],
        ]
        for term in terminals:
            if shutil.which(term[0]):
                subprocess.Popen(term)
                return

    def _ping_Worker(self):
        time_re = re.compile(r"time[=<]([\d\.]+)\s*ms", re.IGNORECASE)
        ttl_re = re.compile(r"ttl=(\d+)", re.IGNORECASE)
        from_re = re.compile(r"from\s+([^\s:]+)", re.IGNORECASE)

        while not self.is_closed:
            if self.is_paused:
                time.sleep(0.3)
                continue

            start_t = time.time()
            self.sent += 1
            ts = datetime.now().strftime("%H:%M:%S")

            try:
                proc = subprocess.run(
                    ["ping", "-c", "1", "-W", "2", self.target],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    timeout=3.5,
                )
                out = proc.stdout or ""
                m_time = time_re.search(out)
                if proc.returncode == 0 and m_time:
                    rtt = float(m_time.group(1))
                    m_ttl = ttl_re.search(out)
                    ttl = m_ttl.group(1) if m_ttl else "-"
                    m_from = from_re.search(out)
                    src = m_from.group(1) if m_from else self.target

                    self.received += 1
                    if self.min_rtt is None or rtt < self.min_rtt:
                        self.min_rtt = rtt
                    if rtt > self.max_rtt:
                        self.max_rtt = rtt
                    self.sum_rtt += rtt

                    tag = self.tag_warn if rtt > 150.0 else self.tag_ok
                    line = f"[{ts}] Risposta da {src}: bytes=64 tempo={rtt:.1f}ms TTL={ttl}"
                    GLib.idle_add(self._append_log, line, tag)
                    GLib.idle_add(self._update_header, rtt)
                else:
                    self.lost += 1
                    line = f"[{ts}] TIMEOUT / Nessuna risposta da {self.target}"
                    GLib.idle_add(self._append_log, line, self.tag_err)
                    GLib.idle_add(self._update_header, -1.0)
            except Exception as ex:
                self.lost += 1
                line = f"[{ts}] Errore Ping: {ex}"
                GLib.idle_add(self._append_log, line, self.tag_err)
                GLib.idle_add(self._update_header, -1.0)

            elapsed = time.time() - start_t
            if elapsed < 1.0:
                time.sleep(1.0 - elapsed)

    def _run_tracert(self):
        tracer_bin = None
        for candidate in ["traceroute", "tracepath"]:
            if shutil.which(candidate):
                tracer_bin = candidate
                break

        if not tracer_bin:
            self._append_log(
                "Errore: né 'traceroute' né 'tracepath' sono installati.", self.tag_err
            )
            return

        self._append_log(
            f"--- Avvio {tracer_bin.upper()} verso {self.target} ---", self.tag_info
        )

        def worker():
            cmd = (
                ["traceroute", "-n", "-m", "20", "-w", "1", self.target]
                if tracer_bin == "traceroute"
                else ["tracepath", "-n", "-m", "20", self.target]
            )
            try:
                proc = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                )
                for line in proc.stdout:
                    if self.is_closed:
                        proc.terminate()
                        break
                    s = line.strip()
                    if s:
                        GLib.idle_add(self._append_log, f"  [TRACERT] {s}", self.tag_info)
            except Exception as ex:
                GLib.idle_add(
                    self._append_log, f"Errore Tracert: {ex}", self.tag_err
                )

        threading.Thread(target=worker, daemon=True).start()

    def _update_header(self, last_rtt):
        if self.is_closed:
            return False
        loss_pct = round(self.lost * 100.0 / self.sent) if self.sent > 0 else 0
        if self.received > 0 and self.min_rtt is not None:
            avg = self.sum_rtt / self.received
            rtt_str = f"Min:{self.min_rtt:.1f}ms Media:{avg:.1f}ms Max:{self.max_rtt:.1f}ms"
        else:
            rtt_str = "Min:- Media:- Max:-"

        self.lbl_stats.set_text(
            f"Inviati:{self.sent} | Ricevuti:{self.received} | Persi:{self.lost} ({loss_pct}%)  —  {rtt_str}"
        )

        ctx = self.lbl_badge.get_style_context()
        ctx.remove_class("qp-badge-wait")
        ctx.remove_class("qp-badge-online")
        ctx.remove_class("qp-badge-timeout")

        if last_rtt is None:
            self.lbl_badge.set_text("● IN ATTESA")
            ctx.add_class("qp-badge-wait")
        elif last_rtt >= 0:
            self.lbl_badge.set_text(f"● ONLINE ({last_rtt:.1f} ms)")
            ctx.add_class("qp-badge-online")
        else:
            self.lbl_badge.set_text("● TIMEOUT")
            ctx.add_class("qp-badge-timeout")
        return False

    def _append_log(self, text: str, tag):
        if self.is_closed:
            return False
        end_iter = self.textbuffer.get_end_iter()
        self.textbuffer.insert_with_tags(end_iter, text + "\n", tag)
        mark = self.textbuffer.create_mark(None, self.textbuffer.get_end_iter(), False)
        self.textview.scroll_to_mark(mark, 0.0, True, 0.0, 1.0)
        return False

    def _on_destroy(self, *_args):
        self.is_closed = True


class QuickPingLinuxApp:
    def __init__(self):
        self.mode = TriggerMode.POPUP_ON_RIGHT_CLICK
        self.detected_target = None
        self.detected_at = 0.0
        self.last_primary_text = ""

        # Stima coordinate cursore (aggiornata sia da GDK/XWayland sia dai delta di /dev/input/mice)
        screen = Gdk.Screen.get_default()
        self.cursor_x = (screen.get_width() // 2) if screen else 500
        self.cursor_y = (screen.get_height() // 2) if screen else 400
        self.screen_w = screen.get_width() if screen else 1920
        self.screen_h = screen.get_height() if screen else 1080

        # Applica stile CSS
        provider = Gtk.CssProvider()
        provider.load_from_data(APP_CSS)
        Gtk.StyleContext.add_provider_for_screen(
            Gdk.Screen.get_default(),
            provider,
            Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION,
        )

        self.popup = FloatingPingPopup(self)
        self._setup_tray()
        self._setup_selection_monitoring()
        self._start_global_mouse_listener()

    def _setup_tray(self):
        self.menu = Gtk.Menu()

        header = Gtk.MenuItem(label="⚡ QuickPing Linux - Attivo")
        header.set_sensitive(False)
        self.menu.append(header)

        item_manual = Gtk.MenuItem(label="🔍 Ping IP/Host dagli Appunti o Manuale...")
        item_manual.connect("activate", self._on_manual_ping)
        self.menu.append(item_manual)

        self.menu.append(Gtk.SeparatorMenuItem())

        mode_label = Gtk.MenuItem(label="Modalità attivazione Ping:")
        mode_label.set_sensitive(False)
        self.menu.append(mode_label)

        self.radio_m1 = Gtk.RadioMenuItem(
            label="1. Seleziona + Tasto Destro ➔ Mostra bottone Ping (Consigliato)"
        )
        self.radio_m2 = Gtk.RadioMenuItem.new_with_label_from_widget(
            self.radio_m1, "2. Seleziona + Tasto Destro ➔ Apri subito finestra Ping"
        )
        self.radio_m3 = Gtk.RadioMenuItem.new_with_label_from_widget(
            self.radio_m1, "3. Appena selezioni IP/Host ➔ Mostra subito bottone Ping"
        )

        self.radio_m1.set_active(True)
        self.radio_m1.connect("toggled", self._on_mode_changed, TriggerMode.POPUP_ON_RIGHT_CLICK)
        self.radio_m2.connect("toggled", self._on_mode_changed, TriggerMode.DIRECT_ON_RIGHT_CLICK)
        self.radio_m3.connect("toggled", self._on_mode_changed, TriggerMode.POPUP_ON_SELECTION)

        self.menu.append(self.radio_m1)
        self.menu.append(self.radio_m2)
        self.menu.append(self.radio_m3)

        self.menu.append(Gtk.SeparatorMenuItem())

        self.chk_autostart = Gtk.CheckMenuItem(label="Avvia automaticamente al login")
        self.chk_autostart.set_active(self._is_autostart_enabled())
        self.chk_autostart.connect("toggled", self._on_toggle_autostart)
        self.menu.append(self.chk_autostart)

        self.menu.append(Gtk.SeparatorMenuItem())

        item_quit = Gtk.MenuItem(label="❌ Esci")
        item_quit.connect("activate", lambda *_: Gtk.main_quit())
        self.menu.append(item_quit)

        self.menu.show_all()

        if HAS_APPINDICATOR:
            self.indicator = AppIndicator3.Indicator.new(
                "quickping-linux",
                "network-transmit-receive",
                AppIndicator3.IndicatorCategory.APPLICATION_STATUS,
            )
            self.indicator.set_status(AppIndicator3.IndicatorStatus.ACTIVE)
            self.indicator.set_menu(self.menu)
        else:
            self.status_icon = Gtk.StatusIcon()
            self.status_icon.set_from_icon_name("network-transmit-receive")
            self.status_icon.set_tooltip_text("QuickPing - Seleziona un IP/Host e fai Tasto Destro")
            self.status_icon.connect(
                "popup-menu",
                lambda icon, button, activate_time: self.menu.popup(
                    None, None, Gtk.StatusIcon.position_menu, icon, button, activate_time
                ),
            )
            self.status_icon.connect("activate", self._on_manual_ping)

    def _on_mode_changed(self, widget, mode):
        if widget.get_active():
            self.mode = mode

    def _autostart_path(self) -> Path:
        return Path.home() / ".config" / "autostart" / "quickping.desktop"

    def _is_autostart_enabled(self) -> bool:
        return self._autostart_path().exists()

    def _on_toggle_autostart(self, widget):
        p = self._autostart_path()
        if widget.get_active():
            p.parent.mkdir(parents=True, exist_ok=True)
            script_path = Path(__file__).resolve()
            p.write_text(
                f"[Desktop Entry]\n"
                f"Type=Application\n"
                f"Name=QuickPing\n"
                f"Comment=Ping rapido selezionando IP o Hostname a schermo\n"
                f"Exec=/usr/bin/env python3 \"{script_path}\"\n"
                f"Icon=network-transmit-receive\n"
                f"Terminal=false\n"
                f"Categories=Network;Utility;\n"
                f"X-GNOME-Autostart-enabled=true\n",
                encoding="utf-8",
            )
        else:
            if p.exists():
                p.unlink()

    def _on_manual_ping(self, *_args):
        text = self._read_primary_or_clipboard()
        target = extract_target_from_text(text)
        if target:
            self.open_ping_window(target)
            return

        dlg = Gtk.Dialog(
            title="QuickPing - Inserisci IP o Host",
            transient_for=None,
            flags=0,
        )
        dlg.add_buttons(
            Gtk.STOCK_CANCEL, Gtk.ResponseType.CANCEL,
            "Avvia Ping", Gtk.ResponseType.OK,
        )
        dlg.set_default_size(320, 120)
        box = dlg.get_content_area()
        box.set_spacing(8)
        box.set_margin_top(12)
        box.set_margin_bottom(12)
        box.set_margin_start(12)
        box.set_margin_end(12)

        lbl = Gtk.Label(label="Indirizzo IP o Hostname:")
        lbl.set_xalign(0)
        entry = Gtk.Entry()
        entry.set_text("8.8.8.8")
        entry.connect("activate", lambda *_: dlg.response(Gtk.ResponseType.OK))

        box.pack_start(lbl, False, False, 0)
        box.pack_start(entry, False, False, 0)
        dlg.show_all()

        resp = dlg.run()
        entered = entry.get_text().strip()
        dlg.destroy()

        if resp == Gtk.ResponseType.OK and entered:
            cleaned = extract_target_from_text(entered) or entered
            self.open_ping_window(cleaned)

    def _setup_selection_monitoring(self):
        """
        Monitora la selezione primaria di Linux (PRIMARY selection):
        su Linux (sia X11 che Wayland) qualsiasi testo evidenziato col mouse
        finisce automaticamente nel buffer PRIMARY senza bisogno di Ctrl+C.
        """
        self.primary_clip = Gtk.Clipboard.get(Gdk.SELECTION_PRIMARY)
        self.primary_clip.connect("owner-change", self._on_primary_owner_change)

    def _on_primary_owner_change(self, *_args):
        # Quando l'utente seleziona del testo in qualsiasi finestra
        GLib.timeout_add(60, self._check_primary_selection)

    def _check_primary_selection(self):
        text = self._read_primary_selection()
        target = extract_target_from_text(text)
        if target:
            self.detected_target = target
            self.detected_at = time.time()
            self._refresh_cursor_position()
            if self.mode == TriggerMode.POPUP_ON_SELECTION:
                self.popup.show_near_cursor(target, self.cursor_x, self.cursor_y)
        else:
            self.detected_target = None
        return False

    def _read_primary_selection(self) -> str:
        # 1. Prova prima via GTK Primary Clipboard (sincronizzato da Mutter/KWin e nativo su X11)
        try:
            txt = self.primary_clip.wait_for_text()
            if txt and extract_target_from_text(txt):
                return txt
        except Exception:
            pass

        # 2. Prova via wl-paste (Wayland nativo su CachyOS / KDE / Hyprland / Sway)
        if shutil.which("wl-paste") and os.environ.get("WAYLAND_DISPLAY"):
            try:
                res = subprocess.run(
                    ["wl-paste", "--primary", "--no-newline"],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    text=True,
                    timeout=0.25,
                )
                if res.returncode == 0 and res.stdout:
                    return res.stdout
            except Exception:
                pass

        # 3. Prova via xclip (X11)
        if shutil.which("xclip"):
            try:
                res = subprocess.run(
                    ["xclip", "-o", "-selection", "primary"],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.DEVNULL,
                    text=True,
                    timeout=0.25,
                )
                if res.returncode == 0 and res.stdout:
                    return res.stdout
            except Exception:
                pass

        return ""

    def _read_primary_or_clipboard(self) -> str:
        txt = self._read_primary_selection()
        if txt:
            return txt
        try:
            clip = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
            return clip.wait_for_text() or ""
        except Exception:
            return ""

    def _refresh_cursor_position(self):
        try:
            display = Gdk.Display.get_default()
            if display:
                seat = display.get_default_seat()
                if seat and seat.get_pointer():
                    _, x, y = seat.get_pointer().get_position()
                    if x > 0 or y > 0:
                        self.cursor_x, self.cursor_y = int(x), int(y)
                        return
        except Exception:
            pass

    def _start_global_mouse_listener(self):
        """
        Ascolta i click del mouse a livello kernel tramite /dev/input/mice
        così il Tasto Destro e il Tasto Centrale vengono rilevati sia sotto X11
        che sotto Wayland (GNOME su Zorin OS e KDE/Hyprland su CachyOS).
        """
        threading.Thread(target=self._mice_listener_worker, daemon=True).start()

    def _mice_listener_worker(self):
        mice_path = "/dev/input/mice"
        if not os.access(mice_path, os.R_OK):
            # Se l'utente non ha ancora eseguito install.sh per i permessi di /dev/input/mice,
            # passa automaticamente alla modalità "Mostra bottone appena selezioni l'IP"
            # che funziona senza permessi su /dev/input/mice!
            print(
                "[QuickPing] Nota: /dev/input/mice non leggibile dall'utente corrente. "
                "Esegui ./install.sh per abilitare il rilevamento del Tasto Destro globale su Wayland/X11. "
                "Attivata nel frattempo la modalità automatica alla selezione.",
                file=sys.stderr,
            )
            GLib.idle_add(lambda: self.radio_m3.set_active(True))
            return

        prev_left = False
        prev_right = False
        prev_middle = False
        drag_dist = 0
        last_left_up_time = 0.0

        try:
            with open(mice_path, "rb", buffering=0) as f:
                while True:
                    pkt = f.read(3)
                    if not pkt or len(pkt) < 3:
                        continue
                    buttons = pkt[0]
                    dx = struct.unpack("b", bytes([pkt[1]]))[0]
                    dy = struct.unpack("b", bytes([pkt[2]]))[0]

                    # Aggiorna stima posizione cursore (in /dev/input/mice dy è invertito rispetto allo schermo)
                    self.cursor_x = max(0, min(self.screen_w, self.cursor_x + dx))
                    self.cursor_y = max(0, min(self.screen_h, self.cursor_y - dy))

                    left = bool(buttons & 0x01)
                    right = bool(buttons & 0x02)
                    middle = bool(buttons & 0x04)

                    if left:
                        drag_dist += abs(dx) + abs(dy)

                    # Rilascio tasto sinistro dopo trascinamento o doppio/triplo clic
                    if prev_left and not left:
                        now = time.time()
                        is_dbl_click = (now - last_left_up_time) <= 0.45
                        last_left_up_time = now
                        if drag_dist >= 5 or is_dbl_click:
                            GLib.timeout_add(70, self._check_primary_selection)
                        else:
                            self.detected_target = None
                        drag_dist = 0

                    # Rilascio Tasto Destro
                    if prev_right and not right:
                        GLib.idle_add(self._on_global_right_click)

                    # Rilascio Tasto Centrale (Rotellina)
                    if prev_middle and not middle:
                        GLib.idle_add(self._on_global_middle_click)

                    prev_left = left
                    prev_right = right
                    prev_middle = middle
        except Exception as ex:
            print(f"[QuickPing] Errore lettura /dev/input/mice: {ex}", file=sys.stderr)

    def _on_global_right_click(self):
        self._refresh_cursor_position()
        # Controlla subito la selezione primaria
        text = self._read_primary_selection()
        target = extract_target_from_text(text)
        if target:
            self.detected_target = target
            self.detected_at = time.time()

        has_recent = (
            self.detected_target is not None
            and (time.time() - self.detected_at) < 30.0
        )
        if has_recent:
            target = self.detected_target
            if self.mode == TriggerMode.DIRECT_ON_RIGHT_CLICK:
                self.detected_target = None
                self.open_ping_window(target)
            else:
                self.popup.show_near_cursor(target, self.cursor_x, self.cursor_y)
        return False

    def _on_global_middle_click(self):
        text = self._read_primary_selection()
        target = extract_target_from_text(text) or self.detected_target
        if target and (time.time() - self.detected_at) < 25.0:
            self.detected_target = None
            self.open_ping_window(target)
        return False

    def open_ping_window(self, target: str, start_tracert: bool = False):
        self.popup.hide_popup()
        win = PingWindow(target, start_tracert=start_tracert)
        win.show_all()


def main():
    signal.signal(signal.SIGINT, signal.SIG_DFL)
    app = QuickPingLinuxApp()
    Gtk.main()


if __name__ == "__main__":
    main()
