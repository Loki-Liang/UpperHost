using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenDeviceStudio.Control.Commands;

namespace OpenDeviceStudio.Control.Scheduling;

public static class CommandDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddOpenDeviceStudioCommandDispatcher<TCommand, TResult>(
        this IServiceCollection services,
        BoundedCommandDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(CommandDispatcherRegistrationMarker<TCommand, TResult>)))
            return services;

        var configured = options ?? new BoundedCommandDispatcherOptions();
        configured.Validate();

        services.AddOpenDeviceStudioControlResourceArbiter(configured.MaxSharedReadersPerResource);

        services.AddSingleton<CommandDispatcherRegistrationMarker<TCommand, TResult>>();
        services.TryAddSingleton<CommandRuntime<TCommand, TResult>>();
        services.TryAddSingleton(sp =>
            new BoundedCommandDispatcher<TCommand, TResult>(
                sp.GetRequiredService<CommandRuntime<TCommand, TResult>>(),
                configured,
                sp.GetService<ICommandConnectionEpochValidator>(),
                sp.GetService<TimeProvider>() ?? TimeProvider.System,
                sp.GetRequiredService<ICommandResourceArbiter>()));
        services.AddSingleton<IHostedService>(sp =>
            new CommandDispatcherHostedService<TCommand, TResult>(
                sp.GetRequiredService<BoundedCommandDispatcher<TCommand, TResult>>()));

        return services;
    }

    public static IServiceCollection AddOpenDeviceStudioControlResourceArbiter(
        this IServiceCollection services,
        int maxSharedReadersPerResource = 4)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (maxSharedReadersPerResource <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSharedReadersPerResource));

        EnsureSharedResourceArbiter(services, maxSharedReadersPerResource);
        return services;
    }

    private static void EnsureSharedResourceArbiter(
        IServiceCollection services,
        int maxSharedReadersPerResource)
    {
        var existing = services
            .FirstOrDefault(static descriptor =>
                descriptor.ServiceType == typeof(CommandResourceArbiterRegistration))
            ?.ImplementationInstance as CommandResourceArbiterRegistration;

        if (existing is not null)
        {
            if (existing.MaxSharedReadersPerResource != maxSharedReadersPerResource)
            {
                throw new InvalidOperationException(
                    "All host-registered command dispatchers must use the same " +
                    "MaxSharedReadersPerResource so Control resource arbitration remains authoritative.");
            }

            return;
        }

        services.AddSingleton(
            new CommandResourceArbiterRegistration(maxSharedReadersPerResource));
        services.TryAddSingleton<ICommandResourceArbiter>(_ =>
            new CommandResourceCoordinator(maxSharedReadersPerResource));
    }

    private sealed record CommandResourceArbiterRegistration(
        int MaxSharedReadersPerResource);

    private sealed class CommandDispatcherRegistrationMarker<TCommand, TResult>;
}

internal sealed class CommandDispatcherHostedService<TCommand, TResult>(
    BoundedCommandDispatcher<TCommand, TResult> dispatcher) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        dispatcher.StartAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) =>
        dispatcher.StopAsync(cancellationToken);
}
