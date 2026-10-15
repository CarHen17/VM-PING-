using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
namespace VmPing.Desktop;

public sealed class LatencyChart : Control
{
    public static readonly StyledProperty<double?[]> SamplesProperty = AvaloniaProperty.Register<LatencyChart, double?[]>(nameof(Samples), []);
    public double?[] Samples { get => GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    static LatencyChart() => AffectsRender<LatencyChart>(SamplesProperty);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(28, 140, 150, 180)), 1);
    private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8F83F7")), 1.8, lineCap: PenLineCap.Round);
    private static readonly IPen LossPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#EA7185")), 2);
    private static readonly IBrush FillBrush = new ImmutableSolidColorBrush(Color.FromArgb(22, 143, 131, 247));
    private static readonly IBrush DotBrush = new ImmutableSolidColorBrush(Color.Parse("#A49BFF"));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
        for (var row = 1; row <= 3; row++)
        {
            var y = Bounds.Height * row / 4;
            context.DrawLine(GridPen, new Point(0, y), new Point(Bounds.Width, y));
        }
        var samples = Samples;
        if (samples.Length == 0) return;
        var maximum = Math.Max(1, samples.Max() ?? 1) * 1.2;
        var step = Bounds.Width / 59;
        Point? previous = null;
        for (var index = 0; index < samples.Length; index++)
        {
            var x = index * step;
            if (samples[index] is not double value)
            {
                context.DrawLine(LossPen, new Point(x, Bounds.Height - 8), new Point(x, Bounds.Height - 2));
                previous = null;
                continue;
            }
            var point = new Point(x, Bounds.Height - 5 - value / maximum * (Bounds.Height - 10));
            if (previous is Point start)
            {
                var fill = new StreamGeometry();
                using (var geometry = fill.Open())
                {
                    geometry.BeginFigure(new Point(start.X, Bounds.Height), true);
                    geometry.LineTo(start); geometry.LineTo(point);
                    geometry.LineTo(new Point(point.X, Bounds.Height)); geometry.EndFigure(true);
                }
                context.DrawGeometry(FillBrush, null, fill);
                context.DrawLine(LinePen, start, point);
            }
            previous = point;
        }
        if (previous is Point last) context.DrawEllipse(DotBrush, null, last, 2.5, 2.5);
    }
}
