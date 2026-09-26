using System.Collections.Concurrent;
using System.Threading.Channels;

namespace UpperHost.Acquisition;

public sealed class AcquisitionSession : IAsyncDisposable
{
    private sealed class RequiredHandle(AcquisitionRequiredComponentRegistration registration)
    {
        public AcquisitionRequiredComponentRegistration Registration { get; } = registration;
        public AcquisitionComponentRuntimeState State { get; set; } = AcquisitionComponentRuntimeState.Created;
        public string? Error { get; set; }
    }

    private sealed class SourceHandle(IAcquisitionSource source)
    {
        public IAcquisitionSource Source { get; } = source;
        public AcquisitionComponentRuntimeState State { get; set; } = AcquisitionComponentRuntimeState.Created;
        public string? Error { get; set; }
    }

    private sealed class OptionalHandle(AcquisitionOptionalComponentRegistration registration)
    {
        public AcquisitionOptionalComponentRegistration Registration { get; } = registration;
        public AcquisitionComponentRuntimeState State { get; set; } = AcquisitionComponentRuntimeState.Created;
        public string? Error { get; set; }
    }

    private sealed record StartupCleanupHandle(
        IAcquisitionSessionComponent Component,
        string ComponentId,
        string? SourceId,
        AcquisitionComponentKind Kind);

    private enum ConvergenceReason
    {
        Stop,
        Fault,
        Abort
    }

    private readonly AcquisitionSessionDefinition _definition;
    private readonly TimeProvider _timeProvider;
    private readonly Action<AcquisitionSession>? _onTerminal;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _optionalGate = new(1, 1);
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly CancellationTokenSource _sessionStop = new();
    private readonly CancellationTokenSource _abort = new();
    private readonly IReadOnlyDictionary<string, AcquisitionIngressGate> _ingressBySource;
    private readonly TaskCompletionSource<AcquisitionSessionResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentQueue<AcquisitionFault> _secondaryFaults = new();
    private readonly ConcurrentQueue<AcquisitionFault> _optionalFaults = new();
    private readonly ConcurrentQueue<AcquisitionFault> _sourceIsolationFaults = new();
    private readonly Dictionary<string, RequiredHandle> _required;
    private readonly Dictionary<string, SourceHandle> _sources;
    private readonly Dictionary<string, OptionalHandle> _optional;

    private AcquisitionSessionState _state = AcquisitionSessionState.Created;
    private AcquisitionStartupPhase _phase = AcquisitionStartupPhase.None;
    private AcquisitionFault? _rootFault;
    private DateTimeOffset? _startedAt;
    private DateTimeOffset? _endedAt;
    private Task? _supervisor;
    private int _startOnce;
    private int _stopRequested;
    private int _abortRequested;
    private int _convergeOnce;
    private int _terminalOnce;
    private readonly object _disposeGate = new();
    private Task? _disposeTask;
    private int _storedSecondaryFaults;
    private int _droppedSecondaryFaults;
    private int _activeMetric;

    internal AcquisitionSession(
        AcquisitionSessionDefinition definition,
        TimeProvider timeProvider,
        Action<AcquisitionSession>? onTerminal = null)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _onTerminal = onTerminal;
        _ingressBySource = definition.Sources.ToDictionary(
            static source => source.SourceId,
            _ => new AcquisitionIngressGate(definition.Mode),
            StringComparer.Ordinal);

        _required = definition.RequiredComponents.ToDictionary(
            static item => item.Component.ComponentId,
            static item => new RequiredHandle(item),
            StringComparer.Ordinal);
        _sources = definition.Sources.ToDictionary(
            static source => source.SourceId,
            static source => new SourceHandle(source),
            StringComparer.Ordinal);
        _optional = definition.OptionalComponents.ToDictionary(
            static item => item.Component.ComponentId,
            static item => new OptionalHandle(item),
            StringComparer.Ordinal);
    }

    public string SessionId => _definition.SessionId;
    public string ProcessingEpoch => _definition.ProcessingEpoch;
    public AcquisitionSessionMode Mode => _definition.Mode;
    public Task<AcquisitionSessionResult> Completion => _completion.Task;

    public AcquisitionSessionState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    public AcquisitionSessionSnapshot GetSnapshot()
    {
        lock (_stateGate)
        {
            var ready = _required.Values.Count(static item => IsReadyOrBeyond(item.State)) +
                        _sources.Values.Count(static item => IsReadyOrBeyond(item.State));

            return new AcquisitionSessionSnapshot(
                SessionId,
                Mode,
                _state,
                _phase,
                ready,
                _required.Count + _sources.Count,
                _rootFault,
                _ingressBySource.Values.Any(static gate => gate.IsAccepting),
                RejectedLateIngressCount(),
                _startedAt,
                _endedAt);
        }
    }

    public async Task StartAsync(CancellationToken startRequestToken = default)
    {
        if (Interlocked.CompareExchange(ref _startOnce, 1, 0) != 0)
            throw new InvalidOperationException("An acquisition session can only be started once.");

        string currentComponentId = "session";
        string? currentSourceId = null;
        var startupCleanup = new Stack<StartupCleanupHandle>();

        Transition(AcquisitionSessionState.Preparing, AcquisitionStartupPhase.Validating);

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(
            startRequestToken,
            _sessionStop.Token,
            _abort.Token);

        try
        {
            SetPhase(AcquisitionStartupPhase.PreparingRequiredComponents);
            foreach (var handle in _required.Values.OrderBy(EffectivePrepareOrder))
            {
                currentComponentId = handle.Registration.Component.ComponentId;
                currentSourceId = null;
                SetRequiredState(handle, AcquisitionComponentRuntimeState.Preparing);
                startupCleanup.Push(new StartupCleanupHandle(
                    handle.Registration.Component,
                    currentComponentId,
                    null,
                    handle.Registration.Component.Kind));

                var context = CreateContext(currentComponentId, null);
                await handle.Registration.Component
                    .PrepareAsync(context, startupCts.Token)
                    .ConfigureAwait(false);

                SetRequiredState(handle, AcquisitionComponentRuntimeState.Ready);
            }

            SetPhase(AcquisitionStartupPhase.PreparingSources);
            foreach (var handle in _sources.Values)
            {
                startupCts.Token.ThrowIfCancellationRequested();
                currentComponentId = handle.Source.ComponentId;
                currentSourceId = handle.Source.SourceId;
                SetSourceState(handle, AcquisitionComponentRuntimeState.Preparing);
                startupCleanup.Push(new StartupCleanupHandle(
                    handle.Source,
                    currentComponentId,
                    currentSourceId,
                    AcquisitionComponentKind.Source));

                var context = CreateContext(currentComponentId, currentSourceId);
                await handle.Source
                    .PrepareAsync(context, startupCts.Token)
                    .ConfigureAwait(false);

                SetSourceState(handle, AcquisitionComponentRuntimeState.Ready);
            }

            SetPhase(AcquisitionStartupPhase.RequiredReady);
            startupCts.Token.ThrowIfCancellationRequested();
            EnsureRequiredReady();
            Transition(AcquisitionSessionState.Ready, AcquisitionStartupPhase.RequiredReady);
            OpenAllIngress();

            SetPhase(AcquisitionStartupPhase.StartingSources);
            foreach (var handle in _sources.Values)
            {
                currentComponentId = handle.Source.ComponentId;
                currentSourceId = handle.Source.SourceId;

                await handle.Source
                    .StartAsync(startupCts.Token)
                    .ConfigureAwait(false);

                // A Source may report a Required fault and still return normally from StartAsync.
                // Observe the linked startup cancellation before committing the Source/Session to Running.
                startupCts.Token.ThrowIfCancellationRequested();
                SetSourceState(handle, AcquisitionComponentRuntimeState.Running);
            }

            startupCts.Token.ThrowIfCancellationRequested();
            Transition(AcquisitionSessionState.Running, AcquisitionStartupPhase.AttachingOptionalComponents);
            _supervisor = RunSupervisorAsync();

            foreach (var optional in _definition.OptionalComponents)
            {
                await AttachOptionalAsync(optional.Component.ComponentId, CancellationToken.None).ConfigureAwait(false);

                if (_rootFault is not null ||
                    Volatile.Read(ref _stopRequested) != 0 ||
                    Volatile.Read(ref _abortRequested) != 0 ||
                    State != AcquisitionSessionState.Running)
                {
                    break;
                }
            }

            SetPhaseIfState(AcquisitionSessionState.Running, AcquisitionStartupPhase.Running);
        }
        catch (OperationCanceledException ex)
        {
            if (_rootFault is not null)
            {
                await CleanupStartupFailureAsync(
                        AcquisitionSessionState.Faulted,
                        ex,
                        currentComponentId,
                        currentSourceId,
                        startupCleanup)
                    .ConfigureAwait(false);
                return;
            }

            var category = startRequestToken.IsCancellationRequested
                ? AcquisitionFaultCategory.OperatorAbort
                : AcquisitionFaultCategory.OperatorAbort;
            var fault = CreateFault(
                category,
                currentComponentId,
                currentSourceId,
                ex,
                "Acquisition startup was cancelled before the session reached Running.");
            RecordRootOrSecondary(fault, RequiredRole(currentComponentId, currentSourceId));
            Interlocked.Exchange(ref _abortRequested, 1);
            _abort.Cancel();

            await CleanupStartupFailureAsync(
                    AcquisitionSessionState.Aborted,
                    ex,
                    currentComponentId,
                    currentSourceId,
                    startupCleanup)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var category = _phase == AcquisitionStartupPhase.StartingSources
                ? AcquisitionFaultCategory.Source
                : AcquisitionFaultCategory.Preparation;
            var fault = CreateFault(
                category,
                currentComponentId,
                currentSourceId,
                ex,
                ex.Message);
            RecordRootOrSecondary(fault, RequiredRole(currentComponentId, currentSourceId));

            await CleanupStartupFailureAsync(
                    AcquisitionSessionState.Faulted,
                    ex,
                    currentComponentId,
                    currentSourceId,
                    startupCleanup)
                .ConfigureAwait(false);
        }
    }

    public Task<AcquisitionSessionResult> StopAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted)
            return WaitCompletionAsync(cancellationToken);

        if (TryTerminateBeforeStart(AcquisitionFaultCategory.OperatorAbort, "Session stopped before startup."))
            return WaitCompletionAsync(cancellationToken);

        Interlocked.Exchange(ref _stopRequested, 1);
        TryCancel(_sessionStop);
        WakeSupervisor();
        return WaitCompletionAsync(cancellationToken);
    }

    public async Task<AcquisitionSessionResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted)
            return await WaitCompletionAsync(cancellationToken).ConfigureAwait(false);

        if (TryTerminateBeforeStart(AcquisitionFaultCategory.OperatorAbort, "Session aborted before startup."))
            return await WaitCompletionAsync(cancellationToken).ConfigureAwait(false);

        Interlocked.Exchange(ref _abortRequested, 1);
        CloseAllIngress();
        TryCancel(_sessionStop);
        WakeSupervisor();

        // Cancellation callbacks may synchronously cascade through the active
        // convergence token. Dispatch cancellation independently so AbortAsync never
        // occupies that callback chain while waiting for the same Session Completion.
        // The task is still owned and observed after terminal convergence.
        var abortCancellation = Task.Run(
            () => TryCancel(_abort),
            CancellationToken.None);
        var result = await WaitCompletionAsync(cancellationToken).ConfigureAwait(false);
        await abortCancellation
            .WaitAsync(_definition.Options.EffectiveAbortTimeout, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public Task<AcquisitionSessionResult> HostShutdownAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted)
            return WaitCompletionAsync(cancellationToken);

        if (TryTerminateBeforeStart(AcquisitionFaultCategory.HostShutdown, "Host stopped before session startup."))
            return WaitCompletionAsync(cancellationToken);

        Interlocked.Exchange(ref _stopRequested, 1);
        TryCancel(_sessionStop);
        WakeSupervisor();
        return WaitCompletionAsync(cancellationToken);
    }

    public async Task<bool> AttachOptionalAsync(
        string componentId,
        CancellationToken cancellationToken = default)
    {
        if (!_optional.TryGetValue(componentId, out var handle))
            throw new KeyNotFoundException($"Optional acquisition component '{componentId}' is not part of the frozen session definition.");

        var state = State;
        if (state is not (AcquisitionSessionState.Ready or AcquisitionSessionState.Running))
            return false;

        await _optionalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (handle.State == AcquisitionComponentRuntimeState.Running)
                return true;

            if (State is not (AcquisitionSessionState.Ready or AcquisitionSessionState.Running))
                return false;

            SetOptionalState(handle, AcquisitionComponentRuntimeState.Preparing);

            using var attachCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _sessionStop.Token,
                _abort.Token);

            try
            {
                await handle.Registration.Component
                    .AttachAsync(CreateContext(componentId, null), attachCts.Token)
                    .ConfigureAwait(false);
                SetOptionalState(handle, AcquisitionComponentRuntimeState.Running, clearError: true);
                return true;
            }
            catch (OperationCanceledException) when (_sessionStop.IsCancellationRequested || _abort.IsCancellationRequested)
            {
                SetOptionalState(handle, AcquisitionComponentRuntimeState.Ready);
                return false;
            }
            catch (Exception ex)
            {
                SetOptionalFailure(handle, ex);
                ReportFault(componentId, null, AcquisitionFaultCategory.OptionalComponent, ex, ex.Message);
                return false;
            }
        }
        finally
        {
            _optionalGate.Release();
        }
    }

    public async Task DetachOptionalAsync(
        string componentId,
        CancellationToken cancellationToken = default)
    {
        if (!_optional.TryGetValue(componentId, out var handle))
            throw new KeyNotFoundException($"Optional acquisition component '{componentId}' is not part of the frozen session definition.");

        await _optionalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DetachOptionalCoreAsync(handle, cancellationToken, preserveFault: false).ConfigureAwait(false);
        }
        finally
        {
            _optionalGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try
        {
            if (!_completion.Task.IsCompleted)
                await AbortAsync().ConfigureAwait(false);

            if (_supervisor is not null)
                await _supervisor.ConfigureAwait(false);
        }
        finally
        {
            _wake.Writer.TryComplete();
            _sessionStop.Dispose();
            _abort.Dispose();
            _optionalGate.Dispose();
        }
    }

    private async Task RunSupervisorAsync()
    {
        try
        {
            while (!_completion.Task.IsCompleted)
            {
                await _wake.Reader.ReadAsync().ConfigureAwait(false);

                if (Volatile.Read(ref _abortRequested) != 0)
                {
                    await ConvergeAsync(ConvergenceReason.Abort).ConfigureAwait(false);
                    return;
                }

                await DrainSourceIsolationFaultsAsync().ConfigureAwait(false);
                await DrainOptionalFaultsAsync().ConfigureAwait(false);

                if (Volatile.Read(ref _abortRequested) != 0)
                {
                    await ConvergeAsync(ConvergenceReason.Abort).ConfigureAwait(false);
                    return;
                }

                if (_rootFault is not null)
                {
                    await ConvergeAsync(ConvergenceReason.Fault).ConfigureAwait(false);
                    return;
                }

                if (Volatile.Read(ref _stopRequested) != 0)
                {
                    await ConvergeAsync(ConvergenceReason.Stop).ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (ChannelClosedException) when (_completion.Task.IsCompleted)
        {
        }
        catch (Exception ex)
        {
            var fault = CreateFault(
                AcquisitionFaultCategory.Unknown,
                "session-supervisor",
                null,
                ex,
                "Acquisition session supervisor failed.");
            RecordRootOrSecondary(fault, "supervisor");
            Interlocked.Exchange(ref _stopRequested, 1);
            await ConvergeAsync(ConvergenceReason.Fault).ConfigureAwait(false);
        }
    }

    private async Task DrainOptionalFaultsAsync()
    {
        while (_optionalFaults.TryDequeue(out var fault))
        {
            if (!_optional.TryGetValue(fault.ComponentId, out var handle))
                continue;

            await _optionalGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await DetachOptionalCoreAsync(handle, _abort.Token, preserveFault: true).ConfigureAwait(false);
            }
            finally
            {
                _optionalGate.Release();
            }
        }
    }

    private async Task DrainSourceIsolationFaultsAsync()
    {
        while (_sourceIsolationFaults.TryDequeue(out var fault))
        {
            if (fault.SourceId is null || !_sources.TryGetValue(fault.SourceId, out var handle))
                continue;

            if (handle.State is AcquisitionComponentRuntimeState.Isolated
                or AcquisitionComponentRuntimeState.Completed
                or AcquisitionComponentRuntimeState.Aborted)
                continue;

            try
            {
                using var budget = CreateBudget(_definition.Options.EffectiveStopTimeout);
                var ingress = _ingressBySource[handle.Source.SourceId];
                ingress.Close();
                await handle.Source.StopAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
                await ingress.WaitForDrainAsync(budget.Token).ConfigureAwait(false);
                await handle.Source.FinalizeAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
                SetSourceState(handle, AcquisitionComponentRuntimeState.Isolated, fault.Message);

                var isolation = new AcquisitionSourceIsolation(
                    SessionId,
                    handle.Source.SourceId,
                    handle.Source.ConnectionEpoch,
                    fault,
                    ProcessingEpoch);

                foreach (var observer in _definition.SourceIsolationObservers)
                    await observer.SourceIsolatedAsync(isolation, budget.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var escalation = CreateFault(
                    AcquisitionFaultCategory.RequiredBranch,
                    handle.Source.ComponentId,
                    handle.Source.SourceId,
                    ex,
                    "Source isolation could not be propagated safely to dependent processing/quality state.");
                RecordRootOrSecondary(escalation, "source");
                TryCancel(_sessionStop);
                return;
            }

            if (_sources.Values.All(static item =>
                    item.State is AcquisitionComponentRuntimeState.Isolated
                        or AcquisitionComponentRuntimeState.Faulted
                        or AcquisitionComponentRuntimeState.Aborted
                        or AcquisitionComponentRuntimeState.Completed))
            {
                RecordRootOrSecondary(fault, "source");
                TryCancel(_sessionStop);
                return;
            }
        }
    }

    private async Task ConvergeAsync(ConvergenceReason reason)
    {
        if (Interlocked.Exchange(ref _convergeOnce, 1) != 0)
            return;

        var stopStarted = _timeProvider.GetTimestamp();
        TryCancel(_sessionStop);
        CloseAllIngress();

        if (reason == ConvergenceReason.Abort)
        {
            await AbortAndCompleteAsync().ConfigureAwait(false);
            AcquisitionTelemetry.StopDuration.Record(
                _timeProvider.GetElapsedTime(stopStarted).TotalSeconds);
            return;
        }

        if (reason == ConvergenceReason.Fault)
            Transition(AcquisitionSessionState.Faulting, AcquisitionStartupPhase.Stopping);
        else
            Transition(AcquisitionSessionState.Stopping, AcquisitionStartupPhase.Stopping);

        var inFlight = new List<Task>();
        using var deadline = CreateDeadline(_definition.Options.EffectiveStopTimeout);
        // Component operations receive Abort cancellation directly. The stop deadline
        // is an independent signal with asynchronous continuations; the timer callback
        // never cancels component work itself. This prevents TimeProvider callbacks
        // from re-entering the Session convergence state machine synchronously.
        using var convergenceCts = CancellationTokenSource.CreateLinkedTokenSource(_abort.Token);
        var convergenceToken = convergenceCts.Token;

        try
        {
            foreach (var handle in _sources.Values.Reverse())
            {
                if (handle.State is not (AcquisitionComponentRuntimeState.Running
                    or AcquisitionComponentRuntimeState.Ready
                    or AcquisitionComponentRuntimeState.Preparing))
                    continue;

                SetSourceState(handle, AcquisitionComponentRuntimeState.Stopping);
                var ok = await RunRequiredOperationAsync(
                    () => handle.Source.StopAsync(convergenceToken),
                    deadline,
                    inFlight,
                    AcquisitionFaultCategory.Source,
                    handle.Source.ComponentId,
                    handle.Source.SourceId,
                    "Source stop failed.").ConfigureAwait(false);

                if (!ok)
                    SetSourceState(handle, AcquisitionComponentRuntimeState.Faulted);
            }

            await deadline
                .WaitAsync(WaitForAllIngressDrainAsync(convergenceToken))
                .ConfigureAwait(false);
            await deadline
                .WaitAsync(DetachAllOptionalAsync(convergenceToken))
                .ConfigureAwait(false);

            foreach (var handle in _required.Values.OrderBy(EffectiveStopOrder))
            {
                if (handle.State is not (AcquisitionComponentRuntimeState.Ready
                    or AcquisitionComponentRuntimeState.Running
                    or AcquisitionComponentRuntimeState.Preparing))
                    continue;

                SetRequiredState(handle, AcquisitionComponentRuntimeState.Stopping);
                var ok = await RunRequiredOperationAsync(
                    () => handle.Registration.Component.StopAsync(convergenceToken),
                    deadline,
                    inFlight,
                    FaultCategoryFor(handle.Registration.Component.Kind),
                    handle.Registration.Component.ComponentId,
                    null,
                    "Required acquisition component stop/drain failed.").ConfigureAwait(false);

                if (!ok)
                    SetRequiredState(handle, AcquisitionComponentRuntimeState.Faulted);
            }

            SetPhase(AcquisitionStartupPhase.Finalizing);

            foreach (var handle in _required.Values.OrderBy(EffectiveFinalizeOrder))
            {
                if (handle.State == AcquisitionComponentRuntimeState.Faulted)
                    continue;

                var ok = await RunRequiredOperationAsync(
                    () => handle.Registration.Component.FinalizeAsync(convergenceToken),
                    deadline,
                    inFlight,
                    AcquisitionFaultCategory.Finalization,
                    handle.Registration.Component.ComponentId,
                    null,
                    "Required acquisition component finalization failed.").ConfigureAwait(false);

                SetRequiredState(
                    handle,
                    ok && handle.Error is null
                        ? AcquisitionComponentRuntimeState.Completed
                        : AcquisitionComponentRuntimeState.Faulted);
            }

            foreach (var handle in _sources.Values)
            {
                if (handle.State is AcquisitionComponentRuntimeState.Isolated
                    or AcquisitionComponentRuntimeState.Faulted)
                    continue;

                var ok = await RunRequiredOperationAsync(
                    () => handle.Source.FinalizeAsync(convergenceToken),
                    deadline,
                    inFlight,
                    AcquisitionFaultCategory.Finalization,
                    handle.Source.ComponentId,
                    handle.Source.SourceId,
                    "Source finalization failed.").ConfigureAwait(false);

                SetSourceState(
                    handle,
                    ok && handle.Error is null
                        ? AcquisitionComponentRuntimeState.Completed
                        : AcquisitionComponentRuntimeState.Faulted);
            }

            await deadline
                .WaitAsync(DisposeAllAsync(convergenceToken, inFlight))
                .ConfigureAwait(false);

            if (_rootFault is null)
                CompleteTerminal(AcquisitionSessionState.Completed);
            else
            {
                if (State == AcquisitionSessionState.Stopping)
                    Transition(AcquisitionSessionState.Faulting, AcquisitionStartupPhase.Finalizing);
                CompleteTerminal(AcquisitionSessionState.Faulted);
            }
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            await AbortAndCompleteAsync(inFlight).ConfigureAwait(false);
        }
        catch (TimeoutException) when (deadline.IsExpired)
        {
            var timeout = CreateFault(
                AcquisitionFaultCategory.ShutdownTimeout,
                "session-convergence",
                null,
                null,
                "Acquisition stop/finalize exceeded the configured global shutdown budget.");
            RecordRootOrSecondary(timeout, "session");

            // The deadline only releases the supervisor wait. Cancel component work
            // from the normal async supervisor flow, never from the timer callback.
            TryCancel(convergenceCts);
            await AbortAndCompleteAsync(inFlight).ConfigureAwait(false);
        }

        AcquisitionTelemetry.StopDuration.Record(
            _timeProvider.GetElapsedTime(stopStarted).TotalSeconds);
    }

    private async Task AbortAndCompleteAsync(List<Task>? inFlight = null)
    {
        if (State != AcquisitionSessionState.Aborting)
            Transition(AcquisitionSessionState.Aborting, AcquisitionStartupPhase.Aborting);

        Interlocked.Exchange(ref _abortRequested, 1);
        TryCancel(_sessionStop);
        TryCancel(_abort);

        using var budget = CreateBudget(_definition.Options.EffectiveAbortTimeout);
        var pending = inFlight ?? [];

        foreach (var optional in _optional.Values.Reverse())
            await RunOptionalAbortAsync(optional, budget.Token).ConfigureAwait(false);

        foreach (var source in _sources.Values.Reverse())
        {
            try
            {
                var task = source.Source.AbortAsync(budget.Token).AsTask();
                pending.Add(task);
                await task.WaitAsync(budget.Token).ConfigureAwait(false);
                SetSourceState(source, AcquisitionComponentRuntimeState.Aborted);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    AcquisitionFaultCategory.Finalization,
                    source.Source.ComponentId,
                    source.Source.SourceId,
                    ex,
                    "Source abort failed."));
            }
        }

        try
        {
            await WaitForAllIngressDrainAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.ShutdownTimeout,
                "session-ingress",
                null,
                null,
                "Acquisition ingress did not quiesce within the abort budget."));
        }

        foreach (var required in _required.Values.Reverse())
        {
            try
            {
                var task = required.Registration.Component.AbortAsync(budget.Token).AsTask();
                pending.Add(task);
                await task.WaitAsync(budget.Token).ConfigureAwait(false);
                SetRequiredState(required, AcquisitionComponentRuntimeState.Aborted);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    AcquisitionFaultCategory.Finalization,
                    required.Registration.Component.ComponentId,
                    null,
                    ex,
                    "Required component abort failed."));
            }
        }

        try
        {
            await DisposeAllAsync(budget.Token, pending).ConfigureAwait(false);
            var stillRunning = pending.Where(static task => !task.IsCompleted).ToArray();
            if (stillRunning.Length > 0)
            {
                try
                {
                    await Task.WhenAll(stillRunning)
                        .WaitAsync(budget.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    _abort.IsCancellationRequested &&
                    !budget.IsCancellationRequested)
                {
                    // Stop/finalize work canceled by the deliberate Abort is quiescent,
                    // not an Abort failure. The tasks are observed here so cancellation
                    // cannot escape and strand Session Completion.
                }
                catch (Exception ex) when (!budget.IsCancellationRequested)
                {
                    AddSecondary(CreateFault(
                        AcquisitionFaultCategory.Finalization,
                        "session-inflight",
                        null,
                        ex,
                        "One or more in-flight acquisition operations faulted while aborting."));
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.ShutdownTimeout,
                "session-abort",
                null,
                null,
                "One or more acquisition components did not quiesce within the abort budget."));
        }

        CompleteTerminal(AcquisitionSessionState.Aborted);
    }

    private async Task CleanupStartupFailureAsync(
        AcquisitionSessionState terminalState,
        Exception original,
        string currentComponentId,
        string? currentSourceId,
        Stack<StartupCleanupHandle> startupCleanup)
    {
        TryCancel(_sessionStop);
        CloseAllIngress();

        if (terminalState == AcquisitionSessionState.Aborted)
        {
            if (State != AcquisitionSessionState.Aborting)
                Transition(AcquisitionSessionState.Aborting, AcquisitionStartupPhase.Aborting);
            TryCancel(_abort);
        }
        else if (State != AcquisitionSessionState.Faulting)
        {
            Transition(AcquisitionSessionState.Faulting, AcquisitionStartupPhase.Stopping);
        }

        using var budget = CreateBudget(_definition.Options.EffectiveAbortTimeout);
        var timedOut = false;

        while (startupCleanup.TryPop(out var handle))
        {
            try
            {
                await handle.Component
                    .StopAsync(budget.Token)
                    .AsTask()
                    .WaitAsync(budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    FaultCategoryFor(handle.Kind),
                    handle.ComponentId,
                    handle.SourceId,
                    ex,
                    "Startup rollback stop failed."));
            }

            try
            {
                await handle.Component
                    .FinalizeAsync(budget.Token)
                    .AsTask()
                    .WaitAsync(budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    AcquisitionFaultCategory.Finalization,
                    handle.ComponentId,
                    handle.SourceId,
                    ex,
                    "Startup rollback finalization failed."));
            }

            try
            {
                await handle.Component
                    .DisposeAsync()
                    .AsTask()
                    .WaitAsync(budget.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                timedOut = true;
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    AcquisitionFaultCategory.Finalization,
                    handle.ComponentId,
                    handle.SourceId,
                    ex,
                    "Startup rollback dispose failed."));
            }
        }

        try
        {
            await WaitForAllIngressDrainAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            timedOut = true;
        }

        if (timedOut)
        {
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.ShutdownTimeout,
                currentComponentId,
                currentSourceId,
                original,
                "Startup rollback exceeded the configured cleanup budget."));
        }

        CompleteTerminal(terminalState);
    }

    private async Task<bool> RunRequiredOperationAsync(
        Func<ValueTask> operation,
        OperationDeadline deadline,
        List<Task> inFlight,
        AcquisitionFaultCategory category,
        string componentId,
        string? sourceId,
        string message)
    {
        try
        {
            var task = operation().AsTask();
            inFlight.Add(task);
            await deadline.WaitAsync(task).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException) when (deadline.IsExpired)
        {
            throw;
        }
        catch (Exception ex)
        {
            var fault = CreateFault(category, componentId, sourceId, ex, message);
            RecordRootOrSecondary(fault, RequiredRole(componentId, sourceId));
            return false;
        }
    }

    private async Task DetachAllOptionalAsync(CancellationToken cancellationToken)
    {
        await _optionalGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var handle in _optional.Values.Reverse())
                await DetachOptionalCoreAsync(handle, cancellationToken, preserveFault: true).ConfigureAwait(false);
        }
        finally
        {
            _optionalGate.Release();
        }
    }

    private async Task DetachOptionalCoreAsync(
        OptionalHandle handle,
        CancellationToken cancellationToken,
        bool preserveFault)
    {
        if (handle.State is not (AcquisitionComponentRuntimeState.Running
            or AcquisitionComponentRuntimeState.Faulted
            or AcquisitionComponentRuntimeState.Preparing))
            return;

        try
        {
            await handle.Registration.Component.DetachAsync(cancellationToken).ConfigureAwait(false);
            SetOptionalState(
                handle,
                preserveFault && handle.Error is not null
                    ? AcquisitionComponentRuntimeState.Faulted
                    : AcquisitionComponentRuntimeState.Ready,
                handle.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            SetOptionalFailure(handle, ex);
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.OptionalComponent,
                handle.Registration.Component.ComponentId,
                null,
                ex,
                "Optional component detach failed."));
        }
    }

    private async Task RunOptionalAbortAsync(OptionalHandle handle, CancellationToken cancellationToken)
    {
        try
        {
            await handle.Registration.Component.AbortAsync(cancellationToken).ConfigureAwait(false);
            SetOptionalState(handle, AcquisitionComponentRuntimeState.Aborted, handle.Error);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.OptionalComponent,
                handle.Registration.Component.ComponentId,
                null,
                ex,
                "Optional component abort failed."));
        }
    }

    private async Task DisposeAllAsync(CancellationToken cancellationToken, List<Task> inFlight)
    {
        foreach (var optional in _optional.Values.Reverse())
            await DisposeOptionalAsync(optional, cancellationToken, inFlight).ConfigureAwait(false);

        foreach (var source in _sources.Values.Reverse())
            await DisposeRequiredAsync(
                source.Source,
                source.Source.ComponentId,
                source.Source.SourceId,
                AcquisitionFaultCategory.Source,
                cancellationToken,
                inFlight).ConfigureAwait(false);

        foreach (var required in _required.Values.OrderByDescending(EffectivePrepareOrder))
            await DisposeRequiredAsync(
                required.Registration.Component,
                required.Registration.Component.ComponentId,
                null,
                AcquisitionFaultCategory.Finalization,
                cancellationToken,
                inFlight).ConfigureAwait(false);
    }

    private async Task DisposeOptionalAsync(
        OptionalHandle handle,
        CancellationToken cancellationToken,
        List<Task> inFlight)
    {
        try
        {
            var task = handle.Registration.Component.DisposeAsync().AsTask();
            inFlight.Add(task);
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AddSecondary(CreateFault(
                AcquisitionFaultCategory.OptionalComponent,
                handle.Registration.Component.ComponentId,
                null,
                ex,
                "Optional component dispose failed."));
        }
    }

    private async Task DisposeRequiredAsync(
        IAsyncDisposable component,
        string componentId,
        string? sourceId,
        AcquisitionFaultCategory category,
        CancellationToken cancellationToken,
        List<Task> inFlight)
    {
        try
        {
            var task = component.DisposeAsync().AsTask();
            inFlight.Add(task);
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordRootOrSecondary(
                CreateFault(category, componentId, sourceId, ex, "Required component dispose failed."),
                RequiredRole(componentId, sourceId));
        }
    }

    private AcquisitionComponentContext CreateContext(string componentId, string? sourceId) =>
        new(
            SessionId,
            Mode,
            ProcessingEpoch,
            componentId,
            sourceId,
            _definition.Configuration,
            _sessionStop.Token,
            _abort.Token,
            _timeProvider,
            sourceId is null ? null : _ingressBySource[sourceId],
            (category, exception, message) =>
                ReportFault(componentId, sourceId, category, exception, message));

    private bool ReportFault(
        string componentId,
        string? sourceId,
        AcquisitionFaultCategory category,
        Exception? exception,
        string? message)
    {
        if (_completion.Task.IsCompleted)
            return false;

        var fault = CreateFault(
            category,
            componentId,
            sourceId,
            exception,
            message ?? exception?.Message ?? "Acquisition component reported a fault.");

        if (sourceId is not null &&
            _definition.Options.MultiSourceFailurePolicy == AcquisitionMultiSourceFailurePolicy.IsolateFailedSource &&
            _sources.Count > 1)
        {
            NoteComponentFault(fault);
            AddSecondary(fault);
            _sourceIsolationFaults.Enqueue(fault);
            AcquisitionTelemetry.Faults.Add(1, AcquisitionTelemetry.FaultTags(Mode, category, "source-isolated"));
            WakeSupervisor();
            return true;
        }

        if (_optional.TryGetValue(componentId, out var optional) && !optional.Registration.EscalateFault)
        {
            NoteComponentFault(fault);
            AddSecondary(fault);
            _optionalFaults.Enqueue(fault);
            AcquisitionTelemetry.Faults.Add(1, AcquisitionTelemetry.FaultTags(Mode, category, "optional"));
            WakeSupervisor();
            return true;
        }

        RecordRootOrSecondary(fault, RequiredRole(componentId, sourceId));
        TryCancel(_sessionStop);
        WakeSupervisor();
        return true;
    }

    private void RecordRootOrSecondary(AcquisitionFault fault, string role)
    {
        NoteComponentFault(fault);

        if (Interlocked.CompareExchange(ref _rootFault, fault, null) is null)
        {
            AcquisitionTelemetry.Faults.Add(
                1,
                AcquisitionTelemetry.FaultTags(Mode, fault.Category, role));
            return;
        }

        AddSecondary(fault);
    }

    private void NoteComponentFault(AcquisitionFault fault)
    {
        lock (_stateGate)
        {
            if (fault.SourceId is not null && _sources.TryGetValue(fault.SourceId, out var source))
            {
                source.Error ??= fault.Message;
                return;
            }

            if (_required.TryGetValue(fault.ComponentId, out var required))
            {
                required.Error ??= fault.Message;
                return;
            }

            if (_optional.TryGetValue(fault.ComponentId, out var optional))
                optional.Error ??= fault.Message;
        }
    }

    private void AddSecondary(AcquisitionFault fault)
    {
        var stored = Interlocked.Increment(ref _storedSecondaryFaults);
        if (stored <= _definition.Options.SecondaryFaultCapacity)
        {
            _secondaryFaults.Enqueue(fault);
            return;
        }

        Interlocked.Decrement(ref _storedSecondaryFaults);
        Interlocked.Increment(ref _droppedSecondaryFaults);
    }

    private AcquisitionFault CreateFault(
        AcquisitionFaultCategory category,
        string componentId,
        string? sourceId,
        Exception? exception,
        string message) =>
        new(
            category,
            componentId,
            sourceId,
            message,
            exception,
            _timeProvider.GetUtcNow());

    private void EnsureRequiredReady()
    {
        lock (_stateGate)
        {
            if (_required.Values.Any(static item => item.State != AcquisitionComponentRuntimeState.Ready) ||
                _sources.Values.Any(static item => item.State != AcquisitionComponentRuntimeState.Ready))
            {
                throw new InvalidOperationException("Required acquisition readiness barrier is incomplete.");
            }
        }
    }

    private bool TryTerminateBeforeStart(AcquisitionFaultCategory category, string message)
    {
        lock (_stateGate)
        {
            if (_state != AcquisitionSessionState.Created || Volatile.Read(ref _startOnce) != 0)
                return false;
        }

        var fault = CreateFault(category, "session", null, null, message);
        RecordRootOrSecondary(fault, "session");
        Transition(AcquisitionSessionState.Aborting, AcquisitionStartupPhase.Aborting);
        CompleteTerminal(AcquisitionSessionState.Aborted);
        return true;
    }

    private void CompleteTerminal(AcquisitionSessionState terminalState)
    {
        if (Interlocked.Exchange(ref _terminalOnce, 1) != 0)
            return;

        CloseAllIngress();
        Transition(terminalState, AcquisitionStartupPhase.Completed);

        if (Interlocked.Exchange(ref _activeMetric, 0) == 1)
            AcquisitionTelemetry.ActiveSessions.Add(-1, AcquisitionTelemetry.ModeTags(Mode));

        AcquisitionSessionResult result;
        lock (_stateGate)
        {
            _endedAt ??= _timeProvider.GetUtcNow();
            result = new AcquisitionSessionResult(
                SessionId,
                Mode,
                terminalState,
                _rootFault,
                _secondaryFaults.ToArray(),
                Volatile.Read(ref _droppedSecondaryFaults),
                _startedAt,
                _endedAt.Value,
                ProcessingEpoch,
                _definition.ReplaySourceArtifactId,
                RejectedLateIngressCount(),
                _required.Values
                    .Select(static item => new AcquisitionComponentResult(
                        item.Registration.Component.ComponentId,
                        item.Registration.Component.Kind,
                        item.State,
                        item.Error))
                    .Concat(_optional.Values.Select(static item => new AcquisitionComponentResult(
                        item.Registration.Component.ComponentId,
                        item.Registration.Component.Kind,
                        item.State,
                        item.Error)))
                    .ToArray(),
                _sources.Values
                    .Select(static item => new AcquisitionSourceResult(
                        item.Source.ComponentId,
                        item.Source.SourceId,
                        item.Source.ConnectionEpoch,
                        item.State,
                        item.Error))
                    .ToArray());
        }

        _completion.TrySetResult(result);
        _wake.Writer.TryComplete();
        _onTerminal?.Invoke(this);
    }

    private void Transition(AcquisitionSessionState next, AcquisitionStartupPhase phase)
    {
        AcquisitionSessionState previous;
        lock (_stateGate)
        {
            previous = _state;
            if (previous == next)
            {
                _phase = phase;
                return;
            }

            if (!IsAllowedTransition(previous, next))
                throw new InvalidOperationException($"Illegal acquisition session transition: {previous} -> {next}.");

            _state = next;
            _phase = phase;

            if (next == AcquisitionSessionState.Running)
            {
                _startedAt ??= _timeProvider.GetUtcNow();
                if (Interlocked.Exchange(ref _activeMetric, 1) == 0)
                    AcquisitionTelemetry.ActiveSessions.Add(1, AcquisitionTelemetry.ModeTags(Mode));
            }

            if (next is AcquisitionSessionState.Completed or AcquisitionSessionState.Faulted or AcquisitionSessionState.Aborted)
                _endedAt ??= _timeProvider.GetUtcNow();
        }

        AcquisitionTelemetry.StateTransitions.Add(
            1,
            AcquisitionTelemetry.StateTags(Mode, previous, next));
    }

    private void SetPhase(AcquisitionStartupPhase phase)
    {
        lock (_stateGate)
            _phase = phase;
    }

    private void SetPhaseIfState(
        AcquisitionSessionState expectedState,
        AcquisitionStartupPhase phase)
    {
        lock (_stateGate)
        {
            if (_state == expectedState)
                _phase = phase;
        }
    }

    private void SetRequiredState(
        RequiredHandle handle,
        AcquisitionComponentRuntimeState state,
        string? error = null)
    {
        lock (_stateGate)
        {
            handle.State = state;
            if (error is not null)
                handle.Error = error;
        }
    }

    private void SetSourceState(
        SourceHandle handle,
        AcquisitionComponentRuntimeState state,
        string? error = null)
    {
        lock (_stateGate)
        {
            handle.State = state;
            if (error is not null)
                handle.Error = error;
        }
    }

    private void SetOptionalState(
        OptionalHandle handle,
        AcquisitionComponentRuntimeState state,
        string? error = null,
        bool clearError = false)
    {
        lock (_stateGate)
        {
            handle.State = state;
            if (clearError)
                handle.Error = null;
            else if (error is not null)
                handle.Error = error;
        }
    }

    private void SetOptionalFailure(OptionalHandle handle, Exception exception)
    {
        lock (_stateGate)
        {
            handle.State = AcquisitionComponentRuntimeState.Faulted;
            handle.Error = exception.Message;
        }
    }

    private void OpenAllIngress()
    {
        foreach (var gate in _ingressBySource.Values)
            gate.Open();
    }

    private void CloseAllIngress()
    {
        foreach (var gate in _ingressBySource.Values)
            gate.Close();
    }

    private Task WaitForAllIngressDrainAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_ingressBySource.Values.Select(gate => gate.WaitForDrainAsync(cancellationToken)));

    private long RejectedLateIngressCount() =>
        _ingressBySource.Values.Sum(static gate => gate.RejectedAfterClose);

    private OperationDeadline CreateDeadline(TimeSpan timeout) =>
        new(_timeProvider, timeout);

    private OperationBudget CreateBudget(TimeSpan timeout) =>
        new(_timeProvider, timeout);

    private sealed class OperationDeadline : IDisposable
    {
        private readonly TaskCompletionSource _expired =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ITimer _timer;
        private int _isExpired;
        private int _disposed;

        public OperationDeadline(TimeProvider timeProvider, TimeSpan timeout)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((OperationDeadline)state!).Expire(),
                this,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public bool IsExpired => Volatile.Read(ref _isExpired) != 0;

        public async Task WaitAsync(Task task)
        {
            ArgumentNullException.ThrowIfNull(task);

            if (!task.IsCompleted)
            {
                var completed = await Task
                    .WhenAny(task, _expired.Task)
                    .ConfigureAwait(false);

                if (!ReferenceEquals(completed, task) && !task.IsCompleted)
                    throw new TimeoutException("The acquisition operation exceeded its global deadline.");
            }

            await task.ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _timer.Dispose();
        }

        private void Expire()
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Interlocked.Exchange(ref _isExpired, 1);

            // Do not complete the deadline task from inside a TimeProvider timer
            // callback. FakeTimeProvider.Advance() executes callbacks synchronously,
            // and completing the task there can let Session convergence re-enter the
            // timer-dispatch cycle. Queue only the control signal; cleanup remains
            // owned by the supervisor after this callback has returned.
            ThreadPool.QueueUserWorkItem(
                static state => ((TaskCompletionSource)state!).TrySetResult(),
                _expired,
                preferLocal: false);
        }
    }

    private sealed class OperationBudget : IDisposable
    {
        private readonly CancellationTokenSource _source = new();
        private readonly ITimer _timer;
        private int _disposed;

        public OperationBudget(TimeProvider timeProvider, TimeSpan timeout)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                _source,
                timeout,
                Timeout.InfiniteTimeSpan);
        }

        public CancellationToken Token => _source.Token;
        public bool IsCancellationRequested => _source.IsCancellationRequested;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _timer.Dispose();
            _source.Dispose();
        }
    }

    private void WakeSupervisor() => _wake.Writer.TryWrite(1);

    private async Task<AcquisitionSessionResult> WaitCompletionAsync(CancellationToken cancellationToken) =>
        await _completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    private static void TryCancel(CancellationTokenSource source)
    {
        try
        {
            if (!source.IsCancellationRequested)
                source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static bool IsReadyOrBeyond(AcquisitionComponentRuntimeState state) =>
        state is AcquisitionComponentRuntimeState.Ready
            or AcquisitionComponentRuntimeState.Running
            or AcquisitionComponentRuntimeState.Stopping
            or AcquisitionComponentRuntimeState.Completed;

    private static bool IsAllowedTransition(AcquisitionSessionState from, AcquisitionSessionState to) =>
        (from, to) switch
        {
            (AcquisitionSessionState.Created, AcquisitionSessionState.Preparing) => true,
            (AcquisitionSessionState.Created, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Preparing, AcquisitionSessionState.Ready) => true,
            (AcquisitionSessionState.Preparing, AcquisitionSessionState.Faulting) => true,
            (AcquisitionSessionState.Preparing, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Ready, AcquisitionSessionState.Running) => true,
            (AcquisitionSessionState.Ready, AcquisitionSessionState.Faulting) => true,
            (AcquisitionSessionState.Ready, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Running, AcquisitionSessionState.Stopping) => true,
            (AcquisitionSessionState.Running, AcquisitionSessionState.Faulting) => true,
            (AcquisitionSessionState.Running, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Stopping, AcquisitionSessionState.Completed) => true,
            (AcquisitionSessionState.Stopping, AcquisitionSessionState.Faulting) => true,
            (AcquisitionSessionState.Stopping, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Faulting, AcquisitionSessionState.Faulted) => true,
            (AcquisitionSessionState.Faulting, AcquisitionSessionState.Aborting) => true,
            (AcquisitionSessionState.Aborting, AcquisitionSessionState.Aborted) => true,
            _ => false
        };

    private static int EffectivePrepareOrder(RequiredHandle handle) =>
        handle.Registration.PrepareOrder != 0
            ? handle.Registration.PrepareOrder
            : handle.Registration.Component.Kind switch
            {
                AcquisitionComponentKind.Processing => 100,
                AcquisitionComponentKind.Router => 200,
                AcquisitionComponentKind.RawRecorder => 300,
                AcquisitionComponentKind.RequiredArtifact => 400,
                _ => 500
            };

    private static int EffectiveStopOrder(RequiredHandle handle) =>
        handle.Registration.StopOrder != 0
            ? handle.Registration.StopOrder
            : handle.Registration.Component.Kind switch
            {
                AcquisitionComponentKind.RawRecorder => 100,
                AcquisitionComponentKind.Processing => 200,
                AcquisitionComponentKind.Router => 300,
                AcquisitionComponentKind.RequiredArtifact => 400,
                _ => 500
            };

    private static int EffectiveFinalizeOrder(RequiredHandle handle) =>
        handle.Registration.FinalizeOrder != 0
            ? handle.Registration.FinalizeOrder
            : handle.Registration.Component.Kind switch
            {
                AcquisitionComponentKind.Processing => 100,
                AcquisitionComponentKind.Router => 200,
                AcquisitionComponentKind.RequiredArtifact => 300,
                AcquisitionComponentKind.RawRecorder => 400,
                _ => 500
            };

    private static AcquisitionFaultCategory FaultCategoryFor(AcquisitionComponentKind kind) =>
        kind switch
        {
            AcquisitionComponentKind.Source => AcquisitionFaultCategory.Source,
            AcquisitionComponentKind.RawRecorder => AcquisitionFaultCategory.RawIntegrity,
            AcquisitionComponentKind.Processing => AcquisitionFaultCategory.Processing,
            AcquisitionComponentKind.Router => AcquisitionFaultCategory.RequiredBranch,
            AcquisitionComponentKind.RequiredArtifact => AcquisitionFaultCategory.Finalization,
            _ => AcquisitionFaultCategory.Unknown
        };

    private string RequiredRole(string componentId, string? sourceId)
    {
        if (sourceId is not null)
            return "source";
        if (_optional.ContainsKey(componentId))
            return "optional-escalated";
        return "required";
    }
}
