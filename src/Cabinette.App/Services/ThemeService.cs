using System.Windows;
using System.Windows.Media;
using Cabinette.Core.Models;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Cabinette.App.Services;

public interface IThemeService
{
    /// <summary>The user's preference (from settings).</summary>
    ThemePreference Preference { get; }

    /// <summary>The theme actually shown.</summary>
    bool IsDark { get; }

    /// <summary>Raised on the UI thread after the theme dictionaries were swapped.</summary>
    event EventHandler? ThemeChanged;

    /// <summary>Applies a preference (System follows Windows' "app mode" live). Call on the UI thread.</summary>
    void Apply(ThemePreference preference);
}

/// <summary>
/// Swaps merged dictionary index 2 of App.xaml between Themes/Light.xaml and Themes/Dark.xaml and keeps WPF-UI's own
/// theme and accent in sync. A <c>--theme</c> command-line override wins over the setting.
/// </summary>
public sealed class ThemeService : IThemeService, IDisposable
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const int ThemeDictionaryIndex = 2;

    private readonly ThemePreference? _override;
    private bool _applied;
    private bool _watchingSystem;

    public ThemeService(ThemePreference? overridePreference)
    {
        _override = overridePreference;
    }

    public ThemePreference Preference { get; private set; } = ThemePreference.System;

    public bool IsDark { get; private set; }

    public event EventHandler? ThemeChanged;

    public void Apply(ThemePreference preference)
    {
        Preference = preference;
        var effective = _override ?? preference;
        SetSystemWatch(effective == ThemePreference.System);
        Update(effective);
    }

    public void Dispose() => SetSystemWatch(false);

    public static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void Update(ThemePreference effective)
    {
        var dark = effective switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => IsSystemDark(),
        };

        if (_applied && dark == IsDark)
        {
            return;
        }

        _applied = true;
        IsDark = dark;

        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        var dictionaries = app.Resources.MergedDictionaries;
        var index = FindThemeDictionaryIndex(dictionaries);
        var replacement = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Cabinette;component/Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Absolute),
        };
        if (index >= 0)
        {
            dictionaries[index] = replacement;
        }
        else
        {
            dictionaries.Add(replacement);
        }

        try
        {
            var wpfUiTheme = dark ? ApplicationTheme.Dark : ApplicationTheme.Light;
            ApplicationThemeManager.Apply(wpfUiTheme, WindowBackdropType.None, updateAccent: false);
            if (replacement["Cab.AccentColor"] is Color accent)
            {
                ApplicationAccentColorManager.Apply(accent, wpfUiTheme);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("Applying the WPF-UI theme failed.", ex);
        }

        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static int FindThemeDictionaryIndex(IList<ResourceDictionary> dictionaries)
    {
        for (var i = 0; i < dictionaries.Count; i++)
        {
            var source = dictionaries[i].Source?.OriginalString ?? "";
            if (source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase) ||
                source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return dictionaries.Count > ThemeDictionaryIndex ? ThemeDictionaryIndex : -1;
    }

    private void SetSystemWatch(bool watch)
    {
        if (watch == _watchingSystem)
        {
            return;
        }

        _watchingSystem = watch;
        if (watch)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        else
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle))
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if ((_override ?? Preference) == ThemePreference.System)
            {
                Update(ThemePreference.System);
            }
        });
    }
}
