using System.Windows;
using System.Windows.Media;

namespace FiltrMikrofonu;

public sealed class LevelMeter : FrameworkElement
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(nameof(Accent), typeof(Brush),
        typeof(LevelMeter), new FrameworkPropertyMetadata(Brushes.MediumPurple, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush Track = Freeze(Color.FromRgb(56, 50, 73));
    private static readonly Brush Amber = Freeze(Color.FromRgb(255, 191, 123));
    private double normalizedLevel;

    public Brush Accent { get => (Brush)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    public double NormalizedLevel
    {
        get => normalizedLevel;
        set
        {
            if (normalizedLevel == value)
                return;
            normalizedLevel = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        const int segments = 34;
        const double gap = 3;
        double step = (ActualWidth + gap) / segments;
        if (step <= gap || ActualHeight <= 0)
            return;
        int active = (int)Math.Ceiling(normalizedLevel * segments);
        for (int i = 0; i < segments; i++)
        {
            Brush fill = i >= active ? Track : i >= 31 ? Amber : Accent;
            drawingContext.DrawRoundedRectangle(fill, null, new Rect(i * step, 0, step - gap, ActualHeight), 2, 2);
        }
    }

    private static Brush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
