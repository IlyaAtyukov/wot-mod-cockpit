using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Stage4;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.App.ViewModels;

/// <summary>Одно замечание к форме — и у поля, и в общем списке внизу экрана.</summary>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Message">Что не так и чем это кончится.</param>
public sealed record SettingsDesignerIssue(SettingsIssueSeverity Severity, string Message)
{
    /// <summary>Контрол, к которому относится замечание; <c>null</c> — замечание уровня формы.</summary>
    public SettingControlViewModel? Control { get; init; }

    /// <summary>Где именно: «шапка», «левая колонка · Галочка» и тому подобное.</summary>
    public string Where { get; init; } = string.Empty;

    /// <summary>Значок серьёзности для списка.</summary>
    public string Badge => Severity switch
    {
        SettingsIssueSeverity.Error => "ошибка",
        SettingsIssueSeverity.Warning => "предупр.",
        _ => "справка"
    };
}

/// <summary>Один файл предпросмотра: то, что кокпит предлагает записать в проект.</summary>
public sealed partial class GeneratedArtifactViewModel : ObservableObject
{
    /// <summary>Создаёт строку предпросмотра.</summary>
    /// <param name="file">Файл от генератора.</param>
    /// <param name="fullPath">Абсолютный путь либо пустая строка, если проекта нет.</param>
    public GeneratedArtifactViewModel(SettingsCodeFile file, string fullPath)
    {
        File = file;
        FullPath = fullPath;
        Exists = fullPath.Length > 0 && System.IO.File.Exists(fullPath);
        HasMarkers = Exists && HasGeneratedRegion(fullPath);
    }

    /// <summary>
    /// В существующем файле стоят маркеры <c>cockpit-generated</c>, то есть перезапишется
    /// ТОЛЬКО участок между ними. Различать это обязательно: диалог подтверждения, который
    /// всем подряд обещает «перезапишется целиком», пугает тем, чего в обычном случае не
    /// происходит, и не выделяет единственный случай, когда это правда.
    /// </summary>
    public bool HasMarkers { get; }

    private static bool HasGeneratedRegion(string path)
    {
        try
        {
            return System.IO.File.ReadAllText(path, Encoding.UTF8)
                .Contains(GeneratedRegionWriter.BeginToken, StringComparison.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Файл.</summary>
    public SettingsCodeFile File { get; }

    /// <summary>Абсолютный путь.</summary>
    public string FullPath { get; }

    /// <summary>Файл на диске уже есть.</summary>
    public bool Exists { get; }

    /// <summary>Имя файла.</summary>
    public string Title => File.RelativePath[(File.RelativePath.LastIndexOf('/') + 1)..];

    /// <summary>Путь относительно корня проекта.</summary>
    public string RelativePath => File.RelativePath;

    /// <summary>Содержимое.</summary>
    public string Content => File.Content;

    /// <summary>Пояснение про файл.</summary>
    public string Note => File.Note;

    /// <summary>Подсветка как у python.</summary>
    public bool IsPython => File.IsPython;

    /// <summary>Что с ним будет при записи.</summary>
    public string Action => File.IsSnippet
        ? "фрагмент для показа — в проект не пишется"
        : File.Kind switch
        {
            SettingsFileKind.CreateOnce => Exists ? "уже есть — не трогаем" : "будет создан",
            SettingsFileKind.Design => Exists ? "будет перезаписан" : "будет создан",
            _ => !Exists
                ? "будет создан"
                : HasMarkers
                    ? "будет обновлён участок между маркерами; ваш код ниже сохранится"
                    : "маркеров нет — будет перезаписан ЦЕЛИКОМ, копия уедет в .bak"
        };

    /// <summary>Размер в символах.</summary>
    public string Size => Content.Length.ToString("N0", CultureInfo.CurrentCulture) + " символов";
}

/// <summary>
/// Дизайнер настроек ModsSettingsAPI: слева палитра, в центре две колонки окна игры,
/// справа свойства выбранного контрола.
/// <para>
/// Экран отвечает на вопрос «как показать игроку настройки моего мода» и отвечает не
/// ссылкой на чужую документацию, а формой, которую собирают мышью, и кодом, который
/// после этого можно записать в проект.
/// </para>
/// <para>
/// Три вещи, из-за которых здесь больше кода, чем «список плюс форма».
/// </para>
/// <list type="number">
///   <item>Проверки. У ModsSettingsAPI нет НИ ОДНОЙ собственной проверки: неизвестный тип
///   он пишет в лог и подставляет пустышку, индекс за пределами списка не ловит,
///   несуществующий код клавиши принимает молча. Всё, что мододел узнал бы из игры,
///   он обязан узнать здесь.</item>
///   <item>Версия шаблона. Добавленный контрол без поднятия <c>settingsVersion</c>
///   в окне НЕ ПОЯВИТСЯ, а поднятая версия СБРОСИТ настройки игрока. Экран следит
///   за составом ключей и говорит об этом сам.</item>
///   <item>Живое состояние. Если у игрока стоят моды с ModsSettingsAPI, рядом лежит
///   <c>modsettings.dat</c> с их работающими шаблонами и занятыми горячими клавишами.
///   Кокпит читает его — и только читает; без него экран работает так же, просто без импорта
///   и без проверки занятых клавиш.</item>
/// </list>
/// </summary>
public sealed partial class SettingsDesignerViewModel : ObservableObject, IDisposable
{
    /// <summary>Имя ключа настройки: ASCII-идентификатор, как в питоне.</summary>
    private static readonly Regex VarNamePattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    /// <summary>Ключ мода: ASCII без пробелов. Именно им API сортирует список модов.</summary>
    private static readonly Regex LinkagePattern = new(@"^[\x21-\x7E]+$", RegexOptions.Compiled);

    /// <summary>
    /// Задержка перед реакцией на смену проекта. Причина та же, что у этапа 3:
    /// <see cref="MainViewModel.ProjectDir"/> меняется в НАЧАЛЕ загрузки проекта,
    /// а разобранный паспорт появляется в её конце.
    /// </summary>
    private const int ProjectDebounceMs = 120;

    private readonly MainViewModel _main;
    private readonly ISettingsDesignerBackend _backend;
    private readonly IEditorLauncher _editor;
    private readonly LiveModSettingsReader _live = new();
    private readonly DispatcherTimer _projectDebounce;

    private bool _loading;
    private bool _disposed;

    /// <summary>Снимок формы на момент последней сборки кода; null — код не собирали.</summary>
    private string? _generatedSnapshot;
    private ModProject? _project;
    private IReadOnlyList<string> _generatedVarNames = Array.Empty<string>();
    private int _generatedVersion;

    /// <summary>Создаёт модель экрана и цепляет её к главному окну.</summary>
    /// <param name="main">Модель главного экрана: у неё живут проект и переключатель экранов.</param>
    /// <param name="backend">Шов к генератору кода.</param>
    /// <param name="editor">Внешний редактор.</param>
    public SettingsDesignerViewModel(
        MainViewModel main,
        ISettingsDesignerBackend backend,
        IEditorLauncher editor)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));

        Palette = SettingsControlSchema.PaletteOrder.Select(t => new SettingsPaletteItem(t)).ToList();
        KnownKeys = backend.KnownKeys.Count > 0 ? backend.KnownKeys : SettingsHotKey.KnownKeys;

        Column1.CollectionChanged += (_, _) => OnFormChanged();
        Column2.CollectionChanged += (_, _) => OnFormChanged();

        _projectDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(ProjectDebounceMs)
        };
        _projectDebounce.Tick += OnProjectDebounceTick;

        _main.PropertyChanged += OnMainPropertyChanged;
        _main.AttachSettingsDesigner(this);
    }

    private void OnMainPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.ProjectDir))
        {
            return;
        }

        _projectDebounce.Stop();
        _projectDebounce.Start();
    }

    private void OnProjectDebounceTick(object? sender, EventArgs e)
    {
        _projectDebounce.Stop();
        SetProject(_main.CurrentProject);
    }

    // =================================================================== состояние экрана

    /// <summary>Палитра типов контролов.</summary>
    public IReadOnlyList<SettingsPaletteItem> Palette { get; }

    /// <summary>Клавиши для редактора горячей клавиши.</summary>
    public IReadOnlyList<SettingsKeyCode> KnownKeys { get; }

    /// <summary>Левая колонка окна настроек.</summary>
    public ObservableCollection<SettingControlViewModel> Column1 { get; } = new();

    /// <summary>Правая колонка окна настроек.</summary>
    public ObservableCollection<SettingControlViewModel> Column2 { get; } = new();

    /// <summary>Замечания проверки.</summary>
    public ObservableCollection<SettingsDesignerIssue> Issues { get; } = new();

    /// <summary>Файлы предпросмотра последней генерации.</summary>
    public ObservableCollection<GeneratedArtifactViewModel> Artifacts { get; } = new();

    /// <summary>Шаблоны установленных модов из живого <c>modsettings.dat</c>.</summary>
    public ObservableCollection<LiveModTemplate> LiveTemplates { get; } = new();

    /// <summary>
    /// Файл состояния ModsSettingsAPI вместо найденного в профиле игрока; <c>null</c> — искать
    /// как обычно (<see cref="LiveModSettingsReader.FindDefaultPath"/>). Нужен тестам: настоящий
    /// <c>modsettings.dat</c> хранит настройки игрока и шаблоны всех его модов, и обычный прогон
    /// тестов читать его не должен.
    /// </summary>
    public string? LiveStatePathOverride { get; set; }

    /// <summary>Генератор кода подключён.</summary>
    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>Почему генератор недоступен.</summary>
    public string UnavailableReason => _backend.UnavailableReason;

    /// <summary>Имя генератора — чтобы «чей это код» не было загадкой.</summary>
    public string GeneratorName => _backend.Name;

    /// <summary>Выбранный контрол; его свойства правит панель справа.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private SettingControlViewModel? _selected;

    /// <summary>Контрол выбран.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Выбранный файл предпросмотра.</summary>
    [ObservableProperty]
    private GeneratedArtifactViewModel? _selectedArtifact;

    /// <summary>Выбранный чужой шаблон в панели импорта.</summary>
    [ObservableProperty]
    private LiveModTemplate? _selectedLiveTemplate;

    /// <summary>Панель предпросмотра кода открыта.</summary>
    [ObservableProperty]
    private bool _showCode;

    /// <summary>Панель импорта из живого состояния открыта.</summary>
    [ObservableProperty]
    private bool _showImport;

    /// <summary>Превью ужимается по ширине панели вместо прокрутки в масштабе 1:1.</summary>
    [ObservableProperty]
    private bool _fitPreview = true;

    /// <summary>Строка состояния экрана.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Путь к живому файлу состояния.</summary>
    [ObservableProperty]
    private string _livePath = string.Empty;

    /// <summary>Почему живое состояние не прочитано.</summary>
    [ObservableProperty]
    private string _liveError = string.Empty;

    // =================================================================== шапка формы

    /// <summary>Ключ мода в состоянии API.</summary>
    [ObservableProperty]
    private string _linkage = string.Empty;

    /// <summary>Заголовок блока мода в окне настроек.</summary>
    [ObservableProperty]
    private string _modDisplayName = string.Empty;

    /// <summary>Версия состава формы строкой.</summary>
    [ObservableProperty]
    private string _settingsVersionText = "1";

    /// <summary>У мода есть верхний тумблер вкл/выкл.</summary>
    [ObservableProperty]
    private bool _hasEnabled = true;

    /// <summary>Значение тумблера по умолчанию.</summary>
    [ObservableProperty]
    private bool _enabledDefault = true;

    partial void OnLinkageChanged(string value) => OnFormChanged();

    partial void OnModDisplayNameChanged(string value) => OnFormChanged();

    partial void OnSettingsVersionTextChanged(string value) => OnFormChanged();

    partial void OnHasEnabledChanged(bool value) => OnFormChanged();

    partial void OnEnabledDefaultChanged(bool value) => OnFormChanged();

    // =================================================================== проект

    /// <summary>Каталог текущего проекта либо пусто.</summary>
    public string ProjectDir => _project?.ProjectDir ?? string.Empty;

    /// <summary>Проект открыт: без него генерировать некуда.</summary>
    public bool HasProject => _project is not null;

    /// <summary>Имя пакета мода — каталог, куда лягут файлы.</summary>
    public string PackageName => _project?.PackageName ?? string.Empty;

    /// <summary>Куда именно лягут файлы.</summary>
    public string TargetHint => _project is null
        ? "проект не выбран"
        : PackageName.Length == 0
            ? "у проекта не задано имя пакета — генерировать некуда"
            : $"{_project.SrcDirRelative}/res/scripts/client/gui/mods/{PackageName}/settings_schema.py";

    /// <summary>Форма пуста: ни одного контрола.</summary>
    public bool IsEmpty => Column1.Count == 0 && Column2.Count == 0;

    /// <summary>
    /// Подхватывает проект. Зовётся из главного экрана при открытии другого мода.
    /// Форма при этом перечитывается из <c>settings.design.json</c>, если он есть.
    /// </summary>
    /// <param name="project">Проект либо <c>null</c>.</param>
    public void SetProject(ModProject? project)
    {
        bool sameProject = _project is not null && project is not null
            && string.Equals(_project.ProjectDir, project.ProjectDir, StringComparison.OrdinalIgnoreCase);

        _project = project;

        OnPropertyChanged(nameof(ProjectDir));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(PackageName));
        OnPropertyChanged(nameof(TargetHint));

        if (sameProject)
        {
            return;
        }

        Artifacts.Clear();
        SelectedArtifact = null;
        ShowCode = false;
        _generatedSnapshot = null;
        IsPreviewStale = false;

        if (project is null)
        {
            return;
        }

        // Паспорт мода — разумное начальное состояние шапки: linkage совпадает с id мода,
        // заголовок — с его отображаемым именем.
        if (Linkage.Length == 0)
        {
            Linkage = project.ModId;
        }

        if (ModDisplayName.Length == 0)
        {
            ModDisplayName = string.IsNullOrWhiteSpace(project.DisplayName) ? project.ModId : project.DisplayName;
        }

        TryLoadDesign(silent: true);
        Revalidate();
    }

    /// <summary>Поднимает экран после загрузки окна.</summary>
    public void Initialize()
    {
        // Живое состояние читается сразу и молча: из него берутся занятые чужими модами
        // горячие клавиши, а его отсутствие — обычное дело и не повод ничего показывать.
        ReadLiveState(silent: true);

        SetProject(_main.CurrentProject);
        Revalidate();
    }

    // =================================================================== правка формы

    /// <summary>Добавить контрол из палитры в конец более короткой колонки.</summary>
    /// <param name="item">Строка палитры.</param>
    [RelayCommand]
    private void AddFromPalette(SettingsPaletteItem? item)
    {
        if (item is null)
        {
            return;
        }

        int column = Column1.Count <= Column2.Count ? 1 : 2;
        InsertNew(item.Type, column, -1);
    }

    /// <summary>
    /// Создаёт контрол и кладёт его в колонку. Единственная точка создания: и палитра,
    /// и перетаскивание из палитры зовут её.
    /// </summary>
    /// <param name="type">Тип контрола.</param>
    /// <param name="column">Номер колонки: 1 или 2.</param>
    /// <param name="index">Куда вставить; -1 — в конец.</param>
    /// <returns>Созданный контрол.</returns>
    public SettingControlViewModel InsertNew(SettingsControlType type, int column, int index)
    {
        var control = new SettingControlViewModel(type, OnFormChanged);

        if (SettingsControlSchema.HasValue(type))
        {
            control.VarName = UniqueVarName(type);
        }

        ObservableCollection<SettingControlViewModel> target = column == 2 ? Column2 : Column1;
        if (index < 0 || index > target.Count)
        {
            target.Add(control);
        }
        else
        {
            target.Insert(index, control);
        }

        Selected = control;
        StatusText = SettingsControlSchema.DisplayName(type) + " добавлен";
        return control;
    }

    /// <summary>
    /// Переносит контрол в другое место: и перестановка внутри колонки, и переезд в соседнюю.
    /// </summary>
    /// <param name="control">Контрол.</param>
    /// <param name="column">Колонка назначения: 1 или 2.</param>
    /// <param name="index">Позиция; -1 — в конец.</param>
    public void MoveControl(SettingControlViewModel control, int column, int index)
    {
        ArgumentNullException.ThrowIfNull(control);

        ObservableCollection<SettingControlViewModel> source = Column1.Contains(control) ? Column1 : Column2;
        ObservableCollection<SettingControlViewModel> target = column == 2 ? Column2 : Column1;

        int from = source.IndexOf(control);
        if (from < 0)
        {
            return;
        }

        if (ReferenceEquals(source, target))
        {
            int to = index < 0 || index > source.Count ? source.Count - 1 : index;

            // Индекс приходит из превью и считается ДО изъятия элемента.
            if (to > from)
            {
                to--;
            }

            to = Math.Clamp(to, 0, source.Count - 1);
            if (to != from)
            {
                source.Move(from, to);
            }
        }
        else
        {
            source.RemoveAt(from);
            int to = index < 0 || index > target.Count ? target.Count : index;
            target.Insert(to, control);
        }

        Selected = control;
    }

    private bool CanEditSelection() => Selected is not null;

    /// <summary>Удалить выбранный контрол.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void RemoveSelected()
    {
        SettingControlViewModel? control = Selected;
        if (control is null)
        {
            return;
        }

        ObservableCollection<SettingControlViewModel> source = Column1.Contains(control) ? Column1 : Column2;
        int index = source.IndexOf(control);
        if (index < 0)
        {
            return;
        }

        source.RemoveAt(index);
        Selected = source.Count == 0 ? null : source[Math.Min(index, source.Count - 1)];
        StatusText = "контрол удалён";
    }

    /// <summary>Поднять выбранный контрол.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void MoveUp() => Shift(-1);

    /// <summary>Опустить выбранный контрол.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void MoveDown() => Shift(1);

    private void Shift(int delta)
    {
        SettingControlViewModel? control = Selected;
        if (control is null)
        {
            return;
        }

        ObservableCollection<SettingControlViewModel> source = Column1.Contains(control) ? Column1 : Column2;
        int index = source.IndexOf(control);
        int target = index + delta;

        if (index < 0 || target < 0 || target >= source.Count)
        {
            return;
        }

        source.Move(index, target);
        Selected = control;
    }

    /// <summary>Перенести выбранный контрол в соседнюю колонку.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void SwapColumn()
    {
        SettingControlViewModel? control = Selected;
        if (control is null)
        {
            return;
        }

        MoveControl(control, Column1.Contains(control) ? 2 : 1, -1);
    }

    /// <summary>Скопировать выбранный контрол рядом.</summary>
    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void DuplicateSelected()
    {
        SettingControlViewModel? control = Selected;
        if (control is null)
        {
            return;
        }

        ObservableCollection<SettingControlViewModel> source = Column1.Contains(control) ? Column1 : Column2;
        int column = ReferenceEquals(source, Column2) ? 2 : 1;

        SettingsControlSpec spec = control.ToSpec();
        SettingControlViewModel copy = InsertNew(control.Type, column, source.IndexOf(control) + 1);
        copy.Load(spec, KnownKeys);

        if (SettingsControlSchema.HasValue(control.Type))
        {
            copy.VarName = UniqueVarName(control.Type, spec.VarName);
        }

        Selected = copy;
    }

    partial void OnSelectedChanged(SettingControlViewModel? value)
    {
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveUpCommand.NotifyCanExecuteChanged();
        MoveDownCommand.NotifyCanExecuteChanged();
        SwapColumnCommand.NotifyCanExecuteChanged();
        DuplicateSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Очистить форму целиком.</summary>
    [RelayCommand]
    private void ClearForm()
    {
        if (!IsEmpty && !ConfirmHandler(
                "Очистить форму",
                "Все контролы будут удалены из формы дизайнера." + Environment.NewLine +
                "Файлы проекта при этом не меняются: очистка живёт только в кокпите," + Environment.NewLine +
                "пока вы не нажмёте «записать в проект»." + Environment.NewLine + Environment.NewLine +
                "Продолжить?"))
        {
            return;
        }

        _loading = true;
        try
        {
            Column1.Clear();
            Column2.Clear();
            Selected = null;
        }
        finally
        {
            _loading = false;
        }

        OnFormChanged();
        StatusText = "форма очищена";
    }

    /// <summary>
    /// Типовая форма для пустого состояния: одна галочка «включить мод». Дальше её
    /// проще дополнить, чем собирать с нуля, и она сразу показывает, как выглядит блок мода.
    /// </summary>
    [RelayCommand]
    private void StartTypicalForm()
    {
        _loading = true;
        try
        {
            Column1.Clear();
            Column2.Clear();

            if (Linkage.Length == 0)
            {
                Linkage = _project?.ModId ?? "ru.local.my_mod";
            }

            if (ModDisplayName.Length == 0)
            {
                ModDisplayName = _project?.DisplayName ?? "Мой мод";
            }

            HasEnabled = true;
            EnabledDefault = true;

            var check = new SettingControlViewModel(SettingsControlType.CheckBox, OnFormChanged)
            {
                Text = "Показывать сообщение в ангаре",
                VarName = "showGreeting",
                BoolValue = true,
                TooltipHeader = "Сообщение в ангаре",
                TooltipBody = "Мод напишет строку в системный чат при входе в ангар"
            };

            Column1.Add(check);
            Selected = check;
        }
        finally
        {
            _loading = false;
        }

        OnFormChanged();
        StatusText = "создана типовая форма: одна галочка";
    }

    /// <summary>Поднять версию состава формы на единицу.</summary>
    [RelayCommand]
    private void BumpVersion()
    {
        int version = ParseVersion() ?? 0;
        SettingsVersionText = (version + 1).ToString(CultureInfo.InvariantCulture);
        StatusText = "settingsVersion = " + SettingsVersionText
                     + " · при следующем запуске игры настройки игрока будут сброшены к умолчаниям шаблона";
    }

    // =================================================================== проверка

    /// <summary>Ошибок в форме.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGenerate))]
    [NotifyPropertyChangedFor(nameof(IssuesHeader))]
    private int _errorCount;

    /// <summary>Предупреждений в форме.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IssuesHeader))]
    private int _warningCount;

    /// <summary>
    /// Форму правили ПОСЛЕ сборки кода: то, что видно в предпросмотре, уже не то, что на экране.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CodeHeader))]
    private bool _isPreviewStale;

    /// <summary>Заголовок панели предпросмотра — он же предупреждение об устаревании.</summary>
    public string CodeHeader => IsPreviewStale
        ? "форма изменилась после генерации — соберите код заново"
        : "предпросмотр: это будет записано в проект";

    /// <summary>Генерировать можно: ошибок нет, есть проект и есть что генерировать.</summary>
    public bool CanGenerate =>
        ErrorCount == 0 && (!IsEmpty || HasEnabled) && HasProject && _backend.IsAvailable;

    /// <summary>Снимок формы для сравнения «то ли показано, что собрано».</summary>
    private string FormSnapshot()
    {
        try
        {
            return Linkage.Trim() + " |form| " + SettingsTemplateJson.Render(ToSpec())
                   + " |project| " + (_project?.ProjectDir ?? string.Empty);
        }
        catch (Exception)
        {
            // Форма настолько сломана, что не печатается. Тогда предпросмотр заведомо устарел.
            return Guid.NewGuid().ToString("N");
        }
    }

    /// <summary>Заголовок панели замечаний.</summary>
    public string IssuesHeader => ErrorCount == 0 && WarningCount == 0
        ? "замечаний нет"
        : $"ошибок: {ErrorCount} · предупреждений: {WarningCount}";

    private void OnFormChanged()
    {
        if (_loading)
        {
            return;
        }

        OnPropertyChanged(nameof(IsEmpty));
        Revalidate();
    }

    /// <summary>
    /// Полная проверка формы. У самого ModsSettingsAPI проверок нет ни одной, поэтому
    /// список правил здесь — это ровно тот список, который иначе пришлось бы узнавать
    /// из сломанного окна настроек в игре.
    /// </summary>
    public void Revalidate()
    {
        var issues = new List<SettingsDesignerIssue>();

        // -------- шапка
        if (Linkage.Trim().Length == 0)
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                "не задан linkage — ключ, по которому API хранит настройки этого мода")
            { Where = "шапка" });
        }
        else if (!LinkagePattern.IsMatch(Linkage.Trim()))
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                "linkage обязан быть ASCII без пробелов: generateSettingsData сортирует моды "
                + "через str.lower, и на unicode-ключе окно настроек не откроется НИ У ОДНОГО мода")
            { Where = "шапка" });
        }
        else if (!Linkage.Contains('.', StringComparison.Ordinal))
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Info,
                "linkage принято писать обратным доменом (io.github.ник.my_mod) — так он точно "
                + "не совпадёт с ключом чужого мода")
            { Where = "шапка" });
        }

        // Ключ, уже занятый ЧУЖИМ установленным модом: под ним лежат его настройки, и
        // регистрация с тем же ключом их затрёт. Свой же мод, установленный в игру,
        // законно занимает свой ключ — по нему и отличаем.
        string ownId = _project?.ModId ?? string.Empty;
        string linkage = Linkage.Trim();

        if (linkage.Length > 0
            && !string.Equals(linkage, ownId, StringComparison.Ordinal)
            && _live.Templates.Any(t => string.Equals(t.Linkage, linkage, StringComparison.Ordinal)))
        {
            LiveModTemplate owner = _live.Templates.First(t => string.Equals(t.Linkage, linkage, StringComparison.Ordinal));

            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Warning,
                $"этот linkage уже занят установленным модом «{owner.DisplayName}»: "
                + "ваш шаблон заменит его блок в окне настроек и сотрёт настройки игрока "
                + "для того мода. Ключ обязан быть свой и неизменный")
            { Where = "шапка" });
        }

        if (ModDisplayName.Trim().Length == 0)
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                "не задан заголовок блока мода (modDisplayName) — в окне настроек он обязателен")
            { Where = "шапка" });
        }

        int? version = ParseVersion();
        if (version is null)
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                "settingsVersion должен быть целым числом")
            { Where = "шапка" });
        }
        else if (version < 0)
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                "settingsVersion не может быть отрицательным")
            { Where = "шапка" });
        }

        if (IsEmpty)
        {
            // Форма из ОДНОГО тумблера — законный мод «включить/выключить галочкой»:
            // api.getSettingsFromTemplate читает 'enabled' ДО колонок, и окно нарисует блок
            // мода с тумблером. Ошибка тут только когда тумблера нет — вот тогда блок
            // действительно пуст.
            issues.Add(HasEnabled
                ? new SettingsDesignerIssue(SettingsIssueSeverity.Warning,
                    "в колонках нет ни одного контрола: в окне у мода будет только верхний "
                    + "тумблер «вкл/выкл». Это рабочая форма, но добавить в неё нечего")
                { Where = "форма" }
                : new SettingsDesignerIssue(SettingsIssueSeverity.Error,
                    "в форме нет ни одного контрола и нет тумблера — блок мода в окне будет пустым")
                { Where = "форма" });
        }

        // -------- контролы
        var names = new Dictionary<string, SettingControlViewModel>(StringComparer.Ordinal);

        foreach ((SettingControlViewModel control, int column) in AllControlsWithColumn())
        {
            var errors = new List<string>();
            var warnings = new List<string>();

            ValidateControl(control, names, errors, warnings);

            control.SetIssues(errors, warnings);

            string where = (column == 1 ? "левая колонка" : "правая колонка") + " · " + control.TypeDisplay
                           + (control.VarName.Length > 0 ? " · " + control.VarName : string.Empty);

            foreach (string error in errors)
            {
                issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Error, error)
                {
                    Control = control,
                    Where = where
                });
            }

            foreach (string warning in warnings)
            {
                issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Warning, warning)
                {
                    Control = control,
                    Where = where
                });
            }
        }

        // -------- версия шаблона против состава
        if (_generatedVarNames.Count > 0 && version is not null)
        {
            var current = AllControls()
                .Where(c => SettingsControlSchema.HasValue(c.Type))
                .Select(c => c.VarName.Trim())
                .ToList();

            bool composition = !current.SequenceEqual(_generatedVarNames, StringComparer.Ordinal);

            if (composition && version <= _generatedVersion)
            {
                issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Warning,
                    $"состав контролов изменился, а settingsVersion остался {version}: "
                    + "ModsSettingsAPI покажет игроку СТАРУЮ форму, и новых ключей в настройках "
                    + "не будет. Поднимите версию — но помните, что поднятая версия сбрасывает "
                    + "значения игрока к умолчаниям шаблона")
                { Where = "шапка" });
            }
        }

        // -------- замечания генератора
        //
        // Свои правила экран уже проверил сам; генератор добавляет то, чего экран знать
        // не может (правила MSA0xx валидатора Core: посторонние ключи из импорта, мёртвые
        // поля, типы вне выборки реальных шаблонов, легаси-форма горячей клавиши). Дублирующие
        // формулировки отсеивает сам шов — см. CoreSettingsDesignerBackend.CoveredByDesigner.
        try
        {
            foreach (SettingsIssue issue in _backend.Validate(ToSpec()))
            {
                issues.Add(new SettingsDesignerIssue(issue.Severity, issue.Message)
                {
                    Where = issue.Field.Length > 0 ? "генератор · " + issue.Field : "генератор"
                });
            }
        }
        catch (Exception ex)
        {
            issues.Add(new SettingsDesignerIssue(SettingsIssueSeverity.Warning,
                "генератор не смог проверить форму: " + Short(ex))
            { Where = "генератор" });
        }

        Issues.Clear();
        foreach (SettingsDesignerIssue issue in issues
                     .OrderByDescending(i => i.Severity)
                     .ThenBy(i => i.Where, StringComparer.CurrentCulture))
        {
            Issues.Add(issue);
        }

        ErrorCount = issues.Count(i => i.Severity == SettingsIssueSeverity.Error);
        WarningCount = issues.Count(i => i.Severity == SettingsIssueSeverity.Warning);

        // Предпросмотр устаревает молча: генерация закрыта CanGenerate, а запись — нет,
        // и без этой сверки кнопка «записать в проект» осталась бы яркой и положила бы на
        // диск форму, отличную от той, что видит человек.
        IsPreviewStale = _generatedSnapshot is not null
                         && !string.Equals(_generatedSnapshot, FormSnapshot(), StringComparison.Ordinal);

        GenerateCodeCommand.NotifyCanExecuteChanged();
        WriteFilesCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanGenerate));
    }

    private void ValidateControl(
        SettingControlViewModel control,
        Dictionary<string, SettingControlViewModel> names,
        List<string> errors,
        List<string> warnings)
    {
        // -------- подпись
        if (SettingsControlSchema.HasText(control.Type) && control.Text.Trim().Length == 0)
        {
            if (control.Type is SettingsControlType.Label
                or SettingsControlType.CheckBox
                or SettingsControlType.RadioButtonGroup)
            {
                errors.Add("нет подписи — контрол окажется в окне безымянным");
            }
            else
            {
                warnings.Add("пустая подпись: окно уберёт заголовок и поднимет контрол наверх");
            }
        }

        // -------- ключ настройки
        if (SettingsControlSchema.HasValue(control.Type))
        {
            string varName = control.VarName.Trim();

            if (varName.Length == 0)
            {
                errors.Add("не задан varName — ключ, под которым значение попадёт в настройки");
            }
            else if (!VarNamePattern.IsMatch(varName))
            {
                errors.Add($"varName '{varName}' не является идентификатором ASCII "
                           + "(буквы, цифры и подчёркивание, не с цифры)");
            }
            else if (string.Equals(varName, "enabled", StringComparison.Ordinal))
            {
                errors.Add("varName 'enabled' занят самим API: под этим ключом хранится тумблер мода");
            }
            else if (names.TryGetValue(varName, out SettingControlViewModel? other))
            {
                errors.Add($"varName '{varName}' уже занят контролом «{other.Text}»: "
                           + "ключи уникальны в пределах ОБЕИХ колонок");
            }
            else
            {
                names[varName] = control;
            }
        }

        // -------- список пунктов
        if (SettingsControlSchema.HasOptions(control.Type))
        {
            if (control.Options.Count == 0)
            {
                errors.Add("список пунктов пуст");
            }
            else
            {
                if (control.IndexValue < 0 || control.IndexValue >= control.Options.Count)
                {
                    errors.Add($"значение по умолчанию — ИНДЕКС пункта, а он вне списка "
                               + $"(нужно 0…{control.Options.Count - 1})");
                }

                if (control.Options.Any(o => o.Label.Trim().Length == 0))
                {
                    errors.Add("у пункта пустая подпись: окно читает у пункта только label");
                }

                if (control.Options.Any(o => o.Tooltip.Trim().Length > 0))
                {
                    warnings.Add("подсказка пункта окном 1.6.4 не читается: в теле фабрики "
                                 + "радиокнопок обращения к tooltip пункта нет вовсе");
                }
            }

            if (control.ScrollsInGame)
            {
                warnings.Add($"пунктов больше {SettingsControlSchema.DropdownScrollLimit}: "
                             + "в окне у списка появится полоса прокрутки");
            }
        }

        // -------- диапазон
        if (SettingsControlSchema.HasRange(control.Type))
        {
            double? min = control.Minimum;
            double? max = control.Maximum;
            double? snap = control.SnapInterval;

            if (min is null || max is null || snap is null)
            {
                errors.Add("минимум, максимум и шаг должны быть числами");
            }
            else
            {
                if (min >= max)
                {
                    errors.Add("минимум должен быть строго меньше максимума");
                }

                if (snap <= 0)
                {
                    errors.Add("шаг (snapInterval) должен быть больше нуля");
                }

                if (control.Type == SettingsControlType.RangeSlider)
                {
                    double? low = control.RangeLow;
                    double? high = control.RangeHigh;
                    double distance = SettingControlViewModel.ParseNumber(control.MinRangeDistanceText) ?? 0;

                    if (low is null || high is null)
                    {
                        errors.Add("концы диапазона должны быть числами");
                    }
                    else
                    {
                        if (low < min || high > max)
                        {
                            errors.Add("концы диапазона выходят за минимум и максимум");
                        }

                        if (high < low)
                        {
                            errors.Add("правый конец диапазона меньше левого");
                        }
                        else if (high - low < distance)
                        {
                            errors.Add($"ширина диапазона меньше minRangeDistance ({distance})");
                        }
                    }
                }
                else
                {
                    double? value = control.ValueNumber;
                    if (value is null)
                    {
                        errors.Add("значение по умолчанию должно быть числом");
                    }
                    else if (value < min || value > max)
                    {
                        errors.Add("значение по умолчанию вне диапазона");
                    }
                }
            }
        }

        // -------- формат метки
        if (SettingsControlSchema.HasFormat(control.Type)
            && !control.Format.Contains("{{value}}", StringComparison.Ordinal))
        {
            warnings.Add("в формате нет подстановки {{value}}: метка справа от ползунка "
                         + "будет показывать один и тот же текст при любом значении");
        }

        // -------- ширина
        if (SettingsControlSchema.HasWidth(control.Type))
        {
            double? width = control.Width;
            if (control.WidthText.Trim().Length > 0 && width is null)
            {
                errors.Add("ширина должна быть числом");
            }
            else if (width > SettingsControlSchema.ColumnWidth)
            {
                warnings.Add($"ширина {width} больше колонки ({SettingsControlSchema.ColumnWidth} px): "
                             + "контрол налезет на соседнюю колонку");
            }
        }

        // -------- цвет
        if (control.Type == SettingsControlType.ColorChoice
            && !SettingControlViewModel.TryParseColor(control.ColorValue, out _))
        {
            errors.Add("цвет задаётся шестью шестнадцатеричными цифрами без решётки (например FFCC00)");
        }

        // -------- горячая клавиша
        if (control.Type == SettingsControlType.HotKey)
        {
            IReadOnlyList<int> codes = control.HotKeyCodes;

            if (codes.Count == 0)
            {
                warnings.Add("клавиша не назначена: checkKeyset на пустом списке всегда вернёт False");
            }

            foreach (int code in codes)
            {
                if (SettingsHotKey.ExcludedKeys.Contains(code))
                {
                    errors.Add($"клавишу {SettingsHotKey.LabelOf(code)} назначить нельзя: "
                               + "она в EXCLUDED_KEYS самого API");
                }
            }

            if (codes.Count == 1 && codes[0] == 1)
            {
                warnings.Add("Escape в окне настроек означает «отменить назначение»: "
                             + "игрок не сможет назначить эту клавишу заново");
            }

            if (codes.Count > 0)
            {
                foreach ((string linkage, string varName, IReadOnlyList<int> busy) in _live.BusyHotKeys)
                {
                    if (busy.Count == codes.Count && busy.OrderBy(c => c).SequenceEqual(codes.OrderBy(c => c)))
                    {
                        warnings.Add($"это сочетание уже занято установленным модом "
                                     + $"{linkage} ({varName}) — проверено по modsettings.dat");
                        break;
                    }
                }
            }
        }

        // -------- значение вообще
        if (control.Type is SettingsControlType.Slider or SettingsControlType.NumericStepper
            && control.NumberText.Trim().Length == 0)
        {
            errors.Add("не задано значение по умолчанию");
        }
    }

    private IEnumerable<SettingControlViewModel> AllControls() => Column1.Concat(Column2);

    private IEnumerable<(SettingControlViewModel Control, int Column)> AllControlsWithColumn() =>
        Column1.Select(c => (c, 1)).Concat(Column2.Select(c => (c, 2)));

    private int? ParseVersion() =>
        int.TryParse(SettingsVersionText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;

    private string UniqueVarName(SettingsControlType type, string? preferred = null)
    {
        string prefix = string.IsNullOrWhiteSpace(preferred)
            ? type switch
            {
                SettingsControlType.CheckBox => "option",
                SettingsControlType.RadioButtonGroup => "mode",
                SettingsControlType.Dropdown => "choice",
                SettingsControlType.Slider => "level",
                SettingsControlType.TextInput => "text",
                SettingsControlType.NumericStepper => "count",
                SettingsControlType.HotKey => "hotKey",
                SettingsControlType.ColorChoice => "color",
                SettingsControlType.RangeSlider => "range",
                _ => "value"
            }
            : preferred!;

        var busy = new HashSet<string>(
            AllControls().Select(c => c.VarName.Trim()),
            StringComparer.Ordinal);

        if (!busy.Contains(prefix))
        {
            return prefix;
        }

        for (int i = 2; i < 1000; i++)
        {
            string candidate = prefix + i.ToString(CultureInfo.InvariantCulture);
            if (!busy.Contains(candidate))
            {
                return candidate;
            }
        }

        return prefix + Guid.NewGuid().ToString("N")[..6];
    }

    // =================================================================== снимок формы

    /// <summary>Собирает неизменяемый снимок формы для генератора и для показа JSON.</summary>
    /// <returns>Форма.</returns>
    public SettingsFormSpec ToSpec() => new()
    {
        Linkage = Linkage.Trim(),
        ModDisplayName = ModDisplayName.Trim(),
        SettingsVersion = ParseVersion() ?? 1,
        HasEnabled = HasEnabled,
        EnabledDefault = EnabledDefault,
        Column1 = Column1.Select(c => c.ToSpec()).ToList(),
        Column2 = Column2.Select(c => c.ToSpec()).ToList()
    };

    /// <summary>Заполняет экран из снимка: так работают импорт и открытие проекта.</summary>
    /// <param name="form">Форма.</param>
    public void LoadSpec(SettingsFormSpec form)
    {
        ArgumentNullException.ThrowIfNull(form);

        _loading = true;
        try
        {
            Linkage = form.Linkage;
            ModDisplayName = form.ModDisplayName;
            SettingsVersionText = form.SettingsVersion.ToString(CultureInfo.InvariantCulture);
            HasEnabled = form.HasEnabled;
            EnabledDefault = form.EnabledDefault;

            Column1.Clear();
            Column2.Clear();

            Fill(Column1, form.Column1);
            Fill(Column2, form.Column2);

            Selected = Column1.FirstOrDefault() ?? Column2.FirstOrDefault();
        }
        finally
        {
            _loading = false;
        }

        _generatedVarNames = form.AllControls
            .Where(c => SettingsControlSchema.HasValue(c.Type))
            .Select(c => c.VarName)
            .ToList();
        _generatedVersion = form.SettingsVersion;

        OnFormChanged();

        void Fill(ObservableCollection<SettingControlViewModel> target, IReadOnlyList<SettingsControlSpec> specs)
        {
            foreach (SettingsControlSpec spec in specs)
            {
                var control = new SettingControlViewModel(spec.Type, OnFormChanged);
                control.Load(spec, KnownKeys);
                target.Add(control);
            }
        }
    }

    // =================================================================== генерация

    /// <summary>
    /// Собрать код и показать его. НИЧЕГО не пишет на диск: запись — отдельная кнопка
    /// с подтверждением и явными путями.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private void GenerateCode()
    {
        SettingsCodeResult result;
        try
        {
            result = _backend.Generate(ToSpec(), BuildRequest());
        }
        catch (Exception ex)
        {
            StatusText = "генератор: " + Short(ex);
            return;
        }

        if (result.Error is not null)
        {
            StatusText = "код не собран: " + result.Error;
            return;
        }

        Artifacts.Clear();
        foreach (SettingsCodeFile file in result.Files)
        {
            Artifacts.Add(new GeneratedArtifactViewModel(file, FullPath(file)));
        }

        Artifacts.Add(new GeneratedArtifactViewModel(
            new SettingsCodeFile("шаблон (JSON)", TemplateJson(), SettingsFileKind.Design)
            {
                IsSnippet = true,
                Note = "так шаблон выглядит в modsettings.dat; в проект не пишется"
            },
            string.Empty));

        SelectedArtifact = Artifacts.FirstOrDefault(a => a.IsPython) ?? Artifacts.FirstOrDefault();
        ShowCode = true;

        // Снимок формы: по нему видно, что предпросмотр перестал отвечать экрану.
        _generatedSnapshot = FormSnapshot();
        IsPreviewStale = false;

        // Кнопка записи включается только после того, как есть ЧТО записывать: команда
        // сама об этом не узнает — список файлов ей не свойство, а коллекция.
        WriteFilesCommand.NotifyCanExecuteChanged();

        StatusText = $"код собран ({result.GeneratorName}); файлов: {result.Files.Count}. "
                     + "Ничего не записано — нажмите «записать в проект»";
    }

    /// <summary>Показать шаблон в том виде, в каком он уедет в состояние API.</summary>
    [RelayCommand]
    private void ShowTemplateJson()
    {
        var artifact = new GeneratedArtifactViewModel(
            new SettingsCodeFile("шаблон (JSON)", TemplateJson(), SettingsFileKind.Design)
            {
                IsSnippet = true,
                Note = "так шаблон выглядит в секции templates файла modsettings.dat"
            },
            string.Empty);

        GeneratedArtifactViewModel? existing = Artifacts.FirstOrDefault(a => a.Title == artifact.Title);
        if (existing is not null)
        {
            Artifacts.Remove(existing);
        }

        Artifacts.Add(artifact);
        SelectedArtifact = artifact;
        ShowCode = true;

        WriteFilesCommand.NotifyCanExecuteChanged();
        StatusText = "показан JSON шаблона";
    }

    private string TemplateJson()
    {
        try
        {
            string json = _backend.RenderTemplateJson(ToSpec());
            return json.Length > 0 ? json : SettingsTemplateJson.Render(ToSpec());
        }
        catch (Exception)
        {
            return SettingsTemplateJson.Render(ToSpec());
        }
    }

    /// <summary>
    /// Записывать можно только то, что СЕЙЧАС видно на экране и собрано без ошибок.
    /// <para>Три условия, и каждое закрывает свою дыру: без первого нечего писать; без
    /// второго кнопка оставалась бы активной при ошибке в форме (генерация уже погашена,
    /// а запись — нет) и клала бы на диск старый код; без третьего — тот же старый код
    /// после любой правки, не создавшей ошибки.</para>
    /// </summary>
    private bool CanWriteFiles() =>
        Artifacts.Any(a => a.FullPath.Length > 0) && ErrorCount == 0 && !IsPreviewStale;

    /// <summary>Записать собранные файлы в проект.</summary>
    [RelayCommand(CanExecute = nameof(CanWriteFiles))]
    private void WriteFiles()
    {
        var targets = Artifacts.Where(a => a.FullPath.Length > 0).ToList();
        if (targets.Count == 0 || _project is null)
        {
            return;
        }

        var plan = new StringBuilder();
        foreach (GeneratedArtifactViewModel artifact in targets)
        {
            plan.Append("    ").Append(artifact.FullPath).Append(Environment.NewLine)
                .Append("        ").Append(artifact.Action).Append(" · ").Append(artifact.Size)
                .Append(Environment.NewLine);
        }

        if (!ConfirmHandler(
                "Записать настройки в проект",
                $"В проект будет записано файлов: {targets.Count}." + Environment.NewLine + Environment.NewLine +
                plan + Environment.NewLine +
                "Существующие файлы кокпит копирует рядом с расширением .bak." + Environment.NewLine +
                "settings.py, если он уже есть, не трогается вовсе: это ваш файл." + Environment.NewLine +
                Environment.NewLine +
                "Продолжить?"))
        {
            StatusText = "запись отменена";
            return;
        }

        int written = 0;
        int skipped = 0;

        foreach (GeneratedArtifactViewModel artifact in targets)
        {
            try
            {
                if (artifact.File.Kind == SettingsFileKind.CreateOnce && File.Exists(artifact.FullPath))
                {
                    skipped++;
                    continue;
                }

                string? dir = Path.GetDirectoryName(artifact.FullPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                if (File.Exists(artifact.FullPath))
                {
                    File.Copy(artifact.FullPath, artifact.FullPath + ".bak", overwrite: true);
                }

                // UTF-8 БЕЗ BOM и с переводами строк \n: ровно так кокпит пишет питон мода,
                // и ровно так его читают tools/check_py2_syntax.py и компилятор байткода.
                File.WriteAllText(
                    artifact.FullPath,
                    artifact.Content.Replace("\r\n", "\n", StringComparison.Ordinal),
                    SettingsDesignFile.Utf8NoBom());

                written++;
            }
            catch (Exception ex)
            {
                StatusText = "запись не удалась: " + Short(ex);
                return;
            }
        }

        _generatedVarNames = AllControls()
            .Where(c => SettingsControlSchema.HasValue(c.Type))
            .Select(c => c.VarName.Trim())
            .ToList();
        _generatedVersion = ParseVersion() ?? 1;

        Revalidate();

        StatusText = $"записано файлов: {written}"
                     + (skipped > 0 ? $", оставлено как есть: {skipped}" : string.Empty)
                     + ". Не забудьте позвать settings.register() из onAccountShowGUI";
    }

    private bool CanOpenArtifact() => SelectedArtifact is { FullPath.Length: > 0 } && _editor.IsAvailable;

    /// <summary>Открыть выбранный файл во внешнем редакторе.</summary>
    [RelayCommand(CanExecute = nameof(CanOpenArtifact))]
    private void OpenArtifact()
    {
        GeneratedArtifactViewModel? artifact = SelectedArtifact;
        if (artifact is null || artifact.FullPath.Length == 0)
        {
            return;
        }

        try
        {
            _editor.OpenAt(artifact.FullPath, 1);
        }
        catch (Exception ex)
        {
            StatusText = "редактор: " + Short(ex);
        }
    }

    /// <summary>Скопировать показанный текст в буфер обмена.</summary>
    [RelayCommand]
    private void CopyArtifact()
    {
        GeneratedArtifactViewModel? artifact = SelectedArtifact;
        if (artifact is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(artifact.Content);
            StatusText = artifact.Title + " скопирован в буфер обмена";
        }
        catch (Exception ex)
        {
            StatusText = "буфер обмена: " + Short(ex);
        }
    }

    partial void OnSelectedArtifactChanged(GeneratedArtifactViewModel? value)
    {
        OpenArtifactCommand.NotifyCanExecuteChanged();
    }

    private SettingsCodeRequest BuildRequest()
    {
        ModProject? project = _project;
        string package = project?.PackageName ?? string.Empty;

        return new SettingsCodeRequest
        {
            ProjectDir = project?.ProjectDir ?? string.Empty,
            SrcDirRelative = project?.SrcDirRelative ?? "src",
            PackageName = package,
            LogTag = project?.LogTag ?? string.Empty,
            ModId = project?.ModId ?? string.Empty,
            ConfigRelativePath = SettingsValuesPath(project, package),
            GameConfigRelative = project?.GameConfigRelative ?? string.Empty,
            CreateUserFile = true
        };
    }

    /// <summary>
    /// Путь к файлу ЗНАЧЕНИЙ настроек относительно корня игры.
    ///
    /// <para>Каталог берётся тот же, что у конфига мода из рецепта 09
    /// (<see cref="ModProject.GameConfigRelative"/>), а ФАЙЛ — обязательно другой. Раньше сюда
    /// подставлялся сам <c>GameConfigRelative</c>, и это была не мелочь: сгенерированный
    /// <c>settings.py</c> в <c>_save()</c> пишет <c>json.dumps(_values)</c> — то есть
    /// ПЕРЕЗАПИСЫВАЕТ файл целиком при каждом «Применить» игрока. Указав туда
    /// <c>config.json</c>, кокпит своими руками стирал бы у игрока конфиг мода со всеми
    /// ключами, которые туда положил рецепт 09. Ровно это и запрещает
    /// <c>SettingsDesignerService.Apply</c>, отказываясь генерировать при совпадении путей;
    /// экран шёл мимо той проверки, потому что зовёт генератор через шов.</para>
    /// </summary>
    /// <param name="project">Проект мода; <c>null</c> — проект не выбран.</param>
    /// <param name="package">Имя python-пакета мода.</param>
    /// <returns>Относительный путь прямыми слэшами либо пустая строка.</returns>
    private static string SettingsValuesPath(ModProject? project, string package)
    {
        const string fileName = "settings.json";

        string fallback = package.Length > 0 ? $"mods/configs/{package}/{fileName}" : string.Empty;
        string config = (project?.GameConfigRelative ?? string.Empty).Trim().Replace('\\', '/');

        int slash = config.LastIndexOf('/');
        string candidate = slash > 0 ? string.Concat(config.AsSpan(0, slash), "/", fileName) : fallback;

        if (candidate.Length == 0)
        {
            return string.Empty;
        }

        // Пользователь мог назвать конфиг мода тем же именем. Тогда отходим ещё на шаг:
        // общий файл на двоих означал бы потерю чужих ключей при каждом «Применить».
        return string.Equals(candidate, config, StringComparison.OrdinalIgnoreCase)
            ? candidate[..^fileName.Length] + "settings.values.json"
            : candidate;
    }

    private string FullPath(SettingsCodeFile file)
    {
        if (_project is null || file.IsSnippet || file.RelativePath.Contains('(', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return Path.Combine(_project.ProjectDir, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    // =================================================================== импорт

    /// <summary>Прочитать форму из проекта (файл дизайнера либо код мода).</summary>
    [RelayCommand]
    private void ImportFromProject() => TryLoadDesign(silent: false);

    private void TryLoadDesign(bool silent)
    {
        if (_project is null)
        {
            if (!silent)
            {
                StatusText = "проект не выбран";
            }

            return;
        }

        SettingsImportResult result;
        try
        {
            result = _backend.ImportFromProject(_project.ProjectDir);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                StatusText = "импорт: " + Short(ex);
            }

            return;
        }

        if (result.Error is not null || result.Form is null)
        {
            if (!silent)
            {
                StatusText = "импорт не удался: " + (result.Error ?? "форма не прочитана");
            }

            return;
        }

        LoadSpec(result.Form);

        StatusText = "форма прочитана из " + (result.Source.Length > 0 ? result.Source : "проекта")
                     + (result.Notes.Count > 0 ? " · " + string.Join(" · ", result.Notes) : string.Empty);
    }

    /// <summary>Открыть панель импорта из живого состояния ModsSettingsAPI.</summary>
    [RelayCommand]
    private void OpenLiveImport()
    {
        ReadLiveState(silent: false);
        ShowImport = true;
    }

    private void ReadLiveState(bool silent)
    {
        bool ok;
        try
        {
            ok = _live.Read(LiveStatePathOverride);
        }
        catch (Exception ex)
        {
            LiveError = Short(ex);
            return;
        }

        LivePath = _live.FilePath;
        LiveError = ok ? string.Empty : _live.Error;

        LiveTemplates.Clear();
        foreach (LiveModTemplate template in _live.Templates)
        {
            LiveTemplates.Add(template);
        }

        if (!silent)
        {
            StatusText = ok
                ? $"прочитано шаблонов: {LiveTemplates.Count} (только чтение)"
                : "живое состояние не прочитано: " + LiveError;
        }
    }

    private bool CanImportLive() => SelectedLiveTemplate is not null;

    /// <summary>Загрузить в дизайнер выбранный чужой шаблон.</summary>
    [RelayCommand(CanExecute = nameof(CanImportLive))]
    private void ImportFromLive()
    {
        LiveModTemplate? template = SelectedLiveTemplate;
        if (template is null)
        {
            return;
        }

        if (!ConfirmHandler(
                "Загрузить чужой шаблон",
                $"Форма дизайнера будет заменена шаблоном мода «{template.DisplayName}»." + Environment.NewLine +
                Environment.NewLine +
                "Это НАСТРОЙКИ ЧУЖОГО МОДА из профиля игрока. Кокпит открыл файл на чтение" + Environment.NewLine +
                "и ничего в нём не менял и не изменит." + Environment.NewLine + Environment.NewLine +
                "Загрузить как образец?"))
        {
            return;
        }

        SettingsImportResult result = _live.ToForm(template);

        if (result.Error is not null || result.Form is null)
        {
            StatusText = "шаблон не загружен: " + (result.Error ?? "пусто");
            return;
        }

        LoadSpec(result.Form);
        ShowImport = false;

        StatusText = "загружен чужой шаблон " + template.Linkage
                     + (result.Notes.Count > 0 ? " · " + string.Join(" · ", result.Notes) : string.Empty);
    }

    partial void OnSelectedLiveTemplateChanged(LiveModTemplate? value)
    {
        ImportFromLiveCommand.NotifyCanExecuteChanged();
    }

    // =================================================================== служебное

    /// <summary>
    /// Диалог подтверждения. Свойством — чтобы тест мог подставить свой обработчик,
    /// как это сделано в <see cref="MainViewModel.ConfirmHandler"/>.
    /// </summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    private static bool DefaultConfirm(string caption, string body)
        => MessageBox.Show(
               body,
               caption,
               MessageBoxButton.YesNo,
               MessageBoxImage.Question,
               MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _projectDebounce.Stop();
        _projectDebounce.Tick -= OnProjectDebounceTick;
        _main.PropertyChanged -= OnMainPropertyChanged;
    }
}
