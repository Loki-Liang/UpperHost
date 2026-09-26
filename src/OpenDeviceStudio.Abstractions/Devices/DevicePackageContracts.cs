using System.Globalization;

namespace OpenDeviceStudio.Abstractions.Devices;

public enum DeviceConfigurationValueKind
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Selection,
    SecretReference
}

public sealed record DeviceSecretReference(string Source, string Key)
{
    public override string ToString() => $"{Source}:{Key}";
}

public sealed record DeviceConfigurationFieldDescriptor(
    string Path,
    string DisplayName,
    DeviceConfigurationValueKind Kind,
    bool Required = false,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? AllowedValues = null,
    string? Description = null);

public sealed record DeviceConfigurationValidationError(string Path, string Message);

public sealed class DeviceConfigurationValidationException : InvalidOperationException
{
    public DeviceConfigurationValidationException(
        IReadOnlyList<DeviceConfigurationValidationError> errors)
        : base(string.Join(
            Environment.NewLine,
            errors.Select(error => $"{error.Path}: {error.Message}")))
    {
        Errors = errors;
    }

    public IReadOnlyList<DeviceConfigurationValidationError> Errors { get; }
}

public interface IDeviceConfigurationSchema
{
    string Version { get; }
    Type ConfigurationType { get; }
    IReadOnlyList<DeviceConfigurationFieldDescriptor> Fields { get; }
    IReadOnlyList<DeviceConfigurationValidationError> Validate(object configuration);
    void ValidateAndThrow(object configuration);
}

public sealed class DeviceConfigurationField<TConfiguration>
{
    public DeviceConfigurationField(
        DeviceConfigurationFieldDescriptor descriptor,
        Func<TConfiguration, object?> valueAccessor)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        ValueAccessor = valueAccessor ?? throw new ArgumentNullException(nameof(valueAccessor));
    }

    public DeviceConfigurationFieldDescriptor Descriptor { get; }
    internal Func<TConfiguration, object?> ValueAccessor { get; }
}

public sealed class DeviceConfigurationSchema<TConfiguration> : IDeviceConfigurationSchema
{
    private readonly IReadOnlyList<DeviceConfigurationField<TConfiguration>> _rules;
    private readonly Func<TConfiguration, IEnumerable<DeviceConfigurationValidationError>>? _crossFieldValidator;

    public DeviceConfigurationSchema(
        string version,
        IEnumerable<DeviceConfigurationField<TConfiguration>> fields,
        Func<TConfiguration, IEnumerable<DeviceConfigurationValidationError>>? crossFieldValidator = null)
    {
        if (string.IsNullOrWhiteSpace(version))
            throw new ArgumentException("Configuration schema version is required.", nameof(version));

        Version = version;
        _rules = fields?.ToArray() ?? throw new ArgumentNullException(nameof(fields));
        _crossFieldValidator = crossFieldValidator;
        Fields = _rules.Select(rule => rule.Descriptor).ToArray();
    }

    public string Version { get; }
    public Type ConfigurationType => typeof(TConfiguration);
    public IReadOnlyList<DeviceConfigurationFieldDescriptor> Fields { get; }

    public IReadOnlyList<DeviceConfigurationValidationError> Validate(object configuration)
    {
        if (configuration is not TConfiguration typed)
        {
            return
            [
                new DeviceConfigurationValidationError(
                    "$",
                    $"Configuration must be assignable to {typeof(TConfiguration).FullName}.")
            ];
        }

        var errors = new List<DeviceConfigurationValidationError>();
        foreach (var rule in _rules)
            ValidateRule(typed, rule, errors);

        if (_crossFieldValidator is not null)
            errors.AddRange(_crossFieldValidator(typed));

        return errors;
    }

    public void ValidateAndThrow(object configuration)
    {
        var errors = Validate(configuration);
        if (errors.Count > 0)
            throw new DeviceConfigurationValidationException(errors);
    }

    private static void ValidateRule(
        TConfiguration configuration,
        DeviceConfigurationField<TConfiguration> rule,
        ICollection<DeviceConfigurationValidationError> errors)
    {
        var descriptor = rule.Descriptor;
        var value = rule.ValueAccessor(configuration);

        if (descriptor.Required &&
            (value is null || value is string text && string.IsNullOrWhiteSpace(text)))
        {
            errors.Add(new DeviceConfigurationValidationError(descriptor.Path, "Value is required."));
            return;
        }

        if (value is null)
            return;

        if (descriptor.Kind == DeviceConfigurationValueKind.SecretReference &&
            value is not DeviceSecretReference)
        {
            errors.Add(
                new DeviceConfigurationValidationError(
                    descriptor.Path,
                    "Secret values must be represented by DeviceSecretReference, never plaintext metadata."));
            return;
        }

        if (descriptor.AllowedValues is { Count: > 0 })
        {
            var candidate = Convert.ToString(value, CultureInfo.InvariantCulture);
            if (candidate is null ||
                !descriptor.AllowedValues.Contains(candidate, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add(
                    new DeviceConfigurationValidationError(
                        descriptor.Path,
                        $"Value must be one of: {string.Join(", ", descriptor.AllowedValues)}."));
            }
        }

        if (descriptor.Minimum is null && descriptor.Maximum is null)
            return;

        if (!TryToDouble(value, out var numeric))
        {
            errors.Add(
                new DeviceConfigurationValidationError(
                    descriptor.Path,
                    "Value must be numeric for range validation."));
            return;
        }

        if (descriptor.Minimum is double minimum && numeric < minimum)
        {
            errors.Add(
                new DeviceConfigurationValidationError(
                    descriptor.Path,
                    $"Value must be greater than or equal to {minimum.ToString(CultureInfo.InvariantCulture)}."));
        }

        if (descriptor.Maximum is double maximum && numeric > maximum)
        {
            errors.Add(
                new DeviceConfigurationValidationError(
                    descriptor.Path,
                    $"Value must be less than or equal to {maximum.ToString(CultureInfo.InvariantCulture)}."));
        }
    }

    private static bool TryToDouble(object value, out double numeric)
    {
        try
        {
            numeric = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return !double.IsNaN(numeric) && !double.IsInfinity(numeric);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            numeric = default;
            return false;
        }
    }
}

public enum DeviceSignalMode
{
    Snapshot,
    Stream
}

public sealed record DeviceSignalDescriptor(
    string Key,
    string DisplayName,
    string ValueType,
    string? Unit = null,
    DeviceSignalMode Mode = DeviceSignalMode.Snapshot,
    double? NominalSampleRateHz = null,
    string? Description = null);

public sealed record DeviceSimulatorDescriptor(
    string Id,
    string DisplayName,
    string ImplementationType,
    string? Description = null);

public sealed record DeviceDiagnosticDescriptor(
    string Id,
    string DisplayName,
    string? Description = null);

public sealed record DeviceDiagnosticsMetadata(
    IReadOnlyList<DeviceDiagnosticDescriptor> Diagnostics);

public enum DeviceDependencyKind
{
    Provider,
    Protocol,
    Transport,
    NativeDriver,
    Other
}

public sealed record DeviceDependencyDescriptor(
    string Id,
    DeviceDependencyKind Kind,
    string? VersionRange = null,
    bool Optional = false);

public sealed record DevicePackageDescriptor(
    string PackageId,
    Version PackageVersion,
    string DeviceId,
    string DisplayName,
    string? Vendor,
    string? Model,
    IReadOnlyList<string> CapabilityTypes,
    IReadOnlyList<string> TransportTypes,
    IReadOnlyList<DeviceParameterDescriptor> Parameters,
    IReadOnlyList<DeviceCommandDescriptor> Commands,
    IReadOnlyList<DeviceSignalDescriptor> Signals,
    IDeviceConfigurationSchema ConfigurationSchema,
    DeviceSimulatorDescriptor? DefaultSimulator = null,
    DeviceDiagnosticsMetadata? Diagnostics = null,
    IReadOnlyList<DeviceDependencyDescriptor>? Dependencies = null);

public sealed record DevicePackageQuery(
    string? Capability = null,
    string? Transport = null,
    string? Vendor = null);

public enum DevicePackageRegistrationOutcome
{
    Added,
    ReplacedOlderVersion,
    IgnoredOlderVersion,
    Unchanged
}

public sealed record DevicePackageRegistrationResult(
    DevicePackageRegistrationOutcome Outcome,
    DevicePackageDescriptor ActiveDescriptor);

public sealed class DevicePackageConflictException : InvalidOperationException
{
    public DevicePackageConflictException(string packageId, Version packageVersion)
        : base($"Device package '{packageId}' version '{packageVersion}' is already registered with a different descriptor.")
    {
        PackageId = packageId;
        PackageVersion = packageVersion;
    }

    public string PackageId { get; }
    public Version PackageVersion { get; }
}

public interface IDevicePackageCatalog
{
    IReadOnlyCollection<DevicePackageDescriptor> Descriptors { get; }
    DevicePackageRegistrationResult Register(DevicePackageDescriptor descriptor);
    bool TryGet(string packageId, out DevicePackageDescriptor? descriptor);
    IReadOnlyCollection<DevicePackageDescriptor> Query(DevicePackageQuery query);
}
