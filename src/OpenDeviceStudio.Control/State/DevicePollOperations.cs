using OpenDeviceStudio.Control.Commands;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Control.State;

public static class DevicePollOperations
{
    private static readonly CommandSafetyMetadata ReadOnlySafety =
        new(
            ReadOnly: true,
            Idempotent: true,
            Motion: false,
            Hazardous: false,
            RetryAllowed: true);

    public static DevicePollOperation<TState> FromCommandDispatcher<TCommand, TResult, TState>(
        BoundedCommandDispatcher<TCommand, TResult> dispatcher,
        Func<DevicePollContext, TCommand> commandFactory,
        Func<CommandExecutionResult<TResult>, DevicePollSample<TState>> projector,
        IReadOnlyList<CommandResourceClaim>? resourceClaims = null,
        CommandPriority priority = CommandPriority.Low,
        TimeSpan? queueTimeout = null,
        CommandExecutionOptions? execution = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(commandFactory);
        ArgumentNullException.ThrowIfNull(projector);

        var claims = resourceClaims?.ToArray() ?? [];

        return async (context, cancellationToken) =>
        {
            var command = commandFactory(context);
            var result = await dispatcher.EnqueueAsync(
                command,
                new CommandDispatchOptions(
                    Priority: priority,
                    ResourceClaims: claims,
                    Execution: execution,
                    Safety: ReadOnlySafety,
                    ConnectionEpoch: context.ConnectionEpoch,
                    QueueTimeout: queueTimeout),
                cancellationToken).ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                throw new DevicePollDispatchException(
                    context.GroupId,
                    result.Status,
                    result.Code,
                    result.Message,
                    result.Exception);
            }

            return projector(result);
        };
    }
}

public sealed class DevicePollDispatchException : InvalidOperationException
{
    public DevicePollDispatchException(
        string groupId,
        CommandExecutionStatus status,
        string? code,
        string? message,
        Exception? innerException = null)
        : base(
            $"Poll group '{groupId}' was not dispatched successfully: {status} ({code ?? "no_code"}). {message}",
            innerException)
    {
        GroupId = groupId;
        Status = status;
        Code = code;
    }

    public string GroupId { get; }
    public CommandExecutionStatus Status { get; }
    public string? Code { get; }
}
