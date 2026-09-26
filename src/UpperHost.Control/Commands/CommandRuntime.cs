using System.Diagnostics;
using UpperHost.Abstractions.Devices;
using UpperHost.Abstractions.Observability;

namespace UpperHost.Control.Commands;

public enum CommandExecutionStatus
{
    Succeeded,
    Rejected,
    TimedOut,
    Cancelled,
    Faulted,
    UnknownOutcome
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
        using var activity = UpperHostTelemetry.StartActivity(
            "upperhost.command.execute",
            ActivityKind.Internal,
            new UpperHostTelemetryContext(
                CommandId: executionId,
                Operation: typeof(TCommand).Name));

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
                    return Complete(
                        executionId,
                        CommandExecutionStatus.Rejected,
                        default,
                        decision.Code ?? "command_rejected",
                        decision.Message ?? $"Command rejected by guard '{guard.Name}'.",
                        null,
                        timer.Elapsed,
                        activity);
                }
            }

            var value = await _target.ExecuteAsync(command, timeoutCts.Token).ConfigureAwait(false);
            timer.Stop();
            return Complete(
                executionId,
                CommandExecutionStatus.Succeeded,
                value,
                null,
                null,
                null,
                timer.Elapsed,
                activity);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            return Complete(
                executionId,
                CommandExecutionStatus.Cancelled,
                default,
                "command_cancelled",
                "Command execution was cancelled.",
                ex,
                timer.Elapsed,
                activity);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            timer.Stop();
            return Complete(
                executionId,
                CommandExecutionStatus.TimedOut,
                default,
                "command_timeout",
                $"Command did not complete within {options.Timeout}.",
                ex,
                timer.Elapsed,
                activity);
        }
        catch (Exception ex)
        {
            timer.Stop();
            return Complete(
                executionId,
                CommandExecutionStatus.Faulted,
                default,
                "command_fault",
                ex.Message,
                ex,
                timer.Elapsed,
                activity);
        }
    }

    private static CommandExecutionResult<TResult> Complete(
        string executionId,
        CommandExecutionStatus status,
        TResult? value,
        string? code,
        string? message,
        Exception? exception,
        TimeSpan duration,
        Activity? activity)
    {
        var tags = UpperHostTelemetry.CreateMetricTags(
            new UpperHostMetricContext(
                Operation: typeof(TCommand).Name,
                Outcome: status.ToString(),
                ErrorType: exception?.GetType().FullName));

        UpperHostTelemetry.CommandExecutions.Add(1, tags);
        UpperHostTelemetry.CommandDurationSeconds.Record(duration.TotalSeconds, tags);
        if (status != CommandExecutionStatus.Succeeded)
            UpperHostTelemetry.CommandFailures.Add(1, tags);

        if (activity is not null)
        {
            activity.SetTag("upperhost.result", status.ToString());
            activity.SetTag("upperhost.error.code", code);
            activity.SetTag("upperhost.elapsed_ms", duration.TotalMilliseconds);
            if (exception is not null)
                activity.SetTag("error.type", exception.GetType().FullName);

            activity.SetStatus(
                status == CommandExecutionStatus.Succeeded
                    ? ActivityStatusCode.Ok
                    : status == CommandExecutionStatus.Cancelled
                        ? ActivityStatusCode.Unset
                        : ActivityStatusCode.Error,
                code);
        }

        return new CommandExecutionResult<TResult>(
            executionId,
            status,
            value,
            code,
            message,
            exception,
            duration);
    }
}
