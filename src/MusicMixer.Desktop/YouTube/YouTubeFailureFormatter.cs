// Преобразование ошибок YouTube в технические подробности для UI.
// Слой Desktop, internal. Локализованный текст берётся по Kind,
// а здесь собирается ограниченный диагностический хвост.
using MusicMixer.Application.YouTube;

namespace MusicMixer.Desktop.YouTube;

/// <summary>
/// Оставляет пользовательский текст ошибки чистым, но при неожиданных сбоях
/// показывает выполнимые технические подробности.
/// </summary>
internal static class YouTubeFailureFormatter
{
    public static string? GetTechnicalDetails(YouTubeServiceException exception)
    {
        // Для проверки входа уже есть локализованный и выполнимый совет;
        // сырой лог yt-dlp с ссылками на cookies только перегружает диалог.
        if (exception.Kind == YouTubeFailureKind.SignInRequired)
        {
            return null;
        }

        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(exception.Diagnostics))
        {
            // Хвост FFmpeg ограничен 1500 символами: причина обычно в конце,
            // а весь лог в интерфейсе не нужен.
            var diagnostics = exception.Diagnostics
                .Replace("\r", string.Empty, StringComparison.Ordinal)
                .Trim();

            if (diagnostics.Length > 1_500)
            {
                diagnostics = diagnostics[^1_500..];
            }

            details.Add(diagnostics);
        }

        // Для сетевых/загрузочных сбоев полезны сообщения вложенных исключений;
        // для «видео недоступно» они только запутают пользователя.
        if (exception.Kind is
            YouTubeFailureKind.SearchFailed or
            YouTubeFailureKind.DownloadFailed or
            YouTubeFailureKind.ConversionFailed)
        {
            for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
            {
                if (!string.IsNullOrWhiteSpace(inner.Message) &&
                    !details.Contains(inner.Message, StringComparer.Ordinal))
                {
                    details.Add(inner.Message);
                }
            }
        }

        return details.Count == 0
            ? null
            : string.Join(Environment.NewLine, details);
    }
}
