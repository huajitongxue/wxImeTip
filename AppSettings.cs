using System.IO;
using System.Text.Json;

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
