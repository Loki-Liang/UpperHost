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
        builder.AddFileSystemRawRecording(
            new FileSystemRawRecorderOptions(Path.Combine("data", "raw")));
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

    public static OpenDeviceStudioApplicationBuilder AddWorkflowExecutionRuntime(
        this OpenDeviceStudioApplicationBuilder builder,
        int maxSharedReadersPerResource = 4,
        WorkflowJournalFailurePolicy journalFailurePolicy = WorkflowJournalFailurePolicy.FailExecution)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddOpenDeviceStudioControlResourceArbiter(
            maxSharedReadersPerResource);
        builder.Services.TryAddSingleton<WorkflowExecutionCoordinator>(sp =>
            new WorkflowExecutionCoordinator(
                sp.GetRequiredService<ICommandResourceArbiter>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetService<IWorkflowExecutionJournal>(),
                journalFailurePolicy));
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

    public static OpenDeviceStudioApplicationBuilder AddFileSystemRawRecording(
        this OpenDeviceStudioApplicationBuilder builder,
        FileSystemRawRecorderOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        builder.Services.RemoveAll<IRawRecorderFactory>();
        builder.Services.AddSingleton<IRawRecorderFactory>(sp =>
            new FileSystemRawRecorderFactory(
                options,
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return builder;
    }

    public static OpenDeviceStudioApplicationBuilder DisableRawRecording(
        this OpenDeviceStudioApplicationBuilder builder,
        string reason)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        builder.Services.RemoveAll<IRawRecorderFactory>();
        builder.Services.AddSingleton<IRawRecorderFactory>(
            new DisabledRawRecorderFactory(reason));
        return builder;
    }

    private sealed class DisabledRawRecorderFactory(string reason) : IRawRecorderFactory
    {
        public bool IsEnabled => false;
        public string DisabledReason { get; } = reason;
        public IRawRecorder Create() =>
            throw new InvalidOperationException("Raw recording is disabled.");
    }
}
