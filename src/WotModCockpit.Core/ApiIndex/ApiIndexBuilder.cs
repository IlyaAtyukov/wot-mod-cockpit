using System.Diagnostics;

namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Настройки индексации. Значения по умолчанию — те, что измерены как правильные;
/// всё, что выключено, выключено по результатам замера, а не «на всякий случай».
/// </summary>
public sealed record ApiIndexOptions
{
    /// <summary>Настройки по умолчанию.</summary>
    public static ApiIndexOptions Default { get; } = new();

    /// <summary>Индексировать сам файл как символ-модуль (8052 штуки). Даёт поиск по имени модуля.</summary>
    public bool IndexModules { get; init; } = true;

    /// <summary>
    /// Индексировать объявления событий <c>self.onX = Event.Event()</c> отдельным видом символа.
    /// Обязательно: у <c>onArenaPeriodChange</c> ноль объявлений как <c>def</c>/<c>class</c>,
    /// то есть без этого правила событие не находится вообще.
    /// </summary>
    public bool IndexEvents { get; init; } = true;

    /// <summary>Индексировать <c>X = namedtuple(...)</c> и <c>X = type(...)</c> как классы.</summary>
    public bool IndexSyntheticClasses { get; init; } = true;

    /// <summary>
    /// Индексировать модульные синглтоны <c>g_playerEvents = _PlayerEvents()</c> отдельным видом
    /// (<see cref="SymbolKind.Service"/>). Обязательно: <c>def</c>/<c>class</c> у них нет, а именно
    /// через них мод и подписывается на события (<c>g_playerEvents.onAvatarReady += handler</c>).
    /// В корпусе таких 142 при 114 различных именах — 0.08 % индекса.
    /// </summary>
    public bool IndexModuleSingletons { get; init; } = true;

    /// <summary>
    /// Индексировать константы <c>ALL_CAPS = ...</c>. По умолчанию выключено: это удваивает
    /// размер индекса ради символов, которые почти всегда ищут не по имени, а по месту.
    /// </summary>
    public bool IndexConstants { get; init; }

    /// <summary>Забирать список параметров (<c>(self, vehicleID)</c>). Строки интернируются, цена мала.</summary>
    public bool CaptureSignatures { get; init; } = true;

    /// <summary>
    /// Пропускать docstring после <c>def</c>/<c>class</c>.
    /// <b>По умолчанию выключено, и это результат замера, а не небрежность:</b> режим
    /// с пропуском и без него дал ОДИНАКОВЫЕ 156 710 символов (разница 0) — ложные срабатывания
    /// сидят в модульных docstring в начале файла, а не после определений. Зато любой пропуск
    /// вводит состояние между строками, а именно оно на этом корпусе теряет по 391 <c>def</c>
    /// на файл. Оставлено ради возможности проверить гипотезу на новой версии клиента.
    /// </summary>
    public bool SkipDocstrings { get; init; }

    /// <summary>Интернировать имена (экономит 2.4 МБ и 76 тысяч аллокаций).</summary>
    public bool InternNames { get; init; } = true;

    /// <summary>Как часто слать отчёт о прогрессе. UI не нужны проценты — нужен признак жизни.</summary>
    public int ProgressBatch { get; init; } = 256;

    /// <summary>Степень параллелизма; 0 = по числу ядер.</summary>
    public int MaxDegreeOfParallelism { get; init; }
}

/// <summary>Отчёт о ходе индексации.</summary>
/// <param name="Processed">Сколько файлов уже обработано.</param>
/// <param name="Total">Сколько файлов всего.</param>
/// <param name="Stage">Стадия: перечисление, разбор, сборка структур.</param>
public sealed record ApiIndexProgress(int Processed, int Total, string Stage)
{
    /// <summary>Доля выполненного, 0..1.</summary>
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Processed / Total, 0, 1);
}

/// <summary>Файл, который не удалось прочитать или разобрать. Индексацию такое не останавливает.</summary>
/// <param name="Path">Путь к файлу.</param>
/// <param name="Message">Причина.</param>
public sealed record ApiIndexBuildError(string Path, string Message);

/// <summary>Результат сборки индекса.</summary>
/// <param name="Index">Собранный индекс (при неудаче — <see cref="ApiIndex.Empty"/>).</param>
/// <param name="Errors">Файлы, которые пришлось пропустить.</param>
/// <param name="Elapsed">Время сборки.</param>
/// <param name="Layout">Раскладка дерева, по которой шла сборка.</param>
public sealed record ApiIndexBuildResult(
    ApiIndex Index,
    IReadOnlyList<ApiIndexBuildError> Errors,
    TimeSpan Elapsed,
    SourceTreeLayout Layout);

/// <summary>
/// Параллельный обход дерева исходников и сборка индекса.
///
/// <para><b>Замеры этой реализации</b> на 8052 файлах (59 МБ, 166 436 символов, 28 ядер):
/// полная сборка — около 250 мс тепло (в один поток 480-570 мс, то есть параллелизм даёт
/// примерно 2.2x), из них перечисление дерева 20 мс и построение структур поиска 10-20 мс.
/// Холодный старт по замерам разведки — около 700 мс.</para>
///
/// <para><b>Кеша индекса на диске нет — это решение по замеру, а не упущение.</b>
/// Путь без кеша: 719 мс холодно, 105-127 мс тепло (прототип разведки). Путь с кешем: 43 мс,
/// из которых 23 мс стоит одна только валидация (обход дерева ради count+size+maxMtime),
/// то есть ниже ~40 мс он не опускается в принципе. Выигрыш — десятки миллисекунд на тёплом
/// старте, при том что индексация и так идёт в фоне и пользователь её не видит. В обмен кеш
/// приносит целый класс багов: в замерах он уронил прототип рассогласованием формата
/// (<c>BinaryReader.Read7BitEncodedInt</c> на чужой версии файла). Не окупается.</para>
///
/// <para><b>Прогресс отдаётся, а проценты в UI показывать не надо:</b> при таком бюджете
/// прогресс-бар успевает только моргнуть. Правильный UI — неопределённый индикатор
/// с порогом показа около 300 мс.</para>
/// </summary>
public sealed class ApiIndexBuilder
{
    private readonly ApiIndexOptions _options;

    /// <summary>Создаёт построитель с настройками по умолчанию.</summary>
    public ApiIndexBuilder() : this(null) { }

    /// <summary>Создаёт построитель.</summary>
    /// <param name="options">Настройки; <c>null</c> = <see cref="ApiIndexOptions.Default"/>.</param>
    public ApiIndexBuilder(ApiIndexOptions? options) => _options = options ?? ApiIndexOptions.Default;

    /// <summary>Настройки, с которыми работает построитель.</summary>
    public ApiIndexOptions Options => _options;

    /// <summary>
    /// Перечисляет <c>.py</c> дерева. Не бросает на несуществующем каталоге, недоступном диске
    /// и битом пути — возвращает пустой массив.
    /// </summary>
    /// <param name="root">Корень дерева.</param>
    /// <returns>Абсолютные пути.</returns>
    public static string[] EnumerateFiles(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return [];

        try
        {
            return Directory.GetFiles(root, "*.py", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                MatchType = MatchType.Simple
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return [];
        }
    }

    /// <summary>
    /// Собирает индекс. Ни один файл не может уронить сборку: ошибки чтения и разбора
    /// собираются в <see cref="ApiIndexBuildResult.Errors"/> и отдаются наверх — если счётчик
    /// вдруг вырастет на новой версии клиента, мододел должен это увидеть.
    /// </summary>
    /// <param name="root">Корень дерева исходников. Может отсутствовать — тогда индекс пустой.</param>
    /// <param name="progress">Приёмник прогресса; может быть <c>null</c>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат сборки.</returns>
    /// <exception cref="OperationCanceledException">Сборку отменили.</exception>
    public ApiIndexBuildResult Build(string? root, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        SourceTreeLayout layout = SourceTreeLayout.Inspect(root);

        if (!layout.Exists)
        {
            sw.Stop();
            return new ApiIndexBuildResult(ApiIndex.Empty, [], sw.Elapsed, layout);
        }

        progress?.Report(new ApiIndexProgress(0, 0, "перечисление файлов"));
        string[] paths = EnumerateFiles(layout.Path);
        int total = paths.Length;
        cancellationToken.ThrowIfCancellationRequested();

        if (total == 0)
        {
            sw.Stop();
            return new ApiIndexBuildResult(new ApiIndex(layout.Path, [], []), [], sw.Elapsed, layout);
        }

        // Порядок файлов фиксируем: символы одного файла обязаны лежать подряд,
        // а выдача поиска — не прыгать между запусками.
        Array.Sort(paths, StringComparer.Ordinal);

        var meta = new SourceFileEntry?[total];
        var chunks = new List<SymbolEntry>?[total];
        var errors = new System.Collections.Concurrent.ConcurrentBag<ApiIndexBuildError>();
        var pools = new ThreadLocal<StringPool>(() => new StringPool(_options.InternNames), trackAllValues: false);
        var scanners = new ThreadLocal<PythonSymbolScanner>(() => new PythonSymbolScanner(_options), trackAllValues: false);

        int processed = 0;
        var po = new ParallelOptions { CancellationToken = cancellationToken };
        if (_options.MaxDegreeOfParallelism > 0) po.MaxDegreeOfParallelism = _options.MaxDegreeOfParallelism;

        try
        {
            Parallel.For(0, total, po, i =>
            {
                try
                {
                    string? text = SourceFileService.ReadTextUncached(paths[i]);
                    if (text is null)
                    {
                        errors.Add(new ApiIndexBuildError(paths[i], "файл не прочитан"));
                        chunks[i] = new List<SymbolEntry>(0);
                        meta[i] = MakeFile(layout.Path, paths[i], default, 0);
                        return;
                    }

                    SourceFileEntry file = MakeFile(layout.Path, paths[i], SourceFileService.ExtractVirtualPath(text), text.Length);
                    meta[i] = file;

                    var list = new List<SymbolEntry>(32);
                    if (_options.IndexModules)
                    {
                        list.Add(new SymbolEntry
                        {
                            FileId = i,
                            Line = 1,
                            Parent = -1,
                            Name = pools.Value!.Intern(file.ModuleName),
                            Signature = null,
                            Kind = SymbolKind.Module,
                            Origin = SymbolOrigin.ModuleFile,
                            Decorators = DecoratorFlags.None,
                            Indent = 0
                        });
                    }

                    scanners.Value!.Scan(i, text, list, pools.Value);
                    chunks[i] = list;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Один битый файл не должен стоить пользователю всего индекса.
                    errors.Add(new ApiIndexBuildError(paths[i], $"{ex.GetType().Name}: {ex.Message}"));
                    chunks[i] ??= new List<SymbolEntry>(0);
                    meta[i] ??= MakeFile(layout.Path, paths[i], default, 0);
                }
                finally
                {
                    int done = Interlocked.Increment(ref processed);
                    if (progress is not null && (done % _options.ProgressBatch == 0 || done == total))
                        progress.Report(new ApiIndexProgress(done, total, "разбор файлов"));
                }
            });
        }
        finally
        {
            pools.Dispose();
            scanners.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ApiIndexProgress(total, total, "сборка структур поиска"));

        int n = 0;
        for (int i = 0; i < total; i++) n += chunks[i]!.Count;

        var files = new SourceFileEntry[total];
        var symbols = new SymbolEntry[n];
        int k = 0;
        for (int i = 0; i < total; i++)
        {
            files[i] = meta[i]!;
            List<SymbolEntry> c = chunks[i]!;
            int offset = k;
            for (int j = 0; j < c.Count; j++)
            {
                SymbolEntry s = c[j];
                if (s.Parent >= 0) s.Parent += offset;   // Parent был локальным индексом внутри чанка
                symbols[k++] = s;
            }
        }

        var index = new ApiIndex(layout.Path, files, symbols);
        sw.Stop();
        return new ApiIndexBuildResult(index, errors.ToArray(), sw.Elapsed, layout);
    }

    /// <summary>Асинхронная обёртка над <see cref="Build"/>: сборка уходит в пул потоков.</summary>
    /// <param name="root">Корень дерева.</param>
    /// <param name="progress">Приёмник прогресса.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат сборки.</returns>
    public Task<ApiIndexBuildResult> BuildAsync(string? root, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Build(root, progress, cancellationToken), cancellationToken);

    /// <summary>Метаданные файла: относительный путь, виртуальный путь, подкорень и слой.</summary>
    /// <param name="root">Корень дерева.</param>
    /// <param name="fullPath">Абсолютный путь к файлу.</param>
    /// <param name="virtualPath">Виртуальный путь из шапки; пустой спан = откат на относительный путь.</param>
    /// <param name="length">Длина файла в символах.</param>
    /// <returns>Запись файла.</returns>
    public static SourceFileEntry MakeFile(string root, string fullPath, ReadOnlySpan<char> virtualPath, int length)
    {
        string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        return new SourceFileEntry
        {
            RelativePath = rel,
            VirtualPath = virtualPath.IsEmpty ? rel : virtualPath.ToString(),
            Length = length,
            Root = SourceTreeLayout.ClassifyRoot(rel),
            Tier = SourceTreeLayout.ClassifyTier(rel)
        };
    }
}
