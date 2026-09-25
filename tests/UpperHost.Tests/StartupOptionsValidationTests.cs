using Microsoft.Extensions.Options;
using UpperHost.Hosting;
using UpperHost.Starters;

namespace UpperHost.Tests;

public sealed class StartupOptionsValidationTests
{
    [Fact]
    public async Task Transport_options_are_revalidated_on_host_start()
    {
        var builder = UpperHostApplication.CreateBuilder();
        builder.AddUpperHostApplication();

        builder.Configuration["UpperHost:Transport:Resilience:MaximumDelayMs"] = "-1";

        await using var app = builder.Build();
        var error = await Assert.ThrowsAsync<OptionsValidationException>(
            () => app.StartAsync());

        Assert.Contains(
            "UpperHost:Transport:Resilience:MaximumDelayMs",
            error.Message);
    }
}
