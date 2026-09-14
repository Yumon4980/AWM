# AltTabReplacer

替代 Windows 默认 `Alt+Tab` 的窗口切换器（C# / .NET 8 / WPF）。

> 后台常驻，按下 `Alt+Tab` 弹出带缩略图的选择器，支持物理键直达、程序组 / 程序组合、跨组搜索与拖拽管理。

---

## 状态

P1 MVP + P2 已完成代码，P3（多显示器 / 安装包 / 开机自启 UI）部分实现。
完整设计见 [`docs/design.md`](docs/design.md)，面向用户的快速上手见 [`docs/用户使用说明.md`](docs/用户使用说明.md)。

## 功能一览

- **接管 `Alt+Tab`**：用低层键盘钩子吞掉系统保留组合，弹自己的选择器
- **缩略图网格**：DWM 实时截图，基于 `PrintWindow + PW_RENDERFULLCONTENT`，兼容 DirectX / 现代应用
- **16 个物理键直达**：`1234 / QWER / ASDF / ZXCV`（与 Alt-Tab Terminator 一致）
- **三种对象**：
  - **程序**：按下直接切换
  - **程序组**：按下进入二级（同一进程多窗口自动折叠）
  - **程序组合**：按下**同时**激活 / 启动全部成员（最多 4 个，记可执行路径）
- **拖拽管理**：把一行拖到另一行中部=并组 / 拖到行间=排序，按住 `Shift` / `Ctrl` 强制排序或并入
- **跨组扁平搜索**：按 `/` 进入搜索模式，输入即过滤
- **布局持久化**：拖动后自动写入 `layout.json`，支持热重载
- **排除规则**：通过 `--config` 打开配置窗口，按进程名 / 窗口标题隐藏
- **托盘图标**：右键菜单含"显示选择器"和"退出"
- **完整日志**：每日滚动日志写到 `%APPDATA%\AltTabReplacer\logs\`

## 怎么跑

### 方式一：用现成 exe（推荐）

`publish/` 目录下有已经编译好的发布版：

```
publish/
├── AltTabReplacer.exe          # 主程序（需管理员权限）
├── AltTabReplacer.Core.dll
└── ...
```

直接双击运行即可，会自动请求 UAC 提权。

### 方式二：一键脚本

```powershell
.\build-and-run.ps1
```

脚本会自动 `dotnet restore` → `dotnet build -c Debug` → `Start-Process -Verb RunAs` 提权启动。

### 方式三：手动编译

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```powershell
dotnet restore
dotnet build -c Release
# 产物：src\AltTabReplacer.App\bin\Release\net8.0-windows\AltTabReplacer.exe

# 生成单文件发布版到 publish/：
dotnet publish src\AltTabReplacer.App -c Release -r win-x64 --self-contained false -o publish
```

> **不要用 `dotnet run`。** `app.manifest` 声明了 `requireAdministrator`，
> `dotnet run` 走 `CreateProcess` 无法触发 UAC，会直接报 740「请求的操作需要提升」。
> 必须用 `Start-Process -Verb RunAs`、或在资源管理器里双击 exe 启动。
>
> 为什么非要管理员权限：低层键盘钩子受 UIPI 约束，非提权进程**收不到**
> 发往更高完整性级别窗口的按键。不提权的话，任务管理器这类窗口在前台时
> `Alt+Tab` 一定漏给系统，弹出来的是系统自带的切换器。
>
> 想开机自启又不想每次弹 UAC：用「任务计划程序」建一个任务，触发器选登录，
> 勾选「使用最高权限运行」。启动文件夹和 Run 注册表项做不到免提示。

## 打开规则配置窗口

后台模式下双击 exe 不会显示窗口（设计如此——这是个常驻服务）。
需要修改排除规则时：

```powershell
Start-Process .\AltTabReplacer.exe -ArgumentList '--config' -Verb RunAs
```

或者从托盘图标右键菜单进入。配置窗口关闭后整个进程退出，不会回到后台模式。

## 使用

启动后**无任何窗口**（除托盘图标外）。在任意位置按下 `Alt+Tab`，
系统自带的任务切换器会被取代，选择器出现在屏幕中央：

| 按键 / 操作 | 作用 |
|---|---|
| `Alt + Tab` | 唤起选择器（首次按下）/ 关闭（再按一次） |
| `1` `2` `3` `4` | 选中第 1–4 项 |
| `Q` `W` `E` `R` | 选中第 5–8 项 |
| `A` `S` `D` `F` | 选中第 9–12 项 |
| `Z` `X` `C` `V` | 选中第 13–16 项 |
| `Tab` / `↓` | 下一项（注意先松开 `Alt`） |
| `Shift + Tab` / `↑` | 上一项 |
| `Enter` | 确认：程序切换 / 程序组进入二级 / 程序组合打开全部 |
| `Esc` / `Backspace` | 逐级回退：二级 → 一级 → 关闭 |
| `/` | 进入跨组扁平搜索，`Esc` 退出 |
| 鼠标左键点击 | 切换到当前项 |
| 鼠标右键 | 操作菜单（重命名 / 关闭 / 删除 / 解散等） |
| 鼠标拖动 | 拖到行中部=建组 / 并入；拖到行间=排序 |

> **三种对象的区别**：
>
> | 对象 | 行为 |
> |---|---|
> | **程序** | 一个窗口。按下直接切换 |
> | **程序组** | 容器。按下进入二级（严格两级，不能嵌套组） |
> | **程序组合** | 一组程序（最多 4 个）。按下**同时打开全部** |

### 分组与组合

- 同一进程开了 ≥2 个窗口时**自动折叠成一个程序组**，只占一个槽位（`AutoGroupThreshold` 可调，设 99 等于关闭）
- 把一行拖到另一行**中部**：目标是程序 / 程序组合 → 建**程序组合**；目标是程序组 → 把这一行**加入该组**
- 拖到行间只排序；按住 `Shift` 强制排序，按住 `Ctrl` 强制并入
- 自动折叠的组只剩 1 个成员时自动解散；手动建的组即使为空也保留
- 二级内也能拖动排序、并成程序组合
- 二级把成员拖到顶部面包屑 = **移出该组**
- 槽位超过 16 个时进入"未入网格"列表
- 左上角 **「+ 新建程序组」** 创建空程序组，再把别的拖进来即可

> **身份锚定在「进程名 + 窗口标题」上**。手动建的程序组 / 程序组合记的是
> **具体哪些窗口**——拖一个 VS Code 窗口进组不会把其余 VS Code 窗口也拽进来。
> 自动折叠出来的组按进程记（语义是"这个程序的全部窗口"）。
> 标题变了（VS Code 切文件）时退回按进程匹配，不会让窗口掉出分组。
> 程序组合会记住每个成员的**可执行路径**，窗口关掉后再按组合就能重新拉起来。

## 配置文件

所有配置位于 `%APPDATA%\AltTabReplacer\`（通常是 `C:\Users\<你>\AppData\Roaming\AltTabReplacer\`）。

| 文件 | 用途 |
|---|---|
| `settings.json` | 全局配置：热键、布局尺寸、主题色、行为开关 |
| `layout.json` | 槽位布局：顺序、分组（拖动自动写入，支持热重载） |
| `rules.json` | 排除规则：哪些窗口不显示 |
| `logs\app-YYYYMMDD.log` | 每日滚动日志 |

### 默认 settings.json

```json
{
  "Hotkey": { "Modifiers": "Alt", "Key": "Tab" },
  "Layout": {
    "CellWidth": 256, "CellHeight": 144, "CellPadding": 8,
    "MaxColumns": 4, "AutoGroupThreshold": 2,
    "WidthRatio": 0.5, "HeightRatio": 0.55
  },
  "Theme": { "Accent": "#FF0078D4", "Background": "#CC202020", "CornerRadius": 8 },
  "Behavior": { "HideOnWindowChange": true, "IgnoreFullscreen": true, "PollIntervalMs": 1000 }
}
```

### 修改热键

编辑 `settings.json` 的 `Hotkey` 字段，例如改成 `F1`：

```json
{ "Hotkey": { "Modifiers": "", "Key": "F1" } }
```

或 `Ctrl + Alt + Tab`：

```json
{ "Hotkey": { "Modifiers": "Ctrl, Alt", "Key": "Tab" } }
```

保存后**重启程序**生效。

### 排除规则示例

参见 [`samples/rules.example.json`](samples/rules.example.json)，格式：

```json
[
  { "Action": "Hide", "ProcessName": "explorer" },
  { "Action": "Hide", "Title": "Cortana" }
]
```

> 注：`rules.json` 只负责**排除**，不再管排序。
> 顺序由 `layout.json` 通过拖拽自动维护（详见 [设计文档 5.6](docs/design.md)）。

## 目录结构

```
AltTabReplacer/
├── AltTabReplacer.sln
├── README.md                    本文件
├── build-and-run.ps1            一键脚本：restore + build + 提权启动
├── docs/
│   ├── design.md                详细设计文档
│   ├── groupplan.md             分组 / 组合的实现计划
│   └── 用户使用说明.md           面向用户的快速上手（含公众号宣传版）
├── publish/                     dotnet publish 产物（可执行版本）
├── samples/
│   └── rules.example.json       排除规则示例
└── src/
    ├── AltTabReplacer.Core/     服务 + 模型 + Win32 P/Invoke
    │   ├── Infrastructure/      EventBus / Logger / Win32
    │   ├── Models/              WindowInfo / SortRule / Settings / LayoutModels
    │   ├── Services/            Hotkey / Enumerator / Thumbnail / Capture /
    │   │                        Activator / RuleStore / LayoutStore /
    │   │                        LayoutResolver / SlotEditor / DropHitTest /
    │   │                        IconExtractor / ProcessPathResolver /
    │   │                        LowLevelKeyboardHook
    │   └── KeyMap.cs            16 个索引键的 VK 映射
    └── AltTabReplacer.App/      WPF 启动器 + 视图
        ├── App.xaml(.cs)        入口 + 热键响应 + 托盘 + 钩子自愈
        ├── HostWindow           隐藏的 HWND（接收 WM_HOTKEY）
        ├── SelectorWindow       主选择器（含拖拽、二级、搜索）
        ├── ConfigWindow         --config 模式的规则编辑窗口
        ├── RenameDialog         内嵌重命名对话框
        ├── ViewModels/          Selector / Slot / Config / ConfigRule
        └── app.manifest         requireAdministrator + DPI 感知
```

## 常见问题

**Q: 热键被占用怎么办？**
A: 编辑 `%APPDATA%\AltTabReplacer\settings.json`，改 `Hotkey.Modifiers` 或 `Hotkey.Key`（如 `"F1"`、`"Alt, Space"`），重启程序。

**Q: 任务管理器在前台时按 Alt+Tab 没反应？**
A: 这是 Windows 的 UIPI 安全机制。本程序已声明 `requireAdministrator`，
以管理员身份启动后可以接收提权窗口的按键。如果还是不行，确认 exe 是用管理员身份启动的。

**Q: 如何开机自启又不弹 UAC？**
A: 用「任务计划程序」创建登录时触发的任务，操作启动 `AltTabReplacer.exe`，勾选「使用最高权限运行」。普通启动文件夹 / 注册表 Run 项做不到免提示。

**Q: 某些窗口没有出现在选择器里？**
A: 当前版本默认过滤掉了 UWP / Store App（Cloaked 窗口）。如有特殊需求可通过源码修改 `WindowEnumerator` 的过滤条件。

**Q: 缩略图显示黑屏或「(no preview)」？**
A: 极少数使用 GPU 独占渲染的窗口无法被 `PrintWindow` 截取，已自动降级为占位图。

**Q: 如何彻底退出？**
A: 托盘图标右键 → 退出，或命令行：

```powershell
taskkill /im AltTabReplacer.exe /f
```

**Q: 按 `1QWE` 没反应？**
A: 确认鼠标焦点在选择器窗口内。点击选择器空白处后再按即可。
另外确认不是处在搜索模式（按了 `/` 进入搜索）——搜索模式下索引键交给文本框。

**Q: 修改 layout.json 后没生效？**
A: 程序支持热重载。如果不生效，确认 JSON 格式正确（没有逗号错误），
可在 `%APPDATA%\AltTabReplacer\logs\` 下查看错误日志。

**Q: 为什么按着 Alt 时按 Tab 不能在选择器内移动？**
A: 按着 `Alt` 时按 `Tab` 会被识别成热键，等同关闭选择器（toggle 行为）。
用 `Tab` 上下移动前要先**松开 `Alt`**。

## 路线图

| 阶段 | 状态 | 内容 |
|---|---|---|
| P1 MVP | ✅ | 热键 / 缩略图 / 16 物理键 / 鼠标点击 |
| P2 布局管理 | ✅ | 程序组 / 程序组合 / 拖拽 / 持久化 / 跨组搜索 |
| P3 体验打磨 | 🚧 | 托盘图标已完成；多显示器适配、安装包、开机自启 UI 待办 |

详见 [`docs/design.md`](docs/design.md)。

## 卸载

1. 托盘图标右键 → 退出，或 `taskkill /im AltTabReplacer.exe /f`
2. 删除 `publish/` 或 exe 文件
3. （可选）删除配置目录：`%APPDATA%\AltTabReplacer\`
4. （可选）删除「任务计划程序」中创建的自启任务

## 运行环境

- 操作系统：Windows 10 / 11
- 框架：.NET 8 Desktop Runtime
- 目标框架：`net8.0-windows`
