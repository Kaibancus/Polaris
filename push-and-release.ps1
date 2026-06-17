# Push and release v1.9.1

Push-Location C:\Tools\Polaris

Write-Host "=== Checking remotes ===" -ForegroundColor Cyan
git remote -v

Write-Host "`n=== Pushing to Polaris remote ===" -ForegroundColor Cyan
git push Polaris master
git push Polaris v1.9.1

Write-Host "`n=== Creating GitHub release ===" -ForegroundColor Cyan
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

Write-Host "`n=== ✅ v1.9.1 released successfully! ===" -ForegroundColor Green
Write-Host "`nOpening release page in browser..."
gh release view v1.9.1 --web

Pop-Location
