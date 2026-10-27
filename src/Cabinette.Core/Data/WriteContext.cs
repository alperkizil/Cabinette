using System.Diagnostics;
using Cabinette.Core.Models;
using Cabinette.Core.Vault;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Data;

/// <summary>
/// One serialized write: an open connection with an IMMEDIATE transaction, the category snapshot,
/// a journal of file-system changes (undone if the transaction fails), post-commit actions and the change set for the event.
/// </summary>
internal sealed class WriteContext
{
    private readonly List<Action> _undo = [];
    private readonly List<Action> _postCommit = [];
    private CategoryTree? _tree;

    public WriteContext(SqliteConnection connection, SqliteTransaction transaction, string? vaultPath, DateTimeOffset now)
    {
        Connection = connection;
        Transaction = transaction;
        VaultPath = vaultPath;
        Now = now;
        NowText = DbValues.ToText(now);
    }

    public SqliteConnection Connection { get; }

    public SqliteTransaction Transaction { get; }

    public string? VaultPath { get; }

    public DateTimeOffset Now { get; }

    public string NowText { get; }

    public ChangeSet Changes { get; } = new();

    /// <summary>Error to throw after the commit and the Changed event (partial success).</summary>
    public Exception? PostCommitError { get; set; }

    public CategoryTree Tree => _tree ??= CategoryTree.Load(Connection, Transaction);

    public void InvalidateTree() => _tree = null;

    // ------------------------------------------------------------ SQL

    public int Execute(string sql, object? param = null) => Connection.Execute(sql, param, Transaction);

    public T? Scalar<T>(string sql, object? param = null) => Connection.ExecuteScalar<T>(sql, param, Transaction);

    public List<T> Query<T>(string sql, object? param = null) => Connection.Query<T>(sql, param, Transaction).AsList();

    public T? QuerySingleOrDefault<T>(string sql, object? param = null) => Connection.QuerySingleOrDefault<T>(sql, param, Transaction);

    // ------------------------------------------------------------ files (journaled)

    public void MoveFile(string from, string to)
    {
        FileOps.MoveFile(from, to);
        _undo.Add(() => File.Move(to, from));
    }

    public void MoveDirectory(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase))
        {
            if (string.Equals(from, to, StringComparison.Ordinal))
            {
                return;
            }

            // A letter-case-only rename goes through a temporary name. Both steps are journaled, so a failure in the second
            // step (or later in the write) puts the folder back under its old name.
            var temp = FileOps.CaseRenameTempPath(from);
            MoveDirectory(from, temp);
            AfterCaseRenameFirstStep?.Invoke(temp);
            MoveDirectory(temp, to);
            return;
        }

        FileOps.MoveDirectory(from, to);
        _undo.Add(() => FileOps.MoveDirectory(to, from));
    }

    /// <summary>Test hook: runs between the two steps of a letter-case-only folder rename, with the temporary path.</summary>
    internal Action<string>? AfterCaseRenameFirstStep { get; set; }

    public void EnsureDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            return;
        }

        // Remember the top-most folder we create so a rollback can remove it again (only if still empty).
        var missing = new List<string>();
        for (var current = path; current is not null && !Directory.Exists(current); current = Path.GetDirectoryName(current))
        {
            missing.Add(current);
        }

        FileOps.CreateDirectory(path);

        // Register top-most first: undo runs in reverse, so the deepest folder is removed first.
        missing.Reverse();
        foreach (var created in missing)
        {
            _undo.Add(() =>
            {
                if (Directory.Exists(created) && !Directory.EnumerateFileSystemEntries(created).Any())
                {
                    Directory.Delete(created);
                }
            });
        }
    }

    /// <summary>Registers a file that was just created so a rollback deletes it.</summary>
    public void FileCreated(string path) => _undo.Add(() => FileOps.TryDelete(path));

    /// <summary>Runs after a successful commit (still inside the write lock). Exceptions become <see cref="PostCommitError"/>.</summary>
    public void OnCommitted(Action action) => _postCommit.Add(action);

    internal void Undo()
    {
        for (var i = _undo.Count - 1; i >= 0; i--)
        {
            try
            {
                _undo[i]();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cabinette: undo step failed: {ex}");
            }
        }

        _undo.Clear();
    }

    internal void RunPostCommit()
    {
        foreach (var action in _postCommit)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PostCommitError ??= ex;
            }
        }
    }
}

/// <summary>Accumulates what changed during one or more writes; becomes one <see cref="LibraryChangedEventArgs"/>.</summary>
internal sealed class ChangeSet
{
    public LibraryChangeKind Kind { get; set; }

    public HashSet<long> CategoryIds { get; } = [];

    public HashSet<long> ItemIds { get; } = [];

    public List<DuplicateInfo> MergedDuplicates { get; } = [];

    public AddedBy? Source { get; set; }

    public bool IsEmpty => Kind == LibraryChangeKind.None && CategoryIds.Count == 0 && ItemIds.Count == 0 && MergedDuplicates.Count == 0;

    /// <summary>Category changed (properties, counts or contents): adds it and its ancestors.</summary>
    public void Category(CategoryTree tree, long categoryId)
    {
        Kind |= LibraryChangeKind.Categories;
        AddWithAncestors(tree, categoryId);
    }

    public void Categories(CategoryTree tree, IEnumerable<long> categoryIds)
    {
        foreach (var id in categoryIds)
        {
            Category(tree, id);
        }
    }

    /// <summary>Only the unread state of these categories (and so of their ancestors) changed.</summary>
    public void Viewed(CategoryTree tree, IEnumerable<long> categoryIds)
    {
        Kind |= LibraryChangeKind.Viewed;
        foreach (var id in categoryIds)
        {
            AddWithAncestors(tree, id);
        }
    }

    /// <summary>Item changed; <paramref name="categoryIds"/> (with ancestors) are the threads showing it.</summary>
    public void Item(long itemId, CategoryTree? tree = null, IEnumerable<long>? categoryIds = null)
    {
        Kind |= LibraryChangeKind.Items;
        ItemIds.Add(itemId);
        if (tree is not null && categoryIds is not null)
        {
            foreach (var id in categoryIds)
            {
                AddWithAncestors(tree, id);
            }
        }
    }

    public void MergeFrom(ChangeSet other)
    {
        Kind |= other.Kind;
        CategoryIds.UnionWith(other.CategoryIds);
        ItemIds.UnionWith(other.ItemIds);
        MergedDuplicates.AddRange(other.MergedDuplicates);
        Source ??= other.Source;
    }

    public void Clear()
    {
        Kind = LibraryChangeKind.None;
        CategoryIds.Clear();
        ItemIds.Clear();
        MergedDuplicates.Clear();
    }

    public LibraryChangedEventArgs ToEventArgs() => new()
    {
        Kind = Kind,
        CategoryIds = CategoryIds.ToArray(),
        ItemIds = ItemIds.ToArray(),
        Source = Source,
        MergedDuplicates = MergedDuplicates.ToArray(),
    };

    private void AddWithAncestors(CategoryTree tree, long categoryId)
    {
        CategoryIds.Add(categoryId);
        foreach (var ancestor in tree.AncestorIds(categoryId))
        {
            CategoryIds.Add(ancestor);
        }
    }
}
