using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Polaris.Views;

/// <summary>A tiny borderless, click-through, top-most layered window that carries
/// a snapshot of the icon currently being dragged from the main dock. The main
/// dock window is sized to a compact box around its content (for layered-window
/// composition performance), so an icon dragged past that box would otherwise be
/// clipped away. Hosting the dragged icon in this independent full-desktop-roaming
/// overlay keeps it visible anywhere on screen while the drag is in flight.
///
/// It only ever <i>follows the cursor</i> by moving its (icon-sized) window, so the
/// per-frame composited area stays tiny no matter where on the desktop it goes.</summary>
public sealed class DragGhostWindow : Window, IDragGhost
{
    private readonly Image _image;

    public DragGhostWindow(ImageSource snapshot, double dipWidth, double dipHeight)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        IsHitTestVisible = false;            // never steal pointer input during the drag
        Focusable = false;
        SizeToContent = SizeToContent.Manual;
        Width = Math.Max(1, dipWidth);
        Height = Math.Max(1, dipHeight);

        _image = new Image
        {
            Source = snapshot,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false,
        };
        Content = _image;
    }

    /// <summary>Centres the ghost on a virtual-desktop point (in DIPs).</summary>
    public void MoveCenterTo(double dipX, double dipY)
    {
        Left = dipX - Width / 2.0;
        Top = dipY - Height / 2.0;
    }

    /// <summary>Drag feedback: fade the icon while it sits over the delete zone.</summary>
    public double GhostOpacity
    {
        set => _image.Opacity = value;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Click-through + no activation + out of Alt+Tab, so the overlay is a
        // purely visual ghost that never interrupts the drag's mouse capture.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}
