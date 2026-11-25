using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FiltrMikrofonu.Audio;

namespace FiltrMikrofonu;

public sealed class EqualizerEditor : FrameworkElement
{
    private EqualizerSettings settings = new();
    private readonly Stack<EqualizerSettings> undo = new(), redo = new();
    private int selected;
    private bool editing, linearScale, showGrid = true;
    internal EqualizerSettings Settings
    {
        get => settings;
        set { settings = value; selected = Math.Clamp(selected, 0, value.Points.Length - 1); undo.Clear(); redo.Clear(); Notify(false); }
    }
    internal EqualizerPoint SelectedPoint => settings.Points[selected];
    internal int SelectedIndex => selected;
    internal int PointCount => settings.Points.Length;
    internal bool CanDelete => PointCount > 2;
    internal bool CanUndo => undo.Count > 0;
    internal bool CanRedo => redo.Count > 0;
    public bool LinearScale { get => linearScale; set { linearScale = value; InvalidateVisual(); } }
    public bool ShowGrid { get => showGrid; set { showGrid = value; InvalidateVisual(); } }
    public event EventHandler? Changed;
    public event EventHandler? SelectionChanged;
    public EqualizerEditor()
    {
        Focusable = true;
        Cursor = Cursors.Cross;
        ToolTip = "Kliknij puste miejsce, aby dodać punkt. Przeciągaj w obu osiach. Prawy klik lub Delete usuwa punkt. Ctrl+Z/Y cofa i ponawia.";
        System.Windows.Automation.AutomationProperties.SetName(this, "Edytor krzywej EQ — częstotliwość i poziom");
    }
    private double Left => 35;
    private double Right => Math.Max(Left + 1, ActualWidth - 20);
    private double Top => 25;
    private double Bottom => Math.Max(Top + 1, ActualHeight - 28);
    private double X(double hz) => Left + (linearScale ? (hz - 20) / 19980 : Math.Log(hz / 20) / Math.Log(1000)) * (Right - Left);
    private double Y(float db) => Top + (30 - db) / 60 * (Bottom - Top);
    private double Hertz(double x)
    {
        double fraction = Math.Clamp((x - Left) / (Right - Left), 0, 1);
        return linearScale ? 20 + fraction * 19980 : 20 * Math.Pow(1000, fraction);
    }
    private float Gain(double y) => (float)Math.Clamp(30 - 60 * (y - Top) / (Bottom - Top), -30, 30);
    private Point Position(EqualizerPoint point) => new(X(point.Hertz), Y(point.Gain));
    private void Notify(bool changed = true)
    {
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }
    private void Remember()
    {
        undo.Push(settings);
        redo.Clear();
    }
    private void Replace(EqualizerPoint[] points, int index)
    {
        settings = settings with { Curve = points };
        selected = Math.Clamp(index, 0, points.Length - 1);
        Notify();
    }
    internal void SelectPoint(int index) { selected = Math.Clamp(index, 0, PointCount - 1); Notify(false); }
    internal void EditSelected(double hz, float db, bool remember = true)
    {
        if (!double.IsFinite(hz) || !float.IsFinite(db)) return;
        var points = settings.Points.ToArray();
        // Neighbors are barriers: preserve the selected node and its index for the entire drag.
        double minimum = selected == 0 ? 20 : points[selected-1].Hertz + .01;
        double maximum = selected == points.Length-1 ? 20000 : points[selected+1].Hertz - .01;
        hz = Math.Clamp(hz, minimum, maximum);
        var next = new EqualizerPoint(hz, Math.Clamp(db,-30,30));
        if (next == points[selected]) return;
        if (remember) Remember();
        points[selected] = next;
        Replace(points, selected);
    }

    internal void AddPoint(double hz, float db)
    {
        if (!double.IsFinite(hz) || !float.IsFinite(db) || PointCount >= 256) return;
        hz = Math.Clamp(hz,20,20000); db = Math.Clamp(db,-30,30);
        var points = settings.Points.ToList();
        int near = points.FindIndex(p => Math.Abs(p.Hertz-hz)<.02);
        if (near >= 0) { SelectPoint(near); EditSelected(hz,db); return; }
        Remember();
        var point = new EqualizerPoint(hz,db); points.Add(point); points.Sort((a,b)=>a.Hertz.CompareTo(b.Hertz));
        Replace(points.ToArray(), points.IndexOf(point));
    }
    internal void DeleteSelected()
    {
        if (!CanDelete) return;
        Remember(); var points = settings.Points.ToList(); points.RemoveAt(selected); Replace(points.ToArray(),selected);
    }
    internal void Flatten()
    {
        Remember(); Replace(settings.Points.Select(p=>p with { Gain=0 }).ToArray(), selected);
    }
    internal void Invert()
    {
        Remember(); Replace(settings.Points.Select(p=>p with { Gain=-p.Gain }).ToArray(), selected);
    }
    internal void Undo()
    {
        if (!CanUndo) return;
        redo.Push(settings); settings=undo.Pop(); selected=Math.Min(selected,PointCount-1); Notify();
    }
    internal void Redo()
    {
        if (!CanRedo) return;
        undo.Push(settings); settings=redo.Pop(); selected=Math.Min(selected,PointCount-1); Notify();
    }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var muted = new SolidColorBrush(Color.FromRgb(138,153,173));
        var accent = new SolidColorBrush(Color.FromRgb(107,153,198));
        var line = new Pen(new SolidColorBrush(Color.FromRgb(41,52,66)),1);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(8,10,13)),null,new Rect(RenderSize),6,6);
        void Label(string text,double x,double y,Brush brush,double size=10) => dc.DrawText(
            new FormattedText(text,CultureInfo.InvariantCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),size,brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(x,y));
        foreach (int db in new[]{-30,-15,0,15,30})
        {
            double y=Y(db);
            if(showGrid || db==0) dc.DrawLine(line,new Point(Left,y),new Point(Right,y));
            Label(db>0 ? "+"+db : db.ToString(),2,y-7,muted);
        }
        double[] frequencies=linearScale ? [20,4000,8000,12000,16000,20000] : [20,50,100,200,500,1000,2000,5000,10000,20000];
        foreach(double hz in frequencies)
        {
            double x=X(hz);
            if(showGrid) dc.DrawLine(line,new Point(x,Top),new Point(x,Bottom));
            Label(hz>=1000 ? $"{hz/1000:0.#}k" : $"{hz:0}",x-10,Bottom+7,muted);
        }
        var points=settings.Points;
        var geometry=new StreamGeometry();
        using(var context=geometry.Open())
        {
            // The DSP interpolates in log frequency. Sample that same curve in either display scale.
            context.BeginFigure(new Point(Left,Y(points[0].Gain)),false,false);
            int band=0;
            for(int i=1;i<=512;i++)
            {
                double x=Left+(Right-Left)*i/512;
                double hz=Hertz(x);
                while(band<points.Length-2 && hz>points[band+1].Hertz)band++;
                double mix=Math.Clamp(Math.Log(hz/points[band].Hertz)/Math.Log(points[band+1].Hertz/points[band].Hertz),0,1);
                float gain=(float)(points[band].Gain+(points[band+1].Gain-points[band].Gain)*mix);
                context.LineTo(new Point(x,Y(gain)),true,false);
            }
        }
        dc.DrawGeometry(null,new Pen(accent,2),geometry);
        for(int i=0;i<points.Length;i++)dc.DrawEllipse(i==selected ? Brushes.White : accent,new Pen(accent,1),Position(points[i]),i==selected?5:4,i==selected?5:4);
        Label($"Punkt {selected+1}/{points.Length}   {SelectedPoint.Hertz:0.##} Hz   {SelectedPoint.Gain:+0.00;-0.00;0.00} dB",Left,3,accent,11);
    }
    private int Hit(Point position)
    {
        var points=settings.Points;
        int closest=Enumerable.Range(0,points.Length).MinBy(i=>(Position(points[i])-position).LengthSquared);
        return (Position(points[closest])-position).Length<=11 ? closest : -1;
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var position=e.GetPosition(this);
        if(position.X<Left || position.X>Right || position.Y<Top || position.Y>Bottom)return;
        int hit=Hit(position);
        if(hit<0) AddPoint(Hertz(position.X),Gain(position.Y));
        else { SelectPoint(hit); Remember(); }
        editing=true; CaptureMouse(); e.Handled=true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if(editing && IsMouseCaptured && e.LeftButton==MouseButtonState.Pressed)
        {
            Point position=e.GetPosition(this); EditSelected(Hertz(position.X),Gain(position.Y),false);
        }
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        editing=false; ReleaseMouseCapture(); base.OnMouseLeftButtonUp(e);
    }
    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e); Focus(); int hit=Hit(e.GetPosition(this));
        if(hit>=0) { SelectPoint(hit); DeleteSelected(); e.Handled=true; }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool control=(Keyboard.Modifiers & ModifierKeys.Control)!=0;
        double step=(Keyboard.Modifiers & ModifierKeys.Shift)!=0 ? .001 : .01;
        switch(e.Key)
        {
            case Key.Z when control: Undo(); break;
            case Key.Y when control: Redo(); break;
            case Key.Delete: case Key.Back: DeleteSelected(); break;
            case Key.Left: EditSelected(SelectedPoint.Hertz/(1+step),SelectedPoint.Gain); break;
            case Key.Right: EditSelected(SelectedPoint.Hertz*(1+step),SelectedPoint.Gain); break;
            case Key.Up: EditSelected(SelectedPoint.Hertz,SelectedPoint.Gain+(float)(step*50)); break;
            case Key.Down: EditSelected(SelectedPoint.Hertz,SelectedPoint.Gain-(float)(step*50)); break;
            case Key.Home: EditSelected(SelectedPoint.Hertz,0); break;
            case Key.PageUp: SelectPoint(selected-1); break;
            case Key.PageDown: SelectPoint(selected+1); break;
            default: base.OnKeyDown(e); return;
        }
        e.Handled=true;
    }
}
