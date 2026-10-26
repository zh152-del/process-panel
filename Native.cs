using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ProcessPanel;

// V2.1：窗口合成只依赖 WPF 分层窗口（AllowsTransparency）——alpha 真实参与 DWM 合成，
// 圆角由 WPF Border 抗锯齿绘制。实测本机（Win10 19045 + DWMBlurGlass）上
// SetWindowCompositionAttribute 的亚克力 tint/模糊不生效、非分层窗口 alpha 被忽略、
// SetWindowRgn 圆角外露黑角，故相关实现全部移除。
internal static class Native
{
    // ================= 置于最底层 =================
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private static readonly IntPtr HwndBottom = new IntPtr(1); // HWND_BOTTOM
    private static readonly IntPtr HwndTopmost = new IntPtr(-1); // HWND_TOPMOST
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;

    public static void SendToBottom(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    /// <summary>置顶常驻（不抢焦点）：面板需要始终可见时使用。</summary>
    public static void SetTopMost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
    }

    /// <summary>
    /// 启动预览：临时置顶若干秒，让用户看到面板位置，随后自动回到最底层常驻。
    /// 不抢焦点（SWP_NOACTIVATE），不改变常驻置底行为。
    /// </summary>
    public static void PreviewToTopThenBottom(Window window, int seconds)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        };
        t.Start();
    }

    // ================= 永不激活/抢前台 =================
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static void MakeNonActivating(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        IntPtr ex = GetWindowLongPtr(hwnd, GwlExStyle);
        long merged = ex.ToInt64() | WsExNoActivate | WsExToolWindow;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(merged));
    }

    // ================= 顶层窗口枚举（前台/后台进程分类） =================
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    private const int GwExStyle32 = -20;

    /// <summary>
    /// 收集「拥有可见顶层主窗口」的进程 PID 集合。
    /// 判定：窗口可见 + 有非空标题 + 不是工具窗口(WS_EX_TOOLWINDOW)。
    /// </summary>
    public static HashSet<int> GetVisibleWindowPids()
    {
        var pids = new HashSet<int>();
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindow(hWnd)) return true;
            if (!IsWindowVisible(hWnd)) return true;
            int ex = GetWindowLong(hWnd, GwExStyle32);
            if ((ex & WsExToolWindow) != 0) return true;
            if (GetWindowTextLength(hWnd) == 0) return true;

            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pid != 0) pids.Add((int)pid);
            return true;
        }, IntPtr.Zero);
        return pids;
    }

    /// <summary>当前前台活动窗口所属进程 PID；无窗口时返回 0。</summary>
    public static int GetForegroundPid()
    {
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return 0;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return (int)pid;
    }
}
