using Cabinette.Core.Data;
using Cabinette.Core.Models;
using Cabinette.Core.Tests.Infrastructure;

namespace Cabinette.Core.Tests;

/// <summary>Settings › Appearance in the library's settings: stored like the others, and readable across versions.</summary>
public sealed class AppearanceSettingsTests
{
    [Fact]
    public async Task Bold_text_is_off_by_default_and_stored_like_the_others()
    {
        await using var t = await TestLibrary.CreateAsync();
        Assert.False((await t.Library.GetSettingsAsync()).BoldText);

        var changed = (await t.Library.GetSettingsAsync()) with { BoldText = true, TextSize = TextSize.Large };
        await t.Library.SaveSettingsAsync(changed);
        await t.ReopenAsync();
        Assert.Equal(changed, await t.Library.GetSettingsAsync());
    }

    [Fact]
    public async Task Settings_saved_before_Bold_text_existed_open_with_it_off()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { BoldText = true, Theme = ThemePreference.Dark });
        await t.Library.DisposeAsync();
        Assert.Equal(1, Execute(t, "UPDATE settings SET value = json_remove(value, '$.BoldText') WHERE key = 'app_settings'"));

        await t.OpenAsync();
        var settings = await t.Library.GetSettingsAsync();
        Assert.False(settings.BoldText);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
    }

    /// <summary>
    /// An older build reads this build's settings like this build reads a newer one's: a setting it doesn't know is skipped.
    /// If it made the whole read fail, every setting would silently go back to its default (LoadSettings catches the error).
    /// </summary>
    [Fact]
    public async Task A_setting_from_a_newer_build_is_skipped_and_the_rest_kept()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { BoldText = true, TextSize = TextSize.ExtraLarge, Theme = ThemePreference.Dark });
        await t.Library.DisposeAsync();
        Assert.Equal(1, Execute(t, "UPDATE settings SET value = json_set(value, '$.SettingFromTheFuture', 'whatever', '$.AnotherOne', json('{\"a\":[1,2]}')) WHERE key = 'app_settings'"));

        await t.OpenAsync();
        var settings = await t.Library.GetSettingsAsync();
        Assert.True(settings.BoldText);
        Assert.Equal(TextSize.ExtraLarge, settings.TextSize);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(t.Vault, settings.VaultPath);
    }

    [Fact]
    public async Task The_font_is_the_default_until_chosen_and_stored_by_name()
    {
        await using var t = await TestLibrary.CreateAsync();
        Assert.Equal(AppFont.SegoeUIVariable, (await t.Library.GetSettingsAsync()).Font);

        var changed = (await t.Library.GetSettingsAsync()) with { Font = AppFont.JetBrainsMono, BoldText = true };
        await t.Library.SaveSettingsAsync(changed);
        await t.Library.DisposeAsync();
        Assert.Equal("JetBrainsMono", Scalar(t, "SELECT json_extract(value, '$.Font') FROM settings WHERE key = 'app_settings'"));

        await t.OpenAsync();
        Assert.Equal(changed, await t.Library.GetSettingsAsync());
    }

    [Fact]
    public async Task Settings_saved_before_the_font_existed_open_with_the_default_font()
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { Font = AppFont.Lexend, TextSize = TextSize.Large });
        await t.Library.DisposeAsync();
        Assert.Equal(1, Execute(t, "UPDATE settings SET value = json_remove(value, '$.Font') WHERE key = 'app_settings'"));

        await t.OpenAsync();
        var settings = await t.Library.GetSettingsAsync();
        Assert.Equal(AppFont.SegoeUIVariable, settings.Font);
        Assert.Equal(TextSize.Large, settings.TextSize);
    }

    /// <summary>
    /// A newer build may offer a font this one doesn't know. Its name reads as the default font, and every other setting is
    /// kept: a plain enum converter would fail the whole read, and all settings would go back to their defaults.
    /// </summary>
    [Theory]
    [InlineData("'ComicNeue'")]
    [InlineData("''")]
    [InlineData("42")]
    [InlineData("json('{\"family\":\"x\"}')")]
    [InlineData("json('[1,2]')")]
    [InlineData("json('null')")]
    public async Task A_font_from_a_newer_build_reads_as_the_default_and_keeps_the_rest(string value)
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.SaveSettingsAsync((await t.Library.GetSettingsAsync()) with { Font = AppFont.Sitka, BoldText = true, Theme = ThemePreference.Dark });
        await t.Library.DisposeAsync();
        Assert.Equal(1, Execute(t, $"UPDATE settings SET value = json_set(value, '$.Font', {value}) WHERE key = 'app_settings'"));

        await t.OpenAsync();
        var settings = await t.Library.GetSettingsAsync();
        Assert.Equal(AppFont.SegoeUIVariable, settings.Font);
        Assert.True(settings.BoldText);
        Assert.Equal(ThemePreference.Dark, settings.Theme);
        Assert.Equal(t.Vault, settings.VaultPath);
    }

    /// <summary>A font stored by number (no build writes that, but JsonStringEnumConverter read it) or in another case still reads.</summary>
    [Theory]
    [InlineData("9", AppFont.JetBrainsMono)]
    [InlineData("'lexend'", AppFont.Lexend)]
    [InlineData("'CascadiaMono'", AppFont.CascadiaMono)]
    public async Task A_known_font_reads_however_it_is_written(string value, AppFont expected)
    {
        await using var t = await TestLibrary.CreateAsync();
        await t.Library.DisposeAsync();
        Assert.Equal(1, Execute(t, $"UPDATE settings SET value = json_set(value, '$.Font', {value}) WHERE key = 'app_settings'"));

        await t.OpenAsync();
        Assert.Equal(expected, (await t.Library.GetSettingsAsync()).Font);
    }

    private static object? Scalar(TestLibrary t, string sql)
    {
        using var connection = new Database(t.DataDirectory).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static int Execute(TestLibrary t, string sql)
    {
        using var connection = new Database(t.DataDirectory).Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteNonQuery();
    }
}
