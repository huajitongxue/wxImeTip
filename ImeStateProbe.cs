using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Interop;

namespace ImeTip;

/// <summary>
/// 诊断模式：不显示窗口，连续采样 10 秒，把状态变化写进 probe.log。
/// 用法：ImeTip.exe --probe
///
/// 存在的意义：界面上的"中/英"只有一个字，读错了根本看不出是错在哪一步。
/// 这个模式会把语言 ID、转换模式原始值、前台窗口标题全打出来，一眼定位问题。
/// </summary>
internal static class ImeStateProbe
{
    public static void Run(double seconds = 10)
    {
        string path = Path.Combine(Environment.CurrentDirectory, "probe.log");

        var log = new StringBuilder();
        log.AppendLine($"=== ImeTip 诊断报告 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
        log.AppendLine($"进程 PID = {Environment.ProcessId}，采样时长 = {seconds} 秒");
        log.AppendLine();
        log.AppendLine("─── 自检 1：悬浮窗扩展样式 ───");
        log.AppendLine(CheckWindowStyles());
        log.AppendLine();
        log.AppendLine("─── 自检 2：输入法状态采样 ───");

        var watch = Stopwatch.StartNew();
        ImeState? previous = null;
        int samples = 0;
        int records = 0;
        bool reportedNull = false;

        while (watch.Elapsed.TotalSeconds < seconds)
        {
            ImeState? current = ImeStateReader.Read(IntPtr.Zero);
            samples++;

            if (current is null)
            {
                if (!reportedNull)
                {
                    log.AppendLine("[  0.00s] 读不到前台窗口（本次跳过）");
                    reportedNull = true;
                }
            }
            else if (previous is null || !current.Value.Equals(previous.Value))
            {
                records++;
                log.AppendLine($"[{watch.Elapsed.TotalSeconds,6:F2}s] {current.Value.Describe()}");
                previous = current;
            }

            Thread.Sleep(100);
        }

        log.AppendLine();
        log.AppendLine($"共采样 {samples} 次，捕获到 {records} 条状态记录。");
        log.AppendLine("（如果你在采样期间按过 Shift 切换中英文，应当能看到多条记录）");

        File.WriteAllText(path, log.ToString());
    }

    /// <summary>
    /// 自检：把窗口创建出来（但**不显示**），再读回它的扩展样式，
    /// 确认 WS_EX_NOACTIVATE / WS_EX_TOOLWINDOW 真的写进去了。
    /// 这两个位若静默设置失败，程序会变成"看着能用、实际是假的"，
    /// 所以必须能自动验证。
    /// </summary>
    private static string CheckWindowStyles()
    {
        var window = new MainWindow();
        try
        {
            // EnsureHandle 会强制创建底层 HWND（触发 SourceInitialized），但不会显示窗口
            IntPtr hwnd = new WindowInteropHelper(window).EnsureHandle();
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);

            bool noActivate = (exStyle & NativeMethods.WS_EX_NOACTIVATE) != 0;
            bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;

            return $"HWND=0x{hwnd:X8}  扩展样式=0x{exStyle:X8}  " +
                   $"NOACTIVATE={(noActivate ? "OK" : "失败")}  TOOLWINDOW={(toolWindow ? "OK" : "失败")}";
        }
        finally
        {
            window.Close();
        }
    }
}
