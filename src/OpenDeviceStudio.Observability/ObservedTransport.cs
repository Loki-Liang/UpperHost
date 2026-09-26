using System.Diagnostics;
using OpenDeviceStudio.Abstractions.Observability;
using OpenDeviceStudio.Abstractions.Transports;

namespace OpenDeviceStudio.Observability;

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
            OpenDeviceStudioTelemetry.TransportBytes.Add(
                data.Length,
                OpenDeviceStudioTelemetry.CreateMetricTags(MetricContext("send", "success")));
            activity?.SetTag("opendevicestudio.transport.bytes", data.Length);
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
            activity?.SetTag("opendevicestudio.elapsed_ms", timer.Elapsed.TotalMilliseconds);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReceiveAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var activity = StartActivity("receive");
        var timer = Stopwatch.StartNew();
        await using var enumerator = _inner
            .ReceiveAsync(cancellationToken)
            .GetAsyncEnumerator(cancellationToken);

        try
        {
            while (true)
            {
                bool hasItem;
                ReadOnlyMemory<byte> chunk = default;

                try
                {
                    hasItem = await enumerator.MoveNextAsync().ConfigureAwait(false);
                    if (hasItem)
                        chunk = enumerator.Current;
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

                if (!hasItem)
                {
                    activity?.SetStatus(ActivityStatusCode.Ok);
                    yield break;
                }

                Record("receive", "success");
                OpenDeviceStudioTelemetry.TransportBytes.Add(
                    chunk.Length,
                    OpenDeviceStudioTelemetry.CreateMetricTags(MetricContext("receive", "success")));
                yield return chunk;
            }
        }
        finally
        {
            timer.Stop();
            activity?.SetTag("opendevicestudio.elapsed_ms", timer.Elapsed.TotalMilliseconds);
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
            activity?.SetTag("opendevicestudio.elapsed_ms", timer.Elapsed.TotalMilliseconds);
        }
    }

    private Activity? StartActivity(string operation) =>
        OpenDeviceStudioTelemetry.StartActivity(
            $"opendevicestudio.transport.{operation}",
            ActivityKind.Client,
            new OpenDeviceStudioTelemetryContext(
                Transport: Endpoint.Scheme,
                Operation: operation));

    private OpenDeviceStudioMetricContext MetricContext(
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
        var tags = OpenDeviceStudioTelemetry.CreateMetricTags(MetricContext(operation, outcome, errorType));
        OpenDeviceStudioTelemetry.TransportOperations.Add(1, tags);
        if (errorType is not null)
            OpenDeviceStudioTelemetry.TransportFailures.Add(1, tags);
    }

    private static void MarkFailure(Activity? activity, Exception exception, string errorCode)
    {
        activity?.SetTag("error.type", exception.GetType().FullName);
        activity?.SetTag("opendevicestudio.error.code", errorCode);
        activity?.SetStatus(ActivityStatusCode.Error, errorCode);
    }
}
