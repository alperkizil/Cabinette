namespace Cabinette.App.ViewModels.Thread;

/// <summary>Where Ctrl+V inside the thread pane goes.</summary>
internal enum ThreadPasteTarget
{
    /// <summary>Let the focused text box paste normally.</summary>
    TextBox,

    /// <summary>Add the clipboard content to the thread as files.</summary>
    Thread,

    /// <summary>Swallow the key (the thread takes no files, e.g. "Deleted").</summary>
    Nothing,
}

internal static class ThreadPasteRouting
{
    /// <summary>
    /// A focused text box with Unicode text on the clipboard always pastes the text, even when there is also a bitmap
    /// (Excel cells, Word) or files. Otherwise files or an image go to the thread, and outside text boxes so does text.
    /// </summary>
    public static ThreadPasteTarget Route(bool inTextBox, bool hasUnicodeText, bool hasFilesOrImage, bool threadAcceptsFiles)
    {
        if (inTextBox && (hasUnicodeText || !hasFilesOrImage || !threadAcceptsFiles))
        {
            return ThreadPasteTarget.TextBox;
        }

        return threadAcceptsFiles ? ThreadPasteTarget.Thread : ThreadPasteTarget.Nothing;
    }
}
