using System.Globalization;
using Cabinette.App.Localization;
using Cabinette.App.Services.Monitor;

namespace Cabinette.App.Services.Ai;

/// <summary>The category a grouped AI notice opens when clicked.</summary>
/// <param name="Id">Its id, when a change named it; else it is found by <paramref name="Name"/>.</param>
public sealed record AiCategoryTarget(long? Id, string? Name);

/// <summary>One grouped notice about what an AI client changed.</summary>
/// <param name="Client">The client's display name; the toast's title.</param>
/// <param name="Lines">One line per kind of change ("3 files added to Images", "2 files deleted"), for the toast.</param>
/// <param name="OneLine">The in-app notice: one line starting with the client ("Claude Code added 3 files to Images · deleted 2 files").</param>
/// <param name="Category">The one category involved (clicking opens it), else null.</param>
/// <param name="HasProblems">Something failed (a vault operation, files that couldn't be added).</param>
public sealed record AiChangeMessage(string Client, IReadOnlyList<string> Lines, string OneLine, AiCategoryTarget? Category, bool HasProblems)
{
    public string Title => Client;

    public string Body => string.Join("\n", Lines);
}

/// <summary>
/// Texts of the grouped AI notices (SPEC.md section 10, "Toasts"): the changes one client made within the merge window,
/// worded like the vault monitor's ("3 files added to Images") for the toast, and as one active sentence starting with the
/// client for the in-app notice ("Claude Code added 3 files to Images").
/// </summary>
public static class AiChangeText
{
    /// <summary>Files that went to more categories than this get the first ones by name and one line for the rest.</summary>
    public const int MaxCategoryLines = 4;

    /// <summary>A toast holds at most this many lines; more are summed up as "…and N more" (Windows cuts long toasts).</summary>
    public const int MaxLines = 6;

    /// <summary>Changes arriving within this window are merged into one notice (the vault monitor's).</summary>
    public static TimeSpan MergeWindow => VaultChangeText.MergeWindow;

    /// <summary>A notice goes out this long after the last change if nothing else arrives (the vault monitor's).</summary>
    public static TimeSpan QuietDelay => VaultChangeText.QuietDelay;

    /// <summary>When to show the pending notice: shortly after the last change, but at most <see cref="MergeWindow"/> after the first.</summary>
    public static DateTimeOffset FlushAt(DateTimeOffset firstAt, DateTimeOffset lastAt) => VaultChangeText.FlushAt(firstAt, lastAt);

    /// <summary>
    /// The notice for one client's (merged) changes, or null when there is nothing to say.
    /// </summary>
    /// <param name="includeSuccess">
    /// False when "Notifications on vault changes" is off: then only failed vault operations are shown, and null is
    /// returned when none failed.
    /// </param>
    public static AiChangeMessage? Build(string client, IReadOnlyList<AiChange> changes, bool includeSuccess = true)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var shown = includeSuccess ? changes : changes.Where(c => c.Kind == AiChangeKind.OperationFailed).ToList();
        var parts = Parts(shown);
        if (parts.Count == 0)
        {
            return null;
        }

        var name = string.IsNullOrWhiteSpace(client) ? L.Get("AiNotify_UnknownClient") : client.Trim();
        var lines = Cap(parts.Select(p => p.Line).ToList());
        var predicates = Cap(parts.Select(p => p.Predicate).ToList());
        var oneLine = L.Format("AiNotify_InApp", name, string.Join(" · ", predicates));
        var problems = shown.Any(c => c.Kind == AiChangeKind.OperationFailed || (c.Kind == AiChangeKind.FilesAdded && c.Failures > 0));
        return new AiChangeMessage(name, lines, oneLine, SingleCategory(shown), problems);
    }

    /// <summary>One part per kind of change, in a fixed order: the toast line and the in-app predicate.</summary>
    public static IReadOnlyList<(string Line, string Predicate)> Parts(IReadOnlyList<AiChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var parts = new List<(string Line, string Predicate)>();
        void Add(string lineKey, string predicateKey, params object?[] args) => parts.Add((L.Format(lineKey, args), L.Format(predicateKey, args)));

        IEnumerable<AiChange> Of(AiChangeKind kind) => changes.Where(c => c.Kind == kind);
        int Sum(AiChangeKind kind) => Of(kind).Sum(c => Math.Max(0, c.Count));

        // A failed vault operation first: it is the one thing that always shows.
        foreach (var failed in Of(AiChangeKind.OperationFailed))
        {
            if (string.IsNullOrWhiteSpace(failed.Detail))
            {
                Add("AiNotify_OperationFailedPlain", "AiNotify_OperationFailedPlainP");
            }
            else
            {
                Add("AiNotify_OperationFailed", "AiNotify_OperationFailedP", failed.Detail.Trim());
            }
        }

        AddFilesAdded(changes, Add);

        AddPerCategory(Of(AiChangeKind.NoteCreated), Add,
            ("AiNotify_NoteAddedTo", "AiNotify_NoteAddedToP"), ("AiNotify_NotesAddedTo", "AiNotify_NotesAddedToP"),
            ("AiNotify_NoteAdded", "AiNotify_NoteAddedP"), ("AiNotify_NotesAdded", "AiNotify_NotesAddedP"));
        AddPerCategory(Of(AiChangeKind.FilesAddedToCategory), Add,
            ("AiNotify_AlsoAddedOne", "AiNotify_AlsoAddedOneP"), ("AiNotify_AlsoAdded", "AiNotify_AlsoAddedP"),
            ("AiNotify_AlsoAddedOneNoName", "AiNotify_AlsoAddedOneNoNameP"), ("AiNotify_AlsoAddedNoName", "AiNotify_AlsoAddedNoNameP"));
        AddPerCategory(Of(AiChangeKind.FilesMoved), Add,
            ("AiNotify_MovedOne", "AiNotify_MovedOneP"), ("AiNotify_Moved", "AiNotify_MovedP"),
            ("AiNotify_MovedOneNoName", "AiNotify_MovedOneNoNameP"), ("AiNotify_MovedNoName", "AiNotify_MovedNoNameP"));
        AddPerCategory(Of(AiChangeKind.FilesRemovedFromCategory), Add,
            ("AiNotify_RemovedFromOne", "AiNotify_RemovedFromOneP"), ("AiNotify_RemovedFrom", "AiNotify_RemovedFromP"),
            ("AiNotify_RemovedFromOneNoName", "AiNotify_RemovedFromOneNoNameP"), ("AiNotify_RemovedFromNoName", "AiNotify_RemovedFromNoNameP"));

        var renamed = Of(AiChangeKind.FileRenamed).ToList();
        var renamedCount = renamed.Sum(c => Math.Max(0, c.Count));
        if (renamedCount == 1 && renamed.Count == 1 && !string.IsNullOrWhiteSpace(renamed[0].Detail))
        {
            Add("AiNotify_RenamedTo", "AiNotify_RenamedToP", renamed[0].Detail!.Trim());
        }
        else
        {
            AddCount(renamedCount, Add, ("AiNotify_RenamedOne", "AiNotify_RenamedOneP"), ("AiNotify_Renamed", "AiNotify_RenamedP"));
        }

        AddCount(Sum(AiChangeKind.FilesTagged), Add, ("AiNotify_TaggedOne", "AiNotify_TaggedOneP"), ("AiNotify_Tagged", "AiNotify_TaggedP"));
        AddCount(Sum(AiChangeKind.FilesUntagged), Add, ("AiNotify_UntaggedOne", "AiNotify_UntaggedOneP"), ("AiNotify_Untagged", "AiNotify_UntaggedP"));
        AddCount(Sum(AiChangeKind.FilesStarred), Add, ("AiNotify_StarredOne", "AiNotify_StarredOneP"), ("AiNotify_Starred", "AiNotify_StarredP"));
        AddCount(Sum(AiChangeKind.FilesUnstarred), Add, ("AiNotify_UnstarredOne", "AiNotify_UnstarredOneP"), ("AiNotify_Unstarred", "AiNotify_UnstarredP"));
        AddCount(Sum(AiChangeKind.FilesDeleted), Add, ("AiNotify_DeletedOne", "AiNotify_DeletedOneP"), ("AiNotify_Deleted", "AiNotify_DeletedP"));

        AddNamedCategories(Of(AiChangeKind.CategoryCreated).ToList(), Add,
            ("AiNotify_CategoryCreated", "AiNotify_CategoryCreatedP"), ("AiNotify_CategoriesCreatedTwo", "AiNotify_CategoriesCreatedTwoP"),
            ("AiNotify_CategoriesCreated", "AiNotify_CategoriesCreatedP"));

        var categoriesRenamed = Of(AiChangeKind.CategoryRenamed).ToList();
        if (categoriesRenamed.Count == 1 && NewName(categoriesRenamed[0]) is { } newName)
        {
            Add("AiNotify_CategoryRenamed", "AiNotify_CategoryRenamedP", newName);
        }
        else if (categoriesRenamed.Count > 0)
        {
            Add("AiNotify_CategoriesRenamed", "AiNotify_CategoriesRenamedP", N(categoriesRenamed.Count));
        }

        AddNamedCategories(Of(AiChangeKind.CategoryDeleted).ToList(), Add,
            ("AiNotify_CategoryDeleted", "AiNotify_CategoryDeletedP"), ("AiNotify_CategoriesDeletedTwo", "AiNotify_CategoriesDeletedTwoP"),
            ("AiNotify_CategoriesDeleted", "AiNotify_CategoriesDeletedP"));

        if (Of(AiChangeKind.PointersMigrated).Any())
        {
            var migrated = Sum(AiChangeKind.PointersMigrated);
            if (migrated == 0)
            {
                Add("AiNotify_MigratedNone", "AiNotify_MigratedNoneP");
            }
            else
            {
                AddCount(migrated, Add, ("AiNotify_MigratedOne", "AiNotify_MigratedOneP"), ("AiNotify_Migrated", "AiNotify_MigratedP"));
            }
        }

        if (Of(AiChangeKind.VaultMoved).LastOrDefault() is { } moved)
        {
            if (string.IsNullOrWhiteSpace(moved.Detail))
            {
                Add("AiNotify_VaultMovedPlain", "AiNotify_VaultMovedPlainP");
            }
            else
            {
                Add("AiNotify_VaultMoved", "AiNotify_VaultMovedP", moved.Detail.Trim());
            }
        }

        var backups = Of(AiChangeKind.BackupCreated).ToList();
        if (backups.Count == 1)
        {
            if (string.IsNullOrWhiteSpace(backups[0].Detail))
            {
                Add("AiNotify_BackupPlain", "AiNotify_BackupPlainP");
            }
            else
            {
                Add("AiNotify_Backup", "AiNotify_BackupP", backups[0].Detail!.Trim());
            }
        }
        else if (backups.Count > 1)
        {
            Add("AiNotify_Backups", "AiNotify_BackupsP", N(backups.Count));
        }

        return parts;
    }

    /// <summary>
    /// The one category the changes involved (files added, moved or put in it, a note, the category created or renamed),
    /// else null. A deleted category counts as involved but can't be opened. Files added to a category the change
    /// doesn't name (sorted by type, names unknown) count as another category, so no wrong one is offered.
    /// </summary>
    public static AiCategoryTarget? SingleCategory(IReadOnlyList<AiChange> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var involved = new List<(long? Id, string? Name, bool Deleted)>();
        var unknown = false;

        // A rename's new name replaces the old one; otherwise the first spelling stays.
        void Involve(long? id, string? name, bool deleted, bool renamed = false)
        {
            name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
            if (id is null && name is null)
            {
                unknown = true;
                return;
            }

            for (var i = 0; i < involved.Count; i++)
            {
                var known = involved[i];
                // The same category: by id when both have one (a rename changes the name), else by name.
                var same = id is not null && known.Id is not null
                    ? id == known.Id
                    : name is not null && known.Name is not null && string.Equals(name, known.Name, StringComparison.CurrentCultureIgnoreCase);
                if (same)
                {
                    involved[i] = (known.Id ?? id, renamed ? name ?? known.Name : known.Name ?? name, known.Deleted || deleted);
                    return;
                }
            }

            involved.Add((id, name, deleted));
        }

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case AiChangeKind.FilesAdded when change.AddedByCategory is { Count: > 0 } byCategory:
                    var named = 0;
                    foreach (var (name, count) in byCategory)
                    {
                        if (count > 0)
                        {
                            Involve(null, name, deleted: false);
                            named += count;
                        }
                    }

                    if (change.Count > named)
                    {
                        // Some went to categories the change doesn't name.
                        unknown = true;
                    }

                    break;
                case AiChangeKind.FilesAdded when change.Count > 0:
                case AiChangeKind.NoteCreated:
                case AiChangeKind.FilesAddedToCategory:
                case AiChangeKind.FilesRemovedFromCategory:
                case AiChangeKind.FilesMoved:
                case AiChangeKind.CategoryCreated:
                    Involve(change.CategoryId, change.CategoryName, deleted: false);
                    break;
                case AiChangeKind.CategoryRenamed:
                    Involve(change.CategoryId, NewName(change), deleted: false, renamed: true);
                    break;
                case AiChangeKind.CategoryDeleted:
                    Involve(change.CategoryId, change.CategoryName, deleted: true);
                    break;
            }
        }

        if (unknown || involved.Count != 1)
        {
            return null;
        }

        var only = involved[0];
        return only.Deleted ? null : new AiCategoryTarget(only.Id, only.Name);
    }

    // ------------------------------------------------------------------ pieces

    private static void AddFilesAdded(IReadOnlyList<AiChange> changes, Action<string, string, object?[]> add)
    {
        var byCategory = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var unnamed = 0;
        var duplicates = 0;
        var failures = 0;
        foreach (var change in changes.Where(c => c.Kind == AiChangeKind.FilesAdded))
        {
            duplicates += Math.Max(0, change.Duplicates);
            failures += Math.Max(0, change.Failures);
            var count = Math.Max(0, change.Count);
            if (change.AddedByCategory is { Count: > 0 } split)
            {
                var named = 0;
                foreach (var (name, n) in split)
                {
                    if (n > 0 && !string.IsNullOrWhiteSpace(name))
                    {
                        byCategory[name.Trim()] = byCategory.GetValueOrDefault(name.Trim()) + n;
                        named += n;
                    }
                }

                unnamed += Math.Max(0, count - named);
            }
            else if (!string.IsNullOrWhiteSpace(change.CategoryName))
            {
                if (count > 0)
                {
                    byCategory[change.CategoryName.Trim()] = byCategory.GetValueOrDefault(change.CategoryName.Trim()) + count;
                }
            }
            else
            {
                unnamed += count;
            }
        }

        var ordered = byCategory.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        var shownByName = ordered.Count <= MaxCategoryLines ? ordered : ordered.Take(MaxCategoryLines - 1).ToList();
        foreach (var (name, count) in shownByName)
        {
            if (count == 1)
            {
                add("AiNotify_AddedOneTo", "AiNotify_AddedOneToP", [name]);
            }
            else
            {
                add("AiNotify_AddedTo", "AiNotify_AddedToP", [N(count), name]);
            }
        }

        if (ordered.Count > shownByName.Count)
        {
            var rest = ordered.Skip(shownByName.Count).ToList();
            add("AiNotify_AddedToOthers", "AiNotify_AddedToOthersP", [N(rest.Sum(kv => kv.Value)), N(rest.Count)]);
        }

        AddCount(unnamed, add, ("AiNotify_AddedOne", "AiNotify_AddedOneP"), ("AiNotify_Added", "AiNotify_AddedP"));
        AddCount(duplicates, add, ("AiNotify_DuplicateOne", "AiNotify_DuplicateOneP"), ("AiNotify_Duplicates", "AiNotify_DuplicatesP"));
        AddCount(failures, add, ("AiNotify_FailedOne", "AiNotify_FailedOneP"), ("AiNotify_Failed", "AiNotify_FailedP"));
    }

    /// <summary>Files (or notes) per category name: one line per category, in order of count.</summary>
    private static void AddPerCategory(
        IEnumerable<AiChange> changes,
        Action<string, string, object?[]> add,
        (string Line, string Predicate) oneNamed,
        (string Line, string Predicate) manyNamed,
        (string Line, string Predicate) oneUnnamed,
        (string Line, string Predicate) manyUnnamed)
    {
        var byCategory = new Dictionary<string, int>(StringComparer.CurrentCultureIgnoreCase);
        var unnamed = 0;
        foreach (var change in changes)
        {
            var count = Math.Max(0, change.Count);
            if (string.IsNullOrWhiteSpace(change.CategoryName))
            {
                unnamed += count;
            }
            else if (count > 0)
            {
                byCategory[change.CategoryName.Trim()] = byCategory.GetValueOrDefault(change.CategoryName.Trim()) + count;
            }
        }

        foreach (var (name, count) in byCategory.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            if (count == 1)
            {
                add(oneNamed.Line, oneNamed.Predicate, [name]);
            }
            else
            {
                add(manyNamed.Line, manyNamed.Predicate, [N(count), name]);
            }
        }

        AddCount(unnamed, add, oneUnnamed, manyUnnamed);
    }

    /// <summary>Categories created or deleted: by name for one or two, else a count.</summary>
    private static void AddNamedCategories(
        IReadOnlyList<AiChange> changes,
        Action<string, string, object?[]> add,
        (string Line, string Predicate) one,
        (string Line, string Predicate) two,
        (string Line, string Predicate) many)
    {
        if (changes.Count == 0)
        {
            return;
        }

        var names = changes.Select(c => c.CategoryName?.Trim()).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.CurrentCultureIgnoreCase).ToList();
        var unnamed = changes.Count(c => string.IsNullOrWhiteSpace(c.CategoryName));
        var total = names.Count + unnamed;
        if (unnamed == 0 && names.Count == 1)
        {
            add(one.Line, one.Predicate, [names[0]]);
        }
        else if (unnamed == 0 && names.Count == 2)
        {
            add(two.Line, two.Predicate, [names[0], names[1]]);
        }
        else
        {
            add(many.Line, many.Predicate, [N(total)]);
        }
    }

    private static void AddCount(int count, Action<string, string, object?[]> add, (string Line, string Predicate) one, (string Line, string Predicate) many)
    {
        if (count == 1)
        {
            add(one.Line, one.Predicate, []);
        }
        else if (count > 1)
        {
            add(many.Line, many.Predicate, [N(count)]);
        }
    }

    private static string? NewName(AiChange change)
    {
        var name = !string.IsNullOrWhiteSpace(change.CategoryName) ? change.CategoryName : change.Detail;
        return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    /// <summary>At most <see cref="MaxLines"/>: the first ones and "…and N more".</summary>
    private static IReadOnlyList<string> Cap(List<string> lines)
    {
        if (lines.Count <= MaxLines)
        {
            return lines;
        }

        var kept = lines.Take(MaxLines - 1).ToList();
        kept.Add(L.Format("Intake_AndMore", N(lines.Count - kept.Count)));
        return kept;
    }

    private static string N(int value) => value.ToString("N0", CultureInfo.CurrentCulture);
}
