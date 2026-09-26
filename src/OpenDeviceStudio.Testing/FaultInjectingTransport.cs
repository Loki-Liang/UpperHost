using System.Runtime.CompilerServices;
using OpenDeviceStudio.Abstractions.Transports;

namespace OpenDeviceStudio.Testing;

public enum FaultKind
{
    InitialOpenFailure,
    ReconnectFailure,
    SendFailure,
    ReceiveFailure,
    SendTimeout,
    ReceiveTimeout,
    Disconnect,
    CommandRejected,
    DroppedReceive,
    CorruptedReceive
}

public sealed record FaultInjectionEvent(
    string ProfileName,
    int Seed,
    FaultKind Kind,
    string Operation,
    long OperationIndex,
    TransportEndpoint Endpoint);

public sealed class FaultInjectedException : IOException
{
    public FaultInjectedException(FaultInjectionEvent fault)
        : base($"Fault profile '{fault.ProfileName}' (seed {fault.Seed}) injected {fault.Kind} at {fault.Endpoint.Scheme}://{fault.Endpoint.Address} during {fault.Operation} #{fault.OperationIndex}.")
    {
        Fault = fault;
    }

    public FaultInjectionEvent Fault { get; }
    public FaultKind Kind => Fault.Kind;
    public int Seed => Fault.Seed;
}

public sealed record TransportFaultProfile(
    string Name,
    int Seed = 0,
    TimeSpan? SendLatency = null,
    TimeSpan? ReceiveLatency = null,
    int FailInitialOpenAttempts = 0,
    int FailReconnectAttempts = 0,
    int FailEverySend = 0,
    int FailEveryReceive = 0,
    int TimeoutEverySend = 0,
    int TimeoutEveryReceive = 0,
    int RejectEverySend = 0,
    int DropEveryReceive = 0,
    int CorruptEveryReceive = 0,
    int MaxReceiveFragmentSize = 0,
    int DisconnectAfterSend = 0,
    int DisconnectAfterReceive = 0)
{
    public static TransportFaultProfile None { get; } = new("none");
}

public sealed record FaultInjectionOptions(
    TimeSpan? SendLatency = null,
    TimeSpan? ReceiveLatency = null,
    int FailEverySend = 0,
    int DropEveryReceive = 0);

public static class FaultProfileExtensions
{
    public static FaultInjectingTransport UseFaultProfile(
        this ITransport transport,
        TransportFaultProfile profile,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(profile);
        return new FaultInjectingTransport(transport, profile, timeProvider ?? TimeProvider.System);
    }
}

public sealed class FaultInjectingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly TransportFaultProfile _profile;
    private readonly TimeProvider _timeProvider;
    private long _sendCount;
    private long _receiveCount;
    private int _initialOpenAttempts;
    private int _reconnectAttempts;
    private int _disposed;
    private bool _everOpened;
    private TransportState _state;

    public FaultInjectingTransport(ITransport inner, FaultInjectionOptions? options = null)
        : this(
            inner,
            new TransportFaultProfile(
                "legacy",
                SendLatency: options?.SendLatency,
                ReceiveLatency: options?.ReceiveLatency,
                FailEverySend: options?.FailEverySend ?? 0,
                DropEveryReceive: options?.DropEveryReceive ?? 0),
            TimeProvider.System)
    {
    }

    internal FaultInjectingTransport(ITransport inner, TransportFaultProfile profile, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ValidateProfile(profile);

        _inner = inner;
        _profile = profile;
        _timeProvider = timeProvider;
        _state = inner.State;
    }

    public event Action<FaultInjectionEvent>? FaultInjected;

    public TransportFaultProfile Profile => _profile;
    public TransportEndpoint Endpoint => _inner.Endpoint;
    public TransportState State => _state;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        if (_state == TransportState.Open)
            return;

        _state = TransportState.Opening;

        if (!_everOpened)
        {
            var attempt = Interlocked.Increment(ref _initialOpenAttempts);
            if (attempt <= _profile.FailInitialOpenAttempts)
            {
                _state = TransportState.Faulted;
                throw CreateFault(FaultKind.InitialOpenFailure, "open", attempt);
            }
        }
        else
        {
            var attempt = Interlocked.Increment(ref _reconnectAttempts);
            if (attempt <= _profile.FailReconnectAttempts)
            {
                _state = TransportState.Faulted;
                throw CreateFault(FaultKind.ReconnectFailure, "reconnect", attempt);
            }
        }

        await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
        _everOpened = true;
        _state = TransportState.Open;
    }

    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _inner.CloseAsync(cancellationToken).ConfigureAwait(false);
        _state = TransportState.Closed;
    }

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOpen();

        var count = Interlocked.Increment(ref _sendCount);
        await DelayAsync(_profile.SendLatency, cancellationToken).ConfigureAwait(false);

        ThrowIfTriggered(_profile.TimeoutEverySend, count, FaultKind.SendTimeout, "send");
        ThrowIfTriggered(_profile.RejectEverySend, count, FaultKind.CommandRejected, "send");
        ThrowIfTriggered(_profile.FailEverySend, count, FaultKind.SendFailure, "send", faultTransport: true);

        await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);

        if (_profile.DisconnectAfterSend > 0 && count == _profile.DisconnectAfterSend)
            await DisconnectAsync("send", count).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        EnsureOpen();

        await foreach (var chunk in _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            var count = Interlocked.Increment(ref _receiveCount);
            await DelayAsync(_profile.ReceiveLatency, cancellationToken).ConfigureAwait(false);

            ThrowIfTriggered(_profile.TimeoutEveryReceive, count, FaultKind.ReceiveTimeout, "receive");
            ThrowIfTriggered(_profile.FailEveryReceive, count, FaultKind.ReceiveFailure, "receive", faultTransport: true);

            if (_profile.DisconnectAfterReceive > 0 && count == _profile.DisconnectAfterReceive)
            {
                await DisconnectAsync("receive", count).ConfigureAwait(false);
            }

            if (IsTriggered(_profile.DropEveryReceive, count))
            {
                Publish(FaultKind.DroppedReceive, "receive", count);
                continue;
            }

            var payload = chunk.ToArray();
            if (IsTriggered(_profile.CorruptEveryReceive, count) && payload.Length > 0)
            {
                payload[^1] ^= CorruptionMask(_profile.Seed, count);
                Publish(FaultKind.CorruptedReceive, "receive", count);
            }

            if (_profile.MaxReceiveFragmentSize <= 0 || payload.Length <= _profile.MaxReceiveFragmentSize)
            {
                yield return payload;
                continue;
            }

            for (var offset = 0; offset < payload.Length; offset += _profile.MaxReceiveFragmentSize)
            {
                var length = Math.Min(_profile.MaxReceiveFragmentSize, payload.Length - offset);
                yield return payload.AsMemory(offset, length).ToArray();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _state = TransportState.Closed;
        }
    }

    private async Task DelayAsync(TimeSpan? latency, CancellationToken cancellationToken)
    {
        if (latency is not { } value || value <= TimeSpan.Zero)
            return;

        await Task.Delay(value, _timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private async Task DisconnectAsync(string operation, long operationIndex)
    {
        await _inner.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        _state = TransportState.Faulted;
        throw CreateFault(FaultKind.Disconnect, operation, operationIndex);
    }

    private void ThrowIfTriggered(
        int every,
        long operationIndex,
        FaultKind kind,
        string operation,
        bool faultTransport = false)
    {
        if (!IsTriggered(every, operationIndex))
            return;

        if (faultTransport)
            _state = TransportState.Faulted;

        throw CreateFault(kind, operation, operationIndex);
    }

    private FaultInjectedException CreateFault(FaultKind kind, string operation, long operationIndex)
    {
        var fault = new FaultInjectionEvent(_profile.Name, _profile.Seed, kind, operation, operationIndex, Endpoint);
        FaultInjected?.Invoke(fault);
        return new FaultInjectedException(fault);
    }

    private void Publish(FaultKind kind, string operation, long operationIndex) =>
        FaultInjected?.Invoke(new FaultInjectionEvent(_profile.Name, _profile.Seed, kind, operation, operationIndex, Endpoint));

    private void EnsureOpen()
    {
        if (_state != TransportState.Open)
            throw new InvalidOperationException($"Fault-injecting transport '{Endpoint.Scheme}://{Endpoint.Address}' is not open; state={_state}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    private static bool IsTriggered(int every, long operationIndex) =>
        every > 0 && operationIndex % every == 0;

    private static byte CorruptionMask(int seed, long operationIndex)
    {
        var mixed = unchecked(seed * 397) ^ unchecked((int)operationIndex * 7919);
        var value = (byte)(mixed & 0xFF);
        return value == 0 ? (byte)0xA5 : value;
    }

    private static void ValidateProfile(TransportFaultProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Name))
            throw new ArgumentException("Fault profile name is required.", nameof(profile));

        if (profile.SendLatency < TimeSpan.Zero || profile.ReceiveLatency < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(profile), "Fault latency cannot be negative.");

        var counters = new[]
        {
            profile.FailInitialOpenAttempts,
            profile.FailReconnectAttempts,
            profile.FailEverySend,
            profile.FailEveryReceive,
            profile.TimeoutEverySend,
            profile.TimeoutEveryReceive,
            profile.RejectEverySend,
            profile.DropEveryReceive,
            profile.CorruptEveryReceive,
            profile.MaxReceiveFragmentSize,
            profile.DisconnectAfterSend,
            profile.DisconnectAfterReceive
        };

        if (counters.Any(static value => value < 0))
            throw new ArgumentOutOfRangeException(nameof(profile), "Fault profile counters cannot be negative.");
    }
}
