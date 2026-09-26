using System.Threading;
using System.Windows;

namespace ImeTip;

public partial class App : Application
{
    /// <summary>单实例互斥体：同一时间只允许一个 ImeTip 在跑。</summary>
    private static Mutex? _instanceMutex;

    // 用 Local\ 前缀：只在当前登录会话内生效，不会和其他用户的实例互相干扰
    private const string MutexName = @"Local\ImeTip.SingleInstance";
    private const string ShowWindowEventName = @"Local\ImeTip.ShowWindow";

    /// <summary>
    /// 程序入口。
    /// App.xaml 里已去掉 StartupUri，改由这里手动创建窗口，
    /// 目的是能判断"要不要显示界面"（诊断模式不显示界面）。
    /// </summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 只有菜单里的「退出」才结束进程。
        //
        // 为什么必须显式设置：本程序现在有第二个窗口（设置窗口）了。
        // 默认的 OnLastWindowClose 语义在"主窗口只是 Hide、从不 Close"的场景下容易出意外 ——
        // 万一哪天关掉设置窗口把整个程序带走了，用户会觉得"点个设置程序就没了"。
        // 显式声明"只能被主动退出"，最稳。
        // （--probe / --snapshot / 单实例分支都自己在末尾调 Shutdown()，不受影响。）
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 把未处理异常写进日志。GUI 程序出错往往只有一个"闪退"，
        // 没有这层兜底就只能靠猜。
        DispatcherUnhandledException += (_, args) =>
            DiagnosticsLog.Write($"!!! UI 线程未处理异常: {args.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            DiagnosticsLog.Write($"!!! 未处理异常: {args.ExceptionObject}");

        if (e.Args.Contains("--probe"))
        {
            ImeStateProbe.Run();
            Shutdown();
            return;
        }

        // 【临时验证入口：确认设置窗口渲染出的颜色，验证后删除】
        if (e.Args.Contains("--settingsshot"))
        {
            var shotWindow = new SettingsWindow(
                AppSettings.Load(), _ => { }, () => { }, WindowEffects.BlurAvailable);
            shotWindow.Show();

            var shotTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1200)
            };
            shotTimer.Tick += (_, _) => { shotTimer.Stop(); shotWindow.ReportRenderedColors(); Shutdown(); };
            shotTimer.Start();
            return;
        }

        // 诊断用：把界面渲染到内存位图并把颜色写进日志，然后退出
        if (e.Args.Contains("--snapshot"))
        {
            var probeWindow = new MainWindow();
            probeWindow.Show();
            probeWindow.SnapshotAndExit();
            return;
        }

        // ── 单实例保护 ──
        // 没有它的话，反复双击 exe 会开出多个进程 → 多个托盘图标 + 多个悬浮窗，
        // 用户完全不知道该关哪个（而且关掉一个，另一个还在）。
        // 第二个实例的职责是"把已经在跑的那个叫出来"，然后自己安静退出。
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            DiagnosticsLog.Write($"检测到已有实例在运行（PID={Environment.ProcessId}），唤醒它后退出");
            SignalExistingInstance();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        window.Show();
        ListenForShowRequests(window);
    }

    /// <summary>通知已经在跑的那个实例把自己显示出来。</summary>
    private static void SignalExistingInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ShowWindowEventName, out EventWaitHandle? signal))
            {
                signal.Set();
                signal.Dispose();
            }
        }
        catch
        {
            // 唤醒失败也不影响：对方本来就在运行
        }
    }

    /// <summary>在后台线程等"唤醒"信号，收到就把悬浮窗显示出来。</summary>
    private static void ListenForShowRequests(MainWindow window)
    {
        var signal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowWindowEventName);

        var thread = new Thread(() =>
        {
            while (true)
            {
                signal.WaitOne();

                // 切回 UI 线程再操作窗口（WPF 控件有线程亲和性）
                window.Dispatcher.Invoke(window.ShowFromTray);
            }
        })
        {
            IsBackground = true,   // 后台线程，不阻止进程退出
            Name = "ImeTip.ShowSignal",
        };

        thread.Start();
    }
}
