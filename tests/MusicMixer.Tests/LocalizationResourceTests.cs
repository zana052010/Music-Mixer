using System.Text.Json;

namespace MusicMixer.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void EnglishAndRussianDictionaries_HaveIdenticalNonEmptyKeys()
    {
        var english = Load("en");
        var russian = Load("ru");

        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            russian.Keys.OrderBy(key => key, StringComparer.Ordinal));

        Assert.All(english, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
        Assert.All(russian, pair => Assert.False(string.IsNullOrWhiteSpace(pair.Value)));
    }

    private static Dictionary<string, string> Load(string culture)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Localization",
            $"Strings.{culture}.json");

        Assert.True(File.Exists(path), $"Missing localization resource: {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
            ?? throw new InvalidOperationException($"Could not parse {path}.");
    }
}
