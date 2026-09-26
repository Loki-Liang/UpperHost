using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UpperHost.Abstractions.Diagnostics;
using UpperHost.Abstractions.Events;
using UpperHost.Abstractions.Storage;
using UpperHost.Abstractions.Transports;
using UpperHost.Connections;
using UpperHost.Control.Scheduling;
using UpperHost.Control.State;
using UpperHost.Diagnostics;
using UpperHost.Events;
using UpperHost.Hosting;
using UpperHost.Observability;
using UpperHost.Storage.FileSystem;
using UpperHost.Transport.Serial;
using UpperHost.Transport.Simulator;
using UpperHost.Transport.Tcp;
using UpperHost.Workflows;

namespace UpperHost.Starters;

public static class UpperHostStarterExtensions
{
    public static UpperHostApplicationBuilder AddUpperHostDefaults(this UpperHostApplicationBuilder builder)
    {
        builder.AddUpperHost();
        builder.Services.AddUpperHostConnections();
        builder.Services.TryAddSingleton<WorkflowRunner>();
        builder.Services.TryAddSingleton<IEventBus, EventBus>();
        builder.Services.TryAddSingleton<IAlarmService, AlarmService>();
        builder.Services.TryAddSingleton<HealthService>();
        builder.AddConfiguredUpperHostObservability();
        return builder;
    }

    public static UpperHostApplicationBuilder AddCommandDispatcher<TCommand, TResult>(
        this UpperHostApplicationBuilder builder,
        BoundedCommandDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddUpperHostCommandDispatcher<TCommand, TResult>(options);
        return builder;
    }

    public static UpperHostApplicationBuilder AddControlResourceArbitration(
        this UpperHostApplicationBuilder builder,
        int maxSharedReadersPerResource = 4)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddUpperHostControlResourceArbiter(maxSharedReadersPerResource);
        return builder;
    }

    public static UpperHostApplicationBuilder AddDeviceState<TState>(
        this UpperHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddUpperHostDeviceState<TState>();
        return builder;
    }

    public static UpperHostApplicationBuilder AddDevicePolling<TState>(
        this UpperHostApplicationBuilder builder,
        Func<IServiceProvider, IEnumerable<DevicePollGroup<TState>>> groupFactory,
        DevicePollRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddUpperHostDevicePolling(groupFactory, options);
        return builder;
    }

    public static UpperHostApplicationBuilder AddSimulatorTransport(this UpperHostApplicationBuilder builder, string name = "default")
    {
        return builder.AddUpperHostTransport(_ => new SimulatorTransport(name));
    }

    public static UpperHostApplicationBuilder AddSerialTransport(this UpperHostApplicationBuilder builder, SerialTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        return builder.AddUpperHostTransport(_ => new SerialTransport(options));
    }

    public static UpperHostApplicationBuilder AddTcpTransport(this UpperHostApplicationBuilder builder, TcpTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        return builder.AddUpperHostTransport(_ => new TcpTransport(options));
    }

    public static UpperHostApplicationBuilder AddFileSystemStorage(this UpperHostApplicationBuilder builder, string directory)
    {
        builder.Services.AddSingleton<IKeyValueStore>(_ => new JsonFileKeyValueStore(directory));
        return builder;
    }
}
