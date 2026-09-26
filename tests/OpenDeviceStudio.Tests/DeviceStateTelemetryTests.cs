using System.Diagnostics.Metrics;
using Microsoft.Extensions.Time.Testing;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Control.State;

namespace OpenDeviceStudio.Tests;

public sealed class DeviceStateTelemetryTests
{
    [Fact]
    public void Snapshot_and_epoch_metrics_use_only_low_cardinality_tags()
    {
        var observed = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName &&
                instrument.Name.StartsWith("opendevicestudio.control.", StringComparison.Ordinal))
                meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, state) =>
        {
            if (instrument.Name is "opendevicestudio.control.snapshot.observations" or
                "opendevicestudio.control.epoch.changes")
            {
                Interlocked.Increment(ref observed);
                Assert.DoesNotContain(tags.ToArray(), tag =>
                    tag.Key.Contains("device", StringComparison.OrdinalIgnoreCase) ||
                    tag.Key.Contains("parameter", StringComparison.OrdinalIgnoreCase) ||
                    tag.Key.Contains("serial", StringComparison.OrdinalIgnoreCase));
            }
        });
        listener.Start();

        var time = new FakeTimeProvider();
        using var adapter = new AsyncDisposableAdapter<DeviceSnapshotStore<int>>(
            new DeviceSnapshotStore<int>(time));
        adapter.Value.AdvanceConnectionEpoch("device-secret-123", 1);
        adapter.Value.Apply(new DeviceObservation<int>(
            "device-secret-123",
            new DeviceStatePartitionKey("parameter:private-key"),
            1,
            DeviceObservationSource.Push,
            time.GetTimestamp(),
            42));

        Assert.True(Volatile.Read(ref observed) >= 1);
    }

    private sealed class AsyncDisposableAdapter<T>(T value) : IDisposable
        where T : IAsyncDisposable
    {
        public T Value { get; } = value;
        public void Dispose() => Value.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
