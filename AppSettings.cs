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
