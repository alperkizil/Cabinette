using System.Threading;

namespace Cabinette.App.Services;

/// <summary>
/// Runs work on a fresh background STA thread. WPF rendering (DrawingVisual, RenderTargetBitmap), which the icon
/// generator may use, requires STA; the thread pool is MTA and the UI thread must stay free.
/// </summary>
public static class StaWorker
{
    public static Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
            finally
            {
                // Let WPF objects created on this thread release their dispatcher.
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = "Cabinette STA worker",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
