# Step 1: Check status and commit all changes

Write-Host "=== Step 1: Checking git status ===" -ForegroundColor Cyan
Push-Location C:\Tools\Polaris
git status --short

Write-Host "`n=== Step 2: Committing changes ===" -ForegroundColor Cyan

# Commit 1: Glass panel shrink
Write-Host "`n[Commit 1/4] Glass panel 10% smaller..."
git add Services/PanelTheme.cs
git commit -m "Shrink glass dock panel by 10%

Reduce glass slab footprint for better performance:
- ColumnPitch: 79.2 -> 71.28 (10% smaller)
- RowPitch: 79.2 -> 71.28 (10% smaller)  
- Height: 350 -> 315 (10% smaller)
- ResidentGap: 1.5 -> 3.1 (prevent icon overlap with frame after magnification)

Icon size unchanged, tighter spacing reduces layered-window area and composition cost.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

# Commit 2: Saturn darkPad
Write-Host "[Commit 2/4] Saturn darkPad opacity..."
git add Services/PanelTheme.cs
git commit -m "Reduce Saturn darkPad opacity to 0.5 for dark mode

Subtle 50% opacity improves visual balance in beta dark-mode ring icons.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

# Commit 3: Drag ghost fix
Write-Host "[Commit 3/4] Drag ghost fix..."
git add Views/DragGhostWindow.cs Views/RadialWindow.Interaction.cs Views/RadialWindow.Hover.cs Views/RadialWindow.xaml.cs
git commit -m "Fix glass drag ghost: independent overlay window

Fixes glass icon disappearing when dragged outside panel bounds.

Root cause: glass icons live in clipped ScrollViewer; dragged icon reparents to PanelCanvas but compact layered window clips at edge.

Solution: independent borderless overlay window (DragGhostWindow) with snapshot of dragged icon:
- Topmost, transparent, click-through (WS_EX_TRANSPARENT|TOOLWINDOW|NOACTIVATE)
- SnapshotIcon uses absolute Viewbox shifted by Canvas.Left/Top to sample exactly icon glyph box (1.0x size)
- Avoids VisualBrush DescendantBounds shrinking from overflow children (hover label, glow layers)
- Ghost follows cursor; real icon hidden (opacity 0) during drag
- Lifecycle: created on drag-start, updated in OnMouseMove, disposed on drop/cancel

Verified icon stays visible during reorder, delete, and left-dock drop.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

# Commit 4: Performance optimizations
Write-Host "[Commit 4/4] Frame rate optimizations..."
git add Views/RadialWindow.Glass.cs Views/RadialWindow.Taskbar.cs Views/RadialWindow.xaml.cs Views/RadialWindow.Summon.cs
git commit -m "Optimize glass frame rate: cache static blurs and timer lifecycle

Zero-visual-impact performance optimizations for liquid-glass theme.

Problem: layered window with AllowsTransparency uses software per-pixel-alpha composition with no dirty-rect optimization. Every animation (orbit light, magnify wave, taskbar sweep) triggers full-window recomposite. Static blurred layers without BitmapCache re-rasterize blur every frame (expensive).

Optimizations:
1. Add BitmapCache to 3 uncached static blur layers:
   - Glass gear bead (DropShadow Blur=14 + glyph Blur=3, DeviceScale-aware)
   - Clock text (DropShadow Blur=10, updates 1/sec so rebuild negligible)
   - Resident-frame glow (BlurEffect Radius=4)
   RenderTransform (rotate, scale) doesn't invalidate cache -> spinning gear and zooming tiles remain cached textures

2. Stop _previewWarmTimer when panel hidden:
   - Was polling window thumbnails (BitBlt) every 2.5s even when hidden
   - Now starts on ShowPanel, stops on HidePanel

3. Cache taskbar tile 'plate' (icon + HighQuality-scaled image):
   - BitmapCache(2.0) for crisp 1.7x hover zoom
   - Eliminates per-frame re-sampling under continuous sweep animations

Result: 30-60 recomposites/sec now only blit cached textures instead of recalculating 4 large blurs + N tile images. Pixel-identical, smoother frame rate.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Step 3: Updating version to 1.9.1 ===" -ForegroundColor Cyan
$csproj = Get-Content Polaris.csproj -Raw
$csproj = $csproj -replace '<Version>1\.9\.0</Version>', '<Version>1.9.1</Version>'
$csproj = $csproj -replace '<FileVersion>1\.9\.0\.0</FileVersion>', '<FileVersion>1.9.1.0</FileVersion>'
$csproj | Set-Content Polaris.csproj -NoNewline

git add Polaris.csproj
git commit -m "Bump version to 1.9.1

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"

Write-Host "`n=== Step 4: Creating and pushing tag v1.9.1 ===" -ForegroundColor Cyan
git tag -a v1.9.1 -m "v1.9.1

Improvements:
- Glass drag ghost fix: dragged icons now stay visible outside panel bounds
- Glass performance optimizations: cached static blurs + timer lifecycle (zero visual impact, smoother frame rate)
- Glass dock panel 10% smaller (tighter spacing, reduced footprint)
- Saturn dark-mode beta: darkPad opacity reduced to 0.5"

Write-Host "`n=== Step 5: Pushing to origin ===" -ForegroundColor Cyan
git push origin master
git push origin v1.9.1

Write-Host "`n=== Step 6: Creating GitHub release ===" -ForegroundColor Cyan
$releaseNotes = @"
## Improvements

### 🎯 Drag Ghost Fix
- **Fixed glass icon disappearing when dragged outside panel bounds**
  - Icons now stay visible during reorder, delete, and left-dock drop
  - Independent overlay window with precise 1.0× snapshot rendering
  - Resolves clipping issues with compact layered window

### ⚡ Performance Optimizations (Zero Visual Impact)
- **Cached static blur layers** (glass gear, clock text, resident frame glow)
  - Eliminates per-frame re-rasterization during animations
  - RenderTransform-compatible caching (spinning gear remains cached texture)
- **Cached taskbar tile images** (2.0× scale for crisp 1.7× zoom)
- **Timer lifecycle fix**: stop thumbnail polling when panel hidden
- **Result**: Smoother frame rate during orbit light, magnify wave, and taskbar sweep animations

### 🎨 Layout Refinements
- **Glass dock panel 10% smaller** (tighter spacing, reduced footprint)
  - Column/Row pitch: 79.2 → 71.28
  - Height: 350 → 315
  - Icon size unchanged, better performance
  - Increased ResidentGap (1.5 → 3.1) prevents overlap after magnification
- **Saturn dark-mode beta**: darkPad opacity reduced to 0.5 for better visual balance

---

**Technical Details**:
- Layered window composition cost reduced by caching 4 large blur effects
- Preview warm timer now respects panel visibility
- Drag ghost uses absolute Viewbox with Canvas offset compensation to avoid VisualBrush DescendantBounds shrinking
"@

gh release create v1.9.1 --title "v1.9.1" --notes $releaseNotes

Write-Host "`n=== ✅ Done! v1.9.1 released ===" -ForegroundColor Green
git log --oneline -6
gh release view v1.9.1 --web

Pop-Location
