using System.Text.Json;
using Cabinette.Ipc;
using Cabinette.Mcp.Tools;
using ModelContextProtocol.Protocol;

namespace Cabinette.Mcp.Tests;

/// <summary>Tests that start real processes never run next to each other (one of them watches handle inheritance).</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessCollection
{
    public const string Name = "Processes";
}

/// <summary>A launcher for tests that must never start anything: it counts calls and fails like a missing Cabinette.exe.</summary>
internal sealed class RefusingLauncher : ICabinetteLauncher
{
    public int Starts { get; private set; }

    public void Start()
    {
        Starts++;
        throw new CabinetteUnavailableException("test: there is no Cabinette to start");
    }
}

internal static class TestTools
{
    public const string WorkingDirectory = @"C:\Work\Project";

    public static ConnectionTimings FastTimings { get; } = new()
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        StartTimeout = TimeSpan.FromSeconds(2),
        StartPollInterval = TimeSpan.FromMilliseconds(20),
        NotReadyRetryInterval = TimeSpan.FromMilliseconds(20),
        NotReadyTimeout = TimeSpan.FromMilliseconds(300),
    };

    public static CabinetteTool Tool(string name, FakeCabinette cabinette, ConnectionTimings? timings = null)
        => new(
            ToolCatalog.Find(name) ?? throw new ArgumentException($"no tool {name}"),
            new CabinetteConnection(cabinette.PipeName, new RefusingLauncher(), timings ?? FastTimings),
            () => WorkingDirectory);

    public static IDictionary<string, JsonElement> Args(string json)
        => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];

    public static string Text(CallToolResult result, int index = 0) => ((TextContentBlock)result.Content[index]).Text;

    /// <summary>The record as the app's side of the channel would see it (AiProtocol's JSON).</summary>
    public static string ArgsJson<T>(T args) => JsonSerializer.Serialize(args, AiProtocol.JsonOptions);

    /// <summary>The folder with Cabinette.slnx.</summary>
    public static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Cabinette.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Cabinette.slnx not found above the test output.");
    }
}
