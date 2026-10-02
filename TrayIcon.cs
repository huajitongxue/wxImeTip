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

    // 三个状态的图标，构造时一次加载完，之后反复复用。
    // ⚠️ 绝不能 Dispose 掉正在被 _notifyIcon 使用的那个 —— 托盘会立刻变成一块空白，
    //    所以要等最后的 Dispose 里统一释放。
    private readonly Icon? _iconChinese;
    private readonly Icon? _iconEnglish;
    private readonly Icon? _iconUnknown;

    /// <param name="menu">托盘右键菜单，由 AppMenu 造好传入。</param>
    /// <param name="visibilityToggled">左键单击托盘图标 = 显示/隐藏悬浮窗</param>
    internal TrayIcon(ContextMenuStrip menu, Action visibilityToggled)
    {
        _iconChinese = LoadIcon("tray-zh.ico");
        _iconEnglish = LoadIcon("tray-en.ico");
        _iconUnknown = LoadIcon("tray-unknown.ico");

        _notifyIcon = new NotifyIcon
        {
            // 启动那一刻还不知道输入法状态，先挂"中"那一档；
            // 主窗口很快会做第一次采样，随即纠正成真实状态。
            // 三个资源全都读不到时才退回几何兜底图标（保证托盘不会是空白）。
            Icon = _iconChinese ?? CreateFallbackIcon(),
            Text = "ImeTip",
            Visible = true,
            ContextMenuStrip = menu,
        };

        DiagnosticsLog.Write(
            $"托盘图标三态 = 中{(_iconChinese is null ? "缺失" : "就绪")}、" +
            $"英{(_iconEnglish is null ? "缺失" : "就绪")}、" +
            $"未知{(_iconUnknown is null ? "缺失" : "就绪")}");

        // 左键单击托盘图标 = 显示/隐藏悬浮窗（和双击都一样，符合直觉）
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) visibilityToggled();
        };
    }

    /// <summary>
    /// 按当前输入法状态换托盘图标。
    ///
    /// Unreliable（这个窗口读不到状态）时**保持原样不动** —— 和悬浮窗的处理保持一致：
    /// 宁可显示旧信息，也不给一个确定但错误的答案。
    /// </summary>
    internal void SetMode(ImeMode mode)
    {
        Icon? next = mode switch
        {
            ImeMode.Chinese => _iconChinese,
            ImeMode.English => _iconEnglish,
            ImeMode.Unknown => _iconUnknown,
            _ => null,                 // Unreliable 等：不切换
        };

        // 用引用比对挡掉重复赋值 —— 同一个图标反复设置会让托盘闪一下
        if (next is not null && !ReferenceEquals(_notifyIcon.Icon, next))
        {
            _notifyIcon.Icon = next;
        }
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

        // 图标要等 NotifyIcon 释放之后再放 —— 它此刻还引用着其中一个
        _iconChinese?.Dispose();
        _iconEnglish?.Dispose();
        _iconUnknown?.Dispose();
    }

    /// <summary>
    /// 从程序集里读出一个托盘图标（assets 目录下的多尺寸 ICO）。
    ///
    /// 读不到时返回 null 而不是抛异常 —— 由调用方决定怎么兜底，
    /// 不能让"图标没找到"这种事把整个程序拖崩。
    /// </summary>
    private static Icon? LoadIcon(string fileName)
    {
        try
        {
            // 资源名 = 根命名空间 + "." + 项目内相对路径（斜杠换成点）
            string resourceName = $"{typeof(TrayIcon).Namespace}.assets.{fileName}";

            using Stream? stream = typeof(TrayIcon).Assembly
                .GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                // 顺手把现有资源名打出来，免得为了一个名字反复猜
                DiagnosticsLog.Write(
                    $"⚠ 读不到托盘图标 {resourceName}；程序集里现有：" +
                    string.Join(", ", typeof(TrayIcon).Assembly.GetManifestResourceNames()));
                return null;
            }

            // 从多尺寸 ICO 里挑最贴近系统小图标尺寸的那一档。
            // 高 DPI 下 SmallIconSize 会大于 16，这里跟着一起变大，避免放大发虚。
            Size target = SystemInformation.SmallIconSize;
            using var loaded = new Icon(stream, target);
            return (Icon)loaded.Clone();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 兜底图标：深色圆 + 中间一个青绿点（纯几何绘制，不依赖任何外部文件）。
    /// 只在托盘图标资源全都读不到时才会用到（理论上不该发生）。
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
