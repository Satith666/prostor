using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace WinPulse.App.Controls;

/// <summary>
/// График с заливкой: последние значения прижаты к правому краю.
/// Тип свойств — IReadOnlyList, а не массив: WPF не умеет задавать свойства-массивы внутри шаблонов.
/// Values — основная серия (с заливкой), Values2 — вторая линия (например, запись или отправка).
/// </summary>
public sealed class AreaChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(AreaChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Values2Property = DependencyProperty.Register(
        nameof(Values2), typeof(IReadOnlyList<double>), typeof(AreaChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(AreaChart), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(AreaChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinimumScaleProperty = DependencyProperty.Register(
        nameof(MinimumScale), typeof(double), typeof(AreaChart), new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(AreaChart), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty Stroke2Property = DependencyProperty.Register(
        nameof(Stroke2), typeof(Brush), typeof(AreaChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
        nameof(ShowGrid), typeof(bool), typeof(AreaChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(AreaChart), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillOpacityProperty = DependencyProperty.Register(
        nameof(FillOpacity), typeof(double), typeof(AreaChart), new FrameworkPropertyMetadata(0.16, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<double>? Values { get => (IReadOnlyList<double>?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public IReadOnlyList<double>? Values2 { get => (IReadOnlyList<double>?)GetValue(Values2Property); set => SetValue(Values2Property, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    /// <summary>Верх шкалы. NaN — подбирается по данным.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    /// <summary>Минимальный верх шкалы при автоподборе (чтобы шум не выглядел как пики).</summary>
    public double MinimumScale { get => (double)GetValue(MinimumScaleProperty); set => SetValue(MinimumScaleProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush Stroke2 { get => (Brush)GetValue(Stroke2Property); set => SetValue(Stroke2Property, value); }
    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public double FillOpacity { get => (double)GetValue(FillOpacityProperty); set => SetValue(FillOpacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 4 || h < 4) return;

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));

        if (ShowGrid)
        {
            var gridPen = new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot };
            gridPen.Freeze();
            for (int i = 1; i <= 3; i++)
            {
                double y = Math.Round(h * i / 4) + 0.5;
                dc.DrawLine(gridPen, new Point(0, y), new Point(w, y));
            }
        }

        double max = Maximum;
        if (double.IsNaN(max) || max <= 0)
        {
            max = MinimumScale;
            if (Values is not null) foreach (var v in Values) if (v > max) max = v;
            if (Values2 is not null) foreach (var v in Values2) if (v > max) max = v;
            max *= 1.15;
        }

        DrawSeries(dc, Values, Stroke, max, w, h, fill: true);
        DrawSeries(dc, Values2, Stroke2, max, w, h, fill: false);
    }

    private void DrawSeries(DrawingContext dc, IReadOnlyList<double>? values, Brush stroke, double max, double w, double h, bool fill)
    {
        if (values is null || values.Count < 2) return;
        int n = values.Count;
        int capacity = Math.Max(Capacity, n);
        double step = w / (capacity - 1);
        double x0 = w - (n - 1) * step;
        double Y(double v) => h - Math.Clamp(v / max, 0, 1) * (h - 1.5) - 0.75;

        if (fill)
        {
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(new Point(x0, h), true, true);
                for (int i = 0; i < n; i++) ctx.LineTo(new Point(x0 + i * step, Y(values[i])), true, false);
                ctx.LineTo(new Point(x0 + (n - 1) * step, h), true, false);
            }
            area.Freeze();
            // Заливка градиентом: у линии плотнее, к низу графика растворяется.
            Brush fillBrush;
            if (stroke is SolidColorBrush sb)
            {
                byte topAlpha = (byte)(255 * Math.Clamp(FillOpacity * 2.6, 0, 1));
                fillBrush = new LinearGradientBrush(
                    Color.FromArgb(topAlpha, sb.Color.R, sb.Color.G, sb.Color.B),
                    Color.FromArgb(0, sb.Color.R, sb.Color.G, sb.Color.B), 90);
            }
            else
            {
                fillBrush = new SolidColorBrush(Color.FromArgb(40, 128, 128, 128));
            }
            fillBrush.Freeze();
            dc.DrawGeometry(fillBrush, null, area);
        }

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(x0, Y(values[0])), false, false);
            for (int i = 1; i < n; i++) ctx.LineTo(new Point(x0 + i * step, Y(values[i])), true, true);
        }
        line.Freeze();
        var pen = new Pen(stroke, 2) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);
    }
}

/// <summary>
/// Круговой датчик: дуга от 12 часов по часовой стрелке, с подсветкой цвета дуги и плавным переходом между значениями.
/// </summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender, OnRingBrushChanged));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(10.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly DependencyProperty ShownProperty = DependencyProperty.Register(
        "Shown", typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private readonly System.Windows.Media.Effects.DropShadowEffect _glow = new()
    {
        ShadowDepth = 0,
        BlurRadius = 12,
        Opacity = 0.75,
        Color = Colors.DeepSkyBlue,
    };

    public RingGauge()
    {
        Effect = _glow;
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush RingBrush { get => (Brush)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }
    public Brush TrackBrush { get => (Brush)GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var gauge = (RingGauge)d;
        double target = e.NewValue is double v && !double.IsNaN(v) ? v : 0;
        var animation = new System.Windows.Media.Animation.DoubleAnimation(target, TimeSpan.FromMilliseconds(450))
        {
            EasingFunction = new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut },
        };
        gauge.BeginAnimation(ShownProperty, animation);
    }

    private static void OnRingBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is SolidColorBrush b) ((RingGauge)d)._glow.Color = b.Color;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        double t = Math.Max(1, Thickness);
        double r = (Math.Min(w, h) - t) / 2 - 2;
        if (r < 4) return;
        var center = new Point(w / 2, h / 2);

        var track = new Pen(TrackBrush, t);
        track.Freeze();
        dc.DrawEllipse(null, track, center, r, r);

        double max = Maximum > 0 ? Maximum : 100;
        double fraction = Math.Clamp((double)GetValue(ShownProperty) / max, 0, 1);
        if (fraction <= 0.001) return;

        var pen = new Pen(RingBrush, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        if (fraction >= 0.999)
        {
            dc.DrawEllipse(null, pen, center, r, r);
            return;
        }

        double angle = -Math.PI / 2 + fraction * 2 * Math.PI;
        var start = new Point(center.X, center.Y - r);
        var end = new Point(center.X + r * Math.Cos(angle), center.Y + r * Math.Sin(angle));
        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.ArcTo(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true, false);
        }
        arc.Freeze();
        dc.DrawGeometry(null, pen, arc);
    }
}

public sealed class StackSegment
{
    public StackSegment(double value, Brush brush)
    {
        Value = value;
        Brush = brush;
    }

    public double Value { get; }
    public Brush Brush { get; }
}

/// <summary>Полоса из частей (например, из чего состоит память).</summary>
public sealed class StackBar : FrameworkElement
{
    public static readonly DependencyProperty SegmentsProperty = DependencyProperty.Register(
        nameof(Segments), typeof(IReadOnlyList<StackSegment>), typeof(StackBar),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<StackSegment>? Segments
    {
        get => (IReadOnlyList<StackSegment>?)GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var segments = Segments;
        if (segments is null || segments.Count == 0 || w < 4 || h < 2) return;
        double total = segments.Sum(s => Math.Max(0, s.Value));
        if (total <= 0) return;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h), Math.Min(6, h / 2), Math.Min(6, h / 2)));
        double x = 0;
        foreach (var s in segments)
        {
            double sw = Math.Max(0, s.Value) / total * w;
            if (sw <= 0) continue;
            dc.DrawRectangle(s.Brush, null, new Rect(x, 0, Math.Max(0, sw - 1.5), h));
            x += sw;
        }
        dc.Pop();
    }
}

/// <summary>Простая полоса заполнения 0…1.</summary>
public sealed class FractionBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(FractionBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(FractionBar), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(FractionBar), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public Brush Track { get => (Brush)GetValue(TrackProperty); set => SetValue(TrackProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 2 || h < 1) return;
        double r = h / 2;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, 0, w, h), r, r);
        double fw = Math.Clamp(Value, 0, 1) * w;
        if (fw > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(0, 0, Math.Max(fw, h), h), r, r);
    }
}

/// <summary>Хронология по дням: столбики из критичных событий, ошибок и предупреждений.</summary>
public sealed class DaysChart : FrameworkElement
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(
        nameof(Days), typeof(IReadOnlyList<WinPulse.Core.Diagnostics.DayBucket>), typeof(DaysChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CriticalBrushProperty = DependencyProperty.Register(
        nameof(CriticalBrush), typeof(Brush), typeof(DaysChart), new FrameworkPropertyMetadata(Brushes.IndianRed, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ErrorBrushProperty = DependencyProperty.Register(
        nameof(ErrorBrush), typeof(Brush), typeof(DaysChart), new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WarningBrushProperty = DependencyProperty.Register(
        nameof(WarningBrush), typeof(Brush), typeof(DaysChart), new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyBrushProperty = DependencyProperty.Register(
        nameof(EmptyBrush), typeof(Brush), typeof(DaysChart), new FrameworkPropertyMetadata(Brushes.DimGray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LabelBrushProperty = DependencyProperty.Register(
        nameof(LabelBrush), typeof(Brush), typeof(DaysChart), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<WinPulse.Core.Diagnostics.DayBucket>? Days
    {
        get => (IReadOnlyList<WinPulse.Core.Diagnostics.DayBucket>?)GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public Brush CriticalBrush { get => (Brush)GetValue(CriticalBrushProperty); set => SetValue(CriticalBrushProperty, value); }
    public Brush ErrorBrush { get => (Brush)GetValue(ErrorBrushProperty); set => SetValue(ErrorBrushProperty, value); }
    public Brush WarningBrush { get => (Brush)GetValue(WarningBrushProperty); set => SetValue(WarningBrushProperty, value); }
    public Brush EmptyBrush { get => (Brush)GetValue(EmptyBrushProperty); set => SetValue(EmptyBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        var days = Days;
        if (days is null || days.Count == 0 || w < 20 || h < 30) return;

        const double labelHeight = 16;
        double chartH = h - labelHeight;
        int n = days.Count;
        double slot = w / n;
        double barW = Math.Max(2, slot - 3);
        int max = Math.Max(4, days.Max(d => d.Total));
        double pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        for (int i = 0; i < n; i++)
        {
            var d = days[i];
            double x = i * slot + (slot - barW) / 2;
            if (d.Total == 0)
            {
                dc.DrawRoundedRectangle(EmptyBrush, null, new Rect(x, chartH - 3, barW, 3), 1, 1);
            }
            else
            {
                double y = chartH;
                void Segment(int count, Brush brush)
                {
                    if (count <= 0) return;
                    double sh = Math.Max(3, count / (double)max * (chartH - 4));
                    y -= sh;
                    dc.DrawRoundedRectangle(brush, null, new Rect(x, y, barW, sh - 1), 1.5, 1.5);
                }
                Segment(d.Warnings, WarningBrush);
                Segment(d.Errors, ErrorBrush);
                Segment(d.Critical, CriticalBrush);
            }

            bool label = (n - 1 - i) % 7 == 0;
            if (label)
            {
                var text = new FormattedText(d.Day.ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture),
                    System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, LabelBrush, pixelsPerDip);
                double tx = Math.Clamp(i * slot + slot / 2 - text.Width / 2, 0, Math.Max(0, w - text.Width));
                dc.DrawText(text, new Point(tx, chartH + 3));
            }
        }
    }
}
