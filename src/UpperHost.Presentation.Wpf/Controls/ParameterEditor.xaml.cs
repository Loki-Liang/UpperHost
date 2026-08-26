using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using UpperHost.Presentation.Wpf.Mvvm;

namespace UpperHost.Presentation.Wpf.Controls;

public partial class ParameterEditor : UserControl
{
    public static readonly DependencyProperty ParametersProperty = DependencyProperty.Register(
        nameof(Parameters),
        typeof(ObservableCollection<ParameterEntry>),
        typeof(ParameterEditor),
        new PropertyMetadata(null));

    public ParameterEditor() => InitializeComponent();

    public ObservableCollection<ParameterEntry>? Parameters
    {
        get => (ObservableCollection<ParameterEntry>?)GetValue(ParametersProperty);
        set => SetValue(ParametersProperty, value);
    }

    public event Action<ParameterEntry>? CommitRequested;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ParameterEntry entry } && !entry.IsReadOnly)
            CommitRequested?.Invoke(entry);
    }
}
