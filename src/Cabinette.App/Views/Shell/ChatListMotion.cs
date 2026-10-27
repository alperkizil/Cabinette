using System.Windows;
using System.Windows.Controls;
using Cabinette.App.ViewModels.Shell;
using Cabinette.App.Views.Motion;

namespace Cabinette.App.Views.Shell;

/// <summary>
/// Makes the chat list react to live updates. Files added by a drop, the Explorer entries, the vault monitor or the AI
/// all end in the same refresh, so the list behaves the same whatever added them:
/// <list type="bullet">
/// <item>a category that moves (sorted by last upload) glides to its new place while the rows it passes make room;</item>
/// <item>new rows fade in;</item>
/// <item>unread badges that appear or grow pop (with A–Z sort that is all that moves).</item>
/// </list>
/// FLIP: <see cref="Capture"/> records where the realized rows are right before a refresh merges into the list,
/// <see cref="Play"/> lays the list out at once and starts each moved row at its old place. Only realized rows that are
/// on screen animate, recycled containers are reset, and bursts are coalesced (see <see cref="ChatListMotionRules"/>).
/// Purely visual: the list already holds its new order before anything moves.
/// </summary>
internal sealed class ChatListMotion
{
    /// <summary>x:Name of the unread badge in the chat-list row template.</summary>
    public const string BadgeName = "UnreadBadge";

    private static readonly DependencyProperty GenerationProperty = DependencyProperty.RegisterAttached(
        "MotionGeneration", typeof(int), typeof(ChatListMotion), new PropertyMetadata(0));

    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "MotionHooked", typeof(bool), typeof(ChatListMotion), new PropertyMetadata(false));

    /// <summary>The row is gliding over its neighbours to a new place (drawn on top, with an opaque background).</summary>
    private static readonly DependencyProperty JumpingProperty = DependencyProperty.RegisterAttached(
        "MotionJumping", typeof(bool), typeof(ChatListMotion), new PropertyMetadata(false));

    private readonly ListBox _list;
    private readonly TimeProvider _time;
    private readonly MotionBurstGate _gate = MotionBurstGate.ForChatList();
    private Snapshot? _before;

    public ChatListMotion(ListBox list, TimeProvider? time = null)
    {
        _list = list;
        _time = time ?? TimeProvider.System;
        VirtualizingStackPanel.AddCleanUpVirtualizedItemHandler(list, (_, e) =>
        {
            if (e.UIElement is ListBoxItem item)
            {
                Reset(item);
            }
        });
    }

    /// <summary>Call right before a refresh changes the rows: remembers the order, the badges and where rows are drawn now.</summary>
    public void Capture(IReadOnlyList<CategoryRowViewModel> rows)
    {
        _before = null;
        if (!MotionSettings.IsEnabled || !_list.IsVisible || ScrollHost() is not { } scroll)
        {
            return;
        }

        // Drawn positions include a glide still in flight, so an interrupted glide continues smoothly.
        var tops = new Dictionary<CategoryRowViewModel, double>(ReferenceEqualityComparer.Instance);
        foreach (var (row, container) in Realized(scroll))
        {
            tops[row] = container.TranslatePoint(default, scroll).Y;
        }

        var unread = new Dictionary<CategoryRowViewModel, int>(ReferenceEqualityComparer.Instance);
        foreach (var row in rows)
        {
            unread[row] = row.UnreadCount;
        }

        _before = new Snapshot([.. rows], unread, tops);
    }

    /// <summary>Call right after the refresh changed the rows.</summary>
    public void Play(IReadOnlyList<CategoryRowViewModel> rows)
    {
        var before = _before;
        _before = null;
        if (before is null || !MotionSettings.IsEnabled || !_list.IsVisible || ScrollHost() is not { } scroll)
        {
            return;
        }

        _list.UpdateLayout();
        var viewport = scroll.ViewportHeight > 0 ? scroll.ViewportHeight : scroll.ActualHeight;
        var oldIndex = IndexOf(before.Order);
        var newIndex = IndexOf(rows);
        var jumpers = FlipPlanner.FindJumpers(before.Order, rows);

        var samples = new List<FlipRow<CategoryRowViewModel>>();
        var containers = new Dictionary<CategoryRowViewModel, ListBoxItem>(ReferenceEqualityComparer.Instance);
        var appearing = new List<ListBoxItem>();
        var pops = new List<ListBoxItem>();
        foreach (var (row, container) in Realized(scroll).ToList())
        {
            // A row still gliding to its new place stays a jumper, even when this refresh doesn't move it again
            // (a second refresh for the same category): otherwise it would continue see-through, under the others.
            if ((bool)container.GetValue(JumpingProperty))
            {
                jumpers.Add(row);
            }

            Settle(container);
            if (!newIndex.TryGetValue(row, out var index))
            {
                continue; // a container still holding a removed row
            }

            var top = container.TranslatePoint(default, scroll).Y;
            var height = container.ActualHeight;
            var visible = top < viewport && top + height > 0;
            var old = oldIndex.GetValueOrDefault(row, -1);
            samples.Add(new FlipRow<CategoryRowViewModel>(row, old, index, before.Tops.TryGetValue(row, out var oldTop) ? oldTop : null, top, height));
            containers[row] = container;
            if (old < 0 && visible)
            {
                appearing.Add(container);
            }

            if (visible && row.UnreadCount > before.Unread.GetValueOrDefault(row, 0))
            {
                pops.Add(container);
            }
        }

        var moves = FlipPlanner.Plan(samples, viewport, jumpers);
        var plan = ChatListMotionRules.Decide(true, moves.Count + appearing.Count, jumpers.Count, pops.Count, _gate, _time.GetUtcNow());
        if (plan.Glide)
        {
            foreach (var move in moves)
            {
                Glide(containers[move.Key], move.Offset, move.IsJumper);
            }

            foreach (var container in appearing)
            {
                Appear(container);
            }
        }

        if (plan.Pop)
        {
            foreach (var container in pops)
            {
                PopBadge(container);
            }
        }
    }

    private static void Glide(ListBoxItem container, double offset, bool jumper)
    {
        if (Animator.EnsureTranslate(container) is not { } shift)
        {
            return;
        }

        Hook(container);
        var generation = NextGeneration(container);
        if (jumper)
        {
            // The jumping row passes over the ones making room. Rows are see-through, so it gets the sidebar's
            // background meanwhile (a selected or hovered row already has an opaque one).
            Panel.SetZIndex(container, 1);
            container.SetValue(JumpingProperty, true);
            if (!container.IsSelected && !container.IsMouseOver && RowChrome(container) is { } chrome)
            {
                chrome.SetResourceReference(Border.BackgroundProperty, "Cab.SidebarBackground");
            }
        }

        var glide = Animator.To(0, jumper ? MotionTokens.Glide : MotionTokens.Shift, jumper ? MotionTokens.SoftSpring : MotionTokens.Out, offset);
        glide.Completed += (_, _) =>
        {
            if ((int)container.GetValue(GenerationProperty) == generation)
            {
                container.ClearValue(Panel.ZIndexProperty);
                container.ClearValue(JumpingProperty);
                RowChrome(container)?.ClearValue(Border.BackgroundProperty);
            }
        };
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, glide);
    }

    /// <summary>The row's rounded border from the Shell.RowItem template (its hover and selection triggers set its background).</summary>
    private static Border? RowChrome(ListBoxItem container) => container.Template?.FindName("Bd", container) as Border;

    private static void Appear(ListBoxItem container)
    {
        Hook(container);
        container.BeginAnimation(UIElement.OpacityProperty, Animator.To(1, MotionTokens.Appear, MotionTokens.Out, 0));
    }

    private static void PopBadge(ListBoxItem container)
    {
        if (VisualTreeSearch.FindByName(container, BadgeName) is not { IsVisible: true } badge || Animator.EnsureScale(badge) is not { } scale)
        {
            return;
        }

        Hook(container);
        Animator.Scale(scale, Animator.To(1, MotionTokens.Pop, MotionTokens.Bounce, 0.4));
    }

    /// <summary>Ends a glide or fade at once (the row is drawn at its laid-out place).</summary>
    private static void Settle(ListBoxItem container)
    {
        NextGeneration(container);
        if (container.RenderTransform is System.Windows.Media.TranslateTransform { IsFrozen: false } shift)
        {
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty, null);
            shift.Y = 0;
        }

        container.BeginAnimation(UIElement.OpacityProperty, null);
        container.ClearValue(Panel.ZIndexProperty);
        container.ClearValue(JumpingProperty);
        RowChrome(container)?.ClearValue(Border.BackgroundProperty); // only ever a value set by Glide
    }

    /// <summary>A container is recycled for another row (or virtualized): nothing of the old row's motion may stay.</summary>
    private static void Reset(ListBoxItem container)
    {
        if (!(bool)container.GetValue(HookedProperty))
        {
            return; // never animated
        }

        Settle(container);
        if (VisualTreeSearch.FindByName(container, BadgeName)?.RenderTransform is System.Windows.Media.ScaleTransform { IsFrozen: false } scale)
        {
            Animator.StopScale(scale);
        }
    }

    private static void Hook(ListBoxItem container)
    {
        if ((bool)container.GetValue(HookedProperty))
        {
            return;
        }

        container.SetValue(HookedProperty, true);
        container.DataContextChanged += (sender, _) => Reset((ListBoxItem)sender);
    }

    private static int NextGeneration(ListBoxItem container)
    {
        var generation = (int)container.GetValue(GenerationProperty) + 1;
        container.SetValue(GenerationProperty, generation);
        return generation;
    }

    private ScrollViewer? ScrollHost() => VisualTreeSearch.FindDescendant<ScrollViewer>(_list);

    private IEnumerable<(CategoryRowViewModel Row, ListBoxItem Container)> Realized(ScrollViewer scroll)
    {
        if (VisualTreeSearch.FindDescendant<VirtualizingPanel>(_list) is not { } panel)
        {
            yield break;
        }

        foreach (var child in panel.Children)
        {
            if (child is ListBoxItem { DataContext: CategoryRowViewModel row, IsVisible: true } container && container.IsDescendantOf(scroll))
            {
                yield return (row, container);
            }
        }
    }

    private static Dictionary<CategoryRowViewModel, int> IndexOf(IReadOnlyList<CategoryRowViewModel> rows)
    {
        var map = new Dictionary<CategoryRowViewModel, int>(rows.Count, ReferenceEqualityComparer.Instance);
        for (var i = 0; i < rows.Count; i++)
        {
            map.TryAdd(rows[i], i);
        }

        return map;
    }

    private sealed record Snapshot(
        IReadOnlyList<CategoryRowViewModel> Order,
        Dictionary<CategoryRowViewModel, int> Unread,
        Dictionary<CategoryRowViewModel, double> Tops);
}
