using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Services;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Часть экрана «Моды» про обновление клиента.
/// <para>
/// После обновления игры paths.xml смотрит в новый каталог <c>mods\&lt;версия&gt;</c>, а свои
/// моды остаются в каталоге прошлой версии — и в игре их просто нет. Экран замечает это при
/// каждом чтении каталога (<see cref="ClientMigrationDetector"/>, только чтение) и показывает
/// баннер; сам переход — проверка совместимости и перенос — делается в отдельном окне и только
/// по кнопке (<see cref="ClientMigrationViewModel"/>).
/// </para>
/// </summary>
public sealed partial class ModsViewModel
{
    private int _migrationGeneration;
    private bool _migrationOpen;

    /// <summary>Что осталось в прошлой версии клиента; <c>null</c> — не выяснено или нечего.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingMigration), nameof(MigrationBannerText), nameof(MigrationButtonText))]
    [NotifyCanExecuteChangedFor(nameof(OpenMigrationCommand))]
    private ClientMigrationState? _migration;

    /// <summary>Свои моды остались в прошлой версии — показать баннер.</summary>
    public bool HasPendingMigration => Migration?.NeedsMigration == true;

    /// <summary>Текст баннера.</summary>
    public string MigrationBannerText
    {
        get
        {
            if (Migration is not { NeedsMigration: true } m)
            {
                return string.Empty;
            }

            int n = m.Pending.Count;

            // «их» для 21 мода звучит естественно, а для одного — нет: местоимение по n == 1, не по Plural.
            string it = n == 1 ? "его" : "их";
            return $"Клиент обновился: {m.PreviousVersion} → {m.Game.Version}. " +
                   $"{n} {Plural(n, "свой мод остался", "своих мода остались", "своих модов остались")} в каталоге прошлой версии " +
                   $"({string.Join(", ", m.Pending.Select(p => p.DisplayName))}) — в игре {it} сейчас нет. " +
                   $"Кокпит проверит {it} по исходникам новой версии и перенесёт то, что проверку пройдёт.";
        }
    }

    /// <summary>Подпись кнопки баннера.</summary>
    public string MigrationButtonText => Migration is { } m ? $"Перейти на {ClientVersions.Short(m.Game.Version)}…" : "Перейти…";

    /// <summary>
    /// Показать окно перехода. Подменяется в тестах; по умолчанию — модальное окно.
    /// Всё, что окно сделало, остаётся в его модели.
    /// </summary>
    public Action<ClientMigrationViewModel> MigrationHandler { get; set; } = DefaultMigration;

    /// <summary>
    /// Как ставится мод при переходе. Прежних копий мода в новом каталоге нет по построению (иначе
    /// мод не ждал бы переноса), а правило «выключить прежние» узнаёт их и по началу имени файла —
    /// <c>ru.local.clock_</c> зацепил бы только что перенесённый <c>ru.local.clock_lite_1.0.mtmod</c>.
    /// Поэтому оно выключено; файл с тем же именем не перезаписывается.
    /// </summary>
    public static ModInstallOptions MigrationInstallOptions => new()
    {
        DisablePreviousVersions = false,
        OverwriteSameName = false,
    };

    /// <summary>
    /// Каталог шаблона мода — рядом с ним живут деревья исходников. Подменяется в тестах;
    /// <c>null</c> — по умолчанию: <see cref="MainViewModel.ResolveTemplateDir"/> (переменная
    /// <c>WOTCOCKPIT_TEMPLATE_DIR</c>, <c>templateDir</c> из настроек, соседние каталоги).
    /// </summary>
    public Func<string?>? FindTemplateDir { get; set; }

    /// <summary>Шаблон по правилу <see cref="FindTemplateDir"/>; не нашлось — <c>null</c>.</summary>
    private string? TemplateDirForMigration()
    {
        if (FindTemplateDir is { } custom)
        {
            return custom();
        }

        string dir = _main.ResolveTemplateDir();
        return dir.Length > 0 ? dir : null;
    }

    /// <summary>
    /// Как готовить исходники новой версии. Подменяется в тестах; по умолчанию — uncompyle6 из
    /// окружения шаблона или из настроек.
    /// </summary>
    public Func<GameInstall, string, IProgress<ClientSourceProgress>, CancellationToken, Task<ClientSourceExtraction>>? ExtractClientSources { get; set; }

    /// <summary>
    /// Распознавание после чтения каталога. В фоне и молча: сбой здесь не повод портить экран,
    /// баннера просто не будет.
    /// </summary>
    private async Task DetectMigrationAsync(GameInstall game)
    {
        int generation = ++_migrationGeneration;
        ClientMigrationState? state;
        try
        {
            IReadOnlyList<string> projects = CollectProjectDirs();
            state = await Task.Run(() => ClientMigrationDetector.Detect(game, projects)).ConfigureAwait(true);
        }
        catch (Exception)
        {
            state = null;
        }

        if (generation == _migrationGeneration)
        {
            Migration = state;
        }
    }

    private void ForgetMigration()
    {
        ++_migrationGeneration;
        Migration = null;
    }

    private bool CanOpenMigration() => HasPendingMigration && !_migrationOpen;

    /// <summary>
    /// «Перейти на новую версию…»: окно с проверкой совместимости своих модов и переносом.
    /// Пишет в игру только служба библиотеки и только по кнопке в окне.
    /// </summary>
    /// <returns>Задача.</returns>
    [RelayCommand(CanExecute = nameof(CanOpenMigration))]
    private async Task OpenMigrationAsync()
    {
        if (Migration is not { NeedsMigration: true } state)
        {
            return;
        }

        string? template = TemplateDirForMigration();
        string current = _main.ClientSourcesDir;
        var dialog = new ClientMigrationViewModel(new ClientMigrationContext
        {
            State = state,
            CurrentTree = current.Length > 0 ? current : null,
            TemplateDir = template,
            Extract = (target, progress, ct) => (ExtractClientSources ?? DefaultExtract)(state.Game, target, progress, ct),
            Check = (s, newTree, oldTree, progress, ct) =>
                new ClientMigrationPlanner(new JsonProjectStore()).CheckAsync(s, newTree, oldTree, progress, ct),
            BuildProject = _main.BuildForModsAsync,
            Install = (game, artifact) => _library.Install(game, artifact, MigrationInstallOptions),
            UseClientSources = _main.UseClientSourcesAsync,
            Journal = result =>
            {
                foreach (ModOperationResult related in result.Related)
                {
                    AddJournalLine(SafeLogLine(related));
                }

                AddJournalLine(SafeLogLine(result));
            },
            Confirm = (caption, body) => ConfirmHandler(caption, body),
        });

        _migrationOpen = true;
        OpenMigrationCommand.NotifyCanExecuteChanged();
        try
        {
            // Под окном каталог не перечитывается: окно само в него пишет, а после закрытия
            // список перечитывается один раз целиком.
            using (HoldBackgroundRefresh())
            {
                MigrationHandler(dialog);
            }
        }
        finally
        {
            _migrationOpen = false;
            OpenMigrationCommand.NotifyCanExecuteChanged();
        }

        if (dialog.TransferredCount > 0)
        {
            await RefreshAsync().ConfigureAwait(true);
            StatusText = dialog.StatusText;
        }
    }

    /// <summary>Исходники новой версии: uncompyle6 шаблона или из настроек.</summary>
    private Task<ClientSourceExtraction> DefaultExtract(
        GameInstall game, string target, IProgress<ClientSourceProgress> progress, CancellationToken ct)
    {
        string? settingsPython = null;
        try
        {
            settingsPython = _settings.Load().PythonExe;
        }
        catch (Exception)
        {
            // Настроек нет — остаётся окружение шаблона и python из PATH.
        }

        string? template = TemplateDirForMigration();
        return Task.Run(async () =>
        {
            progress.Report(new ClientSourceProgress("поиск uncompyle6", 0, 0));
            string? python = Uncompyle6Decompiler.FindPython(Uncompyle6Decompiler.DefaultCandidates(template, settingsPython));
            if (python is null)
            {
                throw new InvalidOperationException(
                    "не найден Python с пакетами uncompyle6 и xdis. Поставьте проверенные версии " +
                    "(pip install -r <шаблон>\\tools\\requirements-decompile.txt) в окружение шаблона .tools\\venv_uncompyle, " +
                    "в Python из настроек кокпита (pythonExe) или в python из PATH — либо положите готовые исходники " +
                    $"новой версии в каталог {ClientSourceTrees.DirPrefix}{ClientVersions.Short(game.Version)} рядом с текущими.");
            }

            using var decompiler = new Uncompyle6Decompiler(python);
            return await new ClientSourceExtractor(decompiler)
                .ExtractAsync(game, target, progress, ct).ConfigureAwait(false);
        }, ct);
    }

    /// <summary>Окно перехода поверх главного окна.</summary>
    /// <param name="dialog">Модель окна.</param>
    private static void DefaultMigration(ClientMigrationViewModel dialog)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            // Нет приложения (тест, headless) — окна нет.
            return;
        }

        var window = new Views.ClientMigrationDialog { DataContext = dialog };
        if (app.MainWindow is { IsLoaded: true } owner && !ReferenceEquals(owner, window))
        {
            window.Owner = owner;
        }

        window.ShowDialog();
    }
}
