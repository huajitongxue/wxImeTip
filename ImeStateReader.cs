using System.Text;

namespace ImeTip;

/// <summary>程序要显示的四种状态。</summary>
internal enum ImeMode
{
    /// <summary>读不出来：前台是管理员权限窗口，或目标进程没有响应。显示为「?」。</summary>
    Unknown,

    /// <summary>该窗口不通过 IMM32 暴露状态（如 Windows 11 记事本），本次不更新显示。</summary>
    Unreliable,

    /// <summary>接下来敲键盘会打出中文。</summary>
    Chinese,

    /// <summary>接下来敲键盘会打出英文。</summary>
    English,
}

/// <summary>
/// 一次采样的完整结果。除了结论还保留所有原始数据，方便出问题时排查。
/// </summary>
internal readonly record struct ImeState(
    ImeMode Mode,
    IntPtr ForegroundWindow,
    string WindowClass,
    IntPtr KeyboardLayout,
    int LanguageId,            // HKL 低 16 位：0x0804 简体中文，0x0409 英语(美国)
    bool LayoutIsIme,
    IntPtr ImeWindow,
    int OpenStatus,            // IME 打开状态（判定中文的必要条件），-1 = 读取失败
    int ConversionMode,        // 转换模式位，-1 = 读取失败
    string RuleUsed,           // 本次用的哪条判定路径，便于排查
    string WindowTitle)
{
    public string Describe()
    {
        string open = OpenStatus < 0 ? "失败" : OpenStatus.ToString();
        string conv = ConversionMode < 0 ? "失败" : ConversionMode.ToString();

        return $"模式={Mode,-10} 路径={RuleUsed,-18} 语言ID=0x{LanguageId:X4} " +
               $"打开={open,-4} 转换模式={conv,-6} " +
               $"前台=0x{ForegroundWindow.ToInt64():X8} IME窗口=0x{ImeWindow.ToInt64():X8} " +
               $"类={WindowClass} 标题={WindowTitle}";
    }
}

/// <summary>
/// 读取当前前台窗口的输入法中/英状态。
///
/// ─── 判定规则（依据 aardio/ImTip 官方文档，实机验证）───
///     中文 = (opened != 0) && (convMode & IME_CMODE_NATIVE) != 0
/// ⚠️ 只看 convMode 会得到完全错误的结果：输入法"关闭"（英文直通）时
///    convMode 仍可能等于 1。本项目最大的一个坑。
///
/// ─── 读取路径（依次尝试）───
///   ① 顶层窗口的默认 IME 窗口
///   ② 真正持有键盘焦点的子窗口的默认 IME 窗口
///      Windows 11 记事本（WinUI3 打包应用）走的是系统 IME，
///      顶层窗口永远报 opened=0，需要换目标再试。
///   ③ 都不行时，若该窗口类已知不可靠 → 返回 Unreliable（保持上次显示，绝不瞎报）
///
/// ─── 转换模式取值对照（标准模式）───
///     0 英文半角英文符号 / 1 中文半角英文符号 / 8 英文全角 / 9 中文全角
///     1024 英文半角中文符号 / 1025 中文半角中文符号 / 1033 中文全角中文符号
///
/// ─── 怪异模式 ───
///   官方文档列出的一种不规范实现："opened 中文返回 2、英文返回 1，convMode 无效"。
///   对这类输入法改用 (opened == 2) 判定中文。
/// </summary>
internal static class ImeStateReader
{
    private const int LanguageIdEnglishUs = 0x0409;

    /// <summary>
    /// 系统外壳界面（任务栏、托盘弹窗、桌面、开始菜单）。
    ///
    /// 这些窗口不是用户的"打字目标"——点它们并不改变"接下来会打出什么"。
    /// 但它们的线程没有真实的输入法上下文，`IMC_GETOPENSTATUS` 只会返回 0，
    /// 于是每次点一下任务栏，指示器就会莫名其妙从「中」跳成「英」。
    /// 正确做法是：遇到它们就保持上一次显示，不做更新。
    /// </summary>
    private static readonly HashSet<string> ShellSurfaceClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Shell_TrayWnd",                          // 主任务栏
            "Shell_SecondaryTrayWnd",                 // 副显示器任务栏
            "TopLevelWindowForOverflowXamlIsland",    // Win11 托盘溢出弹窗（点箭头出来的那个）
            "NotifyIconOverflowWindow",               // Win10 托盘溢出弹窗
            "Progman",                                // 桌面
            "WorkerW",                                // 桌面（壁纸层）
            "ApplicationManager_DesktopShellWindow",  // 开始菜单
        };

    /// <summary>
    /// 已知不通过 IMM32 暴露输入法状态的窗口类。
    /// 对这类窗口老实的做法是"不更新显示"，而不是硬报一个错误的状态。
    /// </summary>
    private static readonly HashSet<string> UnreliableWindowClasses =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Notepad",   // Windows 11 记事本（WinUI3）：顶层窗口 opened 恒为 0（现已由"焦点子窗口"路径兜住）
        };

    /// <summary>
    /// 采样一次。
    /// 返回 null 表示"本次不适用"（前台窗口就是自己、或压根没有前台窗口），
    /// 调用方应当保持上一次的状态不变。
    /// </summary>
    public static ImeState? Read(IntPtr ownWindow)
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || hwnd == ownWindow) return null;

        uint tid = NativeMethods.GetWindowThreadProcessId(hwnd, IntPtr.Zero);
        string title = DescribeWindow(hwnd, out string className);

        if (tid == 0)
            return Make(ImeMode.Unknown, hwnd, className, IntPtr.Zero, 0, false,
                IntPtr.Zero, -1, -1, "线程ID无效", title);

        // 当前选的键盘布局是什么
        IntPtr hkl = NativeMethods.GetKeyboardLayout(tid);
        int languageId = (int)(hkl.ToInt64() & 0xFFFF);
        bool layoutIsIme = NativeMethods.ImmIsIME(hkl);

        // 系统外壳界面（任务栏 / 托盘弹窗 / 桌面 / 开始菜单）：
        // 用户点它们不是在打字，这些窗口也没有真实的输入法上下文。
        // 一律保持上一次显示，否则"点一下任务栏，指示器就从『中』跳到『英』"。
        //
        // ⚠️ 这个判断必须放在"布局非输入法 → 英文"之前：
        //    外壳窗口所在线程的键盘布局是什么完全没保证，
        //    万一它不是输入法，就会先被判成英文跑掉，这个检查就永远轮不到。
        if (ShellSurfaceClasses.Contains(className))
            return Make(ImeMode.Unreliable, hwnd, className, hkl, languageId, layoutIsIme,
                IntPtr.Zero, -1, -1, "系统界面(任务栏/托盘)", title);

        // 普通键盘布局（如「英语(美国)」）不是输入法，必定打出英文，无需再问。
        if (!layoutIsIme)
            return Make(ImeMode.English, hwnd, className, hkl, languageId, false,
                IntPtr.Zero, -1, -1, "布局非输入法", title);

        // ① 先问顶层窗口的默认 IME 窗口
        IntPtr imeWnd = NativeMethods.ImmGetDefaultIMEWnd(hwnd);
        int openStatus = imeWnd == IntPtr.Zero ? -1 : Query(imeWnd, NativeMethods.IMC_GETOPENSTATUS);
        int conversionMode = imeWnd == IntPtr.Zero ? -1 : Query(imeWnd, NativeMethods.IMC_GETCONVERSIONMODE);
        string path = "顶层窗口";

        // ② 顶层窗口没有报告"打开"时，改问"真正持有键盘焦点的子窗口"。
        //
        //    为什么必须这样做：opened == 0 有两种完全不同的可能——
        //      a) 确实处于英文模式（正常情况）
        //      b) 这个窗口根本不通过 IMM32 暴露状态（如 Windows 11 记事本，顶层永远报 0）
        //    光看顶层窗口的值区分不了这两种情况，所以一律以焦点子窗口的值优先。
        //
        //    实测（Win11 + 微信输入法 + 记事本）：
        //      中文时：顶层 opened=0（错的），焦点子窗口 opened=1（对的）
        //      英文时：两者都是 0（此时 0 恰好就是正确答案）
        if (openStatus <= 0)
        {
            IntPtr focus = GetFocusWindow(tid, hwnd);
            if (focus != IntPtr.Zero)
            {
                IntPtr imeWnd2 = NativeMethods.ImmGetDefaultIMEWnd(focus);
                if (imeWnd2 != IntPtr.Zero && imeWnd2 != imeWnd)
                {
                    int open2 = Query(imeWnd2, NativeMethods.IMC_GETOPENSTATUS);

                    // ⚠️ 这里是 >= 0 而不是 > 0：只要查询成功就采用，0 同样是有效结果。
                    // 写成 > 0 会把手动切到英文的情况误判成"读不到"，
                    // 进而保持上一次显示——那就永远切不到"英"了。
                    if (open2 >= 0)
                    {
                        imeWnd = imeWnd2;
                        openStatus = open2;
                        conversionMode = Query(imeWnd2, NativeMethods.IMC_GETCONVERSIONMODE);
                        path = "焦点子窗口";
                    }
                }
            }
        }

        // ③ 注意：这里判的是"查询失败"（负数），不是"读到了 0"。
        //    读到 0 是有效结果（英文），只有根本读不通才叫未知。
        if (openStatus < 0)
        {
            bool knownBuggy = UnreliableWindowClasses.Contains(className);
            return Make(knownBuggy ? ImeMode.Unreliable : ImeMode.Unknown,
                hwnd, className, hkl, languageId, true, imeWnd, openStatus, conversionMode,
                knownBuggy ? "状态不可靠(保持上次)" : path + ":打开状态读取失败", title);
        }

        // ④ 判定
        bool opened = openStatus != 0;

        // 英语键盘布局永远不是中文输入状态（官方文档明确要求处理）
        if (languageId == LanguageIdEnglishUs) opened = false;

        bool chinese;
        string rule;

        if (openStatus == 2)
        {
            // 怪异模式：该输入法用 2 表示中文、1 表示英文，convMode 无效
            chinese = true;
            rule = path + ":怪异模式(打开=2)";
        }
        else if (conversionMode < 0)
        {
            chinese = false;
            rule = path + ":转换模式读取失败";
        }
        else
        {
            chinese = opened && (conversionMode & NativeMethods.IME_CMODE_NATIVE) != 0;
            rule = path + ":标准(opened且NATIVE位)";
        }

        return Make(chinese ? ImeMode.Chinese : ImeMode.English, hwnd, className, hkl,
            languageId, true, imeWnd, openStatus, conversionMode, rule, title);
    }

    private static IntPtr GetFocusWindow(uint tid, IntPtr fallback)
    {
        var info = new NativeMethods.GUITHREADINFO
        {
            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GUITHREADINFO>()
        };

        if (!NativeMethods.GetGUIThreadInfo(tid, ref info)) return IntPtr.Zero;
        if (info.hwndFocus == IntPtr.Zero || info.hwndFocus == fallback) return IntPtr.Zero;
        return info.hwndFocus;
    }

    private static ImeState Make(
        ImeMode mode, IntPtr hwnd, string className, IntPtr hkl, int languageId,
        bool layoutIsIme, IntPtr imeWnd, int openStatus, int conversionMode,
        string rule, string title)
        => new(mode, hwnd, className, hkl, languageId, layoutIsIme,
               imeWnd, openStatus, conversionMode, rule, title);

    /// <summary>
    /// 向 IME 窗口发一条 WM_IME_CONTROL 查询，失败返回 -1。
    /// 必须用带超时的 SendMessage，否则目标进程卡死会把我们一起拖住。
    /// </summary>
    private static int Query(IntPtr imeWindow, int command)
    {
        IntPtr ok = NativeMethods.SendMessageTimeout(
            imeWindow,
            NativeMethods.WM_IME_CONTROL,
            new IntPtr(command),
            IntPtr.Zero,
            NativeMethods.SMTO_ABORTIFHUNG,
            200,
            out IntPtr result);

        return ok == IntPtr.Zero ? -1 : unchecked((int)result.ToInt64());
    }

    private static string DescribeWindow(IntPtr hwnd, out string className)
    {
        var title = new StringBuilder(256);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);

        var cls = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, cls, cls.Capacity);
        className = cls.ToString();

        return title.ToString();
    }
}
