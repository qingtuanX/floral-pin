# 花笺钉桌面 FloralPin

把 [花笺](https://github.com/Achilng/floral-notepaper) 的便签 / 磁贴窗口**单独**「钉到桌面」或「恢复到置顶」的 Windows 小工具。

A tiny Windows companion for [floral-notepaper](https://github.com/Achilng/floral-notepaper):
pin a sticky-note window to the **desktop layer** (below all normal windows), or bring it back on top — one hotkey, per note.

```
鼠标指到便签上，按 Ctrl+Alt+D：
    置顶（默认）  ⇄  钉在桌面
    （浮在所有窗口之上）    （沉到所有窗口之下，只出现在桌面上，Win+D 不会被收走）
```

## 为什么做这个

花笺的磁贴 / 快捷便签目前只能强制置顶（官方需求：issue [#361](https://github.com/Achilng/floral-notepaper/issues/361)、[#262](https://github.com/Achilng/floral-notepaper/issues/262)）。
把便签当日程 / 待办用时，一直浮在最上层会遮挡其他窗口，但有时又需要它浮上来用一下。
所以需要的不是「全局开关」，而是**每个便签独立选择**。

花笺应用内的实现见 PR [#378](https://github.com/Achilng/floral-notepaper/pull/378)（尚未合并）。
本工具是外部实现：现在就能用、不动花笺本体、随时可撤。

## 安装

1. 把本仓库下载 / 克隆到任意固定目录（不要放 `Program Files`；放了也能用，日志会自动转到 `%LOCALAPPDATA%\FloralPin`）
2. 双击 `花笺钉桌面.cmd`
3. 选 `[1] 开始后台守护`；想开机自动生效再选 `[6] 开启开机自启`

环境要求：Windows 10 / 11（`.NET Framework 4.x` 系统自带）+ 花笺任意版本（便携版 / 安装版均可）。

## 使用

**日常**：鼠标指到便签上，按 `Ctrl+Alt+D` 切换；钉在桌面的便签再按一次即恢复置顶。

- 热键冲突会自动降级：`Ctrl+Alt+D` → `Ctrl+Alt+P` → `Ctrl+Alt+K`，实际生效的是哪个，菜单 `[5]` 状态里可以看到
- 新打开的便签保持花笺原生行为（置顶），不做任何全局强制

**菜单**（双击 `花笺钉桌面.cmd`）：

| 选项 | 作用 |
| --- | --- |
| 1 | 开始后台守护（让热键生效） |
| 2 | 切换鼠标下的便签（3 秒倒计时，不用记热键） |
| 3 / 4 | 全部钉到桌面 / 全部恢复置顶 |
| 5 | 查看当前状态（含实际热键、日志路径） |
| 6 / 7 | 开启 / 关闭开机自启 |

**命令行**：

```
FloralPin.exe watch          后台守护（注册热键）
FloralPin.exe toggle [hwnd]  切换鼠标下的窗口（或指定句柄）
FloralPin.exe pin            全部钉到桌面
FloralPin.exe restore        全部恢复置顶
FloralPin.exe status         查看状态
FloralPin.exe install        开启开机自启（启动文件夹快捷方式）
FloralPin.exe uninstall      关闭开机自启
```

运行日志在程序目录 `FloralPin.log`（目录不可写时自动转到 `%LOCALAPPDATA%\FloralPin\`）。

## 原理

与花笺 PR #378 相同的思路：把目标窗口 `SetParent` 到承载桌面图标的 `WorkerW`
（找不到时退化为 `Progman`），窗口成为桌面层的子窗口，从而位于所有普通窗口之下、
`Win+D`「显示桌面」也不会把它收走；恢复时脱离子窗口状态并重新置顶。

识别便签窗口：进程名以 `floral-notepaper` 开头 + 标题包含 `便签` / `便箋` / `磁贴` / `磁貼` / `Quick Note` / `Pin Mode`
（覆盖简体 / 繁体 / 英文界面）。

## 已知限制

- **不记得每个便签的选择**：便签关闭再打开是新的窗口，需要重新按一次热键（外部程序拿不到便签身份）
- **仅 Windows**（macOS / Linux 无对应的窗口机制）
- 属非标准窗口操作，个别安全软件可能提示，请选择允许 / 信任
- 菜单脚本 `花笺钉桌面.cmd` 为 **GBK 编码**（保证在中文 Windows 控制台直接显示正常）；
  GitHub 上预览该文件会显示乱码，属正常现象
- 部分安全软件会拦截注册表启动项写入，所以开机自启用的是「启动」文件夹快捷方式

## 编译

双击 `build.cmd`：用系统自带 .NET Framework 编译器，从 `FloralPin.cs` 生成 `FloralPin.exe`。
源码全部可见，可以自行编译后再用。

## English

A tiny Windows tool that pins [floral-notepaper](https://github.com/Achilng/floral-notepaper) sticky-note
windows to the desktop layer (below every normal window, survives "Show Desktop"), or restores them to
always-on-top — per note, toggled with `Ctrl+Alt+D` while hovering a note.

- Install: put the folder anywhere (not required to be outside Program Files, but recommended), run
  `花笺钉桌面.cmd` (menu) → `[1]` to start the background daemon, `[6]` to enable autostart.
- Same technique as floral-notepaper PR [#378](https://github.com/Achilng/floral-notepaper/pull/378):
  `SetParent` the window under the desktop-icon `WorkerW`.
- The menu script is GBK-encoded (Chinese Windows console); the `FloralPin.exe` CLI itself is ASCII-clean.

## License

MIT
