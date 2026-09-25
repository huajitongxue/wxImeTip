using System.Runtime.InteropServices;
using System.Text;

namespace ImeTip;

/// <summary>
/// 所有 Win32 API 的声明集中在这里（P/Invoke）。
/// 业务代码不要直接调用本类，统一走 ImeStateReader。
/// </summary>
internal static class NativeMethods
{
    // ───────────── 窗口扩展样式 ─────────────
    internal const int GWL_EXSTYLE = -20;
    internal const int WS_EX_TOOLWINDOW = 0x00000080;   // 不出现在 Alt+Tab 列表
    internal const int WS_EX_NOACTIVATE = 0x08000000;   // 被点击时不夺取焦点

    // ───────────── IME 相关常量 ─────────────
    /// <summary>"本族语言"模式位。中文输入法置 1，英文模式置 0。</summary>
    internal const int IME_CMODE_NATIVE = 0x0001;

    internal const uint WM_IME_CONTROL = 0x0283;
    internal const int IMC_GETCONVERSIONMODE = 0x0001;
    internal const int IMC_GETOPENSTATUS = 0x0005;

    /// <summary>目标窗口无响应时不要傻等，直接放弃这次查询。</summary>
    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    // ───────────── 窗口消息（用来钩住本窗口自己的消息循环）─────────────
    internal const uint WM_MOUSEACTIVATE = 0x0021;   // 鼠标点在本窗口上，问"要不要激活"
    internal const int MA_NOACTIVATE = 3;            // 回答：不要激活
    internal const uint WM_MOVING = 0x0216;          // 拖动过程中，报告"尚未生效"的新位置
    internal const uint WM_EXITSIZEMOVE = 0x0232;    // 拖动/缩放结束

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;

    // ───────────── 右键菜单相关 ─────────────
    /// <summary>设置窗口的"拥有者"。注意要用 Ptr 版本写入，详见 SetWindowLongPtr。</summary>
    internal const int GWL_HWNDPARENT = -8;

    /// <summary>
    /// 良性消息，什么都不做。官方文档要求：弹出菜单后往自己窗口 Post 一条 WM_NULL，
    /// 否则同一个菜单第二次弹出时可能"一闪就没"。
    /// </summary>
    internal const uint WM_NULL = 0x0000;

    // ───────────── 结构体 ─────────────

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hwndActive;
        public IntPtr hwndFocus;      // 真正持有键盘焦点的窗口（可能是子窗口）
        public IntPtr hwndCapture;
        public IntPtr hwndMenuOwner;
        public IntPtr hwndMoveSize;
        public IntPtr hwndCaret;
        public RECT rcCaret;
    }

    // ───────────── user32.dll ─────────────

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetKeyboardLayout(uint idThread);

    /// <summary>带超时的 SendMessage。跨进程发消息必须用它，否则对方卡死会把我们拖住。</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr SendMessageTimeout(
        IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)]
    internal static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    // GWL_EXSTYLE 这类样式值都是 32 位，用 32 位版本即可，32/64 位系统通用。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    internal static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    internal static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    // ───────────── 右键菜单要用到的几个（详见 AppMenu.cs 的说明）─────────────

    /// <summary>
    /// 把某个窗口设为前台。
    /// 下拉菜单只有在"本进程是前台窗口"时才会在点到菜单外面时自动关闭，
    /// 所以弹出菜单前必须先调它。
    /// ⚠️ 返回值要检查：Windows 的"前台锁定"机制可能让它失败。
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>校验句柄是否还有效（菜单关闭时，用户原来那个窗口可能已经被关掉了）。</summary>
    [DllImport("user32.dll")]
    internal static extern bool IsWindow(IntPtr hWnd);

    /// <summary>
    /// 设置窗口的拥有者。句柄是"指针宽度"的值，必须用 Ptr 版本 ——
    /// 上面那个 32 位的 SetWindowLongW 适合放样式位，拿来塞 HWND 在 64 位下会截断。
    /// 本项目只发布 win-x64（见 README），所以直接调 Ptr 入口。
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // ───────────── imm32.dll ─────────────

    /// <summary>判断这个键盘布局是不是输入法（而不是普通键盘，如「英语(美国)」）。</summary>
    [DllImport("imm32.dll")]
    internal static extern bool ImmIsIME(IntPtr hkl);

    /// <summary>取得该窗口所属线程的默认 IME 窗口。跨进程读状态要靠它当"传话筒"。</summary>
    [DllImport("imm32.dll")]
    internal static extern IntPtr ImmGetDefaultIMEWnd(IntPtr hWnd);
}
