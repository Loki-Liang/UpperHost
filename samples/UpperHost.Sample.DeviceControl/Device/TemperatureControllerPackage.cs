using UpperHost.Abstractions.Devices;
using UpperHost.Sample.DeviceControl.Protocol;
using UpperHost.Sample.DeviceControl.Simulator;

namespace UpperHost.Sample.DeviceControl.Device;

public sealed class TemperatureControllerPackageOptions
{
    public string Transport { get; set; } = "Simulator";
    public double MinimumCelsius { get; set; } = 5;
    public double MaximumCelsius { get; set; } = 95;
    public double InitialTargetCelsius { get; set; } = 30;
    public DeviceSecretReference? CalibrationCredential { get; set; }
}

public static class TemperatureControllerPackage
{
    public const string PackageId = "upperhost.sample.temperature-controller";
    public const string ConfigurationRoot = "DevicePackages:TemperatureController";

    public static DeviceConfigurationSchema<TemperatureControllerPackageOptions> ConfigurationSchema { get; } =
        new(
            "1.0",
            [
                new(
                    new DeviceConfigurationFieldDescriptor(
                        $"{ConfigurationRoot}:Transport",
                        "Transport",
                        DeviceConfigurationValueKind.Selection,
                        Required: true,
                        AllowedValues: ["Simulator"]),
                    options => options.Transport),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        $"{ConfigurationRoot}:MinimumCelsius",
                        "Minimum temperature",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 500),
                    options => options.MinimumCelsius),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        $"{ConfigurationRoot}:MaximumCelsius",
                        "Maximum temperature",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 500),
                    options => options.MaximumCelsius),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        $"{ConfigurationRoot}:InitialTargetCelsius",
                        "Initial target temperature",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 500),
                    options => options.InitialTargetCelsius),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        $"{ConfigurationRoot}:CalibrationCredential",
                        "Calibration credential reference",
                        DeviceConfigurationValueKind.SecretReference,
                        Description: "Reference only. Plaintext credentials must not enter package metadata."),
                    options => options.CalibrationCredential)
            ],
            ValidateCrossFields);

    public static DevicePackageDescriptor Descriptor { get; } =
        new(
            PackageId,
            new Version(1, 0, 0),
            TemperatureControllerDevice.DeviceId,
            "Simulated Temperature Controller",
            "UpperHost",
            "TC-SIM-1",
            ["connect", "command", "parameters", "readback"],
            ["Simulator"],
            [
                new DeviceParameterDescriptor(
                    "targetCelsius",
                    "Target temperature",
                    DeviceParameterKind.Decimal,
                    Unit: "°C",
                    Minimum: 5,
                    Maximum: 95),
                new DeviceParameterDescriptor(
                    "actualCelsius",
                    "Actual temperature",
                    DeviceParameterKind.Decimal,
                    Unit: "°C",
                    IsReadOnly: true),
                new DeviceParameterDescriptor(
                    "running",
                    "Running",
                    DeviceParameterKind.Boolean,
                    IsReadOnly: true)
            ],
            [
                new DeviceCommandDescriptor("readStatus", "Read status"),
                new DeviceCommandDescriptor("start", "Start temperature control"),
                new DeviceCommandDescriptor("stop", "Stop temperature control")
            ],
            [
                new DeviceSignalDescriptor(
                    "actualCelsius",
                    "Actual temperature",
                    typeof(double).FullName!,
                    "°C",
                    DeviceSignalMode.Snapshot,
                    Description: "Current measured temperature exposed by the status response.")
            ],
            ConfigurationSchema,
            new DeviceSimulatorDescriptor(
                "temperature-controller-simulator",
                "Temperature Controller Simulator",
                typeof(TemperatureControllerSimulator).FullName!,
                "Deterministic simulator used by the reference Device Control sample."),
            new DeviceDiagnosticsMetadata(
                [
                    new DeviceDiagnosticDescriptor(
                        "status-readback",
                        "Status readback",
                        "Verifies command acknowledgement and device truth through readback.")
                ]),
            [
                new DeviceDependencyDescriptor(
                    typeof(TemperatureControllerProtocol).FullName!,
                    DeviceDependencyKind.Protocol),
                new DeviceDependencyDescriptor(
                    "UpperHost.Transport.Simulator",
                    DeviceDependencyKind.Transport)
            ]);

    private static IEnumerable<DeviceConfigurationValidationError> ValidateCrossFields(
        TemperatureControllerPackageOptions options)
    {
        if (options.MaximumCelsius <= options.MinimumCelsius)
        {
            yield return new DeviceConfigurationValidationError(
                $"{ConfigurationRoot}:MaximumCelsius",
                $"Value must be greater than {ConfigurationRoot}:MinimumCelsius.");
        }

        if (options.InitialTargetCelsius < options.MinimumCelsius ||
            options.InitialTargetCelsius > options.MaximumCelsius)
        {
            yield return new DeviceConfigurationValidationError(
                $"{ConfigurationRoot}:InitialTargetCelsius",
                $"Value must be between {ConfigurationRoot}:MinimumCelsius and {ConfigurationRoot}:MaximumCelsius.");
        }
    }
}
