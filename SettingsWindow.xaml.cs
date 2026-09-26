using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
    private readonly bool _blurSupported;

    /// <summary>
    /// 初始化控件期间置为 true，用来**抑制事件回调**。
    /// 不加这个的话，构造时给 Slider 赋值会立刻触发 ValueChanged → 回调 → 又去改设置，
    /// 白白做一轮无用功，还可能在对象没构造完时引发空引用。
    /// </summary>
    private bool _loading = true;

    internal SettingsWindow(
        AppSettings settings,
        Action<AppTheme> themeChanged,
        Action preview,
        bool blurSupported)
    {
        InitializeComponent();

        _settings = settings;
        _themeChanged = themeChanged;
        _preview = preview;
        _blurSupported = blurSupported;

        // 模糊下拉：用 Tag 存枚举值，避免依赖"索引恰好等于枚举值"这种脆弱假设
        BlurCombo.Items.Add(new ComboBoxItem { Content = "无", Tag = BlurMode.None });
        BlurCombo.Items.Add(new ComboBoxItem { Content = "模糊", Tag = BlurMode.Blur });
        BlurCombo.Items.Add(new ComboBoxItem { Content = "亚克力", Tag = BlurMode.Acrylic });

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

        foreach (ComboBoxItem item in BlurCombo.Items)
        {
            if (item.Tag is BlurMode mode && mode == _settings.Blur)
            {
                BlurCombo.SelectedItem = item;
                break;
            }
        }
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
        BlurCombo.IsEnabled = transparent && _blurSupported;

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

    private void OnBlurChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (BlurCombo.SelectedItem is not ComboBoxItem item || item.Tag is not BlurMode mode) return;

        _settings.Blur = mode;
        _preview();
    }

    /// <summary>
    /// 让设置窗口自己跟着主题走（深色/透明档用暗色界面，浅色档用亮色界面）。
    /// 只改底色和前景色，不追求完整换肤 —— 一个工具窗口没必要。
    /// </summary>
    internal void ApplyPalette(AppTheme theme)
    {
        bool useDark = theme != AppTheme.Light;

        var background = new SolidColorBrush(useDark
            ? Color.FromRgb(0x20, 0x20, 0x22)
            : Color.FromRgb(0xFA, 0xFA, 0xFA));
        background.Freeze();

        var foreground = new SolidColorBrush(useDark
            ? Color.FromRgb(0xEA, 0xEA, 0xEA)
            : Color.FromRgb(0x1A, 0x1A, 0x1A));
        foreground.Freeze();

        // 设在窗口本身而不是 Body 上：Body 有 Margin，只设它的话四周会漏出窗口的默认底色。
        // Foreground 会自动向下继承给所有子控件。
        Background = background;
        Foreground = foreground;
    }
}
