using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Observability;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Tests;

public sealed class ConfigurationOptionsTests
{
    [Fact]
    public async Task Configured_transport_binds_typed_options()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Tcp";
        builder.Configuration["OpenDeviceStudio:Transport:Tcp:Host"] = "device.local";
        builder.Configuration["OpenDeviceStudio:Transport:Tcp:Port"] = "9100";
        builder.Configuration["OpenDeviceStudio:Transport:Tcp:ReadBufferSize"] = "32768";

        builder.AddOpenDeviceStudioApplication();
        await using var app = builder.Build();

        var options = app.Services
            .GetRequiredService<IOptions<OpenDeviceStudioTransportOptions>>()
            .Value;

        Assert.Equal("Tcp", options.Type);
        Assert.Equal("device.local", options.Tcp.Host);
        Assert.Equal(9100, options.Tcp.Port);
        Assert.Equal(32768, options.Tcp.ReadBufferSize);
    }

    [Fact]
    public void Missing_serial_port_fails_with_canonical_path_before_runtime_io()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Type"] = "Serial";

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.AddOpenDeviceStudioApplication());

        Assert.Contains("OpenDeviceStudio:Transport:Serial:PortName", error.Message);
    }

    [Fact]
    public void Cross_field_resilience_validation_fails_with_canonical_paths()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.Configuration["OpenDeviceStudio:Transport:Resilience:InitialDelayMs"] = "5000";
        builder.Configuration["OpenDeviceStudio:Transport:Resilience:MaximumDelayMs"] = "1000";

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.AddOpenDeviceStudioApplication());

        Assert.Contains(
            "OpenDeviceStudio:Transport:Resilience:MaximumDelayMs",
            error.Message);
        Assert.Contains(
            "OpenDeviceStudio:Transport:Resilience:InitialDelayMs",
            error.Message);
    }

    [Fact]
    public async Task Transport_defaults_are_deterministic()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.AddOpenDeviceStudioApplication();
        await using var app = builder.Build();

        var options = app.Services
            .GetRequiredService<IOptions<OpenDeviceStudioTransportOptions>>()
            .Value;

        Assert.Equal("Simulator", options.Type);
        Assert.Equal("default", options.Simulator.Name);
        Assert.Equal(115200, options.Serial.BaudRate);
        Assert.Equal(9000, options.Tcp.Port);
        Assert.True(options.Resilience.Enabled);
        Assert.Equal(5, options.Resilience.MaxAttempts);
        Assert.Equal(250, options.Resilience.InitialDelayMs);
        Assert.Equal(5000, options.Resilience.MaximumDelayMs);
        Assert.Equal(2.0, options.Resilience.BackoffFactor);
    }

    [Fact]
    public void Configured_observability_validation_reports_canonical_uri_path()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
        builder.Configuration["OpenDeviceStudio:Observability:Otlp:Enabled"] = "true";
        builder.Configuration["OpenDeviceStudio:Observability:Otlp:Endpoint"] = "relative-endpoint";

        var error = Assert.Throws<InvalidOperationException>(
            () => builder.AddConfiguredOpenDeviceStudioObservability());

        Assert.Contains("OpenDeviceStudio:Observability:Otlp:Endpoint", error.Message);
    }

    [Fact]
    public async Task Structured_logging_redacts_supported_secret_categories()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "opendevicestudio-configuration-secret-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var builder = OpenDeviceStudioApplication.CreateBuilder().AddOpenDeviceStudio();
            var options = new OpenDeviceStudioObservabilityOptions
            {
                ServiceName = "OpenDeviceStudio.Tests",
                ServiceVersion = "1.0.0"
            };
            options.FileLogging.Enabled = true;
            options.FileLogging.Path = Path.Combine(directory, "opendevicestudio-.json");
            options.FileLogging.AsyncBufferSize = 128;

            builder.AddOpenDeviceStudioObservability(options);

            var app = builder.Build();
            await app.StartAsync();

            var logger = app.Services.GetRequiredService<ILogger<ConfigurationOptionsTests>>();
            logger.LogInformation(
                "secret boundary {Password} {AccessToken} {ApiKey} {PrivateKey} {ConnectionString}",
                "password-value",
                "token-value",
                "api-key-value",
                "private-key-value",
                "connection-string-value");

            await app.DisposeAsync();

            var contents = string.Join(
                Environment.NewLine,
                Directory.GetFiles(directory, "opendevicestudio-*.json").Select(File.ReadAllText));

            Assert.Contains("[REDACTED]", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("password-value", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("token-value", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("api-key-value", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("private-key-value", contents, StringComparison.Ordinal);
            Assert.DoesNotContain("connection-string-value", contents, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
