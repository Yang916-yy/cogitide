using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Workbench.Desktop;

/// <summary>A live resource binding, including when a dialog is already open.</summary>
public sealed class LocExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) =>
        LocalizedControl.ResourceBinding(key, []);
}

internal static class LocalizedControl
{
    // On the Windows backend, an already arranged header can keep its old constraint
    // after a language change. Remeasure only the header ancestry once bindings settle;
    // do not recreate tabs, change selection, or invalidate the entire window tree.
    public static void RemeasureTabHeaders(TabControl tabs) => Dispatcher.UIThread.Post(() =>
    {
        if (TopLevel.GetTopLevel(tabs) is null) return;
        foreach (var item in tabs.Items.OfType<TabItem>())
        {
            for (var current = (item.Header as Control ?? item) as Avalonia.Visual;
                 current is Layoutable layout; current = current.GetVisualParent())
            {
                layout.InvalidateMeasure();
                if (ReferenceEquals(current, tabs)) break;
            }
        }
        tabs.InvalidateMeasure();
        tabs.UpdateLayout();
    }, DispatcherPriority.Background);

    public static void Text(TextBlock control, string key, params object?[] arguments) =>
        control.Bind(TextBlock.TextProperty, ResourceBinding(key, arguments));

    public static void Content(ContentControl control, string key, params object?[] arguments) =>
        control.Bind(ContentControl.ContentProperty, ResourceBinding(key, arguments));

    internal static Binding ResourceBinding(string key, object?[] arguments) => new(nameof(Localization.Revision))
    {
        Source = Localization.Instance,
        Mode = BindingMode.OneWay,
        Converter = new FuncValueConverter<int, string>(_ => Localization.Format(key,
            arguments.Select(value => value is Func<string> deferred ? deferred() : value).ToArray()))
    };
}
