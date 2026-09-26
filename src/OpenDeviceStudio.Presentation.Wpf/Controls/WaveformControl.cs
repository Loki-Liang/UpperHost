using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenDeviceStudio.Abstractions.Presentation;

namespace OpenDeviceStudio.Presentation.Wpf.Controls;

public sealed class WaveformControl : Control
{
    public static readonly DependencyProperty SnapshotProperty =
        DependencyProperty.Register(
            nameof(Snapshot),
            typeof(WaveformRenderSnapshot),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineThicknessProperty =
        DependencyProperty.Register(
            nameof(LineThickness),
            typeof(double),
            typeof(WaveformControl),
            new FrameworkPropertyMetadata(
                1.0,
                FrameworkPropertyMetadataOptions.AffectsRender,
                null,
                CoerceLineThickness));

    private long _generation;
    private long _viewportRevision;

    public WaveformRenderSnapshot? Snapshot
    {
        get => (WaveformRenderSnapshot?)GetValue(SnapshotProperty);
        set => SetValue(SnapshotProperty, value);
    }

    public double LineThickness
    {
        get => (double)GetValue(LineThicknessProperty);
        set => SetValue(LineThicknessProperty, value);
    }

    public bool ApplySnapshot(WaveformRenderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        VerifyAccess();

        if (snapshot.Generation < _generation)
            return false;

        if (snapshot.Generation == _generation &&
            snapshot.ViewportRevision < _viewportRevision)
        {
            return false;
        }

        _generation = snapshot.Generation;
        _viewportRevision = snapshot.ViewportRevision;
        Snapshot = snapshot;
        return true;
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var snapshot = Snapshot;
        if (snapshot is null ||
            snapshot.Series.Count == 0 ||
            ActualWidth <= 0 ||
            ActualHeight <= 0)
        {
            return;
        }

        var startTicks = snapshot.Viewport.Start.UtcDateTime.Ticks;
        var endTicks = snapshot.Viewport.End.UtcDateTime.Ticks;
        var durationTicks = Math.Max(1, endTicks - startTicks);
        var laneHeight = ActualHeight / snapshot.Series.Count;
        var pen = new Pen(
            Foreground ?? SystemColors.HighlightBrush,
            LineThickness);

        if (pen.CanFreeze)
            pen.Freeze();

        for (var seriesIndex = 0; seriesIndex < snapshot.Series.Count; seriesIndex++)
        {
            var series = snapshot.Series[seriesIndex];
            if (series.Points.Count == 0)
                continue;

            var minimum = series.VisibleMinimum ?? -1;
            var maximum = series.VisibleMaximum ?? 1;
            if (maximum <= minimum)
            {
                var center = minimum;
                minimum = center - 0.5;
                maximum = center + 0.5;
            }

            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                var hasFigure = false;
                for (var pointIndex = 0; pointIndex < series.Points.Count; pointIndex++)
                {
                    var point = series.Points[pointIndex];
                    var relativeTicks = Math.Clamp(
                        point.Timestamp.UtcDateTime.Ticks - startTicks,
                        0,
                        durationTicks);
                    var x = relativeTicks * ActualWidth / durationTicks;
                    var normalized = (point.Value - minimum) / (maximum - minimum);
                    var y = seriesIndex * laneHeight +
                        (1.0 - Math.Clamp(normalized, 0, 1)) * laneHeight;
                    var renderPoint = new Point(x, y);

                    if (!hasFigure || point.BreakBefore)
                    {
                        context.BeginFigure(renderPoint, isFilled: false, isClosed: false);
                        hasFigure = true;
                    }
                    else
                    {
                        context.LineTo(renderPoint, isStroked: true, isSmoothJoin: false);
                    }
                }
            }

            if (geometry.CanFreeze)
                geometry.Freeze();

            drawingContext.DrawGeometry(
                null,
                pen,
                geometry);
        }
    }

    private static object CoerceLineThickness(
        DependencyObject dependencyObject,
        object baseValue)
    {
        var value = (double)baseValue;
        return double.IsFinite(value) && value > 0 ? value : 1.0;
    }
}
