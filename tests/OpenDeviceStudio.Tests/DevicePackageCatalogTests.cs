using Microsoft.Extensions.DependencyInjection;
using OpenDeviceStudio.Abstractions.Devices;
using OpenDeviceStudio.Hosting;

namespace OpenDeviceStudio.Tests;

public sealed class DevicePackageCatalogTests
{
    [Fact]
    public void Invalid_typed_configuration_fails_during_composition_with_exact_path()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        var descriptor = CreateDescriptor(
            "temperature-controller",
            new Version(1, 0, 0),
            CreateSchema());

        var error = Assert.Throws<DeviceConfigurationValidationException>(
            () => builder.AddDevicePackage(
                descriptor,
                new TestConfiguration
                {
                    Transport = "Simulator",
                    Minimum = 5,
                    Maximum = 95,
                    Target = 120
                }));

        Assert.Contains(
            error.Errors,
            item => item.Path == "Test:Target");
        Assert.Contains("Test:Target", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Schema_validates_required_range_enum_secret_reference_and_cross_field_rules()
    {
        var schema = CreateSchema();

        var errors = schema.Validate(
            new TestConfiguration
            {
                Transport = "Tcp",
                Minimum = 50,
                Maximum = 10,
                Target = 150,
                Credential = new DeviceSecretReference("Environment", "DEVICE_TOKEN")
            });

        Assert.Contains(errors, item => item.Path == "Test:Transport");
        Assert.Contains(errors, item => item.Path == "Test:Target");
        Assert.Contains(errors, item => item.Path == "Test:Maximum");

        var valid = schema.Validate(
            new TestConfiguration
            {
                Transport = "Simulator",
                Minimum = 5,
                Maximum = 95,
                Target = 30,
                Credential = new DeviceSecretReference("Environment", "DEVICE_TOKEN")
            });

        Assert.Empty(valid);
    }

    [Fact]
    public async Task Catalog_keeps_highest_version_and_filters_by_stable_metadata()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        var schema = CreateSchema();

        builder.AddDevicePackageDescriptor(
            CreateDescriptor("temperature-controller", new Version(1, 0, 0), schema));
        builder.AddDevicePackageDescriptor(
            CreateDescriptor("temperature-controller", new Version(2, 0, 0), schema));
        builder.AddDevicePackageDescriptor(
            CreateDescriptor(
                "daq",
                new Version(1, 0, 0),
                schema,
                vendor: "OtherVendor",
                capabilities: ["streaming"],
                transports: ["Tcp"]));

        await using var app = builder.Build();
        await app.StartAsync();

        var catalog = app.Services.GetRequiredService<IDevicePackageCatalog>();

        Assert.True(catalog.TryGet("temperature-controller", out var temperature));
        Assert.NotNull(temperature);
        Assert.Equal(new Version(2, 0, 0), temperature!.PackageVersion);

        var matches = catalog.Query(
            new DevicePackageQuery(
                Capability: "command",
                Transport: "Simulator",
                Vendor: "OpenDeviceStudio"));

        var match = Assert.Single(matches);
        Assert.Equal("temperature-controller", match.PackageId);
        Assert.Equal(2, catalog.Descriptors.Count);
    }

    [Fact]
    public async Task Same_package_and_version_with_different_descriptors_fails_deterministically()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        var schema = CreateSchema();

        builder.AddDevicePackageDescriptor(
            CreateDescriptor("temperature-controller", new Version(1, 0, 0), schema));
        builder.AddDevicePackageDescriptor(
            CreateDescriptor("temperature-controller", new Version(1, 0, 0), schema));

        await using var app = builder.Build();

        var error = await Assert.ThrowsAsync<DevicePackageConflictException>(
            () => app.StartAsync());

        Assert.Equal("temperature-controller", error.PackageId);
        Assert.Equal(new Version(1, 0, 0), error.PackageVersion);
    }

    private static DeviceConfigurationSchema<TestConfiguration> CreateSchema() =>
        new(
            "1.0",
            [
                new(
                    new DeviceConfigurationFieldDescriptor(
                        "Test:Transport",
                        "Transport",
                        DeviceConfigurationValueKind.Selection,
                        Required: true,
                        AllowedValues: ["Simulator"]),
                    configuration => configuration.Transport),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        "Test:Minimum",
                        "Minimum",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 100),
                    configuration => configuration.Minimum),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        "Test:Maximum",
                        "Maximum",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 100),
                    configuration => configuration.Maximum),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        "Test:Target",
                        "Target",
                        DeviceConfigurationValueKind.Decimal,
                        Required: true,
                        Minimum: -100,
                        Maximum: 100),
                    configuration => configuration.Target),
                new(
                    new DeviceConfigurationFieldDescriptor(
                        "Test:Credential",
                        "Credential reference",
                        DeviceConfigurationValueKind.SecretReference),
                    configuration => configuration.Credential)
            ],
            configuration =>
            {
                var errors = new List<DeviceConfigurationValidationError>();
                if (configuration.Maximum <= configuration.Minimum)
                {
                    errors.Add(
                        new DeviceConfigurationValidationError(
                            "Test:Maximum",
                            "Maximum must be greater than minimum."));
                }

                if (configuration.Target < configuration.Minimum ||
                    configuration.Target > configuration.Maximum)
                {
                    errors.Add(
                        new DeviceConfigurationValidationError(
                            "Test:Target",
                            "Target must be inside the configured range."));
                }

                return errors;
            });

    private static DevicePackageDescriptor CreateDescriptor(
        string packageId,
        Version version,
        IDeviceConfigurationSchema schema,
        string vendor = "OpenDeviceStudio",
        IReadOnlyList<string>? capabilities = null,
        IReadOnlyList<string>? transports = null) =>
        new(
            packageId,
            version,
            $"{packageId}-device",
            packageId,
            vendor,
            "SIM",
            capabilities ?? ["connect", "command", "parameters"],
            transports ?? ["Simulator"],
            [],
            [],
            [
                new DeviceSignalDescriptor(
                    "value",
                    "Value",
                    typeof(double).FullName!,
                    Mode: DeviceSignalMode.Stream,
                    NominalSampleRateHz: 10)
            ],
            schema,
            new DeviceSimulatorDescriptor(
                $"{packageId}-simulator",
                $"{packageId} simulator",
                "Tests.Simulator"));

    private sealed class TestConfiguration
    {
        public string Transport { get; set; } = "Simulator";
        public double Minimum { get; set; } = 5;
        public double Maximum { get; set; } = 95;
        public double Target { get; set; } = 30;
        public DeviceSecretReference? Credential { get; set; }
    }
}
