# ⚡ QuickPing (Windows & Linux X11/Wayland)

**QuickPing** è un'utility desktop leggerissima che permette di eseguire istantaneamente **Ping** e **Traceroute** verso qualsiasi **indirizzo IPv4** o **Nome Host / Dominio** presente a schermo (in browser web, gestionali aziendali, PDF, fogli di calcolo, terminali), semplicemente selezionandolo con il mouse e premendo il **Tasto Destro** (o il tasto centrale/rotellina).

---

## ✨ Caratteristiche Principali

* **Universale (Qualsiasi Sorgente a Schermo)**: funziona su testo selezionato in qualunque applicazione senza bisogno di estensioni browser dedicate.
* **Estrazione Intelligente (IPv4 + Hostname/URL)**:
  * Estrae automaticamente gli indirizzi IPv4 anche se selezionati insieme a subnet o testo adiacente (es. `88.147.3.5/32` ➔ `88.147.3.5`).
  * Riconosce nomi host e domini anche dentro URL completi (es. `https://wholesale.siportal.it/login` ➔ `wholesale.siportal.it`).
  * **Filtro anti-falsi positivi**: ignora automaticamente numeri di telefono separati da punti (es. `800.030.199`, `09.5832.5832`) e indirizzi email (es. `utente@dominio.it`).
* **3 Modalità di Attivazione (selezionabili dall'icona nella System Tray)**:
  1. **Seleziona + Tasto Destro ➔ Mostra bottone `⚡ Ping <Target>`** *(Predefinita — non interferisce con il normale menu contestuale "Copia" delle applicazioni)*.
  2. **Seleziona + Tasto Destro ➔ Apri subito finestra Ping** *(Apertura immediata senza passaggi intermedi)*.
  3. **Appena selezioni un IP/Host ➔ Mostra subito bottone `⚡ Ping`** *(Senza nemmeno premere il tasto destro)*.
* **Scorciatoie Rapide**:
  * **Clic Centrale (Rotellina)** o **`Ctrl + Tasto Destro`** sul testo selezionato aprono direttamente la finestra di Ping.
* **Finestra di Ping Dedicata (Dark Theme)**:
  * **Sempre in primo piano (`📌 Primo piano`)**: resta visibile sopra il browser o il gestionale mentre lavori.
  * **Statistiche Live**: stato `● ONLINE (ms)` / `● TIMEOUT`, pacchetti inviati/ricevuti/persi (`%`) e latenza `Min / Media / Max`.
  * **Log con Timestamp (`[HH:mm:ss]`) e Colori Semaforici**: verde (risposta normale), giallo (latenza > 150ms), rosso (timeout/scaduto).
  * **Strumenti Integrati**: `⏸ Pausa / ▶ Riprendi`, `🔄 Reset`, `🛣️ Tracert` (traceroute integrato nella finestra) e apertura rapida nel terminale di sistema (`💻 CMD` / `Terminale`).

---

## 📁 Struttura del Repository

```text
QuickPing/
├── QuickPing.cs          # Codice sorgente C# nativo per Windows (zero dipendenze esterne)
├── QuickPing.exe         # Eseguibile Windows pronto all'uso (~34 KB)
├── linux/
│   ├── quickping.py      # Applicazione nativa Linux (Python 3 + GTK3, X11 & Wayland)
│   └── install.sh        # Installer automatico per Zorin OS / Ubuntu e CachyOS / Arch
├── README.md             # Documentazione del progetto
├── GEMINI.md             # Contesto tecnico e storico del progetto per Antigravity AI
└── AGENTS.md             # Regole e architettura per assistenti AI
```

---

## 🪟 Utilizzo e Compilazione su Windows

### Esecuzione immediata
Avvia direttamente **[QuickPing.exe](QuickPing.exe)**. L'applicazione si posiziona nella System Tray (accanto all'orologio di Windows).
* Per attivare l'avvio automatico all'accensione del PC: fai **Tasto Destro sull'icona verde nella System Tray** ➔ spunta **"Avvia automaticamente con Windows"**.

### Ricompilazione da sorgente (senza installare Visual Studio)
Il programma usa il compilatore nativo di .NET Framework già incluso in Windows 10/11:

```powershell
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /target:winexe /optimize+ /out:QuickPing.exe QuickPing.cs
```

*(Nota: se `QuickPing.exe` è già in esecuzione, all'avvio la nuova istanza chiude e sostituisce automaticamente il processo precedente).*

---

## 🐧 Installazione e Utilizzo su Linux (Zorin OS 18.1 & CachyOS)

La versione Linux è progettata per funzionare **sia sotto X11 che sotto Wayland** (testata per **Zorin OS 18.1** basato su Ubuntu/GNOME e **CachyOS** basato su Arch/KDE/Hyprland/GNOME).

### Come funziona sotto X11 e Wayland
1. **Primary Selection Buffer**: legge automaticamente il testo evidenziato col mouse tramite `GTK Primary Clipboard`, `wl-paste --primary` (Wayland) e `xclip` (X11), senza mai sovrascrivere gli appunti di `Ctrl+C`.
2. **Rilevamento Click Globale (`/dev/input/mice`)**: intercetta il rilascio del Tasto Destro e del Tasto Centrale anche sopra finestre Wayland native tramite una regola `udev` in sola lettura.
3. **Posizionamento e Always-on-Top**: utilizza GTK3 con bridge XWayland/X11 (`GDK_BACKEND=x11,wayland`) per consentire il posizionamento del bottone fluttuante vicino al cursore e l'opzione `📌 Primo piano`.

### Installazione rapida
Apri un terminale nella cartella `linux/` ed esegui:

```bash
cd linux
chmod +x install.sh
./install.sh
```

Lo script `install.sh` provvederà automaticamente a:
1. Rilevare il gestore pacchetti (`apt` su Zorin OS / Ubuntu oppure `pacman` su CachyOS / Arch) e installare le dipendenze leggere (`python3-gi`, `gtk3`, `wl-clipboard`, `xclip`, `traceroute`, `libayatana-appindicator`).
2. Configurare la regola udev `/etc/udev/rules.d/99-quickping-mice.rules` per abilitare subito il tasto destro globale senza riavviare.
3. Installare l'eseguibile in `~/.local/bin/quickping`, creare la voce nel menu applicazioni e attivare l'avvio automatico in `~/.config/autostart/quickping.desktop`.
