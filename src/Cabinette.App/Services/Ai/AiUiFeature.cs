using Cabinette.App.ViewModels.Dialogs;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.Services.Ai;

/// <summary>DI registration for the AI UI: the grouped toasts and Settings › AI. The composition root calls this once.</summary>
public static class AiUiFeature
{
    public static IServiceCollection AddAiUi(this IServiceCollection services)
    {
        // The grouped toasts. The app creates the notifier on the UI thread at startup (its timer belongs there); the "ai"
        // handler then gets the same instance on the local channel's threads.
        services.AddSingleton<AiChangeNotifier>();
        services.AddSingleton<IAiChangeReporter>(sp => sp.GetRequiredService<AiChangeNotifier>());

        // Settings › AI: copy buttons and the section's view model (one per Settings dialog).
        services.AddSingleton<IClipboardWriter, WpfClipboardWriter>();
        services.AddTransient<AiSettingsViewModel>();
        services.AddSingleton<Func<AiSettingsViewModel>>(sp => () => sp.GetRequiredService<AiSettingsViewModel>());
        return services;
    }
}
