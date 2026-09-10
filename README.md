# AltTabReplacer

替代 Windows 默认 `Alt+Tab` 的窗口切换器（C# / .NET 8 / WPF，个人自用工具）。

## 状态

P1 MVP 已完成代码。**需要 .NET 8 SDK 才能编译**（已检测到 .NET 8 Desktop Runtime，但 SDK 缺失）。

## 功能

- 全局热键 `Ctrl + Alt + Tab` 唤起选择器
- 缩略图网格（基于 `PrintWindow + PW_RENDERFULLCONTENT`，兼容 DirectX / 现代应用）
- 键盘 `123QWE / ASDF / ZXCV` 物理键切换（QWERTY 键盘，与 Alt-Tab Terminator 一致）
- 鼠标 hover / 点击切换
- `Tab` / `Shift+Tab` 翻页（超过 35 个窗口时）
- 释放任一修饰键 → 切到当前高亮 + 自动隐藏
- `Esc` → 取消
- 自定义排序规则（置顶 / 排除 / 优先级）
- 规则热重载（外部编辑 rules.json 自动生效）
- 完整日志到 `%APPDATA%\AltTabReplacer\logs\`

## 怎么跑

### 1. 装 .NET 8 SDK

仅 Runtime 装好了，**SDK 没装**。两种方式二选一：

**A) 命令行安装（推荐）**
```powershell
winget install Microsoft.DotNet.SDK.8
```
安装完后**重开 PowerShell** 让 PATH 生效。

**B) 手动下载**
<https://dotnet.microsoft.com/download/dotnet/8.0> → 下载 `SDK 8.x.x → x64` → 安装。

验证：
```powershell
dotnet --list-sdks
# 应该看到类似 8.0.xxx 的一行
```

### 2. 编译运行

在项目根目录（即 `AltTabReplacer.sln` 所在目录）打开 PowerShell：

```powershell
dotnet restore
dotnet build -c Debug
dotnet run --project src/AltTabReplacer.App
```

第一次 `dotnet restore` 会下载 `System.Drawing.Common` NuGet 包，需要联网。后续会被缓存。

### 3. 使用

启动后**无任何窗口**（设计如此——这是个常驻后台服务）。在任意位置按下 `Ctrl + Alt + Tab`：
- 看到选择器弹出
- 按 `1 2 3 Q W E A S D` 等切换高亮
- 鼠标移动 / 点击
- 松开 `Ctrl` 或 `Alt` → 切到高亮窗口
- `Esc` 取消

## 配置位置

| 文件 | 路径 | 用途 |
|---|---|---|
| `settings.json` | `%APPDATA%\AltTabReplacer\settings.json` | 全局配置（热键、布局、主题） |
| `rules.json` | `%APPDATA%\AltTabReplacer\rules.json` | 排序规则（自动生成 + 热重载） |
| `app-YYYYMMDD.log` | `%APPDATA%\AltTabReplacer\logs\` | 每日滚动日志 |

> `%APPDATA%` 在 Windows 上通常是 `C:\Users\<你>\AppData\Roaming\`。

### 默认 settings.json
```json
{
  "Hotkey": { "Modifiers": "Ctrl, Alt", "Key": "Tab" },
  "Layout": { "CellWidth": 256, "CellHeight": 144, "CellPadding": 8, "MaxColumns": 10, "MaxPerPage": 35 },
  "Theme": { "Accent": "#FF0078D4", "Background": "#CC202020", "CornerRadius": 8 },
  "Behavior": { "HideOnWindowChange": true, "IgnoreFullscreen": true, "PollIntervalMs": 1000 }
}
```

### 示例 rules.json
见 `samples/rules.example.json`。

## 目录结构

```
AltTabReplacer/
├── AltTabReplacer.sln
├── docs/design.md              详细设计文档
├── samples/rules.example.json  规则示例
├── src/
│   ├── AltTabReplacer.Core/    服务 + 模型 + Win32
│   │   ├── Infrastructure/      EventBus / Logger / Win32 P/Invoke
│   │   ├── Models/              WindowInfo / SortRule / Settings
│   │   └── Services/            Hotkey / Enumerator / Thumbnail /
│   │                            Capture / Activator / RuleStore / Engine
│   └── AltTabReplacer.App/     WPF 启动器 + 视图
│       ├── App.xaml(.cs)        入口 + 热键响应
│       ├── HostWindow           隐藏的 HWND（接收 WM_HOTKEY）
│       ├── SelectorWindow       主选择器
│       ├── ViewModels/
│       └── app.manifest         DPI 感知
└── README.md (本文件)
```

## 常见问题

**Q: 热键被占用怎么办？**
A: 编辑 `%APPDATA%\AltTabReplacer\settings.json`，改 `Hotkey.Modifiers` 或 `Hotkey.Key`（如 `"F1"`、`"Alt, Space"` 等），重启程序。详见 `HotkeyService.ParseKey`。

**Q: 某些窗口不显示？**
A: 当前版本过滤掉了 UWP / Store App（Cloaked 窗口）。后续 P3 阶段会增加兼容。

**Q: 缩略图是黑屏？**
A: 极少数使用 GPU 独占的窗口无法被 `PrintWindow` 截取。已 fallback 到 "(no preview)" 占位。

**Q: 怎么彻底退出？**
A: 当前版本没有托盘图标，最简单是**任务管理器结束 `AltTabReplacer.exe`**，或用 `taskkill /im AltTabReplacer.exe /f`。后续 P3 加托盘。

## 路线图

| 阶段 | 状态 |
|---|---|
| P1 MVP：热键 + 缩略图 + 123QWE + 鼠标 | ✅ 代码完成 |
| P2 自定义排序规则 + 配置 UI | ⏳ |
| P3 体验打磨（托盘、自启动、多显示器） | ⏳ |

详见 `docs/design.md`。
