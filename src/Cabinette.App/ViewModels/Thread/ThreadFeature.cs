using Cabinette.App.Services;
using Cabinette.Core.Services;
using Cabinette.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>DI registration for everything the thread workstream owns. The shell's composition root calls this once.</summary>
public static class ThreadFeature
{
    public static IServiceCollection AddThreadFeature(this IServiceCollection services)
    {
        services.AddSingleton<ThumbnailLoader>();
        // The Gallery's cell size is kept in the app's state file when the app has one (not in sandboxed dev runs).
        services.AddSingleton(sp => ThreadViewState.Create(sp.GetService<AppStateStore>(), sp.GetService<CommandLineOptions>()));
        services.AddSingleton<IThreadViewModelFactory, ThreadViewModelFactory>();
        return services;
    }

    /// <summary>Resolves dependencies lazily so the shell can register its services in any order.</summary>
    private sealed class ThreadViewModelFactory(IServiceProvider provider) : IThreadViewModelFactory
    {
        public ThreadViewModel Create() => new(new ThreadServices(
            provider.GetRequiredService<ILibraryService>(),
            provider.GetRequiredService<IShellContextMenuService>(),
            provider.GetRequiredService<IShellOperations>(),
            provider.GetRequiredService<IFileIntake>(),
            provider.GetRequiredService<IDialogService>(),
            provider.GetRequiredService<INotificationService>(),
            provider.GetRequiredService<IShellNavigator>(),
            provider.GetRequiredService<ThumbnailLoader>(),
            provider.GetRequiredService<ThreadViewState>())
        {
            Directory = provider.GetService<CategoryDirectory>(),
            IconGenerator = provider.GetService<ICategoryIconGenerator>(),
        });
    }
}
