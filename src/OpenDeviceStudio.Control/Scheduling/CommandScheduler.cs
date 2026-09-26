using OpenDeviceStudio.Control.Commands;

namespace OpenDeviceStudio.Control.Scheduling;

public enum CommandPriority
{
    Critical = 0,
    High = 10,
    Normal = 20,
    Low = 30
}

/// <summary>
/// Compatibility facade for the original scheduler API. New code should resolve and use
/// <see cref="BoundedCommandDispatcher{TCommand,TResult}"/> from the application composition root.
/// </summary>
public sealed class CommandScheduler<TCommand, TResult> : IAsyncDisposable
{
    private readonly BoundedCommandDispatcher<TCommand, TResult> _dispatcher;

    public CommandScheduler(CommandRuntime<TCommand, TResult> runtime)
        : this(
            runtime,
            new BoundedCommandDispatcherOptions(
                Capacity: 256,
                PerPriorityCapacity: 128,
                MaxConcurrency: 1))
    {
    }

    public CommandScheduler(
        CommandRuntime<TCommand, TResult> runtime,
        BoundedCommandDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(options);

        _dispatcher = new BoundedCommandDispatcher<TCommand, TResult>(runtime, options);
        _dispatcher.StartAsync().GetAwaiter().GetResult();
    }

    public int PendingCount => _dispatcher.PendingCount;

    public Task<CommandExecutionResult<TResult>> EnqueueAsync(
        TCommand command,
        CommandPriority priority = CommandPriority.Normal,
        CommandExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return _dispatcher.EnqueueAsync(
            command,
            new CommandDispatchOptions(
                Priority: priority,
                Execution: options,
                Safety: options?.Safety ?? CommandSafetyMetadata.Legacy),
            cancellationToken);
    }

    public ValueTask DisposeAsync() => _dispatcher.DisposeAsync();
}
