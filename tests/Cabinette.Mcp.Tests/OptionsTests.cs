using System.Text.RegularExpressions;

namespace Cabinette.Mcp.Tests;

/// <summary>The command line, the Debug --data-dir rule, the default data folder, and the client's display name.</summary>
public sealed partial class OptionsTests
{
    [Fact]
    public void WithoutOptionsAReleaseBuildUsesTheRealDataFolderAndTheCabinetteNextToIt()
    {
        var result = McpCommandLine.Parse([], requireDataDirectory: false);

        var options = Assert.IsType<McpOptions>(result.Options);
        Assert.Equal(McpPaths.DefaultDataDirectory, options.DataDirectory);
        Assert.False(options.IsDevDataDirectory);
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Cabinette.exe"), options.AppPath);
    }

    [Fact]
    public void FullDataDirAndAppAreNormalized()
    {
        var result = McpCommandLine.Parse(["--data-dir", @"C:\Work\Project\dev\data\", "--app", @"C:\Work\Project\..\bin\Cabinette.exe"], requireDataDirectory: true);

        var options = Assert.IsType<McpOptions>(result.Options);
        Assert.Equal(@"C:\Work\Project\dev\data", options.DataDirectory);
        Assert.True(options.IsDevDataDirectory);
        Assert.Equal(@"C:\Work\bin\Cabinette.exe", options.AppPath);
    }

    [Fact]
    public void ALauncherForARelativeCabinetteExeCantBeMade()
        => Assert.Throws<ArgumentException>(() => new CabinetteLauncher("Cabinette.exe", null, new NoStarter()));

    private sealed class NoStarter : IProcessStarter
    {
        public int Start(string exePath, IReadOnlyList<string> arguments, string workingDirectory) => throw new InvalidOperationException("nothing may be started");
    }

    [Fact]
    public void QuotedValuesAreAccepted()
    {
        var result = McpCommandLine.Parse(["--data-dir", "\"D:\\Cab data\""], requireDataDirectory: true);
        Assert.Equal(@"D:\Cab data", result.Options!.DataDirectory);
    }

    [Fact]
    public void ADebugBuildRefusesToRunWithoutDataDir()
    {
        var result = McpCommandLine.Parse([], requireDataDirectory: true);

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Debug build", result.Message);
        Assert.Contains("--data-dir", result.Message);
    }

    [Fact]
    public void ADebugBuildRefusesToRunWithOnlyApp()
    {
        var result = McpCommandLine.Parse(["--app", @"C:\x\Cabinette.exe"], requireDataDirectory: true);
        Assert.Equal(2, result.ExitCode);
        Assert.Null(result.Options);
    }

    [Fact]
    public void ThisDebugBuildRequiresDataDir()
    {
#if DEBUG
        Assert.True(BuildInfo.RequiresDataDirectory);
#else
        Assert.False(BuildInfo.RequiresDataDirectory);
#endif
    }

    [Theory]
    [InlineData("--verbose")]
    [InlineData("--data-dir=C:\\x")]
    [InlineData("--tray")]
    [InlineData("stdio")]
    public void UnknownArgumentsExitWithCode2(string argument)
    {
        var result = McpCommandLine.Parse(["--data-dir", @"C:\x", argument], requireDataDirectory: false);

        Assert.Null(result.Options);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains($"unknown argument \"{argument}\"", result.Message);
    }

    [Theory]
    [InlineData("--data-dir")]
    [InlineData("--app")]
    public void AnOptionWithoutValueExitsWithCode2(string option)
    {
        var result = McpCommandLine.Parse([option], requireDataDirectory: false);
        Assert.Equal(2, result.ExitCode);
        Assert.Contains("needs a value", result.Message);
    }

    [Fact]
    public void AnOptionGivenTwiceExitsWithCode2()
    {
        var result = McpCommandLine.Parse(["--data-dir", @"C:\a", "--data-dir", @"C:\b"], requireDataDirectory: false);
        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void HelpPrintsUsageAndExitsWithCode0()
    {
        var result = McpCommandLine.Parse(["--help"], requireDataDirectory: true);
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Usage: cabinette-mcp.exe", result.Message);
    }

    [Fact]
    public void TheDefaultDataFolderIsTheAppsOwn()
    {
        // The app's AppPaths.DefaultDataDirectory (pinned by its source text, since this project can't reference the app).
        var appPaths = File.ReadAllText(Path.Combine(TestTools.RepositoryRoot(), "src", "Cabinette.App", "Services", "AppPaths.cs"));
        var mcpPaths = File.ReadAllText(Path.Combine(TestTools.RepositoryRoot(), "src", "Cabinette.Mcp", "McpCommandLine.cs"));

        var appExpression = DefaultDataDirectoryExpression().Match(appPaths);
        var mcpExpression = DefaultDataDirectoryExpression().Match(mcpPaths);
        Assert.True(appExpression.Success, "AppPaths.DefaultDataDirectory not found");
        Assert.True(mcpExpression.Success, "McpPaths.DefaultDataDirectory not found");
        Assert.Equal(Normalize(appExpression.Groups["expr"].Value), Normalize(mcpExpression.Groups["expr"].Value));
        Assert.Equal(
            "Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),\"Cabinette\")",
            Normalize(mcpExpression.Groups["expr"].Value));

        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Cabinette"),
            McpPaths.DefaultDataDirectory);
    }

    [Theory]
    [InlineData("claude-code", "Claude Code", "Claude Code")]
    [InlineData("claude-code", null, "Claude Code")]
    [InlineData("Claude-Code ", "Something else", "Claude Code")]
    [InlineData("codex-mcp-client", "Codex", "Codex")]
    [InlineData("codex-mcp-client", null, "Codex")]
    [InlineData("codex", null, "Codex")]
    [InlineData("cursor-vscode", "Cursor", "Cursor")]
    [InlineData("my-agent", null, "my-agent")]
    [InlineData("my-agent", "   ", "my-agent")]
    [InlineData(null, null, "AI client")]
    [InlineData("", "", "AI client")]
    [InlineData("\u0007\u200B", null, "AI client")]
    public void ClientNamesComeFromTheClientInfo(string? name, string? title, string expected)
        => Assert.Equal(expected, ClientNames.FromClientInfo(name, title));

    [Theory]
    [InlineData("  My\tAgent \r\n v2  ", "My Agent v2")]
    [InlineData("Evil\u202Egnp.exe", "Evilgnp.exe")]
    [InlineData("Bell\u0007Ring", "BellRing")]
    [InlineData("Zero\u200BWidth", "ZeroWidth")]
    [InlineData("Line\u2028Break", "Line Break")]
    public void ClientNamesAreSanitized(string title, string expected)
        => Assert.Equal(expected, ClientNames.FromClientInfo("x", title));

    [Fact]
    public void ClientNamesAreCappedAt64Characters()
    {
        var name = ClientNames.FromClientInfo("x", new string('a', 63) + "\U0001F600tail");

        Assert.True(name.Length <= ClientNames.MaxLength);
        Assert.Equal(new string('a', 63), name); // the emoji's surrogate pair isn't split
        Assert.Equal(64, ClientNames.FromClientInfo("x", new string('b', 200)).Length);
    }

    private static string Normalize(string expression) => WhiteSpace().Replace(expression, "");

    [GeneratedRegex(@"DefaultDataDirectory\s*=>\s*(?<expr>[^;]+);")]
    private static partial Regex DefaultDataDirectoryExpression();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpace();
}
