using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Shapes;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using MessageBox = System.Windows.MessageBox;

namespace BiliPauseWpf
{
    public partial class MainWindow : Window
    {
        private IntPtr _gameHwnd, _videoHwnd, _renderHwnd;
        private string _gameExe = "", _videoExe = "";
        private IntPtr _hook = IntPtr.Zero;
        private IntPtr _pickerHook = IntPtr.Zero;
        private bool _running;
        private bool _pickingGame, _pickingVideo;
        private Thread? _hookThread;
        private volatile bool _stopHook;
        private string _cfgPath = "";
        private bool _minimizeToTray;
        private bool _askedTrayPref;
        private NotifyIcon? _notifyIcon;
        private static LowLevelMouseProc? _hookDelegate, _pickerDelegate;

        private const int WH_MOUSE_LL = 14;
        private const int WM_XBUTTONDOWN = 0x020B;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int VK_SPACE = 0x20, VK_LEFT = 0x25, VK_CONTROL = 0x11;
        private const int XBUTTON1 = 0x0001, XBUTTON2 = 0x0002;
        private const uint GA_ROOTOWNER = 3;
        private const string CHROMIUM_RENDER = "Chrome_RenderWidgetHostHWND";

        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern short GetKeyState(int nVirtKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr hWndParent, IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(int x, int y);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern IntPtr GetFocus();
        [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
        [DllImport("user32.dll")] private static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
        [DllImport("user32.dll")] private static extern int GetWindowTextLengthW(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int nExitCode);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr hObject);
        [DllImport("kernel32.dll")] private static extern IntPtr LoadLibraryW(string lpFileName);
        [DllImport("kernel32.dll")] private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr hLibModule);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr hProcess, uint dwFlags, System.Text.StringBuilder lpExeName, ref uint lpdwSize);
        [DllImport("psapi.dll", SetLastError = true)] private static extern uint GetProcessImageFileNameW(IntPtr hProcess, System.Text.StringBuilder lpImageFileName, uint nSize);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int pt_x, pt_y; }

        public MainWindow()
        {
            InitializeComponent();
            _cfgPath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "bili_pause.ini");
            SetupTrayIcon();
            LoadConfig();
            if (_gameHwnd == IntPtr.Zero || _videoHwnd == IntPtr.Zero) AutoDetect();
            UpdateDisplay();
            DrawDot();
            UpdatePlaceholders();
            Closed += (_, _) => { if (_running) DoStop(); _notifyIcon?.Dispose(); };
        }

        static System.Drawing.Icon LoadIcon()
        {
            var icoPath = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Environment.ProcessPath!)!, "app.ico");
            if (File.Exists(icoPath)) return new System.Drawing.Icon(icoPath);
            return System.Drawing.SystemIcons.Application;
        }

        void SetupTrayIcon()
        {
            _notifyIcon = new NotifyIcon
            {
                Icon = LoadIcon(),
                Text = "BiliPause",
                Visible = false
            };
            _notifyIcon.DoubleClick += (_, _) => ShowFromTray();
            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("Show", null, (_, _) => ShowFromTray());
            menu.Items.Add("Exit", null, (_, _) => { DoStop(); _notifyIcon.Dispose(); Application.Current.Shutdown(); });
            _notifyIcon.ContextMenuStrip = menu;
        }

        void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            _notifyIcon!.Visible = false;
        }

        void SetStatus(string t) => txtStatus.Text = t;

        void UpdatePlaceholders()
        {
            phGame.Visibility = string.IsNullOrEmpty(txtGame.Text) ? Visibility.Visible : Visibility.Collapsed;
            phVideo.Visibility = string.IsNullOrEmpty(txtVideo.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        void DrawDot()
        {
            dotCanvas.Children.Clear();
            var c = _running ? Colors.LimeGreen : Colors.Gray;
            dotCanvas.Children.Add(new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(c) });
        }

        bool IsValid(IntPtr h) => h != IntPtr.Zero && IsWindow(h) && IsWindowVisible(h);

        static string GetWinText(IntPtr h) { int l = GetWindowTextLengthW(h); if (l == 0) return ""; var sb = new System.Text.StringBuilder(l + 1); GetWindowTextW(h, sb, l + 1); return sb.ToString(); }
        static string GetWinClass(IntPtr h) { var sb = new System.Text.StringBuilder(256); GetClassNameW(h, sb, 256); return sb.ToString(); }

        static string GetExeName(IntPtr hwnd)
        {
            GetWindowThreadProcessId(hwnd, out uint pid); if (pid == 0) return "";
            IntPtr hProc = OpenProcess(0x0410, false, pid); if (hProc == IntPtr.Zero) hProc = OpenProcess(0x1000, false, pid);
            if (hProc == IntPtr.Zero) return "";
            string? r = null; var sb = new System.Text.StringBuilder(260); uint s = 260;
            if (QueryFullProcessImageNameW(hProc, 0, sb, ref s)) r = System.IO.Path.GetFileName(sb.ToString());
            else { uint l = GetProcessImageFileNameW(hProc, sb, 260); if (l > 0) r = System.IO.Path.GetFileName(sb.ToString()); }
            CloseHandle(hProc); return r ?? "";
        }

        IntPtr FindRender(IntPtr p) { IntPtr f = IntPtr.Zero; EnumChildWindows(p, (h, _) => { if (GetWinClass(h) == CHROMIUM_RENDER) { f = h; return false; } return true; }, IntPtr.Zero); return f; }

        void LoadConfig()
        {
            if (!File.Exists(_cfgPath)) return;
            foreach (var l in File.ReadAllLines(_cfgPath))
            { if (l.StartsWith("GameExe=")) _gameExe = l[8..]; if (l.StartsWith("VideoExe=")) _videoExe = l[9..]; }
            if (_gameExe != "") _gameHwnd = FindByExe(_gameExe);
            if (_videoExe != "") { _videoHwnd = FindByExe(_videoExe); if (_videoHwnd != IntPtr.Zero) _renderHwnd = FindRender(_videoHwnd); }
        }

        void SaveConfig()
        {
            var sb = new System.Text.StringBuilder();
            if (_gameExe != "") sb.AppendLine($"GameExe={_gameExe}");
            if (_videoExe != "") sb.AppendLine($"VideoExe={_videoExe}");
            File.WriteAllText(_cfgPath, sb.ToString());
        }

        IntPtr FindByExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return IntPtr.Zero;
            var t = exe.ToLowerInvariant(); IntPtr f = IntPtr.Zero;
            EnumWindows((h, _) => { if (f != IntPtr.Zero) return false; if (!IsWindowVisible(h)) return true; var e = GetExeName(h); if (e.ToLowerInvariant() == t) { f = h; return false; } return true; }, IntPtr.Zero);
            return f;
        }

        void AutoDetect()
        {
            var gk = new[] { "\u539f\u795e", "genshin", "yuanshen" };
            var vk = new[] { "bilibili", "\u54d4\u54e9\u54d4\u54e9", "blbl" };
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                var t = GetWinText(h).ToLowerInvariant(); if (t == "") return true;
                if (_gameHwnd == IntPtr.Zero) foreach (var k in gk) if (t.Contains(k)) { _gameHwnd = h; _gameExe = GetExeName(h); break; }
                if (_videoHwnd == IntPtr.Zero) foreach (var k in vk) if (t.Contains(k)) { _videoHwnd = h; _renderHwnd = FindRender(h); _videoExe = GetExeName(h); break; }
                return true;
            }, IntPtr.Zero);
            SaveConfig();
        }

        void UpdateDisplay()
        {
            txtGame.Text = IsValid(_gameHwnd) ? GetWinText(_gameHwnd) : "";
            txtVideo.Text = IsValid(_videoHwnd) ? GetWinText(_videoHwnd) : "";
            UpdatePlaceholders();
            if (IsValid(_gameHwnd) && IsValid(_videoHwnd)) SetStatus("Ready - press Start");
            else if (!IsValid(_gameHwnd) && !IsValid(_videoHwnd)) SetStatus("Select Game and Video, then Start");
            else if (!IsValid(_gameHwnd)) SetStatus("Video found.  Select Game.");
            else SetStatus("Game found.  Select Video.");
        }

        void SendKey(uint vk, IntPtr dn, IntPtr up)
        {
            var d = IsValid(_renderHwnd) ? _renderHwnd : _videoHwnd;
            if (!IsValid(d)) return;
            uint ot = GetCurrentThreadId(); GetWindowThreadProcessId(d, out uint tt);
            bool at = AttachThreadInput(ot, tt, true);
            var pf = GetFocus(); SetFocus(d);
            SendMessageW(d, WM_KEYDOWN, (IntPtr)vk, dn);
            SendMessageW(d, WM_KEYUP, (IntPtr)vk, up);
            if (pf != IntPtr.Zero) SetFocus(pf);
            if (at) AttachThreadInput(ot, tt, false);
        }

        void SendSpace() => SendKey(VK_SPACE, (IntPtr)0x00390001, unchecked((IntPtr)0xC0390001));
        void SendLeft() => SendKey(VK_LEFT, (IntPtr)0x004B0001, unchecked((IntPtr)0xC04B0001));

        IntPtr HookCallback(int n, IntPtr w, IntPtr l)
        {
            if (n >= 0 && (int)w == WM_XBUTTONDOWN)
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                int btn = (int)(info.mouseData >> 16) & 0xFFFF;
                bool ctrl = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                var fg = GetForegroundWindow();
                bool inGame = IsValid(_gameHwnd) && (fg == _gameHwnd || IsChild(_gameHwnd, fg));
                bool hd = false;
                if (btn == XBUTTON1) { if (ctrl || inGame) { SendSpace(); hd = true; } }
                else if (btn == XBUTTON2) { if (ctrl || inGame) { SendLeft(); hd = true; } }
                if (hd) return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        void HookThreadProc()
        {
            _hookDelegate = HookCallback;
            _hook = SetWindowsHookExW(WH_MOUSE_LL, _hookDelegate, IntPtr.Zero, 0);
            if (_hook == IntPtr.Zero) { Dispatcher.Invoke(() => SetStatus($"Hook failed ({Marshal.GetLastWin32Error()}). Admin?")); return; }
            Dispatcher.Invoke(() => { SetStatus("Active"); DrawDot(); });
            MSG msg; while (!_stopHook && GetMessageW(out msg, IntPtr.Zero, 0, 0) != 0) { }
            if (_hook != IntPtr.Zero) { UnhookWindowsHookEx(_hook); _hook = IntPtr.Zero; }
            _running = false;
            Dispatcher.Invoke(() => { SetStatus("Stopped"); DrawDot(); btnToggle.Content = "Start"; btnToggle.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#22C55E")); });
        }

        void StartPicker(bool isGame)
        {
            _pickingGame = isGame; _pickingVideo = !isGame;
            _pickerDelegate = PickerCallback;
            _pickerHook = SetWindowsHookExW(WH_MOUSE_LL, _pickerDelegate, IntPtr.Zero, 0);
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            timer.Tick += (_, _) => { CancelPicker(); timer.Stop(); }; timer.Start();
            SetStatus(isGame ? "Click the Genshin window (5s)..." : "Click the Bilibili window (5s)...");
        }

        IntPtr PickerCallback(int n, IntPtr w, IntPtr l)
        {
            int msg = (int)w;
            if (n >= 0 && (msg == WM_LBUTTONDOWN || msg == WM_RBUTTONDOWN))
            {
                var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(l);
                var cl = WindowFromPoint(info.pt.x, info.pt.y);
                var tp = GetAncestor(cl, GA_ROOTOWNER); if (tp == IntPtr.Zero) tp = cl;
                Dispatcher.Invoke(() => PickerDone(tp));
                if (_pickerHook != IntPtr.Zero) { UnhookWindowsHookEx(_pickerHook); _pickerHook = IntPtr.Zero; }
                return (IntPtr)1;
            }
            return CallNextHookEx(IntPtr.Zero, n, w, l);
        }

        void PickerDone(IntPtr h)
        {
            var t = GetWinText(h); var e = GetExeName(h);
            if (_pickingGame) { _gameHwnd = h; _gameExe = e; txtGame.Text = t; }
            else { _videoHwnd = h; _videoExe = e; _renderHwnd = FindRender(h); txtVideo.Text = t; }
            _pickingGame = _pickingVideo = false; SaveConfig(); UpdatePlaceholders();
            SetStatus("Saved.  Start when ready.");
        }

        void CancelPicker()
        {
            if (_pickerHook != IntPtr.Zero) { UnhookWindowsHookEx(_pickerHook); _pickerHook = IntPtr.Zero; }
            _pickingGame = _pickingVideo = false;
            SetStatus("Selection cancelled.");
        }

        // ── Button handlers ──
        void BtnSelectGame(object s, RoutedEventArgs e) => StartPicker(true);
        void BtnSelectVideo(object s, RoutedEventArgs e) => StartPicker(false);
        void TxtGame_TextChanged(object s, TextChangedEventArgs e) => UpdatePlaceholders();
        void TxtVideo_TextChanged(object s, TextChangedEventArgs e) => UpdatePlaceholders();

        void BtnToggle(object s, RoutedEventArgs e)
        {
            if (_running) DoStop(); else DoStart();
        }

        void DoStart()
        {
            if (!IsValid(_gameHwnd)) { SetStatus("Select the Game window first."); return; }
            if (!IsValid(_videoHwnd)) { SetStatus("Select the Video window first."); return; }
            _running = true; _stopHook = false;
            _hookThread = new Thread(HookThreadProc) { IsBackground = true };
            _hookThread.Start();
            btnToggle.Content = "Stop";
            btnToggle.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#EF4444"));
            DrawDot();
        }

        void DoStop()
        {
            _stopHook = true; PostQuitMessage(0);
            _running = false;
            btnToggle.Content = "Start";
            btnToggle.Background = new SolidColorBrush((Color)System.Windows.Media.ColorConverter.ConvertFromString("#22C55E"));
            SetStatus("Stopped"); DrawDot();
        }

        void BtnMinimize(object s, RoutedEventArgs e)
        {
            if (!_askedTrayPref)
            {
                var result = MessageBox.Show("Minimize to system tray?\n\nYes — hide to tray   No — normal minimize",
                    "BiliPause", MessageBoxButton.YesNo, MessageBoxImage.Question);
                _minimizeToTray = result == MessageBoxResult.Yes;
                _askedTrayPref = true;
            }
            if (_minimizeToTray) { Hide(); _notifyIcon!.Visible = true; }
            else WindowState = WindowState.Minimized;
        }

        void BtnClose(object s, RoutedEventArgs e)
        {
            if (_running) DoStop();
            _notifyIcon?.Dispose();
            Application.Current.Shutdown();
        }

        void Window_StateChanged(object s, EventArgs e)
        {
            if (WindowState == WindowState.Minimized && _minimizeToTray)
            {
                Hide();
                _notifyIcon!.Visible = true;
            }
        }
    }
}