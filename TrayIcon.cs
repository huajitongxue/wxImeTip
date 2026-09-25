using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ImeTip;

/// <summary>
/// 系统托盘图标。
///
/// WPF 没有内置托盘图标，所以借用了 WinForms 的 NotifyIcon —— 这是业界通行做法。
/// 本文件只依赖 WinForms / Drawing，**不要在这里 using WPF 的命名空间**，
/// 否则 Color / Point / Brush 这些同名类型会冲突。
///
/// 菜单不在这里构建：它由 AppMenu 统一造好（和悬浮窗右键菜单同源），再注入进来。
/// 这样"两个菜单内容完全一致"这件事是结构上保证的，不靠人工维护。
/// 勾选状态的同步也不在这里做 —— AppMenu 在每次弹出前从真实状态现算。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    /// <param name="menu">托盘右键菜单，由 AppMenu 造好传入。</param>
    /// <param name="visibilityToggled">左键单击托盘图标 = 显示/隐藏悬浮窗</param>
    internal TrayIcon(ContextMenuStrip menu, Action visibilityToggled)
    {
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
