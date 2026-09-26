using System.Threading.Channels;
using UpperHost.Abstractions.Observability;
using UpperHost.Control.Commands;

namespace UpperHost.Control.Scheduling;

public enum CommandAdmissionMode
{
    Wait,
    Reject
}

public sealed record CommandResourceKey(string Category, string Value) : IComparable<CommandResourceKey>
{
    public int CompareTo(CommandResourceKey? other)
    {
        if (other is null)
            return 1;

        var category = string.Compare(Category, other.Category, StringComparison.Ordinal);
        return category != 0
            ? category
            : string.Compare(Value, other.Value, StringComparison.Ordinal);
    }

    public override string ToString() => $"{Category}:{Value}";
}

public sealed record CommandDispatchOptions(
    CommandPriority Priority = CommandPriority.Normal,
    IReadOnlyList<CommandResourceKey>? Resources = null,
    IReadOnlyList<CommandResourceClaim>? ResourceClaims = null,
    CommandAdmissionMode AdmissionMode = CommandAdmissionMode.Wait,
    CommandExecutionOptions? Execution = null,
    CommandSafetyMetadata? Safety = null,
    long? ConnectionEpoch = null,
    TimeSpan? QueueTimeout = null);

public sealed record BoundedCommandDispatcherOptions(
    int Capacity = 256,
    int PerPriorityCapacity = 128,
    int MaxConcurrency = 4,
    int PerResourcePendingCapacity = 64,
    int MaxSharedReadersPerResource = 4)
{
    internal void Validate()
    {
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        if (PerPriorityCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(PerPriorityCapacity));
        if (MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
        if (PerResourcePendingCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(PerResourcePendingCapacity));
        if (MaxSharedReadersPerResource <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxSharedReadersPerResource));
    }
}

/// <summary>
/// Bounded, explicitly-owned command dispatcher. Construction has no background side effects;
/// callers must start the dispatcher before admitting commands.
/// </summary>
public interface ICommandConnectionEpochValidator
{
    bool IsCurrent(long connectionEpoch, IReadOnlyList<CommandResourceKey>? resources);
}

public sealed class BoundedCommandDispatcher<TCommand, TResult> : IAsyncDisposable
{
    private sealed record Envelope(
        TCommand Command,
        CommandDispatchOptions Options,
        IReadOnlyList<CommandResourceClaim> ResourceClaims,
        IDisposable ResourcePendingReservation,
        long EnqueuedTimestamp,
        TimeSpan? QueueTimeout,
        CancellationToken CancellationToken,
        TaskCompletionSource<CommandExecutionResult<TResult>> Completion);

    private static readonly CommandPriority[] FairSchedule =
    [
        CommandPriority.Critical,
        CommandPriority.High,
        CommandPriority.Critical,
        CommandPriority.Normal,
        CommandPriority.Critical,
        CommandPriority.High,
        CommandPriority.Critical,
        CommandPriority.Low,
        CommandPriority.Critical,
        CommandPriority.High,
        CommandPriority.Critical,
        CommandPriority.Normal,
        CommandPriority.Critical,
        CommandPriority.High,
        CommandPriority.Critical
    ];

    private readonly CommandRuntime<TCommand, TResult> _runtime;
    private readonly BoundedCommandDispatcherOptions _options;
    private readonly Dictionary<CommandPriority, Channel<Envelope>> _lanes;
    private readonly SemaphoreSlim _pendingSlots;
    private readonly SemaphoreSlim _available = new(0);
    private readonly SemaphoreSlim _executionSlots;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CommandResourceCoordinator _resources;
    private readonly CommandResourcePendingLimiter _resourcePending;
    private readonly ICommandConnectionEpochValidator? _epochValidator;
    private readonly TimeProvider _timeProvider;
    private readonly object _activeGate = new();
    private readonly HashSet<Task> _active = [];
    private Task? _pump;
    private int _state;
    private int _pending;
    private int _pendingHighWater;
    private int _scheduleIndex;

    public BoundedCommandDispatcher(
        CommandRuntime<TCommand, TResult> runtime,
        BoundedCommandDispatcherOptions? options = null,
        ICommandConnectionEpochValidator? epochValidator = null,
        TimeProvider? timeProvider = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new BoundedCommandDispatcherOptions();
        _epochValidator = epochValidator;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _options.Validate();

        _pendingSlots = new SemaphoreSlim(_options.Capacity, _options.Capacity);
        _executionSlots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        _resources = new CommandResourceCoordinator(_options.MaxSharedReadersPerResource);
        _resourcePending = new CommandResourcePendingLimiter(_options.PerResourcePendingCapacity);
        _lanes = Enum.GetValues<CommandPriority>().ToDictionary(
            priority => priority,
            _ => Channel.CreateBounded<Envelope>(
                new BoundedChannelOptions(_options.PerPriorityCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                }));
    }

    public int Capacity => _options.Capacity;
    public int PendingCount => Volatile.Read(ref _pending);
    public int PendingHighWater => Volatile.Read(ref _pendingHighWater);
    public bool IsRunning => Volatile.Read(ref _state) == 1;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
            throw new InvalidOperationException("Command dispatcher can only be started once.");

        _pump = RunAsync(_shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task<CommandExecutionResult<TResult>> EnqueueAsync(
        TCommand command,
        CommandDispatchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new CommandDispatchOptions();

        if (!IsRunning)
            return Rejected("dispatcher_not_running", "Command dispatcher is not accepting commands.");

        if (!_lanes.TryGetValue(options.Priority, out var lane))
            throw new ArgumentOutOfRangeException(nameof(options), "Unknown command priority.");
        if (options.QueueTimeout is { } queueTimeout && queueTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Queue timeout must be greater than zero.");

        var enqueuedTimestamp = _timeProvider.GetTimestamp();
        using var admissionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var admissionTimer = CreateQueueTimer(options.QueueTimeout, admissionCts);

        var slotAcquired = options.AdmissionMode switch
        {
            CommandAdmissionMode.Reject => _pendingSlots.Wait(0),
            _ => await WaitForPendingSlotAsync(admissionCts.Token).ConfigureAwait(false)
        };

        if (!slotAcquired)
        {
            if (cancellationToken.IsCancellationRequested)
                return Cancelled();
            if (admissionCts.IsCancellationRequested)
                return ExpiredBeforeDispatch();
            return RejectAdmission(
                options.Priority,
                "dispatcher_capacity",
                "Command dispatcher capacity is full.");
        }

        var resourceClaims = NormalizeResourceClaims(options);
        IDisposable? resourcePendingReservation = null;
        try
        {
            resourcePendingReservation = options.AdmissionMode == CommandAdmissionMode.Reject
                ? _resourcePending.TryReserve(resourceClaims)
                : await _resourcePending.ReserveAsync(resourceClaims, admissionCts.Token).ConfigureAwait(false);

            if (resourcePendingReservation is null)
            {
                _pendingSlots.Release();
                return RejectAdmission(
                    options.Priority,
                    "resource_pending_capacity",
                    "At least one command resource has reached its pending capacity.");
            }

            var completion = new TaskCompletionSource<CommandExecutionResult<TResult>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var envelope = new Envelope(
                command,
                options,
                resourceClaims,
                resourcePendingReservation,
                enqueuedTimestamp,
                options.QueueTimeout,
                cancellationToken,
                completion);

            if (options.AdmissionMode == CommandAdmissionMode.Reject)
            {
                if (!lane.Writer.TryWrite(envelope))
                {
                    resourcePendingReservation.Dispose();
                    _pendingSlots.Release();
                    return RejectAdmission(
                        options.Priority,
                        "priority_capacity",
                        $"Priority lane '{options.Priority}' is full.");
                }
            }
            else
            {
                await lane.Writer.WriteAsync(envelope, admissionCts.Token).ConfigureAwait(false);
            }

            resourcePendingReservation = null;
            var pending = Interlocked.Increment(ref _pending);
            UpdatePendingHighWater(pending);
            CommandDispatcherTelemetry.PendingCommands.Add(
                1,
                CommandDispatcherTelemetry.PriorityTags(options.Priority));
            _available.Release();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            resourcePendingReservation?.Dispose();
            _pendingSlots.Release();
            return cancellationToken.IsCancellationRequested
                ? Cancelled()
                : ExpiredBeforeDispatch();
        }
        catch (ChannelClosedException)
        {
            resourcePendingReservation?.Dispose();
            _pendingSlots.Release();
            return RejectAdmission(
                options.Priority,
                "dispatcher_stopping",
                "Command dispatcher is stopping.");
        }
        catch
        {
            resourcePendingReservation?.Dispose();
            _pendingSlots.Release();
            throw;
        }

    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var previous = Interlocked.CompareExchange(ref _state, 2, 1);
        if (previous == 0)
        {
            Interlocked.Exchange(ref _state, 3);
            return;
        }

        if (previous is 2 or 3)
        {
            if (_pump is not null)
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        foreach (var lane in _lanes.Values)
            lane.Writer.TryComplete();

        _available.Release();

        try
        {
            if (_pump is not null)
                await _pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _shutdown.Cancel();
            if (_pump is not null)
                await _pump.ConfigureAwait(false);
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref _state, 3);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _state) == 1)
            await StopAsync().ConfigureAwait(false);

        _shutdown.Cancel();
        _shutdown.Dispose();
        _pendingSlots.Dispose();
        _available.Dispose();
        _executionSlots.Dispose();
    }

    private async Task<bool> WaitForPendingSlotAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _pendingSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _state) != 1 && PendingCount == 0)
                    break;

                await _available.WaitAsync(cancellationToken).ConfigureAwait(false);
                await _executionSlots.WaitAsync(cancellationToken).ConfigureAwait(false);

                if (!TryDequeue(out var envelope))
                {
                    _executionSlots.Release();
                    continue;
                }

                var task = ExecuteAsync(envelope, cancellationToken);
                lock (_activeGate)
                    _active.Add(task);
                _ = ObserveExecutionAsync(task);
            }

            Task[] remaining;
            lock (_activeGate)
                remaining = [.. _active];

            if (remaining.Length > 0)
                await Task.WhenAll(remaining).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            CancelPending();
            Task[] remaining;
            lock (_activeGate)
                remaining = [.. _active];

            if (remaining.Length > 0)
                await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    private bool TryDequeue(out Envelope envelope)
    {
        for (var attempt = 0; attempt < FairSchedule.Length; attempt++)
        {
            var priority = FairSchedule[_scheduleIndex];
            _scheduleIndex = (_scheduleIndex + 1) % FairSchedule.Length;

            if (!_lanes[priority].Reader.TryRead(out envelope!))
                continue;

            envelope.ResourcePendingReservation.Dispose();
            Interlocked.Decrement(ref _pending);
            _pendingSlots.Release();
            CommandDispatcherTelemetry.PendingCommands.Add(
                -1,
                CommandDispatcherTelemetry.PriorityTags(envelope.Options.Priority));
            CommandDispatcherTelemetry.QueueWaitSeconds.Record(
                _timeProvider.GetElapsedTime(envelope.EnqueuedTimestamp).TotalSeconds,
                CommandDispatcherTelemetry.PriorityTags(envelope.Options.Priority));
            return true;
        }

        envelope = null!;
        return false;
    }

    private async Task ExecuteAsync(Envelope envelope, CancellationToken dispatcherToken)
    {
        using var dispatchCts = CancellationTokenSource.CreateLinkedTokenSource(
            dispatcherToken,
            envelope.CancellationToken);
        using var queueTimer = CreateRemainingQueueTimer(envelope, dispatchCts);

        try
        {
            if (IsQueueExpired(envelope))
            {
                envelope.Completion.TrySetResult(ExpiredBeforeDispatch());
                return;
            }

            var resourceWaitStarted = _timeProvider.GetTimestamp();
            await using var lease = await _resources
                .AcquireAsync(envelope.ResourceClaims, dispatchCts.Token)
                .ConfigureAwait(false);
            CommandDispatcherTelemetry.ResourceWaitSeconds.Record(
                _timeProvider.GetElapsedTime(resourceWaitStarted).TotalSeconds,
                CommandDispatcherTelemetry.PriorityTags(envelope.Options.Priority));

            if (envelope.CancellationToken.IsCancellationRequested)
            {
                envelope.Completion.TrySetResult(Cancelled());
                return;
            }

            if (IsQueueExpired(envelope))
            {
                envelope.Completion.TrySetResult(ExpiredBeforeDispatch());
                return;
            }

            queueTimer?.Dispose();

            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(
                dispatcherToken,
                envelope.CancellationToken);

            if (envelope.Options.ConnectionEpoch is long connectionEpoch &&
                _epochValidator is not null &&
                !_epochValidator.IsCurrent(
                    connectionEpoch,
                    envelope.ResourceClaims.Select(static claim => claim.Resource).ToArray()))
            {
                CommandDispatcherTelemetry.EpochInvalidations.Add(
                    1,
                    CommandDispatcherTelemetry.PriorityTags(envelope.Options.Priority));
                envelope.Completion.TrySetResult(
                    Rejected(
                        "connection_epoch_stale",
                        "Command belongs to a stale connection epoch and will not be dispatched."));
                return;
            }

            var executionOptions = envelope.Options.Execution ?? CommandExecutionOptions.Default;
            executionOptions = executionOptions with
            {
                Safety = envelope.Options.Safety ?? CommandSafetyMetadata.MutatingNonIdempotent
            };

            var result = await _runtime.ExecuteAsync(
                envelope.Command,
                executionOptions,
                executionCts.Token).ConfigureAwait(false);
            envelope.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            if (!envelope.CancellationToken.IsCancellationRequested &&
                !dispatcherToken.IsCancellationRequested &&
                IsQueueExpired(envelope))
            {
                envelope.Completion.TrySetResult(ExpiredBeforeDispatch(ex));
                return;
            }

            envelope.Completion.TrySetResult(new CommandExecutionResult<TResult>(
                Guid.NewGuid().ToString("N"),
                CommandExecutionStatus.Cancelled,
                default,
                "dispatcher_cancelled",
                "Command was cancelled before or during dispatch.",
                ex,
                TimeSpan.Zero));
        }
        catch (Exception ex)
        {
            envelope.Completion.TrySetResult(new CommandExecutionResult<TResult>(
                Guid.NewGuid().ToString("N"),
                CommandExecutionStatus.Faulted,
                default,
                "dispatcher_fault",
                ex.Message,
                ex,
                TimeSpan.Zero));
        }
    }

    private async Task ObserveExecutionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        finally
        {
            lock (_activeGate)
                _active.Remove(task);
            _executionSlots.Release();
        }
    }

    private void CancelPending()
    {
        foreach (var lane in _lanes.Values)
        {
            while (lane.Reader.TryRead(out var envelope))
            {
                envelope.ResourcePendingReservation.Dispose();
                Interlocked.Decrement(ref _pending);
                _pendingSlots.Release();
                CommandDispatcherTelemetry.PendingCommands.Add(
                    -1,
                    CommandDispatcherTelemetry.PriorityTags(envelope.Options.Priority));
                envelope.Completion.TrySetResult(Cancelled());
            }
        }
    }

    private ITimer? CreateQueueTimer(
        TimeSpan? queueTimeout,
        CancellationTokenSource cancellation)
    {
        if (queueTimeout is null)
            return null;

        return _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            cancellation,
            queueTimeout.Value,
            Timeout.InfiniteTimeSpan);
    }

    private ITimer? CreateRemainingQueueTimer(
        Envelope envelope,
        CancellationTokenSource cancellation)
    {
        if (envelope.QueueTimeout is null)
            return null;

        var elapsed = _timeProvider.GetElapsedTime(envelope.EnqueuedTimestamp);
        var remaining = envelope.QueueTimeout.Value - elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            cancellation.Cancel();
            return null;
        }

        return _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            cancellation,
            remaining,
            Timeout.InfiniteTimeSpan);
    }

    private bool IsQueueExpired(Envelope envelope) =>
        envelope.QueueTimeout is { } timeout &&
        _timeProvider.GetElapsedTime(envelope.EnqueuedTimestamp) >= timeout;

    private static IReadOnlyList<CommandResourceClaim> NormalizeResourceClaims(CommandDispatchOptions options)
    {
        var claims = new Dictionary<CommandResourceKey, CommandResourceAccess>();

        if (options.Resources is not null)
        {
            foreach (var resource in options.Resources)
                claims[resource] = CommandResourceAccess.Exclusive;
        }

        if (options.ResourceClaims is not null)
        {
            foreach (var claim in options.ResourceClaims)
            {
                if (claims.TryGetValue(claim.Resource, out var existing) &&
                    existing == CommandResourceAccess.Exclusive)
                    continue;

                claims[claim.Resource] = claim.Access;
            }
        }

        return claims
            .OrderBy(static pair => pair.Key)
            .Select(static pair => new CommandResourceClaim(pair.Key, pair.Value))
            .ToArray();
    }

    private void UpdatePendingHighWater(int pending)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _pendingHighWater);
            if (pending <= observed ||
                Interlocked.CompareExchange(ref _pendingHighWater, pending, observed) == observed)
                return;
        }
    }

    private static CommandExecutionResult<TResult> RejectAdmission(
        CommandPriority priority,
        string code,
        string message)
    {
        CommandDispatcherTelemetry.AdmissionRejections.Add(
            1,
            CommandDispatcherTelemetry.ResultTags(priority, code));
        return Rejected(code, message);
    }

    private static CommandExecutionResult<TResult> Rejected(string code, string message) =>
        new(
            Guid.NewGuid().ToString("N"),
            CommandExecutionStatus.Rejected,
            default,
            code,
            message,
            null,
            TimeSpan.Zero);

    private static CommandExecutionResult<TResult> ExpiredBeforeDispatch(Exception? exception = null) =>
        new(
            Guid.NewGuid().ToString("N"),
            CommandExecutionStatus.TimedOut,
            default,
            "command_expired_before_dispatch",
            "Command expired before it crossed the dispatch side-effect boundary.",
            exception,
            TimeSpan.Zero);

    private static CommandExecutionResult<TResult> Cancelled() =>
        new(
            Guid.NewGuid().ToString("N"),
            CommandExecutionStatus.Cancelled,
            default,
            "dispatcher_cancelled",
            "Command was cancelled before dispatch.",
            null,
            TimeSpan.Zero);

}
