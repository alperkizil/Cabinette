using Cabinette.Core.Categorization;
using Cabinette.Core.Models;
using Cabinette.Core.Services;
using Cabinette.Core.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Cabinette.Core.Tests;

public sealed class SeedingAndSettingsTests
{
    [Fact]
    public async Task FirstOpen_SeedsEveryBuiltInCategory()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var all = await t.Library.GetAllCategoriesAsync();

        Assert.Equal(ExtensionMap.BuiltInCategories.Count, all.Count);
        foreach (var def in ExtensionMap.BuiltInCategories)
        {
            var cat = Assert.Single(all, c => c.BuiltInKey == def.Key);
            Assert.Equal(def.Name, cat.Name);
            Assert.Equal(def.IconKey, cat.IconKey);
            Assert.Null(cat.IconPng);
            Assert.Equal(def.Key == ExtensionMap.DeletedKey ? CategoryKind.System : CategoryKind.BuiltIn, cat.Kind);
            if (def.ParentKey is null)
            {
                Assert.Null(cat.ParentId);
            }
            else
            {
                Assert.Equal(all.Single(c => c.BuiltInKey == def.ParentKey).Id, cat.ParentId);
            }
        }

        Assert.Equal("C-C++", all.Single(c => c.BuiltInKey == "code.cpp").FolderName);
        Assert.Equal("JS-TS", all.Single(c => c.BuiltInKey == "code.jsts").FolderName);
        Assert.Equal("C#", all.Single(c => c.BuiltInKey == "code.csharp").FolderName);
    }

    [Fact]
    public async Task Database_UsesWalAndSchemaVersion1_AndReopenDoesNotReseed()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.ReopenAsync();
        Assert.Equal(ExtensionMap.BuiltInCategories.Count, (await t.Library.GetAllCategoriesAsync()).Count);

        await using var connection = new SqliteConnection($"Data Source={Path.Combine(t.DataDirectory, "cabinette.db")};Pooling=False");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode";
        Assert.Equal("wal", (string)cmd.ExecuteScalar()!);
        cmd.CommandText = "SELECT version FROM schema_info";
        Assert.Equal(1L, (long)cmd.ExecuteScalar()!);
    }

    [Fact]
    public async Task InitializeVault_CreatesAFolderForEveryCategory()
    {
        await using var t = await TestLibrary.CreateAsync();
        Assert.Equal(t.Vault, t.Library.VaultPath);
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Audio")));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Code", "Python")));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Code", "C-C++")));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Code", "JS-TS")));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "Deleted")));
        Assert.True(Directory.Exists(Path.Combine(t.Vault, "3D models")));
    }

    [Fact]
    public async Task InitializeVault_WithAnotherPathLater_IsNotAllowed()
    {
        await using var t = await TestLibrary.CreateAsync();
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.InitializeVaultAsync(Path.Combine(t.Root, "other")));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);

        // The same path again is fine (idempotent).
        await t.Library.InitializeVaultAsync(t.Vault);
    }

    [Fact]
    public async Task CopyAdd_WithoutVault_ThrowsVaultNotConfigured()
    {
        await using var t = await TestLibrary.CreateAsync(initializeVault: false);
        var file = t.CreateFile("a.txt", "hello");
        var ex = await Assert.ThrowsAsync<LibraryException>(() => t.AddAsync(null, ItemMode.Copy, file));
        Assert.Equal(LibraryErrorCode.VaultNotConfigured, ex.Code);

        var notes = await t.BuiltInAsync("notes");
        ex = await Assert.ThrowsAsync<LibraryException>(() => t.Library.CreateNoteAsync(notes.Id, "hi"));
        Assert.Equal(LibraryErrorCode.VaultNotConfigured, ex.Code);
    }

    [Fact]
    public async Task Settings_RoundTripAndPersist()
    {
        await using var t = await TestLibrary.CreateAsync();
        var defaults = await t.Library.GetSettingsAsync();
        Assert.Equal(ItemMode.Copy, defaults.DefaultAddMode);
        Assert.True(defaults.RecycleVaultFiles);
        Assert.Equal(CategorySort.LastUpload, defaults.ChatListSort);

        var changed = defaults with
        {
            DefaultAddMode = ItemMode.Pointer,
            Theme = ThemePreference.Dark,
            StartWithWindows = false,
            CloseToTray = false,
            NotifyOnVaultChanges = false,
            RecycleVaultFiles = false,
            DeleteRealPointerFiles = false,
            CategoryDeletePolicy = CategoryDeletePolicy.Ask,
            FolderSubfoldersAsSubcategories = false,
            FolderAlsoAddToTypeCategories = true,
            ChatListSort = CategorySort.Alphabetical,
        };
        await t.Library.SaveSettingsAsync(changed);
        Assert.Contains(t.Events, e => e.Kind.HasFlag(LibraryChangeKind.Settings));
        Assert.Equal(changed, await t.Library.GetSettingsAsync());

        await t.ReopenAsync();
        Assert.Equal(changed, await t.Library.GetSettingsAsync());
        Assert.Equal(t.Vault, t.Library.VaultPath);
    }

    [Fact]
    public async Task SaveSettings_ChangingTheVaultPath_IsRejected()
    {
        await using var t = await TestLibrary.CreateAsync();
        var settings = await t.Library.GetSettingsAsync();
        var ex = await Assert.ThrowsAsync<LibraryException>(
            () => t.Library.SaveSettingsAsync(settings with { VaultPath = Path.Combine(t.Root, "elsewhere") }));
        Assert.Equal(LibraryErrorCode.NotAllowed, ex.Code);
        Assert.Equal(t.Vault, (await t.Library.GetSettingsAsync()).VaultPath);

        // Saving with a null vault path keeps the configured vault.
        await t.Library.SaveSettingsAsync(settings with { VaultPath = null, Theme = ThemePreference.Light });
        Assert.Equal(t.Vault, (await t.Library.GetSettingsAsync()).VaultPath);
    }

    [Fact]
    public async Task DefaultAddMode_IsUsedWhenRequestHasNoMode()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { DefaultAddMode = ItemMode.Pointer });
        var file = t.CreateFile("p.txt", "pointer me");
        var result = await t.Library.AddPathsAsync([file], new AddRequest());
        var item = Assert.Single(result.Added);
        Assert.Equal(ItemMode.Pointer, item.Mode);
        Assert.Equal(file, item.FullPath);
    }
}
