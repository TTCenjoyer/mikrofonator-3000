using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

public sealed class EqualizerEditor : FrameworkElement
{
    private EqualizerSettings settings = new();
    private int selected = 4;
    internal EqualizerSettings Settings
    {
        get => settings;
        set { settings = value; selected = Math.Min(selected, value.Points.Length - 1); InvalidateVisual(); }
    }
    public event EventHandler? Changed;
    public EqualizerEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ToolTip = "Przeciągnij punkt. Dwuklik zeruje pasmo. Strzałki wybierają pasmo i zmieniają poziom.";
        System.Windows.Automation.AutomationProperties.SetName(this, "Korektor z liniową krzywą — przeciągnij punkty");
    }
    private double Left => 26;
    private double Right => Math.Max(Left + 1, ActualWidth - 16);
    private double Top => 22;
    private double Bottom => Math.Max(Top + 1, ActualHeight - 24);
    private Point PointAt(int index, float value) => new(
        Left + Math.Log(settings.Points[index].Hertz / 20) / Math.Log(20000.0 / 20) * (Right - Left),
        Top + (30 - value) / 60 * (Bottom - Top));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var muted = new SolidColorBrush(Color.FromRgb(138,153,173));
        var accent = new SolidColorBrush(Color.FromRgb(107,153,198));
        var line = new Pen(new SolidColorBrush(Color.FromRgb(41,52,66)), 1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(8,10,13)), null, new Rect(RenderSize), 6, 6);
        void Label(string text, double x, double y, Brush brush, double size = 9) => dc.DrawText(
            new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x,y));
        foreach (int db in new[] { -30, -15, 0, 15, 30 })
        {
            double y = PointAt(0, db).Y;
            dc.DrawLine(line, new Point(Left,y), new Point(Right,y));
            Label(db > 0 ? "+" + db : db.ToString(), 2, y-6, muted);
        }
        var points = settings.Points;
        float[] gains = points.Select(p => p.Gain).ToArray();
        for (int i = 0; i < gains.Length; i++)
        {
            Point point = PointAt(i, gains[i]);
            dc.DrawLine(line, new Point(point.X,Top), new Point(point.X,Bottom));
            string hz = points[i].Hertz >= 1000 ? $"{points[i].Hertz/1000:0.#}k" : $"{points[i].Hertz:0}";
            if (i == 0 || point.X - PointAt(i-1,0).X > 28) Label(hz, point.X-9, Bottom+6, muted);
            if (i > 0) dc.DrawLine(new Pen(accent,2), PointAt(i-1,gains[i-1]),point);
        }
        for (int i = 0; i < gains.Length; i++)
            dc.DrawEllipse(i == selected ? Brushes.White : accent, new Pen(accent,1), PointAt(i,gains[i]), 4,4);
        Label($"{points[selected].Hertz:0} Hz   {gains[selected]:+0.0;-0.0;0.0} dB", Left, 3, accent, 10);
    }
    private void SetGain(float value)
    {
        var points = settings.Points.ToArray();
        points[selected] = points[selected] with { Gain = Math.Clamp(MathF.Round(value * 2) / 2, -30, 30) };
        Settings = settings with { Curve = points };
        Changed?.Invoke(this, EventArgs.Empty);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Point position = e.GetPosition(this);
        selected = Enumerable.Range(0,settings.Points.Length).MinBy(i => Math.Abs(PointAt(i,0).X-position.X));
        CaptureMouse();
        SetGain(e.ClickCount == 2 ? 0 : (float)(30 - 60*(position.Y-Top)/(Bottom-Top)));
        e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            SetGain((float)(30 - 60*(e.GetPosition(this).Y-Top)/(Bottom-Top)));
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e); }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch(e.Key)
        {
            case Key.Left: selected = Math.Max(0,selected-1); break;
            case Key.Right: selected = Math.Min(settings.Points.Length-1,selected+1); break;
            case Key.Up: SetGain(settings.Points[selected].Gain+.5f); break;
            case Key.Down: SetGain(settings.Points[selected].Gain-.5f); break;
            case Key.Home: SetGain(0); break;
            default: base.OnKeyDown(e); return;
        }
        InvalidateVisual(); e.Handled = true;
    }
}
