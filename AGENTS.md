# AGENTS.md

Consulta **[GEMINI.md](./GEMINI.md)** per l'architettura completa del progetto **QuickPing**, lo storico delle decisioni tecniche e le istruzioni di compilazione e manutenzione per **Windows (`QuickPing.cs`)** e **Linux X11/Wayland (`linux/quickping.py` & `linux/install.sh`)**.

## Regole Rapide per gli Agenti
1. **Windows (`QuickPing.cs`)**: mantenere la sintassi compatibile con **C# 5** (`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`) così il programma compila nativamente su qualsiasi installazione di Windows 10/11 senza dipendenze esterne. Se `QuickPing.exe` è in esecuzione durante la ricompilazione, rinominare prima il binario attivo in `QuickPing.old.exe` e poi compilare il nuovo `QuickPing.exe`.
2. **Linux (`linux/quickping.py`, `linux/install.sh`)**: preservare sempre i terminatori di riga Unix (`LF`, `\n`) e mantenere il supporto simultaneo per **X11** e **Wayland** su **Zorin OS 18.1** (`apt`) e **CachyOS** (`pacman`).
3. **Parità di Funzionalità**: ogni modifica alla logica di estrazione di IPv4/Hostname deve essere applicata sia a `QuickPing.cs` sia a `linux/quickping.py`.
