using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cabinette.App.ViewModels.Dialogs;

/// <summary>New category: name, parent (picker) and an optional PNG icon; otherwise a generated default icon.</summary>
public sealed partial class NewCategoryDialogViewModel : DialogViewModelBase
{
    private readonly ILibraryService _library;
    private readonly CategoryIconService _icons;
    private readonly CategoryDirectory _directory;
    private readonly DialogService _dialogs;
    private bool _touched;

    public NewCategoryDialogViewModel(
        ILibraryService library,
        CategoryIconService icons,
        CategoryDirectory directory,
        DialogService dialogs,
        long? parentId)
        : base(L.Get(parentId is null ? "NewCategory_Title" : "NewCategory_SubTitle"))
    {
        _library = library;
        _icons = icons;
        _directory = directory;
        _dialogs = dialogs;
        _parentId = parentId;
        _parentPath = DescribeParent(parentId);
    }

    public override double Width => 460;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private string _name = "";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private long? _parentId;

    [ObservableProperty]
    private string _parentPath;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    private byte[]? _iconPng;

    [ObservableProperty]
    private ImageSource? _iconPreview;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isBusy;

    public bool HasCustomIcon => IconPng is not null;

    public override void Accept()
    {
        if (CreateCommand.CanExecute(null))
        {
            CreateCommand.Execute(null);
        }
    }

    partial void OnNameChanged(string value)
    {
        _touched = true;
        Error = NameRules.ValidateCategoryName(value);
    }

    partial void OnParentIdChanged(long? value) => ParentPath = DescribeParent(value);

    private string DescribeParent(long? id) => id is { } value && _directory.Get(value) is not null
        ? _directory.GetPath(value)
        : L.Get("NewCategory_TopLevel");

    [RelayCommand]
    private async Task ChooseParentAsync()
    {
        var result = await _dialogs.ShowCategoryPickerAsync(L.Get("NewCategory_PickParentTitle"), ParentId, allowCreate: false, allowTopLevel: true);
        if (result is not null)
        {
            ParentId = result.CategoryId;
        }
    }

    [RelayCommand]
    private async Task ChooseIconAsync()
    {
        var path = _dialogs.PickIconFile();
        if (path is null)
        {
            return;
        }

        try
        {
            var png = await CategoryIconService.LoadAndScaleIconAsync(path);
            IconPng = png;
            IconPreview = await _icons.GetAsync(png);
        }
        catch (Exception ex)
        {
            AppLog.Warn("Icon file could not be read.", ex);
            Error = L.Get("Category_IconFailed");
        }
    }

    [RelayCommand]
    private void ClearIcon()
    {
        IconPng = null;
        IconPreview = null;
    }

    private bool CanCreate() => !IsBusy && (!_touched || NameRules.ValidateCategoryName(Name) is null);

    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        _touched = true;
        var validation = NameRules.ValidateCategoryName(Name);
        if (validation is not null)
        {
            Error = validation;
            return;
        }

        IsBusy = true;
        try
        {
            var name = Name.Trim();
            var png = IconPng ?? await _icons.GenerateAsync(ExtensionMap.FolderIconKey, name);
            var category = await _library.CreateCategoryAsync(name, ParentId, png);
            Close(category);
        }
        catch (LibraryException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            AppLog.Error("Creating a category failed.", ex);
            Error = L.Get("Notify_Error");
        }
        finally
        {
            IsBusy = false;
        }
    }
}
