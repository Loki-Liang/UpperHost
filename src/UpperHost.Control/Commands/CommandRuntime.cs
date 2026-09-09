using System.Diagnostics;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Control.Commands;

public enum CommandExecutionStatus
{
    Succeeded,
    Rejected,
    TimedOut,
    Cancelled,
    Faulted
}

public sealed record CommandExecutionOptions(TimeSpan Timeout)
{
    public static CommandExecutionOptions Default { get; } = new(TimeSpan.FromSeconds(30));
}

public sealed record CommandGuardDecision(bool Allowed, string? Code = null, string? Message = null)
{
    public static CommandGuardDecision Allow() => new(true);
    public static CommandGuardDecision Reject(string code, string message) => new(false, code, message);
}

public interface ICommandGuard<in TCommand>
{
    string Name { get; }
    ValueTask<CommandGuardDecision> EvaluateAsync(TCommand command, CancellationToken cancellationToken = default);
}

public sealed record CommandExecutionResult<TResult>(
    string ExecutionId,
    CommandExecutionStatus Status,
    TResult? Value,
    string? Code,
    string? Message,
    Exception? Exception,
    TimeSpan Duration)
{
    public bool IsSuccess => Status == CommandExecutionStatus.Succeeded;
}

public sealed class CommandRuntime<TCommand, TResult>
{
    private readonly ICommandable<TCommand, TResult> _target;
    private readonly IReadOnlyList<ICommandGuard<TCommand>> _guards;

    public CommandRuntime(
        ICommandable<TCommand, TResult> target,
        IEnumerable<ICommandGuard<TCommand>>? guards = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _guards = guards?.ToArray() ?? [];
    }

    public async Task<CommandExecutionResult<TResult>> ExecuteAsync(
        TCommand command,
        CommandExecutionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= CommandExecutionOptions.Default;
        if (options.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Command timeout must be greater than zero.");

        var executionId = Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);

        try
        {
            foreach (var guard in _guards)
            {
                var decision = await guard.EvaluateAsync(command, timeoutCts.Token).ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    timer.Stop();
                    return new CommandExecutionResult<TResult>(
                        executionId,
                        CommandExecutionStatus.Rejected,
                        default,
                        decision.Code ?? "command_rejected",
                        decision.Message ?? $"Command rejected by guard '{guard.Name}'.",
                        null,
                        timer.Elapsed);
                }
            }

            var value = await _target.ExecuteAsync(command, timeoutCts.Token).ConfigureAwait(false);
            timer.Stop();
            return new CommandExecutionResult<TResult>(
                executionId,
                CommandExecutionStatus.Succeeded,
                value,
                null,
                null,
                null,
                timer.Elapsed);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            return new CommandExecutionResult<TResult>(
                executionId,
                CommandExecutionStatus.Cancelled,
                default,
                "command_cancelled",
                "Command execution was cancelled.",
                ex,
                timer.Elapsed);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            timer.Stop();
            return new CommandExecutionResult<TResult>(
                executionId,
                CommandExecutionStatus.TimedOut,
                default,
                "command_timeout",
                $"Command did not complete within {options.Timeout}.",
                ex,
                timer.Elapsed);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return new CommandExecutionResult<TResult>(
                executionId,
                CommandExecutionStatus.Faulted,
                default,
                "command_fault",
                ex.Message,
                ex,
                timer.Elapsed);
        }
    }
}
