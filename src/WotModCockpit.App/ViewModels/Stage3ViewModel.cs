using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Stage3;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Оболочка этапа 3: панель Problems, каталог рецептов и мастер нового проекта, а также
/// связки между ними и экранами этапов 1-2.
/// <para>
/// Отдельный класс, а не три куска в <see cref="MainViewModel"/>, по двум причинам.
/// Первая: этап 3 обязан подключаться и отключаться целиком — если реализаций Core нет,
/// главный экран должен работать ровно как раньше. Вторая: связки идут В ОБЕ стороны
/// (Problems → браузер API, рецепт → браузер API, мастер → открыть новый проект в кокпите),
/// и держать их в модели одного экрана значило бы завязать экраны друг на друга.
/// </para>
/// </summary>
public sealed partial class Stage3ViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Задержка перед реакцией на смену проекта. Нужна не ради скорости: свойство
    /// <see cref="MainViewModel.ProjectDir"/> меняется в НАЧАЛЕ загрузки проекта, а сам
    /// <see cref="MainViewModel.CurrentProject"/> появляется в её конце. Отложенный тик
    /// гарантирует, что читаем уже загруженный проект, а не предыдущий.
    /// </summary>
    private const int ProjectDebounceMs = 120;

    private readonly MainViewModel _main;
    private readonly IScaffoldBackend _scaffold;
    private readonly IEditorLauncher _editor;
    private readonly DispatcherTimer _projectDebounce;

    private bool _disposed;

    /// <summary>Создаёт оболочку и подключает её к главному экрану.</summary>
    /// <param name="main">Модель главного экрана: у неё живут проект, игра и браузер API.</param>
    /// <param name="problems">Панель Problems.</param>
    /// <param name="recipes">Каталог рецептов.</param>
    /// <param name="scaffold">Шов к скаффолдеру нового проекта.</param>
    /// <param name="editor">Внешний редактор.</param>
    public Stage3ViewModel(
        MainViewModel main,
        ProblemsViewModel problems,
        RecipesViewModel recipes,
        IScaffoldBackend scaffold,
        IEditorLauncher editor)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        Problems = problems ?? throw new ArgumentNullException(nameof(problems));
        Recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _scaffold = scaffold ?? throw new ArgumentNullException(nameof(scaffold));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        _projectDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ProjectDebounceMs)
        };
        _projectDebounce.Tick += OnProjectDebounceTick;

        _main.PropertyChanged += OnMainPropertyChanged;
        Problems.ApiBrowserRequested += OnProblemApiRequested;
        Recipes.ApiBrowserRequested += OnRecipeApiRequested;

        // Главный экран должен уметь спросить у нас разрешение перед сборкой — см.
        // MainViewModel.Stage3PreflightGateAsync.
        _main.AttachStage3(this);
    }

    /// <summary>Панель Problems.</summary>
    public ProblemsViewModel Problems { get; }

    /// <summary>Каталог рецептов.</summary>
    public RecipesViewModel Recipes { get; }

    /// <summary>Мастер нового проекта подключён.</summary>
    public bool CanCreateProject => _scaffold.IsAvailable;

    // =================================================================== навигация

    /// <summary>Показан каталог рецептов.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildScreen))]
    private bool _isRecipesScreen;

    /// <summary>
    /// Показана панель Problems под лентой лога. Отдельный тумблер, а не экран: находки
    /// нужны РЯДОМ со сборкой и логом, а не вместо них.
    /// </summary>
    [ObservableProperty]
    private bool _showProblems = true;

    /// <summary>
    /// Показан экран сборки и лога — то есть не браузер API и не рецепты.
    /// Установка в <c>true</c> гасит оба других экрана; установка в <c>false</c> игнорируется,
    /// как и у переключателей браузера API: снятая галочка не должна оставлять окно пустым.
    /// </summary>
    public bool IsBuildScreen
    {
        get => !_main.IsApiScreen && !IsRecipesScreen;
        set
        {
            if (!value)
            {
                // Снятая галочка не должна оставлять окно без единого видимого экрана.
                // Уведомление здесь обязательно: без него привязка тумблера остаётся
                // в состоянии «снят», хотя экран сборки по-прежнему на месте, и чип
                // начинает врать про то, что показано.
                OnPropertyChanged(nameof(IsBuildScreen));
                return;
            }

            IsRecipesScreen = false;
            _main.IsApiScreen = false;
        }
    }

    partial void OnIsRecipesScreenChanged(bool value)
    {
        if (!value)
        {
            return;
        }

        // Экраны взаимоисключающие: браузер API живёт в той же ячейке разметки.
        _main.IsApiScreen = false;

        // Каталог ленивый: первый заход и грузит его, и сверяет с индексом клиента.
        _ = Recipes.LoadAsync();
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsApiScreen):
                if (_main.IsApiScreen)
                {
                    IsRecipesScreen = false;
                }

                OnPropertyChanged(nameof(IsBuildScreen));
                break;

            case nameof(MainViewModel.ProjectDir):
                _projectDebounce.Stop();
                _projectDebounce.Start();
                break;
        }
    }

    private void OnProjectDebounceTick(object? sender, EventArgs e)
    {
        _projectDebounce.Stop();
        PushProject();
    }

    /// <summary>
    /// Отдаёт текущий проект панели Problems и каталогу рецептов. Публичный, потому что его
    /// же зовёт подъём окна: к моменту создания оболочки проект уже мог быть загружен.
    /// </summary>
    public void PushProject()
    {
        ModProject? project = _main.CurrentProject;
        Problems.SetProject(project);
        Recipes.SetProject(project);
    }

    /// <summary>Поднимает этап 3 после загрузки окна.</summary>
    public void Initialize() => PushProject();

    // =================================================================== мосты к браузеру API

    private void OnProblemApiRequested(object? sender, ProblemItem problem)
    {
        // У находки может быть три разных зацепки, и они не равноценны.
        // 1. Прямой виртуальный путь символа клиента — самый точный переход.
        if (problem.ApiVirtualPath.Length > 0)
        {
            _ = OpenInApiBrowserAsync(problem.ApiVirtualPath, problem.Line);
            return;
        }

        // 2. Основание правила вида "scripts/client/game.py:127" — показываем ровно ту строку
        //    клиента, из-за которой правило вообще существует.
        if (TrySplitReference(problem.Reference, out string path, out int line))
        {
            _ = OpenInApiBrowserAsync(path, line);
            return;
        }

        // 3. Осталось только имя — ищем его в индексе.
        if (problem.ApiSymbol.Length > 0)
        {
            SearchInApiBrowser(problem.ApiSymbol);
        }
    }

    private void OnRecipeApiRequested(object? sender, RecipeSymbol symbol)
    {
        string needle = symbol.Name ?? string.Empty;
        if (string.IsNullOrEmpty(needle))
        {
            int dot = symbol.Module.LastIndexOf('.');
            needle = dot >= 0 ? symbol.Module[(dot + 1)..] : symbol.Module;
        }

        SearchInApiBrowser(needle);
    }

    /// <summary>
    /// Виртуальный путь клиента внутри свободного текста ссылки. Ссылки правил выглядят
    /// по-разному: и «scripts/client/game.py:127», и
    /// «scripts/client/gui/mods/__init__.py:_findValidMODs (except ImportError)» —
    /// поэтому путь выхватывается регуляркой, а номер строки берётся, только если он есть.
    /// </summary>
    private static readonly Regex ReferencePath = new(
        @"(?<path>[\w./\\+-]+\.py)(?::(?<line>\d+))?",
        RegexOptions.Compiled);

    /// <summary>
    /// Разбирает ссылку вида <c>scripts/client/game.py:127</c>.
    /// </summary>
    /// <param name="reference">Ссылка.</param>
    /// <param name="path">Виртуальный путь.</param>
    /// <param name="line">Номер строки; 0, если его нет.</param>
    /// <returns>true, если в ссылке нашёлся путь к файлу клиента.</returns>
    private static bool TrySplitReference(string? reference, out string path, out int line)
    {
        path = string.Empty;
        line = 0;

        if (string.IsNullOrWhiteSpace(reference))
        {
            return false;
        }

        Match match = ReferencePath.Match(reference);
        if (!match.Success)
        {
            return false;
        }

        path = match.Groups["path"].Value.Replace('\\', '/');

        if (match.Groups["line"].Success && int.TryParse(match.Groups["line"].Value, out int parsed))
        {
            line = parsed;
        }

        return true;
    }

    private async Task OpenInApiBrowserAsync(string virtualPath, int line)
    {
        ApiBrowserViewModel? browser = _main.ApiBrowser;
        if (browser is null)
        {
            _main.StatusMessage = "браузер API недоступен";
            return;
        }

        IsRecipesScreen = false;
        _main.IsApiScreen = true;

        bool found = await browser.OpenVirtualPathAsync(virtualPath, line).ConfigureAwait(true);
        _main.StatusMessage = found
            ? "браузер API: " + virtualPath
            : "браузер API: файла " + virtualPath + " нет в индексе";
    }

    private void SearchInApiBrowser(string needle)
    {
        ApiBrowserViewModel? browser = _main.ApiBrowser;
        if (browser is null)
        {
            _main.StatusMessage = "браузер API недоступен";
            return;
        }

        IsRecipesScreen = false;
        _main.IsApiScreen = true;
        browser.Pane = ApiBrowserPane.Search;
        browser.SearchText = needle;
        _main.StatusMessage = "браузер API: поиск «" + needle + "»";
    }

    // =================================================================== проверка перед сборкой

    /// <summary>
    /// Прогоняет preflight перед сборкой и, если нашлись ошибки, спрашивает разрешение.
    /// <para>
    /// НЕ блокирует: право собрать заведомо кривой мод остаётся за человеком — иногда его
    /// как раз и собирают, чтобы посмотреть, как он падает. Задача проверки — не дать
    /// узнать об ошибке только из python.log после запуска игры.
    /// </para>
    /// </summary>
    /// <param name="project">Проект, который собираются собирать.</param>
    /// <param name="confirm">Диалог подтверждения главного экрана.</param>
    /// <returns>true — продолжать сборку.</returns>
    public async Task<bool> ConfirmBuildAsync(ModProject project, Func<string, string, bool> confirm)
    {
        if (!Problems.AnalyzeBeforeBuild || !Problems.IsAvailable)
        {
            return true;
        }

        ShowProblems = true;

        PreflightReport report = await Problems.RunAsync(project).ConfigureAwait(true);

        if (report.Error is not null)
        {
            // Сама проверка сломалась — это не повод мешать сборке.
            return true;
        }

        var errors = report.Problems.Where(p => p.Severity == ProblemSeverity.Error).ToList();
        if (errors.Count == 0)
        {
            return true;
        }

        string listing = string.Join(
            Environment.NewLine,
            errors.Take(6).Select(p => $"    {p.Code}  {p.Location}{Environment.NewLine}        {p.Message}"));

        if (errors.Count > 6)
        {
            listing += Environment.NewLine + $"    … ещё {errors.Count - 6}";
        }

        return confirm(
            "Preflight нашёл ошибки",
            $"Перед сборкой найдено ошибок: {errors.Count}." + Environment.NewLine + Environment.NewLine +
            listing + Environment.NewLine + Environment.NewLine +
            "Это находки уровня «клиент закроется» или «мод не загрузится»." + Environment.NewLine +
            "Подробности — в панели Problems под лентой лога." + Environment.NewLine + Environment.NewLine +
            "Собрать всё равно?");
    }

    // =================================================================== мастер нового проекта

    /// <summary>
    /// Показ диалога мастера. Свойством — чтобы тест мог подставить свой обработчик,
    /// как это сделано с <see cref="MainViewModel.ConfirmHandler"/>.
    /// </summary>
    public Func<NewProjectViewModel, bool> ShowNewProjectDialog { get; set; } = DefaultShowDialog;

    /// <summary>Диалог «открыть созданный проект?».</summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    private bool CanCreateNewProject() => _scaffold.IsAvailable;

    /// <summary>
    /// Создать новый проект мода из шаблона и, если получилось, предложить открыть его
    /// в кокпите и в VS Code.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateNewProject))]
    private void NewProject()
    {
        string parent = GuessParentDir();

        // GameDir передаётся, чтобы мастер не дал создать проект внутри установленной игры:
        // кокпит читает mods\<версия> и res_mods\<версия>, но не пишет в них ничего.
        // Шаблон и префикс id — из настроек кокпита; выбранный в мастере шаблон запоминается.
        var vm = new NewProjectViewModel(_scaffold, parent, _main.GameDir, _main.ResolveTemplateDir(), _main.ModIdPrefix)
        {
            TemplateChosen = _main.RememberTemplateDir
        };

        bool created;
        try
        {
            created = ShowNewProjectDialog(vm);
        }
        catch (Exception ex)
        {
            _main.StatusMessage = "мастер нового проекта: " + Short(ex);
            return;
        }

        if (!created || vm.CreatedProjectDir.Length == 0)
        {
            _main.StatusMessage = "новый проект не создан";
            return;
        }

        string dir = vm.CreatedProjectDir;
        _main.StatusMessage = "проект создан: " + dir;

        if (ConfirmHandler(
                "Открыть новый проект",
                $"Проект создан:{Environment.NewLine}{Environment.NewLine}    {dir}{Environment.NewLine}{Environment.NewLine}" +
                "Открыть его в кокпите вместо текущего? Сразу после открытия пройдёт preflight."))
        {
            _main.OpenProject(dir);
        }

        if (ConfirmHandler(
                "Открыть в VS Code",
                $"Открыть {dir} в VS Code?"))
        {
            try
            {
                _editor.OpenFolder(dir);
            }
            catch (Exception ex)
            {
                _main.StatusMessage = "VS Code: " + Short(ex);
            }
        }
    }

    /// <summary>
    /// Где по умолчанию предлагать создать проект: рядом с текущим, а не в корне диска.
    /// </summary>
    /// <returns>Каталог либо пустая строка.</returns>
    private string GuessParentDir()
    {
        try
        {
            string current = _main.ProjectDir;
            if (!string.IsNullOrWhiteSpace(current))
            {
                string? parent = Path.GetDirectoryName(current.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                {
                    return parent;
                }
            }
        }
        catch (Exception)
        {
            // Путь мог стать недоступным — тогда пользователь выберет каталог сам.
        }

        return string.Empty;
    }

    private static bool DefaultShowDialog(NewProjectViewModel vm)
    {
        var dialog = new Views.NewProjectDialog
        {
            DataContext = vm
        };

        try
        {
            if (Application.Current?.MainWindow is Window owner && !ReferenceEquals(owner, dialog))
            {
                dialog.Owner = owner;
            }
        }
        catch (Exception)
        {
            // Владельца может не быть (тесты, ранний старт) — диалог откроется без него.
        }

        return dialog.ShowDialog() == true && vm.IsCompleted;
    }

    private static bool DefaultConfirm(string caption, string body)
        => MessageBox.Show(
               body,
               caption,
               MessageBoxButton.YesNo,
               MessageBoxImage.Question,
               MessageBoxResult.Yes) == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _projectDebounce.Stop();
        _main.PropertyChanged -= OnMainPropertyChanged;
        Problems.ApiBrowserRequested -= OnProblemApiRequested;
        Recipes.ApiBrowserRequested -= OnRecipeApiRequested;

        Problems.Dispose();
        Recipes.Dispose();
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
