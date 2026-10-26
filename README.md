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

### 日志

每次任务栏状态变化都会记一行到 exe 旁边的 `taskbar-autohide.log`（上限 256 KB，超了自动清掉），内容长这样：

```
14:04:48.136  change on -> off: fg=Static
14:04:50.439  change off -> on: fg=Static[hidden] -> stand-in=Static[zoomed]
```

`fg=` 是判定的依据，`stand-in=` 表示前台窗口不可判定、按 z 序最上面那个可见窗口拍的板。排查「该收没收 / 该出不出」先看这个文件。

`--log` 额外记录每一个前台事件（很啰嗦，只在查问题时用）。

## 实现要点

- `SetWinEventHook` 监听 `EVENT_SYSTEM_FOREGROUND` 与前台窗口的 `EVENT_OBJECT_LOCATIONCHANGE`，替代轮询；事件到一个 45 ms 去抖（只从首个事件起算，不重置）后判定。另外挂了 `EVENT_OBJECT_HIDE` / `EVENT_OBJECT_DESTROY`（判定依据的那个窗口消失了）和 `EVENT_SYSTEM_MINIMIZEEND`（还原动画结束），用来补前台事件漏掉的那一刻。
- 判定结果通过 `SHAppBarMessage(ABM_SETSTATE)` 切换系统自动隐藏。这个调用是同步的，**会阻塞 250–530 ms**，因为 explorer 在此期间移动任务栏并更新工作区。所有判定与调用串行化，避免过期判定覆盖新判定。
- **状态从实测读取**（工作区高度 vs 显示器高度），不缓存"我上次设成什么"。explorer 重启、手动改设置、漏事件都能自愈；另有 2 秒的兜底检查。
- **前台窗口不可用时按 z 序找替身**：前台可能是隐藏的辅助窗口（微信关掉聊天弹窗后、WPS 从任务栏激活文档时都会留一个），也可能是空。这时按 z 序取最上面那个「用户看得见」的顶层窗口（跳过桌面、任务栏/开始/搜索/Alt+Tab 等 shell 面、隐形、最小化、cloaked 的 UWP、tool 窗口和 no-activate 窗口）来判定，并在 170 ms 后再确认一次，避免在两个窗口交接的空隙里来回弹。
- 「不可见的前台窗口」不再直接放弃判定（这正是 v1.0 卡住的根因）；只有连替身都找不到（锁屏、空桌面）才保持现状。点托盘图标时前台会交给隐藏宿主窗口，过去靠"不判定"避免任务栏冒出来，现在替身取到的就是下面那个最大化窗口，结果一样但状态不会卡住。

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
| 弹出窗关掉（前台落到隐藏窗口） → 任务栏收起 | 约 800 ms（v1.0 是**永不**） |
| 在多个最大化应用间切换 | 0 次多余切换 |
| 空闲 30 秒 CPU | ≈0 ms |
| 内存 | 14.3 MB（私有 9.4 MB） |
| 显存 | 0（不涉及 GPU） |

覆盖场景：资源管理器、UWP（计算器/设置）、Chrome、Edge、WPS（`OpusApp` 主窗口与其 Qt 隐藏窗口）、任务管理器、远程桌面对话框、无边框全屏、多窗口混合、托盘图标、Win+D、explorer 强制重启、锁屏与快速锁屏/解锁循环。

## 测试

`test\` 下有一套不依赖微信/WPS 的复现工具，用于回归「前台落在隐藏窗口」这一类问题：

```powershell
# 编译复现器
%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:exe ^
  /out:test\popup-repro.exe test\popup-repro.cs

.\test\repro-scenario.ps1     # 三阶段时间线：最大化 → 弹窗 → 弹窗消失
.\test\appbar-state.ps1       # 当前任务栏到底收没收（读工作区高度）
.\test\window-list.ps1        # z 序窗口清单 + 工具判定用的那些字段
```

`repro-scenario.ps1` 的 phase3 就是 v1.0 的失败点：弹出窗自己消失并把前台交给一个隐藏窗口，v1.0 停在 shown 不动，v1.1 在 1 秒内收起。


## 已知限制

- 那 250–530 ms 的延迟来自 explorer，**无法从外部缩短**：关掉任务栏动画（`TaskbarAnimations`、`SPI_SETCLIENTAREAANIMATION`）、改传 `hWnd=0`、把调用挪到后台线程都实测无效。
- 工具不区分"哪个"任务栏：`ABM_SETSTATE` 只作用于主屏 appbar。接了副屏需要重测。
- 会在桌面上**主动纠正**你对"自动隐藏任务栏"的手动改动（这是设计意图）。
- 登录时若比 explorer 先启动，首轮可能找不到任务栏句柄，2 秒内的兜底检查会补上。

## 变更记录

**v1.1** — 修两类「自动隐藏失效」：

- 微信等应用弹出窗消失后任务栏不收回：弹窗关掉时微信把前台交给了自己的**隐藏辅助窗口**，v1.0 对不可见的前台一律「不判定」，于是停在弹窗期间的那个"任务栏常显"状态上。现在按 z 序取最上面的可见窗口判定（stand-in）。
- 通过任务栏打开 WPS 已打开文件后任务栏不收回：同一种前台状态，只是触发源是 explorer 激活 + WPS 的 Qt 辅助窗口。同一处修复覆盖。
- 补挂 `EVENT_OBJECT_HIDE` / `EVENT_OBJECT_DESTROY` / `EVENT_SYSTEM_MINIMIZEEND`，判定依据的窗口消失或还原结束时立刻重判，不用等 2 秒兜底。
- 日志改为决策级常开（几行/次状态变化），`--log` 才记全部前台事件。

**v1.0** — 首版：最大化/全屏时收起任务栏，其余时候常显。

## 许可

MIT
