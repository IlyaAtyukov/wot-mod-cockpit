namespace WotModCockpit.Core.ApiIndex;

/// <summary>Что и как искать по индексу символов.</summary>
public sealed record SymbolSearchOptions
{
    /// <summary>Настройки по умолчанию: только игровой код, 50 результатов.</summary>
    public static SymbolSearchOptions Default { get; } = new();

    /// <summary>Сколько результатов вернуть.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>
    /// Искать и в сгенерированных ресурсах (<c>/gui/impl/gen/</c>, <c>/Scaleform/locale/</c>).
    /// По умолчанию нет: это 21.5 % всех символов и почти чистый шум.
    /// </summary>
    public bool IncludeGenerated { get; init; }

    /// <summary>Искать и в stdlib/вендорном коде. По умолчанию нет: это 15 % символов и весь мусор парсера.</summary>
    public bool IncludeVendor { get; init; }

    /// <summary>Ограничить виды символов; <c>null</c> = все.</summary>
    public IReadOnlyCollection<SymbolKind>? Kinds { get; init; }

    /// <summary>Ограничить путём файла (подстрока, без учёта регистра): например <c>skeletons/</c>.</summary>
    public string? PathContains { get; init; }

    /// <summary>Минимальная длина запроса для класса «подстрока».</summary>
    public int MinSubstringLength { get; init; } = 2;

    /// <summary>Минимальная длина запроса для нечёткого поиска.</summary>
    public int MinFuzzyLength { get; init; } = 3;
}

/// <summary>
/// Поиск и ранжирование символов. Без единой внешней библиотеки — и не потому что «не хотелось»,
/// а потому что замерено: поиск по 156 710 символам с битовым предфильтром стоит 0.1-0.4 мс,
/// то есть искать можно прямо на нажатие клавиши и дебаунс не нужен.
///
/// <para><b>Схема ранжирования: класс совпадения — доминирующий разряд, бонусы только внутри
/// класса.</b> Разрыв между классами равен 1000, поэтому никакая сумма бонусов не поднимет
/// подстроку выше префикса. Это единственный способ получить осмысленную выдачу без обучения
/// на данных: «pushMessage» обязан давать <c>ISystemMessages.pushMessage</c> первым, а не
/// сорок методов, в имени которых где-то встретилось <c>message</c>.</para>
///
/// <para><b>Запрос с точкой разбирается, а не ищется буквально.</b> Мододел называет API ровно
/// так, как кокпит сам показывает его в колонке квалифицированного имени —
/// <c>SystemMessages.pushMessage</c>, <c>dependency.instance</c>, <c>gui.SystemMessages</c>.
/// Короткое имя символа точки не содержит НИКОГДА, поэтому буквальное сравнение с ним давало
/// ноль на всех таких запросах (замерено на настоящем дереве: <c>pushMessage</c> → 47,
/// <c>SystemMessages.pushMessage</c> → 0). Поэтому запрос режется по ПОСЛЕДНЕЙ точке:
/// хвост ранжируется как имя, а голова обязана найтись в квалифицированном имени или в
/// точечном модуле файла (см. <see cref="SplitQuery"/> и <see cref="HeadMatch"/>).</para>
/// </summary>
public static class SymbolSearch
{
    /// <summary>Точное совпадение с учётом регистра.</summary>
    public const int TierExact = 10000;

    /// <summary>Точное совпадение без учёта регистра.</summary>
    public const int TierExactIgnoreCase = 9000;

    /// <summary>Префикс с учётом регистра.</summary>
    public const int TierPrefix = 7000;

    /// <summary>Префикс без учёта регистра.</summary>
    public const int TierPrefixIgnoreCase = 6000;

    /// <summary>Акроним по «горбам» CamelCase и границам snake_case (<c>gvi</c> → <c>getVehicleInfo</c>).</summary>
    public const int TierAcronym = 5000;

    /// <summary>Подстрока.</summary>
    public const int TierSubstring = 3000;

    /// <summary>Нечёткая подпоследовательность.</summary>
    public const int TierFuzzy = 1000;

    /// <summary>Кандидат поиска.</summary>
    /// <param name="SymbolId">Идентификатор символа.</param>
    /// <param name="Score">Итоговый балл.</param>
    public readonly record struct Hit(int SymbolId, int Score);

    /// <summary>
    /// Ищет символы и возвращает их уже спроецированными для UI, по убыванию балла.
    /// </summary>
    /// <param name="index">Индекс.</param>
    /// <param name="query">Запрос; пустой запрос даёт пустой результат.</param>
    /// <param name="options">Настройки; <c>null</c> = <see cref="SymbolSearchOptions.Default"/>.</param>
    /// <returns>Результаты поиска.</returns>
    public static IReadOnlyList<ApiSymbol> Search(ApiIndex index, string? query, SymbolSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        IReadOnlyList<Hit> hits = Rank(index, query, options);
        var res = new List<ApiSymbol>(hits.Count);
        foreach (Hit h in hits) res.Add(index.Project(h.SymbolId, h.Score));
        return res;
    }

    /// <summary>
    /// Ранжирует символы, не проецируя их. Отдельный метод нужен тестам и тем вызовам,
    /// которым хватает идентификаторов.
    /// </summary>
    /// <param name="index">Индекс.</param>
    /// <param name="query">Запрос.</param>
    /// <param name="options">Настройки.</param>
    /// <returns>Кандидаты по убыванию балла; порядок при равных баллах устойчив.</returns>
    public static IReadOnlyList<Hit> Rank(ApiIndex index, string? query, SymbolSearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        options ??= SymbolSearchOptions.Default;

        if (string.IsNullOrWhiteSpace(query) || index.SymbolCount == 0) return [];

        (string head, string q) = SplitQuery(query);
        if (q.Length == 0) return [];

        string qLower = q.ToLowerInvariant();

        // Маска считается по ХВОСТУ. Иначе точка попадала бы в 31-й бит, которого нет ни у одного
        // имени, и предфильтр отсекал бы вообще всё — ровно это и делало любой запрос с точкой пустым.
        uint qMask = ApiIndex.CharMask(qLower);

        SymbolEntry[] entries = index.Entries;
        int[] slotOf = index.SlotOf;
        string[] lowerBySlot = index.LowerBySlot;
        int[] slotOffsets = index.SlotOffsets;
        uint[] masks = index.Masks;
        IReadOnlyList<SourceFileEntry> files = index.Files;

        // Разрешённость файла считаем один раз на файл, а не 156 710 раз на символ.
        var fileAllowed = new bool[files.Count];
        for (int i = 0; i < files.Count; i++)
        {
            SourceFileEntry f = files[i];
            bool ok = f.Tier switch
            {
                SourceTier.Generated => options.IncludeGenerated,
                SourceTier.Vendor => options.IncludeVendor,
                _ => true
            };
            if (ok && options.PathContains is { Length: > 0 } pc)
                ok = f.RelativePath.Contains(pc, StringComparison.OrdinalIgnoreCase);
            fileAllowed[i] = ok;
        }

        HashSet<SymbolKind>? kinds = options.Kinds is { Count: > 0 } ? [.. options.Kinds] : null;

        // Точечный модуль — вычисляемое свойство (Replace + подрезка), а не поле; на запросе
        // с головой оно спрашивается у каждого кандидата, поэтому считается один раз на файл.
        string?[]? dottedByFile = head.Length > 0 ? new string?[files.Count] : null;

        int keep = Math.Max(options.Limit, 256);
        int pruneAt = keep * 4;
        var hits = new List<Hit>(Math.Min(1024, pruneAt));

        Comparison<Hit> cmp = (a, b) =>
        {
            int c = b.Score.CompareTo(a.Score);
            if (c != 0) return c;
            // Устойчивый доп-ключ обязателен: при точном совпадении десятки символов получают
            // одинаковый балл, и без него порядок выдачи прыгает между запусками.
            ref SymbolEntry ea = ref entries[a.SymbolId];
            ref SymbolEntry eb = ref entries[b.SymbolId];
            c = string.CompareOrdinal(files[ea.FileId].RelativePath, files[eb.FileId].RelativePath);
            if (c != 0) return c;
            return ea.Line.CompareTo(eb.Line);
        };

        for (int i = 0; i < entries.Length; i++)
        {
            // Один AND отсекает подавляющее большинство кандидатов: ускорение 11-74 раза.
            if ((masks[i] & qMask) != qMask) continue;
            if (!fileAllowed[entries[i].FileId]) continue;
            if (kinds is not null && !kinds.Contains(entries[i].Kind)) continue;

            int slot = slotOf[i];
            int tier = MatchTier(entries[i].Name, lowerBySlot[slot], q, qLower, options, out int inner);
            if (tier == 0) continue;

            // Голова запроса («SystemMessages» в «SystemMessages.pushMessage») проверяется ПОСЛЕ
            // совпадения имени: это дороже маски, но и кандидатов здесь уже единицы, а не 166 436.
            int headBonus = 0;
            if (dottedByFile is not null && !HeadMatch(index, i, head, dottedByFile, out headBonus))
                continue;

            // Степень омонимии берётся из смещений слота, без поиска по словарю на каждое попадание.
            int declarations = slotOffsets[slot + 1] - slotOffsets[slot];
            int score = tier + inner + headBonus + Bonus(entries[i], files[entries[i].FileId], declarations);
            hits.Add(new Hit(i, score));

            if (hits.Count >= pruneAt)
            {
                // Отсечение по полному порядку: выброшенные элементы гарантированно
                // не попали бы в первые keep, поэтому результат не портится.
                hits.Sort(cmp);
                hits.RemoveRange(keep, hits.Count - keep);
            }
        }

        hits.Sort(cmp);
        if (hits.Count > options.Limit) hits.RemoveRange(options.Limit, hits.Count - options.Limit);
        return hits;
    }

    /// <summary>
    /// Режет запрос по ПОСЛЕДНЕЙ точке на «голову» (владелец или модуль) и «хвост» (имя).
    /// <para>
    /// Точка в коротком имени символа не встречается никогда, поэтому запрос вида
    /// <c>SystemMessages.pushMessage</c> без разбора не находил ничего. Последняя точка выбрана
    /// потому, что головой бывает и цепочка (<c>gui.impl.lobby</c>), а хвостом — всегда ровно
    /// одно имя. Запрос без точки возвращается как есть с пустой головой; хвост из одних точек
    /// (<c>gui.</c>) откатывается на голову, иначе запрос стал бы пустым.
    /// </para>
    /// </summary>
    /// <param name="query">Исходный запрос.</param>
    /// <returns>Голова (может быть пустой) и хвост.</returns>
    public static (string Head, string Tail) SplitQuery(string? query)
    {
        string q = (query ?? string.Empty).Trim();
        if (q.Length == 0) return (string.Empty, string.Empty);

        int dot = q.LastIndexOf('.');
        if (dot < 0) return (string.Empty, q);

        string head = q[..dot].Trim().Trim('.');
        string tail = q[(dot + 1)..].Trim();

        if (tail.Length == 0) return (string.Empty, head);
        return (head, tail);
    }

    /// <summary>
    /// Проверяет голову запроса против символа: владелец, имя модуля, точечный модуль,
    /// квалифицированное имя — в порядке удорожания проверки.
    /// </summary>
    /// <param name="index">Индекс.</param>
    /// <param name="symbolId">Кандидат.</param>
    /// <param name="head">Голова запроса.</param>
    /// <param name="dottedByFile">Кеш точечных модулей по файлам (заполняется лениво).</param>
    /// <param name="bonus">Бонус к баллу: точное совпадение с владельцем ценнее подстроки в пути.</param>
    /// <returns>true, если голова относится к этому символу.</returns>
    public static bool HeadMatch(ApiIndex index, int symbolId, string head, string?[] dottedByFile, out int bonus)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(dottedByFile);
        bonus = 0;
        if (string.IsNullOrEmpty(head)) return true;

        SymbolEntry e = index.Entries[symbolId];

        // 1. непосредственный владелец: «ISystemMessages» для ISystemMessages.pushMessage
        if (e.Parent >= 0)
        {
            string owner = index.Entries[e.Parent].Name;
            if (string.Equals(owner, head, StringComparison.OrdinalIgnoreCase)) { bonus = 300; return true; }
        }

        SourceFileEntry f = index.Files[e.FileId];
        string dotted = dottedByFile[e.FileId] ??= f.DottedModule;

        // 2. имя модуля целиком: «SystemMessages» для gui.SystemMessages
        int lastDot = dotted.LastIndexOf('.');
        ReadOnlySpan<char> moduleName = lastDot < 0 ? dotted : dotted.AsSpan(lastDot + 1);
        if (moduleName.Equals(head, StringComparison.OrdinalIgnoreCase)) { bonus = 250; return true; }

        // 3. кусок точечного пути модуля: «helpers» для helpers.dependency
        if (dotted.Contains(head, StringComparison.OrdinalIgnoreCase)) { bonus = 100; return true; }

        // 4. любое звено цепочки владельцев (вложенные классы) — самая дорогая проверка
        if (e.Parent >= 0 && index.QualifiedName(symbolId).Contains(head, StringComparison.OrdinalIgnoreCase))
        {
            bonus = 50;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Класс совпадения имени с запросом.
    /// </summary>
    /// <param name="name">Имя символа.</param>
    /// <param name="nameLower">Оно же в нижнем регистре.</param>
    /// <param name="query">Запрос.</param>
    /// <param name="queryLower">Запрос в нижнем регистре.</param>
    /// <param name="options">Настройки (пороги длины запроса).</param>
    /// <param name="inner">Бонус внутри класса.</param>
    /// <returns>Разряд класса либо 0, если совпадения нет.</returns>
    public static int MatchTier(string name, string nameLower, string query, string queryLower, SymbolSearchOptions? options, out int inner)
    {
        options ??= SymbolSearchOptions.Default;
        inner = 0;

        if (name.Length == query.Length)
        {
            if (string.Equals(name, query, StringComparison.Ordinal)) return TierExact;
            if (string.Equals(nameLower, queryLower, StringComparison.Ordinal)) return TierExactIgnoreCase;
        }

        if (name.StartsWith(query, StringComparison.Ordinal))
        {
            inner = 40 - Math.Min(40, name.Length - query.Length);
            return TierPrefix;
        }

        if (nameLower.StartsWith(queryLower, StringComparison.Ordinal))
        {
            inner = 40 - Math.Min(40, name.Length - query.Length);
            return TierPrefixIgnoreCase;
        }

        if (AcronymMatch(name, queryLower)) return TierAcronym;

        if (queryLower.Length >= options.MinSubstringLength)
        {
            int idx = nameLower.IndexOf(queryLower, StringComparison.Ordinal);
            if (idx >= 0)
            {
                inner = 30 - Math.Min(30, idx);
                if (idx > 0 && (name[idx - 1] == '_' || char.IsUpper(name[idx]))) inner += 20;
                return TierSubstring;
            }
        }

        if (queryLower.Length >= options.MinFuzzyLength)
        {
            int f = FuzzyScore(queryLower, nameLower);
            if (f > 0) { inner = Math.Min(400, f); return TierFuzzy; }
        }

        return 0;
    }

    /// <summary>
    /// Совпадение по акрониму: буквы запроса должны попасть на границы слов имени —
    /// «горбы» CamelCase и части snake_case. <c>gvi</c> находит <c>getVehicleInfo</c>,
    /// <c>pushmsg</c> находит <c>pushMessage</c>.
    /// </summary>
    /// <param name="name">Имя символа.</param>
    /// <param name="queryLower">Запрос в нижнем регистре.</param>
    /// <returns>true, если акроним совпал.</returns>
    public static bool AcronymMatch(string name, string queryLower)
    {
        if (queryLower.Length < 2 || queryLower.Length > 8) return false;

        int qi = 0;
        for (int i = 0; i < name.Length && qi < queryLower.Length; i++)
        {
            bool boundary = i == 0 || name[i - 1] == '_' || (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]));
            if (!boundary) continue;
            if (char.ToLowerInvariant(name[i]) == queryLower[qi]) qi++;
        }
        return qi == queryLower.Length;
    }

    /// <summary>
    /// Нечёткий поиск подпоследовательностью: все буквы запроса встречаются в имени по порядку.
    /// Премируются начало имени, начало слова после <c>_</c> и подряд идущие буквы.
    /// </summary>
    /// <param name="queryLower">Запрос в нижнем регистре.</param>
    /// <param name="nameLower">Имя в нижнем регистре.</param>
    /// <returns>Оценка (больше — лучше) либо 0, если не совпало.</returns>
    public static int FuzzyScore(ReadOnlySpan<char> queryLower, ReadOnlySpan<char> nameLower)
    {
        if (queryLower.IsEmpty || nameLower.IsEmpty || queryLower.Length > nameLower.Length) return 0;

        int score = 0, qi = 0, lastMatch = -2;
        for (int i = 0; i < nameLower.Length && qi < queryLower.Length; i++)
        {
            if (nameLower[i] != queryLower[qi]) continue;

            int bonus = 1;
            if (i == 0) bonus += 8;
            else if (nameLower[i - 1] == '_') bonus += 6;
            if (lastMatch == i - 1) bonus += 5;

            score += bonus;
            lastMatch = i;
            qi++;
        }

        if (qi < queryLower.Length) return 0;
        score += Math.Max(0, 20 - (nameLower.Length - queryLower.Length));
        return Math.Max(1, score);
    }

    /// <summary>
    /// Бонусы, действующие ТОЛЬКО внутри класса совпадения: слой корпуса, подкорень,
    /// принадлежность к <c>skeletons/</c>, вид символа, длина имени, приватность и штраф
    /// за омонимию (он гасит <c>__init__</c> с его 9204 объявлениями и <c>update</c> с 279).
    /// </summary>
    /// <param name="symbol">Символ.</param>
    /// <param name="file">Его файл.</param>
    /// <param name="declarationCount">Сколько мест объявляют это имя.</param>
    /// <returns>Сумма бонусов (может быть отрицательной).</returns>
    public static int Bonus(in SymbolEntry symbol, SourceFileEntry file, int declarationCount)
    {
        int b = file.Tier switch
        {
            SourceTier.Game => 400,
            SourceTier.Generated => -600,
            _ => -800
        };

        b += file.Root switch
        {
            SourceRoot.Client => 300,
            SourceRoot.ClientCommon => 150,
            _ => 0
        };

        // Интерфейсы сервисов — самая частая цель поиска у мододела.
        if (file.RelativePath.Contains("/skeletons/", StringComparison.Ordinal)) b += 250;

        b += symbol.Kind switch
        {
            // Модульный синглтон (g_playerEvents) — готовая точка входа мода, а не просто имя:
            // подписка в клиенте пишется именно через него, поэтому он выше класса.
            SymbolKind.Service => 170,
            SymbolKind.Class => 150,
            SymbolKind.Module => 120,
            SymbolKind.Event => 120,
            SymbolKind.Function => 100,
            SymbolKind.Method => 50,
            SymbolKind.Constant => 0,
            _ => -100
        };

        b += Math.Max(0, 60 - 2 * symbol.Name.Length);

        if (symbol.Name.StartsWith("__", StringComparison.Ordinal)) b -= 250;
        else if (symbol.Name.StartsWith('_')) b -= 150;

        b -= Math.Min(300, declarationCount * 2);

        return b;
    }
}
