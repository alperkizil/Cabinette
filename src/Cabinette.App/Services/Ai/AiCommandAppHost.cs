using System.Windows;
using System.Windows.Threading;

namespace Cabinette.App.Services.Ai;

/// <summary>The running app for the "ai" command handler: <see cref="AppLifetime"/>, the main window and the UI thread. Create on the UI thread.</summary>
public sealed class AiCommandAppHost : IAiCommandHost
{
    private readonly AppLifetime _lifetime;
    private readonly IShellNavigator _navigator;
    private readonly Dispatcher _dispatcher;

    public AiCommandAppHost(AppLifetime lifetime, IShellNavigator navigator)
    {
        _lifetime = lifetime;
        _navigator = navigator;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
    }

    public bool IsQuitting => _lifetime.IsQuitting;

    /// <summary>Asked on the UI thread, where operations begin and end.</summary>
    public Task<bool> IsOperationRunningAsync() => _dispatcher.InvokeAsync(() => _lifetime.IsOperationRunning).Task;

    public Task ShowAsync(long categoryId, long? focusItemId) => _dispatcher.InvokeAsync(() =>
    {
        _lifetime.ActivateMainWindow();
        _navigator.OpenCategory(categoryId, focusItemId);
    }).Task;

    public Task<T> InvokeOnUiAsync<T>(Func<Task<T>> action) => _dispatcher.InvokeAsync(action).Task.Unwrap();
}
