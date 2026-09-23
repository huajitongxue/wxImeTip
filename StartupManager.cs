using Microsoft.Win32;

namespace ImeTip;

/// <summary>
/// 开机自启管理。
///
/// 实现方式：往注册表 HKEY_CURRENT_USER\...\Run 写一项。
/// 选它而不是"启动文件夹快捷方式"的原因：不需要创建 .lnk 文件（那要调 COM），
/// 一行注册表就够了，而且用户能在"任务管理器 → 启动"里看到并管理它。
///
/// 注意：只动 HKCU（当前用户），不需要管理员权限，也不会影响别的用户。
/// </summary>
internal static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ImeTip";

    internal static bool IsEnabled()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    internal static void Set(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null) return;

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return;

                // 路径加引号：万一装在带空格的目录里也不会被截断
                key.SetValue(ValueName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 注册表被策略锁定时静默失败，不影响程序其他功能
        }
    }
}
