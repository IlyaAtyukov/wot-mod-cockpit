using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Settings;
using CoreTemplateJson = WotModCockpit.Core.Settings.TemplateJson;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// Шов дизайнера настроек к настоящему генератору Core
/// (<see cref="SettingsCodeGenerator"/>, <see cref="TemplateValidator"/>,
/// <see cref="TemplateImporter"/>, <see cref="ISettingsDesignerService"/>).
///
/// <para><b>Почему перевод идёт через JSON шаблона, а не через поле-в-поле.</b> У экрана и
/// у Core две независимые модели формы (<see cref="SettingsFormSpec"/> и
/// <see cref="SettingsTemplate"/>), и написать между ними третий, никем не проверенный
/// переводчик означало бы завести третье место, где шаблон может тихо разъехаться.
/// Вместо этого стыком служит ровно тот словарь, который уезжает в <c>modsettings.dat</c>:
/// экран печатает его <see cref="SettingsTemplateJson.Render"/> (структура сверена с 13
/// живыми шаблонами), Core читает его <see cref="CoreTemplateJson.Parse"/> (круговой рейс
/// по тем же 13 живым шаблонам — 13 из 13 без расхождений). Обе стороны уже покрыты
/// тестами на этом самом формате.</para>
///
/// <para><b>Чужие ключи мост не теряет.</b> Ключ, которого дизайнер сам не порождает
/// (<c>itemRenderer</c> у двух живых выпадающих списков, <c>filename</c> внутри вариантов
/// InfoPanel), едет дальше в <see cref="SettingsControlSpec.Unknown"/> и
/// <see cref="SettingsOptionSpec.Extra"/> — сырым литералом JSON, вместе со своим типом.
/// Это не аккуратность: без <c>settingsVersion</c> API сравнивает шаблоны КАК ТЕКСТ, и
/// выброшенный ключ означает молчаливый сброс настроек игрока. Всё, что мост всё-таки
/// приводит к своему виду, он перечисляет в замечаниях импорта
/// (<c>SettingsDesignFile.FromLiveTemplate</c> сверяет исходный шаблон с обратной печатью
/// и докладывает про каждое расхождение).</para>
///
/// <para><b>Что даёт подключение Core по сравнению со встроенным генератором.</b>
/// Схема пишется УЧАСТКОМ по маркерам <c>cockpit-generated</c>: дописанное человеком ниже
/// закрывающего маркера переживает перегенерацию формы (встроенный генератор переписывал
/// файл целиком). Плюс 39 правил <c>MSA0xx</c> валидатора Core и импорт формы прямо из
/// <c>settings_schema.py</c>, когда <c>settings.design.json</c> в проекте нет.</para>
/// </summary>
public sealed class CoreSettingsDesignerBackend : ISettingsDesignerBackend
{
    /// <summary>
    /// Правила Core, о которых экран и без того докладывает СВОИМИ словами
    /// (см. <c>SettingsDesignerViewModel.Revalidate</c>). Пропускать их сюда — значит
    /// показать пользователю одну и ту же беду дважды, разными формулировками.
    /// </summary>
    private static readonly HashSet<string> CoveredByDesigner = new(StringComparer.Ordinal)
    {
        TemplateRules.BadLinkage,             // MSA001
        TemplateRules.EmptyDisplayName,       // MSA002
        TemplateRules.BadSettingsVersion,     // MSA005
        TemplateRules.EmptyForm,              // MSA006
        TemplateRules.EmptyText,              // MSA011
        TemplateRules.MissingVarName,         // MSA013
        TemplateRules.BadVarName,             // MSA014
        TemplateRules.ReservedVarName,        // MSA015
        TemplateRules.DuplicateVarName,       // MSA016
        TemplateRules.NoOptions,              // MSA020
        TemplateRules.OptionIndexOutOfRange,  // MSA021
        TemplateRules.EmptyOptionLabel,       // MSA022
        TemplateRules.DropdownScroll,         // MSA023
        TemplateRules.BadRange,               // MSA024
        TemplateRules.BadSnapInterval,        // MSA025
        TemplateRules.ValueOutOfRange,        // MSA027
        TemplateRules.FormatWithoutPlaceholder, // MSA028
        TemplateRules.BadColor,               // MSA029
        TemplateRules.ExcludedKey,            // MSA030
        TemplateRules.WidthTooLarge,          // MSA038
        TemplateRules.RangeTooNarrow          // MSA040
    };

    private readonly ISettingsDesignerService _service;

    /// <summary>Создаёт шов.</summary>
    /// <param name="service">Служба дизайнера Core: пути проекта и чтение документа формы.</param>
    public CoreSettingsDesignerBackend(ISettingsDesignerService service)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string UnavailableReason => string.Empty;

    /// <inheritdoc />
    public string Name => "генератор Core · SettingsCodeGenerator";

    /// <summary>
    /// Своей таблицы клавиш у Core нет: коды <c>KEY_*</c> живут в
    /// <see cref="SettingsHotKey.KnownKeys"/> и сняты с <c>client/Keys.py</c>. Пустой список —
    /// это «бери встроенную», ровно как описано в контракте.
    /// </summary>
    public IReadOnlyList<SettingsKeyCode> KnownKeys => Array.Empty<SettingsKeyCode>();

    /// <inheritdoc />
    public IReadOnlyList<SettingsIssue> Validate(SettingsFormSpec form)
    {
        ArgumentNullException.ThrowIfNull(form);

        SettingsTemplate? template = TryToTemplate(form, out _);
        if (template is null)
        {
            // Форма настолько сломана, что даже не печатается. Экран уже сказал об этом
            // своими правилами; повторять нечего.
            return Array.Empty<SettingsIssue>();
        }

        return Translate(TemplateValidator.Validate(template));
    }

    /// <inheritdoc />
    public string RenderTemplateJson(SettingsFormSpec form)
    {
        ArgumentNullException.ThrowIfNull(form);

        SettingsTemplate? template = TryToTemplate(form, out _);

        // Пустая строка — законный ответ контракта: экран покажет собственный рендер.
        return template is null ? string.Empty : CoreTemplateJson.ToJson(template, indented: true, sortKeys: true);
    }

    /// <inheritdoc />
    public SettingsCodeResult Generate(SettingsFormSpec form, SettingsCodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(request);

        SettingsTemplate? template = TryToTemplate(form, out string error);
        if (template is null)
        {
            return SettingsCodeResult.Failed("форма не печатается как шаблон: " + error);
        }

        ModProject project = ToProject(request);
        var options = new SettingsCodeOptions
        {
            PackageName = project.PackageName,
            LogTag = string.IsNullOrWhiteSpace(request.LogTag) ? project.PackageName : request.LogTag.Trim(),
            ConfigRelativePath = string.IsNullOrWhiteSpace(request.ConfigRelativePath)
                ? $"mods/configs/{project.PackageName}/settings.json"
                : request.ConfigRelativePath.Trim().Replace('\\', '/')
        };

        var bad = new List<string>(options.Validate());

        // Столкновение с конфигом мода из рецепта 09. Та же охранка есть в
        // SettingsDesignerService.Apply, но экран зовёт генератор ЧЕРЕЗ ЭТОТ ШОВ и мимо неё
        // проходит. Цена ошибки — не «два источника правды»: settings.py в _save() делает
        // json.dumps(_values), то есть первое же «Применить» игрока стёрло бы config.json
        // со всеми ключами рецепта 09.
        if (SamePath(options.ConfigRelativePath, request.GameConfigRelative))
        {
            bad.Add($"путь к значениям настроек ('{options.ConfigRelativePath}') совпадает с конфигом мода "
                    + "из рецепта 09: модуль настроек переписывает свой файл целиком при каждом «Применить» "
                    + "и стёр бы у игрока конфиг мода. Дайте настройкам отдельный файл, например "
                    + $"mods/configs/{project.PackageName}/settings.json");
        }

        if (bad.Count > 0)
        {
            return SettingsCodeResult.Failed(string.Join("; ", bad));
        }

        GeneratedSettingsCode code = SettingsCodeGenerator.Generate(template, options);

        var design = new SettingsDesign
        {
            Template = template,
            Code = options,
            GeneratedSettingsVersion = template.SettingsVersion
        };

        foreach (SettingControl control in template.AllControls)
        {
            if (control.ContributesSetting && control.VarName is { Length: > 0 } name)
            {
                design.GeneratedVarNames.Add(name);
            }
        }

        string packageRelative = PackageRelativeDir(request, project);
        var issues = new List<SettingsIssue>(Translate(code.Validation));

        var files = new List<SettingsCodeFile>
        {
            new(
                SettingsDesignerService.DesignFileName,
                design.ToJson() + "\n",
                SettingsFileKind.Design)
            {
                Note = "источник истины формы; в .mtmod не попадает"
            },
            new(
                packageRelative + "/" + code.SchemaFileName,
                SchemaContent(project, code, issues),
                SettingsFileKind.Generated)
            {
                Note = "машинный участок между маркерами cockpit-generated; дописанное ниже сохраняется"
            }
        };

        if (request.CreateUserFile)
        {
            files.Add(new SettingsCodeFile(
                packageRelative + "/" + code.UserFileName,
                code.UserModuleCode,
                SettingsFileKind.CreateOnce)
            {
                Note = "создаётся один раз; существующий файл кокпит не трогает"
            });
        }

        // Вызов, без которого форма в игре не появится. В main.py его вставляет ЧЕЛОВЕК:
        // register() обязан стоять внутри onAccountShowGUI, а единственный механизм вставки,
        // который есть у кокпита, дописывает блок в КОНЕЦ файла — то есть положил бы вызов
        // на уровень модуля, где он выполнится при импорте (ровно это запрещает WOT001).
        // Поэтому фрагмент показывается и копируется, но в проект не пишется.
        files.Add(new SettingsCodeFile(
            "вызов из main.py (фрагмент)",
            code.IntegrationSnippet,
            SettingsFileKind.Generated)
        {
            IsSnippet = true,
            Note = "скопируйте в свой main.py; в проект кокпит его не пишет"
        });

        return new SettingsCodeResult(files, issues)
        {
            GeneratorName = Name
        };
    }

    /// <summary>Два относительных пути игры указывают на один файл.</summary>
    private static bool SamePath(string? left, string? right)
    {
        static string Normal(string? value) =>
            (value ?? string.Empty).Trim().Replace('\\', '/').Trim('/');

        string a = Normal(left);
        string b = Normal(right);

        return a.Length > 0 && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc />
    public SettingsImportResult ImportFromProject(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return SettingsImportResult.Failed("проект не выбран");
        }

        var notes = new List<string>();
        string designPath = Path.Combine(projectDir, SettingsDesignerService.DesignFileName);

        if (File.Exists(designPath))
        {
            SettingsImportResult fromDesign = FromDesignFile(designPath, notes);
            if (fromDesign.Error is null)
            {
                return fromDesign;
            }

            notes.Add(fromDesign.Error);
        }

        // Документа дизайнера нет (или он не читается) — пробуем разобрать сам код мода.
        // Это ровно то, ради чего у Core есть TemplateImporter: форма, написанная руками
        // или другим инструментом, всё равно должна открываться в дизайнере.
        string? schema = FindSchemaModule(projectDir);
        if (schema is null)
        {
            // Причину, по которой не открылся сам документ дизайнера, здесь нельзя терять:
            // SettingsImportResult.Failed заметки НЕ несёт, и без этой склейки пользователь
            // с испорченным settings.design.json читал бы, что файла «нет», — и искал бы
            // не ту беду. Ровно этот случай (битый JSON рядом с отсутствующей схемой)
            // раньше показывал заведомо ложное «в проекте нет settings.design.json».
            string why = notes.Count > 0
                ? string.Join("; ", notes)
                : $"в проекте нет {SettingsDesignerService.DesignFileName}";

            return SettingsImportResult.Failed(
                why + ", и модуль схемы (settings_schema.py) тоже не найден — читать форму неоткуда");
        }

        TemplateImportResult parsed;
        try
        {
            parsed = TemplateImporter.FromPythonFile(schema);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsImportResult.Failed(Path.GetFileName(schema) + " не читается: " + ex.Message);
        }

        if (parsed.Template is null)
        {
            return SettingsImportResult.Failed(
                Path.GetFileName(schema) + ": " + (parsed.Problems.Count > 0 ? parsed.Describe() : "шаблон не найден"));
        }

        notes.Add("форма разобрана из кода мода: " + Path.GetFileName(schema));
        foreach (string problem in parsed.Problems)
        {
            notes.Add(problem);
        }

        return new SettingsImportResult(ToForm(parsed.Template, notes), notes) { Source = schema };
    }

    // =================================================================== перевод моделей

    /// <summary>
    /// Форма экрана → шаблон Core. Стык — JSON шаблона: обе стороны печатают и читают
    /// именно его, и обе проверены на живых данных.
    /// </summary>
    private static SettingsTemplate? TryToTemplate(SettingsFormSpec form, out string error)
    {
        error = string.Empty;
        try
        {
            return CoreTemplateJson.Parse(SettingsTemplateJson.Render(form), form.Linkage.Trim());
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Шаблон Core → форма экрана. Тот же стык, в обратную сторону.</summary>
    private static SettingsFormSpec ToForm(SettingsTemplate template, List<string> notes)
    {
        string json = CoreTemplateJson.ToJson(template, indented: false, sortKeys: true);

        if (JsonNode.Parse(json) is not JsonObject root)
        {
            return new SettingsFormSpec { Linkage = template.Linkage };
        }

        return SettingsDesignFile.FromLiveTemplate(root, template.Linkage, notes);
    }

    private static IReadOnlyList<SettingsIssue> Translate(TemplateValidationResult result)
    {
        var issues = new List<SettingsIssue>();

        foreach (TemplateIssue issue in result.Issues)
        {
            if (CoveredByDesigner.Contains(issue.Code))
            {
                continue;
            }

            string text = issue.Code + " · " + issue.Message;
            if (issue.Hint is { Length: > 0 } hint)
            {
                text += " — " + hint;
            }

            issues.Add(new SettingsIssue(Severity(issue.Severity), text)
            {
                Field = issue.VarName ?? issue.Path
            });
        }

        return issues;
    }

    private static SettingsIssueSeverity Severity(TemplateIssueSeverity severity) => severity switch
    {
        TemplateIssueSeverity.Error => SettingsIssueSeverity.Error,
        TemplateIssueSeverity.Warning => SettingsIssueSeverity.Warning,
        _ => SettingsIssueSeverity.Info
    };

    // =================================================================== файлы

    /// <summary>
    /// Текст файла схемы. Если файл уже есть и в нём стоят маркеры — переписывается ТОЛЬКО
    /// участок между ними: всё, что человек дописал ниже, остаётся на месте. Этого не умел
    /// встроенный генератор, и это главная причина подключать Core.
    /// </summary>
    private string SchemaContent(ModProject project, GeneratedSettingsCode code, List<SettingsIssue> issues)
    {
        string path = Path.Combine(_service.PackageDirectory(project), code.SchemaFileName);

        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path, Encoding.UTF8);
                string? merged = GeneratedRegionWriter.Replace(existing, code.SchemaRegionBody);
                if (merged is not null)
                {
                    return merged;
                }

                // Маркеров нет: файл написан руками, другим инструментом либо СТАРЫМ
                // встроенным генератором (у того маркеров не было). Core на этом месте
                // отказывается писать (RegionWriteOutcome.Conflict); шов путь оставляет —
                // иначе такие файлы нечем было бы перевести на новый формат, — но человек
                // обязан узнать об этом ДО записи, а не по пропавшему коду.
                issues.Add(new SettingsIssue(
                    SettingsIssueSeverity.Warning,
                    $"{code.SchemaFileName} в проекте уже есть, но маркеров "
                    + $"«{GeneratedRegionWriter.BeginToken}» в нём нет: кокпит не сможет обновить только "
                    + "свой участок и заменит файл ЦЕЛИКОМ — всё, что написано в нём руками, пропадёт. "
                    + "Прежний текст кокпит скопирует рядом с расширением .bak")
                {
                    Field = code.SchemaFileName
                });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            issues.Add(new SettingsIssue(
                SettingsIssueSeverity.Warning,
                $"{code.SchemaFileName} не читается ({ex.Message}) — кокпит предложит записать его заново, "
                + "целиком. Прежний текст уедет в .bak")
            {
                Field = code.SchemaFileName
            });
        }

        return code.SchemaModuleCode;
    }

    private static ModProject ToProject(SettingsCodeRequest request) => new()
    {
        ProjectDir = request.ProjectDir,
        SrcDirRelative = string.IsNullOrWhiteSpace(request.SrcDirRelative) ? "src" : request.SrcDirRelative.Trim(),
        PackageName = request.PackageName.Trim(),
        LogTag = request.LogTag,
        ModId = request.ModId
    };

    /// <summary>Каталог пакета ОТНОСИТЕЛЬНО корня проекта, прямыми слэшами — как ждёт экран.</summary>
    private string PackageRelativeDir(SettingsCodeRequest request, ModProject project)
    {
        string absolute = _service.PackageDirectory(project);

        if (absolute.Length > 0 && project.ProjectDir.Length > 0)
        {
            string relative = Path.GetRelativePath(project.ProjectDir, absolute);
            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                return relative.Replace('\\', '/');
            }
        }

        string src = string.IsNullOrWhiteSpace(request.SrcDirRelative) ? "src" : request.SrcDirRelative.Trim();
        return $"{src}/res/scripts/client/gui/mods/{project.PackageName}";
    }

    private SettingsImportResult FromDesignFile(string path, List<string> notes)
    {
        string json;
        try
        {
            json = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsImportResult.Failed(SettingsDesignerService.DesignFileName + " не читается: " + ex.Message);
        }

        try
        {
            SettingsDesign design = SettingsDesign.FromJson(json);
            if (design.Template.ControlCount > 0 || design.Template.Linkage.Length > 0)
            {
                return new SettingsImportResult(ToForm(design.Template, notes), notes) { Source = path };
            }
        }
        catch (JsonException ex)
        {
            return SettingsImportResult.Failed(
                SettingsDesignerService.DesignFileName + " не разбирается как JSON: " + ex.Message);
        }

        // Файл разобрался, но секции template в нём нет: это документ ВСТРОЕННОГО генератора
        // (плоский формат). Читаем его его же читателем, чтобы форма не потерялась.
        if (SettingsDesignFile.TryLoad(json, out SettingsFormSpec? form, out string error) && form is not null)
        {
            notes.Add("прочитан документ встроенного генератора; при следующей записи он будет "
                      + "сохранён в формате Core (секция template)");
            return new SettingsImportResult(form, notes) { Source = path };
        }

        return SettingsImportResult.Failed(
            SettingsDesignerService.DesignFileName + ": " + (error.Length > 0 ? error : "формы в файле нет"));
    }

    /// <summary>
    /// Ищет модуль схемы в дереве исходников. Именно модуль схемы, а не <c>main.py</c>:
    /// шаблон живёт в нём, и разбирать надо его.
    /// </summary>
    private static string? FindSchemaModule(string projectDir)
    {
        string src = Path.Combine(projectDir, "src");
        string root = Directory.Exists(src) ? src : projectDir;

        try
        {
            return Directory
                .EnumerateFiles(root, SettingsCodeOptions.Default.SchemaFileName, SearchOption.AllDirectories)
                .OrderBy(p => p.Length)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
