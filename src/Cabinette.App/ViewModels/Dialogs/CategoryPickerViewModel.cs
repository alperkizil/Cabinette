using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>A row of the category tree in the picker (flattened, indented).</summary>
public sealed partial class PickerNodeViewModel : ObservableObject
{
    public PickerNodeViewModel(Category? category, int depth, string name)
    {
        Category = category;
        Depth = depth;
        Name = name;
    }

    /// <summary>Null for the "Top level" pseudo row and for <see cref="BuiltInKeyToCreate"/>.</summary>
    public Category? Category { get; }

    /// <summary>A built-in category that doesn't exist (any more): offered anyway, and only created when it is chosen.</summary>
    public string? BuiltInKeyToCreate { get; init; }

    public long? Id => Category?.Id;

    public string Name { get; }

    public string? IconKey => BuiltInKeyToCreate is { } key
        ? ExtensionMap.GetDefinition(key)?.IconKey
        : Category?.IconKey ?? (Category is null ? "toplevel" : "folder");

    public int Depth { get; }

    public Thickness Indent => new(Depth * 22, 0, 0, 0);

    public List<PickerNodeViewModel> Children { get; } = [];

    public PickerNodeViewModel? Parent { get; set; }

    public bool HasChildren => Children.Count > 0;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private ImageSource? _icon;

    /// <summary>Search match highlight (when filtering).</summary>
    [ObservableProperty]
    private bool _isMatch;
}

/// <summary>Result of the internal picker: the chosen id, where null means "Top level".</summary>
public sealed record CategoryPickResult(long? CategoryId)
{
    /// <summary>Set when the user chose the offered built-in category that doesn't exist yet: the caller creates it.</summary>
    public string? BuiltInKeyToCreate { get; init; }
}

/// <summary>Category tree picker with search and an optional "New category…" button.</summary>
public sealed partial class CategoryPickerViewModel : DialogViewModelBase
{
    private readonly ILibraryService _library;
    private readonly CategoryIconService _icons;
    private readonly Func<long?, Task<Category?>>? _createCategory;
    private readonly bool _allowTopLevel;
    private readonly long? _excludeSubtreeOf;
    private readonly string? _builtInKey;
    private List<PickerNodeViewModel> _roots = [];
    private PickerNodeViewModel? _topLevelNode;
    private PickerNodeViewModel? _builtInToCreateNode;

    /// <param name="builtInKey">
    /// A top-level built-in category to suggest (e.g. "notes" when pasting text): preselected when it exists (or a
    /// top-level category with its name does); otherwise offered and preselected as a row that is only created if chosen.
    /// </param>
    public CategoryPickerViewModel(
        string title,
        ILibraryService library,
        CategoryIconService icons,
        long? preselectedCategoryId,
        Func<long?, Task<Category?>>? createCategory,
        bool allowTopLevel = false,
        long? excludeSubtreeOf = null,
        string? builtInKey = null)
        : base(title)
    {
        _library = library;
        _icons = icons;
        _createCategory = createCategory;
        _allowTopLevel = allowTopLevel;
        _excludeSubtreeOf = excludeSubtreeOf;
        _builtInKey = builtInKey;
        PreselectedId = preselectedCategoryId;
    }

    public override double Width => 460;

    public override double Height => 560;

    public long? PreselectedId { get; private set; }

    public bool AllowCreate => _createCategory is not null;

    public ObservableCollection<PickerNodeViewModel> VisibleNodes { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ChooseCommand))]
    private PickerNodeViewModel? _selected;

    [ObservableProperty]
    private bool _isEmpty;

    public async Task LoadAsync()
    {
        IReadOnlyList<Category> all;
        try
        {
            all = await _library.GetAllCategoriesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Warn("Picker could not load categories.", ex);
            all = [];
        }

        var excluded = new HashSet<long>();
        if (_excludeSubtreeOf is { } excludedRoot)
        {
            excluded.Add(excludedRoot);
            bool added;
            do
            {
                added = false;
                foreach (var c in all)
                {
                    if (c.ParentId is { } p && excluded.Contains(p) && excluded.Add(c.Id))
                    {
                        added = true;
                    }
                }
            }
            while (added);
        }

        var usable = all.Where(c => c.Kind != CategoryKind.System && !excluded.Contains(c.Id)).ToList();
        var byParent = usable.ToLookup(c => c.ParentId);
        var comparer = StringComparer.CurrentCultureIgnoreCase;

        List<PickerNodeViewModel> Build(long? parentId, int depth, PickerNodeViewModel? parent)
        {
            var nodes = new List<PickerNodeViewModel>();
            foreach (var category in byParent[parentId].OrderBy(c => c.Name, comparer))
            {
                var node = new PickerNodeViewModel(category, depth, category.Name) { Parent = parent };
                node.Children.AddRange(Build(category.Id, depth + 1, node));
                node.Icon = _icons.TryGetCached(category.IconPng);
                if (node.Icon is null && category.IconPng is not null)
                {
                    _ = LoadIconAsync(node, category.IconPng);
                }

                nodes.Add(node);
            }

            return nodes;
        }

        _roots = Build(null, 0, null);
        _topLevelNode = _allowTopLevel ? new PickerNodeViewModel(null, 0, L.Get("NewCategory_TopLevel")) : null;
        AddSuggestedBuiltIn(usable, comparer);

        // Expand the path to the preselected category.
        var preselected = Flatten(_roots).FirstOrDefault(n => n.Id is not null && n.Id == PreselectedId);
        for (var p = preselected?.Parent; p is not null; p = p.Parent)
        {
            p.IsExpanded = true;
        }

        Rebuild();
        Selected = preselected
                   ?? (PreselectedId is null ? _builtInToCreateNode : null)
                   ?? (_allowTopLevel && PreselectedId is null ? _topLevelNode : null);
    }

    /// <summary>
    /// The suggested built-in category: preselect it when it exists. When the user deleted it, offer a row for it instead of
    /// re-creating it up front, so cancelling (or choosing another category) leaves the library as it was.
    /// </summary>
    private void AddSuggestedBuiltIn(List<Category> usable, StringComparer comparer)
    {
        _builtInToCreateNode = null;
        if (_builtInKey is not { } key || ExtensionMap.GetDefinition(key) is not { ParentKey: null } definition)
        {
            return;
        }

        // A user category with the default name is what the library would adopt as the built-in anyway.
        var existing = usable.FirstOrDefault(c => c.BuiltInKey == key)
                       ?? usable.FirstOrDefault(c => c.ParentId is null && c.BuiltInKey is null && comparer.Equals(c.Name, definition.Name));
        if (existing is not null)
        {
            PreselectedId ??= existing.Id;
            return;
        }

        var node = new PickerNodeViewModel(null, 0, definition.Name) { BuiltInKeyToCreate = key };
        var index = _roots.FindIndex(r => comparer.Compare(r.Name, node.Name) > 0);
        _roots.Insert(index < 0 ? _roots.Count : index, node);
        _builtInToCreateNode = node;
        _ = LoadDefaultIconAsync(node, definition);
    }

    private async Task LoadDefaultIconAsync(PickerNodeViewModel node, BuiltInCategoryDefinition definition)
    {
        if (await _icons.GenerateAsync(definition.IconKey, definition.Name) is { } png)
        {
            node.Icon = await _icons.GetAsync(png);
        }
    }

    public void ToggleExpand(PickerNodeViewModel node)
    {
        if (!node.HasChildren || !string.IsNullOrWhiteSpace(SearchText))
        {
            return;
        }

        node.IsExpanded = !node.IsExpanded;
        Rebuild();
    }

    public override void Accept()
    {
        if (ChooseCommand.CanExecute(null))
        {
            ChooseCommand.Execute(null);
        }
    }

    partial void OnSearchTextChanged(string value) => Rebuild();

    private bool CanChoose() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanChoose))]
    private void Choose()
    {
        if (Selected is not null)
        {
            Close(new CategoryPickResult(Selected.Id) { BuiltInKeyToCreate = Selected.BuiltInKeyToCreate });
        }
    }

    [RelayCommand]
    private void Toggle(PickerNodeViewModel? node)
    {
        if (node is not null)
        {
            ToggleExpand(node);
        }
    }

    [RelayCommand]
    private async Task NewCategoryAsync()
    {
        if (_createCategory is null)
        {
            return;
        }

        var created = await _createCategory(Selected?.Id);
        if (created is null)
        {
            return;
        }

        PreselectedId = created.Id;
        SearchText = "";
        await LoadAsync();
    }

    private void Rebuild()
    {
        var query = SearchText.Trim();
        var visible = new List<PickerNodeViewModel>();
        if (query.Length == 0)
        {
            if (_topLevelNode is not null)
            {
                visible.Add(_topLevelNode);
            }

            void Walk(IEnumerable<PickerNodeViewModel> nodes)
            {
                foreach (var node in nodes)
                {
                    node.IsMatch = false;
                    visible.Add(node);
                    if (node.IsExpanded)
                    {
                        Walk(node.Children);
                    }
                }
            }

            Walk(_roots);
        }
        else
        {
            // Matches plus their ancestors, fully expanded.
            bool Walk(PickerNodeViewModel node, List<PickerNodeViewModel> output)
            {
                var own = node.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);
                var childOutput = new List<PickerNodeViewModel>();
                var anyChild = false;
                foreach (var child in node.Children)
                {
                    anyChild |= Walk(child, childOutput);
                }

                if (!own && !anyChild)
                {
                    return false;
                }

                node.IsMatch = own;
                output.Add(node);
                output.AddRange(childOutput);
                return true;
            }

            foreach (var root in _roots)
            {
                Walk(root, visible);
            }
        }

        var selected = Selected;
        VisibleNodes.Clear();
        foreach (var node in visible)
        {
            VisibleNodes.Add(node);
        }

        IsEmpty = VisibleNodes.Count == 0;
        Selected = selected is not null && visible.Contains(selected) ? selected : (query.Length > 0 ? visible.FirstOrDefault(n => n.IsMatch) : selected);
    }

    private async Task LoadIconAsync(PickerNodeViewModel node, byte[] png) => node.Icon = await _icons.GetAsync(png);

    private static IEnumerable<PickerNodeViewModel> Flatten(IEnumerable<PickerNodeViewModel> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.Children))
            {
                yield return child;
            }
        }
    }
}
