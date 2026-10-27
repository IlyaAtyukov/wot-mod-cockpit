using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Lint;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Preflight;
using WotModCockpit.Core.Recipes;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.App.Stage3;

/// <summary>
/// Регистрация швов этапа 3. Отдельный класс, чтобы <c>AppServices</c> оставался коротким,
/// а подключение или отключение этапа 3 было одной строкой.
/// <para>
/// Правило то же, что у браузера API: если реализации Core в контейнере нет,
/// кладём заглушку — экран открывается и объясняет своё состояние, а кокпит работает.
/// Проверка идёт по <see cref="IServiceCollection"/>, а не по try/catch на резолве:
/// <c>ValidateOnBuild</c> уронил бы приложение на старте раньше, чем мы что-то поймали бы.
/// </para>
/// </summary>
public static class Stage3Registration
{
    /// <summary>Добавляет швы preflight, рецептов и мастера проекта.</summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    public static void AddBackends(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(d => d.ServiceType == typeof(IPreflightService)))
        {
            // Pyright и настройки берутся через GetService, а не через конструктор:
            // встроенный контейнер не умеет необязательные параметры, а линтер и хранилище
            // настроек — именно необязательные. Без них панель работает, просто без pyright
            // и без extraPaths на исходники клиента.
            services.AddSingleton<IPreflightBackend>(sp => new CorePreflightBackend(
                sp.GetRequiredService<IPreflightService>(),
                sp.GetService<IPyrightRunner>(),
                sp.GetService<ISettingsStore>()));
        }
        else
        {
            services.AddSingleton<IPreflightBackend, UnavailablePreflightBackend>();
        }

        if (services.Any(d => d.ServiceType == typeof(IRecipeService)))
        {
            services.AddSingleton<IRecipeBackend, CoreRecipeBackend>();
        }
        else
        {
            services.AddSingleton<IRecipeBackend, UnavailableRecipeBackend>();
        }

        if (services.Any(d => d.ServiceType == typeof(IProjectScaffolder)))
        {
            // Настройки — через GetService по той же причине, что у preflight: шаблон по умолчанию
            // берётся из templateDir, но без хранилища мастер обязан работать на пробе диска.
            services.AddSingleton<IScaffoldBackend>(sp => new CoreScaffoldBackend(
                sp.GetRequiredService<IProjectScaffolder>(),
                sp.GetService<ISettingsStore>()));
        }
        else
        {
            services.AddSingleton<IScaffoldBackend, UnavailableScaffoldBackend>();
        }
    }
}

// ===================================================================== preflight

/// <summary>
/// Шов панели Problems к <see cref="IPreflightService"/>.
/// <para>
/// Перевод почти прямой: находка Core уже содержит и последствие (<c>Message</c>),
/// и способ починки (<c>Hint</c>), и основание в исходниках клиента (<c>Reference</c>).
/// UI добавляет к ней две вещи, которых у Core нет и быть не должно: абзац «почему это
/// вообще правило» (<see cref="PreflightExplanations"/>) и зацепку для перехода в браузер
/// API — имя символа клиента, о котором идёт речь.
/// </para>
/// </summary>
public sealed class CorePreflightBackend : IPreflightBackend
{
    /// <summary>Импорт в строке-подсказке: из него достаётся имя модуля для браузера API.</summary>
    private static readonly Regex ImportLine = new(
        @"^\s*(?:from\s+(?<mod>[A-Za-z_][\w.]*)|import\s+(?<mod>[A-Za-z_][\w.]*))",
        RegexOptions.Compiled);

    /// <summary>Точечное имя модуля внутри свободного текста ссылки.</summary>
    private static readonly Regex DottedName = new(
        @"(?<![\w./\\:])[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+(?![\w/\\:])",
        RegexOptions.Compiled);

    /// <summary>
    /// Коды, которые одно правило Core выдаёт СВЕРХ своего <c>Id</c>.
    /// <para>
    /// Класс правила в Core один, а категорий находок у него может быть несколько:
    /// <c>UnknownApiRule</c> зарегистрирован под <c>WOT002</c>, но выдаёт ещё <c>WOT003</c>
    /// (модуль есть, имени нет) и <c>WOT004</c> (нет атрибута) — у них общий оракул
    /// клиентского API. Выключение считается по <c>Id</c> класса
    /// (<c>PreflightAnalyzer</c>: <c>if (!context.Options.IsEnabled(rule.Id)) continue;</c>),
    /// поэтому наивная передача скрытых кодов в Core делала так: пользователь скрывает
    /// в панели одну находку <c>WOT002</c>, а вместе с ней молча пропадают все находки
    /// <c>WOT003</c> и <c>WOT004</c>, при том что панель честно пишет «скрыто правил: 1 (WOT002)».
    /// Панель обязана не выглядеть чистой там, где она просто перестала считать.
    /// </para>
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> RuleCodeFamilies =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["WOT002"] = ["WOT002", "WOT003", "WOT004"]
        };

    private readonly IPreflightService _service;
    private readonly IPyrightRunner? _pyright;
    private readonly ISettingsStore? _settings;

    /// <summary>Создаёт шов.</summary>
    /// <param name="service">Служба правил Core.</param>
    /// <param name="pyright">Разовый запуск pyright либо <c>null</c>, если он не подключён.</param>
    /// <param name="settings">
    /// Настройки кокпита: из них берётся дерево исходников клиента для <c>extraPaths</c>.
    /// Без него pyright тонет в шуме — замерено: 19 диагностик против 6 на том же моде.
    /// </param>
    public CorePreflightBackend(
        IPreflightService service,
        IPyrightRunner? pyright = null,
        ISettingsStore? settings = null)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _pyright = pyright;
        _settings = settings;
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string UnavailableReason => string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<PreflightRuleCard> Rules
    {
        get
        {
            try
            {
                // Карточка нужна на КАЖДУЮ категорию находок, а не на класс правила:
                // UnknownApiRule — один класс с тремя кодами, и без разворота семейства
                // WOT003 и WOT004 отсутствовали бы в справочнике правил, хотя панель их показывает.
                var cards = new List<PreflightRuleCard>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (Core.Preflight.IPreflightRule rule in _service.Rules)
                {
                    string[] codes = RuleCodeFamilies.TryGetValue(rule.Id, out string[]? family)
                        ? family
                        : [rule.Id];

                    foreach (string code in codes)
                    {
                        if (!seen.Add(code)) continue;

                        cards.Add(new PreflightRuleCard(
                            code,
                            PreflightExplanations.TitleFor(code) is { Length: > 0 } title ? title : rule.Title,
                            PreflightExplanations.For(code),
                            ProblemSeverity.Warning));
                    }
                }

                return cards;
            }
            catch (Exception)
            {
                // Список правил — справка. Его отказ не имеет права ронять панель.
                return Array.Empty<PreflightRuleCard>();
            }
        }
    }

    /// <summary>
    /// Отбирает из скрытых кодов те, которые можно выключить в Core, НЕ погасив заодно
    /// категорию, которую пользователь не скрывал (см. <see cref="RuleCodeFamilies"/>).
    /// Правило с несколькими кодами выключается только тогда, когда скрыты все его коды;
    /// иначе оно продолжает считаться, а лишние находки отсеет фильтр панели.
    /// </summary>
    /// <param name="disabled">Коды, скрытые пользователем в панели.</param>
    /// <returns>Коды, безопасные для передачи в <c>PreflightOptions.DisabledRules</c>.</returns>
    public static IEnumerable<string> DisableableInCore(IReadOnlyCollection<string> disabled)
    {
        ArgumentNullException.ThrowIfNull(disabled);

        var hidden = new HashSet<string>(disabled, StringComparer.OrdinalIgnoreCase);

        foreach (string code in disabled)
        {
            if (RuleCodeFamilies.TryGetValue(code, out string[]? family) && !family.All(hidden.Contains))
            {
                continue;
            }

            yield return code;
        }
    }

    /// <inheritdoc />
    public async Task<PreflightReport> AnalyzeAsync(
        ModProject project,
        PreflightOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(options);

        // Настройки службы — общие и живут между прогонами: скрытые пользователем правила
        // надо не отфильтровывать после, а не считать вовсе.
        try
        {
            Core.Preflight.PreflightOptions core = _service.Options;
            core.DisabledRules.Clear();
            foreach (string rule in DisableableInCore(options.DisabledRules))
            {
                core.DisabledRules.Add(rule);
            }
        }
        catch (Exception)
        {
            // Реализация вправе отдавать неизменяемые настройки — тогда просто гоним всё.
        }

        PreflightResult result;
        try
        {
            result = await _service.AnalyzeAsync(project, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PreflightReport.Failed(Describe(ex));
        }

        var problems = new List<ProblemItem>(result.Diagnostics.Count);
        foreach (PreflightDiagnostic d in result.Diagnostics)
        {
            problems.Add(Translate(d));
        }

        string note = string.Empty;
        if (options.RunPyright)
        {
            note = await AppendPyrightAsync(project, problems, cancellationToken).ConfigureAwait(false);
        }

        return new PreflightReport(problems, result.FilesAnalyzed, result.Elapsed) { Note = note };
    }

    /// <summary>
    /// Точечные имена модулей САМОГО мода: <c>src/res/scripts/client/gui/mods/battle_clock/main.py</c>
    /// → <c>gui.mods.battle_clock.main</c>.
    ///
    /// <para>Нужны, чтобы pyright не ругался «Import "gui.mods.battle_clock" could not be
    /// resolved» на собственный пакет мода. Такой импорт правильный: движок монтирует
    /// <c>scripts/client</c> из архива мода в <c>sys.path</c>. Выразить это в <c>extraPaths</c>
    /// нельзя, и это измерено — добавление корня мода не помогает ни первым, ни последним,
    /// потому что <c>gui</c> в клиенте обычный пакет с <c>__init__.py</c> и pyright резолвит
    /// его туда. Гасится только доказанное: имена собраны обходом дерева проекта, опечатка
    /// в этот список не попадёт.</para>
    /// </summary>
    private static IReadOnlyList<string> OwnModules(ModProject project)
    {
        var names = new List<string>();

        try
        {
            foreach (string file in PreflightAnalyzer.EnumerateSources(project.SrcDir))
            {
                string relative = Path.GetRelativePath(project.ProjectDir, file).Replace('\\', '/');
                string dotted = PreflightAnalyzer.DottedModule(relative);
                if (dotted.Length > 0) names.Add(dotted);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Дерево могли снести между двумя щелчками — это не повод не запускать pyright.
        }

        return names;
    }

    /// <summary>
    /// Догоняет находки pyright и кладёт их в тот же список.
    /// <para>
    /// Отказ линтера — это ЗАМЕЧАНИЕ о прогоне, а не отказ прогона: 22 правила preflight уже
    /// отработали, и терять их результат из-за отсутствующего Node.js нельзя.
    /// </para>
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <param name="problems">Куда дописывать.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Строка замечания для панели.</returns>
    private async Task<string> AppendPyrightAsync(
        ModProject project,
        List<ProblemItem> problems,
        CancellationToken cancellationToken)
    {
        if (_pyright is null)
        {
            return "pyright не подключён";
        }

        PyrightResult result;
        try
        {
            result = await _pyright
                .RunAsync(new[] { project.SrcDir }, WotSrcDir(), null, cancellationToken, OwnModules(project))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "pyright: " + Describe(ex);
        }

        if (!result.IsCompleted)
        {
            return "pyright: " + result.Message;
        }

        foreach (PyrightDiagnostic d in result.Diagnostics)
        {
            problems.Add(Translate(d, project));
        }

        return $"pyright: находок {result.Diagnostics.Count}"
               + (result.SuppressedCount > 0 ? $", отброшено шума {result.SuppressedCount}" : string.Empty)
               + $" · {result.Duration.TotalMilliseconds:N0} мс";
    }

    /// <inheritdoc />
    public Task<PyrightProbe> ProbePyrightAsync(CancellationToken cancellationToken)
    {
        if (_pyright is null)
        {
            return Task.FromResult(new PyrightProbe(false, "pyright не подключён"));
        }

        // Locate() лезет в PATH и в каталоги npm — это диск и процессы, не UI-поток.
        return Task.Run(
            () =>
            {
                try
                {
                    string path = _pyright.Locate();

                    return path.Length == 0
                        ? new PyrightProbe(false, "pyright не найден — поставьте его командой  npm i -g pyright")
                        : new PyrightProbe(true, "pyright: " + path);
                }
                catch (Exception ex)
                {
                    return new PyrightProbe(false, "pyright: " + Describe(ex));
                }
            },
            cancellationToken);
    }

    /// <summary>Дерево декомпилированных исходников клиента для <c>extraPaths</c>.</summary>
    /// <returns>Путь либо <c>null</c>.</returns>
    private string? WotSrcDir()
    {
        try
        {
            string? dir = _settings?.Load().WotSrcDir;
            return string.IsNullOrWhiteSpace(dir) ? null : dir;
        }
        catch (Exception)
        {
            // Настройки могут быть недоступны — pyright просто отработает без extraPaths.
            return null;
        }
    }

    /// <summary>Диагностика pyright → строка панели Problems.</summary>
    /// <param name="d">Диагностика.</param>
    /// <param name="project">Проект — для относительного пути.</param>
    /// <returns>Находка.</returns>
    private static ProblemItem Translate(PyrightDiagnostic d, ModProject project)
    {
        string relative = d.File;
        try
        {
            if (!string.IsNullOrEmpty(project.ProjectDir) && Path.IsPathRooted(d.File))
            {
                relative = Path.GetRelativePath(project.ProjectDir, d.File).Replace('\\', '/');
            }
        }
        catch (Exception)
        {
            // Путь мог оказаться на другом томе — тогда покажем абсолютный.
        }

        return new ProblemItem
        {
            Code = "pyright:" + (d.Rule ?? "syntax"),
            Severity = d.Severity switch
            {
                PyrightSeverity.Error => ProblemSeverity.Error,
                PyrightSeverity.Warning => ProblemSeverity.Warning,
                _ => ProblemSeverity.Info
            },
            Message = d.Message,
            Explanation =
                "Находка pyright — внешнего анализатора типов, запущенного в режиме "
                + "typeCheckingMode=off и с extraPaths на декомпилированные исходники клиента. "
                + "В этом режиме он не проверяет типы, а ловит ровно то, чего не видят лексические "
                + "правила: неразрешённые импорты, обращения к несуществующим именам и синтаксис.\n\n"
                + "Помните, что код мода — это Python 2.7, а pyright разбирает его правилами "
                + "Python 3: часть замечаний про py2-встроенные имена отсеяна заранее, но "
                + "остаток стоит читать с этой поправкой.",
            Hint = string.Empty,
            FilePath = d.File,
            RelativePath = relative,
            Line = d.Line,
            Column = d.Column,
            Source = "pyright"
        };
    }

    private static ProblemItem Translate(PreflightDiagnostic d)
    {
        string reference = d.Reference ?? string.Empty;
        string snippet = d.Snippet ?? string.Empty;

        return new ProblemItem
        {
            Code = d.RuleId,
            Severity = Map(d.Severity),
            Message = d.Message,
            Explanation = PreflightExplanations.For(d.RuleId),
            Hint = d.Hint ?? string.Empty,
            Reference = reference,
            Snippet = snippet,
            FilePath = d.FilePath,
            RelativePath = d.RelativePath,
            Line = d.Line,
            Column = d.Column,
            Source = "preflight",
            ApiSymbol = ApiSymbolOf(d.RuleId, reference, snippet)
        };
    }

    private static ProblemSeverity Map(Core.Preflight.PreflightSeverity severity) => severity switch
    {
        Core.Preflight.PreflightSeverity.Error => ProblemSeverity.Error,
        Core.Preflight.PreflightSeverity.Warning => ProblemSeverity.Warning,
        _ => ProblemSeverity.Info
    };

    /// <summary>
    /// Имя, которое имеет смысл искать в браузере API. Только для правил про исчезнувший
    /// символ: у остальных ссылка указывает на исходник клиента, и переход по ней делает
    /// сама оболочка (см. <c>Stage3ViewModel.TrySplitReference</c>).
    /// </summary>
    /// <param name="ruleId">Код правила.</param>
    /// <param name="reference">Ссылка находки.</param>
    /// <param name="snippet">Строка мода.</param>
    /// <returns>Точечное имя модуля либо пустая строка.</returns>
    private static string ApiSymbolOf(string ruleId, string reference, string snippet)
    {
        if (ruleId is not ("WOT002" or "WOT003" or "WOT004"))
        {
            return string.Empty;
        }

        // У WOT003/WOT004 модуль стоит прямо в ссылке («…, gui.Scaleform.lobby_entry»).
        Match dotted = DottedName.Match(reference);
        if (dotted.Success && !dotted.Value.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            return dotted.Value;
        }

        // У WOT002 ссылка указывает на загрузчик, зато сама строка мода — это import.
        Match import = ImportLine.Match(snippet);
        return import.Success ? import.Groups["mod"].Value : string.Empty;
    }

    private static string Describe(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}

// ===================================================================== рецепты

/// <summary>
/// Шов каталога рецептов к <see cref="IRecipeService"/>.
/// <para>
/// Код рецепта берётся СЫРЫМ, с токенами шаблона (<c>my_mod</c>, <c>MyMod</c>): подстановку
/// делает UI на каждое нажатие клавиши в поле параметра (<see cref="RecipeRenderer"/>).
/// Гонять ради этого сервис было бы и медленнее, и бесполезнее — сервис умеет подставлять
/// только паспорт проекта, а в форме правят ещё и имена классов, методов и событий.
/// </para>
/// </summary>
public sealed class CoreRecipeBackend : IRecipeBackend
{
    private readonly IRecipeService _service;
    private readonly Dictionary<string, Recipe> _byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Создаёт шов.</summary>
    /// <param name="service">Каталог рецептов Core.</param>
    public CoreRecipeBackend(IRecipeService service)
        => _service = service ?? throw new ArgumentNullException(nameof(service));

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string UnavailableReason => string.Empty;

    /// <inheritdoc />
    public Task<IReadOnlyList<RecipeCard>> GetRecipesAsync(CancellationToken cancellationToken)
    {
        // Каталог встроен в сборку: чтение ресурсов — это миллисекунды, но уводим его
        // с UI-потока, потому что код всех шестнадцати рецептов читается здесь же.
        return Task.Run<IReadOnlyList<RecipeCard>>(
            () =>
            {
                var cards = new List<RecipeCard>();

                foreach (Recipe recipe in _service.All)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    string code;
                    try
                    {
                        code = recipe.Code;
                    }
                    catch (Exception ex)
                    {
                        // Битый ресурс не должен прятать весь каталог: показываем карточку
                        // с честным сообщением вместо кода.
                        code = "# рецепт не прочитан: " + ex.Message;
                    }

                    _byId[recipe.Id] = recipe;

                    cards.Add(new RecipeCard
                    {
                        Id = recipe.Id,
                        Title = recipe.Title,
                        Category = CategoryOf(recipe.Category),
                        Level = LevelOf(recipe.Difficulty),
                        Summary = recipe.Summary,
                        Rationale = recipe.Rationale,
                        Pitfall = recipe.Pitfall,
                        Code = code,
                        Symbols = recipe.Requirements
                            .Select(r => new RecipeSymbol(r.Module, r.Name, r.IsCheckable))
                            .ToList()
                    });
                }

                return cards;
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<RecipeCompatibility> CheckAsync(RecipeCard recipe, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        if (!_byId.TryGetValue(recipe.Id, out Recipe? core))
        {
            return Task.FromResult(RecipeCompatibility.Unknown);
        }

        return Task.Run(
            () =>
            {
                RecipeCompatibilityReport report = _service.CheckCompatibility(core);

                // Имя RecipeCompatibility занято записью UI из этого же пространства имён,
                // поэтому перечисление Core приходится называть полностью.
                return new RecipeCompatibility(
                    report.Status switch
                    {
                        Core.Recipes.RecipeCompatibility.Ok => RecipeCompatibilityState.Ok,
                        Core.Recipes.RecipeCompatibility.NeedsReview => RecipeCompatibilityState.NeedsReview,
                        _ => RecipeCompatibilityState.Unknown
                    },
                    report.Message,
                    report.Missing.Select(m => m.Display).ToList());
            },
            cancellationToken);
    }

    /// <summary>Рубрика Core → заголовок группы в списке.</summary>
    /// <param name="category">Рубрика.</param>
    /// <returns>Русский заголовок.</returns>
    private static string CategoryOf(RecipeCategory category) => category switch
    {
        RecipeCategory.Lifecycle => "Каркас и жизненный цикл",
        RecipeCategory.Logging => "Логи и диагностика",
        RecipeCategory.Ui => "Показать игроку",
        RecipeCategory.Events => "События клиента",
        RecipeCategory.GameState => "Состояние игры",
        RecipeCategory.Input => "Ввод и хоткеи",
        RecipeCategory.Configuration => "Настройки",
        RecipeCategory.Patching => "Монкипатчи",
        RecipeCategory.Interop => "API чужих модов",
        _ => "Прочее"
    };

    private static RecipeLevel LevelOf(RecipeDifficulty difficulty) => difficulty switch
    {
        RecipeDifficulty.Beginner => RecipeLevel.Beginner,
        RecipeDifficulty.Advanced => RecipeLevel.Advanced,
        _ => RecipeLevel.Intermediate
    };
}

// ===================================================================== мастер проекта

/// <summary>
/// Шов мастера нового проекта к <see cref="IProjectScaffolder"/>.
/// <para>
/// Проверка формы отдана Core целиком (<see cref="ScaffoldOptions.Validate"/> и
/// <see cref="NameConventions"/>): именно там лежит знание, которого у UI нет — список имён,
/// занятых модулями клиента, и требование к идентификатору, из-за которого build.py
/// переименовывает артефакт.
/// </para>
/// <para>
/// Предпросмотр строится ЗДЕСЬ и намеренно не повторяет обход шаблона файл в файл: показывать
/// сто одинаковых строк «копия» бессмысленно. Показываются места подстановки — те самые,
/// в которых расходится ручное переименование, — и одна сводная строка про остальное.
/// </para>
/// </summary>
public sealed class CoreScaffoldBackend : IScaffoldBackend
{
    /// <summary>Каталоги шаблона, которые в новый проект не едут (тот же список, что у Core).</summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".scratch", ".tools", ".vs", ".idea", "dist", "__pycache__", "bin", "obj", "node_modules"
    };

    private readonly IProjectScaffolder _scaffolder;

    /// <summary>Создаёт шов.</summary>
    /// <param name="scaffolder">Мастер Core.</param>
    /// <param name="settings">
    /// Хранилище настроек: шаблон по умолчанию берётся из <c>templateDir</c>. <c>null</c> — только
    /// переменная окружения и соседние каталоги (<see cref="TemplateLocator"/>).
    /// </param>
    public CoreScaffoldBackend(IProjectScaffolder scaffolder, ISettingsStore? settings = null)
    {
        _scaffolder = scaffolder ?? throw new ArgumentNullException(nameof(scaffolder));

        try
        {
            DefaultTemplateDir = TemplateLocator.Resolve(settings?.Load());
        }
        catch (Exception)
        {
            DefaultTemplateDir = string.Empty;
        }
    }

    /// <inheritdoc />
    public bool IsAvailable => true;

    /// <inheritdoc />
    public string UnavailableReason => string.Empty;

    /// <inheritdoc />
    public string DefaultTemplateDir { get; }

    /// <inheritdoc />
    public IReadOnlyList<ScaffoldIssue> Validate(ScaffoldRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var issues = ToOptions(request)
                .Validate()
                .Select(message => new ScaffoldIssue(FieldOf(message), message, true))
                .ToList();

            // Запрет писать внутрь игры спрашивается ЗДЕСЬ, а не только в Core: тогда кнопка
            // «создать» гаснет и причина видна ДО того, как на диск что-то попало.
            string? game = string.IsNullOrWhiteSpace(request.TargetDir)
                ? null
                : ProjectScaffolder.GameGuard(request.TargetDir, request.GameDir);

            if (game is not null)
            {
                issues.Add(new ScaffoldIssue(nameof(ScaffoldRequest.TargetDir), game, true));
            }

            return issues;
        }
        catch (Exception ex)
        {
            return new[] { new ScaffoldIssue(string.Empty, "проверка мастера: " + ex.Message, false) };
        }
    }

    /// <inheritdoc />
    public Task<ScaffoldPlan> PlanAsync(ScaffoldRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        return Task.Run(() => BuildPlan(request, cancellationToken), cancellationToken);
    }

    private ScaffoldPlan BuildPlan(ScaffoldRequest request, CancellationToken cancellationToken)
    {
        string template = string.IsNullOrWhiteSpace(request.TemplateDir) ? DefaultTemplateDir : request.TemplateDir;
        var issues = new List<ScaffoldIssue>();

        if (string.IsNullOrWhiteSpace(template) || !Directory.Exists(template))
        {
            issues.Add(new ScaffoldIssue(nameof(ScaffoldRequest.TemplateDir), TemplateLocator.NotFoundMessage(template), true));
            return new ScaffoldPlan(Array.Empty<ScaffoldFileAction>(), issues);
        }

        string package = request.Package.Trim();
        var files = new List<ScaffoldFileAction>
        {
            new(
                $"src/res/scripts/client/gui/mods/mod_{package}.py",
                ScaffoldActionKind.Rename,
                "точка входа: загрузчик берёт только mod_*.pyc прямо из этого каталога"),
            new(
                $"src/res/scripts/client/gui/mods/{package}/",
                ScaffoldActionKind.Directory,
                "тело мода; имя пакета попадёт в PACKAGE, ENTRY и RES_DIR — три разных написания"),
            new(
                $"src/res/scripts/client/gui/mods/{package}/main.py",
                ScaffoldActionKind.Rewrite,
                // Скобки берутся ОДНИ: тег мог приехать уже в скобках ('[BattleTimer]' —
                // ровно так он написан в шаблоне), и предпросмотр показывал [[BattleTimer]].
                $"MOD_ID, MOD_NAME, LOG_PREFIX = [{NameConventions.NormalizeLogTag(request.LogTag)}], " +
                "логгер, каталог конфигов и _PATCH_MARKER"),
            new(
                "src/meta.xml",
                ScaffoldActionKind.Rewrite,
                $"<id>{request.ModId}</id>, <version>{request.Version}</version>, <name>{request.DisplayName}</name>"),
            new(
                "tools/test_stub_loader.py",
                ScaffoldActionKind.Rewrite,
                "проверено запуском: без переписывания тест падает с FileNotFoundError в свежем проекте"),
            new(
                "build.py",
                ScaffoldActionKind.Rewrite,
                "FALLBACK_ARCHIVE_NAME; сама сборка data-driven и точку входа находит сканированием")
        };

        int copied = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                string relative = Path.GetRelativePath(template, file);
                if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Any(part => SkipDirectories.Contains(part)))
                {
                    continue;
                }

                copied++;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            issues.Add(new ScaffoldIssue(nameof(ScaffoldRequest.TemplateDir), "шаблон не прочитан: " + ex.Message, false));
        }

        if (copied > 0)
        {
            files.Add(new ScaffoldFileAction(
                $"…и ещё {Math.Max(0, copied - 4)} файлов шаблона",
                ScaffoldActionKind.Copy,
                "копируются как есть; .git, .scratch, .tools, dist и __pycache__ не едут"));
        }

        return new ScaffoldPlan(files, issues);
    }

    /// <inheritdoc />
    public async Task<ScaffoldResult> CreateAsync(
        ScaffoldRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        Core.Scaffold.ScaffoldResult result;
        try
        {
            progress?.Report("копируем шаблон…");
            result = await _scaffolder.CreateAsync(ToOptions(request), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ScaffoldResult.Failed("создание прервано");
        }
        catch (Exception ex)
        {
            return ScaffoldResult.Failed("мастер: " + ex.Message);
        }

        string message = result.Success
            ? $"проект создан: файлов {result.FilesWritten}, подстановок {result.Replacements}"
            : "проект не создан: " + string.Join("; ", result.Errors);

        return new ScaffoldResult(result.Success, result.TargetDirectory, message, result.Log);
    }

    private ScaffoldOptions ToOptions(ScaffoldRequest request) => new()
    {
        TargetDirectory = request.TargetDir,
        Package = request.Package.Trim(),
        ModId = Empty(request.ModId),
        DisplayName = Empty(request.DisplayName),
        Version = string.IsNullOrWhiteSpace(request.Version) ? "1.0.0" : request.Version.Trim(),
        LogTag = Empty(request.LogTag),
        Description = Empty(request.Description),
        TemplateDirectory = string.IsNullOrWhiteSpace(request.TemplateDir) ? DefaultTemplateDir : request.TemplateDir.Trim(),
        GameDirectory = Empty(request.GameDir)
    };

    private static string? Empty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// К какому полю формы отнести сообщение Core. Нужно, чтобы замечание встало под своим
    /// полем, а не общим списком внизу.
    /// </summary>
    /// <param name="message">Сообщение.</param>
    /// <returns>Имя поля либо пустая строка.</returns>
    private static string FieldOf(string message)
    {
        if (message.Contains("пакет", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ScaffoldRequest.Package);
        }

        if (message.Contains("идентификатор", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ScaffoldRequest.ModId);
        }

        if (message.Contains("версия", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ScaffoldRequest.Version);
        }

        if (message.Contains("тег лога", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ScaffoldRequest.LogTag);
        }

        if (message.Contains("каталог", StringComparison.OrdinalIgnoreCase))
        {
            return nameof(ScaffoldRequest.TargetDir);
        }

        return string.Empty;
    }
}
