using Cabinette.App.ViewModels.Shell;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>A chat-list row of a category whose files all left shows "No files yet" and no time.</summary>
public sealed class EmptyCategoryRowTests
{
    private static readonly DateTimeOffset LastUpload = DateTimeOffset.Now.AddMinutes(-5);

    [Fact]
    public void AnEmptiedCategory_ShowsNoTime()
    {
        var row = new CategoryRowViewModel(Summary(itemCount: 0, lastItemName: null), icons: null!);

        Assert.Equal("", row.TimeText);
        row.RefreshTime();
        Assert.Equal("", row.TimeText);
    }

    [Fact]
    public void ACategoryWithFiles_ShowsItsLastUploadTime_AndLosesItWhenEmptied()
    {
        var row = new CategoryRowViewModel(Summary(itemCount: 2, lastItemName: "file.txt"), icons: null!);
        Assert.NotEqual("", row.TimeText);

        row.Update(Summary(itemCount: 0, lastItemName: null));

        Assert.Equal("", row.TimeText);
    }

    private static CategorySummary Summary(int itemCount, string? lastItemName) => new()
    {
        Category = new Category { Id = 7, Name = "Other", FolderName = "Other", CreatedAt = LastUpload, LastUploadAt = LastUpload },
        ItemCount = itemCount,
        LastItemName = lastItemName,
    };
}
