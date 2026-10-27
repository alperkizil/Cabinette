using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Cabinette.App.Services.Ai;

namespace Cabinette.App.Views.Dialogs;

/// <summary>
/// Fills a TextBlock with an activity sentence (Settings › AI): plain text, and file and category names as links that
/// run <see cref="GetLinkCommand"/> with their <see cref="AiActivitySegment"/>. The text wraps like any sentence.
/// Every link is a tab stop named after its file or category; the activity list lets Tab walk them all. A virtualized
/// list only has the rows around the visible ones (and the rest of its cache comes later, in the background), so a
/// focused link scrolls into view and makes sure the rows before and after its own exist: the next Tab or Shift+Tab
/// always finds them, however fast the keys come.
/// </summary>
public static class AiActivityInlines
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.RegisterAttached(
        "Segments", typeof(IReadOnlyList<AiActivitySegment>), typeof(AiActivityInlines), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty LinkCommandProperty = DependencyProperty.RegisterAttached(
        "LinkCommand", typeof(ICommand), typeof(AiActivityInlines), new PropertyMetadata(null, OnChanged));

    public static IReadOnlyList<AiActivitySegment>? GetSegments(DependencyObject element) => (IReadOnlyList<AiActivitySegment>?)element.GetValue(SegmentsProperty);

    public static void SetSegments(DependencyObject element, IReadOnlyList<AiActivitySegment>? value) => element.SetValue(SegmentsProperty, value);

    public static ICommand? GetLinkCommand(DependencyObject element) => (ICommand?)element.GetValue(LinkCommandProperty);

    public static void SetLinkCommand(DependencyObject element, ICommand? value) => element.SetValue(LinkCommandProperty, value);

    private static void OnChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBlock text)
        {
            Fill(text, GetSegments(text), GetLinkCommand(text));
        }
    }

    private static void Fill(TextBlock text, IReadOnlyList<AiActivitySegment>? segments, ICommand? command)
    {
        text.Inlines.Clear();
        if (segments is null)
        {
            return;
        }

        foreach (var segment in segments)
        {
            if (!segment.IsLink)
            {
                text.Inlines.Add(new Run(segment.Text));
                continue;
            }

            var link = new Hyperlink(new Run(segment.Text))
            {
                Command = command,
                CommandParameter = segment,
                TextDecorations = null,
                FocusVisualStyle = null,
            };
            AutomationProperties.SetName(link, segment.Text);
            link.SetResourceReference(TextElement.ForegroundProperty, "Cab.Accent");
            link.SetResourceReference(TextElement.FontWeightProperty, Services.UiWeight.StrongKey);
            link.MouseEnter += (_, _) => link.TextDecorations = TextDecorations.Underline;
            link.MouseLeave += (_, _) => link.TextDecorations = null;
            link.GotKeyboardFocus += (_, _) =>
            {
                link.TextDecorations = TextDecorations.Underline;
                RealizeNeighborRows(text);
                link.BringIntoView();
            };
            link.LostKeyboardFocus += (_, _) => link.TextDecorations = null;
            text.Inlines.Add(link);
        }
    }

    /// <summary>In a virtualized list: creates the rows just before and after the one holding <paramref name="text"/>, if missing.</summary>
    private static void RealizeNeighborRows(DependencyObject text)
    {
        DependencyObject? node = text;
        while (node is not null && node is not ListBoxItem)
        {
            node = VisualTreeHelper.GetParent(node);
        }

        if (node is not ListBoxItem row ||
            ItemsControl.ItemsControlFromItemContainer(row) is not { } list ||
            VisualTreeHelper.GetParent(row) is not VirtualizingPanel panel)
        {
            return;
        }

        var index = list.ItemContainerGenerator.IndexFromContainer(row);
        if (index < 0)
        {
            return;
        }

        foreach (var neighbor in new[] { index + 1, index - 1 })
        {
            if (neighbor >= 0 && neighbor < list.Items.Count && list.ItemContainerGenerator.ContainerFromIndex(neighbor) is null)
            {
                panel.BringIndexIntoViewPublic(neighbor);
            }
        }
    }
}
