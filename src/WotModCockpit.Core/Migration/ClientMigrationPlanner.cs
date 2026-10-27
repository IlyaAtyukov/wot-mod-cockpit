using System.Diagnostics;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using ClientIndex = WotModCockpit.Core.ApiIndex.ApiIndex;

namespace WotModCockpit.Core.Migration;

/// <summary>Свой мод из прошлой версии и итог его проверки.</summary>
/// <param name="OldMod">Файл мода в каталоге прошлой версии.</param>
/// <param name="Project">Проект мода; <c>null</c> — не прочитался.</param>
/// <param name="Report">Итог проверки; <c>null</c> — проверить не вышло.</param>
/// <param name="Problem">Почему не проверен.</param>
public sealed record MigrationCandidate(InstalledMod OldMod, ModProject? Project, ModCompatibilityReport? Report, string? Problem)
{
    /// <summary>Идентификатор мода.</summary>
    public string ModId => OldMod.Mine?.ModId ?? OldMod.ModId ?? OldMod.FileName;

    /// <summary>Можно переносить без вопросов.</summary>
    public bool IsCompatible => Report?.IsCompatible == true;
}

/// <summary>Итог проверки перед переходом.</summary>
/// <param name="NewTree">Дерево исходников новой версии.</param>
/// <param name="OldTree">Дерево, с которым сравнивали; <c>null</c> — сравнить было не с чем.</param>
/// <param name="OldTreeVersion">Его версия, если известна.</param>
/// <param name="Candidates">Свои моды и итоги.</param>
/// <param name="Elapsed">Сколько заняло.</param>
public sealed record MigrationCheck(
    string NewTree,
    string? OldTree,
    string? OldTreeVersion,
    IReadOnlyList<MigrationCandidate> Candidates,
    TimeSpan Elapsed)
{
    /// <summary>
    /// Сравнивали с исходниками ИМЕННО прошлой версии (по отметке или имени каталога). Только
    /// тогда «совместим» значит «ничего из нужного моду не сломалось»: дерево неизвестной или
    /// другой версии может и не содержать того, что сломалось, и спрятать новые ошибки
    /// preflight как «давние». Без этого автоматический перенос не делается.
    /// </summary>
    public bool BaselineIsPreviousVersion { get; init; }

    /// <summary>Модулей в индексе новой версии; 0 — дерево пустое, проверять было нечем; −1 — неизвестно.</summary>
    public int NewTreeModules { get; init; } = -1;

    /// <summary>
    /// База сравнения была, но исходников в ней нет (пустой <c>client\</c>, оборванное копирование):
    /// сравнения не было, <see cref="OldTree"/> — <c>null</c>, а здесь — путь к ней, чтобы сказать об этом.
    /// </summary>
    public string? EmptyBaseline { get; init; }
}

/// <summary>
/// Проверка своих модов перед переходом на новую версию клиента: индексы обеих версий
/// строятся по разу, дальше каждый мод сверяется <see cref="ModCompatibilityChecker"/>.
/// Только чтение.
/// </summary>
public sealed class ClientMigrationPlanner
{
    private readonly IProjectStore _store;

    /// <summary>Создаёт планировщик.</summary>
    /// <param name="store">Чтение проектов.</param>
    public ClientMigrationPlanner(IProjectStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>Индекс дерева: те же правила, что у кокпита, плюс константы.</summary>
    /// <param name="tree">Дерево исходников.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Индекс.</returns>
    public static ClientIndex BuildIndex(string tree, CancellationToken ct) =>
        new ApiIndexBuilder(new ApiIndexOptions { IndexConstants = true }).Build(tree, null, ct).Index;

    /// <summary>Проверяет моды, ожидающие переноса.</summary>
    /// <param name="state">Что осталось в прошлой версии.</param>
    /// <param name="newTree">Дерево новой версии.</param>
    /// <param name="oldTree">Дерево для сравнения (обычно текущее из настроек); то же самое или пустое — без сравнения.</param>
    /// <param name="progress">Ход.</param>
    /// <param name="ct">Отмена.</param>
    /// <returns>Итог.</returns>
    public Task<MigrationCheck> CheckAsync(
        ClientMigrationState state,
        string newTree,
        string? oldTree,
        IProgress<string>? progress,
        CancellationToken ct) => Task.Run(() => Check(state, newTree, oldTree, progress, ct), ct);

    private MigrationCheck Check(ClientMigrationState state, string newTree, string? oldTree, IProgress<string>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        var watch = Stopwatch.StartNew();

        // Дерево той же или более новой версии, чем клиент, — не база сравнения: с ним «ничего не
        // изменилось» и все ошибки preflight выглядят давними.
        string? oldVersion = ClientSourceTrees.VersionOf(oldTree);
        bool compare = !string.IsNullOrWhiteSpace(oldTree)
                       && ClientSourceTrees.LooksLikeTree(oldTree)
                       && !string.Equals(Path.GetFullPath(oldTree!), Path.GetFullPath(newTree), StringComparison.OrdinalIgnoreCase)
                       && (oldVersion is null || ClientVersions.Compare(ClientVersions.Short(oldVersion), ClientVersions.Short(state.Game.Version)) < 0);

        progress?.Report("индекс новой версии");
        ClientIndex newIndex = BuildIndex(newTree, ct);
        ClientIndex? oldIndex = null;
        if (compare)
        {
            progress?.Report("индекс прошлой версии");
            oldIndex = BuildIndex(oldTree!, ct);
        }

        var files = new SourceFileService();
        var checker = new ModCompatibilityChecker(
            oldIndex,
            newIndex,
            files,
            compare ? ClientSourceTrees.ReadExtraModules(oldTree) : null,
            ClientSourceTrees.ReadExtraModules(newTree),
            newFailedModules: ClientSourceTrees.ReadFailedModules(newTree),
            oldFailedModules: compare ? ClientSourceTrees.ReadFailedModules(oldTree) : null);

        var candidates = new List<MigrationCandidate>();
        foreach (InstalledMod mod in state.Pending)
        {
            ct.ThrowIfCancellationRequested();
            string title = mod.DisplayName;
            progress?.Report("проверка «" + title + "»");

            ModProject? project = null;
            try
            {
                project = mod.Mine is null ? null : _store.Load(mod.Mine.ProjectDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                candidates.Add(new MigrationCandidate(mod, null, null, "проект не прочитан: " + ex.Message));
                continue;
            }

            if (project is null)
            {
                candidates.Add(new MigrationCandidate(mod, null, null, "проект мода не найден: " + (mod.Mine?.ProjectDir ?? "?")));
                continue;
            }

            try
            {
                candidates.Add(new MigrationCandidate(mod, project, checker.Check(project), null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                candidates.Add(new MigrationCandidate(mod, project, null, "проверка не прошла: " + ex.Message));
            }
        }

        // Сравнение состоялось, только если в базе нашлись исходники: дерево с правильным именем,
        // но пустым индексом не даёт ничего, кроме preflight новой версии.
        bool compared = compare && checker.HasBaseline;
        return new MigrationCheck(
            Path.GetFullPath(newTree),
            compared ? Path.GetFullPath(oldTree!) : null,
            compared ? oldVersion : null,
            candidates,
            watch.Elapsed)
        {
            BaselineIsPreviousVersion = compared && state.PreviousVersion is { } previous
                                        && ClientSourceTrees.IsForVersion(oldTree, previous),
            NewTreeModules = newIndex.Files.Count,
            EmptyBaseline = compare && !compared ? Path.GetFullPath(oldTree!) : null,
        };
    }
}
