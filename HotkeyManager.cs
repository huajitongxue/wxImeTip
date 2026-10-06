using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace ImeTip;

/// <summary>
/// 全局快捷键。
///
/// 实现方式是 Windows 的**低级键盘钩子**（WH_KEYBOARD_LL）。
///
/// 为什么不用 RegisterHotKey：
///   那个 API 只能注册**组合键**（如 Ctrl+Alt+Q），没法单独注册一个 Ctrl；
///   而且注册成功后这个按键就被本程序"独占"了 —— Ctrl+C 复制会直接失效。
///   低级钩子只是在旁边"旁听"，按键照常传给原来的程序（我们永远调 CallNextHookEx）。
///
/// ⚠️ 三条硬规矩，违反任何一条都会让整个系统的键盘变卡、或者程序直接崩：
///   ① 回调里**只做判断**，绝不做耗时的事。系统对低级钩子有超时限制（约 300ms），
///      超时就把钩子摘掉了。要干活就丢给 Dispatcher 异步去做。
///   ② 回调委托必须用**静态字段**存住。如果只传个局部变量进去，GC 一回收，
///      系统回调到一个已释放的地址 → 整个进程崩溃（这种崩溃还特别难查）。
///   ③ 回调整体包 try/catch。托管异常穿透到系统的钩子链里是未定义行为。
///
/// ⚠️ 已知限制：以管理员权限运行的窗口，它的按键我们收不到（UIPI 权限隔离）。
///    这跟"读不到管理员窗口的输入法状态"是同一个原因。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private readonly Action _onSummon;
    private readonly Dispatcher _dispatcher;

    private IntPtr _hook = IntPtr.Zero;

    // 规矩②：委托和实例都用静态字段存住，防止被 GC 回收
    private static NativeMethods.LowLevelKeyboardProc? _proc;
    private static HotkeyManager? _current;

    // ── "单独按一下 Ctrl" 的状态机 ──
    //   按下 Ctrl       → 打标记
    //   中途按了别的键  → 标记作废（说明这是 Ctrl+C 这类组合键）
    //   Ctrl 松开       → 标记还在，才算"单独按了一下"
    //
    //   这个做法不需要任何计时器，而且**零延迟**：松开的那一刻立刻就知道结果。
    private bool _ctrlDown;
    private bool _usedWithOtherKey;

    /// <param name="onSummon">"单独按一下 Ctrl"被触发时调用（在 UI 线程上执行）。</param>
    internal HotkeyManager(Action onSummon)
    {
        _onSummon = onSummon;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _current = this;
        _proc = HookProc;      // 存到静态字段里，别让它被回收
    }

    /// <summary>钩子当前是否装着。</summary>
    internal bool IsRunning => _hook != IntPtr.Zero;

    /// <summary>
    /// 装上钩子。失败返回 false，绝不抛异常 —— 快捷键用不了，程序也该照常能用。
    /// ⚠️ 必须在**有消息泵的线程**（也就是 UI 线程）上调用，否则回调永远不会被触发。
    /// </summary>
    internal bool TryStart()
    {
        if (_hook != IntPtr.Zero) return true;

        try
        {
            IntPtr module = NativeMethods.GetModuleHandle(null);

            _hook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_KEYBOARD_LL, _proc!, module, 0);

            if (_hook == IntPtr.Zero)
            {
                DiagnosticsLog.Write(
                    $"⚠ 键盘钩子安装失败（Win32 错误 {Marshal.GetLastWin32Error()}），Ctrl 快捷召唤不可用");
                return false;
            }

            DiagnosticsLog.Write("键盘钩子 = 已安装（Ctrl 快捷召唤已就绪）");
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticsLog.Write("⚠ 键盘钩子安装异常：" + ex.Message);
            _hook = IntPtr.Zero;
            return false;
        }
    }

    /// <summary>卸掉钩子。停用后我们**一个按键都不再监听**（比留着钩子加个开关更干净）。</summary>
    internal void Stop()
    {
        if (_hook != IntPtr.Zero)
        {
            try
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
            }
            catch
            {
                // 卸不掉也只能算了，进程退出时系统会一并清理
            }

            _hook = IntPtr.Zero;
            DiagnosticsLog.Write("键盘钩子 = 已卸载");
        }

        // 顺手清掉状态，免得下次启用时残留上一次的标记
        _ctrlDown = false;
        _usedWithOtherKey = false;
    }

    public void Dispose()
    {
        Stop();
        if (ReferenceEquals(_current, this)) _current = null;
    }

    // =======================================================================
    // 钩子回调
    // =======================================================================

    /// <summary>
    /// 系统回调。⚠️ 这里必须**立刻返回**，任何耗时操作都会拖慢整个系统的键盘。
    /// </summary>
    private static IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            // nCode < 0 时必须原样往下传，不能碰（官方明确规定）
            if (nCode >= 0) _current?.Inspect(wParam, lParam);
        }
        catch
        {
            // 规矩③：绝不让异常穿透到系统钩子链里
        }

        // 规矩：我们只旁听，按键该去哪个程序还去哪个程序
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    /// <summary>
    /// 状态机本体。只做几个整数比较，耗时以纳秒计 —— 符合"回调必须极快"的要求。
    /// </summary>
    private void Inspect(IntPtr wParam, IntPtr lParam)
    {
        int msg = (int)wParam;
        bool isDown = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
        bool isUp = msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP;
        if (!isDown && !isUp) return;

        var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);

        // 程序模拟出来的按键不算（避免其它软件的自动操作把我们带着一起动）
        if ((data.flags & NativeMethods.LLKHF_INJECTED) != 0) return;

        uint vk = data.vkCode;
        bool isCtrl = vk is NativeMethods.VK_CONTROL
                          or NativeMethods.VK_LCONTROL
                          or NativeMethods.VK_RCONTROL;

        if (isCtrl)
        {
            if (isDown)
            {
                // 按住 Ctrl 不放系统会连发 KeyDown，别把标记重置掉 ——
                // 否则"按住一会儿再松开"就不算数了。
                if (!_ctrlDown)
                {
                    _ctrlDown = true;
                    _usedWithOtherKey = false;
                }
            }
            else if (_ctrlDown)
            {
                bool isSingleTap = !_usedWithOtherKey;
                _ctrlDown = false;
                _usedWithOtherKey = false;

                if (isSingleTap) Summon();
            }

            return;
        }

        // 按了任何别的键 → 这次就不是"单独按 Ctrl"了
        if (isDown && _ctrlDown) _usedWithOtherKey = true;
    }

    /// <summary>
    /// 触发。**只负责派发**，真正的工作（移动窗口）交给消息队列稍后执行，
    /// 这样钩子回调能立刻返回。
    /// </summary>
    private void Summon()
    {
        _dispatcher.BeginInvoke(_onSummon, DispatcherPriority.Normal);
    }
}
