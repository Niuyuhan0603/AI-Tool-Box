using Microsoft.UI.Xaml;
using Serilog;
using System.Runtime.InteropServices;

namespace AI_Tool_Box.Services;

/// <summary>
/// 系统托盘图标服务（"缩小至托盘"）：
///   • 点击主窗口"关闭"（X）时若设置为"缩小至托盘"，拦截 AppWindow.Closing 并隐藏窗口（进程与所有后台任务不停止）
///   • 托盘图标右键菜单：打开主界面 / 退出
///   • 托盘图标左键单击/双击：打开主界面
/// 实现：Win32 Shell_NotifyIcon + 隐藏消息窗口（WndProc）接收托盘回调（WinUI3 无内置托盘 API）。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ILogger _logger;
    private readonly IAppSettingsService _settings;
    private readonly Window _mainWindow;

    private IntPtr _hwnd = IntPtr.Zero;
    private IntPtr _hIcon = IntPtr.Zero;
    private bool _iconAdded;
    private bool _forceExit;
    private bool _disposed;

    /// <summary>最近一次托盘恢复时间（双击去抖，避免重复 Activate 抢焦点）。</summary>
    private DateTime _lastShowUtc = DateTime.MinValue;
    private WndProcDelegate? _wndProcDelegate; // 保持引用防 GC

    private const uint WM_USER = 0x0400;
    private const uint WM_APP_TRAY_CALLBACK = WM_USER + 0x0500;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_SHOWTIP = 0x00000080;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const uint MF_STRING = 0x00000000;
    private const uint TPM_RETURNCMD = 0x00000100;
    private const uint TPM_RIGHTBUTTON = 0x00000002;
    private const uint TPM_NONOTIFY = 0x00000080;
    private const uint TPM_BOTTOMALIGN = 0x00000020;
    private const uint TPM_RIGHTALIGN = 0x00000008;
    private const int MENU_OPEN = 1;
    private const int MENU_EXIT = 2;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpdata);
    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string lpNewItem);
    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);
    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ExtractIcon(IntPtr hInst, string lpszExeFileName, int nIconIndex);
    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    // ===== 清除窗口 TOPMOST 标志（避免恢复窗口后长期置顶遮挡桌面点击）=====
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    /// <summary>初始化托盘服务。</summary>
    public TrayIconService(ILogger logger, IAppSettingsService settings, Window mainWindow)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _mainWindow = mainWindow ?? throw new ArgumentNullException(nameof(mainWindow));
    }

    /// <summary>创建消息窗口 + 添加托盘图标 + 挂钩关闭拦截。</summary>
    public void Initialize()
    {
        try
        {
            CreateMessageWindow();
            AddTrayIcon();
            _mainWindow.AppWindow.Closing += OnWindowClosing;
            _logger.Information("托盘服务已就绪（关闭窗口行为：{Behavior}）",
                _settings.CloseBehavior == "exit" ? "直接退出" : "缩小至托盘");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "托盘初始化失败");
        }
    }

    /// <summary>关闭窗口拦截：设置为"缩小至托盘"时取消关闭并隐藏窗口（后台进程不停止）。</summary>
    private void OnWindowClosing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_forceExit)
        {
            return; // 托盘菜单"退出"路径：放行正常关闭
        }
        if (_settings.CloseBehavior != "exit")
        {
            args.Cancel = true;
            sender.Hide();
            _logger.Information("已缩小至托盘（后台进程保持运行，双击托盘图标可恢复）");
        }
    }

    /// <summary>托盘图标回调窗口过程。</summary>
    private IntPtr TrayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY_CALLBACK)
        {
            uint evt = unchecked((uint)lParam.ToInt64() & 0xFFFF);
            if (evt == WM_RBUTTONUP)
            {
                ShowTrayMenu();
            }
            else if (evt == WM_LBUTTONDBLCLK || evt == WM_LBUTTONUP)
            {
                ShowMainWindow();
            }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    /// <summary>托盘右键菜单（Win32 弹出菜单）。</summary>
    private void ShowTrayMenu()
    {
        try
        {
            var menu = CreatePopupMenu();
            AppendMenuW(menu, MF_STRING, MENU_OPEN, "打开主界面");
            AppendMenuW(menu, MF_STRING, MENU_EXIT, "退出");
            GetCursorPos(out var pt);
            int cmd = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_NONOTIFY | TPM_BOTTOMALIGN | TPM_RIGHTALIGN,
                pt.x, pt.y, _hwnd, IntPtr.Zero);
            DestroyMenu(menu);

            if (cmd == MENU_OPEN)
            {
                ShowMainWindow();
            }
            else if (cmd == MENU_EXIT)
            {
                ExitApplication();
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "托盘右键菜单失败");
        }
    }

    /// <summary>恢复显示主窗口并激活（不设为常住置顶；双击托盘只恢复一次，避免反复抢焦点）。</summary>
    private void ShowMainWindow()
    {
        // 双击托盘图标会依次触发 LBUTTONUP + LBUTTONDBLCLK + LBUTTONUP → 300ms 内只处理一次
        if ((DateTime.UtcNow - _lastShowUtc).TotalMilliseconds < 300) return;
        _lastShowUtc = DateTime.UtcNow;

        try
        {
            var appWindow = _mainWindow.AppWindow;
            if (!appWindow.IsVisible)
            {
                appWindow.Show();
            }
            _mainWindow.Activate();
            // 防御：清除可能遗留的 TOPMOST 标志（旧版本曾调用 MoveInZOrderAtTop，会导致窗口长期置顶遮挡点击）
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow);
                SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            catch { /* 清除置顶失败不影响恢复显示 */ }
            _logger.Information("托盘：已恢复主界面");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "恢复主界面失败");
        }
    }

    /// <summary>托盘菜单"退出"：放行关闭 → 走正常退出流程（Window.Closed → 清理内嵌进程 → 进程退出）。</summary>
    private void ExitApplication()
    {
        _forceExit = true;
        RemoveTrayIcon();
        _logger.Information("托盘菜单：正在退出程序");
        try
        {
            _mainWindow.AppWindow.Destroy();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "销毁主窗口失败，强制退出");
        }
        // 兜底：确保进程退出
        if (Application.Current is not null)
        {
            Application.Current.Exit();
        }
    }

    private void CreateMessageWindow()
    {
        var hInst = GetModuleHandleW(null);
        _wndProcDelegate = TrayWndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = hInst,
            lpszClassName = "AI_Tool_Box_TrayWindow"
        };
        ushort atom = RegisterClassExW(ref wc);
        _logger.Information("托盘消息窗口类注册：atom={Atom} err={Error}", atom, Marshal.GetLastWin32Error());
        _hwnd = CreateWindowExW(0, wc.lpszClassName, "AI_Tool_Box_Tray",
            0, 0, 0, 1, 1, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        _logger.Information("托盘消息窗口句柄：{Hwnd} err={Error}", _hwnd, Marshal.GetLastWin32Error());
    }

    private void AddTrayIcon()
    {
        if (_iconAdded) return;
        try
        {
            var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                      ?? System.Reflection.Assembly.GetExecutingAssembly().Location;
            _hIcon = ExtractIcon(IntPtr.Zero, exe, 0);
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = ExtractIcon(GetModuleHandleW(null), "AI-Tool-Box.exe", 0);
            }
            // 兜底：从 exe 模块加载 IDI_APPLICATION（ExtractIcon 偶发失败时保证有图标）
            if (_hIcon == IntPtr.Zero)
            {
                _hIcon = LoadIcon(hInstOrNull(), new IntPtr(32512));
            }
            _logger.Information("托盘图标句柄：{Icon}（exe={Exe}）", _hIcon, exe);

            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_APP_TRAY_CALLBACK,
                hIcon = _hIcon,
                szTip = "AI-Tool-Box"
            };
            _iconAdded = Shell_NotifyIconW(NIM_ADD, ref data);
            _logger.Information("托盘图标添加：{Result} err={Error} hwnd={Hwnd}",
                _iconAdded, Marshal.GetLastWin32Error(), _hwnd);
            if (_iconAdded)
            {
                data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
                data.uVersion = NOTIFYICON_VERSION_4;
                Shell_NotifyIconW(NIM_SETVERSION, ref data);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "添加托盘图标失败");
        }
    }

    private static IntPtr hInstOrNull()
    {
        try { return GetModuleHandleW(null); } catch { return IntPtr.Zero; }
    }

    private void RemoveTrayIcon()
    {
        if (!_iconAdded) return;
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = 1
            };
            Shell_NotifyIconW(NIM_DELETE, ref data);
            _iconAdded = false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "移除托盘图标失败");
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    /// <summary>释放托盘资源（应用退出时调用）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RemoveTrayIcon();
    }
}
