using System.Diagnostics;
using UpperHost.Abstractions.Observability;
using UpperHost.Abstractions.Transports;

namespace UpperHost.Observability;

public sealed class ObservedTransport : ITransport
{
    private readonly ITransport _inner;

    public ObservedTransport(ITransport inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public TransportEndpoint Endpoint => _inner.Endpoint;
    public TransportState State => _inner.State;

    public Task OpenAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync("open", token => _inner.OpenAsync(token), cancellationToken);

    public Task CloseAsync(CancellationToken cancellationToken = default) =>
        ObserveAsync("close", token => _inner.CloseAsync(token), cancellationToken);

    public async ValueTask SendAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity("send");
        var timer = Stopwatch.StartNew();
        try
        {
            await _inner.SendAsync(data, cancellationToken).ConfigureAwait(false);
            timer.Stop();
            Record("send", "success", null);
            UpperHostTelemetry.TransportBytes.Add(
                data.Length,
                UpperHostTelemetry.CreateTags(Context("send", "success")));
            activity?.SetTag("upperhost.transport.bytes", data.Length);
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            Record("send", "faulted", "transport_send_fault");
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
            activity?.SetTag("upperhost.error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
        {
            Record("receive", "success", null);
            UpperHostTelemetry.TransportBytes.Add(
                chunk.Length,
                UpperHostTelemetry.CreateTags(Context("receive", "success")));
            yield return chunk;
        }
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();

    private async Task ObserveAsync(
        string operation,
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        using var activity = StartActivity(operation);
        var timer = Stopwatch.StartNew();
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
            timer.Stop();
            Record(operation, "success", null);
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            timer.Stop();
            Record(operation, "faulted", $"transport_{operation}_fault");
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
            activity?.SetTag("upperhost.error.type", ex.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private Activity? StartActivity(string operation) =>
        UpperHostTelemetry.StartActivity(
            $"upperhost.transport.{operation}",
            ActivityKind.Client,
            Context(operation));

    private UpperHostTelemetryContext Context(
        string operation,
        string? result = null,
        string? errorCode = null) =>
        new(
            Transport: Endpoint.Scheme,
            Operation: operation,
            Result: result,
            ErrorCode: errorCode);

    private void Record(string operation, string result, string? errorCode)
    {
        var tags = UpperHostTelemetry.CreateTags(Context(operation, result, errorCode));
        UpperHostTelemetry.TransportOperations.Add(1, tags);
        if (errorCode is not null)
            UpperHostTelemetry.TransportFailures.Add(1, tags);
    }
}
