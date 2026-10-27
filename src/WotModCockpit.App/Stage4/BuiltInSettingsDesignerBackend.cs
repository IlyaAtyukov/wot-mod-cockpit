using System.Globalization;
using System.IO;
using System.Text;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// Встроенный генератор кода настроек: та самая пара файлов, ради которой дизайнер и нужен.
/// <para>
/// <b>Почему файлов два.</b> <c>settings_schema.py</c> — полностью машинный: он
/// переписывается ЦЕЛИКОМ при каждом сохранении формы, и правки в нём обречены.
/// <c>settings.py</c> создаётся ОДИН РАЗ и дальше принадлежит мододелу: там живут
/// загрузка своего JSON-конфига, подписка на «Применить» и раздача значений остальному моду.
/// Маркеров-регионов внутри файлов нет намеренно: перегенерация куска чужого файла —
/// это гонка с человеком, которую человек всегда проигрывает.
/// </para>
/// <para>
/// <b>Почему шаблон строится ОТ СОХРАНЁННЫХ значений.</b> ModsSettingsAPI никогда не
/// сливает старые значения в новый шаблон: <c>setModTemplate</c> при любой принятой правке
/// делает <c>settings = getSettingsFromTemplate(template)</c>, то есть сбрасывает выбор
/// игрока к тому, что написано в шаблоне. Единственная защита — держать значения в своём
/// JSON и подставлять их в шаблон при регистрации: тогда «сброс» возвращает игрока к его же
/// настройкам, а заводское значение достаётся только новым ключам.
/// </para>
/// <para>
/// <b>Почему литеральные словари, а не <c>templates.createX</c>.</b> Результат побайтно
/// тот же (фабрики только собирают такой же dict), но файл не импортирует чужой мод —
/// правилу WOT018 нечего сказать, и шаблон, который уедет в <c>modsettings.dat</c>,
/// виден в коде целиком.
/// </para>
/// </summary>
public sealed class BuiltInSettingsDesignerBackend : ISettingsDesignerBackend
{
    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string UnavailableReason => string.Empty;

    /// <inheritdoc />
    public string Name => "встроенный генератор кокпита";

    /// <inheritdoc />
    public IReadOnlyList<SettingsKeyCode> KnownKeys => SettingsHotKey.KnownKeys;

    /// <inheritdoc />
    public IReadOnlyList<SettingsIssue> Validate(SettingsFormSpec form) => Array.Empty<SettingsIssue>();

    /// <inheritdoc />
    public string RenderTemplateJson(SettingsFormSpec form) => SettingsTemplateJson.Render(form);

    /// <inheritdoc />
    public SettingsCodeResult Generate(SettingsFormSpec form, SettingsCodeRequest request)
    {
        ArgumentNullException.ThrowIfNull(form);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.PackageName))
        {
            return SettingsCodeResult.Failed(
                "у проекта не задано имя python-пакета: класть settings_schema.py некуда. "
                + "Пакет задаётся в wotmod.json (packageName) и совпадает с каталогом "
                + "src/res/scripts/client/gui/mods/<пакет>");
        }

        string package = request.PackageName.Trim();
        string src = string.IsNullOrWhiteSpace(request.SrcDirRelative) ? "src" : request.SrcDirRelative.Trim();
        string dir = $"{src}/res/scripts/client/gui/mods/{package}";

        var files = new List<SettingsCodeFile>
        {
            new(
                SettingsDesignFile.FileName,
                SettingsDesignFile.Save(form),
                SettingsFileKind.Design)
            {
                Note = "источник истины дизайнера; в .mtmod не попадает"
            },
            new(
                dir + "/settings_schema.py",
                BuildSchema(form),
                SettingsFileKind.Generated)
            {
                Note = "машинный файл, переписывается целиком"
            }
        };

        if (request.CreateUserFile)
        {
            files.Add(new SettingsCodeFile(
                dir + "/settings.py",
                BuildRuntime(form, request, package),
                SettingsFileKind.CreateOnce)
            {
                Note = "создаётся один раз; существующий файл кокпит не трогает"
            });
        }

        return new SettingsCodeResult(files, Array.Empty<SettingsIssue>())
        {
            GeneratorName = Name
        };
    }

    /// <inheritdoc />
    public SettingsImportResult ImportFromProject(string projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir))
        {
            return SettingsImportResult.Failed("проект не выбран");
        }

        string path = Path.Combine(projectDir, SettingsDesignFile.FileName);
        if (!File.Exists(path))
        {
            return SettingsImportResult.Failed(
                $"в проекте нет {SettingsDesignFile.FileName}. Встроенный импортёр читает только его; "
                + "разбор рукописного кода мода делает реализация Core, а она не подключена");
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SettingsImportResult.Failed("файл дизайнера не прочитан: " + ex.Message);
        }

        if (!SettingsDesignFile.TryLoad(json, out SettingsFormSpec? form, out string error) || form is null)
        {
            return SettingsImportResult.Failed(error);
        }

        return new SettingsImportResult(form, Array.Empty<string>()) { Source = path };
    }

    // ============================================================ settings_schema.py

    private static string BuildSchema(SettingsFormSpec form)
    {
        var sb = new StringBuilder();

        sb.Append("# -*- coding: utf-8 -*-\n");
        sb.Append("\"\"\"СГЕНЕРИРОВАНО «Дизайнером настроек» WoT Mod Cockpit. НЕ РЕДАКТИРОВАТЬ РУКАМИ.\n\n");
        sb.Append("Файл переписывается ЦЕЛИКОМ при каждом сохранении формы в дизайнере.\n");
        sb.Append("Источник истины — settings.design.json в корне проекта.\n");
        sb.Append("Свой код пишите в settings.py — его дизайнер не трогает.\n\n");
        sb.Append("ПРИ ЛЮБОМ ИЗМЕНЕНИИ СОСТАВА КОНТРОЛОВ ПОДНИМИТЕ SETTINGS_VERSION.\n");
        sb.Append("Пока он не поднят, ModsSettingsAPI показывает СТАРУЮ форму из\n");
        sb.Append("modsettings.dat, а новых ключей в возвращённом словаре просто нет.\n");
        sb.Append("Когда поднят — API регенерирует настройки из шаблона, поэтому шаблон\n");
        sb.Append("строится от СОХРАНЁННЫХ значений (см. build_template ниже).\n\n");
        sb.Append("cockpit-designer: schema v1\n");
        sb.Append("\"\"\"\n\n");

        sb.Append("# Ключ мода в состоянии ModsSettingsAPI. МЕНЯТЬ НЕЛЬЗЯ: к нему привязаны\n");
        sb.Append("# настройки игрока. Только ASCII-str: generateSettingsData делает\n");
        sb.Append("# sorted(..., key=str.lower), а str.lower на unicode в py2 бросает TypeError\n");
        sb.Append("# и окно настроек не откроется НИ У ОДНОГО мода.\n");
        sb.Append("LINKAGE = ").Append(Py(form.Linkage, unicode: false)).Append('\n');
        sb.Append("SETTINGS_VERSION = ").Append(form.SettingsVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("MOD_DISPLAY_NAME = ").Append(Py(form.ModDisplayName, unicode: true)).Append('\n');
        sb.Append("HAS_ENABLED = ").Append(form.HasEnabled ? "True" : "False").Append("\n\n");

        sb.Append("# varName -> заводское значение. Единственный источник умолчаний: ровно этот\n");
        sb.Append("# словарь выводит из шаблона сам API (getSettingsFromTemplate).\n");
        sb.Append("DEFAULTS = {\n");

        if (form.HasEnabled)
        {
            sb.Append("    'enabled': ").Append(form.EnabledDefault ? "True" : "False").Append(",\n");
        }

        foreach (SettingsControlSpec control in ValueControls(form))
        {
            sb.Append("    ").Append(Py(control.VarName, unicode: false)).Append(": ")
              .Append(PyValue(control)).Append(",\n");
        }

        sb.Append("}\n\n");

        sb.Append("# varName -> тип контрола. Нужен, чтобы привести значения из своего JSON\n");
        sb.Append("# к тому, что ждёт окно (в py2 json отдаёт байтовые str после byteify).\n");
        sb.Append("VAR_TYPES = {\n");

        if (form.HasEnabled)
        {
            sb.Append("    'enabled': 'CheckBox',\n");
        }

        foreach (SettingsControlSpec control in ValueControls(form))
        {
            sb.Append("    ").Append(Py(control.VarName, unicode: false)).Append(": '")
              .Append(control.Type.ToString()).Append("',\n");
        }

        sb.Append("}\n\n");

        var names = ValueControls(form).Select(c => c.VarName).ToList();
        sb.Append("VAR_NAMES = (");
        sb.Append(string.Join(", ", names.Select(n => Py(n, unicode: false))));
        if (names.Count == 1)
        {
            sb.Append(',');
        }

        sb.Append(")\n\n\n");

        sb.Append("def build_template(values):\n");
        sb.Append("    \"\"\"Шаблон окна настроек. values — ТЕКУЩИЕ значения игрока.\n\n");
        sb.Append("    Литеральные словари, а не gui.modsSettingsApi.templates.createX: результат\n");
        sb.Append("    тот же (фабрики только собирают такой же dict), зато файл не импортирует\n");
        sb.Append("    чужой мод — правилу WOT018 нечего сказать, и шаблон, который уедет\n");
        sb.Append("    в modsettings.dat, виден целиком.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    def v(name):\n");
        sb.Append("        if name in values:\n");
        sb.Append("            return values[name]\n");
        sb.Append("        return DEFAULTS[name]\n\n");
        sb.Append("    return ").Append(SettingsTemplateJson.RenderPythonTemplate(form, 4)).Append('\n');

        return sb.ToString();
    }

    private static IEnumerable<SettingsControlSpec> ValueControls(SettingsFormSpec form) =>
        form.AllControls.Where(c => SettingsControlSchema.HasValue(c.Type) && c.VarName.Length > 0);

    // ============================================================ settings.py

    private static string BuildRuntime(SettingsFormSpec form, SettingsCodeRequest request, string package)
    {
        string logTag = string.IsNullOrWhiteSpace(request.LogTag) ? package : request.LogTag.Trim().TrimStart('[');
        string prefix = "[" + logTag + "]";
        string config = string.IsNullOrWhiteSpace(request.ConfigRelativePath)
            ? $"mods/configs/{package}/settings.json"
            : request.ConfigRelativePath;

        string joined = string.Join(
            ", ",
            config.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(p => Py(p, unicode: false)));

        var sb = new StringBuilder();

        sb.Append("# -*- coding: utf-8 -*-\n");
        sb.Append("\"\"\"Настройки мода. Создано дизайнером кокпита ОДИН РАЗ — дальше файл ваш.\n\n");
        sb.Append("Состав формы живёт в settings_schema.py (его переписывает дизайнер),\n");
        sb.Append("значения — в своём JSON рядом с игрой. Зеркало обязательно: ModsSettingsAPI\n");
        sb.Append("при любой принятой правке шаблона СБРАСЫВАЕТ настройки к значениям из\n");
        sb.Append("шаблона, поэтому шаблон строится от сохранённых значений — тогда сброс\n");
        sb.Append("возвращает игрока к его же выбору.\n\n");
        sb.Append("Отсюда моду нужны три вещи:\n");
        sb.Append("    settings.register()            — из onAccountShowGUI, один раз;\n");
        sb.Append("    settings.get('varName')        — прочитать значение;\n");
        sb.Append("    settings.subscribe(listener)   — узнать про «Применить».\n");
        sb.Append("\"\"\"\n");
        sb.Append("import json\n");
        sb.Append("import logging\n");
        sb.Append("import os\n");
        sb.Append("import traceback\n\n");
        sb.Append("import settings_schema\n\n");
        sb.Append("LOG_PREFIX = '").Append(prefix.Replace("'", "\\'", StringComparison.Ordinal)).Append("'\n");
        sb.Append("_logger = logging.getLogger('").Append(logTag.Replace("'", "\\'", StringComparison.Ordinal)).Append("')\n\n");
        sb.Append("CONFIG_RELATIVE_PATH = os.path.join(").Append(joined).Append(")\n\n");
        sb.Append("_values = dict(settings_schema.DEFAULTS)\n");
        sb.Append("_registered = False\n");
        sb.Append("_listeners = []\n\n\n");

        sb.Append("def _config_path():\n");
        sb.Append("    \"\"\"Полный путь к своему конфигу. Рабочий каталог клиента — корень игры,\n");
        sb.Append("    но полагаться на это одно нельзя, поэтому проверяем и ResMgr.appDirectory().\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    roots = []\n");
        sb.Append("    try:\n");
        sb.Append("        roots.append(os.getcwd())\n");
        sb.Append("    except Exception:\n");
        sb.Append("        pass\n");
        sb.Append("    try:\n");
        sb.Append("        import ResMgr\n");
        sb.Append("        app = ResMgr.appDirectory()\n");
        sb.Append("        if app:\n");
        sb.Append("            roots.append(app)\n");
        sb.Append("            roots.append(os.path.dirname(app.rstrip('/' + chr(92))))\n");
        sb.Append("    except Exception:\n");
        sb.Append("        pass\n");
        sb.Append("    if not roots:\n");
        sb.Append("        roots.append('.')\n");
        sb.Append("    for root in roots:\n");
        sb.Append("        path = os.path.join(root, CONFIG_RELATIVE_PATH)\n");
        sb.Append("        if os.path.isfile(path):\n");
        sb.Append("            return path\n");
        sb.Append("    return os.path.join(roots[0], CONFIG_RELATIVE_PATH)\n\n\n");

        sb.Append("def _load():\n");
        sb.Append("    path = _config_path()\n");
        sb.Append("    if not os.path.isfile(path):\n");
        sb.Append("        return\n");
        sb.Append("    try:\n");
        sb.Append("        handle = open(path, 'rb')\n");
        sb.Append("        try:\n");
        sb.Append("            raw = handle.read()\n");
        sb.Append("        finally:\n");
        sb.Append("            handle.close()\n");
        sb.Append("        saved = json.loads(raw.decode('utf-8-sig', 'replace'))\n");
        sb.Append("        if isinstance(saved, dict):\n");
        sb.Append("            for key in settings_schema.DEFAULTS:\n");
        sb.Append("                if key in saved:\n");
        sb.Append("                    _values[key] = saved[key]\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.error('%s broken settings at %s:\\n%s',\n");
        sb.Append("                      LOG_PREFIX, path, traceback.format_exc())\n\n\n");

        sb.Append("def _save():\n");
        sb.Append("    path = _config_path()\n");
        sb.Append("    try:\n");
        sb.Append("        folder = os.path.dirname(path)\n");
        sb.Append("        if folder and not os.path.isdir(folder):\n");
        sb.Append("            os.makedirs(folder)\n");
        sb.Append("        handle = open(path, 'wb')\n");
        sb.Append("        try:\n");
        sb.Append("            handle.write(json.dumps(_values, ensure_ascii=False, indent=4).encode('utf-8'))\n");
        sb.Append("        finally:\n");
        sb.Append("            handle.close()\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.error('%s cannot save settings to %s:\\n%s',\n");
        sb.Append("                      LOG_PREFIX, path, traceback.format_exc())\n\n\n");

        sb.Append("def _apply(new_settings):\n");
        sb.Append("    \"\"\"Перелить пришедшие значения к себе, не теряя умолчаний.\n\n");
        sb.Append("    Ключа может НЕ БЫТЬ: если состав контролов изменили без поднятия\n");
        sb.Append("    settingsVersion, API отдаст словарь по СТАРОМУ шаблону.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    if not isinstance(new_settings, dict):\n");
        sb.Append("        return\n");
        sb.Append("    for key in list(_values.keys()):\n");
        sb.Append("        if key in new_settings:\n");
        sb.Append("            _values[key] = new_settings[key]\n\n\n");

        sb.Append("def _on_settings_changed(linkage, new_settings):\n");
        sb.Append("    \"\"\"Игрок нажал «Применить» или OK.\n\n");
        sb.Append("    onSettingsChanged — Event.Event(): событие ШИРОКОВЕЩАТЕЛЬНОЕ и приходит\n");
        sb.Append("    от всех модов, поэтому первым делом фильтруем по linkage. Отписаться\n");
        sb.Append("    в API 1.6.4 нечем, обработчик переживёт fini() — он обязан быть\n");
        sb.Append("    идемпотентным и не падать.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    try:\n");
        sb.Append("        if linkage != settings_schema.LINKAGE:\n");
        sb.Append("            return\n");
        sb.Append("        _apply(new_settings)\n");
        sb.Append("        _save()\n");
        sb.Append("        for listener in tuple(_listeners):\n");
        sb.Append("            try:\n");
        sb.Append("                listener(dict(_values))\n");
        sb.Append("            except Exception:\n");
        sb.Append("                _logger.error('%s settings listener failed:\\n%s',\n");
        sb.Append("                              LOG_PREFIX, traceback.format_exc())\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.error('%s onSettingsChanged failed:\\n%s',\n");
        sb.Append("                      LOG_PREFIX, traceback.format_exc())\n\n\n");

        sb.Append("def subscribe(listener):\n");
        sb.Append("    \"\"\"Позвать listener(values) после каждого применения настроек.\"\"\"\n");
        sb.Append("    if listener not in _listeners:\n");
        sb.Append("        _listeners.append(listener)\n\n\n");

        sb.Append("def register():\n");
        sb.Append("    \"\"\"Звать из onAccountShowGUI. True — окно настроек доступно.\n\n");
        sb.Append("    Не из init(): порядок init() у модов задаётся сортировкой имён mod_*.pyc\n");
        sb.Append("    и меняется при установке любого другого мода. И не позже: при открытии\n");
        sb.Append("    окна clearState() стирает настройки всех модов, которые в этой сессии\n");
        sb.Append("    ещё не зарегистрировались.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    global _registered\n");
        sb.Append("    if _registered:\n");
        sb.Append("        return True\n");
        sb.Append("    _load()\n");
        sb.Append("    api = None\n");
        sb.Append("    try:\n");
        sb.Append("        from gui.modsSettingsApi import g_modsSettingsApi\n");
        sb.Append("        api = g_modsSettingsApi\n");
        sb.Append("    except ImportError:\n");
        sb.Append("        _logger.info('%s ModsSettingsAPI is not installed, using own config only',\n");
        sb.Append("                     LOG_PREFIX)\n");
        sb.Append("        return False\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.error('%s ModsSettingsAPI import failed:\\n%s',\n");
        sb.Append("                      LOG_PREFIX, traceback.format_exc())\n");
        sb.Append("        return False\n");
        sb.Append("    if api is None:\n");
        sb.Append("        return False\n");
        sb.Append("    try:\n");
        sb.Append("        template = settings_schema.build_template(_values)\n");
        sb.Append("        saved = api.setModTemplate(\n");
        sb.Append("            settings_schema.LINKAGE, template, _on_settings_changed)\n");
        sb.Append("        if saved is None:\n");
        sb.Append("            _logger.warning('%s setModTemplate returned None', LOG_PREFIX)\n");
        sb.Append("        else:\n");
        sb.Append("            _apply(saved)\n");
        sb.Append("            _save()\n");
        sb.Append("        _registered = True\n");
        sb.Append("        return True\n");
        sb.Append("    except Exception:\n");
        sb.Append("        _logger.error('%s setModTemplate failed:\\n%s',\n");
        sb.Append("                      LOG_PREFIX, traceback.format_exc())\n");
        sb.Append("        return False\n\n\n");

        sb.Append("def get(name, default=None):\n");
        sb.Append("    \"\"\"Чтение настройки. Работает и без установленного ModsSettingsAPI.\"\"\"\n");
        sb.Append("    try:\n");
        sb.Append("        if name in _values:\n");
        sb.Append("            return _values[name]\n");
        sb.Append("        if name in settings_schema.DEFAULTS:\n");
        sb.Append("            return settings_schema.DEFAULTS[name]\n");
        sb.Append("    except Exception:\n");
        sb.Append("        pass\n");
        sb.Append("    return default\n\n\n");

        sb.Append("def is_enabled():\n");
        sb.Append("    \"\"\"Тумблер мода в окне настроек. Гасит контролы ВИЗУАЛЬНО — логику\n");
        sb.Append("    выключения мод обязан реализовать сам, начав с этой проверки.\n");
        sb.Append("    \"\"\"\n");
        sb.Append("    return bool(get('enabled', True))\n");

        if (form.AllControls.Any(c => c.Type == SettingsControlType.HotKey))
        {
            sb.Append("\n\n");
            sb.Append("def is_hotkey_down(name):\n");
            sb.Append("    \"\"\"Нажато ли сочетание, назначенное контролу HotKey.\n\n");
            sb.Append("    checkKeyset разворачивает спецкоды -1 ALT / -2 CTRL / -3 SHIFT в пары\n");
            sb.Append("    левый-правый и требует, чтобы нажаты были ВСЕ коды сразу. Без\n");
            sb.Append("    ModsSettingsAPI проверяем сами через BigWorld.isKeyDown, а спецкоды\n");
            sb.Append("    раскрываем руками.\n");
            sb.Append("    \"\"\"\n");
            sb.Append("    keys = get(name) or []\n");
            sb.Append("    if not keys:\n");
            sb.Append("        return False\n");
            sb.Append("    try:\n");
            sb.Append("        from gui.modsSettingsApi import g_modsSettingsApi\n");
            sb.Append("        return bool(g_modsSettingsApi.checkKeyset(keys))\n");
            sb.Append("    except ImportError:\n");
            sb.Append("        pass\n");
            sb.Append("    except Exception:\n");
            sb.Append("        _logger.error('%s checkKeyset failed:\\n%s',\n");
            sb.Append("                      LOG_PREFIX, traceback.format_exc())\n");
            sb.Append("        return False\n");
            sb.Append("    try:\n");
            sb.Append("        import BigWorld\n");
            sb.Append("        pairs = {-1: (56, 184), -2: (29, 157), -3: (42, 54)}\n");
            sb.Append("        for code in keys:\n");
            sb.Append("            if code in pairs:\n");
            sb.Append("                left, right = pairs[code]\n");
            sb.Append("                if not (BigWorld.isKeyDown(left) or BigWorld.isKeyDown(right)):\n");
            sb.Append("                    return False\n");
            sb.Append("            elif not BigWorld.isKeyDown(code):\n");
            sb.Append("                return False\n");
            sb.Append("        return True\n");
            sb.Append("    except Exception:\n");
            sb.Append("        _logger.error('%s isKeyDown failed:\\n%s',\n");
            sb.Append("                      LOG_PREFIX, traceback.format_exc())\n");
            sb.Append("        return False\n");
        }

        return sb.ToString();
    }

    // ============================================================ литералы

    private static string PyValue(SettingsControlSpec control) => control.Value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => Py(s, control.Type == SettingsControlType.TextInput),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Num(d),
        int[] codes => SettingsHotKey.RenderList(codes),
        double[] pair => "[" + string.Join(", ", pair.Select(Num)) + "]",
        IReadOnlyList<int> codes => SettingsHotKey.RenderList(codes.ToArray()),
        IReadOnlyList<double> pair => "[" + string.Join(", ", pair.Select(Num)) + "]",
        _ => Py(control.Value.ToString() ?? string.Empty, unicode: true)
    };

    private static string Num(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-9
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.##########", CultureInfo.InvariantCulture);

    private static string Py(string value, bool unicode)
    {
        var sb = new StringBuilder(value.Length + 3);

        if (unicode)
        {
            sb.Append('u');
        }

        sb.Append('\'');

        foreach (char c in value)
        {
            switch (c)
            {
                case '\'': sb.Append("\\'"); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }

        sb.Append('\'');
        return sb.ToString();
    }
}
