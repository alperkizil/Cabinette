using Cabinette.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cabinette.App.Services.Ai;

/// <summary>DI registration for reading file contents (the read_file tool). The composition root calls this once.</summary>
public static class AiContentFeature
{
    /// <summary>
    /// <see cref="IFileContentReader"/>, and <see cref="ITextExtractor"/> as Windows' IFilters run in a hidden child process
    /// (<c>Cabinette.exe --extract-text</c>), never in Cabinette's own.
    /// </summary>
    public static IServiceCollection AddAiContentReading(this IServiceCollection services)
    {
        services.TryAddSingleton<ITextExtractor>(_ => new IsolatedTextExtractor(IsolatedTextExtractor.DefaultExecutablePath()));
        services.TryAddSingleton<IFileContentReader, FileContentReader>();
        return services;
    }
}
