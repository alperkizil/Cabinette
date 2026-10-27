using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cabinette.Ipc;
using Cabinette.Mcp.Tools;

namespace Cabinette.Mcp.Tests;

/// <summary>The tool list matches AiTools and the args records, and says what the model needs to know.</summary>
public sealed class ToolCatalogTests
{
    private static readonly string[] ExpectedDestructive =
    [
        AiTools.UntagFiles, AiTools.RemoveFromCategory, AiTools.MoveFiles, AiTools.RenameFile, AiTools.DeleteFiles,
        AiTools.UnstarFiles, AiTools.RenameCategory, AiTools.DeleteCategory, AiTools.MoveVault,
    ];

    [Fact]
    public void ThereIsOneToolPerAiToolsConstantWithTheSameName()
    {
        var constants = typeof(AiTools).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .Order()
            .ToList();

        Assert.Equal(constants, ToolCatalog.All.Select(t => t.Name).Order().ToList());
        Assert.Equal(AiTools.All.Order(), ToolCatalog.All.Select(t => t.Name).Order());
    }

    [Fact]
    public void EachSchemaHasExactlyTheArgsRecordsProperties()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.True(tool.ArgsType.Namespace == "Cabinette.Ipc" && tool.ArgsType.Name.EndsWith("Args", StringComparison.Ordinal), tool.Name);
            AssertMatches(tool.Name, tool.ArgsType, tool.Input);
        }
    }

    [Fact]
    public void TheFilterSchemaHasExactlyAiFiltersProperties()
        => AssertMatches("filter", typeof(AiFilter), ToolCatalog.FilterSchema());

    [Fact]
    public void EveryToolAndParameterIsDescribed()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.False(string.IsNullOrWhiteSpace(tool.Title), tool.Name);
            AssertDescribed(tool.Name, tool.Input.ToJsonObject());
        }
    }

    [Fact]
    public void ReadingToolsAreReadOnlyAndTheRightOnesDestructive()
    {
        foreach (var tool in ToolCatalog.All)
        {
            Assert.Equal(AiTools.Reading.Contains(tool.Name), tool.ReadOnly);
            Assert.Equal(ExpectedDestructive.Contains(tool.Name), tool.Destructive);
            if (tool.ReadOnly)
            {
                Assert.True(tool.Idempotent, tool.Name);
            }
        }

        Assert.False(ToolCatalog.Find(AiTools.ShowInCabinette)!.ReadOnly); // it opens a window on the user's screen
    }

    /// <summary>
    /// README.md says Codex asks before the tools marked destructive (seen with Codex 0.159) and names some on each side; the
    /// names match the flags.
    /// </summary>
    [Fact]
    public void TheReadmesCodexApprovalNoteNamesToolsByTheirDestructiveFlag()
    {
        var readme = File.ReadAllText(Path.Combine(TestTools.RepositoryRoot(), "README.md"));
        var line = Assert.Single(readme.Split('\n'), l => l.Contains("**Codex asks only before the tools Cabinette marks as destructive**", StringComparison.Ordinal));
        var split = line.IndexOf("Reads and the other changes", StringComparison.Ordinal);
        Assert.True(split > 0, line);

        static List<ToolDefinition> Named(string text) => System.Text.RegularExpressions.Regex.Matches(text, "`([a-z_]+)`")
            .Select(m => ToolCatalog.Find(m.Groups[1].Value)).OfType<ToolDefinition>().ToList();
        var destructive = Named(line[..split]);
        var others = Named(line[split..]);
        Assert.NotEmpty(destructive);
        Assert.NotEmpty(others);
        Assert.All(destructive, tool => Assert.True(tool.Destructive, tool.Name));
        Assert.All(others, tool => Assert.False(tool.Destructive || tool.ReadOnly, tool.Name));
    }

    [Fact]
    public void TheDescriptionsSayWhatTheModelNeedsToKnow()
    {
        string Description(string name) => ToolCatalog.Find(name)!.Description;

        foreach (var name in new[] { AiTools.MoveVault, AiTools.MigratePointers, AiTools.CreateBackup })
        {
            Assert.Contains("returns at once", Description(name));
            Assert.Contains("get_operation_status", Description(name));
            Assert.Contains("busy", Description(name));
        }

        Assert.Contains("Recycle Bin", Description(AiTools.DeleteFiles));
        Assert.Contains("without asking", Description(AiTools.DeleteFiles));
        Assert.Contains("Recycle Bin", Description(AiTools.DeleteCategory));
        Assert.Contains("user's screen", Description(AiTools.ShowInCabinette));
        Assert.Contains("nextOffset", Description(AiTools.ListFiles));
        Assert.Contains("nextOffset", Description(AiTools.ReadFile));
        Assert.Contains("hasMore", Description(AiTools.Search));
        Assert.Contains("relative path is resolved", Description(AiTools.AddFiles));
        foreach (var tool in ToolCatalog.All.Where(t => t.Input.PropertyNames.Any(p => p.EndsWith("Ids", StringComparison.Ordinal) || p.EndsWith("Id", StringComparison.Ordinal))))
        {
            var parameters = tool.Input.ToJsonObject()["properties"]!.AsObject();
            foreach (var (name, schema) in parameters.Where(p => p.Key.EndsWith("Id", StringComparison.Ordinal) || p.Key.EndsWith("Ids", StringComparison.Ordinal)))
            {
                if (name == "operationId")
                {
                    continue;
                }

                var text = (string)schema!["description"]!;
                Assert.True(text.Contains("search", StringComparison.Ordinal) || text.Contains("list_", StringComparison.Ordinal), $"{tool.Name}.{name} should say where its ids come from");
            }
        }

        var instructions = ToolCatalog.ServerInstructions;
        foreach (var phrase in new[] { "thread", "ids", "copy", "pointer", "Recycle Bin", "confirm", "busy" })
        {
            Assert.Contains(phrase, instructions);
        }
    }

    /// <summary>
    /// The schemas can't say "exactly one of these two", so the first sentence of the description does: models read
    /// that much at least (a Codex session asked for it).
    /// </summary>
    [Theory]
    [InlineData(AiTools.ListFiles, "categoryId", "starred")]
    [InlineData(AiTools.ShowInCabinette, "fileId", "categoryId")]
    public void TheFirstSentenceSaysWhichParametersExcludeEachOther(string tool, string first, string second)
    {
        var description = ToolCatalog.Find(tool)!.Description;
        var firstSentence = description[..(description.IndexOf(". ", StringComparison.Ordinal) + 1)];

        Assert.StartsWith($"Give exactly one of `{first}` or `{second}`", firstSentence);
        Assert.Contains("never both", firstSentence);
        var properties = ToolCatalog.Find(tool)!.Input.ToJsonObject()["properties"]!;
        Assert.Contains($"`{second}`", (string)properties[first]!["description"]!);
        Assert.Contains($"`{first}`", (string)properties[second]!["description"]!);
    }

    /// <summary>
    /// The general text about Cabinette belongs in the server instructions only: repeated in each of the 25 tool
    /// descriptions it would cost tokens with every request (a Codex session suspected it was).
    /// </summary>
    [Fact]
    public void NoToolDescriptionRepeatsTheServerInstructions()
    {
        foreach (var tool in ToolCatalog.All)
        {
            AssertHasNoInstructions(tool.Name, tool.Description);
        }
    }

    /// <summary>Fails when the text contains the intro or any sentence of the server instructions.</summary>
    internal static void AssertHasNoInstructions(string label, string text)
    {
        Assert.False(text.Contains("personal file organizer", StringComparison.OrdinalIgnoreCase), $"{label} repeats the introduction");
        foreach (var sentence in ToolCatalog.ServerInstructions.Split(". ").Select(s => s.Trim().TrimEnd('.')).Where(s => s.Length >= 30))
        {
            Assert.False(text.Contains(sentence, StringComparison.Ordinal), $"{label} repeats the server instructions: \"{sentence}\"");
        }
    }

    [Fact]
    public void SchemasAreClosedObjects()
    {
        foreach (var tool in ToolCatalog.All)
        {
            var schema = tool.Input.ToJsonObject();
            Assert.Equal("object", (string)schema["type"]!);
            Assert.False((bool)schema["additionalProperties"]!);
            Assert.NotNull(schema["properties"]);
        }
    }

    private static void AssertMatches(string label, Type recordType, ObjectSchema schema)
    {
        var properties = recordType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.Name != "EqualityContract")
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
            .Order()
            .ToList();
        Assert.True(properties.SequenceEqual(schema.PropertyNames.Order()), $"{label}: record {string.Join(",", properties)} vs schema {string.Join(",", schema.PropertyNames.Order())}");

        // Required = the record's constructor parameters without a default value.
        var constructor = recordType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
        var required = constructor?.GetParameters()
            .Where(p => !p.HasDefaultValue)
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name!))
            .Order()
            .ToList() ?? [];
        Assert.True(required.SequenceEqual(schema.Required.Order()), $"{label}: required {string.Join(",", required)} vs schema {string.Join(",", schema.Required.Order())}");
    }

    private static void AssertDescribed(string label, JsonObject schema)
    {
        foreach (var (name, property) in schema["properties"]!.AsObject())
        {
            Assert.False(string.IsNullOrWhiteSpace((string?)property!["description"]), $"{label}.{name} has no description");
            if ((string?)property["type"] == "object")
            {
                AssertDescribed($"{label}.{name}", property.AsObject());
            }
        }
    }
}
