// FloralPin.cs — 花笺（floral-notepaper）便签/磁贴窗口的「钉到桌面 / 恢复置顶」切换工具
//               并附带一个桌面番茄钟。
//
// 原理与花笺 PR #378 相同：把窗口 SetParent 到桌面图标所在的 WorkerW
// （找不到时退化为 Progman）。「钉到桌面」的窗口位于所有普通窗口之下、
// 只出现在桌面上，Win+D「显示桌面」也不会把它收走；恢复时脱离子窗口状态并重新置顶。
//
// 用法（本目录下的 花笺钉桌面.cmd 提供菜单入口）：
//   FloralPin.exe watch           后台守护：注册全局热键 Ctrl+Alt+D
//   FloralPin.exe toggle [hwnd]   切换鼠标下的窗口（也可直接指定窗口句柄）
//   FloralPin.exe pin             把所有便签/磁贴窗口钉到桌面
//   FloralPin.exe restore         把所有窗口恢复为置顶小窗
//   FloralPin.exe status          查看当前花笺窗口与状态
//   FloralPin.exe pomodoro [start|toggle|skip|reset|close]   番茄钟
//   FloralPin.exe install         开机自启（启动文件夹快捷方式）
//   FloralPin.exe uninstall       取消开机自启
//
// 番茄钟：小倒计时条（默认置顶，可被 Ctrl+Alt+D 切到桌面层），
// 点一下窗口 = 开始/暂停，右键 = 更多操作；时长配置见 pomodoro.json。
//
// 运行日志：本目录 FloralPin.log（热键注册结果、每次切换的记录）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using WinTimer = System.Windows.Forms.Timer;

static class FloralPin
{
    const int GWL_STYLE = -16;
    const int GWL_EXSTYLE = -20;
    const int WS_CHILD = unchecked((int)0x40000000);
    const int WS_POPUP = unchecked((int)0x80000000);
    const int WS_EX_TOPMOST = 0x00000008;

    const uint SWP_NOSIZE = 0x0001;
    const uint SWP_NOMOVE = 0x0002;
    const uint SWP_NOZORDER = 0x0004;
    const uint SWP_NOACTIVATE = 0x0010;
    const uint SWP_FRAMECHANGED = 0x0020;
    const uint SWP_SHOWWINDOW = 0x0040;

    const uint WM_SPAWN_WORKER = 0x052C;
    const uint SMTO_NORMAL = 0x0000;
    const uint WM_HOTKEY = 0x0312;
    const uint PM_REMOVE = 0x0001;

    const uint MOD_ALT = 0x0001;
    const uint MOD_CONTROL = 0x0002;
    const uint MOD_NOREPEAT = 0x4000;
    const int HOTKEY_ID = 20260919;

    // 番茄钟
    const string POMODORO_TITLE = "FloralPinPomodoro";
    const int SW_SHOW = 5;
    const int PM_MSG_BASE = 0x8000 + 77;
    const int PM_TOGGLE = PM_MSG_BASE + 1;
    const int PM_SKIP = PM_MSG_BASE + 2;
    const int PM_RESET = PM_MSG_BASE + 3;
    const int PM_CLOSE = PM_MSG_BASE + 4;

    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

    [DllImport("user32.dll")]
    static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll")]
    static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll")]
    static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowW(string lpClassName, string lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string lpszWindow);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll")]
    static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("kernel32.dll")]
    static extern uint GetLastError();

    [DllImport("kernel32.dll")]
    static extern void SetLastError(uint dwErrCode);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW")]
    static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    // 静态字段持有，防止 GC 回收导致互斥体句柄提前关闭（单实例保护会失效）
    static Mutex watcherMutex;

    // 热键候选：依次尝试，被占用时自动顺延（结果写入 hotkey.txt，status 可查）
    static readonly string[] HOTKEY_NAMES = new string[] { "Ctrl+Alt+D", "Ctrl+Alt+P", "Ctrl+Alt+K" };
    static readonly uint[] HOTKEY_VKS = new uint[] { 0x44, 0x50, 0x4B };

    static string runtimeDirCache;

    // ---------------------------------------------------------------- 日志与基础工具

    static string ExeDir()
    {
        return System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    }

    // 运行时目录：优先程序所在目录（方便绿色携带），不可写时落到 LocalAppData
    // （典型场景：程序被放进 Program Files 等受保护目录）
    static string RuntimeDir()
    {
        if (runtimeDirCache != null) return runtimeDirCache;
        try
        {
            string probe = System.IO.Path.Combine(ExeDir(), ".write-probe");
            System.IO.File.WriteAllText(probe, "");
            System.IO.File.Delete(probe);
            runtimeDirCache = ExeDir();
        }
        catch
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloralPin");
            try { System.IO.Directory.CreateDirectory(dir); } catch { }
            runtimeDirCache = dir;
        }
        return runtimeDirCache;
    }

    static string LogPath()
    {
        return System.IO.Path.Combine(RuntimeDir(), "FloralPin.log");
    }

    static void Log(string message)
    {
        try
        {
            string path = LogPath();
            System.IO.FileInfo info = new System.IO.FileInfo(path);
            if (info.Exists && info.Length > 512 * 1024) System.IO.File.Delete(path);
            System.IO.File.AppendAllText(path,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + message + Environment.NewLine);
        }
        catch { }
    }

    static string WindowTitle(IntPtr hwnd)
    {
        StringBuilder sb = new StringBuilder(512);
        int len = GetWindowTextW(hwnd, sb, sb.Capacity);
        if (len <= 0) return "";
        return sb.ToString(0, len);
    }

    static string WindowClass(IntPtr hwnd)
    {
        StringBuilder sb = new StringBuilder(256);
        int len = GetClassNameW(hwnd, sb, sb.Capacity);
        if (len <= 0) return "";
        return sb.ToString(0, len);
    }

    static bool IsFloralProcess(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return false;
        try
        {
            Process p = Process.GetProcessById((int)pid);
            return p.ProcessName.StartsWith("floral-notepaper", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // 便签 / 磁贴窗口的标题（覆盖 zh-CN、zh-HK、en 三种语言）
    static bool IsSurfaceTitle(string title)
    {
        string[] keys = new string[] { "便签", "便箋", "磁贴", "磁貼", "Quick Note", "Pin Mode" };
        for (int i = 0; i < keys.Length; i++)
        {
            if (title.IndexOf(keys[i], StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }
        return false;
    }

    static List<IntPtr> FindSurfaceWindows()
    {
        IntPtr desktop = FindDesktopParent();
        return FindSurfaceWindows(desktop);
    }

    // 未钉的窗口是顶层窗口；钉过的窗口已成为桌面层的子窗口，两者都要枚举
    static List<IntPtr> FindSurfaceWindows(IntPtr desktopParent)
    {
        List<IntPtr> result = new List<IntPtr>();

        EnumWindowsProc collect = delegate(IntPtr hwnd, IntPtr lparam)
        {
            if (result.Contains(hwnd)) return true;
            if (!IsWindowVisible(hwnd)) return true;
            string title = WindowTitle(hwnd);
            if (title.Length == 0 || !IsSurfaceTitle(title)) return true;
            if (!IsFloralProcess(hwnd)) return true;
            result.Add(hwnd);
            return true;
        };

        EnumWindows(collect, IntPtr.Zero);
        if (desktopParent != IntPtr.Zero) EnumChildWindows(desktopParent, collect, IntPtr.Zero);
        return result;
    }

    // 桌面图标层：优先找「含有 SHELLDLL_DefView 的 WorkerW」，否则退化为 Progman
    static IntPtr FindDesktopParent()
    {
        IntPtr progman = FindWindowW("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;

        IntPtr ignored;
        SendMessageTimeoutW(progman, WM_SPAWN_WORKER, IntPtr.Zero, IntPtr.Zero, SMTO_NORMAL, 1000, out ignored);

        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
        {
            if (WindowClass(hwnd) == "WorkerW")
            {
                if (FindWindowExW(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                {
                    found = hwnd;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);

        if (found != IntPtr.Zero) return found;
        if (FindWindowExW(progman, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) return progman;
        return IntPtr.Zero;
    }

    static POINT ParentClientOrigin(IntPtr parent)
    {
        POINT pt = new POINT();
        pt.X = 0;
        pt.Y = 0;
        ClientToScreen(parent, ref pt);
        return pt;
    }

    static bool IsPinned(IntPtr hwnd, IntPtr desktop)
    {
        return desktop != IntPtr.Zero && GetParent(hwnd) == desktop;
    }

    // ---------------------------------------------------------------- 钉 / 还原

    static bool PinWindow(IntPtr hwnd, IntPtr parent, out string error)
    {
        error = null;

        RECT wr;
        if (!GetWindowRect(hwnd, out wr))
        {
            error = "GetWindowRect 失败";
            return false;
        }
        POINT origin = ParentClientOrigin(parent);

        int style = GetWindowLongW(hwnd, GWL_STYLE);
        int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
        SetWindowLongW(hwnd, GWL_STYLE, (style & ~WS_POPUP) | WS_CHILD);
        SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOPMOST);

        SetLastError(0);
        SetParent(hwnd, parent);
        uint lastError = GetLastError();
        if (lastError != 0)
        {
            SetWindowLongW(hwnd, GWL_STYLE, style);
            SetWindowLongW(hwnd, GWL_EXSTYLE, exStyle);
            error = "SetParent 失败（错误码 " + lastError + "）";
            return false;
        }

        // 子窗口坐标相对父窗口，WorkerW 一般覆盖整个虚拟屏幕，这里按父窗口客户区原点换算
        SetWindowPos(hwnd, IntPtr.Zero, wr.Left - origin.X, wr.Top - origin.Y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        return true;
    }

    static bool UnpinWindow(IntPtr hwnd, out string error)
    {
        error = null;

        IntPtr parent = GetParent(hwnd);
        RECT wr;
        if (!GetWindowRect(hwnd, out wr))
        {
            error = "GetWindowRect 失败";
            return false;
        }
        int screenX = wr.Left;
        int screenY = wr.Top;
        if (parent != IntPtr.Zero)
        {
            POINT origin = ParentClientOrigin(parent);
            screenX += origin.X;
            screenY += origin.Y;
        }

        int style = GetWindowLongW(hwnd, GWL_STYLE);
        SetWindowLongW(hwnd, GWL_STYLE, (style & ~WS_CHILD) | WS_POPUP);

        SetLastError(0);
        SetParent(hwnd, IntPtr.Zero);
        uint lastError = GetLastError();
        if (lastError != 0)
        {
            error = "SetParent(null) 失败（错误码 " + lastError + "）";
            return false;
        }

        SetWindowPos(hwnd, HWND_TOPMOST, screenX, screenY, 0, 0,
            SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        return true;
    }

    // ---------------------------------------------------------------- 切换

    static void ToggleWindow(IntPtr hwnd, string source)
    {
        IntPtr desktop = FindDesktopParent();
        if (desktop == IntPtr.Zero)
        {
            Log(source + "：找不到桌面图标层，无法切换");
            Console.WriteLine("找不到桌面图标层（WorkerW/Progman）。");
            return;
        }
        string title = WindowTitle(hwnd);
        string error;
        if (IsPinned(hwnd, desktop))
        {
            if (UnpinWindow(hwnd, out error))
            {
                Log(source + "：恢复置顶 <" + title + ">");
                Console.WriteLine("已恢复置顶: " + title);
            }
            else
            {
                Log(source + "：恢复失败 " + error);
                Console.WriteLine("恢复失败: " + error);
            }
        }
        else
        {
            if (PinWindow(hwnd, desktop, out error))
            {
                Log(source + "：钉到桌面 <" + title + ">");
                Console.WriteLine("已钉到桌面: " + title);
            }
            else
            {
                Log(source + "：钉到桌面失败 " + error);
                Console.WriteLine("失败: " + error);
            }
        }
    }

    // 从命中的子窗口向上找便签/磁贴窗口本身（也识别我们自己的番茄钟窗口）
    static IntPtr FindSurfaceAncestor(IntPtr hwnd)
    {
        IntPtr current = hwnd;
        for (int i = 0; i < 12 && current != IntPtr.Zero; i++)
        {
            if (WindowTitle(current) == POMODORO_TITLE && IsOwnProcess(current)) return current;
            if (WindowClass(current) == "Tauri Window"
                && IsSurfaceTitle(WindowTitle(current))
                && IsFloralProcess(current))
            {
                return current;
            }
            current = GetParent(current);
        }
        return IntPtr.Zero;
    }

    static void ToggleUnderCursor(string source)
    {
        POINT pt;
        if (!GetCursorPos(out pt))
        {
            Log(source + "：GetCursorPos 失败");
            return;
        }
        IntPtr hit = WindowFromPoint(pt);
        IntPtr target = FindSurfaceAncestor(hit);
        if (target == IntPtr.Zero)
        {
            Log(source + "：鼠标下没有便签/磁贴窗口（光标 " + pt.X + "," + pt.Y + "）");
            Console.WriteLine("鼠标下没有便签/磁贴窗口。请把鼠标移到便签上再试。");
            return;
        }
        ToggleWindow(target, source);
    }

    // ---------------------------------------------------------------- 命令实现

    static int CmdStatus()
    {
        bool daemonRunning = false;
        try
        {
            using (Mutex m = Mutex.OpenExisting("FloralPinWatcher")) daemonRunning = true;
        }
        catch { }

        string hotkey = "Ctrl+Alt+D";
        try
        {
            string hotkeyFile = System.IO.Path.Combine(RuntimeDir(), "hotkey.txt");
            if (System.IO.File.Exists(hotkeyFile))
            {
                string text = System.IO.File.ReadAllText(hotkeyFile).Trim();
                if (text.Length > 0) hotkey = text;
            }
        }
        catch { }

        Console.WriteLine("守护进程: " + (daemonRunning
            ? "运行中（热键 " + hotkey + "：鼠标下的便签 钉桌面/置顶 切换）"
            : "未运行（菜单 [1] 启动）"));
        Console.WriteLine("日志文件: " + LogPath());
        Console.WriteLine("番茄钟: " + (FindPomodoroWindow() != IntPtr.Zero ? "运行中" : "未运行（菜单 [8] 打开）"));
        IntPtr desktop = FindDesktopParent();
        Console.WriteLine("桌面图标层 (WorkerW/Progman): " + (desktop == IntPtr.Zero ? "未找到" : "0x" + desktop.ToInt64().ToString("X")));
        List<IntPtr> windows = FindSurfaceWindows(desktop);
        Console.WriteLine("花笺便签/磁贴窗口: " + windows.Count + " 个");
        for (int i = 0; i < windows.Count; i++)
        {
            IntPtr h = windows[i];
            bool pinned = IsPinned(h, desktop);
            Console.WriteLine(string.Format("  [{0}] hwnd=0x{1}  {2}",
                pinned ? "钉在桌面" : "置顶显示",
                h.ToInt64().ToString("X"),
                WindowTitle(h)));
        }
        if (windows.Count == 0)
        {
            Console.WriteLine("  （没有找到可见的便签/磁贴窗口——先让花笺把便签显示出来）");
        }
        return 0;
    }

    static int CmdToggle(string[] args)
    {
        if (args.Length > 1)
        {
            string raw = args[1];
            try
            {
                if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(2);
                IntPtr hwnd = new IntPtr(Convert.ToInt64(raw, 16));
                if (!IsWindow(hwnd))
                {
                    Console.WriteLine("窗口句柄无效或已关闭: " + args[1]);
                    return 1;
                }
                ToggleWindow(hwnd, "命令行");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("解析句柄失败: " + ex.Message);
                return 1;
            }
        }
        ToggleUnderCursor("命令行");
        return 0;
    }

    static int CmdPinAll()
    {
        IntPtr desktop = FindDesktopParent();
        if (desktop == IntPtr.Zero)
        {
            Console.WriteLine("找不到桌面图标层（WorkerW/Progman），无法钉到桌面。");
            return 1;
        }
        List<IntPtr> windows = FindSurfaceWindows(desktop);
        int count = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            if (IsPinned(windows[i], desktop)) continue;
            string error;
            if (PinWindow(windows[i], desktop, out error))
            {
                count++;
                Console.WriteLine("已钉到桌面: " + WindowTitle(windows[i]));
            }
            else
            {
                Console.WriteLine("失败: " + WindowTitle(windows[i]) + " —— " + error);
            }
        }
        Log("批量：钉到桌面 " + count + " 个窗口");
        Console.WriteLine("完成，共钉 " + count + " 个窗口。");
        return 0;
    }

    static int CmdRestoreAll()
    {
        IntPtr desktop = FindDesktopParent();
        List<IntPtr> windows = FindSurfaceWindows(desktop);
        int count = 0;
        for (int i = 0; i < windows.Count; i++)
        {
            if (!IsPinned(windows[i], desktop)) continue;
            string error;
            if (UnpinWindow(windows[i], out error))
            {
                count++;
                Console.WriteLine("已恢复置顶: " + WindowTitle(windows[i]));
            }
            else
            {
                Console.WriteLine("失败: " + WindowTitle(windows[i]) + " —— " + error);
            }
        }
        Log("批量：恢复置顶 " + count + " 个窗口");
        Console.WriteLine("完成，共恢复 " + count + " 个窗口。");
        return 0;
    }

    static int CmdWatch()
    {
        bool createdNew;
        watcherMutex = new Mutex(true, "FloralPinWatcher", out createdNew);
        if (!createdNew) return 0; // 已有守护进程在跑

        int successIndex = -1;
        for (int i = 0; i < HOTKEY_VKS.Length; i++)
        {
            if (RegisterHotKey(IntPtr.Zero, HOTKEY_ID, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, HOTKEY_VKS[i]))
            {
                successIndex = i;
                break;
            }
        }
        if (successIndex < 0)
        {
            Log("守护启动失败：Ctrl+Alt+D / P / K 三个热键都被占用");
            return 1;
        }
        Log("守护启动：热键 " + HOTKEY_NAMES[successIndex] + " 注册成功");
        try
        {
            System.IO.File.WriteAllText(System.IO.Path.Combine(RuntimeDir(), "hotkey.txt"), HOTKEY_NAMES[successIndex]);
        }
        catch { }

        while (true)
        {
            MSG msg;
            while (PeekMessageW(out msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_HOTKEY && msg.wParam.ToInt64() == HOTKEY_ID)
                {
                    ToggleUnderCursor("热键");
                }
            }
            Thread.Sleep(120);
        }
    }

    static int CmdAutostart(bool enable)
    {
        string exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        string exeDir = ExeDir();
        string startupDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs\Startup");
        string lnkPath = System.IO.Path.Combine(startupDir, "FloralPin.lnk");

        // 顺手清理注册表 Run 项（部分安全软件会拦截写入，这里只做清理，不依赖它）
        try
        {
            Microsoft.Win32.RegistryKey runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (runKey != null) runKey.DeleteValue("FloralPin", false);
        }
        catch { }

        if (enable)
        {
            string error = CreateShortcut(lnkPath, exePath, exeDir);
            if (error != null)
            {
                Log("自启：创建快捷方式失败 " + error);
                Console.WriteLine("创建启动快捷方式失败: " + error);
                return 1;
            }
            if (!System.IO.File.Exists(lnkPath))
            {
                Log("自启：快捷方式未生成（被安全软件拦截？）");
                Console.WriteLine("写入被安全软件拦截（快捷方式未生成）。请在安全软件中允许后重试。");
                return 1;
            }
            Log("自启：已开启 " + lnkPath);
            Console.WriteLine("已开启开机自启（启动文件夹快捷方式）: " + lnkPath);
            return 0;
        }

        try
        {
            if (System.IO.File.Exists(lnkPath)) System.IO.File.Delete(lnkPath);
            Log("自启：已关闭");
            Console.WriteLine("已关闭开机自启。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("删除快捷方式失败: " + ex.Message);
            return 1;
        }
    }

    static string CreateShortcut(string lnkPath, string exePath, string exeDir)
    {
        try
        {
            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return "WScript.Shell COM 不可用";
            object shell = Activator.CreateInstance(shellType);
            try
            {
                object shortcut = shellType.InvokeMember("CreateShortcut",
                    System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exePath });
                shortcutType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "watch" });
                shortcutType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { exeDir });
                shortcutType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { "花笺便签钉到桌面（后台守护）" });
                shortcutType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
                Marshal.ReleaseComObject(shortcut);
            }
            finally
            {
                Marshal.ReleaseComObject(shell);
            }
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    static int CmdDebug()
    {
        IntPtr desktop = FindDesktopParent();
        Console.WriteLine("桌面图标层: " + (desktop == IntPtr.Zero ? "未找到" : "0x" + desktop.ToInt64().ToString("X")));
        EnumWindowsProc dump = delegate(IntPtr hwnd, IntPtr lparam)
        {
            if (!IsFloralProcess(hwnd)) return true;
            Console.WriteLine(string.Format("  hwnd=0x{0,-8} visible={1,-5} parent=0x{2,-8} class={3}  title={4}",
                hwnd.ToInt64().ToString("X"),
                IsWindowVisible(hwnd) ? "是" : "否",
                GetParent(hwnd).ToInt64().ToString("X"),
                WindowClass(hwnd), WindowTitle(hwnd)));
            return true;
        };
        Console.WriteLine("顶层窗口:");
        EnumWindows(dump, IntPtr.Zero);
        if (desktop != IntPtr.Zero)
        {
            Console.WriteLine("桌面层(WorkerW)的子窗口:");
            EnumChildWindows(desktop, dump, IntPtr.Zero);
        }
        return 0;
    }

    // ---------------------------------------------------------------- 番茄钟（命令入口）

    static bool IsOwnProcess(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0) return false;
        try
        {
            Process p = Process.GetProcessById((int)pid);
            return p.ProcessName.StartsWith("FloralPin", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    // 找正在运行的番茄钟窗口（我们自己的窗口，标题固定）
    static IntPtr FindPomodoroWindow()
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd, IntPtr lparam)
        {
            if (WindowTitle(hwnd) == POMODORO_TITLE && IsOwnProcess(hwnd))
            {
                found = hwnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static int CmdPomodoro(string[] args)
    {
        string action = args.Length > 1 ? args[1].ToLowerInvariant() : "start";
        IntPtr hwnd = FindPomodoroWindow();

        if (hwnd != IntPtr.Zero)
        {
            switch (action)
            {
                case "start":
                case "show":
                    ShowWindow(hwnd, SW_SHOW);
                    SetForegroundWindow(hwnd);
                    Console.WriteLine("番茄钟已在运行。");
                    return 0;
                case "toggle":
                    PostMessage(hwnd, PM_TOGGLE, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("已切换 开始/暂停。");
                    return 0;
                case "skip":
                    PostMessage(hwnd, PM_SKIP, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("已跳过当前阶段。");
                    return 0;
                case "reset":
                    PostMessage(hwnd, PM_RESET, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("已重置当前阶段。");
                    return 0;
                case "close":
                    PostMessage(hwnd, PM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine("已关闭番茄钟。");
                    return 0;
                default:
                    Console.WriteLine("未知操作: " + action + "（可用: start/toggle/skip/reset/close）");
                    return 1;
            }
        }

        if (action == "start" || action == "show")
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            PomodoroSettings settings = PomodoroSettings.Load();
            Application.Run(new PomodoroForm(settings));
            return 0;
        }

        Console.WriteLine("番茄钟没有在运行（用 pomodoro start 打开）。");
        return 1;
    }

    static void PrintUsage()
    {
        Console.WriteLine("用法: FloralPin.exe [watch|toggle [hwnd]|pin|restore|status|pomodoro [start|toggle|skip|reset|close]|install|uninstall]");
    }

    [STAThread]
    static int Main(string[] args)
    {
        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "status";
        switch (command)
        {
            case "watch": return CmdWatch();
            case "toggle": return CmdToggle(args);
            case "pin": return CmdPinAll();
            case "restore":
            case "unpin": return CmdRestoreAll();
            case "status": return CmdStatus();
            case "pomodoro":
            case "pomo": return CmdPomodoro(args);
            case "install": return CmdAutostart(true);
            case "uninstall": return CmdAutostart(false);
            case "debug": return CmdDebug();
            default:
                PrintUsage();
                return 1;
        }
    }

    // ================= 番茄钟：配置 =================

    class PomodoroSettings
    {
        public double FocusMinutes = 25.0;
        public double ShortBreakMinutes = 5.0;
        public double LongBreakMinutes = 15.0;
        public int CyclesBeforeLongBreak = 4;
        public bool AutoStartNext = true;
        public bool Sound = true;
        public bool Notify = true;
        public int WindowX = -1;
        public int WindowY = -1;

        public static string FilePath()
        {
            return System.IO.Path.Combine(RuntimeDir(), "pomodoro.json");
        }

        public static PomodoroSettings Load()
        {
            PomodoroSettings s = new PomodoroSettings();
            string text = null;
            try
            {
                if (System.IO.File.Exists(FilePath())) text = System.IO.File.ReadAllText(FilePath());
            }
            catch { }
            if (text == null)
            {
                s.Save(); // 首次运行写出默认配置，方便直接改
                return s;
            }
            s.FocusMinutes = ReadNumber(text, "focus_minutes", s.FocusMinutes);
            s.ShortBreakMinutes = ReadNumber(text, "short_break_minutes", s.ShortBreakMinutes);
            s.LongBreakMinutes = ReadNumber(text, "long_break_minutes", s.LongBreakMinutes);
            s.CyclesBeforeLongBreak = (int)Math.Max(1, Math.Round(ReadNumber(text, "cycles_before_long_break", s.CyclesBeforeLongBreak)));
            s.AutoStartNext = ReadBool(text, "auto_start_next", s.AutoStartNext);
            s.Sound = ReadBool(text, "sound", s.Sound);
            s.Notify = ReadBool(text, "notify", s.Notify);
            s.WindowX = (int)Math.Round(ReadNumber(text, "window_x", s.WindowX));
            s.WindowY = (int)Math.Round(ReadNumber(text, "window_y", s.WindowY));
            return s;
        }

        public void Save()
        {
            string json =
                "{\n" +
                "  \"_说明\": \"番茄钟配置；改完保存后，重新打开番茄钟生效。window_x/window_y 为窗口位置，会自动写回\",\n" +
                "  \"focus_minutes\": " + Fmt(FocusMinutes) + ",\n" +
                "  \"short_break_minutes\": " + Fmt(ShortBreakMinutes) + ",\n" +
                "  \"long_break_minutes\": " + Fmt(LongBreakMinutes) + ",\n" +
                "  \"cycles_before_long_break\": " + CyclesBeforeLongBreak.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"auto_start_next\": " + (AutoStartNext ? "true" : "false") + ",\n" +
                "  \"sound\": " + (Sound ? "true" : "false") + ",\n" +
                "  \"notify\": " + (Notify ? "true" : "false") + ",\n" +
                "  \"window_x\": " + WindowX.ToString(CultureInfo.InvariantCulture) + ",\n" +
                "  \"window_y\": " + WindowY.ToString(CultureInfo.InvariantCulture) + "\n" +
                "}\n";
            try
            {
                System.IO.File.WriteAllText(FilePath(), json);
            }
            catch { }
        }

        static string Fmt(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static double ReadNumber(string text, string key, double fallback)
        {
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(-?[0-9]+(?:\\.[0-9]+)?)");
            if (!m.Success) return fallback;
            double v;
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }

        static bool ReadBool(string text, string key, bool fallback)
        {
            Match m = Regex.Match(text, "\"" + Regex.Escape(key) + "\"\\s*:\\s*(true|false)");
            if (!m.Success) return fallback;
            return m.Groups[1].Value == "true";
        }
    }

    // ================= 番茄钟：倒计时窗口 =================

    class PomodoroForm : Form
    {
        const int PHASE_FOCUS = 0;
        const int PHASE_SHORT = 1;
        const int PHASE_LONG = 2;
        const int WIN_W = 246;
        const int WIN_H = 64;

        PomodoroSettings settings;
        Label timeLabel;
        Label phaseLabel;
        Label dotsLabel;
        NotifyIcon tray;
        WinTimer ticker;
        List<ToolStripMenuItem> runItems = new List<ToolStripMenuItem>();
        List<ToolStripMenuItem> layerItems = new List<ToolStripMenuItem>();

        int phase = PHASE_FOCUS;
        int remainingSeconds = 25 * 60;
        bool running = true;
        int focusDoneInSet = 0;

        bool dragging = false;
        bool dragged = false;
        Point dragCursor;
        Point dragWindowPos;

        static readonly Color ColorBg = Color.FromArgb(32, 34, 37);
        static readonly Color ColorText = Color.FromArgb(232, 234, 237);
        static readonly Color ColorDim = Color.FromArgb(140, 146, 154);
        static readonly Color ColorTrack = Color.FromArgb(70, 74, 80);
        static readonly Color ColorBorder = Color.FromArgb(62, 66, 72);
        static readonly Color ColorFocus = Color.FromArgb(255, 107, 91);
        static readonly Color ColorBreak = Color.FromArgb(78, 203, 113);

        public PomodoroForm(PomodoroSettings s)
        {
            settings = s;

            Text = POMODORO_TITLE;           // 供命令行/热键识别；无边框窗口不显示标题
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(WIN_W, WIN_H);
            BackColor = ColorBg;
            DoubleBuffered = true;
            KeyPreview = true;

            timeLabel = new Label();
            timeLabel.AutoSize = false;
            timeLabel.Location = new Point(14, 6);
            timeLabel.Size = new Size(128, 54);
            timeLabel.Font = new Font("Consolas", 21f, FontStyle.Bold);
            timeLabel.ForeColor = ColorText;
            timeLabel.TextAlign = ContentAlignment.MiddleLeft;
            timeLabel.BackColor = Color.Transparent;
            Controls.Add(timeLabel);

            phaseLabel = new Label();
            phaseLabel.AutoSize = false;
            phaseLabel.Location = new Point(144, 10);
            phaseLabel.Size = new Size(96, 22);
            phaseLabel.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            phaseLabel.ForeColor = ColorFocus;
            phaseLabel.TextAlign = ContentAlignment.MiddleLeft;
            phaseLabel.BackColor = Color.Transparent;
            Controls.Add(phaseLabel);

            dotsLabel = new Label();
            dotsLabel.AutoSize = false;
            dotsLabel.Location = new Point(144, 34);
            dotsLabel.Size = new Size(96, 20);
            dotsLabel.Font = new Font("Microsoft YaHei UI", 9f, FontStyle.Regular);
            dotsLabel.ForeColor = ColorDim;
            dotsLabel.TextAlign = ContentAlignment.MiddleLeft;
            dotsLabel.BackColor = Color.Transparent;
            Controls.Add(dotsLabel);

            remainingSeconds = PhaseTotalSeconds();
            if (settings.WindowX >= 0 && settings.WindowY >= 0)
            {
                Location = new Point(settings.WindowX, settings.WindowY);
            }
            else
            {
                Rectangle wa = Screen.PrimaryScreen.WorkingArea;
                Location = new Point(wa.Right - WIN_W - 24, wa.Top + 24);
            }

            using (GraphicsPath path = RoundedRect(new Rectangle(0, 0, WIN_W, WIN_H), 14))
            {
                Region = new Region(path);
            }

            BuildTray();
            ticker = new WinTimer();
            ticker.Interval = 1000;
            ticker.Tick += delegate { OnTick(); };
            ticker.Start();
            UpdateUi();
            Log("番茄钟：窗口打开（" + PhaseLabel() + " " + FmtSeconds(remainingSeconds) + "）");
        }

        static GraphicsPath RoundedRect(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int d = radius * 2;
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // ---------- 状态 ----------

        int PhaseTotalSeconds()
        {
            double minutes;
            if (phase == PHASE_FOCUS) minutes = settings.FocusMinutes;
            else if (phase == PHASE_SHORT) minutes = settings.ShortBreakMinutes;
            else minutes = settings.LongBreakMinutes;
            return Math.Max(1, (int)Math.Round(minutes * 60.0));
        }

        string PhaseLabel()
        {
            if (phase == PHASE_FOCUS) return "专注";
            if (phase == PHASE_SHORT) return "短休";
            return "长休";
        }

        Color PhaseColor()
        {
            return phase == PHASE_FOCUS ? ColorFocus : ColorBreak;
        }

        static string FmtSeconds(int seconds)
        {
            int s = Math.Max(0, seconds);
            return (s / 60).ToString("00") + ":" + (s % 60).ToString("00");
        }

        static string Fmt(double minutes)
        {
            return minutes.ToString("0.###", CultureInfo.InvariantCulture);
        }

        void EnterPhase(int newPhase, bool autoRun)
        {
            phase = newPhase;
            remainingSeconds = PhaseTotalSeconds();
            running = autoRun;
            UpdateUi();
        }

        void ToggleRun()
        {
            running = !running;
            Log("番茄钟：" + (running ? "开始" : "暂停") + "（" + PhaseLabel() + " " + FmtSeconds(remainingSeconds) + "）");
            UpdateUi();
        }

        void ResetPhase()
        {
            remainingSeconds = PhaseTotalSeconds();
            running = false;
            Log("番茄钟：重置本阶段（" + PhaseLabel() + "）");
            UpdateUi();
        }

        void ResetAll()
        {
            phase = PHASE_FOCUS;
            focusDoneInSet = 0;
            remainingSeconds = PhaseTotalSeconds();
            running = false;
            Log("番茄钟：重置全部");
            UpdateUi();
        }

        void SkipPhase()
        {
            string skipped = PhaseLabel();
            if (phase == PHASE_FOCUS) EnterPhase(PHASE_SHORT, false); // 跳过不算完成，不记入轮次
            else EnterPhase(PHASE_FOCUS, false);
            Log("番茄钟：跳过 " + skipped + " → " + PhaseLabel());
        }

        void OnTick()
        {
            if (!running) return;
            remainingSeconds--;
            if (remainingSeconds <= 0)
            {
                FinishPhase();
                return;
            }
            UpdateUi();
        }

        void FinishPhase()
        {
            string finished = PhaseLabel();
            bool longBreak = false;
            if (phase == PHASE_FOCUS)
            {
                focusDoneInSet++;
                int cycles = Math.Max(1, settings.CyclesBeforeLongBreak);
                longBreak = (focusDoneInSet % cycles) == 0;
                EnterPhase(longBreak ? PHASE_LONG : PHASE_SHORT, settings.AutoStartNext);
                Notify("专注结束", longBreak
                    ? "完成一轮！长休 " + Fmt((settings.LongBreakMinutes)) + " 分钟吧"
                    : "休息 " + Fmt(settings.ShortBreakMinutes) + " 分钟吧");
            }
            else
            {
                EnterPhase(PHASE_FOCUS, settings.AutoStartNext);
                Notify("休息结束", "继续专注 " + Fmt(settings.FocusMinutes) + " 分钟");
            }
            Log("番茄钟：" + finished + " 结束 → " + PhaseLabel() + (running ? "（自动开始）" : "（已暂停）"));
        }

        void Notify(string title, string text)
        {
            if (settings.Notify && tray != null)
            {
                try { tray.ShowBalloonTip(6000, title, text, ToolTipIcon.Info); } catch { }
            }
            if (settings.Sound)
            {
                try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        void UpdateUi()
        {
            int total = PhaseTotalSeconds();
            if (remainingSeconds > total) remainingSeconds = total;
            timeLabel.Text = FmtSeconds(remainingSeconds);
            timeLabel.ForeColor = running ? ColorText : ColorDim;
            phaseLabel.Text = PhaseLabel() + (running ? "" : " · 暂停");
            phaseLabel.ForeColor = PhaseColor();

            int cycles = Math.Max(1, settings.CyclesBeforeLongBreak);
            int done = focusDoneInSet % cycles;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < cycles; i++) sb.Append(i < done ? "●" : "○");
            dotsLabel.Text = sb.ToString();

            if (tray != null)
            {
                string tip = "番茄钟 " + phaseLabel.Text + " " + timeLabel.Text;
                tray.Text = tip.Length > 62 ? tip.Substring(0, 62) : tip;
            }
            foreach (ToolStripMenuItem item in runItems) item.Text = running ? "暂停" : "开始";
            foreach (ToolStripMenuItem item in layerItems)
            {
                item.Text = IsOnDesktop() ? "恢复置顶" : "钉到桌面";
            }
            Invalidate();
        }

        // ---------- 桌面层 ----------

        bool IsOnDesktop()
        {
            IntPtr desktop = FindDesktopParent();
            return desktop != IntPtr.Zero && GetParent(Handle) == desktop;
        }

        void ToggleLayer()
        {
            IntPtr desktop = FindDesktopParent();
            if (desktop == IntPtr.Zero)
            {
                Log("番茄钟：找不到桌面图标层，无法切换");
                return;
            }
            string error;
            if (IsOnDesktop())
            {
                if (UnpinWindow(Handle, out error)) Log("番茄钟：恢复置顶");
                else Log("番茄钟：恢复置顶失败 " + error);
            }
            else
            {
                if (PinWindow(Handle, desktop, out error)) Log("番茄钟：钉到桌面");
                else Log("番茄钟：钉到桌面失败 " + error);
            }
            UpdateUi();
        }

        // ---------- 外观 ----------

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

            int total = PhaseTotalSeconds();
            double frac = 1.0 - (double)Math.Max(0, remainingSeconds) / total;
            if (frac < 0) frac = 0;
            if (frac > 1) frac = 1;

            int inner = ClientSize.Width - 8;
            int doneWidth = (int)Math.Round(inner * frac);
            int y = ClientSize.Height - 8;
            using (Brush accent = new SolidBrush(PhaseColor()))
            {
                e.Graphics.FillRectangle(accent, 4, y, doneWidth, 3);
            }
            using (Brush track = new SolidBrush(ColorTrack))
            {
                e.Graphics.FillRectangle(track, 4 + doneWidth, y, inner - doneWidth, 3);
            }
            using (Pen border = new Pen(ColorBorder))
            {
                e.Graphics.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }
        }

        // ---------- 交互 ----------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                dragged = false;
                dragCursor = Cursor.Position;
                dragWindowPos = Location;
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                Point now = Cursor.Position;
                int dx = now.X - dragCursor.X;
                int dy = now.Y - dragCursor.Y;
                if (Math.Abs(dx) > 3 || Math.Abs(dy) > 3) dragged = true;
                Location = new Point(dragWindowPos.X + dx, dragWindowPos.Y + dy);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && dragging)
            {
                dragging = false;
                Capture = false;
                if (!dragged) ToggleRun();          // 单击 = 开始/暂停
                else SavePosition();
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == PM_TOGGLE) { ToggleRun(); return; }
            if (m.Msg == PM_SKIP) { SkipPhase(); return; }
            if (m.Msg == PM_RESET) { ResetPhase(); return; }
            if (m.Msg == PM_CLOSE) { CloseMe(); return; }
            base.WndProc(ref m);
        }

        // ---------- 托盘与菜单 ----------

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = MakeTomatoIcon();
            tray.Text = "番茄钟";
            tray.Visible = true;
            tray.ContextMenuStrip = BuildMenu();
            tray.DoubleClick += delegate { ShowMe(); };
            ContextMenuStrip = BuildMenu();
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem run = new ToolStripMenuItem("开始", null, delegate { ToggleRun(); });
            runItems.Add(run);
            menu.Items.Add(run);
            menu.Items.Add(new ToolStripMenuItem("跳过当前阶段", null, delegate { SkipPhase(); }));
            menu.Items.Add(new ToolStripMenuItem("重置本阶段", null, delegate { ResetPhase(); }));
            menu.Items.Add(new ToolStripMenuItem("重置全部", null, delegate { ResetAll(); }));
            menu.Items.Add(new ToolStripSeparator());
            ToolStripMenuItem layer = new ToolStripMenuItem("钉到桌面", null, delegate { ToggleLayer(); });
            layerItems.Add(layer);
            menu.Items.Add(layer);
            menu.Items.Add(new ToolStripMenuItem("打开设置文件", null, delegate { OpenSettings(); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关闭番茄钟", null, delegate { CloseMe(); }));
            menu.Opening += delegate { UpdateUi(); };
            return menu;
        }

        void ShowMe()
        {
            ShowWindow(Handle, SW_SHOW);
            Activate();
        }

        void OpenSettings()
        {
            try { Process.Start("notepad.exe", PomodoroSettings.FilePath()); }
            catch (Exception ex) { Log("番茄钟：打开设置失败 " + ex.Message); }
        }

        void SavePosition()
        {
            try
            {
                if (GetParent(Handle) != IntPtr.Zero) return; // 贴在桌面层时坐标是相对父窗口的，不保存
                settings.WindowX = Location.X;
                settings.WindowY = Location.Y;
                settings.Save();
            }
            catch { }
        }

        void CloseMe()
        {
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            SavePosition();
            if (ticker != null) ticker.Stop();
            if (tray != null)
            {
                tray.Visible = false;
                tray.Dispose();
                tray = null;
            }
            Log("番茄钟：关闭");
            base.OnFormClosing(e);
        }

        static Icon MakeTomatoIcon()
        {
            Icon icon = SystemIcons.Application;
            try
            {
                using (Bitmap bmp = new Bitmap(32, 32))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        using (Brush body = new SolidBrush(Color.FromArgb(230, 70, 60)))
                        {
                            g.FillEllipse(body, 4, 9, 24, 21);
                        }
                        using (Brush leaf = new SolidBrush(Color.FromArgb(60, 165, 75)))
                        {
                            g.FillEllipse(leaf, 13, 4, 7, 7);
                        }
                    }
                    IntPtr handle = bmp.GetHicon();
                    Icon created = (Icon)Icon.FromHandle(handle).Clone();
                    DestroyIcon(handle);
                    icon = created;
                }
            }
            catch { }
            return icon;
        }
    }
}
