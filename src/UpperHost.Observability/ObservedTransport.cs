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
            Record("send", "success");
            UpperHostTelemetry.TransportBytes.Add(
                data.Length,
                UpperHostTelemetry.CreateMetricTags(MetricContext("send", "success")));
            activity?.SetTag("upperhost.transport.bytes", data.Length);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Unset);
            throw;
        }
        catch (Exception ex)
        {
            Record("send", "faulted", ex.GetType().FullName);
            MarkFailure(activity, ex, "transport_send_fault");
            throw;
        }
        finally
        {
            timer.Stop();
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity("receive");
        var timer = Stopwatch.StartNew();
        try
        {
            await foreach (var chunk in _inner.ReceiveAsync(cancellationToken).ConfigureAwait(false))
            {
                Record("receive", "success");
                UpperHostTelemetry.TransportBytes.Add(
                    chunk.Length,
                    UpperHostTelemetry.CreateMetricTags(MetricContext("receive", "success")));
                yield return chunk;
            }

            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Unset);
            throw;
        }
        catch (Exception ex)
        {
            Record("receive", "faulted", ex.GetType().FullName);
            MarkFailure(activity, ex, "transport_receive_fault");
            throw;
        }
        finally
        {
            timer.Stop();
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
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
            Record(operation, "success");
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Unset);
            throw;
        }
        catch (Exception ex)
        {
            Record(operation, "faulted", ex.GetType().FullName);
            MarkFailure(activity, ex, $"transport_{operation}_fault");
            throw;
        }
        finally
        {
            timer.Stop();
            activity?.SetTag("upperhost.elapsed_ms", timer.Elapsed.TotalMilliseconds);
        }
    }

    private Activity? StartActivity(string operation) =>
        UpperHostTelemetry.StartActivity(
            $"upperhost.transport.{operation}",
            ActivityKind.Client,
            new UpperHostTelemetryContext(
                Transport: Endpoint.Scheme,
                Operation: operation));

    private UpperHostMetricContext MetricContext(
        string operation,
        string outcome,
        string? errorType = null) =>
        new(
            Transport: Endpoint.Scheme,
            Operation: operation,
            Outcome: outcome,
            ErrorType: errorType);

    private void Record(string operation, string outcome, string? errorType = null)
    {
        var tags = UpperHostTelemetry.CreateMetricTags(MetricContext(operation, outcome, errorType));
        UpperHostTelemetry.TransportOperations.Add(1, tags);
        if (errorType is not null)
            UpperHostTelemetry.TransportFailures.Add(1, tags);
    }

    private static void MarkFailure(Activity? activity, Exception exception, string errorCode)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("upperhost.error.code", errorCode);
        activity?.SetStatus(ActivityStatusCode.Error, errorCode);
    }
}
