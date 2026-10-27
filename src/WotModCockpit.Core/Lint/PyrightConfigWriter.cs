using System.Text;
using System.Text.Json;

namespace WotModCockpit.Core.Lint;

/// <summary>
/// Готовит временное рабочее место для разового запуска pyright: конфиг и заглушки
/// нативных модулей движка.
///
/// <para><b>pythonVersion 2.7 использовать НЕЛЬЗЯ</b>, и это проверено запуском.
/// Pyright 1.1.413 принимает такое значение в конфиге, но typeshed давно выбросил
/// стабы Python 2, поэтому вместе с ними пропадают ВСЕ встроенные имена: на файле
/// в девять строк получается 1450 диагностик, включая «isinstance is not defined»
/// и «AttributeError is not defined», причём большинство приезжает вообще из чужого
/// <c>typing_extensions.py</c> в site-packages. Плюс парсер Python 2 из pyright удалён:
/// <c>print 'x'</c> и обратные кавычки всё равно остаются синтаксическими ошибками.
/// Поэтому ставится <c>pythonVersion 3.8</c> — под ним pyright разбирает тот py2-код,
/// который синтаксически совместим с py3 (а именно такой и пишут в модах), и молчит
/// про остальное.</para>
///
/// <para><b>typeCheckingMode обязан быть "off"</b>. Замер на эталонном моде (1351 строка):
/// off — 6 диагностик, basic — 13, и все семь лишних в basic ложные, потому что это
/// правила Python 3 на коде Python 2 (<c>Cannot access attribute "decode" for class "str"</c>
/// — в py2 у str метод decode есть). В режиме off остаётся ровно то, ради чего фича и нужна:
/// несуществующие модули и неопределённые имена.</para>
///
/// <para><b>API чужих модов заглушками НЕ накрываются</b>, и это решение, а не пропуск.
/// <c>gui.modsListApi</c> и <c>gui.modsSettingsApi</c> лежат внутри пакета <c>gui</c>,
/// который в клиенте настоящий: положив в <c>stubPath</c> пакет <c>gui/</c>, мы заслонили бы
/// им весь настоящий <c>gui</c> — и <c>gui.Scaleform</c>, и <c>gui.shared</c>, и всё
/// остальное. Поэтому диагностика <c>reportMissingImports</c> по известным API чужих модов
/// отсеивается в <see cref="PyrightRunner"/> (см. там же замер: 5 таких предупреждений
/// на отрендеренном каталоге рецептов), а настоящий риск такого импорта разбирает
/// правило preflight WOT018.</para>
/// </summary>
public static class PyrightConfigWriter
{
    /// <summary>Нативные модули движка: <c>.py</c> у них нет, резолвить их неоткуда.</summary>
    /// <remarks>
    /// Без заглушек это 16 диагностик «Import could not be resolved» на эталонном моде —
    /// чистый шум, потому что модули существуют, просто написаны на C++.
    /// </remarks>
    /// <remarks>
    /// Список выверен по дереву исходников: каждое имя здесь — модуль, у которого в клиенте
    /// НЕТ <c>.py</c>. Похожие на нативные <c>Keys</c>, <c>Account</c>, <c>SoundGroups</c>,
    /// <c>WWISE</c>, <c>BWLogging</c>, <c>BWUtil</c>, <c>AvatarInputHandler</c>, <c>PyGUI</c>
    /// и <c>PixieBG</c> сюда НЕ входят намеренно: они существуют как обычный питон, и заглушка
    /// заслонила бы настоящий модуль — <c>Keys.KEY_ОПЕЧАТКА</c> перестала бы быть ошибкой.
    /// На этот случай <see cref="WriteNativeStubs(string, string?)"/> ещё и проверяет дерево.
    /// </remarks>
    public static IReadOnlyList<string> NativeModules { get; } =
    [
        "BigWorld", "ResMgr", "Math", "GUI", "CGF", "AnimationSequence", "Physics", "Pixie",
        "FMOD", "Vehicular", "Windowing", "BWPersonality", "BWAutoTest",
        "_BigWorld", "_Math", "_ResMgr"
    ];

    /// <summary>Подкаталоги дерева исходников клиента, которые идут в <c>extraPaths</c>.</summary>
    /// <remarks>
    /// Именно они превращают 19 диагностик в 6: без них pyright не находит ни
    /// <c>PlayerEvents</c>, ни <c>Avatar</c>, ни <c>gui.*</c>, хотя всё это лежит в дереве.
    /// Путь обязан быть НАСТОЯЩИМ путём Windows: pyright молча игнорирует записи,
    /// которые не резолвятся.
    /// </remarks>
    public static IReadOnlyList<string> SourceSubdirectories { get; } = ["client", "client_common", "common"];

    /// <summary>
    /// Пишет <c>pyrightconfig.json</c> и заглушки нативных модулей в рабочий каталог.
    /// </summary>
    /// <param name="workspaceDirectory">Временный каталог рабочего места; создаётся при необходимости.</param>
    /// <param name="sourceDirectories">Каталоги с кодом мода, которые надо проверить.</param>
    /// <param name="wotSrcDirectory">Дерево декомпилированных исходников клиента; пусто — не добавлять extraPaths.</param>
    /// <returns>Полный путь к записанному конфигу.</returns>
    public static string Write(string workspaceDirectory, IEnumerable<string> sourceDirectories, string? wotSrcDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentNullException.ThrowIfNull(sourceDirectories);

        Directory.CreateDirectory(workspaceDirectory);

        string stubDir = Path.Combine(workspaceDirectory, "stubs");
        WriteNativeStubs(stubDir, wotSrcDirectory);

        var extraPaths = new List<string>();
        if (!string.IsNullOrWhiteSpace(wotSrcDirectory) && Directory.Exists(wotSrcDirectory))
        {
            foreach (string sub in SourceSubdirectories)
            {
                string path = Path.Combine(wotSrcDirectory, sub);
                if (Directory.Exists(path)) extraPaths.Add(Normalize(path));
            }
        }

        // ВНИМАНИЕ: 'include' здесь НЕ пишется, и это не упущение.
        // Pyright принимает в 'include' только ОТНОСИТЕЛЬНЫЕ пути, а абсолютный молча
        // выбрасывает: «Ignoring path ... in "include" array because it is not relative»
        // — и анализирует НОЛЬ файлов, отвечая при этом «замечаний нет». Каталог проекта
        // лежит где угодно, относительным путём от %TEMP% его не выразить (бывает и другой
        // диск), поэтому пути передаются аргументами командной строки: их pyright принимает
        // абсолютными. Проверено запуском: с 'include' — filesAnalyzed 0, аргументом — 1.
        var config = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["stubPath"] = Normalize(stubDir),
            ["extraPaths"] = extraPaths,

            // 3.8, а не 2.7: см. комментарий к классу — 2.7 сносит вообще все встроенные имена.
            ["pythonVersion"] = "3.8",

            // Единственный режим, дающий сигнал без шума на коде Python 2.
            ["typeCheckingMode"] = "off",

            // Мод не поставляет .py к стабам, и это норма.
            ["reportMissingModuleSource"] = "none",

            // Дерево клиента — декомпилят: в нём хватает своих странностей,
            // и разбирать чужие файлы смысла нет.
            ["useLibraryCodeForTypes"] = false,
            ["analyzeUnannotatedFunctions"] = true
        };

        string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        string configPath = Path.Combine(workspaceDirectory, "pyrightconfig.json");
        File.WriteAllText(configPath, json, new UTF8Encoding(false));
        return configPath;
    }

    /// <summary>
    /// Пишет <c>.pyi</c>-заглушки нативных модулей: каждая объявляет
    /// <c>__getattr__ -&gt; Any</c>, поэтому импорт и любые атрибуты становятся законными,
    /// а сам модуль остаётся непрозрачным — что и соответствует действительности.
    /// </summary>
    /// <param name="stubDirectory">Каталог для стабов.</param>
    /// <param name="wotSrcDirectory">
    /// Дерево исходников клиента. Если задано, модуль, у которого в дереве ЕСТЬ <c>.py</c>,
    /// заглушкой не накрывается: настоящий модуль всегда информативнее непрозрачной затычки,
    /// и накрыв его, мы бы спрятали настоящие ошибки в обращениях к нему.
    /// </param>
    /// <returns>Сколько файлов записано.</returns>
    public static int WriteNativeStubs(string stubDirectory, string? wotSrcDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stubDirectory);
        Directory.CreateDirectory(stubDirectory);

        const string body = """
            # Заглушка нативного модуля движка: .py у него нет, атрибуты неизвестны.
            from typing import Any


            def __getattr__(name: str) -> Any: ...
            """;

        int n = 0;
        foreach (string module in NativeModules)
        {
            if (ExistsInClientTree(wotSrcDirectory, module)) continue;

            File.WriteAllText(Path.Combine(stubDirectory, module + ".pyi"), body, new UTF8Encoding(false));
            n++;
        }

        return n;
    }

    /// <summary>
    /// Модуль есть в дереве клиента настоящим питоном — значит заглушка ему не нужна.
    ///
    /// <para>Сравнение имён СТРОГО ПОРЕГИСТРОВОЕ, и это принципиально. Pyright регистр
    /// учитывает: <c>import GUI</c> он не резолвит в пакет <c>client/gui</c> (проверено
    /// запуском — «Import "GUI" could not be resolved»). А вот <c>File.Exists</c> на
    /// Windows регистр игнорирует и на <c>GUI\__init__.py</c> ответил бы «есть»,
    /// после чего нативный <c>GUI</c> остался бы без заглушки и дал ложную диагностику
    /// в каждом моде, который его импортирует.</para>
    /// </summary>
    public static bool ExistsInClientTree(string? wotSrcDirectory, string module)
    {
        if (string.IsNullOrWhiteSpace(wotSrcDirectory) || !Directory.Exists(wotSrcDirectory)) return false;

        foreach (string sub in SourceSubdirectories)
        {
            string root = Path.Combine(wotSrcDirectory, sub);
            if (!Directory.Exists(root)) continue;

            if (HasEntry(Directory.EnumerateFiles(root, module + ".py"), module + ".py")) return true;

            if (HasEntry(Directory.EnumerateDirectories(root, module), module) &&
                File.Exists(Path.Combine(root, module, "__init__.py")))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Среди найденных путей есть тот, чьё имя совпадает с образцом ПОБУКВЕННО.</summary>
    private static bool HasEntry(IEnumerable<string> paths, string expectedName)
    {
        foreach (string path in paths)
        {
            if (string.Equals(Path.GetFileName(path), expectedName, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
