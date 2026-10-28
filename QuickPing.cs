using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

namespace QuickPing
{
    static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private static Mutex _mutex;

        [STAThread]
        static void Main()
        {
            // Se c'è già un'istanza precedente di QuickPing attiva, la chiude e la sostituisce con questa aggiornata
            try
            {
                int currentPid = Process.GetCurrentProcess().Id;
                foreach (Process p in Process.GetProcessesByName("QuickPing"))
                {
                    if (p.Id != currentPid)
                    {
                        try { p.Kill(); p.WaitForExit(1500); } catch { }
                    }
                }
            }
            catch { }

            bool createdNew;
            _mutex = new Mutex(true, "QuickPing_SingleInstance_Mutex_v2", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("QuickPing è già in esecuzione nella barra di sistema (vicino all'orologio).",
                    "QuickPing", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new QuickPingContext());
        }
    }

    public enum TriggerMode
    {
        PopupOnRightClick = 0,     // Predefinito: Seleziona IP + Tasto Destro -> Mostra bottone "Ping <IP>"
        DirectOnRightClick = 1,    // Seleziona IP + Tasto Destro -> Apre subito la finestra di Ping
        PopupOnSelection = 2       // Appena selezioni un IP col mouse -> Mostra subito il bottone "Ping <IP>"
    }

    public class QuickPingContext : ApplicationContext
    {
        // Win32 API per Hook Mouse e Tastiera
        private const int WH_MOUSE_LL = 14;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;
        private const int WM_MBUTTONUP = 0x0208;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int x; public int y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        private const int VK_SHIFT = 0x10;
        private const int VK_CONTROL = 0x11;
        private const byte VK_C = 0x43;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        private NotifyIcon _trayIcon;
        private IntPtr _hookId = IntPtr.Zero;
        private LowLevelMouseProc _hookProc;
        private FloatingPingPopup _popup;
        private System.Windows.Forms.Timer _selectionTimer;

        // Stato selezione mouse
        private Point _lButtonDownPt;
        private int _lastLButtonUpTick;
        private Point _lastLButtonUpPt;
        private string _detectedIp;
        private DateTime _detectedAt = DateTime.MinValue;
        private bool _isCapturingClipboard = false;

        public TriggerMode CurrentMode { get; private set; }
        private ToolStripMenuItem _menuModePopupRightClick;
        private ToolStripMenuItem _menuModeDirectRightClick;
        private ToolStripMenuItem _menuModePopupSelection;
        private ToolStripMenuItem _menuAutoStart;

        private static readonly Regex Ipv4Regex = new Regex(
            @"(?<![\d\.])((?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3})(?![\d\.])",
            RegexOptions.Compiled);

        // Riconosce nomi host / domini (es. wholesale.siportal.it o https://wholesale.siportal.it/...)
        // escludendo numeri telefonici (es. 800.030.199) e indirizzi email (con @)
        private static readonly Regex HostnameRegex = new Regex(
            @"(?<![@\w\.-])(?:https?://)?((?:[a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,24})(?![@\w\.-])",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public QuickPingContext()
        {
            CurrentMode = TriggerMode.PopupOnRightClick;

            _popup = new FloatingPingPopup(this);
            // Forza la creazione dell'handle del popup in anticipo
            IntPtr dummy = _popup.Handle;

            _selectionTimer = new System.Windows.Forms.Timer();
            _selectionTimer.Interval = 85;
            _selectionTimer.Tick += OnSelectionTimerTick;

            SetupTrayIcon();
            InstallMouseHook();
        }

        private void SetupTrayIcon()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            var headerItem = new ToolStripMenuItem("⚡ QuickPing - Attivo") { Enabled = false };
            headerItem.Font = new Font(menu.Font, FontStyle.Bold);
            menu.Items.Add(headerItem);

            var manualPingItem = new ToolStripMenuItem("🔍 Ping IP dagli Appunti / Manuale...", null, (s, e) => OpenManualOrClipboardPing());
            menu.Items.Add(manualPingItem);

            menu.Items.Add(new ToolStripSeparator());

            var modeHeader = new ToolStripMenuItem("Modalità attivazione Ping:") { Enabled = false };
            menu.Items.Add(modeHeader);

            _menuModePopupRightClick = new ToolStripMenuItem("1. Seleziona IP + Tasto Destro ➔ Mostra bottone Ping (Consigliato)", null, (s, e) => SetMode(TriggerMode.PopupOnRightClick));
            _menuModeDirectRightClick = new ToolStripMenuItem("2. Seleziona IP + Tasto Destro ➔ Apri subito finestra Ping", null, (s, e) => SetMode(TriggerMode.DirectOnRightClick));
            _menuModePopupSelection = new ToolStripMenuItem("3. Appena selezioni un IP ➔ Mostra subito bottone Ping", null, (s, e) => SetMode(TriggerMode.PopupOnSelection));

            menu.Items.Add(_menuModePopupRightClick);
            menu.Items.Add(_menuModeDirectRightClick);
            menu.Items.Add(_menuModePopupSelection);
            UpdateModeMenuChecks();

            menu.Items.Add(new ToolStripSeparator());

            _menuAutoStart = new ToolStripMenuItem("Avvia automaticamente con Windows", null, ToggleAutoStart);
            _menuAutoStart.Checked = IsAutoStartEnabled();
            menu.Items.Add(_menuAutoStart);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("❌ Esci", null, (s, e) => ExitThread()));

            _trayIcon = new NotifyIcon
            {
                Icon = CreatePingIcon(),
                Text = "QuickPing - Seleziona un IP e fai Tasto Destro",
                ContextMenuStrip = menu,
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) => OpenManualOrClipboardPing();
            _trayIcon.ShowBalloonTip(3500, "QuickPing è attivo!",
                "Seleziona un indirizzo IP in qualsiasi programma e premi il Tasto Destro per avviare il Ping.",
                ToolTipIcon.Info);
        }

        private void SetMode(TriggerMode mode)
        {
            CurrentMode = mode;
            UpdateModeMenuChecks();
        }

        private void UpdateModeMenuChecks()
        {
            _menuModePopupRightClick.Checked = (CurrentMode == TriggerMode.PopupOnRightClick);
            _menuModeDirectRightClick.Checked = (CurrentMode == TriggerMode.DirectOnRightClick);
            _menuModePopupSelection.Checked = (CurrentMode == TriggerMode.PopupOnSelection);
        }

        private bool IsAutoStartEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    return key != null && key.GetValue("QuickPing") != null;
                }
            }
            catch { return false; }
        }

        private void ToggleAutoStart(object sender, EventArgs e)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return;
                    if (_menuAutoStart.Checked)
                    {
                        key.DeleteValue("QuickPing", false);
                        _menuAutoStart.Checked = false;
                    }
                    else
                    {
                        key.SetValue("QuickPing", "\"" + Application.ExecutablePath + "\"");
                        _menuAutoStart.Checked = true;
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Impossibile aggiornare l'avvio automatico: " + ex.Message, "QuickPing", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenManualOrClipboardPing()
        {
            string clipIp = ExtractIpFromText(SafeGetClipboardText());
            if (!string.IsNullOrEmpty(clipIp))
            {
                OpenPingWindow(clipIp);
            }
            else
            {
                using (var dlg = new ManualIpDialog())
                {
                    if (dlg.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.EnteredTarget))
                    {
                        OpenPingWindow(dlg.EnteredTarget.Trim());
                    }
                }
            }
        }

        public static Icon CreatePingIcon()
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (SolidBrush bg = new SolidBrush(Color.FromArgb(22, 163, 74)))
                {
                    g.FillEllipse(bg, 2, 2, 28, 28);
                }
                using (Pen ring = new Pen(Color.White, 2.5f))
                {
                    g.DrawEllipse(ring, 7, 7, 18, 18);
                }
                using (SolidBrush dot = new SolidBrush(Color.White))
                {
                    g.FillEllipse(dot, 13, 13, 6, 6);
                }
                return Icon.FromHandle(bmp.GetHicon());
            }
        }

        private void InstallMouseHook()
        {
            _hookProc = HookCallback;
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                _hookId = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                MSLLHOOKSTRUCT hookStruct = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                Point pt = new Point(hookStruct.pt.x, hookStruct.pt.y);

                if (msg == WM_LBUTTONDOWN)
                {
                    _lButtonDownPt = pt;
                    if (_popup != null && _popup.Visible)
                    {
                        if (_popup.Bounds.Contains(pt))
                        {
                            _popup.BeginInvoke(new Action(() => _popup.HandleScreenClick(pt)));
                            return CallNextHookEx(_hookId, nCode, wParam, lParam);
                        }
                        else
                        {
                            _popup.BeginInvoke(new Action(() => _popup.HidePopup()));
                        }
                    }
                }
                else if (msg == WM_LBUTTONUP)
                {
                    if (_popup != null && _popup.Visible && _popup.Bounds.Contains(pt))
                    {
                        return CallNextHookEx(_hookId, nCode, wParam, lParam);
                    }

                    int now = Environment.TickCount;
                    int dx = Math.Abs(pt.X - _lButtonDownPt.X);
                    int dy = Math.Abs(pt.Y - _lButtonDownPt.Y);
                    bool isDrag = (dx >= 5 || dy >= 4);

                    int dblDx = Math.Abs(pt.X - _lastLButtonUpPt.X);
                    int dblDy = Math.Abs(pt.Y - _lastLButtonUpPt.Y);
                    bool isDoubleClick = (now - _lastLButtonUpTick <= SystemInformation.DoubleClickTime) && (dblDx <= 12 && dblDy <= 12);
                    bool isShiftClick = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

                    _lastLButtonUpTick = now;
                    _lastLButtonUpPt = pt;

                    if (isDrag || isDoubleClick || isShiftClick)
                    {
                        _selectionTimer.Stop();
                        _selectionTimer.Start();
                    }
                    else
                    {
                        _detectedIp = null;
                    }
                }
                else if (msg == WM_RBUTTONUP)
                {
                    bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    bool hasRecentIp = !string.IsNullOrEmpty(_detectedIp) && (DateTime.UtcNow - _detectedAt).TotalSeconds < 30;

                    if (ctrlDown)
                    {
                        // Ctrl + Tasto Destro: forza cattura e apertura immediata
                        string ipToOpen = hasRecentIp ? _detectedIp : null;
                        _popup.BeginInvoke(new Action(async () =>
                        {
                            if (string.IsNullOrEmpty(ipToOpen))
                                ipToOpen = await CaptureSelectedIpNowAsync();
                            if (!string.IsNullOrEmpty(ipToOpen))
                                OpenPingWindow(ipToOpen);
                        }));
                    }
                    else if (hasRecentIp)
                    {
                        string ip = _detectedIp;
                        _popup.BeginInvoke(new Action(() =>
                        {
                            if (CurrentMode == TriggerMode.DirectOnRightClick)
                            {
                                _detectedIp = null;
                                OpenPingWindow(ip);
                            }
                            else
                            {
                                _popup.ShowNearCursor(ip, pt);
                            }
                        }));
                    }
                }
                else if (msg == WM_MBUTTONUP)
                {
                    // Tasto centrale (rotellina) su un IP appena selezionato -> apre subito il Ping!
                    bool hasRecentIp = !string.IsNullOrEmpty(_detectedIp) && (DateTime.UtcNow - _detectedAt).TotalSeconds < 25;
                    if (hasRecentIp)
                    {
                        string ip = _detectedIp;
                        _detectedIp = null;
                        _popup.BeginInvoke(new Action(() => OpenPingWindow(ip)));
                    }
                }
            }
            return CallNextHookEx(_hookId, nCode, wParam, lParam);
        }

        private async void OnSelectionTimerTick(object sender, EventArgs e)
        {
            _selectionTimer.Stop();

            // Non eseguire se i tasti del mouse sono ancora premuti
            if ((GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0)
                return;

            string ip = await CaptureSelectedIpNowAsync();
            if (!string.IsNullOrEmpty(ip))
            {
                _detectedIp = ip;
                _detectedAt = DateTime.UtcNow;

                if (CurrentMode == TriggerMode.PopupOnSelection)
                {
                    _popup.ShowNearCursor(ip, Cursor.Position);
                }
            }
            else
            {
                _detectedIp = null;
            }
        }

        private async Task<string> CaptureSelectedIpNowAsync()
        {
            if (_isCapturingClipboard) return null;

            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return null;

            StringBuilder sbClass = new StringBuilder(128);
            GetClassName(fg, sbClass, sbClass.Capacity);
            string cls = sbClass.ToString();

            // Evita di inviare Ctrl+C a Excel o console classiche dove Ctrl+C interrompe i processi
            if (cls.IndexOf("XLMAIN", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            bool isConsole = cls.IndexOf("ConsoleWindowClass", StringComparison.OrdinalIgnoreCase) >= 0
                          || cls.IndexOf("PuTTY", StringComparison.OrdinalIgnoreCase) >= 0;

            _isCapturingClipboard = true;
            try
            {
                if (isConsole)
                {
                    return ExtractIpFromText(SafeGetClipboardText());
                }

                string oldText = SafeGetClipboardText();
                SafeClearClipboard();

                SendCtrlC();

                string copied = null;
                for (int i = 0; i < 7; i++)
                {
                    await Task.Delay(20);
                    copied = SafeGetClipboardText();
                    if (!string.IsNullOrEmpty(copied))
                        break;
                }

                if (!string.IsNullOrEmpty(oldText))
                {
                    SafeSetClipboardText(oldText);
                }

                return ExtractIpFromText(copied);
            }
            finally
            {
                _isCapturingClipboard = false;
            }
        }

        private static void SendCtrlC()
        {
            bool ctrlAlreadyDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            if (!ctrlAlreadyDown)
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);

            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            if (!ctrlAlreadyDown)
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }

        private static string SafeGetClipboardText()
        {
            try
            {
                if (Clipboard.ContainsText())
                    return Clipboard.GetText();
            }
            catch { }
            return null;
        }

        private static void SafeClearClipboard()
        {
            try { Clipboard.Clear(); } catch { }
        }

        private static void SafeSetClipboardText(string text)
        {
            try { Clipboard.SetText(text); } catch { }
        }

        public static string ExtractIpFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            string cleaned = text.Trim();
            if (cleaned.Length > 140) return null;

            // 1. Cerca prima un indirizzo IPv4 valido
            Match mIp = Ipv4Regex.Match(cleaned);
            if (mIp.Success)
            {
                return mIp.Groups[1].Value;
            }

            // 2. Cerca un nome host / dominio (es. wholesale.siportal.it o https://wholesale.siportal.it)
            Match mHost = HostnameRegex.Match(cleaned);
            if (mHost.Success)
            {
                return mHost.Groups[1].Value.ToLowerInvariant();
            }

            return null;
        }

        public void OpenPingWindow(string ip, bool startTracert = false)
        {
            _popup.HidePopup();
            var win = new PingWindow(ip, startTracert);
            win.Show();
        }

        protected override void ExitThreadCore()
        {
            if (_hookId != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
            if (_trayIcon != null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
            }
            base.ExitThreadCore();
        }
    }

    // Barra fluttuante che compare sopra il cursore quando fai Tasto Destro su un IP selezionato
    public class FloatingPingPopup : Form
    {
        private readonly QuickPingContext _ctx;
        private string _currentIp;
        private Button _btnPing;
        private Button _btnTracert;
        private System.Windows.Forms.Timer _autoHideTimer;

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000008 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TOOLWINDOW
                return cp;
            }
        }

        public FloatingPingPopup(QuickPingContext ctx)
        {
            _ctx = ctx;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(24, 24, 27);
            Padding = new Padding(4);
            Size = new Size(255, 40);

            _btnPing = new Button
            {
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(22, 163, 74),
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                Cursor = Cursors.Hand,
                Location = new Point(4, 4),
                Size = new Size(176, 32),
                TextAlign = ContentAlignment.MiddleCenter
            };
            _btnPing.FlatAppearance.BorderSize = 0;

            _btnTracert = new Button
            {
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(63, 63, 70),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5f, FontStyle.Regular),
                Cursor = Cursors.Hand,
                Location = new Point(184, 4),
                Size = new Size(67, 32),
                Text = "Tracert"
            };
            _btnTracert.FlatAppearance.BorderSize = 0;

            Controls.Add(_btnPing);
            Controls.Add(_btnTracert);

            _autoHideTimer = new System.Windows.Forms.Timer { Interval = 6500 };
            _autoHideTimer.Tick += (s, e) => HidePopup();
        }

        public void HandleScreenClick(Point screenPt)
        {
            if (!Visible || string.IsNullOrEmpty(_currentIp)) return;
            Point clientPt = PointToClient(screenPt);
            string ip = _currentIp;
            HidePopup();
            if (_btnTracert.Bounds.Contains(clientPt))
            {
                _ctx.OpenPingWindow(ip, true);
            }
            else
            {
                _ctx.OpenPingWindow(ip, false);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        public void ShowNearCursor(string ip, Point cursorPt)
        {
            _currentIp = ip;
            string btnText = "⚡ Ping " + ip;
            _btnPing.Text = btnText;

            int textWidth = TextRenderer.MeasureText(btnText, _btnPing.Font).Width;
            int pingBtnWidth = Math.Max(165, Math.Min(340, textWidth + 24));
            _btnPing.Size = new Size(pingBtnWidth, 32);
            _btnTracert.Location = new Point(pingBtnWidth + 8, 4);
            Size = new Size(pingBtnWidth + 8 + _btnTracert.Width + 4, 40);
            Invalidate();

            // Posiziona appena sopra il cursore così il menu contestuale normale del browser resta sotto
            int x = cursorPt.X - 30;
            int y = cursorPt.Y - Height - 14;

            Screen scr = Screen.FromPoint(cursorPt);
            if (x < scr.WorkingArea.Left + 4) x = scr.WorkingArea.Left + 4;
            if (x + Width > scr.WorkingArea.Right - 4) x = scr.WorkingArea.Right - Width - 4;
            if (y < scr.WorkingArea.Top + 4) y = cursorPt.Y + 18;

            if (!Visible)
                Show();

            SetWindowPos(Handle, HWND_TOPMOST, x, y, Width, Height, SWP_NOACTIVATE | SWP_SHOWWINDOW);

            _autoHideTimer.Stop();
            _autoHideTimer.Start();
        }

        public void HidePopup()
        {
            _autoHideTimer.Stop();
            if (Visible)
                Hide();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen borderPen = new Pen(Color.FromArgb(34, 197, 94), 1))
            {
                e.Graphics.DrawRectangle(borderPen, 0, 0, Width - 1, Height - 1);
            }
        }
    }

    // Finestra dedicata al Ping verso l'IP selezionato
    public class PingWindow : Form
    {
        private readonly string _ip;
        private System.Windows.Forms.Timer _pingTimer;
        private bool _isPaused = false;
        private bool _isPingingNow = false;

        private int _sent = 0;
        private int _received = 0;
        private int _lost = 0;
        private long _minRtt = long.MaxValue;
        private long _maxRtt = 0;
        private long _sumRtt = 0;

        private Label _lblStatusBadge;
        private Label _lblStats;
        private RichTextBox _logBox;
        private Button _btnPause;
        private CheckBox _chkTopMost;

        public PingWindow(string ip, bool runTracertImmediately = false)
        {
            _ip = ip;
            Text = "Ping ➔ " + ip;
            Icon = QuickPingContext.CreatePingIcon();
            Size = new Size(510, 390);
            MinimumSize = new Size(430, 300);
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Color.FromArgb(18, 18, 22);
            ForeColor = Color.FromArgb(240, 240, 245);
            Font = new Font("Segoe UI", 9f);

            Point cur = Cursor.Position;
            Screen scr = Screen.FromPoint(cur);
            int x = Math.Max(scr.WorkingArea.Left + 10, Math.Min(cur.X - Width / 2, scr.WorkingArea.Right - Width - 10));
            int y = Math.Max(scr.WorkingArea.Top + 10, Math.Min(cur.Y - 40, scr.WorkingArea.Bottom - Height - 10));
            Location = new Point(x, y);

            BuildUi();

            _pingTimer = new System.Windows.Forms.Timer { Interval = 1000 };
            _pingTimer.Tick += async (s, e) => await ExecuteSinglePingAsync();
            _pingTimer.Start();

            Shown += async (s, e) =>
            {
                await ExecuteSinglePingAsync();
                if (runTracertImmediately)
                    RunTracertInLog();
            };
        }

        private void BuildUi()
        {
            Panel topPanel = new Panel
            {
                Dock = DockStyle.Top,
                Height = 76,
                BackColor = Color.FromArgb(28, 28, 34),
                Padding = new Padding(12, 8, 12, 8)
            };

            Label lblIpTitle = new Label
            {
                Text = "TARGET: " + _ip,
                Font = new Font("Consolas", 12f, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(12, 10)
            };

            int titleWidth = TextRenderer.MeasureText(lblIpTitle.Text, lblIpTitle.Font).Width;

            Button btnCopyIp = new Button
            {
                Text = "Copia",
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f),
                ForeColor = Color.LightGray,
                BackColor = Color.FromArgb(45, 45, 55),
                Size = new Size(56, 24),
                Location = new Point(Math.Min(330, 18 + titleWidth), 9),
                Cursor = Cursors.Hand
            };
            btnCopyIp.FlatAppearance.BorderSize = 0;
            btnCopyIp.Click += (s, e) =>
            {
                try { Clipboard.SetText(_ip); } catch { }
                btnCopyIp.Text = "OK!";
            };

            _lblStatusBadge = new Label
            {
                Text = "● IN CORSO...",
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(250, 204, 21),
                AutoSize = true,
                Location = new Point(390, 11),
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };

            _lblStats = new Label
            {
                Text = "Inviati: 0  |  Ricevuti: 0  |  Persi: 0 (0%)   —   Min: -  Media: -  Max: -",
                Font = new Font("Consolas", 9f),
                ForeColor = Color.FromArgb(190, 195, 210),
                AutoSize = true,
                Location = new Point(12, 44)
            };

            topPanel.Controls.Add(lblIpTitle);
            topPanel.Controls.Add(btnCopyIp);
            topPanel.Controls.Add(_lblStatusBadge);
            topPanel.Controls.Add(_lblStats);

            Panel bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = Color.FromArgb(28, 28, 34),
                Padding = new Padding(8, 6, 8, 6)
            };

            _btnPause = CreateFooterButton("⏸ Pausa", 8, 80);
            _btnPause.Click += (s, e) =>
            {
                _isPaused = !_isPaused;
                _btnPause.Text = _isPaused ? "▶ Riprendi" : "⏸ Pausa";
                AppendLog(_isPaused ? "--- Ping in pausa ---" : "--- Ping ripreso ---", Color.Gray);
            };

            Button btnReset = CreateFooterButton("🔄 Reset", 94, 72);
            btnReset.Click += (s, e) =>
            {
                _sent = _received = _lost = 0;
                _minRtt = long.MaxValue;
                _maxRtt = _sumRtt = 0;
                _logBox.Clear();
                UpdateStatsHeader(null);
            };

            Button btnTracert = CreateFooterButton("🛣️ Tracert", 172, 82);
            btnTracert.Click += (s, e) => RunTracertInLog();

            Button btnCmd = CreateFooterButton("💻 CMD", 260, 72);
            btnCmd.Click += (s, e) =>
            {
                try
                {
                    Process.Start("cmd.exe", "/k title Ping " + _ip + " && ping -t " + _ip);
                }
                catch { }
            };

            _chkTopMost = new CheckBox
            {
                Text = "📌 Primo piano",
                Checked = true,
                AutoSize = true,
                ForeColor = Color.LightGray,
                Location = new Point(352, 12),
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _chkTopMost.CheckedChanged += (s, e) => TopMost = _chkTopMost.Checked;

            bottomPanel.Controls.Add(_btnPause);
            bottomPanel.Controls.Add(btnReset);
            bottomPanel.Controls.Add(btnTracert);
            bottomPanel.Controls.Add(btnCmd);
            bottomPanel.Controls.Add(_chkTopMost);

            _logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(14, 14, 18),
                ForeColor = Color.FromArgb(74, 222, 128),
                Font = new Font("Consolas", 9.75f),
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                DetectUrls = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };

            Controls.Add(_logBox);
            Controls.Add(topPanel);
            Controls.Add(bottomPanel);
        }

        private Button CreateFooterButton(string text, int x, int width)
        {
            var btn = new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(45, 45, 55),
                ForeColor = Color.White,
                Location = new Point(x, 7),
                Size = new Size(width, 29),
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderSize = 0;
            return btn;
        }

        private async Task ExecuteSinglePingAsync()
        {
            if (_isPaused || _isPingingNow || IsDisposed) return;
            _isPingingNow = true;

            try
            {
                _sent++;
                using (Ping p = new Ping())
                {
                    byte[] buffer = new byte[32];
                    PingReply reply = await p.SendPingAsync(_ip, 2500, buffer);
                    if (IsDisposed) return;

                    string timestamp = DateTime.Now.ToString("HH:mm:ss");
                    if (reply.Status == IPStatus.Success)
                    {
                        _received++;
                        long rtt = reply.RoundtripTime;
                        if (rtt < _minRtt) _minRtt = rtt;
                        if (rtt > _maxRtt) _maxRtt = rtt;
                        _sumRtt += rtt;

                        int ttl = reply.Options != null ? reply.Options.Ttl : 0;
                        Color lineColor = rtt > 150 ? Color.FromArgb(250, 204, 21) : Color.FromArgb(74, 222, 128);
                        AppendLog(string.Format("[{0}] Risposta da {1}: bytes=32 tempo={2}ms TTL={3}",
                            timestamp, reply.Address, rtt, ttl), lineColor);

                        UpdateStatsHeader(rtt);
                    }
                    else
                    {
                        _lost++;
                        AppendLog(string.Format("[{0}] TIMEOUT / {1} ({2})", timestamp, reply.Status, _ip),
                            Color.FromArgb(248, 113, 113));
                        UpdateStatsHeader(-1);
                    }
                }
            }
            catch (Exception ex)
            {
                _lost++;
                string timestamp = DateTime.Now.ToString("HH:mm:ss");
                AppendLog(string.Format("[{0}] Errore Ping: {1}", timestamp, ex.Message), Color.FromArgb(248, 113, 113));
                UpdateStatsHeader(-1);
            }
            finally
            {
                _isPingingNow = false;
            }
        }

        private void UpdateStatsHeader(long? lastRtt)
        {
            int lossPct = _sent > 0 ? (int)Math.Round((double)_lost * 100.0 / _sent) : 0;
            string rttStats = _received > 0
                ? string.Format("Min:{0}ms Media:{1}ms Max:{2}ms", _minRtt, _sumRtt / _received, _maxRtt)
                : "Min:- Media:- Max:-";

            _lblStats.Text = string.Format("Inviati:{0} | Ricevuti:{1} | Persi:{2} ({3}%)  —  {4}",
                _sent, _received, _lost, lossPct, rttStats);

            if (lastRtt == null)
            {
                _lblStatusBadge.Text = "● IN ATTESA";
                _lblStatusBadge.ForeColor = Color.FromArgb(250, 204, 21);
            }
            else if (lastRtt >= 0)
            {
                _lblStatusBadge.Text = string.Format("● ONLINE ({0} ms)", lastRtt.Value);
                _lblStatusBadge.ForeColor = Color.FromArgb(74, 222, 128);
            }
            else
            {
                _lblStatusBadge.Text = "● TIMEOUT";
                _lblStatusBadge.ForeColor = Color.FromArgb(248, 113, 113);
            }
        }

        private void RunTracertInLog()
        {
            AppendLog(string.Format("--- Avvio TRACERT verso {0} (max 20 hop) ---", _ip), Color.FromArgb(96, 165, 250));
            Task.Run(() =>
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo("tracert.exe", "-d -h 20 -w 1000 " + _ip)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        StandardOutputEncoding = Encoding.GetEncoding(850)
                    };
                    using (Process proc = Process.Start(psi))
                    {
                        string line;
                        while ((line = proc.StandardOutput.ReadLine()) != null)
                        {
                            if (IsDisposed) break;
                            if (!string.IsNullOrWhiteSpace(line))
                            {
                                string captured = line.Trim();
                                BeginInvoke(new Action(() =>
                                    AppendLog("  [TRACERT] " + captured, Color.FromArgb(147, 197, 253))));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (!IsDisposed)
                        BeginInvoke(new Action(() => AppendLog("Errore Tracert: " + ex.Message, Color.Red)));
                }
            });
        }

        private void AppendLog(string text, Color color)
        {
            if (IsDisposed || _logBox.IsDisposed) return;
            _logBox.SelectionStart = _logBox.TextLength;
            _logBox.SelectionLength = 0;
            _logBox.SelectionColor = color;
            _logBox.AppendText(text + Environment.NewLine);
            _logBox.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _pingTimer.Stop();
            _pingTimer.Dispose();
            base.OnFormClosing(e);
        }
    }

    public class ManualIpDialog : Form
    {
        private TextBox _txtIp;
        public string EnteredTarget
        {
            get { return _txtIp.Text; }
        }

        public ManualIpDialog()
        {
            Text = "QuickPing - Inserisci IP o Host";
            Size = new Size(340, 145);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            TopMost = true;

            Label lbl = new Label { Text = "Indirizzo IP o Hostname:", Location = new Point(14, 14), AutoSize = true };
            _txtIp = new TextBox { Location = new Point(14, 38), Width = 295, Text = "8.8.8.8" };

            Button btnOk = new Button { Text = "Avvia Ping", DialogResult = DialogResult.OK, Location = new Point(145, 72), Width = 80 };
            Button btnCancel = new Button { Text = "Annulla", DialogResult = DialogResult.Cancel, Location = new Point(230, 72), Width = 79 };

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            Controls.Add(lbl);
            Controls.Add(_txtIp);
            Controls.Add(btnOk);
            Controls.Add(btnCancel);
        }
    }
}
