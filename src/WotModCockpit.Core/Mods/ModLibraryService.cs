using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Mods;

/// <summary>Настройки фасада лаунчера.</summary>
public sealed record ModLibraryOptions
{
    /// <summary>Значения по умолчанию.</summary>
    public static ModLibraryOptions Default { get; } = new();

    /// <summary>
    /// Путь к журналу операций. <c>null</c> — <c>%APPDATA%\WotModCockpit\mods-operations.log</c>,
    /// та же папка, где лежит <c>settings.json</c> кокпита (с <c>WOTCOCKPIT_SETTINGS_DIR</c> —
    /// каталог из неё, см. <see cref="ModLibraryService.DefaultOperationLogPath"/>).
    /// </summary>
    public string? OperationLogPath { get; init; }

    /// <summary>Писать ли журнал вообще. В тестах выключается, чтобы не трогать профиль пользователя.</summary>
    public bool WriteOperationLog { get; init; } = true;

    /// <summary>Настройки сканера.</summary>
    public ModScanOptions Scan { get; init; } = ModScanOptions.Default;
}

/// <summary>
/// Реализация <see cref="IModLibraryService"/>: сканер + переключатель + иконки в одном фасаде,
/// плюс журнал операций.
/// <para>
/// Разделение обязанностей внутри намеренное: <see cref="ModScanner"/> умеет ТОЛЬКО читать,
/// <see cref="ModToggle"/> — единственное место, где вообще есть запись, и каждая запись проходит
/// через его проверки (граница каталога модов, расширение файла, запущенная игра, занятость файла,
/// свободное целевое имя, совпадение снимка с диском).
/// </para>
/// </summary>
public sealed class ModLibraryService : IModLibraryService
{
    /// <summary>Имя файла журнала операций.</summary>
    public const string OperationLogFileName = "mods-operations.log";

    private readonly ModScanner _scanner;
    private readonly ModToggle _toggle;
    private readonly ModLibraryOptions _options;
    // System.Threading.Lock — это .NET 9, а Core собирается под net8.0; здесь обычный монитор.
    private readonly object _iconLock = new();
    private readonly Dictionary<string, ModIcon> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Создаёт фасад.</summary>
    /// <param name="gameProbe">Проба «запущена ли игра»; <c>null</c> — настоящая, по списку процессов.</param>
    /// <param name="options">Настройки; <c>null</c> — по умолчанию.</param>
    public ModLibraryService(IGameRunningProbe? gameProbe = null, ModLibraryOptions? options = null)
    {
        _options = options ?? ModLibraryOptions.Default;
        _scanner = new ModScanner(_options.Scan);
        _toggle = new ModToggle(gameProbe);
    }

    /// <inheritdoc />
    public string OperationLogPath => _options.OperationLogPath ?? DefaultOperationLogPath();

    /// <summary>
    /// Путь к журналу по умолчанию — рядом с <c>settings.json</c> кокпита: в
    /// <c>%APPDATA%\WotModCockpit</c>, а при заданной <c>WOTCOCKPIT_SETTINGS_DIR</c> — в ней.
    /// Иначе вторая копия кокпита (или прогон со снимками окон) дописывала бы свои операции
    /// в настоящий журнал пользователя.
    /// </summary>
    /// <returns>Абсолютный путь.</returns>
    public static string DefaultOperationLogPath() => Path.Combine(
        Path.GetDirectoryName(Services.JsonSettingsStore.DefaultSettingsPath(
            Environment.GetEnvironmentVariable(Services.JsonSettingsStore.SettingsDirEnvironmentVariable)))!,
        OperationLogFileName);

    // ------------------------------------------------------------------ чтение

    /// <inheritdoc />
    public ModLibraryScan Scan(GameInstall game, IReadOnlyList<string>? projectDirs = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _scanner.Scan(game.ModsDir, projectDirs);
    }

    /// <inheritdoc />
    public InstalledMod? Refresh(GameInstall game, string fullPath, IReadOnlyList<string>? projectDirs = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _scanner.ScanOne(game.ModsDir, fullPath, _scanner.ReadProjects(projectDirs));
    }

    /// <inheritdoc />
    public ResModsSummary ScanResMods(GameInstall game)
    {
        ArgumentNullException.ThrowIfNull(game);
        return _scanner.ScanResMods(game.ResModsDir);
    }

    /// <inheritdoc />
    public GameRunningState CheckGame() => _toggle.CheckGame();

    /// <inheritdoc />
    public ModIcon ReadIcon(string mtmodPath, string? modId = null)
    {
        if (string.IsNullOrWhiteSpace(mtmodPath))
        {
            return ModIcon.None;
        }

        string key;
        try
        {
            FileInfo info = new(mtmodPath);
            if (!info.Exists)
            {
                return ModIcon.None;
            }

            // Ключ включает время и размер: перезалили мод — кеш сам обесценился.
            key = $"{info.FullName}|{info.LastWriteTimeUtc.Ticks}|{info.Length}|{modId}";
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return ModIcon.Failed(ex.Message);
        }

        lock (_iconLock)
        {
            if (_iconCache.TryGetValue(key, out ModIcon? cached))
            {
                return cached;
            }
        }

        ModIcon icon = ModIcons.Read(mtmodPath, modId);

        lock (_iconLock)
        {
            _iconCache[key] = icon;
        }

        return icon;
    }

    // ------------------------------------------------------------------ операции

    /// <inheritdoc />
    public ModOperationResult Enable(GameInstall game, string fullPath, ModWriteExpectation? expect = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Journal(_toggle.Enable(game.ModsDir, fullPath, expect));
    }

    /// <inheritdoc />
    public ModOperationResult Disable(GameInstall game, string fullPath, ModWriteExpectation? expect = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        return Journal(_toggle.Disable(game.ModsDir, fullPath, expect));
    }

    /// <inheritdoc />
    public ModOperationResult Delete(GameInstall game, string fullPath, string confirmFileName, ModWriteExpectation? expect = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        string source = ModToggle.Normalize(fullPath);

        // Подтверждение проверяется ПЕРВЫМ: пока имя не совпало, кокпит даже не смотрит на диск.
        if (!string.Equals((confirmFileName ?? string.Empty).Trim(), Path.GetFileName(source), StringComparison.Ordinal))
        {
            return Journal(new ModOperationResult(
                ModOperation.Delete,
                ModOperationStatus.RefusedConfirmationMismatch,
                "напечатанное имя файла не совпало с именем удаляемого файла",
                source));
        }

        ModOperationResult? refusal = _toggle.Validate(ModOperation.Delete, game.ModsDir, source, expect);
        if (refusal is not null)
        {
            return Journal(refusal);
        }

        try
        {
            using (FileStream _ = new(source, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                // проверка занятости: если файл кем-то удерживается, удалять нельзя
            }

            File.Delete(source);
        }
        catch (IOException ex)
        {
            return Journal(new ModOperationResult(
                ModOperation.Delete, ModOperationStatus.RefusedFileLocked,
                "файл занят другим процессом: " + ex.Message, source));
        }
        catch (UnauthorizedAccessException ex)
        {
            return Journal(new ModOperationResult(
                ModOperation.Delete, ModOperationStatus.Failed, ex.Message, source));
        }

        return Journal(new ModOperationResult(
            ModOperation.Delete, ModOperationStatus.Ok, "файл удалён безвозвратно", source));
    }

    /// <inheritdoc />
    public ModOperationResult Install(GameInstall game, string artifactPath, ModInstallOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(game);

        ModInstallOptions opts = options ?? ModInstallOptions.Default;
        string source = ModToggle.Normalize(artifactPath);

        if (!ModFileNames.IsEnabledFile(source))
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedNotModFile,
                "устанавливать можно только файл *.mtmod", source));
        }

        if (!File.Exists(source))
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedSourceMissing,
                "собранного файла нет — сначала соберите мод", source));
        }

        if (!Directory.Exists(game.ModsDir))
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedModsDirMissing,
                $"каталога модов нет: {game.ModsDir}", source));
        }

        GameRunningState state = _toggle.CheckGame();
        if (state.BlocksWrites)
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedGameRunning, state.Message, source));
        }

        // Санитарная проверка: ставим только то, что вообще открывается как ZIP.
        // Мод, который не читается здесь, не прочитается и движком.
        string? archiveProblem = CheckArchiveReadable(source);
        if (archiveProblem is not null)
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.Failed, archiveProblem, source));
        }

        string targetDir = game.ModsDir;
        if (!string.IsNullOrWhiteSpace(opts.SubfolderRelative))
        {
            targetDir = ModToggle.Normalize(Path.Combine(game.ModsDir, opts.SubfolderRelative!));
            if (!ModToggle.IsInside(game.ModsDir, targetDir))
            {
                return Journal(new ModOperationResult(
                    ModOperation.Install, ModOperationStatus.RefusedOutsideModsDir,
                    "подпапка ведёт за пределы каталога модов", source, targetDir));
            }
        }

        string target = Path.Combine(targetDir, Path.GetFileName(source));
        if (!ModToggle.IsInside(game.ModsDir, target) || !ModFileNames.IsEnabledFile(target))
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedOutsideModsDir,
                "целевой путь вне каталога модов", source, target));
        }

        bool targetExists = File.Exists(target);
        if (targetExists && !opts.OverwriteSameName)
        {
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.RefusedTargetExists,
                "файл с таким именем уже установлен — та же версия уже в игре", source, target));
        }

        string temp = target + ".cockpit-tmp";
        try
        {
            Directory.CreateDirectory(targetDir);
            File.Copy(source, temp, overwrite: true);

            if (targetExists)
            {
                File.Move(temp, target, overwrite: true);
            }
            else
            {
                File.Move(temp, target);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDeleteTemp(temp);
            return Journal(new ModOperationResult(
                ModOperation.Install, ModOperationStatus.Failed, ex.Message, source, target));
        }

        IReadOnlyList<ModOperationResult> related = opts.DisablePreviousVersions
            ? DisableOtherVersions(game, target)
            : Array.Empty<ModOperationResult>();

        return Journal(new ModOperationResult(
            ModOperation.Install,
            ModOperationStatus.Ok,
            targetExists
                ? "файл заменён; изменения вступят в силу при следующем запуске игры"
                : "мод установлен; изменения вступят в силу при следующем запуске игры",
            source,
            target)
        {
            Related = related
        });
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>
    /// Выключает другие установленные версии того же мода. Именно выключает, а не удаляет:
    /// предыдущая сборка остаётся на диске и включается обратно одним действием.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="justInstalled">Путь только что установленного файла — его трогать нельзя.</param>
    /// <returns>Результаты выключений.</returns>
    private IReadOnlyList<ModOperationResult> DisableOtherVersions(GameInstall game, string justInstalled)
    {
        InstalledMod? installed = _scanner.ScanOne(game.ModsDir, justInstalled);
        string modId = installed?.ModId ?? string.Empty;
        if (modId.Length == 0)
        {
            modId = IdFromFileName(Path.GetFileName(justInstalled));
        }

        if (modId.Length == 0)
        {
            return Array.Empty<ModOperationResult>();
        }

        ModLibraryScan scan = _scanner.Scan(game.ModsDir);
        List<ModOperationResult> results = [];

        foreach (InstalledMod m in scan.Mods)
        {
            if (m.State != ModState.Enabled ||
                m.FullPath.Equals(justInstalled, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Тот же мод — по <id> из meta.xml, а у архива без meta.xml — по <id> из имени файла
            // ЦЕЛИКОМ. Не по началу имени: ru.local.clock_ — это и ru.local.clock_lite_1.0.mtmod,
            // совсем другой мод, и выключать его установка ru.local.clock не вправе.
            bool sameMod = !string.IsNullOrEmpty(m.ModId)
                ? m.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)
                : IdFromFileName(Path.GetFileName(m.FullPath)).Equals(modId, StringComparison.OrdinalIgnoreCase);

            if (!sameMod)
            {
                continue;
            }

            results.Add(Journal(_toggle.Disable(game.ModsDir, m.FullPath)));
        }

        return results;
    }

    /// <summary>Идентификатор, выведенный из имени файла по правилу build.py <c>&lt;id&gt;_&lt;version&gt;.mtmod</c>.</summary>
    /// <param name="fileName">Имя файла.</param>
    /// <returns>Идентификатор либо пустая строка.</returns>
    private static string IdFromFileName(string fileName)
    {
        string display = ModFileNames.DisplayNameFromFileName(fileName);
        return ModFileNames.VersionFromFileName(fileName) is null ? string.Empty : display;
    }

    /// <summary>Проверяет, что файл открывается как ZIP и не пуст.</summary>
    /// <param name="path">Путь к архиву.</param>
    /// <returns><c>null</c>, если всё хорошо, иначе текст проблемы.</returns>
    private static string? CheckArchiveReadable(string path)
    {
        try
        {
            using FileStream fs = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using ZipArchive zip = new(fs, ZipArchiveMode.Read, leaveOpen: false);
            return zip.Entries.Count > 0
                ? null
                : "архив пуст — движок скажет «mod is empty or dont have mount root»";
        }
        catch (InvalidDataException ex)
        {
            return "файл не читается как ZIP: " + ex.Message;
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return ex.Message;
        }
    }

    /// <summary>Убирает временный файл, если установка сорвалась.</summary>
    /// <param name="temp">Путь временного файла.</param>
    private static void TryDeleteTemp(string temp)
    {
        try
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            // остался временный файл — не повод падать
        }
    }

    // ------------------------------------------------------------------ журнал

    /// <summary>
    /// Дописывает строку в журнал и возвращает результат как есть. Журнал — не критичный путь:
    /// любая беда с ним молча игнорируется, операция от этого не «ломается задним числом».
    /// </summary>
    /// <param name="result">Результат операции.</param>
    /// <returns>Он же.</returns>
    private ModOperationResult Journal(ModOperationResult result)
    {
        if (!_options.WriteOperationLog)
        {
            return result;
        }

        try
        {
            string path = OperationLogPath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.AppendAllText(path, result.ToLogLine() + Environment.NewLine, Encoding.UTF8);
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            // журнал недоступен — операция уже выполнена, молчим
        }

        return result;
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ReadOperationLog(int maxLines = 200)
    {
        if (maxLines <= 0)
        {
            return Array.Empty<string>();
        }

        try
        {
            string path = OperationLogPath;
            if (!File.Exists(path))
            {
                return Array.Empty<string>();
            }

            string[] all = File.ReadAllLines(path, Encoding.UTF8);
            return all.Length <= maxLines ? all : all[^maxLines..];
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return Array.Empty<string>();
        }
    }

    // ------------------------------------------------------------------ поиск проектов

    /// <summary>
    /// Находит соседние проекты модов рядом с известным: каталоги <c>wot-mod-*</c> в том же
    /// родительском каталоге, у которых есть <c>build.py</c> либо <c>src\meta.xml</c>.
    /// <para>
    /// Нужно затем, чтобы список «МОИ МОДЫ» показывал все проекты пользователя, а не только
    /// открытый сейчас: у соседних проектов (<c>&lt;репозитории&gt;\my-mod</c>) может не быть
    /// <c>wotmod.json</c>, и в списке недавних кокпита они могут не значиться вовсе.
    /// </para>
    /// </summary>
    /// <param name="anyProjectDir">Любой известный каталог проекта.</param>
    /// <returns>Абсолютные пути найденных каталогов, включая исходный. Пустой список — норма.</returns>
    public static IReadOnlyList<string> DiscoverSiblingProjects(string? anyProjectDir)
    {
        if (string.IsNullOrWhiteSpace(anyProjectDir))
        {
            return Array.Empty<string>();
        }

        try
        {
            string dir = Path.GetFullPath(anyProjectDir);
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null || !Directory.Exists(parent))
            {
                return Directory.Exists(dir) ? [dir] : Array.Empty<string>();
            }

            List<string> found = [];
            foreach (string candidate in Directory.EnumerateDirectories(parent, "wot-mod-*", SearchOption.TopDirectoryOnly))
            {
                if (File.Exists(Path.Combine(candidate, "build.py")) ||
                    File.Exists(Path.Combine(candidate, "src", "meta.xml")))
                {
                    found.Add(candidate);
                }
            }

            if (Directory.Exists(dir) && !found.Any(f => f.Equals(dir, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(dir);
            }

            found.Sort(StringComparer.OrdinalIgnoreCase);
            return found;
        }
        catch (Exception ex) when (ModScanner.IsExpectedIoFailure(ex))
        {
            return Array.Empty<string>();
        }
    }
}

/// <summary>
/// Регистрация лаунчера в DI. Вынесена сюда, а не дописана в <c>AddCockpitCore</c>, чтобы
/// добавление экрана «Моды» не трогало существующий композиционный корень
/// (его состав проверяют уже написанные тесты).
/// </summary>
public static class ModLibraryServiceCollectionExtensions
{
    /// <summary>
    /// Добавляет <see cref="IGameRunningProbe"/> и <see cref="IModLibraryService"/>.
    /// Оба Singleton: у службы есть кеш иконок, а проба процессов не имеет состояния.
    /// </summary>
    /// <param name="services">Коллекция сервисов.</param>
    /// <returns>Та же коллекция для цепочки вызовов.</returns>
    public static IServiceCollection AddModLibrary(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IGameRunningProbe, ProcessGameRunningProbe>();
        services.AddSingleton<IModLibraryService>(sp =>
            new ModLibraryService(sp.GetRequiredService<IGameRunningProbe>()));

        return services;
    }
}
