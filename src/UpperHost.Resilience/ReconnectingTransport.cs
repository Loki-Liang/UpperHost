using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Resilience;

public sealed record ReconnectPolicy(
    int MaxAttempts = 5,
    TimeSpan? InitialDelay = null,
    double BackoffFactor = 2.0,
    TimeSpan? MaximumDelay = null,
    bool ReconnectOnEndOfStream = true)
{
    public TimeSpan FirstDelay => InitialDelay ?? TimeSpan.FromMilliseconds(250);
    public TimeSpan MaxDelay => MaximumDelay ?? TimeSpan.FromSeconds(5);

    public void Validate()
    {
        if (MaxAttempts < 0) throw new ArgumentOutOfRangeException(nameof(MaxAttempts));
        if (FirstDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(InitialDelay));
        if (BackoffFactor < 1) throw new ArgumentOutOfRangeException(nameof(BackoffFactor));
        if (MaxDelay < FirstDelay) throw new ArgumentOutOfRangeException(nameof(MaximumDelay));
    }
}

public sealed class ReconnectingTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly ReconnectPolicy _policy;
    private readonly SemaphoreSlim _reconnectGate = new(1, 1);

    public ReconnectingTransport(ITransport inner, ReconnectPolicy? policy = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? new ReconnectPolicy();
        _policy.Validate();
    }

    public TransportEndpoint Endpoint => _inner.Endpoint;
    public TransportState State => _inner.State;

    public Task OpenAsync(CancellationToken cancellationToken = default) => _inner.OpenAsync(cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default) => _inner.CloseAsync(cancellationToken);

    public async ValueTask SendAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;

        for (var attempt = 0; attempt <= _policy.MaxAttempts; attempt++)
        {
            try
            {
                await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (attempt >= _policy.MaxAttempts)
                    break;

                await ReconnectAsync(attempt + 1, cancellationToken).ConfigureAwait(false);
            }
        }

        ExceptionDispatchInfo.Capture(lastError!).Throw();
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<ReadOnlyMemory<byte>>? enumerator = null;
        var reconnectAttempts = 0;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                enumerator ??= _inner.ReceiveAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

                var hasItem = false;
                ReadOnlyMemory<byte> current = default;
                Exception? failure = null;

                try
                {
                    hasItem = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasItem)
                        current = enumerator.Current;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failure = ex;
                }

                if (failure is null && hasItem)
                {
                    reconnectAttempts = 0;
                    yield return current;
                    continue;
                }

                await enumerator.DisposeAsync().ConfigureAwait(false);
                enumerator = null;

                if (failure is null && !_policy.ReconnectOnEndOfStream)
                    yield break;

                if (reconnectAttempts >= _policy.MaxAttempts)
                {
                    if (failure is not null)
                        ExceptionDispatchInfo.Capture(failure).Throw();
                    yield break;
                }

                reconnectAttempts++;
                await ReconnectAsync(reconnectAttempts, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (enumerator is not null)
                await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        _reconnectGate.Dispose();
    }

    private async Task ReconnectAsync(int attempt, CancellationToken cancellationToken)
    {
        await _reconnectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            try
            {
                await _inner.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // A broken transport can fail while closing. Re-open remains the recovery action.
            }

            await Task.Delay(GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            await _inner.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reconnectGate.Release();
        }
    }

    private TimeSpan GetDelay(int attempt)
    {
        var multiplier = Math.Pow(_policy.BackoffFactor, Math.Max(0, attempt - 1));
        var ticks = Math.Min(_policy.MaxDelay.Ticks, _policy.FirstDelay.Ticks * multiplier);
        return TimeSpan.FromTicks((long)ticks);
    }
}
