using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
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
    private SettingsWindow? _settingsWindow;

    /// <summary>
    /// 最近一次算出来的悬停提示内容。**每次都算，但只有在设置里打开开关时才真的贴上去** ——
    /// 这样打开开关的瞬间就有内容，不用干等下一次刷新。
    /// </summary>
    private string? _lastTip;

    /// <summary>全局快捷键（目前只有"单独按一下 Ctrl"这一条）。</summary>
    private HotkeyManager? _hotkeys;

    /// <summary>快捷召唤的触发次数。只用来在日志里记前几次，方便确认功能确实生效，
    /// 之后就不记了 —— 不然每次打字前按一下 Ctrl 都会刷一行日志。</summary>
    private int _summonCount;
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>玻璃框是否扩展成功。透明主题要靠它才真正透明，所以值得记进日志。</summary>
    private bool _glassFrameOk;

    /// <summary>
    /// 当前是否已经给窗口贴过模糊（accent）。
    ///
    /// ⚠️ 专门用来决定"要不要清掉它"。实测发现：**只要调用过
    /// SetWindowCompositionAttribute，窗口就会变成不透明** ——
    /// 所以"明明没开模糊、却主动去调一次 ClearAccent"会把好好的透明窗口搞成色块。
    /// 只有确实贴过模糊时才需要清。
    /// </summary>
    private bool _accentApplied;

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

        // 挂上我们自己的消息处理，拦截几个关键消息（见 WndProc）。
        //
        // ⚠️ 顺带把窗口表面清成透明 —— 这是 AllowsTransparency 的替代方案。
        //    为什么不再用 AllowsTransparency：它会让窗口变成"分层窗口"，
        //    而分层窗口会挡住 DWM 的合成效果，模糊/亚克力就永远贴不上去。
        //    CompositionTarget.BackgroundColor 同样能实现透明背景，且不与合成打架。
        if (HwndSource.FromHwnd(_hwnd) is { } source)
        {
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
            source.AddHook(WndProc);
        }

        // ⚠️ 必须紧跟着把玻璃框扩展到整窗。
        //    只设上面那行是不够的：那只是让 WPF 的内容带 Alpha 画出来，
        //    而客户区在 DWM 眼里仍是不透明的，最终会渲染成一个色块
        //    （用户看到的就是"卡片背景根本不是透明的"）。两步缺一不可。
        _glassFrameOk = WindowEffects.TryExtendGlassFrame(_hwnd);

        // 圆角主要靠 XAML 里 Border.CornerRadius 自己画（窗口四角本身就是透明的）。
        // 这里只是顺手问一句系统，失败完全没关系。
        WindowEffects.TrySetRoundedCorners(_hwnd);

        // 句柄出来了，必须重新应用一次外观 —— 模糊（accent）没有句柄是贴不上的。
        // 构造函数里那次调用只能设好颜色。
        ApplyAppearance();
    }

    /// <summary>
    /// 本窗口的原生消息钩子。
    ///
    /// ⚠️ 这里**故意不再处理 `WM_MOVING`** —— 以前窗口是分层窗口
    /// （`AllowsTransparency=True`）时必须手工 `SetWindowPos` 跟随，
    /// 否则拖动会"松手才跳"。但去掉分层窗口之后，Windows 的模态拖动循环
    /// 自己就能正常移动窗口，我们再插手反而会和它打架：
    /// 拖动会变成"鼠标带着窗口越滑越快、松手还在飞"的失控状态。
    /// （这是实测踩出来的：用户拖动时鼠标从屏幕左边一路滑到右边。）
    ///
    /// 结论：**非分层窗口不要手工处理 WM_MOVING。**
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

            case (int)NativeMethods.WM_NCHITTEST:
                // 把整窗都声明成"客户端区域"。
                //
                // 为什么需要：调了 DwmExtendFrameIntoClientArea 之后，被扩展成"玻璃"的区域
                // 默认会被当成非客户区（标题栏之类），鼠标消息就不是发到客户区了 ——
                // 结果是左键拖动和右键菜单可能失灵。
                // 明确回答 HTCLIENT 就能保证点击照常进到 WPF。
                handled = true;
                return new IntPtr(NativeMethods.HTCLIENT);

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
            settingsRequested: OpenSettings,
            exitRequested: ExitApplication);

        _tray = new TrayIcon(_appMenu.TrayMenu, ToggleVisibility);

        // 全局快捷键。钩子是否真的装上由设置决定（见 SyncHotkeyState）。
        _hotkeys = new HotkeyManager(SummonToCursor);
        SyncHotkeyState();

        DiagnosticsLog.Write(
            $"=== ImeTip 启动 PID={Environment.ProcessId} 悬浮窗=0x{_hwnd.ToInt64():X8} 托盘图标=已创建 " +
            $"主题={_settings.Theme} 不透明度={_settings.CardOpacityPercent}% " +
            $"边框={(_settings.ShowCardBorder ? "显示" : "隐藏")} 描边={(_settings.TextOutline ? "开" : "关")} " +
            $"模糊={_settings.Blur} 玻璃框={(_glassFrameOk ? "成功" : "失败")} " +
            $"快捷键Ctrl召唤={(_settings.HotkeySummonEnabled ? "开" : "关")} " +
            $"开机自启={StartupManager.IsEnabled()} ===");

        Refresh();
        _timer.Start();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _timer.Stop();
        SavePosition();

        // 卸掉键盘钩子。不卸的话，虽然进程退出时系统也会清理，
        // 但显式卸载更干净，也避免调试时留下一个"还在监听"的残留。
        _hotkeys?.Dispose();
        _hotkeys = null;

        // 先把设置窗口关掉：它是普通窗口，留着会让进程迟迟不退出
        _settingsWindow?.Close();
        _settingsWindow = null;

        _tray?.Dispose();
        _tray = null;

        // 必须放在 _tray 之后：NotifyIcon 还引用着托盘那份菜单
        _appMenu?.Dispose();
        _appMenu = null;
    }

    /// <summary>
    /// 【诊断用】等界面稳定后，把悬浮窗渲染到内存位图，把颜色分布写进日志，然后退出。
    /// 用途：验证"某个主题下到底渲染出了什么颜色"，不依赖抓屏也不依赖肉眼。
    /// </summary>
    public void SnapshotAndExit()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReportRenderedColors();
            Application.Current.Shutdown();
        };
        timer.Start();
    }

    private void ReportRenderedColors()
    {
        var size = new Size(Width, Height);
        Root.Measure(size);
        Root.Arrange(new Rect(size));
        Root.UpdateLayout();

        var bitmap = new RenderTargetBitmap(
            (int)Width, (int)Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Root);

        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            // 内存里是 BGRA，拼成 0xRRGGBB 便于看
            uint rgb = (uint)((pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i]);
            counts[rgb] = counts.TryGetValue(rgb, out int n) ? n + 1 : 1;
        }

        string top = string.Join("  ", counts.OrderByDescending(kv => kv.Value).Take(12)
            .Select(kv => $"#{kv.Key >> 16 & 0xFF:X2}{kv.Key >> 8 & 0xFF:X2}{kv.Key & 0xFF:X2}×{kv.Value}"));

        string textColor = StateText.Foreground is SolidColorBrush sb
            ? $"#{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}"
            : StateText.Foreground?.ToString() ?? "(null)";

        DiagnosticsLog.Write(
            $"渲染快照 主题={_settings.Theme} 显示={StateText.Text} 文字画刷={textColor} | 像素分布: {top}");
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
    /// 把整窗换成指定主题。只在启动时、切主题时、以及设置窗口改动时调用（低频）。
    /// </summary>
    private void ApplyTheme(AppTheme theme)
    {
        _settings.Theme = theme;
        ApplyAppearance();
    }

    /// <summary>
    /// 按当前设置把整个外观贴上去：文字色、卡片底色（含用户调的透明度）、
    /// 边框开关、文字描边、以及尽量尝试的背景模糊。
    ///
    /// ⚠️ 有两个调用时机很关键：
    ///   ① 构造时（此时还没句柄）—— 只能设好颜色；
    ///   ② OnSourceInitialized 之后 —— 这时句柄有了，模糊才贴得上。
    /// </summary>
    private void ApplyAppearance()
    {
        ThemePalette palette = ThemePalette.For(_settings.Theme);

        // 重建三个文字画刷。低频操作，重建最省心，也省得引入资源字典这层间接。
        // 依然 Freeze()：WPF 渲染时不必反复做安全检查。
        _chineseBrush = CreateFrozenBrush(palette.Chinese);
        _englishBrush = CreateFrozenBrush(palette.English);
        _unknownBrush = CreateFrozenBrush(palette.Unknown);

        bool transparentTheme = _settings.Theme == AppTheme.Transparent;
        byte alpha = AlphaFromPercent(_settings.CardOpacityPercent);

        // 模糊只在透明主题下有意义；失败就返回 false，自动退回纯透明。
        bool blurOn = transparentTheme
                   && _settings.Blur != BlurMode.None
                   && WindowEffects.TrySetAccent(_hwnd, _settings.Blur, palette.CardBackground, alpha);

        if (blurOn)
        {
            _accentApplied = true;

            // 底色交给合成层去画，这里必须完全透明，否则会"模糊之上再叠一层色"被压暗两次
            Root.Background = Brushes.Transparent;
        }
        else
        {
            // ⚠️ 只有**确实贴过模糊**才去清它，没贴过就绝对不要碰。
            //    实测：只要调过 SetWindowCompositionAttribute，窗口就会变成不透明色块 ——
            //    "没开模糊却主动调一次 ClearAccent"会把好好的透明窗口毁掉。
            if (_accentApplied && _hwnd != IntPtr.Zero)
            {
                WindowEffects.ClearAccent(_hwnd);
                _accentApplied = false;
            }

            byte a = transparentTheme ? alpha : palette.CardBackground.A;
            Root.Background = CreateFrozenBrush(Color.FromArgb(
                a, palette.CardBackground.R, palette.CardBackground.G, palette.CardBackground.B));
        }

        // 边框只换刷子、不动 BorderThickness —— 改厚度会让文字位置跟着抖一下
        Root.BorderBrush = _settings.ShowCardBorder
            ? CreateFrozenBrush(palette.CardBorder)
            : Brushes.Transparent;

        ApplyTextOutline(palette);

        // ⚠️ 关键：把当前显示的文字颜色也重新贴一遍。
        //    不补这行，切主题后"中/英"字会停在旧主题的颜色上，
        //    要等到用户下次切换输入法才会更新 —— 而他可能半天都不切。
        StateText.Foreground = BrushFor(_lastMode);

        _settingsWindow?.ApplyPalette(_settings.Theme);
    }

    /// <summary>
    /// 给「中/英」字加描边。背景透明后壁纸任意，没有描边字就可能糊成一片。
    ///
    /// 用 DropShadowEffect（ShadowDepth=0 就成了均匀描边）而不是叠 5 个 TextBlock：
    /// 单字、变化极低频，开销可忽略；而且能自动跟随 Foreground / 主题换色，零布局风险。
    /// </summary>
    private void ApplyTextOutline(ThemePalette palette)
    {
        if (!_settings.TextOutline || palette.TextOutline.A == 0)
        {
            StateText.Effect = null;
            return;
        }

        var outline = new DropShadowEffect
        {
            Color = palette.TextOutline,
            ShadowDepth = 0,        // 0 = 四周均匀，不是投影
            BlurRadius = 3,
            Opacity = 1.0,
        };
        outline.Freeze();
        StateText.Effect = outline;
    }

    private static byte AlphaFromPercent(int percent)
        => (byte)Math.Clamp((int)Math.Round(percent / 100.0 * 255.0), 0, 255);

    /// <summary>
    /// 按设置里的开关启用/停用键盘钩子。
    ///
    /// 停用时是**真的卸载钩子**，而不是"留着钩子再加个 if 判断"——
    /// 不监听就是彻底不监听，比"监听了但不用"干净。
    /// </summary>
    private void SyncHotkeyState()
    {
        if (_hotkeys is null) return;

        if (_settings.HotkeySummonEnabled) _hotkeys.TryStart();
        else _hotkeys.Stop();
    }

    /// <summary>设置窗口里改了快捷键开关 → 立刻启用/停用并落盘。</summary>
    private void OnHotkeyChanged()
    {
        SyncHotkeyState();
        _settings.Save();
    }

    /// <summary>
    /// 把悬浮窗召到鼠标光标正上方（水平居中对齐），像"叫过来"一样。
    ///
    /// 想解决的麻烦：平时方框放在屏幕边缘不碍事，可真要打字时它又太远，
    /// 得用鼠标把它"拽"到输入框旁边 —— 这一下既打断思路又要瞄准。
    /// 现在鼠标停在输入框上、按一下 Ctrl，方框就自己过来了。
    ///
    /// 全程用**物理像素**坐标：GetCursorPos / GetWindowRect / SetWindowPos 都是物理像素，
    /// 一律不掺 WPF 的逻辑坐标 —— 少一次 DPI 换算就少一个出错的地方。
    /// 窗口尺寸也从 GetWindowRect 现取，而不是用 Width/Height 属性
    /// （那是逻辑值，在 125% / 150% 缩放下跟物理像素对不上）。
    ///
    /// ⚠️ 刻意**不调 SavePosition()**：用户希望"记住的位置"仍然是他手动拖到的那个，
    ///    按 Ctrl 只是临时召唤一下。所以移动完就完事，不写配置文件。
    /// </summary>
    private void SummonToCursor()
    {
        if (_hwnd == IntPtr.Zero) return;
        if (!IsVisible) return;              // 已经收进托盘了就别乱动

        if (!NativeMethods.GetCursorPos(out NativeMethods.POINT cursor)) return;
        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT box)) return;

        int w = box.Right - box.Left;
        int h = box.Bottom - box.Top;

        // 鼠标指针自己就有高度（约 20px），这里再留一点空隙，
        // 免得方框压住指针，或者压住正在输入的那一行字。
        const int gap = 28;

        int left = cursor.X - w / 2;         // 水平居中于鼠标
        int top = cursor.Y - h - gap;        // 落在鼠标正上方

        // ── 屏幕边界处理 ──
        // 用"虚拟屏幕"（所有显示器拼起来的并集）而不是主屏，多显示器时左上角可能是负数。
        int screenLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
        int screenTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);

        // 鼠标贴着屏幕顶部时，上方根本放不下 → 改放到鼠标下方
        if (top < screenTop) top = cursor.Y + gap;

        // 水平方向也夹进屏幕内，免得跑出可视范围
        left = Math.Clamp(left, screenLeft, Math.Max(screenLeft, screenLeft + screenW - w));
        top = Math.Clamp(top, screenTop, Math.Max(screenTop, screenTop + screenH - h));

        NativeMethods.SetWindowPos(
            _hwnd, IntPtr.Zero, left, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        // 只记前几次，便于确认功能确实生效；之后不记，免得刷日志
        if (_summonCount < 3)
        {
            _summonCount++;
            DiagnosticsLog.Write(
                $"Ctrl 快捷召唤 #{_summonCount} → 鼠标({cursor.X},{cursor.Y})，方框移到({left},{top})");
        }
    }

    /// <summary>设置窗口里改了任何东西 → 立刻重贴外观并落盘，实现"拖动即预览"。</summary>
    private void OnAppearancePreview()
    {
        ApplyAppearance();
        UpdateTooltip();        // 「诊断」里的悬停开关可能刚被改过，一起刷新
        _settings.Save();
    }

    /// <summary>打开设置窗口。已经开着就把它提到前面，不重复开。</summary>
    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _settings,
            themeChanged: OnThemeChanged,
            preview: OnAppearancePreview,
            hotkeyChanged: OnHotkeyChanged,
            blurSupported: WindowEffects.BlurAvailable);

        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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

        // 托盘图标跟着状态走（中文=青绿「中」、英文=琥珀「E」、读不到=灰「?」）。
        // Unreliable 时它内部会保持不动，和下面 switch 里"保持上一次显示"的策略一致。
        _tray?.SetMode(s.Mode);

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
                _lastTip = tip + Environment.NewLine +
                           $"⚠ {s.RuleUsed} → 保持上一次显示";
                UpdateTooltip();
                return;

            default:
                StateText.Text = "?";
                _lastMode = ImeMode.Unknown;
                StateText.Foreground = BrushFor(_lastMode);
                break;
        }

        _lastTip = tip;
        UpdateTooltip();
    }

    /// <summary>
    /// 把悬停提示贴到窗口上 —— 开关关着时则清掉它。
    ///
    /// 这个提示是**排查问题**用的。默认关闭：正常使用时鼠标扫过方框就弹出一大段
    /// 原始数据反而碍事。开关在设置窗口的「诊断」那一节里。
    /// </summary>
    private void UpdateTooltip()
    {
        Root.ToolTip = _settings.ShowDebugTooltip ? _lastTip : null;
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
