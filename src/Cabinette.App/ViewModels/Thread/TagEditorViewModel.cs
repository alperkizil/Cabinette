using System.Collections.ObjectModel;
using System.Text;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

public sealed partial class TagChipViewModel : ObservableObject
{
    public TagChipViewModel(string name, bool isPartial)
    {
        Name = name;
        IsPartial = isPartial;
    }

    public string Name { get; }

    /// <summary>Only some of the edited files have this tag (multi-select). Clicking the chip applies it to all.</summary>
    [ObservableProperty]
    public partial bool IsPartial { get; set; }

    [RelayCommand]
    private void Promote() => IsPartial = false;
}

/// <summary>Chip editor for one or more items' tags, with autocomplete from the library's existing tags.</summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    private const int MaxSuggestions = 12;
    private readonly IReadOnlyList<TagInfo> _allTags;

    public TagEditorViewModel(string title, string subtitle, IReadOnlyCollection<string> common, IReadOnlyCollection<string> partial, IReadOnlyList<TagInfo> allTags)
    {
        Title = title;
        Subtitle = subtitle;
        _allTags = allTags;
        InitialCommon = common.ToList();
        InitialPartial = partial.ToList();
        Chips = new ObservableCollection<TagChipViewModel>(
            common.Select(t => new TagChipViewModel(t, false)).Concat(partial.Select(t => new TagChipViewModel(t, true))));
        Input = "";
        RefreshSuggestions();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<string> InitialCommon { get; }

    public IReadOnlyList<string> InitialPartial { get; }

    public ObservableCollection<TagChipViewModel> Chips { get; }

    public ObservableCollection<string> Suggestions { get; } = [];

    [ObservableProperty]
    public partial string Input { get; set; }

    public bool HasPartial => Chips.Any(c => c.IsPartial);

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Tags that every item must end up with.</summary>
    public IReadOnlyList<string> FinalTags => Chips.Where(c => !c.IsPartial).Select(c => c.Name).ToList();

    /// <summary>Tags that must be removed from every item (were there before and the chip was removed).</summary>
    public IReadOnlyList<string> RemovedTags
    {
        get
        {
            var remaining = new HashSet<string>(Chips.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            return InitialCommon.Concat(InitialPartial).Where(t => !remaining.Contains(t)).ToList();
        }
    }

    /// <summary>Tags to add to every item (new chips and promoted partial chips).</summary>
    public IReadOnlyList<string> AddedTags
    {
        get
        {
            var common = new HashSet<string>(InitialCommon, StringComparer.OrdinalIgnoreCase);
            return FinalTags.Where(t => !common.Contains(t)).ToList();
        }
    }

    /// <summary>Normalizes a typed tag the way the library stores it: trimmed, no leading '#', inner spaces become '-'.</summary>
    public static string Normalize(string text)
    {
        var trimmed = text.Trim().TrimStart('#').Trim();
        var sb = new StringBuilder(trimmed.Length);
        var lastDash = false;
        foreach (var c in trimmed)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastDash)
                {
                    sb.Append('-');
                    lastDash = true;
                }

                continue;
            }

            sb.Append(c);
            lastDash = false;
        }

        return sb.ToString();
    }

    /// <summary>Adds the typed text as a chip. Returns false when there was nothing to add.</summary>
    public bool CommitInput()
    {
        var added = false;
        foreach (var part in Input.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            added |= AddTag(part);
        }

        Input = "";
        return added;
    }

    public bool AddTag(string text)
    {
        var name = Normalize(text);
        if (name.Length == 0)
        {
            return false;
        }

        var existing = Chips.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.IsPartial = false;
        }
        else
        {
            // Reuse the library's spelling of an existing tag.
            var known = _allTags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
            Chips.Add(new TagChipViewModel(known?.Name ?? name, false));
        }

        RefreshSuggestions();
        OnPropertyChanged(nameof(HasPartial));
        return true;
    }

    public void RemoveLast()
    {
        if (Chips.Count > 0)
        {
            Chips.RemoveAt(Chips.Count - 1);
            RefreshSuggestions();
            OnPropertyChanged(nameof(HasPartial));
        }
    }

    [RelayCommand]
    private void Remove(TagChipViewModel chip)
    {
        Chips.Remove(chip);
        RefreshSuggestions();
        OnPropertyChanged(nameof(HasPartial));
    }

    [RelayCommand]
    private void AddSuggestion(string name)
    {
        AddTag(name);
        Input = "";
    }

    partial void OnInputChanged(string value)
    {
        if (value.Contains(','))
        {
            CommitInput();
            return;
        }

        RefreshSuggestions();
    }

    private void RefreshSuggestions()
    {
        var used = new HashSet<string>(Chips.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
        var query = Normalize(Input ?? "");
        var matches = _allTags
            .Where(t => !used.Contains(t.Name))
            .Where(t => query.Length == 0 || t.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(t => query.Length > 0 && t.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(t => t.ItemCount)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxSuggestions)
            .Select(t => t.Name);

        Suggestions.Clear();
        foreach (var name in matches)
        {
            Suggestions.Add(name);
        }

        OnPropertyChanged(nameof(HasSuggestions));
    }
}
