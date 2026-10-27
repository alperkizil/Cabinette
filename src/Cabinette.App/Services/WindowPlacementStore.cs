using System.IO;
using System.Text.Json;
using System.Windows;

namespace Cabinette.App.Services;

public sealed record WindowPlacement
{
    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public bool IsMaximized { get; init; }

    public double SidebarWidth { get; init; }
}

/// <summary>Remembers the main window's size, position and sidebar width in a small JSON file in the data folder.</summary>
public sealed class WindowPlacementStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _file;

    public WindowPlacementStore(AppPaths paths)
    {
        _file = paths.WindowStateFile;
    }

    public WindowPlacement? Load()
    {
        try
        {
            if (!File.Exists(_file))
            {
                return null;
            }

            var placement = JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_file));
            return placement is not null && IsUsable(placement) ? placement : null;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Window placement could not be read.", ex);
            return null;
        }
    }

    public void Save(WindowPlacement placement)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(placement, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn("Window placement could not be saved.", ex);
        }
    }

    /// <summary>Rejects sizes that are too small and positions that are no longer on any screen.</summary>
    private static bool IsUsable(WindowPlacement p)
    {
        if (p.Width < 400 || p.Height < 300 || double.IsNaN(p.Left) || double.IsNaN(p.Top))
        {
            return false;
        }

        var screen = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var titleArea = new Rect(p.Left + 40, p.Top, Math.Max(1, p.Width - 80), 40);
        return screen.IntersectsWith(titleArea);
    }
}
