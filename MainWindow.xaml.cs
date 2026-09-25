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
    // 主题画刷。故意不做成 static readonly：切换主题时要整体换掉。
    // 依然 Freeze()：只在切主题时重建（低频），而 ApplyState 每 400ms 只读字段、零分配。
    private Brush _chineseBrush = Brushes.Transparent;
    private Brush _englishBrush = Brushes.Transparent;
    private Brush _unknownBrush = Brushes.Transparent;

    private readonly DispatcherTimer _timer;
    private readonly AppSettings _settings = AppSettings.Load();

    private TrayIcon? _tray;
    private AppMenu? _appMenu;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>
    /// 窗口是否已经真正定位过（ApplyStartupPosition 跑过）。
    ///
    /// 专门用来挡住一种情况：诊断模式（--probe）会创建一个窗口做样式自检再立刻关掉，
    /// 那个窗口从没定位过，Left/Top 是系统给的默认值（物理 32,32）。
    /// 若不管它，关闭时就把这个无意义的坐标写进配置了 ——
    /// 用户下次启动会发现方块跑到了屏幕左上角。
    /// </summary>
    private bool _positionReady;

    /// <summary>
    /// 当前显示的是哪种模式。切换主题后要用它把文字颜色重新贴回去 ——
    /// 否则"中"字会停在旧主题的颜色上，直到用户下次切换输入法才更新，
    /// 而他可能半天都不切输入法。这是个很容易漏掉的 bug。
    /// </summary>
    private ImeMode _lastMode = ImeMode.Unknown;

    // 诊断计数：用来区分「刷新循环没跑」和「刷新了但数据本身不对」——这是两个完全不同的 bug
    private int _tickCount;
    private int _skipCount;
    private int _failCount;
    private ImeState? _lastState;

    public MainWindow()
    {
        InitializeComponent();

        // 主题必须在窗口显示之前贴好，否则用户会看到"先深后浅"闪一下
        ApplyTheme(_settings.Theme);

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
                //
                // 注意：这只拦"激活"，鼠标消息照常投递 ——
                // 所以左键拖动和右键弹菜单都不受影响。
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
        _positionReady = true;      // 到这为止位置才算"有意义的"，此前不许保存

        // 一套菜单定义，造出两份实例：悬浮窗一份、托盘一份。
        // 内容天然完全一致，不用人工维护两份。
        _appMenu = new AppMenu(
            isVisible: () => IsVisible,
            isAutoStartEnabled: StartupManager.IsEnabled,
            currentTheme: () => _settings.Theme,
            visibilityToggled: ToggleVisibility,
            autoStartChanged: OnAutoStartChanged,
            themeChanged: OnThemeChanged,
            exitRequested: ExitApplication);

        _tray = new TrayIcon(_appMenu.TrayMenu, ToggleVisibility);

        DiagnosticsLog.Write(
            $"=== ImeTip 启动 PID={Environment.ProcessId} 悬浮窗=0x{_hwnd.ToInt64():X8} " +
            $"托盘图标=已创建 主题={_settings.Theme} 开机自启={StartupManager.IsEnabled()} ===");

        Refresh();
        _timer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        SavePosition();

        _tray?.Dispose();
        _tray = null;

        // 必须放在 _tray 之后：NotifyIcon 还引用着托盘那份菜单
        _appMenu?.Dispose();
        _appMenu = null;
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
        // 没见过光的窗口不配写入位置，理由见 _positionReady 的注释
        if (!_positionReady) return;

        if (double.IsNaN(Left) || double.IsNaN(Top)) return;

        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.Save();
    }

    /// <summary>
    /// 切换"开机自启"。返回值是**实际生效的状态**，供菜单回填勾选。
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

    private void OnThemeChanged(AppTheme theme)
    {
        ApplyTheme(theme);
        _settings.Save();       // 立即落盘，重启后保持
        DiagnosticsLog.Write($"主题切换为 {theme}");
    }

    /// <summary>
    /// 把整窗颜色换成指定主题。只在启动时和用户手动切主题时调用（低频）。
    /// </summary>
    private void ApplyTheme(AppTheme theme)
    {
        ThemePalette palette = ThemePalette.For(theme);
        _settings.Theme = theme;

        // 重建三个文字画刷。低频操作，重建最省心，也省得引入资源字典这层间接。
        // 依然 Freeze()：WPF 渲染时不必反复做安全检查。
        _chineseBrush = CreateFrozenBrush(palette.Chinese);
        _englishBrush = CreateFrozenBrush(palette.English);
        _unknownBrush = CreateFrozenBrush(palette.Unknown);

        Root.Background = CreateFrozenBrush(palette.CardBackground);
        Root.BorderBrush = CreateFrozenBrush(palette.CardBorder);

        // ⚠️ 关键：把当前显示的文字颜色也重新贴一遍。
        //    不补这行，切主题后"中/英"字会停在旧主题的颜色上，
        //    要等到用户下次切换输入法才会更新 —— 而他可能半天都不切。
        StateText.Foreground = BrushFor(_lastMode);
    }

    /// <summary>某个模式在当前主题下该用哪个颜色。ApplyState 与 ApplyTheme 共用。</summary>
    private Brush BrushFor(ImeMode mode) => mode switch
    {
        ImeMode.Chinese => _chineseBrush,
        ImeMode.English => _englishBrush,
        _ => _unknownBrush,
    };

    private void ToggleVisibility()
    {
        if (IsVisible) HideFromTray();
        else ShowFromTray();
    }

    /// <summary>显示悬浮窗。供菜单、托盘图标单击、以及"第二个实例启动"时调用。</summary>
    public void ShowFromTray()
    {
        if (IsVisible) return;

        Show();
        Refresh();
        _timer.Start();
    }

    /// <summary>收进托盘。注意这是"隐藏"而不是"退出"——退出只在菜单里提供。</summary>
    private void HideFromTray()
    {
        if (!IsVisible) return;

        SavePosition();     // 隐藏前先把位置存下来
        Hide();
        _timer.Stop();      // 看不见的时候不必刷新，省资源
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
                _lastMode = ImeMode.Chinese;
                StateText.Foreground = BrushFor(_lastMode);
                break;

            case ImeMode.English:
                StateText.Text = "英";
                _lastMode = ImeMode.English;
                StateText.Foreground = BrushFor(_lastMode);
                break;

            case ImeMode.Unreliable:
                // 两类情况都走这里：
                //   ① 系统界面（任务栏/托盘弹窗）—— 不是打字目标，不需要更新
                //   ② 该窗口不通过 IMM32 暴露状态 —— 读不到就别瞎报
                // 共同做法：保持上一次显示。宁可显示旧信息，也不给确定但错误的答案。
                // （_lastMode 也保持不变，这样切主题时贴的还是原来那个颜色）
                Root.ToolTip = tip + Environment.NewLine +
                               $"⚠ {s.RuleUsed} → 保持上一次显示";
                return;

            default:
                StateText.Text = "?";
                _lastMode = ImeMode.Unknown;
                StateText.Foreground = BrushFor(_lastMode);
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

    /// <summary>
    /// 右键 = 弹出菜单（不再直接隐藏；"隐藏"就在菜单里）。
    ///
    /// ⚠️ 必须用 ButtonUp，不能用 ButtonDown：
    ///    菜单弹出时会抓取鼠标，紧接着的右键"抬起"会被投递给刚弹出的菜单，
    ///    于是菜单刚出现就被关掉 —— 典型的"一闪即没"。
    ///    这也是 Windows 本来的约定（右键抬起才发 WM_CONTEXTMENU）。
    /// </summary>
    private void OnMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _appMenu?.ShowFromBlock(_hwnd);
    }

    /// <summary>
    /// 创建并"冻结"画刷。冻结后 WPF 不必每次渲染都做安全检查，也更省内存。
    /// 参数用 Color 而不是三个 byte：卡片底色/边框带 Alpha，不能被丢掉。
    /// </summary>
    private static Brush CreateFrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
