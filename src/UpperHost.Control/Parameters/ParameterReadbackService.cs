using System.Globalization;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Control.Parameters;

public sealed record ParameterReadResult(string Key, object? Value, DeviceParameterDescriptor Descriptor);

public sealed record ParameterWriteOptions(
    bool VerifyReadback = true,
    bool ThrowOnMismatch = true,
    double NumericTolerance = 0.000001);

public sealed record ParameterWriteResult(
    string Key,
    object? RequestedValue,
    object? ActualValue,
    bool Verified,
    DeviceParameterDescriptor Descriptor);

public sealed class ParameterReadbackException : InvalidOperationException
{
    public ParameterReadbackException(string message) : base(message)
    {
    }
}

public sealed class ParameterReadbackService
{
    private readonly IParameterProvider _provider;

    public ParameterReadbackService(IParameterProvider provider) =>
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));

    public async Task<ParameterReadResult> ReadAsync(
        string key,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var descriptor = await FindAsync(key, cancellationToken).ConfigureAwait(false);
        return new ParameterReadResult(descriptor.Key, descriptor.Value, descriptor);
    }

    public async Task<ParameterWriteResult> WriteAsync(
        string key,
        object? value,
        ParameterWriteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        options ??= new ParameterWriteOptions();
        if (options.NumericTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Numeric tolerance cannot be negative.");

        var before = await FindAsync(key, cancellationToken).ConfigureAwait(false);
        if (before.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{key}' is read-only.");

        await _provider.SetParameterAsync(before.Key, value, cancellationToken).ConfigureAwait(false);

        if (!options.VerifyReadback)
            return new ParameterWriteResult(before.Key, value, null, false, before);

        var after = await FindAsync(before.Key, cancellationToken).ConfigureAwait(false);
        var verified = AreEquivalent(value, after.Value, options.NumericTolerance);
        if (!verified && options.ThrowOnMismatch)
        {
            throw new ParameterReadbackException(
                $"Parameter '{before.Key}' readback mismatch. Requested '{value ?? "<null>"}', actual '{after.Value ?? "<null>"}'.");
        }

        return new ParameterWriteResult(after.Key, value, after.Value, verified, after);
    }

    private async Task<DeviceParameterDescriptor> FindAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var parameters = await _provider.GetParametersAsync(cancellationToken).ConfigureAwait(false);
        return parameters.FirstOrDefault(parameter =>
                   parameter.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
               ?? throw new KeyNotFoundException($"Parameter '{key}' was not found.");
    }

    private static bool AreEquivalent(object? expected, object? actual, double numericTolerance)
    {
        if (ReferenceEquals(expected, actual))
            return true;
        if (expected is null || actual is null)
            return false;

        if (TryGetNumber(expected, out var expectedNumber) && TryGetNumber(actual, out var actualNumber))
            return Math.Abs(expectedNumber - actualNumber) <= numericTolerance;

        return Equals(expected, actual);
    }

    private static bool TryGetNumber(object value, out double number)
    {
        switch (value)
        {
            case byte v: number = v; return true;
            case sbyte v: number = v; return true;
            case short v: number = v; return true;
            case ushort v: number = v; return true;
            case int v: number = v; return true;
            case uint v: number = v; return true;
            case long v: number = v; return true;
            case ulong v: number = v; return true;
            case float v: number = v; return true;
            case double v: number = v; return true;
            case decimal v: number = (double)v; return true;
            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                number = parsed;
                return true;
            default:
                number = default;
                return false;
        }
    }
}
