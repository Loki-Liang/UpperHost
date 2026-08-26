using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Presentation.Wpf.Controls;

public partial class DeviceList : UserControl
{
    public static readonly DependencyProperty DevicesProperty = DependencyProperty.Register(
        nameof(Devices),
        typeof(IEnumerable<IDevice>),
        typeof(DeviceList),
        new PropertyMetadata(null));

    public DeviceList() => InitializeComponent();

    public IEnumerable<IDevice>? Devices
    {
        get => (IEnumerable<IDevice>?)GetValue(DevicesProperty);
        set => SetValue(DevicesProperty, value);
    }
}
