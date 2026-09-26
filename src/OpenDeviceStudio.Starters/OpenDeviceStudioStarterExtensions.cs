using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenDeviceStudio.Abstractions.Diagnostics;
using OpenDeviceStudio.Abstractions.Events;
using OpenDeviceStudio.Abstractions.Storage;
using OpenDeviceStudio.Abstractions.Transports;
using OpenDeviceStudio.Acquisition;
using OpenDeviceStudio.Connections;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Control.State;
using OpenDeviceStudio.Diagnostics;
using OpenDeviceStudio.Events;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Observability;
using OpenDeviceStudio.Storage.FileSystem;
using OpenDeviceStudio.Transport.Serial;
using OpenDeviceStudio.Transport.Simulator;
using OpenDeviceStudio.Transport.Tcp;
using OpenDeviceStudio.Workflows;

namespace OpenDeviceStudio.Starters;

public static class OpenDeviceStudioStarterExtensions
{
    public static OpenDeviceStudioApplicationBuilder AddOpenDeviceStudioDefaults(this OpenDeviceStudioApplicationBuilder builder)
    {
        builder.AddOpenDeviceStudio();
        builder.Services.AddOpenDeviceStudioConnections();
        builder.Services.AddOpenDeviceStudioAcquisition();
        builder.Services.TryAddSingleton<WorkflowRunner>();
        builder.Services.TryAddSingleton<IEventBus, EventBus>();
        builder.Services.TryAddSingleton<IAlarmService, AlarmService>();
        builder.Services.TryAddSingleton<HealthService>();
        builder.AddConfiguredOpenDeviceStudioObservability();
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddAcquisitionRuntime(this OpenDeviceStudioApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOpenDeviceStudioAcquisition();
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddCommandDispatcher<TCommand, TResult>(
        this OpenDeviceStudioApplicationBuilder builder,
        BoundedCommandDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOpenDeviceStudioCommandDispatcher<TCommand, TResult>(options);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddControlResourceArbitration(
        this OpenDeviceStudioApplicationBuilder builder,
        int maxSharedReadersPerResource = 4)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOpenDeviceStudioControlResourceArbiter(maxSharedReadersPerResource);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddDeviceState<TState>(
        this OpenDeviceStudioApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOpenDeviceStudioDeviceState<TState>();
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddDevicePolling<TState>(
        this OpenDeviceStudioApplicationBuilder builder,
        Func<IServiceProvider, IEnumerable<DevicePollGroup<TState>>> groupFactory,
        DevicePollRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddOpenDeviceStudioDevicePolling(groupFactory, options);
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder AddSimulatorTransport(this OpenDeviceStudioApplicationBuilder builder, string name = "default")
    {
        return builder.AddOpenDeviceStudioTransport(_ => new SimulatorTransport(name));
    }

    public static OpenDeviceStudioApplicationBuilder AddSerialTransport(this OpenDeviceStudioApplicationBuilder builder, SerialTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        return builder.AddOpenDeviceStudioTransport(_ => new SerialTransport(options));
    }

    public static OpenDeviceStudioApplicationBuilder AddTcpTransport(this OpenDeviceStudioApplicationBuilder builder, TcpTransportOptions options)
    {
        builder.Services.AddSingleton(options);
        return builder.AddOpenDeviceStudioTransport(_ => new TcpTransport(options));
    }

    public static OpenDeviceStudioApplicationBuilder AddFileSystemStorage(this OpenDeviceStudioApplicationBuilder builder, string directory)
    {
        builder.Services.AddSingleton<IKeyValueStore>(_ => new JsonFileKeyValueStore(directory));
        return builder;
    }
}
