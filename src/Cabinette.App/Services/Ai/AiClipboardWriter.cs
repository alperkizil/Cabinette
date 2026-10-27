using System.Runtime.InteropServices;
using System.Windows;

namespace Cabinette.App.Services.Ai;

/// <summary>Puts text on the clipboard (Settings › AI's copy buttons). Injectable so tests never touch the real clipboard.</summary>
public interface IClipboardWriter
{
    /// <summary>False when the clipboard stayed busy (another program held it open). UI thread.</summary>
    bool TrySetText(string text);
}

/// <summary>The Windows clipboard, retried briefly while another program holds it open.</summary>
public sealed class WpfClipboardWriter : IClipboardWriter
{
    public bool TrySetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // SetDataObject with copy=true keeps the text after Cabinette exits; it retries internally too.
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(30);
            }
            catch (COMException ex)
            {
                AppLog.Warn("Copying to the clipboard failed: it stayed busy.", ex);
            }
            catch (ExternalException ex)
            {
                AppLog.Warn("Copying to the clipboard failed.", ex);
                return false;
            }
        }

        return false;
    }
}
