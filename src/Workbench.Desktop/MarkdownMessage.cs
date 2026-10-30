using L = Workbench.Desktop.Localization;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Markdig.Extensions.Tables;

namespace Workbench.Desktop;

/// <summary>Native, resource-free Markdown. Model content cannot load images, HTML or remote resources.</summary>
public sealed class MarkdownMessage : ContentControl
{
    private const int MaxMarkdownCharacters = 100_000;
    private const int MaxRenderNodes = 1000;
    private const int MaxRenderDepth = 24;
    private const int MaxPlainTextCharacters = 50_000;
    public static readonly StyledProperty<string?> MarkdownProperty = AvaloniaProperty.Register<MarkdownMessage, string?>(nameof(Markdown));
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().DisableHtml().Build();
    private static readonly FontFamily CodeFont = new("Cascadia Code, Consolas, Menlo, monospace");
    private readonly DispatcherTimer _renderTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    public string? Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }

    public MarkdownMessage()
    {
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); RenderMarkdown(); };
        DetachedFromVisualTree += (_, _) => { _renderTimer.Stop(); L.Changed -= RefreshLocalization; };
        AttachedToVisualTree += (_, _) => { L.Changed += RefreshLocalization; if (IsVisible) RenderMarkdown(); };
    }

    private void RefreshLocalization() { if (IsVisible) RenderMarkdown(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && !IsVisible) _renderTimer.Stop();
        else if ((change.Property == MarkdownProperty || change.Property == IsVisibleProperty) && IsVisible && !_renderTimer.IsEnabled) _renderTimer.Start();
    }

    private void RenderMarkdown()
    {
        var source = Markdown ?? "";
        // Reject oversized input before parsing; a streaming response must not monopolize the dispatcher.
        if (source.Length > MaxMarkdownCharacters)
        {
            RenderPlainText(source, L.Text("MarkdownLong"));
            return;
        }
        try
        {
            var document = Markdig.Markdown.Parse(source, Pipeline);
            if (!WithinRenderBudget(document))
            {
                RenderPlainText(source, L.Text("MarkdownComplex"));
                return;
            }
            var stack = new StackPanel { Spacing = 11 };
            foreach (var block in document) stack.Children.Add(RenderBlock(block));
            Content = stack;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        {
            RenderPlainText(source, L.Text("MarkdownUnavailable"));
        }
    }

    private static bool WithinRenderBudget(MarkdownObject root)
    {
        var pending = new Stack<(MarkdownObject Node, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.TryPop(out var item))
        {
            if (++visited > MaxRenderNodes || item.Depth > MaxRenderDepth) return false;
            // Count block and inline structure before allocating any Avalonia controls or recursive spans.
            IEnumerable<MarkdownObject> children = item.Node switch
            {
                ContainerBlock block => block,
                LeafBlock { Inline: { } inline } => new MarkdownObject[] { inline },
                ContainerInline inline => inline,
                _ => Array.Empty<MarkdownObject>()
            };
            foreach (var child in children)
            {
                if (visited + pending.Count >= MaxRenderNodes) return false;
                pending.Push((child, item.Depth + 1));
            }
        }
        return true;
    }

    private void RenderPlainText(string source, string notice)
    {
        if (source.Length > MaxPlainTextCharacters)
            notice += L.Format("MarkdownTruncated", MaxPlainTextCharacters);
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = notice, TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brush.Parse("#8D949F") });
        panel.Children.Add(new SelectableTextBlock
        {
            Text = source[..Math.Min(source.Length, MaxPlainTextCharacters)], TextWrapping = TextWrapping.Wrap,
            FontSize = 14, LineHeight = 24, Foreground = Brush.Parse("#D8DBE1")
        });
        Content = panel;
    }

    private Control RenderBlock(Block block)
    {
        switch (block)
        {
            case HeadingBlock heading:
                var title = Text(heading.Inline);
                title.FontSize = heading.Level <= 2 ? 19 : 16;
                title.FontWeight = FontWeight.SemiBold;
                title.Margin = new Thickness(0, 9, 0, 1);
                return title;
            case ParagraphBlock paragraph: return Text(paragraph.Inline);
            case CodeBlock code: return Code(code);
            case ListBlock list:
                var items = new StackPanel { Spacing = 6 };
                var number = 1;
                if (list.IsOrdered) int.TryParse(list.OrderedStart, out number);
                foreach (var item in list.OfType<ListItemBlock>())
                {
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("24,*") };
                    row.Children.Add(new TextBlock { Text = list.IsOrdered ? $"{number++}." : "•", FontSize = 14, Margin = new Thickness(2, 2, 0, 0), Foreground = Brush.Parse("#B9BDC5") });
                    var content = new StackPanel { Spacing = 6 };
                    foreach (var child in item) content.Children.Add(RenderBlock(child));
                    Grid.SetColumn(content, 1);
                    row.Children.Add(content);
                    items.Children.Add(row);
                }
                return items;
            case QuoteBlock quote:
                var quoted = new StackPanel { Spacing = 8 };
                foreach (var child in quote) quoted.Children.Add(RenderBlock(child));
                return new Border { BorderBrush = Brush.Parse("#515661"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(14, 2), Child = quoted };
            case ThematicBreakBlock: return new Border { Height = 1, Background = Brush.Parse("#3B3E45"), Margin = new Thickness(0, 7) };
            case Table table:
                var rows = new StackPanel();
                foreach (var row in table.OfType<TableRow>())
                {
                    var grid = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(',', Enumerable.Repeat("*", Math.Max(1, row.Count)))) };
                    for (var i = 0; i < row.Count; i++)
                    {
                        var cell = new StackPanel { Margin = new Thickness(10, 9), Spacing = 4 };
                        foreach (var child in (TableCell)row[i]) cell.Children.Add(RenderBlock(child));
                        if (row.IsHeader) cell.SetValue(TextBlock.FontWeightProperty, FontWeight.SemiBold);
                        Grid.SetColumn(cell, i);
                        grid.Children.Add(cell);
                    }
                    rows.Children.Add(new Border { Child = grid, BorderBrush = Brush.Parse("#3B3E45"), BorderThickness = new Thickness(0, 0, 0, 1) });
                }
                return rows;
            case LeafBlock leaf: return new SelectableTextBlock { Text = leaf.Lines.ToString(), TextWrapping = TextWrapping.Wrap, FontSize = 14 };
            case ContainerBlock container:
                var children = new StackPanel { Spacing = 8 };
                foreach (var child in container) children.Children.Add(RenderBlock(child));
                return children;
            default: return new TextBlock();
        }
    }

    private SelectableTextBlock Text(ContainerInline? source)
    {
        var text = new SelectableTextBlock { Inlines = new InlineCollection(), TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 24, Foreground = Brush.Parse("#D8DBE1") };
        if (source is not null) AddInlines(source, text.Inlines);
        return text;
    }

    private void AddInlines(ContainerInline source, InlineCollection target)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = CodeFont, FontSize = 12.5, Foreground = Brush.Parse("#CDD3DD") }); break;
                case LineBreakInline: target.Add(new Run("\n")); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeight.SemiBold;
                    else span.FontStyle = FontStyle.Italic;
                    AddInlines(emphasis, span.Inlines);
                    target.Add(span);
                    break;
                case LinkInline link:
                    // Images remain text. Links only open a validated web URL after an explicit click.
                    var linkText = new Span { Foreground = Brush.Parse("#B4C5DF") };
                    AddInlines(link, linkText.Inlines);
                    if (link.IsImage) target.Add(new Run(L.Text("ImagePlaceholder")));
                    target.Add(linkText);
                    if (!link.IsImage && Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
                    {
                        var button = new Button { Content = "↗", FontSize = 11, Padding = new Thickness(3, 0), MinHeight = 20, Background = Brushes.Transparent, Foreground = Brush.Parse("#A8B4C8"), BorderThickness = new Thickness(0) };
                        ToolTip.SetTip(button, uri.Host);
                        button.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception) { } };
                        target.Add(new InlineUIContainer { Child = button });
                    }
                    break;
                case AutolinkInline auto: target.Add(new Run(auto.Url)); break;
                case ContainerInline nested: AddInlines(nested, target); break;
            }
        }
    }

    private Control Code(CodeBlock block)
    {
        var code = block.Lines.ToString();
        var language = (block as FencedCodeBlock)?.Info ?? "code";
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(14, 7, 8, 3) };
        header.Children.Add(new TextBlock { Text = language, FontSize = 10, Foreground = Brush.Parse("#8D949F"), VerticalAlignment = VerticalAlignment.Center });
        var copy = new Button { Content = L.Text("Copy"), Classes = { "toolbar" }, FontSize = 10, MinHeight = 25, Padding = new Thickness(8, 3) };
        LocalizedControl.Content(copy, "Copy");
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            try { await clipboard.SetTextAsync(code); LocalizedControl.Content(copy, "Copied"); }
            catch (Exception) { LocalizedControl.Content(copy, "CopyFailed"); }
        };
        Grid.SetColumn(copy, 1);
        header.Children.Add(copy);
        grid.Children.Add(header);
        var source = new SelectableTextBlock { Text = code, FontFamily = CodeFont, FontSize = 12, LineHeight = 20, Foreground = Brush.Parse("#C8CDD6") };
        var scroll = new ScrollViewer { Content = source, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, MaxHeight = 420, Margin = new Thickness(14, 2, 14, 13) };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return new Border { Background = Brush.Parse("#292B2F"), CornerRadius = new CornerRadius(9), Child = grid, Margin = new Thickness(0, 3) };
    }
}
