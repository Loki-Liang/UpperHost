using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Presentation.Wpf.Controls;

public partial class CommandPanel : UserControl
{
    public static readonly DependencyProperty CommandsProperty = DependencyProperty.Register(
        nameof(Commands),
        typeof(IEnumerable<DeviceCommandDescriptor>),
        typeof(CommandPanel),
        new PropertyMetadata(null));

    public CommandPanel() => InitializeComponent();

    public IEnumerable<DeviceCommandDescriptor>? Commands
    {
        get => (IEnumerable<DeviceCommandDescriptor>?)GetValue(CommandsProperty);
        set => SetValue(CommandsProperty, value);
    }

    public event Action<DeviceCommandDescriptor>? CommandRequested;

    private void Command_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DeviceCommandDescriptor command })
            CommandRequested?.Invoke(command);
    }
}
