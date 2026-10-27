using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Всё, что диалогу «Собрать сетап» нужно снаружи. Отдельной записью, чтобы модель диалога
/// проверялась тестами без окна, без build.py и без настоящей заглушки.
/// </summary>
public sealed record SetupBundleContext
{
    /// <summary>Заглушка установщика; <c>null</c> — не найдена, собирать нечем.</summary>
    public string? StubPath { get; init; }

    /// <summary>Версия клиента, под которую собираются моды (из paths.xml); пусто — неизвестна.</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>Корень игры: класть сетап внутрь него нельзя.</summary>
    public string? GameRoot { get; init; }

    /// <summary>Собрать проект build.py без установки в игру — <see cref="MainViewModel.BuildForModsAsync"/>.</summary>
    public required Func<string, Task<ModsBuildOutcome>> BuildProject { get; init; }

    /// <summary>Перечитать проекты после пересборки.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyList<SetupBundleCandidate>> ReadCandidates { get; init; } =
        SetupBundleCandidates.Read;

    /// <summary>Упаковать сетап. Зовётся в фоновом потоке.</summary>
    public Func<SetupBundleRequest, SetupBundleResult> Pack { get; init; } = r => SetupBundleBuilder.Build(r);

    /// <summary>Спросить «да/нет»: заголовок, текст.</summary>
    public required Func<string, string, bool> Confirm { get; init; }

    /// <summary>Выбрать папку: начальная папка → выбранная либо <c>null</c>.</summary>
    public Func<string, string?> PickFolder { get; init; } = _ => null;

    /// <summary>Показать файл в проводнике.</summary>
    public Action<string> Reveal { get; init; } = _ => { };

    /// <summary>Прошлый выбор.</summary>
    public SetupBundleSettings Settings { get; init; } = new();

    /// <summary>Часы — подменяются в тестах.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>
/// Диалог «Собрать сетап»: какие свои моды упаковать в один <c>Setup.exe</c>, как назвать
/// сборку и куда положить файл.
/// <para>
/// Галочки живут ТОЛЬКО здесь. На самом экране «Моды» их нет намеренно: там лежат чужие моды
/// пользователя, и массовые операции над ними опасны. Здесь же список — только свои проекты, а
/// итог — файл в папке сетапов: в каталог игры диалог не пишет ничего. Пересборка идёт тем же
/// build.py без <c>--install</c>, что и «Собрать и установить».
/// </para>
/// </summary>
public sealed partial class SetupBundleViewModel : ObservableObject
{
    private readonly SetupBundleContext _context;
    private bool _bulkChange;

    /// <summary>Создаёт модель диалога.</summary>
    /// <param name="candidates">Свои проекты (<see cref="SetupBundleCandidates.Read"/>).</param>
    /// <param name="context">Связь с кокпитом.</param>
    public SetupBundleViewModel(IReadOnlyList<SetupBundleCandidate> candidates, SetupBundleContext context)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        _context = context ?? throw new ArgumentNullException(nameof(context));

        SetupBundleSettings s = context.Settings ?? new SetupBundleSettings();
        var selected = new HashSet<string>(s.SelectedModIds ?? [], StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(s.KnownModIds ?? [], StringComparer.OrdinalIgnoreCase);

        foreach (SetupBundleCandidate c in candidates)
        {
            // Новый проект (его не было в прошлый раз) отмечается сам; снятую галочку помним.
            bool isChecked = selected.Contains(c.ModId) || !known.Contains(c.ModId);
            var row = new SetupComponentViewModel(c, isChecked);
            row.PropertyChanged += OnRowChanged;
            Components.Add(row);
        }

        _bundleName = string.IsNullOrWhiteSpace(s.Name) ? SetupBundleSettings.DefaultName : s.Name.Trim();
        _bundleVersion = SetupBundleBuilder.DefaultVersion(context.Now());
        _outputDir = string.IsNullOrWhiteSpace(s.OutputDir) ? SetupBundleSettings.DefaultOutputDir() : s.OutputDir.Trim();
        _rebuild = s.Rebuild;
    }

    /// <summary>Закрыть окно.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Свои проекты с галочками.</summary>
    public ObservableCollection<SetupComponentViewModel> Components { get; } = new();

    /// <summary>Ход сборки — свежие строки внизу.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Имя сборки — заголовок окна сетапа.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Problem), nameof(CanBuild), nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private string _bundleName;

    /// <summary>Версия сборки, по умолчанию сегодняшняя дата.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Problem), nameof(CanBuild), nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private string _bundleVersion;

    /// <summary>Папка, куда ляжет сетап.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputPath), nameof(Problem), nameof(CanBuild), nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private string _outputDir;

    /// <summary>Пересобрать выбранные проекты перед упаковкой.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Problem), nameof(CanBuild), nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand))]
    private bool _rebuild;

    /// <summary>Идёт сборка или упаковка: всё остальное в окне заблокировано.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBuild), nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(BuildCommand), nameof(CloseCommand), nameof(SelectAllCommand), nameof(SelectNoneCommand), nameof(BrowseOutputCommand))]
    private bool _isWorking;

    /// <summary>Что сейчас происходит или чем кончилось.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Итог упаковки прошёл успешно.</summary>
    [ObservableProperty]
    private bool _lastRunSucceeded;

    /// <summary>Готовый сетап; <c>null</c> — ещё не собран.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult), nameof(Warning))]
    [NotifyCanExecuteChangedFor(nameof(OpenFolderCommand))]
    private string? _resultPath;

    /// <summary>Сборка не идёт — поля можно править.</summary>
    public bool IsIdle => !IsWorking;

    /// <summary>Сетап собран в этом окне.</summary>
    public bool HasResult => ResultPath is not null;

    /// <summary>Сколько модов отмечено.</summary>
    public int SelectedCount => Components.Count(c => c.IsChecked);

    /// <summary>«выбрано 5 из 9».</summary>
    public string SelectionSummary => $"выбрано {SelectedCount} из {Components.Count}";

    /// <summary>Полный путь будущего сетапа.</summary>
    public string OutputPath
    {
        get
        {
            try
            {
                return Path.Combine(OutputDir?.Trim() ?? string.Empty, SetupBundleBuilder.DefaultFileName(BundleName, BundleVersion));
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }

    /// <summary>Что мешает собрать; пустая строка — ничего.</summary>
    public string Problem
    {
        get
        {
            if (_context.StubPath is null)
            {
                return "Заглушка установщика не найдена рядом с кокпитом (setup\\WotModCockpit.Setup.exe) — пересоберите кокпит.";
            }

            if (Components.Count == 0)
            {
                return "Своих проектов не найдено: кокпит ищет их рядом с открытыми и недавними проектами.";
            }

            if (SelectedCount == 0)
            {
                return "Отметьте хотя бы один мод.";
            }

            if ((SetupBundleBuilder.ValidateName(BundleName) ?? SetupBundleBuilder.ValidateVersion(BundleVersion)) is { } bad)
            {
                return bad;
            }

            if (string.IsNullOrWhiteSpace(OutputDir) || !Path.IsPathFullyQualified(OutputDir.Trim()))
            {
                return "Укажите полный путь к папке, куда положить сетап.";
            }

            if (IsInsideGame(OutputDir))
            {
                return "Сетап нельзя класть в папку игры: кокпит туда не пишет. Выберите другую папку.";
            }

            if (Components.Where(c => c.IsChecked).GroupBy(c => c.ModId, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1) is { } twin)
            {
                return $"Мод {twin.Key} отмечен в двух проектах — оставьте один.";
            }

            if (!Rebuild && Components.FirstOrDefault(c => c.IsChecked && !c.HasBuild) is { } unbuilt)
            {
                return $"У «{unbuilt.Title}» нет сборки в dist — включите «Пересобрать» или снимите галочку.";
            }

            return string.Empty;
        }
    }

    /// <summary>Что стоит знать, но собрать не мешает.</summary>
    public string Warning
    {
        get
        {
            if (!Rebuild)
            {
                string[] stale = Components.Where(c => c.IsChecked && c.IsStale).Select(c => "«" + c.Title + "»").ToArray();
                if (stale.Length > 0)
                {
                    return "Сборка старше правок в исходниках: " + string.Join(", ", stale) +
                           ". В сетап уйдёт старая версия — включите «Пересобрать».";
                }
            }

            // Только что собранный здесь же файл — не повод для предупреждения: об итоге уже
            // сказала строка «Готово».
            string output = OutputPath;
            bool justBuilt = ResultPath is { } built && string.Equals(built, output, StringComparison.OrdinalIgnoreCase);
            return !justBuilt && File.Exists(output)
                ? "Такой файл уже есть — перед записью кокпит спросит, заменить ли его."
                : string.Empty;
        }
    }

    /// <summary>Можно собирать.</summary>
    public bool CanBuild => !IsWorking && Problem.Length == 0;

    /// <summary>Снимок выбора для настроек — сохраняется при закрытии окна.</summary>
    /// <returns>Настройки диалога.</returns>
    public SetupBundleSettings CurrentSettings()
    {
        SetupBundleSettings previous = _context.Settings ?? new SetupBundleSettings();
        var known = new HashSet<string>(previous.KnownModIds ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (SetupComponentViewModel c in Components)
        {
            known.Add(c.ModId);
        }

        return new SetupBundleSettings
        {
            Name = string.IsNullOrWhiteSpace(BundleName) ? SetupBundleSettings.DefaultName : BundleName.Trim(),
            OutputDir = OutputDir?.Trim() ?? string.Empty,
            Rebuild = Rebuild,
            SelectedModIds = Components.Where(c => c.IsChecked).Select(c => c.ModId).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            KnownModIds = known.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    // ================================================================= команды

    /// <summary>Отметить все.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectAll() => SetAll(true);

    /// <summary>Снять все.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void SelectNone() => SetAll(false);

    /// <summary>Выбрать папку для сетапа.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void BrowseOutput()
    {
        string? picked = _context.PickFolder(OutputDir);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            OutputDir = picked;
        }
    }

    /// <summary>Показать готовый сетап в проводнике.</summary>
    [RelayCommand(CanExecute = nameof(HasResult))]
    private void OpenFolder()
    {
        if (ResultPath is { } path)
        {
            _context.Reveal(path);
        }
    }

    /// <summary>Закрыть окно. Посреди сборки нельзя: build.py и упаковку не бросают на полпути.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Собрать сетап: при необходимости пересобрать выбранные проекты, упаковать, проверить
    /// чтением. Первая же неудача останавливает всё — полусетап хуже никакого.
    /// </summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanBuild))]
    private async Task BuildAsync()
    {
        if (!CanBuild)
        {
            return;
        }

        List<SetupComponentViewModel> selected = Components.Where(c => c.IsChecked).ToList();
        string output = OutputPath;
        bool overwrite = false;

        if (File.Exists(output))
        {
            if (!_context.Confirm("Собрать сетап", $"Файл уже есть:\n\n{output}\n\nЗаменить его новой сборкой?"))
            {
                StatusText = "не собрано: файл с таким именем уже есть — поменяйте версию или папку";
                return;
            }

            overwrite = true;
        }

        IsWorking = true;
        LastRunSucceeded = false;
        ResultPath = null;
        Log.Clear();

        try
        {
            List<string> files = Rebuild ? await RebuildAsync(selected).ConfigureAwait(true) : TakeBuilt(selected);
            if (files.Count != selected.Count)
            {
                return;
            }

            StatusText = "упаковка…";
            var request = new SetupBundleRequest
            {
                StubPath = _context.StubPath!,
                OutputPath = output,
                Name = BundleName.Trim(),
                Version = BundleVersion.Trim(),
                GameVersion = _context.GameVersion,
                ModFiles = files,
                Overwrite = overwrite,
            };

            SetupBundleResult result = await Task.Run(() => _context.Pack(request)).ConfigureAwait(true);

            foreach (SetupBundleComponent c in result.Manifest.Components)
            {
                AddLog($"в сетапе: {c.Name} {c.Version} — {c.FileName}");
            }

            ResultPath = result.OutputPath;
            LastRunSucceeded = true;
            int n = result.Manifest.Components.Count;
            StatusText = $"Готово: {Path.GetFileName(result.OutputPath)} — {FormatSize(result.SizeBytes)}, {n} {Plural(n, "мод", "мода", "модов")}. " +
                         "Файл перечитан и сверен по контрольным суммам.";
            AddLog("сетап: " + result.OutputPath);
        }
        catch (SetupBundleException ex)
        {
            Fail("упаковка не удалась: " + ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail("упаковка не удалась: " + ex.Message);
        }
        finally
        {
            IsWorking = false;
            RefreshCandidates();
            OnPropertyChanged(nameof(Warning));
        }
    }

    /// <summary>Пересобирает выбранные проекты по одному; первая неудача останавливает всё.</summary>
    /// <returns>Собранные файлы; меньше выбранного — значит, остановились.</returns>
    private async Task<List<string>> RebuildAsync(List<SetupComponentViewModel> selected)
    {
        var files = new List<string>();
        for (int i = 0; i < selected.Count; i++)
        {
            SetupComponentViewModel c = selected[i];
            StatusText = $"сборка {i + 1} из {selected.Count}: «{c.Title}»… вывод build.py — на экране «Сборка и лог»";

            ModsBuildOutcome outcome;
            try
            {
                outcome = await _context.BuildProject(c.ProjectDir).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Fail($"«{c.Title}»: сборка не запустилась — {ex.Message}");
                return files;
            }

            if (!outcome.Started)
            {
                Fail($"«{c.Title}»: сборка не запустилась — {outcome.Message}");
                return files;
            }

            if (!outcome.Success)
            {
                Fail(outcome.Cancelled
                    ? $"«{c.Title}»: сборка отменена — сетап не собран"
                    : $"«{c.Title}»: сборка не удалась — {outcome.Message}. Вывод build.py — на экране «Сборка и лог».");
                return files;
            }

            // Путь — только из вывода build.py: угаданное имя могло бы упаковать старый файл из dist.
            string artifact = outcome.Result!.ArtifactPath;
            if (artifact.Length == 0 || !File.Exists(artifact))
            {
                Fail($"«{c.Title}»: build.py не сообщил, какой файл собран, — что упаковывать, неизвестно");
                return files;
            }

            files.Add(artifact);
            AddLog($"собран «{c.Title}»: {Path.GetFileName(artifact)} за {outcome.Result.Duration.TotalSeconds:0.0} с");
        }

        return files;
    }

    /// <summary>Берёт готовые сборки из dist без пересборки.</summary>
    /// <returns>Файлы; меньше выбранного — значит, у кого-то сборки нет.</returns>
    private List<string> TakeBuilt(List<SetupComponentViewModel> selected)
    {
        var files = new List<string>();
        foreach (SetupComponentViewModel c in selected)
        {
            string? built = c.Candidate.Project.BuiltArtifactPath;
            if (built is null || !File.Exists(built))
            {
                Fail($"«{c.Title}»: сборки в dist нет — включите «Пересобрать»");
                return files;
            }

            files.Add(built);
            AddLog($"готовая сборка «{c.Title}»: {Path.GetFileName(built)}" + (c.IsStale ? " (старше правок в исходниках)" : string.Empty));
        }

        return files;
    }

    /// <summary>После сборки dist поменялся — строки списка показывают свежее время сборки.</summary>
    private void RefreshCandidates()
    {
        IReadOnlyList<SetupBundleCandidate> fresh;
        try
        {
            fresh = _context.ReadCandidates(Components.Select(c => c.ProjectDir).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (SetupComponentViewModel row in Components)
        {
            SetupBundleCandidate? match = fresh.FirstOrDefault(
                c => string.Equals(c.Project.ProjectDir, row.ProjectDir, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                row.Update(match);
            }
        }

        NotifySelectionChanged();
    }

    private void Fail(string message)
    {
        LastRunSucceeded = false;
        StatusText = message;
        AddLog("ОШИБКА: " + message);
    }

    private void AddLog(string line) => Log.Add($"{_context.Now():HH:mm:ss}  {line}");

    private void SetAll(bool value)
    {
        _bulkChange = true;
        try
        {
            foreach (SetupComponentViewModel c in Components)
            {
                c.IsChecked = value;
            }
        }
        finally
        {
            _bulkChange = false;
        }

        NotifySelectionChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulkChange && e.PropertyName == nameof(SetupComponentViewModel.IsChecked))
        {
            NotifySelectionChanged();
        }
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(Problem));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(CanBuild));
        BuildCommand.NotifyCanExecuteChanged();
    }

    private bool IsInsideGame(string dir)
    {
        if (string.IsNullOrWhiteSpace(_context.GameRoot))
        {
            return false;
        }

        try
        {
            string game = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_context.GameRoot)) + Path.DirectorySeparatorChar;
            string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir.Trim())) + Path.DirectorySeparatorChar;
            return target.StartsWith(game, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string FormatSize(long bytes) => bytes >= 1024 * 1024
        ? (bytes / 1024d / 1024d).ToString("0.0", CultureInfo.GetCultureInfo("ru-RU")) + " МБ"
        : (bytes / 1024d).ToString("0", CultureInfo.GetCultureInfo("ru-RU")) + " КБ";

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
            _ => many,
        };
    }
}

/// <summary>Строка списка в диалоге «Собрать сетап»: свой проект и галочка.</summary>
public sealed partial class SetupComponentViewModel : ObservableObject
{
    /// <summary>Создаёт строку.</summary>
    /// <param name="candidate">Проект.</param>
    /// <param name="isChecked">Отмечен ли.</param>
    public SetupComponentViewModel(SetupBundleCandidate candidate, bool isChecked)
    {
        _candidate = candidate ?? throw new ArgumentNullException(nameof(candidate));
        _isChecked = isChecked;
    }

    /// <summary>Отмечен для сетапа.</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Проект.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Version), nameof(BuildState), nameof(HasBuild), nameof(IsStale), nameof(IsWarning), nameof(Description))]
    private SetupBundleCandidate _candidate;

    /// <summary><c>&lt;id&gt;</c> мода.</summary>
    public string ModId => Candidate.ModId;

    /// <summary>Каталог проекта.</summary>
    public string ProjectDir => Candidate.Project.ProjectDir;

    /// <summary>Имя мода.</summary>
    public string Title => Candidate.Project.DisplayName;

    /// <summary>Версия из meta.xml проекта.</summary>
    public string Version => Candidate.Project.Version;

    /// <summary>Описание из meta.xml проекта.</summary>
    public string Description => Candidate.Project.Description;

    /// <summary>В dist есть сборка.</summary>
    public bool HasBuild => Candidate.HasBuild;

    /// <summary>Сборка старше правок в исходниках.</summary>
    public bool IsStale => Candidate.IsStale;

    /// <summary>Строку стоит подсветить.</summary>
    public bool IsWarning => !HasBuild || IsStale;

    /// <summary>Что со сборкой.</summary>
    public string BuildState
    {
        get
        {
            if (!HasBuild)
            {
                return "нет сборки в dist";
            }

            string when = Candidate.Project.BuiltWriteTimeUtc is { } t
                ? t.ToLocalTime().ToString("dd.MM HH:mm", CultureInfo.InvariantCulture)
                : "?";
            return IsStale ? $"сборка {when} старше правок в src" : $"собран {when}";
        }
    }

    /// <summary>Подменяет проект свежим чтением с диска.</summary>
    /// <param name="fresh">Свежее описание.</param>
    internal void Update(SetupBundleCandidate fresh) => Candidate = fresh;
}
