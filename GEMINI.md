# GEMINI.md — Contesto di Progetto e Storico Decisioni (QuickPing)

Questo file (insieme a `AGENTS.md`) è l'equivalente di `CLAUDE.md` per **Google Antigravity**: viene caricato automaticamente dall'agente AI quando apri questa cartella come workspace per avere memoria completa di quanto progettato e deciso.

---

## 🎯 Obiettivo del Progetto
Creare un tool leggero, immediato e multipiattaforma (**Windows 10/11** e **Linux X11 + Wayland** per **Zorin OS 18.1** e **CachyOS**) che permetta di:
1. Selezionare con il mouse un qualsiasi **indirizzo IPv4** (es. `88.147.3.5` o `88.147.3.5/32`) o **Nome Host / Dominio** (es. `wholesale.siportal.it` o `https://wholesale.siportal.it`) da qualsiasi applicazione a schermo.
2. Premere il **Tasto Destro** del mouse (o la Rotellina centrale / `Ctrl + Tasto Destro`) e visualizzare un pulsante rapido fluttuante **`⚡ Ping <target>`** (oppure aprire direttamente la finestra di Ping).
3. Eseguire il Ping continuo in una finestrella compatta, dark-theme, sempre in primo piano (`📌 Primo piano`), con statistiche in tempo reale (pacchetti inviati/ricevuti/persi, RTT min/avg/max), timestamp per ogni risposta e funzione `Tracert` integrata.

---

## 🏗️ Architettura e Scelte Tecniche

### 1. Versione Windows (`QuickPing.cs` ➔ `QuickPing.exe`)
* **Compilatore**: Compilato con il compilatore C# 5 di sistema (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) per produrre un singolo eseguibile nativo di ~34 KB con **zero dipendenze esterne**.
* **IMPORTANTE per modifiche future a `QuickPing.cs`**:
  * Mantenere la compatibilità con la sintassi **C# 5** (non usare *expression-bodied properties* `=>` o string interpolation `$""`, usare `string.Format` e `{ get { return ...; } }`) così `csc.exe` integrato in Windows compila sempre senza errori.
  * Comando di compilazione:
    ```powershell
    & "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /optimize+ /out:"C:\Users\Marcello\.gemini\antigravity\scratch\QuickPing\QuickPing.exe" "C:\Users\Marcello\.gemini\antigravity\scratch\QuickPing\QuickPing.cs"
    ```
* **Meccanismo Hook & Appunti su Windows**:
  * Usa un Low-Level Mouse Hook (`WH_MOUSE_LL` / `SetWindowsHookEx`).
  * Al rilascio del tasto sinistro (`WM_LBUTTONUP`) dopo un trascinamento, doppio/triplo clic o `Shift+Clic`, attende 85ms e legge la selezione in modo asincrono (`await Task.Delay(20)`) salvando e ripristinando il contenuto originale della Clipboard, evitando finestre di terminale (`ConsoleWindowClass`, `PuTTY`) o Excel (`XLMAIN`) per non inviare `SIGINT` o disturbare selezioni di celle.
  * Il popup fluttuante (`FloatingPingPopup`) usa gli stili `WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW` e gestisce il clic direttamente nell'hook `WM_LBUTTONDOWN` (`HandleScreenClick`), garantendo che il clic sul pulsante `⚡ Ping` funzioni al primo colpo anche mentre il menu contestuale nativo del browser (`TrackPopupMenu`) è aperto.
  * All'avvio, `Main()` termina eventuali istanze precedenti di `QuickPing` così un doppio clic sul nuovo eseguibile aggiorna istantaneamente l'istanza in memoria.

### 2. Versione Linux (`linux/quickping.py` + `linux/install.sh`)
* **Distribuzioni Target**: **Zorin OS 18.1** (base Ubuntu/GNOME) e **CachyOS** (base Arch Linux con KDE Plasma / Hyprland / GNOME).
* **Compatibilità X11 + Wayland**:
  * **Lettura selezione**: usa il buffer nativo `PRIMARY` di Linux (che si popola automaticamente quando si seleziona testo col mouse, senza inviare `Ctrl+C`) interrogando in cascata `Gtk.Clipboard.get(Gdk.SELECTION_PRIMARY)`, `wl-paste --primary --no-newline` (Wayland) e `xclip -o -selection primary` (X11).
  * **Rilevamento globale click mouse su Wayland e X11**: legge i pacchetti a 3 byte da `/dev/input/mice` (configurato in lettura per l'utente tramite regola `udev` `/etc/udev/rules.d/99-quickping-mice.rules` da `install.sh`). Se `/dev/input/mice` non è ancora accessibile, passa automaticamente in fallback alla modalità "Mostra bottone appena selezioni un IP/Host".
  * **Posizionamento e Always-on-Top**: imposta `GDK_BACKEND="x11,wayland"` per sfruttare XWayland quando presente, consentendo `window.move(x, y)` e `window.set_keep_above(True)` anche nelle sessioni Wayland.
  * **Codifica file**: assicurarsi sempre che `linux/quickping.py` e `linux/install.sh` abbiano terminatori di riga Unix (`LF`, `\n`) e non `CRLF`.

### 3. Regole di Estrazione Target (Regex IPv4 & Hostname)
* **IPv4**: `(?<![\d\.])((?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3})(?![\d\.])`
* **Hostname / Dominio**: `(?<![@\w\.-])(?:https?://)?((?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,24})(?![@\w\.-])`
  * Esclude numeri di telefono con punti (es. `800.030.199`, `09.5832.5832`) perché richiede un TLD alfabetico finale `[a-zA-Z]{2,24}`.
  * Esclude indirizzi email (es. `rosetta.cocuzza@siportal.it`) grazie ai lookaround negativi su `@`.
