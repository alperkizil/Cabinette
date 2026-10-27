using System.Windows;

namespace Cabinette.App.Services;

/// <summary>
/// Opens threads in the right-hand pane. The main view model registers itself as the handler, which keeps
/// this service free of a dependency cycle (thread view models can take <see cref="IShellNavigator"/>).
/// </summary>
public sealed class ShellNavigator : IShellNavigator
{
    private Func<long, long?, Task>? _handler;

    public long? CurrentCategoryId { get; internal set; }

    internal void SetHandler(Func<long, long?, Task> handler) => _handler = handler;

    public void OpenCategory(long categoryId, long? focusItemId = null)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _ = InvokeAsync(categoryId, focusItemId);
        }
        else
        {
            dispatcher.BeginInvoke(() => _ = InvokeAsync(categoryId, focusItemId));
        }
    }

    private async Task InvokeAsync(long categoryId, long? focusItemId)
    {
        if (_handler is null)
        {
            return;
        }

        try
        {
            await _handler(categoryId, focusItemId);
        }
        catch (Exception ex)
        {
            AppLog.Error($"Opening category {categoryId} failed.", ex);
        }
    }
}
