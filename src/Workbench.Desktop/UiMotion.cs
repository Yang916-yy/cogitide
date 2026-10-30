using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Workbench.Desktop;

/// <summary>Short, optional visual feedback. Motion never delays an action or owns its state.</summary>
internal static class UiMotion
{
    private static readonly Dictionary<Control, RevealRun> Active = [];
    private static bool _reducedMotion;

    // Avalonia 12.1's cross-platform IPlatformSettings does not expose an OS motion preference.
    // The explicit application preference is therefore the source of truth on every platform.
    public static bool ReducedMotion
    {
        get => _reducedMotion;
        set
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => ReducedMotion = value);
                return;
            }
            if (_reducedMotion == value) return;
            _reducedMotion = value;
            if (value)
                foreach (var control in Active.Keys.ToArray()) Cancel(control);
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    public static event EventHandler? Changed;

    /// <summary>
    /// Returns a fresh transitions collection for a button, text box, or another templated control.
    /// Reassign it when <see cref="Changed"/> fires; an empty collection disables transitions immediately.
    /// </summary>
    public static Transitions CreateFeedbackTransitions() => ReducedMotion ? [] :
    [
        new BrushTransition
        {
            Property = TemplatedControl.BackgroundProperty,
            Duration = TimeSpan.FromMilliseconds(120),
            Easing = new CubicEaseOut()
        },
        new BrushTransition
        {
            Property = TemplatedControl.BorderBrushProperty,
            Duration = TimeSpan.FromMilliseconds(120),
            Easing = new CubicEaseOut()
        }
    ];

    /// <summary>Call after making a panel visible. Repeated calls replace the previous reveal.</summary>
    public static void Reveal(Control control)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Reveal(control));
            return;
        }
        Cancel(control);
        if (ReducedMotion || !control.IsVisible) return;
        var run = new RevealRun();
        Active.Add(control, run);
        control.DetachedFromVisualTree += ControlDetached;
        // All exceptions are observed inside the task. Decorative animation must not crash the UI.
        _ = RevealAsync(control, run);
    }

    private static async Task RevealAsync(Control control, RevealRun run)
    {
        try
        {
            var initial = new KeyFrame { Cue = new Cue(0) };
            var final = new KeyFrame { Cue = new Cue(1) };
            initial.Setters.Add(new Setter(Visual.OpacityProperty, 0d));
            final.Setters.Add(new Setter(Visual.OpacityProperty, control.Opacity));
            // Preserve any transform already used by the control's layout or interaction styling.
            if (control.RenderTransform is null)
            {
                initial.Setters.Add(new Setter(Visual.RenderTransformProperty, TransformOperations.Parse("translate(0px, 6px)")));
                final.Setters.Add(new Setter(Visual.RenderTransformProperty, TransformOperations.Identity));
            }
            var animation = new Animation
            {
                Duration = TimeSpan.FromMilliseconds(150),
                Easing = new CubicEaseOut(),
                FillMode = FillMode.None,
                Children = { initial, final }
            };
            // None leaves existing bindings/local values intact. Cancellation removes animation
            // priority synchronously, so turning motion off immediately restores the visible panel.
            await animation.RunAsync(control, run.Cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.WriteLine($"UI reveal skipped: {ex.GetType().Name}"); }
        finally
        {
            if (Active.TryGetValue(control, out var current) && ReferenceEquals(current, run))
            {
                Active.Remove(control);
                control.DetachedFromVisualTree -= ControlDetached;
                run.Cancellation.Dispose();
            }
        }
    }

    private static void ControlDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Control control) Cancel(control);
    }

    private static void Cancel(Control control)
    {
        if (!Active.Remove(control, out var run)) return;
        control.DetachedFromVisualTree -= ControlDetached;
        try { run.Cancellation.Cancel(); }
        catch (Exception ex) { Trace.WriteLine($"UI reveal cancellation skipped: {ex.GetType().Name}"); }
        finally { run.Cancellation.Dispose(); }
    }

    private sealed class RevealRun
    {
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
