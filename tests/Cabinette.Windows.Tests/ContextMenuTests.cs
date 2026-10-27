using System.Globalization;
using System.IO;
using System.Windows.Interop;
using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Tests;

public sealed class ContextMenuTests(ITestOutputHelper output)
{
    private static readonly IReadOnlyList<CustomMenuItem> CustomItems =
    [
        new("show", "Show in folder"),
        new("rename", "Rename"),
        CustomMenuItem.Separator,
        new("tags", "Tags…"),
        new("add", "Add to category") { Children = [new("add:1", "Images"), new("add:2", "R&D")] },
        new("disabled", "Remove from this category") { IsEnabled = false },
        CustomMenuItem.Separator,
        new("delete", "Delete"),
    ];

    private static bool EnglishUi => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en";

    [Fact]
    public void RealFileGetsCustomItemsThenShellItems()
    {
        using var temp = new TempFolder();
        var file = temp.File("menu test.txt");

        var items = Sta.Run(() =>
        {
            using var session = ContextMenuSession.Create(IntPtr.Zero, [file], CustomItems, extendedVerbs: false);
            var snapshot = session.Snapshot(initializeSubmenus: true);

            // "Send to" finishes filling through messages it posts to this thread; a real menu's modal loop dispatches
            // them, so do the same. Without this the check fails whenever the machine is busy.
            Sta.PumpUntil(() => SubmenusFilled(snapshot = session.Snapshot(initializeSubmenus: false)), TimeSpan.FromSeconds(5));
            return snapshot;
        });
        Print(items);

        // Custom items first, in order, with their ids.
        Assert.Equal("show", items[0].CustomId);
        Assert.Equal("rename", items[1].CustomId);
        Assert.True(items[2].IsSeparator);
        var add = items.Single(i => i.Text == "Add to category");
        Assert.Equal(["add:1", "add:2"], add.Children.Select(c => c.CustomId));
        Assert.Equal("R&D", add.Children[1].Text);
        Assert.False(items.Single(i => i.CustomId == "disabled").IsEnabled);
        var lastCustom = items.ToList().FindLastIndex(i => i.CustomId == "delete");

        // Then a separator and the shell's items.
        Assert.True(items[lastCustom + 1].IsSeparator);
        var shell = items.Skip(lastCustom + 2).ToList();
        Assert.True(shell.Count(i => !i.IsSeparator) >= 5, "expected several shell items");
        Assert.All(shell.Where(i => !i.IsSeparator && i.Children.Count == 0), i => Assert.True(i.IsShellItem, i.ToString()));

        var verbs = Flatten(shell).Select(i => i.Verb).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("delete", verbs);
        Assert.Contains("rename", verbs);
        Assert.Contains("properties", verbs);

        // "Send to" is a lazily filled submenu: after WM_INITMENUPOPUP it has real entries.
        var submenus = shell.Where(i => i.Children.Count > 0).ToList();
        Assert.Contains(submenus, s => s.Children.Count(c => !c.IsSeparator) >= 2);
        if (EnglishUi)
        {
            var sendTo = shell.Single(i => i.Text.StartsWith("Send to", StringComparison.OrdinalIgnoreCase));
            Assert.True(sendTo.Children.Count(c => !c.IsSeparator) >= 2, "Send to was not populated");
            Assert.Contains(shell, i => i.Text.StartsWith("Open with", StringComparison.OrdinalIgnoreCase));
        }

        // No doubled separators.
        for (var i = 1; i < items.Count; i++)
        {
            Assert.False(items[i].IsSeparator && items[i - 1].IsSeparator, $"double separator at {i}");
        }
    }

    /// <summary>Cut + paste in Explorer would move a library file away behind Cabinette's back, so the shell's Cut is removed.</summary>
    [Fact]
    public void ShellCutIsRemovedButCopyStays()
    {
        using var temp = new TempFolder();
        var file = temp.File("no cut.txt");
        var other = temp.File(Path.Combine("sub", "other.txt"));

        foreach (var paths in new[] { new[] { file }, new[] { file, other } })
        {
            var items = Sta.Run(() => ShellContextMenuService.BuildMenuForTest(paths, CustomItems, initializeSubmenus: false));
            var verbs = Flatten(items).Select(i => i.Verb).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

            Assert.DoesNotContain("cut", verbs);
            Assert.Contains("copy", verbs);
            Assert.Contains("delete", verbs);
            for (var i = 1; i < items.Count; i++)
            {
                Assert.False(items[i].IsSeparator && items[i - 1].IsSeparator, $"double separator at {i}");
            }
        }
    }

    [Fact]
    public void FilesInDifferentFoldersStillGetShellItems()
    {
        using var temp = new TempFolder();
        var a = temp.File(Path.Combine("one", "a.txt"));
        var b = temp.File(Path.Combine("two", "b.txt"));

        var items = Sta.Run(() => ShellContextMenuService.BuildMenuForTest([a, b], [new("x", "Cabinette item")]));
        Print(items);

        Assert.Equal("x", items[0].CustomId);
        var verbs = Flatten(items).Select(i => i.Verb).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Contains("delete", verbs);
        Assert.Contains("properties", verbs);
    }

    [Fact]
    public void FolderGetsShellItems()
    {
        using var temp = new TempFolder();
        var items = Sta.Run(() => ShellContextMenuService.BuildMenuForTest([temp.Path], [new("x", "Cabinette item")]));
        Print(items);
        Assert.Contains(Flatten(items), i => i.IsShellItem);
    }

    [Fact]
    public void MissingFilesGetOnlyCustomItems()
    {
        var items = Sta.Run(() => ShellContextMenuService.BuildMenuForTest(
            [@"C:\Cabinette\missing\file.txt"], CustomItems, initializeSubmenus: true));

        Assert.All(Flatten(items), i => Assert.False(i.IsShellItem));
        Assert.Equal(CustomItems.Count, items.Count);
        Assert.False(items[^1].IsSeparator);
    }

    [Fact]
    public void ExtendedVerbsAddItems()
    {
        using var temp = new TempFolder();
        var file = temp.File("extended.txt");
        var normal = Sta.Run(() => Flatten(ShellContextMenuService.BuildMenuForTest([file], [], initializeSubmenus: false)).Count());

        var extended = Sta.Run(() => Flatten(ShellContextMenuService.BuildMenuForTest([file], [], initializeSubmenus: false, extendedVerbs: true)).Count());
        output.WriteLine($"normal {normal}, extended {extended}");
        Assert.True(extended >= normal);
    }

    /// <summary>
    /// The real routing path: WM_INITMENUPOPUP sent to the owner window reaches the shell menu through the HwndSource hook
    /// and fills a lazily populated submenu (for images, "Open with" holds one placeholder until it is initialized).
    /// </summary>
    [Fact]
    public void OwnerWindowHookFillsLazySubmenu()
    {
        using var temp = new TempFolder();
        var file = Path.Combine(temp.Path, "route.png");
        ThumbnailTests.WritePng(file, 8, 8, _ => { });

        var (before, after, text) = Sta.Run(() =>
        {
            using var owner = new HwndSource(new HwndSourceParameters("Cabinette menu owner test") { Width = 0, Height = 0 });
            using var session = ContextMenuSession.Create(owner.Handle, [file], [], extendedVerbs: false);
            using var router = MenuMessageRouter.Attach(owner.Handle, session);
            Assert.Equal(owner.Handle, router.OwnerHwnd);

            // Find the submenu that the shell fills lazily (Send to): the one whose item count grows on init.
            var count = GetMenuItemCount(session.Handle);
            for (var i = 0; i < count; i++)
            {
                var sub = GetSubMenu(session.Handle, i);
                if (sub == IntPtr.Zero)
                {
                    continue;
                }

                var itemsBefore = GetMenuItemCount(sub);
                SendMessage(owner.Handle, WM_INITMENUPOPUP, sub, i);
                var itemsAfter = GetMenuItemCount(sub);
                if (itemsAfter > itemsBefore + 1)
                {
                    return (itemsBefore, itemsAfter, session.Snapshot(false)[i].Text);
                }
            }

            return (0, 0, "(none)");
        });

        output.WriteLine($"'{text}': {before} → {after} items");
        Assert.True(after > before + 1);
    }

    /// <summary>What <see cref="RealFileGetsCustomItemsThenShellItems"/> expects of the lazily filled submenus.</summary>
    private static bool SubmenusFilled(IReadOnlyList<MenuItemSnapshot> items)
    {
        static int Entries(MenuItemSnapshot item) => item.Children.Count(c => !c.IsSeparator);

        return EnglishUi
            ? items.Any(i => i.Text.StartsWith("Send to", StringComparison.OrdinalIgnoreCase) && Entries(i) >= 2)
            : items.Any(i => Entries(i) >= 2);
    }

    private void Print(IReadOnlyList<MenuItemSnapshot> items, string indent = "")
    {
        foreach (var item in items)
        {
            output.WriteLine(indent + item);
            Print(item.Children, indent + "    ");
        }
    }

    private static IEnumerable<MenuItemSnapshot> Flatten(IEnumerable<MenuItemSnapshot> items)
        => items.SelectMany(i => i.Children.Count == 0 ? [i] : Flatten(i.Children).Prepend(i));
}
