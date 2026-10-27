using static Cabinette.Windows.Interop.NativeMethods;

namespace Cabinette.Windows.Interop;

/// <summary>Reads the file list of a CF_HDROP handle (a DROPFILES block), as the clipboard and drag and drop hand it over.</summary>
internal static class HDrop
{
    /// <summary>Upper bound for the initial list capacity (a hostile count must not allocate a huge list up front).</summary>
    private const int MaxInitialCapacity = 10_000;

    /// <summary>The paths in the handle, in order. Empty entries are skipped. The caller keeps ownership of the handle.</summary>
    public static unsafe List<string> ReadPaths(IntPtr hdrop)
    {
        if (hdrop == IntPtr.Zero)
        {
            return [];
        }

        var count = DragQueryFile(hdrop, uint.MaxValue, null, 0);
        var files = new List<string>((int)Math.Min(count, MaxInitialCapacity));
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFile(hdrop, i, null, 0);
            if (length == 0)
            {
                continue;
            }

            var buffer = new char[length + 1];
            fixed (char* p = buffer)
            {
                var copied = DragQueryFile(hdrop, i, p, (uint)buffer.Length);
                if (copied > 0)
                {
                    files.Add(new string(p, 0, (int)copied));
                }
            }
        }

        return files;
    }
}
