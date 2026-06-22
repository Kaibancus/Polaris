# GPU 渲染方案评估（优化帧率与内存）

> 分支：`代码重构`　目标：评估把 Polaris 从 **WPF 软件分层窗口** 迁移到 **GPU 合成** 的可行性、收益、代价与风险。本文只做评估与选型，不含实现。

## 1. 现状与根因

Polaris 的所有可视窗口都用 **`AllowsTransparency="True"`（WPF 分层窗口 / layered window）**：

| 窗口 | 类型 | 说明 |
|---|---|---|
| `RadialWindow` 主 dock | AllowsTransparency | 玻璃/Saturn，常被 `SizeToActiveContent` 钳到接近全屏（实测 1436×1061） |
| `SideDockWindow` 侧 dock | AllowsTransparency | 浮动异形条 |
| `DragGhostWindow` | AllowsTransparency | 拖拽残影 |
| `NotchClockWindow` | AllowsTransparency | 刘海时钟 |
| `WindowPreviewPopup` / 设置弹窗 | AllowsTransparency | 缩略图/下拉 |

**根因链**：`AllowsTransparency=True` ⇒ WPF 走 **Tier-0 纯软件渲染**，每帧把整窗位图在 CPU 端合成后用 `UpdateLayeredWindow` **整屏上传**（无脏矩形）。于是：

- **帧率**：任何像素变化（轨道光、放大波、运行呼吸灯）都触发整窗 CPU 重绘 + 整屏上传 → 实测 60–112% CPU，帧率被压在 60fps 以下。
- **内存**：占用几乎全是**非托管软件渲染表面**（托管堆仅 ~7–19MB，工作集却 400MB–1GB+）。这些 render target 在系统 RAM 中。

已做的增量优化（v2.0.x）：窗口按内容缩小、玻璃图标虚拟化、隐藏缩 1×1、空闲 trim、静止暂停轨道光/放大波。**但只要有动画在跑，整屏每帧上传不可避免——这是架构天花板。**

## 2. 为什么"GPU + 透明"在纯 WPF 里做不到

WPF 的硬件加速（Tier-2）只对**普通不透明窗口**生效。`AllowsTransparency=True` 强制 layered window，**没有任何受支持的纯 WPF 方式能同时获得 GPU 合成 + 逐像素 alpha**。要两者兼得，必须**离开 WPF 的窗口渲染模型**，改用 Windows 的现代合成栈。

关键事实：Polaris 的"玻璃磨砂"是**乳白半透明叠层**（`GlassChrome`），**并非真实采样模糊桌面**（非真 acrylic）。因此 per-pixel alpha 的真实需求只是：① 让桌面以可变 alpha 透出；② 浮动异形（圆角板 + 周围全透明可点击穿透）。这降低了对"实时背景模糊"的依赖，但**异形 per-pixel alpha** 仍是硬约束。

## 3. 候选方案对比

### 方案 A：DirectComposition + DXGI 交换链 + Direct2D/Win2D 重写（GPU 原生 per-pixel alpha）
- **做法**：无边框窗口加 `WS_EX_NOREDIRECTIONBITMAP`，建 DirectComposition device/target/visual，呈现一个**预乘 alpha 的 DXGI swapchain**；用 **Direct2D**（或 Win2D 封装）重绘所有内容。这是 WinUI/现代应用获取 GPU 透明窗口的标准做法。
- **帧率**：★★★★★ 合成移到 GPU，消除每帧 CPU 整屏上传；动画可满刷新且 CPU 近乎 0。
- **内存**：★★★★ 软件 render target（系统 RAM 数百 MB）转为 GPU 纹理（VRAM），系统工作集显著下降。
- **视觉保真**：★★★☆ D2D 有等价效果（高斯模糊、阴影、渐变）+ DirectWrite 文本，但**与 WPF 的 BlurEffect/文本渲染存在细微差异**，需逐一调校。
- **工作量**：★☆☆☆☆（**极大**）。需把 `Views/` 下 ~10,170 行、含 19 BlurEffect / 15 DropShadow / 35 BitmapCache / 17 RadialGradient / 35 Path 几何 / 19 TextBlock 的**整套绘制层用 D2D 重写**；放大波、轨道光、Saturn 环、RadialIcon 等全部重做。WPF 仅能保留布局/逻辑/配置。
- **风险**：高。WPF↔WinRT(Win2D) 互操作、DPI、命中测试、拖拽残影、多显示器、设置窗内嵌等都要重做。

### 方案 B：迁移到 WinUI 3 / Windows App SDK
- **做法**：用 WinUI 3（Composition 视觉层，GPU 加速，原生支持透明 + Acrylic/Mica）整体重写 UI。
- **帧率/内存**：★★★★★ / ★★★★ 同 A 的合成收益，且 Acrylic/Mica 由 DWM 负责。
- **视觉**：★★★☆ XAML 方言不同、**无 WPF Effects**、控件/动画体系不同，玻璃观感需重建。
- **工作量**：★☆☆☆☆（**最大，等同重写整个应用**）：XAML、控件、动画、数据绑定、托盘/Hook/窗口管理全部迁移。
- **风险**：最高。等于新项目。

### 方案 C：DWM 背景模糊 / Acrylic（`SetWindowCompositionAttribute` / `DwmEnableBlurBehind`）
- **做法**：在现有（仍 WPF）窗口上让 DWM 对**矩形窗口区域**做背景模糊/acrylic。
- **限制**：acrylic 填满**整个矩形窗口**，无法表达"圆角浮动板 + 周围全透明穿透"的异形 per-pixel alpha；且仍不解决 dock 自身动画的软件合成。
- **结论**：✗ 与当前浮动异形玻璃观感不兼容，**不能替代**，至多用于某个矩形子窗（收益有限）。

### 方案 D：放弃 per-pixel alpha，改不透明窗口 + 窗口区域（Window Region）
- **做法**：去掉 `AllowsTransparency`，用 `SetWindowRgn` 把窗口裁成圆角形状；WPF 回到 Tier-2 GPU 渲染。
- **帧率/内存**：★★★★ / ★★★★ 立刻拿到 GPU 渲染，**几乎不重写绘制层**（最低成本拿到大部分收益！）。
- **视觉**：★★☆☆ **硬伤**：窗口区域是 1-bit 掩码，**边缘锯齿、无半透明软边/外发光/投影超出板外**；玻璃"透出桌面"的半透感丢失（变不透明背板）。与"液态玻璃"招牌观感冲突。
- **风险**：中。视觉退化明显，可能不可接受。

### 方案 E：WPF 视觉树 → RenderTargetBitmap → DComp（混合）
- WPF 的 `RenderTargetBitmap` 本身是**软件光栅化**，把它再喂给 DComp 等于保留 CPU 光栅 + 多一次拷贝，**得不偿失**。✗ 反模式，排除。

## 4. 收益与代价小结

| 方案 | 帧率 | 内存 | 视觉保真 | 工作量 | 风险 |
|---|---|---|---|---|---|
| A. DComp + D2D/Win2D 重写 | ★★★★★ | ★★★★ | ★★★☆ | 极大 | 高 |
| B. WinUI 3 迁移 | ★★★★★ | ★★★★ | ★★★☆ | 最大 | 最高 |
| C. DWM Acrylic | ★☆ | ★☆ | ✗异形 | 小 | 低（但无效） |
| D. 不透明 + 窗口区域 | ★★★★ | ★★★★ | ★★☆（硬伤） | 小 | 中 |
| E. RTB→DComp | ✗ | ✗ | — | — | — |

## 5. 建议

1. **不建议**立刻全量重写（方案 A/B）：工作量数周、风险高、且玻璃观感需逐一回调，性价比对一个 dock 工具偏低。
2. **先做低成本验证（spike）**：用方案 A 的栈**只移植一个最简单窗口**（推荐 `NotchClockWindow` 或 `DragGhostWindow`，可视元素最少）到 `WS_EX_NOREDIRECTIONBITMAP + DirectComposition + Direct2D` 或 **Win2D**，实测：
   - 同等内容下 **CPU/帧率** 改善幅度；
   - **系统工作集** 下降幅度（软件 RT → VRAM）；
   - 文本/模糊的**视觉差异**是否可接受。
   - 用这份真实数据再决定是否投入主 dock 重写。
3. **若验证收益显著且视觉可接受**：按"窗口逐个迁移"推进（DragGhost → NotchClock → SideDock → 主 dock），每步可独立发布、回滚。优先用 **Win2D**（对 D2D 的 C# 友好封装），但需引入 Windows App SDK 互操作。
4. **若验证收益有限或视觉不可接受**：维持当前 WPF 架构 + 已有增量优化，接受"活跃态整屏上传"的天花板（这是当前最稳妥的工程取舍）。
5. **方案 D 作为备选**：若可接受牺牲软边/半透感换取低成本 GPU 渲染，可对**非玻璃主题**（Saturn）试点窗口区域方案。

## 6. 关键技术备注（供实现参考）

- per-pixel alpha 的 GPU 路径 = `WS_EX_NOREDIRECTIONBITMAP` + `DCompositionCreateDevice` + `IDCompositionTarget`(SetRoot) + 预乘 alpha 的 `IDXGISwapChain1`（`DXGI_ALPHA_MODE_PREMULTIPLIED`）。
- 透明区点击穿透：DComp 窗口对全透明像素天然穿透（同 layered window）。
- 文本：DirectWrite（Win2D `CanvasTextLayout`）替代 WPF `TextBlock`；需重建字体回退（Segoe UI + 微软雅黑）。
- 效果：WPF `BlurEffect`→D2D `GaussianBlur`；`DropShadowEffect`→D2D `Shadow`；`RadialGradientBrush`→D2D 径向渐变；`BitmapCache` 的"烘焙一次"语义→D2D 离屏 `ID2D1Bitmap`/command list 缓存。
- 内存预期：即便 GPU 化，per-pixel alpha 后备缓冲仍需 窗口面积×4 字节，但落在 VRAM 且无每帧 CPU 上传；系统 RAM 工作集预计从数百 MB 降到以托管堆为主的低位。
- DPI / 多显示器 / `MonitorLayout` 现有逻辑可复用（仍是窗口定位），只是绘制后端替换。

---
*结论：GPU 渲染能根治帧率瓶颈并显著降低系统内存，但代价是重写整套绘制层（或迁移 WinUI）。建议先用单窗口 spike 实测收益与视觉差异，再决定是否推进主 dock。*

## 7. Spike 实测结果（已执行，代码重构分支）

按建议执行了单窗口 spike：用 **Vortice.Windows**（D3D11/DXGI/Direct2D/DirectComposition 的 C# 封装；比 Win2D 更适合 WPF，无需 Windows App SDK）实现了 `WS_EX_NOREDIRECTIONBITMAP + DirectComposition + Direct2D` 的 GPU 逐像素 alpha 窗口。

**① 可行性 / 视觉保真（`DragGhostWindowGpu`，`POLARIS_GPU_GHOST=1`）**
把拖拽残影从 WPF 分层窗口换成 GPU 渲染（WPF `Pbgra32` 快照 → D2D 预乘位图）。实测：残影**清晰跟随光标、背景透明正常、拖到删除区正常变淡、点击穿透、无偏移/闪烁/崩溃**。→ **GPU 逐像素 alpha 管线在目标机上工作正常，视觉无损**，主 dock 重写的核心架构风险已排除。

**② CPU / 内存量化（`GpuBenchmark`，`POLARIS_GPU_BENCH=gpu|wpf`）**
拖拽残影太小（合成成本随窗口**面积**增长），故另建公平基准：**同一动画**（移动的径向渐变光斑）在 **1440×900** 逐像素 alpha 大窗口里、由同一 `CompositionTarget.Rendering` 时钟驱动、跑 6 秒，分别走 WPF(AllowsTransparency) 与 GPU(DComp+D2D) 两条合成路径，采样本进程 CPU 与工作集（3 轮均值）：

| 模式 | CPU（单核%） | 工作集 |
|---|---|---|
| WPF 软件分层（`UpdateLayeredWindow` 整屏上传） | **19.2%** | **236 MB** |
| GPU DComp + Direct2D（DWM 在 GPU 合成） | **7.5%** | **98 MB** |
| **改善** | **↓ ~61%** | **↓ ~58%（-138 MB）** |

**结论（实测支撑）**：单个大型动画透明窗口，GPU 合成把 **CPU 降约 6 成、系统内存降约 6 成**（后备缓冲移到 VRAM、消除每帧 CPU 整屏上传）。主 dock 面积更大、永久动画更多（轨道光 + 运行辉光 + 放大波），收益只会更大——这正对应它实测的 60–112% CPU 与数百 MB 占用。**GPU 渲染方向已被实测验证，值得推进主 dock 重写**；代价仍是重写整套 D2D 绘制层（见 §3-A 工作量）。

**Spike 产物（均在本分支，默认关闭、零生产影响）**：`Services/Gpu/CompositionHost.cs`、`Views/DragGhostWindowGpu.cs`（`IDragGhost` 接口 A/B 切换）、`Services/Gpu/GpuBenchmark.cs`、`Services/Gpu/GlassPrototypeWindow.cs`；Vortice 包仅在本分支引入。

**③ 玻璃材质 + 文本视觉回调（`GlassPrototypeWindow`，`POLARIS_GLASS_PROTO=1`）**
按建议②做了视觉回调原型：用 Direct2D 复刻液态玻璃 slab 的整套图层（投影、清玻璃径向体、磨砂乳白层、边缘暗角、中心高光、明亮 rim），用 **DirectWrite** 画时钟（Segoe UI + 微软雅黑）。与真实 WPF 玻璃 dock 重叠对比、按真实公式（slab 整体 `opacity = 1 - PanelTransparency`、`frostStrength = 1 - PanelTransparency`、各层 ARGB 与渐变半径逐一对齐）调校后，用户确认 **D2D 能复刻玻璃质感 + 清晰文本**（半透明通透感、磨砂、圆角、rim、文字渲染均到位）。要点经验：① 必须用与真实 slab 相同的**比例**（高大宽条）才能让径向渐变 falloff 一致（薄条会显著偏不均匀）；② 整块要乘 slab 透明度（~0.40），磨砂是**集中而非铺满**的乳白层，真实 dock 以通透为主；③ WPF `Pbgra32` ↔ D2D 预乘 BGRA 直接对应，色彩无明显偏差。**剩余像素级色彩/亮度精调属正式移植时同位置同几何的常规调校**，非 D2D 能力限制。

**下一步建议**：① 把 §3-A 的逐窗迁移正式排期（DragGhost ✅、NotchClock ✅ → SideDock → 主 dock）；② 主 dock 重写复用本 spike 的 `CompositionHost` + 玻璃绘制原型作为起点；生产投影/模糊用真正的 D2D `Shadow`/`GaussianBlur` 效果（已在 NotchClock 迁移中验证 GaussianBlur 经命令列表渲染正常、平滑无分层）；③ 文本用 DirectWrite，注意字体回退（Segoe UI + 微软雅黑 / 华文新魏）与 DPI。

## 8. 逐窗迁移进度（代码重构分支）

| 窗口 | 状态 | 说明 |
|---|---|---|
| `DragGhostWindow` | ✅ GPU 版可用（`POLARIS_GPU_GHOST=1`） | 视觉验证通过 |
| `NotchClockWindow` | ✅ GPU 版可用（`POLARIS_GPU_NOTCH=1`） | 梯形板 + 真 **GaussianBlur** 暗光晕 + DirectWrite 立体金字，用户确认与原版一致、平滑无分层 |
| `SideDockWindow` | ✅ GPU 版**已设为默认**（`POLARIS_GPU_SIDEDOCK=0` 回退 WPF） | D2D 玻璃 slab + 钉住列 + 命中测试/悬停标签 + macOS 放大波 + **运行应用条** + **交互**（点击启动、拖拽重排、拖出删除、`WM_DROPFILES` 拖入固定）+ **与原版对齐**（透明底板、柔和运行点、启动弹跳、右键菜单、悬浮缩略图）+ **host 集成**：抽出 `ISideDock` 接口（WPF/GPU 两实现互换，同 ghost/notch 的 A/B 模式）；GPU dock 可见性状态机（realize 后隐藏，main/edge/drag/pinned 四个显示原因 → DoShow/DoHide），接入 edge-poll 召唤/收起、热键、拖入 `TryAcceptDrop`、`GetDockScreenBounds`（屏幕 DIP）、双向 live-sync（GPU 改动经 `MainDockChanged` 通知主 dock，主 dock 改动经 `RefreshFromConfig` 刷新 GPU dock）、Polaris 磁贴 `ToggleDocks`。**关键 DPI 修复**：`CompositionHost` 须 `_d2d.SetDpi(96×scale)`（D2D 设备上下文 DPI 默认 96 且独立于目标位图 DPI，不设会全按 1.0x 绘制）。布局 DIP、窗口/交换链物理像素；`_winX/_winY/_winW/_winH` 与 `s.Center` 均为 DIP（窗口物理位置 = `_winX×_dpi`）。 |
| 主 dock `RadialWindow` | ⬜ 待迁移（收益最大） | |

迁移采用 `IDragGhost` / `INotchClock` 接口 + 环境变量做 A/B，默认仍走 WPF（零生产影响），验证满意后再翻默认。**NotchClock 迁移额外验证了 D2D 真高斯模糊管线**（主 dock 的 19 处 BlurEffect / 15 处 DropShadow 移植所需的关键能力）。**侧 dock Stage A** 抽出了可复用的 `Services/Gpu/GlassSlab.cs`（玻璃/暗色 slab 的 D2D 绘制，主 dock 也将复用）。

## 9. 踩坑清单 / 经验（迁移主 dock `RadialWindow` 前必读）

ghost / notch / 侧 dock 三轮迁移踩过的坑，按主题归类。主 dock 迁移直接套用即可少走弯路。

### 9.1 DPI（最容易翻车，务必先搭对）
- **`_d2d.SetDpi(96×scale)` 是头号坑**。设置目标位图（`BitmapProperties1`）的 DPI **不会**设置 D2D 设备上下文的 DPI——后者默认 96 且独立。不设会令所有 DIP 空间的绘制按 1.0x 出图：内容偏小、贴窗口左上、命中测试（1.5x）与绘制错位。修复点在 `CompositionHost`（绑定 target 后调用一次）。诊断：`_host.Context.Dpi` 应为 `(144,144)`。
- **单位纪律**：布局全程用 DIP；Win32 窗口 + DComp 交换链用**物理像素**（DIP×scale）；窗口位置 = `布局DIP × _dpi`。每个字段心里要清楚是 DIP 还是物理。侧 dock 曾因把已是 DIP 的 `_winX` 又除一次 `_dpi` 导致弹窗错位。屏幕物理点 → 窗口本地 DIP：`physical/_dpi - _winX(DIP)`。
- **DpiScale 取值用 `EnumDisplaySettings`**（物理分辨率 ÷ `SystemParameters.PrimaryScreenWidth`），与调用方 DPI 感知无关、窗口未实现时也可靠；`GetDpiForWindow/GetDpiForMonitor` 会在早期 race 回落到 96。
- `_host.Context.Dpi` 返回的是 `Size`，用 `.Width/.Height`（**不是** `.X/.Y`）。

### 9.2 Vortice / 命名冲突
- `GradientStop` 在多个命名空间存在 → 全限定 `Vortice.Direct2D1.GradientStop`。
- 同时用 WPF 与 Vortice 时，`Color` / `Colors` 在 `System.Windows.Media` 与 `Vortice.Mathematics` 间二义 → 在 WPF 代码处全限定 `System.Windows.Media.Color(s)`。

### 9.3 WPF 弹出物挂到纯 Win32 GPU 窗口上
- GPU dock 没有每图标的 WPF 视觉，无法直接作为 `PlacementTarget`。两种已验证套路：
  - **右键菜单**：用 `Popup` + `PlacementMode.Absolute` + 屏幕 DIP 偏移（Absolute 偏移就是屏幕 DIP）；先 `shell.Measure(infinity)` 拿 `DesiredSize` 再算居中。
  - **悬浮缩略图**：在悬浮图标上空泊一个**透明、点击穿透**（`WS_EX_TRANSPARENT|TOOLWINDOW|NOACTIVATE`）的 1×_gIcon 锚 WPF 窗口，作为现成 `WindowPreviewPopup` 的 `PlacementTarget`，**整套复用**（实时捕获/缓存/关闭按钮/最小化回退全白送）。GPU dock 用自己的 hover 检测驱动其 `OnPointerEnter/Leave`。

### 9.4 输入 / Win32 消息
- `WM_DROPFILES` 的 HDROP 在 **wParam**（不是 lParam）；消息处理签名要能拿到 wParam。经典 `WM_DROPFILES` 只收 `CF_HDROP`（桌面快捷方式/exe），收不到虚拟 shell item。
- 命中测试外区返回 `HTTRANSPARENT` 让空 reserve 区点击穿透；`WS_EX_NOACTIVATE` 防止点击抢焦点。

### 9.5 运行应用 图标/名称
- profile 专属 AUMID（Edge 的 `MSEdge.UserData.Profile1`）的 AppsFolder 查询会返回**非空的通用空白文档图标**，null 回退永不触发 → 对非 `\WindowsApps\` 的 Win32 路径**先取真实 exe 图标**，再退 AUMID。`packaged = 路径含 "\WindowsApps\"`。
- 运行条名称用 exe 的 `FileVersionInfo.FileDescription`（→ 文件名 → 窗口标题），别直接用窗口标题（浏览器是整页标题、终端是 tab 名）。

### 9.6 渲染节流 / 可见性
- 关键优化：dock 仅在「放大波活跃 / 弹跳中 / hover」时渲染，静止即停。**别加持续呼吸脉冲**，会破坏 idle 节流（运行点改用静态径向渐变而非脉冲）。
- 隐藏时 `Tick` 顶部 `!_shown` 早退，停止光标轮询与渲染。

### 9.7 窗口生命周期 / host 集成
- GPU 窗口在 `Rebuild` 时**销毁重建**（含每次召唤 DoShow→Rebuild）。可行但偏重；在同一同步调用里「建好再显示」可避免可见闪烁。**外部别缓存 hwnd**（Rebuild 会销毁它）——通过对象/接口驱动，靠 `s_instances` 映射回查。
- host 集成沿用 `IDragGhost/INotchClock/ISideDock` 接口 + env flag 的 A/B 模式。侧 dock 的可见性是**多原因状态机**（main/edge/drag/pinned + bounceHold/menuHold），主 dock 同理需复刻其显隐原因。给 host 的几何（如 `GetDockScreenBounds`）要返回 **DIP**（edge poll 在 DIP 下工作）。

### 9.8 开发/调试环境
- 运行中的 `Polaris.exe` 会锁定输出 exe，构建前须先停进程；**不能** `Stop-Process -Name`，须先取 PID 再 `Stop-Process -Id`。错误日志在 `%AppData%\Polaris\errors.log`。
- 截图脚本须 `SetProcessDPIAware()` 才能拿到真实物理分辨率；用 `Start-Process -WindowStyle Hidden -File`（执行策略 + 隐藏避免截图进程自己进运行条）。
- **坑**：`IsWindowVisible` 查一个已被 Rebuild 销毁的旧句柄会假阴性——验证显隐要重新 `FindWindow` 或直接看截图。
- 拖拽测试须用非提权 shell 启动（或经 explorer.exe），否则 UIPI 完整性不匹配导致拖入失败。

### 9.9 主 dock `RadialWindow` 迁移特有提示
- 体量最大（19 BlurEffect + 15 DropShadow），常为全屏 layered 窗口——D2D 真高斯模糊管线（NotchClock 已验证）+ `GlassSlab.cs` 可复用。
- 其 notch（`POLARIS_GPU_NOTCH`）与拖拽 ghost（`POLARIS_GPU_GHOST`）已迁移；剩径向图标环本体、轨道光、运行光晕。
- 主 dock 同样要走接口 + env flag + 多原因显隐状态机；几何返回 DIP；沿用本清单全部 DPI/弹窗/节流经验。
## 10. 独立渲染线程方案（已实施，藏在 `POLARIS_GPU_RENDERTHREAD=1` 开关后、默认关）

> 状态：**已实施（v1.7.x，默认关）**。两 dock（`MainDockWindowGpu` + `SideDockWindowGpu`）已迁移到专用渲染线程，
> 由 DXGI 帧延迟可等待对象（`IDXGISwapChain2.FrameLatencyWaitableObject` + `MaximumFrameLatency=1`）按显示器刷新率自适应节拍。
> 本机（59Hz）实测：召唤/消散/悬停放大/拖拽/双主题（液态玻璃 + 土星）全部正常，10 轮快速召唤/消散压测无崩溃、无 errors.log、
> 私有内存平稳（186→192MB）、线程数稳定（渲染线程复用、无泄漏）。真正的大赢家仍是真实高刷硬件（120/144Hz → ~2-2.4×），
> 待用户在高刷屏上 A/B 验证稳定后再议是否默认开启。开关关时走原 `FrameClock`（`CompositionTarget.Rendering`）路径，零生产影响。

### 10.0 结论先行
- 技术可行，是突破本机 ~38-40fps（`CompositionTarget.Rendering`/WPF MediaContext 时钟上限）、吃满高刷屏的**唯一**途径。
- 成本不在"开线程调 Render"，而在**三套线程亲和 API（Win32 HWND / DirectComposition / D3D-D2D）的纪律** + **`Tick` 与 `Render` 间共享可变状态的竞态消除**。
- **最终落地的线程模型**（比设计初稿更简洁）：渲染线程**独占** CompositionHost/设备 + `_slots`/几何/动画相位/波形（全部只在渲染线程或 loop 静止期改写）；
  UI 线程只持有 HWND/WndProc/拖放 shim/WPF 弹窗。设备生命周期（建/销/Resize/SetIntro/Saturn 缓存）一律 `Post`/`Invoke` 到渲染线程；
  Rebuild/Dispose 用 `Invoke` 屏障保证「设备先销毁、HWND 后销毁」且渲染线程不在帧中；UI↔渲染只剩**少量交互标量**（拖拽点、滚动目标、
  召唤/淡入淡出相位、launch/bounce 标记）用单个 `_stateLock` 守护——Tick 整帧持锁（vblank 等待在锁外），UI 写标量只持锁极短，互等 ≤1 帧。
  渲染线程内对 UI-only 操作（`EnsureShimTopmost`/`DrivePreview`/`OnDismissComplete` 的 `SW_HIDE`）一律 `Dispatcher.BeginInvoke` 回 UI，
  且预览按 hover 变化节流回 UI，避免泛洪。
- 内存**不降**（+1 线程栈/dock）；降内存的功劳已由 GlassSlab 泄漏修复拿到。实测私有内存与 FrameClock 路径持平。
- 基础设施：`Services/Gpu/CompositionHost.cs`（新增可等待交换链：`waitable` 构造参数 + `WaitForVBlank()` + ResizeBuffers 保留 flag）、
  `Services/Gpu/RenderLoop.cs`（专用后台线程：命令队列 + 可等待节拍 + 活跃/空闲门 + 干净 Stop/Join）。

### 10.1 范围（重要：只 2 个窗口）
- 需渲染线程的只有 **`MainDockWindowGpu` + `SideDockWindowGpu`**（交互式 60fps）。
- `NotchClockWindowGpu` 是 **1 秒 DispatcherTimer**（1fps，无关）；`DragGhostWindowGpu` 用临时 `_host.Render`；GlassPrototype/GpuBenchmark 仅 dev 标志。这些**不动**。


### 10.2 真正的难点
**(a) 线程亲和铁律**（违反 = 崩溃/设备移除/静默损坏）：
- **Win32 HWND**（`EnsureShimTopmost`、`ShowWindow/SW_HIDE`、消息泵）**必须留 UI 线程**。
- **DirectComposition**（`CompositionHost.cs` `CreateTargetForHwnd`、`SetIntro`→`_dcomp.Commit()`）非自由线程，Commit 必须串行、单线程拥有。
- **D3D11/D2D**：`_d2dFactory` 是 `SingleThreaded`（`CompositionHost.cs:116`），immediate context 非线程安全；跨线程用 = `DXGI_ERROR_DEVICE_REMOVED`。

**(b) 共享可变状态面（最大成本）**：`Tick()`（`MainDockWindowGpu.cs:670-908`）每帧在 UI 线程写、`Render()`（末尾 `_host.Present()` @1123）读约 30 个字段：
- 波形 `_waveCur[]`/`_waveOffX/Y[]`/`_slots`（`_slots` 会被 `Rebuild()` 整体替换）；
- 动画 `_summon`/`_fadeOpacity`/`_spinAngle`/`_innerAngle`/`_outerAngle`/`_saturnTime`/`_orbitAngle`/`_runSweep`/`_runPulse`/`_gearAngle`/`_gearScale`/`_badgePulse`/`_glassScroll`；
- 交互 `_dragging`/`_dragX/Y`/`_pressIdx`/`_extDragPt`/`_labelIdx`/`_labelOp`/`_flashKeys`。
- → 必须引入**不可变快照**；尤其 `_slots` 须深拷贝，否则 `Rebuild` 替换列表时渲染线程枚举它会崩。
- 先例：`PollAttention`（`MainDockWindowGpu.cs:914`）已用 `Task.Run` 算 `_flashKeys` 回填字段。

### 10.3 推荐架构：生产者(UI)/消费者(渲染线程)
```
UI 线程 (Tick，由轻量计时器或保留 FrameClock 驱动)：
  ├─ GetCursorPos + 波形/动画数学（CPU，便宜）
  ├─ Win32 HWND 操作（EnsureShimTopmost / SW_HIDE）  ← 必须留这
  ├─ 产出 FrameState 不可变快照 → 原子发布到 per-dock 槽位
  └─（不再直接画）
渲染线程 (拥有 2 个 CompositionHost 的 GPU)：
  loop: 等 vblank → 取每 dock 最新快照(若 dirty) → BeginDraw/画/EndDraw → Present(1)
        DComp Commit(淡入淡出/偏移)也在本线程
```
- DComp 全部搬渲染线程独占；UI 线程不再碰 DComp；**淡出结束需"渲染线程→UI 线程"发信号触发 `SW_HIDE`**。
- 一条渲染线程服务 2 窗口即可（同屏共享 vblank，串行 Present 开销可接受）。
- 节拍优先 **`DCompositionWaitForCompositorClock`**（一次等待覆盖全部窗口）；次选 per-swapchain `FRAME_LATENCY_WAITABLE_OBJECT`（建交换链时加 flag，`CompositionHost` 的 `SwapChainDescription1.Flags`）。

### 10.4 分阶段实施（可回退）
| 阶段 | 内容 | 风险 | 验证 |
|---|---|---|---|
| **P0** | 加 `POLARIS_GPU_RENDERTHREAD=1` 开关，默认关；FrameClock 留回退 | 极低 | — |
| **P1** | **快照重构**：定义 `FrameState`(record)，`Render(in FrameState)` 改吃参数不读字段；`Tick` 末尾产快照。**仍 UI 线程单线程跑** | 低 | 行为应与现状一致 |
| **P2** | `CompositionHost` 线程安全化：D2D 工厂改 `MultiThreaded`（或确保只渲染线程碰 D2D）；DComp Commit 归渲染线程 | 中 | 仍单线程功能不变 |
| **P3** | 加 RenderLoop 线程：UI 产快照(三缓冲+dirty，无锁)，渲染线程等 vblank→画→Present | 高 | fps 表 + 稳定性 |
| **P4** | 生命周期协调：Realize/Resize/Dispose/show-hide 跨线程编排（见 10.5） | 高 | 1000× 召唤/消散/缩放/换主题循环 |
| **P5** | 空闲门控：无新快照时渲染线程休眠（沿用现有 settle gate，`Tick` 末尾的 render gate @904-907） | 中 | 空闲 CPU/功耗 |
| **P6** | 验证：本机 Surface Laptop 6/Arc iGPU(~59)、真高刷机(120/144)、内存持平、退出干净、A/B 对比回退 | — | — |

**P1 先单独落地并验证**——降 UI/渲染耦合，即使不上线程也有价值，是最安全的地基。

### 10.5 风险项（按严重度）
| # | 风险 | 严重度 | 缓解 |
|---|---|---|---|
| 1 | 30 字段数据竞争（撕裂帧、`_slots` 枚举崩溃） | 🔴 | 不可变快照 + 原子发布；slot 列表深拷贝 |
| 2 | D3D/D2D/DComp 跨线程亲和违规 → 设备移除/崩溃 | 🔴 | 渲染线程独占 GPU；D2D 工厂 MultiThreaded |
| 3 | Resize（dock 增减图标）跨线程 | 🔴 | 暂停渲染→Resize→恢复；不在帧中途 Resize |
| 4 | 拆卸顺序（退出/消散先停线程再放设备） | 🔴 | Dispose 先 signal+join 渲染线程再 release |
| 5 | 淡出完成→`SW_HIDE` 跨线程信号丢失 → dock 卡住不隐藏 | 🟠 | 渲染线程发完成事件，UI 线程 marshal 执行 |
| 6 | 拖拽 shim z-order（UI `EnsureShimTopmost` vs 渲染线程 DComp commit） | 🟠 | shim 仍 UI 线程管；快照带 z 意图 |
| 7 | DPI 变更在 UI 线程到达，渲染线程用旧 scale 重建目标 | 🟠 | DPI 变更经快照/暂停-重建目标 |
| 8 | 复杂度/可维护性：未来每个特性都得守快照边界 | 🟠 | 文档化边界；P1 固化规则 |
| 9 | 功耗/发热：满刷新率比 38fps 更耗 | 🟡 | 空闲门控停渲染线程 |

### 10.6 可能遇到的具体 BUG
- `0x887A0005 DXGI_ERROR_DEVICE_REMOVED`：Resize/换主题时设备被两线程同时碰。
- 枚举 `_slots` 崩溃：`Rebuild()`(UI) 替换列表时渲染线程正迭代——唯有快照深拷贝可防。
- 放大波撕裂/闪烁：读到半更新的 `_waveCur/_waveOffX`——需原子换快照。
- dock 永不隐藏："淡出完成→SW_HIDE"信号跨线程丢失。
- 退出挂死：渲染线程阻塞在 Present 未 join。
- 首帧黑屏：Present 早于第一个快照产出。
- 死锁：渲染线程持锁时 UI 线程在 Dispose/Resize 等它。
- DComp Commit 跑错线程 → `E_INVALIDARG`/静默失效 → 淡入淡出/滑入不动。
- DPI 错配：监视器迁移后渲染线程用旧 scale 重建目标 → 整体放大/缩小。

### 10.7 优缺点与建议
- **优点**：解锁真实刷新率（本机集成 Arc iGPU 稳 59；高刷 120/144，~2-2.4×）；渲染脱离 UI 线程鼠标/消息抢占，节拍更稳更顺。
- **缺点**：跨 2 窗口 + CompositionHost 的大改；一整类难复现并发 BUG；内存不降；本机收益小；对日用常驻工具稳定性有实质风险。
- **工作量**：P1 ~1-2 天；P2-P5 ~3-5 天 + 长稳测试。
- **建议**：无条件先做 P1（快照重构，低风险、单独有价值）；线程化默认关、env 开关；先真高刷机 A/B 验证确有收益且稳定再议默认开启；本机若只 59Hz，性价比不足可不上。
