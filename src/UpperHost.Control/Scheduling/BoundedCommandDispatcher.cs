using System.Collections.Concurrent;
using System.Threading.Channels;
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
    CommandAdmissionMode AdmissionMode = CommandAdmissionMode.Wait,
    CommandExecutionOptions? Execution = null);

public sealed record BoundedCommandDispatcherOptions(
    int Capacity = 256,
    int PerPriorityCapacity = 128,
    int MaxConcurrency = 4)
{
    internal void Validate()
    {
        if (Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(Capacity));
        if (PerPriorityCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(PerPriorityCapacity));
        if (MaxConcurrency <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency));
    }
}

/// <summary>
/// Bounded, explicitly-owned command dispatcher. Construction has no background side effects;
/// callers must start the dispatcher before admitting commands.
/// </summary>
public sealed class BoundedCommandDispatcher<TCommand, TResult> : IAsyncDisposable
{
    private sealed record Envelope(
        TCommand Command,
        CommandDispatchOptions Options,
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
    private readonly ResourceCoordinator _resources = new();
    private readonly object _activeGate = new();
    private readonly HashSet<Task> _active = [];
    private Task? _pump;
    private int _state;
    private int _pending;
    private int _scheduleIndex;

    public BoundedCommandDispatcher(
        CommandRuntime<TCommand, TResult> runtime,
        BoundedCommandDispatcherOptions? options = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _options = options ?? new BoundedCommandDispatcherOptions();
        _options.Validate();

        _pendingSlots = new SemaphoreSlim(_options.Capacity, _options.Capacity);
        _executionSlots = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
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

        var slotAcquired = options.AdmissionMode switch
        {
            CommandAdmissionMode.Reject => _pendingSlots.Wait(0),
            _ => await WaitForPendingSlotAsync(cancellationToken).ConfigureAwait(false)
        };

        if (!slotAcquired)
            return Rejected("dispatcher_capacity", "Command dispatcher capacity is full.");

        var completion = new TaskCompletionSource<CommandExecutionResult<TResult>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var envelope = new Envelope(command, options, cancellationToken, completion);

        try
        {
            if (options.AdmissionMode == CommandAdmissionMode.Reject)
            {
                if (!lane.Writer.TryWrite(envelope))
                {
                    _pendingSlots.Release();
                    return Rejected("priority_capacity", $"Priority lane '{options.Priority}' is full.");
                }
            }
            else
            {
                await lane.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (ChannelClosedException)
        {
            _pendingSlots.Release();
            return Rejected("dispatcher_stopping", "Command dispatcher is stopping.");
        }
        catch
        {
            _pendingSlots.Release();
            throw;
        }

        Interlocked.Increment(ref _pending);
        _available.Release();
        return await completion.Task.ConfigureAwait(false);
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

            Interlocked.Decrement(ref _pending);
            _pendingSlots.Release();
            return true;
        }

        envelope = null!;
        return false;
    }

    private async Task ExecuteAsync(Envelope envelope, CancellationToken dispatcherToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            dispatcherToken,
            envelope.CancellationToken);

        try
        {
            await using var lease = await _resources
                .AcquireAsync(envelope.Options.Resources, linked.Token)
                .ConfigureAwait(false);

            if (envelope.CancellationToken.IsCancellationRequested)
            {
                envelope.Completion.TrySetResult(Cancelled());
                return;
            }

            var result = await _runtime.ExecuteAsync(
                envelope.Command,
                envelope.Options.Execution,
                linked.Token).ConfigureAwait(false);
            envelope.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
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
                Interlocked.Decrement(ref _pending);
                _pendingSlots.Release();
                envelope.Completion.TrySetResult(Cancelled());
            }
        }
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

    private static CommandExecutionResult<TResult> Cancelled() =>
        new(
            Guid.NewGuid().ToString("N"),
            CommandExecutionStatus.Cancelled,
            default,
            "dispatcher_cancelled",
            "Command was cancelled before dispatch.",
            null,
            TimeSpan.Zero);

    private sealed class ResourceCoordinator
    {
        private readonly ConcurrentDictionary<CommandResourceKey, Entry> _entries = new();

        public async ValueTask<IAsyncDisposable> AcquireAsync(
            IReadOnlyList<CommandResourceKey>? resources,
            CancellationToken cancellationToken)
        {
            if (resources is null || resources.Count == 0)
                return EmptyLease.Instance;

            var keys = resources
                .Distinct()
                .Order()
                .ToArray();

            var acquired = new List<(CommandResourceKey Key, Entry Entry)>(keys.Length);
            try
            {
                foreach (var key in keys)
                {
                    var entry = AcquireReference(key);
                    try
                    {
                        await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                        acquired.Add((key, entry));
                    }
                    catch
                    {
                        ReleaseReference(key, entry);
                        throw;
                    }
                }

                return new ResourceLease(this, acquired);
            }
            catch
            {
                for (var i = acquired.Count - 1; i >= 0; i--)
                {
                    acquired[i].Entry.Semaphore.Release();
                    ReleaseReference(acquired[i].Key, acquired[i].Entry);
                }

                throw;
            }
        }

        private Entry AcquireReference(CommandResourceKey key)
        {
            while (true)
            {
                var entry = _entries.GetOrAdd(key, static _ => new Entry());
                if (entry.TryAddReference())
                    return entry;

                ((ICollection<KeyValuePair<CommandResourceKey, Entry>>)_entries)
                    .Remove(new KeyValuePair<CommandResourceKey, Entry>(key, entry));
            }
        }

        private void ReleaseReference(CommandResourceKey key, Entry entry)
        {
            if (!entry.ReleaseReference())
                return;

            ((ICollection<KeyValuePair<CommandResourceKey, Entry>>)_entries)
                .Remove(new KeyValuePair<CommandResourceKey, Entry>(key, entry));
            entry.Semaphore.Dispose();
        }

        private sealed class Entry
        {
            private readonly object _gate = new();
            private int _references;
            private bool _retired;

            public SemaphoreSlim Semaphore { get; } = new(1, 1);

            public bool TryAddReference()
            {
                lock (_gate)
                {
                    if (_retired)
                        return false;
                    _references++;
                    return true;
                }
            }

            public bool ReleaseReference()
            {
                lock (_gate)
                {
                    _references--;
                    if (_references != 0)
                        return false;

                    _retired = true;
                    return true;
                }
            }
        }

        private sealed class ResourceLease(
            ResourceCoordinator owner,
            List<(CommandResourceKey Key, Entry Entry)> acquired) : IAsyncDisposable
        {
            private int _disposed;

            public ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                    return ValueTask.CompletedTask;

                for (var i = acquired.Count - 1; i >= 0; i--)
                {
                    acquired[i].Entry.Semaphore.Release();
                    owner.ReleaseReference(acquired[i].Key, acquired[i].Entry);
                }

                return ValueTask.CompletedTask;
            }
        }

        private sealed class EmptyLease : IAsyncDisposable
        {
            public static EmptyLease Instance { get; } = new();
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
