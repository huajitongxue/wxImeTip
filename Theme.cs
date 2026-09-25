using System.Windows.Media;

namespace ImeTip;

/// <summary>
/// 界面主题。只有两档，手动切换，不读系统设置。
///
/// ⚠️ Dark 必须是 0：旧版 settings.json 里没有 Theme 字段，
///    反序列化后会取默认值 0，正好落回"深色"——也就是改造前一直在用的观感。
///    这样老用户升级后不会突然变样。
/// </summary>
internal enum AppTheme
{
    Dark = 0,
    Light = 1,
}

/// <summary>
/// 一套主题的全部颜色。
///
/// 颜色只在这里定义一处。
/// MainWindow.xaml 里那几个颜色字面量只是"设计器预览用"的默认值，
/// 运行时会被 ApplyTheme() 整个覆盖 —— 要改颜色请改这个文件。
///
/// 用的是带 Alpha 的 Color：卡片是 AllowsTransparency=True 的半透明窗口，
/// 底色/边框的 alpha 直接决定"透不透、显不显眼"。
/// 文字三色的 alpha 恒为 255（用 Color.FromRgb），不参与透明混合。
/// </summary>
internal sealed record ThemePalette(
    Color CardBackground,
    Color CardBorder,
    Color Chinese,
    Color English,
    Color Unknown)
{
    /// <summary>深色：就是改造前一直在用的那套值，一个字节都没动。</summary>
    internal static readonly ThemePalette Dark = new(
        CardBackground: Color.FromArgb(0xE6, 0x26, 0x26, 0x28),
        CardBorder:     Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF),
        Chinese:        Color.FromRgb(0x4E, 0xC9, 0xB0),
        English:        Color.FromRgb(0xE0, 0xA4, 0x58),
        Unknown:        Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>
    /// 浅色：为"在深色壁纸/深色软件里显眼"而设计。
    ///
    /// 三个关键取舍：
    ///  1. 卡片 alpha 取 0xF5(245)，比深色的 0xE6(230) 更高。
    ///     因为半透明白叠在深色壁纸上会发灰 —— 0.9×白 + 0.1×深 ≈ 232，脏兮兮的。
    ///     这是浅色主题最容易做丑的地方。
    ///  2. 边框换成深色描边（#33000000）。浅色卡片若沿用白色边框，
    ///     在白色窗口（如记事本）上会跟背景糊在一起，只剩描边能兜住可辨识度。
    ///  3. **中/英换用与深色主题完全不同的色相**：
    ///        中：绿(#4EC9B0) → 宝蓝(#1D4ED8)
    ///        英：琥珀(#E0A458) → 玫红(#BE185D)
    ///     刻意不"保持色相只调明暗" —— 那样两档主题下"中"都是绿，
    ///     用户切了主题会觉得"颜色根本没变"，等于白换。
    ///     两档主题差异越大，切换的感知越明确，这也正是加浅色主题的目的。
    ///     附带好处：宝蓝与玫红落在"蓝-黄"轴上，红绿色盲也能分辨。
    ///
    /// 对比度（对白底）：中 7.0:1、英 6.4:1，都远超 WCAG AA 的 4.5:1。
    /// </summary>
    internal static readonly ThemePalette Light = new(
        CardBackground: Color.FromArgb(0xF5, 0xFF, 0xFF, 0xFF),
        CardBorder:     Color.FromArgb(0x33, 0x00, 0x00, 0x00),
        Chinese:        Color.FromRgb(0x1D, 0x4E, 0xD8),
        English:        Color.FromRgb(0xBE, 0x18, 0x5D),
        Unknown:        Color.FromRgb(0x6B, 0x72, 0x80));

    internal static ThemePalette For(AppTheme theme) =>
        theme == AppTheme.Light ? Light : Dark;
}
