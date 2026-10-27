using System.Collections.ObjectModel;
using System.ComponentModel;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Thread;

public enum DateFilterPreset
{
    Any = 0,
    Today,
    Last7Days,
    Last30Days,
    ThisYear,
    Custom,
}

public enum SizeFilterPreset
{
    Any = 0,
    Small,
    Medium,
    Large,
}

public enum ModeFilter
{
    All = 0,
    Copy,
    Pointer,
}

public sealed partial class FilterOption : ObservableObject
{
    public FilterOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>State of the thread's filter flyout (SPEC.md §7): type, date added, size, copy/pointer, missing only, extension.</summary>
public sealed partial class ThreadFilterViewModel : ObservableObject
{
    public const long SmallLimitBytes = 1024 * 1024;
    public const long LargeLimitBytes = 100L * 1024 * 1024;

    private bool _suppress;

    public ThreadFilterViewModel()
    {
        var types = ExtensionMap.BuiltInCategories
            .Where(d => d.ParentKey is null && d.Key is not ExtensionMap.DeletedKey)
            .Select(d => new FilterOption(d.Key, ThreadItemViewModel.TypeName(d.Key, ItemKind.File)));
        Types = new ObservableCollection<FilterOption>(types);
        foreach (var option in Types)
        {
            option.PropertyChanged += OnOptionChanged;
        }

        ExtensionsText = "";
    }

    /// <summary>Raised after any change, so the thread can re-query (debounced by the caller).</summary>
    public event EventHandler? Changed;

    public ObservableCollection<FilterOption> Types { get; }

    [ObservableProperty]
    public partial DateFilterPreset DatePreset { get; set; }

    [ObservableProperty]
    public partial DateTime? CustomFrom { get; set; }

    [ObservableProperty]
    public partial DateTime? CustomTo { get; set; }

    [ObservableProperty]
    public partial SizeFilterPreset SizePreset { get; set; }

    [ObservableProperty]
    public partial ModeFilter Mode { get; set; }

    [ObservableProperty]
    public partial bool MissingOnly { get; set; }

    [ObservableProperty]
    public partial string ExtensionsText { get; set; }

    public bool IsCustomDate => DatePreset == DateFilterPreset.Custom;

    public bool IsActive => !Build().IsEmpty;

    public ItemFilter Build()
    {
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(now.Date, now.Offset);
        (DateTimeOffset? from, DateTimeOffset? to) = DatePreset switch
        {
            DateFilterPreset.Today => (today, null),
            DateFilterPreset.Last7Days => (today.AddDays(-6), null),
            DateFilterPreset.Last30Days => (today.AddDays(-29), null),
            DateFilterPreset.ThisYear => (new DateTimeOffset(new DateTime(now.Year, 1, 1), now.Offset), null),
            DateFilterPreset.Custom => (
                CustomFrom is { } f ? new DateTimeOffset(f.Date, now.Offset) : null,
                CustomTo is { } t ? new DateTimeOffset(t.Date.AddDays(1).AddTicks(-1), now.Offset) : null),
            _ => ((DateTimeOffset?)null, (DateTimeOffset?)null),
        };

        (long? min, long? max) = SizePreset switch
        {
            SizeFilterPreset.Small => ((long?)null, (long?)SmallLimitBytes - 1),
            SizeFilterPreset.Medium => (SmallLimitBytes, LargeLimitBytes),
            SizeFilterPreset.Large => (LargeLimitBytes + 1, null),
            _ => ((long?)null, (long?)null),
        };

        var types = Types.Where(t => t.IsSelected).Select(t => t.Key).ToList();
        var extensions = Services.ExtensionList.Parse(ExtensionsText);

        return new ItemFilter
        {
            TypeKeys = types.Count > 0 ? types : null,
            AddedFrom = from,
            AddedTo = to,
            MinSizeBytes = min,
            MaxSizeBytes = max,
            Mode = Mode switch
            {
                ModeFilter.Copy => ItemMode.Copy,
                ModeFilter.Pointer => ItemMode.Pointer,
                _ => null,
            },
            MissingOnly = MissingOnly,
            Extensions = extensions.Count > 0 ? extensions : null,
        };
    }

    [RelayCommand]
    public void Reset()
    {
        _suppress = true;
        try
        {
            foreach (var option in Types)
            {
                option.IsSelected = false;
            }

            DatePreset = DateFilterPreset.Any;
            CustomFrom = null;
            CustomTo = null;
            SizePreset = SizeFilterPreset.Any;
            Mode = ModeFilter.All;
            MissingOnly = false;
            ExtensionsText = "";
        }
        finally
        {
            _suppress = false;
        }

        RaiseChanged();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(DatePreset))
        {
            base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsCustomDate)));
        }

        if (e.PropertyName is not (nameof(IsActive) or nameof(IsCustomDate)))
        {
            RaiseChanged();
        }
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs e) => RaiseChanged();

    private void RaiseChanged()
    {
        if (_suppress)
        {
            return;
        }

        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(IsActive)));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
