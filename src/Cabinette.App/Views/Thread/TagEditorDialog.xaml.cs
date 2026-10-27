using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// Themed tag chip editor. Enter or comma adds a tag, Backspace in the empty box removes the last chip,
/// Tab completes the first suggestion, Enter on an empty box saves, Esc cancels.
/// </summary>
public partial class TagEditorDialog : Window
{
    private readonly TagEditorViewModel _editor;

    public TagEditorDialog(TagEditorViewModel editor)
    {
        InitializeComponent();
        Services.UiScale.FitWindow(this);
        _editor = editor;
        DataContext = editor;
        Loaded += (_, _) => Keyboard.Focus(Input);
    }

    private void Input_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                if (string.IsNullOrWhiteSpace(_editor.Input))
                {
                    Save();
                }
                else
                {
                    _editor.CommitInput();
                }

                e.Handled = true;
                break;
            case Key.Back when Input.Text.Length == 0:
                _editor.RemoveLast();
                e.Handled = true;
                break;
            case Key.Tab when !string.IsNullOrWhiteSpace(_editor.Input) && _editor.Suggestions.Count > 0:
                _editor.AddSuggestionCommand.Execute(_editor.Suggestions[0]);
                e.Handled = true;
                break;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e) => Save();

    private void Save()
    {
        _editor.CommitInput();
        DialogResult = true;
    }

    private void ChipBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not TextBox)
        {
            Keyboard.Focus(Input);
            Input.CaretIndex = Input.Text.Length;
            e.Handled = true;
        }
    }

    private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
