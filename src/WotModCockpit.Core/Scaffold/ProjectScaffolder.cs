using System.Text;

namespace WotModCockpit.Core.Scaffold;

/// <summary>Мастер нового проекта мода.</summary>
public interface IProjectScaffolder
{
    /// <summary>
    /// Создаёт новый проект мода из шаблона: копирует дерево, переименовывает точку входа
    /// и каталог пакета, подставляет идентификатор, имя, версию и тег лога.
    /// </summary>
    /// <param name="options">Параметры мастера.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Итог; исключений не бросает — все отказы приходят в <see cref="ScaffoldResult.Errors"/>.</returns>
    Task<ScaffoldResult> CreateAsync(ScaffoldOptions options, CancellationToken cancellationToken = default);
}

/// <summary>
/// Копирует шаблон <c>wot-mod-template</c> (где он лежит — см. <see cref="TemplateLocator"/>)
/// и подставляет в него имя нового мода.
///
/// <para>Подстановка — не косметика. Имя пакета встречается в шаблоне в трёх независимых
/// написаниях и в девяти файлах, причём три из них (<c>PACKAGE</c>, <c>ENTRY</c>, <c>RES_DIR</c>
/// в стабе) — это одно и то же имя в трёх РАЗНЫХ форматах: через точки, через точки с
/// <c>.main</c> и через слэши. Ошибка в любом из них даёт мод, который грузится, но не находит
/// своё тело: в python.log одна строка ERROR и тишина. Поэтому все три выводятся здесь из
/// одного поля, а не вводятся руками.</para>
///
/// <para>Отдельно важен <c>tools/test_stub_loader.py</c>: он жёстко зашивает
/// <c>mod_hello_tanker.py</c> и падает с FileNotFoundError, если переименовать только
/// <c>src/</c>. Он тоже переписывается — иначе у пользователя в свежесозданном проекте
/// сразу лежит сломанный тест.</para>
/// </summary>
public sealed class ProjectScaffolder : IProjectScaffolder
{
    /// <summary>Токены шаблона. Порядок важен: длинные раньше коротких, иначе
    /// <c>hello_tanker</c> съест часть <c>ru.local.hello_tanker</c>.
    /// Это идентификатор САМОГО шаблона, который мастер заменяет на идентификатор нового мода;
    /// менять его можно только вместе с шаблоном, иначе подстановка перестанет срабатывать.</summary>
    private const string TemplateModId = "ru.local.hello_tanker";
    private const string TemplateDisplayName = "Hello Tanker";
    private const string TemplateLogTag = "HelloTanker";
    private const string TemplatePackage = "hello_tanker";
    private const string TemplateVersion = "1.0.0";

    /// <summary>Каталоги, которые в новый проект не едут.</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".scratch", ".tools", ".vs", ".idea", "dist", "__pycache__", "bin", "obj", "node_modules"
    };

    /// <summary>
    /// Расширения, внутри которых делается подстановка. Всё остальное копируется побайтово:
    /// подставлять токены в .pyc или картинку — верный способ испортить файл.
    /// </summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".py", ".pyi", ".json", ".xml", ".md", ".ps1", ".txt", ".cfg", ".ini", ".yml", ".yaml", ".gitignore"
    };

    private readonly string _defaultTemplate;

    /// <summary>Создаёт мастер.</summary>
    /// <param name="defaultTemplateDirectory">
    /// Шаблон по умолчанию (обычно <see cref="Models.CockpitSettings.TemplateDir"/>), если он не
    /// задан в <see cref="ScaffoldOptions.TemplateDirectory"/>. Пусто — искать самому
    /// (<see cref="ProbeTemplateDirectory"/>).
    /// </param>
    public ProjectScaffolder(string? defaultTemplateDirectory = null) =>
        _defaultTemplate = defaultTemplateDirectory?.Trim() ?? string.Empty;

    /// <summary>
    /// Ищет шаблон без настроек кокпита: переменная окружения <c>WOTCOCKPIT_TEMPLATE_DIR</c>,
    /// затем соседние с кокпитом каталоги <c>wot-mod-template*</c>. Полный порядок с настройкой
    /// <c>templateDir</c> — в <see cref="TemplateLocator.Resolve(Models.CockpitSettings?)"/>.
    /// </summary>
    /// <returns>Путь к шаблону либо пустая строка.</returns>
    public static string ProbeTemplateDirectory() => TemplateLocator.Resolve((string?)null);

    /// <inheritdoc />
    public async Task<ScaffoldResult> CreateAsync(ScaffoldOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        IReadOnlyList<string> validation = options.Validate();
        if (validation.Count > 0) return ScaffoldResult.Failed(options.TargetDirectory, [.. validation]);

        string target = Path.GetFullPath(options.TargetDirectory);

        // Запрет писать в игру проверяется ПЕРВЫМ и от шаблона не зависит: до него мастер молча
        // раскладывал дерево исходников в <папка игры>\mods\<версия>, то есть в каталог,
        // который кокпит везде подписывает «ИГРА (только чтение)» и который читают панель
        // деплоя и детектор перекрытия .mtmod раскладкой. Проверено запуском один раз —
        // success=True и 14 файлов внутри игры.
        string? gameGuard = GameGuard(target, options.GameDirectory);
        if (gameGuard is not null) return ScaffoldResult.Failed(target, gameGuard);

        string template = ResolveTemplate(options);
        if (string.IsNullOrWhiteSpace(template) || !Directory.Exists(template))
        {
            return ScaffoldResult.Failed(options.TargetDirectory, TemplateLocator.NotFoundMessage(template));
        }

        if (!File.Exists(Path.Combine(template, "build.py")))
        {
            return ScaffoldResult.Failed(options.TargetDirectory,
                $"'{template}' не похож на шаблон мода: в нём нет build.py. " + TemplateLocator.WhereToGetHint + ".");
        }

        // Создать проект ВНУТРИ шаблона — это молча испортить сам шаблон.
        if (IsSameOrInside(target, Path.GetFullPath(template)))
        {
            return ScaffoldResult.Failed(target, "каталог назначения находится внутри шаблона — выберите другой");
        }

        string? parent = Path.GetDirectoryName(target);
        if (string.IsNullOrEmpty(parent))
        {
            return ScaffoldResult.Failed(target,
                "каталог назначения не может быть корнем диска — выберите подкаталог");
        }

        if (Directory.Exists(target) && !options.Overwrite)
        {
            bool empty = !Directory.EnumerateFileSystemEntries(target).Any();
            if (!empty)
            {
                return ScaffoldResult.Failed(target,
                    $"каталог '{target}' не пуст. Выберите пустой каталог или явно разрешите перезапись (Overwrite).");
            }
        }

        // Пишем в СОСЕДНИЙ временный каталог и переносим в target только после того, как
        // прошли пост-проверки. До этого провал мастера оставлял на диске 14 файлов, и
        // следующая попытка в тот же каталог отбивалась как «каталог не пуст» — то есть
        // одна опечатка в имени пакета делала выбранный каталог непригодным навсегда.
        string staging = Path.Combine(parent, ".cockpit-new-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(parent);

            ScaffoldResult result = await Task
                .Run(() => Copy(options, template, staging, target, cancellationToken), cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success) return result;

            Promote(staging, target);
            return result;
        }
        catch (OperationCanceledException)
        {
            return ScaffoldResult.Failed(target, "создание проекта отменено");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return ScaffoldResult.Failed(target, "не удалось создать проект: " + ex.Message);
        }
        finally
        {
            // Успех уже перенёс каталог, поэтому здесь остаётся только мусор неудачи.
            TryDeleteDirectory(staging);
        }
    }

    /// <summary>
    /// Каталог назначения лежит внутри установленной игры — тогда причина отказа, иначе <c>null</c>.
    /// </summary>
    /// <param name="target">Полный путь назначения.</param>
    /// <param name="gameDirectory">Корень игры из настроек; пусто — проверка не делается.</param>
    /// <returns>Текст отказа либо <c>null</c>.</returns>
    public static string? GameGuard(string target, string? gameDirectory)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (string.IsNullOrWhiteSpace(gameDirectory)) return null;

        string game;
        try
        {
            game = Path.GetFullPath(gameDirectory.Trim());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;                                   // путь к игре не задан толком — не наше дело
        }

        if (!IsSameOrInside(Path.GetFullPath(target), game)) return null;

        return $"каталог назначения находится внутри установленной игры ('{game}') — игра только для чтения. " +
               "Дерево исходников в mods\\<версия> засорит загрузчик и собьёт показания панели деплоя. " +
               "Выберите каталог вне игры, а в игру ставьте уже собранный .mtmod кнопкой «Установить».";
    }

    /// <summary>Переносит готовый проект из временного каталога в целевой.</summary>
    private static void Promote(string staging, string target)
    {
        if (!Directory.Exists(target))
        {
            Directory.Move(staging, target);
            return;
        }

        MergeInto(staging, target);
    }

    /// <summary>Переносит содержимое каталога в существующий, перезаписывая одноимённое.</summary>
    private static void MergeInto(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Move(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(source))
        {
            MergeInto(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Не удалось убрать за собой — хуже, чем ничего, но не повод рушить мастер.
        }
    }

    /// <param name="options">Параметры.</param>
    /// <param name="template">Шаблон.</param>
    /// <param name="writeRoot">Куда физически писать (временный каталог).</param>
    /// <param name="target">Куда проект приедет в итоге — только для протокола и путей в результате.</param>
    /// <param name="ct">Отмена.</param>
    private ScaffoldResult Copy(ScaffoldOptions options, string template, string writeRoot, string target,
        CancellationToken ct)
    {
        var log = new List<string>();
        string package = options.Package.Trim();

        // Подстановки: сначала самые длинные. 'ru.local.hello_tanker' обязан замениться
        // ДО того, как 'hello_tanker' превратится в имя нового пакета, иначе идентификатор
        // склеится из старого префикса и нового хвоста.
        (string From, string To)[] substitutions =
        [
            (TemplateModId, options.EffectiveModId),
            (TemplateDisplayName, options.EffectiveDisplayName),
            (TemplateLogTag, options.EffectiveLogTag),
            (TemplatePackage, package)
        ];

        Directory.CreateDirectory(writeRoot);
        log.Add($"шаблон: {template}");
        log.Add($"назначение: {target}");

        int files = 0;
        int replacements = 0;

        foreach (string sourceFile in EnumerateTemplateFiles(template))
        {
            ct.ThrowIfCancellationRequested();

            string relative = Path.GetRelativePath(template, sourceFile);
            string destRelative = RenamePath(relative, package);
            string destFile = Path.Combine(writeRoot, destRelative);

            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);

            if (TextExtensions.Contains(Path.GetExtension(sourceFile)))
            {
                string text = File.ReadAllText(sourceFile, Encoding.UTF8);

                // ОДИН проход, а не четыре Replace подряд: токены пересекаются
                // ('hello_tanker' — часть 'ru.local.hello_tanker'), и цепочка Replace
                // подставляла в СВОЙ ЖЕ результат. Пакет 'hello_tanker_pro' давал
                // <id>ru.local.hello_tanker_pro_pro</id>, а имя 'HelloTanker Pro' —
                // '<новый тег> Pro'. См. NameConventions.ReplaceTokensOnce.
                text = NameConventions.ReplaceTokensOnce(text, substitutions, out int before);

                text = ApplyMetadata(destRelative, text, options);

                // UTF8Encoding(false): без BOM. .py с BOM в py2.7 читается, но config.sample.json
                // с BOM ломает json.loads в самом моде — а мод его пример и копирует.
                File.WriteAllText(destFile, text, new UTF8Encoding(false));
                replacements += before;
            }
            else
            {
                File.Copy(sourceFile, destFile, overwrite: true);
            }

            files++;
        }

        string[] modsPath = ["src", "res", "scripts", "client", "gui", "mods"];
        string entryPoint = Path.Combine(target, Path.Combine(modsPath), $"mod_{package}.py");
        string packageDir = Path.Combine(target, Path.Combine(modsPath), package);

        // Проверяем то, что ЛЕЖИТ НА ДИСКЕ (временный каталог), а рапортуем то, где оно
        // окажется после переноса: пользователю нужен путь его проекта, а не служебный.
        string stagedEntryPoint = Path.Combine(writeRoot, Path.Combine(modsPath), $"mod_{package}.py");
        string stagedPackageDir = Path.Combine(writeRoot, Path.Combine(modsPath), package);

        log.Add($"скопировано файлов: {files}, подстановок: {replacements}");
        log.Add($"точка входа: mod_{package}.py");
        log.Add($"пакет мода: {package}");
        log.Add($"идентификатор: {options.EffectiveModId}, версия: {options.Version}");
        log.Add($"тег лога: [{options.EffectiveLogTag}] и [{options.EffectiveLogTag}/loader]");
        log.Add($"каталог конфигов: mods/configs/{package}/config.json");

        var errors = new List<string>();
        if (!File.Exists(stagedEntryPoint)) errors.Add($"точка входа не создана: {entryPoint}");
        if (!Directory.Exists(stagedPackageDir)) errors.Add($"каталог пакета не создан: {packageDir}");
        if (!File.Exists(Path.Combine(stagedPackageDir, "main.py"))) errors.Add("тело мода main.py не создано");

        // Ни одного старого токена остаться не должно: любой уцелевший 'hello_tanker'
        // в стабе — это мод, который грузится и не находит себя.
        string? leftover = FindLeftoverToken(writeRoot, substitutions);
        if (leftover is not null) errors.Add($"в созданном проекте остался токен шаблона: {leftover}");

        return new ScaffoldResult(
            errors.Count == 0, target, entryPoint, packageDir, files, replacements, errors, log);
    }

    /// <summary>
    /// Точечные правки, которые подстановкой токенов не делаются: версия и описание.
    /// Версия шаблона — «1.0.0», и заменять её глобально нельзя: эта же строка стоит
    /// в номерах версий чужих API и в примерах README.
    /// </summary>
    private static string ApplyMetadata(string relativePath, string text, ScaffoldOptions options)
    {
        string normalized = relativePath.Replace('\\', '/');

        if (normalized.Equals("src/meta.xml", StringComparison.OrdinalIgnoreCase))
        {
            text = ReplaceXmlElement(text, "version", options.Version);
            text = ReplaceXmlElement(text, "description", System.Security.SecurityElement.Escape(options.EffectiveDescription));
        }
        else if (normalized.Equals("build.py", StringComparison.OrdinalIgnoreCase))
        {
            // Используется, только когда meta.xml отсутствует, но пусть будет согласован.
            text = text.Replace(
                $"FALLBACK_ARCHIVE_NAME = \"{options.EffectiveModId}_{TemplateVersion}.mtmod\"",
                $"FALLBACK_ARCHIVE_NAME = \"{options.EffectiveModId}_{options.Version}.mtmod\"",
                StringComparison.Ordinal);
        }

        return text;
    }

    private static string ReplaceXmlElement(string xml, string element, string value)
    {
        string open = "<" + element + ">";
        string close = "</" + element + ">";
        int start = xml.IndexOf(open, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return xml;

        int from = start + open.Length;
        int end = xml.IndexOf(close, from, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? xml : string.Concat(xml.AsSpan(0, from), value, xml.AsSpan(end));
    }

    /// <summary>Переименовывает файл точки входа и каталог пакета в относительном пути.</summary>
    private static string RenamePath(string relativePath, string package)
    {
        string[] parts = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Equals(TemplatePackage, StringComparison.Ordinal))
            {
                parts[i] = package;
            }
            else if (parts[i].Equals($"mod_{TemplatePackage}.py", StringComparison.Ordinal))
            {
                parts[i] = $"mod_{package}.py";
            }
        }

        return Path.Combine(parts);
    }

    private static IEnumerable<string> EnumerateTemplateFiles(string root)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            foreach (string sub in Directory.EnumerateDirectories(dir))
            {
                if (!SkipDirectories.Contains(Path.GetFileName(sub))) stack.Push(sub);
            }

            foreach (string file in Directory.EnumerateFiles(dir))
            {
                // .pyc шаблона — мусор от чужих запусков, а не часть проекта.
                if (Path.GetExtension(file).Equals(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
                yield return file;
            }
        }
    }

    /// <summary>
    /// Ищет уцелевшие токены шаблона. Токен, который САМ входит в какое-нибудь из НОВЫХ
    /// значений (пакет <c>hello_tanker_pro</c> содержит <c>hello_tanker</c>, имя
    /// <c>HelloTanker Pro</c> содержит <c>HelloTanker</c>), проверять бессмысленно: он будет
    /// «находиться» в каждом правильно подставленном месте, и мастер отказывал бы в законном
    /// имени, оставив при этом на диске недоделанный проект.
    /// </summary>
    /// <param name="target">Каталог созданного проекта.</param>
    /// <param name="substitutions">Подстановки, которые были сделаны.</param>
    /// <returns>Описание находки либо <c>null</c>.</returns>
    private static string? FindLeftoverToken(string target, (string From, string To)[] substitutions)
    {
        bool checkPackage = !ValueContains(substitutions, TemplatePackage);
        bool checkTag = !ValueContains(substitutions, TemplateLogTag);
        if (!checkPackage && !checkTag) return null;

        foreach (string file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
        {
            if (!TextExtensions.Contains(Path.GetExtension(file))) continue;

            // README шаблона рассказывает про сам шаблон и законно упоминает его имя;
            // важны только исходники мода и скрипты сборки.
            string rel = Path.GetRelativePath(target, file).Replace('\\', '/');
            if (rel.Equals("README.md", StringComparison.OrdinalIgnoreCase)) continue;

            string text = File.ReadAllText(file, Encoding.UTF8);
            if (checkPackage && text.Contains(TemplatePackage, StringComparison.Ordinal)) return $"{rel}: {TemplatePackage}";
            if (checkTag && text.Contains(TemplateLogTag, StringComparison.Ordinal)) return $"{rel}: {TemplateLogTag}";
        }

        return null;
    }

    /// <summary>Хоть одно новое значение содержит этот токен шаблона.</summary>
    private static bool ValueContains((string From, string To)[] substitutions, string token)
    {
        foreach ((_, string to) in substitutions)
        {
            if (to.Contains(token, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    private static bool IsSameOrInside(string candidate, string root)
    {
        string a = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string b = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        return a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Шаблон для этого запуска: явно заданный в мастере, иначе <see cref="TemplateLocator"/>
    /// (переменная окружения, шаблон по умолчанию, соседние каталоги). Не нашлось ничего —
    /// возвращается шаблон по умолчанию как есть, чтобы отказ назвал то, что пробовали.
    /// </summary>
    private string ResolveTemplate(ScaffoldOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.TemplateDirectory)) return options.TemplateDirectory.Trim();

        string resolved = TemplateLocator.Resolve(_defaultTemplate);
        return resolved.Length > 0 ? resolved : _defaultTemplate;
    }
}
