using Microsoft.Extensions.DependencyInjection;
using UpperHost.Abstractions.Devices;
using UpperHost.Hosting;

namespace UpperHost.Tests;

public sealed class DeviceManagerTests
{
    [Fact]
    public async Task Registered_device_is_exposed_and_managed_after_host_start()
    {
        var builder = UpperHostApplication.CreateBuilder().AddUpperHost();
        var device = new FakeDevice();
        builder.Services.AddSingleton<IDevice>(device);

        await using var app = builder.Build();
        await app.StartAsync();

        var registry = app.Services.GetRequiredService<IDeviceRegistry>();
        var manager = app.Services.GetRequiredService<IDeviceManager>();

        Assert.True(registry.TryGet(device.Descriptor.Id, out var registered));
        Assert.Same(device, registered);

        await manager.ConnectAsync(device.Descriptor.Id);
        Assert.Equal(DeviceState.Online, device.State);

        await manager.DisconnectAsync(device.Descriptor.Id);
        Assert.Equal(DeviceState.Offline, device.State);
    }

    private sealed class FakeDevice : IDevice, IConnectable
    {
        public DeviceDescriptor Descriptor { get; } = new("fake-1", "Fake Device");
        public DeviceState State { get; private set; } = DeviceState.Offline;
        public IReadOnlyCollection<string> Capabilities { get; } = [nameof(IConnectable)];

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = DeviceState.Online;
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = DeviceState.Offline;
            return Task.CompletedTask;
        }
    }
}
