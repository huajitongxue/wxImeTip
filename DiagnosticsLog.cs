using System.IO;
using System.Text;

namespace ImeTip;

/// <summary>
/// 极简诊断日志，写到 <c>%LOCALAPPDATA%\ImeTip\ImeTip.log</c>。
///
/// 为什么正式版也保留它：这个程序显示的是"一个汉字"，一旦出错，
/// 光看界面根本无法判断是「没刷新」还是「读到的数据本身是错的」。
/// 有了日志，每一次状态变化都留下了完整的原始数据，排查时不用靠猜。
///
/// 代价可以忽略：只在状态变化、启动、出错时写，常驻时几乎不产生新内容。
/// </summary>
internal static class DiagnosticsLog
{
    private const long MaxBytes = 512 * 1024;
    private static readonly object Gate = new();
    private static bool _folderReady;

    internal static string FolderPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ImeTip");

    internal static string FilePath { get; } = Path.Combine(FolderPath, "ImeTip.log");

    internal static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                if (!_folderReady)
                {
                    Directory.CreateDirectory(FolderPath);
                    _folderReady = true;
                }

                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes)
                {
                    File.Delete(FilePath);
                }

                File.AppendAllText(
                    FilePath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 记日志失败绝不能影响主程序
        }
    }
}
