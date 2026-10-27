using System.Windows;

namespace Cabinette.App.Services;

/// <summary>
/// App-wide "Bold text" (Settings › Appearance). Every text weight Cabinette draws comes from five shared resources,
/// <c>{DynamicResource Cab.FontWeight.*}</c>, replaced live here like <see cref="UiScale"/>'s transform. Plain text uses
/// <see cref="BodyKey"/>, set as the inherited weight on each window, title bar, tooltip and context menu; text with a weight
/// of its own uses the key named after that weight. Off, each key holds the weight it is named after, so nothing changes;
/// on, each is one step heavier (<see cref="Map"/>), so headings still stand out. Icons (ui:SymbolIcon) keep their own weight.
/// </summary>
public static class UiWeight
{
    /// <summary>Light text (the large app name on the empty state).</summary>
    public const string LightKey = "Cab.FontWeight.Light";

    /// <summary>Plain text: the inherited weight of everything that sets none.</summary>
    public const string BodyKey = "Cab.FontWeight.Body";

    /// <summary>Medium text (the thread's date pills).</summary>
    public const string MediumKey = "Cab.FontWeight.Medium";

    /// <summary>SemiBold text: titles, names, buttons and labels.</summary>
    public const string StrongKey = "Cab.FontWeight.Strong";

    /// <summary>Bold text (the extension label on file icons); already as heavy as it gets.</summary>
    public const string BoldKey = "Cab.FontWeight.Bold";

    /// <summary>Each key with the weight it has while Bold text is off (the app's own design).</summary>
    public static IReadOnlyList<(string Key, FontWeight Designed)> Keys { get; } =
    [
        (LightKey, FontWeights.Light),
        (BodyKey, FontWeights.Normal),
        (MediumKey, FontWeights.Medium),
        (StrongKey, FontWeights.SemiBold),
        (BoldKey, FontWeights.Bold),
    ];

    public static bool IsBold { get; private set; }

    /// <summary>
    /// The weight drawn for a designed weight: unchanged while off; on, Light becomes Regular, Regular becomes SemiBold,
    /// Medium and SemiBold become Bold, and Bold (or heavier) stays.
    /// </summary>
    public static FontWeight Map(FontWeight designed, bool bold)
    {
        if (!bold)
        {
            return designed;
        }

        return designed.ToOpenTypeWeight() switch
        {
            >= 700 => designed,
            >= 500 => FontWeights.Bold,
            >= 400 => FontWeights.SemiBold,
            _ => FontWeights.Normal,
        };
    }

    /// <summary>Applies Bold text live. Call on the UI thread. Without an Application (tests) there is nothing to update.</summary>
    public static void Apply(bool bold)
    {
        IsBold = bold;
        if (Application.Current is { } app)
        {
            Apply(bold, app.Resources);
        }
    }

    /// <summary>Puts the weights for <paramref name="bold"/> into <paramref name="resources"/> (the app's own, or a test host's).</summary>
    public static void Apply(bool bold, ResourceDictionary resources)
    {
        foreach (var (key, designed) in Keys)
        {
            resources[key] = Map(designed, bold);
        }
    }
}
