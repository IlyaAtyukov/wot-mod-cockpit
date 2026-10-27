using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Core.Migration;

/// <summary>
/// Что видно после обновления клиента: активная версия, прошлая версия со своими модами и что
/// из своего туда ещё не переехало.
/// </summary>
public sealed record ClientMigrationState
{
    /// <summary>Активная установка (paths.xml).</summary>
    public required GameInstall Game { get; init; }

    /// <summary>Прошлая версия, в каталоге которой лежат свои моды; <c>null</c> — такой нет.</summary>
    public string? PreviousVersion { get; init; }

    /// <summary>Каталог модов прошлой версии.</summary>
    public string? PreviousModsDir { get; init; }

    /// <summary>
    /// Свои моды, ВКЛЮЧЁННЫЕ в прошлой версии, которых нет в активном каталоге ни в каком виде.
    /// Их и предлагается перенести.
    /// </summary>
    public IReadOnlyList<InstalledMod> Pending { get; init; } = Array.Empty<InstalledMod>();

    /// <summary>
    /// Свои моды, которые в прошлой версии были ВЫКЛЮЧЕНЫ. Не переносятся: выключал их человек,
    /// и переход на новую версию — не повод включать их обратно.
    /// </summary>
    public IReadOnlyList<InstalledMod> Disabled { get; init; } = Array.Empty<InstalledMod>();

    /// <summary>Есть что переносить.</summary>
    public bool NeedsMigration => Pending.Count > 0;

    /// <summary>Нечего сравнивать: прошлой версии со своими модами нет.</summary>
    public static ClientMigrationState Nothing(GameInstall game) => new() { Game = game };
}

/// <summary>
/// Поиск своих модов, оставшихся в каталоге прошлой версии. Только чтение.
/// <para>
/// Прошлая версия — самая новая из более старых, в каталоге которой есть хоть один СВОЙ мод
/// (связанный с проектом кокпита по <c>&lt;id&gt;</c> или имени файла). Каталоги без своих
/// модов пропускаются: после нескольких обновлений игры в <c>mods\</c> копятся десятки
/// старых версий с чужими модпаками.
/// </para>
/// </summary>
public static class ClientMigrationDetector
{
    /// <summary>Смотрит, что осталось в прошлой версии.</summary>
    /// <param name="game">Активная установка.</param>
    /// <param name="projectDirs">Каталоги своих проектов.</param>
    /// <param name="scanner">Сканер; <c>null</c> — с настройками по умолчанию.</param>
    /// <returns>Состояние перехода.</returns>
    public static ClientMigrationState Detect(GameInstall game, IReadOnlyList<string> projectDirs, ModScanner? scanner = null)
    {
        ArgumentNullException.ThrowIfNull(game);
        scanner ??= new ModScanner();

        if (projectDirs is null || projectDirs.Count == 0)
        {
            return ClientMigrationState.Nothing(game);
        }

        foreach ((string version, string dir) in ClientVersions.ModsDirs(game.RootPath).Reverse())
        {
            if (ClientVersions.Compare(version, game.Version) >= 0 ||
                string.Equals(Path.GetFullPath(dir), Path.GetFullPath(game.ModsDir), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            List<InstalledMod> own = scanner.Scan(dir, projectDirs).Mods.Where(m => m.IsMine).ToList();
            if (own.Count == 0)
            {
                continue;
            }

            var inActive = new HashSet<string>(
                scanner.Scan(game.ModsDir, projectDirs).Mods.Where(m => m.IsMine).Select(m => m.Mine!.ModId),
                StringComparer.OrdinalIgnoreCase);

            // По одному файлу на мод: включённый и самый свежий. Выключенные копии того же мода
            // рядом с включённой (прежние версии) переносить незачем.
            List<InstalledMod> pending = own
                .Where(m => m.State == ModState.Enabled && !inActive.Contains(m.Mine!.ModId))
                .GroupBy(m => m.Mine!.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => m.LastWriteTimeUtc).First())
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var pendingIds = new HashSet<string>(pending.Select(m => m.Mine!.ModId), StringComparer.OrdinalIgnoreCase);
            List<InstalledMod> disabled = own
                .Where(m => m.State == ModState.Disabled && !inActive.Contains(m.Mine!.ModId) && !pendingIds.Contains(m.Mine!.ModId))
                .GroupBy(m => m.Mine!.ModId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(m => m.LastWriteTimeUtc).First())
                .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            return new ClientMigrationState
            {
                Game = game,
                PreviousVersion = version,
                PreviousModsDir = dir,
                Pending = pending,
                Disabled = disabled,
            };
        }

        return ClientMigrationState.Nothing(game);
    }
}
