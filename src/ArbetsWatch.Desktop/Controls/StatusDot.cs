using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ArbetsWatch.Desktop.Controls;

public enum StatusTone
{
    None,
    Neutral,
    Success,
    Warning,
    Failure,
    Running,
}

/// <summary>
/// A small status indicator colored from the theme's Status*Brush resources (from Repo Watch). It glows softly,
/// and while its tone is Running it gets the <c>:active</c> pseudo-class, which the theme animates as an
/// outward pulse unless motion is reduced. Always pair with a text label.
/// </summary>
public sealed class StatusDot : Control
{
    public static readonly StyledProperty<StatusTone> ToneProperty =
        AvaloniaProperty.Register<StatusDot, StatusTone>(nameof(Tone));

    /// <summary>Animation phase of the pulse ring, 0..1. Driven by the theme's animation; 0 = no ring.</summary>
    public static readonly StyledProperty<double> PulseProperty =
        AvaloniaProperty.Register<StatusDot, double>(nameof(Pulse));

    static StatusDot()
    {
        AffectsRender<StatusDot>(ToneProperty, PulseProperty);
    }

    public StatusDot()
    {
        Width = 10;
        Height = 10;
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public StatusTone Tone
    {
        get => GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double Pulse
    {
        get => GetValue(PulseProperty);
        set => SetValue(PulseProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ToneProperty)
        {
            PseudoClasses.Set(":active", Tone == StatusTone.Running);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (Tone == StatusTone.None)
        {
            return;
        }

        var color = this.TryFindResource($"Status{Tone}Brush", ActualThemeVariant, out var resource) && resource is ISolidColorBrush brush
            ? brush.Color
            : Colors.Gray;

        var radius = Math.Min(Bounds.Width, Bounds.Height) / 2;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

        if (Tone != StatusTone.Neutral)
        {
            var halo = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(110, color.R, color.G, color.B), 0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
                },
            };
            context.DrawEllipse(halo, null, center, radius * 2.2, radius * 2.2);
        }

        if (Pulse > 0)
        {
            var alpha = (byte)(200 * (1 - Pulse));
            var ring = radius * (1 + (1.8 * Pulse));
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), 1.5), center, ring, ring);
        }

        context.DrawEllipse(new SolidColorBrush(color), null, center, radius, radius);
    }
}
