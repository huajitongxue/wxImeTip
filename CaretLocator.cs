using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ImeTip;

/// <summary>
/// 取「前台窗口的文本插入点（caret）」位置，供"把悬浮窗召到输入框光标上方"用。
///
/// ─── 为什么路子这么绕（以下每一步都在真机上实测过）───
///   目标程序是 Electron / Chromium 系（目前实测能拿到的是 Cherry Studio）。
///   这类程序**自己画光标，不走系统的 caret** —— 所以 GetGUIThreadInfo 的 hwndCaret
///   永远是 0，只能改问无障碍接口（MSAA）的 OBJID_CARET：
///
///     ① 先**唤醒**：Chromium 的无障碍树**默认是关的**（为了性能），必须给它的渲染子窗口
///        （类名前缀 Chrome_RenderWidgetHostHWND —— 注意**末尾没有数字 1**，
///         网上常见的 `...HWND1` 是 ClassNN，把类名和序号连在一起了）
///        发一条 WM_GETOBJECT（wParam=0, lParam=1）把它叫醒；
///
///     ② 再**取接口**：AccessibleObjectFromWindow(**顶层窗口**, OBJID_CARET, IID_IAccessible)。
///        ⚠️ 必须传顶层窗口 —— 实测传渲染子窗口只会返回 S_FALSE；
///
///     ③ 最后拿**原始指针**按 vtable 槽位调 accLocation（槽 22）。
///        不能用 dynamic、也不能用 [ComImport]，两种写法实测都会抛异常，
///        原因写在 NativeMethods 里那两段注释。
///
/// ─── 冷启动要等约 1 秒 ───
///   首次唤醒后立刻查会返回 S_FALSE，约 1 秒后才就绪 —— 所以提供 <see cref="WarmupIfNeeded"/>，
///   由主窗口在已有的状态轮询里顺手把它唤醒，把这段等待提前消化掉。
///
/// ─── 一律降级，绝不添乱 ───
///   本类的公开方法**不抛异常、不给半成品**：任何一步不满足就返回 false，
///   调用方会退回"召到鼠标旁边"。也就是说，这个功能最坏的结果就是"和现在一样"。
/// </summary>
internal static class CaretLocator
{
    private const ushort VT_I4 = 3;          // VARIANT 类型：32 位整数
    private const int ChildIdSelf = 0;       // CHILDID_SELF：问元素自己，而不是它的子元素

    // IAccessible 的 vtable 槽位（0-based 索引）
    private const int SlotChildCount = 8;    // get_accChildCount
    private const int SlotAccLocation = 22;  // accLocation

    /// <summary>
    /// 唯一认准的目标进程名（<see cref="Process.ProcessName"/>，不含 .exe），
    /// 已归一化：小写、去掉空格 / 点 / 连字符 / 下划线。
    /// 实测 Cherry Studio 的可执行文件是 "Cherry Studio.exe"，归一化后正好是 "cherrystudio"。
    ///
    /// ⚠️ 万一以后它改名导致不生效：翻 %LOCALAPPDATA%\ImeTip\ImeTip.log，
    ///    找 "Ctrl 快捷召唤" 那几行里的「前台进程=xxx」，把 xxx 按同样规则填到这里即可。
    ///    （下面还有 "cherry" + "studio" 的兜底匹配，一般改名不会漏。）
    /// </summary>
    private const string TargetProcessNormalized = "cherrystudio";

    /// <summary>唤醒节流：同一个进程在这个间隔内不重复发 WM_GETOBJECT。</summary>
    private const long RewakeIntervalMs = 5000;

    // ── 预热状态 ──
    private static int _warmedPid;
    private static IntPtr _warmedRenderWnd = IntPtr.Zero;
    private static long _lastWakeTick;
    private static bool _warmupLogged;

    // ===================================================================
    // 预热
    // ===================================================================

    /// <summary>
    /// 趁状态轮询的空档，把当前前台（若是目标程序）的无障碍树唤醒。
    ///
    /// 由 UI 线程调用。内部是 SendMessageTimeout（带超时），不会长时间阻塞；
    /// 而且**只在第一次、以及每 5 秒一次才真正发消息**，平时就是几个整数比较。
    /// 非目标程序、失败，一律静默返回。
    /// </summary>
    internal static void WarmupIfNeeded()
    {
        try
        {
            IntPtr top = NativeMethods.GetForegroundWindow();
            if (top == IntPtr.Zero) return;

            if (!TryGetProcessName(top, out string processName)) return;
            if (!IsTarget(processName)) return;

            uint pid = GetPid(top);
            long now = Environment.TickCount64;

            bool wndAlive = _warmedRenderWnd != IntPtr.Zero
                            && NativeMethods.IsWindow(_warmedRenderWnd);

            // 同一个进程、刚唤醒过、窗口还活着 → 不必重发
            if ((int)pid == _warmedPid && wndAlive && now - _lastWakeTick < RewakeIntervalMs) return;

            IntPtr render = FindRenderWidget(top);
            if (render == IntPtr.Zero) return;

            WakeUp(render);

            _warmedPid = (int)pid;
            _warmedRenderWnd = render;
            _lastWakeTick = now;

            if (!_warmupLogged)
            {
                _warmupLogged = true;
                DiagnosticsLog.Write(
                    $"Cherry Studio 无障碍树 = 已预热（渲染窗口=0x{render.ToInt64():X8}）");
            }
        }
        catch
        {
            // 预热失败无所谓 —— 正式召唤时还会再试一次
        }
    }

    // ===================================================================
    // 取插入点
    // ===================================================================

    /// <summary>
    /// 取前台窗口当前文本插入点的矩形（**屏幕物理像素**，与"召到鼠标旁边"同一坐标系）。
    /// </summary>
    /// <param name="rect">成功时是插入点矩形（实测常见为 1×25 的竖线）。</param>
    /// <param name="detail">
    /// 无论成败都给一句人话，供日志用。成功如 "插入点(434,950 1x25)"；
    /// 失败如 "前台进程=notepad（非目标）" / "此刻没有插入点（光标不在输入框里）"。
    /// </param>
    /// <returns>拿到有效插入点返回 true；其余一律 false（调用方退回鼠标）。</returns>
    internal static bool TryGetCaretRect(out NativeMethods.RECT rect, out string detail)
    {
        rect = default;

        // VARIANT 的内存布局是按 x64 定的（见 NativeMethods）。不是 x64 就别冒险。
        if (!Environment.Is64BitProcess)
        {
            detail = "非 x64 进程，未适配";
            return false;
        }

        IntPtr top = NativeMethods.GetForegroundWindow();
        if (top == IntPtr.Zero)
        {
            detail = "没有前台窗口";
            return false;
        }

        if (!TryGetProcessName(top, out string processName))
        {
            detail = "取前台进程名失败";
            return false;
        }

        // 只对目标程序生效 —— 其它程序一律回去用鼠标位置
        if (!IsTarget(processName))
        {
            detail = $"前台进程={processName}（非目标）";
            return false;
        }

        // 冷启动兜底：还没预热时这里补一次（本次多半仍拿不到，下一次就正常了）
        WarmupIfNeeded();

        IntPtr acc = IntPtr.Zero;
        try
        {
            Guid iid = NativeMethods.IID_IAccessible;
            int hr = NativeMethods.AccessibleObjectFromWindow(
                top, NativeMethods.OBJID_CARET, ref iid, out acc);

            if (hr != 0 || acc == IntPtr.Zero)
            {
                detail = $"取插入点接口失败 hr=0x{hr:X8}";
                return false;
            }

            IntPtr vtable = Marshal.ReadIntPtr(acc);
            if (vtable == IntPtr.Zero)
            {
                detail = "vtable 为空";
                return false;
            }

            // 探针：先调一次接口里唯一不需要 VARIANT 参数的方法，确认它确实可用
            //（返回值不关心；顺便也能促使无障碍树就绪）
            IntPtr childCountAddr = Marshal.ReadIntPtr(vtable, SlotChildCount * IntPtr.Size);
            if (childCountAddr == IntPtr.Zero)
            {
                detail = "get_accChildCount 槽位为空";
                return false;
            }

            Marshal.GetDelegateForFunctionPointer<NativeMethods.GetChildCountFn>(childCountAddr)
                   .Invoke(acc, out _);

            IntPtr accLocationAddr = Marshal.ReadIntPtr(vtable, SlotAccLocation * IntPtr.Size);
            if (accLocationAddr == IntPtr.Zero)
            {
                detail = "accLocation 槽位为空";
                return false;
            }

            var accLocation =
                Marshal.GetDelegateForFunctionPointer<NativeMethods.AccLocationFn>(accLocationAddr);

            var varChild = new NativeMethods.VARIANT { vt = VT_I4, lVal = ChildIdSelf };
            int hr2 = accLocation(acc, out int x, out int y, out int w, out int h, varChild);

            // ⚠️ S_FALSE(1) **不是失败** —— 它的意思是"此刻确实没有插入点"
            //（比如焦点不在输入框上）。这种情况安静地退回鼠标就好。
            if (hr2 == 1)
            {
                detail = "此刻没有插入点（光标不在输入框里）";
                return false;
            }

            if (hr2 != 0)
            {
                detail = $"accLocation 失败 hr=0x{hr2:X8}";
                return false;
            }

            if (w <= 0 || h <= 0)
            {
                detail = $"插入点矩形无效 {w}x{h}";
                return false;
            }

            // 坐标必须落在虚拟屏幕内，否则当成脏数据丢掉
            int screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
            int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

            if (x < screenLeft || y < screenTop
                || x > screenLeft + screenW || y > screenTop + screenH)
            {
                detail = $"插入点坐标越界 ({x},{y})";
                return false;
            }

            rect = new NativeMethods.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
            detail = $"插入点({x},{y} {w}x{h})";
            return true;
        }
        catch (Exception ex)
        {
            detail = "查询插入点异常：" + ex.GetType().Name;
            return false;
        }
        finally
        {
            // 取接口成功会让引用计数 +1，必须还回去，否则每按一次 Ctrl 就漏一个引用。
            // ⚠️ 万一将来这里引发崩溃（理论上不该），第一处理是**直接删掉这一行** ——
            //    漏一个引用远好过一个 use-after-free。
            if (acc != IntPtr.Zero)
            {
                try { Marshal.Release(acc); } catch { /* 释放失败也不致命 */ }
            }
        }
    }

    // ===================================================================
    // 内部工具
    // ===================================================================

    /// <summary>发 WM_GETOBJECT 唤醒 Chromium 的无障碍树。</summary>
    private static void WakeUp(IntPtr renderWindow)
    {
        // 超时给 50ms：即使超时返回，消息依然会送达并最终完成唤醒
        //（超时只是"不再等回执"），所以短超时是安全的，也不会把 UI 卡出可感顿挫。
        NativeMethods.SendMessageTimeout(
            renderWindow, NativeMethods.WM_GETOBJECT, IntPtr.Zero, new IntPtr(1),
            NativeMethods.SMTO_ABORTIFHUNG, 50, out _);
    }

    /// <summary>
    /// 在顶层窗口的后代里找 Chromium 的渲染子窗口（类名前缀 Chrome_RenderWidgetHostHWND）。
    /// **只用来发唤醒消息** —— 真正取接口时必须用顶层窗口（实测结论）。
    /// </summary>
    private static IntPtr FindRenderWidget(IntPtr topLevel)
    {
        IntPtr found = IntPtr.Zero;

        NativeMethods.EnumChildWindows(topLevel, (hwnd, _) =>
        {
            var sb = new StringBuilder(64);
            NativeMethods.GetClassName(hwnd, sb, sb.Capacity);

            if (sb.ToString().StartsWith("Chrome_RenderWidgetHostHWND",
                                         StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;      // 找到就停
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 是不是目标程序。
    ///
    /// 归一化之后再比对，而不是死抠字面 —— 这样 "Cherry Studio" / "CherryStudio" /
    /// "cherry-studio" / "Cherry_Studio" 全都能命中；再加一条"名字里同时含
    /// cherry 和 studio"的兜底，一般的改名也漏不掉。
    /// </summary>
    private static bool IsTarget(string processName)
    {
        string n = NormalizeName(processName);
        if (n.Length == 0) return false;

        return n == TargetProcessNormalized
            || (n.Contains("cherry") && n.Contains("studio"));
    }

    private static string NormalizeName(string name)
        => name.Replace(" ", string.Empty)
               .Replace(".", string.Empty)
               .Replace("-", string.Empty)
               .Replace("_", string.Empty)
               .ToLowerInvariant();

    /// <summary>取进程名（自动去掉 .exe）。失败返回 false，绝不抛异常。</summary>
    private static bool TryGetProcessName(IntPtr hwnd, out string processName)
    {
        processName = string.Empty;

        try
        {
            uint pid = GetPid(hwnd);
            if (pid == 0) return false;

            using Process p = Process.GetProcessById((int)pid);
            processName = p.ProcessName;
            return processName.Length > 0;
        }
        catch
        {
            // 进程已退出、或是受保护进程访问受限 —— 一律当作失败
            return false;
        }
    }

    private static uint GetPid(IntPtr hwnd)
    {
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }
}
