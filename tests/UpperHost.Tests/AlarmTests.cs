using UpperHost.Abstractions.Diagnostics;
using UpperHost.Diagnostics;

namespace UpperHost.Tests;

public sealed class AlarmTests
{
    [Fact]
    public async Task Alarm_can_be_raised_acknowledged_and_cleared()
    {
        var service = new AlarmService();
        var alarm = await service.RaiseAsync("device-1", "TEMP", "Temperature high", AlarmSeverity.Warning);

        Assert.Single(service.Active);
        Assert.True(await service.AcknowledgeAsync(alarm.Id));
        Assert.Equal(AlarmStatus.Acknowledged, Assert.Single(service.Active).Status);
        Assert.True(await service.ClearAsync(alarm.Id));
        Assert.Empty(service.Active);
    }
}
