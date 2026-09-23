using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ImeTip;

public partial class MainWindow : Window
{
    private static readonly Brush ChineseBrush = CreateFrozenBrush(0x4E, 0xC9, 0xB0);
    private static readonly Brush EnglishBrush = CreateFrozenBrush(0xE0, 0xA4, 0x58);
    private static readonly Brush UnknownBrush = CreateFrozenBrush(0x88, 0x88, 0x88);

    private readonly DispatcherTimer _timer;
    private readonly AppSettings _settings = AppSettings.Load();

    private TrayIcon? _tray;
    private IntPtr _hwnd = IntPtr.Zero;

    // 诊断计数：用来区分「刷新循环没跑」和「刷新了但数据本身不对」——这是两个完全不同的 bug
    private int _tickCount;
    private int _skipCount;
    private int _failCount;
    private ImeState? _lastState;

    public MainWindow()
    {
        InitializeComponent();

        // 用 Normal 优先级。Background 优先级在调度器繁忙时可能被一直推迟，
        // 对于这种"状态一变就得跟上"的场景不合适。
        //
        // 间隔取 400ms 而不是更短：每次刷新都要跨进程给输入法发消息，
        // 频率太高会给第三方输入法（如微信输入法）造成不必要的压力。
        // 400ms 对"状态指示"这种人眼感知已经足够，实际开销可忽略不计。
        _timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += (_, _) => Refresh();

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>
    /// 窗口句柄一生成就立刻改扩展样式，必须赶在窗口显示之前。
    /// </summary>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // WS_EX_NOACTIVATE —— 被点击时不要夺取焦点。
        //   这一条是整个程序的命门：没有它，一拖动本窗口，
        //   自己就变成了"前台窗口"，于是读到的输入法状态永远是自己的，
        //   而且还会把用户正在打字的窗口焦点打断。
        // WS_EX_TOOLWINDOW —— 不出现在 Alt+Tab 列表里。
        int exStyle = NativeMethods.GetWindowLong(_hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            _hwnd,
            NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW);

        // 挂上我们自己的消息处理，拦截几个关键消息（见 WndProc）
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    /// <summary>
    /// 本窗口的原生消息钩子。
    /// 只要加了 WS_EX_NOACTIVATE，WM_MOVING 就必须处理，否则拖拽会出怪毛病。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case (int)NativeMethods.WM_MOUSEACTIVATE:
                // 点击本窗口时明确回答"别激活我"。
                // 这是 WS_EX_NOACTIVATE 的加固保险：万一有别的因素想抢焦点，在这里堵死。
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);

            case (int)NativeMethods.WM_MOVING:
                // 无边框窗口 + NOACTIVATE 时，拖动过程中 Windows 报告的只是
                // "尚未生效"的新位置（为了让你有机会限制拖动范围）。
                // 如果不在这里主动挪窗口，就会表现为：
                //   —— 鼠标在动，窗口纹丝不动，直到松手才"啪"地跳过去。
                var rect = Marshal.PtrToStructure<NativeMethods.RECT>(lParam);
                NativeMethods.SetWindowPos(
                    hwnd, IntPtr.Zero, rect.Left, rect.Top, 0, 0,
                    NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                break;

            case (int)NativeMethods.WM_EXITSIZEMOVE:
                // 拖动结束，把新位置记下来（而不是等退出时才记，
                // 这样即使程序被强制结束，位置也不会丢）
                SavePosition();
                break;
        }

        return IntPtr.Zero;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyStartupPosition();

        _tray = new TrayIcon(
            autoStartEnabled: StartupManager.IsEnabled(),
            autoStartChanged: OnAutoStartChanged,
            visibilityToggled: ToggleVisibility,
            exitRequested: ExitApplication);

        DiagnosticsLog.Write(
            $"=== ImeTip 启动 PID={Environment.ProcessId} 悬浮窗=0x{_hwnd.ToInt64():X8} " +
            $"托盘图标=已创建 开机自启={StartupManager.IsEnabled()} ===");

        Refresh();
        _timer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        SavePosition();
        _tray?.Dispose();
        _tray = null;
    }

    /// <summary>恢复上次的位置；没有记录或记录已失效时，贴到工作区右下角。</summary>
    private void ApplyStartupPosition()
    {
        if (_settings.WindowLeft is double left
            && _settings.WindowTop is double top
            && IsPositionUsable(left, top))
        {
            Left = left;
            Top = top;
            DiagnosticsLog.Write($"窗口位置 = ({left:F0}, {top:F0})  来源 = 上次记忆");
            return;
        }

        Rect workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 24;
        Top = workArea.Bottom - Height - 24;

        string reason = _settings.WindowLeft is null ? "首次运行" : "记忆位置已失效(可能是显示器变了)";
        DiagnosticsLog.Write($"窗口位置 = ({Left:F0}, {Top:F0})  来源 = 默认右下角（{reason}）");
    }

    /// <summary>
    /// 判断记录下来的位置是否还在可见范围内。
    ///
    /// 这一步必须做：上次可能是在外接显示器上用的，这次显示器拔了，
    /// 直接套用旧坐标会把窗口放到屏幕外面 —— 用户会以为"程序坏了/打不开"。
    /// </summary>
    private bool IsPositionUsable(double left, double top)
    {
        double virtualLeft = SystemParameters.VirtualScreenLeft;
        double virtualTop = SystemParameters.VirtualScreenTop;
        double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

        const double margin = 8;
        return left >= virtualLeft - margin
            && top >= virtualTop - margin
            && left + Width <= virtualRight + margin
            && top + Height <= virtualBottom + margin;
    }

    private void SavePosition()
    {
        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    /// <summary>
    /// 切换"开机自启"。返回值是**实际生效的状态**，供托盘菜单回填勾选。
    /// 用真实状态回填而不是"我请求的状态"，注册表写失败时界面就不会假装成功。
    /// </summary>
    private static bool OnAutoStartChanged(bool requested)
    {
        StartupManager.Set(requested);

        // 写完再读回来核对，而不是假设写成功了
        bool actual = StartupManager.IsEnabled();
        DiagnosticsLog.Write($"开机自启 请求={requested} 实际={actual}");
        return actual;
    }

    private void ToggleVisibility()
    {
        if (IsVisible) HideFromTray();
        else ShowFromTray();
    }

    /// <summary>显示悬浮窗。供托盘菜单、托盘图标单击、以及"第二个实例启动"时调用。</summary>
    public void ShowFromTray()
    {
        if (IsVisible) return;

        Show();
        Refresh();
        _timer.Start();
        _tray?.SetVisibleState(true);
    }

    /// <summary>收进托盘。注意这是"隐藏"而不是"退出"——退出只在托盘菜单里提供。</summary>
    private void HideFromTray()
    {
        if (!IsVisible) return;

        SavePosition();     // 隐藏前先把位置存下来
        Hide();
        _timer.Stop();      // 看不见的时候不必刷新，省资源
        _tray?.SetVisibleState(false);
    }

    private void ExitApplication()
    {
        SavePosition();
        Application.Current.Shutdown();
    }

    private void Refresh()
    {
        _tickCount++;

        ImeState? state = ImeStateReader.Read(_hwnd);

        if (state is null)
        {
            // 前台窗口是自己（或没有前台窗口）时保持原样，不要瞎刷新
            _skipCount++;
            return;
        }

        ImeState s = state.Value;
        if (s.Mode == ImeMode.Unknown) _failCount++;

        // 只在结果变化时记日志，避免刷爆文件
        if (_lastState is null || !s.Equals(_lastState.Value))
        {
            DiagnosticsLog.Write($"tick={_tickCount} {s.Describe()}");
            _lastState = s;
        }

        ApplyState(s);
    }

    private void ApplyState(ImeState s)
    {
        _tray?.SetTooltip($"ImeTip：{DescribeMode(s.Mode)}");

        string tip = s.Describe() + Environment.NewLine +
                     $"刷新={_tickCount}  跳过={_skipCount}  读失败={_failCount}";

        switch (s.Mode)
        {
            case ImeMode.Chinese:
                StateText.Text = "中";
                StateText.Foreground = ChineseBrush;
                break;

            case ImeMode.English:
                StateText.Text = "英";
                StateText.Foreground = EnglishBrush;
                break;

            case ImeMode.Unreliable:
                // 两类情况都走这里：
                //   ① 系统界面（任务栏/托盘弹窗）—— 不是打字目标，不需要更新
                //   ② 该窗口不通过 IMM32 暴露状态 —— 读不到就别瞎报
                // 共同做法：保持上一次显示。宁可显示旧信息，也不给确定但错误的答案。
                Root.ToolTip = tip + Environment.NewLine +
                               $"⚠ {s.RuleUsed} → 保持上一次显示";
                return;

            default:
                StateText.Text = "?";
                StateText.Foreground = UnknownBrush;
                break;
        }

        // 鼠标悬停时显示原始数据 + 计数，出问题不用靠猜
        Root.ToolTip = tip;
    }

    private static string DescribeMode(ImeMode mode) => mode switch
    {
        ImeMode.Chinese => "中文",
        ImeMode.English => "英文",
        ImeMode.Unreliable => "此窗口读不到状态",
        _ => "未知",
    };

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标键在极短时间内被松开时 WPF 会抛异常，忽略即可
        }
    }

    private void OnMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 右键 = 收进托盘（不是退出）。退出请走托盘菜单。
        ToggleVisibility();
    }

    /// <summary>
    /// 创建并"冻结"画刷。冻结后 WPF 不必每次渲染都做安全检查，也更省内存。
    /// </summary>
    private static Brush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
