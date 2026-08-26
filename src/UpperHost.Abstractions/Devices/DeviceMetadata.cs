namespace UpperHost.Abstractions.Devices;

public enum DeviceParameterKind
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Selection
}

public sealed record DeviceParameterDescriptor(
    string Key,
    string DisplayName,
    DeviceParameterKind Kind,
    object? Value = null,
    string? Unit = null,
    double? Minimum = null,
    double? Maximum = null,
    bool IsReadOnly = false,
    IReadOnlyList<string>? Options = null);

public sealed record DeviceCommandDescriptor(
    string Name,
    string DisplayName,
    string? Description = null,
    bool IsDangerous = false);

public interface IParameterProvider
{
    Task<IReadOnlyList<DeviceParameterDescriptor>> GetParametersAsync(CancellationToken cancellationToken = default);
    Task SetParameterAsync(string key, object? value, CancellationToken cancellationToken = default);
}

public interface ICommandDescriptorProvider
{
    Task<IReadOnlyList<DeviceCommandDescriptor>> GetCommandsAsync(CancellationToken cancellationToken = default);
}
