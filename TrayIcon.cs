using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ImeTip;

/// <summary>
/// 托盘图标与右键菜单。
///
/// WPF 没有内置托盘图标，所以借用了 WinForms 的 NotifyIcon —— 这是业界通行做法。
/// 本文件只依赖 WinForms / Drawing，**不要在这里 using WPF 的命名空间**，
/// 否则 Color / Point / Brush 这些同名类型会冲突。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _autoStartItem;
    private readonly ToolStripMenuItem _visibleItem;

    /// <param name="autoStartEnabled">开机自启当前是否已启用（会写入菜单勾选状态）</param>
    /// <param name="autoStartChanged">
    /// 用户切换"开机自启"时回调，参数是请求的新状态；
    /// <b>返回值是实际生效的状态</b>（例如注册表写入失败时返回 false）。
    /// </param>
    /// <param name="visibilityToggled">用户要求显示/隐藏悬浮窗</param>
    /// <param name="exitRequested">用户要求退出程序</param>
    internal TrayIcon(
        bool autoStartEnabled,
        Func<bool, bool> autoStartChanged,
        Action visibilityToggled,
        Action exitRequested)
    {
        // 刻意不用 CheckOnClick：让"勾选状态"只由程序根据真实状态设置，
        // 避免"点一下 → 自动勾选 → 回调里再设置 → 事件再触发"的循环。
        _visibleItem = new ToolStripMenuItem("显示悬浮窗") { Checked = true };
        _visibleItem.Click += (_, _) => visibilityToggled();

        _autoStartItem = new ToolStripMenuItem("开机自启") { Checked = autoStartEnabled };
        _autoStartItem.Click += (_, _) =>
        {
            // ⚠️ 必须把实际生效的状态回填到勾选上。
            //    之前漏了这一步，导致勾永远不变、看起来像"点不动"。
            //    用"实际状态"而不是"请求状态"，注册表写失败时界面就不会假装成功。
            bool requested = !_autoStartItem.Checked;
            _autoStartItem.Checked = autoStartChanged(requested);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_visibleItem);
        menu.Items.Add(_autoStartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exitRequested());

        _notifyIcon = new NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "ImeTip",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 左键单击托盘图标 = 显示/隐藏悬浮窗（和双击都一样，符合直觉）
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) visibilityToggled();
        };
    }

    /// <summary>同步"显示悬浮窗"菜单项的勾选状态。</summary>
    internal void SetVisibleState(bool visible) => _visibleItem.Checked = visible;

    /// <summary>鼠标悬停托盘图标时显示的提示文字（系统限制约 63 个字符）。</summary>
    internal void SetTooltip(string text)
    {
        if (text.Length > 60) text = text[..60];
        _notifyIcon.Text = text;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;   // 必须先隐藏，否则托盘里会留一个"僵尸图标"
        _notifyIcon.Dispose();
    }

    /// <summary>
    /// 在内存里画一个托盘图标：深色圆 + 中间一个青绿点。
    /// 这样就不必额外附带一个 .ico 文件，也方便以后按状态换颜色。
    /// </summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var background = new SolidBrush(Color.FromArgb(38, 38, 40));
            g.FillEllipse(background, 0, 0, 31, 31);

            using var ring = new Pen(Color.FromArgb(90, 255, 255, 255), 2f);
            g.DrawEllipse(ring, 1, 1, 29, 29);

            using var dot = new SolidBrush(Color.FromArgb(78, 201, 176));
            g.FillEllipse(dot, 10, 10, 12, 12);
        }

        // GetHicon 返回的是需要手工释放的 Win32 句柄，
        // 而 Icon.FromHandle 的 Icon 对象并不拥有它 —— 所以先 Clone 再 DestroyIcon。
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
