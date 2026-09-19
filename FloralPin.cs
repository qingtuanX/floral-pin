// FloralPin.cs — 花笺（floral-notepaper）便签/磁贴窗口的「钉到桌面 / 恢复置顶」切换工具
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
//   FloralPin.exe install         开机自启（启动文件夹快捷方式）
//   FloralPin.exe uninstall       取消开机自启
//
// 运行日志：本目录 FloralPin.log（热键注册结果、每次切换的记录）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

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

    // 从命中的子窗口向上找便签/磁贴窗口本身
    static IntPtr FindSurfaceAncestor(IntPtr hwnd)
    {
        IntPtr current = hwnd;
        for (int i = 0; i < 12 && current != IntPtr.Zero; i++)
        {
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

    static void PrintUsage()
    {
        Console.WriteLine("用法: FloralPin.exe [watch|toggle [hwnd]|pin|restore|status|install|uninstall]");
    }

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
            case "install": return CmdAutostart(true);
            case "uninstall": return CmdAutostart(false);
            case "debug": return CmdDebug();
            default:
                PrintUsage();
                return 1;
        }
    }
}
