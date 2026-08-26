using System.Collections.ObjectModel;
using UpperHost.Abstractions.Devices;

namespace UpperHost.Presentation.Wpf.Mvvm;

public sealed class ParameterEntry : ObservableObject
{
    private object? _value;

    public ParameterEntry(DeviceParameterDescriptor descriptor)
    {
        Descriptor = descriptor;
        _value = descriptor.Value;
    }

    public DeviceParameterDescriptor Descriptor { get; }
    public string Key => Descriptor.Key;
    public string DisplayName => Descriptor.DisplayName;
    public DeviceParameterKind Kind => Descriptor.Kind;
    public string? Unit => Descriptor.Unit;
    public double? Minimum => Descriptor.Minimum;
    public double? Maximum => Descriptor.Maximum;
    public bool IsReadOnly => Descriptor.IsReadOnly;
    public IReadOnlyList<string>? Options => Descriptor.Options;

    public object? Value
    {
        get => _value;
        set => SetProperty(ref _value, value);
    }
}

public sealed class ParameterEditorViewModel
{
    private readonly IParameterProvider _provider;

    public ParameterEditorViewModel(IParameterProvider provider) => _provider = provider;

    public ObservableCollection<ParameterEntry> Parameters { get; } = [];

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var parameters = await _provider.GetParametersAsync(cancellationToken).ConfigureAwait(false);
        Parameters.Clear();
        foreach (var parameter in parameters)
            Parameters.Add(new ParameterEntry(parameter));
    }

    public Task CommitAsync(ParameterEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.IsReadOnly)
            throw new InvalidOperationException($"Parameter '{entry.Key}' is read-only.");

        return _provider.SetParameterAsync(entry.Key, entry.Value, cancellationToken);
    }
}
