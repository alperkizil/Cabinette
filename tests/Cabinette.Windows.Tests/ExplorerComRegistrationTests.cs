using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Cabinette.Core.Models;
using Cabinette.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using DataObject = System.Windows.DataObject;

namespace Cabinette.Windows.Tests;

/// <summary>
/// Registers class objects with COM (CoRegisterClassObject: visible to the whole Windows session while the test runs, under
/// fresh random class ids that nothing else uses; no registry writes). Only runs when CABINETTE_COM_TESTS=1.
/// </summary>
public sealed class ExplorerComRegistrationTests(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("CABINETTE_COM_TESTS") == "1";

    [Fact]
    public void Registered_classes_hand_out_drop_targets_through_com()
    {
        if (!Enabled)
        {
            output.WriteLine("Set CABINETTE_COM_TESTS=1 to run this test (it registers COM class objects for a moment).");
            return;
        }

        using var temp = new TempFolder();
        string[] files = [temp.File("a.txt"), temp.File("b.txt")];

        var (drop, createHr, dropHr, effect, registeredWhileServing, registeredAfterRevoke) = Sta.Run(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            using var server = new ExplorerDropServer(Guid.NewGuid(), Guid.NewGuid());
            ExplorerDrop? received = null;
            server.Dropped += (_, d) => received = d;
            server.Register();
            var registered = server.IsRegistered;

            // Act as Explorer from another thread: activate the pointer class through COM, then DragEnter and Drop.
            int create = 0, dropResult = 0;
            uint dropEffect = 0;
            var caller = new Thread(() =>
            {
                create = CoCreateInstance(server.PointerClsid, IntPtr.Zero, CLSCTX_LOCAL_SERVER, ComIids.IDropTarget, out var instance);
                if (create < 0)
                {
                    return;
                }

                var target = (IDropTarget)instance;
                var data = (ComDataObject)new DataObject(DataFormats.FileDrop, files);
                uint enter = DROPEFFECT_COPY;
                _ = target.DragEnter(data, 1, default, ref enter);
                dropEffect = DROPEFFECT_COPY;
                dropResult = target.Drop(data, 0, default, ref dropEffect);
                Marshal.ReleaseComObject(target);
            });
            caller.SetApartmentState(ApartmentState.MTA);
            caller.Start();

            Assert.True(Sta.PumpUntil(() => !caller.IsAlive && (received is not null || create < 0), TimeSpan.FromSeconds(30)), "No drop arrived.");
            server.Revoke();
            return (received, create, dropResult, dropEffect, registered, server.IsRegistered);
        });

        Assert.True(registeredWhileServing);
        Assert.False(registeredAfterRevoke);
        Assert.Equal(S_OK, createHr);
        Assert.Equal(S_OK, dropHr);
        Assert.Equal(DROPEFFECT_COPY, effect);
        Assert.NotNull(drop);
        Assert.Equal(ItemMode.Pointer, drop.Mode);
        Assert.Equal(files, drop.Paths);
    }

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoCreateInstance(
        in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
}
