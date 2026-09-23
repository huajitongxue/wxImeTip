# ImeTip — 输入法状态悬浮窗

常驻桌面的小方块，实时显示**当前会打出中文还是英文**。
不用再低头看右下角，也不用担心"一着急打出一串字母"。

- 中文 → 青色「中」
- 英文 → 橙黄「英」
- 读不到 → 灰色「?」

---

## 怎么用

**运行**：双击 `发布\ImeTip.exe`（单文件，无需安装）

| 操作 | 效果 |
|---|---|
| 按住左键拖动 | 移动位置（会自动记住，下次回到原位） |
| 右键点小方块 | 收进托盘 |
| 左键点托盘图标 | 显示 / 隐藏悬浮窗 |
| 右键点托盘图标 | 菜单：显示悬浮窗 / 开机自启 / 退出 |
| **鼠标悬停小方块** | 显示这一次读取的**全部原始数据**（排查问题用） |

**关闭**：托盘图标右键 → 退出。

**开机自启**：托盘菜单里勾选。想取消可以在菜单里再点一次，
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
| `MainWindow.xaml(.cs)` | 悬浮窗界面与交互 |
| `TrayIcon.cs` | 托盘图标与菜单 |
| `AppSettings.cs` | 设置持久化（`%APPDATA%\ImeTip\settings.json`） |
| `StartupManager.cs` | 开机自启（写 `HKCU\...\Run`） |
| `DiagnosticsLog.cs` | 诊断日志 |
| `ImeStateProbe.cs` | `--probe` 诊断模式 |

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
