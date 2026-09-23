# 设置界面改造 — 实施计划

> 目标：在主选择器（SelectorWindow）的标题栏加入设置按钮；点击打开新的 SettingsWindow，
> 采用左右列表 UI（左侧分类导航 + 右侧详情面板），左侧第一项为「主题」，
> 右侧第一项为主题设置下拉框（跟随系统主题 / 浅色 / 深色）。

---

## 1. 现状分析

- `SelectorWindow` 当前 `WindowStyle=None`、`AllowsTransparency=True`，**没有可见标题栏**。
  整体界面是单层自定义绘制（半透明深色背景 + 圆角 + 内部网格）。
- 配色为硬编码 `#FF0078D4` / `#FF202020` / `#FFDDDDDD` 等，全部内联在 XAML 触发器里。
- 已有 `ConfigWindow`（--config 参数唤起，规则编辑器），保持独立，本次不动。
- `Settings.Theme` 当前只有 `Accent / Background / CornerRadius` 三个静态字段，没有
  「主题模式」概念；不存在深色/浅色资源字典切换机制。
- 项目使用 `Microsoft.Win32.SystemEvents` 可用于监听系统主题切换，但当前未使用。

---

## 2. 设计要点

### 2.1 标题栏

SelectorWindow 顶部加一个高度约 36px 的自定义标题栏 `Border`（DockPanel.Dock="Top"）：

```
┌──────────────────────────────────────────────────┐
│  AltTabReplacer                              [⚙] │  ← 标题栏
├──────────────────────────────────────────────────┤
│  （现有网格 + 预览区）                              │
└──────────────────────────────────────────────────┘
```

- 背景略浅于主面板（`#FF2A2A2A`），用 `BorderThickness="0,0,0,1"` + `BorderBrush=#FF1A1A1A` 划分。
- 左侧 `TextBlock`：标题文字 `AltTabReplacer`，字号 12，前景色 `#FFAAAAAA`。
- 右侧 `Button`：齿轮字符（`Segoe MDL2 Assets` 的 `\uE713`），32×32，无背景，悬停时浅色背景。
- 标题栏**不处理拖动**——选择器不需要被拖动。
- 与原 Grid 的间距：把现在的 `Grid Margin="12"` 调整为标题栏下面留出 margin 即可，不影响现有内容布局。

### 2.2 SettingsWindow

独立 WPF 窗口（非透明、非全屏），约 `720 × 500`，样式与现有 ConfigWindow 视觉一致（深色背景）。

```
┌────────────────────────────────────────────────────────────┐
│  AltTabReplacer — 设置                                  [×]│
├──────────────┬─────────────────────────────────────────────┤
│ 🎨 主题      │  主题                                         │
│ ⌨ 快捷键     │  ┌───────────────────────────────────────┐  │
│ 📐 布局      │  │ 主题模式    [跟随系统主题 ▼]           │  │
│ ⚙ 行为      │  └───────────────────────────────────────┘  │
│ 📋 规则      │                                             │
│              │  ...                                        │
└──────────────┴─────────────────────────────────────────────┘
```

- **左列**：`ListBox`，宽 ~180px，背景 `#FF1A1A1A`，选中项 `Background=#FF0078D4`。
  5 个分类（先做前 3 项详情）：
  - 主题（Theme）
  - 快捷键（Hotkey）
  - 布局（Layout）
  - 行为（Behavior）
  - 规则（Rules） — 点击时直接打开 ConfigWindow
- **右列**：`ContentControl` + `DataTemplate` 切换；当前选中分类决定显示哪个面板。
- **主题面板**：
  - 第一行：`主题模式` + `ComboBox`（跟随系统主题 / 浅色 / 深色）
  - ComboBox 双向绑定到 `Settings.Theme.Mode`，修改立即生效 + 落盘 + 切换资源字典
- **深色 / 浅色两套资源**：`Themes/Dark.xaml` 与 `Themes/Light.xaml`，通过 `Application.Resources.MergedDictionaries` 切换。
- SettingsWindow 自身使用主题资源（窗口背景、文字色），所以切换主题后 SettingsWindow 立刻可见效果。

### 2.3 主题模式枚举

```csharp
public enum ThemeMode { System, Light, Dark }
```

- `System`（跟随系统）：监听 `Microsoft.Win32.SystemEvents.UserPreferenceChanged`，根据 `UISettings.ColorValues` 决定显示深色还是浅色。
- `Light` / `Dark`：强制使用对应主题。
- 默认 `System`，写入 `ThemeConfig.Mode`（旧字段保留，不破坏现有 settings.json）。

---

## 3. 实施步骤

### Step 1 — `Settings.Theme` 增加 Mode 字段
- 文件：`src/AltTabReplacer.Core/Models/Settings.cs`
- 加 `ThemeMode` 枚举（放在 `Settings` 同文件或独立 `ThemeMode.cs`，放同文件）
- `ThemeConfig` 加 `Mode` 属性，默认 `System`
- 向后兼容：`SettingsLoader.Load` 不需要改（JSON 反序列化自动填默认）

### Step 2 — 创建主题资源字典
- 新建 `src/AltTabReplacer.App/Themes/Dark.xaml`
  - 资源：`WindowBackground` `#E6202020`，`PanelBackground` `#FF1C1C1C`，
    `PanelBackgroundAlt` `#FF252525`，`TextForeground` `#FFDDDDDD`，
    `TextForegroundDim` `#FF999999`，`AccentBrush` `#FF0078D4`，
    `BorderBrush` `#FF333333`，`DropTargetBackground` `#FF13314A`，
    `SelectedBackground` `#FF0078D4` 等
- 新建 `src/AltTabReplacer.App/Themes/Light.xaml`
  - 浅色对应值：`WindowBackground` `#F2FFFFFF`，`PanelBackground` `#FFFFFFFF`，
    `TextForeground` `#FF202020`，`AccentBrush` `#FF0078D4` 等

### Step 3 — 主题管理器
- 新建 `src/AltTabReplacer.App/ThemeManager.cs`（App 项目内）
- 静态类：
  - `public static void ApplyTheme(ThemeMode mode)`：合并/替换 MergedDictionaries
  - `public static ThemeMode EffectiveMode`：`System` 时实时读系统值
  - 监听 `SystemEvents.UserPreferenceChanged`，`System` 模式下自动重新应用
- 调用时机：
  - `App.OnStartup` 加载完设置后立刻 `ApplyTheme(settings.Theme.Mode)`
  - SettingsViewModel 修改 Mode 时调用

### Step 4 — SettingsWindow + ViewModel
- 新建 `src/AltTabReplacer.App/SettingsWindow.xaml` + `.cs`
- 新建 `src/AltTabReplacer.App/ViewModels/SettingsViewModel.cs`
- VM 暴露：
  - `ObservableCollection<SettingsCategory>` 分类列表
  - `SettingsCategory SelectedCategory`
  - 直接持有 `Settings _settings` 引用（修改即时生效）
- 主题面板 ComboBox：
  - 三项 `ThemeMode.System / .Light / .Dark`，显示文本走 `IValueConverter`
  - SelectedItem 双向绑定到 `Settings.Theme.Mode`
  - 修改时调用 `SettingsLoader.Save(...)` 和 `ThemeManager.ApplyTheme(...)`

### Step 5 — App 装配
- 文件：`src/AltTabReplacer.App/App.xaml.cs`
- `RunBackgroundMode` 末尾：
  - 调用 `ThemeManager.ApplyTheme(_settings.Theme.Mode)`
  - 创建 `SelectorWindow` 时订阅新事件 `SettingsRequested`
  - 事件触发：`var win = new SettingsWindow(_settings, ...); win.Show()`（单例：已开则 `Activate()`）
- `OnStartup` 中 `App.xaml` 全局资源初始化时合并 Dark.xaml 占位（保证 XAML 解析时不缺资源）

### Step 6 — SelectorWindow 加标题栏
- 文件：`src/AltTabReplacer.App/SelectorWindow.xaml`
- PART_Grid 内、外层 Border 上面加 DockPanel.Dock="Top" Border 作为标题栏
- 现有 PART_Root 的 `Grid Margin="12"` 改为 `Margin="0,0,12,12"`（让标题栏满宽）
- 齿轮按钮 `PART_SettingsBtn`，`Click="OnSettingsClick"`
- 文件：`src/AltTabReplacer.App/SelectorWindow.xaml.cs`
- `public event Action? SettingsRequested`
- `private void OnSettingsClick(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke();`

### Step 7 — 编译验证
- `dotnet build -c Debug` 通过
- 检查 `TreatWarningsAsErrors=true` 不破坏

---

## 4. 范围控制（明确不做）

- **不改 SelectorWindow 的深色配色**：本次只新建设置窗口本身的主题切换。
  把所有 `#FFxxxxxx` 改为 `DynamicResource` 是大工程，且会破坏现有视觉效果，单独走后续 PR。
- **不改 ConfigWindow 配色**：同上。
- **不做完整主题色板（强调色 / 危险色等自定义）**：只做基础 WindowBackground / Text / Accent 几个 token。
- **快捷键 / 布局 / 行为面板**：先放占位说明「在 SettingsViewModel 里扩展」，保证左侧导航完整、点击不崩。设置面板按需后续实现。

---

## 6. 验收

- [ ] 按热键唤起选择器，标题栏右上角有齿轮按钮
- [ ] 点击齿轮弹出设置窗口，左列导航 5 项，第一项「主题」默认选中
- [ ] 主题面板第一行是「主题模式」下拉框，三项可选
- [ ] 切换后设置窗口的窗口背景和文字颜色立刻变化（深色/浅色）
- [ ] 选择「跟随系统主题」时，修改 Windows 主题后设置窗口自动跟随
- [ ] 关闭再打开程序，主题选择保持
- [ ] `dotnet build` 通过