using System.Drawing;
using System.Windows.Forms;

namespace ImeTip;

/// <summary>
/// 悬浮窗右键菜单 + 托盘右键菜单（两套内容完全一致）。
///
/// ─── 为什么单独一个文件，而不是塞进 MainWindow ───
///   ImeTip.csproj 刻意移除了 System.Drawing / System.Windows.Forms 的隐式 using，
///   因为它们的 Color / Point / Brush 会和 WPF 的同名类型打架（变成"不明确的引用"）。
///   所以"碰 WinForms"的代码必须集中在这里；MainWindow 只通过 Action / Func 与它通信。
///
/// ─── 为什么弹菜单前要"抢前台" ───
///   WinForms 的下拉菜单只有在本进程是前台窗口时，才会在"点到菜单外面"时自动关闭。
///   托盘菜单一直正常，是因为 NotifyIcon 内部在弹出前先替我们调了一次 SetForegroundWindow
///   —— 这一步对调用方是隐形的。
///   而悬浮窗是 WS_EX_NOACTIVATE 窗口，右键根本不会激活它，
///   所以必须自己补上这一步，否则菜单会出现"点外面关不掉"。
///
/// ─── 为什么勾选状态不手工同步 ───
///   两套菜单的勾选都在 Opening 事件里从"当前真实状态"现算。
///   不保存第二份状态，就不可能一个勾了、另一个没勾。
/// </summary>
internal sealed class AppMenu : IDisposable
{
    private readonly ContextMenuStrip _trayMenu;
    private readonly ContextMenuStrip _popupMenu;

    /// <summary>弹菜单之前用户所在的前台窗口。菜单关掉之后要还给人家。</summary>
    private IntPtr _foregroundToRestore = IntPtr.Zero;

    /// <summary>托盘用的那一份，交给 TrayIcon。</summary>
    internal ContextMenuStrip TrayMenu => _trayMenu;

    internal AppMenu(
        Func<bool> isVisible,
        Func<bool> isAutoStartEnabled,
        Func<AppTheme> currentTheme,
        Action visibilityToggled,
        Func<bool, bool> autoStartChanged,
        Action<AppTheme> themeChanged,
        Action settingsRequested,
        Action exitRequested)
    {
        _trayMenu = BuildMenu(isVisible, isAutoStartEnabled, currentTheme,
                              visibilityToggled, autoStartChanged, themeChanged,
                              settingsRequested, exitRequested);

        _popupMenu = BuildMenu(isVisible, isAutoStartEnabled, currentTheme,
                               visibilityToggled, autoStartChanged, themeChanged,
                               settingsRequested, exitRequested);

        // 悬浮窗那一份关掉时，把前台还给用户
        _popupMenu.Closed += (_, _) => RestorePreviousForeground();
    }

    /// <summary>
    /// 造一个菜单。方块菜单和托盘菜单都是它造出来的，所以内容天然逐字一致。
    /// </summary>
    private static ContextMenuStrip BuildMenu(
        Func<bool> isVisible,
        Func<bool> isAutoStartEnabled,
        Func<AppTheme> currentTheme,
        Action visibilityToggled,
        Func<bool, bool> autoStartChanged,
        Action<AppTheme> themeChanged,
        Action settingsRequested,
        Action exitRequested)
    {
        // 刻意不用 CheckOnClick：让"勾选状态"只由程序按真实状态设置，
        // 避免"点一下 → 自动勾 → 回调里再设置 → 又触发一次"的循环。
        var visibleItem = new ToolStripMenuItem("显示悬浮窗");
        visibleItem.Click += (_, _) => visibilityToggled();

        var darkItem = new ToolStripMenuItem("深色主题");
        darkItem.Click += (_, _) => themeChanged(AppTheme.Dark);

        var lightItem = new ToolStripMenuItem("浅色主题");
        lightItem.Click += (_, _) => themeChanged(AppTheme.Light);

        var transItem = new ToolStripMenuItem("透明主题");
        transItem.Click += (_, _) => themeChanged(AppTheme.Transparent);

        var autoStartItem = new ToolStripMenuItem("开机自启");
        autoStartItem.Click += (_, _) => autoStartChanged(!autoStartItem.Checked);

        var settingsItem = new ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => settingsRequested();

        var exitItem = new ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => exitRequested();

        var menu = new ContextMenuStrip();
        menu.Items.Add(visibleItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(darkItem);
        menu.Items.Add(lightItem);
        menu.Items.Add(transItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(settingsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        // 每次弹出之前，从"当前真实状态"刷新勾选。
        // 两套菜单各算各的、算法相同，所以永远不会不一致。
        menu.Opening += (_, _) =>
        {
            visibleItem.Checked = isVisible();
            autoStartItem.Checked = isAutoStartEnabled();

            AppTheme theme = currentTheme();
            darkItem.Checked = theme == AppTheme.Dark;
            lightItem.Checked = theme == AppTheme.Light;
            transItem.Checked = theme == AppTheme.Transparent;
        };

        return menu;
    }

    /// <summary>
    /// 从悬浮窗右键弹出菜单。只能由 UI 线程调用。
    /// </summary>
    internal void ShowFromBlock(IntPtr blockWindow)
    {
        // ① 先记下用户原来在哪个窗口，菜单关掉后要还给他。
        //    他可能正在那个窗口里打字，不还回去等于偷走了他的输入焦点。
        _foregroundToRestore = NativeMethods.GetForegroundWindow();

        // ② 抢前台。这是"点菜单外面能自动关闭"的前提条件。
        //    用户刚刚点击了我们的窗口，满足"调用进程收到了最后一次输入事件"这条前提，
        //    通常能成功。失败了菜单就可能关不掉，所以必须记进日志。
        bool activated = NativeMethods.SetForegroundWindow(blockWindow);
        DiagnosticsLog.Write($"右键菜单 抢前台={(activated ? "成功" : "失败")}");

        // ③ 弹出。坐标用 Cursor.Position ——它和 Show(Point) 都是"物理像素的屏幕坐标"，
        //    同一个坐标系、零转换、零 DPI 风险；而且和托盘菜单用的是同一个来源，
        //    两个菜单的手感天然一致。
        _popupMenu.Show(Cursor.Position);

        // ④ 让菜单"属于"悬浮窗。
        //    否则菜单可能被 Topmost 的悬浮窗盖住（owned 窗口才会稳压在 owner 之上，
        //    而且 owner 是 topmost 时，owned 窗口也会跟着 topmost）。
        //    ⚠️ 句柄必须在 Show 之后取：下拉窗口的窗口句柄是弹出时才创建的。
        NativeMethods.SetWindowLongPtr(
            _popupMenu.Handle, NativeMethods.GWL_HWNDPARENT, blockWindow);

        // ⑤ 官方文档要求的"良性消息"。
        //    没有它，同一个菜单第二次弹出时可能"一闪就没"。
        NativeMethods.PostMessage(blockWindow, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// 菜单关闭后，把前台还给用户原来那个窗口。
    /// </summary>
    private void RestorePreviousForeground()
    {
        IntPtr previous = _foregroundToRestore;
        _foregroundToRestore = IntPtr.Zero;

        // 那个窗口可能在这期间被关掉了
        if (previous == IntPtr.Zero || !NativeMethods.IsWindow(previous)) return;

        // 已经就是它了，不用折腾
        if (NativeMethods.GetForegroundWindow() == previous) return;

        NativeMethods.SetForegroundWindow(previous);
    }

    public void Dispose()
    {
        _trayMenu.Dispose();
        _popupMenu.Dispose();
    }
}
