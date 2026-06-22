# Polaris 工程日志（BUG 与优化记录）

本日志记录项目所有 **BUG 及其修复方案**、**性能优化**、**功能优化**，以及重要的
**调查结论 / 已知限制**。纯 UI 系数调节（间距、缩放、透明度、字体、颜色等视觉微调）、
文档/README、版本号、发布脚本等不在此记录。

## 维护约定
- 后续所有 BUG（含原因与解决方法）、性能优化、功能优化都追加到本日志。
- 每条尽量标注关联 commit（短 hash），便于回溯。
- 新条目加到对应小节的**顶部**（时间倒序，最新在上）。
- BUG 条目应包含：**现象 → 根因 → 修复方案**。
- 即使实验被回滚、没有 commit，只要是有价值的调查结论或已知限制，也记录到
  「调查结论 / 已知限制」一节，避免未来重复试错。

---

## ⚠️ 调查结论 / 已知限制

### DWM 缩略图预览填充：必须「整窗渲染 + DwmQueryThumbnailSourceSize 比例」才能无黑边
- **背景**：悬停预览用 DWM Thumbnail，缩略图四周/单边出现黑带。DWM 始终**保持源比例居中**（不拉伸），
  所以单边黑带 = tile 比例 ≠ 实际渲染内容比例。
- **三种组合实测**：
  | 渲染模式 | tile 高度比例来源 | 结果 |
  |---------|------------------|------|
  | 客户区(`fSourceClientAreaOnly=true`) | `GetClientRect` | ❌ 新 Outlook 等 WebView2/UWP 壳应用顶层 HWND 客户区是空壳（实测 `217x29`）→ tile 被压扁 |
  | 客户区 | `SourceSize`(整窗) | ❌ 整窗含标题栏，客户区更短 → 顶部留标题栏高度黑带 |
  | **整窗(`false`) + `SourceSize`** | `SourceSize` | ✅ 两者同源（同一整窗测量），数学上精确填满，无单边黑带（Win11 隐形边框极薄可忽略） |
- **结论**：`GetClientRect` 对 WebView2/UWP 壳应用不可靠；`DwmQueryThumbnailSourceSize` 是 DWM 自己的可靠测量。
  唯一对所有窗口都精确填满的组合是**整窗渲染 + SourceSize 比例**。`DwmUpdateThumbnailProperties` 的目标矩形用
  floor(左上)/ceil(右下) 取整以确保完全覆盖（`Math.Round` 会差半像素露出底色黑线）。
- **已知限制**：DWM 缩略图是合成在目标矩形**之上的不透明覆盖层**，不在 WPF 视觉树，无法被 WPF 圆角裁剪；
  任何要盖在其上的元素（关闭按钮/标题）必须放在缩略图**之外**（如顶部 header）或独立更高层 HWND。

### GPU Dock 帧率上限 ~38-40fps 的根因 = UI 线程定时器 / WPF 渲染时钟；60fps 需独立渲染线程
- **背景**：本机为真实 **Surface Laptop 6 for Business**（Intel Core Ultra 7 165H + 集成 **Intel Arc** iGPU，59Hz 显示，平衡电源）。注：`HypervisorPresent=True` 是开启 Windows **VBS/HVCI** 安全特性所致，**非来宾虚拟机**（早期曾误判为「VM/虚拟 GPU」，此处更正）。GPU Dock 单帧 draw 仅 4-5ms、`Present(1)` 仅 0.1ms
  （DComp flip 交换链 2 缓冲，低于刷新率时队列不满故不阻塞、不起节流作用），帧预算充裕但帧率仍只
  ~38fps。瓶颈在「谁以多高频率调用 Render」，而非 GPU 绘制本身。
- **逐方案实测**（`POLARIS_GPUFPS=1`，含 worst_gap 与内存列）：
  | 渲染驱动 | 帧率 | 帧间最差 gap | 结论 |
  |---------|------|-------------|------|
  | DispatcherTimer(16ms) | ~32fps | 78ms | WM_TIMER ~15.6ms 粒度，16ms 别名成 ~31ms |
  | + timeBeginPeriod(1) | ~30-35fps | 47-78ms | **timeBeginPeriod 对 WM_TIMER 无效** |
  | + DispatcherPriority.Render | ~30-40fps | 47ms | 抬优先级躲开鼠标 Input 饿死，仍卡 ~47ms=3×15.6ms |
  | FrameClock(CompositionTarget.Rendering) | ~38fps（修内存泄漏后 44-53fps） | — | vsync 对齐，最平滑 |
- **根因**：①`DispatcherTimer` 由 `WM_TIMER` 驱动，分辨率被系统 ~15.6ms tick 钳制，`timeBeginPeriod(1)`
  只影响 Sleep/多媒体/可等待定时器、**不影响 WM_TIMER**；②`CompositionTarget.Rendering` 走 WPF
  `MediaContext` 渲染时钟，未对我们的独立 DComp 窗口稳定锁到显示 vblank；叠加笔记本平衡电源下 iGPU
  动态降频，cadence 不均（实测秒级 25→59 抖动，非显卡算力不足）。两者都在 UI 线程，且与鼠标消息泵竞争。
- **其他应用如何做到 60fps/刷新率**：独立渲染线程 + DXGI 可等待交换链
  （`FRAME_LATENCY_WAITABLE_OBJECT`+`WaitForSingleObject`）或 `DCompositionWaitForCompositorClock`，
  或专用线程上真正阻塞的 `Present(1)`，把节拍对齐到 GPU/显示 vblank，脱离 UI 线程与 WPF 时钟。
- **结论**：UI 线程任何定时器方案在本机封顶 ~38-40fps；修复 GlassSlab 内存泄漏（去掉每帧原生纹理
  churn）后 FrameClock 已升到 44-53fps、**已逼近本机 59Hz 上限**，故本机再上 60fps 边际收益很小；要在
  高刷机达屏幕刷新率仍需「独立渲染线程 + 可等待交换链」（方案③，较大改造，待评估）。
- **命令冻结排查附记**：本环境同步 `dotnet build` 偶发被中断（exe 不更新且无残留进程），实测给足
  等待时间的同步增量构建（~2-3s）可稳定落盘；`edit/create` 改动与 `git commit/push` 均正常持久化。

### 液态玻璃主 Dock 帧率瓶颈 = 轨道光大面积半透明 blend（软件渲染固有成本）
- **背景**：玻璃主 Dock 是 `AllowsTransparency=True` 的分层窗口（`UpdateLayeredWindow`
  软件逐像素 alpha 合成，无脏矩形）。实测空闲仅 ~10 FPS、悬停 ~14 FPS。
- **调查方法**：`POLARIS_FPS=1` 开启 `FpsProfiler`，逐项实验对比 `GlassIdle` 场景帧率。
- **结论（经 6 次实测排除）**：瓶颈是**轨道光（`GlassOrbitLight`）那个 ~1500px 的半透明
  渐变发光层每帧在整个 slab 面积上 alpha-blend**，这是软件渲染管线的固有成本：
  | 实验 | 空闲 FPS | 结论 |
  |------|---------|------|
  | 基线（轨道光开） | ~10 | — |
  | **禁用轨道光** | **~21** | 轨道光是瓶颈，吃掉约一半帧预算 |
  | 缩小窗口面积 -23% | ~10 | 窗口面积无关 |
  | lamp BitmapCache `RenderAtScale` 0.5→0.25 | ~10 | 缓存分辨率无关 |
  | 轨道旋转帧率 30→15fps | ~10 | 旋转频率无关 |
  | 旋转从父 Canvas 移到 lamp 自身 | ~10 | 缓存结构无关 |
  | 移除圆角 clip | ~9（更慢） | clip 无关 |
- **含义**：「缩窗口面积 / 拆分动画到独立窗口」对玻璃帧率**无效**；`BitmapCache`/降帧/
  改旋转结构都无法降低 blend 成本。要提升玻璃空闲帧率**只能牺牲视觉**（缩小 glow 覆盖
  范围、降低不透明度，或默认禁用轨道光——低性能模式已禁用它）。
- **保留产出**：新增 `GlassIdle` profiling 场景标记（与 `SaturnIdle` 对称），便于未来用
  `POLARIS_FPS=1` 单独读玻璃空闲帧率。(commit `c3d89d1`)

---

## ⚡ 性能优化 / 健壮性

- **GPU 双 dock 帧率达刷新率 + 消除 GC 卡顿（渲染线程改默认 + GC 低延迟 + 火焰零分配 + 绘制序缓存）**：在「独立渲染线程」基础设施（见下条）之上，本轮把它**改为默认开启**并修掉稳态周期性卡顿，使两 dock 稳定贴合显示刷新率。
  - **①渲染线程默认开**：`MainDockWindowGpu` + `SideDockWindowGpu` 的 `UseRenderThread` 由 `== "1"`（默认关）改为 `!= "0"`（默认开，仅 `POLARIS_GPU_RENDERTHREAD=0` 退回旧 FrameClock 路径），与 `POLARIS_GPU_MAINDOCK=0` 等退回开关惯例一致。实测渲染线程路径稳态 59-60fps / 32ms，UI 线程 FrameClock 封顶 ~38-53fps。
  - **②GC SustainedLowLatency（消除 gen2 卡顿）**：诊断（`POLARIS_GPUFPS`）发现 GC 堆仅 2-17MB，但 80s 交互内触发 ~26 次 gen2，且**每次 worst-gap 尖峰（203-328ms）都精确落在一次 gen2 上**——每帧画刷/渐变/几何 COM 对象 churn 把对象提升到 gen2，触发频繁阻塞式 gen2 回收。修复：新增 `Services/Gpu/RenderGcScope.cs`（引用计数协调器），任一 dock 活跃渲染时把 `GCSettings.LatencyMode` 设为 `SustainedLowLatency` **推迟** gen2，最后一个 dock 空闲时还原（隐藏托盘态仍正常回收）。`StartDriver`/`StopDriver` 各 Enter/Leave 一次、`_gcActive` 配平。实测稳态悬停 gen2 26→11，长段保持 60fps/32ms 平直。
  - **③土星火焰零分配羽化**：原「slab+火焰几何并集（`CombineWithGeometry` Union）单次填充」虽修了重叠双重变暗，但**每帧分配 ~6 个原生 COM 几何对象**，侧 dock 火焰悬停时引爆 gen2（②尖峰来源之一）。改为 slab 与火焰各自**以不透明 alpha 255 画入命令列表**（不透明叠不透明=无 `SourceOver` 双重变暗），面板透明度在合成时**经 opacity 图层（`LayerParameters1` 结构体、零 GC 分配）一次性施加**。既保持同一 alpha、重叠不变深，又消除每帧几何 churn。
  - **④绘制顺序缓存**：两 dock 的 `Render` 原本每帧 `new int[_slots.Count]` + `Array.Sort(…, 闭包)` 排序放大序。改为复用 `_orderBuf`（长度变化才重建）+ 缓存 `_orderCmp` 比较器，消除每帧数组 + lambda 两处小分配。
  - **实测结论**：稳态（悬停/放大/拖拽）稳定 59-60fps、32ms 帧间，gen2 大幅减少；残留尖峰仅来自主题切换/召唤的一次性 GPU 资源重建（私有内存 930-945MB 跳变），非稳态问题。

- **独立渲染线程（GPU 双 dock；藏在 `POLARIS_GPU_RENDERTHREAD`，初期默认关、现已默认开，见本节顶部条目）**：GPU dock 原本由 `FrameClock`（`CompositionTarget.Rendering`）在 **UI 线程**驱动渲染，被 WPF MediaContext 时钟封顶在本机 ~38-53fps；高刷机（4K@144）UI 线程更被渲染+输入争用拖垮（点击/右键/激活延迟）。**根因**：渲染跑在 UI 线程，且 WPF 时钟不锁 DComp vblank。**优化**：为 `MainDockWindowGpu` + `SideDockWindowGpu` 各起一条专用渲染线程，按 **DXGI 帧延迟可等待对象**（`IDXGISwapChain2.FrameLatencyWaitableObject` + `MaximumFrameLatency=1`，`SwapChainFlags.FrameLatencyWaitableObject`）自适应显示器刷新率（60/120/144Hz）节拍，并把 UI 线程从渲染中解放。**线程模型**：渲染线程独占 CompositionHost/D3D-D2D-DComp 设备 + `_slots`/几何/动画相位/波形（只在渲染线程或 loop 静止期改写）；UI 线程只持 HWND/WndProc/拖放 shim/WPF 弹窗。设备生命周期（建/销/`Resize`/`SetIntro`/Saturn 缓存）一律 `Post`/`Invoke` 到渲染线程；Rebuild/Dispose 用 `Invoke` 屏障保证「设备先销毁、HWND 后销毁」且渲染线程不在帧中（满足 §10.2(a) 三套线程亲和 API 纪律）；UI↔渲染仅剩少量交互标量（拖拽点、滚动目标、召唤/淡入淡出/launch/bounce 相位）用单个 `_stateLock` 守护（Tick 整帧持锁、vblank 等待在锁外，UI 写标量极短，互等 ≤1 帧，无死锁）；Tick 内 UI-only 操作（`EnsureShimTopmost`/`DrivePreview`/`OnDismissComplete` 的 `SW_HIDE`）`Dispatcher.BeginInvoke` 回 UI，预览按 hover 变化节流避免泛洪。**基础设施**：`Services/Gpu/CompositionHost.cs`（新增可等待交换链：`waitable` 构造参数 + `WaitForVBlank()`，`ResizeBuffers` 保留 flag）、新 `Services/Gpu/RenderLoop.cs`（命令队列 + 可等待节拍 + 活跃/空闲门 + 干净 Stop/Join）。**实测**（59Hz Surface Laptop 6，flag 开）：液态玻璃 + 土星双主题召唤/消散/悬停放大/拖拽全部正常，10 轮快速召唤/消散 + 边缘轮询压测无崩溃、无 errors.log、私有内存平稳（186→192MB，与 FrameClock 路径持平）、线程数稳定（渲染线程复用无泄漏）。flag 关时走原 `FrameClock` 路径，零生产影响；高刷机 A/B 验证稳定后再议默认开启（见 `docs/gpu-rendering-evaluation.md` §10）。
- **活跃态内存的真实边界 + 静止悬停去空转（轨道光/放大波）**：用户实测发现 trim 只在「被动/静止」时降内存，**实际交互（鼠标移动，或运行 App 的呼吸指示灯）会让分层窗口持续 `UpdateLayeredWindow` 整屏重传，页面立刻换回**，故活跃态内存仍高（仅侧 dock ~200MB、双 dock ~800MB）。诊断确认：①主玻璃 dock 的窗口因 `SizeToActiveContent`（IconSize 68 × 7 列 + 底部侧 dock 反向占用顶高）算出超屏高度被钳到全屏 1436×1061，是一个全屏分层软件窗口；②其**放大波 `OnMagTick` / 侧 dock `OnWaveTick` 原本只在光标离开时才停**——光标停在 dock 上不动也每帧空转重算（主 dock 60fps、侧 dock 每帧重建火焰几何）。改为**收敛到当前目标即停 tick**（光标移动经 `EnsureMagTicking`/`EnsureWaveTicking` 重启，离开分支也补重启确保回弹），视觉不变；③主 dock 的**轨道光**改为可暂停时钟（`RadialWindow.RegisterAmbientClock` + `SetAmbientPaused`，经 `GlassOrbitLight.Build` 的 `registerClock` 回调），光标静止即冻结。结论性边界：**绿色运行呼吸灯按用户要求始终呼吸不冻结**（冻结会让其停在随机相位「时亮时不亮」），而只要呼吸灯在动，整屏每帧重传不可避免——故活跃态内存的根本下降仍需改 GPU 渲染架构（去 `AllowsTransparency`，改动大、改玻璃观感），未实施。静止悬停的 CPU 因放大波/轨道光停转而显著下降。
- **空闲判据改为「光标未在可见界面上活动」（覆盖仅侧 dock / 设置界面两态）+ 侧 dock 关闭空转**：原 idle-trim 只在两 dock 全隐藏时触发，导致「仅侧 dock」与「设置界面打开」两态全基线常驻。改为：主 dock 隐藏且光标未在可见次级界面（侧 dock / 设置窗）上**移动**即视为被动可 trim（静止窗口画面由 DWM 持有、换出不变黑，下次交互按需换回）。设置界面态 196MB→~70MB。侧 dock 的装饰星空闪烁亦改为光标静止即暂停。
  与「常驻+运行区重复显示」两类 BUG 的共同根因是 **`Process.MainModule.FileName` 读不到
  路径**——它需要 `PROCESS_VM_READ`，对反调试保护进程（UU加速器）、跨位（32/64）、提权进程
  都会失败。而 Windows shell/任务栏用的是更低权限的 **`QueryFullProcessImageName`**（只需
  `PROCESS_QUERY_LIMITED_INFORMATION`）。改进：①`WindowPreviewService` 公开
  `TryGetProcessImagePath`（封装已有的 `QueryFullProcessImageName`）；②把 `SnapshotRunning`、
  `FindWindowProcess`、`GetWindowsByExeName` 等所有进程路径读取点从 `MainModule.FileName`
  统一切到它——实测能读到 UU 主程序真实路径 `…\UU\5224\uu.exe`（之前完全读不到）。这让
  运行快照与运行区（GetTaskbarApps 早已用此 API）看进程的方式一致，大量受保护/跨位进程的
  路径匹配得以恢复，减少对脆弱的进程名/标题 fallback 的依赖。③提取共享的
  `IsSameOrChildInstallFolder`（同目录或一层版本子目录，带系统/共享目录与一层深度保护，避免
  Steam steamapps 等深层误判），**绿灯检测与运行区去重共用同一套安装目录匹配**，把 launcher
  （`UU\uu_launcher.exe`）与其版本子目录主程序（`UU\5224\uu.exe`）健壮地关联起来；窗口标题
  匹配退化为极端兜底。

## 🐛 BUG 修复

- **土星侧 Dock 黑色背景与黑色火焰叠加处变深**：
  - **现象**：土星侧 Dock 把 slab 背景与「黑色火焰」舌头设为同一透明度后，两者**重叠的根部区域颜色更深**，
    形成一道比周围深的接缝。
  - **根因**：火焰根部**故意伸进 slab**（让单次高斯羽化把两者融成一团）。但 slab 与火焰是两次独立的
    半透明 `SourceOver` 填充，重叠区 alpha 叠成 `a·(2−a)` → 比非重叠区深。
  - **修复**：把 slab 圆角矩形与（裁剪后的）火焰**几何并集（`CombineWithGeometry` Union）成一个几何体，
    只 `FillGeometry` 一次**。单次填充 = 全区均匀 alpha，重叠区不再双重变暗，且仍只羽化一次保持融合外观。

- **主 Dock 悬停名字标签初次显示模糊**：
  - **现象**：主 Dock 悬停图标弹出的名字标签，在图标放大动画过程中显示发虚/模糊，定住后才清晰。
  - **根因**：标签中心 `lx/ly` 是随放大动画每帧变化的小数坐标，文本绘制原点落在非整设备像素上 → 灰度
    抗锯齿把字形糊开；动画停住坐标稳定后才清晰。
  - **修复**：`DrawHoverLabel` 绘制前把标签中心**对齐到设备像素栅格**（`Math.Round(v*_dpi)/_dpi`）；并在
    `CompositionHost` 显式设 `TextAntialiasMode.Grayscale`（预乘 alpha 合成面上 ClearType 无效，统一灰度更干净）。
    另把标签宽度由「字符数×固定每字宽」估算改为 DirectWrite **实测文本宽度** + 固定内边距（按名字缓存），
    消除短名/拉丁名余量过大、宽度不一致的问题。

- **拖拽越拖越卡（高刷屏）根因 = 渲染线程整帧持锁饿死 UI 输入**：
  - **现象**：在高刷新率机器上拖动图标，越拖越涩、发粘，但帧率诊断显示渲染线程稳定 140+ fps（worst gap 16ms），并非掉帧。
  - **根因**：`RenderThreadFrame()` 用 `lock(_stateLock)` 包住整个 `Tick()`（推进+渲染+Present，144Hz 下数毫秒）；而 UI 线程的 `WM_MOUSEMOVE`/拖拽输入写状态也要拿同一把 `_stateLock`，于是每次鼠标输入都要等渲染线程放锁——等满一整帧 → 即便高帧率，输入也"粘手"。先前为绕过此问题把计数变更路径从 `RelayoutInPlace()` 换成 `Rebuild()`，只是症状掩盖。
  - **修复**：①从拖拽输入热路径（LBUTTONDOWN/MOUSEMOVE/LBUTTONUP、运行栏拖动、UpdateDragGap）移除 `_stateLock`；②`RenderThreadFrame()` 去锁（`Tick()` 直接调用）；③移除启动/弹跳/intro/显隐/外部拖入/滚轮等标量状态转换里残留的 `_stateLock`。x64 上 float/int/bool 单字写入足够原子，可容忍跨帧瞬时读取。两 Dock 同步处理。
  - **验证**：用户确认拖拽不再卡。后续把计数变更路径从 `Rebuild()` 改回 `RelayoutInPlace()`，消除增删图标闪烁、并修复侧 Dock 删除图标后短时间内无法再次呼出的问题（`Rebuild()` 会拆建 host/swapchain/DComp → 闪一帧黑 + 打断边缘呼出状态机）。

- **高刷新率下动画周期被压缩（液态玻璃轨道光/流动蓝光/齿轮加速）**：
  - **现象**：144Hz 屏上液态玻璃轨道冷光、运行图标流动蓝光、设置齿轮旋转周期大幅缩短（约 2.4× 加速）。
  - **根因**：这些动画用「每帧固定步进」推进（如 `_orbitAngle += 16f*360f/36000f`），隐含假设 60fps（16ms/帧）；144Hz 下每秒推进 2.4 倍。土星行星/星环用真实 `dt` 积分，已正确。
  - **修复**：两 Dock 的 `Tick()` 引入 `_animLastMs` + 每帧 `frameDt`（钳 0–0.1s），把 `_orbitAngle`、`_runSweep`、`_gearAngle`、玻璃滚动缓动 tau、缓动常数 k 全改为按 `frameDt` 积分，周期恢复（36s/4.2s/1.7s）；在 `CreateHostResources` 把 `_animLastMs=0` 清零，避免重建后首帧拿到超大 dt 跳变。

- **土星主题主 Dock 运行图标蓝色流动光环静止**：
  - **现象**：土星主题下主 Dock 运行图标周围的蓝色 sweep 光环不转动（液态玻璃主题正常）。
  - **根因**：主 Dock 有两条独立布局路径——液态玻璃分支会在检测到运行应用时设 `_anyRunning = true`，但土星分支 `BuildSaturnLayout()` 算了 `run` 标志、图标也标成 running，却**漏设 `_anyRunning`**。而 `_runSweep`（sweep 角度）只在 `if (_anyRunning)` 内推进，故土星主题下角度永不更新 → 静止。与刷新率无关，是独立遗漏。
  - **修复**：在 `BuildSaturnLayout()` 的图标循环里补 `if (run) _anyRunning = true;`，与液态玻璃分支一致。

- **侧 Dock 悬停图标后 `errors.log` 每帧刷 `SideDockWindowGpu.Tick → NRE`（与主 Dock 同一半初始化陷阱）**：
  - **现象**：悬停侧 Dock 图标后，`errors.log` 高频（单次会话达 441 次）刷
    `SideDockWindowGpu.Tick → NullReferenceException`，渲染 Tick 被异常中断。
  - **根因**：与下条主 Dock 完全相同的时序陷阱——`EnsureAnchor()` 以 `_anchorWin != null` 为「已初始化」
    判据，但 `_preview` 最后才创建；若中途抛异常（`_anchorWin` 已赋值、`_preview` 仍 null），下次
    `EnsureAnchor` 因 `_anchorWin!=null` 直接 return → `_preview` 永远 null → `DrivePreview` 里
    `_preview!.Placement` 每帧 NRE。主 Dock 此前已修，侧 Dock 漏修。
  - **修复**：移植主 Dock 同款修复——`EnsureAnchor` 改以 `_preview` 为「完成」标志 + 整段 try-catch
    回滚半成品状态（`_anchorWin?.Close()`、三字段置 null）并 `Log.Warn` 记录原始异常；`DrivePreview`
    用 `_preview`/`_anchorWin` 前加 null 守卫、去掉不安全的 `_preview!`。
  - **验证**：两轮自动化压测（反复召唤双 Dock + 横扫悬停主/侧 Dock 图标 + 消散，共 ~35s）后
    `errors.log` **零新增 NRE**（修复前同类操作必刷）。

- **主 Dock 悬停某些图标后无法点击、伴随系统报错提示音**：
  - **现象**：悬停到（某状态下的）图标后，主 Dock 点击无响应并发出系统报错音；`errors.log` 每帧刷 `MainDockWindowGpu.DrivePreview → NullReferenceException`（经 FrameClock/MediaContext 渲染回调），渲染 Tick 被异常中断，连带吞掉点击。
  - **根因**：`EnsureAnchor()` 以 `_anchorWin != null` 作为「已初始化」判据，但 `_preview` 是该方法**最后**才创建的。若 `_anchorWin.Show()` 或其后步骤抛异常（`_anchorWin` 字段已赋值、`_preview` 仍为 null），下次进入 `EnsureAnchor` 因 `_anchorWin!=null` 直接 return → `_preview` 永远为 null → `DrivePreview` 里 `_preview!.Placement` 的强制解引用每帧 NRE。属预先存在的半初始化时序陷阱（最早 22:43 即出现，与同日的渲染表面裁剪改动无关）。
  - **修复**：①`EnsureAnchor` 改以 `_preview` 为「完成」标志、整段包 try-catch，失败时回滚半成品状态（`_anchorWin?.Close()`、三字段置 null）以便后续悬停干净重试，并 `Log.Warn` 记录原始异常；②`DrivePreview` 在用 `_preview`/`_anchorWin` 前加 null 守卫，去掉不安全的 `_preview!`。

- **设置滑块拖动卡顿、且无法点击滑轨定位**：
  - **现象**：拖动「面板透明度 / 图标大小 / 字体大小」滑块时不流畅、发涩；点击滑轨某处不会跳到该百分比。
  - **根因**：① 滑块样式未设 `IsMoveToPointEnabled`，点击滑轨只触发 `DecreaseLarge/IncreaseLarge` 翻页而非定位。② `OnSettingChanged`（滑块 `ValueChanged`）每次变化都同步调用 `CommitSettings → _persist()` **把 config.json 写盘**；拖动每像素触发一次磁盘 I/O 阻塞 UI 线程 → 拖动发卡。
  - **修复**：① 滑块样式加 `IsMoveToPointEnabled=True`（点击滑轨即定位）。② `OnSettingChanged` 改为**仅即时刷新百分比标签 + 防抖 140ms 后再 `CommitSettings`**（停止拖动才写盘），并在 `OnClosing` flush 未落盘的最后取值，避免丢失。
  - **效果**：拖动顺滑、可点击定位、百分比实时显示；磁盘写入由"每像素一次"降为"停手一次"。

- **GPU 双 Dock 悬停时私有内存持续暴涨（GB 级原生泄漏）**：
  - **现象**：悬停/活动渲染时进程私有字节（priv）以 ~120 MB/s 持续增长，20 秒涨到 ~2.7 GB 且
    隐藏后不回落；空闲（隐藏）则稳定。`POLARIS_GPUFPS=1` 实测 active 段 priv 169→2666 MB，而
    托管堆（GC）全程平稳 3-7 MB、Gen2 仅 7 次——**纯原生泄漏，非托管堆**。
  - **根因**：`Services/Gpu/GlassSlab.cs` 的玻璃 slab 每帧绘制 ~9 个渐变层，每层都新建一个原生
    `ID2D1GradientStopCollection`（`Stops(ctx,...)`），但该集合被**直接当实参传给
    `Create*GradientBrush` 而从未 Dispose**——只释放了 brush。每个渐变停止集合在 Intel Arc 上会
    分配一块渐变 ramp 纹理（计入 priv），每帧泄漏 ~9 个 × 双 Dock，累积成 GB 级。
  - **修复**：新增 `RadialBrush`/`LinearBrush` 辅助方法，用 `using var stops` 创建 brush 后立即
    释放停止集合（D2D 中 brush 创建时已 AddRef 该集合，本地句柄可安全释放、brush 仍有效）；
    9 处渐变创建（DrawGlass 7 + DrawDark 2）全部改走辅助方法。
  - **效果**：active 悬停 priv 由泄漏到 2.7 GB → **全程稳定 ~400 MB（降幅 ~85%）**；并且消除每帧
    原生纹理 churn 后，FrameClock 帧率由 ~38fps → **44-53fps（双 Dock）/ 触及 59fps（单 Dock）**（实测于**真实 Surface Laptop 6 + 集成 Intel Arc iGPU、59Hz、平衡电源**；`HypervisorPresent` 为 VBS 安全特性，非来宾 VM）。旁证：
    SaturnScene 与侧 Dock 内其余 `CreateGradientStopCollection` 调用方均已用 `using`、不泄漏；
    `GlassPrototypeWindow.cs` 有同样模式但仅 `POLARIS_GLASS_PROTO=1` dev 标志启用，非生产路径。
  - 配套：新增 `Services/GpuFrameStats.cs`（`POLARIS_GPUFPS=1` 开启的逐 Dock fps/帧间最差 gap/
    内存 CSV 采集器）；侧 Dock 隐藏后 `_timer?.Stop()` 停掉 vsync 渲染循环，避免空闲 churn。

- **GPU 双 Dock 点击启动动画与原版不一致**：
  - **现象**：①侧边 Dock 点击图标后图标卡在放大弹出状态，弹跳回落后又被重新放大；弹跳又矮又慢、
    无落地回弹、无膨胀。②主 Dock 点击后图标向上跳着缩小（而非朝自身中心），且缩小动画未结束双
    Dock 就关闭；后续要求改为「先恢复原大小→再放大回悬浮尺寸→才关闭」。
  - **根因**：①侧 Dock 弹跳期间未压制悬停放大波（光标停在图标上→每帧重新放大）；弹跳后未主动
    收起也未阻止重放大；弹跳曲线用单正弦半周（apex@50%、无 BounceEase 落地、无 scale pop），且
    起跳前未把放大 pop 归零，缩小中的 pop 抵消起跳高度→又矮又慢。②主 Dock 误加了向上 hop（WPF
    原版 `RadialWindow` 启动**无弹跳**），`HidePanel` 纯淡出未先去放大、未 hold 到动画结束。
  - **修复**（对照 WPF 原版 `SideDockWindow.Bounce`/`DockBounce`/`RadialIcon.PlayLaunchBounce`、
    `RadialWindow.HidePanel→ResetMagnify` 重做）：
    - 侧 Dock：点击先**可见去放大**（130ms settle，`_byBounce` 压住放大波）→ 再起跳；弹跳曲线
      移植 `DockBounce`（520ms、apex@33% QuadEaseOut 上冲 + `BounceEase(2,2.4)` 双段落地 + 同步
      scale pop 至 1.2x）；弹跳完成置 `_dismissing` 锁并清 `_byEdge` 主动收起，淡出期 Tick 早退
      不跑放大波，杜绝光标停留导致的重放大。
    - 主 Dock：移除 hop；改为**先去放大恢复 1.0 → 再放大至 `MagnifyPeak`(1.7x 悬浮尺寸) → hold
      到结束才淡出关闭**（`PressScale` 两段 QuadEaseOut，`_launching` 期强制 `active=false` 让缩放
      锚定图标自身中心）。

- **一批 Dock 交互 BUG（光标/同步/预览/常驻）**（`089015f`、`eb42a81`）：
  - **侧边 Dock 召唤时鼠标转圈（AppStarting 光标）**：Dock 与 drop-shim 的 `WNDCLASSEXW` 未设
    `hCursor`，OS 在窗口上显示忙碌光标。修复：窗口类设标准箭头 `LoadCursorW(IDC_ARROW)`。
  - **主 Dock 删除常驻图标后侧边 Dock 不更新**：主 Dock 在触发 `AppsChanged` 前已
    `MirrorResidentToLeft`，故 App 里 `if (MirrorResidentToLeft(...))` 返回 false →
    `RefreshFromConfig` 被跳过。修复：`AppsChanged` 改为无条件刷新侧边 Dock。
  - **侧边 Dock 更新时消失重刷（闪烁）**：`RefreshFromConfig` 改用就地 relayout 后仍闪——横向
    Dock 增删图标会重新居中(窗口横移)，交换链 `ResizeBuffers` 与窗口移动间露出空白帧。修复：
    先 resize 交换链并渲染好新内容，**再**移动窗口；并补 `SyncShim` 同步 region。
  - **鼠标移到无运行窗口的图标时旧悬浮缩略窗卡住不关**：移入新目标的 `OnPointerEnter` 取消了关闭
    定时器，但新目标无窗口不显示 → 旧弹窗滞留。修复：打开定时器发现新目标无窗口时关闭弹窗。
  - **每次启动把常驻区强行塞满**：拖拽/删除改变 `Ring0Count` 只存了 `_config.Settings`，未回写
    per-theme 的 `ThemeAppearances[theme].Ring0Count`；启动 `LoadAppearance` 用旧值(0=auto=14)
    覆盖。修复：常驻数变化时同步 `SaveAppearance` 持久化 per-theme。
  - **此电脑/文件资源管理器悬停缩略图相同**：二者都跑在共享 explorer.exe 内，按进程/AUMID 无法区分
    → 此电脑预览空、资源管理器预览所有窗口(含此电脑)。修复：shell 文件夹固定项(`::CLSID`)按窗口
    标题包含匹配只显示自己的窗口(对齐运行灯 `IsShellItemRunning`)；通用文件资源管理器取所有
    explorer 窗口再排除被其他 shell 文件夹固定项认领(标题命中)的窗口。

- **从桌面拖图标进 GPU Dock 时图标抓不起来（鼠标按下被 Dock 吞掉）**（`5e8db52`）：GPU
  Dock 是合成窗口（`WS_EX_NOREDIRECTIONBITMAP`），且窗口尺寸远大于可见内容（含拖拽/阴影
  留白，土星主题几乎全屏）。这圈透明余量属于置顶窗口，**会拦截本该落到下方桌面图标的鼠标
  按下**，导致桌面图标无法被抓起拖入 Dock；现象呈"偶发"（实为按下点是否落在余量内 + 渲染
  线程是否繁忙的叠加）。根因：合成窗口无法用 `WS_EX_TRANSPARENT`（需配 `WS_EX_LAYERED`，
  与合成互斥，故为 no-op），而逐消息 `WM_NCHITTEST→HTTRANSPARENT` 穿透在渲染线程繁忙时
  回应延迟、被 OS 当作不透明。**修复**：用 `SetWindowRgn` 把窗口裁切到可见内容（slab/土星
  盘 + 放大/标签留白），余量直接不属于窗口——OS 级、与渲染线程无关的穿透，等价于 WPF Dock
  逐像素 alpha 命中测试（`MainDockWindowGpu.ApplyWindowRegion`，`SyncShim` 内随布局更新）。
  排查中确认 `WS_EX_TRANSPARENT`、鼠标捕获、OLE 注册、shim z-order、UI 线程阻塞均非根因。

- **进程保护应用（UU加速器）的常驻图标不亮绿色运行灯**：UU加速器固定的是
  `uu_launcher.exe`（启动器，拉起主程序后自身无窗口），而真正有窗口的主进程 `uu`
  开启了反调试保护，**`Process.MainModule.FileName` 读不到其 exe 路径**，导致
  `SnapshotRunning` 既不把它计入 `Paths` 也无路径可匹配——所有基于路径/文件名/同目录的
  检测（byPath/byExe/byFolder）全部失败，绿灯不亮；但运行区（窗口枚举）仍能显示它，造成
  「有图标、无绿灯」的不一致。**修复**：`RunningSnapshot` 新增 `WindowTitles`，只收集
  「有可见窗口但路径读不到」的受保护进程的主窗口标题；`IsEntryRunning` 增加 `byTitle`
  fallback——当某受保护窗口标题精确等于固定项的显示名时判定为运行（UU 主窗口标题
  「UU加速器」正好等于固定项名）。只存受保护进程标题，普通应用标题不入集合，避免误判。
  顺带新增 `IsRunningInSameFolderWithWindow`（同安装目录有可见窗口的兄弟主程序，带
  系统/共享目录保护）覆盖「路径可读的启动器（如 iQiyi）」场景。
  - **后续**：同一应用还会同时出现在常驻区和运行区（运行区未排除它）。同因路径/AUMID
    读不到，运行区的 path/aumid/fileName 排除全部落空。修复：侧 Dock running strip 增加
    `excludeTitles`（常驻 pin 的显示名），过滤时运行窗口标题等于某常驻 pin 名则排除
    （UU 窗口标题「UU加速器」= pin 名），避免「常驻+运行区」重复显示。
- **侧边 Dock 隐藏时放大波循环泄漏**（`c3d89d1`）：光标停在侧边 Dock 上时关闭 Dock，
  `OnWaveTick`（`CompositionTarget.Rendering`）不会被注销，每帧在隐藏窗口后持续空转
  （隐藏时 CPU 泄漏）。修复：`DoHide()` 折叠后调用 `ResetWave()` 并把 `_waveCursorY`
  置 NaN，保证渲染回调被拆除。
- **玻璃拖拽图标飞出窗口边界后消失**（`f71134f`）：玻璃图标在被裁剪的滚动层里，拖拽时
  重父到 PanelCanvas，但紧凑分层窗口会在窗口边缘裁掉图标。修复：用独立的无边框置顶穿透
  覆盖窗口 `DragGhostWindow` 承载拖拽图标快照；快照用绝对 `Viewbox`+`Canvas` 偏移精确
  采样图标本体（避免 VisualBrush 采样到溢出子元素导致缩小）。
- **运行中 UWP 应用无法固定到常驻区**（`817b7b4`）：打包应用（计算器/商店等）的运行瓦片
  无可读 exe 路径、只有 AUMID，导致右键菜单隐藏「固定到常驻区」。修复：新增
  `ShellNamespace.FromAumid` 由 AUMID 构建稳定、抗更新的 AppEntry；菜单条件放宽为
  路径或 AUMID 任一存在；`PinRunningApp` 优先用 AUMID 构建。
- **更新检查报告陈旧版本**（`fe2a257`）：无论实际 release 如何，更新检查器都报固定旧版本。
- **多显示器底边误判**（`db2ec04`）：仅对真正的外侧底边做任务栏触发屏蔽，按显示器排布判断。
- **跨应用窗口预览 + 低性能模式玻璃卡顿**（`24bbbf0`）。
- **常驻托盘应用激活失败**（腾讯视频/QQLive）（`ba48a73`）。
- **文件资源管理器误报运行中**（`fb42757`）：同时屏蔽底部 Dock 下的系统任务栏触发。
- **多进程启动/预览**（`0fd25ca`）。
- **打包应用运行条图标错误 + 图标提取**（`f18de5b`）。
- **拖出常驻图标时按预期常驻数实时重排动画**（`1ea76d4`）。
- **侧边 Dock 与玻璃主 Dock 在底/顶边的交互冲突**（`dae641f`）。
- **玻璃拖拽标签残留**（`f1d48a1`）：并优化侧边 Dock 与弹出性能。
- **侧边 Dock 消失与最小化启动 bug**（`dc6ca0f`）：常驻区可自定义数量。
- **文件资源管理器点击不开窗 / 拖拽后背景模糊丢失**（`c8a74ca`）。
- **空 AUMID 导致所有任务栏应用合并为一个 key**（`44ce316`）：去重时只有第一个存活。
- **裁剪图标在渲染线程 HighQuality 升采样掉帧**（`d2ebbd0`）：改为一次性升采样到 256，
  让 UI 始终缩小大位图；预览每行最多 6 列。
- **预览弹窗裁掉多余窗口**（隐藏滚动条后只显示 6 个中的 4 个）（`28fa90a`）：瓦片换行（最多 5 列）。
- **打包应用（新版 Teams/Outlook）显示文件资源管理器缩略图**（`7cf7129`）：因经
  `explorer.exe shell:AppsFolder\AUMID` 启动；改为按 AppUserModelID 匹配窗口。
- **60Hz 面板拍频 judder**（`741e2b6`，回归自 `cf23618`）：60fps 与 59.94Hz present 拍频；
  在 <90Hz 面板过采样 2x（60→120），高刷新率用原生率。
- **预览弹窗水平滚动条 / 大图标裁剪**（`57529d4`）。
- **文件资源管理器单窗口也显示悬停预览**（`cbfb3d6`，其它应用仍需 ≥2 窗口）。
- **行星加速闪烁**（`054088d`）：去掉缓存圆盘上的 Effect 切换；Ctrl+4 切换开关面板。
- **行星悬停加速即时响应**（`b6845b9`）：短 0.18s 加速 / 0.9s 缓降。
- **possible-null-argument 警告**（`efec931`，`ApplyPinnedRunning`）。

## ⚡ 性能优化

- **高刷新率机输入延迟缓解：启动离线化 + 缩小 GPU 主 Dock 渲染表面**（GPU 渲染移植在 4K@144 上的输入响应退化）：
  - **背景**：GPU 双 Dock 的渲染（`FrameClock`=`CompositionTarget.Rendering`，按显示刷新率触发）跑在 **UI 线程**，输入（`WndProc` 的点击/右键/悬停）也走 UI 线程消息泵。4K@144 下每帧近全屏栅格化量约为本机 59Hz 的 ~5×（刷新率 2.44× × 像素 2.0×），把单条 UI 线程占满，导致点击/右键菜单/激活/缩略图延迟严重；本机（59Hz、半分辨率）因负载仅 ~1/5、UI 线程有余量反而流畅。根因是「渲染放在 UI 线程」，非 GPU 算力（单帧 draw 仅 6-8ms≪16.6ms 预算）。
  - **①启动离线化**（`AppLauncher.Launch`）：`ActivateExisting`（枚举窗口+SetForegroundWindow）、`Process.Start`、shell 解析等同步重活原本阻塞 UI 线程数十~数百 ms。改为 `dismiss`（HidePanel，须 UI 线程）前台先调 → 重活 `LaunchCore` 丢 `Task.Run` 后台 → 错误 `MessageBox` marshal 回 UI 线程。验证所用 shell API（`Process.Start`/`SHGetPropertyStoreFromParsingName`）均 free-threaded、非 STA-bound，MTA 后台安全。（注：缩略图抓图 `WindowPreviewPopup` 早已后台化。）
  - **②缩小渲染表面**（`MainDockWindowGpu` 尺寸计算）：主 Dock 窗口原为「窗口内拖拽」留 `glassDragHeadroom=1.8×icon` 的四周透明余量，但拖拽已改用独立桌面 overlay `DragGhostWindowGpu`，这片余量基本是死像素；而交换链 back buffer 是整个 `_winW×_winH`、每帧 `Clear`+绘制全覆盖。将其 `1.8→0.6 icon`（保留少量供纯文字图标 fallback），**每帧栅格化像素 ~−20%**（4K@150% 实测 2.8M→2.2M px）；放大/悬停标签余量（`hoverHeadroom`+shadow）完全未动，放大图标与名称标签不被裁（顶部余量 268 DIP ≫ 所需 178 DIP）。
  - **定位**：二者均保帧率、低风险，**减轻** UI 线程争用但不根除——右键/激活的消息派发延迟只要渲染仍在 UI 线程就只能缓解；彻底解决需独立渲染线程（见 `docs/gpu-rendering-evaluation.md` §10 备用计划）。侧 Dock 沿屏边布局、尺寸已贴合内容，无类似可削余量。

- **空闲判据改为"光标未在可见界面上活动"（覆盖仅侧 dock / 设置界面两态）+ 侧 dock 关闭空转**：原 idle-trim 只在两 dock 全隐藏时触发，导致"仅侧 dock"(~280MB)与"设置界面打开"(~196MB)两态全基线常驻。两项改造：
  ①**trim 判据泛化**：`App.EvaluateIdleTrim` 不再要求全隐藏，而是"主 dock 未显示 且 光标未在可见次级界面（侧 dock / 设置窗）上**移动**"即视为被动可 trim——静止窗口的画面由 DWM 持有、换出页面不会变黑，下次交互按需换回。设置界面态实测 196MB→~70MB。
  ②**侧 dock 关闭未关注时的持续空转**：侧 dock 是 **1436×1040 的大型 `AllowsTransparency` 分层窗口**，软件合成时每次 opacity tick 重传整面；而它的运行点"呼吸"动画 + 暗色 dock 星空闪烁是 `RepeatBehavior.Forever`，加上放大波 `OnWaveTick` 原本只在光标**离开**时才停（光标停在 dock 上不动也每帧空转重建火焰几何）——导致"仅侧 dock"态空耗 **~63% CPU** 且页面持续变热使 trim 失效。修复：(a) 把这些永久动画改用可控时钟（`SideDockWindow.RegisterAmbientLoop`/`SetAmbientPaused`，经 `RadialIcon.AmbientRegistrar` 钩子覆盖钉住图标的运行点），光标不在 dock 上移动时 `Pause()` 冻结（绿点静止但仍可见，画面信息不丢），移动即 `Resume()`；(b) `OnWaveTick` 改为收敛到当前目标即停 tick（光标移动经 `EnsureWaveTicking` 重启，离开分支也补 `EnsureWaveTicking` 确保回弹），视觉完全不变；(c) 侧 dock 这些分层窗循环帧率由 `AmbientFrameRate`(60) 降到 `GlassLoopFrameRate`(30)，与主 dock 同源理由。实测"仅侧 dock 静止悬停"CPU 63%→**8.8%**、内存 280MB→**~50MB**（静止满 2s 后 trim 回收）。附带让 case 3 中光标在主 dock 时侧 dock 呼吸也暂停。
- **空闲时归还工作集（双 dock 隐藏降内存）**：Polaris 是 WPF + `AllowsTransparency` 托盘应用，空闲态（两 dock 都隐藏）约 440MB 占用几乎全是**非托管**——WPF/MilCore 软件渲染表面、原生图形栈（Direct2D/DirectWrite/WIC）、已加载的运行时/框架镜像、两个隐藏 dock 窗口；托管堆仅 ~7MB，GC 回收不了。空闲时这些页都不会被触碰，可安全换出到待机列表（即"托盘应用最小化后内存骤降"的同款机制）。新增 `Services/MemoryTrimmer.TrimWorkingSet()`（对自身进程 `EmptyWorkingSet`），由 `App` 的 100ms 边缘轮询挂载 `EvaluateIdleTrim`：两 dock 均隐藏且无设置窗口时，空闲满 2s 触发一次 trim，之后每 30s 复 trim 一次保持低位；任一 dock 显示立即复位计时（交互期绝不 trim）。实测空闲 WS 约 440MB→稳态在 ~20–70MB 间振荡（trim 瞬间低至 ~17MB），物理 RAM 真正归还系统；下次召唤仅按需换回少量页，无可感卡顿。
- **玻璃网格图标虚拟化（显示态深度降内存）**：液态玻璃主 Dock 的图标网格只渲染可视视口内（含上下各 1 行缓冲）的行，离屏行的 `RadialIcon` 对象被丢弃（移出滚动层、退订事件、槽位置 null），由 GC 回收其软件渲染的非托管可视子树——这是显示态占用的主体。滚回视口时按需用 `CreateIcon` 重建。关键设计：`_iconElements` 保持与 `_slotPositions`、配置条目 **1:1 全长**（离屏槽为 null），故所有按索引的代码（放大波 `Magnify`、悬停 spread、拖拽重排 `Reorder`、运行态刷新）只需各加一处 null 守卫即可照常工作，无需重写索引逻辑。配套：①每帧放大波 tick 跳过 null 槽；②重建图标时 `ResetMagnifySlot` 重置该槽放大状态、`RefreshIconState` 从 `_runStateCache`（每轮询覆盖**全部**条目，含离屏）同步套用绿色运行灯，故滚入即时点亮；③拖拽中（`_pressedIcon != null`）暂停虚拟化，drop 触发 `Rebuild` 重新裁剪；④滚动定格后 `GC.Collect(Optimized, 非阻塞, ApplicationIdle 优先级)` 促使非托管渲染资源真正归还。实测显示态约 1029MB→566MB（首次）/ 滚遍全部行后稳定 ~661MB（**-36%**，且收益持久不随使用侵蚀；仅 detach 不丢弃对象时会回升到 842MB，因 BitmapCache/MilCore 资源挂在存活对象上）。仅在网格可滚动（行数 > `VisibleRows`）时生效。
- **隐藏时把分层窗口缩到 1×1**（接续"隐藏时释放内存"）：`AllowsTransparency=True` 会维持一块
  约 窗口面积×4 字节的逐像素 alpha 软件合成缓冲（外加渲染暂存），是隐藏态占用的主体。`HidePanel`
  在 `ClearVisualTree()` 之后把 `Width=Height=1`，迫使 WPF 释放这块大缓冲；下次 `ShowFaded` 的
  `SizeToActiveContent` 会在 `Rebuild` 前恢复真实尺寸，用户全程看不到 1×1 窗口。实测隐藏态
  553MB→509MB（再 -44MB，无任何视觉变化）。
- **⚠️ 调查结论：显示态 ~1GB 是 `AllowsTransparency` 软件渲染的架构固有成本**。dotnet-counters 实测
  托管堆仅 ~10–19MB，~1GB 全是非托管（WPF MilCore 软件合成）。对照实验：空 Dock 738MB、满 42 图标
  1029MB（图标视觉子树 ≈291MB ≈7MB/个，而图标位图本身仅 256×256×4≈262KB/个，故大头不是位图而是
  软件渲染中间缓冲）；`POLARIS_NOCACHE` 实验证明 BitmapCache 反而更省（关掉更高），非元凶。空 Dock 仍
  738MB 说明横跨屏幕的玻璃 slab 软件渲染缓冲占很大比重。结论：要大幅压低**显示态**内存须改架构
  （改 GPU 渲染去掉 AllowsTransparency，或图标虚拟化），二者均有视觉/行为风险，未实施。
- **隐藏时释放内存**：Dock 隐藏时归还其占用的渲染内存。①`WindowPreviewService.TrimThumbCacheForHide`
  在隐藏时丢弃可重新捕获的窗口缩略图（仅保留最小化窗口的 last-good 帧，其像素已无法重捕），
  缩略图是最大的缓存位图；②`HidePanel` 折叠后调用新提取的 `ClearVisualTree()` 清空整个 Dock
  视觉树及其 `BitmapCache` 位图（下次 `ShowFaded` 反正会 `Rebuild` 重建，故零额外成本）。实测
  隐藏态内存约 653MB→553MB（-15%）。（注：曾试在隐藏后 `GC.Collect`，仅再降 ~12MB，收益太小
  且有打断 GC 启发式之虞，已回滚。）
- **缓存更多静态模糊层**（`c3d89d1`）：每次分层窗口重合成都会重栅格化的静态模糊层改用
  `BitmapCache`（零视觉变化）：Saturn 辉光环、运行指示点光晕（侧边+主 Dock RunDotGlow）、
  玻璃中心设置按钮。
- **玻璃帧率：跳过空闲工作 + 缓存悬停标签**（`b5e813d`）：放大波只在缩放/偏移超过 epsilon
  时才下发 `SetMagnify`/`SetZIndex`（静止图标每帧零写入）；玻璃悬停标签加 BitmapCache；
  时钟 culture 缓存；任务条瓦片改用冻结共享画刷。
- **玻璃帧率：缓存静态模糊 + 定时器生命周期**（`9dabd92`）：玻璃齿轮/时钟/常驻框发光加
  BitmapCache；缩略图预热定时器仅在面板显示时运行；任务条瓦片图缓存。
- **两种性能模式下优化液态玻璃帧率**（`002e9c1`）。
- **打磨任务栏 guard、全屏检测与动画性能**（`fe58e29`）。
- **缓存玻璃发光描边**（`35be898`）。
- **过采样率提升常驻循环动画帧率**（`bc95b7f`）：缩短显示/展开/隐藏过渡降低延迟。
- **隐藏时折叠画布**降低帧率开销（`1636a61`）。
- **Saturn 轮辐改烘焙切向渐变**（`23ce6ab`）：旋转层上无每帧 BlurEffect。
- **动画 DesiredFrameRate 匹配显示器刷新率**（`cf23618`）：原硬编码 120 浪费 60Hz 渲染预算。
- **缩略图弹窗即时显示缓存帧 + 异步流式更新**（`3f8efa4`）。
- **Saturn 轴对称辉光环移到静态层 + 去掉卫星光晕模糊**（`f2726a9`）：消除随放大增长的最大
  每帧模糊。
- **Saturn 自转圆盘缓存为位图 + 静态投影**（`6405182`）：放大后恢复帧率。
- **优化动画帧率**（`346683d`）：透明窗口按内容缩小居中；玻璃静态层与悬停辉光改
  BitmapCache + Opacity 动画。
- **最小化窗口缩略图预热缓存**（`ea4826a`）。
- ⚠️ 注：`05aca66`（缓存旋转环特征）因共享动画轨道变换导致悬停/加速闪烁，已被 `33c52eb` 回滚。

## ✨ 功能优化 / 新增

- **土星面板黑色浓度重映射（幂曲线）**：用户反馈土星黑色面板整体偏淡。把土星的「面板透明度设置 →
  有效不透明度」由线性 `1 − t` 改为幂曲线 `opacity = 1 − t^1.737`，使 **50% 透明度滑块 ≈ 旧 30%**
  （不透明度 0.7），同时保留滑块两端（0=全实、1=全透）可用。仅作用于**土星**（主 Dock 圆盘 + 侧 Dock
  slab/火焰），**液态玻璃保持线性 `1 − t` 不变**。helper 见 `DockTuning.SaturnPanelOpacity`；土星默认
  透明度由 0.05 调整为 0.30（≈0.88 不透明度）。

- **窗口预览重设计为 Windows 任务栏式**：预览改为「顶部 header（app 图标 + 标题在左、关闭 ✕ 在右、
  等高对齐）+ 下方实时缩略图」，**背景跟随系统深/浅主题**（`SystemTheme.IsLight`：浅色用近白 `#F3F3F6`、
  深色用近黑 `#1E1E22`，文字/关闭按钮配套）。
  - **关键简化**：标题与关闭按钮移到缩略图**上方**后不再与 DWM overlay 重叠，关闭按钮**回归普通 WPF 元素**，
    删除了之前为「按钮浮于 DWM 之上」而引入的独立 topmost `Popup` + 热区 + 防误关那一整套机制
    （`BuildCloseButton`/`ShowCloseButtonFor`/`CloseHoveredWindow` 等）。
  - **缩略图填充**：tile 高度按 `DwmQueryThumbnailSourceSize` 比例设置 + 整窗渲染，精确填满无黑边
    （详见「调查结论」DWM 缩略图条目）。
- **预览缩略图任务栏式关闭按钮**：悬停缩略图**右上角热区**(46×40)时淡入一个红色圆角 ✕ 按钮
  (`PopupAnimation.Fade`)，点击关闭对应窗口(空了则关整个预览)。
  - **关键陷阱**：DWM thumbnail 是合成在 tile **之上**的不透明覆盖层，放在 tile 视觉树里的普通
    WPF 按钮会被它完全盖住(Edge 等 GPU 窗口上尤其明显)。故按钮改放在**独立的 topmost `Popup`**——
    OS 把它合成在主预览 popup(及其 DWM overlay)之上，得以显示。这是「任何要盖在 DWM 缩略图上的
    元素都必须是独立更高层 HWND」这一约束的应用。
  - **交互细节**：①热区检测用 `thumbHost.MouseMove`(DWM overlay 不拦截输入，WPF 仍能收到)；
    ②按钮 hover 时计入 `_pointerInPopup`，避免指针移到独立按钮 HWND 时主预览被误关；③共享单个
    Popup 按 hover 的 tile 重新锚定(切换 tile 先关再开让 Fade 重播)；④定位用 `Custom` placement
    (实测 `Relative` 模式的偏移基准不对会整体左移)，右上角内缩(距右 2px、距上 1px)。
- **DWM Thumbnail 实时窗口预览**（替换 PrintWindow 截图）：悬停 Dock 图标弹出的窗口预览，
  由 `PrintWindow` GDI 截图改为 **DWM Thumbnail API**（`DwmRegisterThumbnail`）。
  - **动机**：`PrintWindow` 对 GPU 合成的浏览器（Edge/Chrome）即使在前台可见也只能截到黑/空帧，
    最小化窗口更无法截取 → 这些应用预览常年显示「已最小化」占位或黑块（旧的 PrintWindow 缓存方案
    `ea4826a` 因源帧本就是黑的而注定失败）。
  - **方案**：新增 `Services/Gpu/DwmThumbnail.cs` 封装 注册/更新/查询尺寸/注销 生命周期；
    `WindowPreviewPopup` 在弹窗实现自己的 HWND 后，为每个 tile 注册一个 DWM thumbnail 到该 HWND，
    DWM 直接复用它已为源窗口合成的帧 → **GPU 窗口显示实时内容、且源窗口最小化后仍持续显示最后画面**。
    注册失败的 tile 回退到原 PrintWindow/图标。
  - **关键陷阱**：DWM thumbnail 是 DWM 在 dest rect **之上**合成的不透明覆盖层，**不在 WPF 视觉树**、
    无法被 WPF 圆角/裁切，也**不随 PopupAnimation 淡出**；故切换图标/移开/关闭时必须显式 `Hide`/
    `Dispose`，否则旧的实时帧会残留在屏幕上。已在 `OnPointerEnter`（切换图标即 `Close` 旧弹窗）、
    `OnPointerLeave`（指针未落到弹窗时即 `HideDwmThumbnails`）、`ClosePopupUi`/`CloseAnimated`/单 tile
    关闭 全路径接通 Hide/Dispose。
- **GPU 主/侧 Dock 接通从桌面/资源管理器拖入图标**（`5e8db52` 起）：合成 Dock 窗口无法直接做
  OLE 拖放目标，故在 Dock 上方叠一个近乎不可见的普通窗口 `DropShimWindow` 承载 OLE
  `IDropTarget` 并把按下/拖放转发回 Dock；外部拖入支持 CF_HDROP 文件与 CFSTR_SHELLIDLIST
  shell 项（此电脑/回收站等），并实时显示蓝色"+"跟随标记；拖出图标用独立桌面置顶覆盖窗口
  `DragGhostWindowGpu` 承载，图标可越过窗口边界漫游不被裁切。主、侧两个 GPU Dock 一致。
- **精简设置界面**：删除顶部「Polaris 设置」标题与「提示：长按呼出键…」两行（含
  `UpdateHint` 方法及其调用），界面更紧凑。
- **点击 Polaris 之外区域关闭双 Dock**（`b8c6de3`）：主 Dock 打开时，左键点击任意 Polaris
  窗口之外（桌面/别的应用/空白）关闭主 Dock 与侧边 Dock；用独立线程 WH_MOUSE_LL 钩子按
  窗口所属进程判定。
- **主 Dock 光标距离图标放大**（`867ca53`）。
- **Saturn 高细节环 + 刘海日期时间面板 + 默认 High 模式**（`74327d5`）。
- **新消息提醒徽标**（镜像任务栏闪烁）（`a151193`）；后简化为圆点并显示非常驻运行应用（`8f2781d`）。
- **液态玻璃连续磨砂效果**（`741198a`）。
- **可配置切换热键、全局字号、性能模式设置**（`6e33088`）。
- **原生地理定位 + 主题感知 Dock 菜单**（`8dc0b48`）；天气改用 geo-IP 提供商链、去掉 WinRT
  依赖（`3ee35ef`）。
- **侧边 Dock 玻璃立体边 + 右键上下文菜单**（`3bfb12b`）。
- **液态玻璃轨道光、侧边 Dock 弹跳修复**（`3750f26`）。
- **启动弹跳、碎屑带、Dock 天气**（`b18b00c`）。
- **重设计设置窗口**，移除设置内图标管理（`bcc1575`）。
- **可切换 Dock 边 + 多显示器支持**（`3776ebf`）；侧边 Dock 四方向停靠（`08a4bd0`）。
- **AppsFolder 启动器解析为真实 exe**（`20b78a7`）。
- **默认主题改为液态玻璃**（`73332ee`）；玻璃弹出液感动画（`3335a3d`）。
- **双 Dock 落点拖放 / 缩略图关闭窗口**（`450c81d`）；土星侧边 Dock 黑材质内环常驻区（`55c2110`）；
  解耦主题常驻设置（`e2728c8`）。
- **检查更新（GitHub release 自动更新）、Dock 与任务栏合并为单块玻璃含分隔缝、桌面背景模糊**
  （`583495c`）；玻璃 6 列网格可扩展、任务栏溢出 +N（`c8a74ca`）。
- **接受注入的 Ctrl+key 组合**（触控板手势）（`fb15dfd`）。
- **分辨率缩放、玻璃网格、百分比标签、环边淡出**（`e706724`）；检测托盘最小化应用为运行中（`f32bd28`）。
- **每主题独立记忆透明度与图标大小**（`190dc2e`）。
- **支持 Shell 命名空间对象快捷方式**（此电脑/回收站等，经 explorer 打开并提取高清图标）（`9162cb4`）。
- **可切换主题系统**（土星环）（`4b01489`）。
- **Saturn 真实环系**（旋转环层、行星自转、比例轨道）（`936fbb0`）。
- **双层圆环布局、悬停放大显示名称、拟物齿轮旋转**（`067445c`）。
- **运行应用指示器 + 防闪烁覆盖层**（`3ad619b`）。
- **初始版本**：托盘呼出、可配置触发键、单实例、液态玻璃圆盘、拖入添加、长按固定模式（`d0cbd69`）。

## 🔧 重构 / 工程质量

- **重命名 LeftDock* → SideDock***（`7da1198`）：Dock 可停靠任意边，原 `Left` 命名误导；
  重命名类型、10 个分部文件及所有相关标识符。`AppConfig.SideDockApps` 用
  `[JsonPropertyName("LeftDockApps")]` 保留 JSON key，旧配置无损升级。
- 关闭时取消订阅 Dock 窗口事件并停止定时器（`5c49636`）。
- 集中重复的调参字面量以消除漂移风险（`267dd67`）。
- 去重共享 Dock 逻辑到 helpers（`1a4c687`）。
- 从 App 上帝对象提取 TaskbarGuard 子系统（`e88cd6a`）。
- 新增单元测试项目，覆盖纯 Services 逻辑（42 个测试）（`ba5338b`）。
- 新增诊断日志设施，把静默 catch 点路由过去（`2fda714`）。
- 移除死代码（`48e6621`）。
- 拆分 Dock 上帝类为分部类、提取共享 helper（`6007a68`、`23824cd`）。
