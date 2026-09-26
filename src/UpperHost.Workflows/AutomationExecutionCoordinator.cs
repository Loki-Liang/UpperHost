using System.Collections.Concurrent;
using UpperHost.Control.Scheduling;

namespace UpperHost.Workflows;

public sealed record AutomationStepOutcome(
    long Sequence,
    string NodeId,
    AutomationStepStatus Status,
    int Attempts,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    string? Message,
    IReadOnlyList<string> CommandExecutionIds);

public sealed record AutomationExecutionResult(
    string ExecutionId,
    string WorkflowId,
    string WorkflowVersion,
    string RecipeHash,
    AutomationExecutionState State,
    AutomationStationState StationState,
    AutomationStepStatus Outcome,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    TimeSpan Duration,
    IReadOnlyList<AutomationStepOutcome> Steps,
    IReadOnlyList<string> CommandExecutionIds,
    AutomationExecutionData Data,
    bool RecoveryRequired,
    string? FailureReason);

public sealed record AutomationControlResult(
    bool Accepted,
    string? Code = null,
    string? Message = null);

public sealed record AutomationStartResult(
    bool Accepted,
    AutomationExecutionHandle? Execution,
    string? Code = null,
    string? Message = null);

public sealed class AutomationExecutionHandle
{
    private readonly AutomationExecutionCoordinator _owner;
    private readonly AutomationExecutionCoordinator.ActiveExecution _execution;

    internal AutomationExecutionHandle(
        AutomationExecutionCoordinator owner,
        AutomationExecutionCoordinator.ActiveExecution execution)
    {
        _owner = owner;
        _execution = execution;
    }

    public string ExecutionId => _execution.ExecutionId;
    public AutomationExecutionState State => _execution.ReadState();
    public Task<AutomationExecutionResult> Completion => _execution.Completion.Task;

    public Task<AutomationControlResult> PauseAsync(CancellationToken cancellationToken = default) =>
        _owner.RequestPauseAsync(_execution, cancellationToken);

    public Task<AutomationControlResult> ResumeAsync(CancellationToken cancellationToken = default) =>
        _owner.RequestResumeAsync(_execution, cancellationToken);

    public Task<AutomationControlResult> StopAsync(CancellationToken cancellationToken = default) =>
        _owner.RequestStopAsync(_execution, cancellationToken);

    public Task<AutomationControlResult> AbortAsync(CancellationToken cancellationToken = default) =>
        _owner.RequestAbortAsync(_execution, cancellationToken);
}

public sealed class AutomationExecutionCoordinator : IAsyncDisposable
{
    internal enum ControlRequest
    {
        None = 0,
        Pause = 1,
        Stop = 2,
        Abort = 3
    }

    internal sealed class CurrentCancellation(
        CancellationTokenSource source,
        bool cancelOnStop)
    {
        public CancellationTokenSource Source { get; } = source;
        public bool CancelOnStop { get; set; } = cancelOnStop;
    }

    internal sealed class ActiveExecution
    {
        public ActiveExecution(
            AutomationExecutionPlan plan,
            AutomationRecipeSnapshot recipe,
            AutomationMode mode,
            TimeProvider timeProvider)
        {
            Plan = plan;
            Recipe = recipe;
            Mode = mode;
            ExecutionId = Guid.NewGuid().ToString("N");
            StartedAt = timeProvider.GetUtcNow();
            StartedTimestamp = timeProvider.GetTimestamp();
        }

        public object Gate { get; } = new();
        public AutomationExecutionPlan Plan { get; }
        public AutomationRecipeSnapshot Recipe { get; }
        public AutomationMode Mode { get; }
        public string ExecutionId { get; }
        public DateTimeOffset StartedAt { get; }
        public long StartedTimestamp { get; }
        public CancellationTokenSource AbortCts { get; } = new();
        public TaskCompletionSource<AutomationExecutionResult> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<AutomationStepOutcome> StepOutcomes { get; } = new();
        public HashSet<string> CommandExecutionIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<Guid, CurrentCancellation> CurrentCancellations { get; } = [];
        public SemaphoreSlim JournalGate { get; } = new(1, 1);
        public AutomationExecutionData Data { get; } = new();

        public AutomationExecutionState State = AutomationExecutionState.Created;
        public ControlRequest Request;
        public TaskCompletionSource<bool>? ResumeSignal;
        public string? LastSafeCheckpoint;
        public bool HasUnknownPhysicalOutcome;
        public Exception? JournalFault;
        public long JournalSequence;
        public long StepSequence;
        public Task? RunTask;

        public AutomationExecutionState ReadState()
        {
            lock (Gate)
                return State;
        }
    }

    private sealed record NodeExecutionResult(
        AutomationStepResult Result,
        AutomationExecutionData Data);

    private sealed record ParallelChildResult(
        int Index,
        AutomationStepResult Result,
        AutomationExecutionData Data);

    private readonly object _gate = new();
    private readonly ICommandResourceArbiter _resourceArbiter;
    private readonly IAutomationExecutionJournal _journal;
    private readonly IAutomationRecoveryReconciler _reconciler;
    private readonly TimeProvider _timeProvider;
    private AutomationStationState _stationState = AutomationStationState.Idle;
    private ActiveExecution? _active;
    private AutomationRecoveryEvidence? _lastRecoveryEvidence;
    private bool _recoveryRequired;
    private int _disposed;

    public AutomationExecutionCoordinator(
        ICommandResourceArbiter resourceArbiter,
        IAutomationExecutionJournal? journal = null,
        IAutomationRecoveryReconciler? reconciler = null,
        TimeProvider? timeProvider = null)
    {
        _resourceArbiter = resourceArbiter ?? throw new ArgumentNullException(nameof(resourceArbiter));
        _journal = journal ?? NullAutomationExecutionJournal.Instance;
        _reconciler = reconciler ?? new ConservativeAutomationRecoveryReconciler();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public AutomationStationState StationState
    {
        get
        {
            lock (_gate)
                return _stationState;
        }
    }

    public bool HasActiveExecution
    {
        get
        {
            lock (_gate)
                return _active is not null;
        }
    }

    public AutomationRecoveryEvidence? LastRecoveryEvidence
    {
        get
        {
            lock (_gate)
                return _lastRecoveryEvidence;
        }
    }

    public AutomationStartResult TryStart(
        AutomationExecutionPlan plan,
        AutomationRecipeSnapshot recipe,
        AutomationMode mode = AutomationMode.Auto)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(recipe);

        ActiveExecution active;
        AutomationStationState previous;

        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0)
                return new AutomationStartResult(false, null, "coordinator_disposed", "Automation coordinator is disposed.");

            if (_active is not null)
                return new AutomationStartResult(false, null, "station_busy", "The station already has an active execution.");

            if (_recoveryRequired)
            {
                return new AutomationStartResult(
                    false,
                    null,
                    "recovery_required",
                    "The previous execution requires authoritative reconcile/manual recovery before another run.");
            }

            if (_stationState is not (AutomationStationState.Idle or AutomationStationState.Ready))
            {
                return new AutomationStartResult(
                    false,
                    null,
                    "station_not_startable",
                    $"Station state '{_stationState}' cannot start an execution.");
            }

            previous = _stationState;
            _stationState = AutomationStationState.Preparing;
            active = new ActiveExecution(plan, recipe, mode, _timeProvider)
            {
                State = AutomationExecutionState.Preparing
            };
            _active = active;
        }

        var handle = new AutomationExecutionHandle(this, active);
        active.RunTask = RunExecutionAsync(active, previous);
        return new AutomationStartResult(true, handle);
    }

    public AutomationControlResult TryResetStation()
    {
        lock (_gate)
        {
            if (_active is not null)
                return new AutomationControlResult(false, "station_busy", "Cannot reset while an execution is active.");
            if (_recoveryRequired)
                return new AutomationControlResult(false, "recovery_required", "Authoritative recovery is required before reset.");
            if (_stationState is not (AutomationStationState.Completed or AutomationStationState.Faulted or AutomationStationState.Ready))
                return new AutomationControlResult(false, "station_not_resettable", $"Station state '{_stationState}' cannot be reset.");

            _stationState = AutomationStationState.Idle;
            return new AutomationControlResult(true);
        }
    }

    public AutomationControlResult CompleteManualRecovery()
    {
        lock (_gate)
        {
            if (_active is not null)
                return new AutomationControlResult(false, "station_busy", "Cannot complete manual recovery while an execution is active.");
            if (!_recoveryRequired || _stationState != AutomationStationState.Faulted)
                return new AutomationControlResult(false, "recovery_not_pending", "No manual recovery is pending.");

            _recoveryRequired = false;
            _lastRecoveryEvidence = null;
            _stationState = AutomationStationState.Idle;
            return new AutomationControlResult(true);
        }
    }

    public async Task<AutomationRecoveryResult> RecoverAsync(CancellationToken cancellationToken = default)
    {
        AutomationRecoveryEvidence evidence;
        lock (_gate)
        {
            if (_active is not null)
                throw new InvalidOperationException("Cannot recover while an execution is active.");
            if (!_recoveryRequired || _lastRecoveryEvidence is null)
                throw new InvalidOperationException("No automation recovery evidence is pending.");
            if (_stationState != AutomationStationState.Faulted)
                throw new InvalidOperationException($"Station state '{_stationState}' cannot enter recovery.");

            evidence = _lastRecoveryEvidence;
            _stationState = AutomationStationState.Recovering;
        }

        var sequence = evidence.LastJournalSequence;
        await AppendRecoveryEventAsync(
            evidence.ExecutionId,
            Interlocked.Increment(ref sequence),
            AutomationJournalEventType.RecoveryStarted,
            "Authoritative device reconcile started.",
            cancellationToken).ConfigureAwait(false);

        AutomationReconciliationResult reconciliation;
        try
        {
            reconciliation = await _reconciler
                .ReconcileAsync(evidence, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            reconciliation = new AutomationReconciliationResult(false, false, ex.Message);
        }

        AutomationRecoveryDecision decision;
        if (evidence.HasUnknownPhysicalOutcome)
        {
            decision = AutomationRecoveryDecision.ManualIntervention;
        }
        else if (reconciliation.Reconciled && reconciliation.PhysicalStateMatchesExpected)
        {
            decision = evidence.LastSafeCheckpoint is not null
                ? AutomationRecoveryDecision.ResumeSafeCheckpoint
                : AutomationRecoveryDecision.Restart;
        }
        else
        {
            decision = AutomationRecoveryDecision.ManualIntervention;
        }

        lock (_gate)
        {
            if (decision is AutomationRecoveryDecision.ResumeSafeCheckpoint or AutomationRecoveryDecision.Restart)
            {
                _recoveryRequired = false;
                _lastRecoveryEvidence = null;
                _stationState = AutomationStationState.Ready;
            }
            else
            {
                _stationState = AutomationStationState.Faulted;
            }
        }

        await AppendRecoveryEventAsync(
            evidence.ExecutionId,
            Interlocked.Increment(ref sequence),
            AutomationJournalEventType.RecoveryCompleted,
            $"Recovery decision={decision}; reconcile={reconciliation.Reconciled}; match={reconciliation.PhysicalStateMatchesExpected}; detail={reconciliation.Detail}",
            cancellationToken).ConfigureAwait(false);

        return new AutomationRecoveryResult(
            decision,
            reconciliation,
            evidence.LastSafeCheckpoint,
            reconciliation.Detail);
    }

    public void SignalHostStopping()
    {
        ActiveExecution? active;
        TaskCompletionSource<bool>? resume = null;

        lock (_gate)
            active = _active;

        if (active is null)
            return;

        lock (active.Gate)
        {
            if (IsTerminal(active.State))
                return;

            active.Request = ControlRequest.Abort;
            active.State = AutomationExecutionState.AbortRequested;
            resume = active.ResumeSignal;
        }

        SafeCancel(active.AbortCts);
        resume?.TrySetResult(true);
    }

    public async Task WaitForHostStopAsync(CancellationToken cancellationToken = default)
    {
        SignalHostStopping();

        Task? runTask;
        lock (_gate)
            runTask = _active?.RunTask;

        if (runTask is not null)
            await runTask.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        await WaitForHostStopAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task<AutomationControlResult> RequestPauseAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AutomationControlResult result;

        lock (active.Gate)
        {
            if (IsTerminal(active.State))
                return new AutomationControlResult(false, "execution_terminal", $"Execution is already '{active.State}'.");
            if (active.Request >= ControlRequest.Stop)
                return new AutomationControlResult(false, "stronger_request_pending", "Stop/Abort is already pending.");

            active.Request = ControlRequest.Pause;
            if (active.State == AutomationExecutionState.Running)
                active.State = AutomationExecutionState.PauseRequested;
            result = new AutomationControlResult(true);
        }

        await RecordControlRequestAsync(active, "pause", cancellationToken).ConfigureAwait(false);
        return result;
    }

    internal async Task<AutomationControlResult> RequestResumeAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TaskCompletionSource<bool>? resume = null;
        bool stationWasPaused;
        lock (active.Gate)
        {
            if (active.Request != ControlRequest.Pause &&
                active.State is not (AutomationExecutionState.PauseRequested or AutomationExecutionState.Paused))
            {
                return new AutomationControlResult(false, "pause_not_pending", "Execution is not paused or waiting to pause.");
            }

            if (active.Request >= ControlRequest.Stop)
                return new AutomationControlResult(false, "stronger_request_pending", "Stop/Abort is already pending.");

            stationWasPaused = active.State == AutomationExecutionState.Paused;
            active.Request = ControlRequest.None;
            active.State = AutomationExecutionState.Running;
            resume = active.ResumeSignal;
            active.ResumeSignal = null;
        }

        if (stationWasPaused)
            await TransitionStationAsync(active, AutomationStationState.Running, "resume", cancellationToken).ConfigureAwait(false);

        resume?.TrySetResult(true);
        await RecordControlRequestAsync(active, "resume", cancellationToken).ConfigureAwait(false);
        return new AutomationControlResult(true);
    }

    internal async Task<AutomationControlResult> RequestStopAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CancellationTokenSource> toCancel;
        TaskCompletionSource<bool>? resume;

        lock (active.Gate)
        {
            if (IsTerminal(active.State))
                return new AutomationControlResult(false, "execution_terminal", $"Execution is already '{active.State}'.");
            if (active.Request == ControlRequest.Abort)
                return new AutomationControlResult(false, "abort_pending", "Abort is already pending.");

            active.Request = ControlRequest.Stop;
            active.State = AutomationExecutionState.StopRequested;
            toCancel = active.CurrentCancellations.Values
                .Where(static item => item.CancelOnStop)
                .Select(static item => item.Source)
                .ToList();
            resume = active.ResumeSignal;
        }

        foreach (var cts in toCancel)
            SafeCancel(cts);
        resume?.TrySetResult(true);

        await RecordControlRequestAsync(active, "stop", cancellationToken).ConfigureAwait(false);
        return new AutomationControlResult(true);
    }

    internal async Task<AutomationControlResult> RequestAbortAsync(
        ActiveExecution active,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TaskCompletionSource<bool>? resume;

        lock (active.Gate)
        {
            if (IsTerminal(active.State))
                return new AutomationControlResult(false, "execution_terminal", $"Execution is already '{active.State}'.");

            active.Request = ControlRequest.Abort;
            active.State = AutomationExecutionState.AbortRequested;
            resume = active.ResumeSignal;
        }

        SafeCancel(active.AbortCts);
        resume?.TrySetResult(true);

        await RecordControlRequestAsync(active, "abort", cancellationToken).ConfigureAwait(false);
        return new AutomationControlResult(true);
    }

    private async Task RunExecutionAsync(
        ActiveExecution active,
        AutomationStationState previousStationState)
    {
        AutomationStepResult rootResult = AutomationStepResult.Failure("Execution did not start.");
        Exception? fatal = null;

        try
        {
            await JournalAsync(
                active,
                AutomationJournalEventType.ExecutionStarted,
                detail: $"workflow={active.Plan.WorkflowId}@{active.Plan.WorkflowVersion}; recipe={active.Recipe.RecipeId}@{active.Recipe.Version}; hash={active.Recipe.Hash}")
                .ConfigureAwait(false);
            await JournalStationTransitionAsync(active, previousStationState, AutomationStationState.Preparing, "start")
                .ConfigureAwait(false);

            lock (active.Gate)
                active.State = AutomationExecutionState.Running;
            await TransitionStationAsync(active, AutomationStationState.Running, "prepared", active.AbortCts.Token)
                .ConfigureAwait(false);

            var nodeResult = await ExecuteNodeAsync(
                active.Plan.Root,
                active,
                active.Data,
                active.AbortCts.Token).ConfigureAwait(false);
            rootResult = nodeResult.Result;
        }
        catch (OperationCanceledException) when (active.AbortCts.IsCancellationRequested)
        {
            fatal = active.JournalFault;
            rootResult = fatal is null
                ? new AutomationStepResult(AutomationStepStatus.CancelledBeforeSideEffect, "Execution aborted.")
                : AutomationStepResult.Failure("Execution aborted because journal persistence failed.", fatal);
        }
        catch (Exception ex)
        {
            fatal = ex;
            rootResult = AutomationStepResult.Failure(ex.Message, ex);
        }

        var endedAt = _timeProvider.GetUtcNow();
        var duration = _timeProvider.GetElapsedTime(active.StartedTimestamp);
        AutomationExecutionState finalState;
        AutomationStationState finalStationState;

        lock (active.Gate)
        {
            if (active.Request == ControlRequest.Stop &&
                rootResult.Status == AutomationStepStatus.CancelledBeforeSideEffect)
            {
                rootResult = rootResult with
                {
                    Status = AutomationStepStatus.Stopped,
                    Message = rootResult.Message ?? "Orderly stop cancelled the current step."
                };
            }
        }

        var recoveryRequired =
            rootResult.Status is AutomationStepStatus.UnknownPhysicalOutcome or AutomationStepStatus.RecoveryRequired;

        lock (active.Gate)
        {
            if (recoveryRequired)
            {
                active.HasUnknownPhysicalOutcome |= rootResult.Status == AutomationStepStatus.UnknownPhysicalOutcome;
                active.State = AutomationExecutionState.RecoveryRequired;
            }
            else if (active.Request == ControlRequest.Abort && rootResult.Status == AutomationStepStatus.CancelledBeforeSideEffect)
            {
                active.State = AutomationExecutionState.Aborted;
            }
            else if (rootResult.Status == AutomationStepStatus.Stopped)
            {
                active.State = AutomationExecutionState.Stopped;
            }
            else if (rootResult.IsSuccess)
            {
                active.State = AutomationExecutionState.Completed;
            }
            else
            {
                active.State = AutomationExecutionState.Failed;
            }

            finalState = active.State;
        }

        try
        {
            if (finalState is AutomationExecutionState.Completed or AutomationExecutionState.Stopped)
            {
                var current = StationState;
                if (current is AutomationStationState.Running or AutomationStationState.Paused)
                    await TransitionStationAsync(active, AutomationStationState.Stopping, "terminalizing", CancellationToken.None).ConfigureAwait(false);
                await TransitionStationAsync(active, AutomationStationState.Completed, finalState.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await TransitionStationAsync(active, AutomationStationState.Faulted, finalState.ToString(), CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            fatal ??= ex;
            lock (_gate)
                _stationState = AutomationStationState.Faulted;
            finalState = AutomationExecutionState.Failed;
        }

        finalStationState = StationState;

        List<string> commandIds;
        lock (active.Gate)
            commandIds = active.CommandExecutionIds.Order(StringComparer.Ordinal).ToList();

        var stepOutcomes = active.StepOutcomes
            .OrderBy(static outcome => outcome.Sequence)
            .ToArray();

        var failureReason = rootResult.Message ?? fatal?.Message;
        AutomationRecoveryEvidence? evidence = null;

        try
        {
            await JournalAsync(
                active,
                AutomationJournalEventType.ExecutionCompleted,
                stepStatus: rootResult.Status,
                detail: $"state={finalState}; station={finalStationState}; recoveryRequired={recoveryRequired}; reason={failureReason}")
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            fatal ??= ex;
        }

        if (recoveryRequired)
        {
            evidence = new AutomationRecoveryEvidence(
                active.ExecutionId,
                active.Plan.WorkflowId,
                active.Plan.WorkflowVersion,
                active.Recipe.Hash,
                active.LastSafeCheckpoint,
                active.HasUnknownPhysicalOutcome,
                commandIds,
                active.Plan.RequiredDeviceIds,
                failureReason,
                active.JournalSequence);
        }

        var result = new AutomationExecutionResult(
            active.ExecutionId,
            active.Plan.WorkflowId,
            active.Plan.WorkflowVersion,
            active.Recipe.Hash,
            finalState,
            finalStationState,
            rootResult.Status,
            active.StartedAt,
            endedAt,
            duration,
            stepOutcomes,
            commandIds,
            active.Data,
            recoveryRequired,
            failureReason);

        lock (_gate)
        {
            if (ReferenceEquals(_active, active))
                _active = null;

            if (evidence is not null)
            {
                _recoveryRequired = true;
                _lastRecoveryEvidence = evidence;
            }
        }

        lock (active.Gate)
        {
            foreach (var registration in active.CurrentCancellations.Values)
                SafeCancel(registration.Source);
            active.CurrentCancellations.Clear();
        }

        active.AbortCts.Dispose();
        active.JournalGate.Dispose();
        active.Completion.TrySetResult(result);
    }

    private async Task<NodeExecutionResult> ExecuteNodeAsync(
        CompiledAutomationNode node,
        ActiveExecution active,
        AutomationExecutionData data,
        CancellationToken cancellationToken)
    {
        var boundary = await ObserveControlAtBoundaryAsync(active, safePauseBoundary: false, cancellationToken)
            .ConfigureAwait(false);
        if (boundary is not null)
            return new NodeExecutionResult(boundary, data);

        return node switch
        {
            CompiledActionNode action => new NodeExecutionResult(
                await ExecuteActionAsync(action, active, data, cancellationToken).ConfigureAwait(false),
                data),
            CompiledSequenceNode sequence => await ExecuteSequenceAsync(sequence, active, data, cancellationToken).ConfigureAwait(false),
            CompiledParallelNode parallel => await ExecuteParallelAsync(parallel, active, data, cancellationToken).ConfigureAwait(false),
            CompiledCheckpointNode checkpoint => await ExecuteCheckpointAsync(checkpoint, active, data, cancellationToken).ConfigureAwait(false),
            _ => new NodeExecutionResult(
                AutomationStepResult.Failure($"Unsupported compiled node '{node.GetType().FullName}'."),
                data)
        };
    }

    private async Task<NodeExecutionResult> ExecuteSequenceAsync(
        CompiledSequenceNode sequence,
        ActiveExecution active,
        AutomationExecutionData data,
        CancellationToken cancellationToken)
    {
        foreach (var child in sequence.Children)
        {
            var result = await ExecuteNodeAsync(child, active, data, cancellationToken).ConfigureAwait(false);
            if (!result.Result.IsSuccess)
                return result;
        }

        return new NodeExecutionResult(AutomationStepResult.Success(), data);
    }

    private async Task<NodeExecutionResult> ExecuteParallelAsync(
        CompiledParallelNode parallel,
        ActiveExecution active,
        AutomationExecutionData data,
        CancellationToken cancellationToken)
    {
        using var failFastCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var limiter = new SemaphoreSlim(parallel.MaxConcurrency, parallel.MaxConcurrency);

        var tasks = parallel.Children
            .Select((child, index) => RunParallelChildAsync(child, index, active, data, limiter, failFastCts.Token))
            .ToList();

        var results = new List<ParallelChildResult>(tasks.Count);
        var pending = new List<Task<ParallelChildResult>>(tasks);

        while (pending.Count > 0)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            var childResult = await completed.ConfigureAwait(false);
            results.Add(childResult);

            if (parallel.JoinMode == AutomationJoinMode.FailFast &&
                !childResult.Result.IsSuccess)
            {
                SafeCancel(failFastCts);
            }
        }

        var ordered = results.OrderBy(static result => result.Index).ToArray();
        var unknown = ordered.FirstOrDefault(static result =>
            result.Result.Status is AutomationStepStatus.UnknownPhysicalOutcome or AutomationStepStatus.RecoveryRequired);
        if (unknown is not null)
            return new NodeExecutionResult(unknown.Result, data);

        var failure = ordered.FirstOrDefault(static result =>
            !result.Result.IsSuccess &&
            result.Result.Status != AutomationStepStatus.CancelledBeforeSideEffect);
        if (failure is not null)
            return new NodeExecutionResult(failure.Result, data);

        var cancelled = ordered.FirstOrDefault(static result => !result.Result.IsSuccess);
        if (cancelled is not null)
            return new NodeExecutionResult(cancelled.Result, data);

        try
        {
            foreach (var child in ordered)
                data.MergeFrom(child.Data);
        }
        catch (Exception ex)
        {
            return new NodeExecutionResult(
                AutomationStepResult.Failure("Parallel output merge failed.", ex),
                data);
        }

        return new NodeExecutionResult(AutomationStepResult.Success(), data);
    }

    private async Task<ParallelChildResult> RunParallelChildAsync(
        CompiledAutomationNode child,
        int index,
        ActiveExecution active,
        AutomationExecutionData parentData,
        SemaphoreSlim limiter,
        CancellationToken cancellationToken)
    {
        var entered = false;
        try
        {
            await limiter.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            var branchData = parentData.Fork();
            var result = await ExecuteNodeAsync(child, active, branchData, cancellationToken).ConfigureAwait(false);
            return new ParallelChildResult(index, result.Result, branchData);
        }
        catch (OperationCanceledException)
        {
            return new ParallelChildResult(
                index,
                new AutomationStepResult(AutomationStepStatus.CancelledBeforeSideEffect, "Parallel branch cancelled."),
                parentData.Fork());
        }
        finally
        {
            if (entered)
                limiter.Release();
        }
    }

    private async Task<NodeExecutionResult> ExecuteCheckpointAsync(
        CompiledCheckpointNode checkpoint,
        ActiveExecution active,
        AutomationExecutionData data,
        CancellationToken cancellationToken)
    {
        if (checkpoint.Kind == AutomationCheckpointKind.SafeRecovery)
        {
            lock (active.Gate)
                active.LastSafeCheckpoint = checkpoint.Id;
        }

        await JournalAsync(
            active,
            AutomationJournalEventType.CheckpointReached,
            nodeId: checkpoint.Id,
            detail: checkpoint.Kind.ToString(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var boundary = await ObserveControlAtBoundaryAsync(active, safePauseBoundary: true, cancellationToken)
            .ConfigureAwait(false);

        return new NodeExecutionResult(boundary ?? AutomationStepResult.Success(), data);
    }

    private async Task<AutomationStepResult> ExecuteActionAsync(
        CompiledActionNode action,
        ActiveExecution active,
        AutomationExecutionData data,
        CancellationToken cancellationToken)
    {
        var startedAt = _timeProvider.GetUtcNow();
        var stepSequence = Interlocked.Increment(ref active.StepSequence);
        var policy = action.Step.Policy;
        var retry = policy.Retry ?? AutomationRetryPolicy.None;
        var resources = policy.NormalizeResources();
        var allCommandIds = new List<string>();

        await JournalAsync(
            active,
            AutomationJournalEventType.StepStarted,
            nodeId: action.Id,
            detail: $"resources={resources.Count}",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var preconditionContext = new AutomationStepContext(
            active.ExecutionId,
            action.Id,
            active.Mode,
            active.Recipe,
            data.Fork(),
            resources);

        if (action.Step.Precondition is not null)
        {
            bool allowed;
            try
            {
                allowed = await action.Step.Precondition(preconditionContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var failed = AutomationStepResult.Failure($"Precondition for '{action.Id}' failed.", ex);
                await CompleteStepAsync(active, action.Id, stepSequence, 0, startedAt, failed, allCommandIds, cancellationToken)
                    .ConfigureAwait(false);
                return failed;
            }

            if (!allowed)
            {
                var rejected = new AutomationStepResult(
                    AutomationStepStatus.Rejected,
                    $"Precondition for '{action.Id}' rejected execution.");
                await CompleteStepAsync(active, action.Id, stepSequence, 0, startedAt, rejected, allCommandIds, cancellationToken)
                    .ConfigureAwait(false);
                return rejected;
            }
        }

        var attempts = 0;
        AutomationStepResult final = AutomationStepResult.Failure($"Step '{action.Id}' did not execute.");

        while (attempts < retry.MaxAttempts)
        {
            attempts++;
            var attemptData = data.Fork();
            var context = new AutomationStepContext(
                active.ExecutionId,
                action.Id,
                active.Mode,
                active.Recipe,
                attemptData,
                resources);

            var attempt = await ExecuteActionAttemptAsync(
                action,
                active,
                context,
                resources,
                cancellationToken).ConfigureAwait(false);

            allCommandIds.AddRange(context.CommandExecutionIds);
            RegisterCommandIds(active, context.CommandExecutionIds);
            final = attempt;

            if (attempt.IsSuccess)
            {
                try
                {
                    data.MergeFrom(attemptData);
                }
                catch (Exception ex)
                {
                    final = AutomationStepResult.Failure($"Output merge for '{action.Id}' failed.", ex);
                }
                break;
            }

            if (attempt.Status is AutomationStepStatus.UnknownPhysicalOutcome or AutomationStepStatus.RecoveryRequired)
            {
                lock (active.Gate)
                {
                    active.HasUnknownPhysicalOutcome |= attempt.Status == AutomationStepStatus.UnknownPhysicalOutcome;
                }
                break;
            }

            if (!retry.ShouldRetry(attempt.Status) || attempts >= retry.MaxAttempts)
                break;

            var control = await ObserveControlAtBoundaryAsync(
                active,
                safePauseBoundary: false,
                cancellationToken).ConfigureAwait(false);
            if (control is not null)
            {
                final = control;
                break;
            }

            if (retry.Delay > TimeSpan.Zero)
            {
                using var retryDelayCts = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    active.AbortCts.Token);
                var retryRegistration = RegisterCurrentCancellation(
                    active,
                    retryDelayCts,
                    cancelOnStop: true);
                try
                {
                    await Task.Delay(retry.Delay, _timeProvider, retryDelayCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException ex)
                {
                    ControlRequest request;
                    lock (active.Gate)
                        request = active.Request;

                    final = request == ControlRequest.Stop
                        ? new AutomationStepResult(
                            AutomationStepStatus.Stopped,
                            "Orderly stop cancelled retry backoff.",
                            ex)
                        : new AutomationStepResult(
                            AutomationStepStatus.CancelledBeforeSideEffect,
                            "Retry backoff was cancelled.",
                            ex);
                    break;
                }
                finally
                {
                    UnregisterCurrentCancellation(active, retryRegistration);
                }
            }
        }

        await CompleteStepAsync(
            active,
            action.Id,
            stepSequence,
            attempts,
            startedAt,
            final,
            allCommandIds,
            CancellationToken.None).ConfigureAwait(false);

        if (final.IsSuccess && policy.PauseBoundaryAfter)
        {
            var boundary = await ObserveControlAtBoundaryAsync(active, safePauseBoundary: true, cancellationToken)
                .ConfigureAwait(false);
            if (boundary is not null)
                return boundary;
        }

        return final;
    }

    private async Task<AutomationStepResult> ExecuteActionAttemptAsync(
        CompiledActionNode action,
        ActiveExecution active,
        AutomationStepContext context,
        IReadOnlyList<CommandResourceClaim> resources,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            active.AbortCts.Token,
            timeoutCts.Token);

        using var timer = action.Step.Policy.Timeout is { } timeout
            ? _timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                timeoutCts,
                timeout,
                Timeout.InfiniteTimeSpan)
            : null;

        var registrationId = RegisterCurrentCancellation(active, linked, cancelOnStop: true);
        IAsyncDisposable? lease = null;
        var acquired = false;
        var resourceWaitStarted = _timeProvider.GetTimestamp();

        try
        {
            lease = await _resourceArbiter.AcquireAsync(resources, linked.Token).ConfigureAwait(false);
            acquired = true;
            UpdateCurrentCancellation(active, registrationId, action.Step.Policy.CancelOnStop);

            var result = await action.Step.Execute(context, linked.Token).ConfigureAwait(false);
            if (result.CommandExecutionId is not null &&
                !context.CommandExecutionIds.Contains(result.CommandExecutionId, StringComparer.Ordinal))
            {
                RegisterCommandIds(active, [result.CommandExecutionId]);
            }

            if (!result.IsSuccess &&
                result.Status is not (AutomationStepStatus.UnknownPhysicalOutcome or AutomationStepStatus.RecoveryRequired) &&
                action.Step.Compensation is not null)
            {
                var compensation = await action.Step.Compensation(context, linked.Token).ConfigureAwait(false);
                if (!compensation.IsSuccess)
                {
                    result = new AutomationStepResult(
                        AutomationStepStatus.RecoveryRequired,
                        $"Step '{action.Id}' failed and compensation also failed: {compensation.Message}",
                        compensation.Exception,
                        compensation.CommandExecutionId);
                }
            }

            return result;
        }
        catch (OperationCanceledException ex)
        {
            if (timeoutCts.IsCancellationRequested)
            {
                return new AutomationStepResult(
                    AutomationStepStatus.TimedOutBeforeSideEffect,
                    $"Step '{action.Id}' exceeded its timeout before a reported device outcome.",
                    ex);
            }

            ControlRequest request;
            lock (active.Gate)
                request = active.Request;

            return new AutomationStepResult(
                AutomationStepStatus.CancelledBeforeSideEffect,
                request == ControlRequest.Stop
                    ? $"Step '{action.Id}' was cancelled by orderly stop before a reported device outcome."
                    : $"Step '{action.Id}' was cancelled before a reported device outcome.",
                ex);
        }
        catch (Exception ex)
        {
            return AutomationStepResult.Failure($"Step '{action.Id}' threw an exception.", ex);
        }
        finally
        {
            UnregisterCurrentCancellation(active, registrationId);

            if (lease is not null)
                await lease.DisposeAsync().ConfigureAwait(false);

            if (acquired && resources.Count > 0)
            {
                var wait = _timeProvider.GetElapsedTime(resourceWaitStarted);
                await JournalAsync(
                    active,
                    AutomationJournalEventType.ResourceAcquired,
                    nodeId: action.Id,
                    detail: $"claims={resources.Count}; waitMs={wait.TotalMilliseconds:0.###}",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
                await JournalAsync(
                    active,
                    AutomationJournalEventType.ResourceReleased,
                    nodeId: action.Id,
                    detail: $"claims={resources.Count}",
                    cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task CompleteStepAsync(
        ActiveExecution active,
        string nodeId,
        long sequence,
        int attempts,
        DateTimeOffset startedAt,
        AutomationStepResult result,
        IReadOnlyList<string> commandIds,
        CancellationToken cancellationToken)
    {
        var endedAt = _timeProvider.GetUtcNow();
        var distinctCommands = commandIds.Distinct(StringComparer.Ordinal).ToArray();

        active.StepOutcomes.Enqueue(
            new AutomationStepOutcome(
                sequence,
                nodeId,
                result.Status,
                attempts,
                startedAt,
                endedAt,
                result.Message,
                distinctCommands));

        await JournalAsync(
            active,
            AutomationJournalEventType.StepCompleted,
            nodeId,
            result.Status,
            result.CommandExecutionId,
            detail: $"attempts={attempts}; message={result.Message}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<AutomationStepResult?> ObserveControlAtBoundaryAsync(
        ActiveExecution active,
        bool safePauseBoundary,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            ControlRequest request;
            Task resumeTask = Task.CompletedTask;
            bool enterPause = false;
            bool enterStop = false;

            lock (active.Gate)
            {
                request = active.Request;

                if (request == ControlRequest.Abort)
                {
                    active.State = AutomationExecutionState.Aborting;
                }
                else if (request == ControlRequest.Stop)
                {
                    active.State = AutomationExecutionState.Stopping;
                    enterStop = true;
                }
                else if (request == ControlRequest.Pause && safePauseBoundary)
                {
                    active.State = AutomationExecutionState.Paused;
                    active.ResumeSignal ??= new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    resumeTask = active.ResumeSignal.Task;
                    enterPause = true;
                }
                else if (request == ControlRequest.None &&
                         active.State == AutomationExecutionState.PauseRequested)
                {
                    active.State = AutomationExecutionState.Running;
                }
            }

            if (request == ControlRequest.Abort)
                throw new OperationCanceledException(active.AbortCts.Token);

            if (enterStop)
            {
                var current = StationState;
                if (current is AutomationStationState.Running or AutomationStationState.Paused)
                    await TransitionStationAsync(active, AutomationStationState.Stopping, "stop-request", CancellationToken.None).ConfigureAwait(false);

                return new AutomationStepResult(AutomationStepStatus.Stopped, "Orderly stop reached an execution boundary.");
            }

            if (!enterPause)
                return null;

            if (StationState != AutomationStationState.Paused)
                await TransitionStationAsync(active, AutomationStationState.Paused, "safe-pause-boundary", cancellationToken).ConfigureAwait(false);

            await resumeTask.WaitAsync(active.AbortCts.Token).ConfigureAwait(false);
        }
    }

    private Guid RegisterCurrentCancellation(
        ActiveExecution active,
        CancellationTokenSource source,
        bool cancelOnStop)
    {
        var id = Guid.NewGuid();
        lock (active.Gate)
            active.CurrentCancellations[id] = new CurrentCancellation(source, cancelOnStop);
        return id;
    }

    private void UpdateCurrentCancellation(
        ActiveExecution active,
        Guid id,
        bool cancelOnStop)
    {
        bool shouldCancel = false;
        CancellationTokenSource? source = null;

        lock (active.Gate)
        {
            if (!active.CurrentCancellations.TryGetValue(id, out var current))
                return;

            current.CancelOnStop = cancelOnStop;
            if (cancelOnStop && active.Request >= ControlRequest.Stop)
            {
                shouldCancel = true;
                source = current.Source;
            }
        }

        if (shouldCancel && source is not null)
            SafeCancel(source);
    }

    private static void UnregisterCurrentCancellation(ActiveExecution active, Guid id)
    {
        lock (active.Gate)
            active.CurrentCancellations.Remove(id);
    }

    private static void RegisterCommandIds(
        ActiveExecution active,
        IEnumerable<string> commandIds)
    {
        lock (active.Gate)
        {
            foreach (var id in commandIds)
            {
                if (!string.IsNullOrWhiteSpace(id))
                    active.CommandExecutionIds.Add(id);
            }
        }
    }

    private async Task RecordControlRequestAsync(
        ActiveExecution active,
        string request,
        CancellationToken cancellationToken)
    {
        try
        {
            await JournalAsync(
                active,
                AutomationJournalEventType.ControlRequested,
                detail: request,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            lock (active.Gate)
                active.JournalFault = ex;
            SafeCancel(active.AbortCts);
        }
    }

    private async Task TransitionStationAsync(
        ActiveExecution active,
        AutomationStationState to,
        string detail,
        CancellationToken cancellationToken)
    {
        AutomationStationState from;

        lock (_gate)
        {
            from = _stationState;
            if (from == to)
                return;

            if (!IsStationTransitionAllowed(from, to))
                throw new InvalidOperationException($"Illegal station transition '{from}' -> '{to}'.");

            _stationState = to;
        }

        await JournalStationTransitionAsync(active, from, to, detail, cancellationToken).ConfigureAwait(false);
    }

    private Task JournalStationTransitionAsync(
        ActiveExecution active,
        AutomationStationState from,
        AutomationStationState to,
        string detail,
        CancellationToken cancellationToken = default) =>
        JournalAsync(
            active,
            AutomationJournalEventType.StationTransition,
            stationFrom: from,
            stationTo: to,
            detail: detail,
            cancellationToken: cancellationToken);

    private async Task JournalAsync(
        ActiveExecution active,
        AutomationJournalEventType eventType,
        string? nodeId = null,
        AutomationStepStatus? stepStatus = null,
        string? commandExecutionId = null,
        AutomationStationState? stationFrom = null,
        AutomationStationState? stationTo = null,
        string? detail = null,
        CancellationToken cancellationToken = default)
    {
        await active.JournalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var sequence = ++active.JournalSequence;
            var journalEvent = new AutomationJournalEvent(
                active.ExecutionId,
                sequence,
                _timeProvider.GetUtcNow(),
                eventType,
                nodeId,
                stepStatus,
                commandExecutionId,
                stationFrom,
                stationTo,
                detail);

            await _journal.AppendAsync(journalEvent, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            active.JournalGate.Release();
        }
    }

    private async Task AppendRecoveryEventAsync(
        string executionId,
        long sequence,
        AutomationJournalEventType eventType,
        string detail,
        CancellationToken cancellationToken)
    {
        await _journal.AppendAsync(
            new AutomationJournalEvent(
                executionId,
                sequence,
                _timeProvider.GetUtcNow(),
                eventType,
                Detail: detail),
            cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTerminal(AutomationExecutionState state) =>
        state is AutomationExecutionState.Completed
            or AutomationExecutionState.Failed
            or AutomationExecutionState.Aborted
            or AutomationExecutionState.Stopped
            or AutomationExecutionState.RecoveryRequired;

    private static bool IsStationTransitionAllowed(
        AutomationStationState from,
        AutomationStationState to) =>
        (from, to) switch
        {
            (AutomationStationState.Idle, AutomationStationState.Preparing) => true,
            (AutomationStationState.Ready, AutomationStationState.Preparing) => true,
            (AutomationStationState.Preparing, AutomationStationState.Running) => true,
            (AutomationStationState.Preparing, AutomationStationState.Faulted) => true,
            (AutomationStationState.Running, AutomationStationState.Paused) => true,
            (AutomationStationState.Running, AutomationStationState.Stopping) => true,
            (AutomationStationState.Running, AutomationStationState.Completed) => true,
            (AutomationStationState.Running, AutomationStationState.Faulted) => true,
            (AutomationStationState.Paused, AutomationStationState.Running) => true,
            (AutomationStationState.Paused, AutomationStationState.Stopping) => true,
            (AutomationStationState.Paused, AutomationStationState.Faulted) => true,
            (AutomationStationState.Stopping, AutomationStationState.Completed) => true,
            (AutomationStationState.Stopping, AutomationStationState.Faulted) => true,
            (AutomationStationState.Faulted, AutomationStationState.Recovering) => true,
            (AutomationStationState.Faulted, AutomationStationState.Idle) => true,
            (AutomationStationState.Recovering, AutomationStationState.Ready) => true,
            (AutomationStationState.Recovering, AutomationStationState.Faulted) => true,
            (AutomationStationState.Completed, AutomationStationState.Idle) => true,
            _ => false
        };

    private static void SafeCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
