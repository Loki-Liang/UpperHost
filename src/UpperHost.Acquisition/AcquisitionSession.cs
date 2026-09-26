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
    private int _disposeOnce;
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

                var context = CreateContext(currentComponentId, null);
                await handle.Registration.Component
                    .PrepareAsync(context, startupCts.Token)
                    .ConfigureAwait(false);

                SetRequiredState(handle, AcquisitionComponentRuntimeState.Ready);
            }

            SetPhase(AcquisitionStartupPhase.PreparingSources);
            foreach (var handle in _sources.Values)
            {
                currentComponentId = handle.Source.ComponentId;
                currentSourceId = handle.Source.SourceId;
                SetSourceState(handle, AcquisitionComponentRuntimeState.Preparing);

                var context = CreateContext(currentComponentId, currentSourceId);
                await handle.Source
                    .PrepareAsync(context, startupCts.Token)
                    .ConfigureAwait(false);

                SetSourceState(handle, AcquisitionComponentRuntimeState.Ready);
            }

            SetPhase(AcquisitionStartupPhase.RequiredReady);
            EnsureRequiredReady();
            Transition(AcquisitionSessionState.Ready, AcquisitionStartupPhase.RequiredReady);

            SetPhase(AcquisitionStartupPhase.StartingSources);
            foreach (var handle in _sources.Values)
            {
                currentComponentId = handle.Source.ComponentId;
                currentSourceId = handle.Source.SourceId;

                await handle.Source
                    .StartAsync(startupCts.Token)
                    .ConfigureAwait(false);

                SetSourceState(handle, AcquisitionComponentRuntimeState.Running);
            }

            _supervisor = RunSupervisorAsync();
            Transition(AcquisitionSessionState.Running, AcquisitionStartupPhase.AttachingOptionalComponents);

            foreach (var optional in _definition.OptionalComponents)
                await AttachOptionalAsync(optional.Component.ComponentId, CancellationToken.None).ConfigureAwait(false);

            SetPhase(AcquisitionStartupPhase.Running);
        }
        catch (OperationCanceledException ex)
        {
            if (_rootFault is not null)
            {
                await CleanupStartupFailureAsync(
                        AcquisitionSessionState.Faulted,
                        ex,
                        currentComponentId,
                        currentSourceId)
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
                    currentSourceId)
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
                    currentSourceId)
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

    public Task<AcquisitionSessionResult> AbortAsync(CancellationToken cancellationToken = default)
    {
        if (_completion.Task.IsCompleted)
            return WaitCompletionAsync(cancellationToken);

        if (TryTerminateBeforeStart(AcquisitionFaultCategory.OperatorAbort, "Session aborted before startup."))
            return WaitCompletionAsync(cancellationToken);

        Interlocked.Exchange(ref _abortRequested, 1);
        TryCancel(_sessionStop);
        TryCancel(_abort);
        WakeSupervisor();
        return WaitCompletionAsync(cancellationToken);
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
                SetOptionalState(handle, AcquisitionComponentRuntimeState.Running);
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

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeOnce, 1) != 0)
            return;

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
                await handle.Source.StopAsync(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false);
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
        using var budget = CreateBudget(_definition.Options.EffectiveStopTimeout);
        using var convergenceCts = CancellationTokenSource.CreateLinkedTokenSource(
            budget.Token,
            _abort.Token);
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
                    convergenceToken,
                    inFlight,
                    AcquisitionFaultCategory.Source,
                    handle.Source.ComponentId,
                    handle.Source.SourceId,
                    "Source stop failed.").ConfigureAwait(false);

                if (!ok)
                    SetSourceState(handle, AcquisitionComponentRuntimeState.Faulted);
            }

            await DetachAllOptionalAsync(convergenceToken).ConfigureAwait(false);

            foreach (var handle in _required.Values.OrderBy(EffectiveStopOrder))
            {
                if (handle.State is not (AcquisitionComponentRuntimeState.Ready
                    or AcquisitionComponentRuntimeState.Running
                    or AcquisitionComponentRuntimeState.Preparing))
                    continue;

                SetRequiredState(handle, AcquisitionComponentRuntimeState.Stopping);
                var ok = await RunRequiredOperationAsync(
                    () => handle.Registration.Component.StopAsync(convergenceToken),
                    convergenceToken,
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
                    convergenceToken,
                    inFlight,
                    AcquisitionFaultCategory.Finalization,
                    handle.Registration.Component.ComponentId,
                    null,
                    "Required acquisition component finalization failed.").ConfigureAwait(false);

                SetRequiredState(
                    handle,
                    ok ? AcquisitionComponentRuntimeState.Completed : AcquisitionComponentRuntimeState.Faulted);
            }

            foreach (var handle in _sources.Values)
            {
                if (handle.State is AcquisitionComponentRuntimeState.Isolated
                    or AcquisitionComponentRuntimeState.Faulted)
                    continue;

                var ok = await RunRequiredOperationAsync(
                    () => handle.Source.FinalizeAsync(convergenceToken),
                    convergenceToken,
                    inFlight,
                    AcquisitionFaultCategory.Finalization,
                    handle.Source.ComponentId,
                    handle.Source.SourceId,
                    "Source finalization failed.").ConfigureAwait(false);

                SetSourceState(
                    handle,
                    ok ? AcquisitionComponentRuntimeState.Completed : AcquisitionComponentRuntimeState.Faulted);
            }

            await DisposeAllAsync(convergenceToken, inFlight).ConfigureAwait(false);

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
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            var timeout = CreateFault(
                AcquisitionFaultCategory.ShutdownTimeout,
                "session-convergence",
                null,
                null,
                "Acquisition stop/finalize exceeded the configured global shutdown budget.");
            RecordRootOrSecondary(timeout, "session");
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
                await Task.WhenAll(stillRunning).WaitAsync(budget.Token).ConfigureAwait(false);
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
        string? currentSourceId)
    {
        TryCancel(_sessionStop);

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
        var inFlight = new List<Task>();

        foreach (var source in _sources.Values.Reverse())
        {
            if (source.State == AcquisitionComponentRuntimeState.Created)
                continue;

            try
            {
                var task = source.Source.StopAsync(budget.Token).AsTask();
                inFlight.Add(task);
                await task.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    AcquisitionFaultCategory.Source,
                    source.Source.ComponentId,
                    source.Source.SourceId,
                    ex,
                    "Source cleanup after partial startup failed."));
            }
        }

        foreach (var required in _required.Values.OrderBy(EffectiveStopOrder))
        {
            if (required.State == AcquisitionComponentRuntimeState.Created)
                continue;

            try
            {
                var task = required.Registration.Component.StopAsync(budget.Token).AsTask();
                inFlight.Add(task);
                await task.WaitAsync(budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                AddSecondary(CreateFault(
                    FaultCategoryFor(required.Registration.Component.Kind),
                    required.Registration.Component.ComponentId,
                    null,
                    ex,
                    "Required component cleanup after partial startup failed."));
            }
        }

        foreach (var required in _required.Values.OrderBy(EffectiveFinalizeOrder))
        {
            if (required.State == AcquisitionComponentRuntimeState.Created)
                continue;

            try
            {
                var task = required.Registration.Component.FinalizeAsync(budget.Token).AsTask();
                inFlight.Add(task);
                await task.WaitAsync(budget.Token).ConfigureAwait(false);
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
                    "Required component finalization after partial startup failed."));
            }
        }

        try
        {
            await DisposeAllAsync(budget.Token, inFlight).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
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
        CancellationToken budgetToken,
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
            await task.WaitAsync(budgetToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (budgetToken.IsCancellationRequested)
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
            AddSecondary(fault);
            _sourceIsolationFaults.Enqueue(fault);
            AcquisitionTelemetry.Faults.Add(1, AcquisitionTelemetry.FaultTags(Mode, category, "source-isolated"));
            WakeSupervisor();
            return true;
        }

        if (_optional.TryGetValue(componentId, out var optional) && !optional.Registration.EscalateFault)
        {
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
        if (Interlocked.CompareExchange(ref _rootFault, fault, null) is null)
        {
            AcquisitionTelemetry.Faults.Add(
                1,
                AcquisitionTelemetry.FaultTags(Mode, fault.Category, role));
            return;
        }

        AddSecondary(fault);
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
        string? error = null)
    {
        lock (_stateGate)
        {
            handle.State = state;
            if (error is not null)
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

    private OperationBudget CreateBudget(TimeSpan timeout) =>
        new(_timeProvider, timeout);

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
