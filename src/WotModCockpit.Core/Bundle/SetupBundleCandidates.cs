using WotModCockpit.Core.Mods;

namespace WotModCockpit.Core.Bundle;

/// <summary>
/// Проект пользователя как кандидат в сетап: что о нём говорит <c>src\meta.xml</c>, что
/// собрано в <c>dist\</c> и не устарела ли сборка.
/// </summary>
/// <param name="Project">Проект глазами лаунчера (<see cref="ModScanner.ReadProjects"/>).</param>
/// <param name="SourceNewestUtc">Время самой свежей правки в <c>src\</c>; <c>null</c> — не выяснить.</param>
public sealed record SetupBundleCandidate(ModProjectInfo Project, DateTimeOffset? SourceNewestUtc)
{
    /// <summary><c>&lt;id&gt;</c> мода.</summary>
    public string ModId => Project.ModId;

    /// <summary>В <c>dist\</c> есть собранный архив.</summary>
    public bool HasBuild => Project.BuiltArtifactPath is not null;

    /// <summary>
    /// В собранном архиве есть файл, исходника которого в <c>src\</c> больше нет (удалили или
    /// переименовали после сборки). Время записи такого не покажет: у оставшихся файлов оно не
    /// меняется.
    /// </summary>
    public bool BuildHasRemovedSources { get; init; }

    /// <summary>
    /// Исходники правились после сборки: в сетап ушла бы старая версия. Сравнение по времени
    /// записи — дёшево и честно для своих проектов; ложная тревога (например, после
    /// <c>git checkout</c>) стоит одной лишней пересборки. Плюс файлы, которых в src уже нет.
    /// </summary>
    public bool IsStale =>
        HasBuild && (BuildHasRemovedSources ||
                     (SourceNewestUtc is { } src && Project.BuiltWriteTimeUtc is { } built && src > built));
}

/// <summary>Сбор кандидатов в сетап по каталогам проектов. Только чтение.</summary>
public static class SetupBundleCandidates
{
    /// <summary>Сколько файлов исходников смотреть в одном проекте — страховка от гигантского src\.</summary>
    private const int MaxSourceFiles = 5000;

    /// <summary>
    /// Читает проекты. В список попадают только те, у кого есть <c>src\meta.xml</c> с
    /// <c>&lt;id&gt;</c>: без id сетапу не по чему узнать мод в игре и выключить прежнюю версию.
    /// </summary>
    /// <param name="projectDirs">Каталоги проектов.</param>
    /// <returns>Кандидаты, по имени.</returns>
    public static IReadOnlyList<SetupBundleCandidate> Read(IReadOnlyList<string> projectDirs)
    {
        IReadOnlyList<ModProjectInfo> projects = new ModScanner().ReadProjects(projectDirs);

        return projects
            .Where(p => p.HasMeta && p.MetaError is null && p.ModId.Length > 0)
            .Select(p => new SetupBundleCandidate(p, NewestSourceWrite(p.ProjectDir))
            {
                BuildHasRemovedSources = p.BuiltArtifactPath is { } built && HasRemovedSources(built, Path.Combine(p.ProjectDir, "src")),
            })
            .OrderBy(c => c.Project.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(c => c.Project.ProjectDir, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Есть ли в собранном архиве файл без исходника в <c>src\</c>. build.py раскладывает src в
    /// архив один к одному, только <c>.py</c> становится <c>.pyc</c>; поэтому запись архива
    /// <c>res/…/x.pyc</c> ищется как <c>src\res\…\x.py</c> (или сам <c>.pyc</c>), остальные — как
    /// есть. Читается только центральный каталог ZIP. Архив не ZIP или пуст (build.py сняли
    /// посреди упаковки) — «да»: такая сборка не годится. Файл не открыть — «нет», как раньше.
    /// </summary>
    /// <param name="artifact">Собранный архив.</param>
    /// <param name="src">Каталог исходников.</param>
    /// <returns>Признак.</returns>
    public static bool HasRemovedSources(string artifact, string src)
    {
        if (!Directory.Exists(src))
        {
            return false;
        }

        try
        {
            using FileStream fs = new(artifact, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read);
            if (zip.Entries.Count == 0)
            {
                return true;
            }

            foreach (System.IO.Compression.ZipArchiveEntry e in zip.Entries)
            {
                string name = e.FullName.Replace('\\', '/');
                if (name.EndsWith('/') || name.Contains("..", StringComparison.Ordinal))
                {
                    continue;
                }

                string path = Path.Combine(src, name.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    continue;
                }

                if (name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) && File.Exists(path[..^1]))
                {
                    continue;
                }

                return true;
            }
        }
        catch (InvalidDataException)
        {
            // Не ZIP (оборванная запись: build.py сняли посреди упаковки) — годной сборкой это не
            // считается, пересобрать.
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Самая свежая правка в <c>src\</c>. Байткод и кэши питона не в счёт: их пишет не человек,
    /// и запуск тестов не должен объявлять сборку устаревшей.
    /// </summary>
    /// <param name="projectDir">Каталог проекта.</param>
    /// <returns>Время либо <c>null</c>.</returns>
    public static DateTimeOffset? NewestSourceWrite(string projectDir)
    {
        string src = Path.Combine(projectDir, "src");
        if (!Directory.Exists(src))
        {
            return null;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
        };

        DateTime? newest = null;
        int seen = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(src, "*", options))
            {
                if (++seen > MaxSourceFiles)
                {
                    break;
                }

                if (file.Contains(Path.DirectorySeparatorChar + "__pycache__" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase) ||
                    file.EndsWith(".pyo", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DateTime write = File.GetLastWriteTimeUtc(file);
                if (newest is null || write > newest)
                {
                    newest = write;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return newest is null ? null : new DateTimeOffset(newest.Value, TimeSpan.Zero);
    }
}

/// <summary>Где лежит заглушка установщика.</summary>
public static class SetupStubLocator
{
    /// <summary>Имя файла заглушки.</summary>
    public const string FileName = "WotModCockpit.Setup.exe";

    /// <summary>Подкаталог рядом с кокпитом, куда сборка кладёт заглушку.</summary>
    public const string Folder = "setup";

    /// <summary>Ищет <c>setup\WotModCockpit.Setup.exe</c> рядом с приложением.</summary>
    /// <param name="baseDirectory">Каталог приложения; <c>null</c> — <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>Путь либо <c>null</c>.</returns>
    public static string? Find(string? baseDirectory = null)
    {
        string path = Path.Combine(baseDirectory ?? AppContext.BaseDirectory, Folder, FileName);
        return File.Exists(path) ? path : null;
    }
}
