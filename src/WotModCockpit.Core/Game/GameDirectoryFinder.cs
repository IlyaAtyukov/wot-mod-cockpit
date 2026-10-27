using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Поиск установленной игры на первом запуске кокпита. Только чтение: ни реестр, ни файлы
/// игры здесь не меняются.
/// <para>
/// Порядок — по убыванию уверенности:
/// </para>
/// <list type="number">
///   <item>переменная окружения <see cref="EnvironmentVariable"/> (<c>WOTCOCKPIT_GAME_DIR</c>);</item>
///   <item>список установленных программ в реестре (ключи <c>Uninstall</c>, издатель Lesta/Wargaming
///         или имя «Мир танков»/«World of Tanks»/«Tanki»);</item>
///   <item>типовые папки установки на всех фиксированных дисках.</item>
/// </list>
/// <para>
/// Списки повторяют установщик (<c>WotModCockpit.Setup/GameLocator.cs</c>): тот собран под
/// .NET Framework 4.7.2 и C# 7.3, ссылкой его в Core не подключить, поэтому логика скопирована.
/// Меняя списки здесь, поменяйте их и там — иначе кокпит и выданный пользователю Setup.exe
/// будут находить игру по-разному.
/// </para>
/// <para>
/// Признак установки — <c>paths.xml</c> в каталоге: именно его читает
/// <see cref="PathsXmlGameInstallLocator"/>. Ничего не нашлось — возвращается пусто, и шапка
/// кокпита предлагает выбрать каталог вручную; выдумывать путь нельзя.
/// </para>
/// </summary>
public static class GameDirectoryFinder
{
    /// <summary>Переменная окружения с каталогом игры. Главнее реестра и типовых папок.</summary>
    public const string EnvironmentVariable = "WOTCOCKPIT_GAME_DIR";

    private const string PathsXml = "paths.xml";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Издатели, по которым игра опознаётся в списке установленных программ.</summary>
    private static readonly string[] Publishers = ["Lesta", "Wargaming"];

    /// <summary>Имена, по которым игра опознаётся в списке установленных программ.</summary>
    private static readonly string[] DisplayNames = ["Мир танков", "World of Tanks", "Tanki"];

    /// <summary>
    /// Типовые папки установки — на случай, если записи в реестре нет. Первые пять — те же,
    /// что у установщика; остальные — короткие имена, которые встречаются у ручных установок.
    /// </summary>
    private static readonly string[] TypicalFolders =
    [
        @"Games\World_of_Tanks_RU",
        @"Games\World_of_Tanks",
        @"Games\Tanki",
        @"Games\Мир танков",
        @"Games\WoT",
        "World_of_Tanks_RU",
        "World_of_Tanks",
        "Tanki",
        "Мир танков",
        "WoT",
    ];

    /// <summary>Первый найденный каталог игры.</summary>
    /// <returns>Полный путь к каталогу с <c>paths.xml</c> либо <c>null</c>.</returns>
    public static string? FindFirst() => FindCandidates().FirstOrDefault();

    /// <summary>
    /// Все каталоги, похожие на установку игры (в каждом есть <c>paths.xml</c>), без повторов,
    /// в порядке убывания уверенности.
    /// </summary>
    /// <returns>Список; пустой, если ничего не нашлось.</returns>
    public static IReadOnlyList<string> FindCandidates()
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string dir in FromEnvironment().Concat(FromRegistry()).Concat(FromTypicalFolders()))
        {
            string? full = Normalize(dir);
            if (full is not null && seen.Add(full) && LooksLikeGame(full))
            {
                result.Add(full);
            }
        }

        return result;
    }

    /// <summary>Каталог похож на установку игры, если в нём лежит <c>paths.xml</c>.</summary>
    /// <param name="dir">Путь-кандидат.</param>
    /// <returns>Похож ли каталог на игру.</returns>
    public static bool LooksLikeGame(string? dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, PathsXml));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or SecurityException)
        {
            return false;
        }
    }

    private static IEnumerable<string> FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value) ? [] : [value];
    }

    private static IEnumerable<string> FromRegistry() =>
        OperatingSystem.IsWindows() ? ReadUninstallEntries() : [];

    [SupportedOSPlatform("windows")]
    private static List<string> ReadUninstallEntries()
    {
        var found = new List<string>();
        RegistryHive[] hives = [RegistryHive.CurrentUser, RegistryHive.LocalMachine];
        RegistryView[] views = [RegistryView.Registry64, RegistryView.Registry32];

        foreach (RegistryHive hive in hives)
        {
            foreach (RegistryView view in views)
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallKey);
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (string name in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? app = uninstall.OpenSubKey(name);
                        if (app is null)
                        {
                            continue;
                        }

                        string publisher = app.GetValue("Publisher") as string ?? string.Empty;
                        string display = app.GetValue("DisplayName") as string ?? string.Empty;
                        string? location = app.GetValue("InstallLocation") as string;

                        if (!string.IsNullOrWhiteSpace(location)
                            && (ContainsAny(publisher, Publishers) || ContainsAny(display, DisplayNames)))
                        {
                            found.Add(location);
                        }
                    }
                }
                catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
                {
                    // Нет доступа к ветке реестра — ищем дальше.
                }
            }
        }

        return found;
    }

    private static List<string> FromTypicalFolders()
    {
        var found = new List<string>();
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return found;
        }

        foreach (DriveInfo drive in drives)
        {
            bool ready;
            try
            {
                ready = drive.DriveType == DriveType.Fixed && drive.IsReady;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ready = false;
            }

            if (!ready)
            {
                continue;
            }

            foreach (string folder in TypicalFolders)
            {
                found.Add(Path.Combine(drive.RootDirectory.FullName, folder));
            }
        }

        return found;
    }

    private static bool ContainsAny(string text, string[] needles)
    {
        foreach (string needle in needles)
        {
            if (text.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Абсолютный путь без завершающего разделителя либо <c>null</c>.</summary>
    private static string? Normalize(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(dir.Trim().Trim('"'));
            string root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd('\\', '/') : full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or SecurityException)
        {
            return null;
        }
    }
}
