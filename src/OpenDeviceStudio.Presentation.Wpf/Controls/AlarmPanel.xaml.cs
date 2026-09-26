using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OpenDeviceStudio.Abstractions.Diagnostics;

namespace OpenDeviceStudio.Presentation.Wpf.Controls;

public partial class AlarmPanel : UserControl
{
    public static readonly DependencyProperty AlarmsProperty = DependencyProperty.Register(
        nameof(Alarms),
        typeof(IEnumerable<Alarm>),
        typeof(AlarmPanel),
        new PropertyMetadata(null));

    public AlarmPanel() => InitializeComponent();

    public IEnumerable<Alarm>? Alarms
    {
        get => (IEnumerable<Alarm>?)GetValue(AlarmsProperty);
        set => SetValue(AlarmsProperty, value);
    }
}
