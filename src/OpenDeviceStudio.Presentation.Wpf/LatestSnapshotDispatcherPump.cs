using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Windows.Threading;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Presentation;

namespace OpenDeviceStudio.Presentation.Wpf;

public sealed class LatestSnapshotDispatcherPump : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action<WaveformRenderSnapshot> _render;
    private WaveformRenderSnapshot? _pending;
    private long _highestGeneration;
    private int _scheduled;
    private int _active = 1;
    private int _disposed;

    public LatestSnapshotDispatcherPump(
        Dispatcher dispatcher,
        Action<WaveformRenderSnapshot> render)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _render = render ?? throw new ArgumentNullException(nameof(render));
    }

    public bool IsActive => Volatile.Read(ref _active) != 0;

    public void SetActive(bool active)
    {
        Volatile.Write(ref _active, active ? 1 : 0);
        if (!active)
            Interlocked.Exchange(ref _pending, null);
    }

    public bool Post(WaveformRenderSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (Volatile.Read(ref _disposed) != 0 || !IsActive)
            return false;

        UpdateHighestGeneration(snapshot.Generation);
        if (snapshot.Generation < Volatile.Read(ref _highestGeneration))
        {
            WpfPresentationTelemetry.StaleSnapshots.Add(
                1,
                WpfPresentationTelemetry.SnapshotTags(snapshot));
            return false;
        }

        var replaced = Interlocked.Exchange(ref _pending, snapshot);
        if (replaced is not null)
        {
            WpfPresentationTelemetry.CoalescedSnapshots.Add(
                1,
                WpfPresentationTelemetry.SnapshotTags(snapshot));
        }

        Schedule();
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Interlocked.Exchange(ref _pending, null);
        Volatile.Write(ref _active, 0);
    }

    private void Schedule()
    {
        if (Volatile.Read(ref _disposed) != 0 || !IsActive)
            return;

        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
            return;

        try
        {
            _dispatcher.BeginInvoke(
                DispatcherPriority.Render,
                new Action(Drain));
        }
        catch (InvalidOperationException)
        {
            Volatile.Write(ref _scheduled, 0);
        }
    }

    private void Drain()
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !IsActive)
                return;

            var snapshot = Interlocked.Exchange(ref _pending, null);
            if (snapshot is null)
                return;

            if (snapshot.Generation < Volatile.Read(ref _highestGeneration))
            {
                WpfPresentationTelemetry.StaleSnapshots.Add(
                    1,
                    WpfPresentationTelemetry.SnapshotTags(snapshot));
                return;
            }

            _render(snapshot);
            WpfPresentationTelemetry.RenderedSnapshots.Add(
                1,
                WpfPresentationTelemetry.SnapshotTags(snapshot));

            var lag = DateTimeOffset.UtcNow - snapshot.BuiltAt;
            if (lag >= TimeSpan.Zero)
            {
                WpfPresentationTelemetry.DispatchLagMs.Record(
                    lag.TotalMilliseconds,
                    WpfPresentationTelemetry.SnapshotTags(snapshot));
            }
        }
        finally
        {
            Volatile.Write(ref _scheduled, 0);
            if (Volatile.Read(ref _pending) is not null)
                Schedule();
        }
    }

    private void UpdateHighestGeneration(long generation)
    {
        while (true)
        {
            var current = Volatile.Read(ref _highestGeneration);
            if (generation <= current)
                return;

            if (Interlocked.CompareExchange(
                    ref _highestGeneration,
                    generation,
                    current) == current)
            {
                return;
            }
        }
    }
}

internal static class WpfPresentationTelemetry
{
    private static readonly Meter Meter = OpenDeviceStudioTelemetry.Meter;

    public static readonly Counter<long> CoalescedSnapshots =
        Meter.CreateCounter<long>("opendevicestudio.presentation.wpf.coalesced_snapshots", "{snapshot}");

    public static readonly Counter<long> StaleSnapshots =
        Meter.CreateCounter<long>("opendevicestudio.presentation.wpf.stale_snapshots", "{snapshot}");

    public static readonly Counter<long> RenderedSnapshots =
        Meter.CreateCounter<long>("opendevicestudio.presentation.wpf.rendered_snapshots", "{snapshot}");

    public static readonly Histogram<double> DispatchLagMs =
        Meter.CreateHistogram<double>("opendevicestudio.presentation.wpf.dispatch_lag", "ms");

    public static TagList SnapshotTags(WaveformRenderSnapshot snapshot)
    {
        var tags = new TagList
        {
            { "stage.id", snapshot.Source.StageId }
        };
        return tags;
    }
}
