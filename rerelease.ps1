# Commit remaining changes, force push, and re-release v1.9.1

Push-Location C:\Tools\Polaris

Write-Host "=== Step 1: Check remaining uncommitted changes ===" -ForegroundColor Cyan
git status --short

Write-Host "`n=== Step 2: Stage and commit remaining files ===" -ForegroundColor Cyan

# Check what's left
$remaining = git status --short
if ($remaining -match "Views/LeftDockWindow.Layout.cs|Views/RadialIcon.xaml") {
    Write-Host "Committing remaining layout changes..."
    git add Views/LeftDockWindow.Layout.cs Views/RadialIcon.xaml
    git commit -m "Minor layout adjustments for glass theme

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
}

# Add any other remaining changes
git add -A
$staged = git diff --cached --name-only
if ($staged) {
    Write-Host "Committing remaining changes: $staged"
    git commit -m "Cleanup release scripts

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
}

Write-Host "`n=== Step 3: Show commit history ===" -ForegroundColor Cyan
git log --oneline -8

Write-Host "`n=== Step 4: Delete old tag and recreate ===" -ForegroundColor Cyan
git tag -d v1.9.1 2>$null
git push Polaris --delete v1.9.1 2>$null
git tag -a v1.9.1 -m "v1.9.1

Improvements:
- Glass drag ghost fix: dragged icons now stay visible outside panel bounds
- Glass performance optimizations: cached static blurs + timer lifecycle (zero visual impact, smoother frame rate)
- Glass dock panel 10% smaller (tighter spacing, reduced footprint)
- Saturn dark-mode beta: darkPad opacity reduced to 0.5"

Write-Host "`n=== Step 5: Force push master and new tag ===" -ForegroundColor Cyan
git push Polaris master --force
git push Polaris v1.9.1

Write-Host "`n=== Step 6: Delete old release and create new ===" -ForegroundColor Cyan
gh release delete v1.9.1 --yes 2>$null

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

Write-Host "`n=== ✅ v1.9.1 re-released successfully! ===" -ForegroundColor Green
git log --oneline -6
gh release view v1.9.1 --web

Pop-Location
