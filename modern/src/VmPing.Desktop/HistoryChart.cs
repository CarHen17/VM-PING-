using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using VmPing.Core;
namespace VmPing.Desktop;

// Latency over time: average line, min–max band and loss bars along the bottom. Hover shows the slot details.
public sealed class HistoryChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<SampleBucket>> BucketsProperty =
        AvaloniaProperty.Register<HistoryChart, IReadOnlyList<SampleBucket>>(nameof(Buckets), []);
    public static readonly StyledProperty<DateTimeOffset> FromProperty = AvaloniaProperty.Register<HistoryChart, DateTimeOffset>(nameof(From));
    public static readonly StyledProperty<DateTimeOffset> ToProperty = AvaloniaProperty.Register<HistoryChart, DateTimeOffset>(nameof(To));
    public IReadOnlyList<SampleBucket> Buckets { get => GetValue(BucketsProperty); set => SetValue(BucketsProperty, value); }
    public DateTimeOffset From { get => GetValue(FromProperty); set => SetValue(FromProperty, value); }
    public DateTimeOffset To { get => GetValue(ToProperty); set => SetValue(ToProperty, value); }
    static HistoryChart() => AffectsRender<HistoryChart>(BucketsProperty, FromProperty, ToProperty);
    private static readonly IPen GridPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(40, 140, 150, 180)), 1);
    private static readonly IPen LinePen = new ImmutablePen(new ImmutableSolidColorBrush(Color.Parse("#8F83F7")), 1.6, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
    private static readonly IBrush BandBrush = new ImmutableSolidColorBrush(Color.FromArgb(46, 143, 131, 247));
    private static readonly IBrush LossBrush = new ImmutableSolidColorBrush(Color.Parse("#EA7185"));
    private static readonly IBrush CursorBrush = new ImmutableSolidColorBrush(Color.FromArgb(70, 160, 170, 200));
    private static readonly IBrush TextBrush = new ImmutableSolidColorBrush(Color.Parse("#929DB2"));
    private const double Left = 48, Bottom = 22, LossHeight = 16;
    private double? hoverX;
    public event Action<SampleBucket?>? Hovered;
    protected override void OnPointerMoved(PointerEventArgs e) { base.OnPointerMoved(e); hoverX = e.GetPosition(this).X; Hovered?.Invoke(BucketAt(hoverX.Value)); InvalidateVisual(); }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); hoverX = null; Hovered?.Invoke(null); InvalidateVisual(); }
    private double Plot => Math.Max(1, Bounds.Width - Left - 8);
    private double X(DateTimeOffset time) => Left + (time - From).Ticks / (double)Math.Max(1, (To - From).Ticks) * Plot;
    private SampleBucket? BucketAt(double x) => Buckets.FirstOrDefault(b => x >= X(b.Start) && x < Math.Max(X(b.End), X(b.Start) + 2));
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        // A transparent fill makes the whole area hit-testable for hover.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Bounds.Width <= Left + 10 || Bounds.Height <= Bottom + LossHeight + 20 || To <= From) return;
        var top = 8.0;
        var chartBottom = Bounds.Height - Bottom - LossHeight - 4;
        var height = chartBottom - top;
        var buckets = Buckets;
        var maximum = Math.Max(1, buckets.Select(b => b.Maximum ?? 0).DefaultIfEmpty(0).Max());
        // Round the scale to a readable value.
        var step = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        maximum = Math.Ceiling(maximum / step) * step;
        double Y(double value) => chartBottom - value / maximum * height;
        for (var i = 0; i <= 4; i++)
        {
            var value = maximum * i / 4;
            var y = Y(value);
            context.DrawLine(GridPen, new Point(Left, y), new Point(Left + Plot, y));
            Text(context, $"{value:0.#}", new Point(4, y - 7), 10);
        }
        var span = To - From;
        var format = span <= TimeSpan.FromHours(6) ? "HH:mm" : span <= TimeSpan.FromDays(2) ? "dd HH:mm" : "dd/MM";
        for (var i = 0; i <= 6; i++)
        {
            var time = From + span * i / 6;
            Text(context, time.ToLocalTime().ToString(format, CultureInfo.InvariantCulture), new Point(Math.Min(X(time) - 14, Bounds.Width - 60), Bounds.Height - Bottom + 6), 10);
        }
        Point? previous = null;
        foreach (var bucket in buckets)
        {
            var x0 = X(bucket.Start); var x1 = Math.Max(X(bucket.End), x0 + 1);
            if (bucket.Minimum is double min && bucket.Maximum is double max)
                context.FillRectangle(BandBrush, new Rect(x0, Y(max), x1 - x0, Math.Max(1, Y(min) - Y(max))));
            if (bucket.LossPercent > 0 || bucket.Errors > 0)
            {
                var loss = Math.Max(bucket.LossPercent, bucket.Sent == 0 ? 0 : 100d * bucket.Errors / bucket.Sent);
                var bar = Math.Max(2, LossHeight * loss / 100);
                context.FillRectangle(LossBrush, new Rect(x0, chartBottom + 4 + LossHeight - bar, x1 - x0, bar));
            }
            if (bucket.Average is double average)
            {
                var point = new Point((x0 + x1) / 2, Y(average));
                if (previous is Point start && point.X - start.X < Plot / 40) context.DrawLine(LinePen, start, point);
                previous = point;
            }
            else previous = null;
        }
        if (hoverX is double hx && hx >= Left) context.FillRectangle(CursorBrush, new Rect(hx, top, 1, chartBottom - top));
    }
    private static void Text(DrawingContext context, string text, Point at, double size) =>
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, size, TextBrush), at);
}
