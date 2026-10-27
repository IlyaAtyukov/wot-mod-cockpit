using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Settings;

/// <summary>
/// Документ дизайнера настроек — то, что лежит в <c>settings.design.json</c> в корне проекта мода
/// и является ЕДИНСТВЕННЫМ источником истины о форме.
///
/// <para>Почему источник истины — отдельный JSON, а не сам сгенерированный питон: разобрать
/// собственный вывод обратно можно, но тогда любая правка человека в машинном файле становится
/// частью модели, и первое же расхождение превращается в спор двух источников. Здесь машинный
/// файл — производное, и его не жалко перезаписать целиком.</para>
///
/// <para>Секция <c>template</c> внутри файла — это ТОТ ЖЕ САМЫЙ словарь, который уезжает
/// в <c>modsettings.dat</c>, слово в слово (его пишет и читает <see cref="TemplateJson"/>).
/// Значит содержимое файла можно прямо сравнивать с живым состоянием API, а импортированный
/// чужой шаблон переживает круг «прочитали — записали» без потерь.</para>
/// </summary>
public sealed class SettingsDesign
{
    /// <summary>Ключ версии формата документа.</summary>
    public const string DesignVersionKey = "designVersion";

    /// <summary>Ключ, под которым лежит сам шаблон.</summary>
    public const string TemplateKey = "template";

    /// <summary>Ключ секции параметров генерации.</summary>
    public const string CodeKey = "code";

    /// <summary>Текущая версия формата документа.</summary>
    public const int CurrentDesignVersion = 1;

    /// <summary>Версия формата документа.</summary>
    public int DesignVersion { get; set; } = CurrentDesignVersion;

    /// <summary>Шаблон формы.</summary>
    public SettingsTemplate Template { get; set; } = new();

    /// <summary>Параметры генерации для этого проекта.</summary>
    public SettingsCodeOptions Code { get; set; } = SettingsCodeOptions.Default;

    /// <summary>
    /// Состав формы на момент последней генерации: имена настроек в порядке обхода колонок.
    /// По нему дизайнер понимает, что состав изменился и <c>settingsVersion</c> пора поднимать.
    /// </summary>
    public IList<string> GeneratedVarNames { get; } = new List<string>();

    /// <summary>Версия шаблона на момент последней генерации; <c>null</c> — ещё не генерировали.</summary>
    public int? GeneratedSettingsVersion { get; set; }

    /// <summary>Заготовка документа под конкретный проект.</summary>
    /// <param name="project">Проект мода.</param>
    /// <returns>Документ с пустой формой и заполненными linkage и заголовком.</returns>
    public static SettingsDesign ForProject(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        return new SettingsDesign
        {
            Code = SettingsCodeOptions.ForProject(project),
            Template = new SettingsTemplate
            {
                Linkage = project.ModId,
                ModDisplayName = string.IsNullOrWhiteSpace(project.DisplayName) ? project.ModId : project.DisplayName,
                SettingsVersion = 1,
                HasEnabled = true,
                EnabledDefault = true
            }
        };
    }

    /// <summary>Записывает документ в JSON с отступами.</summary>
    /// <returns>Текст файла.</returns>
    public string ToJson()
    {
        var map = new List<KeyValuePair<string, SettingValue>>
        {
            Pair(DesignVersionKey, SettingValue.Of((long)DesignVersion)),
            Pair(TemplateJson.LinkageKey, SettingValue.Of(Template.Linkage)),
            Pair(CodeKey, SettingValue.Map(
            [
                Pair("packageName", SettingValue.Of(Code.PackageName)),
                Pair("logTag", SettingValue.Of(Code.LogTag)),
                Pair("configRelativePath", SettingValue.Of(Code.ConfigRelativePath)),
                Pair("schemaModuleName", SettingValue.Of(Code.SchemaModuleName)),
                Pair("userModuleName", SettingValue.Of(Code.UserModuleName))
            ])),
            Pair("generatedVarNames", SettingValue.List(GeneratedVarNames.Select(n => SettingValue.Of(n))))
        };

        if (GeneratedSettingsVersion is int generated)
            map.Add(Pair("generatedSettingsVersion", SettingValue.Of((long)generated)));

        map.Add(Pair(TemplateKey, TemplateJson.ToValue(Template)));

        return SettingValue.Map(map).ToJson(indented: true, sortKeys: false);
    }

    /// <summary>Читает документ из JSON.</summary>
    /// <param name="json">Текст файла.</param>
    /// <returns>Документ.</returns>
    /// <exception cref="System.Text.Json.JsonException">Текст не разбирается как JSON.</exception>
    public static SettingsDesign FromJson(string json)
    {
        SettingValue root = SettingValue.ParseJson(json);
        var design = new SettingsDesign();

        if (root.Get(DesignVersionKey) is { IsNumeric: true } version)
            design.DesignVersion = (int)version.AsInteger;

        string linkage = root.Get(TemplateJson.LinkageKey)?.AsString ?? string.Empty;

        SettingValue? code = root.Get(CodeKey);
        if (code is { Kind: SettingValueKind.Map })
        {
            SettingsCodeOptions d = SettingsCodeOptions.Default;
            design.Code = new SettingsCodeOptions
            {
                PackageName = code.Get("packageName")?.AsString ?? d.PackageName,
                LogTag = code.Get("logTag")?.AsString ?? d.LogTag,
                ConfigRelativePath = code.Get("configRelativePath")?.AsString ?? d.ConfigRelativePath,
                SchemaModuleName = code.Get("schemaModuleName")?.AsString ?? d.SchemaModuleName,
                UserModuleName = code.Get("userModuleName")?.AsString ?? d.UserModuleName
            };
        }

        if (root.Get("generatedVarNames") is { Kind: SettingValueKind.List } names)
        {
            foreach (SettingValue n in names.Items)
                if (n.AsString is { Length: > 0 } name) design.GeneratedVarNames.Add(name);
        }

        if (root.Get("generatedSettingsVersion") is { IsNumeric: true } gsv)
            design.GeneratedSettingsVersion = (int)gsv.AsInteger;

        SettingValue? template = root.Get(TemplateKey);
        design.Template = template is null
            ? new SettingsTemplate { Linkage = linkage }
            : TemplateJson.FromValue(template, linkage);

        return design;
    }

    /// <summary>Глубокая копия документа.</summary>
    /// <returns>Новый документ.</returns>
    public SettingsDesign Clone()
    {
        var copy = new SettingsDesign
        {
            DesignVersion = DesignVersion,
            Template = Template.Clone(),
            Code = Code with { },
            GeneratedSettingsVersion = GeneratedSettingsVersion
        };
        foreach (string name in GeneratedVarNames) copy.GeneratedVarNames.Add(name);
        return copy;
    }

    private static KeyValuePair<string, SettingValue> Pair(string key, SettingValue value) => new(key, value);
}

/// <summary>Итог применения формы к проекту мода.</summary>
/// <param name="Code">Что сгенерировали; <c>null</c> — до генерации дело не дошло.</param>
/// <param name="Writes">Что и как записали на диск.</param>
/// <param name="DesignFilePath">Путь к <c>settings.design.json</c>.</param>
/// <param name="Errors">Причины отказа; пусто — всё прошло.</param>
public sealed record SettingsApplyResult(
    GeneratedSettingsCode? Code,
    IReadOnlyList<RegionWriteResult> Writes,
    string DesignFilePath,
    IReadOnlyList<string> Errors)
{
    /// <summary>Всё записано и ничего не сломано.</summary>
    public bool Success => Errors.Count == 0 && Writes.Count > 0 && Writes.All(w => w.Ok);

    /// <summary>Отказ без единой записи.</summary>
    /// <param name="designPath">Путь к документу дизайнера.</param>
    /// <param name="errors">Причины.</param>
    /// <returns>Результат.</returns>
    public static SettingsApplyResult Failed(string designPath, params string[] errors) =>
        new(null, [], designPath, errors);

    /// <summary>Готовая к показу сводка.</summary>
    public string Summary =>
        Errors.Count > 0
            ? "дизайнер настроек: " + string.Join("; ", Errors)
            : "дизайнер настроек: " + string.Join(", ",
                Writes.Select(w => $"{Path.GetFileName(w.Path)} — {Describe(w.Outcome)}"));

    private static string Describe(RegionWriteOutcome outcome) => outcome switch
    {
        RegionWriteOutcome.Created => "создан",
        RegionWriteOutcome.Updated => "обновлён",
        RegionWriteOutcome.Unchanged => "без изменений",
        RegionWriteOutcome.Skipped => "оставлен как есть",
        RegionWriteOutcome.Conflict => "КОНФЛИКТ",
        _ => "ОШИБКА"
    };
}

/// <summary>
/// Дизайнер настроек: хранение формы в проекте мода и её превращение в рабочий код.
///
/// <para>Служба ничего не знает про WPF и умеет ровно три вещи: прочитать
/// <c>settings.design.json</c>, записать его обратно и разложить сгенерированные модули по
/// каталогу пакета мода. Всё остальное (палитра, превью, перетаскивание) — дело UI.</para>
/// </summary>
public interface ISettingsDesignerService
{
    /// <summary>Путь к документу дизайнера в проекте.</summary>
    /// <param name="project">Проект.</param>
    /// <returns>Абсолютный путь к <c>settings.design.json</c>.</returns>
    string DesignFilePath(ModProject project);

    /// <summary>Каталог python-пакета мода, куда кладутся сгенерированные модули.</summary>
    /// <param name="project">Проект.</param>
    /// <returns>Абсолютный путь; пустая строка, если у мода нет пакета.</returns>
    string PackageDirectory(ModProject project);

    /// <summary>В проекте уже есть документ дизайнера.</summary>
    /// <param name="project">Проект.</param>
    /// <returns>Признак.</returns>
    bool HasDesign(ModProject project);

    /// <summary>
    /// Читает документ дизайнера. Не бросает: битый JSON — это <c>null</c> и текст ошибки,
    /// а не исключение посреди открытия проекта.
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <param name="error">Причина, если вернулся <c>null</c>.</param>
    /// <returns>Документ либо <c>null</c>, если файла нет или он не читается.</returns>
    SettingsDesign? Load(ModProject project, out string error);

    /// <summary>Записывает документ дизайнера в проект.</summary>
    /// <param name="project">Проект.</param>
    /// <param name="design">Документ.</param>
    /// <returns>Пустая строка при успехе, иначе текст ошибки.</returns>
    string Save(ModProject project, SettingsDesign design);

    /// <summary>
    /// Генерирует модули и раскладывает их по проекту: <c>settings_schema.py</c> перезаписывается
    /// по маркерам, <c>settings.py</c> создаётся один раз.
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <param name="design">Документ дизайнера.</param>
    /// <param name="saveDesign">Заодно сохранить <c>settings.design.json</c>.</param>
    /// <returns>Итог; исключения наружу не выпускаются.</returns>
    SettingsApplyResult Apply(ModProject project, SettingsDesign design, bool saveDesign = true);

    /// <summary>
    /// Надо ли поднять <c>settingsVersion</c>: состав настроек изменился с прошлой генерации.
    /// </summary>
    /// <param name="design">Документ дизайнера.</param>
    /// <param name="reason">Что именно изменилось и чем это грозит.</param>
    /// <returns><c>true</c>, если версию пора поднимать.</returns>
    bool NeedsVersionBump(SettingsDesign design, out string reason);

    /// <summary>
    /// Тело мода уже зовёт <c>settings.register()</c>. Если нет — UI обязан показать,
    /// что именно дописать: сам дизайнер в <c>main.py</c> не лезет.
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <param name="design">Документ дизайнера.</param>
    /// <returns>Признак.</returns>
    bool IsWiredUp(ModProject project, SettingsDesign design);
}
