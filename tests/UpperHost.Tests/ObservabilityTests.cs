using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Observability;
using UpperHost.Abstractions.Transports;
using UpperHost.Control.Commands;
using UpperHost.Diagnostics;
using UpperHost.Hosting;
using UpperHost.Observability;
using UpperHost.Starters;

namespace UpperHost.Tests;

public sealed class ObservabilityTests
{
    [Fact]
    public async Task Command_runtime_emits_metric_and_activity()
    {
        long commandMeasurements = 0;
        Activity? stopped = null;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == UpperHostTelemetry.InstrumentationName)
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "upperhost.command.executions")
                Interlocked.Add(ref commandMeasurements, measurement);
        });
        meterListener.Start();

        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == UpperHostTelemetry.InstrumentationName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity =>
            {
                if (activity.OperationName == "upperhost.command.execute")
                    stopped = activity;
            }
        };
        ActivitySource.AddActivityListener(activityListener);

        var runtime = new CommandRuntime<TestCommand, string>(new TestCommandTarget());
        var result = await runtime.ExecuteAsync(new TestCommand("run"));

        Assert.True(result.IsSuccess);
        Assert.True(Volatile.Read(ref commandMeasurements) >= 1);
        Assert.NotNull(stopped);
        Assert.Equal(result.ExecutionId, stopped!.GetTagItem("upperhost.command.id"));
        Assert.Equal("Succeeded", stopped.GetTagItem("upperhost.result"));
    }

    [Fact]
    public async Task File_logging_writes_structured_scope()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "upperhost-observability-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var builder = UpperHostApplication.CreateBuilder().AddUpperHost();
            var options = new UpperHostObservabilityOptions
            {
                ServiceName = "UpperHost.Tests",
                ServiceVersion = "1.0.0"
            };
            options.FileLogging.Enabled = true;
            options.FileLogging.Path = Path.Combine(directory, "upperhost-.json");
            options.FileLogging.RetainedFileCountLimit = 2;
            options.FileLogging.FileSizeLimitBytes = 1024 * 1024;

            builder.AddUpperHostObservability(options);

            var app = builder.Build();
            await app.StartAsync();

            var logger = app.Services.GetRequiredService<ILogger<ObservabilityTests>>();
            using (logger.BeginUpperHostScope(new UpperHostLogContext(
                DeviceId: "device-1",
                ConnectionId: "connection-1",
                SessionId: "session-1",
                CommandId: "command-1",
                Operation: "test")))
            {
                logger.LogInformation("observability structured log probe");
            }

            await app.DisposeAsync();

            var files = Directory.GetFiles(directory, "upperhost-*.json");
            Assert.NotEmpty(files);
            var contents = string.Join(
                Environment.NewLine,
                files.Select(File.ReadAllText));
            Assert.Contains("observability structured log probe", contents, StringComparison.Ordinal);
            Assert.Contains("DeviceId", contents, StringComparison.Ordinal);
            Assert.Contains("device-1", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("Password", contents, StringComparison.OrdinalIgnoreCase);
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
        var builder = UpperHostApplication.CreateBuilder();
        builder.Configuration["UpperHost:Transport:Type"] = "Simulator";
        builder.AddUpperHostApplication();

        var app = builder.Build();
        await app.StartAsync();

        try
        {
            var probes = app.Services.GetServices<IHealthProbe>().ToArray();
            Assert.Contains(probes, probe => probe is TransportHealthProbe);

            var health = app.Services.GetRequiredService<HealthService>();
            var reports = await health.CheckAllAsync();
            Assert.Contains(reports, report => report.Name == "upperhost.transports");
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    [Fact]
    public void Invalid_otlp_endpoint_fails_fast()
    {
        var builder = UpperHostApplication.CreateBuilder().AddUpperHost();
        var options = new UpperHostObservabilityOptions
        {
            ServiceName = "UpperHost.Tests",
            ServiceVersion = "1.0.0"
        };
        options.Otlp.Enabled = true;
        options.Otlp.Endpoint = "not-an-absolute-uri";

        Assert.Throws<InvalidOperationException>(() =>
            builder.AddUpperHostObservability(options));
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
