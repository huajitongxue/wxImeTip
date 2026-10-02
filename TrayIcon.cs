using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
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
    /// 造托盘图标。
    ///
    /// 首选做法：直接复用打进程序集里的 icon.ico —— 也就是程序自身的图标文件。
    /// 这样托盘图标和 exe 图标永远同款：以后想换图标，只需重跑一次
    /// tools/make-icon.py，不会出现"改了一个忘了另一个"。
    ///
    /// 兜底：万一资源读不出来（理论上不该发生），退回到下面纯几何绘制的圆点图标，
    /// 保证托盘里永远不会是个空白。
    /// </summary>
    private static Icon CreateIcon()
    {
        try
        {
            // 资源名 = 根命名空间 + "." + 项目内相对路径（斜杠换成点）
            string resourceName = $"{typeof(TrayIcon).Namespace}.icon.ico";

            using Stream? stream = typeof(TrayIcon).Assembly
                .GetManifestResourceStream(resourceName);

            if (stream is not null)
            {
                // 从多尺寸 ICO 里挑最贴近系统小图标尺寸的那一档。
                // 高 DPI 下 SmallIconSize 会大于 16，这里跟着一起变大，避免放大发虚。
                Size target = SystemInformation.SmallIconSize;
                using var loaded = new Icon(stream, target);

                DiagnosticsLog.Write(
                    $"托盘图标 = 复用 icon.ico（取 {target.Width}x{target.Height} 那一档）");
                return (Icon)loaded.Clone();
            }
        }
        catch
        {
            // 落到下面兜底，不让托盘图标把整个程序拖崩
        }

        DiagnosticsLog.Write("托盘图标 = 兜底几何图标（icon.ico 资源没读到）");
        return CreateFallbackIcon();
    }

    /// <summary>
    /// 兜底图标：深色圆 + 中间一个青绿点（纯几何绘制，不依赖任何外部文件）。
    /// 只在 icon.ico 资源读不到时才会用到。
    /// </summary>
    private static Icon CreateFallbackIcon()
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
