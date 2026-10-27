using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Часть главного экрана, относящаяся к экрану «Моды» — лаунчеру установленных модов.
/// <para>
/// Отдельным файлом по той же причине, что <c>MainViewModel.Stage3.cs</c> и
/// <c>MainViewModel.Stage4.cs</c>: экран подключается целиком. Если модель не
/// зарегистрирована, <see cref="Mods"/> остаётся <c>null</c>, чип «Моды» гаснет, и главное
/// окно ведёт себя ровно так же, как до появления экрана.
/// </para>
/// <para>
/// Здесь же живёт <see cref="IsBuildChipChecked"/>. Это не дубль <c>IsBuildScreen</c>, а
/// его честная версия для чипа: экран «Моды» лежит той же ячейкой поверх остальных, и без
/// этой поправки чип «Сборка и лог» оставался бы зажжённым, когда показан список модов, —
/// то есть переключатель врал бы про то, что сейчас на экране.
/// </para>
/// </summary>
public sealed partial class MainViewModel
{
    private ModsViewModel? _mods;
    private bool _modsHookedSelf;
    private bool _modsHookedStage3;

    /// <summary>Модель экрана «Моды» либо <c>null</c>, если экран не зарегистрирован.</summary>
    public ModsViewModel? Mods => _mods;

    /// <summary>Экран «Моды» подключён: от этого зависит его чип в шапке.</summary>
    public bool HasMods => _mods is not null;

    /// <summary>Показан экран «Моды».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuildChipChecked))]
    private bool _isModsScreen;

    /// <summary>
    /// Состояние чипа «Сборка и лог» с учётом экрана «Моды».
    /// Снятая галочка игнорируется — как и у остальных чипов, она не должна оставлять
    /// окно без единого видимого экрана.
    /// </summary>
    public bool IsBuildChipChecked
    {
        get => !IsModsScreen && IsBuildScreen;
        set
        {
            if (!value)
            {
                OnPropertyChanged(nameof(IsBuildChipChecked));
                return;
            }

            IsModsScreen = false;
            IsBuildScreen = true;
        }
    }

    /// <summary>
    /// Подключает экран «Моды». Зовётся из его же конструктора: он ссылается на эту модель,
    /// и обратной ссылки в конструкторе быть не может (так же устроены этапы 3 и 4).
    /// </summary>
    /// <param name="mods">Модель экрана.</param>
    public void AttachMods(ModsViewModel mods)
    {
        _mods = mods ?? throw new ArgumentNullException(nameof(mods));

        OnPropertyChanged(nameof(Mods));
        OnPropertyChanged(nameof(HasMods));

        if (!_modsHookedSelf)
        {
            _modsHookedSelf = true;
            PropertyChanged += OnOwnPropertyChangedForMods;
        }

        HookStage3ForMods();
    }

    private void OnOwnPropertyChangedForMods(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(IsApiScreen):
            case nameof(IsSettingsScreen):
                // Экраны взаимоисключающие: все они живут в одной ячейке разметки.
                if (IsApiScreen || IsSettingsScreen)
                {
                    IsModsScreen = false;
                }

                OnPropertyChanged(nameof(IsBuildChipChecked));
                break;

            case nameof(IsBuildScreen):
                OnPropertyChanged(nameof(IsBuildChipChecked));
                break;

            case nameof(Stage3):
            case nameof(HasStage3):
                HookStage3ForMods();
                break;
        }
    }

    private void HookStage3ForMods()
    {
        if (_modsHookedStage3 || Stage3 is null)
        {
            return;
        }

        _modsHookedStage3 = true;
        Stage3.PropertyChanged += OnStage3PropertyChangedForMods;
    }

    private void OnStage3PropertyChangedForMods(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Stage3ViewModel.IsRecipesScreen))
        {
            return;
        }

        if (Stage3?.IsRecipesScreen == true)
        {
            IsModsScreen = false;
        }

        OnPropertyChanged(nameof(IsBuildChipChecked));
    }

    partial void OnIsModsScreenChanged(bool value)
    {
        if (!value)
        {
            // Экран скрыт — опрос процесса игры больше не нужен.
            _mods?.Suspend();
            return;
        }

        IsApiScreen = false;
        IsSettingsScreen = false;

        if (Stage3 is not null)
        {
            Stage3.IsRecipesScreen = false;
        }

        // Каталог игры читается ЛЕНИВО: при старте приложения экран не делает ничего.
        _ = _mods?.EnsureLoadedAsync();
    }

    /// <summary>
    /// Собрать проект по просьбе экрана «Моды» — первая половина «Собрать и установить» из
    /// меню строки.
    /// <para>
    /// build.py запускается БЕЗ <c>--install</c>: в игру собранный архив ставит сам экран
    /// «Моды» через службу библиотеки — с её подтверждением, выключением прежних версий и
    /// журналом, ровно как «Поставить из dist». Отсюда в каталог игры не пишется ничего.
    /// </para>
    /// <para>
    /// Проект читается с диска по каталогу строки и открытым НЕ становится: собрать мод из
    /// списка — не повод менять проект, над которым идёт работа. Проверки те же, что у кнопки
    /// «Собрать»: <c>--clean</c> поверх россыпи и preflight. Вывод идёт в ту же панель сборки,
    /// отмена — той же кнопкой, и одновременно build.py запускается только один.
    /// </para>
    /// </summary>
    /// <param name="projectDir">Каталог проекта строки.</param>
    /// <param name="cancellationToken">Отмена со стороны того, кто просил сборку (снимает build.py).</param>
    /// <returns>Итог: собранный проект и что сказал build.py либо почему до сборки не дошло.</returns>
    public async Task<ModsBuildOutcome> BuildForModsAsync(string projectDir, CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return ModsBuildOutcome.NotStarted("экран «Сборка и лог» занят: " + BusyText);
        }

        ModProject? project;
        try
        {
            project = _projectStore.Load(projectDir);
        }
        catch (Exception ex)
        {
            return ModsBuildOutcome.NotStarted("проект не прочитан: " + Short(ex));
        }

        if (project is null)
        {
            return ModsBuildOutcome.NotStarted("в каталоге нет проекта кокпита (wotmod.json): " + projectDir);
        }

        if (!ConfirmCleanKeepsUninstallable(project, InResMods(project)))
        {
            return ModsBuildOutcome.NotStarted("отменено пользователем");
        }

        if (!await Stage3PreflightGateAsync(project).ConfigureAwait(true))
        {
            return ModsBuildOutcome.NotStarted("сборка отменена — см. панель Problems на экране «Сборка и лог»");
        }

        string name = project.DisplayName.Length > 0 ? project.DisplayName : project.ModId;
        BuildResult result = await RunBuildCoreAsync(BuildAction.Build, project, $"сборка «{name}» для установки", cancellationToken)
            .ConfigureAwait(true);

        return new ModsBuildOutcome(project, result, StatusMessage);
    }

    /// <summary>Дерево исходников клиента из настроек (<c>wotSrcDir</c>); пусто — не задано.</summary>
    public string ClientSourcesDir => _settings.WotSrcDir ?? string.Empty;

    /// <summary>
    /// Переключает кокпит на дерево исходников новой версии клиента: настройки, браузер API,
    /// preflight и маппер traceback дальше работают по нему.
    /// </summary>
    /// <param name="dir">Дерево исходников.</param>
    /// <returns>Задача; браузер API строит индекс в фоне.</returns>
    public async Task UseClientSourcesAsync(string dir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dir);
        _settings.WotSrcDir = dir;
        SaveSettings();

        if (ApiBrowser is not null)
        {
            await ApiBrowser.UseTreeAsync(dir).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Последний выбор в диалоге «Собрать сетап». Живёт в общем объекте настроек главного
    /// экрана: только он пишет settings.json, причём целиком, и отдельная запись в обход него
    /// была бы затёрта его следующим сохранением (тот же приём, что у браузера API).
    /// </summary>
    public SetupBundleSettings SetupBundleSettings => _settings.SetupBundle ??= new SetupBundleSettings();

    /// <summary>Запоминает выбор диалога «Собрать сетап» и сохраняет настройки.</summary>
    /// <param name="value">Новый выбор.</param>
    public void RememberSetupBundle(SetupBundleSettings value)
    {
        _settings.SetupBundle = value ?? throw new ArgumentNullException(nameof(value));
        SaveSettings();
    }

    /// <summary>
    /// Лежит ли в игре россыпь именно этого проекта. У открытого проекта это
    /// <c>DeployInResMods</c>, у любого другого — свежая проба. Проба не удалась — «лежит»:
    /// лишний вопрос про <c>--clean</c> дешевле потерянного <c>dev_manifest.json</c>.
    /// </summary>
    private bool InResMods(ModProject project)
    {
        GameInstall? game = _game;
        if (game is null)
        {
            return false;
        }

        try
        {
            return _deployProbe.Probe(game, project).InResMods;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>Итог сборки, которую запросил экран «Моды».</summary>
/// <param name="Project">Прочитанный проект; <c>null</c> — build.py не запускался.</param>
/// <param name="Result">Что сказал build.py; <c>null</c> — не запускался.</param>
/// <param name="Message">Что показать пользователю.</param>
public sealed record ModsBuildOutcome(ModProject? Project, BuildResult? Result, string Message)
{
    /// <summary>build.py запускался: итог есть, даже если это провал или отмена.</summary>
    public bool Started => Project is not null && Result is not null;

    /// <summary>Сборка прошла.</summary>
    public bool Success => Started && Result!.Success;

    /// <summary>Сборку отменили кнопкой «Отмена».</summary>
    public bool Cancelled => Started && Result!.ExitCode == BuildResult.ExitCodeCancelled;

    /// <summary>До запуска build.py не дошло: занято, проекта нет, отказ в диалоге.</summary>
    /// <param name="message">Почему.</param>
    /// <returns>Итог без сборки.</returns>
    public static ModsBuildOutcome NotStarted(string message) => new(null, null, message);
}
