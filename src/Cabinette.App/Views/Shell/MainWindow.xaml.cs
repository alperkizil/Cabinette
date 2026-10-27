using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;
using Wpf.Ui.Controls;

namespace Cabinette.App.Views.Shell;

/// <summary>What Ctrl+K does at the moment (see <see cref="MainWindow.DecideQuickSwitcherKey"/>).</summary>
internal enum QuickSwitcherKeyAction
{
    /// <summary>Not Ctrl+K, or another dialog is open: the key goes on as usual.</summary>
    None,

    Open,

    /// <summary>Ctrl+K again while the switcher is open.</summary>
    Close,

    /// <summary>Auto-repeat while the keys are held: eaten, nothing happens.</summary>
    Swallow,
}

/// <summary>What closing the main window does.</summary>
public enum WindowCloseAction
{
    /// <summary>Close the window (the app exits).</summary>
    Close,

    /// <summary>Hide it; Cabinette keeps running in the tray.</summary>
    HideToTray,

    /// <summary>Stay open (an operation is running).</summary>
    Block,
}

/// <summary>
/// The main window: chat list on the left, the open thread (or the empty state) on the right, dialogs on top.
/// Handles global shortcuts (Ctrl+K, Ctrl+F, Ctrl+N, Ctrl+V, Esc), unhandled drops and window placement.
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly MainViewModel _vm;
    private readonly WindowPlacementStore? _placementStore;
    private readonly DispatcherTimer _rootDropHide;
    private readonly DispatcherTimer _rootDropShow;
    private readonly DropHighlightWatchdog _rootDropWatchdog;
    private DragEventArgs? _rootDrag;

    public MainWindow(MainViewModel vm, WindowPlacementStore? placementStore)
    {
        _vm = vm;
        _placementStore = placementStore;
        InitializeComponent();
        _rootDropHide = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(90) };
        _rootDropHide.Tick += (_, _) => HideRootDropOverlay();
        _rootDropShow = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(120) };
        _rootDropShow.Tick += (_, _) =>
        {
            _rootDropShow.Stop();
            DropZone.SetIsActive(RootDropOverlay, true);
        };
        _rootDropWatchdog = new DropHighlightWatchdog(HideRootDropOverlay);

        Root.AddHandler(DragDrop.DragOverEvent, new DragEventHandler((_, e) =>
        {
            if (!e.Handled || ReferenceEquals(e, _rootDrag))
            {
                return;
            }

            // An editable text box (the chat-list search) refuses file drags, but it sits in the header the overlay
            // covers: treat it like the rest of the header. (It leaves the drop itself unhandled, so OnRootDrop adds
            // the files.) Any other pane with its own highlight (thread, chat list, empty state) took the drag: hand
            // over at once.
            if (IsTextBoxOutsideThread(e.OriginalSource))
            {
                TakeRootDragOver(e);
            }
            else
            {
                HideRootDropOverlay();
            }
        }), handledEventsToo: true);
        DataContext = vm;
        PreviewKeyDown += OnPreviewKeyDown;
        KeyDown += OnKeyDown;
        Closing += OnClosing;
        RestorePlacement();
    }

    public ChatListPane ChatListPane => LeftPane;

    public Grid ContentRoot => Root;

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// Ctrl+K (quick switcher) works from anywhere in the window, also while typing a note or a search, so it is taken on
    /// the way down, before any text box sees it. Not while a dialog is open; Ctrl+K again closes the switcher.
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        switch (DecideQuickSwitcherKey(key, Keyboard.Modifiers, e.IsRepeat, _vm.Dialogs.Top))
        {
            case QuickSwitcherKeyAction.Open:
                _vm.OpenQuickSwitcherCommand.Execute(null);
                e.Handled = true;
                break;
            case QuickSwitcherKeyAction.Close:
                _vm.Dialogs.Top?.Dismiss();
                e.Handled = true;
                break;
            case QuickSwitcherKeyAction.Swallow:
                e.Handled = true;
                break;
        }
    }

    /// <summary>What a key press means for the quick switcher, given the dialog on top (null: none).</summary>
    internal static QuickSwitcherKeyAction DecideQuickSwitcherKey(Key key, ModifierKeys modifiers, bool isRepeat, DialogViewModelBase? topDialog)
    {
        if (key != Key.K || modifiers != ModifierKeys.Control)
        {
            return QuickSwitcherKeyAction.None;
        }

        if (isRepeat)
        {
            // Holding the keys down must not open and close it over and over.
            return QuickSwitcherKeyAction.Swallow;
        }

        return topDialog switch
        {
            null => QuickSwitcherKeyAction.Open,
            QuickSwitcherViewModel => QuickSwitcherKeyAction.Close,
            _ => QuickSwitcherKeyAction.None, // another dialog is open: Ctrl+K does nothing
        };
    }

    /// <summary>
    /// Global shortcuts, handled while bubbling so the thread view can claim keys first
    /// (e.g. its own Ctrl+F, Esc or Ctrl+V) by marking them handled.
    /// </summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Handled || _vm.Dialogs.IsAnyOpen)
        {
            return;
        }

        if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = _vm.HandleEscape();
            return;
        }

        if (Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        if (e.Key == Key.F)
        {
            _vm.IsFilterOpen = false;
            LeftPane.FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            _vm.NewCategoryCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            // Text boxes handle Ctrl+Home themselves (caret to start), so this only runs elsewhere.
            _vm.GoHomeCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.V)
        {
            var focused = Keyboard.FocusedElement as DependencyObject;
            if (focused is TextBoxBase or System.Windows.Controls.PasswordBox || IsInside(focused, ThreadHost))
            {
                return;
            }

            _vm.PasteCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ drag & drop (anywhere else: auto-categorize)

    private void OnRootDragOver(object sender, DragEventArgs e)
    {
        if (e.Handled || IsInside(e.OriginalSource as DependencyObject, ThreadHost))
        {
            return;
        }

        TakeRootDragOver(e);
    }

    private void TakeRootDragOver(DragEventArgs e)
    {
        var accepts = IsOutsideFileDrop(e.Data) && !_vm.Dialogs.IsAnyOpen;
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _rootDrag = e;

        // Feedback for the parts the panes don't claim (title bar, chat-list header): "Drop to sort automatically".
        // It shows only after a short hover, so crossing the pane splitter on the way into the thread doesn't flash it.
        _rootDropHide.Stop();
        if (accepts)
        {
            if (!DropZone.GetIsActive(RootDropOverlay) && !_rootDropShow.IsEnabled)
            {
                _rootDropShow.Start();
            }

            _rootDropWatchdog.Touch();
        }
        else
        {
            HideRootDropOverlay();
        }
    }

    private void OnRootDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between elements, or into a pane with its own highlight (which handles
        // DragOver itself, so none reaches here): hide only if no DragOver follows shortly.
        _rootDropHide.Stop();
        _rootDropHide.Start();
    }

    private void OnRootDrop(object sender, DragEventArgs e)
    {
        // The thread view handles drops on itself; never add twice.
        if (e.Handled || IsInside(e.OriginalSource as DependencyObject, ThreadHost))
        {
            HideRootDropOverlay();
            return;
        }

        if (!_vm.Dialogs.IsAnyOpen && IsOutsideFileDrop(e.Data) && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            DropZone.Receive(RootDropOverlay); // the add starts right away; this only plays the "received" motion
            HideRootDropOverlay();
            _ = _vm.DropAsync(paths, null);
            return;
        }

        HideRootDropOverlay();
    }

    private void HideRootDropOverlay()
    {
        _rootDropHide.Stop();
        _rootDropShow.Stop();
        _rootDropWatchdog.Stop();
        DropZone.SetIsActive(RootDropOverlay, false);
    }

    /// <summary>Files from outside: a bubble dragged out of a thread carries its files too, but re-adding them makes no sense.</summary>
    internal static bool IsOutsideFileDrop(IDataObject data)
        => data.GetDataPresent(DataFormats.FileDrop) && !data.GetDataPresent(Cabinette.App.Views.Thread.ThreadView.InternalDragFormat);

    private bool IsTextBoxOutsideThread(object? source)
    {
        for (var current = source as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (current is TextBoxBase)
            {
                return !IsInside(current, ThreadHost);
            }
        }

        return false;
    }

    private static bool IsInside(DependencyObject? node, DependencyObject ancestor)
    {
        for (var current = node; current is not null; current = current is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ placement

    private void RestorePlacement()
    {
        var placement = _placementStore?.Load();
        if (placement is null)
        {
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = placement.Left;
        Top = placement.Top;
        Width = Math.Max(MinWidth, placement.Width);
        Height = Math.Max(MinHeight, placement.Height);
        if (placement.SidebarWidth >= SidebarColumn.MinWidth && placement.SidebarWidth <= SidebarColumn.MaxWidth)
        {
            SidebarColumn.Width = new GridLength(placement.SidebarWidth);
        }

        if (placement.IsMaximized)
        {
            Loaded += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// Decides what the close button does (set by the app): hide to the tray, close (exit), or stay open because an
    /// operation must not be interrupted. Null = close.
    /// </summary>
    public Func<WindowCloseAction>? ClosePolicy { get; set; }

    /// <summary>Raised after the window was hidden to the tray instead of closing.</summary>
    public event EventHandler? HiddenToTray;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        switch (ClosePolicy?.Invoke() ?? WindowCloseAction.Close)
        {
            case WindowCloseAction.Block:
                e.Cancel = true;
                return;
            case WindowCloseAction.HideToTray:
                // Keep running (dialogs and running operations stay as they are).
                e.Cancel = true;
                SavePlacement();
                Hide();
                HiddenToTray?.Invoke(this, EventArgs.Empty);
                return;
        }

        _vm.Dialogs.CancelAll();
        SavePlacement();
    }

    private void SavePlacement()
    {
        if (_placementStore is null || !IsVisible)
        {
            return;
        }

        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0)
        {
            return;
        }

        _placementStore.Save(new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
            SidebarWidth = SidebarColumn.ActualWidth,
        });
    }
}
