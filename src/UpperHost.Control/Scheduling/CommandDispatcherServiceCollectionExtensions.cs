using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UpperHost.Control.Commands;

namespace UpperHost.Control.Scheduling;

public static class CommandDispatcherServiceCollectionExtensions
{
    public static IServiceCollection AddUpperHostCommandDispatcher<TCommand, TResult>(
        this IServiceCollection services,
        BoundedCommandDispatcherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(static descriptor =>
                descriptor.ServiceType == typeof(CommandDispatcherRegistrationMarker<TCommand, TResult>)))
            return services;

        var configured = options ?? new BoundedCommandDispatcherOptions();
        configured.Validate();

        services.AddSingleton<CommandDispatcherRegistrationMarker<TCommand, TResult>>();
        services.TryAddSingleton<CommandRuntime<TCommand, TResult>>();
        services.TryAddSingleton(sp =>
            new BoundedCommandDispatcher<TCommand, TResult>(
                sp.GetRequiredService<CommandRuntime<TCommand, TResult>>(),
                configured,
                sp.GetService<ICommandConnectionEpochValidator>()));
        services.AddSingleton<IHostedService>(sp =>
            new CommandDispatcherHostedService<TCommand, TResult>(
                sp.GetRequiredService<BoundedCommandDispatcher<TCommand, TResult>>()));

        return services;
    }

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
