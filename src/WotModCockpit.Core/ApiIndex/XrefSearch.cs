using System.Diagnostics;

namespace WotModCockpit.Core.ApiIndex;

/// <summary>Что за попадание нашлось в строке. Категории взаимоисключающие.</summary>
public enum XrefCategory : byte
{
    /// <summary>Внутри комментария. В этом корпусе практически не встречается (см. <see cref="XrefSearch"/>).</summary>
    Comment = 0,

    /// <summary>Внутри строкового литерала — единственный заметный источник мусора, 0-3.3 %.</summary>
    StringLiteral = 1,

    /// <summary>Само объявление: строка начинается с <c>def</c> или <c>class</c>.</summary>
    Declaration = 2,

    /// <summary>Строка <c>import</c> / <c>from ... import ...</c>.</summary>
    Import = 3,

    /// <summary>Вызов: дальше идёт <c>(</c>.</summary>
    Call = 4,

    /// <summary>Подписка на событие: дальше идёт <c>+=</c>. Это готовый пример «как подписаться».</summary>
    Subscribe = 5,

    /// <summary>Отписка: дальше идёт <c>-=</c>.</summary>
    Unsubscribe = 6,

    /// <summary>Чтение атрибута: слева стоит точка.</summary>
    AttributeRead = 7,

    /// <summary>Всё остальное.</summary>
    Other = 8
}

/// <summary>Запрос «где это используется».</summary>
public sealed record XrefQuery
{
    /// <summary>Имя символа.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Приёмник для уточнения (<c>g_playerEvents</c>, <c>settings</c>). Это главное лекарство
    /// от омонимии: <c>settings.update</c> даёт 21 попадание против 12 625 у голого <c>update</c>,
    /// то есть сужение в 601 раз.
    /// </summary>
    public string? Receiver { get; init; }

    /// <summary>Без учёта регистра. По умолчанию нет: это в 6-10 раз дороже (30-40 мс против 3-5 мс).</summary>
    public bool IgnoreCase { get; init; }

    /// <summary>Искать в сгенерированных ресурсах.</summary>
    public bool IncludeGenerated { get; init; }

    /// <summary>Искать в stdlib/вендорном коде.</summary>
    public bool IncludeVendor { get; init; }

    /// <summary>Оставить только эти категории; <c>null</c> = все.</summary>
    public IReadOnlyCollection<XrefCategory>? Categories { get; init; }

    /// <summary>Сколько попаданий вернуть. Полное число всё равно считается и отдаётся.</summary>
    public int Limit { get; init; } = 500;
}

/// <summary>Одно попадание.</summary>
/// <param name="RelativePath">Путь относительно дерева.</param>
/// <param name="VirtualPath">Путь как в traceback.</param>
/// <param name="FullPath">Абсолютный путь — по нему открывается редактор.</param>
/// <param name="Line">Номер строки, 1-based.</param>
/// <param name="Column">Позиция в строке, 0-based.</param>
/// <param name="Text">Текст строки без отступа-хвоста.</param>
/// <param name="Category">Категория попадания.</param>
/// <param name="Root">Подкорень файла.</param>
/// <param name="Tier">Слой файла.</param>
public sealed record XrefHit(
    string RelativePath,
    string VirtualPath,
    string FullPath,
    int Line,
    int Column,
    string Text,
    XrefCategory Category,
    SourceRoot Root,
    SourceTier Tier);

/// <summary>Результат поиска ссылок.</summary>
/// <param name="Needle">Что реально искали (с приёмником, если он был).</param>
/// <param name="Hits">Попадания, не больше <see cref="XrefQuery.Limit"/>.</param>
/// <param name="TotalHits">Сколько попаданий всего.</param>
/// <param name="FileCount">В скольких файлах.</param>
/// <param name="DeclarationCount">Сколько мест объявляют это имя (степень омонимии).</param>
/// <param name="ByCategory">Разбивка по категориям.</param>
/// <param name="TopReceivers">Самые частые приёмники — готовые кандидаты для уточнения запроса.</param>
/// <param name="Elapsed">Время поиска.</param>
public sealed record XrefResult(
    string Needle,
    IReadOnlyList<XrefHit> Hits,
    int TotalHits,
    int FileCount,
    int DeclarationCount,
    IReadOnlyDictionary<XrefCategory, int> ByCategory,
    IReadOnlyList<(string Receiver, int Count)> TopReceivers,
    TimeSpan Elapsed)
{
    /// <summary>Список обрезан лимитом.</summary>
    public bool Truncated => TotalHits > Hits.Count;

    /// <summary>
    /// Имя объявлено больше чем в 10 местах, то есть поиск без приёмника заведомо бесполезен.
    /// UI обязан сказать это честно, а не вываливать 2182 строки как ответ.
    /// </summary>
    public bool IsHomonym => DeclarationCount > 10;

    /// <summary>Пустой результат.</summary>
    /// <param name="needle">Что искали.</param>
    /// <returns>Результат без попаданий.</returns>
    public static XrefResult Empty(string needle) =>
        new(needle, [], 0, 0, 0, new Dictionary<XrefCategory, int>(), [], TimeSpan.Zero);
}

/// <summary>
/// Полнотекстовый поиск «где это вызывается» по всему дереву исходников.
///
/// <para><b>Почему просто <c>Contains</c> не годится и что вместо него.</b> Три уровня, все замерены:</para>
/// <list type="number">
///   <item><b>Границы идентификатора</b> — самый мощный одиночный фильтр:
///         <c>update</c> 12 625 → 2182 (в 5.8 раза), <c>getVehicle</c> 2587 → 143 (в 18 раз).</item>
///   <item><b>Классификация попадания</b> по одной строке (объявление, импорт, вызов,
///         подписка, чтение атрибута, литерал). Мусор из строковых литералов после границ —
///         0-3.3 %, поэтому его достаточно помечать, а не прятать.</item>
///   <item><b>Уточнение приёмником</b> — лекарство от омонимии, сужение до 601 раза.
///         Кандидаты в приёмники считаются прямо во время скана
///         (<see cref="XrefResult.TopReceivers"/>).</item>
/// </list>
///
/// <para><b>Классической проблемы «мусор в комментариях» здесь не существует:</b> байткод
/// комментарии не хранит, и на 1 463 424 строки корпуса приходится ровно 2 строки комментариев
/// вне 4-строчных шапок uncompyle6.</para>
///
/// <para>Стоимость: полный проход по 59 МБ — 2.6-4.5 мс параллельно плюс перечитывание дерева
/// (30-50 мс при тёплом кеше ОС), поэтому исходники в памяти держать не требуется.</para>
/// </summary>
public static class XrefSearch
{
    /// <summary>Ищет ссылки.</summary>
    /// <param name="index">Индекс (нужен ради списка файлов и степени омонимии).</param>
    /// <param name="query">Запрос.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат; никогда не <c>null</c>.</returns>
    public static XrefResult Find(ApiIndex index, XrefQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrWhiteSpace(query.Name)) return XrefResult.Empty(string.Empty);

        string name = query.Name.Trim();
        string needle = string.IsNullOrWhiteSpace(query.Receiver) ? name : query.Receiver.Trim() + "." + name;
        if (index.Files.Count == 0) return XrefResult.Empty(needle);

        var sw = Stopwatch.StartNew();
        StringComparison cmp = query.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        HashSet<XrefCategory>? cats = query.Categories is { Count: > 0 } ? [.. query.Categories] : null;

        int fileCount = index.Files.Count;
        var perFile = new List<XrefHit>?[fileCount];
        var perFileReceivers = new Dictionary<string, int>?[fileCount];

        Parallel.For(0, fileCount, new ParallelOptions { CancellationToken = cancellationToken }, fi =>
        {
            SourceFileEntry f = index.Files[fi];
            if (f.Tier == SourceTier.Generated && !query.IncludeGenerated) return;
            if (f.Tier == SourceTier.Vendor && !query.IncludeVendor) return;

            // Читаем мимо кеша: полный проход по дереву не должен вытеснять из него
            // файлы, открытые пользователем.
            string? text = SourceFileService.ReadTextUncached(f.FullPath(index.RootPath));
            if (text is null) return;
            if (text.IndexOf(needle, cmp) < 0) return;

            List<XrefHit>? hits = null;
            Dictionary<string, int>? receivers = null;
            int pos = 0, line = 1, scanned = 0;

            while (true)
            {
                int k = text.IndexOf(needle, pos, cmp);
                if (k < 0) break;
                pos = k + 1;

                // границы идентификатора
                if (k > 0 && IsIdentChar(text[k - 1])) continue;
                int end = k + needle.Length;
                if (end < text.Length && IsIdentChar(text[end])) continue;

                while (scanned < k) { if (text[scanned] == '\n') line++; scanned++; }

                int ls = text.LastIndexOf('\n', Math.Max(0, k - 1));
                ls = ls < 0 ? 0 : ls + 1;
                int le = text.IndexOf('\n', k);
                if (le < 0) le = text.Length;

                string lineText = text[ls..le].TrimEnd('\r');
                int col = k - ls;
                XrefCategory cat = Classify(lineText, col, needle.Length);
                if (cats is not null && !cats.Contains(cat)) continue;

                (hits ??= []).Add(new XrefHit(f.RelativePath, f.VirtualPath, f.FullPath(index.RootPath),
                    line, col, lineText, cat, f.Root, f.Tier));

                string? recv = ReadReceiver(lineText, col);
                if (recv is not null)
                {
                    receivers ??= new Dictionary<string, int>(StringComparer.Ordinal);
                    receivers[recv] = receivers.GetValueOrDefault(recv) + 1;
                }
            }

            perFile[fi] = hits;
            perFileReceivers[fi] = receivers;
        });

        var all = new List<XrefHit>();
        var byCategory = new Dictionary<XrefCategory, int>();
        var receiverTotals = new Dictionary<string, int>(StringComparer.Ordinal);
        int total = 0, filesWithHits = 0;

        for (int i = 0; i < fileCount; i++)
        {
            List<XrefHit>? hits = perFile[i];
            if (hits is null || hits.Count == 0) continue;

            filesWithHits++;
            total += hits.Count;
            foreach (XrefHit h in hits)
            {
                byCategory[h.Category] = byCategory.GetValueOrDefault(h.Category) + 1;
                if (all.Count < query.Limit) all.Add(h);
            }

            if (perFileReceivers[i] is { } rec)
                foreach (KeyValuePair<string, int> kv in rec)
                    receiverTotals[kv.Key] = receiverTotals.GetValueOrDefault(kv.Key) + kv.Value;
        }

        var top = receiverTotals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(10)
            .Select(kv => (kv.Key, kv.Value))
            .ToArray();

        sw.Stop();
        return new XrefResult(needle, all, total, filesWithHits, index.DeclarationCount(name), byCategory, top, sw.Elapsed);
    }

    /// <summary>Асинхронная обёртка: полный проход по дереву уходит в пул потоков.</summary>
    /// <param name="index">Индекс.</param>
    /// <param name="query">Запрос.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат.</returns>
    public static Task<XrefResult> FindAsync(ApiIndex index, XrefQuery query, CancellationToken cancellationToken = default) =>
        Task.Run(() => Find(index, query, cancellationToken), cancellationToken);

    private static bool IsIdentChar(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    /// <summary>
    /// Классифицирует попадание по одной строке: дешёвый проход слева до колонки совпадения.
    /// Отслеживание кавычек здесь безопасно ровно потому, что оно НЕ переносится на следующую
    /// строку — рассинхрон на битом литерале стоит одну строку, а не остаток файла.
    /// </summary>
    /// <param name="line">Строка целиком.</param>
    /// <param name="col">Позиция совпадения.</param>
    /// <param name="length">Длина совпадения.</param>
    /// <returns>Категория.</returns>
    public static XrefCategory Classify(string line, int col, int length)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (col < 0 || col > line.Length) return XrefCategory.Other;

        bool inString = false;
        char quote = '\0';
        for (int i = 0; i < col; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') { i++; continue; }
                if (c == quote) inString = false;
                continue;
            }
            if (c == '#') return XrefCategory.Comment;
            if (c == '"' || c == '\'') { inString = true; quote = c; }
        }
        if (inString) return XrefCategory.StringLiteral;

        ReadOnlySpan<char> head = line.AsSpan(0, col).TrimStart();
        if (head.StartsWith("def ", StringComparison.Ordinal) || head.StartsWith("class ", StringComparison.Ordinal))
            return XrefCategory.Declaration;

        ReadOnlySpan<char> full = line.AsSpan().TrimStart();
        if (full.StartsWith("import ", StringComparison.Ordinal)
            || (full.StartsWith("from ", StringComparison.Ordinal) && line.Contains(" import ", StringComparison.Ordinal)))
            return XrefCategory.Import;

        ReadOnlySpan<char> tail = col + length <= line.Length ? line.AsSpan(col + length) : default;
        int t = 0;
        while (t < tail.Length && tail[t] == ' ') t++;
        if (t < tail.Length && tail[t] == '(') return XrefCategory.Call;
        if (t + 1 < tail.Length && tail[t + 1] == '=')
        {
            if (tail[t] == '+') return XrefCategory.Subscribe;
            if (tail[t] == '-') return XrefCategory.Unsubscribe;
        }
        if (col > 0 && line[col - 1] == '.') return XrefCategory.AttributeRead;
        return XrefCategory.Other;
    }

    /// <summary>
    /// Читает идентификатор-приёмник слева от точки (<c>g_playerEvents</c> в
    /// <c>g_playerEvents.onAvatarReady</c>). Это кандидат для уточнения запроса.
    /// </summary>
    /// <param name="line">Строка.</param>
    /// <param name="col">Позиция совпадения.</param>
    /// <returns>Имя приёмника либо <c>null</c>.</returns>
    public static string? ReadReceiver(string line, int col)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (col <= 0 || col > line.Length || line[col - 1] != '.') return null;

        int end = col - 1;
        int start = end;
        while (start > 0 && IsIdentChar(line[start - 1])) start--;
        return start == end ? null : line[start..end];
    }
}
