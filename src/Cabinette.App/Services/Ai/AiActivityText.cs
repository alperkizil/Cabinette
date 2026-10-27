using System.Globalization;
using System.Text;
using Cabinette.App.Localization;
using Cabinette.Core.Models;
using Cabinette.Ipc;

namespace Cabinette.App.Services.Ai;

/// <summary>What a piece of an activity sentence opens when clicked.</summary>
public enum AiActivityLinkKind
{
    None,
    File,
    Category,
}

/// <summary>One piece of an activity sentence: plain text, or the name of a file or category that opens it.</summary>
/// <param name="Id">The file's or category's library id (links only).</param>
public sealed record AiActivitySegment(string Text, AiActivityLinkKind Link = AiActivityLinkKind.None, long Id = 0)
{
    public bool IsLink => Link != AiActivityLinkKind.None;
}

/// <summary>An activity entry in words: the sentence (with links), and the error of a failed call.</summary>
/// <param name="Failed">The call failed (the sentence then names the tool and what it was asked to do).</param>
/// <param name="Error">The failed call's message, when it has one.</param>
public sealed record AiActivityDescription(IReadOnlyList<AiActivitySegment> Segments, bool Failed, string? Error)
{
    /// <summary>The sentence as plain text ("Deleted 3 files: a.pdf, b.pdf, c.pdf").</summary>
    public string Sentence => string.Concat(Segments.Select(s => s.Text));

    /// <summary>"Failed: …" for a failed call, else null.</summary>
    public string? FailureText => !Failed ? null : Error is null ? L.Get("AiActivity_FailedPlain") : L.Format("AiActivity_Failed", Error);
}

/// <summary>
/// Friendly texts for the AI activity log (Settings › AI): tool names for the filter, and one sentence per entry built
/// from its <see cref="AiActivityDetails"/> ("Deleted 3 files: a.pdf, b.pdf, c.pdf", "Searched for “invoice” (12
/// results)", "Read report.pdf"). File and category names are links, except those of deleted files and categories.
/// Entries whose details can't be read (another version wrote them) show the tool's name only.
/// </summary>
public static class AiActivityText
{
    /// <summary>A sentence names at most this many files, then "and N more".</summary>
    public const int MaxNamesShown = 5;

    /// <summary>The tool's friendly name ("Delete files"), or the tool name itself for a tool this version doesn't know.</summary>
    public static string ToolName(string tool)
        => AiTools.All.Contains(tool, StringComparer.Ordinal) ? L.Get("AiTool_" + tool) : tool;

    public static AiActivityDescription Describe(ActivityEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Describe(entry.Action, AiActivityDetails.TryParse(entry.DetailsJson));
    }

    public static AiActivityDescription Describe(string tool, AiActivityDetails? details)
    {
        tool ??= "";
        details = WithoutNullRefs(details);
        if (details is null)
        {
            return new AiActivityDescription(Merge(Text(ToolName(tool))), Failed: false, Error: null);
        }

        if (!details.Ok)
        {
            return new AiActivityDescription(Merge(Attempt(tool, details)), Failed: true, string.IsNullOrWhiteSpace(details.Error) ? null : details.Error.Trim());
        }

        return new AiActivityDescription(Merge(Success(tool, details)), Failed: false, Error: null);
    }

    // ------------------------------------------------------------------ sentences

    private static List<AiActivitySegment> Success(string tool, AiActivityDetails d)
    {
        var category = CategoryLink(d, 0);
        switch (tool)
        {
            case AiTools.Search:
                {
                    var query = Clean(d.Query);
                    if (query is null)
                    {
                        return d.FileCount is { } all ? Compose("AiActivity_SearchedResults", Text(Results(all))) : Compose("AiActivity_SearchedPlain");
                    }

                    return d.FileCount is { } count
                        ? Compose("AiActivity_SearchedFor", Text(query), Text(Results(count)))
                        : Compose("AiActivity_SearchedForPlain", Text(query));
                }

            case AiTools.ListCategories:
                return category is null ? Compose("AiActivity_ListedCategories") : Compose("AiActivity_ListedCategoriesIn", category);
            case AiTools.ListFiles:
                {
                    var sentence = category is null ? Compose("AiActivity_ListedFiles") : Compose("AiActivity_ListedFilesIn", category);
                    if (Clean(d.Query) is { } query)
                    {
                        sentence.AddRange(Compose("AiActivity_Matching", Text(query)));
                    }

                    if (d.FileCount is { } count)
                    {
                        sentence.AddRange(Compose("AiActivity_CountSuffix", Text(FilesCount(count))));
                    }

                    return sentence;
                }

            case AiTools.GetFiles:
                return WithNames(Compose("AiActivity_LookedUp", FilePhrase(d, links: true)), d, links: true);
            case AiTools.ReadFile:
                return Compose(d.Offset is > 0 ? "AiActivity_ReadMore" : "AiActivity_Read", FilePhrase(d, links: true));
            case AiTools.ListTags:
                return Compose("AiActivity_ListedTags");
            case AiTools.GetOperationStatus:
                return Compose("AiActivity_CheckedOperation");
            case AiTools.AddFiles:
                {
                    var linked = string.Equals(d.Mode, InstanceProtocol.PointerMode, StringComparison.OrdinalIgnoreCase);
                    var key = (category is null, linked) switch
                    {
                        (false, false) => "AiActivity_AddedTo",
                        (false, true) => "AiActivity_AddedLinksTo",
                        (true, false) => "AiActivity_Added",
                        (true, true) => "AiActivity_AddedLinks",
                    };
                    return WithNames(Compose(key, FilePhrase(d, links: true), category ?? []), d, links: true);
                }

            case AiTools.CreateNote:
                return category is null ? Compose("AiActivity_CreatedNote") : Compose("AiActivity_CreatedNoteIn", category);
            case AiTools.TagFiles:
                return WithNames(
                    Tags(d) is { } added ? Compose("AiActivity_Tagged", FilePhrase(d, links: true), Text(added)) : Compose("AiActivity_TaggedPlain", FilePhrase(d, links: true)),
                    d,
                    links: true);
            case AiTools.UntagFiles:
                return WithNames(
                    Tags(d) is { } removed ? Compose("AiActivity_Untagged", FilePhrase(d, links: true), Text(removed)) : Compose("AiActivity_UntaggedPlain", FilePhrase(d, links: true)),
                    d,
                    links: true);
            case AiTools.AddToCategory:
                return WithNames(
                    category is null ? Compose("AiActivity_AlsoAdded", FilePhrase(d, links: true)) : Compose("AiActivity_AlsoAddedTo", FilePhrase(d, links: true), category),
                    d,
                    links: true);
            case AiTools.RemoveFromCategory:
                return WithNames(
                    category is null ? Compose("AiActivity_RemovedFromCategory", FilePhrase(d, links: true)) : Compose("AiActivity_RemovedFrom", FilePhrase(d, links: true), category),
                    d,
                    links: true);
            case AiTools.MoveFiles:
                // The first category is the target (AiActivityDetails.Categories: target, then source).
                return WithNames(
                    category is null ? Compose("AiActivity_Moved", FilePhrase(d, links: true)) : Compose("AiActivity_MovedTo", FilePhrase(d, links: true), category),
                    d,
                    links: true);
            case AiTools.RenameFile:
                return RenamedFile(d);
            case AiTools.DeleteFiles:
                // Deleted files can't be opened any more: their names are plain text.
                return WithNames(Compose("AiActivity_Deleted", FilePhrase(d, links: false)), d, links: false);
            case AiTools.StarFiles:
                return WithNames(Compose("AiActivity_Starred", FilePhrase(d, links: true)), d, links: true);
            case AiTools.UnstarFiles:
                return WithNames(Compose("AiActivity_Unstarred", FilePhrase(d, links: true)), d, links: true);
            case AiTools.CreateCategory:
                {
                    var created = category ?? (Clean(d.NewName) is { } name ? Text(name) : null);
                    return created is null ? Compose("AiActivity_CreatedCategoryPlain") : Compose("AiActivity_CreatedCategory", created);
                }

            case AiTools.RenameCategory:
                return RenamedCategory(d);
            case AiTools.DeleteCategory:
                {
                    var name = d.Categories is { Count: > 0 } categories ? Clean(categories[0].Name) : null;
                    return name is null ? Compose("AiActivity_DeletedCategoryPlain") : Compose("AiActivity_DeletedCategory", Text(name));
                }

            case AiTools.MoveVault:
                return FirstPath(d) is { } target ? Compose("AiActivity_MovingVault", Text(target)) : Compose("AiActivity_MovingVaultPlain");
            case AiTools.MigratePointers:
                return Compose("AiActivity_Migrating");
            case AiTools.CreateBackup:
                return FirstPath(d) is { } zip ? Compose("AiActivity_BackingUp", Text(zip)) : Compose("AiActivity_BackingUpPlain");
            case AiTools.ShowInCabinette:
                {
                    var shown = d.Files is { Count: > 0 } files ? FileLink(files[0], links: true) : category;
                    return shown is null ? Compose("AiActivity_ShowedWindow") : Compose("AiActivity_Showed", shown);
                }

            default:
                return Text(ToolName(tool));
        }
    }

    /// <summary>A failed call: the tool's name and what it was asked to work on ("Delete files: a.pdf, b.pdf").</summary>
    private static List<AiActivitySegment> Attempt(string tool, AiActivityDetails d)
    {
        var sentence = Text(ToolName(tool));
        List<AiActivitySegment>? target = null;
        if (d.Files is { Count: > 0 } files)
        {
            target = Names(files.Select(f => FileLink(f, links: true)).ToList(), Math.Max(d.FileCount ?? 0, files.Count));
        }
        else if (d.Categories is { Count: > 0 } categories)
        {
            target = Names(categories.Select(c => CategoryRef(c)).ToList(), categories.Count);
        }
        else if (Clean(d.Query) is { } query)
        {
            target = Compose("AiActivity_Quoted", Text(query));
        }
        else if (d.Paths is { Count: > 0 } paths)
        {
            target = Names(paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Text(p.Trim())).ToList(), paths.Count);
        }
        else if (Clean(d.NewName) is { } name)
        {
            target = Text(name);
        }

        if (target is { Count: > 0 })
        {
            sentence.AddRange(Text(L.Get("AiActivity_ListSeparator")));
            sentence.AddRange(target);
        }

        return sentence;
    }

    private static List<AiActivitySegment> RenamedFile(AiActivityDetails d)
    {
        var file = d.Files is { Count: > 0 } files ? files[0] : null;
        var newName = Clean(d.NewName);
        if (newName is not null && file is not null && !string.Equals(Clean(file.Name), newName, StringComparison.Ordinal) && Clean(file.Name) is not null)
        {
            return Compose("AiActivity_RenamedFile", FileLink(file, links: true), Text(newName));
        }

        if (newName is not null)
        {
            return Compose("AiActivity_RenamedFileTo", file is null ? Text(newName) : FileLink(file with { Name = newName }, links: true));
        }

        return Compose("AiActivity_RenamedFilePlain", FilePhrase(d, links: true));
    }

    private static List<AiActivitySegment> RenamedCategory(AiActivityDetails d)
    {
        var category = d.Categories is { Count: > 0 } categories ? categories[0] : null;
        var oldName = Clean(category?.Name);
        var newName = Clean(d.NewName);
        if (category is not null && oldName is not null && newName is not null && !string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            return Compose("AiActivity_RenamedCategory", CategoryRef(category), Text(newName));
        }

        if (newName is not null)
        {
            return Compose("AiActivity_RenamedCategoryTo", category is null ? Text(newName) : CategoryRef(category with { Name = newName }));
        }

        return category is null ? Compose("AiActivity_RenamedCategoryPlain") : Compose("AiActivity_RenamedCategoryNamed", CategoryRef(category));
    }

    // ------------------------------------------------------------------ pieces

    /// <summary>
    /// The details without null file or category refs: JSON like <c>"files":[null]</c> (another version, a damaged entry)
    /// parses fine but names nothing. The counts stay as they were written.
    /// </summary>
    private static AiActivityDetails? WithoutNullRefs(AiActivityDetails? details)
    {
        if (details is null || (details.Files?.Any(f => f is null) != true && details.Categories?.Any(c => c is null) != true))
        {
            return details;
        }

        return details with
        {
            Files = details.Files?.Where(f => f is not null).ToList(),
            Categories = details.Categories?.Where(c => c is not null).ToList(),
        };
    }

    /// <summary>The files a call named: the name when it was one file, else "3 files".</summary>
    private static List<AiActivitySegment> FilePhrase(AiActivityDetails d, bool links)
    {
        var count = FileTotal(d);
        if (count == 1 && d.Files is { Count: 1 } one)
        {
            return FileLink(one[0], links);
        }

        return Text(FilesCount(count));
    }

    /// <summary>For more than one file: ": a.pdf, b.pdf and 3 more" after the sentence.</summary>
    private static List<AiActivitySegment> WithNames(List<AiActivitySegment> sentence, AiActivityDetails d, bool links)
    {
        var count = FileTotal(d);
        if (count > 1 && d.Files is { Count: > 0 } files)
        {
            sentence.AddRange(Text(L.Get("AiActivity_ListSeparator")));
            sentence.AddRange(Names(files.Select(f => FileLink(f, links)).ToList(), count));
        }

        return sentence;
    }

    /// <summary>The first <see cref="MaxNamesShown"/> names, then "and N more" for the rest of <paramref name="total"/>.</summary>
    private static List<AiActivitySegment> Names(IReadOnlyList<List<AiActivitySegment>> names, int total)
    {
        var shown = names.Take(MaxNamesShown).ToList();
        var result = new List<AiActivitySegment>();
        for (var i = 0; i < shown.Count; i++)
        {
            if (i > 0)
            {
                result.AddRange(Text(L.Get("AiActivity_NameSeparator")));
            }

            result.AddRange(shown[i]);
        }

        var more = Math.Max(total, names.Count) - shown.Count;
        if (more > 0)
        {
            result.AddRange(Text(L.Format("AiActivity_AndMore", N(more))));
        }

        return result;
    }

    private static int FileTotal(AiActivityDetails d) => Math.Max(0, d.FileCount ?? d.Files?.Count ?? 0);

    private static string FilesCount(int count) => count switch
    {
        0 => L.Get("AiActivity_NoFiles"),
        1 => L.Get("AiActivity_OneFile"),
        _ => L.Format("AiActivity_Files", N(count)),
    };

    private static string Results(int count) => count switch
    {
        0 => L.Get("AiActivity_NoResults"),
        1 => L.Get("AiActivity_OneResult"),
        _ => L.Format("AiActivity_Results", N(count)),
    };

    private static string? Tags(AiActivityDetails d)
    {
        var tags = d.Tags?.Select(Clean).OfType<string>().Select(t => "#" + t.TrimStart('#')).ToList();
        return tags is { Count: > 0 } ? string.Join(L.Get("AiActivity_NameSeparator"), tags) : null;
    }

    private static string? FirstPath(AiActivityDetails d) => d.Paths?.Select(Clean).OfType<string>().FirstOrDefault();

    private static List<AiActivitySegment>? CategoryLink(AiActivityDetails d, int index)
        => d.Categories is { } categories && categories.Count > index ? CategoryRef(categories[index]) : null;

    private static List<AiActivitySegment> CategoryRef(AiActivityRef category)
        => Clean(category.Name) is { } name
            ? [new AiActivitySegment(name, AiActivityLinkKind.Category, category.Id)]
            : Text(L.Get("AiActivity_ACategory"));

    private static List<AiActivitySegment> FileLink(AiActivityRef file, bool links)
    {
        var name = Clean(file.Name);
        if (name is null)
        {
            return Text(L.Get("AiActivity_OneFile"));
        }

        return links ? [new AiActivitySegment(name, AiActivityLinkKind.File, file.Id)] : Text(name);
    }

    private static List<AiActivitySegment> Text(string text) => [new AiActivitySegment(text)];

    /// <summary>The localized format with its placeholders replaced by segments (so names inside it can be links).</summary>
    private static List<AiActivitySegment> Compose(string key, params List<AiActivitySegment>[] args)
    {
        var format = L.Get(key);
        var result = new List<AiActivitySegment>();
        var text = new StringBuilder();

        void FlushText()
        {
            if (text.Length > 0)
            {
                result.Add(new AiActivitySegment(text.ToString()));
                text.Clear();
            }
        }

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if ((c == '{' || c == '}') && i + 1 < format.Length && format[i + 1] == c)
            {
                text.Append(c);
                i++;
                continue;
            }

            if (c == '{')
            {
                var close = format.IndexOf('}', i + 1);
                if (close > i + 1 &&
                    int.TryParse(format.AsSpan(i + 1, close - i - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                    index < args.Length)
                {
                    FlushText();
                    result.AddRange(args[index]);
                    i = close;
                    continue;
                }
            }

            text.Append(c);
        }

        FlushText();
        return result;
    }

    /// <summary>Joins neighboring plain-text pieces.</summary>
    private static List<AiActivitySegment> Merge(List<AiActivitySegment> segments)
    {
        var merged = new List<AiActivitySegment>(segments.Count);
        foreach (var segment in segments.Where(s => s.Text.Length > 0))
        {
            if (!segment.IsLink && merged.Count > 0 && !merged[^1].IsLink)
            {
                merged[^1] = new AiActivitySegment(merged[^1].Text + segment.Text);
            }
            else
            {
                merged.Add(segment);
            }
        }

        return merged;
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
