using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace Workbench.Desktop;

/// <summary>Small native line icons, independent of platform emoji and icon fonts.</summary>
public sealed class UiGlyph : TemplatedControl
{
    public static readonly StyledProperty<string> SymbolProperty = AvaloniaProperty.Register<UiGlyph, string>(nameof(Symbol), "plus");
    public string Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    static UiGlyph() => AffectsRender<UiGlyph>(SymbolProperty, ForegroundProperty);
    public UiGlyph() { Width = 16; Height = 16; IsHitTestVisible = false; }
    public override void Render(DrawingContext context)
    {
        var path = Symbol switch
        {
            "folder" => "M3 7 L3 20 L21 20 L21 6 L11 6 L9 4 L3 4 Z",
            "files" => "M6 3 L15 3 L20 8 L20 21 L6 21 Z M15 3 L15 8 L20 8 M3 7 L3 18",
            "changes" => "M5 7 L19 7 M12 2 L12 12 M5 18 L19 18",
            "terminal" => "M4 6 L10 12 L4 18 M12 18 L20 18",
            "settings" => "M5 3 L5 8 M5 12 L5 21 M12 3 L12 14 M12 18 L12 21 M19 3 L19 5 M19 9 L19 21 M2 8 L8 8 L8 12 L2 12 Z M9 14 L15 14 L15 18 L9 18 Z M16 5 L22 5 L22 9 L16 9 Z",
            "send" => "M12 19 L12 5 M6 11 L12 5 L18 11",
            "stop" => "M7 7 L17 7 L17 17 L7 17 Z",
            "image" => "M3 3 L21 3 L21 21 L3 21 Z M3 17 L9 11 L14 16 L17 13 L21 17 M15 7 L17 7 L17 9 L15 9 Z",
            "shield" => "M12 2 L21 6 L20 14 Q18 20 12 23 Q6 20 4 14 L3 6 Z M8 12 L11 15 L16 9",
            "chevron" => "M6 9 L12 15 L18 9",
            _ => "M12 5 L12 19 M5 12 L19 12"
        };
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Foreground, 1.7, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), StreamGeometry.Parse(path));
    }
}
