using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Abstractions.Diagnostics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Diagnostics;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Observability;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task Command_runtime_emits_metric_and_activity()
    {
        long commandMeasurements = 0;
        KeyValuePair<string, object?>[] commandTags = [];
        Activity? stopped = null;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "opendevicestudio.command.executions")
                return;

            Interlocked.Add(ref commandMeasurements, measurement);
            commandTags = tags.ToArray();
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenDeviceStudioTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "opendevicestudio.command.execute")
                    stopped = activity;
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var runtime = new CommandRuntime<TestCommand, string>(new TestCommandTarget());
        var result = await runtime.ExecuteAsync(new TestCommand("run"));

        Assert.True(result.IsSuccess);
        Assert.True(Volatile.Read(ref commandMeasurements) >= 1);
        Assert.NotNull(stopped);
        Assert.Equal(result.ExecutionId, stopped!.GetTagItem("opendevicestudio.command.id"));
        Assert.Equal("Succeeded", stopped.GetTagItem("opendevicestudio.result"));
        Assert.Contains(commandTags, tag =>
            tag.Key == "opendevicestudio.operation" && Equals(tag.Value, nameof(TestCommand)));
        Assert.DoesNotContain(commandTags, tag =>
            tag.Key is "opendevicestudio.command.id" or "opendevicestudio.session.id" or "opendevicestudio.connection.id");
    }

    [Fact]
    public async Task File_logging_writes_structured_scope()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opendevicestudio-observability-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
            var options = new OpenDeviceStudioObservabilityOptions
            {
                ServiceName = "OpenDeviceStudio.Tests",
                ServiceVersion = "1.0.0"
            };
            options.FileLogging.Enabled = true;
            options.FileLogging.Path = Path.Combine(directory, "opendevicestudio-.json");
            options.FileLogging.RetainedFileCountLimit = 2;
            options.FileLogging.FileSizeLimitBytes = 1024 * 1024;

            builder.AddOpenDeviceStudioObservability(options);

            var app = builder.Build();
            await app.StartAsync();

            var logger = app.Services.GetRequiredService<ILogger<ObservabilityTests>>();
            using (logger.BeginOpenDeviceStudioScope(new OpenDeviceStudioLogContext(
                DeviceId: "device-1",
                ConnectionId: "connection-1",
                SessionId: "session-1",
                CommandId: "command-1",
                Operation: "test")))
            {
                logger.LogInformation("observability structured log probe");
                logger.LogInformation(
                    "credential probe {Password} {ApiToken}",
                    "super-secret-value",
                    "token-secret-value");
            }

            Assert.Contains(
                app.Services.GetServices<IHealthProbe>(),
                probe => probe.Name == "opendevicestudio.logging.async_buffer");

            await app.DisposeAsync();

            var files = Directory.GetFiles(directory, "opendevicestudio-*.json");
            Assert.NotEmpty(files);
            var contents = string.Join(
                Environment.NewLine,
                files.Select(File.ReadAllText));
            Assert.Contains("observability structured log probe", contents, StringComparison.Ordinal);
            Assert.Contains("DeviceId", contents, StringComparison.Ordinal);
            Assert.Contains("device-1", contents, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("super-secret-value", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("token-secret-value", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Transport_health_reports_faulted_transport()
    {
        await using var transport = new FakeTransport(TransportState.Faulted);
        var probe = new TransportHealthProbe([transport]);

        var report = await probe.CheckAsync();

        Assert.Equal(HealthStatus.Unhealthy, report.Status);
        Assert.Equal(1, report.Data!["Faulted"]);
    }

    [Fact]
    public async Task Default_starter_registers_observability_health()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Simulator";
        builder.AddOpenDeviceStudioApplication();

        var app = builder.Build();
        await app.StartAsync();

        try
        {
            var probes = app.Services.GetServices<IHealthProbe>().ToArray();
            Assert.Contains(probes, probe => probe is TransportHealthProbe);

            var health = app.Services.GetRequiredService<HealthService>();
            var reports = await health.CheckAllAsync();
            Assert.Contains(reports, report => report.Name == "opendevicestudio.transports");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Invalid_otlp_endpoint_fails_fast()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        var options = new OpenDeviceStudioObservabilityOptions
        {
            ServiceName = "OpenDeviceStudio.Tests",
            ServiceVersion = "1.0.0"
        };
        options.Otlp.Enabled = true;
        options.Otlp.Endpoint = "not-an-absolute-uri";

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddOpenDeviceStudioObservability(options));
    }


    [Fact]
    public async Task Active_alarm_metric_balances_the_same_series()
    {
        var measurements = new List<(long Value, string? Severity)>();

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name != "opendevicestudio.alarms.active")
                return;

            var severity = tags.ToArray()
                .FirstOrDefault(tag => tag.Key == "opendevicestudio.alarm.severity")
                .Value as string;
            measurements.Add((measurement, severity));
        });
        meterListener.Start();

        var alarms = new AlarmService();
        var alarm = await alarms.RaiseAsync(
            "test",
            "alarm",
            "test alarm",
            AlarmSeverity.Warning);
        Assert.True(await alarms.ClearAsync(alarm.Id));

        Assert.Contains(measurements, item =>
            item.Value == 1 && item.Severity == AlarmSeverity.Warning.ToString());
        Assert.Contains(measurements, item =>
            item.Value == -1 && item.Severity == AlarmSeverity.Warning.ToString());
    }

    [Fact]
    public async Task Receive_failure_emits_failure_metric_and_error_activity()
    {
        long failures = 0;
        Activity? stopped = null;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == OpenDeviceStudioTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "opendevicestudio.transport.failures")
                Interlocked.Add(ref failures, measurement);
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == OpenDeviceStudioTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "opendevicestudio.transport.receive")
                    stopped = activity;
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        await using var transport = new ObservedTransport(new FailingReceiveTransport());

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in transport.ReceiveAsync())
            {
            }
        });

        Assert.Equal(1, Volatile.Read(ref failures));
        Assert.NotNull(stopped);
        Assert.Equal(ActivityStatusCode.Error, stopped!.Status);
        Assert.Equal(
            typeof(InvalidOperationException).FullName,
            stopped.GetTagItem("error.type"));
    }

    [Fact]
    public async Task Closed_transport_is_degraded()
    {
        await using var transport = new FakeTransport(TransportState.Closed);
        var probe = new TransportHealthProbe([transport]);

        var report = await probe.CheckAsync();

        Assert.Equal(HealthStatus.Degraded, report.Status);
        Assert.Equal(1, report.Data!["Closed"]);
    }

    [Fact]
    public void Configured_observability_binds_typed_options()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        builder.Configuration["OpenDeviceStudio:Observability:Logging:File:AsyncBufferSize"] = "2048";
        builder.Configuration["OpenDeviceStudio:Observability:Otlp:TraceSampleRatio"] = "0.25";

        builder.AddConfiguredOpenDeviceStudioObservability();
        var app = builder.Build();

        var options = app.Services
            .GetRequiredService<IOptions<OpenDeviceStudioObservabilityOptions>>()
            .Value;

        Assert.Equal(2048, options.FileLogging.AsyncBufferSize);
        Assert.Equal(0.25, options.Otlp.TraceSampleRatio);
    }

    [Fact]
    public void Custom_transport_registration_uses_canonical_observed_pipeline()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        builder.AddOpenDeviceStudioTransport(_ => new FakeTransport(TransportState.Closed));
        var app = builder.Build();

        Assert.IsType<ObservedTransport>(
            app.Services.GetRequiredService<ITransport>());
    }

    private sealed record TestCommand(string Name);

    private sealed class TestCommandTarget : ICommandable<TestCommand, string>
    {
        public Task<string> ExecuteAsync(
            TestCommand command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(command.Name);
        }
    }


    private sealed class FailingReceiveTransport : ITransport
    {
        public TransportEndpoint Endpoint { get; } = new("fake", "failing");
        public TransportState State => TransportState.Open;

        public Task OpenAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task CloseAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (!cancellationToken.IsCancellationRequested)
                throw new InvalidOperationException("receive failed");
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransport(TransportState state) : ITransport
    {
        public TransportEndpoint Endpoint { get; } = new("fake", "test");
        public TransportState State { get; private set; } = state;

        public Task OpenAsync(CancellationToken cancellationToken = default)
        {
            State = TransportState.Open;
            return Task.CompletedTask;
        }

        public Task CloseAsync(CancellationToken cancellationToken = default)
        {
            State = TransportState.Closed;
            return Task.CompletedTask;
        }

        public ValueTask SendAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
