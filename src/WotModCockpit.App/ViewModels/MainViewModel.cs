using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WotModCockpit.App.Collections;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Модель представления единственного экрана кокпита: шапка, тулбар, состояние деплоя,
/// живой python.log и строка состояния.
/// <para>
/// Два правила, которым здесь подчинено всё остальное:
/// </para>
/// <list type="number">
///   <item><b>В игру пишет только build.py и только по явному нажатию.</b> Действия
///         <see cref="BuildAction.BuildAndInstall"/>, <see cref="BuildAction.DevDeploy"/> и
///         <see cref="BuildAction.Uninstall"/> проходят через диалог, в котором ЯВНО написан
///         каталог назначения. Никаких автозапусков по таймеру или по watcher'у.</item>
///   <item><b>UI не падает от несделанного Core.</b> Реализации сервисов пишутся параллельно и
///         могут ещё бросать <see cref="NotImplementedException"/>. Каждый вызов обёрнут, отказ
///         превращается в строку состояния, а не в аварию приложения.</item>
/// </list>
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Период выгрузки накопленных строк лога в UI. Батч, а не строка на Dispatcher.</summary>
    private const int PumpIntervalMs = 100;

    /// <summary>Сколько строк максимум отдаём в UI за один такт, чтобы не залипнуть на прогреве.</summary>
    private const int MaxLinesPerTick = 4000;

    /// <summary>Через сколько миллисекунд тишины считаем незакрытую запись (тело traceback) дозревшей.</summary>
    private const long IdleFlushMs = 400;

    /// <summary>Потолок панели вывода сборки.</summary>
    private const int BuildOutputCap = 5000;

    private readonly ISettingsStore _settingsStore;
    private readonly IGameInstallLocator _locator;
    private readonly IProjectStore _projectStore;
    private readonly IBuildRunner _buildRunner;
    private readonly ILogTailerFactory _tailerFactory;
    private readonly ITracebackMapper _mapper;
    private readonly IDeployProbe _deployProbe;
    private readonly IEditorLauncher _editor;

    private readonly Func<LogEntryViewModel, IReadOnlyList<TracebackFrameViewModel>> _framesFactory;

    /// <summary>Строки, разобранные в фоновом потоке тейлера и ждущие выгрузки в UI.</summary>
    private readonly ConcurrentQueue<LogEntryViewModel> _incoming = new();

    /// <summary>Кольцевой буфер ВСЕХ записей (до фильтра). Вытеснение с головы.</summary>
    private readonly Queue<LogEntryViewModel> _all = new();

    /// <summary>Замок состояния склейки traceback: его трогают и фоновый поток, и таймер UI.</summary>
    private readonly object _glue = new();

    /// <summary>
    /// Склейка строк в записи. Своей копии этой логики в UI нет: та же реализация Core,
    /// что покрыта тестами (<c>LogParserTests.Assembler_*</c>). Трогать только под <see cref="_glue"/>.
    /// </summary>
    private readonly LogEntryAssembler _assembler;

    /// <summary>
    /// Фильтр ленты. Тоже реализация Core (<see cref="LogFilter"/>), а не вторая копия условий:
    /// «только ошибки» здесь означает ровно то же, что в <see cref="LogFilter.IsProblem"/>.
    /// </summary>
    private readonly LogFilter _filter = new();

    private readonly DispatcherTimer _pump;
    private readonly DispatcherTimer _searchDebounce;

    private CockpitSettings _settings = new();
    private GameInstall? _game;
    private ModProject? _project;
    private ILogTailer? _tailer;
    private CancellationTokenSource? _buildCts;

    private long _lastLineTicks;
    private int _truncationPending;
    private string? _pendingNotice;

    private bool _parserBroken;
    private bool _mapperBroken;
    private bool _suspendSettingsSave = true;
    private bool _disposed;

    private int _capacity = 20_000;
    private string _activeSearch = string.Empty;

    /// <summary>Создаёт модель представления. Все зависимости — контракты Core из DI.</summary>
    public MainViewModel(
        ISettingsStore settingsStore,
        IGameInstallLocator locator,
        IProjectStore projectStore,
        IBuildRunner buildRunner,
        ILogParser parser,
        ILogTailerFactory tailerFactory,
        ITracebackMapper mapper,
        IDeployProbe deployProbe,
        IEditorLauncher editor,
        ApiBrowserViewModel? apiBrowser = null)
    {
        ApiBrowser = apiBrowser;
        _settingsStore = settingsStore;
        _locator = locator;
        _projectStore = projectStore;
        _buildRunner = buildRunner;
        _tailerFactory = tailerFactory;
        _mapper = mapper;
        _deployProbe = deployProbe;
        _editor = editor;

        _framesFactory = BuildFrames;

        // EmitUnparsedLines: строка, которая была в файле, обязана быть на экране — пусть и
        // уровнем UNKNOWN. В инструменте отладки молча проглоченная строка хуже неразобранной.
        _assembler = new LogEntryAssembler(parser) { EmitUnparsedLines = true };

        _pump = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(PumpIntervalMs)
        };
        _pump.Tick += OnPump;

        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _searchDebounce.Tick += OnSearchDebounceTick;

        try
        {
            _editor.LaunchFailed += (_, message) => StatusMessage = "VS Code: " + message;
        }
        catch (Exception ex)
        {
            StatusMessage = "редактор недоступен: " + Short(ex);
        }
    }

    // =================================================================== шапка

    /// <summary>Корень установки игры (<c>C:\Games\Tanki</c>). Пусто = игра не выбрана.</summary>
    [ObservableProperty]
    private string _gameDir = string.Empty;

    /// <summary>Активная версия клиента из paths.xml, например <c>1.44.0.0</c>.</summary>
    [ObservableProperty]
    private string _gameVersion = string.Empty;

    /// <summary>Что показать про игру, когда она не определилась.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogEmptyHint))]
    private string _gameProblem = "игра не выбрана";

    /// <summary>Каталог проекта мода.</summary>
    [ObservableProperty]
    private string _projectDir = string.Empty;

    /// <summary>Идентификатор мода из meta.xml.</summary>
    [ObservableProperty]
    private string _modId = string.Empty;

    /// <summary>Версия мода из meta.xml.</summary>
    [ObservableProperty]
    private string _modVersion = string.Empty;

    /// <summary>Человекочитаемое имя мода.</summary>
    [ObservableProperty]
    private string _modDisplayName = string.Empty;

    /// <summary>Что показать про проект, когда он не загружен.</summary>
    [ObservableProperty]
    private string _projectProblem = "проект не выбран";

    /// <summary>Замечания <see cref="IProjectStore.Validate"/>: показываются баннером в шапке.</summary>
    public ObservableCollection<ProjectValidation> ProjectIssues { get; } = new();

    /// <summary>Есть ли среди замечаний хоть одно уровня Error.</summary>
    [ObservableProperty]
    private bool _hasBlockingIssues;

    /// <summary>
    /// В каталоге проекта нет <c>wotmod.json</c>. Пока это так, замечание <c>NO_WOTMOD_JSON</c>
    /// снять нечем, поэтому рядом с ним показывается кнопка «создать wotmod.json».
    /// </summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateWotmodJsonCommand))]
    private bool _wotmodJsonMissing;

    // =================================================================== деплой

    /// <summary>В <c>mods\&lt;ver&gt;\</c> найден .mtmod.</summary>
    [ObservableProperty]
    private bool _deployInMods;

    /// <summary>Путь к найденному .mtmod.</summary>
    [ObservableProperty]
    private string _deployMtmodPath = string.Empty;

    /// <summary>Размер и время записи .mtmod для строки «установлено тогда-то».</summary>
    [ObservableProperty]
    private string _deployMtmodInfo = string.Empty;

    /// <summary>В <c>res_mods\&lt;ver&gt;\</c> лежит россыпь мода.</summary>
    [ObservableProperty]
    private bool _deployInResMods;

    /// <summary>Сколько файлов россыпи найдено.</summary>
    [ObservableProperty]
    private int _deployResModsCount;

    /// <summary>
    /// Россыпь перекрывает .mtmod. Ради этого флага панель деплоя вообще существует:
    /// в paths.xml res_mods стоит ПЕРВЫМ.
    /// </summary>
    [ObservableProperty]
    private bool _isShadowed;

    /// <summary>Сколько старых .mtmod того же мода валяется рядом.</summary>
    [ObservableProperty]
    private int _deployStaleCount;

    /// <summary>Короткая сводка состояния для случая, когда деталей нет.</summary>
    [ObservableProperty]
    private string _deploySummary = "деплой не опрошен";

    /// <summary>Файлы россыпи (первые несколько показываются в подсказке).</summary>
    public ObservableCollection<string> DeployResModsFiles { get; } = new();

    // =================================================================== лог

    /// <summary>Видимые (прошедшие фильтр) записи. Именно к ней привязан виртуализированный список.</summary>
    public BulkObservableCollection<LogEntryViewModel> LogEntries { get; } = new();

    /// <summary>Показывать только записи мода (<see cref="LogEntry.IsMine"/>).</summary>
    [ObservableProperty]
    private bool _onlyMine;

    /// <summary>Показывать только ERROR и WARNING.</summary>
    [ObservableProperty]
    private bool _onlyErrors;

    /// <summary>Прокручивать ленту к последней записи.</summary>
    [ObservableProperty]
    private bool _autoScroll = true;

    /// <summary>Подстрока поиска. Применяется с задержкой 250 мс.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Сколько записей в буфере (до фильтра).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogEmptyHint))]
    private int _totalCount;

    /// <summary>Сколько записей показано (после фильтра).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogEmptyHint))]
    private int _visibleCount;

    /// <summary>Ёмкость кольцевого буфера — для строки состояния.</summary>
    public int BufferCapacity => _capacity;

    /// <summary>Состояние слежения за python.log для строки состояния.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LogEmptyHint))]
    private string _tailerStatus = "лог не отслеживается";

    /// <summary>
    /// Осмысленное пустое состояние ленты: почему записей не видно и что с этим делать.
    /// «Пусто» без объяснения — худшее, что можно показать в инструменте отладки.
    /// </summary>
    public string LogEmptyHint
    {
        get
        {
            if (_game is null)
            {
                return "Игра не найдена.\n\n"
                       + "Укажите каталог установки в шапке — кокпит прочитает paths.xml,\n"
                       + "определит активную версию и начнёт следить за python.log.";
            }

            if (TotalCount == 0)
            {
                return "python.log пока пуст.\n\n"
                       + TailerStatus + "\n"
                       + "Запустите игру: строки появятся здесь сами, перезапускать кокпит не нужно.";
            }

            return $"Ни одна из {TotalCount} записей не проходит фильтр.\n\n"
                   + "Снимите тумблеры «только мой мод» / «только ошибки» или очистите поиск.";
        }
    }

    /// <summary>Просят прокрутить ленту в конец. Обрабатывает <see cref="Views.LogView"/>.</summary>
    public event EventHandler? ScrollToEndRequested;

    // =================================================================== сборка

    /// <summary>Идёт длительная операция: тулбар заблокирован, доступна отмена.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunBuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelBuildCommand))]
    [NotifyCanExecuteChangedFor(nameof(CreateWotmodJsonCommand))]
    private bool _isBusy;

    /// <summary>Что именно сейчас происходит («сборка…», «установка в игру…»).</summary>
    [ObservableProperty]
    private string _busyText = string.Empty;

    /// <summary>Строки вывода build.py.</summary>
    public BulkObservableCollection<BuildOutputLine> BuildOutput { get; } = new();

    /// <summary>Показывать панель вывода сборки.</summary>
    [ObservableProperty]
    private bool _showBuildPanel = true;

    /// <summary>Добавлять <c>--clean</c> к вызову build.py.</summary>
    [ObservableProperty]
    private bool _cleanBuild;

    /// <summary>Командная строка последнего запуска build.py.</summary>
    [ObservableProperty]
    private string _lastCommandLine = string.Empty;

    // =================================================================== состояние

    /// <summary>Найденный python и его версия.</summary>
    [ObservableProperty]
    private string _pythonInfo = "python не проверен";

    /// <summary>Доступен ли внешний редактор.</summary>
    [ObservableProperty]
    private bool _editorAvailable;

    /// <summary>Последнее сообщение для строки состояния.</summary>
    [ObservableProperty]
    private string _statusMessage = "готов";

    /// <summary>
    /// Диалог подтверждения записи в игру. Вынесен свойством, чтобы его можно было подменить
    /// (тесты, автоматизация): аргументы — заголовок и текст, результат — «пользователь согласился».
    /// </summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    // =================================================================== экраны

    /// <summary>
    /// Модель второго экрана — браузера API по декомпилированным исходникам клиента.
    /// <c>null</c>, если он не зарегистрирован (так собирают модель существующие тесты).
    /// </summary>
    public ApiBrowserViewModel? ApiBrowser { get; }

    /// <summary>Показан браузер API вместо экрана сборки и лога.</summary>
    [ObservableProperty]
    private bool _isApiScreen;

    /// <summary>Браузер API вообще доступен (зарегистрирован в контейнере).</summary>
    public bool HasApiBrowser => ApiBrowser is not null;

    /// <summary>
    /// Открыть кадр traceback не в VS Code, а в браузере API — по ВИРТУАЛЬНОМУ пути.
    /// <para>
    /// Это второй способ смотреть кадр, и он нужен именно для клиента: в VS Code открывается
    /// файл с диска (если он вообще распакован), а здесь — карточка символа с его сигнатурой,
    /// декораторами и списком мест, откуда он вызывается. Для кадров самого мода полезнее
    /// по-прежнему VS Code, поэтому существующая кнопка никуда не девается.
    /// </para>
    /// </summary>
    /// <param name="frame">Кадр из развёрнутой записи лога.</param>
    [RelayCommand]
    private async Task OpenFrameInApiBrowserAsync(TracebackFrameViewModel? frame)
    {
        ApiBrowserViewModel? browser = ApiBrowser;
        if (browser is null)
        {
            StatusMessage = "браузер API недоступен";
            return;
        }

        if (frame is null)
        {
            return;
        }

        string virtualPath = frame.Mapping.Frame.VirtualPath;
        if (string.IsNullOrWhiteSpace(virtualPath))
        {
            StatusMessage = "у кадра нет виртуального пути";
            return;
        }

        IsApiScreen = true;

        bool found = await browser
            .OpenVirtualPathAsync(virtualPath, frame.Mapping.Frame.Line)
            .ConfigureAwait(true);

        StatusMessage = found
            ? "браузер API: " + virtualPath
            : "браузер API: файла " + virtualPath + " нет в индексе";
    }

    // =================================================================== жизненный цикл

    /// <summary>
    /// Поднимает состояние: настройки, установка игры, последний проект, python, слежение за логом.
    /// Вызывается из <c>MainWindow.Loaded</c>, а не из конструктора: конструктор обязан быть быстрым
    /// и не лезть на диск.
    /// </summary>
    public void Initialize()
    {
        _settings = Try(() => _settingsStore.Load(), new CockpitSettings(), "настройки");
        _capacity = Math.Clamp(_settings.LogBufferCapacity, 1_000, 200_000);
        OnPropertyChanged(nameof(BufferCapacity));

        OnlyMine = _settings.LogOnlyMine;

        // Путь к редактору и шаблон аргументов живут в настройках, а Configure объявлен только
        // на конкретной реализации: контракт IEditorLauncher про CockpitSettings ничего не знает.
        // Без этого вызова заданный вручную EditorExe молча игнорировался бы, и работал бы
        // только автопоиск.
        if (_editor is VsCodeEditorLauncher vsCode)
        {
            TryDo(
                () => vsCode.Configure(_settings.EditorExe, _settings.EditorArgsTemplate),
                "настройка редактора");
        }

        EditorAvailable = Try(() => _editor.IsAvailable, false, "поиск VS Code");

        _suspendSettingsSave = false;

        SetGameDir(_settings.GameDir, save: false);

        if (!string.IsNullOrWhiteSpace(_settings.LastProjectDir))
        {
            SetProjectDir(_settings.LastProjectDir, save: false);
        }

        // Браузер API работает с тем же settings.json и с тем же полем WotSrcDir, что и маппер
        // traceback. Отдаём ему ТОТ ЖЕ объект настроек: две независимо загруженные копии
        // затирали бы правки друг друга (главный экран сохраняет настройки на каждый тумблер).
        if (ApiBrowser is not null)
        {
            ApiBrowser.AttachSettings(_settings, SaveSettings);
            ApiBrowser.Initialize();
        }

        _pump.Start();
        _ = DetectPythonAsync();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _pump.Stop();
        _searchDebounce.Stop();
        _buildCts?.Cancel();
        ApiBrowser?.Dispose();
        StopTailing();
        SaveSettings();
    }

    // =================================================================== шапка: команды

    /// <summary>Выбрать каталог установки игры.</summary>
    [RelayCommand]
    private void BrowseGame()
    {
        string? picked = PickFolder("Каталог установки «Мира танков» (там, где Tanki.exe и paths.xml)", GameDir);
        if (picked is not null)
        {
            SetGameDir(picked, save: true);
        }
    }

    /// <summary>Выбрать каталог проекта мода.</summary>
    [RelayCommand]
    private void BrowseProject()
    {
        string? picked = PickFolder("Каталог проекта мода (там, где build.py и src\\meta.xml)", ProjectDir);
        if (picked is not null)
        {
            SetProjectDir(picked, save: true);
        }
    }

    /// <summary>Перечитать проект с диска (meta.xml мог измениться из VS Code).</summary>
    [RelayCommand]
    private void ReloadProject()
    {
        if (string.IsNullOrWhiteSpace(ProjectDir))
        {
            StatusMessage = "проект не выбран";
            return;
        }

        SetProjectDir(ProjectDir, save: false);
        StatusMessage = "проект перечитан";
    }

    private bool CanCreateWotmodJson() => !IsBusy && _project is not null && WotmodJsonMissing;

    /// <summary>
    /// Создать <c>wotmod.json</c> из того, что кокпит вывел из каталога проекта.
    /// <para>
    /// Это ЕДИНСТВЕННАЯ запись кокпита на диск помимо своих настроек, и она идёт в каталог
    /// ПРОЕКТА, а не игры, по явному нажатию. <c>src\meta.xml</c> не трогается: манифест мода —
    /// исходник, а не наш кэш (см. <see cref="IProjectStore.SyncMetaXml"/>).
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreateWotmodJson))]
    private void CreateWotmodJson()
    {
        ModProject? project = _project;
        if (project is null)
        {
            StatusMessage = "проект не выбран";
            return;
        }

        string path = Path.Combine(project.ProjectDir, "wotmod.json");

        if (!ConfirmHandler(
                "Создать wotmod.json",
                "В каталог ПРОЕКТА (не игры) будет записан дескриптор кокпита:\n\n" +
                $"    {path}\n\n" +
                "В нём окажутся тег лога, точка входа и имя пакета —\n" +
                "всё то, что сейчас выведено из содержимого каталога.\n" +
                "Каталог игры и Python этой машины туда не пишутся: их берут настройки кокпита.\n" +
                "src\\meta.xml не изменится. Продолжить?"))
        {
            StatusMessage = "отменено пользователем";
            return;
        }

        TryDo(() => _projectStore.Save(project), "сохранение wotmod.json");

        // Перечитываем: замечание NO_WOTMOD_JSON должно уйти из баннера прямо сейчас.
        SetProjectDir(ProjectDir, save: false);
        StatusMessage = File.Exists(path) ? "создан " + path : "wotmod.json создать не удалось";
    }

    /// <summary>Открыть каталог проекта в VS Code.</summary>
    [RelayCommand]
    private void OpenInEditor()
    {
        ModProject? project = _project;
        if (project is null)
        {
            StatusMessage = "проект не выбран";
            return;
        }

        TryDo(() => _editor.OpenFolder(project.ProjectDir), "открытие VS Code");
    }

    /// <summary>Открыть файл кадра traceback в редакторе на нужной строке.</summary>
    /// <param name="frame">Кадр из развёрнутой записи лога.</param>
    [RelayCommand]
    private void OpenFrame(TracebackFrameViewModel? frame)
    {
        if (frame is null)
        {
            return;
        }

        if (!frame.IsResolved || string.IsNullOrEmpty(frame.DiskPath))
        {
            StatusMessage = "кадр не сопоставлен с файлом на диске: " + frame.Display;
            return;
        }

        TryDo(() => _editor.OpenAt(frame.DiskPath!, frame.TargetLine), "открытие файла в VS Code");
        StatusMessage = $"открыто: {frame.DiskPath}:{frame.TargetLine}"
                        + (frame.IsApproximate ? " (строка приблизительная)" : string.Empty);
    }

    // =================================================================== сборка

    /// <summary>
    /// Можно ли запускать это действие. Действия, ПИШУЩИЕ В ИГРУ, дополнительно требуют
    /// опознанной установки: без разобранного paths.xml неизвестны ни версия, ни каталог
    /// назначения, и диалог подтверждения показывал бы не тот путь — а он единственная
    /// преграда перед записью в игру.
    /// </summary>
    /// <param name="action">Что собираются делать.</param>
    /// <returns>true, если кнопку можно оставить активной.</returns>
    private bool CanRunBuild(BuildAction action)
    {
        if (IsBusy || _project is null)
        {
            return false;
        }

        return !action.WritesToGame() || _game is not null;
    }

    /// <summary>
    /// Запускает build.py. Действия, пишущие в игру, сначала спрашивают подтверждение
    /// с явным указанием каталога назначения.
    /// </summary>
    /// <param name="action">Что делать.</param>
    [RelayCommand(CanExecute = nameof(CanRunBuild))]
    private async Task RunBuildAsync(BuildAction action)
    {
        ModProject? project = _project;
        if (project is null)
        {
            StatusMessage = "проект не выбран";
            return;
        }

        if (action.WritesToGame() && _game is null)
        {
            StatusMessage = "каталог игры не опознан — записывать некуда";
            return;
        }

        if (!ConfirmCleanKeepsUninstallable(project, DeployInResMods))
        {
            StatusMessage = "отменено пользователем";
            return;
        }

        // Этап 3: preflight перед сборкой. Не блокирует — только требует подтверждения,
        // если нашлись находки уровня «клиент закроется». См. MainViewModel.Stage3.cs.
        if (!await Stage3PreflightGateAsync(project).ConfigureAwait(true))
        {
            StatusMessage = "сборка отменена — см. панель Problems";
            return;
        }

        if (action.WritesToGame() && !ConfirmWrite(action))
        {
            StatusMessage = "отменено пользователем";
            return;
        }

        await RunBuildCoreAsync(action, project, DescribeAction(action)).ConfigureAwait(true);
    }

    /// <summary>
    /// Сам запуск build.py — без проверок и подтверждений: их делает вызывающий. Общий для
    /// кнопок этого экрана и для «Собрать и установить» на экране «Моды»: одна панель вывода,
    /// один флаг занятости и одна кнопка отмены на оба экрана.
    /// </summary>
    /// <param name="action">Что делать.</param>
    /// <param name="project">Что собирать — не обязательно открытый проект.</param>
    /// <param name="busyText">Что показать, пока идёт запуск.</param>
    /// <param name="external">Отмена со стороны того, кто просил сборку; кнопка «Отмена» экрана работает и без неё.</param>
    /// <returns>
    /// Итог build.py. Отмена и исключение запуска — тоже итог, с кодами
    /// <see cref="BuildResult.ExitCodeCancelled"/> и <see cref="BuildResult.ExitCodeLaunchFailed"/>.
    /// </returns>
    private async Task<BuildResult> RunBuildCoreAsync(
        BuildAction action, ModProject project, string busyText, CancellationToken external = default)
    {
        // Одно правило с открытием проекта (EffectiveGameDirFor): игра из шапки, иначе gameDir
        // проекта. Явно — потому что проект с экрана «Моды» открытым не становится.
        string gameDir = EffectiveGameDirFor(project, GameDir);
        var options = new BuildRunOptions
        {
            Clean = CleanBuild,
            GameDirOverride = gameDir.Length == 0 ? null : gameDir
        };

        IsBusy = true;
        BusyText = busyText;
        ShowBuildPanel = true;
        BuildOutput.ReplaceAll(Array.Empty<BuildOutputLine>());

        // Остановить сборку может и кнопка «Отмена» этого экрана, и тот, кто сборку попросил
        // (окно перехода на новую версию держит главное окно модальным — до этой кнопки не добраться).
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(external);
        _buildCts = cts;

        var progress = new Progress<BuildOutputLine>(AppendBuildLine);
        var sw = Stopwatch.StartNew();

        try
        {
            // Python для build.py: проект → настройки кокпита → python из PATH → py -0p/реестр.
            // Поиск запускает процессы, поэтому в пуле потоков; результат кэшируется локатором.
            string settingsPython = _settings.PythonExe ?? string.Empty;
            string? python = await Task
                .Run(() => PythonLocator.ResolveForBuild(project.PythonExe, settingsPython), cts.Token)
                .ConfigureAwait(true);
            project.EffectivePythonExe = python ?? string.Empty;

            BuildResult result = await _buildRunner
                .RunAsync(action, project, options, progress, cts.Token)
                .ConfigureAwait(true);

            LastCommandLine = result.CommandLine;

            if (result.Success)
            {
                StatusMessage = string.IsNullOrEmpty(result.ArtifactPath)
                    ? $"{BusyText} — готово за {result.Duration.TotalSeconds:0.0} с"
                    : $"{BusyText} — готово: {Path.GetFileName(result.ArtifactPath)} за {result.Duration.TotalSeconds:0.0} с";
            }
            else
            {
                StatusMessage = $"{BusyText} — неудача (код {result.ExitCode}): {result.FailureReason ?? "см. вывод сборки"}";
                AppendBuildLine(BuildOutputLine.Note(StatusMessage));
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = BusyText + " — отменено";
            AppendBuildLine(BuildOutputLine.Note("отменено пользователем"));
            return BuildResult.Cancelled(string.Empty, sw.Elapsed);
        }
        catch (Exception ex)
        {
            StatusMessage = BusyText + " — ошибка: " + Short(ex);
            AppendBuildLine(BuildOutputLine.Note("ОШИБКА ЗАПУСКА: " + Short(ex)));
            return BuildResult.LaunchFailed(string.Empty, Short(ex));
        }
        finally
        {
            sw.Stop();
            _buildCts = null;
            IsBusy = false;
            BusyText = string.Empty;

            // Прерванная на середине Dev-раскладка могла оставить в res_mods половину файлов —
            // состояние деплоя обязано быть перечитано в любом исходе.
            RefreshDeploy();

            // build.py пишет в каталог модов («Собрать и установить», «Удалить из игры») и в
            // dist\ мимо экрана «Моды», и тот показывал бы снимок до сборки: удалённый мод
            // висел бы «ВКЛЮЧЁН». Скрытый экран и так перечитает каталог при показе, показанный —
            // перечитает сейчас.
            _mods?.NotifyExternalChange();
        }
    }

    private bool CanCancelBuild() => IsBusy;

    /// <summary>Убить запущенный build.py.</summary>
    [RelayCommand(CanExecute = nameof(CanCancelBuild))]
    private void CancelBuild()
    {
        _buildCts?.Cancel();
        StatusMessage = "отмена…";
    }

    /// <summary>Скопировать в буфер обмена командную строку последнего запуска build.py.</summary>
    [RelayCommand]
    private void CopyCommandLine()
    {
        if (string.IsNullOrEmpty(LastCommandLine))
        {
            StatusMessage = "команда ещё не запускалась";
            return;
        }

        TryDo(() => Clipboard.SetText(LastCommandLine), "копирование в буфер");
        StatusMessage = "команда скопирована";
    }

    private void AppendBuildLine(BuildOutputLine line)
    {
        BuildOutput.Add(line);
        if (BuildOutput.Count > BuildOutputCap)
        {
            BuildOutput.RemoveFirst(BuildOutput.Count - BuildOutputCap);
        }
    }

    private static string DescribeAction(BuildAction action) => action switch
    {
        BuildAction.Build => "сборка",
        BuildAction.BuildAndInstall => "сборка и установка в игру",
        BuildAction.DevDeploy => "Dev-раскладка в res_mods",
        BuildAction.Uninstall => "удаление из игры",
        BuildAction.List => "листинг архива",
        _ => "операция"
    };

    /// <summary>
    /// Предупреждение про <c>--clean</c> поверх Dev-раскладки.
    /// <para>
    /// <c>build.py --clean</c> делает <c>shutil.rmtree(dist)</c>, а в <c>dist\</c> лежит
    /// <c>dev_manifest.json</c> — ЕДИНСТВЕННЫЙ список файлов, который <c>--uninstall</c> умеет
    /// убирать из <c>res_mods</c>. Потеряв его, кнопка «Удалить из игры» превращается в
    /// «nothing to remove», а россыпь остаётся в игре навсегда и молча перекрывает любой .mtmod.
    /// Свежий build.py из шаблона манифест сохраняет, но кокпит может быть наведён и на проект
    /// со старой копией скрипта, поэтому спрашиваем.
    /// </para>
    /// </summary>
    /// <param name="project">Проект, который собирают.</param>
    /// <param name="inResMods">В игре лежит россыпь ЭТОГО проекта.</param>
    /// <returns>true, если можно продолжать.</returns>
    private bool ConfirmCleanKeepsUninstallable(ModProject project, bool inResMods)
    {
        if (!CleanBuild || !inResMods)
        {
            return true;
        }

        string manifest;
        try
        {
            manifest = Path.Combine(project.DistDir, "dev_manifest.json");
            if (!File.Exists(manifest))
            {
                return true;
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "проверка dev_manifest.json: " + Short(ex);
            return true;
        }

        return ConfirmHandler(
            "Чип --clean и раскладка в res_mods",
            "В игре сейчас лежит РОССЫПЬ этого мода, а вы запускаете сборку с --clean.\n\n" +
            $"    {manifest}\n\n" +
            "--clean стирает весь dist\\, вместе с этим манифестом. Он — единственный список\n" +
            "файлов, который «Удалить из игры» (build.py --uninstall) умеет убирать из res_mods.\n" +
            "Без него россыпь останется в игре навсегда и будет перекрывать .mtmod.\n\n" +
            "Безопасный порядок: сначала «Удалить из игры», потом --clean.\n\n" +
            "Продолжить с --clean?");
    }

    private bool ConfirmWrite(BuildAction action)
    {
        // _game здесь заведомо не null: см. CanRunBuild и проверку в начале RunBuildAsync.
        // Формулировки на случай, если запуск всё же пришёл мимо них, — честные, без подстановки
        // корня установки вместо mods\<версия>.
        string modsDir = _game?.ModsDir ?? "<каталог игры не опознан: назначение определит build.py>";
        string resModsDir = _game?.ResModsDir ?? modsDir;

        string body = action switch
        {
            BuildAction.BuildAndInstall =>
                "Собранный .mtmod будет СКОПИРОВАН в каталог игры:\n\n" +
                $"    {modsDir}\n\n" +
                "Запись выполнит build.py (флаг --install). Продолжить?",

            BuildAction.DevDeploy =>
                "Исходники будут разложены РОССЫПЬЮ в каталог игры:\n\n" +
                $"    {resModsDir}\n\n" +
                "Внимание: в paths.xml res_mods идёт ПЕРВЫМ, поэтому эта россыпь будет\n" +
                "перекрывать установленный .mtmod, пока вы её не удалите.\n\n" +
                "Запись выполнит build.py (флаг --dev). Продолжить?",

            BuildAction.Uninstall =>
                "Из каталога игры будут УДАЛЕНЫ и .mtmod, и россыпь:\n\n" +
                $"    {modsDir}\n" +
                $"    {resModsDir}\n\n" +
                "Удаление выполнит build.py (флаг --uninstall). Продолжить?",

            _ => "Продолжить?"
        };

        return ConfirmHandler("Запись в каталог игры", body);
    }

    private static bool DefaultConfirm(string caption, string body)
        => MessageBox.Show(
               body,
               caption,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning,
               MessageBoxResult.No) == MessageBoxResult.Yes;

    // =================================================================== деплой

    /// <summary>Перечитать состояние мода в каталоге игры. Только чтение диска.</summary>
    [RelayCommand]
    private void RefreshDeploy()
    {
        GameInstall? game = _game;
        ModProject? project = _project;

        if (game is null)
        {
            ResetDeploy("игра не найдена — состояние деплоя неизвестно");
            return;
        }

        if (project is null)
        {
            ResetDeploy("проект не выбран — искать в игре нечего");
            return;
        }

        DeployState state;
        try
        {
            state = _deployProbe.Probe(game, project);
        }
        catch (Exception ex)
        {
            ResetDeploy("опрос деплоя не удался: " + Short(ex));
            return;
        }

        DeployInMods = state.InMods;
        DeployMtmodPath = state.MtmodPath ?? string.Empty;
        DeployInResMods = state.InResMods;
        DeployResModsCount = state.ResModsFiles.Count;
        IsShadowed = state.IsShadowed;
        DeployStaleCount = state.StaleMtmodPaths.Count;

        DeployResModsFiles.Clear();
        foreach (string f in state.ResModsFiles.Take(50))
        {
            DeployResModsFiles.Add(f);
        }

        DeployMtmodInfo = state.MtmodPath is null
            ? string.Empty
            : $"{FormatSize(state.MtmodSizeBytes)} · {FormatWhen(state.MtmodWriteTimeUtc)}";

        DeploySummary = state.IsAbsent
            ? "мода в игре нет ни в каком виде"
            : state.IsShadowed
                ? "мод и в mods, и в res_mods"
                : state.InMods
                    ? "установлен пакетом .mtmod"
                    : "разложен россыпью в res_mods";
    }

    private void ResetDeploy(string summary)
    {
        DeployInMods = false;
        DeployMtmodPath = string.Empty;
        DeployMtmodInfo = string.Empty;
        DeployInResMods = false;
        DeployResModsCount = 0;
        IsShadowed = false;
        DeployStaleCount = 0;
        DeployResModsFiles.Clear();
        DeploySummary = summary;
    }

    private static string FormatSize(long? bytes) => bytes switch
    {
        null => "?",
        < 1024 => $"{bytes} Б",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} КБ",
        _ => $"{bytes / (1024.0 * 1024.0):0.##} МБ"
    };

    private static string FormatWhen(DateTimeOffset? utc)
    {
        if (utc is null)
        {
            return "время неизвестно";
        }

        TimeSpan age = DateTimeOffset.UtcNow - utc.Value;
        return age switch
        {
            { TotalSeconds: < 90 } => "только что",
            { TotalMinutes: < 90 } => $"{(int)age.TotalMinutes} мин назад",
            { TotalHours: < 36 } => $"{(int)age.TotalHours} ч назад",
            _ => utc.Value.ToLocalTime().ToString("dd.MM HH:mm")
        };
    }

    // =================================================================== лог: команды и фильтр

    /// <summary>Очистить представление: и буфер, и видимый список.</summary>
    [RelayCommand]
    private void ClearLog()
    {
        ClearView();
        StatusMessage = "представление очищено (файл лога не тронут)";
    }

    /// <summary>Очистка ленты без сообщения в строке состояния.</summary>
    private void ClearView()
    {
        _all.Clear();
        LogEntries.ReplaceAll(Array.Empty<LogEntryViewModel>());
        while (_incoming.TryDequeue(out _))
        {
            // выбрасываем всё, что не успело доехать
        }

        TotalCount = 0;
        VisibleCount = 0;
    }

    /// <summary>Перечитать python.log с нуля: старое содержимое ленты выбрасывается.</summary>
    [RelayCommand]
    private void RestartTailing()
    {
        // Кнопка называется «перечитать», а не «дочитать»: прогрев отдаёт те же последние
        // килобайты файла заново, и без очистки лента показала бы их вторым экземпляром
        // подряд, без разделителя и без всякого объяснения.
        ClearView();
        StartTailing();
        StatusMessage = "лог перечитан с начала прогрева";
    }

    /// <summary>Прочитать python.log ЦЕЛИКОМ, а не последние килобайты прогрева.</summary>
    [RelayCommand]
    private void ReadWholeLog()
    {
        ClearView();
        StartTailing(int.MaxValue);
        StatusMessage = "прочитан весь python.log, а не хвост прогрева";
    }

    partial void OnOnlyMineChanged(bool value)
    {
        _filter.OnlyMine = value;
        ApplyFilter();
        SaveSettings();
    }

    partial void OnOnlyErrorsChanged(bool value)
    {
        _filter.OnlyErrors = value;
        ApplyFilter();
    }

    partial void OnAutoScrollChanged(bool value)
    {
        if (value)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        _activeSearch = SearchText?.Trim() ?? string.Empty;
        _filter.Text = _activeSearch;
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        LogEntries.ReplaceAll(_all.Where(Passes));
        VisibleCount = LogEntries.Count;

        if (AutoScroll)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Проходит ли запись фильтр. Условия живут в <see cref="LogFilter"/> — второй реализации
    /// здесь нет намеренно: расхождение семантики «только ошибки» между Core и UI однажды уже
    /// давало разницу в сотни записей на реальном python.log.
    /// </summary>
    /// <param name="vm">Запись ленты.</param>
    /// <returns>true, если запись надо показать.</returns>
    private bool Passes(LogEntryViewModel vm)
    {
        // Системные строки кокпита (разделитель усечения) фильтрам уровня и принадлежности
        // не подчиняются: разделитель обязан быть виден и в режиме «только ошибки».
        return vm.IsSystem ? vm.Matches(_activeSearch) : _filter.IsMatch(vm.Entry);
    }

    // =================================================================== лог: конвейер

    /// <summary>
    /// Поднимает слежение за python.log.
    /// </summary>
    /// <param name="primeBytesOverride">
    /// Сколько последних байт прочитать при старте вместо <c>LogPrimeTailKilobytes</c>.
    /// <see cref="int.MaxValue"/> = весь файл (кнопка «дочитать весь файл»).
    /// </param>
    private void StartTailing(int? primeBytesOverride = null)
    {
        StopTailing();

        if (_game is null)
        {
            TailerStatus = "игра не найдена";
            return;
        }

        string path = _game.PythonLogPath;

        ILogTailer? tailer = Try<ILogTailer?>(() => _tailerFactory.Create(path), null, "создание тейлера");
        if (tailer is null)
        {
            TailerStatus = "тейлер недоступен";
            return;
        }

        _tailer = tailer;
        _tailer.LineRead += OnTailerLine;
        _tailer.FileTruncated += OnTailerTruncated;
        _tailer.Error += OnTailerError;

        // Клампим килобайты: настройку правят руками, а KB * 1024 обязано пережить любое число.
        int prime = primeBytesOverride
                    ?? Math.Clamp(_settings.LogPrimeTailKilobytes, 0, 1_000_000) * 1024;

        try
        {
            _tailer.Start(prime);
            TailerStatus = File.Exists(path) ? "следим за python.log" : "python.log ещё нет — ждём";
        }
        catch (Exception ex)
        {
            TailerStatus = "слежение не запущено: " + Short(ex);
        }
    }

    private void StopTailing()
    {
        if (_tailer is null)
        {
            return;
        }

        _tailer.LineRead -= OnTailerLine;
        _tailer.FileTruncated -= OnTailerTruncated;
        _tailer.Error -= OnTailerError;

        try
        {
            _tailer.Stop();
        }
        catch (Exception)
        {
            // Заглушка тейлера имеет право бросить: гасим, чтобы не уронить закрытие окна.
        }

        try
        {
            _tailer.Dispose();
        }
        catch (Exception)
        {
            // то же самое
        }

        _tailer = null;

        lock (_glue)
        {
            _assembler.Reset();
        }
    }

    /// <summary>Приходит из ФОНОВОГО потока тейлера, строго по одной строке и в порядке файла.</summary>
    private void OnTailerLine(object? sender, string line)
    {
        Volatile.Write(ref _lastLineTicks, Environment.TickCount64);

        lock (_glue)
        {
            if (_parserBroken)
            {
                EnqueueLocked(RawEntry(line));
                return;
            }

            try
            {
                foreach (LogEntry entry in _assembler.Push(line))
                {
                    EnqueueLocked(entry);
                }
            }
            catch (Exception ex)
            {
                // Push — итератор, исключение парсера прилетает на переборе.
                MarkParserBroken(ex);
                EnqueueLocked(RawEntry(line));
            }
        }
    }

    private void OnTailerTruncated(object? sender, EventArgs e)
    {
        lock (_glue)
        {
            // Незакрытая запись принадлежит ещё СТАРОМУ содержимому: отдаём её до разделителя,
            // иначе последняя запись перед усечением молча пропадает вместе с телом traceback.
            FlushOpenLocked();

            // Сброс склейки обязателен по контракту ILogTailer: накопленное тело traceback
            // принадлежит выброшенной части файла, а байтовые смещения стали недействительны.
            _assembler.Reset();

            // Разделитель кладём ПРЯМО В ПОТОК, из потока тейлера. Если ставить его из UI-такта,
            // строки нового содержимого, которые тейлер успел положить в _incoming сразу после
            // события, окажутся ВЫШЕ разделителя, и он перестанет означать «ниже — новый файл».
            _incoming.Enqueue(SystemEntry("── лог усечён игрой; уже показанные строки не повторяем ──"));
        }

        Interlocked.Exchange(ref _truncationPending, 1);
    }

    private void OnTailerError(object? sender, Exception e)
    {
        Interlocked.Exchange(ref _pendingNotice, "чтение python.log: " + Short(e));
    }

    /// <summary>Закрывает накопленную запись и ставит её в очередь. Вызывать только под <see cref="_glue"/>.</summary>
    private void FlushOpenLocked()
    {
        if (_parserBroken)
        {
            return;
        }

        try
        {
            foreach (LogEntry entry in _assembler.Flush())
            {
                EnqueueLocked(entry);
            }
        }
        catch (Exception ex)
        {
            MarkParserBroken(ex);
        }
    }

    private void EnqueueLocked(LogEntry entry)
        => _incoming.Enqueue(new LogEntryViewModel(entry, _mapperBroken ? null : _framesFactory));

    private static LogEntry RawEntry(string line) =>
        new(null, LogLevels.Unknown, string.Empty, null, null, line, Array.Empty<string>(), false)
        {
            RawLine = line
        };

    private static LogEntryViewModel SystemEntry(string text)
    {
        var entry = new LogEntry(
            DateTimeOffset.Now,
            LogLevels.Notice,
            "cockpit",
            null,
            null,
            text,
            Array.Empty<string>(),
            false)
        {
            RawLine = text
        };

        return new LogEntryViewModel(entry, null, isSystem: true);
    }

    private void MarkParserBroken(Exception ex)
    {
        if (_parserBroken)
        {
            return;
        }

        _parserBroken = true;
        Interlocked.Exchange(
            ref _pendingNotice,
            "разбор лога отключён: " + Short(ex) + " — строки показываются сырыми");
    }

    /// <summary>Такт выгрузки: раз в 100 мс переносим накопленное в UI одним пакетом.</summary>
    private void OnPump(object? sender, EventArgs e)
    {
        string? notice = Interlocked.Exchange(ref _pendingNotice, null);
        if (notice is not null)
        {
            StatusMessage = notice;
        }

        long last = Volatile.Read(ref _lastLineTicks);
        if (last != 0 && Environment.TickCount64 - last > IdleFlushMs)
        {
            lock (_glue)
            {
                FlushOpenLocked();
            }
        }

        // Сам разделитель уже стоит в очереди на своём месте (см. OnTailerTruncated);
        // здесь остаётся только текст строки состояния — его можно трогать лишь из UI-потока.
        if (Interlocked.Exchange(ref _truncationPending, 0) == 1)
        {
            TailerStatus = "файл усечён игрой — смещения сброшены";
        }

        if (_incoming.IsEmpty)
        {
            return;
        }

        var batch = new List<LogEntryViewModel>(256);
        while (batch.Count < MaxLinesPerTick && _incoming.TryDequeue(out LogEntryViewModel? vm))
        {
            batch.Add(vm);
        }

        var toShow = new List<LogEntryViewModel>(batch.Count);
        foreach (LogEntryViewModel vm in batch)
        {
            _all.Enqueue(vm);
            if (Passes(vm))
            {
                toShow.Add(vm);
            }
        }

        // ПОРЯДОК ВАЖЕН: сначала показать новое, потом вытеснять.
        // Вытеснение опирается на инвариант «видимый список — подпоследовательность буфера»,
        // а он держится только тогда, когда пачка УЖЕ доехала до LogEntries. Если вытеснять
        // раньше, а пачка больше ёмкости (MaxLinesPerTick = 4000 против минимальной ёмкости 1000),
        // вытеснение доедает до самой пачки: её записи выкидываются из _all, но всё равно
        // добавляются в LogEntries, выравнивание голов ломается навсегда, и видимый список
        // растёт без предела (наблюдалось «показано 5504 из 1000 (буфер 1000)»).
        LogEntries.AddRange(toShow);

        // Вытеснение старых записей: что ушло с головы буфера, то уходит и с головы видимого списка.
        int overflow = _all.Count - _capacity;
        if (overflow > 0)
        {
            int visibleDropped = 0;
            for (int i = 0; i < overflow; i++)
            {
                LogEntryViewModel dropped = _all.Dequeue();
                if (visibleDropped < LogEntries.Count && ReferenceEquals(LogEntries[visibleDropped], dropped))
                {
                    visibleDropped++;
                }
            }

            LogEntries.RemoveFirst(visibleDropped);
        }

        TotalCount = _all.Count;
        VisibleCount = LogEntries.Count;

        if (AutoScroll && toShow.Count > 0)
        {
            ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private IReadOnlyList<TracebackFrameViewModel> BuildFrames(LogEntryViewModel vm)
    {
        if (_mapperBroken)
        {
            return Array.Empty<TracebackFrameViewModel>();
        }

        IReadOnlyList<TracebackFrame> frames;
        try
        {
            frames = _mapper.ExtractFrames(vm.Entry);
        }
        catch (Exception ex)
        {
            _mapperBroken = true;
            StatusMessage = "разбор traceback недоступен: " + Short(ex);
            return Array.Empty<TracebackFrameViewModel>();
        }

        if (frames.Count == 0)
        {
            return Array.Empty<TracebackFrameViewModel>();
        }

        string wotSrc = _settings.WotSrcDir ?? string.Empty;
        var result = new List<TracebackFrameViewModel>(frames.Count);

        foreach (TracebackFrame frame in frames)
        {
            TracebackMapping mapping;
            try
            {
                mapping = _mapper.Map(frame, _project, wotSrc, _game);
            }
            catch (Exception ex)
            {
                _mapperBroken = true;
                StatusMessage = "сопоставление traceback недоступно: " + Short(ex);
                mapping = TracebackMapping.Unresolved(frame);
            }

            result.Add(new TracebackFrameViewModel(mapping));
        }

        return result;
    }

    // =================================================================== игра и проект

    private void SetGameDir(string dir, bool save)
    {
        GameDir = dir ?? string.Empty;

        _game = string.IsNullOrWhiteSpace(GameDir)
            ? null
            : Try<GameInstall?>(() => _locator.Detect(GameDir), null, "разбор paths.xml");

        if (_game is null)
        {
            GameVersion = string.Empty;
            // Пустой каталог — обычный первый запуск на машине, где игра не нашлась ни в реестре,
            // ни в типовых папках: подсказываем, что делать, а не просто констатируем.
            GameProblem = string.IsNullOrWhiteSpace(GameDir)
                ? "игра не найдена — укажите каталог игры (там, где paths.xml)"
                : "не похоже на установку игры: нет разбираемого paths.xml";
            TailerStatus = "игра не найдена";
            StopTailing();
        }
        else
        {
            GameVersion = _game.Version;
            GameProblem = string.Empty;
            StartTailing();
        }

        if (_project is not null)
        {
            _project.EffectiveGameDir = EffectiveGameDirFor(_project, GameDir);
        }

        // Кнопки записи в игру живы только при опознанной установке — состояние сменилось.
        RunBuildCommand.NotifyCanExecuteChanged();
        RefreshDeploy();

        if (save)
        {
            SaveSettings();
        }
    }

    private void SetProjectDir(string dir, bool save)
    {
        ProjectDir = dir ?? string.Empty;

        _project = string.IsNullOrWhiteSpace(ProjectDir)
            ? null
            : Try<ModProject?>(() => _projectStore.Load(ProjectDir), null, "загрузка проекта");

        ProjectIssues.Clear();

        lock (_glue)
        {
            // Склейка помечает «свои» записи по тегу проекта, фильтр — по нему же запасным путём.
            _assembler.Project = _project;
        }

        _filter.Tag = _project?.LogTag ?? string.Empty;

        if (_project is null)
        {
            ModId = string.Empty;
            ModVersion = string.Empty;
            ModDisplayName = string.Empty;
            ProjectProblem = string.IsNullOrWhiteSpace(ProjectDir)
                ? "проект не выбран — откройте свой проект или создайте новый мод из шаблона"
                : "каталог не похож на проект мода: нет ни wotmod.json, ни build.py, ни src\\meta.xml";
            HasBlockingIssues = false;
            WotmodJsonMissing = false;
        }
        else
        {
            ModProject project = _project;

            ModId = project.ModId;
            ModVersion = project.Version;
            ModDisplayName = project.DisplayName;
            ProjectProblem = string.Empty;

            // Сниппеты браузера API подставляют идентификатор мода в имена функций и в тег лога.
            if (ApiBrowser is not null && !string.IsNullOrWhiteSpace(project.ModId))
            {
                ApiBrowser.ModId = project.ModId;
            }

            // Фактический каталог игры — в отдельном [JsonIgnore]-свойстве, а не в GameDir:
            // «Создать wotmod.json» сохраняет этот же объект, и подставленный сюда путь этой
            // машины уехал бы в git вместе с проектом. Главнее каталог из шапки (настроек
            // кокпита); свой gameDir проекта берётся, только когда игра в шапке не выбрана.
            project.EffectiveGameDir = EffectiveGameDirFor(project, GameDir);

            WotmodJsonMissing = !Try(
                () => File.Exists(Path.Combine(project.ProjectDir, "wotmod.json")),
                false,
                "проверка wotmod.json");

            _ = ValidateProjectAsync(project);

            EnsureWotSrcDir();
        }

        RunBuildCommand.NotifyCanExecuteChanged();
        CreateWotmodJsonCommand.NotifyCanExecuteChanged();
        RefreshDeploy();

        if (save)
        {
            SaveSettings();
        }
    }

    /// <summary>
    /// Каталог игры, с которым проект реально собирается и ставится: каталог из шапки, если игра
    /// там выбрана, иначе свой <c>gameDir</c> из <c>wotmod.json</c>. Шапка главнее всегда: опознание
    /// установки, кнопки записи и диалог подтверждения смотрят на неё, и сборка не имеет права
    /// писать не туда, куда показал диалог. Несуществующий <c>gameDir</c> проекта (его принесли
    /// с другой машины) показывает проверка проекта (<c>GAME_DIR_NOT_FOUND</c>).
    /// </summary>
    /// <param name="project">Проект.</param>
    /// <param name="globalGameDir">Каталог игры из шапки (настроек кокпита).</param>
    /// <returns>Каталог либо пустая строка.</returns>
    public static string EffectiveGameDirFor(ModProject project, string? globalGameDir)
    {
        ArgumentNullException.ThrowIfNull(project);

        return string.IsNullOrWhiteSpace(globalGameDir)
            ? project.GameDir?.Trim() ?? string.Empty
            : globalGameDir.Trim();
    }

    /// <summary>
    /// Проверка проекта в пуле потоков. <see cref="IProjectStore.Validate"/> ищет Python —
    /// то есть запускает процессы (<c>py -0p</c>, <c>reg query</c>) с таймаутом до 10 секунд.
    /// На UI-потоке это залипание окна при недоступном интерпретаторе, поэтому здесь та же
    /// схема, что в <see cref="DetectPythonAsync"/>.
    /// </summary>
    /// <param name="project">Проект, для которого считаем замечания.</param>
    private async Task ValidateProjectAsync(ModProject project)
    {
        IReadOnlyList<ProjectValidation> issues;
        string settingsPython = _settings.PythonExe ?? string.Empty;
        try
        {
            issues = await Task.Run(() =>
            {
                // Проверка смотрит на тот же Python, которым пойдёт сборка (проект → настройки → автопоиск).
                project.EffectivePythonExe = PythonLocator.ResolveForBuild(project.PythonExe, settingsPython) ?? string.Empty;
                return _projectStore.Validate(project);
            }).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusMessage = "проверка проекта: " + Short(ex);
            return;
        }

        // Пока считали, пользователь мог открыть другой проект — тогда результат протух.
        if (!ReferenceEquals(_project, project))
        {
            return;
        }

        ProjectIssues.Clear();
        foreach (ProjectValidation issue in issues)
        {
            ProjectIssues.Add(issue);
        }

        HasBlockingIssues = ProjectIssues.Any(i => i.Severity == ValidationSeverity.Error);
    }

    /// <summary>
    /// Префикс идентификатора, который мастер «Новый мод…» предлагает для нового мода
    /// (<c>modIdPrefix</c> в settings.json, по умолчанию <c>ru.local</c>).
    /// </summary>
    public string ModIdPrefix =>
        string.IsNullOrWhiteSpace(_settings.ModIdPrefix) ? CockpitSettings.DefaultModIdPrefix : _settings.ModIdPrefix;

    /// <summary>
    /// Шаблон мода по полному правилу <see cref="Core.Scaffold.TemplateLocator"/>: переменная
    /// <c>WOTCOCKPIT_TEMPLATE_DIR</c>, затем <c>templateDir</c> из настроек, затем соседние каталоги.
    /// Смотрит на диск — дёргать по делу (открытие мастера, окна перехода), а не из привязок.
    /// </summary>
    /// <returns>Каталог шаблона либо пустая строка.</returns>
    public string ResolveTemplateDir()
    {
        try
        {
            return Core.Scaffold.TemplateLocator.Resolve(_settings);
        }
        catch (Exception ex)
        {
            StatusMessage = "поиск шаблона мода: " + Short(ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// Запоминает шаблон, выбранный в мастере, в <c>templateDir</c> настроек — чтобы следующий
    /// запуск мастера и окно перехода на новую версию клиента нашли его без повторного выбора.
    /// Сохраняется через общий объект настроек, а не отдельной записью: иначе главный экран
    /// при следующем сохранении затёр бы выбор своей устаревшей копией.
    /// <para>
    /// Шаблон из <c>WOTCOCKPIT_TEMPLATE_DIR</c> не запоминается: переменная — переопределение на
    /// время запуска (мастер открывается с ним же), и записанный навсегда путь к временному
    /// шаблону пережил бы саму переменную — настройка главнее пробы соседей.
    /// </para>
    /// </summary>
    /// <param name="templateDir">Выбранный каталог шаблона.</param>
    public void RememberTemplateDir(string? templateDir)
    {
        if (string.IsNullOrWhiteSpace(templateDir))
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(templateDir.Trim());
            if (!File.Exists(Path.Combine(full, "build.py"))
                || string.Equals(full, _settings.TemplateDir, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    Path.TrimEndingDirectorySeparator(full),
                    Path.TrimEndingDirectorySeparator(Core.Scaffold.TemplateLocator.FromEnvironment()),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            _settings.TemplateDir = full;
            SaveSettings();
        }
        catch (Exception ex)
        {
            StatusMessage = "шаблон не запомнен: " + Short(ex);
        }
    }

    /// <summary>
    /// Если каталог декомпилированного клиента в настройках не задан, пробуем найти его рядом
    /// с проектом: в шаблоне он лежит как <c>&lt;ProjectDir&gt;\.scratch\wot-src</c>.
    /// Без него кадры клиента просто не будут разрешаться — это не ошибка.
    /// </summary>
    private void EnsureWotSrcDir()
    {
        if (!string.IsNullOrWhiteSpace(_settings.WotSrcDir) && Directory.Exists(_settings.WotSrcDir))
        {
            return;
        }

        if (_project is null)
        {
            return;
        }

        try
        {
            string candidate = Path.Combine(_project.ProjectDir, ".scratch", "wot-src");
            if (Directory.Exists(candidate))
            {
                _settings.WotSrcDir = candidate;
                SaveSettings();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = "поиск исходников клиента: " + Short(ex);
        }
    }

    /// <summary>
    /// Ищет Python 3.x для build.py. Поиск лезет в PATH, в <c>py -0p</c> и в реестр,
    /// поэтому уезжает в пул потоков: держать на нём открытие окна нельзя.
    /// </summary>
    private async Task DetectPythonAsync()
    {
        string configured = _settings.PythonExe ?? string.Empty;
        string shown = string.IsNullOrWhiteSpace(configured) ? "автопоиск" : configured;

        PythonInfo = "ищем python…";

        try
        {
            // То же правило, что у сборки: строка состояния не имеет права обещать один Python,
            // а сборка — запускать другой.
            PythonInstall? found = await Task.Run(() =>
            {
                string? command = PythonLocator.ResolveForBuild(null, configured);
                return command is null ? null : PythonLocator.Resolve(command);
            }).ConfigureAwait(true);

            PythonInfo = found is null
                ? $"python не найден ({shown}: PATH, py -0p, реестр) — сборка работать не будет"
                : $"python {found.Version} · {found.ExecutablePath}"
                  + (found.IsSupportedForBuild ? string.Empty : "  ⚠ нужен 3.10+");
        }
        catch (Exception ex)
        {
            PythonInfo = $"поиск python: {Short(ex)}";
        }
    }

    // =================================================================== мелочи

    private void SaveSettings()
    {
        if (_suspendSettingsSave)
        {
            return;
        }

        try
        {
            _settings.GameDir = GameDir;
            _settings.LastProjectDir = ProjectDir;
            _settings.LogOnlyMine = OnlyMine;

            // В «недавние» попадает только то, что реально ЗАГРУЗИЛОСЬ как проект. Иначе список
            // засоряется путями, по которым проекта нет: достаточно один раз запустить кокпит с
            // мусорным lastProjectDir в settings.json, чтобы этот мусор туда переехал навсегда.
            if (_project is not null && !string.IsNullOrWhiteSpace(ProjectDir))
            {
                _settings.RecentProjects.RemoveAll(
                    p => string.Equals(p, ProjectDir, StringComparison.OrdinalIgnoreCase));
                _settings.RecentProjects.Insert(0, ProjectDir);

                if (_settings.RecentProjects.Count > 10)
                {
                    _settings.RecentProjects.RemoveRange(10, _settings.RecentProjects.Count - 10);
                }
            }

            _settingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            StatusMessage = "настройки не сохранены: " + Short(ex);
        }
    }

    private static string? PickFolder(string title, string initial)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            dialog.InitialDirectory = initial;
        }

        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private T Try<T>(Func<T> action, T fallback, string what)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            StatusMessage = what + ": " + Short(ex);
            return fallback;
        }
    }

    private void TryDo(Action action, string what)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            StatusMessage = what + ": " + Short(ex);
        }
    }

    /// <summary>
    /// Короткое человеческое описание сбоя. Незаконченный сервис Core — обычное дело
    /// на этой стадии, и выглядеть оно должно как пометка, а не как авария.
    /// </summary>
    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
