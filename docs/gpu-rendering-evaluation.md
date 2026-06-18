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
