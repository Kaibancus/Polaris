# Commit all changes and release v1.9.1

$ErrorActionPreference = "Stop"
Set-Location C:\Tools\Polaris

Write-Host "=== Checking git status ==="
git status --short

Write-Host "`n=== Commit 1: Shrink glass dock panel by 10% ==="
git add Services/PanelTheme.cs
git commit -m "Shrink glass dock panel by 10%

Reduce glass slab footprint for better performance:
- ColumnPitch: 79.2 → 71.28 (10% smaller)
- RowPitch: 79.2 → 71.28 (10% smaller)
- Height: 350 → 315 (10% smaller)
- ResidentGap: 1.5 → 3.1 (prevent icon overlap with frame after magnification)

Icon size unchanged, tighter spacing reduces layered-window area and composition cost.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Commit 2: Reduce Saturn darkPad to 0.5 ==="
git add Services/PanelTheme.cs
git commit -m "Reduce Saturn darkPad opacity to 0.5 for dark mode

Subtle 50% opacity improves visual balance in beta dark-mode ring icons.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Commit 3: Fix glass drag ghost with independent overlay window ==="
git add Views/DragGhostWindow.cs Views/RadialWindow.Interaction.cs Views/RadialWindow.Hover.cs Views/RadialWindow.xaml.cs
git commit -m "Fix glass drag ghost: independent overlay window

Fixes glass icon disappearing when dragged outside panel bounds.

Root cause: glass icons live in clipped ScrollViewer; dragged icon reparents to PanelCanvas but compact layered window clips at edge.

Solution: independent borderless overlay window (DragGhostWindow) with snapshot of dragged icon:
- Topmost, transparent, click-through (WS_EX_TRANSPARENT|TOOLWINDOW|NOACTIVATE)
- SnapshotIcon uses absolute Viewbox shifted by Canvas.Left/Top to sample exactly icon glyph box (1.0× size)
- Avoids VisualBrush DescendantBounds shrinking from overflow children (hover label, glow layers)
- Ghost follows cursor; real icon hidden (opacity 0) during drag
- Lifecycle: created on drag-start, updated in OnMouseMove, disposed on drop/cancel

Verified icon stays visible during reorder, delete, and left-dock drop.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Commit 4: Optimize glass frame rate with cached static blurs ==="
git add Views/RadialWindow.Glass.cs Views/RadialWindow.Taskbar.cs Views/RadialWindow.xaml.cs Views/RadialWindow.Summon.cs
git commit -m "Optimize glass frame rate: cache static blurs and timer lifecycle

Zero-visual-impact performance optimizations for liquid-glass theme.

Problem: layered window with AllowsTransparency uses software per-pixel-alpha composition with no dirty-rect optimization. Every animation (orbit light, magnify wave, taskbar sweep) triggers full-window recomposite. Static blurred layers without BitmapCache re-rasterize blur every frame (expensive).

Optimizations:
1. Add BitmapCache to 3 uncached static blur layers:
   - Glass gear bead (DropShadow Blur=14 + glyph Blur=3, DeviceScale-aware)
   - Clock text (DropShadow Blur=10, updates 1/sec so rebuild negligible)
   - Resident-frame glow (BlurEffect Radius=4)
   RenderTransform (rotate, scale) doesn't invalidate cache → spinning gear and zooming tiles remain cached textures

2. Stop _previewWarmTimer when panel hidden:
   - Was polling window thumbnails (BitBlt) every 2.5s even when hidden
   - Now starts on ShowPanel, stops on HidePanel

3. Cache taskbar tile 'plate' (icon + HighQuality-scaled image):
   - BitmapCache(2.0) for crisp 1.7× hover zoom
   - Eliminates per-frame re-sampling under continuous sweep animations

Result: 30-60 recomposites/sec now only blit cached textures instead of recalculating 4 large blurs + N tile images. Pixel-identical, smoother frame rate.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Current commit log ==="
git log --oneline -5

Write-Host "`n=== Updating version to 1.9.1 ==="
$csproj = Get-Content Polaris.csproj -Raw
$csproj = $csproj -replace '<Version>1\.9\.0</Version>', '<Version>1.9.1</Version>'
$csproj = $csproj -replace '<FileVersion>1\.9\.0\.0</FileVersion>', '<FileVersion>1.9.1.0</FileVersion>'
$csproj | Set-Content Polaris.csproj -NoNewline

Write-Host "`n=== Commit 5: Bump version to 1.9.1 ==="
git add Polaris.csproj
git commit -m "Bump version to 1.9.1

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Creating tag v1.9.1 ==="
git tag -a v1.9.1 -m "v1.9.1

Improvements:
- Glass drag ghost fix: dragged icons now stay visible outside panel bounds
- Glass performance optimizations: cached static blurs + timer lifecycle (zero visual impact, smoother frame rate)
- Glass dock panel 10% smaller (tighter spacing, reduced footprint)
- Saturn dark-mode beta: darkPad opacity reduced to 0.5"

Write-Host "`n=== Pushing to remote ==="
git push origin master
git push origin v1.9.1

Write-Host "`n=== Done! v1.9.1 released ==="
git log --oneline -6
