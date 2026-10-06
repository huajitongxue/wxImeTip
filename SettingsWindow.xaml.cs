using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImeTip;

/// <summary>
/// 设置窗口。项目里的第二个窗口，专门用来调「透明主题」的外观。
///
/// 设计要点：
///  · **直接操作 MainWindow 那一份 <see cref="AppSettings"/> 实例**，不建第二份状态。
///    这样就不会出现"设置窗口显示的值和实际生效的值不一致"这类问题。
///  · 任何改动都立刻调 preview 回调 → 悬浮窗实时变化。**不必点确定**。
///  · 用系统标题栏，可正常获得焦点（和悬浮窗那种 NOACTIVATE 特例完全不同）。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action _preview;
    private readonly Action<AppTheme> _themeChanged;
    private readonly Action _hotkeyChanged;
    private readonly bool _blurSupported;

    /// <summary>
    /// 初始化控件期间置为 true，用来**抑制事件回调**。
    /// 不加这个的话，构造时给 Slider 赋值会立刻触发 ValueChanged → 回调 → 又去改设置，
    /// 白白做一轮无用功，还可能在对象没构造完时引发空引用。
    /// </summary>
    private bool _loading = true;

    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>
    /// 句柄出来后重新贴一次配色 —— 标题栏的深色只能通过 DWM 属性设置，必须有句柄。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        ApplyPalette(_settings.Theme);
    }

    internal SettingsWindow(
        AppSettings settings,
        Action<AppTheme> themeChanged,
        Action preview,
        Action hotkeyChanged,
        bool blurSupported)
    {
        InitializeComponent();

        _settings = settings;
        _themeChanged = themeChanged;
        _preview = preview;
        _hotkeyChanged = hotkeyChanged;
        _blurSupported = blurSupported;

        BlurHint.Text = blurSupported
            ? "模糊/亚克力走的是未公开的系统接口，不保证在所有系统上都生效。看不到任何变化就说明这台机器不支持——不影响其他功能。"
            : "当前系统不支持背景模糊（需要 Windows 10 1809 及以上），将使用纯透明。";

        SyncFromSettings();
        _loading = false;
        RefreshScopeState();
        ApplyPalette(_settings.Theme);
    }

    /// <summary>把控件状态同步成 <see cref="_settings"/> 的当前值。</summary>
    private void SyncFromSettings()
    {
        DarkRadio.IsChecked = _settings.Theme == AppTheme.Dark;
        LightRadio.IsChecked = _settings.Theme == AppTheme.Light;
        TransRadio.IsChecked = _settings.Theme == AppTheme.Transparent;

        OpacitySlider.Value = _settings.CardOpacityPercent;
        OpacityText.Text = $"{_settings.CardOpacityPercent}%";

        BorderCheck.IsChecked = _settings.ShowCardBorder;
        OutlineCheck.IsChecked = _settings.TextOutline;

        SummonHotkeyCheck.IsChecked = _settings.HotkeySummonEnabled;

        BlurNoneRadio.IsChecked = _settings.Blur == BlurMode.None;
        BlurBlurRadio.IsChecked = _settings.Blur == BlurMode.Blur;
        BlurAcrylicRadio.IsChecked = _settings.Blur == BlurMode.Acrylic;
    }

    /// <summary>
    /// 窗口重新获得焦点时同步一次。
    /// 因为用户可能开着设置窗口、又去托盘菜单里换了主题 —— 不同步的话单选按钮就显示错了。
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);

        bool wasLoading = _loading;
        _loading = true;
        SyncFromSettings();
        _loading = wasLoading;
        RefreshScopeState();
    }

    /// <summary>
    /// 只有「透明主题」下这几项才有意义，其余主题下置灰，
    /// 免得用户改了没反应还以为程序坏了。
    /// </summary>
    private void RefreshScopeState()
    {
        bool transparent = _settings.Theme == AppTheme.Transparent;

        OpacitySlider.IsEnabled = transparent;
        OutlineCheck.IsEnabled = transparent;
        BlurPanel.IsEnabled = transparent && _blurSupported;

        // 边框在所有主题下都有意义（深色/浅色也能把边框藏起来），所以不置灰
        ScopeHint.Text = transparent
            ? string.Empty
            : "以上「不透明度 / 文字描边 / 背景模糊」只对「透明」主题生效，其余主题已置灰。";
    }

    private void OnThemeChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        AppTheme theme = DarkRadio.IsChecked == true ? AppTheme.Dark
                       : LightRadio.IsChecked == true ? AppTheme.Light
                       : AppTheme.Transparent;

        _themeChanged(theme);       // 它会顺带保存设置并刷新悬浮窗
        RefreshScopeState();
    }

    private void OnOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;

        _settings.CardOpacityPercent = (int)Math.Round(e.NewValue);
        OpacityText.Text = $"{_settings.CardOpacityPercent}%";
        _preview();
    }

    private void OnOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.ShowCardBorder = BorderCheck.IsChecked == true;
        _settings.TextOutline = OutlineCheck.IsChecked == true;
        _preview();
    }

    private void OnBlurOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.Blur = BlurBlurRadio.IsChecked == true ? BlurMode.Blur
                       : BlurAcrylicRadio.IsChecked == true ? BlurMode.Acrylic
                       : BlurMode.None;

        _preview();
    }

    /// <summary>
    /// 快捷键开关。走的是**独立回调**（不是外观预览那条）——
    /// 它要干的事是"装/卸键盘钩子"，跟重新贴一遍颜色不是一回事，混在一起语义就乱了。
    /// </summary>
    private void OnHotkeyOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        _settings.HotkeySummonEnabled = SummonHotkeyCheck.IsChecked == true;
        _hotkeyChanged();
    }

    /// <summary>
    /// 让设置窗口自己跟着主题走（深色/透明档用暗色界面，浅色档用亮色界面）。
    ///
    /// 注意这里**同时**做三件事，缺一都会有"看不见字"或"割裂感"：
    ///   ① 设窗口底色与前景（前景给 RadioButton / CheckBox 这类走系统模板的控件用）
    ///   ② 换掉 XAML 里两个具名画刷资源（给显式用了 LabelText 样式的 TextBlock 用）
    ///   ③ 把标题栏也切成深色（否则深色界面顶着一个亮色标题栏，非常割裂）
    /// </summary>
    internal void ApplyPalette(AppTheme theme)
    {
        bool useDark = theme != AppTheme.Light;

        var background = new SolidColorBrush(useDark
            ? Color.FromRgb(0x20, 0x20, 0x22)
            : Color.FromRgb(0xFA, 0xFA, 0xFA));
        background.Freeze();

        var label = new SolidColorBrush(useDark
            ? Color.FromRgb(0xEA, 0xEA, 0xEA)
            : Color.FromRgb(0x1A, 0x1A, 0x1A));
        label.Freeze();

        var hint = new SolidColorBrush(useDark
            ? Color.FromRgb(0xA8, 0xA8, 0xA8)
            : Color.FromRgb(0x60, 0x60, 0x60));
        hint.Freeze();

        Background = background;
        Foreground = label;

        // XAML 里用 {DynamicResource LabelBrush} 绑的就是这两个 key，换掉即全局刷新
        Resources["LabelBrush"] = label;
        Resources["HintBrush"] = hint;

        ApplyTitleBarTheme(useDark);

        DiagnosticsLog.Write(
            $"设置窗配色 主题={theme} 底色={background.Color} 标签色={label.Color} 提示色={hint.Color}");
    }

    /// <summary>把系统标题栏切成深色/浅色，和内容区统一。</summary>
    private void ApplyTitleBarTheme(bool dark)
    {
        if (_hwnd == IntPtr.Zero) return;

        try
        {
            int value = dark ? 1 : 0;
            NativeMethods.DwmSetWindowAttribute(
                _hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
        }
        catch
        {
            // 老系统没这个属性，忽略即可
        }
    }

    /// <summary>
    /// 【诊断用】把窗口内容渲染到内存位图，报告像素颜色分布。
    /// 用途：验证"标签到底画成了什么颜色"—— 不靠肉眼、也不靠抓屏。
    /// 渲染的是 Body（它自己没有背景色），所以出来的颜色基本就是文字颜色，很好判读。
    /// </summary>
    internal void ReportRenderedColors()
    {
        double w = Body.ActualWidth;
        double h = Body.ActualHeight;
        if (w <= 1 || h <= 1)
        {
            DiagnosticsLog.Write("设置窗渲染快照 失败：布局尺寸为 0");
            return;
        }

        var bitmap = new RenderTargetBitmap((int)w, (int)h, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(Body);

        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var counts = new Dictionary<uint, int>();
        for (int i = 0; i < pixels.Length; i += 4)
        {
            uint rgb = (uint)((pixels[i + 2] << 16) | (pixels[i + 1] << 8) | pixels[i]);
            counts[rgb] = counts.TryGetValue(rgb, out int n) ? n + 1 : 1;
        }

        string top = string.Join("  ", counts.OrderByDescending(kv => kv.Value).Take(10)
            .Select(kv => $"#{kv.Key >> 16 & 0xFF:X2}{kv.Key >> 8 & 0xFF:X2}{kv.Key & 0xFF:X2}×{kv.Value}"));

        DiagnosticsLog.Write($"设置窗渲染快照 {bitmap.PixelWidth}x{bitmap.PixelHeight} | {top}");

        // 逐个报告每个文字的"生效前景色" —— 这是排查"有的字黑有的字白"的关键证据
        WalkAndReport(Body, 0);
    }

    private static void WalkAndReport(DependencyObject root, int depth)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);

            if (child is TextBlock tb)
            {
                string text = tb.Text.Length > 12 ? tb.Text[..12] + "…" : tb.Text;
                DiagnosticsLog.Write(
                    $"    文字 depth={depth} \"{text}\" 前景={Describe(tb.Foreground)} " +
                    $"显式={Describe(tb.ReadLocalValue(TextBlock.ForegroundProperty))} " +
                    $"继承={Describe(TextBlock.GetForeground(tb))}");
            }
            else if (child is ContentControl cc && cc.Content is string s)
            {
                DiagnosticsLog.Write(
                    $"    控件 {child.GetType().Name} \"{s}\" 前景={Describe(cc.Foreground)}");
            }

            WalkAndReport(child, depth + 1);
        }
    }

    private static string Describe(object? brush) => brush switch
    {
        SolidColorBrush sb => $"#{sb.Color.A:X2}{sb.Color.R:X2}{sb.Color.G:X2}{sb.Color.B:X2}",
        null => "(空)",
        _ => brush.ToString() ?? "(?)",
    };
}
