# ImeTip — 输入法状态悬浮窗

常驻桌面的小方块，实时显示**当前会打出中文还是英文**。
不用再低头看右下角，也不用担心"一着急打出一串字母"。

- 中文 → 「中」
- 英文 → 「英」
- 读不到 → 「?」

配色有**深色 / 浅色**两套，右键菜单里切换（会记住）。

---

## 怎么用

**运行**：双击 `发布\ImeTip.exe`（单文件，无需安装）

| 操作 | 效果 |
|---|---|
| 按住左键拖动 | 移动位置（会自动记住，下次回到原位） |
| 右键点小方块 | 弹出菜单 |
| 左键点托盘图标 | 显示 / 隐藏悬浮窗 |
| 右键点托盘图标 | 弹出**同一个**菜单 |
| **鼠标悬停小方块** | 显示这一次读取的**全部原始数据**（排查问题用） |

菜单项：`显示悬浮窗`(勾选) / `浅色主题` / `深色主题` / `开机自启`(勾选) / `退出`。

**关闭**：菜单 → 退出。

**开机自启**：菜单里勾选。想取消可以在菜单里再点一次，
也可以去「任务管理器 → 启动应用」里禁用它。

---

## 开发

### 环境
- .NET SDK（当前用 10.0.x）
- 任意编辑器（VS Code 即可）

### 常用命令
```bash
dotnet run                          # 编译并运行
dotnet build                        # 只编译
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o 发布
```
最后一条生成 `发布\ImeTip.exe`。

> VS Code 里可以直接按 `Ctrl+Shift+B`（仓库根目录 `.vscode/tasks.json` 已配好）。
> **不要按 F5 调试** —— 会触发 C# Dev Kit 的微软账号授权检查并报 vsdbg 错误。

### 文件说明
| 文件 | 作用 |
|---|---|
| `NativeMethods.cs` | **所有** Win32 API 声明集中在这里 |
| `ImeStateReader.cs` | 读取输入法状态的核心逻辑（与界面完全解耦） |
| `Theme.cs` | 主题配色表（深色 / 浅色两套颜色，改配色只改这里） |
| `AppMenu.cs` | 右键菜单（两套菜单同源构建 + 从 NOACTIVATE 窗口弹出的前台处理） |
| `MainWindow.xaml(.cs)` | 悬浮窗界面与交互 |
| `TrayIcon.cs` | 托盘图标（菜单由 AppMenu 注入） |
| `AppSettings.cs` | 设置持久化（`%APPDATA%\ImeTip\settings.json`） |
| `StartupManager.cs` | 开机自启（写 `HKCU\...\Run`） |
| `DiagnosticsLog.cs` | 诊断日志 |
| `ImeStateProbe.cs` | `--probe` 诊断模式 |
| `使用说明.md` | **面向使用者**的说明（每次 publish 自动复制到输出目录并改名 `README.md`） |

### 两种发布方式

| 命令 | 产物 | 体积 | 适用 |
|---|---|---|---|
| `--self-contained false` | `发布\ImeTip.exe` | ~190 KB | 自己用；对方需装 .NET 10 桌面运行时 |
| `--self-contained true` | `发布-免安装\ImeTip.exe` | ~72 MB | **发给别人**；对方什么都不用装 |

免安装版加 `-p:EnableCompressionInSingleFile=true` 可显著减小体积。
（WPF **不支持** `PublishTrimmed` 裁剪，72 MB 已是合理下限。）

发给别人时只需要 `ImeTip.exe` + `README.md` 两个文件，
`ImeTip.pdb` 是调试符号，可以不带。

---

## 排查问题

**日志**：`%LOCALAPPDATA%\ImeTip\ImeTip.log`
（只记录启动、状态变化、异常，所以通常很短）

**诊断模式**：`ImeTip.exe --probe`
不显示界面，连续采样 10 秒并把每一步的原始数据写进 `probe.log`。
用它可以在不靠肉眼观察的情况下验证"读取链路是否正常"。

---

## 已知限制

- **管理员权限的窗口读不到状态**（Windows 的权限隔离 UIPI 所致），会显示「?」。
  想让它在管理员窗口里也生效，需要以管理员身份运行本程序。
- **点任务栏 / 托盘 / 桌面时状态不会更新**（保持原样）。
  这是刻意的：点这些地方并不是要打字，它们的输入法状态没有意义。
- **独占全屏的游戏**里悬浮窗会被盖住。
- 换显示器后如果记忆的位置已不在屏幕内，会自动回到右下角（不会跑到看不见的地方）。

---

## 实现要点（踩过的坑）

1. **跨进程读取**：`ImmGetContext` 只对本线程的窗口有效，读别的进程必须改用
   `ImmGetDefaultIMEWnd` + `SendMessage(WM_IME_CONTROL, ...)`。
2. **判定中文必须同时看两个值**：
   `opened = IMC_GETOPENSTATUS` 且 `convMode & IME_CMODE_NATIVE`。
   **只看 convMode 会完全判错** —— 输入法关闭（英文直通）时 convMode 仍可能等于 1。
3. **Windows 11 记事本**（WinUI3）顶层窗口永远报 `opened=0`，
   需要改问"真正持有键盘焦点的子窗口"。
4. **`WS_EX_NOACTIVATE`** 是命门：没有它，一拖动自己就抢走焦点，
   读到的永远是自己的状态。但加了它之后必须处理 `WM_MOVING`，否则拖动会"松手才跳"。
5. **状态判定链里的每个 `return` 都在抢答**：越"不管什么情况都成立"的规则越要排前面。
6. **从 `WS_EX_NOACTIVATE` 窗口弹菜单，必须先自己抢前台**：
   WinForms 下拉菜单只有在本进程是前台窗口时才会"点外面自动关闭"。
   托盘菜单一直正常是因为 `NotifyIcon` 内部替我们调了 `SetForegroundWindow` —— 这一步对调用方是隐形的。
   悬浮窗是 NOACTIVATE 窗口，右键不会激活它，必须自己补：
   记前台 → `SetForegroundWindow` → `Show(Cursor.Position)` → `PostMessage(WM_NULL)`；
   菜单关闭后在 `Closed` 里**把前台还给用户**，否则等于偷走了他正在打字的窗口焦点。
   另外右键必须绑 **`MouseRightButtonUp`**：菜单弹出时会抓取鼠标，
   紧接的"抬起"若落在菜单上会把刚弹出的菜单立刻关掉。
7. **切主题后要重贴当前文字颜色**：不补这一步，"中"字会停在旧主题的颜色上，
   直到用户下次切换输入法才更新 —— 而他可能半天都不切。
