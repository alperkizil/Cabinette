using System.Windows;
using System.Windows.Controls;
using Cabinette.App.ViewModels.Shell;

namespace Cabinette.App.Views.Shell;

/// <summary>Section headers in the search results are plain, non-selectable rows.</summary>
public sealed class SearchItemStyleSelector : StyleSelector
{
    public Style? HeaderStyle { get; set; }

    public Style? RowStyle { get; set; }

    public override Style? SelectStyle(object item, DependencyObject container)
        => item is SearchSectionHeader ? HeaderStyle : RowStyle;
}
