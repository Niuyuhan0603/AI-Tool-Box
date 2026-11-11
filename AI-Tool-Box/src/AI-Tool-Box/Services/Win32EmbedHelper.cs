using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>外部 Win32 窗口嵌入宿主：把外部进程的顶层窗口改为子窗口并放入 WinUI 页面容器区域。
/// 用于 JiYuTrainer（Sciter UI）与 MusicPlayer2（MFC 对话框）的进程内嵌。</summary>
public static class Win32EmbedHelper
{
    // ===== GWL/GWL_EXSTYLE 索引 + 样式常量 =====
    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_MINIMIZEBOX = 0x00020000L;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    private const long WS_SYSMENU = 0x00080000L;
    private const long WS_CLIPSIBLINGS = 0x04000000L;
    private const long WS_CLIPCHILDREN = 0x02000000L;
    private const long WS_VISIBLE = 0x10000000L;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_NOPARENTNOTIFY = 0x00000004L;

    // ===== SetWindowPos 标志 =====
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;
    private const uint SWP_NOSENDCHANGING = 0x0400;
    private const uint SWP_DEFERERASE = 0x2000;
    private const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // ===== RedrawWindow 标志 =====
    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_INTERNALPAINT = 0x0002;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_ERASENOW = 0x0200;
    private const uint RDW_ALLCHILDREN = 0x0080;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ERASE = 0x0004;

    // ===== 窗口消息常量 =====
    private const uint WM_COMMAND = 0x0111; // 向 JiYuTrainer 发 IDM_EXIT(32775) 优雅退出（其 WM_CLOSE 被拒绝）

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    // ===== SetWindowPos hWndInsertAfter 特殊值（IntPtr size 独立，需显式转换负数）=====
    // HWND_TOP = -1  : 放到 Z-order 最顶端（高于所有兄弟 HWND）
    // HWND_BOTTOM = 1 : 放到 Z-order 最底端
    // HWND_NOTOPMOST = -2 : 移除 TOPMOST 标记
    // HWND_TOPMOST = -1 : 与 TOP 数值相同（但窗口会获得 TOPMOST 属性；对 WS_CHILD 不适用）
    private static readonly IntPtr HWND_TOP = new(-1);

    // ===== Win32 P/Invoke =====
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtrW_x64(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtrW_x64(IntPtr hWnd, int nIndex, long dwNewLong);

    // x86 Windows 上无 W 后缀的名称可工作；x64 统一调用 W 后缀
    private static long GetWindowLongPtrW(IntPtr hWnd, int nIndex) => GetWindowLongPtrW_x64(hWnd, nIndex);
    private static long SetWindowLongPtrW(IntPtr hWnd, int nIndex, long dwNewLong) => SetWindowLongPtrW_x64(hWnd, nIndex, dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    /// <summary>启动外部 exe 并等待指定窗口类出现，返回（进程, 窗口句柄）；超时抛出异常。
    /// 高级：若 FindWindowW 仅按类名找不到窗口，枚举所有 PID 相关窗口返回第一个匹配类名或属于该进程的可见顶层窗口。</summary>
    public static async Task<(Process Proc, IntPtr Hwnd)> LaunchAndWaitWindowAsync(
        string exePath, string workingDir, string windowClass, int timeoutMs = 20000)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute = false
        };
        var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动：{exePath}");
        Log.ForContext(typeof(Win32EmbedHelper))
            .Information("Win32Embed：启动 {Exe}（PID {Pid}），等待窗口类 {Class}", exePath, proc.Id, windowClass);

        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var lastReport = DateTime.MinValue;
        while (DateTime.UtcNow < deadline)
        {
            proc.Refresh();
            if (proc.HasExited)
            {
                throw new InvalidOperationException(
                    $"进程提前退出（ExitCode={proc.ExitCode}）。若该程序已在别处运行（单实例限制），请先关闭已有实例。");
            }

            // 策略1：精确类名匹配（大多数情况）
            var hwnd = FindWindowW(windowClass, null);
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
            {
                return (proc, hwnd);
            }

            // 策略2：枚举全部窗口，按 PID + 类名/可见性兜底（Sciter / MFC 可能在初始化时先创建隐藏窗口再显示）
            hwnd = FindMainWindowByPid((uint)proc.Id, windowClass);
            if (hwnd != IntPtr.Zero)
            {
                return (proc, hwnd);
            }

            // 每 2s 输出一次诊断信息
            if ((DateTime.UtcNow - lastReport).TotalSeconds > 2)
            {
                lastReport = DateTime.UtcNow;
                Log.ForContext(typeof(Win32EmbedHelper))
                    .Debug("Win32Embed：等待窗口…PID={Pid} Class={Class}", proc.Id, windowClass);
            }

            await Task.Delay(150).ConfigureAwait(true);
        }

        throw new TimeoutException($"等待窗口类 {windowClass} 超时（{timeoutMs}ms）。请确认窗口类名是否正确或程序是否成功启动。");
    }

    /// <summary>按 PID 找到主窗口：枚举所有顶层窗口并过滤。优先匹配类名，其次是可见窗口。</summary>
    private static IntPtr FindMainWindowByPid(uint pid, string preferClass)
    {
        IntPtr best = IntPtr.Zero;
        IntPtr altVisible = IntPtr.Zero;
        EnumWindows((h, lp) =>
        {
            _ = GetWindowThreadProcessId(h, out var hPid);
            if (hPid != pid) return true; // continue
            if (!IsWindowVisible(h)) return true;

            var sb = new StringBuilder(256);
            _ = GetClassNameW(h, sb, sb.Capacity);
            var cls = sb.ToString();
            if (cls.Equals(preferClass, StringComparison.Ordinal))
            {
                best = h;
                return false; // stop
            }
            if (altVisible == IntPtr.Zero) altVisible = h;
            return true;
        }, (IntPtr)0);
        return best != IntPtr.Zero ? best : altVisible;
    }

    /// <summary>把顶层窗口改造为子窗口并挂到 parentHwnd 下。
    /// 核心改进：
    /// ① 智能保留分层样式（Sciter ALPHA_WINDOW / 自绘 UI 依赖 WS_EX_LAYERED）；
    /// ② SetParent + SetWindowPos 后置后 4 轮异步样式加固（对抗 MFC / Sciter 初始化后期重新抢占 WS_POPUP）；
    /// ③ 全流程 GetLastWin32Error 入日志，便于定位真实失败环节。
    /// 返回 false 仅在窗口句柄无效时出现；SetParent 失败会记录日志但仍尝试继续。</summary>
    public static bool MakeChildOf(IntPtr hwnd, IntPtr parentHwnd, string? windowClassNameHint = null)
    {
        var log = Log.ForContext(typeof(Win32EmbedHelper));
        if (hwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindow(parentHwnd))
        {
            log.Warning("Win32Embed.MakeChildOf：无效窗口句柄（child={Child}, parent={Parent}）", hwnd, parentHwnd);
            return false;
        }

        // 获取类名用于智能样式判断（JiYuTrainerWindow 是 Sciter ALPHA_WINDOW，必须保留 WS_EX_LAYERED）
        var sbCls = new StringBuilder(256);
        _ = GetClassNameW(hwnd, sbCls, sbCls.Capacity);
        string actualClass = sbCls.ToString();
        if (string.IsNullOrEmpty(actualClass)) actualClass = windowClassNameHint ?? "unknown";
        bool isSciter = actualClass.Contains("Sciter", StringComparison.OrdinalIgnoreCase)
                        || actualClass.Contains("JiYuTrainer", StringComparison.OrdinalIgnoreCase);
        bool isMfcDialog = actualClass.Contains("MusicPlayer", StringComparison.OrdinalIgnoreCase)
                           || actualClass.Contains("#32770", StringComparison.Ordinal); // #32770 = 标准对话框

        log.Information(
            "Win32Embed.MakeChildOf：开始嵌入 HWND={Hwnd}（Class={Class}，Sciter={IsSciter}，MFC={IsMfc}）",
            hwnd, actualClass, isSciter, isMfcDialog);

        // ===== 1. 去除顶层样式 + WS_CHILD + WS_CLIPSIBLINGS + WS_CLIPCHILDREN =====
        // WS_CLIPCHILDREN 防止 XAML Island 在子窗口区域绘制导致闪烁或覆盖
        long style = GetWindowLongPtrW(hwnd, GWL_STYLE);
        long orgStyle = style;
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_VISIBLE;
        SetWindowLongPtrW(hwnd, GWL_STYLE, style);
        long errStyle = Marshal.GetLastWin32Error();

        // ===== 2. 扩展样式：智能保留分层 =====
        // Sciter ALPHA_WINDOW 必须保留 WS_EX_LAYERED（使用 UpdateLayeredWindow 做 per-pixel alpha 合成），
        // 一旦剥离 Sciter 无法合成 UI → 黑屏 / 空白。对于 MFC 对话框也保留 LAYERED（现代 UI 也常用）。
        // WS_EX_TRANSPARENT 只在鼠标穿透场景使用，这里全部去掉避免子窗口不能点。
        // WS_EX_TOPMOST 必须去（子窗口 TOPMOST 无意义且可能引起桌面子窗口被提到最前）。
        long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        long orgEx = ex;
        ex &= ~(WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_APPWINDOW);
        ex |= WS_EX_NOPARENTNOTIFY; // 避免子窗口生成/销毁时给父发通知（减少 WinUI 干扰）
        // 不剥离 WS_EX_LAYERED：若是 Sciter 或原始窗口已带此样式，保留它并让桌面合成器负责显示
        if (!isSciter && (orgEx & WS_EX_LAYERED) != 0)
        {
            // 非 Sciter 但有 LAYERED：也保留（很多自绘窗口依赖它不闪烁；如果有问题用户会看到拖影再反馈）
            log.Verbose("Win32Embed：非 Sciter 窗口仍保留 WS_EX_LAYERED（OrgEx=0x{OrgEx:X}）", orgEx);
        }
        SetWindowLongPtrW(hwnd, GWL_EXSTYLE, ex);
        long errEx = Marshal.GetLastWin32Error();

        // ===== 3. SetParent 挂到主窗口 =====
        // MSDN：SetParent 不修改 WS_CHILD / WS_POPUP 样式——我们在第 1 步已经切换，这里即可安全挂接。
        bool parentOk = SetParent(hwnd, parentHwnd);
        int errParent = Marshal.GetLastWin32Error();

        // ===== 4. FRAMECHANGED 重算非客户区 + HWND_TOP 置顶 =====
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_FRAMECHANGED | SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW |
            SWP_NOOWNERZORDER | SWP_NOACTIVATE | SWP_NOSENDCHANGING);
        int err1 = Marshal.GetLastWin32Error();
        BringWindowToTop(hwnd);
        SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        int err2 = Marshal.GetLastWin32Error();

        log.Information(
            "Win32Embed.MakeChildOf：HWND={Hwnd}→Parent={Parent}（StyleOrg=0x{OrgStyle:X}→0x{Style:X}, ExOrg=0x{OrgEx:X}→0x{Ex:X}; SetParent={ParentOk}/{ErrParent}; SWPErr1={Err1}, Err2={Err2}, StyleErr={ErrStyle}, ExErr={ErrEx}）",
            hwnd, parentHwnd, orgStyle, style, orgEx, ex, parentOk, errParent, err1, err2, errStyle, errEx);

        // ===== 6. DWM 合成刷新（JiYuTrainer 黑屏的另一根因）=====
        // Sciter ALPHA_WINDOW（WS_EX_LAYERED）/ MFC 自绘窗口 SetParent 后，DWM 往往不再重新合成这个子窗口，
        // 视觉上"主界面直接消失"（窗口还在，但透明/黑色）。这里做一次最强刷新：
        //  SW_HIDE → SW_SHOW（让 DWM 重建分层位图缓存）+ RedrawWindow 全树强制重绘。
        try
        {
            ShowWindow(hwnd, 0); // SW_HIDE
            ShowWindow(hwnd, 5); // SW_SHOW
            RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero,
                RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW | RDW_ERASENOW | RDW_INTERNALPAINT);
        }
        catch (Exception dwmEx)
        {
            log.Verbose(dwmEx, "DWM 合成刷新异常（忽略继续）");
        }

        // ===== 7. 关键：异步 5~12 轮"样式加固" =====
        // MFC / Sciter 常常在初始化后期（WM_INITDIALOG / Sciter 加载 HTML 后）重新调用 SetWindowLongPtr
        // 把 WS_CHILD 改回 WS_POPUP，并触发 ShowWindow(SW_SHOW)——子窗口就会"从主程序里弹出"到桌面独立显示，
        // 这就是用户看到的"MusicPlayer2 召唤出窗口但不能嵌入" / "JiYuTrainer 托盘图标显示了主界面没有"。
        // Sciter / MFC：做满 12 轮（约 9.5 秒），覆盖全部初始化后期回调；普通窗口做 4 轮足够。
        _ = HardenChildStyleLoop(hwnd, parentHwnd, actualClass, times: isSciter || isMfcDialog ? 12 : 4);
        return true;
    }

    /// <summary>SetParent + WS_CHILD 加固循环（异步，不阻塞 UI 线程）。
    /// 每隔若干毫秒重新检查并修正 style/parent，最多 times 次。</summary>
    private static async Task HardenChildStyleLoop(IntPtr hwnd, IntPtr parentHwnd, string actualClass, int times)
    {
        var log = Log.ForContext(typeof(Win32EmbedHelper));
        try
        {
            for (int i = 0; i < times; i++)
            {
                // 间隔：150, 270, 390, 510, 630, 750, 870, 990, 1110, 1230, 1350, 1470 ms → 12 轮覆盖约 9.6 秒（MFC 初始化最后阶段）
                await Task.Delay(150 + i * 120).ConfigureAwait(true);
                if (!IsWindow(hwnd)) { log.Verbose("加固循环：窗口 {Hwnd} 已失效，终止", hwnd); return; }

                bool needFrameChanged = false;
                long st = GetWindowLongPtrW(hwnd, GWL_STYLE);
                if ((st & WS_CHILD) == 0 || (st & WS_POPUP) != 0)
                {
                    // 被宿主程序改回 POPUP 了：重新改回 WS_CHILD 并 SetParent
                    log.Warning("样式加固（轮次 {I}/{T}）：检测到 WS_POPUP 被重置（Class={Class} Style=0x{St:X}），重新施加 WS_CHILD+SetParent",
                        i + 1, times, actualClass, st);
                    st &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
                    st |= WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN | WS_VISIBLE;
                    SetWindowLongPtrW(hwnd, GWL_STYLE, st);
                    SetParent(hwnd, parentHwnd);
                    needFrameChanged = true;
                }

                // 当前 parent 校验：若被改回桌面（HWND_DESKTOP = parent=0，或自己原来的主窗口），重新 SetParent
                IntPtr curParent = GetParentNative(hwnd);
                if (curParent != parentHwnd)
                {
                    log.Warning("样式加固（轮次 {I}/{T}）：检测到父句柄被改（Class={Class} CurrentParent={Cur} Wanted={Want}），重新 SetParent+TopMost",
                        i + 1, times, actualClass, curParent, parentHwnd);
                    SetParent(hwnd, parentHwnd);
                    needFrameChanged = true;
                }

                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
                    (needFrameChanged ? SWP_FRAMECHANGED : 0) |
                    SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW |
                    SWP_NOOWNERZORDER | SWP_NOACTIVATE);
                BringWindowToTop(hwnd);

                // 每轮末尾：轻量 Redraw（如果上一轮确实检测到异常则加重绘），保持 DWM 不丢帧
                try
                {
                    RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, needFrameChanged
                        ? RDW_INVALIDATE | RDW_ERASE | RDW_FRAME | RDW_ALLCHILDREN | RDW_UPDATENOW
                        : RDW_INVALIDATE | RDW_ALLCHILDREN);
                }
                catch { /* 忽略 */ }
            }
            log.Information("样式加固循环结束（Class={Class}，{Times} 轮完成）", actualClass, times);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "样式加固循环异常（Class={Class}）", actualClass);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);
    private static IntPtr GetParentNative(IntPtr hWnd)
    {
        try { return GetParent(hWnd); } catch { return IntPtr.Zero; }
    }

    /// <summary>把子窗口摆放到宿主区域（页面 DIP 坐标），可选保持宽高比（等比例放大）。
    /// 内部按宿主 DPI 换算为物理像素；每次摆放都以 HWND_TOP 提到 XAML 图层之上。</summary>
    public static void PlaceChild(IntPtr hwnd, double x, double y, double width, double height, bool keepAspect)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;

        // DIP → 物理像素（WinUI 布局坐标为 DIP，SetWindowPos 为物理像素；100% 缩放时 scale=1 与旧行为一致）
        double scale = 1.0;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch { /* DPI 查询失败时按 100% 处理 */ }

        double cx = Math.Max(1, width), cy = Math.Max(1, height);
        double px = x, py = y;

        if (keepAspect)
        {
            // 以 430:520 设计比例等比缩放，居中放置
            const double designW = 430.0, designH = 520.0;
            double s = Math.Min(cx / designW, cy / designH);
            double w = Math.Max(1, designW * s);
            double h = Math.Max(1, designH * s);
            px += (cx - w) / 2;
            py += (cy - h) / 2;
            cx = w;
            cy = h;
        }

        // HWND_TOP：每次摆放松开时重新置顶，保证不被 XAML 内容覆盖
        SetWindowPos(hwnd, HWND_TOP,
            (int)Math.Round(px * scale), (int)Math.Round(py * scale),
            (int)Math.Round(cx * scale), (int)Math.Round(cy * scale),
            SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    /// <summary>
    /// Sciter ALPHA_WINDOW（JiYuTrainer）"贴靠嵌入"——调用其原生窗口接口而非强制改造。
    ///
    /// 依据 JiYuTrainer 源码（JiYuTrainer-master）：
    ///   • MainWindow 硬编码 CreateWindow(WS_POPUP, 430, 520, 父窗口=NULL) + SciterSetOption(SCITER_ALPHA_WINDOW)，
    ///     窗口本质是依赖 WS_EX_LAYERED 做 per-pixel alpha 合成的顶层窗口，没有 SetParent 嵌入接口；
    ///   • CommonWindow::wndProc 的 WM_SHOWWINDOW 分支会在每次显示时把窗口"居中到屏幕中央"，
    ///     之前的 SW_HIDE→SW_SHOW + 强制 WS_CHILD 改造正是窗口消失/跑位/黑屏的根因；
    ///   • 主窗口 WM_CLOSE 直接 return TRUE 拒绝关闭，隐藏须用 ShowWindow(SW_HIDE)（不触发居中）。
    ///
    /// 因此这里**完全不修改窗口样式**（保持 WS_POPUP + WS_EX_LAYERED 原样，Sciter 合成 100% 正常），
    /// 只做两件事：SetParent 挂到宿主（成为 owned popup 子窗口 → 跟随主窗口移动、超出客户区被裁剪），
    /// 以及 SetWindowPos 贴靠定位到容器区域并置顶。显示用 SWP_SHOWWINDOW（WM_SHOWWINDOW 居中发生在
    /// 位置更新之前，最终位置以本方法传入为准）；若仍需保险可再由调用方延迟二次定位。
    /// </summary>
    public static void DockTopLevel(IntPtr hwnd, IntPtr parentHwnd, double x, double y, double width, double height, bool keepAspect)
    {
        var log = Log.ForContext(typeof(Win32EmbedHelper));
        if (hwnd == IntPtr.Zero || parentHwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindow(parentHwnd))
        {
            log.Warning("Win32Embed.DockTopLevel：无效句柄（hwnd={Hwnd}, parent={Parent}）", hwnd, parentHwnd);
            return;
        }
        try
        {
            // 1) 仅挂父，不改任何样式（Sciter 分层合成依赖 WS_EX_LAYERED / WS_POPUP 身份，绝不能动）
            IntPtr cur = GetParentNative(hwnd);
            if (cur != parentHwnd)
            {
                bool ok = SetParent(hwnd, parentHwnd);
                log.Information("Win32Embed.DockTopLevel：SetParent {Hwnd}→{Parent}（保持原样式，Ok={Ok}）", hwnd, parentHwnd, ok);
            }

            // 2) 贴靠定位（复用 PlaceChild 的 DPI 换算 + keepAspect 等比逻辑）
            double scale = 1.0;
            try
            {
                var dpi = GetDpiForWindow(hwnd);
                if (dpi > 0) scale = dpi / 96.0;
            }
            catch { /* 按 100% 处理 */ }

            double cx = Math.Max(1, width), cy = Math.Max(1, height);
            double px = x, py = y;
            if (keepAspect)
            {
                const double designW = 430.0, designH = 520.0;
                double s = Math.Min(cx / designW, cy / designH);
                double w = Math.Max(1, designW * s);
                double h = Math.Max(1, designH * s);
                px += (cx - w) / 2;
                py += (cy - h) / 2;
                cx = w;
                cy = h;
            }

            SetWindowPos(hwnd, HWND_TOP,
                (int)Math.Round(px * scale), (int)Math.Round(py * scale),
                (int)Math.Round(cx * scale), (int)Math.Round(cy * scale),
                SWP_SHOWWINDOW | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
        catch (Exception ex)
        {
            log.Verbose(ex, "Win32Embed.DockTopLevel 异常（hwnd={Hwnd}）", hwnd);
        }
    }

    /// <summary>嵌入后置顶 + 显示（连续多次，抵抗 XAML 岛重新布局时的 Z-order 覆盖）。
    /// 在 JiYuTrainer/MusicPlayer2 页面第一次 PlacePanel 后调用。</summary>
    public static void EnsureTopMostRepeated(IntPtr hwnd, int times = 4, int intervalMs = 80)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        _ = RepeatTop(hwnd, times, intervalMs);
    }

    private static async Task RepeatTop(IntPtr hwnd, int times, int intervalMs)
    {
        var log = Log.ForContext(typeof(Win32EmbedHelper));
        for (int i = 0; i < times; i++)
        {
            try
            {
                BringWindowToTop(hwnd);
                SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_SHOWWINDOW);
            }
            catch (Exception ex)
            {
                log.Verbose(ex, "EnsureTopMostRepeated 第 {I} 次失败", i);
            }
            if (i < times - 1)
            {
                await Task.Delay(intervalMs).ConfigureAwait(true);
            }
        }
        log.Verbose("EnsureTopMostRepeated 完成（{T} 次）", times);
    }

    /// <summary>优雅关闭外部进程：优先调用其原生退出接口，3 秒不退则强杀整树。
    /// JiYuTrainer 主窗口 WM_CLOSE 直接 return TRUE 拒绝关闭（源码），须用其托盘退出命令
    /// WM_COMMAND IDM_EXIT(32775) 触发 OnWmCommand 保存设置并退出；其他程序走 WM_CLOSE。</summary>
    public static void Shutdown(Process? proc)
    {
        try
        {
            if (proc is null || proc.HasExited) return;
            IntPtr hwnd = proc.MainWindowHandle;
            if (hwnd != IntPtr.Zero)
            {
                var sb = new StringBuilder(256);
                _ = GetClassNameW(hwnd, sb, sb.Capacity);
                if (sb.ToString().Contains("JiYuTrainer", StringComparison.OrdinalIgnoreCase))
                {
                    // JiYuTrainer 原生退出接口：WM_COMMAND + IDM_EXIT(32775)
                    PostMessageW(hwnd, WM_COMMAND, new IntPtr(32775), IntPtr.Zero);
                }
                else
                {
                    PostMessageW(hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero);
                }
            }
            if (!proc.WaitForExit(3000))
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception)
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>记录嵌入日志（便于诊断外部窗口嵌入失败场景）。</summary>
    public static void LogEmbed(ILogger logger, string stage, string detail)
    {
        logger.Information("外部窗口嵌入 [{Stage}]：{Detail}", stage, detail);
    }

    /// <summary>把 WinUI 元素相对页面原点的位置换算为主窗口客户区偏移（外部子窗口的坐标基准）。</summary>
    public static Windows.Foundation.Point ElementOriginInWindow(Microsoft.UI.Xaml.UIElement element)
    {
        try
        {
            var transform = element.TransformToVisual(null);
            return transform.TransformPoint(new Windows.Foundation.Point(0, 0));
        }
        catch
        {
            return new Windows.Foundation.Point(0, 0);
        }
    }

    /// <summary>窗口句柄获取（主窗口顶层 HWND，作为子窗口父级）。</summary>
    public static IntPtr GetTopLevelHwnd(Microsoft.UI.Xaml.Window window)
        => WinRT.Interop.WindowNative.GetWindowHandle(window);

    /// <summary>确保窗口可见（嵌入后某些程序默认隐藏）。</summary>
    public static void EnsureVisible(IntPtr hwnd) => ShowWindow(hwnd, 5 /* SW_SHOW */);

    /// <summary>设置前台（无实际效果于子窗口，保留备用）。</summary>
    public static void FocusWindow(IntPtr hwnd) => SetForegroundWindow(hwnd);
}
