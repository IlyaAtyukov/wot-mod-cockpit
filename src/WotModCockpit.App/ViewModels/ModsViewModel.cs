using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Collections;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Project;

namespace WotModCockpit.App.ViewModels;

/// <summary>Какой набор строк показывает список.</summary>
public enum ModsFilter
{
    /// <summary>Все моды, включая выключенные.</summary>
    All,

    /// <summary>Только моды из проектов кокпита.</summary>
    Mine,

    /// <summary>Только чужие.</summary>
    Foreign,

    /// <summary>Только выключенные (<c>.mtmod.off</c>).</summary>
    Disabled
}

/// <summary>
/// Экран «Моды»: что установлено в игре, что из этого своё, что выключено — и что с этим
/// можно сделать.
/// <para>
/// Три вещи определяют устройство экрана, и все три про безопасность, а не про удобство.
/// </para>
/// <list type="number">
/// <item>
/// В каталоге игры лежат ЧУЖИЕ моды, которые пользователь ставил сам. Поэтому здесь нет ни
/// галочек, ни множественного выделения, ни единой массовой операции: действие бьёт ровно по
/// выбранной строке, кнопки живут в панели деталей рядом с полным путём, а диалог
/// подтверждения показывает путь целиком — и исходный, и итоговый. Галочки есть только в
/// отдельном диалоге «Собрать сетап»: там в списке одни свои проекты, а итог — файл вне игры.
/// </item>
/// <item>
/// Моды читаются один раз при старте клиента, поэтому при запущенной игре запись запрещена.
/// Опрос идёт таймером, пока экран открыт, и ещё раз непосредственно перед каждой операцией;
/// «выяснить не удалось» трактуется как «запущена».
/// </item>
/// <item>
/// Выключение обязано быть обратимым: <c>X.mtmod</c> → <c>X.mtmod.off</c>, файл остаётся на
/// месте. Всю работу с диском делает <see cref="IModLibraryService"/> — здесь только показ,
/// подтверждения и журнал.
/// </item>
/// </list>
/// <para>
/// При старте приложения экран не делает НИЧЕГО: каталог игры читается, когда на экран
/// переключились в первый раз.
/// </para>
/// </summary>
public sealed partial class ModsViewModel : ObservableObject, IDisposable
{
    private const int GameWatchIntervalSeconds = 2;
    private const int JournalCapacity = 200;

    /// <summary>Верхняя граница на число каталогов проектов, которые уходят в обход.</summary>
    private const int MaxProjectDirs = 64;

    /// <summary>
    /// Пауза после последнего изменения в каталоге модов перед перечитыванием: копирование
    /// архива — это пачка событий, и читать каталог на каждое незачем.
    /// </summary>
    private const int ChangeDebounceMs = 400;

    private readonly MainViewModel _main;
    private readonly IModLibraryService _library;
    private readonly IGameInstallLocator _locator;
    private readonly ISettingsStore _settings;
    private readonly IEditorLauncher _editor;
    private readonly DispatcherTimer _gameWatch;
    private readonly DispatcherTimer _changeDebounce;

    private readonly List<InstalledModViewModel> _all = new();
    private CancellationTokenSource? _cts;
    private GameInstall? _game;
    private bool _loadedOnce;
    private bool _disposed;

    /// <summary>Экран показан: между <see cref="EnsureLoadedAsync"/> и <see cref="Suspend"/>.</summary>
    private bool _visible;

    /// <summary>Номер последнего начатого чтения: только оно снимает <see cref="IsScanning"/>.</summary>
    private int _refreshGeneration;

    /// <summary>
    /// Сколько команд экрана сейчас идёт — ВМЕСТЕ с их диалогами подтверждения. Пока их
    /// больше нуля, наблюдатель за каталогом список не перечитывает: команда перечитает его
    /// сама, а перечитывание под открытым диалогом подменило бы строки, о которых пользователя
    /// сейчас спрашивают.
    /// </summary>
    private int _commandsInFlight;

    /// <summary>
    /// Файл, строку которого выделить после ближайшего перечитывания, — итог операции
    /// (включённый, выключенный, установленный файл). Выбирает её сама перестройка списка
    /// (<see cref="Rebuild"/>), а не код после неё: так скрытая фильтром строка не
    /// выделяется, а чтение, вытеснившее чтение операции, подхватит этот путь само.
    /// </summary>
    private string? _preferSelectPath;

    private FileSystemWatcher? _watcher;
    private string _watchedDir = string.Empty;

    /// <summary>Создаёт модель экрана.</summary>
    /// <param name="main">Главная модель: от неё берутся каталог игры и открытый проект.</param>
    /// <param name="library">Библиотека модов — единственное, что здесь ходит на диск.</param>
    /// <param name="locator">Разбор <c>paths.xml</c>: из него берётся каталог модов активной версии.</param>
    /// <param name="settings">Глобальные настройки: список проектов, по которым опознаётся «своё».</param>
    /// <param name="editor">Запуск редактора для кнопки «открыть проект».</param>
    public ModsViewModel(
        MainViewModel main,
        IModLibraryService library,
        IGameInstallLocator locator,
        ISettingsStore settings,
        IEditorLauncher editor)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        _gameWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(GameWatchIntervalSeconds)
        };
        _gameWatch.Tick += (_, _) => RefreshGameState();

        _changeDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ChangeDebounceMs)
        };
        _changeDebounce.Tick += OnChangeDebounceTick;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.AttachMods(this);
    }

    // =================================================================== состояние экрана

    /// <summary>Видимые строки — к ним привязан виртуализованный список.</summary>
    public BulkObservableCollection<InstalledModViewModel> Mods { get; } = new();

    /// <summary>Журнал операций, свежие сверху.</summary>
    public ObservableCollection<string> Journal { get; } = new();

    /// <summary>Выбранная строка. Все действия экрана бьют ровно по ней.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealCommand))]
    [NotifyCanExecuteChangedFor(nameof(OpenProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFromDistCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildAndInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    [NotifyCanExecuteChangedFor(nameof(EditDescriptionCommand))]
    private InstalledModViewModel? _selected;

    /// <summary>Подстрока поиска: имя, id, имя файла, подпапка.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Какой набор строк показывать.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFilterAll))]
    [NotifyPropertyChangedFor(nameof(IsFilterMine))]
    [NotifyPropertyChangedFor(nameof(IsFilterForeign))]
    [NotifyPropertyChangedFor(nameof(IsFilterDisabled))]
    private ModsFilter _filter = ModsFilter.All;

    /// <summary>
    /// Показаны все моды. У каждого чипа свой флаг, потому что чипы взаимоисключающие:
    /// так разметке не нужен конвертер «перечисление в галочку».
    /// </summary>
    public bool IsFilterAll
    {
        get => Filter == ModsFilter.All;
        set => SetFilterFlag(value, ModsFilter.All);
    }

    /// <summary>Показаны только свои моды.</summary>
    public bool IsFilterMine
    {
        get => Filter == ModsFilter.Mine;
        set => SetFilterFlag(value, ModsFilter.Mine);
    }

    /// <summary>Показаны только чужие моды.</summary>
    public bool IsFilterForeign
    {
        get => Filter == ModsFilter.Foreign;
        set => SetFilterFlag(value, ModsFilter.Foreign);
    }

    /// <summary>Показаны только выключенные моды.</summary>
    public bool IsFilterDisabled
    {
        get => Filter == ModsFilter.Disabled;
        set => SetFilterFlag(value, ModsFilter.Disabled);
    }

    private void SetFilterFlag(bool on, ModsFilter value)
    {
        if (on)
        {
            Filter = value;
            return;
        }

        // Снятый чип не должен оставлять список без единого фильтра: молча возвращаем «все».
        if (Filter == value)
        {
            Filter = ModsFilter.All;
        }
    }

    /// <summary>Показан журнал операций.</summary>
    [ObservableProperty]
    private bool _showJournal;

    /// <summary>Идёт чтение каталога.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isScanning;

    /// <summary>Каталог модов активной версии клиента либо пустая строка.</summary>
    [ObservableProperty]
    private string _modsDir = string.Empty;

    /// <summary>Сводка над списком: сколько всего, сколько своих, сколько выключено.</summary>
    [ObservableProperty]
    private string _summary = "каталог модов ещё не читали";

    /// <summary>Что показать вместо списка, когда он пуст.</summary>
    [ObservableProperty]
    private string _emptyStateText = "Каталог игры будет прочитан при первом показе экрана.";

    /// <summary>Строка состояния внизу экрана.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Нефатальные сложности обхода: недоступный подкаталог, битый архив и подобное.</summary>
    [ObservableProperty]
    private string _scanProblems = string.Empty;

    /// <summary>
    /// Запись запрещена: игра запущена либо выяснить это не удалось. Неизвестность всегда
    /// трактуется в пользу запрета — ошибиться тут можно ровно один раз.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanWrite))]
    [NotifyPropertyChangedFor(nameof(WriteLockReason))]
    [NotifyCanExecuteChangedFor(nameof(EnableCommand))]
    [NotifyCanExecuteChangedFor(nameof(DisableCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallFromDistCommand))]
    [NotifyCanExecuteChangedFor(nameof(BuildAndInstallCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private bool _gameBlocksWrites;

    /// <summary>Что именно сказала проба процессов.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteLockReason))]
    private string _gameStateMessage = string.Empty;

    /// <summary>Операции записи сейчас разрешены.</summary>
    public bool CanWrite => !GameBlocksWrites && _game is not null;

    /// <summary>Почему запись запрещена. Пустая строка — разрешена.</summary>
    public string WriteLockReason
    {
        get
        {
            if (_game is null)
            {
                return "Каталог игры не определён — изменения недоступны. Укажите папку игры на экране «Сборка и лог».";
            }

            return GameBlocksWrites
                ? GameStateMessage + " Моды читаются один раз при старте клиента, поэтому изменения заблокированы."
                : string.Empty;
        }
    }

    /// <summary>Путь к журналу операций — тот же файл, в который пишет служба.</summary>
    public string OperationLogPath
    {
        get
        {
            try
            {
                return _library.OperationLogPath;
            }
            catch (Exception)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// Спросить подтверждение. Подменяется в тестах; по умолчанию — модальный диалог.
    /// Аргументы: заголовок, тело.
    /// </summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    /// <summary>
    /// Второй шаг подтверждения удаления: человек обязан напечатать имя файла.
    /// Аргументы: заголовок, тело, ожидаемая строка; возврат — напечатанное либо <c>null</c>.
    /// </summary>
    public Func<string, string, string, string?> TypedConfirmHandler { get; set; } = DefaultTypedConfirm;

    /// <summary>
    /// Показать диалог «Имя и описание мода». Подменяется в тестах; по умолчанию — модальное окно.
    /// Возврат: <c>true</c> — нажали «Сохранить», значения лежат в самой модели диалога.
    /// </summary>
    public Func<MetaEditViewModel, bool> EditMetaHandler { get; set; } = DefaultEditMeta;

    /// <summary>
    /// Показать диалог «Собрать сетап». Подменяется в тестах; по умолчанию — модальное окно.
    /// Всё, что диалог сделал (готовый файл, выбор для настроек), остаётся в его модели.
    /// </summary>
    public Action<SetupBundleViewModel> SetupBundleHandler { get; set; } = DefaultSetupBundle;

    /// <summary>Где взять заглушку установщика. Подменяется в тестах.</summary>
    public Func<string?> FindSetupStub { get; set; } = () => SetupStubLocator.Find();

    // =================================================================== жизненный цикл

    /// <summary>
    /// Экран показан: каталог читается. Зовётся при КАЖДОМ переключении на экран — при старте
    /// приложения не делается ничего, даже чтения.
    /// <para>
    /// <b>Почему при каждом показе, а не только при первом.</b> Каталог модов меняют не только
    /// команды этого экрана: «Собрать и установить» и «Удалить из игры» на экране «Сборка и
    /// лог» (это build.py, мимо службы библиотеки), проводник, терминал. Раньше экран читал
    /// каталог один раз и дальше показывал снимок: мод, удалённый кнопкой «Удалить из игры»,
    /// оставался в списке «ВКЛЮЧЁН» — ровно так пользователь и нашёл эту ошибку. Чтение только
    /// читающее и занимает миллисекунды; выделение переживает его (см. <see cref="Rebuild"/>).
    /// </para>
    /// </summary>
    /// <returns>Задача.</returns>
    public async Task EnsureLoadedAsync()
    {
        if (_disposed)
        {
            return;
        }

        _visible = true;
        RefreshGameState();
        _gameWatch.Start();
        SetWatcherEnabled(true);

        bool first = !_loadedOnce;
        if (first)
        {
            _loadedOnce = true;
            LoadJournalFromDisk();
        }

        // Повторный показ читает тихо: строка состояния остаётся за итогом последнего
        // действия пользователя, а не за «прочитано N файлов».
        await RefreshCoreAsync(quiet: !first).ConfigureAwait(true);
    }

    /// <summary>Экран скрыт: опрос процессов игры и наблюдение за каталогом больше не нужны.</summary>
    public void Suspend()
    {
        _visible = false;
        _gameWatch.Stop();
        _changeDebounce.Stop();
        SetWatcherEnabled(false);
    }

    /// <summary>
    /// Каталог модов или сборка проекта могли измениться в обход этого экрана — например,
    /// экран «Сборка и лог» только что отработал build.py. Показанный экран перечитает список
    /// (с паузой, как на события наблюдателя); скрытый перечитает его при следующем показе.
    /// Ни разу не открытый экран не делает ничего: при старте приложения каталог не читается.
    /// </summary>
    public void NotifyExternalChange()
    {
        if (_loadedOnce && _visible && !_disposed)
        {
            ScheduleRefresh();
        }
    }

    // =================================================================== наблюдение за каталогом

    /// <summary>
    /// Следит за каталогом модов, пока экран показан: удаление или копирование файла мимо
    /// кокпита (проводник, терминал, build.py) видно в списке сразу, без кнопки «обновить».
    /// Наблюдатель — только подсказка «пора перечитать»: сам список всегда строится полным
    /// чтением каталога, а не по событиям.
    /// </summary>
    private void WatchModsDir(string modsDir)
    {
        if (_watcher is not null && string.Equals(_watchedDir, modsDir, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        DisposeWatcher();
        _watchedDir = modsDir;
        if (modsDir.Length == 0 || !SafeDirectoryExists(modsDir))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(modsDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                               | NotifyFilters.LastWrite | NotifyFilters.Size
            };
            watcher.Created += OnModsDirChanged;
            watcher.Deleted += OnModsDirChanged;
            watcher.Changed += OnModsDirChanged;
            watcher.Renamed += OnModsDirChanged;
            watcher.Error += OnModsDirWatcherError;
            watcher.EnableRaisingEvents = _visible;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            // Без наблюдателя экран всё равно перечитывает каталог при каждом показе.
            _watcher = null;
        }
    }

    private void SetWatcherEnabled(bool enabled)
    {
        if (_watcher is null)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = enabled;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or FileNotFoundException)
        {
            // Каталог пропал (сменилась версия игры) — наблюдатель пересоздаст следующее чтение.
            DisposeWatcher();
        }
    }

    private void DisposeWatcher()
    {
        if (_watcher is null)
        {
            return;
        }

        _watcher.EnableRaisingEvents = false;
        _watcher.Created -= OnModsDirChanged;
        _watcher.Deleted -= OnModsDirChanged;
        _watcher.Changed -= OnModsDirChanged;
        _watcher.Renamed -= OnModsDirChanged;
        _watcher.Error -= OnModsDirWatcherError;
        _watcher.Dispose();
        _watcher = null;
        _watchedDir = string.Empty;
    }

    /// <summary>События наблюдателя приходят в пуле потоков — список живёт в потоке интерфейса.</summary>
    private void OnModsDirChanged(object sender, FileSystemEventArgs e) =>
        _ = _changeDebounce.Dispatcher.InvokeAsync(ScheduleRefresh, DispatcherPriority.Background);

    /// <summary>
    /// Ошибка наблюдателя: переполнение буфера или пропавший каталог. Что изменилось,
    /// неизвестно — значит, перечитать; а сам наблюдатель после такой ошибки может больше не
    /// сообщать ничего, поэтому он выбрасывается и перечитывание создаёт новый.
    /// </summary>
    private void OnModsDirWatcherError(object sender, ErrorEventArgs e) =>
        _ = _changeDebounce.Dispatcher.InvokeAsync(
            () =>
            {
                if (ReferenceEquals(sender, _watcher))
                {
                    DisposeWatcher();
                }

                ScheduleRefresh();
            },
            DispatcherPriority.Background);

    private void ScheduleRefresh()
    {
        if (_disposed || !_visible)
        {
            return;
        }

        // Каждое новое событие отодвигает перечитывание: читаем, когда каталог затих.
        _changeDebounce.Stop();
        _changeDebounce.Start();
    }

    private void OnChangeDebounceTick(object? sender, EventArgs e)
    {
        _changeDebounce.Stop();
        if (_disposed || !_visible)
        {
            return;
        }

        // Идёт чтение или команда экрана (она перечитает каталог сама, а под её диалогом
        // перечитывание подменило бы строку, о которой спрашивают): пробуем позже.
        if (IsScanning || _commandsInFlight > 0)
        {
            _changeDebounce.Start();
            return;
        }

        // Тихо: строка состояния остаётся за тем, что сделал пользователь, — фоновое чтение
        // не должно стирать итог его последней операции.
        _ = RefreshCoreAsync(quiet: true);
    }

    /// <summary>
    /// Держит наблюдатель на паузе, пока идёт команда: <c>using var hold = HoldBackgroundRefresh();</c>
    /// в начале команды, до первого диалога.
    /// </summary>
    private CommandHold HoldBackgroundRefresh()
    {
        _commandsInFlight++;
        return new CommandHold(this);
    }

    private sealed class CommandHold(ModsViewModel owner) : IDisposable
    {
        private bool _released;

        public void Dispose()
        {
            if (!_released)
            {
                _released = true;
                owner._commandsInFlight--;
            }
        }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsBusy))
        {
            // build.py один на оба экрана: пока он занят, «Собрать и установить» гаснет.
            BuildAndInstallCommand.NotifyCanExecuteChanged();
            return;
        }

        if (e.PropertyName != nameof(MainViewModel.GameDir) && e.PropertyName != nameof(MainViewModel.ProjectDir))
        {
            return;
        }

        if (_loadedOnce)
        {
            // Сменили игру или проект — список устарел. Перечитать можно смело: это только чтение.
            _ = RefreshAsync();
        }
    }

    private void RefreshGameState()
    {
        GameRunningState state;
        try
        {
            state = _library.CheckGame();
        }
        catch (Exception ex)
        {
            // Не смогли спросить — считаем, что игра идёт. Запрет дешевле испорченного каталога.
            state = new GameRunningState(GameRunningStatus.Unknown, null, null, "состояние игры выяснить не удалось: " + Short(ex));
        }

        GameStateMessage = state.Message;
        GameBlocksWrites = state.BlocksWrites;
    }

    // =================================================================== чтение каталога

    private bool CanRefresh() => !IsScanning;

    /// <summary>Перечитать каталог модов. Операция только читающая, подтверждения не требует.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => RefreshCoreAsync(quiet: false);

    /// <summary>
    /// Чтение каталога и перестройка списка.
    /// </summary>
    /// <param name="quiet">
    /// Не трогать строку состояния, если всё прочиталось: так читают наблюдатель за каталогом
    /// и отказ «файл исчез», чтобы не стереть то, что пользователю уже сказано. Ошибка чтения
    /// показывается всегда.
    /// </param>
    private async Task RefreshCoreAsync(bool quiet)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        int generation = ++_refreshGeneration;

        IsScanning = true;
        if (!quiet)
        {
            StatusText = "чтение каталога модов…";
        }

        try
        {
            _game = DetectGame();
            ModsDir = _game?.ModsDir ?? string.Empty;
            WatchModsDir(ModsDir);
            RefreshGameState();
            OnPropertyChanged(nameof(CanWrite));
            OnPropertyChanged(nameof(WriteLockReason));

            if (_game is null)
            {
                ForgetMigration();
                ResetTo("Игра не выбрана.\n\nУкажите папку игры на экране «Сборка и лог» — кокпит разберёт paths.xml и найдёт каталог модов активной версии.");
                StatusText = "игра не выбрана";
                return;
            }

            GameInstall game = _game;

            // Сбор каталогов проектов ходит на диск (перебор соседних папок), поэтому он
            // выполняется в том же фоновом вызове, что и сам обход.
            ModLibraryScan scan = await Task.Run(() => _library.Scan(game, CollectProjectDirs()), ct)
                .ConfigureAwait(true);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            InstalledModViewModel? keep = Rebuild(scan);
            ApplyFilter();
            RestoreSelection(keep);

            // Итог операции выделен (или скрыт фильтром) — дальше выделение обычное.
            _preferSelectPath = null;

            // Не обновился ли клиент: свои моды в каталоге прошлой версии. В фоне, молча.
            _ = DetectMigrationAsync(game);

            if (!quiet)
            {
                StatusText = $"прочитано {scan.Mods.Count} {Plural(scan.Mods.Count, "файл", "файла", "файлов")}"
                             + $" за {scan.Elapsed.TotalMilliseconds:F0} мс · {game.ModsDir}";
            }

            await LoadIconsAsync(ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Обычное дело: список перечитали, не дождавшись предыдущего чтения.
        }
        catch (NotImplementedException)
        {
            ResetTo("Служба библиотеки модов ещё не реализована — показывать нечего.");
            StatusText = "служба библиотеки модов не реализована";
        }
        catch (Exception ex)
        {
            // Вытесненное чтение молчит: список уже строит более новое, и «ошибка» старого
            // стёрла бы его.
            if (ct.IsCancellationRequested)
            {
                return;
            }

            ResetTo("Каталог модов прочитать не удалось:\n\n" + Short(ex));
            StatusText = "ошибка чтения: " + Short(ex);
        }
        finally
        {
            // Флаг снимает только последнее начатое чтение: вытесненное заканчивается раньше,
            // и «читаю каталог…» пропало бы, пока новое ещё читает.
            if (generation == _refreshGeneration)
            {
                IsScanning = false;
            }
        }
    }

    private GameInstall? DetectGame()
    {
        string dir = _main.GameDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        try
        {
            return _locator.Detect(dir);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private IReadOnlyList<string> CollectProjectDirs()
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? dir)
        {
            if (!string.IsNullOrWhiteSpace(dir) && seen.Add(dir))
            {
                dirs.Add(dir);
            }
        }

        Add(_main.CurrentProject?.ProjectDir);

        try
        {
            CockpitSettings s = _settings.Load();
            Add(s.LastProjectDir);
            foreach (string dir in s.RecentProjects)
            {
                Add(dir);
            }
        }
        catch (Exception)
        {
            // Настроек нет — остаётся только открытый проект.
        }

        AddSiblingProjects(dirs, seen);
        return dirs;
    }

    /// <summary>
    /// Добавляет соседей известных проектов: каталоги рядом, в которых есть <c>build.py</c>
    /// и <c>src\</c>.
    /// <para>
    /// Без этого «моим» считался бы только тот мод, чей проект уже открывали в кокпите, —
    /// а установленный в игру <c>ru.example.my_mod</c> выглядел бы чужим, пока пользователь
    /// не откроет его проект. Перебор дешёвый (одно перечисление каталога и две проверки
    /// на кандидата), только читающий, ограничен сверху и ничего не запоминает: список
    /// недавних проектов от этого не меняется.
    /// </para>
    /// </summary>
    private static void AddSiblingProjects(List<string> dirs, HashSet<string> seen)
    {
        List<string> parents = dirs
            .Select(SafeParent)
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string parent in parents)
        {
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateDirectories(parent);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (string child in children)
            {
                if (dirs.Count >= MaxProjectDirs)
                {
                    return;
                }

                if (!seen.Contains(child) && LooksLikeModProject(child))
                {
                    seen.Add(child);
                    dirs.Add(child);
                }
            }
        }
    }

    private static string SafeParent(string dir)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(dir)) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Каталог похож на проект мода. Достаточно <c>build.py</c> и каталога <c>src</c>:
    /// <c>meta.xml</c> требовать нельзя — у только что начатого проекта его может ещё не быть,
    /// и именно это стоит показать в списке отдельной строкой.
    /// </summary>
    private static bool LooksLikeModProject(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "build.py")) && Directory.Exists(Path.Combine(dir, "src"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ResetTo(string emptyText)
    {
        _preferSelectPath = null;
        _all.Clear();
        Mods.ReplaceAll(Array.Empty<InstalledModViewModel>());
        Selected = null;
        EmptyStateText = emptyText;
        ScanProblems = string.Empty;
        Summary = "модов не найдено";
    }

    /// <summary>
    /// Перестраивает строки по свежему чтению.
    /// </summary>
    /// <returns>
    /// Строка, которую выделить вместо прежней, либо <c>null</c>. Выделяет её не этот метод, а
    /// <see cref="RestoreSelection"/> после <see cref="ApplyFilter"/> — когда уже известно,
    /// видна ли она в списке.
    /// </returns>
    private InstalledModViewModel? Rebuild(ModLibraryScan scan)
    {
        string? keepPath = _preferSelectPath is { Length: > 0 } preferred ? preferred : Selected?.FullPath;

        // Иконки прежних строк переходят в новые с тем же источником сразу: иначе каждое
        // перечитывание (а их теперь много — показ экрана, наблюдатель) на миг превращало бы
        // иконки в плитки с буквой. Настоящая иконка всё равно перечитывается следом
        // (LoadIconsAsync) и заменит эту, если файл изменился.
        Dictionary<string, ImageSource> icons = _all
            .Where(r => r.Icon is not null && r.HasIconSource)
            .GroupBy(r => r.IconSourcePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Icon!, StringComparer.OrdinalIgnoreCase);

        // У строки «не установлен» пути нет, и по одному пути выделение с неё слетало при
        // каждом перечитывании. Для неё — и для своего мода, чей файл исчез (удалили здесь,
        // кнопкой «Удалить из игры» или проводником), — запоминается проект: выделится его
        // строка, и карточка сразу скажет «в игре не установлен».
        string? keepProject = Selected is { ProjectDir.Length: > 0 } mineRow ? mineRow.ProjectDir : null;

        _all.Clear();

        // Проект нужен и установленной строке своего мода: без него карточка не скажет, что
        // имя или описание в src\meta.xml уже не такие, как в архиве в игре.
        var projects = new Dictionary<string, ProjectModStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectModStatus status in scan.Projects)
        {
            projects.TryAdd(TrimDir(status.Project.ProjectDir), status);
        }

        foreach (InstalledMod mod in scan.Mods)
        {
            ProjectModStatus? project = mod.Mine is { ProjectDir.Length: > 0 } mine
                && projects.TryGetValue(TrimDir(mine.ProjectDir), out ProjectModStatus? found)
                    ? found
                    : null;
            _all.Add(new InstalledModViewModel(mod, project));
        }

        // Проекты, которых в игре нет, тоже должны быть видны: «Shot Timer — не установлен»
        // это ответ на вопрос пользователя, а пустое место в списке — нет.
        foreach (ProjectModStatus project in scan.Projects)
        {
            if (!project.IsInstalled)
            {
                _all.Add(InstalledModViewModel.NotInstalled(project));
            }
        }

        _all.Sort(CompareRows);

        foreach (InstalledModViewModel row in _all)
        {
            if (row.HasIconSource && icons.TryGetValue(row.IconSourcePath, out ImageSource? icon))
            {
                row.Icon = icon;
            }
        }

        Summary = $"{scan.Mods.Count} {Plural(scan.Mods.Count, "мод", "мода", "модов")}"
                  + $" · {scan.MineCount} {Plural(scan.MineCount, "свой", "своих", "своих")}"
                  + $" · {scan.DisabledCount} выключено";

        EmptyStateText = scan.Mods.Count == 0
            ? "В каталоге модов нет ни одного .mtmod:\n\n" + scan.ModsDir
            : "Ни один мод не подходит под фильтр.";

        ScanProblems = scan.Errors.Count == 0
            ? string.Empty
            : string.Join("\n", scan.Errors.Take(5))
              + (scan.Errors.Count > 5 ? $"\n… ещё {scan.Errors.Count - 5}" : string.Empty);

        InstalledModViewModel? keep = keepPath is { Length: > 0 }
            ? _all.FirstOrDefault(r => string.Equals(r.FullPath, keepPath, StringComparison.OrdinalIgnoreCase))
            : null;

        if (keep is null && keepProject is { Length: > 0 })
        {
            // Строка проекта «не установлен», а если проект в игре есть в другом виде
            // (осталась выключенная копия) — эта копия.
            keep = _all.FirstOrDefault(r => !r.IsInstalled && SameDir(r.ProjectDir, keepProject))
                   ?? _all.FirstOrDefault(r => SameDir(r.ProjectDir, keepProject));
        }

        return keep;
    }

    /// <summary>
    /// Выделяет строку, выбранную <see cref="Rebuild"/>, — когда она уже есть в видимом
    /// списке. Скрытую фильтром не выделяет: карточка показывала бы то, чего в списке нет.
    /// </summary>
    private void RestoreSelection(InstalledModViewModel? keep)
    {
        if (keep is not null && Mods.Contains(keep))
        {
            Selected = keep;
        }
    }

    private static string TrimDir(string dir) => Path.TrimEndingDirectorySeparator(dir ?? string.Empty);

    private static int CompareRows(InstalledModViewModel a, InstalledModViewModel b)
    {
        int g = a.GroupSort.CompareTo(b.GroupSort);
        if (g != 0)
        {
            return g;
        }

        int k = string.Compare(a.GroupKey, b.GroupKey, StringComparison.CurrentCultureIgnoreCase);
        return k != 0 ? k : string.Compare(a.Title, b.Title, StringComparison.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// Достаёт иконки по одной, уступая поток интерфейсу между строками: PNG лежит одной
    /// записью внутри архива, но архивов сотня, и список обязан оставаться отзывчивым.
    /// </summary>
    private async Task LoadIconsAsync(CancellationToken ct)
    {
        // Перебор КОПИИ: пока иконка читается в фоне, новое чтение каталога может перестроить
        // _all, и перебор живого списка упал бы «Collection was modified» — а это исключение
        // ловится как ошибка чтения и стирает только что построенный список.
        foreach (InstalledModViewModel row in _all.ToArray())
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (!row.HasIconSource)
            {
                continue;
            }

            // Для установленного мода это его файл в игре; для своего, у которого иконки в игре
            // ещё нет, — собранный артефакт из dist\ (см. InstalledModViewModel.IconSourcePath).
            string path = row.IconSourcePath;
            string? id = row.ModId.Length > 0 ? row.ModId : null;

            ModIcon icon;
            try
            {
                icon = await Task.Run(() => _library.ReadIcon(path, id), ct).ConfigureAwait(true);
            }
            catch (Exception)
            {
                continue;
            }

            if (icon.HasBytes)
            {
                row.SetIcon(icon.Bytes!);
            }
            else
            {
                // Иконка, перенесённая из прежней строки, больше не соответствует файлу.
                row.Icon = null;
            }
        }
    }

    // =================================================================== фильтр и поиск

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterChanged(ModsFilter value) => ApplyFilter();

    private void ApplyFilter()
    {
        string q = SearchText.Trim();

        IEnumerable<InstalledModViewModel> shown = _all.Where(r => Filter switch
        {
            ModsFilter.Mine => r.IsMine,
            ModsFilter.Foreign => !r.IsMine && r.IsInstalled,
            ModsFilter.Disabled => r.IsInstalled && !r.IsEnabled,
            _ => true
        });

        if (q.Length > 0)
        {
            shown = shown.Where(r => r.Matches(q));
        }

        List<InstalledModViewModel> list = shown.ToList();
        Mods.ReplaceAll(list);

        if (Selected is not null && !list.Contains(Selected))
        {
            Selected = null;
        }
    }

    // =================================================================== действия

    private bool CanEnable() => Selected is { IsInstalled: true, IsEnabled: false } && CanWrite;

    /// <summary>Включить мод: <c>.mtmod.off</c> → <c>.mtmod</c>.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanEnable))]
    private async Task EnableAsync()
    {
        // Пока идут диалоги и сама операция, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;
        if (row?.Mod is null || !EnsureWritable(row))
        {
            return;
        }

        string target = ModFileNames.ToEnabledPath(row.FullPath) ?? row.FullPath;
        string body = $"Включить мод «{row.Title}»?\n\n"
                      + "Файл будет переименован:\n"
                      + row.FullPath + "\n→\n" + target
                      + "\n\nМод начнёт грузиться при следующем запуске игры.";

        if (!ConfirmHandler("Включить мод", body))
        {
            return;
        }

        GameInstall game = _game!;
        InstalledMod mod = row.Mod;
        await RunOperationAsync(row, ModOperation.Enable, () => _library.Enable(game, mod.FullPath, ModWriteExpectation.From(mod)))
            .ConfigureAwait(true);
    }

    private bool CanDisable() => Selected is { IsInstalled: true, IsEnabled: true } && CanWrite;

    /// <summary>Выключить мод: <c>.mtmod</c> → <c>.mtmod.off</c>. Файл остаётся на месте.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanDisable))]
    private async Task DisableAsync()
    {
        // Пока идут диалоги и сама операция, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;
        if (row?.Mod is null || !EnsureWritable(row))
        {
            return;
        }

        string target = ModFileNames.ToDisabledPath(row.FullPath) ?? row.FullPath + ModFileNames.DisabledSuffix;

        var body = new StringBuilder();
        body.Append("Выключить мод «").Append(row.Title).Append("»?\n\n");
        body.Append("Файл будет переименован:\n").Append(row.FullPath).Append("\n→\n").Append(target).Append("\n\n");
        body.Append("Файл не удаляется и остаётся на месте — включить обратно можно тут же.\n");

        if (!row.IsMine)
        {
            body.Append("\nЭТО ЧУЖОЙ МОД. Вы ставили его сами, и кокпит не знает, что сломается без него.\n");
        }

        if (row.HasNativeCode)
        {
            body.Append("\nМод несёт нативные модули (.pyd/.dll) — от него могут зависеть другие моды сборки.\n");
        }

        string caption = row.IsMine ? "Выключить мод" : "Выключить ЧУЖОЙ мод";
        if (!ConfirmHandler(caption, body.ToString()))
        {
            return;
        }

        GameInstall game = _game!;
        InstalledMod mod = row.Mod;
        await RunOperationAsync(row, ModOperation.Disable, () => _library.Disable(game, mod.FullPath, ModWriteExpectation.From(mod)))
            .ConfigureAwait(true);
    }

    private bool CanReveal() => Selected is { IsInstalled: true };

    /// <summary>Показать файл в проводнике. Операция чтения, подтверждения не требует.</summary>
    [RelayCommand(CanExecute = nameof(CanReveal))]
    private void Reveal()
    {
        string? path = Selected?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = "проводник не открылся: " + Short(ex);
        }
    }

    private bool CanOpenProject() => Selected is { ProjectDir.Length: > 0 };

    /// <summary>Открыть каталог проекта в редакторе.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenProject))]
    private void OpenProject()
    {
        string? dir = Selected?.ProjectDir;
        if (string.IsNullOrEmpty(dir))
        {
            return;
        }

        try
        {
            _editor.OpenFolder(dir);
        }
        catch (Exception ex)
        {
            StatusText = "редактор не запустился: " + Short(ex);
        }
    }

    // =================================================================== имя и описание

    /// <summary>
    /// Имя и описание правятся только у СВОЕГО мода.
    /// <para>
    /// Меняется <c>src\meta.xml</c> проекта, и больше ничего. У чужого мода проекта нет, а
    /// переписывать архив в каталоге игры экран «Моды» не умеет и не должен: игра меняется
    /// только сборкой и установкой. Запрет на запись при запущенной игре здесь не нужен —
    /// каталог проекта игра не читает.
    /// </para>
    /// <para>
    /// Пункт гаснет и тогда, когда <c>src\meta.xml</c> проекта нет или он не читается: окно
    /// открылось бы только затем, чтобы сразу сказать «не открыть». Если проект строке
    /// неизвестен (не попал в перечитывание), пункт доступен — ответ даст сам файл.
    /// </para>
    /// </summary>
    private bool CanEditMeta() =>
        Selected is { ProjectDir.Length: > 0 } row
        && row.ProjectInfo is null or { HasMeta: true, MetaError: null };

    /// <summary>Переименовать мод: курсор сразу в поле имени.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanEditMeta))]
    private Task RenameAsync() => EditMetaAsync(MetaEditFocus.Name);

    /// <summary>Изменить описание мода: курсор сразу в поле описания.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanEditMeta))]
    private Task EditDescriptionAsync() => EditMetaAsync(MetaEditFocus.Description);

    /// <summary>
    /// Общий путь обеих команд: прочитать свежий <c>src\meta.xml</c>, показать диалог,
    /// записать ровно имя и описание, перечитать список.
    /// <para>
    /// Значения для диалога берутся из ФАЙЛА ПРОЕКТА, а не из строки списка: строка показывает
    /// то, что стоит в игре, а правится проект. После повторной правки до пересборки они
    /// различаются, и начинать надо с того, что уже в проекте.
    /// </para>
    /// </summary>
    private async Task EditMetaAsync(MetaEditFocus focus)
    {
        // Пока идут диалоги и сама операция, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;
        string projectDir = row?.ProjectDir ?? string.Empty;
        if (row is null || projectDir.Length == 0)
        {
            // Вызов в обход погашенного пункта (чужой мод) — тоже ничего.
            return;
        }

        string path = Path.Combine(projectDir, "src", "meta.xml");

        MetaXmlFields fields;
        try
        {
            fields = MetaXmlEditor.Read(path);
        }
        catch (Exception ex) when (ex is MetaXmlException or IOException or UnauthorizedAccessException)
        {
            StatusText = "имя и описание не открыть: " + ex.Message;
            ConfirmHandler("Имя и описание мода", ex.Message + "\n\n" + path);
            return;
        }

        var edit = new MetaEditViewModel(fields.Id, path, fields.Name, fields.Description, focus);
        if (!EditMetaHandler(edit))
        {
            StatusText = "имя и описание не изменены";
            return;
        }

        // Только изменённые поля: нетронутое поле в файле могли за это время поправить в
        // редакторе, и снимок из окна откатил бы эту правку.
        bool written;
        try
        {
            written = MetaXmlEditor.Write(
                path,
                edit.NameChanged ? edit.ResultName : null,
                edit.DescriptionChanged ? edit.ResultDescription : null);
        }
        catch (Exception ex) when (ex is MetaXmlException or IOException or UnauthorizedAccessException)
        {
            StatusText = "не сохранено: " + ex.Message;
            ConfirmHandler("Имя и описание не сохранены", ex.Message + "\n\n" + path);
            return;
        }

        string message;
        if (!written)
        {
            // Пока окно было открыто, файл уже поправили ровно так же — например, в редакторе.
            message = "в src\\meta.xml уже стоят эти имя и описание — записывать нечего.";
        }
        else
        {
            string saved = edit.NameChanged
                ? $"«{edit.OriginalName}» → «{edit.ResultName}»: сохранено в src\\meta.xml"
                : "описание сохранено в src\\meta.xml";
            message = row.IsInstalled
                ? saved + ". В игре имя и описание сменятся после пересборки мода и установки."
                : row.HasBuiltArtifact
                    ? saved + ". Сборка в dist\\ ещё со старыми — пересоберите, прежде чем ставить."
                    : saved + ".";
        }

        // Если это открытый сейчас проект, его имя стоит и в заголовке главного окна.
        if (string.Equals(TrimDir(_main.ProjectDir), TrimDir(projectDir), StringComparison.OrdinalIgnoreCase)
            && _main.ReloadProjectCommand.CanExecute(null))
        {
            _main.ReloadProjectCommand.Execute(null);
        }

        await RefreshAsync().ConfigureAwait(true);

        // Новое имя может больше не подходить под строку поиска, и строка молча пропала бы из
        // списка — со стороны это выглядит как пропавший мод.
        string query = SearchText.Trim();
        if (written && query.Length > 0 && !Mods.Any(r => SameDir(r.ProjectDir, projectDir)))
        {
            message += $" Строка скрыта поиском «{query}».";
        }

        // Перечитывание заканчивается своим сообщением («прочитано N файлов») — итог правки
        // обязан остаться на виду.
        StatusText = message;
    }

    private static bool SameDir(string a, string b) =>
        a.Length > 0 && string.Equals(TrimDir(a), TrimDir(b), StringComparison.OrdinalIgnoreCase);

    private bool CanInstallFromDist() => Selected is { HasBuiltArtifact: true } && CanWrite;

    /// <summary>Поставить в игру собранный <c>.mtmod</c> из <c>dist\</c> своего проекта.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanInstallFromDist))]
    private async Task InstallFromDistAsync()
    {
        // Пока идут диалоги и сама операция, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;

        // Установка — ровно в ту игру, путь в которую показан в диалоге.
        GameInstall? game = _game;
        if (row is null || !row.HasBuiltArtifact || game is null)
        {
            return;
        }

        await InstallArtifactAsync(row, game, row.BuiltArtifactPath, row.BuiltVersion,
                "Соберите проект на экране «Сборка и лог» и повторите.")
            .ConfigureAwait(true);
    }

    private bool CanBuildAndInstall() => Selected is { ProjectDir.Length: > 0 } && CanWrite && !_main.IsBusy;

    /// <summary>
    /// «Собрать и установить» из меню строки своего мода: build.py собирает проект строки в его
    /// <c>dist\</c>, затем собранный архив ставится в игру так же, как «Поставить из dist».
    /// <para>
    /// build.py здесь запускается БЕЗ <c>--install</c> (см. <see cref="MainViewModel.BuildForModsAsync"/>):
    /// в каталог игры пишет только служба библиотеки и только после диалога с полными путями, а
    /// прежние версии мода выключаются, а не грузятся рядом с новой. Запрет записи при запущенной
    /// игре проверяется и до сборки (незачем собирать то, что всё равно не поставить), и после
    /// неё: сборка идёт секунды, игру за это время могли запустить.
    /// </para>
    /// </summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanBuildAndInstall))]
    private async Task BuildAndInstallAsync()
    {
        // Пока идут сборка, диалоги и сама установка, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;
        GameInstall? game = _game;
        if (row is null || row.ProjectDir.Length == 0 || game is null)
        {
            return;
        }

        RefreshGameState();
        if (!CanWrite)
        {
            StatusText = WriteLockReason;
            return;
        }

        string projectDir = row.ProjectDir;
        StatusText = "сборка «" + row.Title + "»… вывод build.py — на экране «Сборка и лог»";

        ModsBuildOutcome built = await _main.BuildForModsAsync(projectDir).ConfigureAwait(true);
        StatusText = built.Message;

        if (!built.Started)
        {
            // До build.py не дошло: занято, проекта нет или пользователь сам отказался в диалоге.
            return;
        }

        BuildResult result = built.Result!;
        string source = result.ArtifactPath;
        AddJournalLine(BuildLogLine(result.Success, projectDir, source, built.Message));

        if (!result.Success)
        {
            if (!built.Cancelled)
            {
                ConfirmHandler("Сборка не удалась", built.Message + "\n\n" + projectDir
                                                    + "\n\nВывод build.py — на экране «Сборка и лог». В игру не записано ничего.");
            }

            return;
        }

        if (source.Length == 0)
        {
            // Путь к архиву берётся ТОЛЬКО из вывода build.py: предсказанное имя могло бы
            // поставить в игру старый файл из dist\.
            StatusText = "build.py не сообщил, какой файл собран, — установка отменена";
            ConfirmHandler("Собрать и установить", "Сборка прошла, но build.py не напечатал строку «[build] OK <путь>».\n\n"
                                                   + projectDir + "\n\nЧто ставить в игру, неизвестно — установка отменена.");
            return;
        }

        // Пока шла сборка, на экране «Сборка и лог» могли сменить игру. Диалог установки
        // показывает путь той игры, что была при нажатии, и ставить в другую нельзя.
        if (!ReferenceEquals(_game, game))
        {
            StatusText = "каталог игры сменился во время сборки — установка отменена";
            AddJournalLine(BuildLogLine(false, projectDir, source, StatusText));
            return;
        }

        await InstallArtifactAsync(row, game, source, built.Project!.Version,
                "build.py сообщил этот путь, но файла на диске нет.")
            .ConfigureAwait(true);
    }

    /// <summary>Диалог «Собрать сетап» открыт — второй не нужен.</summary>
    private bool _setupDialogOpen;

    private bool CanBuildSetup() => !_setupDialogOpen;

    /// <summary>
    /// «Собрать сетап…»: свои моды — в один Setup.exe, в котором при установке можно выбрать,
    /// что ставить. В игру отсюда не пишется ничего, поэтому запущенная игра не мешает.
    /// <para>
    /// Проекты ищутся так же, как для «своих» строк списка (<see cref="CollectProjectDirs"/>), а
    /// пересборка идёт через <see cref="MainViewModel.BuildForModsAsync"/> — build.py без
    /// <c>--install</c>, с теми же проверками и выводом на экране «Сборка и лог».
    /// </para>
    /// </summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanBuildSetup))]
    private async Task BuildSetupAsync()
    {
        StatusText = "поиск своих проектов для сетапа…";

        IReadOnlyList<SetupBundleCandidate> candidates;
        try
        {
            // Перебор соседних каталогов и чтение dist\ ходят на диск — в фоне, как и обход.
            candidates = await Task.Run(() => SetupBundleCandidates.Read(CollectProjectDirs())).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusText = "проекты для сетапа не прочитаны: " + Short(ex);
            return;
        }

        GameInstall? game = _game ?? DetectGame();
        var dialog = new SetupBundleViewModel(candidates, new SetupBundleContext
        {
            StubPath = FindSetupStub(),
            GameVersion = game?.Version ?? string.Empty,
            GameRoot = game?.RootPath ?? (string.IsNullOrWhiteSpace(_main.GameDir) ? null : _main.GameDir),
            BuildProject = dir => _main.BuildForModsAsync(dir),
            Confirm = (caption, body) => ConfirmHandler(caption, body),
            PickFolder = PickSetupFolder,
            Reveal = RevealInExplorer,
            Settings = _main.SetupBundleSettings,
        });

        _setupDialogOpen = true;
        BuildSetupCommand.NotifyCanExecuteChanged();
        try
        {
            SetupBundleHandler(dialog);
        }
        finally
        {
            _setupDialogOpen = false;
            BuildSetupCommand.NotifyCanExecuteChanged();
        }

        // Выбор запоминается и тогда, когда сетап не собирали: снятая с шаблона галочка не
        // должна возвращаться при следующем открытии.
        try
        {
            _main.RememberSetupBundle(dialog.CurrentSettings());
        }
        catch (Exception ex)
        {
            StatusText = "выбор для сетапа не сохранён: " + Short(ex);
            return;
        }

        if (dialog.ResultPath is { } path)
        {
            AddJournalLine($"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}  {"SETUP",-7} ok    {path}  ({dialog.SelectedCount} {Plural(dialog.SelectedCount, "мод", "мода", "модов")})");
            StatusText = "сетап собран: " + path;
        }
        else
        {
            StatusText = dialog.StatusText.Length > 0 ? "сетап не собран: " + dialog.StatusText : "сетап не собирали";
        }
    }

    private static string? PickSetupFolder(string initial)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Папка для сетапа",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
        {
            picker.InitialDirectory = initial;
        }

        return picker.ShowDialog() == true ? picker.FolderName : null;
    }

    private void RevealInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"")
            {
                UseShellExecute = true
            })?.Dispose();
        }
        catch (Exception ex)
        {
            StatusText = "проводник не открылся: " + Short(ex);
        }
    }

    private static string BuildLogLine(bool ok, string projectDir, string artifact, string message)
    {
        // Тот же вид, что у строк службы библиотеки (ModOperationResult.ToLogLine).
        string arrow = artifact.Length == 0 ? string.Empty : " -> " + artifact;
        return $"{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}  {"BUILD",-7} {(ok ? "ok  " : "FAIL")}  {projectDir}{arrow}  ({message})";
    }

    /// <summary>
    /// Общий хвост «Поставить из dist» и «Собрать и установить»: повторная проверка игры,
    /// диалог с полными путями и сама установка через службу библиотеки.
    /// </summary>
    /// <param name="row">Строка, над которой операция.</param>
    /// <param name="game">Игра, путь в которую будет показан в диалоге.</param>
    /// <param name="source">Собранный <c>.mtmod</c>.</param>
    /// <param name="version">Версия сборки для диалога; пустая — не показывать.</param>
    /// <param name="missingHint">Что сказать, если файла сборки нет.</param>
    private async Task InstallArtifactAsync(
        InstalledModViewModel row,
        GameInstall game,
        string source,
        string version,
        string missingHint)
    {
        RefreshGameState();
        if (!CanWrite)
        {
            StatusText = WriteLockReason;
            return;
        }

        if (!File.Exists(source))
        {
            StatusText = "сборки нет на диске: " + source;
            ConfirmHandler("Установить мод в игру", "Файл сборки не найден:\n\n" + source + "\n\n" + missingHint);
            return;
        }

        string target = Path.Combine(game.ModsDir, Path.GetFileName(source));
        bool sameNameExists = SafeExists(target);

        var body = new StringBuilder();
        body.Append("Установить «").Append(row.Title);
        if (version.Length > 0)
        {
            body.Append(' ').Append(version);
        }

        body.Append("» в игру?\n\n");
        body.Append("откуда:\n").Append(source).Append('\n');
        body.Append("куда:\n").Append(target).Append("\n\n");

        if (sameNameExists)
        {
            body.Append("Файл с таким именем в игре УЖЕ ЕСТЬ и будет перезаписан.\n");
        }

        body.Append("Другие установленные версии этого мода будут ВЫКЛЮЧЕНЫ (.mtmod.off), а не удалены.\n");
        body.Append("\nИзменения вступят в силу при следующем запуске игры.");

        if (!ConfirmHandler("Установить мод в игру", body.ToString()))
        {
            return;
        }

        var options = new ModInstallOptions
        {
            DisablePreviousVersions = true,
            OverwriteSameName = sameNameExists
        };

        await RunOperationAsync(row, ModOperation.Install, () => _library.Install(game, source, options)).ConfigureAwait(true);
    }

    private bool CanDelete() => Selected is { IsInstalled: true } && CanWrite;

    /// <summary>
    /// Удалить файл мода. Два шага: предупреждение с предложением выключить вместо удаления,
    /// затем ввод имени файла побуквенно.
    /// </summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanDelete))]
    private async Task DeleteAsync()
    {
        // Пока идут диалоги и сама операция, наблюдатель не перечитывает список.
        using CommandHold hold = HoldBackgroundRefresh();

        InstalledModViewModel? row = Selected;
        if (row?.Mod is null || !EnsureWritable(row))
        {
            return;
        }

        var body = new StringBuilder();
        body.Append("Удалить файл мода БЕЗ ВОЗМОЖНОСТИ ОТМЕНЫ?\n\n");
        body.Append(row.FullPath).Append('\n');
        body.Append(row.SizeText).Append('\n');

        if (!row.IsMine)
        {
            body.Append("\nЭТО ЧУЖОЙ МОД.\n");
        }

        body.Append("\nВыключение (.mtmod.off) обратимо, удаление — нет.\n");
        body.Append("Если задача в том, чтобы мод не грузился, правильное действие — «Выключить».");

        if (!ConfirmHandler("Удалить мод из игры", body.ToString()))
        {
            return;
        }

        string expected = row.FileName;
        string? typed = TypedConfirmHandler(
            "Подтверждение удаления",
            "Чтобы удалить файл навсегда, напечатайте его имя:\n\n" + row.FullPath,
            expected);

        if (typed is null)
        {
            StatusText = "удаление отменено";
            return;
        }

        // Команда действует только на то, что пользователь видел, когда нажимал кнопку: если
        // выделение за время диалогов ушло на ДРУГОЙ файл — отказ. Сравнивается файл, а не
        // объект строки: перечитывание, закончившееся под диалогом, заменяет строку тем же
        // файлом, и это не повод отказывать. Что сам файл за это время не подменили,
        // проверяет служба по снимку (ModWriteExpectation).
        if (!string.Equals(Selected?.FullPath, row.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "выбор изменился — удаление отменено";
            AddJournalLine("REFUSED удаление отменено: выбор изменился во время подтверждения — " + row.FullPath);
            return;
        }

        GameInstall game = _game!;
        InstalledMod mod = row.Mod;
        await RunOperationAsync(row, ModOperation.Delete, () => _library.Delete(game, mod.FullPath, typed, ModWriteExpectation.From(mod)))
            .ConfigureAwait(true);
    }

    // =================================================================== служебное

    private bool EnsureWritable(InstalledModViewModel row)
    {
        RefreshGameState();

        if (!CanWrite)
        {
            StatusText = WriteLockReason;
            return false;
        }

        if (!SafeExists(row.FullPath))
        {
            // Тихое чтение: обычное затёрло бы объяснение, почему кнопка ничего не сделала,
            // в тот же миг («чтение каталога модов…»).
            StatusText = "файл исчез с диска — список обновлён";
            _ = RefreshCoreAsync(quiet: true);
            return false;
        }

        return true;
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <param name="row">Строка, над которой операция.</param>
    /// <param name="kind">
    /// Какая операция — для записи о сбое, если служба не вернула итог, а бросила исключение.
    /// Раньше такой сбой любой операции уходил в журнал как ENABLE.
    /// </param>
    /// <param name="operation">Сама операция службы.</param>
    private async Task RunOperationAsync(InstalledModViewModel row, ModOperation kind, Func<ModOperationResult> operation)
    {
        // Пока идёт своя операция, наблюдатель за каталогом не перечитывает список: операция
        // перечитает его сама, когда закончит.
        using (HoldBackgroundRefresh())
        {
            ModOperationResult result;
            try
            {
                result = await Task.Run(operation).ConfigureAwait(true);
            }
            catch (NotImplementedException)
            {
                result = new ModOperationResult(
                    kind,
                    ModOperationStatus.Failed,
                    "служба библиотеки модов ещё не реализована",
                    row.FullPath);
            }
            catch (Exception ex)
            {
                result = new ModOperationResult(
                    kind,
                    ModOperationStatus.Failed,
                    Short(ex),
                    row.FullPath);
            }

            foreach (ModOperationResult related in result.Related)
            {
                AddJournalLine(SafeLogLine(related));
            }

            AddJournalLine(SafeLogLine(result));
            StatusText = result.Message;

            if (!result.Success)
            {
                ConfirmHandler("Операция не выполнена", result.Message + "\n\n" + result.SourcePath);
            }

            // Состояние строки НИКОГДА не предполагается по успеху команды: каталог перечитывается.
            // Строку итога (включённый, выключенный, поставленный файл) выделит само
            // перечитывание — и только если она видна под текущим фильтром.
            _preferSelectPath = result.Success && result.TargetPath is { Length: > 0 } target ? target : null;
            await RefreshAsync().ConfigureAwait(true);

            // Перечитывание каталога заканчивается своим сообщением («прочитано N файлов»), и оно
            // затёрло бы итог. Итог обязан остаться на виду — и отказ (модальный диалог уже
            // закрыт, кроме строки состояния и журнала о нём ничего не напоминает), и успех:
            // «файл удалён безвозвратно» иначе видно только в свёрнутом журнале.
            StatusText = result.Message;
        }
    }

    private static string SafeLogLine(ModOperationResult result)
    {
        try
        {
            return result.ToLogLine();
        }
        catch (Exception)
        {
            return result.Status + " " + result.Message + " " + result.SourcePath;
        }
    }

    private void AddJournalLine(string line)
    {
        Journal.Insert(0, line);
        while (Journal.Count > JournalCapacity)
        {
            Journal.RemoveAt(Journal.Count - 1);
        }
    }

    private void LoadJournalFromDisk()
    {
        IReadOnlyList<string> lines;
        try
        {
            lines = _library.ReadOperationLog(JournalCapacity);
        }
        catch (Exception)
        {
            return;
        }

        // Служба отдаёт от старых к новым, экран показывает свежие сверху.
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            Journal.Add(lines[i]);
        }
    }

    private static string Plural(int n, string one, string few, string many)
    {
        int mod100 = n % 100;
        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return (n % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "служба Core ещё не реализована (заглушка)",
        _ => ex.Message
    };

    /// <summary>Диалог «Имя и описание мода» поверх главного окна.</summary>
    /// <param name="edit">Модель диалога; введённое остаётся в ней.</param>
    /// <returns><c>true</c> — нажали «Сохранить».</returns>
    private static bool DefaultEditMeta(MetaEditViewModel edit)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            // Нет приложения (тест, headless) — правка не подтверждается. Отказ безопаснее.
            return false;
        }

        var dialog = new Views.MetaEditDialog { DataContext = edit };
        if (app.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
        }

        return dialog.ShowDialog() == true;
    }

    /// <summary>Диалог «Собрать сетап» поверх главного окна.</summary>
    /// <param name="dialog">Модель диалога.</param>
    private static void DefaultSetupBundle(SetupBundleViewModel dialog)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            // Нет приложения (тест, headless) — окна нет и собирать нечего.
            return;
        }

        var window = new Views.SetupBundleDialog { DataContext = dialog };
        if (app.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }

    private static bool DefaultConfirm(string caption, string body)
        => MessageBox.Show(
               body,
               caption,
               MessageBoxButton.YesNo,
               MessageBoxImage.Warning,
               MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <summary>
    /// Второй шаг удаления: маленькое модальное окно, в котором надо напечатать имя файла.
    /// Собрано кодом, а не отдельным XAML-окном, — ради одного диалога заводить файл окна
    /// незачем, а стили берутся из словаря приложения, поэтому выглядит оно как остальной кокпит.
    /// </summary>
    /// <param name="caption">Заголовок окна.</param>
    /// <param name="body">Текст с полным путём.</param>
    /// <param name="expected">Имя файла, которое надо напечатать.</param>
    /// <returns>Напечатанное имя либо <c>null</c>, если отменили.</returns>
    private static string? DefaultTypedConfirm(string caption, string body, string expected)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            // Нет приложения (тест, headless) — удаление не подтверждается. Отказ безопаснее.
            return null;
        }

        var input = new TextBox
        {
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"),
            Margin = new Thickness(0, 6, 0, 10)
        };

        var ok = new Button { Content = "Удалить навсегда", IsEnabled = false, MinWidth = 150, Margin = new Thickness(0, 0, 6, 0) };
        var cancel = new Button { Content = "Отмена", IsCancel = true, MinWidth = 90 };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = "напечатайте: " + expected,
            Margin = new Thickness(0, 10, 0, 0),
            FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas")
        });
        panel.Children.Add(input);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var window = new Window
        {
            Title = caption,
            Content = panel,
            SizeToContent = SizeToContent.Height,
            Width = 660,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        try
        {
            if (app.MainWindow is not null && !ReferenceEquals(app.MainWindow, window))
            {
                window.Owner = app.MainWindow;
            }
        }
        catch (InvalidOperationException)
        {
            // Главное окно ещё не показано — обойдёмся без владельца.
        }

        string? typed = null;
        input.TextChanged += (_, _) => ok.IsEnabled = string.Equals(input.Text, expected, StringComparison.Ordinal);
        ok.Click += (_, _) =>
        {
            typed = input.Text;
            window.Close();
        };
        cancel.Click += (_, _) => window.Close();

        window.ShowDialog();
        return typed;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _visible = false;
        _gameWatch.Stop();
        _changeDebounce.Stop();
        DisposeWatcher();
        _main.PropertyChanged -= OnMainPropertyChanged;
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
