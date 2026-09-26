using System.Windows.Media;

namespace ImeTip;

/// <summary>
/// 界面主题。三档，手动切换，不读系统设置。
///
/// ⚠️ Dark 必须是 0：旧版 settings.json 里没有 Theme 字段，
///    反序列化后会取默认值 0，正好落回"深色"——也就是改造前一直在用的观感。
///    这样老用户升级后不会突然变样。
/// </summary>
internal enum AppTheme
{
    Dark = 0,
    Light = 1,
    /// <summary>背景透明/半透明，透明度由用户设置决定。文字必须靠描边保证可读。</summary>
    Transparent = 2,
}

/// <summary>
/// 背景模糊的种类，对应 TranslucentTB 的 blur / acrylic 两档。
///
/// ⚠️ None 必须是 0：旧配置没有这个字段时回落成"不模糊"，最安全。
///
/// 说明：这两档走的是未公开的 SetWindowCompositionAttribute。
/// Win11 官方推荐的系统背景材质（DWMWA_SYSTEMBACKDROP_TYPE）对**永不激活的窗口**
/// 必然退化成纯色，所以用不了 —— 详见 WindowEffects.cs 的说明。
/// </summary>
internal enum BlurMode
{
    None = 0,
    Blur = 1,
    Acrylic = 2,
}

/// <summary>
/// 一套主题的全部颜色。
///
/// 颜色只在这里定义一处。
/// MainWindow.xaml 里那几个颜色字面量只是"设计器预览用"的默认值，
/// 运行时会被 ApplyAppearance() 整个覆盖 —— 要改颜色请改这个文件。
///
/// 卡片底色/边框用带 Alpha 的 Color：窗口是透明合成的，
/// alpha 直接决定"透不透、显不显眼"。
/// 文字三色的 alpha 恒为 255（用 Color.FromRgb），不参与透明混合。
/// </summary>
internal sealed record ThemePalette(
    Color CardBackground,
    Color CardBorder,
    Color Chinese,
    Color English,
    Color Unknown,
    Color TextOutline)
{
    /// <summary>深色：就是改造前一直在用的那套值。</summary>
    internal static readonly ThemePalette Dark = new(
        CardBackground: Color.FromArgb(0xE6, 0x26, 0x26, 0x28),
        CardBorder:     Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
        Chinese:        Color.FromRgb(0x4E, 0xC9, 0xB0),
        English:        Color.FromRgb(0xE0, 0xA4, 0x58),
        Unknown:        Color.FromRgb(0x88, 0x88, 0x88),
        TextOutline:    Colors.Transparent);   // 有实心背景，不需要描边

    /// <summary>
    /// 浅色：为"在深色壁纸/深色软件里显眼"而设计。
    ///
    /// 三个关键取舍：
    ///  1. 卡片 alpha 取 0xF5(245)，比深色的 0xE6(230) 更高。
    ///     因为半透明白叠在深色壁纸上会发灰 —— 0.9×白 + 0.1×深 ≈ 232，脏兮兮的。
    ///     这是浅色主题最容易做丑的地方。
    ///  2. 边框换成深色描边（#33000000）。浅色卡片若沿用白色边框，
    ///     在白色窗口（如记事本）上会跟背景糊在一起，只剩描边能兜住可辨识度。
    ///  3. **中/英换用与深色主题完全不同的色相**（绿→宝蓝、琥珀→玫红）。
    ///     刻意不"保持色相只调明暗" —— 那样两档主题下"中"都是绿，
    ///     用户切了主题会觉得"颜色根本没变"，等于白换。
    ///     附带好处：宝蓝与玫红落在"蓝-黄"轴上，红绿色盲也能分辨。
    ///
    /// 对比度（对白底）：中 7.0:1、英 6.4:1，都远超 WCAG AA 的 4.5:1。
    /// </summary>
    internal static readonly ThemePalette Light = new(
        CardBackground: Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF),
        CardBorder:     Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        Chinese:        Color.FromRgb(0x1D, 0x4E, 0xD8),
        English:        Color.FromRgb(0xBE, 0x18, 0x5D),
        Unknown:        Color.FromRgb(0x6B, 0x72, 0x80),
        TextOutline:    Colors.Transparent);

    /// <summary>
    /// 透明：背景几乎全透，靠壁纸本身的观感。
    ///
    /// 关键点：
    ///  1. **CardBackground 的 alpha 只是占位**，真实 alpha 由用户的透明度滑杆决定，
    ///     在 MainWindow.ApplyAppearance 里覆盖 —— 所以不能写死。
    ///  2. 文字色比深色档更亮（#5FE3C0 / #FFC16B），因为透明档多半用在深色壁纸上，
    ///     亮色更跳；万一壁纸是浅色，靠下面的深色描边兜住可读性。
    ///  3. **TextOutline 必须不透明**（#D0000000）：这是"背景全透明时字还看得清"
    ///     的唯一保障，也是透明主题能用起来的前提。
    /// </summary>
    internal static readonly ThemePalette Transparent = new(
        CardBackground: Color.FromArgb(0x00, 0x12, 0x12, 0x14),
        CardBorder:     Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF),
        Chinese:        Color.FromRgb(0x5F, 0xE3, 0xC0),
        English:        Color.FromRgb(0xFF, 0xC1, 0x6B),
        Unknown:        Color.FromRgb(0xD6, 0xD6, 0xD6),
        TextOutline:    Color.FromArgb(0xD0, 0x00, 0x00, 0x00));

    internal static ThemePalette For(AppTheme theme) => theme switch
    {
        AppTheme.Light => Light,
        AppTheme.Transparent => Transparent,
        _ => Dark,
    };
}
