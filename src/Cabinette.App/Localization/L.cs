using System.Globalization;
using System.Resources;
using System.Windows.Markup;

namespace Cabinette.App.Localization;

/// <summary>
/// UI strings. All user-visible text comes from .resx files in this folder so a language can be added later
/// by adding e.g. Strings.tr.resx. Strings.resx belongs to the shell, ThreadStrings.resx to the thread view.
/// </summary>
public static class L
{
    private static readonly ResourceManager[] Managers =
    [
        new("Cabinette.App.Localization.Strings", typeof(L).Assembly),
        new("Cabinette.App.Localization.ThreadStrings", typeof(L).Assembly),
    ];

    public static string Get(string key)
    {
        foreach (var manager in Managers)
        {
            try
            {
                var value = manager.GetString(key, CultureInfo.CurrentUICulture);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (MissingManifestResourceException)
            {
                // That resource file has no entries yet.
            }
        }

        return $"[{key}]";
    }

    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

/// <summary>XAML usage: <c>Text="{loc:Loc Search_Placeholder}"</c> with <c>xmlns:loc="clr-namespace:Cabinette.App.Localization"</c>.</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class LocExtension : MarkupExtension
{
    public LocExtension()
    {
    }

    public LocExtension(string key)
    {
        Key = key;
    }

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => L.Get(Key);
}
