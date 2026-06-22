using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Polaris.Services;

/// <summary>
/// Minimal OLE <c>IDropTarget</c> so a bare HWND — our GPU dock windows, which are
/// not WPF elements — can accept drags from Explorer and the desktop exactly like a
/// WPF <c>AllowDrop</c> window. <c>DragAcceptFiles</c>/<c>WM_DROPFILES</c> alone only
/// catches the legacy simple file drop; Explorer/desktop drags use the OLE drag-drop
/// protocol (<c>IDropTarget</c>) and carry both <c>CF_HDROP</c> files and shell
/// IDList items (This PC, Recycle Bin, packaged apps…). Register on the STA UI thread.
/// </summary>
[ComVisible(true)]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleDropTarget
{
    [PreserveSig] int DragEnter([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject pDataObj,
        int grfKeyState, POINTL pt, ref int pdwEffect);
    [PreserveSig] int DragOver(int grfKeyState, POINTL pt, ref int pdwEffect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop([MarshalAs(UnmanagedType.Interface)] ComTypes.IDataObject pDataObj,
        int grfKeyState, POINTL pt, ref int pdwEffect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINTL { public int X; public int Y; }

internal sealed class OleDropTarget : IOleDropTarget
{
    private const int S_OK = 0;
    private const int DROPEFFECT_NONE = 0;
    private const int DROPEFFECT_COPY = 1;

    private static readonly int CF_HDROP = 15;
    private static readonly int CF_SHELLIDLIST = RegisterClipboardFormat("Shell IDList Array");

    private readonly IntPtr _hwnd;
    // Invoked on a committed drop with the dropped file paths (CF_HDROP), the raw
    // shell-IDList bytes (CFSTR_SHELLIDLIST, or null) and the SCREEN drop point.
    private readonly Action<List<string>, byte[]?, int, int> _onDrop;
    private bool _registered;
    private bool _accepting;

    /// <summary>Optional: invoked on every DragOver with the SCREEN point while a drag is
    /// over us (and null on leave/drop), so the dock can draw a drag-follow preview.</summary>
    public Action<(int x, int y)?>? OnDragMove { get; set; }

    public OleDropTarget(IntPtr hwnd, Action<List<string>, byte[]?, int, int> onDrop)
    {
        _hwnd = hwnd;
        _onDrop = onDrop;
    }

    public void Register()
    {
        if (_registered || _hwnd == IntPtr.Zero)
            return;
        try
        {
            // Allow drag-drop messages through UIPI in case the dock window runs at a
            // higher integrity than the drag source (e.g. Polaris launched as admin,
            // files dragged from a normal Explorer). No-op when integrities match.
            foreach (uint m in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
            {
                try { ChangeWindowMessageFilterEx(_hwnd, m, MSGFLT_ALLOW, IntPtr.Zero); } catch { }
            }
            int oi = OleInitialize(IntPtr.Zero);   // refcounted; harmless if already initialised
            int hr = RegisterDragDrop(_hwnd, this);
            _registered = hr == S_OK;
            if (!_registered)
                Log.Warn("OleDropTarget", $"RegisterDragDrop failed hr=0x{hr:X8} (OleInit=0x{oi:X8})");
        }
        catch (Exception ex) { Log.Warn("OleDropTarget", "RegisterDragDrop failed: " + ex.Message); }
    }

    private const uint WM_DROPFILES = 0x0233, WM_COPYDATA = 0x004A, WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ChangeWindowMessageFilterEx(IntPtr hwnd, uint message, uint action, IntPtr pChangeFilterStruct);

    public void Revoke()
    {
        if (!_registered)
            return;
        try
        {
            RevokeDragDrop(_hwnd);
        }
        catch { /* window already gone */ }
        _registered = false;
    }

    private bool HasFormat(ComTypes.IDataObject data, int cf)
    {
        var fe = Fmt(cf);
        try { return data.QueryGetData(ref fe) == S_OK; }
        catch { return false; }
    }

    private bool Accepts(ComTypes.IDataObject data) =>
        HasFormat(data, CF_HDROP) || (CF_SHELLIDLIST != 0 && HasFormat(data, CF_SHELLIDLIST));

    int IOleDropTarget.DragEnter(ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        bool hd = pDataObj != null && HasFormat(pDataObj, CF_HDROP);
        bool sh = pDataObj != null && CF_SHELLIDLIST != 0 && HasFormat(pDataObj, CF_SHELLIDLIST);
        _accepting = hd || sh;
        pdwEffect = _accepting ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (_accepting) OnDragMove?.Invoke((pt.X, pt.Y));
        return S_OK;
    }

    int IOleDropTarget.DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        pdwEffect = _accepting ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (_accepting) OnDragMove?.Invoke((pt.X, pt.Y));
        return S_OK;
    }

    int IOleDropTarget.DragLeave()
    {
        _accepting = false;
        OnDragMove?.Invoke(null);
        return S_OK;
    }

    int IOleDropTarget.Drop(ComTypes.IDataObject pDataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        try
        {
            var files = ReadHDrop(pDataObj);
            byte[]? shell = ReadBytes(pDataObj, CF_SHELLIDLIST);
            if (files.Count > 0 || shell != null)
            {
                _onDrop(files, shell, pt.X, pt.Y);
                pdwEffect = DROPEFFECT_COPY;
            }
            else
            {
                pdwEffect = DROPEFFECT_NONE;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("OleDropTarget", "drop failed: " + ex.Message);
            pdwEffect = DROPEFFECT_NONE;
        }
        _accepting = false;
        OnDragMove?.Invoke(null);
        return S_OK;
    }

    /// <summary>Extracts the dropped file paths from a CF_HDROP medium.</summary>
    private static List<string> ReadHDrop(ComTypes.IDataObject data)
    {
        var paths = new List<string>();
        var fe = Fmt(CF_HDROP);
        ComTypes.STGMEDIUM medium = default;
        bool got = false;
        try
        {
            data.GetData(ref fe, out medium);
            got = true;
            IntPtr hDrop = medium.unionmember;   // HGLOBAL doubles as the HDROP handle
            if (hDrop == IntPtr.Zero)
                return paths;
            uint count = DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint len = DragQueryFile(hDrop, i, null, 0);
                var sb = new System.Text.StringBuilder((int)len + 1);
                if (DragQueryFile(hDrop, i, sb, len + 1) > 0)
                    paths.Add(sb.ToString());
            }
        }
        catch { /* format absent */ }
        finally { if (got) ReleaseStgMedium(ref medium); }
        return paths;
    }

    /// <summary>Copies the raw HGLOBAL bytes for <paramref name="cf"/>, or null.</summary>
    private static byte[]? ReadBytes(ComTypes.IDataObject data, int cf)
    {
        if (cf == 0)
            return null;
        var fe = Fmt(cf);
        ComTypes.STGMEDIUM medium = default;
        bool got = false;
        try
        {
            data.GetData(ref fe, out medium);
            got = true;
            IntPtr h = medium.unionmember;
            if (h == IntPtr.Zero)
                return null;
            IntPtr p = GlobalLock(h);
            if (p == IntPtr.Zero)
                return null;
            try
            {
                int size = (int)GlobalSize(h);
                if (size <= 0)
                    return null;
                var bytes = new byte[size];
                Marshal.Copy(p, bytes, 0, size);
                return bytes;
            }
            finally { GlobalUnlock(h); }
        }
        catch { return null; }
        finally { if (got) ReleaseStgMedium(ref medium); }
    }

    private static ComTypes.FORMATETC Fmt(int cf) => new()
    {
        cfFormat = (short)cf,
        ptd = IntPtr.Zero,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
    };

    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr pvReserved);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(IntPtr hwnd, IOleDropTarget pDropTarget);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(IntPtr hwnd);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM pmedium);
    [DllImport("user32.dll", SetLastError = true)] private static extern int RegisterClipboardFormat(string lpszFormat);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint DragQueryFile(IntPtr hDrop, uint iFile, System.Text.StringBuilder? buf, uint cch);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr hMem);
}
