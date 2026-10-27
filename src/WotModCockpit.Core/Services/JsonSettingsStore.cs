using System.Text;
using System.Text.Json;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Реализация <see cref="ISettingsStore"/> поверх <c>%APPDATA%\WotModCockpit\settings.json</c>.
/// Контракт — в <see cref="ISettingsStore"/>. Каталог файла можно переопределить переменной
/// окружения <see cref="SettingsDirEnvironmentVariable"/> (<c>WOTCOCKPIT_SETTINGS_DIR</c>): так
/// тесты и вторая копия кокпита не трогают настоящие настройки пользователя.
/// <para>
/// Два правила, из которых следует всё остальное:
/// </para>
/// <list type="number">
///   <item><b>Настройки не имеют права мешать запуску.</b> Любой отказ чтения — нет файла, битый JSON,
///         нет прав, файл занят — превращается в значения по умолчанию, а не в исключение.
///         Кокпит без настроек полезен, кокпит, который не стартует, — нет.</item>
///   <item><b>Первый запуск не должен быть пустым.</b> Когда файла ещё нет (или его не удалось
///         прочитать — пустой, битый, недоступный), поля добираются
///         пробой диска (<see cref="SeedDefaults"/>): каталог игры, шаблон мода и
///         декомпилированные исходники рядом с ним. Проект мода при этом НЕ выбирается:
///         открыть сам шаблон как рабочий проект значило бы править то, из чего мастер
///         потом копирует новые моды, и ставить в игру его демонстрационный мод.</item>
/// </list>
/// </summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    /// <summary>Имя каталога настроек внутри <c>%APPDATA%</c>.</summary>
    public const string AppFolderName = "WotModCockpit";

    /// <summary>Имя файла настроек.</summary>
    public const string FileName = "settings.json";

    /// <summary>
    /// Переменная окружения с каталогом настроек. Задана — файл настроек лежит в ней,
    /// а не в <c>%APPDATA%\WotModCockpit</c>.
    /// </summary>
    public const string SettingsDirEnvironmentVariable = "WOTCOCKPIT_SETTINGS_DIR";

    /// <summary>Потолок списка недавних проектов.</summary>
    private const int MaxRecentProjects = 10;

    /// <summary>UTF-8 без BOM: тот же формат, что и у <c>wotmod.json</c>.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Создаёт хранилище с путём по умолчанию: <c>%APPDATA%\WotModCockpit\settings.json</c>
    /// либо <c>&lt;WOTCOCKPIT_SETTINGS_DIR&gt;\settings.json</c>, если переменная задана.
    /// </summary>
    public JsonSettingsStore()
        : this(null)
    {
    }

    /// <summary>
    /// Создаёт хранилище с явным путём к файлу настроек. Нужно тестам, чтобы не трогать
    /// настоящий <c>%APPDATA%</c> пользователя.
    /// </summary>
    /// <param name="settingsPath">Абсолютный путь к settings.json либо <c>null</c> для пути по умолчанию.</param>
    public JsonSettingsStore(string? settingsPath)
    {
        SettingsPath = string.IsNullOrWhiteSpace(settingsPath)
            ? DefaultSettingsPath(Environment.GetEnvironmentVariable(SettingsDirEnvironmentVariable))
            : Path.GetFullPath(settingsPath);
    }

    /// <summary>Путь к файлу настроек по умолчанию.</summary>
    /// <param name="settingsDirOverride">
    /// Значение <see cref="SettingsDirEnvironmentVariable"/>; пусто — каталог <c>%APPDATA%\WotModCockpit</c>.
    /// </param>
    /// <returns>Полный путь к settings.json.</returns>
    public static string DefaultSettingsPath(string? settingsDirOverride)
    {
        string dir = string.IsNullOrWhiteSpace(settingsDirOverride)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppFolderName)
            : settingsDirOverride.Trim().Trim('"');

        return Path.GetFullPath(Path.Combine(dir, FileName));
    }

    /// <inheritdoc />
    public string SettingsPath { get; }

    /// <inheritdoc />
    public CockpitSettings Load()
    {
        CockpitSettings? settings = null;

        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath, Encoding.UTF8);

                // Пустой файл — обычный результат оборванной записи на старой машине.
                if (!string.IsNullOrWhiteSpace(json))
                {
                    settings = JsonSerializer.Deserialize<CockpitSettings>(json, ProjectStoreDefaults.JsonOptions);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or NotSupportedException
                                      or ArgumentException
                                      or System.Security.SecurityException)
        {
            // Битые или недоступные настройки = как будто их нет. Контракт запрещает бросать отсюда.
            settings = null;
        }

        // Решает не наличие файла, а то, удалось ли его прочитать: пустой, «null» или битый
        // (например, путь с одиночными обратными косыми, набранный руками) settings.json
        // ничего не сообщает о выборе пользователя, и без досева кокпит остался бы без игры.
        bool parsed = settings is not null;
        settings ??= new CockpitSettings();

        Sanitize(settings);

        // Досеиваем только когда читать было нечего: если файл прочитан, пустое поле — это выбор
        // пользователя, и возвращать ему стёртый путь на каждом старте было бы навязчиво.
        if (!parsed)
        {
            SeedDefaults(settings);
        }

        return settings;
    }

    /// <inheritdoc />
    public void Save(CockpitSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Sanitize(settings);

        string json = JsonSerializer.Serialize(settings, ProjectStoreDefaults.JsonOptions);
        WriteAtomic(SettingsPath, Utf8NoBom.GetBytes(json));
    }

    // ------------------------------------------------------------------ внутреннее

    /// <summary>
    /// Приводит настройки в заведомо пригодный вид: непустые ссылки, обрезанные пути,
    /// числа в допустимых границах, список недавних проектов без дублей и без хвоста.
    /// </summary>
    /// <param name="s">Настройки, правятся на месте.</param>
    private static void Sanitize(CockpitSettings s)
    {
        s.GameDir = (s.GameDir ?? string.Empty).Trim();
        s.EditorExe = (s.EditorExe ?? string.Empty).Trim();
        s.WotSrcDir = (s.WotSrcDir ?? string.Empty).Trim();
        s.LastProjectDir = (s.LastProjectDir ?? string.Empty).Trim();
        s.TemplateDir = (s.TemplateDir ?? string.Empty).Trim();

        // Пустая команда python — это «найти самому» (PythonLocator.ResolveForBuild), а не ошибка:
        // на машине, где есть только лаунчер py, литерал «python» сборку как раз и ломал.
        s.PythonExe = (s.PythonExe ?? string.Empty).Trim();

        // Префикс id новых модов без точек по краям: «ru.local.» и «ru.local» — одно и то же.
        // Пустой префикс превратил бы id в голое имя пакета, которое build.py не примет.
        string prefix = (s.ModIdPrefix ?? string.Empty).Trim().Trim('.');
        s.ModIdPrefix = prefix.Length == 0 ? CockpitSettings.DefaultModIdPrefix : prefix;

        // Пустой шаблон = редактор откроется без перехода на строку, это молчаливая потеря функции.
        s.EditorArgsTemplate = string.IsNullOrWhiteSpace(s.EditorArgsTemplate)
            ? "--goto \"{file}:{line}\""
            : s.EditorArgsTemplate;

        s.LogPrimeTailKilobytes = Math.Clamp(s.LogPrimeTailKilobytes, 0, 2048);
        s.LogBufferCapacity = Math.Clamp(s.LogBufferCapacity, 1_000, 200_000);

        List<string> recent = s.RecentProjects ?? new List<string>();
        List<string> cleaned = new(recent.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string? item in recent)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string trimmed = item.Trim();
            if (seen.Add(trimmed))
            {
                cleaned.Add(trimmed);
            }

            if (cleaned.Count == MaxRecentProjects)
            {
                break;
            }
        }

        s.RecentProjects = cleaned;
    }

    /// <summary>
    /// Заполняет пути первого запуска тем, что реально есть на диске. Ничего не выдумывает:
    /// каждый кандидат проверяется на существование, несуществующее не записывается,
    /// а не найденное остаётся пустым — тогда интерфейс предлагает выбрать путь вручную.
    /// <para>
    /// Проект мода здесь намеренно НЕ выбирается: единственный проект, который можно найти
    /// без пользователя, — это сам шаблон, а открыть его как рабочий проект значит править
    /// эталон мастера и по кнопке «Установить» ставить в игру его демонстрационный мод.
    /// Первый запуск показывает пустое состояние с «Новый мод…» и «Открыть проект…».
    /// </para>
    /// </summary>
    /// <param name="s">Настройки, правятся на месте.</param>
    private static void SeedDefaults(CockpitSettings s)
    {
        if (string.IsNullOrWhiteSpace(s.GameDir) || !DirExists(s.GameDir))
        {
            string? game = FindGameDir(s.GameDir);
            if (game is not null)
            {
                s.GameDir = game;
            }
        }

        if (!TemplateLocator.LooksLikeTemplate(s.TemplateDir))
        {
            // Только соседний каталог: переменная окружения — переопределение на время запуска,
            // и навсегда записывать её значение в настройки было бы неверно.
            string template = TemplateLocator.ProbeSiblings();
            if (template.Length > 0)
            {
                s.TemplateDir = template;
            }
        }

        if (string.IsNullOrWhiteSpace(s.WotSrcDir) && !string.IsNullOrWhiteSpace(s.TemplateDir))
        {
            string candidate = Path.Combine(s.TemplateDir, ".scratch", "wot-src");
            if (DirExists(candidate))
            {
                s.WotSrcDir = candidate;
            }
        }
    }

    /// <summary>
    /// Ищет установку игры: сначала уже записанное значение, затем
    /// <see cref="GameDirectoryFinder"/> (переменная окружения, реестр, типовые папки).
    /// Признак установки — <c>paths.xml</c>: именно его читает <see cref="PathsXmlGameInstallLocator"/>.
    /// </summary>
    /// <param name="preferred">Значение, которое уже стоит в настройках (может не существовать).</param>
    /// <returns>Каталог игры либо <c>null</c>, если ничего похожего не нашлось.</returns>
    private static string? FindGameDir(string preferred)
    {
        if (GameDirectoryFinder.LooksLikeGame(preferred))
        {
            return Path.GetFullPath(preferred);
        }

        try
        {
            return GameDirectoryFinder.FindFirst();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            // Поиск игры — удобство, а не обязанность.
            return null;
        }
    }

    /// <summary>Существует ли каталог; на кривом пути — false, а не исключение.</summary>
    /// <param name="dir">Путь-кандидат.</param>
    /// <returns>true, если каталог существует.</returns>
    private static bool DirExists(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// Атомарная запись: временный файл рядом + <c>File.Move(overwrite: true)</c>.
    /// Так оборванная запись (падение, выключение питания) не оставляет обрезанный settings.json.
    /// </summary>
    /// <param name="target">Целевой файл.</param>
    /// <param name="content">Содержимое в готовых байтах.</param>
    private static void WriteAtomic(string target, byte[] content)
    {
        string dir = Path.GetDirectoryName(target) ?? ".";
        Directory.CreateDirectory(dir);

        string tmp = Path.Combine(dir, Path.GetFileName(target) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(tmp, content);
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp))
            {
                try
                {
                    File.Delete(tmp);
                }
                catch (IOException)
                {
                    // временный файл — не повод падать
                }
            }
        }
    }
}
