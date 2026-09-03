using System.Drawing;
using System.Runtime.InteropServices;

namespace FolderVault.Core.Shell;

/// <summary>
/// Keeps a desktop item where the user put it across a lock or unlock.
///
/// <para>Locking swaps a folder for a <c>.lnk</c> of the same displayed name, but as far as the
/// desktop is concerned that is a different item: it remembers positions per file name, and
/// <c>Photos</c> and <c>Photos.lnk</c> are two names. The old position is dropped and the new item
/// lands in the first free slot, so a folder the user had parked in a corner jumps to the top-left
/// every time it is locked, and again when it is unlocked.</para>
///
/// <para>The fix is to ask the live desktop view where the item was, and to put its replacement
/// back there: <c>IFolderView::GetItemPosition</c> before the swap,
/// <c>IFolderView::SelectAndPositionItems</c> after it. Reading the position out of the registry
/// instead would mean parsing an undocumented binary blob whose layout has changed between Windows
/// releases; the view interface is documented and has not.</para>
///
/// <para>Everything here is best-effort. An icon in the wrong place is a papercut, never a reason
/// to fail a lock, so every entry point swallows its failures and reports them as "no".</para>
/// </summary>
public static class DesktopIcons
{
    /// <summary>True if <paramref name="path"/> sits directly on the desktop, user or all-users.</summary>
    public static bool IsOnDesktop(string path)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(parent)) return false;

        return Same(parent, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory))
            || Same(parent, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));

        static bool Same(string a, string b) =>
            b.Length > 0 && string.Equals(a.TrimEnd(Path.DirectorySeparatorChar),
                b.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Where the desktop is currently drawing <paramref name="path"/>, or null if it is not a
    /// desktop item, is not there, or the desktop is auto-arranging - in which case Explorer
    /// decides positions and there is nothing to preserve.
    /// </summary>
    public static Point? TryGetPosition(string path)
    {
        if (!IsOnDesktop(path)) return null;

        return InShellThread<Point?>(() =>
        {
            using var view = DesktopView.Open();
            if (view is null || view.IsAutoArranged) return null;
            return view.GetPosition(path);
        });
    }

    /// <summary>
    /// Puts <paramref name="path"/> at <paramref name="position"/> on the desktop.
    ///
    /// <para>Two kinds of waiting are needed. First the item has to exist in the view at all:
    /// Explorer learns about a newly created file from a change notification that arrives on its
    /// own schedule, and positioning an item it has not seen yet silently does nothing.</para>
    ///
    /// <para>Then the placement has to stick. Explorer does its own placement in response to that
    /// same notification, and if it lands after ours it wins - which showed up as the unlocked
    /// folder reappearing a grid cell or so away from where it had been. So the position is read
    /// back and reapplied until it settles: equal to what was asked for, or unchanged between two
    /// attempts, which is what a snap-to-grid adjustment looks like and is the correct answer
    /// rather than something to fight.</para>
    /// </summary>
    public static bool TryPlaceAt(string path, Point position)
    {
        if (!IsOnDesktop(path)) return false;

        return InShellThread(() =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
            var placed = false;
            Point? previous = null;

            do
            {
                using (var view = DesktopView.Open())
                {
                    if (view is null) return placed;
                    if (view.IsAutoArranged) return false;

                    if (view.TryPlaceAt(path, position))
                    {
                        placed = true;
                        var landed = view.GetPosition(path);
                        if (landed == position || landed == previous) return true;
                        previous = landed;
                    }
                }
                Thread.Sleep(120);
            }
            while (DateTime.UtcNow < deadline);

            return placed;
        });
    }

    /// <summary>
    /// Runs shell COM work on an STA thread. Locking happens on a worker thread so a large Secure
    /// vault does not freeze the UI, and the shell view interfaces are not safe to call from an
    /// MTA thread; borrowing a short-lived STA thread is cheaper than making the whole operation
    /// marshal back to the UI.
    /// </summary>
    private static T? InShellThread<T>(Func<T?> work)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return Run();

        T? result = default;
        var thread = new Thread(() => result = Run()) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(TimeSpan.FromSeconds(10));
        return result;

        T? Run()
        {
            try
            {
                return work();
            }
            catch (Exception ex) when (ex is COMException or InvalidCastException or
                                           UnauthorizedAccessException or ArgumentException)
            {
                // The desktop is mid-refresh, Explorer is restarting, or this build does not
                // expose the view. Either way the icon keeps Explorer's own placement.
                return default;
            }
        }
    }

    /// <summary>The live desktop shell view, and the item lookups worth doing against it.</summary>
    private sealed class DesktopView : IDisposable
    {
        private readonly IFolderView _view;

        private DesktopView(IFolderView view) => _view = view;

        /// <summary>
        /// Finds Explorer's desktop window and takes its active view. Returns null when there is
        /// no desktop to talk to - Explorer restarting, or a session with no shell.
        /// </summary>
        public static DesktopView? Open()
        {
            var shellWindows = (IShellWindows)new ShellWindows();

            object location = CsidlDesktop;
            object root = null!;
            shellWindows.FindWindowSW(ref location, ref root, SwcDesktop, out _, SwfoNeedDispatch,
                out var dispatch);
            if (dispatch is null) return null;

            var provider = (IServiceProvider)dispatch;
            var browserId = typeof(IShellBrowser).GUID;
            var serviceId = SidSTopLevelBrowser;
            provider.QueryService(ref serviceId, ref browserId, out var browserPointer);
            if (browserPointer == nint.Zero) return null;

            IShellBrowser browser;
            try
            {
                browser = (IShellBrowser)Marshal.GetObjectForIUnknown(browserPointer);
            }
            finally
            {
                Marshal.Release(browserPointer);
            }

            browser.QueryActiveShellView(out var shellView);
            return shellView is IFolderView view ? new DesktopView(view) : null;
        }

        /// <summary>
        /// True when Explorer is placing icons itself. <c>GetAutoArrange</c> answers in the
        /// HRESULT - S_OK for on, S_FALSE for off - so it has to be read as a status, not caught
        /// as an error.
        /// </summary>
        public bool IsAutoArranged => _view.GetAutoArrange() == 0;

        public Point? GetPosition(string path)
        {
            using var item = ShellItemId.Parse(path);
            if (item is null) return null;

            if (_view.GetItemPosition(item.Child, out var point) != 0) return null;
            return new Point(point.X, point.Y);
        }

        public bool TryPlaceAt(string path, Point position)
        {
            using var item = ShellItemId.Parse(path);
            if (item is null) return false;

            // GetItemPosition doubles as the existence check: SelectAndPositionItems silently
            // does nothing for an item the view has not picked up yet, and would look like success.
            if (_view.GetItemPosition(item.Child, out _) != 0) return false;

            var point = new POINT { X = position.X, Y = position.Y };
            return _view.SelectAndPositionItems(1, [item.Child], [point],
                SvsiPositionItem | SvsiNoStateChange) == 0;
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(_view)) Marshal.ReleaseComObject(_view);
        }
    }

    /// <summary>
    /// A parsed shell ID list, and the child ID within it that identifies the item to its parent
    /// folder - which is what <c>IFolderView</c> takes.
    /// </summary>
    private sealed class ShellItemId : IDisposable
    {
        private nint _absolute;

        private ShellItemId(nint absolute) => _absolute = absolute;

        /// <summary>Valid only while the owning <see cref="ShellItemId"/> is alive.</summary>
        public nint Child => ILFindLastID(_absolute);

        public static ShellItemId? Parse(string path)
        {
            if (SHParseDisplayName(path, nint.Zero, out var pidl, 0, out _) != 0 || pidl == nint.Zero)
                return null;
            return new ShellItemId(pidl);
        }

        public void Dispose()
        {
            if (_absolute == nint.Zero) return;
            Marshal.FreeCoTaskMem(_absolute);
            _absolute = nint.Zero;
        }
    }

    // ---- Shell interop. Method order below must match each vtable exactly. ----

    private const int CsidlDesktop = 0;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvsiPositionItem = 0x80;
    private const uint SvsiNoStateChange = 0x80000000;

    private static Guid SidSTopLevelBrowser = new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(string pszName, nint pbc, out nint ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", EntryPoint = "#16")]
    private static extern nint ILFindLastID(nint pidl);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [ComImport, Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39")]
    private class ShellWindows;

    /// <summary>
    /// Explorer's window collection. Declared IUnknown-style with the four IDispatch slots spelled
    /// out, because only the vtable position of <c>FindWindowSW</c> matters and the dispatch
    /// plumbing would otherwise shift it by four.
    /// </summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85")]
    private interface IShellWindows
    {
        void GetTypeInfoCount(out uint pctinfo);
        void GetTypeInfo(uint iTInfo, uint lcid, out nint ppTInfo);
        void GetIDsOfNames(ref Guid riid, nint rgszNames, uint cNames, uint lcid, nint rgDispId);
        void Invoke(int dispIdMember, ref Guid riid, uint lcid, ushort wFlags, nint pDispParams,
            nint pVarResult, nint pExcepInfo, nint puArgErr);

        void get_Count(out int count);
        void Item(object index, [MarshalAs(UnmanagedType.IDispatch)] out object folder);
        void _NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        void Register([MarshalAs(UnmanagedType.IDispatch)] object window, int hwnd, int swClass,
            out int cookie);
        void RegisterPending(int threadId, ref object location, ref object locationRoot, int swClass,
            out int cookie);
        void Revoke(int cookie);
        void OnNavigate(int cookie, ref object location);
        void OnActivated(int cookie, [MarshalAs(UnmanagedType.Bool)] bool active);
        void FindWindowSW(ref object location, ref object locationRoot, int swClass, out int hwnd,
            int options, [MarshalAs(UnmanagedType.IDispatch)] out object dispatch);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider
    {
        void QueryService(ref Guid guidService, ref Guid riid, out nint ppvObject);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    private interface IShellBrowser
    {
        // IOleWindow
        void GetWindow(out nint hwnd);
        void ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enterMode);

        // IShellBrowser
        void InsertMenusSB(nint hmenuShared, nint menuWidths);
        void SetMenuSB(nint hmenuShared, nint holemenuRes, nint hwndActiveObject);
        void RemoveMenusSB(nint hmenuShared);
        void SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string statusText);
        void EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enable);
        void TranslateAcceleratorSB(nint msg, ushort id);
        void BrowseObject(nint pidl, uint flags);
        void GetViewStateStream(uint mode, out nint stream);
        void GetControlWindow(uint id, out nint hwnd);
        void SendControlMsg(uint id, uint message, nint wParam, nint lParam, out nint result);
        void QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object shellView);
        void OnViewWindowActive([MarshalAs(UnmanagedType.IUnknown)] object shellView);
        void SetToolbarItems(nint buttons, uint count, uint flags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("CDE725B0-CCC9-4519-917E-325D72FAB4CE")]
    private interface IFolderView
    {
        void GetCurrentViewMode(out uint viewMode);
        void SetCurrentViewMode(uint viewMode);
        void GetFolder(ref Guid riid, out nint ppv);
        void Item(int itemIndex, out nint ppidl);
        void ItemCount(uint flags, out int count);
        void Items(uint flags, ref Guid riid, out nint ppv);
        void GetSelectionMarkedItem(out int item);
        void GetFocusedItem(out int item);

        // Status-carrying results: an item that is not in the view, or a view that is auto
        // arranging, are ordinary answers rather than exceptions.
        [PreserveSig] int GetItemPosition(nint pidl, out POINT point);

        void GetSpacing(out POINT spacing);
        void GetDefaultSpacing(out POINT spacing);
        [PreserveSig] int GetAutoArrange();
        void SelectItem(int item, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count,
            [MarshalAs(UnmanagedType.LPArray)] nint[] apidl,
            [MarshalAs(UnmanagedType.LPArray)] POINT[] points, uint flags);
    }
}
