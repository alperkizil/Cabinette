using Cabinette.App.ViewModels.Thread;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// A date taken that the library read in the background (SPEC.md section 4, "Gallery") reaches an open thread: the library raises
/// an Items change for the item alone, the thread reloads, and the reloaded item counts as changed, so its view model takes it.
/// </summary>
public sealed class DateTakenRefreshTests
{
    private static readonly LibraryItem Photo = new()
    {
        Id = 7,
        FileName = "photo.jpg",
        Extension = "jpg",
        FullPath = @"C:\Vault\Images\photo.jpg",
        HomeCategoryId = 3,
        CategoryIds = [3],
        TypeKey = "images",
        AddedAt = new DateTimeOffset(2026, 10, 24, 9, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public void TheChangeForALandedDate_ReloadsAThreadShowingTheItem_AndNoOther()
    {
        var change = new LibraryChangedEventArgs { Kind = LibraryChangeKind.Items, ItemIds = [Photo.Id] };

        Assert.Equal((true, false, false), ThreadViewModel.ClassifyChange(change, categoryId: 3, new HashSet<long> { Photo.Id }));
        Assert.Equal((false, false, false), ThreadViewModel.ClassifyChange(change, categoryId: 4, new HashSet<long> { 8 }));
    }

    [Fact]
    public void AReloadedItemWithItsDateTaken_CountsAsChanged()
    {
        var withDate = Photo with { TakenAt = new DateTimeOffset(2019, 7, 14, 12, 30, 45, TimeSpan.Zero) };

        Assert.True(ThreadItemViewModel.SameContent(Photo, Photo with { }));
        Assert.False(ThreadItemViewModel.SameContent(Photo, withDate));
        Assert.False(ThreadItemViewModel.SameContent(withDate, withDate with { TakenAt = withDate.TakenAt!.Value.AddHours(1) }));
        Assert.True(ThreadItemViewModel.SameContent(withDate, withDate with { }));
    }
}
