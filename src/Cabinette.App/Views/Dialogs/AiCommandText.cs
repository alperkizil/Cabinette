using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace Cabinette.App.Views.Dialogs;

/// <summary>
/// Shows a command in a read-only TextBox (Settings › AI) so that it wraps only at the spaces between its arguments, never
/// inside a quoted path (not "C: at the end of one line and \Program Files\… on the next). The displayed text has an
/// invisible word joiner (U+2060) between all other characters. A quoted path wider than the whole box may also break
/// after its backslashes (an invisible zero-width space, U+200B, after each). Copying a selection, or dragging it, takes
/// both out again, so what leaves the box is the command itself.
/// </summary>
public static class AiCommandText
{
    public const char WordJoiner = '⁠';
    public const char BreakOpportunity = '​';

    /// <summary>What a TextBox's own text view keeps free beside the text (its caret margin), in device-independent pixels.</summary>
    private const double TextMargin = 6;

    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(string), typeof(AiCommandText), new PropertyMetadata(null, OnCommandChanged));

    /// <summary>
    /// The box's own text weight, bound to it: Bold text changes the weight live without changing the box's width, and a
    /// heavier path may no longer fit on one line, so the command is measured again.
    /// </summary>
    private static readonly DependencyProperty MeasuredWeightProperty = DependencyProperty.RegisterAttached(
        "MeasuredWeight", typeof(FontWeight), typeof(AiCommandText), new PropertyMetadata(default(FontWeight), OnMeasuredWeightChanged));

    public static string? GetCommand(DependencyObject element) => (string?)element.GetValue(CommandProperty);

    public static void SetCommand(DependencyObject element, string? value) => element.SetValue(CommandProperty, value);

    /// <summary>
    /// The command with a word joiner at every place a line may not break: between two characters that aren't spaces, and
    /// everywhere inside double quotes (an escaped <c>`"</c> doesn't end them). It may break after a space outside quotes.
    /// A quoted part (with its quotes) for which <paramref name="tooWide"/> says true may also break after the backslash
    /// that ends each folder name (not after "C:\" or the "\\" of a share, so a line never ends with just the drive).
    /// </summary>
    public static string ForDisplay(string command, Func<string, bool>? tooWide = null)
    {
        var elements = new List<string>();
        var enumerator = StringInfo.GetTextElementEnumerator(command);
        while (enumerator.MoveNext())
        {
            elements.Add(enumerator.GetTextElement());
        }

        var result = new StringBuilder(command.Length * 2);
        var quoted = false;
        var escaped = false;
        var breakInside = false;
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            if (i > 0)
            {
                var previous = elements[i - 1];

                // After a backslash that ends a name: not after the drive ("C:\") or the "\\" of a share.
                if (quoted && breakInside && previous == "\\" && i >= 2 && elements[i - 2] is not (":" or "\\" or "\"") && element != "\"")
                {
                    result.Append(BreakOpportunity);
                }
                else if (quoted || (previous != " " && element != " "))
                {
                    result.Append(WordJoiner);
                }
            }

            result.Append(element);
            if (element == "\"" && !escaped)
            {
                quoted = !quoted;
                if (quoted)
                {
                    breakInside = tooWide is not null && tooWide(QuotedPart(elements, i));
                }
            }

            escaped = element == "`" && !escaped;
        }

        return result.ToString();
    }

    /// <summary>Displayed text back to the command (the invisible characters taken out).</summary>
    public static string FromDisplay(string text)
        => text.Replace(WordJoiner.ToString(), "", StringComparison.Ordinal).Replace(BreakOpportunity.ToString(), "", StringComparison.Ordinal);

    /// <summary>The quoted part that opens at <paramref name="open"/>, quotes included (to the end when it isn't closed).</summary>
    private static string QuotedPart(List<string> elements, int open)
    {
        var part = new StringBuilder(elements[open]);
        var escaped = false;
        for (var i = open + 1; i < elements.Count; i++)
        {
            part.Append(elements[i]);
            if (elements[i] == "\"" && !escaped)
            {
                break;
            }

            escaped = elements[i] == "`" && !escaped;
        }

        return part.ToString();
    }

    private static void OnCommandChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBox box)
        {
            return;
        }

        DataObject.RemoveCopyingHandler(box, OnCopying);
        DataObject.AddCopyingHandler(box, OnCopying);
        box.SizeChanged -= OnSizeChanged;
        box.SizeChanged += OnSizeChanged;
        if (BindingOperations.GetBindingExpression(box, MeasuredWeightProperty) is null)
        {
            BindingOperations.SetBinding(box, MeasuredWeightProperty, new Binding { Path = new PropertyPath(TextElement.FontWeightProperty), RelativeSource = RelativeSource.Self });
        }

        Refresh(box);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged && sender is TextBox box)
        {
            Refresh(box);
        }
    }

    private static void OnMeasuredWeightChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is TextBox box && GetCommand(box) is not null)
        {
            Refresh(box);
        }
    }

    /// <summary>Shows the command for the box's current width (a quoted part wider than the box may break after its backslashes).</summary>
    private static void Refresh(TextBox box)
    {
        var command = GetCommand(box) ?? "";
        var width = box.ActualWidth - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right - TextMargin;
        var text = ForDisplay(command, part => width > 0 && Measure(box, part) > width);
        if (!string.Equals(box.Text, text, StringComparison.Ordinal))
        {
            box.Text = text;
        }
    }

    private static double Measure(TextBox box, string text)
    {
        var typeface = new Typeface(box.FontFamily, box.FontStyle, box.FontWeight, box.FontStretch);
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, box.FlowDirection, typeface, box.FontSize, box.Foreground, VisualTreeHelper.GetDpi(box).PixelsPerDip);
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>A selection being copied or dragged: the command's own text, without the invisible characters.</summary>
    private static void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        foreach (var format in new[] { DataFormats.UnicodeText, DataFormats.Text })
        {
            if (e.DataObject.GetDataPresent(format, false) && e.DataObject.GetData(format, false) is string text)
            {
                e.DataObject.SetData(format, FromDisplay(text), false);
            }
        }
    }
}
