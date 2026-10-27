using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.Services.Ai;
using Cabinette.App.Services.Monitor;
using Cabinette.App.Services.Tray;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Shell;
using Cabinette.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Cabinette.App.DesignTime;

/// <summary>
/// Dev tool behind <c>--screenshot</c>: renders a window off-screen to a PNG (optionally after driving it into a
/// named state with <c>--screenshot-state</c>), then the app exits. Nothing is ever shown on screen.
/// States: main, thread, live-switch, search, tags, filter, drilldown, settings, settings-&lt;section&gt; (appearance, library,
/// adding, deleting, chatlist, windows), dialog, confirm, prompt, picker, picker-new, newcategory, menu, snackbar, adding,
/// paste, drop, firstrun, firstrun-restore; stage 2: tray-menu, progress, progress-backup, move-confirm, move-result,
/// migrate-result, backup-options, backup-result, restore-details, restore-confirm. Appending "-missing" to a state
/// shows the vault monitor as "Vault not found" (e.g. settings-library-missing, tray-menu-missing).
/// Drag-and-drop motion: drop-thread, drop-window, drop-list and film-&lt;name&gt; (see <see cref="MotionPreview"/>);
/// appending "-still" renders a state with animations off. Appending "-minsize" renders any state in a window at its
/// minimum size (e.g. thread:video-minsize with --text-size xlarge, the narrowest thread banner). Appending "-boldswitch"
/// turns Bold text the other way live before the state is set up (compare with a clean start with the other --bold-text).
/// Threads: thread:&lt;name&gt;, thread-menu:&lt;name&gt; (the file menu), thread-select:&lt;name&gt; (three files selected: the bulk action
/// bar, with the notice of files sorted by type, then the one of "Move to category…"), thread-search:&lt;name&gt; (the thread's
/// search box open with a query).
/// Starred files: starred, starred-grid, starred-details, starred-select, starred-menu (the chat list's Starred row shows in
/// "main" and every other top-level state while the sample library has starred files).
/// Gallery (Images): gallery, gallery-small, gallery-large, gallery-name, gallery-taken, gallery-select, gallery-tooltip,
/// gallery-datemenu, gallery-narrow (the window at its minimum width).
/// Quick switcher (Ctrl+K): switcher (empty box: recent categories), switcher:&lt;query&gt; (matches) and
/// switcher-go:&lt;query&gt; (Enter on the best match: its thread, scrolled to it).
/// Stage 4: settings-ai (Settings › AI with an installed path), settings-ai-activity (scrolled to the activity log),
/// settings-ai-empty (a filter that matches nothing), settings-ai-dev (this run's own path and hint) and ai-notice (the
/// in-app notice of an AI's changes). Settings › Appearance › Font: settings-fontlist (the list drawn open).
/// </summary>
public static class ScreenshotRunner
{
    public const double Width = 1200;
    public const double Height = 760;

    public static void PrepareOffscreen(Window window, double width = Width, double height = Height)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -20000;
        window.Top = -20000;
        window.Width = width;
        window.Height = height;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;

        // Off-screen is not enough: a state that moves keyboard focus (an opening dialog focuses its first control)
        // makes WPF call SetFocus, which activated the window and made it the foreground window, taking the keyboard
        // from whatever the user was typing in. WS_EX_NOACTIVATE keeps it from ever becoming the foreground window.
        window.SourceInitialized += (_, _) =>
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            var style = GetWindowLongPtr(handle, GwlExStyle);
            SetWindowLongPtr(handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        };
    }

    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    public static async Task CaptureMainAsync(MainWindow window, MainViewModel vm, IThemeService theme, IServiceProvider services, string state, string path)
    {
        // "-boldswitch" (always last) turns Bold text the other way live, once the window has been drawn, before the state
        // is set up: the result must match a clean start with the other --bold-text.
        var boldSwitch = state.EndsWith("-boldswitch", StringComparison.Ordinal);
        if (boldSwitch)
        {
            state = state[..^"-boldswitch".Length];
        }

        // "-minsize" renders any state in a window at its minimum size (with --text-size xlarge: the narrowest thread pane).
        if (state.EndsWith("-minsize", StringComparison.Ordinal))
        {
            window.Width = window.MinWidth;
            window.Height = window.MinHeight;
            state = state[..^"-minsize".Length];
        }

        // "-still" renders any state with animations off (the reduced-motion path).
        if (state.EndsWith("-still", StringComparison.Ordinal))
        {
            Views.Motion.MotionSettings.Override = false;
            state = state[..^"-still".Length];
        }

        await SettleAsync(700);
        if (boldSwitch)
        {
            UiWeight.Apply(!UiWeight.IsBold);
            await SettleAsync(200);
        }

        FrameworkElement? menu = null;
        Point menuAt = default;

        // Drag-and-drop motion: "film-<name>" saves a contact sheet of frames; see MotionPreview.
        if (state.StartsWith("film-", StringComparison.Ordinal))
        {
            await MotionPreview.FilmAsync(window, vm, services, state["film-".Length..], path);
            return;
        }

        if (await MotionPreview.ShowAsync(window, vm, state))
        {
            state = "";
        }

        // Stage 2 states: vault monitor status, tray menu and the vault operation dialogs, with sample values.
        var monitor = services.GetRequiredService<VaultMonitorHost>();
        monitor.ShowPreview(state.EndsWith("-missing", StringComparison.Ordinal)
            ? new VaultMonitorStatus(VaultMonitorState.VaultUnavailable, 0, DateTimeOffset.Now.AddHours(-2), @"D:\ isn't connected.")
            : new VaultMonitorStatus(VaultMonitorState.Idle, 0, DateTimeOffset.Now.AddMinutes(-7), null));
        if (state.EndsWith("-missing", StringComparison.Ordinal))
        {
            state = state[..^"-missing".Length];
        }

        if (await ShowVaultDialogAsync(vm, state))
        {
            state = "";
        }
        else if (state == "tray-menu")
        {
            menu = services.GetRequiredService<TrayIconService>().BuildMenu();
            menuAt = new Point(ScreenshotRunner.Width - 330, ScreenshotRunner.Height - 330);
            state = "";
        }

        if (state.StartsWith("settings-ai", StringComparison.Ordinal) || state == "ai-notice")
        {
            await ShowAiAsync(window, vm, services, state);
            state = "";
        }

        if (state == "switcher" || state.StartsWith("switcher:", StringComparison.Ordinal) || state.StartsWith("switcher-go:", StringComparison.Ordinal))
        {
            await ShowQuickSwitcherAsync(vm, state);
            state = "";
        }

        // "thread:<name>" opens any top-level category (used for end-to-end checks against a real library).
        if (state.StartsWith("thread:", StringComparison.Ordinal))
        {
            await OpenByNameAsync(vm, state["thread:".Length..]);
            await SettleAsync(1500);
            state = "";
        }

        // "thread-search:<name>" opens the thread's search box with a query (the banner counts "n of m files").
        if (state.StartsWith("thread-search:", StringComparison.Ordinal))
        {
            await OpenByNameAsync(vm, state["thread-search:".Length..]);
            if (vm.CurrentThread is { } thread)
            {
                thread.IsSearchOpen = true;
                thread.SearchText = "a";
            }

            await SettleAsync(1500);
            state = "";
        }

        // "thread-menu:<name>" also selects the newest file and shows the styled file menu over it.
        if (state.StartsWith("thread-menu:", StringComparison.Ordinal))
        {
            await OpenByNameAsync(vm, state["thread-menu:".Length..]);
            await SettleAsync(1500);
            if (vm.CurrentThread is { Items.Count: > 0 } thread)
            {
                thread.SelectOnly([thread.Items[^1]]);
                menu = Views.Thread.ThreadItemMenuBuilder.Build(thread, default);
                menuAt = new Point(560, 330);
            }

            state = "";
        }

        // "thread-select:<name>" selects the three newest files (the bulk action bar) and queues the notices of files
        // sorted by type and of "Move to category…", with sample text.
        if (state.StartsWith("thread-select:", StringComparison.Ordinal))
        {
            await OpenByNameAsync(vm, state["thread-select:".Length..]);
            await SettleAsync(1500);
            if (vm.CurrentThread is { Items.Count: > 0 } thread)
            {
                thread.SelectOnly(thread.Items.TakeLast(3).ToList());
            }

            var parts = string.Join(", ", L.Format("Intake_SortedPart", 2, "Images"), L.Format("Intake_SortedPart", 1, "Code › Python"));
            vm.Notifications.Show(L.Format("Intake_SortedSplitStayed", parts, 1), NotificationKind.Success, L.Get("Intake_Show"), () => { });
            vm.Notifications.Show(L.Format("Thread_MovedToCategoryMany", 3, "Travel 2026"), NotificationKind.Success, L.Get("Thread_ShowAction"), () => { });
            state = "";
        }

        // Starred files: "starred" (the view, its row selected in the chat list), "starred-grid", "starred-details",
        // "starred-select" (two files selected: the bar offers Unstar, no Move or Remove) and "starred-menu" (the file menu).
        if (state.StartsWith("starred", StringComparison.Ordinal))
        {
            await vm.OpenStarredAsync();
            await SettleAsync(1200);
            if (vm.CurrentThread is { } starred)
            {
                switch (state)
                {
                    case "starred-grid":
                        starred.ViewMode = ViewModels.Thread.ThreadViewMode.Grid;
                        break;
                    case "starred-details":
                        starred.ViewMode = ViewModels.Thread.ThreadViewMode.Details;
                        break;
                    case "starred-select" when starred.Items.Count > 1:
                        starred.SelectOnly(starred.Items.TakeLast(2).ToList());
                        break;
                    case "starred-menu" when starred.Items.Count > 0:
                        starred.SelectOnly([starred.Items[^1]]);
                        menu = Views.Thread.ThreadItemMenuBuilder.Build(starred, default);
                        menuAt = new Point(560, 330);
                        break;
                }
            }

            state = "";
        }

        // Gallery (SPEC.md section 4, "Gallery"): "gallery" (Images, which opens in it), "gallery-small" and "gallery-large"
        // (cell sizes), "gallery-name" (sorted by name), "gallery-taken" (by date taken, oldest first), "gallery-select" (three cells
        // selected with the arrow keys: the bulk action bar), "gallery-tooltip" (a cell's tooltip), "gallery-datemenu" (the
        // Date choice) and "gallery-narrow" (the window at its minimum width; add --text-size xlarge for the tightest case).
        if (state.StartsWith("gallery", StringComparison.Ordinal))
        {
            if (state == "gallery-narrow")
            {
                window.Width = window.MinWidth;
                await SettleAsync(300);
            }

            await OpenByNameAsync(vm, "Images");
            await SettleAsync(1200);
            if (vm.CurrentThread is { IsGalleryView: true } gallery)
            {
                switch (state)
                {
                    case "gallery-small":
                        gallery.GalleryCellSize = ViewModels.Thread.GalleryCellSize.Small;
                        break;
                    case "gallery-large":
                        gallery.GalleryCellSize = ViewModels.Thread.GalleryCellSize.Large;
                        break;
                    case "gallery-name":
                        gallery.SortGalleryBy(ViewModels.Thread.GallerySortKey.Name);
                        break;
                    case "gallery-taken":
                        // By date taken, oldest first (a second click on Date reverses), so the photos' own dates lead.
                        gallery.SetGalleryDateKind(ViewModels.Thread.GalleryDateKind.Taken);
                        gallery.SortGalleryBy(ViewModels.Thread.GallerySortKey.Date);
                        break;
                    case "gallery-select":
                        gallery.MoveGalleryCursor(ViewModels.Thread.GalleryMove.Right);
                        gallery.MoveGalleryCursor(ViewModels.Thread.GalleryMove.Right, extend: true);
                        gallery.MoveGalleryCursor(ViewModels.Thread.GalleryMove.Right, extend: true);
                        break;
                    case "gallery-tooltip" when gallery.GalleryRows.OfType<ViewModels.Thread.GalleryCellRow>().FirstOrDefault() is { } row:
                        menu = new ToolTip { Content = row.Items[^1].GalleryToolTipText };
                        menuAt = new Point(700, 300);
                        break;
                    case "gallery-datemenu":
                        menu = Views.Thread.ThreadView.BuildGalleryDateMenu(gallery);
                        menuAt = new Point(640, 120);
                        break;
                }
            }

            state = "";
        }

        switch (state)
        {
            case "thread":
                await OpenByNameAsync(vm, "Images");
                break;
            case "live-switch":
                // Swap themes a few times at runtime; the result must match a clean start in the final theme.
                await OpenByNameAsync(vm, "Documents");
                foreach (var preference in new[] { ThemePreference.Dark, ThemePreference.Light, ThemePreference.Dark })
                {
                    theme.Apply(preference);
                    await SettleAsync(150);
                }

                vm.Search.Query = "#";
                break;
            case "adding":
                _ = vm.DropAsync([@"C:\Windows\Media"], null);
                break;
            case "paste":
                _ = vm.PasteCommand.ExecuteAsync(null);
                break;
            case "picker-new":
                {
                    _ = vm.Dialogs.PickCategoryAsync(L.Get("Intake_PasteTitleText"), null);
                    await SettleAsync(400);
                    if (vm.Dialogs.Top is ViewModels.Dialogs.CategoryPickerViewModel picker)
                    {
                        _ = picker.NewCategoryCommand.ExecuteAsync(null);
                    }

                    break;
                }

            case var s when s.StartsWith("settings-", StringComparison.Ordinal):
                _ = vm.OpenSettingsAsync(s["settings-".Length..]);
                break;
            case "search":
                window.ChatListPane.FocusSearch();
                vm.Search.Query = "inv";
                break;
            case "tags":
                vm.Search.Query = "#";
                break;
            case "filter":
                vm.Search.Filter.Types.First(t => t.Key == "documents").IsChecked = true;
                vm.Search.Filter.DateRange = DateRangeOption.Month;
                vm.IsFilterOpen = true;
                break;
            case "drilldown":
                {
                    var work = vm.ChatList.Rows.First(r => r.Name == "Work");
                    await vm.ActivateRowAsync(work);
                    await SettleAsync(300);
                    var allIn = vm.ChatList.Rows.FirstOrDefault(r => r.IsAllInRow);
                    if (allIn is not null)
                    {
                        await vm.ActivateRowAsync(allIn);
                    }

                    break;
                }

            case "settings":
                _ = vm.OpenSettingsAsync(null);
                break;
            case "dialog":
                _ = vm.Dialogs.ChooseAsync(
                    L.Format("Category_DeleteTitle", "Travel 2026"),
                    $"{L.Format("Category_DeleteHasMany", 5)} {L.Get("Category_DeleteAskMessage")}",
                    [L.Get("Category_DeleteMoveOption"), L.Get("Category_DeleteRecycleOption")]);
                break;
            case "confirm":
                _ = vm.Dialogs.ConfirmAsync(
                    L.Get("Category_EmptyDeletedTitle"),
                    L.Get("Category_EmptyDeletedMessage"),
                    L.Get("Category_EmptyDeletedConfirm"),
                    isDestructive: true);
                break;
            case "prompt":
                _ = vm.Dialogs.PromptTextAsync(L.Get("Category_RenameTitle"), L.Get("Category_NameLabel"), "Travel 2026", NameRules.ValidateCategoryName);
                break;
            case "picker":
                {
                    var notes = vm.ChatList.Rows.FirstOrDefault(r => r.Name == "Notes")?.CategoryId;
                    _ = vm.Dialogs.PickCategoryAsync(L.Get("Intake_PasteTitleText"), notes);
                    break;
                }

            case "newcategory":
                _ = vm.Dialogs.ShowNewCategoryAsync(null);
                break;
            case "menu":
                {
                    var row = vm.ChatList.Rows.First(r => r.Name == "Work");
                    vm.ChatList.SelectedRow = null;
                    menu = window.ChatListPane.BuildMenuFor(row);
                    if (window.ChatListPane.GetRowContainer(row) is { } container)
                    {
                        menuAt = container.TranslatePoint(new Point(180, 40), window.ContentRoot);
                    }

                    break;
                }

            case "snackbar":
                {
                    var task = vm.Notifications.BeginTask(L.Format("Intake_Adding", 24));
                    task.IsIndeterminate = false;
                    task.Progress = 0.42;
                    task.Detail = $"{L.Format("Intake_Progress", 10, 24)} · img24.jpg";
                    vm.Notifications.Show(L.Format("Intake_Duplicate", "img0.jpg", "Images"), NotificationKind.Info, L.Get("Intake_Show"), () => { });
                    break;
                }

            case "drop":
                {
                    var row = vm.ChatList.Rows.First(r => r.Name == "Documents");
                    row.IsDropTarget = true;
                    vm.IsEmptyStateDropTarget = true;
                    break;
                }

            case "scroll":
                {
                    // Scroll the chat list and reveal its overlay scroll bar (normally shown on hover), plus a tooltip.
                    var list = window.ChatListPane.CategoryList;
                    if (list.Template.FindName("ScrollViewer", list) is ScrollViewer viewer)
                    {
                        viewer.ScrollToVerticalOffset(260);
                        await SettleAsync(200);
                        if (viewer.Template.FindName("PART_VerticalScrollBar", viewer) is System.Windows.Controls.Primitives.ScrollBar bar)
                        {
                            bar.Opacity = 1;
                        }
                    }

                    menu = new ToolTip { Content = vm.SortTooltip };
                    menuAt = new Point(200, 92);
                    break;
                }
        }

        await SettleAsync(1100);

        // "settings-fontlist": Settings › Appearance with the Font list drawn open below its box (its popup's content).
        if (state == "settings-fontlist" && FindDescendant<ComboBox>(window.ContentRoot) is { } fonts &&
            fonts.Template.FindName("PART_Popup", fonts) is System.Windows.Controls.Primitives.Popup { Child: FrameworkElement fontList })
        {
            menu = fontList;
            menuAt = fonts.TranslatePoint(new Point(0, fonts.ActualHeight), window.ContentRoot);
        }

        if (Environment.GetEnvironmentVariable("CABINETTE_DUMP_TREE") is { Length: > 0 } typeName)
        {
            DumpTree(window.ContentRoot, typeName);
        }

        var bitmap = Render(window.ContentRoot);
        if (menu is not null)
        {
            bitmap = Compose(bitmap, menu, menuAt);
        }

        Save(bitmap, path);
        WriteTextAudit(window.ContentRoot, menu);
    }

    /// <summary>Opens one of the vault operation dialogs with sample values. Returns false for other states.</summary>
    private static async Task<bool> ShowVaultDialogAsync(MainViewModel vm, string state)
    {
        var dialogs = vm.Dialogs;
        var vault = AppPaths.DefaultVaultSuggestion;
        const string newVault = @"D:\Cabinette Vault";
        const string zip = @"C:\Users\you\Documents\Cabinette backup 2026-10-09.zip";
        DialogViewModelBase? dialog = null;
        switch (state)
        {
            case "progress":
                {
                    var progress = new ProgressDialogViewModel(L.Get("Vault_MoveProgressTitle"));
                    progress.Report(new VaultOperationProgress("copying", 312, 1204, 1_288_490_189, 4_831_838_208, Path.Combine(vault, "Images", "IMG_2041.jpg")));
                    dialog = progress;
                    break;
                }

            case "progress-backup":
                {
                    var progress = new ProgressDialogViewModel(L.Get("Backup_ProgressTitle"));
                    progress.BeginStep(2, 2, L.Get("Backup_StepZip"));
                    progress.Report(new VaultOperationProgress("zipping", 905, 1204, 3_100_000_000, 4_831_838_208, Path.Combine(vault, "Video", "Holiday 2026.mp4")));
                    dialog = progress;
                    break;
                }

            case "move-confirm":
                {
                    var (confirm, contents) = VaultOperationsService.BuildMoveConfirmDialog(vault, newVault);
                    contents.Value = VaultOperationsService.FilesAndSize(1204, 4_831_838_208);
                    dialog = confirm;
                    break;
                }

            case "move-result":
                dialog = VaultOperationsService.BuildMoveResultDialog(new MoveVaultResult(vault, newVault, 1204, 4_831_838_208));
                break;
            case "migrate-result":
                {
                    var missing = new LibraryItem { Id = 1, FileName = "Old backup.zip", FullPath = @"E:\Backups\Old backup.zip", IsMissing = true };
                    dialog = VaultOperationsService.BuildMigrateResultDialog(new MigrateResult(
                        41,
                        [missing],
                        [new AddFailure(@"C:\Users\you\Desktop\locked.xlsx", "The file is open in another program.")]));
                    break;
                }

            case "backup-options":
                dialog = VaultOperationsService.BuildBackupOptionsDialog(42);
                break;
            case "backup-result":
                dialog = VaultOperationsService.BuildBackupResultDialog(new BackupResult(zip, 1245, 5_012_000_000, 3), new MigrateResult(39, [], []));
                break;
            case "restore-details":
                dialog = VaultOperationsService.BuildRestoreDetailsDialog(zip, SampleBackupInfo());
                break;
            case "restore-confirm":
                dialog = VaultOperationsService.BuildRestoreConfirmDialog(zip, newVault, replacesLibrary: true);
                break;
        }

        if (dialog is null)
        {
            return false;
        }

        _ = dialogs.ShowAsync(dialog);
        await Task.CompletedTask;
        return true;
    }

    /// <summary>
    /// Opens a few threads first so the empty box has recent categories (the last one stays open, so it is left out),
    /// then the quick switcher. "switcher:&lt;query&gt;" types a query; "switcher-go:&lt;query&gt;" also presses Enter.
    /// </summary>
    private static async Task ShowQuickSwitcherAsync(MainViewModel vm, string state)
    {
        foreach (var name in new[] { "Travel 2026", "Work", "Images", "Documents", "Notes" })
        {
            if (vm.ChatList.Rows.FirstOrDefault(r => r.Name == name) is { } row)
            {
                await vm.OpenThreadAsync(row);
            }
        }

        await SettleAsync(300);
        _ = vm.OpenQuickSwitcherCommand.ExecuteAsync(null);
        await SettleAsync(400);
        if (vm.Dialogs.Top is not QuickSwitcherViewModel switcher)
        {
            AppLog.Warn("Screenshot: the quick switcher did not open.");
            return;
        }

        var colon = state.IndexOf(':');
        if (colon < 0)
        {
            return;
        }

        switcher.Query = state[(colon + 1)..];
        await SettleAsync(500);
        if (state.StartsWith("switcher-go:", StringComparison.Ordinal))
        {
            // The capture follows about a second later, near the peak of the highlight flash.
            switcher.Accept();
        }
    }

    /// <summary>
    /// Settings › AI with the sample activity log: "settings-ai" (the top, with the path of an installed copy),
    /// "settings-ai-activity" (scrolled down to the log), "settings-ai-empty" (filtered to a tool the AI never used),
    /// "settings-ai-dev" (this run's real path, with the hint when cabinette-mcp.exe isn't next to it), and "ai-notice"
    /// (the in-app notice for Claude Code's changes).
    /// </summary>
    private static async Task ShowAiAsync(MainWindow window, MainViewModel vm, IServiceProvider services, string state)
    {
        if (state == "ai-notice")
        {
            // Not the app's notifier: this one can only add the in-app notice (inside the off-screen window). It has no
            // tray, never activates a window, and its "Show" does nothing.
            using var notifier = new AiChangeNotifier(
                new InAppOnlyNoticeSurface(vm.Notifications),
                notificationsOn: () => true,
                isQuitting: () => false,
                open: (_, _) => Task.CompletedTask,
                Dispatcher.CurrentDispatcher,
                TimeProvider.System);
            notifier.Report(new AiChange("Claude Code", AiChangeKind.FilesAdded, 3) { CategoryId = vm.ChatList.Rows.FirstOrDefault(r => r.Name == "Images")?.CategoryId, CategoryName = "Images" });
            notifier.Report(new AiChange("Claude Code", AiChangeKind.FilesTagged, 3));
            notifier.FlushAll();
            return;
        }

        _ = vm.OpenSettingsAsync("ai");
        await SettleAsync(400);
        if (vm.Dialogs.Top is not SettingsViewModel { Ai: { } ai })
        {
            AppLog.Warn("Screenshot: Settings › AI did not open.");
            return;
        }

        if (state != "settings-ai-dev")
        {
            ai.UsePreviewPath(@"C:\Program Files\Cabinette\" + AiConnectCommands.ExeName);
        }

        await ai.LoadAsync();
        if (state == "settings-ai-empty")
        {
            ai.Activity.ToolFilter.Select(Cabinette.Ipc.AiTools.ListTags);
            await SettleAsync(300);
        }

        if (state is "settings-ai-activity" or "settings-ai-empty")
        {
            await SettleAsync(300);
            if (Views.Motion.VisualTreeSearch.FindByName(window.ContentRoot, "AiSection") is { } section &&
                FindAncestor<ScrollViewer>(section) is { } viewer)
            {
                viewer.ScrollToEnd();
            }
        }
    }

    /// <summary>Screenshots of the AI notice: the in-app notice only; a toast is impossible.</summary>
    private sealed class InAppOnlyNoticeSurface(INotificationService inApp) : IAiNoticeSurface
    {
        public bool IsWindowSeen => true;

        public bool IsWindowVisible => true;

        public bool IsTrayAvailable => false;

        public void ShowToast(string title, string body, Action onClick)
        {
        }

        public void ShowInApp(string message, NotificationKind kind, string? actionText, Action? action) => inApp.Show(message, kind, actionText, action);
    }

    private static T? FindDescendant<T>(DependencyObject node)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            var child = VisualTreeHelper.GetChild(node, i);
            if ((child as T ?? FindDescendant<T>(child)) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject node)
        where T : DependencyObject
    {
        for (var current = VisualTreeHelper.GetParent(node); current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static BackupInfo SampleBackupInfo() => new(DateTimeOffset.Now.AddDays(-3).Date.AddHours(21).AddMinutes(14), "HOME-PC", 1245, 5_012_000_000, 34, 3, 1);

    /// <summary>
    /// First-run states: "firstrun" (plain), "firstrun-restore" (the restore details dialog on top) and
    /// "firstrun-notempty" (the warning for a vault folder that already holds files).
    /// </summary>
    public static async Task PrepareFirstRunAsync(FirstRunViewModel vm, string state)
    {
        if (state == "firstrun-restore")
        {
            _ = vm.Dialogs.ShowAsync(VaultOperationsService.BuildRestoreDetailsDialog(@"C:\Users\you\Documents\Cabinette backup 2026-10-09.zip", SampleBackupInfo()));
        }
        else if (state == "firstrun-notempty")
        {
            _ = vm.ConfirmFolderWithContentAsync();
        }

        await Task.CompletedTask;
    }

    public static async Task CaptureAsync(Window window, string path)
    {
        await SettleAsync(900);
        var root = window.Content as FrameworkElement ?? throw new InvalidOperationException("Window has no content.");
        Save(Render(root), path);
        WriteTextAudit(root, null);
    }

    /// <summary>
    /// Test aid: with CABINETTE_TEXT_AUDIT=&lt;file&gt;, writes every piece of text in the capture (and its menu or tooltip),
    /// one per line: element, weight, font, text. Collapsed and hidden parts and icons (ui:SymbolIcon, a glyph font of its
    /// own) are left out.
    /// </summary>
    private static void WriteTextAudit(DependencyObject root, DependencyObject? menu)
    {
        if (Environment.GetEnvironmentVariable("CABINETTE_TEXT_AUDIT") is not { Length: > 0 } file)
        {
            return;
        }

        var builder = new System.Text.StringBuilder();

        void Line(string kind, FontWeight weight, FontFamily family, string text)
        {
            text = text.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (text.Length > 0)
            {
                builder.Append(kind).Append('\t').Append(weight).Append('\t').Append(family.Source).Append('\t').AppendLine(text);
            }
        }

        void Inlines(IEnumerable<System.Windows.Documents.Inline> inlines)
        {
            foreach (var inline in inlines)
            {
                switch (inline)
                {
                    case System.Windows.Documents.Run run:
                        Line("Run", run.FontWeight, run.FontFamily, run.Text);
                        break;
                    case System.Windows.Documents.Span span:
                        Inlines(span.Inlines);
                        break;
                }
            }
        }

        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case Wpf.Ui.Controls.IconElement:
                case UIElement { Visibility: not Visibility.Visible }:
                    return;
                case TextBlock text when text.Inlines.Count > 0:
                    Inlines(text.Inlines);
                    break;
                case TextBlock text:
                    Line("TextBlock", text.FontWeight, text.FontFamily, text.Text);
                    break;
                case TextBox box:
                    Line("TextBox", box.FontWeight, box.FontFamily, box.Text);
                    break;
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i));
            }
        }

        Walk(root);
        if (menu is not null)
        {
            Walk(menu);
        }

        File.WriteAllText(file, builder.ToString());
    }

    private static async Task OpenByNameAsync(MainViewModel vm, string name)
    {
        // State names are lower-cased by the command-line parser.
        var row = vm.ChatList.Rows.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));
        if (row is not null)
        {
            await vm.ActivateRowAsync(row);
        }
    }

    private static async Task SettleAsync(int milliseconds)
    {
        await Task.Delay(milliseconds);
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    /// <summary>Debug aid: logs the layout sizes below every element of the given type name.</summary>
    private static void DumpTree(DependencyObject root, string typeName)
    {
        var builder = new System.Text.StringBuilder();

        void Walk(DependencyObject node, int depth, bool inside)
        {
            var match = inside || node.GetType().Name == typeName;
            if (match && node is FrameworkElement fe)
            {
                builder.Append(' ', depth * 2).Append(fe.GetType().Name).Append(" name=").Append(fe.Name)
                    .Append(" size=").Append(fe.ActualWidth.ToString("0.#")).Append('x').Append(fe.ActualHeight.ToString("0.#"))
                    .Append(" margin=").Append(fe.Margin).AppendLine();
            }

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                Walk(VisualTreeHelper.GetChild(node, i), match ? depth + 1 : depth, match);
            }
        }

        Walk(root, 0, false);
        AppLog.Info("Tree dump:" + Environment.NewLine + builder);
    }

    private static BitmapSource Render(FrameworkElement root)
    {
        root.UpdateLayout();
        var width = (int)Math.Ceiling(root.ActualWidth);
        var height = (int)Math.Ceiling(root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Context menus and tooltips live in their own popup windows, so render them on their own and draw them on top.</summary>
    private static BitmapSource Compose(BitmapSource window, FrameworkElement menu, Point at)
    {
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        var menuBitmap = Render(menu);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(window, new Rect(0, 0, window.PixelWidth, window.PixelHeight));
            dc.DrawImage(menuBitmap, new Rect(at.X, at.Y, menuBitmap.PixelWidth, menuBitmap.PixelHeight));
        }

        var composed = new RenderTargetBitmap(window.PixelWidth, window.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        composed.Render(visual);
        composed.Freeze();
        return composed;
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(full);
        encoder.Save(stream);
    }
}
