using System.Collections.Concurrent;
using System.Text;

namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Кусок исходника вокруг строки — то, что показывается в карточке символа и в списке ссылок.
/// </summary>
/// <param name="FullPath">Абсолютный путь к файлу.</param>
/// <param name="StartLine">Номер первой строки куска, 1-based.</param>
/// <param name="Lines">Строки без завершающих <c>\r\n</c>.</param>
/// <param name="HighlightLine">Строка, ради которой кусок вырезан, 1-based.</param>
public sealed record SourceSnippet(string FullPath, int StartLine, IReadOnlyList<string> Lines, int HighlightLine)
{
    /// <summary>Кусок как единый текст с переводами строк.</summary>
    public string Text => string.Join(Environment.NewLine, Lines);
}

/// <summary>
/// Чтение файлов дерева исходников с кешем в памяти и разбор шапки uncompyle6.
///
/// <para><b>Почему кеш только в памяти и только на файлы.</b> Замерено: полное чтение всех 8052
/// файлов (59.1 МБ) при тёплом кеше ОС — 28-49 мс параллельно, поэтому кеш нужен не ради
/// скорости массовых операций (xref всё равно перечитывает дерево целиком), а ради
/// повторного показа одного и того же файла в карточке символа.</para>
///
/// <para><b>Кеша индекса на диске нет и не будет</b> — см. <see cref="ApiIndexBuilder"/>.</para>
///
/// <para>Класс потокобезопасен.</para>
/// </summary>
public sealed class SourceFileService
{
    private const string EmbeddedMarker = "# Embedded file name: ";

    /// <summary>Шапка uncompyle6 занимает 4 строки; маркер ищется только в начале файла.</summary>
    public const int HeaderScanLength = 512;

    private sealed class Entry
    {
        public required string Text { get; init; }
        public long Touched;
    }

    private readonly ConcurrentDictionary<string, Entry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private long _cachedChars;
    private long _clock;

    /// <summary>Потолок кеша в символах. По умолчанию 16 млн ≈ 32 МБ UTF-16.</summary>
    public long CharBudget { get; init; } = 16_000_000;

    /// <summary>Сколько символов сейчас в кеше.</summary>
    public long CachedChars => Interlocked.Read(ref _cachedChars);

    /// <summary>Сколько файлов сейчас в кеше.</summary>
    public int CachedFiles => _cache.Count;

    /// <summary>
    /// Достаёт виртуальный путь из 4-й строки шапки:
    /// <c># Embedded file name: scripts/client/gui/SystemMessages.py</c>.
    /// Это ровно тот путь, которым оперирует traceback в <c>python.log</c>, поэтому он и есть
    /// мост между браузером API и логом этапа 1. Шапка есть у всех 8052 файлов, но отсутствие
    /// шапки — не ошибка: тогда возвращается пустой спан, а вызывающий откатывается
    /// на относительный путь.
    /// </summary>
    /// <param name="text">Содержимое файла.</param>
    /// <returns>Виртуальный путь либо пустой спан.</returns>
    public static ReadOnlySpan<char> ExtractVirtualPath(ReadOnlySpan<char> text)
    {
        ReadOnlySpan<char> head = text.Length > HeaderScanLength ? text[..HeaderScanLength] : text;
        int i = head.IndexOf(EmbeddedMarker, StringComparison.Ordinal);
        if (i < 0) return default;

        ReadOnlySpan<char> rest = head[(i + EmbeddedMarker.Length)..];
        int e = rest.IndexOfAny('\r', '\n');
        return (e < 0 ? rest : rest[..e]).Trim();
    }

    /// <summary>Читает файл, кладя его в кеш. Ошибки ввода-вывода превращаются в <c>null</c>.</summary>
    /// <param name="fullPath">Абсолютный путь.</param>
    /// <returns>Текст файла либо <c>null</c>.</returns>
    public string? ReadText(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return null;

        if (_cache.TryGetValue(fullPath, out Entry? hit))
        {
            hit.Touched = Interlocked.Increment(ref _clock);
            return hit.Text;
        }

        string? text = ReadTextUncached(fullPath);
        if (text is null) return null;

        var entry = new Entry { Text = text, Touched = Interlocked.Increment(ref _clock) };
        if (_cache.TryAdd(fullPath, entry))
        {
            Interlocked.Add(ref _cachedChars, text.Length);
            if (Interlocked.Read(ref _cachedChars) > CharBudget) Evict();
        }
        return text;
    }

    /// <summary>
    /// Читает файл мимо кеша. Именно так работает xref: полный проход по дереву не должен
    /// вытеснять из кеша файлы, открытые пользователем.
    /// </summary>
    /// <param name="fullPath">Абсолютный путь.</param>
    /// <returns>Текст файла либо <c>null</c>, если файла нет или он недоступен.</returns>
    public static string? ReadTextUncached(string fullPath)
    {
        try
        {
            // Кодировка всего корпуса — валидный UTF-8 (0 файлов с ошибкой декодирования).
            return File.ReadAllText(fullPath, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Вырезает кусок файла вокруг строки.</summary>
    /// <param name="fullPath">Абсолютный путь.</param>
    /// <param name="line">Строка, 1-based.</param>
    /// <param name="before">Сколько строк до.</param>
    /// <param name="after">Сколько строк после.</param>
    /// <returns>Кусок либо <c>null</c>, если файл не прочитался.</returns>
    public SourceSnippet? GetSnippet(string fullPath, int line, int before = 0, int after = 12)
    {
        string? text = ReadText(fullPath);
        if (text is null) return null;

        int start = Math.Max(1, line - before);
        int end = Math.Max(line, start) + after;

        // Идём по границам строк, а не через Split: в корпусе есть файл на 5 МБ, и разбивать
        // его целиком ради пятнадцати строк — это сто тысяч лишних строковых объектов на клик.
        var lines = new List<string>(Math.Max(1, end - start + 1));
        int current = 1, pos = 0;
        while (pos <= text.Length && current <= end)
        {
            int nl = text.IndexOf('\n', pos);
            int stop = nl < 0 ? text.Length : nl;
            if (current >= start) lines.Add(text[pos..stop].TrimEnd('\r'));
            current++;
            if (nl < 0) break;
            pos = nl + 1;
        }

        return lines.Count == 0 ? null : new SourceSnippet(fullPath, start, lines, line);
    }

    /// <summary>Полностью очищает кеш (например при смене дерева исходников).</summary>
    public void Clear()
    {
        _cache.Clear();
        Interlocked.Exchange(ref _cachedChars, 0);
    }

    /// <summary>Выбрасывает половину самых давних записей. Точность здесь не нужна: это кеш.</summary>
    private void Evict()
    {
        KeyValuePair<string, Entry>[] snapshot = _cache.ToArray();
        Array.Sort(snapshot, static (a, b) => a.Value.Touched.CompareTo(b.Value.Touched));

        for (int i = 0; i < snapshot.Length / 2; i++)
        {
            if (_cache.TryRemove(snapshot[i].Key, out Entry? removed))
                Interlocked.Add(ref _cachedChars, -removed.Text.Length);
        }
    }
}
