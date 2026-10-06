using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImeTip;

/// <summary>
/// 程序设置。存到 %APPDATA%\ImeTip\settings.json。
///
/// 读写一律用 try/catch 包住：配置损坏、目录不可写等情况绝不能导致程序起不来。
/// 对一个常驻小工具来说，"功能少一点但一定能启动"比"设置丢了就崩溃"重要得多。
/// </summary>
internal sealed class AppSettings
{
    /// <summary>窗口上次的位置（相对屏幕的 WPF 逻辑坐标）。null = 从未保存过。</summary>
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>
    /// 界面主题。用字符串序列化（"Dark" / "Light"）比数字可读，手工改配置文件时也看得懂。
    ///
    /// 旧版 settings.json 没有这个字段 → 反序列化后保持默认值 Dark，
    /// 也就是改造前的观感，老用户升级后不会突然变样。
    /// 就算字段值写错了也不怕：Load() 外层有 try/catch，最多丢设置，绝不会起不来。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    // ───────── 透明主题相关（都带默认值，旧配置缺字段时自动取默认，行为与改造前一致）─────────

    /// <summary>卡片不透明度百分比（0 = 全透明，100 = 全不透明）。只在「透明主题」下生效。</summary>
    public int CardOpacityPercent { get; set; } = 35;

    /// <summary>是否显示卡片边框。透明背景下关掉它更通透，开着则更容易辨认出卡片范围。</summary>
    public bool ShowCardBorder { get; set; } = true;

    /// <summary>是否给「中/英」字加描边。背景透明时这是可读性的主要保障。</summary>
    public bool TextOutline { get; set; } = true;

    /// <summary>
    /// 背景模糊种类。默认 None（不模糊）—— 这是未公开 API，不保证在所有系统上生效，
    /// 所以默认关闭，让用户主动去试。
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public BlurMode Blur { get; set; } = BlurMode.None;

    // ───────── 快捷键 ─────────
    //
    // 以后再加新快捷键，就在这一节里继续加字段（都带默认值，保证旧配置能读）。

    /// <summary>
    /// 「单独按一下 Ctrl，把方框召到鼠标旁边」是否启用。
    ///
    /// 默认**开启** —— 这是主动加的功能。关掉时程序会**完全卸载键盘钩子**，
    /// 一个按键都不再监听，比"留着钩子加个开关"更干净。
    /// </summary>
    public bool HotkeySummonEnabled { get; set; } = true;

    private static string FolderPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ImeTip");

    private static string FilePath => Path.Combine(FolderPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded is not null) return loaded;
            }
        }
        catch
        {
            // 配置损坏就当没有，用默认值继续
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // 保存不了也不影响本次使用
        }
    }
}
