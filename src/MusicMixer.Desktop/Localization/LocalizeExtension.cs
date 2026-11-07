// Расширение разметки {loc:Localize Key=...}. Слой Desktop.
// Возвращает живую одностороннюю привязку к LocalizedString.Value,
// поэтому текст меняется вместе с языком без перезагрузки окна.
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace MusicMixer.Desktop.Localization;

/// <summary>
/// Использование: Text="{loc:Localize Key=SearchPlaceholder}".
/// Возвращаемая привязка живая и следует за активным языком.
/// </summary>
public sealed class LocalizeExtension : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        if (string.IsNullOrWhiteSpace(Key))
        {
            return string.Empty;
        }

        var source = LocalizationManager.Instance.GetLocalizedValue(Key);
        return new Binding(nameof(LocalizedString.Value))
        {
            Source = source,
            Mode = BindingMode.OneWay,
        };
    }
}
