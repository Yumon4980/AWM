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
Start-Process src\AltTabReplacer.App\bin\Debug\net8.0-windows\AltTabReplacer.exe -Verb RunAs
```

> **不要用 `dotnet run`。** 程序的 manifest 声明了 `requireAdministrator`，而 `dotnet run` 走
> `CreateProcess`、无法触发 UAC 提权，会直接报错 740「请求的操作需要提升」。必须用
> `Start-Process -Verb RunAs`（或在资源管理器里双击 exe）启动。
>
> 为什么非要管理员权限：低层键盘钩子受 UIPI 约束，非提权进程**收不到**发往更高完整性级别窗口的按键。
> 不提权的话，任务管理器这类窗口在前台时 `Alt+Tab` 一定漏给系统，弹出来的是系统自带的切换器。
>
> 想开机自启又不想每次弹 UAC：用「任务计划程序」建一个任务，触发器选登录，
> 勾选「使用最高权限运行」。启动文件夹和 Run 注册表项做不到免提示。

第一次 `dotnet restore` 会下载 `System.Drawing.Common` NuGet 包，需要联网。后续会被缓存。

### 3. 使用

启动后**无任何窗口**（设计如此——这是个常驻后台服务）。在任意位置按下 `Alt + Tab`（系统自带的任务切换器会被接管，不再弹出）：

| 按键 | 作用 |
|---|---|
| `Alt + Tab` | 唤起选择器，初始选中第 1 项 |
| 再按 `Alt + Tab` | **关闭**选择器，与 `Esc` 相同 |
| `1 2 3 4` / `Q W E R` / `A S D F` / `Z X C V` | 16 个槽位。命中窗口直接切；**命中组则进入二级**，再按一个键选组内窗口 |
| `Esc` / `Backspace` | **逐级回退**：二级 → 一级 → 关闭。在组里按 Esc 等于撤销刚才那次按键 |
| `Tab` / `↓` | 选中下一项（到底循环） |
| `Shift + Tab` / `↑` | 选中上一项 |
| `Enter` | 确认。选中组则进入组，选中窗口则切过去 |
| `/` | 进入搜索模式（跨组扁平搜索），`Esc` 退出 |
| 鼠标拖动 | 拖到**行与行之间**=排序，拖到**行中部**=并成一组 |
| 鼠标右键 | 重命名该程序/分组（留空恢复默认名）、解散分组 |

> 用 `Tab` 上下移动前要**先松开 `Alt`**。`Alt` 还按着的时候按 `Tab` 就是 `Alt+Tab`，
> 会走热键那条路，也就是关闭选择器。

### 分组

- 同一进程开了 ≥2 个窗口时**自动折叠成一个组**，只占一个槽位（`AutoGroupThreshold` 可调，设 99 等于关闭）
- 把一行拖到另一行**中部**即可并成一组；拖到**行间**只是排序
- 拖动时按住 `Shift` 强制排序、按住 `Ctrl` 强制分组
- 组里只剩 1 个成员时自动解散
- 在二级把成员拖到顶部的面包屑上 = **移出该组**（仅限跨进程的组，见下）
- 槽位超过 16 个时，第 16 格自动变成「更多…」组装下余量
- 托盘菜单有「重置分组与顺序」
- 右键任意一行可以**重命名**（程序名或分组名），留空则恢复默认名；分组还能右键**解散**

> **分组的身份锚定在进程名上**，因为只有进程名跨会话稳定（窗口标题会随着换文件、换标签而变）。
> 代价是：自动折叠出来的单进程组没法把"其中某一个窗口"单独拖出来。
> 组内顺序不做持久化，每次按最近使用顺序排——组里第一个永远是你最近用的那个。

## 配置位置

| 文件 | 路径 | 用途 |
|---|---|---|
| `settings.json` | `%APPDATA%\AltTabReplacer\settings.json` | 全局配置（热键、布局、主题） |
| `layout.json` | `%APPDATA%\AltTabReplacer\layout.json` | **槽位布局**：顺序 + 分组（拖动自动写入 + 热重载） |
| `rules.json` | `%APPDATA%\AltTabReplacer\rules.json` | 排除规则（只管隐藏哪些窗口，不再管排序） |
| `app-YYYYMMDD.log` | `%APPDATA%\AltTabReplacer\logs\` | 每日滚动日志 |

> `%APPDATA%` 在 Windows 上通常是 `C:\Users\<你>\AppData\Roaming\`。

### 默认 settings.json
```json
{
  "Hotkey": { "Modifiers": "Alt", "Key": "Tab" },
  "Layout": { "CellWidth": 256, "CellHeight": 144, "CellPadding": 8, "MaxColumns": 4, "AutoGroupThreshold": 2, "WidthRatio": 0.5, "HeightRatio": 0.55 },
  "Theme": { "Accent": "#FF0078D4", "Background": "#CC202020", "CornerRadius": 8 },
  "Behavior": { "HideOnWindowChange": true, "IgnoreFullscreen": true, "PollIntervalMs": 1000 }
}
```

### 示例 rules.json
见 `samples/rules.example.json`。

### 选择器窗口的大小和位置
选择器**始终居中在主显示器**，尺寸按主显示器工作区（已排除任务栏）的比例算，和分辨率无关：

- `WidthRatio` / `HeightRatio`：占工作区的比例，默认 `0.5` / `0.55`
- 下限 640×420 DIP，屏幕比这还小时以屏幕为准
- 单位是 DIP 不是像素，所以高 DPI 缩放下观感一致

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
