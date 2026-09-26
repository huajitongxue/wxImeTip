using System.Runtime.InteropServices;
using System.Windows.Media;

namespace ImeTip;

/// <summary>
/// 窗口特效（背景模糊 / 亚克力 / 圆角）。
///
/// ─── 为什么不用 Win11 官方的系统背景材质 ───
///   官方路线是 DwmSetWindowAttribute(DWMWA_SYSTEMBACKDROP_TYPE) + DwmExtendFrameIntoClientArea。
///   但微软文档明确列出"回退为纯色"的条件，**其中包含「应用窗口处于停用状态」**。
///   而本程序的悬浮窗为了不抢用户输入焦点，加了 WS_EX_NOACTIVATE、并让 WM_MOUSEACTIVATE
///   永远返回 MA_NOACTIVATE —— **它永远处于停用状态**，所以系统材质对它只会画出纯色，
///   永远不可能有模糊。这条路无论怎么配都不会生效，所以直接放弃。
///
/// ─── 那用什么 ───
///   用未公开的 SetWindowCompositionAttribute + ACCENT_ENABLE_BLURBEHIND / ACRYLICBLURBEHIND。
///   它恰恰是**唯一在"失焦状态"下仍保留模糊**的路径；而且有实测表明它在 Win11 上对
///   **ToolWindow** 仍然可用 —— 本程序的窗口正是 WS_EX_TOOLWINDOW。
///   TranslucentTB 自己也是用这个 API 给"同样不会被激活"的任务栏加模糊的，是很有力的旁证。
///
/// ─── 但必须假设它会失败 ───
///   未公开 API、Win11 加强了亮度混合（可能偏暗）、甚至可能在某些版本上完全无效。
///   所以本类的所有方法 **只返回成功/失败，绝不抛异常**；
///   调用方拿到 false 就退化成"纯透明 + 可调 alpha"，保证界面不出事。
/// </summary>
internal static class WindowEffects
{
    /// <summary>亚克力模糊需要 Win10 1809(17763) 及以上；更老的系统直接判为不可用。</summary>
    internal static bool BlurAvailable =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763);

    /// <summary>
    /// 尝试给窗口贴上模糊/亚克力背景。
    /// </summary>
    /// <param name="hwnd">窗口句柄。为 0 直接失败。</param>
    /// <param name="mode">模糊种类；<see cref="BlurMode.None"/> 视为失败（调用方走纯透明）。</param>
    /// <param name="tint">底色（只用它的 RGB，alpha 由 <paramref name="alpha"/> 决定）。</param>
    /// <param name="alpha">底色不透明度 0-255。</param>
    /// <returns>成功 true；任何异常或 API 不可用都返回 false。</returns>
    internal static bool TrySetAccent(IntPtr hwnd, BlurMode mode, Color tint, byte alpha)
    {
        if (hwnd == IntPtr.Zero || mode == BlurMode.None || !BlurAvailable) return false;

        try
        {
            Apply(hwnd, mode == BlurMode.Acrylic ? 4 : 3, PackAbgr(alpha, tint));
            return true;
        }
        catch
        {
            // 包含 EntryPointNotFoundException（老系统没有这个导出）等一切情况。
            // 绝不向外抛：模糊只是锦上添花，失败必须能安静地降级。
            return false;
        }
    }

    /// <summary>关掉模糊，恢复成普通窗口。用于切回深色/浅色主题、或用户把模糊设成"无"。</summary>
    internal static void ClearAccent(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            Apply(hwnd, 0, 0);      // AccentState = 0 → ACCENT_DISABLED
        }
        catch
        {
            // 同上，失败无所谓
        }
    }

    /// <summary>
    /// 把整个窗口变成 DWM 的"玻璃区域"，让透明真正生效。
    ///
    /// ⚠️ **必须和 <c>CompositionTarget.BackgroundColor = Transparent</c> 配套使用，缺一不可**：
    ///   前者让 WPF 的内容带 Alpha 画出来，这一步让 DWM 按 Alpha 去合成。
    ///   只做前者（很容易漏），窗口客户区在 DWM 眼里仍是不透明的，
    ///   最终就会渲染成一个不透明的色块 —— 用户看到的就是"卡片背景根本不是透明的"。
    ///
    /// 边距用全 -1 = "整窗都是玻璃"（官方文档里的写法）。
    /// </summary>
    /// <returns>成功 true；DWM 合成被关闭或 API 缺失时 false。</returns>
    internal static bool TryExtendGlassFrame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            var margins = new NativeMethods.MARGINS
            {
                LeftWidth = -1,
                RightWidth = -1,
                TopHeight = -1,
                BottomHeight = -1,
            };
            return NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0;
        }
        catch
        {
            // DWM 合成被关（现代 Windows 不会）或 API 缺失时静默跳过
            return false;
        }
    }

    /// <summary>
    /// 试着让系统给窗口加圆角。
    /// 对"透明合成"的窗口，系统多数情况下会忽略 —— 所以圆角主要还是靠
    /// XAML 里 Border.CornerRadius 自己画的（窗口四角本就是透明的，视觉上就是圆角）。
    /// 这里纯属锦上添花，失败完全没关系。
    /// </summary>
    internal static void TrySetRoundedCorners(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;

        try
        {
            int round = 2;      // DWMWCP_ROUND
            NativeMethods.DwmSetWindowAttribute(
                hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        }
        catch
        {
        }
    }

    /// <summary>
    /// 真正调用 SetWindowCompositionAttribute。
    /// 结构体必须先封送到非托管内存 —— API 要的是指针，不能直接传托管结构体的引用。
    /// </summary>
    private static void Apply(IntPtr hwnd, int accentState, int gradientColor)
    {
        var policy = new NativeMethods.ACCENT_POLICY
        {
            AccentState = accentState,
            AccentFlags = 2,            // 社区实测值，用来启用 tint
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<NativeMethods.ACCENT_POLICY>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, fDeleteOld: false);

            var data = new NativeMethods.WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = NativeMethods.WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };

            NativeMethods.SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// WPF 的 Color（ARGB）→ 这个 API 要的 <b>0xAABBGGRR</b>（ABGR）。
    /// 顺序不同的原因是 COLORREF 系列历来是 BGR 排布，而 alpha 塞在最高字节。
    /// 写错的话症状是"颜色看着不对/偏色"，很容易被误当成"模糊没生效"。
    /// </summary>
    private static int PackAbgr(byte alpha, Color color)
        => unchecked((alpha << 24) | (color.B << 16) | (color.G << 8) | color.R);
}
