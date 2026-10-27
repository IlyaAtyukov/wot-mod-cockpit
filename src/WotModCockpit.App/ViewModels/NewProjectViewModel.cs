using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WotModCockpit.App.Stage3;

// Псевдонимы, а не импорт namespace: в Core и в шве App лежат ОДНОИМЁННЫЕ ScaffoldResult,
// и открытый using сделал бы имя неоднозначным.
using NameConventions = WotModCockpit.Core.Scaffold.NameConventions;
using CoreScaffolder = WotModCockpit.Core.Scaffold.ProjectScaffolder;

namespace WotModCockpit.App.ViewModels;

/// <summary>Одна строка таблицы «что куда подставится».</summary>
/// <param name="Value">Значение из формы.</param>
/// <param name="Where">Где оно окажется.</param>
public sealed record SubstitutionRow(string Value, string Where);

/// <summary>
/// Мастер нового проекта мода: создаёт проект из шаблона <c>wot-mod-template</c> (отдельный
/// репозиторий; где он ищется — см. <c>TemplateLocator</c>), подставляя во все места собственные
/// идентификатор, имя, пакет, точку входа и тег лога.
/// <para>
/// Почему это не «скопируй папку и переименуй». Одно имя пакета живёт в шаблоне в трёх
/// РАЗНЫХ написаниях (<c>gui.mods.&lt;pkg&gt;</c>, <c>gui.mods.&lt;pkg&gt;.main</c>,
/// <c>scripts/client/gui/mods/&lt;pkg&gt;</c>), тег лога — в двух (<c>[Tag]</c> и
/// <c>[Tag/loader]</c>), а маркер монкипатча обязан быть уникальным, иначе второй мод из того
/// же шаблона увидит на методе ЧУЖОЙ маркер, решит «уже пропатчено мной» и молча не поставит
/// свою логику. Ошибка в любом из этих мест даёт мод, который грузится и ничего не делает,
/// с одной строкой в python.log.
/// </para>
/// <para>
/// Поэтому форма — пять полей, из которых всё остальное ВЫВОДИТСЯ, а не вводится ещё раз.
/// </para>
/// </summary>
public sealed partial class NewProjectViewModel : ObservableObject
{
    /// <summary>Имя пакета: то же, что требует loader и импорт python.</summary>
    private static readonly Regex PackageRe = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Версия мода: build.py кладёт её в имя артефакта.</summary>
    private static readonly Regex VersionRe = new(@"^\d+(\.\d+){0,3}$", RegexOptions.Compiled);

    /// <summary>Тег лога: голое имя без скобок и пробелов.</summary>
    private static readonly Regex TagRe = new(@"^[A-Za-z][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Идентификатор мода: точки допустимы, разделители пути — нет.</summary>
    private static readonly Regex ModIdRe = new(@"^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.Compiled);

    private readonly IScaffoldBackend _backend;

    private CancellationTokenSource? _planCts;

    private bool _packageEdited;
    private bool _modIdEdited;
    private bool _logTagEdited;
    private bool _suspend;

    /// <summary>Создаёт модель диалога.</summary>
    /// <param name="backend">Шов к скаффолдеру Core.</param>
    /// <param name="parentDir">Каталог, рядом с которым логично создать проект (обычно родитель открытого).</param>
    /// <param name="gameDir">
    /// Корень установленной игры. Нужен, чтобы кнопка «создать» погасла ещё до записи, если
    /// пользователь выбрал каталог внутри игры: игра для кокпита только для чтения.
    /// </param>
    /// <param name="templateDir">
    /// Шаблон из настроек кокпита (<c>templateDir</c>). Пусто — шаблон по умолчанию от скаффолдера.
    /// </param>
    /// <param name="modIdPrefix">
    /// Префикс идентификатора из настроек кокпита (<c>modIdPrefix</c>). Пусто — <c>ru.local</c>.
    /// </param>
    public NewProjectViewModel(
        IScaffoldBackend backend,
        string? parentDir = null,
        string? gameDir = null,
        string? templateDir = null,
        string? modIdPrefix = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));

        _templateDir = string.IsNullOrWhiteSpace(templateDir) ? SafeDefaultTemplate(backend) : templateDir.Trim();
        _parentDir = parentDir ?? string.Empty;
        GameDir = gameDir ?? string.Empty;
        ModIdPrefix = NormalizePrefix(modIdPrefix);

        Revalidate();
    }

    /// <summary>
    /// Префикс, из которого выводится идентификатор: <c>&lt;префикс&gt;.&lt;пакет&gt;</c>.
    /// По умолчанию <c>ru.local</c>; для модов, которые будут публиковаться, лучше свой обратный
    /// домен (<c>io.github.&lt;ник&gt;</c>) — меняется полем <c>modIdPrefix</c> в settings.json.
    /// </summary>
    public string ModIdPrefix { get; }

    /// <summary>Подсказка под полем идентификатора: откуда взялся префикс и почему стоит взять свой.</summary>
    public string ModIdHint =>
        $"по умолчанию {ModIdPrefix}.<пакет>. Если мод будет публиковаться, возьмите свой обратный домен, " +
        "например io.github.<ник>.my_mod, — тогда id разных авторов не пересекутся " +
        "(постоянный префикс задаётся полем modIdPrefix в settings.json кокпита)";

    /// <summary>
    /// Вызывается, когда пользователь выбрал годный шаблон (кнопкой «…» или успешным созданием):
    /// главный экран запоминает его в настройках. <c>null</c> — запоминать некуда.
    /// </summary>
    public Action<string>? TemplateChosen { get; set; }

    private static string NormalizePrefix(string? prefix)
    {
        string p = (prefix ?? string.Empty).Trim().Trim('.');
        return p.Length == 0 ? Core.Models.CockpitSettings.DefaultModIdPrefix : p;
    }

    /// <summary>
    /// Корень установленной игры. Не поле формы: пользователь его здесь не правит, оно
    /// приходит из настроек кокпита и служит только запретом «не писать внутрь игры».
    /// </summary>
    public string GameDir { get; }

    private static string SafeDefaultTemplate(IScaffoldBackend backend)
    {
        try
        {
            return backend.DefaultTemplateDir ?? string.Empty;
        }
        catch (Exception)
        {
            // Заглушка вправе не знать шаблона — это не повод не открыть диалог.
            return string.Empty;
        }
    }

    // =================================================================== поля формы

    /// <summary>Отображаемое имя мода: <c>Battle Timer</c>.</summary>
    [ObservableProperty]
    private string _displayName = string.Empty;

    /// <summary>Имя python-пакета: <c>battle_timer</c>.</summary>
    [ObservableProperty]
    private string _package = string.Empty;

    /// <summary>Идентификатор мода: <c>&lt;префикс&gt;.battle_timer</c>, по умолчанию <c>ru.local.battle_timer</c>.</summary>
    [ObservableProperty]
    private string _modId = string.Empty;

    /// <summary>Версия: <c>1.0.0</c>.</summary>
    [ObservableProperty]
    private string _version = "1.0.0";

    /// <summary>Тег лога без скобок: <c>BattleTimer</c>.</summary>
    [ObservableProperty]
    private string _logTag = string.Empty;

    /// <summary>Описание для meta.xml.</summary>
    [ObservableProperty]
    private string _description = string.Empty;

    /// <summary>Каталог, ВНУТРИ которого будет создана папка проекта.</summary>
    [ObservableProperty]
    private string _parentDir = string.Empty;

    /// <summary>Каталог шаблона, откуда копировать.</summary>
    [ObservableProperty]
    private string _templateDir = string.Empty;

    partial void OnDisplayNameChanged(string value)
    {
        if (!_packageEdited)
        {
            SetDerived(() => Package = ToSnakeCase(value));
        }

        Revalidate();
    }

    partial void OnPackageChanged(string value)
    {
        if (!_suspend)
        {
            _packageEdited = value.Length > 0;
        }

        if (!_modIdEdited)
        {
            SetDerived(() => ModId = value.Length == 0 ? string.Empty : ModIdPrefix + "." + value);
        }

        if (!_logTagEdited)
        {
            SetDerived(() => LogTag = ToPascalCase(value));
        }

        Revalidate();
    }

    partial void OnModIdChanged(string value)
    {
        if (!_suspend)
        {
            _modIdEdited = value.Length > 0;
        }

        Revalidate();
    }

    partial void OnLogTagChanged(string value)
    {
        if (!_suspend)
        {
            _logTagEdited = value.Length > 0;
        }

        Revalidate();
    }

    partial void OnVersionChanged(string value) => Revalidate();

    partial void OnParentDirChanged(string value) => Revalidate();

    partial void OnTemplateDirChanged(string value) => Revalidate();

    private void SetDerived(Action set)
    {
        bool previous = _suspend;
        _suspend = true;
        try
        {
            set();
        }
        finally
        {
            _suspend = previous;
        }
    }

    // =================================================================== выведенные значения

    /// <summary>Имя файла точки входа: <c>mod_&lt;пакет&gt;.py</c>. Иначе загрузчик мод не увидит.</summary>
    public string EntryPointFile => Package.Length == 0 ? string.Empty : $"mod_{Package}.py";

    /// <summary>Полный путь будущего проекта.</summary>
    public string TargetDir
    {
        get
        {
            if (ParentDir.Length == 0 || Package.Length == 0)
            {
                return string.Empty;
            }

            try
            {
                return Path.Combine(ParentDir, Package);
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Ожидаемое имя артефакта сборки — его формирует build.py как <c>&lt;id&gt;_&lt;версия&gt;.mtmod</c>.</summary>
    public string ArtifactName => ModId.Length == 0 || Version.Length == 0
        ? string.Empty
        : $"{ModId}_{Version}.mtmod";

    /// <summary>
    /// Таблица «что куда подставится». Это не украшение: именно из-за трёх написаний имени
    /// пакета и двух тегов лога ручное переименование шаблона и ломается.
    /// </summary>
    public ObservableCollection<SubstitutionRow> Substitutions { get; } = new();

    private void RebuildSubstitutions()
    {
        Substitutions.Clear();

        if (Package.Length == 0)
        {
            return;
        }

        Substitutions.Add(new SubstitutionRow(
            $"mod_{Package}.py",
            "src\\res\\scripts\\client\\gui\\mods\\ — точка входа; загрузчик берёт только mod_*.pyc"));

        Substitutions.Add(new SubstitutionRow(
            Package,
            "каталог тела мода, PACKAGE, ENTRY и RES_DIR в стабе — три разных написания одного имени"));

        if (ModId.Length > 0)
        {
            Substitutions.Add(new SubstitutionRow(
                ModId,
                "meta.xml <id>, MOD_ID в стабе и в main.py; из него же имя .mtmod"));
        }

        // Скобки берутся ОДНИ: пользователь вправе ввести тег уже в скобках ('[BattleTimer]'
        // — ровно так он написан в шаблоне), и предпросмотр показывал бы [[BattleTimer]].
        string tag = NameConventions.NormalizeLogTag(LogTag);
        if (tag.Length > 0)
        {
            Substitutions.Add(new SubstitutionRow(
                $"[{tag}]  и  [{tag}/loader]",
                "LOG_PREFIX тела мода и тег стаба; по ним фильтрует живой python.log"));

            Substitutions.Add(new SubstitutionRow(
                tag,
                "logging.getLogger(...) в main.py"));
        }

        Substitutions.Add(new SubstitutionRow(
            $"_{Package}_patched",
            "маркер монкипатча — обязан быть уникальным, иначе моды из одного шаблона затрут друг друга"));

        Substitutions.Add(new SubstitutionRow(
            $"mods\\configs\\{Package}\\config.json",
            "путь конфига мода в каталоге игры"));

        if (DisplayName.Length > 0)
        {
            Substitutions.Add(new SubstitutionRow(DisplayName, "meta.xml <name>, MOD_NAME в main.py"));
        }
    }

    // =================================================================== валидация

    /// <summary>Ошибка поля «отображаемое имя».</summary>
    [ObservableProperty]
    private string _displayNameError = string.Empty;

    /// <summary>Ошибка поля «имя пакета».</summary>
    [ObservableProperty]
    private string _packageError = string.Empty;

    /// <summary>Ошибка поля «идентификатор мода».</summary>
    [ObservableProperty]
    private string _modIdError = string.Empty;

    /// <summary>Ошибка поля «версия».</summary>
    [ObservableProperty]
    private string _versionError = string.Empty;

    /// <summary>Ошибка поля «тег лога».</summary>
    [ObservableProperty]
    private string _logTagError = string.Empty;

    /// <summary>Ошибка поля «каталог назначения».</summary>
    [ObservableProperty]
    private string _parentDirError = string.Empty;

    /// <summary>Ошибка поля «каталог шаблона».</summary>
    [ObservableProperty]
    private string _templateDirError = string.Empty;

    /// <summary>Замечания, которые дал скаффолдер (например «пакет совпал с модулем клиента»).</summary>
    public ObservableCollection<ScaffoldIssue> BackendIssues { get; } = new();

    /// <summary>Форма заполнена корректно.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isValid;

    private void Revalidate()
    {
        DisplayNameError = DisplayName.Trim().Length == 0
            ? "имя мода обязательно — оно попадёт в meta.xml и в список модов"
            : string.Empty;

        PackageError = ValidatePackage();
        ModIdError = ValidateModId();
        VersionError = VersionRe.IsMatch(Version.Trim())
            ? string.Empty
            : "версия вида 1.0.0 — build.py подставляет её в имя .mtmod";
        // Скобки СНАЧАЛА снимаются, потом проверяется остаток: '[TankRadar]' — законная
        // форма ввода, потому что именно в таком виде тег стоит в шаблоне (LOG_PREFIX)
        // и в его README (grep -F "[HelloTanker/loader]").
        LogTagError = TagRe.IsMatch(NameConventions.NormalizeLogTag(LogTag))
            ? string.Empty
            : "тег лога — латиница без пробелов: в коде он станет [Тег] и [Тег/loader]. " +
              "Скобки писать не обязательно, они подставятся сами";

        ParentDirError = ValidateParent();
        TemplateDirError = ValidateTemplate();

        RebuildSubstitutions();
        OnPropertyChanged(nameof(TargetDir));
        OnPropertyChanged(nameof(EntryPointFile));
        OnPropertyChanged(nameof(ArtifactName));

        IsValid = DisplayNameError.Length == 0
                  && PackageError.Length == 0
                  && ModIdError.Length == 0
                  && VersionError.Length == 0
                  && LogTagError.Length == 0
                  && ParentDirError.Length == 0
                  && TemplateDirError.Length == 0;

        _ = RefreshPlanAsync();
    }

    private string ValidatePackage()
    {
        string p = Package.Trim();

        if (p.Length == 0)
        {
            return "имя пакета обязательно: из него выводится имя точки входа mod_<пакет>.py";
        }

        if (!PackageRe.IsMatch(p))
        {
            return "только строчная латиница, цифры и подчёркивание, первый символ — буква";
        }

        if (p.Length > 40)
        {
            return "слишком длинно: имя пакета видно в каждом пути внутри мода";
        }

        if (PythonKeywords.Contains(p))
        {
            return "это ключевое слово Python — импорт такого пакета не соберётся";
        }

        return string.Empty;
    }

    private string ValidateModId()
    {
        string id = ModId.Trim();

        if (id.Length == 0)
        {
            return "идентификатор обязателен: build.py делает из него имя <id>_<версия>.mtmod";
        }

        if (id.Contains('/') || id.Contains('\\'))
        {
            return "разделители пути запрещены: build.py вырежет их и артефакт получит другое имя";
        }

        if (!ModIdRe.IsMatch(id))
        {
            return "допустимы латиница, цифры, точка, дефис и подчёркивание";
        }

        return string.Empty;
    }

    private string ValidateParent()
    {
        string dir = ParentDir.Trim();

        if (dir.Length == 0)
        {
            return "выберите каталог, внутри которого создать проект";
        }

        try
        {
            if (!Directory.Exists(dir))
            {
                return "каталога нет: " + dir;
            }

            string target = TargetDir;
            if (target.Length > 0 && Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
            {
                return $"каталог {target} уже существует и не пуст";
            }

            // Игра — только для чтения. Проверяем здесь, чтобы кнопка «создать» погасла
            // ДО записи: раньше мастер спокойно раскладывал 14 файлов в <папка игры>\mods.
            string? insideGame = CoreScaffolder.GameGuard(target.Length > 0 ? target : dir, GameDir);
            if (insideGame is not null) return insideGame;
        }
        catch (Exception ex)
        {
            return "каталог недоступен: " + ex.Message;
        }

        return string.Empty;
    }

    private string ValidateTemplate()
    {
        string dir = TemplateDir.Trim();

        if (dir.Length == 0)
        {
            return "укажите каталог шаблона мода (тот, где лежат build.py и src\\meta.xml). " +
                   "Шаблон — отдельный репозиторий wot-mod-template, см. README кокпита";
        }

        try
        {
            if (!Directory.Exists(dir))
            {
                return "каталога нет: " + dir;
            }

            if (!File.Exists(Path.Combine(dir, "build.py")))
            {
                return "в каталоге нет build.py — это не шаблон мода (нужен репозиторий wot-mod-template)";
            }
        }
        catch (Exception ex)
        {
            return "каталог недоступен: " + ex.Message;
        }

        return string.Empty;
    }

    // =================================================================== предпросмотр

    /// <summary>Что будет создано.</summary>
    public ObservableCollection<ScaffoldFileAction> PlanFiles { get; } = new();

    /// <summary>Сводка плана.</summary>
    [ObservableProperty]
    private string _planStatus = string.Empty;

    /// <summary>Идёт построение плана или создание.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateCommand))]
    private bool _isBusy;

    /// <summary>Строка состояния диалога.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Мастер подключён.</summary>
    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>Почему мастер недоступен.</summary>
    public string UnavailableReason => _backend.IsAvailable ? string.Empty : _backend.UnavailableReason;

    private async Task RefreshPlanAsync()
    {
        _planCts?.Cancel();
        _planCts?.Dispose();
        _planCts = null;

        BackendIssues.Clear();

        if (!IsValid || !_backend.IsAvailable)
        {
            PlanFiles.Clear();
            PlanStatus = _backend.IsAvailable
                ? "заполните форму — здесь появится список того, что будет создано"
                : _backend.UnavailableReason;
            return;
        }

        var cts = new CancellationTokenSource();
        _planCts = cts;

        ScaffoldRequest request = BuildRequest();

        try
        {
            foreach (ScaffoldIssue issue in _backend.Validate(request))
            {
                BackendIssues.Add(issue);
            }
        }
        catch (Exception ex)
        {
            BackendIssues.Add(new ScaffoldIssue(string.Empty, "проверка мастера: " + Short(ex), false));
        }

        try
        {
            ScaffoldPlan plan = await _backend.PlanAsync(request, cts.Token).ConfigureAwait(true);

            if (!ReferenceEquals(_planCts, cts))
            {
                return;
            }

            PlanFiles.Clear();
            foreach (ScaffoldFileAction file in plan.Files)
            {
                PlanFiles.Add(file);
            }

            foreach (ScaffoldIssue issue in plan.Issues)
            {
                BackendIssues.Add(issue);
            }

            PlanStatus = plan.Files.Count == 0
                ? "план пуст — скаффолдер не сообщил, что будет создано"
                : $"будет создано записей: {plan.Files.Count}";
        }
        catch (OperationCanceledException)
        {
            // Форму правят дальше — придёт следующий план.
        }
        catch (Exception ex)
        {
            PlanFiles.Clear();
            PlanStatus = "план не построен: " + Short(ex);
        }

        // Блокирующее замечание скаффолдера снимает готовность формы: он знает то,
        // чего UI знать не может (например, что имя пакета занято модулем клиента).
        if (BackendIssues.Any(i => i.Blocking))
        {
            IsValid = false;
        }
    }

    private ScaffoldRequest BuildRequest() => new()
    {
        TemplateDir = TemplateDir.Trim(),
        TargetDir = TargetDir,
        ModId = ModId.Trim(),
        Package = Package.Trim(),
        DisplayName = DisplayName.Trim(),
        Version = Version.Trim(),
        // Скобки снимаются здесь же: пользователь вправе скопировать тег из шаблона
        // ('[HelloTanker]') прямо со скобками — шаблон подставит их обратно.
        LogTag = NameConventions.NormalizeLogTag(LogTag),
        Description = Description.Trim(),
        GameDir = GameDir
    };

    // =================================================================== команды

    /// <summary>Проект создан; здесь его каталог.</summary>
    [ObservableProperty]
    private string _createdProjectDir = string.Empty;

    /// <summary>Диалог отработал успешно и его можно закрывать.</summary>
    [ObservableProperty]
    private bool _isCompleted;

    /// <summary>Подробности создания — то же, что build.py пишет в свою панель.</summary>
    public ObservableCollection<string> CreateLog { get; } = new();

    /// <summary>Просят закрыть диалог. Обрабатывает <see cref="Views.NewProjectDialog"/>.</summary>
    public event EventHandler<bool>? CloseRequested;

    /// <summary>Выбрать каталог, внутри которого создать проект.</summary>
    [RelayCommand]
    private void BrowseParent()
    {
        string? picked = PickFolder("Каталог, ВНУТРИ которого создать проект мода", ParentDir);
        if (picked is not null)
        {
            ParentDir = picked;
        }
    }

    /// <summary>Выбрать каталог шаблона.</summary>
    [RelayCommand]
    private void BrowseTemplate()
    {
        string? picked = PickFolder("Каталог шаблона мода (там, где build.py и src\\meta.xml)", TemplateDir);
        if (picked is not null)
        {
            TemplateDir = picked;
            RememberTemplateIfValid();
        }
    }

    /// <summary>Сообщает наружу выбранный шаблон, если он прошёл проверку формы.</summary>
    private void RememberTemplateIfValid()
    {
        if (TemplateDirError.Length != 0 || TemplateDir.Trim().Length == 0)
        {
            return;
        }

        try
        {
            TemplateChosen?.Invoke(TemplateDir.Trim());
        }
        catch (Exception ex)
        {
            StatusText = "шаблон не запомнен: " + Short(ex);
        }
    }

    private bool CanCreate() => IsValid && !IsBusy && _backend.IsAvailable;

    /// <summary>Создать проект.</summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        IsBusy = true;
        StatusText = "создаём проект…";
        CreateLog.Clear();

        var progress = new Progress<string>(line => CreateLog.Add(line));

        ScaffoldResult result;
        try
        {
            result = await _backend
                .CreateAsync(BuildRequest(), progress, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            result = ScaffoldResult.Failed(Short(ex));
        }
        finally
        {
            IsBusy = false;
        }

        foreach (string line in result.Log)
        {
            CreateLog.Add(line);
        }

        StatusText = result.Message;

        if (!result.Success)
        {
            return;
        }

        CreatedProjectDir = result.ProjectDir;
        IsCompleted = true;

        // Шаблон, из которого проект реально создался, — лучший кандидат на «по умолчанию».
        RememberTemplateIfValid();
        CloseRequested?.Invoke(this, true);
    }

    /// <summary>Закрыть диалог, ничего не создавая.</summary>
    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    // =================================================================== мелочи

    private static readonly HashSet<string> PythonKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "assert", "break", "class", "continue", "def", "del", "elif", "else",
        "except", "exec", "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "not", "or", "pass", "print", "raise", "return", "try", "while", "with",
        "yield", "None", "True", "False"
    };

    /// <summary>Отображаемое имя → snake_case: <c>Battle Timer</c> → <c>battle_timer</c>.</summary>
    /// <param name="value">Имя.</param>
    /// <returns>Имя пакета.</returns>
    public static string ToSnakeCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length + 4);
        char previous = '\0';

        foreach (char raw in value.Trim())
        {
            // Не-ASCII (кириллица) в имя пакета не пускаем вовсе: это путь импорта python 2.7.
            // Такой символ работает как разделитель, а не как буква.
            if (raw > 0x7F || !char.IsLetterOrDigit(raw))
            {
                if (sb.Length > 0 && sb[^1] != '_')
                {
                    sb.Append('_');
                }

                previous = '\0';
                continue;
            }

            if (char.IsUpper(raw))
            {
                // Граница горба: BattleTimer -> battle_timer. Аббревиатуры не дробим
                // (HTTPServer -> httpserver): в имени пакета мода это только мешает.
                if (sb.Length > 0 && (char.IsLower(previous) || char.IsDigit(previous)))
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(raw));
            }
            else
            {
                sb.Append(raw);
            }

            previous = raw;
        }

        string result = sb.ToString().Trim('_');

        // Имя пакета обязано начинаться с буквы: цифра в начале — синтаксическая ошибка импорта.
        if (result.Length > 0 && char.IsDigit(result[0]))
        {
            result = "mod_" + result;
        }

        return result;
    }

    /// <summary>Имя пакета → PascalCase для тега лога: <c>battle_timer</c> → <c>BattleTimer</c>.</summary>
    /// <param name="value">Имя пакета.</param>
    /// <returns>Тег.</returns>
    public static string ToPascalCase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        bool upper = true;

        foreach (char c in value.Trim())
        {
            if (c is '_' or '-' or ' ')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    private static string? PickFolder(string title, string initial)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
            {
                dialog.InitialDirectory = initial;
            }
        }
        catch (Exception)
        {
            // Недоступный путь — диалог просто откроется по умолчанию.
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
