using System.Runtime.InteropServices;
using System.Windows.Threading;
using Cabinette.Core.Models;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;
using IDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Cabinette.Windows;

/// <summary>
/// Serves the Explorer entries' COM classes from this process (a COM local server). Explorer, or COM starting
/// Cabinette.exe with <c>-Embedding</c>, gets a drop target from the registered class object and calls DragEnter, then
/// Drop with the whole selection. Drop only reads the paths; <see cref="Dropped"/> runs after Drop has returned.
/// </summary>
public sealed class ExplorerDropServer : IExplorerDropServer
{
    private readonly List<uint> _cookies = [];
    private SynchronizationContext? _context;

    public ExplorerDropServer(Guid copyClsid, Guid pointerClsid)
    {
        CopyClsid = copyClsid;
        PointerClsid = pointerClsid;
    }

    /// <summary>The installed Cabinette's classes ("Copy to Cabinette", "Add to Cabinette (Pointer)").</summary>
    public static ExplorerDropServer ForInstalledApp()
        => new(ExplorerIntegration.CopyDropTargetClsid, ExplorerIntegration.PointerDropTargetClsid);

    /// <summary>The dev entries' classes, served by <c>--data-dir</c> runs.</summary>
    public static ExplorerDropServer ForDevRuns()
        => new(ExplorerIntegration.DevCopyDropTargetClsid, ExplorerIntegration.DevPointerDropTargetClsid);

    public Guid CopyClsid { get; }

    public Guid PointerClsid { get; }

    public bool IsRegistered => _cookies.Count > 0;

    public event EventHandler<ExplorerDrop>? Dropped;

    public event EventHandler<Exception>? DropFailed;

    public void Register()
    {
        if (IsRegistered)
        {
            return;
        }

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("The Explorer drop targets must be registered on an STA thread (the UI thread).");
        }

        // COM delivers the calls through this thread's message loop, which is the dispatcher's: post the handlers there
        // too, so they (and a Revoke from them) run on the registering thread.
        _context = SynchronizationContext.Current ?? new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher);
        try
        {
            RegisterClass(CopyClsid, ItemMode.Copy);
            RegisterClass(PointerClsid, ItemMode.Pointer);
        }
        catch
        {
            Revoke();
            throw;
        }
    }

    public void Revoke()
    {
        foreach (var cookie in _cookies)
        {
            _ = CoRevokeClassObject(cookie);
        }

        _cookies.Clear();
    }

    public void Dispose() => Revoke();

    /// <summary>The class object for one entry, as COM gets it (exposed for tests: nothing is registered).</summary>
    internal ExplorerDropTargetFactory CreateFactory(ItemMode mode) => new(mode, Deliver, ReportFailure);

    private void RegisterClass(Guid clsid, ItemMode mode)
    {
        var hr = CoRegisterClassObject(clsid, CreateFactory(mode), CLSCTX_LOCAL_SERVER, REGCLS_MULTIPLEUSE, out var cookie);
        Marshal.ThrowExceptionForHR(hr);
        _cookies.Add(cookie);
    }

    // Called inside Explorer's Drop (or a factory call), so it never runs the handlers there: they are posted.
    private void Deliver(ExplorerDrop drop) => Post(() => Dropped?.Invoke(this, drop));

    private void ReportFailure(Exception ex) => Post(() => DropFailed?.Invoke(this, ex));

    private void Post(Action action)
    {
        if (_context is { } context)
        {
            context.Post(_ => action(), null);
        }
        else
        {
            // Not registered (a factory made directly, as the tests do): nothing to return to.
            ThreadPool.QueueUserWorkItem(_ => action());
        }
    }
}

/// <summary>The class object COM hands out for one entry: every CreateInstance makes a new <see cref="ExplorerDropTarget"/>.</summary>
internal sealed class ExplorerDropTargetFactory : IClassFactory
{
    private readonly Action<ExplorerDrop> _onDrop;
    private readonly Action<Exception>? _onError;

    public ExplorerDropTargetFactory(ItemMode mode, Action<ExplorerDrop> onDrop, Action<Exception>? onError = null)
    {
        Mode = mode;
        _onDrop = onDrop;
        _onError = onError;
    }

    public ItemMode Mode { get; }

    public int CreateInstance(IntPtr pUnkOuter, in Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero)
        {
            return CLASS_E_NOAGGREGATION;
        }

        try
        {
            var unknown = Marshal.GetIUnknownForObject(new ExplorerDropTarget(Mode, _onDrop, _onError));
            try
            {
                return Marshal.QueryInterface(unknown, riid, out ppvObject);
            }
            finally
            {
                Marshal.Release(unknown);
            }
        }
        catch (Exception ex)
        {
            Report(_onError, ex);
            return E_UNEXPECTED;
        }
    }

    // The app's lifetime doesn't depend on COM's lock count: it runs until the user quits (or, when started only to
    // forward a drop, until that is done).
    public int LockServer(bool fLock) => S_OK;

    internal static void Report(Action<Exception>? onError, Exception ex)
    {
        try
        {
            onError?.Invoke(ex);
        }
        catch (Exception)
        {
            // Reporting must never turn into a failed COM call.
        }
    }
}

/// <summary>
/// One Explorer entry's drop target. DragEnter and DragOver accept with DROPEFFECT_COPY; Drop reads the file-system paths
/// and hands them on as one batch, then returns. It never waits for the files to be added.
/// </summary>
internal sealed class ExplorerDropTarget : IDropTarget
{
    private readonly Action<ExplorerDrop> _onDrop;
    private readonly Action<Exception>? _onError;

    public ExplorerDropTarget(ItemMode mode, Action<ExplorerDrop> onDrop, Action<Exception>? onError = null)
    {
        Mode = mode;
        _onDrop = onDrop;
        _onError = onError;
    }

    public ItemMode Mode { get; }

    public int DragEnter(IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        pdwEffect = DROPEFFECT_COPY;
        return S_OK;
    }

    public int DragOver(uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        pdwEffect = DROPEFFECT_COPY;
        return S_OK;
    }

    public int DragLeave() => S_OK;

    public int Drop(IDataObject? pDataObj, uint grfKeyState, POINTL pt, ref uint pdwEffect)
    {
        try
        {
            var paths = pDataObj is null ? [] : DataObjectPaths.Read(pDataObj);
            if (paths.Count == 0)
            {
                pdwEffect = DROPEFFECT_NONE;
                return S_OK;
            }

            pdwEffect = DROPEFFECT_COPY;
            _onDrop(new ExplorerDrop(Mode, paths));
            return S_OK;
        }
        catch (Exception ex)
        {
            pdwEffect = DROPEFFECT_NONE;
            ExplorerDropTargetFactory.Report(_onError, ex);
            return E_UNEXPECTED;
        }
        finally
        {
            // Explorer's data object (a proxy) is no longer needed: let it go now instead of at the next GC.
            ReleaseComObject(pDataObj);
        }
    }
}
