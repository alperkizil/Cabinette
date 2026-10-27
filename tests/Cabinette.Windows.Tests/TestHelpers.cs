using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Cabinette.Windows.Tests;

internal static class Sta
{
    /// <summary>Runs <paramref name="body"/> on a fresh STA thread and rethrows its exception (assertions included).</summary>
    public static void Run(Action body) => Run(() =>
    {
        body();
        return 0;
    });

    public static T Run<T>(Func<T> body)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(2)))
        {
            throw new TimeoutException("STA test body did not finish.");
        }

        failure?.Throw();
        return result;
    }

    /// <summary>
    /// Dispatches this STA thread's window messages, the way a real menu's modal loop does, until <paramref name="done"/>
    /// returns true or <paramref name="timeout"/> passes. Returns whether <paramref name="done"/> became true.
    /// </summary>
    public static bool PumpUntil(Func<bool> done, TimeSpan timeout)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var elapsed = Stopwatch.StartNew();
        while (!done())
        {
            if (elapsed.Elapsed >= timeout)
            {
                return false;
            }

            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Send, (_, _) => frame.Continue = false, dispatcher);
            timer.Start();
            Dispatcher.PushFrame(frame);
            timer.Stop();
        }

        return true;
    }
}

/// <summary>A temp folder that is deleted afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string? name = null)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Cabinette.Windows.Tests", name ?? Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name, string content = "Cabinette test file")
    {
        var path = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}

internal static class Pixels
{
    /// <summary>Straight (non-premultiplied) BGRA bytes of a bitmap.</summary>
    public static byte[] Bgra(BitmapSource source, out int width, out int height)
    {
        var converted = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        width = converted.PixelWidth;
        height = converted.PixelHeight;
        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);
        return pixels;
    }

    public static BitmapSource DecodePng(byte[] png)
    {
        using var stream = new MemoryStream(png);
        var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    public static (byte B, byte G, byte R, byte A) At(byte[] bgra, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2], bgra[i + 3]);
    }
}
