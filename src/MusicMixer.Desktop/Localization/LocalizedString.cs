// Один стабильный источник привязки для одного ключа локализации.
// Слой Desktop. Создаётся LocalizationManager; XAML слушает Value.
using System.ComponentModel;

namespace MusicMixer.Desktop.Localization;

/// <summary>
/// Стабильный источник привязки для одного ключа локализации. При смене языка
/// менеджер вызывает NotifyValueChanged, и все привязанные элементы обновляются.
/// </summary>
public sealed class LocalizedString : INotifyPropertyChanged
{
    private readonly LocalizationManager _manager;

    internal LocalizedString(LocalizationManager manager, string key)
    {
        _manager = manager;
        Key = key;
    }

    public string Key { get; }

    public string Value => _manager.GetString(Key);

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void NotifyValueChanged() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}
