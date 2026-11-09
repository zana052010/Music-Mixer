// Обход региональной проверки, встроенной в YoutubeExplode (пакет Deorcify).
// Проверка — это инициализатор модуля YoutubeExplode.dll: он смотрит на
// CultureInfo.CurrentCulture (окончание -ru/-by) и на значение реестра
// HKCU\Control Panel\International\Geo\Name (RU/BY) и при совпадении закрывает программу.
// Здесь мы один раз, до запуска интерфейса, запускаем этот инициализатор сами:
// временно ставим инвариантную культуру и подменяем HKCU пустым разделом —
// только для текущего процесса (RegOverridePredefKey), система не меняется.
// Затем всё сразу возвращается; повторно CLR инициализатор модуля не выполняет.
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using YoutubeExplode;

namespace MusicMixer.Infrastructure.YouTube;

public static class YoutubeExplodeRegionShim
{
    private const string EmptyKeyPath = @"Software\MusicMixer\EmptyHive";
    private static readonly IntPtr CurrentUserKey = new(unchecked((int)0x80000001));
    private static int _initialized;

    /// <summary>
    /// Запускает инициализатор модуля YoutubeExplode с нейтральными региональными
    /// настройками. Вызывать как можно раньше, до первого обращения к YoutubeExplode.
    /// </summary>
    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 1)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                RunWithEmptyCurrentUserHive();
            }
            else
            {
                RunLibraryInitializer();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RunWithEmptyCurrentUserHive()
    {
        RegistryKey? emptyKey = null;
        try
        {
            emptyKey = Registry.CurrentUser.CreateSubKey(EmptyKeyPath, writable: false);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            emptyKey = null;
        }

        if (emptyKey is null)
        {
            RunLibraryInitializer();
            return;
        }

        using (emptyKey)
        {
            var overridden = NativeMethods.RegOverridePredefKey(CurrentUserKey, emptyKey.Handle.DangerousGetHandle()) == 0;
            try
            {
                RunLibraryInitializer();
            }
            finally
            {
                if (overridden)
                {
                    _ = NativeMethods.RegOverridePredefKey(CurrentUserKey, IntPtr.Zero);
                }
            }
        }
    }

    // Отдельный метод без инлайнинга: сборка YoutubeExplode не должна
    // инициализироваться раньше, чем мы подготовим окружение.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunLibraryInitializer() =>
        RuntimeHelpers.RunModuleConstructor(typeof(YoutubeClient).Module.ModuleHandle);

    private static class NativeMethods
    {
        [DllImport("advapi32.dll")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern int RegOverridePredefKey(IntPtr hKey, IntPtr hNewHKey);
    }
}
