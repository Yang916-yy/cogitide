using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Workbench.Protocol;
using L = Workbench.Desktop.Localization;

namespace Workbench.Desktop;

/// <summary>Builds a small native Avalonia form; validation is the same contract used by Host and Core.</summary>
internal sealed class RunBudgetWindow : Window
{
    internal RunBudgetWindow(RunOptions initial, OsIsolationStatus? isolationStatus = null)
    {
        Title = L.Text("RunBudgetTitle");
        Width = 480; Height = 530; CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var iterations = new NumericUpDown { Minimum = 1, Maximum = RunLimits.MaximumIterations, Value = initial.MaxIterations, Increment = 64 };
        var hours = new NumericUpDown { Minimum = 1m / 60, Maximum = 168, Value = (decimal)initial.TimeoutSeconds / 3600, Increment = 1, FormatString = "0.##" };
        var context = new NumericUpDown { Minimum = 4000, Maximum = 1_000_000, Value = initial.MaxContextCharacters, Increment = 20000 };
        var tokens = new TextBox { Text = initial.MaxReportedTokens?.ToString(CultureInfo.InvariantCulture), PlaceholderText = L.Text("RunTokensOptional") };
        var compact = new CheckBox { Content = L.Text("RunAutoCompact"), IsChecked = initial.AutoCompact };
        var isolated = new CheckBox { Content = L.Text("OsIsolationRequired"), IsChecked = initial.RequireOsIsolation };
        var isolationHint = new TextBlock { Text = L.Text("OsIsolationHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
        var isolationBackend = new TextBlock { Text = isolationStatus is null ? "" : isolationStatus.Supported
            ? L.Format("OsIsolationAvailable", isolationStatus.Backend) : L.Format("OsIsolationUnavailable", isolationStatus.Reason ?? isolationStatus.Backend),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 11 };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var save = new Button { Content = L.Text("Save"), Classes = { "primary" }, HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            try
            {
                long? tokenLimit = null;
                if (!string.IsNullOrWhiteSpace(tokens.Text)) tokenLimit = long.Parse(tokens.Text, NumberStyles.None, CultureInfo.InvariantCulture);
                var options = initial with
                {
                    MaxIterations = decimal.ToInt32(iterations.Value ?? 256),
                    TimeoutSeconds = decimal.ToInt32((hours.Value ?? 6) * 3600),
                    MaxContextCharacters = decimal.ToInt32(context.Value ?? 100000),
                    MaxReportedTokens = tokenLimit, AutoCompact = compact.IsChecked == true,
                    RequireOsIsolation = isolated.IsChecked == true
                };
                RunLimits.Validate(options);
                Close(options);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException) { error.Text = ex.Message; }
        };
        Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(24), Spacing = 9,
                Children =
                {
                    new TextBlock { Text = L.Text("RunBudgetHint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new TextBlock { Text = L.Text("RunIterations") }, iterations,
                    new TextBlock { Text = L.Text("RunHours") }, hours,
                    new TextBlock { Text = L.Text("RunContextCharacters") }, context,
                    new TextBlock { Text = L.Text("RunReportedTokens") }, tokens, compact, isolated, isolationHint, isolationBackend, error, save
                }
            }
        };
    }
}
