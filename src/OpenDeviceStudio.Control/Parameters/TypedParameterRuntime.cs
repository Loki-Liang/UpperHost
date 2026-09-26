using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Control.Scheduling;
using OpenDeviceStudio.Control.State;

namespace OpenDeviceStudio.Control.Parameters;

public interface IParameterValueCodec<T>
{
    T Decode(object? providerValue);
    object? Encode(T value);
}

public interface IParameterValueComparer<in T>
{
    bool AreEquivalent(T expected, T actual);
}

public static class ParameterCodecs
{
    public static IParameterValueCodec<T> Strict<T>() => StrictCodec<T>.Instance;

    private sealed class StrictCodec<T> : IParameterValueCodec<T>
    {
        public static StrictCodec<T> Instance { get; } = new();

        public T Decode(object? providerValue)
        {
            if (providerValue is T value)
                return value;
            if (providerValue is null && default(T) is null)
                return default!;

            throw new InvalidCastException(
                $"Provider value '{providerValue ?? "<null>"}' is not assignable to {typeof(T).FullName}.");
        }

        public object? Encode(T value) => value;
    }
}

public static class ParameterComparers
{
    public static IParameterValueComparer<T> Exact<T>() => ExactComparer<T>.Instance;

    public static IParameterValueComparer<double> Absolute(double tolerance)
    {
        if (tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        return new DoubleAbsoluteComparer(tolerance);
    }

    public static IParameterValueComparer<double> Relative(double tolerance)
    {
        if (tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        return new DoubleRelativeComparer(tolerance);
    }

    public static IParameterValueComparer<double> AbsoluteOrRelative(
        double absoluteTolerance,
        double relativeTolerance)
    {
        if (absoluteTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteTolerance));
        if (relativeTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(relativeTolerance));

        return new DoubleAbsoluteOrRelativeComparer(
            absoluteTolerance,
            relativeTolerance);
    }

    private sealed class ExactComparer<T> : IParameterValueComparer<T>
    {
        public static ExactComparer<T> Instance { get; } = new();

        public bool AreEquivalent(T expected, T actual) =>
            EqualityComparer<T>.Default.Equals(expected, actual);
    }

    private sealed record DoubleAbsoluteComparer(double Tolerance)
        : IParameterValueComparer<double>
    {
        public bool AreEquivalent(double expected, double actual)
        {
            if (double.IsNaN(expected) || double.IsNaN(actual))
                return double.IsNaN(expected) && double.IsNaN(actual);
            if (double.IsInfinity(expected) || double.IsInfinity(actual))
                return expected.Equals(actual);

            return Math.Abs(expected - actual) <= Tolerance;
        }
    }

    private sealed record DoubleRelativeComparer(double Tolerance)
        : IParameterValueComparer<double>
    {
        public bool AreEquivalent(double expected, double actual)
        {
            if (double.IsNaN(expected) || double.IsNaN(actual))
                return double.IsNaN(expected) && double.IsNaN(actual);
            if (double.IsInfinity(expected) || double.IsInfinity(actual))
                return expected.Equals(actual);

            var scale = Math.Max(Math.Abs(expected), Math.Abs(actual));
            return scale == 0
                ? expected.Equals(actual)
                : Math.Abs(expected - actual) <= Tolerance * scale;
        }
    }

    private sealed record DoubleAbsoluteOrRelativeComparer(
        double AbsoluteTolerance,
        double RelativeTolerance) : IParameterValueComparer<double>
    {
        public bool AreEquivalent(double expected, double actual) =>
            new DoubleAbsoluteComparer(AbsoluteTolerance).AreEquivalent(expected, actual) ||
            new DoubleRelativeComparer(RelativeTolerance).AreEquivalent(expected, actual);
    }
}

public sealed record ParameterRange<T>(
    T Minimum,
    T Maximum,
    IComparer<T>? Comparer = null)
{
    public bool Contains(T value)
    {
        var comparer = Comparer ?? System.Collections.Generic.Comparer<T>.Default;
        return comparer.Compare(value, Minimum) >= 0 &&
               comparer.Compare(value, Maximum) <= 0;
    }
}

public sealed record ParameterContract<T>(
    string Key,
    IParameterValueCodec<T>? Codec = null,
    IParameterValueComparer<T>? ReadbackComparer = null,
    ParameterRange<T>? Range = null,
    IReadOnlySet<T>? AllowedValues = null,
    TimeSpan? StaleAfter = null);

public enum TypedParameterWriteStatus
{
    Verified,
    WrittenUnverified,
    ReadbackMismatch,
    Rejected,
    UnknownOutcome
}

public sealed record TypedParameterReadResult<T>(
    string Key,
    T Value,
    DeviceParameterDescriptor Descriptor,
    DeviceSnapshot<T> Snapshot);

public sealed record TypedParameterWriteResult<T>(
    string Key,
    T RequestedValue,
    TypedParameterWriteStatus Status,
    T? ObservedValue,
    DeviceParameterDescriptor? Descriptor,
    DeviceSnapshot<T>? Snapshot,
    string? Code = null,
    string? Message = null,
    Exception? Exception = null)
{
    public bool Verified => Status == TypedParameterWriteStatus.Verified;
}

public sealed record TypedParameterWriteOptions(
    bool VerifyReadback = true,
    TimeSpan ReadbackTimeout = default,
    IReadOnlyList<CommandResourceClaim>? ResourceClaims = null)
{
    public TimeSpan EffectiveReadbackTimeout =>
        ReadbackTimeout == default ? TimeSpan.FromSeconds(5) : ReadbackTimeout;
}

public sealed class TypedParameterRuntime<T>
{
    private readonly string _deviceId;
    private readonly IParameterProvider _provider;
    private readonly ICommandResourceArbiter _resourceArbiter;
    private readonly DeviceSnapshotStore<T> _snapshots;
    private readonly IDeviceConnectionEpochSource _epochSource;
    private readonly TimeProvider _timeProvider;

    public TypedParameterRuntime(
        string deviceId,
        IParameterProvider provider,
        ICommandResourceArbiter resourceArbiter,
        DeviceSnapshotStore<T> snapshots,
        IDeviceConnectionEpochSource epochSource,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        _deviceId = deviceId;
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _resourceArbiter = resourceArbiter ?? throw new ArgumentNullException(nameof(resourceArbiter));
        _snapshots = snapshots ?? throw new ArgumentNullException(nameof(snapshots));
        _epochSource = epochSource ?? throw new ArgumentNullException(nameof(epochSource));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<TypedParameterReadResult<T>> ReadAsync(
        ParameterContract<T> contract,
        IReadOnlyList<CommandResourceClaim>? resourceClaims = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContract(contract);

        var claims = resourceClaims ?? DefaultClaims(CommandResourceAccess.Exclusive);
        await using var lease = await _resourceArbiter
            .AcquireAsync(claims, cancellationToken)
            .ConfigureAwait(false);

        var observedTimestamp = _timeProvider.GetTimestamp();
        var epoch = _epochSource.GetCurrentEpoch(_deviceId);
        var descriptor = await ReadDescriptorAsync(contract.Key, cancellationToken)
            .ConfigureAwait(false);
        var codec = contract.Codec ?? ParameterCodecs.Strict<T>();
        var value = codec.Decode(descriptor.Value);

        var currentEpoch = _epochSource.GetCurrentEpoch(_deviceId);
        if (currentEpoch != epoch)
            throw new DeviceEpochChangedException(_deviceId, epoch, currentEpoch);

        var snapshot = _snapshots.Apply(new DeviceObservation<T>(
            _deviceId,
            Partition(contract.Key),
            epoch,
            DeviceObservationSource.ParameterReadback,
            observedTimestamp,
            value,
            DeviceSnapshotQuality.Good,
            ObservedAtUtc: _timeProvider.GetUtcNow(),
            StaleAfter: contract.StaleAfter)).Snapshot!;

        DeviceControlTelemetry.ParameterOperations.Add(
            1,
            DeviceControlTelemetry.Tags("parameter.read", "success"));
        return new TypedParameterReadResult<T>(
            contract.Key,
            value,
            descriptor,
            snapshot);
    }

    public async Task<TypedParameterWriteResult<T>> WriteAsync(
        ParameterContract<T> contract,
        T value,
        TypedParameterWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ValidateContract(contract);
        options ??= new TypedParameterWriteOptions();

        if (options.EffectiveReadbackTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Readback timeout must be greater than zero.");

        if (!ValidateValue(contract, value, out var validationMessage))
        {
            return Complete(new TypedParameterWriteResult<T>(
                contract.Key,
                value,
                TypedParameterWriteStatus.Rejected,
                default,
                null,
                null,
                "parameter_validation_failed",
                validationMessage));
        }

        var claims = options.ResourceClaims ?? DefaultClaims(CommandResourceAccess.Exclusive);
        await using var lease = await _resourceArbiter
            .AcquireAsync(claims, cancellationToken)
            .ConfigureAwait(false);

        var epoch = _epochSource.GetCurrentEpoch(_deviceId);
        var descriptor = await ReadDescriptorAsync(contract.Key, cancellationToken)
            .ConfigureAwait(false);

        if (descriptor.IsReadOnly)
        {
            return Complete(new TypedParameterWriteResult<T>(
                contract.Key,
                value,
                TypedParameterWriteStatus.Rejected,
                default,
                descriptor,
                null,
                "parameter_read_only",
                $"Parameter '{contract.Key}' is read-only."));
        }

        var codec = contract.Codec ?? ParameterCodecs.Strict<T>();
        var comparer = contract.ReadbackComparer ?? ParameterComparers.Exact<T>();
        var encoded = codec.Encode(value);
        var writeAttempted = false;

        try
        {
            writeAttempted = true;
            await _provider.SetParameterAsync(
                    descriptor.Key,
                    encoded,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!options.VerifyReadback)
            {
                return Complete(new TypedParameterWriteResult<T>(
                    contract.Key,
                    value,
                    TypedParameterWriteStatus.WrittenUnverified,
                    default,
                    descriptor,
                    null,
                    "parameter_unverified",
                    "Parameter write completed without readback verification."));
            }

            using var readbackCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var timer = _timeProvider.CreateTimer(
                static state => ((CancellationTokenSource)state!).Cancel(),
                readbackCts,
                options.EffectiveReadbackTimeout,
                Timeout.InfiniteTimeSpan);

            var observedTimestamp = _timeProvider.GetTimestamp();
            var after = await ReadDescriptorAsync(descriptor.Key, readbackCts.Token)
                .ConfigureAwait(false);

            if (_epochSource.GetCurrentEpoch(_deviceId) != epoch)
            {
                return Unknown(
                    contract,
                    value,
                    after,
                    "connection_epoch_changed",
                    "Connection epoch changed before readback could be committed.");
            }

            var observed = codec.Decode(after.Value);
            var applied = _snapshots.Apply(new DeviceObservation<T>(
                _deviceId,
                Partition(contract.Key),
                epoch,
                DeviceObservationSource.ParameterReadback,
                observedTimestamp,
                observed,
                DeviceSnapshotQuality.Good,
                ObservedAtUtc: _timeProvider.GetUtcNow(),
                StaleAfter: contract.StaleAfter));

            var status = comparer.AreEquivalent(value, observed)
                ? TypedParameterWriteStatus.Verified
                : TypedParameterWriteStatus.ReadbackMismatch;

            return Complete(new TypedParameterWriteResult<T>(
                contract.Key,
                value,
                status,
                observed,
                after,
                applied.Snapshot,
                status == TypedParameterWriteStatus.Verified
                    ? null
                    : "parameter_readback_mismatch",
                status == TypedParameterWriteStatus.Verified
                    ? null
                    : $"Parameter '{contract.Key}' readback does not match the requested value."));
        }
        catch (OperationCanceledException ex) when (
            writeAttempted &&
            !cancellationToken.IsCancellationRequested)
        {
            return Unknown(
                contract,
                value,
                descriptor,
                "parameter_readback_timeout",
                "Parameter write may have occurred, but readback did not complete before the deadline.",
                ex);
        }
        catch (OperationCanceledException ex) when (writeAttempted)
        {
            return Unknown(
                contract,
                value,
                descriptor,
                "parameter_outcome_unknown",
                "Parameter write may have occurred before cancellation; reconcile before retrying.",
                ex);
        }
        catch (Exception ex) when (writeAttempted)
        {
            return Unknown(
                contract,
                value,
                descriptor,
                "parameter_outcome_unknown",
                "Parameter write crossed an unobservable side-effect boundary; reconcile before retrying.",
                ex);
        }
    }

    private async Task<DeviceParameterDescriptor> ReadDescriptorAsync(
        string key,
        CancellationToken cancellationToken)
    {
        if (_provider is IDirectParameterProvider direct)
            return await direct.GetParameterAsync(key, cancellationToken).ConfigureAwait(false);

        var descriptors = await _provider.GetParametersAsync(cancellationToken).ConfigureAwait(false);
        return descriptors.FirstOrDefault(descriptor =>
                   descriptor.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"Parameter '{key}' was not found.");
    }

    private IReadOnlyList<CommandResourceClaim> DefaultClaims(CommandResourceAccess access) =>
    [
        new CommandResourceClaim(
            new CommandResourceKey("device", _deviceId),
            access)
    ];

    private static DeviceStatePartitionKey Partition(string key) =>
        new($"parameter:{key}");

    private static void ValidateContract(ParameterContract<T> contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentException.ThrowIfNullOrWhiteSpace(contract.Key);
        if (contract.StaleAfter is { } staleAfter && staleAfter < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(contract), "StaleAfter cannot be negative.");
    }

    private static bool ValidateValue(
        ParameterContract<T> contract,
        T value,
        out string? message)
    {
        if (contract.Range is { } range && !range.Contains(value))
        {
            message = $"Parameter '{contract.Key}' is outside the configured range.";
            return false;
        }

        if (contract.AllowedValues is { } allowed && !allowed.Contains(value))
        {
            message = $"Parameter '{contract.Key}' is outside the configured domain.";
            return false;
        }

        message = null;
        return true;
    }

    private static TypedParameterWriteResult<T> Complete(TypedParameterWriteResult<T> result)
    {
        DeviceControlTelemetry.ParameterOperations.Add(
            1,
            DeviceControlTelemetry.Tags("parameter.write", result.Status.ToString()));
        return result;
    }

    private static TypedParameterWriteResult<T> Unknown(
        ParameterContract<T> contract,
        T value,
        DeviceParameterDescriptor? descriptor,
        string code,
        string message,
        Exception? exception = null) =>
        Complete(new(
            contract.Key,
            value,
            TypedParameterWriteStatus.UnknownOutcome,
            default,
            descriptor,
            null,
            code,
            message,
            exception));
}
