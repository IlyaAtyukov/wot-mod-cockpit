using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Collections;
using WotModCockpit.App.Stage3;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>Как сгруппирован список находок.</summary>
public enum ProblemGrouping
{
    /// <summary>По файлу — как в панели Problems у Visual Studio.</summary>
    File = 0,

    /// <summary>По правилу: видно, что именно даёт основной поток замечаний.</summary>
    Rule = 1,

    /// <summary>По серьёзности: сначала то, из-за чего закроется клиент.</summary>
    Severity = 2
}

/// <summary>
/// Одна находка в списке. Обёртка над <see cref="ProblemItem"/>, потому что списку нужны
/// вещи, которых нет в модели: ключ текущей группировки и готовые к показу подписи.
/// </summary>
public sealed partial class ProblemItemViewModel : ObservableObject
{
    /// <summary>Создаёт строку списка.</summary>
    /// <param name="problem">Находка.</param>
    /// <param name="grouping">Как группировать в момент создания.</param>
    public ProblemItemViewModel(ProblemItem problem, ProblemGrouping grouping)
    {
        Problem = problem;
        _groupKey = MakeGroupKey(problem, grouping);
    }

    /// <summary>Сама находка.</summary>
    public ProblemItem Problem { get; }

    /// <summary>Ключ группы. Меняется при смене режима группировки.</summary>
    [ObservableProperty]
    private string _groupKey;

    /// <summary>Код правила.</summary>
    public string Code => Problem.Code;

    /// <summary>Серьёзность.</summary>
    public ProblemSeverity Severity => Problem.Severity;

    /// <summary>Текст находки.</summary>
    public string Message => Problem.Message;

    /// <summary>«файл:строка» либо путь.</summary>
    public string Location => Problem.Location;

    /// <summary>Кусок исходной строки мода.</summary>
    public string Snippet => Problem.Snippet;

    /// <summary>Кто нашёл.</summary>
    public string Source => Problem.Source;

    /// <summary>Значок серьёзности: три буквы вместо иконки — читается так же быстро.</summary>
    public string SeverityBadge => Problem.Severity switch
    {
        ProblemSeverity.Error => "ERR",
        ProblemSeverity.Warning => "WRN",
        _ => "INF"
    };

    /// <summary>Можно открыть файл мода в редакторе.</summary>
    public bool CanOpenInEditor => Problem.HasLocation;

    /// <summary>
    /// Есть куда перейти в браузере API этапа 2: либо символ клиента, о котором находка,
    /// либо ссылка на место в клиенте, из которого следует правило.
    /// </summary>
    public bool CanOpenInApi =>
        Problem.ApiVirtualPath.Length > 0
        || Problem.ApiSymbol.Length > 0
        || Problem.Reference.Length > 0;

    /// <summary>Подсказка строки: всё, что не поместилось.</summary>
    public string ToolTipText
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append(Problem.Code).Append("  ").Append(Problem.Message);

            if (Problem.Hint.Length > 0)
            {
                sb.Append("\n\nкак чинить: ").Append(Problem.Hint);
            }

            if (Problem.Reference.Length > 0)
            {
                sb.Append("\n\nоснование в клиенте: ").Append(Problem.Reference);
            }

            if (Problem.Location.Length > 0)
            {
                sb.Append('\n').Append(Problem.Location);
            }

            // Жест был обнаружим только методом тыка: двойной щелчок работал, но нигде
            // не был подписан, а перехода в браузер API из списка не было вовсе.
            sb.Append("\n\nдвойной щелчок — открыть строку в VS Code; правая кнопка — все действия");

            return sb.ToString();
        }
    }

    /// <summary>Пересчитывает ключ группы под новый режим.</summary>
    /// <param name="grouping">Режим.</param>
    public void Regroup(ProblemGrouping grouping) => GroupKey = MakeGroupKey(Problem, grouping);

    private static string MakeGroupKey(ProblemItem problem, ProblemGrouping grouping) => grouping switch
    {
        ProblemGrouping.Rule => problem.Code.Length > 0 ? problem.Code : "без кода",
        ProblemGrouping.Severity => problem.Severity switch
        {
            ProblemSeverity.Error => "Ошибки — из-за этого закроется клиент или мод не загрузится",
            ProblemSeverity.Warning => "Предупреждения — сломается вероятностно",
            _ => "Замечания — мод исправен, но факт полезно знать"
        },
        _ => problem.RelativePath.Length > 0
            ? problem.RelativePath
            : problem.FilePath.Length > 0
                ? problem.FilePath
                : "проект целиком"
    };
}

/// <summary>
/// Панель Problems: статический анализ кода мода перед сборкой.
/// <para>
/// Для C#-разработчика это «Roslyn-анализатор для мода»: список диагностик с файлом, строкой,
/// серьёзностью и — обязательно — объяснением, ПОЧЕМУ так делать нельзя. Половина правил
/// опирается на факты про загрузчик модов, которые мододел не обязан помнить наизусть
/// (<c>except ImportError</c> на импорте, <c>BigWorld.quit()</c> в <c>game.py:init()</c>,
/// <c>except AttributeError: pass</c> в <c>_callModMethod</c>), поэтому короткого сообщения
/// «нет try/except» здесь недостаточно.
/// </para>
/// <para>
/// Правила, которым подчинено всё остальное:
/// </para>
/// <list type="number">
///   <item><b>Анализ никогда не блокирует сборку.</b> Перед сборкой он предупреждает
///         (см. <see cref="MainViewModel.Stage3PreflightGateAsync"/>), но последнее слово —
///         за человеком: бывает нужно собрать заведомо кривой мод, чтобы посмотреть, как он
///         падает.</item>
///   <item><b>Отсутствие Core — это состояние панели, а не авария.</b> Реализация правил
///         пишется параллельно; пока её нет, панель открывается и объясняет, чего не хватает.</item>
///   <item><b>Всё длинное — асинхронно и отменяемо.</b> Анализ читает весь <c>src\</c>, а pyright
///         вообще внешний процесс на секунды.</item>
/// </list>
/// </summary>
public sealed partial class ProblemsViewModel : ObservableObject, IDisposable
{
    private readonly IPreflightBackend _backend;
    private readonly IEditorLauncher _editor;

    /// <summary>Все находки последнего прогона — до фильтра. Счётчики считаются по ним.</summary>
    private readonly List<ProblemItemViewModel> _all = new();

    /// <summary>Правила, которые пользователь скрыл. Следующий прогон их вообще не считает.</summary>
    private readonly HashSet<string> _muted = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _cts;
    private ModProject? _project;
    private bool _disposed;

    /// <summary>Создаёт модель панели.</summary>
    /// <param name="backend">Шов к реализации правил в Core.</param>
    /// <param name="editor">Внешний редактор для перехода по двойному щелчку.</param>
    public ProblemsViewModel(IPreflightBackend backend, IEditorLauncher editor)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    // =================================================================== список

    /// <summary>Находки после фильтра. К ней привязан виртуализированный список.</summary>
    public BulkObservableCollection<ProblemItemViewModel> Problems { get; } = new();

    /// <summary>Выбранная находка: под списком показываются её подробности.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(SelectedExplanation))]
    [NotifyPropertyChangedFor(nameof(SelectedHint))]
    [NotifyPropertyChangedFor(nameof(SelectedReference))]
    [NotifyCanExecuteChangedFor(nameof(OpenInEditorCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenInApiBrowserCommand))]
    [NotifyCanExecuteChangedFor(nameof(MuteRuleCommand))]
    private ProblemItemViewModel? _selected;

    /// <summary>Что-то выбрано.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>
    /// «Почему это плохо» для выбранной находки. Это главное, ради чего панель существует:
    /// список кодов без объяснения последствия ничему не учит.
    /// </summary>
    public string SelectedExplanation => Selected?.Problem.Explanation ?? string.Empty;

    /// <summary>«Как чинить» для выбранной находки.</summary>
    public string SelectedHint => Selected?.Problem.Hint ?? string.Empty;

    /// <summary>Основание правила в исходниках клиента.</summary>
    public string SelectedReference => Selected?.Problem.Reference ?? string.Empty;

    // =================================================================== фильтр

    /// <summary>Показывать ошибки.</summary>
    [ObservableProperty]
    private bool _showErrors = true;

    /// <summary>Показывать предупреждения.</summary>
    [ObservableProperty]
    private bool _showWarnings = true;

    /// <summary>Показывать замечания.</summary>
    [ObservableProperty]
    private bool _showInfos = true;

    /// <summary>Подстрока поиска по коду правила, тексту и пути.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Режим группировки.</summary>
    [ObservableProperty]
    private ProblemGrouping _grouping = ProblemGrouping.File;

    partial void OnShowErrorsChanged(bool value) => ApplyFilter();

    partial void OnShowWarningsChanged(bool value) => ApplyFilter();

    partial void OnShowInfosChanged(bool value) => ApplyFilter();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnGroupingChanged(ProblemGrouping value)
    {
        foreach (ProblemItemViewModel vm in _all)
        {
            vm.Regroup(value);
        }

        // Полная замена содержимого, а не правка ключей на месте: группировка WPF
        // перестраивается по Reset коллекции, а на изменение свойства элемента она
        // реагирует только при включённой живой группировке, которой у виртуализованного
        // списка нет.
        ApplyFilter();
    }

    // =================================================================== счётчики и состояние

    /// <summary>Сколько ошибок в последнем прогоне (до фильтра).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    [NotifyPropertyChangedFor(nameof(HasErrors))]
    private int _errorCount;

    /// <summary>Сколько предупреждений (до фильтра).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private int _warningCount;

    /// <summary>Сколько замечаний (до фильтра).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeaderText))]
    private int _infoCount;

    /// <summary>Сколько строк показано после фильтра.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private int _visibleCount;

    /// <summary>Есть хоть одна ошибка — на этом основано предупреждение перед сборкой.</summary>
    public bool HasErrors => ErrorCount > 0;

    /// <summary>Заголовок панели со счётчиками — то, что видно, когда панель свёрнута.</summary>
    public string HeaderText => ErrorCount + WarningCount + InfoCount == 0
        ? "Problems"
        : $"Problems — ошибок {ErrorCount}, предупреждений {WarningCount}, замечаний {InfoCount}";

    /// <summary>Идёт анализ.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    [NotifyCanExecuteChangedFor(nameof(AnalyzeCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelCommand))]
    private bool _isRunning;

    /// <summary>Итог последнего прогона одной строкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private string _statusText = "анализ ещё не запускался";

    /// <summary>Прогон не состоялся — здесь причина.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private string _errorText = string.Empty;

    /// <summary>Дополнительно гонять pyright и разбирать его JSON в те же находки.</summary>
    [ObservableProperty]
    private bool _runPyright;

    /// <summary>Что известно про pyright: версия либо причина отказа.</summary>
    [ObservableProperty]
    private string _pyrightStatus = "pyright не проверялся";

    /// <summary>
    /// Проверять перед сборкой. Выключается осознанно: бывает нужно собрать заведомо кривой
    /// мод, чтобы посмотреть, как именно он падает.
    /// </summary>
    [ObservableProperty]
    private bool _analyzeBeforeBuild = true;

    /// <summary>Анализировать сразу при открытии проекта.</summary>
    [ObservableProperty]
    private bool _analyzeOnOpen = true;

    /// <summary>Сколько правил скрыто пользователем.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MutedText))]
    private int _mutedCount;

    /// <summary>Подпись про скрытые правила.</summary>
    public string MutedText => MutedCount == 0
        ? string.Empty
        : $"скрыто правил: {MutedCount} ({string.Join(", ", _muted.OrderBy(x => x, StringComparer.Ordinal))})";

    /// <summary>Реализация правил подключена.</summary>
    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>Карточки правил — для справки и для объяснения кодов.</summary>
    public IReadOnlyList<PreflightRuleCard> Rules => _backend.Rules;

    /// <summary>
    /// Что показать вместо списка. «Пусто» без объяснения — худшее, что можно показать
    /// в инструменте: непонятно, всё ли хорошо или проверка не отработала.
    /// </summary>
    public string EmptyStateText
    {
        get
        {
            if (!_backend.IsAvailable)
            {
                return "Статический анализ не подключён.\n\n"
                       + _backend.UnavailableReason + "\n\n"
                       + "Кокпит от этого не страдает: сборка, лента python.log и браузер API работают.";
            }

            if (_project is null)
            {
                return "Проект мода не выбран.\n\n"
                       + "Preflight проверяет код мода из src\\ по правилам, выведенным из\n"
                       + "декомпилированного клиента: что закроет клиент, что не загрузится,\n"
                       + "что молча не сработает. Укажите каталог проекта в шапке.";
            }

            if (ErrorText.Length > 0)
            {
                return "Проверка не выполнена.\n\n" + ErrorText;
            }

            if (IsRunning)
            {
                return "Идёт анализ кода мода…";
            }

            if (_all.Count == 0)
            {
                return "Замечаний нет.\n\n" + StatusText;
            }

            return $"Ни одна из {_all.Count} находок не проходит фильтр.\n\n"
                   + "Включите нужные уровни или очистите поиск.";
        }
    }

    /// <summary>
    /// Просят открыть находку в браузере API этапа 2. Обрабатывает
    /// <see cref="Stage3ViewModel"/>: у него есть и экран браузера, и переключатель экранов.
    /// </summary>
    public event EventHandler<ProblemItem>? ApiBrowserRequested;

    // =================================================================== проект

    /// <summary>
    /// Сменился открытый проект. Если включён <see cref="AnalyzeOnOpen"/>, сразу запускает анализ.
    /// </summary>
    /// <param name="project">Новый проект либо <c>null</c>.</param>
    public void SetProject(ModProject? project)
    {
        bool same = ReferenceEquals(_project, project);
        _project = project;

        AnalyzeCommand.NotifyCanExecuteChanged();

        if (project is null)
        {
            Clear("проект не выбран");
            return;
        }

        if (!same && AnalyzeOnOpen)
        {
            _ = AnalyzeAsync();
        }
    }

    private void Clear(string status)
    {
        _all.Clear();
        Problems.ReplaceAll(Array.Empty<ProblemItemViewModel>());
        Selected = null;
        ErrorCount = 0;
        WarningCount = 0;
        InfoCount = 0;
        VisibleCount = 0;
        StatusText = status;
        ErrorText = string.Empty;
        OnPropertyChanged(nameof(EmptyStateText));
    }

    // =================================================================== анализ

    private bool CanAnalyze() => !IsRunning && _project is not null && _backend.IsAvailable;

    /// <summary>Прогнать анализ по текущему проекту.</summary>
    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private Task AnalyzeAsync() => RunAsync(_project);

    /// <summary>
    /// Прогоняет анализ и возвращает отчёт. Публичный, потому что тем же прогоном пользуется
    /// проверка перед сборкой — и она обязана дождаться результата, а не запустить и забыть.
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <returns>Отчёт последнего прогона.</returns>
    public async Task<PreflightReport> RunAsync(ModProject? project)
    {
        if (project is null)
        {
            Clear("проект не выбран");
            return PreflightReport.Empty;
        }

        // Прогон задаёт панели её проект: пустое состояние и кнопка «проверить» обязаны
        // говорить про ТОТ проект, чьи находки сейчас на экране, даже если анализ пришёл
        // не из SetProject, а из проверки перед сборкой.
        _project = project;
        AnalyzeCommand.NotifyCanExecuteChanged();

        if (!_backend.IsAvailable)
        {
            ErrorText = _backend.UnavailableReason;
            StatusText = "анализ не подключён";
            OnPropertyChanged(nameof(EmptyStateText));
            return PreflightReport.Failed(_backend.UnavailableReason);
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsRunning = true;
        ErrorText = string.Empty;
        StatusText = "анализ…";
        OnPropertyChanged(nameof(EmptyStateText));

        var options = new PreflightOptions
        {
            DisabledRules = _muted.ToArray(),
            RunPyright = RunPyright
        };

        PreflightReport report;
        try
        {
            report = await _backend.AnalyzeAsync(project, options, cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsRunning = false;
                StatusText = "анализ прерван";
                OnPropertyChanged(nameof(EmptyStateText));
            }

            return PreflightReport.Empty;
        }
        catch (Exception ex)
        {
            report = PreflightReport.Failed(Short(ex));
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsRunning = false;
            }
        }

        // Пока считали, пользователь мог сменить проект или запустить проверку заново.
        if (!ReferenceEquals(_cts, cts) || cts.IsCancellationRequested)
        {
            return report;
        }

        Apply(report);
        return report;
    }

    private void Apply(PreflightReport report)
    {
        _all.Clear();

        foreach (ProblemItem p in report.Problems.OrderByDescending(p => (int)p.Severity)
                     .ThenBy(p => p.RelativePath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(p => p.Line))
        {
            _all.Add(new ProblemItemViewModel(p, Grouping));
        }

        ErrorCount = _all.Count(p => p.Severity == ProblemSeverity.Error);
        WarningCount = _all.Count(p => p.Severity == ProblemSeverity.Warning);
        InfoCount = _all.Count(p => p.Severity == ProblemSeverity.Info);

        ErrorText = report.Error ?? string.Empty;

        StatusText = report.Error is not null
            ? "проверка не выполнена: " + report.Error
            : _all.Count == 0
                ? $"замечаний нет · файлов {report.FilesAnalyzed} · {report.Elapsed.TotalMilliseconds:N0} мс"
                : $"ошибок {ErrorCount}, предупреждений {WarningCount}, замечаний {InfoCount}"
                  + $" · файлов {report.FilesAnalyzed} · {report.Elapsed.TotalMilliseconds:N0} мс";

        if (report.Note.Length > 0)
        {
            StatusText += " · " + report.Note;
        }

        ApplyFilter();
    }

    private bool CanCancel() => IsRunning;

    /// <summary>Прервать анализ.</summary>
    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "отмена…";
    }

    private void ApplyFilter()
    {
        string needle = SearchText?.Trim() ?? string.Empty;

        var shown = new List<ProblemItemViewModel>(_all.Count);
        foreach (ProblemItemViewModel vm in _all)
        {
            bool levelOk = vm.Severity switch
            {
                ProblemSeverity.Error => ShowErrors,
                ProblemSeverity.Warning => ShowWarnings,
                _ => ShowInfos
            };

            if (!levelOk || _muted.Contains(vm.Code))
            {
                continue;
            }

            if (needle.Length > 0 && !Matches(vm, needle))
            {
                continue;
            }

            shown.Add(vm);
        }

        Problems.ReplaceAll(shown);
        VisibleCount = shown.Count;

        if (Selected is not null && !shown.Contains(Selected))
        {
            Selected = null;
        }

        OnPropertyChanged(nameof(EmptyStateText));
    }

    private static bool Matches(ProblemItemViewModel vm, string needle) =>
        vm.Code.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || vm.Message.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || vm.Location.Contains(needle, StringComparison.OrdinalIgnoreCase)
        || vm.Snippet.Contains(needle, StringComparison.OrdinalIgnoreCase);

    // =================================================================== команды строки

    private bool CanOpenInEditor() => Selected?.CanOpenInEditor == true;

    /// <summary>Открыть файл находки в VS Code на её строке.</summary>
    /// <param name="item">Строка списка либо <c>null</c> — тогда берётся выбранная.</param>
    [RelayCommand(CanExecute = nameof(CanOpenInEditor))]
    private void OpenInEditor(ProblemItemViewModel? item)
    {
        ProblemItemViewModel? target = item ?? Selected;
        if (target is null || !target.CanOpenInEditor)
        {
            return;
        }

        try
        {
            _editor.OpenAt(target.Problem.FilePath, Math.Max(1, target.Problem.Line));
            StatusText = $"открыто: {target.Problem.FilePath}:{target.Problem.Line}";
        }
        catch (Exception ex)
        {
            StatusText = "VS Code: " + Short(ex);
        }
    }

    private bool CanOpenInApiBrowser() => Selected?.CanOpenInApi == true;

    /// <summary>
    /// Показать в браузере API то, о чём находка: символ клиента (WOT002/003/004) либо место
    /// в клиенте, из которого следует правило.
    /// </summary>
    /// <param name="item">Строка списка либо <c>null</c> — тогда берётся выбранная.</param>
    [RelayCommand(CanExecute = nameof(CanOpenInApiBrowser))]
    private void OpenInApiBrowser(ProblemItemViewModel? item)
    {
        ProblemItemViewModel? target = item ?? Selected;
        if (target is null || !target.CanOpenInApi)
        {
            return;
        }

        ApiBrowserRequested?.Invoke(this, target.Problem);
    }

    /// <summary>Скопировать показанные находки в буфер обмена — обычным текстом, как из линтера.</summary>
    [RelayCommand]
    private void CopyProblems()
    {
        if (Problems.Count == 0)
        {
            StatusText = "копировать нечего";
            return;
        }

        var sb = new StringBuilder();
        foreach (ProblemItemViewModel vm in Problems)
        {
            sb.Append(vm.Location)
              .Append(": ")
              .Append(vm.SeverityBadge)
              .Append(' ')
              .Append(vm.Code)
              .Append(": ")
              .AppendLine(vm.Message);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            StatusText = $"скопировано находок: {Problems.Count}";
        }
        catch (Exception ex)
        {
            StatusText = "буфер обмена: " + Short(ex);
        }
    }

    private bool CanMuteRule() => Selected is not null;

    /// <summary>
    /// Скрыть правило целиком. Не «удалить находку»: следующий прогон это правило вообще
    /// не считает, и об этом честно написано в панели, чтобы скрытое не выглядело как чистое.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanMuteRule))]
    private void MuteRule()
    {
        string? code = Selected?.Code;
        if (string.IsNullOrEmpty(code))
        {
            return;
        }

        _muted.Add(code);
        MutedCount = _muted.Count;
        ApplyFilter();
        StatusText = $"правило {code} скрыто — снять можно кнопкой «показать все правила»";
    }

    /// <summary>Показать все скрытые правила снова.</summary>
    [RelayCommand]
    private void ResetMutedRules()
    {
        if (_muted.Count == 0)
        {
            return;
        }

        _muted.Clear();
        MutedCount = 0;
        ApplyFilter();
        StatusText = "скрытые правила возвращены — перезапустите анализ";
    }

    partial void OnRunPyrightChanged(bool value)
    {
        if (value)
        {
            _ = ProbePyrightAsync();
        }
        else
        {
            PyrightStatus = "pyright выключен";
        }
    }

    /// <summary>
    /// Проверить, установлен ли pyright. Отдельно от анализа: запуск внешнего процесса
    /// на секунды не должен происходить незаметно, а тумблер обязан честно сказать,
    /// заработает он или нет.
    /// </summary>
    [RelayCommand]
    private async Task ProbePyrightAsync()
    {
        PyrightStatus = "ищем pyright…";
        try
        {
            PyrightProbe probe = await _backend.ProbePyrightAsync(CancellationToken.None).ConfigureAwait(true);
            PyrightStatus = probe.Description;

            if (!probe.IsAvailable && RunPyright)
            {
                // Тумблер, который ничего не делает, хуже выключенного.
                RunPyright = false;
            }
        }
        catch (Exception ex)
        {
            PyrightStatus = "pyright: " + Short(ex);
            RunPyright = false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
