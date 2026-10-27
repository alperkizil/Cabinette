using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using Cabinette.Core.Models;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DataObject = System.Windows.DataObject;

namespace Cabinette.Windows.Tests;

/// <summary>
/// The Explorer entries' drop target, called directly in-process with data objects built here (WPF's, and the shell's own
/// for real files). Nothing is registered with COM or the registry.
/// </summary>
public sealed class ExplorerDropTargetTests
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);

    [Fact]
    public void Drag_enter_and_over_accept_as_a_copy()
    {
        var target = new ExplorerDropTarget(ItemMode.Copy, _ => throw new InvalidOperationException("no drop yet"));
        uint effect = 7;

        Assert.Equal(S_OK, target.DragEnter(null, 1, default, ref effect));
        Assert.Equal(DROPEFFECT_COPY, effect);

        effect = 0;
        Assert.Equal(S_OK, target.DragOver(1, default, ref effect));
        Assert.Equal(DROPEFFECT_COPY, effect);
        Assert.Equal(S_OK, target.DragLeave());
    }

    [Fact]
    public void Drop_hands_the_cf_hdrop_paths_on_as_one_batch_in_the_entrys_mode()
    {
        using var temp = new TempFolder();
        string[] files = [temp.File("a.txt"), temp.File("Fotoğraflar\\çay 1.jpg"), Path.Combine(temp.Path, "Fotoğraflar")];

        var drops = Sta.Run(() =>
        {
            var received = new List<ExplorerDrop>();
            var target = new ExplorerDropTarget(ItemMode.Pointer, received.Add);
            uint effect = DROPEFFECT_COPY;
            Assert.Equal(S_OK, target.Drop(FileDrop(files), 0, default, ref effect));
            Assert.Equal(DROPEFFECT_COPY, effect);
            return received;
        });

        var drop = Assert.Single(drops);
        Assert.Equal(ItemMode.Pointer, drop.Mode);
        Assert.Equal(files, drop.Paths);
    }

    [Fact]
    public void A_data_object_without_file_system_items_is_declined_without_a_batch()
    {
        var drops = Sta.Run(() =>
        {
            var received = new List<ExplorerDrop>();
            var target = new ExplorerDropTarget(ItemMode.Copy, received.Add);
            uint effect = DROPEFFECT_COPY;
            Assert.Equal(S_OK, target.Drop((ComDataObject)new DataObject(DataFormats.UnicodeText, "just text"), 0, default, ref effect));
            Assert.Equal(DROPEFFECT_NONE, effect);

            effect = DROPEFFECT_COPY;
            Assert.Equal(S_OK, target.Drop(null, 0, default, ref effect));
            Assert.Equal(DROPEFFECT_NONE, effect);
            return received;
        });

        Assert.Empty(drops);
    }

    [Fact]
    public void A_failing_callback_is_reported_and_explorer_gets_an_error()
    {
        using var temp = new TempFolder();
        var errors = Sta.Run(() =>
        {
            var reported = new List<Exception>();
            var target = new ExplorerDropTarget(ItemMode.Copy, _ => throw new InvalidOperationException("boom"), reported.Add);
            uint effect = DROPEFFECT_COPY;
            Assert.Equal(E_UNEXPECTED, target.Drop(FileDrop([temp.File("a.txt")]), 0, default, ref effect));
            Assert.Equal(DROPEFFECT_NONE, effect);
            return reported;
        });

        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
    }

    [Fact]
    public void The_shells_own_data_object_works_through_cf_hdrop_and_through_the_shell_item_fallback()
    {
        using var temp = new TempFolder();
        string[] items = [temp.File("one.txt"), temp.File("two words.pdf"), Path.Combine(temp.Path, "sub")];
        Directory.CreateDirectory(items[2]);

        var (viaHDrop, viaShellItems, hdropFromWrapper) = Sta.Run(() =>
        {
            // Explorer's data object type, for real files: CF_HDROP is the main path...
            var drops = new List<ExplorerDrop>();
            var target = new ExplorerDropTarget(ItemMode.Copy, drops.Add);
            uint effect = DROPEFFECT_COPY;
            Assert.Equal(S_OK, target.Drop(ShellDataObject(items), 0, default, ref effect));

            // ...and without CF_HDROP the shell's item list (Shell IDList Array) still yields the paths.
            var inner = ShellDataObject(items);
            try
            {
                var withoutHDrop = new WithoutFileDrop(inner);
                var hdrop = DataObjectPaths.ReadFileDrop(withoutHDrop);
                effect = DROPEFFECT_COPY;
                Assert.Equal(S_OK, target.Drop(withoutHDrop, 0, default, ref effect));
                Assert.Equal(DROPEFFECT_COPY, effect);
                return (drops[0], drops[1], hdrop);
            }
            finally
            {
                Marshal.FinalReleaseComObject(inner);
            }
        });

        Assert.Equal(items, viaHDrop.Paths, StringComparer.OrdinalIgnoreCase);
        Assert.Empty(hdropFromWrapper);
        Assert.Equal(items, viaShellItems.Paths, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_fully_qualified_paths_are_kept_once_each_in_order()
    {
        var clean = DataObjectPaths.Clean([@"C:\b.txt", "", "   ", @"relative\x.txt", @"C:x.txt", @"c:\B.TXT", @"\\server\share\c.txt", @"D:\a"]);

        Assert.Equal([@"C:\b.txt", @"\\server\share\c.txt", @"D:\a"], clean);
    }

    // ------------------------------------------------------------------ class factory

    [Fact]
    public void The_class_factory_creates_a_drop_target_for_its_entry()
    {
        using var temp = new TempFolder();
        var file = temp.File("a.txt");

        var drops = Sta.Run(() =>
        {
            var received = new List<ExplorerDrop>();
            var factory = new ExplorerDropTargetFactory(ItemMode.Pointer, received.Add);

            Assert.Equal(S_OK, factory.CreateInstance(IntPtr.Zero, ComIids.IDropTarget, out var pointer));
            Assert.NotEqual(IntPtr.Zero, pointer);
            try
            {
                var target = (IDropTarget)Marshal.GetObjectForIUnknown(pointer);
                uint effect = DROPEFFECT_COPY;
                Assert.Equal(S_OK, target.DragEnter(null, 1, default, ref effect));
                Assert.Equal(S_OK, target.Drop(FileDrop([file]), 0, default, ref effect));
            }
            finally
            {
                Marshal.Release(pointer);
            }

            // IUnknown works too; other interfaces and aggregation are refused.
            Assert.Equal(S_OK, factory.CreateInstance(IntPtr.Zero, ComIids.IUnknown, out var unknown));
            Marshal.Release(unknown);
            Assert.Equal(E_NOINTERFACE, factory.CreateInstance(IntPtr.Zero, ComIids.IClassFactory, out var none));
            Assert.Equal(IntPtr.Zero, none);
            Assert.Equal(CLASS_E_NOAGGREGATION, factory.CreateInstance(new IntPtr(1), ComIids.IDropTarget, out none));
            Assert.Equal(IntPtr.Zero, none);
            Assert.Equal(S_OK, factory.LockServer(true));
            Assert.Equal(S_OK, factory.LockServer(false));

            // What COM does with the object CoRegisterClassObject gets: its COM wrapper answers IClassFactory.
            var factoryUnknown = Marshal.GetIUnknownForObject(factory);
            try
            {
                Assert.Equal(S_OK, Marshal.QueryInterface(factoryUnknown, ComIids.IClassFactory, out var classFactory));
                Marshal.Release(classFactory);
            }
            finally
            {
                Marshal.Release(factoryUnknown);
            }

            return received;
        });

        var drop = Assert.Single(drops);
        Assert.Equal(ItemMode.Pointer, drop.Mode);
        Assert.Equal([file], drop.Paths);
    }

    /// <summary>
    /// Covers only the fallback for an unregistered server (Dropped raised on the thread pool). The path the app uses, posting
    /// to the UI thread's dispatcher after Drop returned, needs a registered server: see the opt-in ExplorerComRegistrationTests.
    /// </summary>
    [Fact]
    public async Task An_unregistered_server_raises_dropped_on_the_thread_pool_after_drop_returned()
    {
        using var temp = new TempFolder();
        var file = temp.File("a.txt");
        using var server = ExplorerDropServer.ForDevRuns();
        var dropped = new TaskCompletionSource<(ExplorerDrop Drop, int Thread)>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Dropped += (_, drop) => dropped.TrySetResult((drop, Environment.CurrentManagedThreadId));

        // Nothing is registered, so there is no UI thread to post to: the handlers are posted to the thread pool.
        var dropThread = Sta.Run(() =>
        {
            var factory = server.CreateFactory(ItemMode.Copy);
            Assert.Equal(S_OK, factory.CreateInstance(IntPtr.Zero, ComIids.IDropTarget, out var pointer));
            try
            {
                var target = (IDropTarget)Marshal.GetObjectForIUnknown(pointer);
                uint effect = DROPEFFECT_COPY;
                Assert.Equal(S_OK, target.Drop(FileDrop([file]), 0, default, ref effect));
                return Environment.CurrentManagedThreadId;
            }
            finally
            {
                Marshal.Release(pointer);
            }
        });

        var (received, handlerThread) = await dropped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(dropThread, handlerThread);
        Assert.Equal(ItemMode.Copy, received.Mode);
        Assert.Equal([file], received.Paths);
        Assert.False(server.IsRegistered);
    }

    [Fact]
    public void The_server_serves_the_installed_or_the_dev_classes()
    {
        using var installed = ExplorerDropServer.ForInstalledApp();
        using var dev = ExplorerDropServer.ForDevRuns();

        Assert.Equal(ExplorerIntegration.CopyDropTargetClsid, installed.CopyClsid);
        Assert.Equal(ExplorerIntegration.PointerDropTargetClsid, installed.PointerClsid);
        Assert.Equal(ExplorerIntegration.DevCopyDropTargetClsid, dev.CopyClsid);
        Assert.Equal(ExplorerIntegration.DevPointerDropTargetClsid, dev.PointerClsid);
        Assert.Equal(ItemMode.Copy, dev.CreateFactory(ItemMode.Copy).Mode);
    }

    [Fact]
    public void Registering_off_an_sta_thread_is_refused_before_anything_is_registered()
    {
        using var server = new ExplorerDropServer(Guid.NewGuid(), Guid.NewGuid());
        Exception? error = null;
        var thread = new Thread(() => error = Record.Exception(server.Register));
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();

        Assert.IsType<InvalidOperationException>(error);
        Assert.False(server.IsRegistered);
        server.Revoke(); // nothing to revoke: no-op
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>WPF's data object with a file list: GetData(CF_HDROP) renders a DROPFILES block, like a drag from Explorer.</summary>
    internal static ComDataObject FileDrop(string[] paths) => new DataObject(DataFormats.FileDrop, paths);

    /// <summary>The shell's own data object for real items (what Explorer passes): IShellItemArray → BHID_DataObject.</summary>
    private static ComDataObject ShellDataObject(string[] paths)
    {
        var pidls = new IntPtr[paths.Length];
        try
        {
            for (var i = 0; i < paths.Length; i++)
            {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _));
            }

            Marshal.ThrowExceptionForHR(SHCreateShellItemArrayFromIDLists((uint)pidls.Length, pidls, out var array));
            try
            {
                Marshal.ThrowExceptionForHR(array.BindToHandler(IntPtr.Zero, BhidDataObject, IidDataObject, out var pointer));
                try
                {
                    return (ComDataObject)Marshal.GetObjectForIUnknown(pointer);
                }
                finally
                {
                    Marshal.Release(pointer);
                }
            }
            finally
            {
                Marshal.FinalReleaseComObject(array);
            }
        }
        finally
        {
            foreach (var pidl in pidls)
            {
                Marshal.FreeCoTaskMem(pidl);
            }
        }
    }

    private static readonly Guid BhidDataObject = new("B8C0BD9F-ED24-455C-83E6-D5390C4FE8C4");
    private static readonly Guid IidDataObject = new("0000010E-0000-0000-C000-000000000046");

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHCreateShellItemArrayFromIDLists(
        uint cidl, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] IntPtr[] rgpidl, out IShellItemArray ppsiItemArray);

    /// <summary>Forwards to the shell's data object but hides CF_HDROP, so only the shell item list is left.</summary>
    private sealed class WithoutFileDrop(ComDataObject inner) : ComDataObject
    {
        private static bool IsHDrop(FORMATETC format) => format.cfFormat == unchecked((short)CF_HDROP);

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            if (IsHDrop(format))
            {
                throw new COMException("No CF_HDROP here.", DV_E_FORMATETC);
            }

            inner.GetData(ref format, out medium);
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium)
        {
            if (IsHDrop(format))
            {
                throw new COMException("No CF_HDROP here.", DV_E_FORMATETC);
            }

            inner.GetDataHere(ref format, ref medium);
        }

        public int QueryGetData(ref FORMATETC format) => IsHDrop(format) ? DV_E_FORMATETC : inner.QueryGetData(ref format);

        public int GetCanonicalFormatEtc(ref FORMATETC formatIn, out FORMATETC formatOut) => inner.GetCanonicalFormatEtc(ref formatIn, out formatOut);

        public void SetData(ref FORMATETC formatIn, ref STGMEDIUM medium, bool release) => inner.SetData(ref formatIn, ref medium, release);

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => inner.EnumFormatEtc(direction);

        public int DAdvise(ref FORMATETC pFormatetc, ADVF advf, IAdviseSink adviseSink, out int connection) => inner.DAdvise(ref pFormatetc, advf, adviseSink, out connection);

        public void DUnadvise(int connection) => inner.DUnadvise(connection);

        public int EnumDAdvise(out IEnumSTATDATA enumAdvise)
        {
            var hr = inner.EnumDAdvise(out var advise);
            enumAdvise = advise!;
            return hr;
        }
    }
}
