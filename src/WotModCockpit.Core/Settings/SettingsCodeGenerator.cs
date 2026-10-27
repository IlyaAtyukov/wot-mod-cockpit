using System.Globalization;
using System.Reflection;
using System.Text;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.Core.Settings;

/// <summary>
/// Параметры генерации: всё, что относится к КОНКРЕТНОМУ проекту мода, а не к форме.
/// Шаблон (<see cref="SettingsTemplate"/>) переносим между проектами, эти значения — нет.
/// </summary>
public sealed record SettingsCodeOptions
{
    /// <summary>Значения по умолчанию — ровно те токены, что стоят в шаблоне-ресурсе.</summary>
    public static SettingsCodeOptions Default { get; } = new();

    /// <summary>Имя python-пакета мода (<c>gui.mods.&lt;PackageName&gt;</c>).</summary>
    public string PackageName { get; init; } = "my_mod";

    /// <summary>Тег лога БЕЗ скобок (<c>MyMod</c>): по нему кокпит красит «свои» строки python.log.</summary>
    public string LogTag { get; init; } = "MyMod";

    /// <summary>
    /// Путь к своему JSON со значениями ОТНОСИТЕЛЬНО корня игры, прямыми слэшами.
    ///
    /// <para>Файл намеренно отдельный от <c>config.json</c> рецепта 09: там лежат параметры,
    /// которые задаёт разработчик, здесь — значения, которые крутит игрок в окне. Один файл
    /// на двоих означал бы два источника умолчаний, а генератор перезаписывал бы то, что
    /// человек правил руками.</para>
    /// </summary>
    public string ConfigRelativePath { get; init; } = "mods/configs/my_mod/settings.json";

    /// <summary>Имя генерируемого модуля со схемой формы, без расширения.</summary>
    public string SchemaModuleName { get; init; } = "settings_schema";

    /// <summary>Имя пользовательского модуля, без расширения.</summary>
    public string UserModuleName { get; init; } = "settings";

    /// <summary>Имя файла схемы.</summary>
    public string SchemaFileName => SchemaModuleName + ".py";

    /// <summary>Имя пользовательского файла.</summary>
    public string UserFileName => UserModuleName + ".py";

    /// <summary>Собирает параметры из паспорта проекта мода.</summary>
    /// <param name="project">Проект.</param>
    /// <returns>Параметры генерации.</returns>
    public static SettingsCodeOptions ForProject(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);

        string package = string.IsNullOrWhiteSpace(project.PackageName)
            ? Default.PackageName
            : project.PackageName.Trim();

        // LogTag в паспорте хранится префиксом с открывающей скобкой ('[HelloTanker'),
        // чтобы ловить и '[HelloTanker]', и '[HelloTanker/loader]'. В коде нужна голая метка.
        string tag = (project.LogTag ?? string.Empty).Trim().TrimStart('[').TrimEnd(']').Trim();
        if (tag.Length == 0) tag = NameConventions.ToPascalCase(package);
        if (tag.Length == 0) tag = Default.LogTag;

        return new SettingsCodeOptions
        {
            PackageName = package,
            LogTag = tag,
            ConfigRelativePath = $"mods/configs/{package}/settings.json"
        };
    }

    /// <summary>Проверяет сами параметры (форму проверяет <see cref="TemplateValidator"/>).</summary>
    /// <returns>Причины, по которым генерировать нельзя; пусто — всё в порядке.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!IsPythonIdentifier(PackageName))
            errors.Add($"имя пакета '{PackageName}' — не идентификатор Python: модуль настроек некуда положить.");

        if (!IsPythonIdentifier(SchemaModuleName))
            errors.Add($"имя модуля схемы '{SchemaModuleName}' — не идентификатор Python.");

        if (!IsPythonIdentifier(UserModuleName))
            errors.Add($"имя модуля настроек '{UserModuleName}' — не идентификатор Python.");

        if (string.Equals(SchemaModuleName, UserModuleName, StringComparison.Ordinal))
            errors.Add("модуль схемы и пользовательский модуль не могут называться одинаково.");

        if (string.IsNullOrWhiteSpace(ConfigRelativePath))
            errors.Add("не задан путь к своему JSON со значениями.");
        else if (Path.IsPathRooted(ConfigRelativePath) || ConfigRelativePath.Contains("..", StringComparison.Ordinal))
            errors.Add($"путь к своему JSON '{ConfigRelativePath}' обязан быть ОТНОСИТЕЛЬНЫМ корня игры и без '..'.");

        return errors;
    }

    private static bool IsPythonIdentifier(string? s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!char.IsAsciiLetter(s[0]) && s[0] != '_') return false;
        foreach (char ch in s) if (!char.IsAsciiLetterOrDigit(ch) && ch != '_') return false;
        return true;
    }
}

/// <summary>Сгенерированные тексты и всё, что о них надо знать вызывающему.</summary>
/// <param name="Options">Параметры, с которыми генерировали.</param>
/// <param name="SchemaFileName">Имя файла схемы, например <c>settings_schema.py</c>.</param>
/// <param name="SchemaRegionBody">Тело генерируемого участка схемы — БЕЗ маркеров и пролога.</param>
/// <param name="SchemaModuleCode">Полный текст файла схемы для первого создания.</param>
/// <param name="UserFileName">Имя пользовательского файла, например <c>settings.py</c>.</param>
/// <param name="UserModuleCode">Полный текст пользовательского файла (создаётся один раз).</param>
/// <param name="Validation">Находки проверки шаблона.</param>
public sealed record GeneratedSettingsCode(
    SettingsCodeOptions Options,
    string SchemaFileName,
    string SchemaRegionBody,
    string SchemaModuleCode,
    string UserFileName,
    string UserModuleCode,
    TemplateValidationResult Validation)
{
    /// <summary>
    /// Хвост, который дописывается ПОД закрывающим маркером при первом создании файла схемы.
    /// Дальше он принадлежит человеку и при перегенерации сохраняется.
    /// </summary>
    public string SchemaInitialTail { get; init; } = string.Empty;

    /// <summary>Ошибок в форме нет — код можно класть в проект.</summary>
    public bool IsValid => !Validation.HasErrors;

    /// <summary>
    /// Фрагмент, который надо добавить в тело мода, чтобы настройки заработали.
    /// Дизайнер его НЕ вставляет сам: <c>main.py</c> — файл пользователя, а вызов обязан стоять
    /// внутри <c>onAccountShowGUI</c>. Единственный механизм вставки, который есть в кокпите,
    /// дописывает блок в КОНЕЦ файла — то есть положил бы вызов на уровень модуля, где он
    /// выполнился бы при импорте. Это ровно то, что запрещает WOT001.
    /// </summary>
    public string IntegrationSnippet =>
        $"from . import {Options.UserModuleName}\n" +
        "\n" +
        "\n" +
        "def onAccountShowGUI(ctx):\n" +
        "    # Именно здесь, а не в init(): порядок init() у модов задаётся сортировкой имён\n" +
        "    # mod_*.pyc и меняется от установки любого другого мода, а окно настроек стирает\n" +
        "    # состояние модов, не успевших зарегистрироваться до его открытия.\n" +
        $"    {Options.UserModuleName}.register()\n" +
        $"    {Options.UserModuleName}.subscribe(_on_settings_changed)\n" +
        "\n" +
        "\n" +
        "def _on_settings_changed(values):\n" +
        "    # values — КОПИЯ словаря настроек; зовётся после каждого «Применить».\n" +
        "    pass\n";
}

/// <summary>
/// Превращает шаблон дизайнера в рабочий модуль Python 2.7 для ModsSettingsAPI 1.6.4.
///
/// <para><b>Два файла, а не один.</b> <c>settings_schema.py</c> — целиком машинный,
/// переписывается при каждом сохранении формы (по маркерам
/// <see cref="GeneratedRegionWriter"/>, так что дописанное пользователем ниже участка живёт).
/// <c>settings.py</c> — создаётся ОДИН раз и дальше принадлежит человеку. Так перегенерация
/// формы никогда не наступает на правки.</para>
///
/// <para><b>Шаблон печатается литеральными словарями, а не вызовами
/// <c>gui.modsSettingsApi.templates.createX</c>.</b> Результат тот же самый — фабрики только
/// собирают такой же <c>dict</c>, — зато файл схемы вообще не импортирует чужой мод. Это не
/// косметика: импорт <c>gui.modsSettingsApi</c> в модуле, который зовут не из-под
/// <c>try/except</c>, — находка WOT018, а весь смысл дизайнера в том, чтобы мод работал и без
/// установленного ModsSettingsAPI.</para>
///
/// <para><b>Форма шаблона берётся у модели, а не печатается на глаз.</b> Каждый компонент
/// проходит через <see cref="TemplateJson.ControlToValue"/> — тот же метод, которым модель
/// собирает канонический JSON. Значит напечатанный питон и канонический JSON шаблона
/// не могут разъехаться: ни лишнего ключа, ни потерянного <c>itemRenderer</c>, пришедшего
/// импортом. Единственная подмена — поле <c>value</c>: вместо литерала печатается
/// <c>_v(values, 'varName')</c>, чтобы шаблон строился ОТ СОХРАНЁННЫХ значений игрока.</para>
///
/// <para><b>Замыканий в порождённом коде нет намеренно.</b> Естественная запись помощника —
/// вложенная <c>def v(name)</c>, читающая <c>values</c> из объемлющей области. Но при сборке
/// с <c>--compile-all</c> вложенные модули компилирует <c>tools/py27c.py</c>, у которого
/// замыканий нет вовсе (<c>_check_no_closure</c>), и сборка упала бы, не сославшись на
/// дизайнер. Поэтому помощник вынесен на уровень модуля: <c>_v(values, name)</c>.</para>
/// </summary>
public static class SettingsCodeGenerator
{
    /// <summary>Имя встроенного ресурса с каркасом пользовательского модуля.</summary>
    private const string TemplateResource = "WotModCockpit.Core.Settings.SettingsCodeTemplate.py";

    /// <summary>Токены каркаса — ровно те, что стоят в <c>SettingsCodeTemplate.py</c>.</summary>
    private const string TokenPackage = "my_mod";
    private const string TokenLogTag = "MyMod";
    private const string TokenSchemaModule = "settings_schema";

    /// <summary>Насколько длинной может быть строка, которую ещё печатаем в одну строчку.</summary>
    private const int InlineWidth = 78;

    /// <summary>
    /// Ключи, чьи строковые значения обязаны быть БАЙТОВОЙ <c>str</c>, а не <c>unicode</c>.
    /// <c>varName</c> становится ключом словаря настроек, <c>type</c> сравнивается окном
    /// со строковыми константами, <c>iconSource</c> — имя класса ресурса. Всё остальное
    /// текстовое печатается как <c>u'…'</c>: не-ASCII без префикса <c>u</c> в Python 2 даёт
    /// UnicodeDecodeError в GUI и потерянную запись в логе (правило WOT017).
    /// </summary>
    private static readonly HashSet<string> ByteStringKeys = new(StringComparer.Ordinal)
    {
        TemplateJson.TypeKey, TemplateJson.VarNameKey, TemplateJson.ButtonIconSourceKey
    };

    private static readonly Lazy<string> LazyTemplate = new(ReadTemplate, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Каркас пользовательского модуля как есть, без подстановок.</summary>
    /// <remarks>Открыт наружу, чтобы тест мог прогнать сам ресурс через валидатор Python 2.7.</remarks>
    public static string RawUserModuleTemplate => LazyTemplate.Value;

    // ------------------------------------------------------------------ генерация

    /// <summary>
    /// Генерирует оба модуля. Не бросает: проблемы формы приходят в
    /// <see cref="GeneratedSettingsCode.Validation"/>.
    /// </summary>
    /// <param name="template">Шаблон формы.</param>
    /// <param name="options">Параметры проекта; <c>null</c> — значения по умолчанию.</param>
    /// <returns>Тексты и находки.</returns>
    public static GeneratedSettingsCode Generate(SettingsTemplate template, SettingsCodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        SettingsCodeOptions opt = options ?? SettingsCodeOptions.Default;

        string body = BuildSchemaRegion(template, opt);
        string tail = UserTailHint(opt);

        return new GeneratedSettingsCode(
            opt,
            opt.SchemaFileName,
            body,
            GeneratedRegionWriter.Compose(GeneratedRegionWriter.PythonPrologue, body, tail),
            opt.UserFileName,
            BuildUserModule(opt),
            TemplateValidator.Validate(template))
        {
            SchemaInitialTail = tail
        };
    }

    /// <summary>Собирает тело генерируемого участка <c>settings_schema.py</c> (без маркеров).</summary>
    /// <param name="template">Шаблон.</param>
    /// <param name="options">Параметры; <c>null</c> — по умолчанию.</param>
    /// <returns>Текст модуля схемы.</returns>
    public static string BuildSchemaRegion(SettingsTemplate template, SettingsCodeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        SettingsCodeOptions opt = options ?? SettingsCodeOptions.Default;

        var sb = new StringBuilder(8192);

        sb.Append(SchemaDocstring(opt));
        sb.Append('\n');

        sb.Append("LINKAGE = ").Append(Py.Str(template.Linkage)).Append('\n');
        sb.Append("SETTINGS_VERSION = ")
          .Append(template.SettingsVersion is int v ? v.ToString(CultureInfo.InvariantCulture) : "None")
          .Append('\n');
        sb.Append("MOD_DISPLAY_NAME = ").Append(Py.Uni(template.ModDisplayName)).Append('\n');
        sb.Append("HAS_ENABLED = ").Append(Py.Bool(template.HasEnabled)).Append('\n');
        sb.Append('\n');

        AppendDefaults(sb, template);
        sb.Append('\n');
        AppendVarTypes(sb, template);
        sb.Append('\n');
        AppendVarNames(sb, template);
        sb.Append("\n\n");

        sb.Append(
            "def _v(values, name):\n" +
            "    \"\"\"Текущее значение настройки или заводское, если ключа нет.\n" +
            "\n" +
            "    Ключа может не быть по двум причинам: игрок ещё ни разу не открывал окно\n" +
            "    настроек, либо состав контролов изменили, не подняв SETTINGS_VERSION, и\n" +
            "    ModsSettingsAPI отдал словарь по СТАРОМУ шаблону.\n" +
            "\n" +
            "    Помощник вынесен на уровень модуля, а не сделан вложенной функцией: при\n" +
            "    сборке с --compile-all вложенные модули компилирует tools/py27c.py, а он\n" +
            "    замыканий не поддерживает и уронил бы сборку.\n" +
            "    \"\"\"\n" +
            "    if name in values:\n" +
            "        return values[name]\n" +
            "    return DEFAULTS[name]\n" +
            "\n" +
            "\n");

        AppendBuildTemplate(sb, template);

        return sb.ToString();
    }

    /// <summary>Собирает полный текст пользовательского модуля <c>settings.py</c>.</summary>
    /// <param name="options">Параметры; <c>null</c> — по умолчанию.</param>
    /// <returns>Текст модуля.</returns>
    public static string BuildUserModule(SettingsCodeOptions? options = null)
    {
        SettingsCodeOptions opt = options ?? SettingsCodeOptions.Default;

        // ОДИН проход по всем токенам сразу: цепочка Replace бьёт по своему же результату
        // (пакет с именем 'settings' после первой замены перестал бы совпадать сам с собой).
        string text = NameConventions.ReplaceTokensOnce(
            RawUserModuleTemplate,
            [
                (TokenSchemaModule, opt.SchemaModuleName),
                (TokenPackage, opt.PackageName),
                (TokenLogTag, opt.LogTag)
            ],
            out _);

        return ReplaceConfigPath(text, opt.ConfigRelativePath);
    }

    // ------------------------------------------------------------------ печать схемы

    private static string SchemaDocstring(SettingsCodeOptions opt) =>
        "\"\"\"СГЕНЕРИРОВАНО ДИЗАЙНЕРОМ НАСТРОЕК WoT Mod Cockpit. НЕ РЕДАКТИРОВАТЬ РУКАМИ.\n" +
        "\n" +
        "Всё, что стоит МЕЖДУ маркерами cockpit-generated, переписывается целиком при каждом\n" +
        "сохранении формы в дизайнере. Свой код пишите либо ПОСЛЕ закрывающего маркера (он\n" +
        $"сохраняется), либо в {opt.UserFileName} — его дизайнер не трогает вообще.\n" +
        "\n" +
        "Источник истины формы — settings.design.json в корне проекта мода.\n" +
        "\n" +
        "ПОДНИМАЙТЕ SETTINGS_VERSION, КОГДА МЕНЯЕТЕ СОСТАВ КОНТРОЛОВ.\n" +
        "compareTemplates в ModsSettingsAPI сравнивает шаблоны так: если settingsVersion есть\n" +
        "в обоих, шаблон заменяется только при РОСТЕ версии; иначе сравнивается весь JSON.\n" +
        "Отсюда два следствия, оба проверены на живом modsettings.dat:\n" +
        "  * не подняли версию — новый контрол в окне НЕ ПОЯВИТСЯ, а его ключа не будет\n" +
        "    в словаре, который вернёт setModTemplate;\n" +
        "  * подняли версию — настройки игрока по ВСЕМ ключам сбрасываются на значения из\n" +
        "    шаблона. Здесь это безопасно: build_template() строит шаблон ОТ текущих значений,\n" +
        "    поэтому сброс возвращает игрока к его же выбору, а не к заводским.\n" +
        "\n" +
        "Шаблон печатается литеральными словарями, а не вызовами templates.createX: результат\n" +
        "тот же самый (фабрики лишь собирают такой же dict), зато этот файл не импортирует\n" +
        "чужой мод и остаётся рабочим, когда ModsSettingsAPI не установлен.\n" +
        "\"\"\"\n";

    private static void AppendDefaults(StringBuilder sb, SettingsTemplate template)
    {
        sb.Append(
            "# varName -> ЗАВОДСКОЕ значение. Единственный источник умолчаний в моде: ровно этот\n" +
            "# словарь ModsSettingsAPI выведет из шаблона (api.getSettingsFromTemplate), он же\n" +
            "# лежит в основе своего JSON. Второго списка умолчаний заводить нельзя — разъедутся.\n");

        List<(string Key, string Value)> pairs =
        [
            .. template.BuildDefaults()
                .Select(d => (Py.Str(d.Key), Render(d.Value, 4, TemplateJson.ValueKey)))
        ];

        AppendBlockDict(sb, "DEFAULTS", pairs);
    }

    private static void AppendVarTypes(StringBuilder sb, SettingsTemplate template)
    {
        sb.Append("# varName -> тип контрола. Нужен, чтобы приводить значения, пришедшие из JSON.\n");

        var pairs = new List<(string Key, string Value)>();
        if (template.HasEnabled)
            pairs.Add((Py.Str(TemplateJson.EnabledKey), Py.Str(ControlKinds.WireName(ControlKind.CheckBox))));

        foreach (SettingControl c in template.AllControls)
        {
            if (!c.ContributesSetting) continue;
            pairs.Add((Py.Str(c.VarName!), Py.Str(c.TypeName)));
        }

        AppendBlockDict(sb, "VAR_TYPES", pairs);
    }

    private static void AppendVarNames(StringBuilder sb, SettingsTemplate template)
    {
        sb.Append("# Имена настроек в порядке обхода колонок, без служебного 'enabled'.\n");

        List<SettingControl> controls = [.. template.AllControls.Where(c => c.ContributesSetting)];
        if (controls.Count == 0)
        {
            sb.Append("VAR_NAMES = ()\n");
            return;
        }

        sb.Append("VAR_NAMES = (\n");
        foreach (SettingControl c in controls) sb.Append("    ").Append(Py.Str(c.VarName!)).Append(",\n");
        sb.Append(")\n");
    }

    private static void AppendBlockDict(StringBuilder sb, string name, List<(string Key, string Value)> pairs)
    {
        if (pairs.Count == 0)
        {
            sb.Append(name).Append(" = {}\n");
            return;
        }

        sb.Append(name).Append(" = {\n");
        foreach ((string key, string value) in pairs) sb.Append("    ").Append(key).Append(": ").Append(value).Append(",\n");
        sb.Append("}\n");
    }

    private static void AppendBuildTemplate(StringBuilder sb, SettingsTemplate template)
    {
        sb.Append(
            "def build_template(values):\n" +
            "    \"\"\"Шаблон окна настроек, собранный ОТ ТЕКУЩИХ значений игрока.\n" +
            "\n" +
            "    values — словарь varName -> значение (обычно прочитанный из своего JSON).\n" +
            "    Именно поэтому сброс настроек, который API устраивает при замене шаблона,\n" +
            "    возвращает игрока к его же выбору, а не к заводским значениям автора.\n" +
            "    \"\"\"\n" +
            "    return {\n");

        sb.Append("        ").Append(Py.Str(TemplateJson.ModDisplayNameKey)).Append(": MOD_DISPLAY_NAME,\n");
        if (template.SettingsVersion is not null)
            sb.Append("        ").Append(Py.Str(TemplateJson.SettingsVersionKey)).Append(": SETTINGS_VERSION,\n");
        if (template.HasEnabled)
            sb.Append("        ").Append(Py.Str(TemplateJson.EnabledKey))
              .Append(": _v(values, ").Append(Py.Str(TemplateJson.EnabledKey)).Append("),\n");

        AppendColumn(sb, TemplateJson.Column1Key, template.Column1);
        AppendColumn(sb, TemplateJson.Column2Key, template.Column2);

        // Посторонние ключи верхнего уровня — те, что пришли импортом чужого шаблона.
        // Дизайнер их не создаёт, но и не теряет: их отсутствие изменило бы JSON шаблона,
        // а это без settingsVersion равносильно сбросу настроек игрока.
        foreach (string key in template.ExtraOrder)
            sb.Append("        ").Append(Py.Str(key)).Append(": ").Append(Render(template.Extra[key], 8, key)).Append(",\n");

        sb.Append("    }\n");
    }

    private static void AppendColumn(StringBuilder sb, string name, IList<SettingControl> controls)
    {
        if (controls.Count == 0)
        {
            sb.Append("        ").Append(Py.Str(name)).Append(": [],\n");
            return;
        }

        sb.Append("        ").Append(Py.Str(name)).Append(": [\n");
        foreach (SettingControl c in controls) sb.Append("            ").Append(RenderControl(c, 12)).Append(",\n");
        sb.Append("        ],\n");
    }

    /// <summary>
    /// Печатает один компонент. Форма словаря берётся у модели
    /// (<see cref="TemplateJson.ControlToValue"/>) — значит напечатанный питон не может
    /// разойтись с каноническим JSON шаблона. Подменяется ровно одно поле: <c>value</c>.
    /// </summary>
    private static string RenderControl(SettingControl control, int indent)
    {
        SettingValue map = TemplateJson.ControlToValue(control);
        var pairs = new List<(string Key, string Value)>(map.Entries.Count);

        foreach (KeyValuePair<string, SettingValue> e in map.Entries)
        {
            string rendered = e.Key == TemplateJson.ValueKey && control.ContributesSetting
                ? $"_v(values, {Py.Str(control.VarName!)})"
                : Render(e.Value, indent + 4, e.Key);

            pairs.Add((Py.Str(e.Key), rendered));
        }

        return RenderPairs(pairs, indent);
    }

    /// <summary>Печатает значение модели питоновским литералом.</summary>
    /// <param name="value">Значение.</param>
    /// <param name="indent">Отступ, с которого начинаются вложенные строки.</param>
    /// <param name="key">Имя ключа — по нему решается, <c>str</c> это или <c>unicode</c>.</param>
    /// <returns>Литерал; может быть многострочным.</returns>
    private static string Render(SettingValue value, int indent, string? key)
    {
        switch (value.Kind)
        {
            case SettingValueKind.Null:
                return "None";

            case SettingValueKind.Bool:
                return Py.Bool(value.AsBool);

            case SettingValueKind.Integer:
            case SettingValueKind.Number:
                // Текст числа берётся у модели: она хранит исходную форму ('3' против '3.0'),
                // а без settingsVersion API сравнивает шаблоны как ТЕКСТ — подмена целого
                // дробным молча сбросила бы настройки игрока.
                return value.NumberText();

            case SettingValueKind.String:
                return key is not null && ByteStringKeys.Contains(key)
                    ? Py.Str(value.AsString)
                    : Py.Uni(value.AsString);

            case SettingValueKind.List:
                return RenderList(value, indent, key);

            case SettingValueKind.Map:
                return RenderPairs(
                    [.. value.Entries.Select(e => (Py.Str(e.Key), Render(e.Value, indent + 4, e.Key)))],
                    indent);

            default:
                return "None";
        }
    }

    private static string RenderList(SettingValue value, int indent, string? key)
    {
        if (value.Items.Count == 0) return "[]";

        string[] rendered = [.. value.Items.Select(i => Render(i, indent + 4, key))];

        int inline = rendered.Sum(r => r.Length + 2) + indent + 2;
        if (inline <= InlineWidth && !rendered.Any(r => r.Contains('\n', StringComparison.Ordinal)))
        {
            return "[" + string.Join(", ", rendered) + "]";
        }

        var sb = new StringBuilder();
        string pad = new(' ', indent + 4);
        sb.Append("[\n");
        foreach (string item in rendered) sb.Append(pad).Append(item).Append(",\n");
        sb.Append(new string(' ', indent)).Append(']');
        return sb.ToString();
    }

    private static string RenderPairs(List<(string Key, string Value)> pairs, int indent)
    {
        if (pairs.Count == 0) return "{}";

        int inline = pairs.Sum(p => p.Key.Length + p.Value.Length + 4) + indent + 2;
        if (inline <= InlineWidth && !pairs.Any(p => p.Value.Contains('\n', StringComparison.Ordinal)))
        {
            return "{" + string.Join(", ", pairs.Select(p => p.Key + ": " + p.Value)) + "}";
        }

        var sb = new StringBuilder();
        string pad = new(' ', indent + 4);
        sb.Append("{\n");
        foreach ((string k, string v) in pairs) sb.Append(pad).Append(k).Append(": ").Append(v).Append(",\n");
        sb.Append(new string(' ', indent)).Append('}');
        return sb.ToString();
    }

    private static string UserTailHint(SettingsCodeOptions opt) =>
        "\n" +
        "# Всё, что вы допишете НИЖЕ этой строки, дизайнер сохранит при перегенерации.\n" +
        $"# Но обычно свой код удобнее держать в {opt.UserFileName}.\n";

    // ------------------------------------------------------------------ мелочи

    private static string ReplaceConfigPath(string text, string relativePath)
    {
        const string key = "CONFIG_RELATIVE_PATH = ";
        int at = text.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return text;

        int end = text.IndexOf('\n', at);
        if (end < 0) end = text.Length;

        return string.Concat(text.AsSpan(0, at), key, ConfigJoin(relativePath), text.AsSpan(end));
    }

    /// <summary>
    /// <c>mods/configs/x/settings.json</c> → <c>os.path.join('mods', 'configs', 'x', 'settings.json')</c>.
    /// Именно <c>join</c>, а не строка с разделителями: литерал с обратными слэшами в Python 2 —
    /// это набор escape-последовательностей, и один <c>\n</c> в имени каталога рушит путь молча.
    /// </summary>
    private static string ConfigJoin(string relativePath)
    {
        string[] parts = (relativePath ?? string.Empty)
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 0
            ? "os.path.join('mods', 'configs', 'settings.json')"
            : "os.path.join(" + string.Join(", ", parts.Select(Py.Str)) + ")";
    }

    private static string ReadTemplate()
    {
        Assembly asm = typeof(SettingsCodeGenerator).Assembly;
        Stream? s = asm.GetManifestResourceStream(TemplateResource);

        if (s is null)
        {
            // Логическое имя ресурса собирается MSBuild'ом из RootNamespace и пути и меняется
            // от одной только правки RootNamespace — ищем по суффиксу, как это делает RecipeLibrary.
            string? match = Array.Find(asm.GetManifestResourceNames(),
                n => n.EndsWith(".SettingsCodeTemplate.py", StringComparison.OrdinalIgnoreCase));
            if (match is not null) s = asm.GetManifestResourceStream(match);
        }

        if (s is null)
        {
            throw new InvalidOperationException(
                "Встроенный ресурс SettingsCodeTemplate.py не найден. Проверьте " +
                "<EmbeddedResource Include=\"Settings\\SettingsCodeTemplate.py\" /> в WotModCockpit.Core.csproj.");
        }

        using var reader = new StreamReader(s, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    /// <summary>
    /// Печать литералов Python 2.7. Разделение <c>str</c>/<c>unicode</c> здесь не педантизм:
    /// <c>linkage</c> и <c>varName</c> обязаны быть байтовыми <c>str</c> (иначе <c>str.lower</c>
    /// в <c>generateSettingsData</c> бросит TypeError и окно не откроется ни у одного мода),
    /// а любой не-ASCII текст обязан быть <c>unicode</c> (иначе UnicodeDecodeError в GUI и
    /// потерянная строка в логе — правило WOT017).
    /// </summary>
    internal static class Py
    {
        internal static string Bool(bool v) => v ? "True" : "False";

        internal static string Str(string? s) => "'" + Escape(s) + "'";

        internal static string Uni(string? s) => "u'" + Escape(s) + "'";

        private static string Escape(string? s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;

            var sb = new StringBuilder(s.Length + 8);
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\'': sb.Append("\\'"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20 || ch == 0x7F)
                            sb.Append("\\x").Append(((int)ch).ToString("x2", CultureInfo.InvariantCulture));
                        else sb.Append(ch);
                        break;
                }
            }

            return sb.ToString();
        }
    }
}
