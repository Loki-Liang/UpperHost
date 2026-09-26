using System.Collections.Concurrent;

namespace UpperHost.Control.Scheduling;

public enum CommandResourceAccess
{
    SharedRead,
    Exclusive
}

public sealed record CommandResourceClaim(
    CommandResourceKey Resource,
    CommandResourceAccess Access = CommandResourceAccess.Exclusive);

public interface ICommandResourceArbiter
{
    ValueTask<IAsyncDisposable> AcquireAsync(
        IReadOnlyList<CommandResourceClaim> claims,
        CancellationToken cancellationToken = default);
}

internal sealed class CommandResourcePendingLimiter(int capacity)
{
    private readonly ConcurrentDictionary<CommandResourceKey, Entry> _entries = new();

    public IDisposable? TryReserve(IReadOnlyList<CommandResourceClaim> claims)
    {
        if (claims.Count == 0)
            return EmptyReservation.Instance;

        var acquired = new List<(CommandResourceKey Key, Entry Entry)>(claims.Count);
        foreach (var claim in claims)
        {
            var entry = AcquireReference(claim.Resource);
            if (!entry.Slots.Wait(0))
            {
                ReleaseReference(claim.Resource, entry);
                ReleaseAll(acquired);
                return null;
            }

            acquired.Add((claim.Resource, entry));
        }

        return new Reservation(this, acquired);
    }

    public async ValueTask<IDisposable> ReserveAsync(
        IReadOnlyList<CommandResourceClaim> claims,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
            return EmptyReservation.Instance;

        var acquired = new List<(CommandResourceKey Key, Entry Entry)>(claims.Count);
        try
        {
            foreach (var claim in claims)
            {
                var entry = AcquireReference(claim.Resource);
                try
                {
                    await entry.Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquired.Add((claim.Resource, entry));
                }
                catch
                {
                    ReleaseReference(claim.Resource, entry);
                    throw;
                }
            }

            return new Reservation(this, acquired);
        }
        catch
        {
            ReleaseAll(acquired);
            throw;
        }
    }

    private Entry AcquireReference(CommandResourceKey key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry(capacity));
            if (entry.TryAddReference())
                return entry;

            ((ICollection<KeyValuePair<CommandResourceKey, Entry>>)_entries)
                .Remove(new KeyValuePair<CommandResourceKey, Entry>(key, entry));
        }
    }

    private void ReleaseAll(List<(CommandResourceKey Key, Entry Entry)> acquired)
    {
        for (var i = acquired.Count - 1; i >= 0; i--)
        {
            acquired[i].Entry.Slots.Release();
            ReleaseReference(acquired[i].Key, acquired[i].Entry);
        }
    }

    private void ReleaseReference(CommandResourceKey key, Entry entry)
    {
        if (!entry.ReleaseReference())
            return;

        ((ICollection<KeyValuePair<CommandResourceKey, Entry>>)_entries)
            .Remove(new KeyValuePair<CommandResourceKey, Entry>(key, entry));
        entry.Slots.Dispose();
    }

    private sealed class Entry(int capacity)
    {
        private readonly object _gate = new();
        private int _references;
        private bool _retired;

        public SemaphoreSlim Slots { get; } = new(capacity, capacity);

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

    private sealed class Reservation(
        CommandResourcePendingLimiter owner,
        List<(CommandResourceKey Key, Entry Entry)> acquired) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            owner.ReleaseAll(acquired);
        }
    }

    private sealed class EmptyReservation : IDisposable
    {
        public static EmptyReservation Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class CommandResourceCoordinator(int maxSharedReadersPerResource)
    : ICommandResourceArbiter
{
    private readonly ConcurrentDictionary<CommandResourceKey, Entry> _entries = new();

    public async ValueTask<IAsyncDisposable> AcquireAsync(
        IReadOnlyList<CommandResourceClaim> claims,
        CancellationToken cancellationToken)
    {
        if (claims.Count == 0)
            return EmptyLease.Instance;

        var acquired = new List<(CommandResourceKey Key, Entry Entry, IAsyncDisposable Lease)>(claims.Count);
        try
        {
            foreach (var claim in claims)
            {
                var entry = AcquireReference(claim.Resource);
                try
                {
                    var lease = await entry.Gate
                        .AcquireAsync(claim.Access, cancellationToken)
                        .ConfigureAwait(false);
                    acquired.Add((claim.Resource, entry, lease));
                }
                catch
                {
                    ReleaseReference(claim.Resource, entry);
                    throw;
                }
            }

            return new ResourceSetLease(this, acquired);
        }
        catch
        {
            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                await acquired[i].Lease.DisposeAsync().ConfigureAwait(false);
                ReleaseReference(acquired[i].Key, acquired[i].Entry);
            }

            throw;
        }
    }

    private Entry AcquireReference(CommandResourceKey key)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(key, _ => new Entry(maxSharedReadersPerResource));
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
    }

    private sealed class Entry(int maxSharedReaders)
    {
        private readonly object _gate = new();
        private int _references;
        private bool _retired;

        public AsyncResourceGate Gate { get; } = new(maxSharedReaders);

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

    private sealed class ResourceSetLease(
        CommandResourceCoordinator owner,
        List<(CommandResourceKey Key, Entry Entry, IAsyncDisposable Lease)> acquired) : IAsyncDisposable
    {
        private int _disposed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            for (var i = acquired.Count - 1; i >= 0; i--)
            {
                await acquired[i].Lease.DisposeAsync().ConfigureAwait(false);
                owner.ReleaseReference(acquired[i].Key, acquired[i].Entry);
            }
        }
    }

    private sealed class EmptyLease : IAsyncDisposable
    {
        public static EmptyLease Instance { get; } = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class AsyncResourceGate
{
    private sealed record Waiter(
        TaskCompletionSource<IAsyncDisposable> Completion,
        CancellationTokenRegistration Cancellation);

    private readonly object _gate = new();
    private readonly int _maxReaders;
    private readonly Queue<Waiter> _readers = new();
    private readonly Queue<Waiter> _writers = new();
    private int _activeReaders;
    private bool _writerActive;

    public AsyncResourceGate(int maxReaders)
    {
        if (maxReaders <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxReaders));
        _maxReaders = maxReaders;
    }

    public ValueTask<IAsyncDisposable> AcquireAsync(
        CommandResourceAccess access,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (access == CommandResourceAccess.SharedRead &&
                !_writerActive &&
                _writers.Count == 0 &&
                _activeReaders < _maxReaders)
            {
                _activeReaders++;
                return ValueTask.FromResult<IAsyncDisposable>(
                    new GateLease(this, CommandResourceAccess.SharedRead));
            }

            if (access == CommandResourceAccess.Exclusive &&
                !_writerActive &&
                _activeReaders == 0)
            {
                _writerActive = true;
                return ValueTask.FromResult<IAsyncDisposable>(
                    new GateLease(this, CommandResourceAccess.Exclusive));
            }

            var completion = new TaskCompletionSource<IAsyncDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cancellation = cancellationToken.Register(
                static state =>
                {
                    var pair = ((TaskCompletionSource<IAsyncDisposable> Completion, CancellationToken Token))state!;
                    pair.Completion.TrySetCanceled(pair.Token);
                },
                (completion, cancellationToken));
            var waiter = new Waiter(completion, cancellation);

            if (access == CommandResourceAccess.Exclusive)
                _writers.Enqueue(waiter);
            else
                _readers.Enqueue(waiter);

            return new ValueTask<IAsyncDisposable>(completion.Task);
        }
    }

    private void Release(CommandResourceAccess access)
    {
        List<(Waiter Waiter, IAsyncDisposable Lease)> ready = [];

        lock (_gate)
        {
            if (access == CommandResourceAccess.Exclusive)
                _writerActive = false;
            else
                _activeReaders--;

            DrainCancelled(_writers);
            DrainCancelled(_readers);

            while (!_writerActive && _activeReaders == 0 && _writers.Count > 0)
            {
                var writer = _writers.Dequeue();
                writer.Cancellation.Dispose();
                var lease = new GateLease(this, CommandResourceAccess.Exclusive);
                if (!writer.Completion.TrySetResult(lease))
                    continue;

                _writerActive = true;
                ready.Add((writer, lease));
                break;
            }

            if (_writerActive || _writers.Count > 0)
                return;

            while (_activeReaders < _maxReaders && _readers.Count > 0)
            {
                var reader = _readers.Dequeue();
                reader.Cancellation.Dispose();
                var lease = new GateLease(this, CommandResourceAccess.SharedRead);
                if (!reader.Completion.TrySetResult(lease))
                    continue;

                _activeReaders++;
                ready.Add((reader, lease));
            }
        }
    }

    private static void DrainCancelled(Queue<Waiter> queue)
    {
        while (queue.Count > 0 && queue.Peek().Completion.Task.IsCanceled)
        {
            var cancelled = queue.Dequeue();
            cancelled.Cancellation.Dispose();
        }
    }

    private sealed class GateLease(
        AsyncResourceGate owner,
        CommandResourceAccess access) : IAsyncDisposable
    {
        private int _disposed;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                owner.Release(access);

            return ValueTask.CompletedTask;
        }
    }
}
