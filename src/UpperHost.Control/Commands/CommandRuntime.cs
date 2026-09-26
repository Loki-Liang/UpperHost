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

public enum CommandExecutionMilestone
{
    BeforeSideEffect = 0,
    SideEffectMayHaveStarted = 10,
    DeviceAcknowledged = 20,
    Completed = 30
}

public sealed record CommandSafetyMetadata(
    bool ReadOnly,
    bool Idempotent,
    bool Motion,
    bool Hazardous,
    bool RetryAllowed)
{
    public static CommandSafetyMetadata Legacy { get; } =
        new(ReadOnly: true, Idempotent: true, Motion: false, Hazardous: false, RetryAllowed: false);

    public static CommandSafetyMetadata MutatingNonIdempotent { get; } =
        new(ReadOnly: false, Idempotent: false, Motion: false, Hazardous: false, RetryAllowed: false);

    public bool MayHaveSideEffects => !ReadOnly || Motion || Hazardous;

    public bool AutomaticRetryAllowed =>
        RetryAllowed &&
        Idempotent &&
        !Motion &&
        !Hazardous;
}

public sealed record CommandExecutionOptions(TimeSpan Timeout)
{
    public static CommandExecutionOptions Default { get; } = new(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Legacy direct CommandRuntime calls keep the historical timeout/cancel semantics.
    /// The bounded dispatcher overrides this with the conservative mutating default unless
    /// the application supplies explicit command safety metadata.
    /// </summary>
    public CommandSafetyMetadata Safety { get; init; } = CommandSafetyMetadata.Legacy;
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

public sealed class CommandExecutionContext(string executionId)
{
    private int _milestone = (int)CommandExecutionMilestone.BeforeSideEffect;

    public string ExecutionId { get; } = executionId;
    public CommandExecutionMilestone Milestone => (CommandExecutionMilestone)Volatile.Read(ref _milestone);

    public void MarkSideEffectMayHaveStarted() =>
        AdvanceTo(CommandExecutionMilestone.SideEffectMayHaveStarted);

    public void MarkDeviceAcknowledged() =>
        AdvanceTo(CommandExecutionMilestone.DeviceAcknowledged);

    public void MarkCompleted() =>
        AdvanceTo(CommandExecutionMilestone.Completed);

    private void AdvanceTo(CommandExecutionMilestone target)
    {
        var desired = (int)target;
        while (true)
        {
            var observed = Volatile.Read(ref _milestone);
            if (observed >= desired)
                return;

            if (Interlocked.CompareExchange(ref _milestone, desired, observed) == observed)
                return;
        }
    }
}

public interface IContextualCommandable<in TCommand, TResult>
{
    Task<TResult> ExecuteAsync(
        TCommand command,
        CommandExecutionContext context,
        CancellationToken cancellationToken = default);
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
    public CommandExecutionMilestone Milestone { get; init; } = CommandExecutionMilestone.BeforeSideEffect;
}

public sealed class CommandRuntime<TCommand, TResult>
{
    private readonly ICommandable<TCommand, TResult>? _legacyTarget;
    private readonly IContextualCommandable<TCommand, TResult>? _contextualTarget;
    private readonly IReadOnlyList<ICommandGuard<TCommand>> _guards;
    private readonly TimeProvider _timeProvider;

    public CommandRuntime(
        ICommandable<TCommand, TResult> target,
        IEnumerable<ICommandGuard<TCommand>>? guards = null)
        : this(target, guards, TimeProvider.System)
    {
    }

    public CommandRuntime(
        ICommandable<TCommand, TResult> target,
        IEnumerable<ICommandGuard<TCommand>>? guards,
        TimeProvider timeProvider)
    {
        _legacyTarget = target ?? throw new ArgumentNullException(nameof(target));
        _guards = guards?.ToArray() ?? [];
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public CommandRuntime(
        IContextualCommandable<TCommand, TResult> target,
        IEnumerable<ICommandGuard<TCommand>>? guards = null,
        TimeProvider? timeProvider = null)
    {
        _contextualTarget = target ?? throw new ArgumentNullException(nameof(target));
        _guards = guards?.ToArray() ?? [];
        _timeProvider = timeProvider ?? TimeProvider.System;
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
        var context = new CommandExecutionContext(executionId);
        using var activity = UpperHostTelemetry.StartActivity(
            "upperhost.command.execute",
            ActivityKind.Internal,
            new UpperHostTelemetryContext(
                CommandId: executionId,
                Operation: typeof(TCommand).Name));

        var startedAt = _timeProvider.GetTimestamp();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timeoutTimer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            timeoutCts,
            options.Timeout,
            Timeout.InfiniteTimeSpan);

        try
        {
            foreach (var guard in _guards)
            {
                var decision = await guard.EvaluateAsync(command, timeoutCts.Token).ConfigureAwait(false);
                if (!decision.Allowed)
                {
                    return Complete(
                        executionId,
                        CommandExecutionStatus.Rejected,
                        default,
                        decision.Code ?? "command_rejected",
                        decision.Message ?? $"Command rejected by guard '{guard.Name}'.",
                        null,
                        _timeProvider.GetElapsedTime(startedAt),
                        context.Milestone,
                        activity);
                }
            }

            TResult value;
            if (_contextualTarget is not null)
            {
                value = await _contextualTarget
                    .ExecuteAsync(command, context, timeoutCts.Token)
                    .ConfigureAwait(false);
            }
            else
            {
                if (options.Safety.MayHaveSideEffects)
                    context.MarkSideEffectMayHaveStarted();

                value = await _legacyTarget!
                    .ExecuteAsync(command, timeoutCts.Token)
                    .ConfigureAwait(false);
            }

            context.MarkCompleted();
            return Complete(
                executionId,
                CommandExecutionStatus.Succeeded,
                value,
                null,
                null,
                null,
                _timeProvider.GetElapsedTime(startedAt),
                context.Milestone,
                activity);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            return CompleteCancellation(
                executionId,
                CommandExecutionStatus.Cancelled,
                "command_cancelled",
                "Command execution was cancelled.",
                options,
                context,
                ex,
                _timeProvider.GetElapsedTime(startedAt),
                activity);
        }
        catch (OperationCanceledException ex) when (timeoutCts.IsCancellationRequested)
        {
            return CompleteCancellation(
                executionId,
                CommandExecutionStatus.TimedOut,
                "command_timeout",
                $"Command did not complete within {options.Timeout}.",
                options,
                context,
                ex,
                _timeProvider.GetElapsedTime(startedAt),
                activity);
        }
        catch (Exception ex)
        {
            return Complete(
                executionId,
                CommandExecutionStatus.Faulted,
                default,
                "command_fault",
                ex.Message,
                ex,
                _timeProvider.GetElapsedTime(startedAt),
                context.Milestone,
                activity);
        }
    }

    private static CommandExecutionResult<TResult> CompleteCancellation(
        string executionId,
        CommandExecutionStatus cancellationStatus,
        string cancellationCode,
        string cancellationMessage,
        CommandExecutionOptions options,
        CommandExecutionContext context,
        Exception exception,
        TimeSpan duration,
        Activity? activity)
    {
        var outcomeUnknown =
            options.Safety.MayHaveSideEffects &&
            context.Milestone >= CommandExecutionMilestone.SideEffectMayHaveStarted;

        return Complete(
            executionId,
            outcomeUnknown ? CommandExecutionStatus.UnknownOutcome : cancellationStatus,
            default,
            outcomeUnknown ? "command_outcome_unknown" : cancellationCode,
            outcomeUnknown
                ? "Command crossed the side-effect boundary but final device state is unknown; reconcile before retry."
                : cancellationMessage,
            exception,
            duration,
            context.Milestone,
            activity);
    }

    private static CommandExecutionResult<TResult> Complete(
        string executionId,
        CommandExecutionStatus status,
        TResult? value,
        string? code,
        string? message,
        Exception? exception,
        TimeSpan duration,
        CommandExecutionMilestone milestone,
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
            activity.SetTag("upperhost.command.milestone", milestone.ToString());
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
            duration)
        {
            Milestone = milestone
        };
    }
}
