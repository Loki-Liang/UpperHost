namespace OpenDeviceStudio.Workflows;

internal sealed class WorkflowExecutionControl : IDisposable
{
    private static readonly HashSet<WorkflowExecutionStatus> TerminalStates =
    [
        WorkflowExecutionStatus.Completed,
        WorkflowExecutionStatus.Failed,
        WorkflowExecutionStatus.Stopped,
        WorkflowExecutionStatus.Aborted,
        WorkflowExecutionStatus.RecoveryRequired
    ];

    private readonly object _gate = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenRegistration _callerCancellation;
    private TaskCompletionSource? _resume;
    private WorkflowExecutionStatus _state = WorkflowExecutionStatus.Created;
    private Task<WorkflowExecutionResult>? _completion;
    private long _journalSequence;
    private string? _lastSafeCheckpoint;
    private int _journalFailures;
    private int _disposed;

    public WorkflowExecutionControl(
        string executionId,
        CancellationToken callerCancellation)
    {
        ExecutionId = executionId;
        _callerCancellation = callerCancellation.Register(
            static state => ((WorkflowExecutionControl)state!).RequestAbort(),
            this);
    }

    public string ExecutionId { get; }
    public CancellationToken AbortToken => _abort.Token;
    public CancellationToken StopToken => _stop.Token;

    public Task<WorkflowExecutionResult>? Completion
    {
        get
        {
            lock (_gate)
                return _completion;
        }
    }

    public WorkflowExecutionStatus State
    {
        get
        {
            lock (_gate)
                return _state;
        }
    }

    public string? LastSafeCheckpoint
    {
        get
        {
            lock (_gate)
                return _lastSafeCheckpoint;
        }
    }

    public int JournalFailureCount => Volatile.Read(ref _journalFailures);

    public bool IsAbortRequested => State == WorkflowExecutionStatus.AbortRequested;

    public bool IsStopRequested =>
        State is WorkflowExecutionStatus.StopRequested or WorkflowExecutionStatus.Stopping;

    public void AttachCompletion(Task<WorkflowExecutionResult> completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        lock (_gate)
            _completion = completion;
    }

    public bool RequestPause()
    {
        lock (_gate)
        {
            if (_state == WorkflowExecutionStatus.PauseRequested)
                return true;

            if (_state != WorkflowExecutionStatus.Running)
                return false;

            _state = WorkflowExecutionStatus.PauseRequested;
            return true;
        }
    }

    public bool RequestResume()
    {
        TaskCompletionSource? resume = null;

        lock (_gate)
        {
            if (_state == WorkflowExecutionStatus.PauseRequested)
            {
                _state = WorkflowExecutionStatus.Running;
                return true;
            }

            if (_state != WorkflowExecutionStatus.Paused)
                return false;

            _state = WorkflowExecutionStatus.Running;
            resume = _resume;
            _resume = null;
        }

        resume?.TrySetResult();
        return true;
    }

    public bool RequestStop()
    {
        TaskCompletionSource? resume;

        lock (_gate)
        {
            if (TerminalStates.Contains(_state))
                return false;

            if (_state == WorkflowExecutionStatus.AbortRequested)
                return false;

            if (_state is WorkflowExecutionStatus.StopRequested or WorkflowExecutionStatus.Stopping)
                return true;

            _state = WorkflowExecutionStatus.StopRequested;
            resume = _resume;
            _resume = null;
        }

        _stop.Cancel();
        resume?.TrySetResult();
        return true;
    }

    public bool RequestAbort()
    {
        TaskCompletionSource? resume;

        lock (_gate)
        {
            if (TerminalStates.Contains(_state))
                return false;

            if (_state == WorkflowExecutionStatus.AbortRequested)
                return true;

            _state = WorkflowExecutionStatus.AbortRequested;
            resume = _resume;
            _resume = null;
        }

        _stop.Cancel();
        _abort.Cancel();
        resume?.TrySetResult();
        return true;
    }

    public bool TrySetRuntimeState(
        WorkflowExecutionStatus target,
        out WorkflowExecutionStatus previous)
    {
        lock (_gate)
        {
            previous = _state;

            if (TerminalStates.Contains(_state))
                return false;

            if (_state == WorkflowExecutionStatus.AbortRequested &&
                target is not WorkflowExecutionStatus.Aborted and
                    not WorkflowExecutionStatus.RecoveryRequired and
                    not WorkflowExecutionStatus.Failed)
            {
                return false;
            }

            if ((_state is WorkflowExecutionStatus.StopRequested or WorkflowExecutionStatus.Stopping) &&
                target is WorkflowExecutionStatus.Preparing or
                    WorkflowExecutionStatus.Running or
                    WorkflowExecutionStatus.PauseRequested or
                    WorkflowExecutionStatus.Paused)
            {
                return false;
            }

            if (_state == target)
                return false;

            _state = target;
            return true;
        }
    }

    public void ForceTerminalState(WorkflowExecutionStatus terminalState)
    {
        if (!TerminalStates.Contains(terminalState))
            throw new ArgumentOutOfRangeException(nameof(terminalState));

        lock (_gate)
            _state = terminalState;
    }

    public bool TryEnterPaused(out Task resumeTask)
    {
        lock (_gate)
        {
            if (_state != WorkflowExecutionStatus.PauseRequested)
            {
                resumeTask = Task.CompletedTask;
                return false;
            }

            _state = WorkflowExecutionStatus.Paused;
            _resume = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            resumeTask = _resume.Task;
            return true;
        }
    }

    public void SetLastSafeCheckpoint(string nodeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        lock (_gate)
            _lastSafeCheckpoint = nodeId;
    }

    public long NextJournalSequence() =>
        Interlocked.Increment(ref _journalSequence);

    public void RecordJournalFailure() =>
        Interlocked.Increment(ref _journalFailures);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _callerCancellation.Dispose();
        _stop.Dispose();
        _abort.Dispose();
    }
}
