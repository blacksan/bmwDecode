using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace BmwDecode.App.Views;

/// <summary>
/// Groups an ItemsControl by a property of its items (the shared view models expose plain sorted lists, not
/// WPF collection views): <c>views:ItemsGrouping.By="Category"</c>.
/// </summary>
public static class ItemsGrouping
{
    public static readonly DependencyProperty ByProperty = DependencyProperty.RegisterAttached(
        "By", typeof(string), typeof(ItemsGrouping), new PropertyMetadata(null, OnByChanged));

    public static string? GetBy(DependencyObject o) => (string?)o.GetValue(ByProperty);

    public static void SetBy(DependencyObject o, string? value) => o.SetValue(ByProperty, value);

    private static void OnByChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl items)
        {
            return;
        }

        items.Items.GroupDescriptions.Clear();
        if (e.NewValue is string { Length: > 0 } property)
        {
            items.Items.GroupDescriptions.Add(new PropertyGroupDescription(property));
        }
    }
}
