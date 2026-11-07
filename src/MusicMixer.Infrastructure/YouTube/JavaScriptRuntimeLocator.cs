// Поиск JavaScript runtime для yt-dlp.
// С версии 2025.11.12 yt-dlp использует внешний JS-движок для YouTube;
// без него часть видео получает "Sign in to confirm you're not a bot".
namespace MusicMixer.Infrastructure.YouTube;

/// <summary>
/// Находит установленный Deno, Node.js или Bun и возвращает значение для
/// аргумента yt-dlp <c>--js-runtimes runtime:path</c>.
/// </summary>
internal static class JavaScriptRuntimeLocator
{
    private static readonly (string Name, string FileName)[] Candidates =
    [
        ("deno", "deno"),
        ("node", "node"),
        ("bun", "bun"),
    ];

    public static string? Find()
    {
        foreach (var (name, fileName) in Candidates)
        {
            var executableName = OperatingSystem.IsWindows()
                ? fileName + ".exe"
                : fileName;
            var path = FindOnPath(executableName) ?? FindInKnownLocations(name, executableName);
            if (path is not null)
            {
                return $"{name}:{path}";
            }
        }

        return null;
    }

    private static string? FindOnPath(string executableName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        foreach (var directory in path.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executableName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private static string? FindInKnownLocations(string name, string executableName)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (name == "node")
            {
                candidates.Add(Path.Combine(programFiles, "nodejs", executableName));
                candidates.Add(Path.Combine(programFilesX86, "nodejs", executableName));
                candidates.Add(Path.Combine(local, "Programs", "nodejs", executableName));
            }
            else if (name == "deno")
            {
                candidates.Add(Path.Combine(home, ".deno", "bin", executableName));
                candidates.Add(Path.Combine(local, "Deno", "bin", executableName));
            }
            else
            {
                candidates.Add(Path.Combine(home, ".bun", "bin", executableName));
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add(Path.Combine("/opt/homebrew/bin", executableName));
            candidates.Add(Path.Combine("/usr/local/bin", executableName));
            candidates.Add(Path.Combine(home, ".deno", "bin", executableName));
            candidates.Add(Path.Combine(home, ".bun", "bin", executableName));
        }
        else
        {
            candidates.Add(Path.Combine("/usr/local/bin", executableName));
            candidates.Add(Path.Combine("/usr/bin", executableName));
            candidates.Add(Path.Combine(home, ".deno", "bin", executableName));
            candidates.Add(Path.Combine(home, ".bun", "bin", executableName));
        }

        return candidates.FirstOrDefault(File.Exists);
    }
}
