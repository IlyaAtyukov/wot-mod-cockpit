namespace WotModCockpit.Core.Preflight.Rules;

/// <summary>
/// WOT014. Точка входа названа не <c>mod_*.py</c> или лежит не прямо в
/// <c>scripts/client/gui/mods/</c>.
///
/// <para><b>Последствие.</b> <c>gui/mods/__init__.py:_findValidMODs</c> открывает через
/// <c>ResMgr</c> именно <c>__path__[0]</c> и перебирает только ПРЯМЫХ детей
/// (<c>modsFolder.keys()</c>); <c>_isValidMOD</c> = <c>scriptName.startswith('mod_')</c> и
/// <c>endswith(_MOD_NAME_POSTFIX)</c>, где <c>_MOD_NAME_POSTFIX = '.py'</c> только при
/// <c>IS_DEVELOPMENT</c>, иначе <c>'.pyc'</c>. Значит подкаталог — не точка входа, имя без
/// префикса — не точка входа. Мод при этом не падает: он просто НЕ ГРУЗИТСЯ, и в
/// <c>python.log</c> нет ни строки — это худший вид отказа, потому что искать нечего.</para>
///
/// <para>Те же проверки делает <c>build.py</c> (<c>check_source_tree</c>, <c>verify_archive</c>);
/// правило показывает их ДО сборки, специально теми же словами, чтобы источник истины
/// остался один.</para>
/// </summary>
public sealed class EntryPointLayoutRule : IPreflightRule
{
    /// <summary>Каталог, который перебирает загрузчик клиента.</summary>
    public const string ModsDirectory = "res/scripts/client/gui/mods/";

    /// <inheritdoc/>
    public string Id => "WOT014";

    /// <inheritdoc/>
    public string Title => "Раскладка точки входа";

    /// <inheritdoc/>
    public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(output);
        if (context.Files.Count == 0) return;

        int direct = 0;

        foreach (ModSourceFile file in context.Files)
        {
            int at = file.RelativePath.IndexOf(ModsDirectory, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;

            string tail = file.RelativePath[(at + ModsDirectory.Length)..];
            string name = Path.GetFileNameWithoutExtension(tail);
            bool nested = tail.Contains('/', StringComparison.Ordinal);
            bool prefixed = name.StartsWith("mod_", StringComparison.Ordinal);

            if (!nested && prefixed) { direct++; continue; }

            if (nested && prefixed && DefinesLoaderApi(file))
            {
                output.Add(Project(context, file, 0, PreflightSeverity.Error,
                    $"Файл {tail} назван как точка входа, но лежит в подкаталоге. _findValidMODs перебирает " +
                    "только ПРЯМЫХ детей scripts/client/gui/mods, поэтому загрузчик его не увидит: мод не запустится, " +
                    "и в python.log не будет ни одной строки.",
                    "Перенесите точку входа прямо в res/scripts/client/gui/mods/, а тело мода оставьте в пакете."));
                continue;
            }

            if (!nested && !prefixed && DefinesLoaderApi(file))
            {
                output.Add(Project(context, file, 0, PreflightSeverity.Error,
                    $"Файл {tail} объявляет init()/fini(), лежит прямо в scripts/client/gui/mods, но не начинается " +
                    "с mod_. _isValidMOD требует префикса mod_, поэтому загрузчик пройдёт мимо — мод молча не запустится.",
                    "Переименуйте файл в mod_<имя>.py."));
            }
        }

        if (direct == 0)
        {
            if (context.Options.IsLibrary) return;

            ModSourceFile anchor = context.Files[0];

            // Отсутствие точки входа — приговор только для МОДА. Библиотека (ModsListAPI,
            // ModsSettingsAPI, XVM) точки входа не имеет и не должна: её поднимает чужой
            // загрузчик или импортируют другие моды. Проверено чтением самих .mtmod:
            // com.modxvm.xvm_13.1.0.0059, izeberg.modssettingsapi_1.6.4 и
            // me.poliroid.modslistapi_1.6.01 не содержат ни одного mod_*.pyc.
            // Признак, по которому их видно: во всём дереве нет def init()/fini() верхнего уровня.
            bool looksLikeLibrary = !context.Files.Any(DefinesLoaderApi);

            if (looksLikeLibrary)
            {
                output.Add(Project(context, anchor, 0, PreflightSeverity.Warning,
                    "В дереве нет ни одного файла res/scripts/client/gui/mods/mod_*.py — загрузчик клиента " +
                    "импортирует ТОЛЬКО такие файлы. Но нет и ни одного def init()/fini() верхнего уровня, " +
                    "то есть это, похоже, БИБЛИОТЕКА для других модов, а не мод: её поднимает сторонний " +
                    "загрузчик или импортируют соседи, и точка входа ей не нужна.",
                    "Если это мод — создайте res/scripts/client/gui/mods/mod_<имя>.py. Если библиотека — " +
                    "включите в настройках preflight признак «это библиотека» (PreflightOptions.IsLibrary) " +
                    "или выключите правило WOT014."));
                return;
            }

            output.Add(Project(context, anchor, 0, PreflightSeverity.Error,
                "В дереве мода нет ни одного файла res/scripts/client/gui/mods/mod_*.py, хотя def init()/fini() " +
                "верхнего уровня есть. Загрузчик клиента импортирует ТОЛЬКО такие файлы (и только " +
                "скомпилированные в .pyc), поэтому эти init()/fini() не вызовет никто: мод не будет " +
                "загружен вообще и никакого сообщения об этом не появится.",
                "Создайте res/scripts/client/gui/mods/mod_<имя>.py — build.py скомпилирует его в mod_<имя>.pyc. " +
                "Если же это библиотека, которую поднимает сторонний загрузчик (так устроен XVM: его init() " +
                "зовёт XFW, а не gui.mods), включите признак «это библиотека» в настройках preflight."));
        }
    }

    private static bool DefinesLoaderApi(ModSourceFile file)
    {
        foreach (PythonDefinition def in file.Model.Definitions)
            if (def.IsTopLevel && !def.IsClass && def.Name is "init" or "fini") return true;
        return false;
    }

    private PreflightDiagnostic Project(PreflightContext context, ModSourceFile file, int line,
        PreflightSeverity severity, string message, string hint) =>
        new(Id, severity, file.FullPath, line, 0, message)
        {
            Hint = hint,
            Reference = "scripts/client/gui/mods/__init__.py:_findValidMODs / _isValidMOD",
            RelativePath = file.RelativePath
        };
}
