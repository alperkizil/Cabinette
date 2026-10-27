using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Cabinette.App.Localization;
using Cabinette.App.Services;
using Cabinette.App.ViewModels.Dialogs;
using Cabinette.App.Views.Dialogs;
using Cabinette.App.Views.Thread;
using Cabinette.Core.Models;

namespace Cabinette.App.Tests;

/// <summary>
/// Regression tests for the review of Bold text and Font (branch review/fonts-sonnet, findings M1, M2 and L3, and the
/// suspected font-listing failure). Each failed before its fix.
/// </summary>
public class FontReviewRegressionTests
{
    private static List<FontOption> Options()
        => UiFont.Available().Select(c => new FontOption(c.Font, L.Get(c.NameKey), c.Text)).ToList();

    private static ComboBox FontList(List<FontOption> options, ResourceDictionary controls)
    {
        // The same shape as Settings › Appearance › Font: the app's style, the item type and a template that shows the name.
        var template = new DataTemplate(typeof(FontOption));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(FontOption.Name)));
        template.VisualTree = text;
        var combo = new ComboBox { ItemsSource = options, ItemTemplate = template, Style = (Style)controls["Shell.ComboBox"], Width = 300 };
        combo.Resources.MergedDictionaries.Add(controls);
        return combo;
    }

    /// <summary>M1: screen readers and the closed list read each font's name, not the record's ToString().</summary>
    [Fact]
    public void The_font_list_items_are_named_by_the_font_in_UI_Automation() => HiddenWpf.RunSta(() =>
    {
        var options = Options();
        var combo = FontList(options, BoldTextTests.Controls.Value!);
        using var source = HiddenWpf.Source(combo, 400, 300);
        combo.SelectedItem = options[2];
        combo.IsDropDownOpen = true;
        combo.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(200));

        for (var i = 0; i < options.Count; i++)
        {
            var container = (ComboBoxItem)combo.ItemContainerGenerator.ContainerFromIndex(i);
            Assert.Equal(options[i].Name, UIElementAutomationPeer.CreatePeerForElement(container).GetName());
        }

        Assert.Equal(options[2].Name, combo.Text);
    });

    /// <summary>M1: typing a font's first letter chooses that font (type-ahead matched "FontOption { …" before).</summary>
    [Fact]
    public void Typing_the_first_letter_of_a_font_chooses_that_font() => HiddenWpf.RunSta(() =>
    {
        var options = Options();
        var combo = FontList(options, BoldTextTests.Controls.Value!);
        using var source = HiddenWpf.Source(combo, 400, 300);
        combo.UpdateLayout();
        combo.SelectedItem = options.Single(o => o.Font == AppFont.Lexend);

        combo.RaiseEvent(new TextCompositionEventArgs(Keyboard.PrimaryDevice, new TextComposition(InputManager.Current, combo, "I"))
        {
            RoutedEvent = TextCompositionManager.TextInputEvent,
        });

        Assert.Equal(AppFont.Inter, ((FontOption)combo.SelectedItem).Font);
    });

    /// <summary>
    /// M2: Esc in the open font list closes the list, not the whole Settings dialog; with the list closed, Esc closes the
    /// dialog as before. DialogHost can't be built without an Application, so its Esc branch runs here on an ancestor, with
    /// the real <see cref="DialogHost.IsInOpenDropDown"/>.
    /// </summary>
    [Fact]
    public void Esc_in_the_open_font_list_closes_the_list_and_not_the_dialog() => HiddenWpf.RunSta(() =>
    {
        var controls = BoldTextTests.Controls.Value!;
        var options = Options();
        var combo = new ComboBox { ItemsSource = options, DisplayMemberPath = nameof(FontOption.Name), Style = (Style)controls["Shell.ComboBox"], Width = 300 };
        var dialogHost = new Border { Child = combo };
        dialogHost.Resources.MergedDictionaries.Add(controls);
        var dismissed = false;
        dialogHost.PreviewKeyDown += (_, e) =>
        {
            // DialogHost.OnPreviewKeyDown, Key.Escape branch.
            if (e.Key == Key.Escape && !DialogHost.IsInOpenDropDown(e.OriginalSource as DependencyObject, dialogHost))
            {
                dismissed = true;
                e.Handled = true;
            }
        };
        using var source = HiddenWpf.Source(dialogHost, 400, 300);
        combo.SelectedItem = options[0];
        combo.IsDropDownOpen = true;
        combo.UpdateLayout();
        HiddenWpf.Pump(TimeSpan.FromMilliseconds(200));

        void PressEsc(UIElement target)
        {
            var preview = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            target.RaiseEvent(preview);
            if (!preview.Handled)
            {
                target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
            }
        }

        PressEsc((UIElement)combo.ItemContainerGenerator.ContainerFromIndex(1));
        Assert.False(dismissed, "the dialog host dismissed Settings on the Esc that was meant to close the font list");
        Assert.False(combo.IsDropDownOpen);

        PressEsc(combo);
        Assert.True(dismissed, "with the list closed, Esc must still close the dialog");
    });

    /// <summary>L3: when "3 selected" shortens to "3", screen readers still hear "3 selected".</summary>
    [Fact]
    public void The_shortened_selection_count_keeps_its_meaning_in_UI_Automation() => GalleryTests.RunOnDispatcher(async () =>
    {
        var vm = await BoldTextTests.OpenThreadAsync("Images");
        var view = new ThreadView { DataContext = vm };
        var host = BoldTextTests.WindowLikeHost(view);
        host.HorizontalAlignment = HorizontalAlignment.Left;
        UiWeight.Apply(true, host.Resources);
        UiFont.Apply(AppFont.Lexend, host.Resources);
        host.SetResourceReference(TextElement.FontFamilyProperty, UiFont.TextKey);
        using var source = HiddenWpf.Source(host, 900, 700);
        vm.SelectOnly(vm.Items.TakeLast(3).ToList());

        var count = (TextBlock)view.FindName("SelectionCount");
        for (var width = 900; width >= 300 && count.Text != "3"; width -= 10)
        {
            host.Width = width;
            await GalleryReviewSupport.SettleAsync(view);
        }

        Assert.Equal("3", count.Text); // the short form showed
        Assert.Equal(vm.SelectionText, UIElementAutomationPeer.CreatePeerForElement(count).GetName());
        vm.Dispose();
    });

    /// <summary>Suspected in the review: if Windows can't list its fonts, Settings still opens and offers every font.</summary>
    [Fact]
    public void A_failure_listing_the_installed_fonts_offers_every_font()
    {
        Assert.Null(UiFont.ReadInstalledFamilies(() => throw new InvalidOperationException("damaged font")));

        var installed = UiFont.ReadInstalledFamilies(() => [new FontFamily("Segoe UI")]);
        Assert.NotNull(installed);
        Assert.Contains("Segoe UI", installed);
    }
}
