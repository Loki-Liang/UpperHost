using Microsoft.Extensions.Options;
using OpenDeviceStudio.Hosting;
using OpenDeviceStudio.Starters;

namespace OpenDeviceStudio.Tests;

public sealed class StartupOptionsValidationTests
{
    [Fact]
    public async Task Transport_options_are_revalidated_on_host_start()
    {
        var builder = OpenDeviceStudioApplication.CreateBuilder();
        builder.AddOpenDeviceStudioApplication();

        builder.Configuration["OpenDeviceStudio:Transport:Resilience:MaximumDelayMs"] = "-1";

        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync());

        Assert.Contains(
            "OpenDeviceStudio:Transport:Resilience:MaximumDelayMs",
            error.Message);
    }
}
