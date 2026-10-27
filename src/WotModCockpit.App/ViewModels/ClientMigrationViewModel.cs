using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Всё, что окну перехода нужно снаружи. Отдельной записью, чтобы модель проверялась тестами
/// без игры, без декомпилятора и без build.py.
/// </summary>
public sealed record ClientMigrationContext
{
    /// <summary>Что осталось в прошлой версии.</summary>
    public required ClientMigrationState State { get; init; }

    /// <summary>Текущее дерево исходников из настроек (wotSrcDir).</summary>
    public string? CurrentTree { get; init; }

    /// <summary>Каталог шаблона мода — рядом с ним живут деревья исходников.</summary>
    public string? TemplateDir { get; init; }

    /// <summary>Извлечь исходники новой версии в каталог.</summary>
    public required Func<string, IProgress<ClientSourceProgress>, CancellationToken, Task<ClientSourceExtraction>> Extract { get; init; }

    /// <summary>Проверить моды: состояние, новое дерево, дерево для сравнения.</summary>
    public required Func<ClientMigrationState, string, string?, IProgress<string>, CancellationToken, Task<MigrationCheck>> Check { get; init; }

    /// <summary>
    /// Собрать проект build.py без установки (<see cref="MainViewModel.BuildForModsAsync"/>); отмена
    /// снимает build.py — иначе «Остановить» ждало бы конца сборки.
    /// </summary>
    public required Func<string, CancellationToken, Task<ModsBuildOutcome>> BuildProject { get; init; }

    /// <summary>Поставить собранный мод в каталог новой версии (служба библиотеки модов).</summary>
    public required Func<GameInstall, string, ModOperationResult> Install { get; init; }

    /// <summary>Переключить кокпит на новое дерево исходников.</summary>
    public required Func<string, Task> UseClientSources { get; init; }

    /// <summary>Записать итог установки в журнал операций экрана «Моды».</summary>
    public Action<ModOperationResult>? Journal { get; init; }

    /// <summary>Спросить да/нет (заголовок, текст); <c>null</c> — всегда «нет».</summary>
    public Func<string, string, bool>? Confirm { get; init; }

    /// <summary>Что лежит в dist проекта и не устарела ли сборка.</summary>
    public Func<string, SetupBundleCandidate?> ReadBuild { get; init; } =
        dir => SetupBundleCandidates.Read([dir]).FirstOrDefault();

    /// <summary>Часы — подменяются в тестах.</summary>
    public Func<DateTime> Now { get; init; } = () => DateTime.Now;
}

/// <summary>
/// Окно «Переход на новую версию клиента».
/// <para>
/// После обновления игры paths.xml смотрит в новый каталог модов, а свои моды остаются в
/// каталоге прошлой версии. Окно делает переход по шагам: исходники новой версии (готовые
/// или извлечённые из scripts.pkg), проверка каждого своего мода по ним, переключение кокпита
/// на новые исходники и перенос — автоматически только того, что проверку прошло, и только если
/// сравнивали с исходниками ИМЕННО прошлой версии. Без них (или с деревом неизвестной версии)
/// «совместим» не доказано: строки не отмечаются, перенос — вручную и с вопросом.
/// </para>
/// <para>
/// Безопасность — та же, что у всего экрана «Моды»: в игру пишет только служба библиотеки
/// (только <c>*.mtmod</c>, только в каталог модов, не при запущенной игре); старый каталог не
/// трогается вовсе — откатиться можно, просто вернув прошлую версию игры. Выключенные в прошлой
/// версии моды не переносятся: их выключал человек.
/// </para>
/// </summary>
public sealed partial class ClientMigrationViewModel : ObservableObject
{
    private readonly ClientMigrationContext _context;
    private CancellationTokenSource? _cts;

    /// <summary>Создаёт модель окна.</summary>
    /// <param name="context">Связь с кокпитом.</param>
    public ClientMigrationViewModel(ClientMigrationContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        ClientMigrationState s = context.State;
        foreach (InstalledMod mod in s.Pending)
        {
            Rows.Add(new MigrationRowViewModel(mod, wasDisabled: false, s.PreviousVersion ?? "?"));
        }

        foreach (InstalledMod mod in s.Disabled)
        {
            Rows.Add(new MigrationRowViewModel(mod, wasDisabled: true, s.PreviousVersion ?? "?"));
        }

        // Галочку в строке ставит человек — кнопка «Перенести отмеченные» обязана это увидеть.
        foreach (MigrationRowViewModel row in Rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
        }

        _statusText = Rows.Count == 0
            ? "Переносить нечего."
            : "Нажмите «Начать переход»: исходники новой версии, проверка модов и перенос того, что проверку прошло.";
    }

    /// <summary>Закрыть окно.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Новая версия клиента.</summary>
    public string NewVersion => _context.State.Game.Version;

    /// <summary>Прошлая версия, в которой остались моды.</summary>
    public string PreviousVersion => _context.State.PreviousVersion ?? "?";

    /// <summary>Шапка окна.</summary>
    public string Headline =>
        $"Клиент обновился: {PreviousVersion} → {NewVersion}. Свои моды остались в {_context.State.PreviousModsDir}, " +
        $"а игра читает {_context.State.Game.ModsDir}.";

    /// <summary>Свои моды прошлой версии.</summary>
    public ObservableCollection<MigrationRowViewModel> Rows { get; } = new();

    /// <summary>Ход перехода — свежие строки внизу.</summary>
    public ObservableCollection<string> Log { get; } = new();

    /// <summary>Переносить всё, что прошло проверку, без отдельной кнопки.</summary>
    [ObservableProperty]
    private bool _autoTransfer = true;

    /// <summary>Идёт работа.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(TransferSelectedCommand), nameof(CancelCommand), nameof(CloseCommand))]
    private bool _isWorking;

    /// <summary>Проверка выполнена — дальше можно переносить отмеченное.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(TransferSelectedCommand))]
    private bool _isChecked;

    /// <summary>Что сейчас делается.</summary>
    [ObservableProperty]
    private string _stageText = string.Empty;

    /// <summary>Доля сделанного, 0…1.</summary>
    [ObservableProperty]
    private double _progress;

    /// <summary>Сколько делать — неизвестно (полоса без доли).</summary>
    [ObservableProperty]
    private bool _isIndeterminate;

    /// <summary>Итог или подсказка.</summary>
    [ObservableProperty]
    private string _statusText;

    /// <summary>Дерево исходников новой версии, когда оно готово.</summary>
    [ObservableProperty]
    private string? _newTree;

    /// <summary>
    /// Сравнивали с исходниками именно прошлой версии. Без этого «совместим» не доказано, и
    /// автоматического переноса нет — только ручной, с вопросом.
    /// </summary>
    [ObservableProperty]
    private bool _baselineVerified;

    /// <summary>Работы нет.</summary>
    public bool IsIdle => !IsWorking;

    /// <summary>Сколько модов перенесено в этом окне.</summary>
    public int TransferredCount => Rows.Count(r => r.IsTransferred);

    // ================================================================= команды

    private bool CanStart() => !IsWorking && !IsChecked && Rows.Any(r => !r.WasDisabled);

    /// <summary>Исходники → проверка → переключение кокпита → автоматический перенос.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        IsWorking = true;
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;
        try
        {
            string tree = await EnsureSourcesAsync(ct).ConfigureAwait(true);
            NewTree = tree;

            MigrationCheck check = await CheckModsAsync(tree, ct).ConfigureAwait(true);

            if (check.NewTreeModules == 0 || !ClientSourceTrees.LooksLikeTree(tree))
            {
                // Пустое дерево — не замена рабочему: кокпит остаётся на прежних исходниках.
                AddLog($"кокпит НЕ переключён: в {tree} нет исходников клиента");
            }
            else
            {
                try
                {
                    await _context.UseClientSources(tree).ConfigureAwait(true);
                    AddLog($"кокпит переключён на исходники {NewVersion}: {tree}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Переносу это не мешает: моды уже проверены по новому дереву.
                    AddLog("кокпит НЕ переключён на новые исходники (укажите их в «Браузере API»): " + ex.Message);
                }
            }

            IsChecked = true;

            List<MigrationRowViewModel> compatible = Rows
                .Where(r => r.IsChecked && r.IsCompatible && r.IsVerified && r.CanTransfer).ToList();
            if (AutoTransfer && BaselineVerified && compatible.Count > 0)
            {
                await TransferAsync(compatible, ct).ConfigureAwait(true);
            }

            StatusText = Summary();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Переход отменён. Ничего не перенесено, кроме уже отмеченного в журнале.";
            AddLog("отменено");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Переход остановлен: " + ex.Message;
            AddLog("ОШИБКА: " + ex.Message);
        }
        finally
        {
            StageText = string.Empty;
            IsIndeterminate = false;
            IsWorking = false;
            _cts.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(TransferredCount));
        }
    }

    private bool CanTransferSelected() => !IsWorking && IsChecked && Rows.Any(r => r.IsChecked && r.CanTransfer);

    /// <summary>Перенести отмеченные вручную — в том числе те, что проверку не прошли.</summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanTransferSelected))]
    private async Task TransferSelectedAsync()
    {
        List<MigrationRowViewModel> rows = Rows.Where(r => r.IsChecked && r.CanTransfer).ToList();
        List<MigrationRowViewModel> failing = rows.Where(r => !r.IsCompatible).ToList();
        List<MigrationRowViewModel> unproven = rows.Where(r => r.IsCompatible && !r.IsVerified).ToList();
        if (failing.Count > 0 || unproven.Count > 0)
        {
            var body = new System.Text.StringBuilder();
            if (failing.Count > 0)
            {
                body.Append("Проверку по исходникам ").Append(NewVersion).Append(" НЕ прошли:\n")
                    .Append(string.Join("\n", failing.Select(r => "• " + r.Title + " — " + r.VerdictText))).Append("\n\n");
            }

            if (unproven.Count > 0 && !BaselineVerified)
            {
                body.Append("С исходниками ").Append(PreviousVersion)
                    .Append(" сравнить не вышло, поэтому изменения клиента для них не проверены:\n")
                    .Append(string.Join("\n", unproven.Select(r => "• " + r.Title))).Append("\n\n");
            }
            else if (unproven.Count > 0)
            {
                // Сравнение было — не проверены отдельные модули; каждый назван.
                body.Append("Проверены не до конца — часть того, что они берут из клиента, сравнить не с чем:\n")
                    .Append(string.Join("\n", unproven.Select(r => "• " + r.Title + ": " + string.Join("; ",
                        r.Candidate?.Report?.Issues.Where(i => i.Kind == ModCompatibilityChecker.UnverifiedKind).Select(i => i.Message)
                        ?? Array.Empty<string>()))))
                    .Append("\n\n");
            }

            body.Append("В игре такой мод может упасть с ошибкой в python.log или работать неверно. Перенести всё равно?");
            if (_context.Confirm?.Invoke("Перенести непроверенные моды", body.ToString()) != true)
            {
                return;
            }
        }

        IsWorking = true;
        _cts = new CancellationTokenSource();
        try
        {
            await TransferAsync(rows, _cts.Token).ConfigureAwait(true);
            StatusText = Summary();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Перенос остановлен.";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusText = "Перенос остановлен: " + ex.Message;
            AddLog("ОШИБКА: " + ex.Message);
        }
        finally
        {
            IsWorking = false;
            _cts.Dispose();
            _cts = null;
            OnPropertyChanged(nameof(TransferredCount));
        }
    }

    /// <summary>Остановить извлечение или проверку.</summary>
    [RelayCommand(CanExecute = nameof(IsWorking))]
    private void Cancel() => _cts?.Cancel();

    /// <summary>Закрыть окно; посреди работы нельзя.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    // ================================================================= шаги

    private async Task<string> EnsureSourcesAsync(CancellationToken ct)
    {
        string? existing = ClientSourceTrees.FindExisting(NewVersion, _context.CurrentTree, _context.TemplateDir);
        if (existing is not null)
        {
            AddLog($"исходники {NewVersion} уже есть: {existing}");
            return existing;
        }

        string target = ClientSourceTrees.DefaultDirFor(NewVersion, _context.CurrentTree, _context.TemplateDir);
        if (IsInside(target, _context.State.Game.RootPath))
        {
            // Дерево кладётся рядом с текущим; если текущее лежит в папке игры, класть туда же
            // нельзя: в игру пишет только служба библиотеки, и только моды.
            throw new InvalidOperationException(
                $"исходники пришлось бы положить в папку игры ({target}) — кокпит туда ничего не пишет. " +
                "Укажите в «Браузере API» дерево исходников вне папки игры и начните переход снова.");
        }

        if (Directory.Exists(target))
        {
            // Каталог с этим именем есть, но исходников клиента этой версии в нём нет (пустой,
            // чужой, другой версии). Затирать его кокпит не станет.
            throw new InvalidOperationException(
                $"каталог {target} уже есть, но исходников клиента {NewVersion} в нём нет. " +
                "Удалите или переименуйте его — тогда кокпит извлечёт исходники заново.");
        }

        StageText = $"исходники клиента {NewVersion}: извлечение из scripts.pkg и декомпиляция — это несколько минут";
        AddLog("извлечение исходников в " + target);
        IsIndeterminate = true;
        var progress = new Progress<ClientSourceProgress>(p =>
        {
            if (p.Total > 0)
            {
                IsIndeterminate = false;
                Progress = (double)p.Done / p.Total;
                StageText = $"исходники клиента {NewVersion}: {p.Stage} {p.Done} из {p.Total}";
            }
            else
            {
                StageText = $"исходники клиента {NewVersion}: {p.Stage}";
            }
        });

        ClientSourceExtraction result = await _context.Extract(target, progress, ct).ConfigureAwait(true);
        AddLog($"исходники готовы за {result.Elapsed.TotalSeconds:F0} с: {result.Decompiled} файлов, " +
               $"частично {result.Partial}, не вышло {result.Failed}; модулей дополнительных пакетов {result.ExtraModules}");
        return result.TreeDir;
    }

    private async Task<MigrationCheck> CheckModsAsync(string tree, CancellationToken ct)
    {
        string? baseline = Baseline(tree);

        StageText = "проверка модов по исходникам " + NewVersion;
        IsIndeterminate = true;
        var progress = new Progress<string>(s => StageText = s);
        MigrationCheck check = await _context.Check(_context.State, tree, baseline, progress, ct).ConfigureAwait(true);
        BaselineVerified = check.BaselineIsPreviousVersion;

        if (check.EmptyBaseline is { } empty)
        {
            AddLog($"в {empty} нет исходников — сравнивать не с чем, проверены только замечания preflight; " +
                   "автоматического переноса не будет");
        }
        else if (check.OldTree is null)
        {
            AddLog($"сравнивать не с чем: исходников {PreviousVersion} нет — проверены только замечания preflight; " +
                   "автоматического переноса не будет");
        }
        else if (!BaselineVerified)
        {
            AddLog($"сравнение с исходниками {check.OldTreeVersion ?? "неизвестной версии"} ({check.OldTree}) — " +
                   $"это не исходники {PreviousVersion}, поэтому автоматического переноса не будет");
        }
        else
        {
            AddLog($"сравнение с исходниками {check.OldTreeVersion} ({check.OldTree})");
        }

        foreach (MigrationCandidate candidate in check.Candidates)
        {
            MigrationRowViewModel? row = Rows.FirstOrDefault(r => !r.WasDisabled &&
                string.Equals(r.ModId, candidate.ModId, StringComparison.OrdinalIgnoreCase));
            if (row is null)
            {
                continue;
            }

            // Модуль, который сравнить не с чем (нет в исходниках прошлой версии, не декомпилировался,
            // переехал в дополнительный пакет): такой мод проверен не до конца, даже если база та самая.
            bool verified = BaselineVerified &&
                            candidate.Report?.Issues.Any(i => i.Kind == ModCompatibilityChecker.UnverifiedKind) != true;
            row.Apply(candidate, verified, PreviousVersion, BaselineVerified);
            AddLog($"«{row.Title}»: {row.VerdictText}");
        }

        return check;
    }

    /// <summary>
    /// С чем сравнивать. Лучше всего — исходники именно прошлой версии (по отметке или имени
    /// каталога рядом с текущими). Нет их — текущее дерево из настроек, если оно не новой версии:
    /// сравнение с ним что-то да покажет, но доказательством не считается (см.
    /// <see cref="MigrationCheck.BaselineIsPreviousVersion"/>). Новое дерево базой не бывает
    /// никогда — с ним «ничего не изменилось» всегда.
    /// </summary>
    private string? Baseline(string newTree)
    {
        string? previous = ClientSourceTrees.FindExisting(PreviousVersion, _context.CurrentTree, _context.TemplateDir);
        if (previous is not null)
        {
            return previous;
        }

        string? current = _context.CurrentTree;
        if (!ClientSourceTrees.LooksLikeTree(current) || ClientSourceTrees.IsForVersion(current, NewVersion) ||
            string.Equals(Path.GetFullPath(current!), Path.GetFullPath(newTree), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return current;
    }

    private static bool IsInside(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        string dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    private void OnRowPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MigrationRowViewModel.IsChecked) or nameof(MigrationRowViewModel.CanTransfer)
            or nameof(MigrationRowViewModel.IsTransferred))
        {
            TransferSelectedCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task TransferAsync(IReadOnlyList<MigrationRowViewModel> rows, CancellationToken ct)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            MigrationRowViewModel row = rows[i];
            ModProject project = row.Candidate!.Project!;
            StageText = $"перенос {i + 1} из {rows.Count}: «{row.Title}»";
            IsIndeterminate = false;
            Progress = (double)i / rows.Count;

            (string? artifact, string how) = await ArtifactAsync(row, project, ct).ConfigureAwait(true);
            if (artifact is null)
            {
                AddLog($"«{row.Title}»: НЕ перенесён — {how}");
                continue;
            }

            ModOperationResult result;
            try
            {
                result = await Task.Run(() => _context.Install(_context.State.Game, artifact), ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                result = new ModOperationResult(ModOperation.Install, ModOperationStatus.Failed, ex.Message, artifact);
            }

            _context.Journal?.Invoke(result);
            if (result.Success)
            {
                row.MarkTransferred($"перенесён ({how}): {result.TargetPath}");
                AddLog($"«{row.Title}»: перенесён ({how}) → {result.TargetPath}");
            }
            else
            {
                row.MarkFailed("не перенесён: " + result.Message);
                AddLog($"«{row.Title}»: НЕ перенесён — {result.Message}");
            }
        }

        Progress = 1;
        OnPropertyChanged(nameof(TransferredCount));
        TransferSelectedCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Что ставить: проверялись ИСХОДНИКИ проекта, значит, ставится их сборка. Свежая в dist —
    /// она; устарела или её нет — build.py собирает заново (без установки, вывод на экране
    /// «Сборка и лог»).
    /// </summary>
    private async Task<(string? Artifact, string How)> ArtifactAsync(MigrationRowViewModel row, ModProject project, CancellationToken ct)
    {
        SetupBundleCandidate? build = _context.ReadBuild(project.ProjectDir);
        if (build is { HasBuild: true, IsStale: false } && File.Exists(build.Project.BuiltArtifactPath))
        {
            return (build.Project.BuiltArtifactPath, "сборка из dist");
        }

        row.ResultText = "сборка…";
        ModsBuildOutcome outcome = await _context.BuildProject(project.ProjectDir, ct).ConfigureAwait(true);
        if (outcome.Cancelled && ct.IsCancellationRequested)
        {
            row.ResultText = "сборка остановлена";
            ct.ThrowIfCancellationRequested();
        }

        if (!outcome.Success)
        {
            string why = outcome.Started ? "сборка не удалась: " + outcome.Message : "сборка не запустилась: " + outcome.Message;
            row.MarkFailed(why);
            return (null, why);
        }

        string artifact = outcome.Result!.ArtifactPath;
        if (artifact.Length == 0 || !File.Exists(artifact))
        {
            row.MarkFailed("build.py не сообщил, какой файл собран");
            return (null, "build.py не сообщил, какой файл собран");
        }

        return (artifact, "пересобран");
    }

    private string Summary()
    {
        int total = Rows.Count(r => !r.WasDisabled);
        int compatible = Rows.Count(r => r.IsCompatible);
        int moved = Rows.Count(r => r.IsTransferred);
        int blocked = Rows.Count(r => r.HasProblem && !r.IsTransferred);
        int failed = Rows.Count(r => r.HasFailed && !r.IsTransferred);
        int waiting = Rows.Count(r => r.IsChecked && r.CanTransfer);

        int unproven = Rows.Count(r => r.IsCompatible && !r.IsVerified && !r.IsTransferred);

        string text = $"Совместимы {compatible} из {total}, перенесено {moved}.";
        if (!BaselineVerified && unproven > 0)
        {
            text += $" С исходниками {PreviousVersion} сравнить не вышло (рядом с текущими их нет), изменения клиента " +
                    "не проверены — поэтому автоматически ничего не перенесено; перенести можно вручную, с вопросом.";
        }
        else if (unproven > 0)
        {
            text += $" Проверены не до конца — {unproven}: часть модулей, которые они импортируют, сравнить не с чем " +
                    "(подробности в строке); автоматически не перенесены, перенести можно вручную, с вопросом.";
        }

        if (blocked > 0)
        {
            text += $" С проблемами совместимости — {blocked}: они не отмечены; разберите находки и при желании отметьте и перенесите вручную.";
        }

        if (failed > 0)
        {
            text += $" Не удалось перенести — {failed}, причины в строках и журнале.";
        }

        if (waiting > 0)
        {
            text += $" Отмечено к переносу — {waiting}: кнопка «Перенести отмеченные».";
        }

        return moved > 0 ? text + " Изменения вступят в силу при следующем запуске игры." : text;
    }

    private void AddLog(string line) => Log.Add($"{_context.Now():HH:mm:ss}  {line}");
}

/// <summary>Строка окна перехода: свой мод прошлой версии.</summary>
public sealed partial class MigrationRowViewModel : ObservableObject
{
    /// <summary>Создаёт строку.</summary>
    /// <param name="mod">Файл в каталоге прошлой версии.</param>
    /// <param name="wasDisabled">Был выключен — не переносится.</param>
    /// <param name="previousVersion">Прошлая версия — для подписи.</param>
    public MigrationRowViewModel(InstalledMod mod, bool wasDisabled, string previousVersion)
    {
        OldMod = mod ?? throw new ArgumentNullException(nameof(mod));
        WasDisabled = wasDisabled;
        _verdictText = wasDisabled ? $"был выключен в {previousVersion} — не переносится" : "ждёт проверки";
    }

    /// <summary>Файл мода в прошлой версии.</summary>
    public InstalledMod OldMod { get; }

    /// <summary>Был выключен в прошлой версии.</summary>
    public bool WasDisabled { get; }

    /// <summary>Имя мода.</summary>
    public string Title => OldMod.DisplayName;

    /// <summary>Идентификатор мода.</summary>
    public string ModId => OldMod.Mine?.ModId ?? OldMod.ModId ?? OldMod.FileName;

    /// <summary>Файл в прошлой версии.</summary>
    public string OldFile => OldMod.FileName;

    /// <summary>Итог проверки.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompatible), nameof(CanTransfer), nameof(HasProblem))]
    private MigrationCandidate? _candidate;

    /// <summary>Отмечен к переносу.</summary>
    [ObservableProperty]
    private bool _isChecked;

    /// <summary>Вердикт для строки.</summary>
    [ObservableProperty]
    private string _verdictText;

    /// <summary>Подробности проверки (по строке на находку).</summary>
    [ObservableProperty]
    private string _details = string.Empty;

    /// <summary>Итог переноса.</summary>
    [ObservableProperty]
    private string _resultText = string.Empty;

    /// <summary>Перенесён.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanTransfer))]
    private bool _isTransferred;

    /// <summary>Перенос не удался.</summary>
    [ObservableProperty]
    private bool _hasFailed;

    /// <summary>Проверен сравнением с исходниками прошлой версии (а не одним preflight).</summary>
    [ObservableProperty]
    private bool _isVerified = true;

    /// <summary>Проверку прошёл.</summary>
    public bool IsCompatible => Candidate?.IsCompatible == true;

    /// <summary>Проверен, и есть проблемы (или проверить не вышло).</summary>
    public bool HasProblem => Candidate is not null && !Candidate.IsCompatible;

    /// <summary>Можно переносить: не выключен, проект есть, ещё не перенесён.</summary>
    public bool CanTransfer => !WasDisabled && Candidate?.Project is not null && !IsTransferred;

    /// <summary>
    /// Применяет итог проверки. Отмечается только совместимый и только при сравнении с исходниками
    /// прошлой версии: без него «совместим» не доказано.
    /// </summary>
    /// <param name="candidate">Итог.</param>
    /// <param name="verified">Сравнивали с исходниками прошлой версии.</param>
    /// <param name="previousVersion">Прошлая версия — для подписи.</param>
    /// <param name="baselineCompared">
    /// С исходниками прошлой версии сравнивали — тогда «не до конца» значит «часть модулей сравнить
    /// не с чем», а не «сравнения не было».
    /// </param>
    public void Apply(MigrationCandidate candidate, bool verified = true, string? previousVersion = null, bool baselineCompared = false)
    {
        Candidate = candidate;
        IsVerified = verified;
        if (candidate.Report is { } report)
        {
            int blocking = report.BlockingIssues.Count;
            VerdictText = report.IsCompatible
                ? (verified
                      ? "совместим"
                      : baselineCompared
                          ? "совместим, но проверен не до конца: часть модулей сравнить не с чем (см. подробности)"
                          : $"совместим, но не доказано: без сравнения с {previousVersion ?? "прошлой версией"}") +
                  (report.ChangedClientModules.Count > 0
                    ? $" (изменились модули клиента: {string.Join(", ", report.ChangedClientModules)})"
                    : string.Empty)
                : $"есть проблемы: {blocking}";
            Details = string.Join(Environment.NewLine, report.Issues.Where(i => i.Kind != "модули изменились").Select(i => i.ToString()));
        }
        else
        {
            VerdictText = "не проверен: " + candidate.Problem;
        }

        IsChecked = verified && candidate.IsCompatible && CanTransfer;
    }

    /// <summary>Перенесён.</summary>
    /// <param name="text">Подпись.</param>
    public void MarkTransferred(string text)
    {
        IsTransferred = true;
        HasFailed = false;
        IsChecked = false;
        ResultText = text;
    }

    /// <summary>Перенос не удался.</summary>
    /// <param name="text">Причина.</param>
    public void MarkFailed(string text)
    {
        HasFailed = true;
        ResultText = text;
    }
}
