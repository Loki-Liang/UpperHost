using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace UpperHost.Dataflow;

public enum StreamBranchDelivery
{
    Required,
    Optional
}

public enum StreamOverflowPolicy
{
    Wait,
    Reject,
    DropOldest,
    DropNewest,
    Latest
}

public enum StreamBranchFailurePolicy
{
    Propagate,
    Isolate
}

public enum StreamOwnershipPolicy
{
    SharedImmutable,
    CopyPerBranch
}

public sealed record StreamBranchOptions(
    string Name,
    int Capacity = 256,
    StreamBranchDelivery Delivery = StreamBranchDelivery.Optional,
    StreamOverflowPolicy Overflow = StreamOverflowPolicy.DropOldest,
    StreamBranchFailurePolicy FailurePolicy = StreamBranchFailurePolicy.Isolate,
    StreamOwnershipPolicy Ownership = StreamOwnershipPolicy.SharedImmutable);

public sealed record StreamBranchSnapshot(
    string Name,
    StreamBranchDelivery Delivery,
    StreamOverflowPolicy Overflow,
    StreamBranchFailurePolicy FailurePolicy,
    int Capacity,
    int QueueDepth,
    int HighWatermark,
    long Accepted,
    long Consumed,
    long Dropped,
    long Rejected,
    bool IsFaulted,
    string? FaultMessage);

public sealed class StreamBranchOverflowException(string branchName)
    : InvalidOperationException($"Stream branch '{branchName}' rejected an item because its bounded capacity was exhausted.");

public sealed class StreamBranchFaultException(string branchName, Exception innerException)
    : InvalidOperationException($"Required stream branch '{branchName}' is faulted.", innerException);

public sealed class StreamRouter<T> : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, StreamBranch<T>> _branches = new(StringComparer.Ordinal);
    private readonly Func<T, T>? _branchCloner;
    private readonly Action<T>? _releaseOwnedItem;
    private int _requiredTopologySealed;
    private int _disposed;

    public StreamRouter(Func<T, T>? branchCloner = null, Action<T>? releaseOwnedItem = null)
    {
        _branchCloner = branchCloner;
        _releaseOwnedItem = releaseOwnedItem;
    }

    public bool RequiredTopologySealed => Volatile.Read(ref _requiredTopologySealed) != 0;

    public int BranchCount => _branches.Count;

    public StreamBranch<T> RegisterBranch(StreamBranchOptions options)
    {
        ThrowIfDisposed();
        Validate(options);

        if (options.Delivery == StreamBranchDelivery.Required && RequiredTopologySealed)
            throw new InvalidOperationException("Required stream topology is sealed; new Required branches cannot be registered.");

        var branch = new StreamBranch<T>(
            options,
            options.Ownership == StreamOwnershipPolicy.CopyPerBranch ? _releaseOwnedItem : null,
            RemoveBranch);

        if (!_branches.TryAdd(options.Name, branch))
        {
            branch.DisposeFromRouter();
            throw new InvalidOperationException($"A stream branch named '{options.Name}' is already registered.");
        }

        if (options.Delivery == StreamBranchDelivery.Required && RequiredTopologySealed)
        {
            if (_branches.TryRemove(options.Name, out var removed))
                removed.DisposeFromRouter();

            throw new InvalidOperationException("Required stream topology was sealed while the branch was being registered.");
        }

        return branch;
    }

    public void SealRequiredTopology()
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _requiredTopologySealed, 1);
    }

    public async ValueTask PublishAsync(T item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        foreach (var branch in _branches.Values
                     .OrderBy(static branch => branch.Options.Delivery)
                     .ThenBy(static branch => branch.Options.Name, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (branch.TryGetFault(out var fault))
            {
                if (branch.Options.Delivery == StreamBranchDelivery.Required ||
                    branch.Options.FailurePolicy == StreamBranchFailurePolicy.Propagate)
                {
                    throw new StreamBranchFaultException(branch.Options.Name, fault);
                }

                continue;
            }

            var branchItem = item;
            var ownsCopy = branch.Options.Ownership == StreamOwnershipPolicy.CopyPerBranch;
            if (ownsCopy)
            {
                if (_branchCloner is null)
                    throw new InvalidOperationException(
                        $"Branch '{branch.Options.Name}' requires CopyPerBranch ownership, but the router has no cloner.");

                branchItem = _branchCloner(item);
            }

            var accepted = false;
            try
            {
                accepted = await branch.PublishAsync(branchItem, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (ownsCopy && !accepted)
                    _releaseOwnedItem?.Invoke(branchItem);
            }
        }
    }

    public IReadOnlyList<StreamBranchSnapshot> GetSnapshots() =>
        _branches.Values
            .OrderBy(static branch => branch.Options.Name, StringComparer.Ordinal)
            .Select(static branch => branch.GetSnapshot())
            .ToArray();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        var branches = _branches.Values.ToArray();
        _branches.Clear();

        foreach (var branch in branches)
            await branch.DisposeFromRouterAsync().ConfigureAwait(false);
    }

    private void RemoveBranch(StreamBranch<T> branch)
    {
        if (_branches.TryGetValue(branch.Options.Name, out var current) &&
            ReferenceEquals(current, branch))
        {
            _branches.TryRemove(branch.Options.Name, out _);
        }
    }

    private void Validate(StreamBranchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Name))
            throw new ArgumentException("Stream branch name is required.", nameof(options));

        if (options.Capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Stream branch capacity must be greater than zero.");

        if (options.Delivery == StreamBranchDelivery.Required &&
            options.Overflow is StreamOverflowPolicy.DropOldest or StreamOverflowPolicy.DropNewest or StreamOverflowPolicy.Latest)
        {
            throw new ArgumentException(
                "Required branches cannot use a lossy overflow policy. Use Wait or Reject.",
                nameof(options));
        }

        if (options.Delivery == StreamBranchDelivery.Required &&
            options.FailurePolicy == StreamBranchFailurePolicy.Isolate)
        {
            throw new ArgumentException(
                "Required branches cannot isolate failures; Required failure must propagate to the owning route/session.",
                nameof(options));
        }

        if (options.Ownership == StreamOwnershipPolicy.CopyPerBranch && _branchCloner is null)
        {
            throw new ArgumentException(
                "CopyPerBranch ownership requires a router branch cloner.",
                nameof(options));
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }
}

public sealed class StreamBranch<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly SemaphoreSlim _publishGate = new(1, 1);
    private readonly Action<T>? _releaseOwnedItem;
    private readonly Action<StreamBranch<T>> _onDispose;
    private Exception? _fault;
    private long _accepted;
    private long _consumed;
    private long _dropped;
    private long _rejected;
    private int _highWatermark;
    private int _disposed;

    internal StreamBranch(
        StreamBranchOptions options,
        Action<T>? releaseOwnedItem,
        Action<StreamBranch<T>> onDispose)
    {
        Options = options;
        _releaseOwnedItem = releaseOwnedItem;
        _onDispose = onDispose;
        _channel = Channel.CreateBounded<T>(new BoundedChannelOptions(options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    }

    public StreamBranchOptions Options { get; }

    public async ValueTask<T> ReadAsync(CancellationToken cancellationToken = default)
    {
        var item = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        Interlocked.Increment(ref _consumed);
        return item;
    }

    public async IAsyncEnumerable<T> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref _consumed);
            yield return item;
        }
    }

    public void ReportFailure(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);

        if (Interlocked.CompareExchange(ref _fault, error, null) is null)
            _channel.Writer.TryComplete(error);
    }

    public StreamBranchSnapshot GetSnapshot()
    {
        var fault = Volatile.Read(ref _fault);
        var depth = _channel.Reader.CanCount ? _channel.Reader.Count : 0;

        return new StreamBranchSnapshot(
            Options.Name,
            Options.Delivery,
            Options.Overflow,
            Options.FailurePolicy,
            Options.Capacity,
            depth,
            Volatile.Read(ref _highWatermark),
            Interlocked.Read(ref _accepted),
            Interlocked.Read(ref _consumed),
            Interlocked.Read(ref _dropped),
            Interlocked.Read(ref _rejected),
            fault is not null,
            fault?.Message);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return ValueTask.CompletedTask;

        _onDispose(this);
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    internal bool TryGetFault(out Exception fault)
    {
        fault = Volatile.Read(ref _fault)!;
        return fault is not null;
    }

    internal async ValueTask<bool> PublishAsync(T item, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return false;

        if (TryGetFault(out var fault))
        {
            if (Options.Delivery == StreamBranchDelivery.Required ||
                Options.FailurePolicy == StreamBranchFailurePolicy.Propagate)
            {
                throw new StreamBranchFaultException(Options.Name, fault);
            }

            return false;
        }

        await _publishGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return false;

            return Options.Overflow switch
            {
                StreamOverflowPolicy.Wait => await WriteWaitAsync(item, cancellationToken).ConfigureAwait(false),
                StreamOverflowPolicy.Reject => WriteReject(item),
                StreamOverflowPolicy.DropOldest => WriteDropOldest(item),
                StreamOverflowPolicy.DropNewest => WriteDropNewest(item),
                StreamOverflowPolicy.Latest => WriteLatest(item),
                _ => throw new ArgumentOutOfRangeException()
            };
        }
        finally
        {
            _publishGate.Release();
        }
    }

    internal void DisposeFromRouter()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            DisposeCore();
    }

    internal ValueTask DisposeFromRouterAsync()
    {
        DisposeFromRouter();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<bool> WriteWaitAsync(T item, CancellationToken cancellationToken)
    {
        try
        {
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            MarkAccepted();
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
    }

    private bool WriteReject(T item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            MarkAccepted();
            return true;
        }

        Interlocked.Increment(ref _rejected);

        if (Options.Delivery == StreamBranchDelivery.Required ||
            Options.FailurePolicy == StreamBranchFailurePolicy.Propagate)
        {
            throw new StreamBranchOverflowException(Options.Name);
        }

        return false;
    }

    private bool WriteDropNewest(T item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            MarkAccepted();
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private bool WriteDropOldest(T item)
    {
        if (_channel.Writer.TryWrite(item))
        {
            MarkAccepted();
            return true;
        }

        if (_channel.Reader.TryRead(out var dropped))
        {
            Interlocked.Increment(ref _dropped);
            _releaseOwnedItem?.Invoke(dropped);
        }

        if (_channel.Writer.TryWrite(item))
        {
            MarkAccepted();
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private bool WriteLatest(T item)
    {
        while (_channel.Reader.TryRead(out var dropped))
        {
            Interlocked.Increment(ref _dropped);
            _releaseOwnedItem?.Invoke(dropped);
        }

        if (_channel.Writer.TryWrite(item))
        {
            MarkAccepted();
            return true;
        }

        Interlocked.Increment(ref _dropped);
        return false;
    }

    private void MarkAccepted()
    {
        Interlocked.Increment(ref _accepted);
        var depth = _channel.Reader.CanCount ? _channel.Reader.Count : 0;

        while (true)
        {
            var current = Volatile.Read(ref _highWatermark);
            if (depth <= current)
                return;

            if (Interlocked.CompareExchange(ref _highWatermark, depth, current) == current)
                return;
        }
    }

    private void DisposeCore()
    {
        _channel.Writer.TryComplete();

        while (_channel.Reader.TryRead(out var remaining))
            _releaseOwnedItem?.Invoke(remaining);
    }
}
