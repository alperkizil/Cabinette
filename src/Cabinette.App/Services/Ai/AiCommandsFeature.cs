using Cabinette.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.Services.Ai;

/// <summary>DI registration for the "ai" command handler (the tools cabinette-mcp.exe calls). The composition root calls this once.</summary>
public static class AiCommandsFeature
{
    public static IServiceCollection AddAiCommands(this IServiceCollection services)
    {
        services.AddSingleton<AiActivityLog>();
        services.AddSingleton<IAiActivityLog>(sp => sp.GetRequiredService<AiActivityLog>());
        services.AddSingleton<AiCommandOperations>();

        // Resolve on the UI thread (the host keeps its dispatcher): the composition root does, when it opens the AiCommandGate.
        services.AddSingleton<IAiCommandHost, AiCommandAppHost>();
        services.AddSingleton<IAiVaultOperations>(sp => sp.GetRequiredService<VaultOperationsService>());

        // Reading contents comes from another stage 4 piece: optional, so a build without it answers read_file with "failed".
        services.AddSingleton(sp => new AiCommandHandler(
            sp.GetRequiredService<ILibraryService>(),
            sp.GetRequiredService<IAiActivityLog>(),
            sp.GetRequiredService<IAiChangeReporter>(),
            sp.GetRequiredService<IAiCommandHost>(),
            sp.GetRequiredService<IAiVaultOperations>(),
            sp.GetRequiredService<AiCommandOperations>(),
            sp.GetService<IFileContentReader>()));
        return services;
    }
}
