# taskbar-autohide

桌面或普通窗口在前台时任务栏常显；**窗口最大化/全屏时任务栏自动隐藏**。

Windows 自带的「自动隐藏任务栏」是全局开关，TranslucentTB 只能改透明度——两者都做不到「只在最大化时隐藏」。这是个 11 KB 的小工具：**约 14 MB 内存、空闲 0 CPU、0 显存**，事件驱动，不轮询。

## 行为

| 前台窗口 | 任务栏 |
|---|---|
| 桌面（`Progman` / `WorkerW`） | 显示 |
| 普通窗口（含不可最大化的小对话框） | 显示 |
| 最大化窗口 | 隐藏 |
| 全屏窗口 / 无边框全屏（如全屏视频） | 隐藏 |
| 任务栏自身、开始菜单、Alt+Tab、托盘宿主、锁屏界面 | **保持现状，不判定** |

自动隐藏的语义：鼠标移到屏幕底边仍会划出任务栏——这是系统行为，不是漏判。

## 用法

直接运行 `taskbar-autohide.exe` 即可，无窗口、无托盘图标，同时只能跑一个实例（互斥锁）。

```powershell
# 安装到 %USERPROFILE%\claude-scripts 并加开机自启
.\install.ps1

# 卸载（结束进程、删除自启项、把自动隐藏恢复成关）
.\install.ps1 -Uninstall

# 手动恢复：把「自动隐藏任务栏」关掉并退出
.\taskbar-autohide.exe --off
```

自启是启动文件夹里一个 `.lnk`。取消：删掉它，或任务管理器 → 启动 → 禁用。

## 实现要点

- `SetWinEventHook` 监听 `EVENT_SYSTEM_FOREGROUND` 与前台窗口的 `EVENT_OBJECT_LOCATIONCHANGE`，替代轮询；事件到一个 45 ms 去抖（只从首个事件起算，不重置）后判定。
- 判定结果通过 `SHAppBarMessage(ABM_SETSTATE)` 切换系统自动隐藏。这个调用是同步的，**会阻塞 250–530 ms**，因为 explorer 在此期间移动任务栏并更新工作区。所有判定与调用串行化，避免过期判定覆盖新判定。
- **状态从实测读取**（工作区高度 vs 显示器高度），不缓存"我上次设成什么"。explorer 重启、手动改设置、漏事件都能自愈；另有 2 秒的兜底检查。
- 「不可见的前台窗口」一律不改变状态：点托盘图标时会把前台交给一个隐藏的宿主窗口，否则任务栏会从最大化窗口上冒出来。

## 构建

不需要安装 SDK，用 .NET Framework 自带的编译器：

```powershell
.\build.ps1
# 等价于
# %SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /optimize+ /target:winexe /out:taskbar-autohide.exe taskbar-autohide.cs
```

`/target:winexe` 保证没有控制台窗口，所以登录启动时不会闪黑框，也不需要 VBS 包装。

## 实测

Windows 11 26300（1707×1067 缩放，任务栏 48 px），各测 4 轮以上：

| 指标 | 数值 |
|---|---|
| 最大化 → 任务栏消失 | 515–545 ms（其中判定约 55 ms，其余是 explorer 的固定开销） |
| 还原 → 任务栏出现 | 310–312 ms |
| 切回一个已最大化的窗口 | 438–455 ms |
| 在多个最大化应用间切换 | 0 次多余切换 |
| 空闲 30 秒 CPU | ≈0 ms |
| 内存 | 14.3 MB（私有 9.4 MB） |
| 显存 | 0（不涉及 GPU） |

覆盖场景：资源管理器、UWP（计算器/设置）、Chrome、Edge、WPS、任务管理器、远程桌面对话框、无边框全屏、多窗口混合、托盘图标、Win+D、explorer 强制重启、锁屏与快速锁屏/解锁循环。

## 已知限制

- 那 250–530 ms 的延迟来自 explorer，**无法从外部缩短**：关掉任务栏动画（`TaskbarAnimations`、`SPI_SETCLIENTAREAANIMATION`）、改传 `hWnd=0`、把调用挪到后台线程都实测无效。
- 工具不区分"哪个"任务栏：`ABM_SETSTATE` 只作用于主屏 appbar。接了副屏需要重测。
- 会在桌面上**主动纠正**你对"自动隐藏任务栏"的手动改动（这是设计意图）。
- 登录时若比 explorer 先启动，首轮可能找不到任务栏句柄，2 秒内的兜底检查会补上。

## 许可

MIT
