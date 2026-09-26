using System.Collections.Concurrent;
using OpenDeviceStudio.Abstractions.Workflows;
using OpenDeviceStudio.Control.Scheduling;

namespace OpenDeviceStudio.Workflows;

public sealed class WorkflowExecutionHandle
{
    private readonly WorkflowExecutionControl _control;

    internal WorkflowExecutionHandle(
        WorkflowExecutionControl control,
        Task<WorkflowExecutionResult> completion)
    {
        _control = control;
        Completion = completion;
    }

    public string ExecutionId => _control.ExecutionId;
    public WorkflowExecutionStatus Status => _control.State;
    public Task<WorkflowExecutionResult> Completion { get; }

    public bool RequestPause() => _control.RequestPause();
    public bool RequestResume() => _control.RequestResume();
    public bool RequestStop() => _control.RequestStop();
    public bool RequestAbort() => _control.RequestAbort();
}

public sealed partial class WorkflowExecutionCoordinator : IAsyncDisposable
{
    private sealed record NodeResultCore(
        WorkflowNodeStatus Status,
        int Attempts = 1,
        string? Message = null,
        Exception? Exception = null);

    private sealed class OutcomeCollector
    {
        private readonly ConcurrentQueue<(long Sequence, WorkflowNodeOutcome Outcome)> _items = new();
        private long _sequence;

        public void Add(WorkflowNodeOutcome outcome) =>
            _items.Enqueue((Interlocked.Increment(ref _sequence), outcome));

        public IReadOnlyList<WorkflowNodeOutcome> Snapshot() =>
            _items
                .OrderBy(static item => item.Sequence)
                .Select(static item => item.Outcome)
                .ToArray();
    }

    private readonly ICommandResourceArbiter _resourceArbiter;
    private readonly TimeProvider _timeProvider;
    private readonly IWorkflowExecutionJournal _journal;
    private readonly WorkflowJournalFailurePolicy _journalFailurePolicy;
    private readonly ConcurrentDictionary<string, WorkflowExecutionControl> _activeByStation =
        new(StringComparer.Ordinal);
    private int _disposed;

    public WorkflowExecutionCoordinator(
        ICommandResourceArbiter resourceArbiter,
        TimeProvider? timeProvider = null,
        IWorkflowExecutionJournal? journal = null,
        WorkflowJournalFailurePolicy journalFailurePolicy = WorkflowJournalFailurePolicy.FailExecution)
    {
        _resourceArbiter = resourceArbiter ?? throw new ArgumentNullException(nameof(resourceArbiter));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _journal = journal ?? NullWorkflowExecutionJournal.Instance;
        _journalFailurePolicy = journalFailurePolicy;
    }

    public int ActiveExecutionCount => _activeByStation.Count;

    public WorkflowExecutionHandle Start(
        WorkflowExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Definition);
        ArgumentNullException.ThrowIfNull(request.RecipeSnapshot);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.StationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Mode);
        request.RecipeSnapshot.ValidateIntegrity();

        var plan = WorkflowPlanCompiler.Compile(request.Definition);
        var executionId = Guid.NewGuid().ToString("N");
        var control = new WorkflowExecutionControl(executionId, cancellationToken);

        if (!_activeByStation.TryAdd(request.StationId, control))
        {
            control.Dispose();
            throw new InvalidOperationException(
                $"Station '{request.StationId}' already has an active workflow execution.");
        }

        WorkflowExecutionTelemetry.ActiveExecutions.Add(1);

        var completion = ExecuteAsync(plan, request, control);
        control.AttachCompletion(completion);
        return new WorkflowExecutionHandle(control, completion);
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var controls = _activeByStation.Values.ToArray();
        foreach (var control in controls)
            control.RequestAbort();

        var completions = controls
            .Select(static control => control.Completion)
            .Where(static completion => completion is not null)
            .Cast<Task<WorkflowExecutionResult>>()
            .ToArray();

        if (completions.Length > 0)
        {
            await Task.WhenAll(completions)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async ValueTask<WorkflowRecoveryDecision> PlanRecoveryAsync(
        WorkflowRecoveryEvidence evidence,
        IWorkflowRecoveryReconciler reconciler,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(reconciler);

        // Persisted execution state is evidence, never physical-state authority.
        // Every restart decision must reconcile through #63/readback (or an equivalent
        // authoritative product adapter) before ResumeFromSafeCheckpoint is accepted.
        var decision = await reconciler
            .ReconcileAsync(evidence, cancellationToken)
            .ConfigureAwait(false);

        if (!decision.Reconciled)
        {
            return new WorkflowRecoveryDecision(
                WorkflowRecoveryAction.ManualIntervention,
                false,
                string.IsNullOrWhiteSpace(decision.Reason)
                    ? "Authoritative device-state reconciliation did not succeed."
                    : decision.Reason);
        }

        if (decision.Action == WorkflowRecoveryAction.ResumeFromSafeCheckpoint &&
            string.IsNullOrWhiteSpace(evidence.LastSafeCheckpoint))
        {
            return new WorkflowRecoveryDecision(
                WorkflowRecoveryAction.ManualIntervention,
                true,
                "ResumeFromSafeCheckpoint was rejected because no completed safe checkpoint exists.");
        }

        return decision;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await StopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task<WorkflowExecutionResult> ExecuteAsync(
        WorkflowExecutionPlan plan,
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control)
    {
        var startedAtUtc = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        var outcomes = new OutcomeCollector();
        var commandExecutionIds = new ConcurrentQueue<string>();
        var data = new WorkflowExecutionData();
        var context = new WorkflowExecutionContext(
            control.ExecutionId,
            plan.Root.NodeId,
            request.RecipeSnapshot,
            data,
            commandExecutionIds);

        WorkflowExecutionStatus terminalStatus = WorkflowExecutionStatus.Failed;
        string? failureReason = null;

        try
        {
            await AppendJournalAsync(
                control,
                "execution_created",
                WorkflowExecutionStatus.Created,
                null,
                $"workflow={plan.WorkflowId};version={plan.Version};mode={request.Mode}",
                request.RecipeSnapshot.Hash,
                CancellationToken.None).ConfigureAwait(false);

            NodeResultCore rootResult;
            if (control.IsAbortRequested)
            {
                rootResult = new NodeResultCore(
                    WorkflowNodeStatus.Aborted,
                    Message: "Execution was aborted before preparation completed.");
            }
            else if (control.IsStopRequested)
            {
                rootResult = new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    Message: "Execution was stopped before preparation completed.");
            }
            else
            {
                await TransitionAsync(
                    request,
                    control,
                    WorkflowExecutionStatus.Preparing,
                    "Execution plan compiled and recipe snapshot frozen.",
                    CancellationToken.None).ConfigureAwait(false);

                if (control.IsAbortRequested)
                {
                    rootResult = new NodeResultCore(
                        WorkflowNodeStatus.Aborted,
                        Message: "Execution was aborted during preparation.");
                }
                else if (control.IsStopRequested)
                {
                    rootResult = new NodeResultCore(
                        WorkflowNodeStatus.Stopped,
                        Message: "Execution was stopped before the first node started.");
                }
                else
                {
                    await TransitionAsync(
                        request,
                        control,
                        WorkflowExecutionStatus.Running,
                        null,
                        CancellationToken.None).ConfigureAwait(false);

                    rootResult = await ExecuteNodeAsync(
                        plan.Root,
                        context,
                        request,
                        control,
                        outcomes,
                        control.AbortToken).ConfigureAwait(false);
                }
            }

            if (control.IsAbortRequested &&
                rootResult.Status != WorkflowNodeStatus.RecoveryRequired)
            {
                rootResult = new NodeResultCore(
                    WorkflowNodeStatus.Aborted,
                    rootResult.Attempts,
                    rootResult.Message ?? "Abort requested.",
                    rootResult.Exception);
            }
            else if (control.IsStopRequested &&
                     rootResult.Status == WorkflowNodeStatus.Succeeded)
            {
                rootResult = new NodeResultCore(
                    WorkflowNodeStatus.Stopped,
                    rootResult.Attempts,
                    "Stop requested after the current node completed.",
                    rootResult.Exception);
            }

            terminalStatus = MapTerminalStatus(rootResult.Status);
            failureReason = rootResult.Message;

            if (terminalStatus == WorkflowExecutionStatus.Stopped)
                await EnsureStoppingAsync(request, control).ConfigureAwait(false);

            await TransitionAsync(
                request,
                control,
                terminalStatus,
                failureReason,
                CancellationToken.None).ConfigureAwait(false);

            foreach (var commandExecutionId in commandExecutionIds)
            {
                await AppendJournalAsync(
                    control,
                    "command_correlation",
                    terminalStatus,
                    null,
                    null,
                    request.RecipeSnapshot.Hash,
                    CancellationToken.None,
                    commandExecutionId).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException ex) when (control.IsAbortRequested)
        {
            terminalStatus = WorkflowExecutionStatus.Aborted;
            failureReason = "Execution abort was requested.";
            failureReason = await TryFinalizeFailureStateAsync(
                request,
                control,
                terminalStatus,
                failureReason,
                ex).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            terminalStatus = WorkflowExecutionStatus.Failed;
            failureReason = ex.Message;
            failureReason = await TryFinalizeFailureStateAsync(
                request,
                control,
                terminalStatus,
                failureReason,
                ex).ConfigureAwait(false);
        }
        finally
        {
            if (_activeByStation.TryGetValue(request.StationId, out var current) &&
                ReferenceEquals(current, control))
            {
                _activeByStation.TryRemove(request.StationId, out _);
            }

            WorkflowExecutionTelemetry.ActiveExecutions.Add(-1);
            control.Dispose();
        }

        var result = new WorkflowExecutionResult(
            control.ExecutionId,
            plan.WorkflowId,
            plan.Version,
            terminalStatus,
            request.RecipeSnapshot,
            request.StationId,
            request.Mode,
            startedAtUtc,
            _timeProvider.GetUtcNow(),
            outcomes.Snapshot(),
            commandExecutionIds.ToArray(),
            control.LastSafeCheckpoint,
            control.JournalFailureCount,
            failureReason);

        var tags = WorkflowExecutionTelemetry.ExecutionTags(request.Mode, terminalStatus);
        WorkflowExecutionTelemetry.Executions.Add(1, tags);
        WorkflowExecutionTelemetry.ExecutionDurationSeconds.Record(
            _timeProvider.GetElapsedTime(startedTimestamp).TotalSeconds,
            tags);

        if (terminalStatus is not WorkflowExecutionStatus.Completed)
            WorkflowExecutionTelemetry.Failures.Add(1, tags);

        return result;
    }

    private async Task EnsureStoppingAsync(
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control)
    {
        await TransitionAsync(
            request,
            control,
            WorkflowExecutionStatus.Stopping,
            "Orderly stop requested; no new workflow node will start.",
            CancellationToken.None).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        WorkflowExecutionStatus target,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (!control.TrySetRuntimeState(target, out var previous))
            return;

        await NotifyStateAsync(
            request,
            control,
            previous,
            target,
            reason,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task NotifyStateAsync(
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        WorkflowExecutionStatus previous,
        WorkflowExecutionStatus target,
        string? reason,
        CancellationToken cancellationToken)
    {
        if (request.StationStateAuthority is not null)
        {
            await request.StationStateAuthority
                .OnExecutionTransitionAsync(
                    control.ExecutionId,
                    previous,
                    target,
                    reason,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        await AppendJournalAsync(
            control,
            "state_transition",
            target,
            null,
            $"{previous}->{target}: {reason}",
            request.RecipeSnapshot.Hash,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> TryFinalizeFailureStateAsync(
        WorkflowExecutionRequest request,
        WorkflowExecutionControl control,
        WorkflowExecutionStatus terminalStatus,
        string? failureReason,
        Exception primaryException)
    {
        try
        {
            await TransitionAsync(
                request,
                control,
                terminalStatus,
                failureReason,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception transitionException)
        {
            control.ForceTerminalState(terminalStatus);
            failureReason =
                $"{failureReason} Terminal-state publication also failed: {transitionException.Message}";
        }

        try
        {
            await AppendJournalAsync(
                control,
                "execution_failure",
                terminalStatus,
                null,
                primaryException.Message,
                request.RecipeSnapshot.Hash,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception journalException)
        {
            failureReason =
                $"{failureReason} Failure-journal append also failed: {journalException.Message}";
        }

        return failureReason;
    }

    private async ValueTask AppendJournalAsync(
        WorkflowExecutionControl control,
        string eventType,
        WorkflowExecutionStatus status,
        string? nodeId,
        string? message,
        string? recipeHash,
        CancellationToken cancellationToken,
        string? commandExecutionId = null)
    {
        var journalEvent = new WorkflowJournalEvent(
            control.NextJournalSequence(),
            control.ExecutionId,
            _timeProvider.GetUtcNow(),
            eventType,
            status,
            nodeId,
            message,
            recipeHash,
            commandExecutionId);

        try
        {
            await _journal
                .AppendAsync(journalEvent, cancellationToken)
                .ConfigureAwait(false);
        }
        catch when (_journalFailurePolicy == WorkflowJournalFailurePolicy.ContinueExecution)
        {
            control.RecordJournalFailure();
        }
    }

    private static WorkflowExecutionStatus MapTerminalStatus(WorkflowNodeStatus status) =>
        status switch
        {
            WorkflowNodeStatus.Succeeded => WorkflowExecutionStatus.Completed,
            WorkflowNodeStatus.Stopped => WorkflowExecutionStatus.Stopped,
            WorkflowNodeStatus.Aborted => WorkflowExecutionStatus.Aborted,
            WorkflowNodeStatus.RecoveryRequired => WorkflowExecutionStatus.RecoveryRequired,
            _ => WorkflowExecutionStatus.Failed
        };

    private static IReadOnlyList<CommandResourceClaim> NormalizeWorkflowClaims(
        IReadOnlyList<WorkflowResourceRequirement>? resources)
    {
        if (resources is null || resources.Count == 0)
            return Array.Empty<CommandResourceClaim>();

        var claims = new Dictionary<CommandResourceKey, CommandResourceAccess>();
        foreach (var resource in resources)
        {
            var claim = resource.ToCommandClaim();
            if (claims.TryGetValue(claim.Resource, out var existing) &&
                existing == CommandResourceAccess.Exclusive)
            {
                continue;
            }

            claims[claim.Resource] = claim.Access;
        }

        return claims
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new CommandResourceClaim(pair.Key, pair.Value))
            .ToArray();
    }

    private CancellationTimer? CreateCancellationTimer(
        TimeSpan? timeout,
        CancellationTokenSource cancellation)
    {
        if (timeout is null)
            return null;

        return new CancellationTimer(
            _timeProvider,
            timeout.Value,
            cancellation);
    }

    private async Task DelayAsync(
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        if (delay <= TimeSpan.Zero)
            return;

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        using var registration = cancellationToken.Register(
            static state =>
            {
                var pair =
                    ((TaskCompletionSource Completion, CancellationToken Token))state!;
                pair.Completion.TrySetCanceled(pair.Token);
            },
            (completion, cancellationToken));

        using var timer = _timeProvider.CreateTimer(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            completion,
            delay,
            Timeout.InfiniteTimeSpan);

        await completion.Task.ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(WorkflowExecutionCoordinator));
    }

    private sealed class CancellationTimer : IDisposable
    {
        private readonly ITimer _timer;
        private int _timedOut;

        public CancellationTimer(
            TimeProvider timeProvider,
            TimeSpan timeout,
            CancellationTokenSource cancellation)
        {
            _timer = timeProvider.CreateTimer(
                state =>
                {
                    Interlocked.Exchange(ref _timedOut, 1);
                    ((CancellationTokenSource)state!).Cancel();
                },
                cancellation,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public bool TimedOut => Volatile.Read(ref _timedOut) != 0;

        public void Dispose() => _timer.Dispose();
    }
}
