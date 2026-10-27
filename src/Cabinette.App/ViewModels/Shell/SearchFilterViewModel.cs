using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Shell;

public enum DateRangeOption
{
    Any,
    Today,
    Week,
    Month,
    Year,
}

public enum SizeRangeOption
{
    Any,
    Small,
    Medium,
    Large,
}

public enum StorageOption
{
    Any,
    Copy,
    Pointer,
}

/// <summary>A toggle chip in the filter flyout.</summary>
public sealed partial class FilterChip : ObservableObject
{
    public FilterChip(string key, string label)
    {
        Key = key;
        Label = label;
    }

    public string Key { get; }

    public string Label { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>Filters for the main-window search: type, date added, size, copy/pointer, missing only, extension.</summary>
public sealed partial class SearchFilterViewModel : ObservableObject
{
    private static readonly string[] TypeKeys =
    [
        "images", "video", "audio", "documents", "text", "notes", "spreadsheets", "presentations", "code",
        "archives", "executable", "ebooks", "fonts", "design", "models3d", "diskimages", "links", "other",
    ];

    private bool _suspend;

    public SearchFilterViewModel()
    {
        Types = TypeKeys.Select(k => new FilterChip(k, L.Get("Type_" + k))).ToList();
        foreach (var chip in Types)
        {
            chip.PropertyChanged += (_, _) => RaiseChanged();
        }
    }

    /// <summary>Raised whenever any filter value changes.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<FilterChip> Types { get; }

    [ObservableProperty]
    private DateRangeOption _dateRange;

    [ObservableProperty]
    private SizeRangeOption _sizeRange;

    [ObservableProperty]
    private StorageOption _storage;

    [ObservableProperty]
    private bool _missingOnly;

    [ObservableProperty]
    private string _extensionsText = "";

    public bool IsEmpty => ToItemFilter().IsEmpty;

    /// <summary>Number of active filter groups (for the badge on the filter button).</summary>
    public int ActiveCount =>
        (Types.Any(t => t.IsChecked) ? 1 : 0) + (DateRange != DateRangeOption.Any ? 1 : 0) + (SizeRange != SizeRangeOption.Any ? 1 : 0) +
        (Storage != StorageOption.Any ? 1 : 0) + (MissingOnly ? 1 : 0) + (ParseExtensions().Count > 0 ? 1 : 0);

    public ItemFilter ToItemFilter()
    {
        var now = DateTimeOffset.Now;
        var types = Types.Where(t => t.IsChecked).Select(t => t.Key).ToList();
        var extensions = ParseExtensions();
        DateTimeOffset? from = DateRange switch
        {
            DateRangeOption.Today => new DateTimeOffset(now.Date, now.Offset),
            DateRangeOption.Week => now.AddDays(-7),
            DateRangeOption.Month => now.AddDays(-30),
            DateRangeOption.Year => now.AddYears(-1),
            _ => null,
        };
        const long mb = 1024 * 1024;
        (long? min, long? max) size = SizeRange switch
        {
            SizeRangeOption.Small => (null, mb - 1),
            SizeRangeOption.Medium => (mb, 100 * mb),
            SizeRangeOption.Large => (100 * mb + 1, null),
            _ => (null, null),
        };

        return new ItemFilter
        {
            TypeKeys = types.Count > 0 ? types : null,
            AddedFrom = from,
            MinSizeBytes = size.min,
            MaxSizeBytes = size.max,
            Mode = Storage switch
            {
                StorageOption.Copy => ItemMode.Copy,
                StorageOption.Pointer => ItemMode.Pointer,
                _ => null,
            },
            MissingOnly = MissingOnly,
            Extensions = extensions.Count > 0 ? extensions : null,
        };
    }

    [RelayCommand]
    private void Reset()
    {
        _suspend = true;
        foreach (var chip in Types)
        {
            chip.IsChecked = false;
        }

        DateRange = DateRangeOption.Any;
        SizeRange = SizeRangeOption.Any;
        Storage = StorageOption.Any;
        MissingOnly = false;
        ExtensionsText = "";
        _suspend = false;
        RaiseChanged();
    }

    partial void OnDateRangeChanged(DateRangeOption value) => RaiseChanged();

    partial void OnSizeRangeChanged(SizeRangeOption value) => RaiseChanged();

    partial void OnStorageChanged(StorageOption value) => RaiseChanged();

    partial void OnMissingOnlyChanged(bool value) => RaiseChanged();

    partial void OnExtensionsTextChanged(string value) => RaiseChanged();

    private IReadOnlyList<string> ParseExtensions() => ExtensionList.Parse(ExtensionsText);

    private void RaiseChanged()
    {
        if (_suspend)
        {
            return;
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ActiveCount));
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
