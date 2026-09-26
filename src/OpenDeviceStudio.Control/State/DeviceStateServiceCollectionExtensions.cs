using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Control.State;

public static class DeviceStateServiceCollectionExtensions
{
    public static IServiceCollection AddOpenDeviceStudioDeviceState<TState>(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.TryAddSingleton<DeviceControlStateRegistry>();
        services.TryAddSingleton<IDeviceConnectionEpochSource>(sp =>
            sp.GetRequiredService<DeviceControlStateRegistry>());
        services.TryAddSingleton<ICommandConnectionEpochValidator>(sp =>
            sp.GetRequiredService<DeviceControlStateRegistry>());

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(DeviceStateRegistrationMarker<TState>)))
            return services;

        services.AddSingleton<DeviceStateRegistrationMarker<TState>>();
        services.TryAddSingleton(sp =>
            new DeviceSnapshotStore<TState>(
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IDeviceEpochInvalidationSink>(sp =>
            sp.GetRequiredService<DeviceSnapshotStore<TState>>());

        return services;
    }

    public static IServiceCollection AddOpenDeviceStudioDevicePolling<TState>(
        this IServiceCollection services,
        Func<IServiceProvider, IEnumerable<DevicePollGroup<TState>>> groupFactory,
        DevicePollRuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(groupFactory);

        services.AddOpenDeviceStudioDeviceState<TState>();

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(DevicePollingRegistrationMarker<TState>)))
        {
            throw new InvalidOperationException(
                $"Polling for state type '{typeof(TState).FullName}' is already registered.");
        }

        services.AddSingleton<DevicePollingRegistrationMarker<TState>>();
        services.AddSingleton(sp =>
            new DevicePollRuntime<TState>(
                groupFactory(sp),
                sp.GetRequiredService<DeviceSnapshotStore<TState>>(),
                sp.GetRequiredService<IDeviceConnectionEpochSource>(),
                options,
                sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton<IHostedService>(sp =>
            new DevicePollHostedService<TState>(
                sp.GetRequiredService<DevicePollRuntime<TState>>()));

        return services;
    }

    private sealed class DeviceStateRegistrationMarker<TState>;
    private sealed class DevicePollingRegistrationMarker<TState>;
}

internal sealed class DevicePollHostedService<TState>(
    DevicePollRuntime<TState> runtime) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        runtime.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        runtime.StopAsync(cancellationToken);
}
