namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Индекс символов клиента, целиком в памяти.
///
/// <para><b>Почему в памяти и без внешних библиотек.</b> Замерено на этой реализации и на этом
/// самом корпусе (8052 файла, 59 МБ, 166 436 символов): полная сборка индекса — около 250 мс,
/// сам индекс — 26.9 МБ, поиск по символам — 0.3-0.9 мс на запрос, полнотекстовый поиск ссылок
/// с перечитыванием дерева — 30-40 мс. SQLite/FTS5/Lucene при таких числах не окупаются ничем:
/// одна только инициализация такой библиотеки стоила бы дороже всего поиска.</para>
///
/// <para>Объект неизменяемый и потокобезопасный: пересборка создаёт новый экземпляр.</para>
/// </summary>
public sealed class ApiIndex
{
    private readonly SymbolEntry[] _entries;
    private readonly SourceFileEntry[] _files;

    // Имена сгруппированы по «слотам» — по одному слоту на УНИКАЛЬНОЕ имя (156 710 символов,
    // но всего 80 724 различных имени). Нижний регистр и маски считаются на слот, а не на символ:
    // иначе только массив lower[] стоил бы лишние 5 МБ и лишние 86 тысяч аллокаций.
    private readonly int[] _slotOf;          // символ -> слот
    private readonly string[] _lowerBySlot;  // слот -> имя в нижнем регистре
    private readonly uint[] _masks;          // символ -> битовая маска (плоско: это горячий цикл поиска)
    private readonly Dictionary<string, int> _nameToSlot;
    private readonly int[] _slotOffsets;     // слот -> начало в _idsBySlot
    private readonly int[] _idsBySlot;       // все символы, сгруппированные по слоту

    private readonly Dictionary<string, int> _fileByVirtualPath;
    private readonly int[] _fileFirst;   // файл -> первый символ
    private readonly int[] _fileLast;    // файл -> последний символ
    private readonly int[] _kindCounts;

    /// <summary>Собирает индекс и достраивает поисковые структуры (нижний регистр, маски, карта имён).</summary>
    /// <param name="rootPath">Корень дерева исходников.</param>
    /// <param name="files">Файлы; порядок задаёт <see cref="SymbolEntry.FileId"/>.</param>
    /// <param name="symbols">Символы; <see cref="SymbolEntry.Parent"/> — индексы в этом же массиве.</param>
    public ApiIndex(string rootPath, SourceFileEntry[] files, SymbolEntry[] symbols)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(symbols);

        RootPath = rootPath ?? string.Empty;
        _files = files;
        _entries = symbols;
        BuiltAt = DateTimeOffset.Now;

        int n = symbols.Length;
        _kindCounts = new int[Enum.GetValues<SymbolKind>().Length];

        // 1. один проход: символ -> слот уникального имени (и заодно счётчик по видам)
        _slotOf = new int[n];
        _nameToSlot = new Dictionary<string, int>(Math.Max(16, n / 2), StringComparer.Ordinal);
        var uniqueNames = new List<string>(Math.Max(16, n / 2));
        for (int i = 0; i < n; i++)
        {
            string name = symbols[i].Name;
            if (!_nameToSlot.TryGetValue(name, out int slot))
            {
                slot = uniqueNames.Count;
                _nameToSlot[name] = slot;
                uniqueNames.Add(name);
            }
            _slotOf[i] = slot;

            int k = (int)symbols[i].Kind;
            if (k < _kindCounts.Length) _kindCounts[k]++;
        }

        // 2. нижний регистр и маска — по одному разу на УНИКАЛЬНОЕ имя
        int u = uniqueNames.Count;
        _lowerBySlot = new string[u];
        var maskBySlot = new uint[u];
        if (u > 4096)
            Parallel.For(0, u, s => Prepare(uniqueNames[s], out _lowerBySlot[s], out maskBySlot[s]));
        else
            for (int s = 0; s < u; s++) Prepare(uniqueNames[s], out _lowerBySlot[s], out maskBySlot[s]);

        _masks = new uint[n];
        for (int i = 0; i < n; i++) _masks[i] = maskBySlot[_slotOf[i]];

        // 3. группировка символов по слотам сортировкой подсчётом — вместо 80 724 отдельных
        //    массивов получается два плоских, и строится это одним линейным проходом
        _slotOffsets = new int[u + 1];
        for (int i = 0; i < n; i++) _slotOffsets[_slotOf[i] + 1]++;
        for (int s = 0; s < u; s++) _slotOffsets[s + 1] += _slotOffsets[s];

        _idsBySlot = new int[n];
        var cursor = new int[u];
        for (int i = 0; i < n; i++)
        {
            int slot = _slotOf[i];
            _idsBySlot[_slotOffsets[slot] + cursor[slot]++] = i;
        }

        // 4. диапазон символов каждого файла. Построитель кладёт символы файла подряд, поэтому
        //    это [min, max] и обход файла становится O(символов файла), а не O(всего индекса).
        _fileFirst = new int[files.Length];
        _fileLast = new int[files.Length];
        Array.Fill(_fileFirst, -1);
        Array.Fill(_fileLast, -1);
        for (int i = 0; i < n; i++)
        {
            int f = symbols[i].FileId;
            if (f < 0 || f >= files.Length) continue;
            if (_fileFirst[f] < 0) _fileFirst[f] = i;
            _fileLast[f] = i;
        }

        _fileByVirtualPath = new Dictionary<string, int>(files.Length, StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < files.Length; i++) _fileByVirtualPath.TryAdd(files[i].VirtualPath, i);
    }

    /// <summary>Пустой индекс: дерева нет либо оно ещё не собрано. Все операции на нём законны.</summary>
    public static ApiIndex Empty { get; } = new(string.Empty, [], []);

    /// <summary>Корень дерева исходников, по которому собран индекс.</summary>
    public string RootPath { get; }

    /// <summary>Момент завершения сборки.</summary>
    public DateTimeOffset BuiltAt { get; }

    /// <summary>Файлы дерева.</summary>
    public IReadOnlyList<SourceFileEntry> Files => _files;

    /// <summary>Сколько символов в индексе.</summary>
    public int SymbolCount => _entries.Length;

    /// <summary>Индекс пуст (дерева нет или сборка не запускалась).</summary>
    public bool IsEmpty => _entries.Length == 0;

    internal SymbolEntry[] Entries => _entries;
    internal uint[] Masks => _masks;
    internal int[] SlotOf => _slotOf;
    internal string[] LowerBySlot => _lowerBySlot;
    internal int[] SlotOffsets => _slotOffsets;

    /// <summary>Имя символа в нижнем регистре (общее для всех символов с таким же именем).</summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <returns>Имя в нижнем регистре.</returns>
    public string LowerName(int symbolId) => _lowerBySlot[_slotOf[symbolId]];

    /// <summary>
    /// Считает нижний регистр и маску для одного уникального имени. Уже строчное имя
    /// возвращается тем же экземпляром: <c>ToLowerInvariant</c> аллоцирует всегда, даже когда
    /// менять нечего, а таких имён в корпусе больше трети.
    /// </summary>
    private static void Prepare(string name, out string lower, out uint mask)
    {
        bool hasUpper = false;
        for (int i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i])) { hasUpper = true; break; }
        }
        lower = hasUpper ? name.ToLowerInvariant() : name;
        mask = CharMask(lower);
    }

    /// <summary>Запись символа по идентификатору.</summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <returns>Копия записи.</returns>
    public SymbolEntry GetEntry(int symbolId) => _entries[symbolId];

    /// <summary>Файл символа.</summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <returns>Файл.</returns>
    public SourceFileEntry GetFile(int symbolId) => _files[_entries[symbolId].FileId];

    /// <summary>Сколько символов данного вида.</summary>
    /// <param name="kind">Вид.</param>
    /// <returns>Количество.</returns>
    public int CountOf(SymbolKind kind)
    {
        int k = (int)kind;
        return k >= 0 && k < _kindCounts.Length ? _kindCounts[k] : 0;
    }

    /// <summary>Сколько символов данного вида пришло из указанного источника.</summary>
    /// <param name="kind">Вид.</param>
    /// <param name="origin">Источник.</param>
    /// <returns>Количество.</returns>
    public int CountOf(SymbolKind kind, SymbolOrigin origin)
    {
        int c = 0;
        foreach (SymbolEntry e in _entries)
            if (e.Kind == kind && e.Origin == origin) c++;
        return c;
    }

    /// <summary>Сколько файлов в указанном подкорне.</summary>
    /// <param name="root">Подкорень.</param>
    /// <returns>Количество файлов.</returns>
    public int FileCountOf(SourceRoot root)
    {
        int c = 0;
        foreach (SourceFileEntry f in _files) if (f.Root == root) c++;
        return c;
    }

    /// <summary>Сколько файлов в указанном слое.</summary>
    /// <param name="tier">Слой.</param>
    /// <returns>Количество файлов.</returns>
    public int FileCountOf(SourceTier tier)
    {
        int c = 0;
        foreach (SourceFileEntry f in _files) if (f.Tier == tier) c++;
        return c;
    }

    /// <summary>
    /// В скольких РАЗНЫХ местах объявлено имя. Это степень омонимии, и её надо показывать
    /// пользователю: у <c>update</c> 279 объявлений, и поиск ссылок по такому имени
    /// без уточнения приёмником заведомо бесполезен.
    /// </summary>
    /// <param name="name">Имя символа.</param>
    /// <returns>Количество объявлений.</returns>
    public int DeclarationCount(string? name) =>
        name is not null && _nameToSlot.TryGetValue(name, out int slot)
            ? _slotOffsets[slot + 1] - _slotOffsets[slot]
            : 0;

    /// <summary>Все символы с точно таким именем.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Идентификаторы символов в порядке объявления; пусто, если имени нет.</returns>
    public IReadOnlyList<int> IdsOf(string? name)
    {
        if (name is null || !_nameToSlot.TryGetValue(name, out int slot)) return Array.Empty<int>();
        return new ArraySegment<int>(_idsBySlot, _slotOffsets[slot], _slotOffsets[slot + 1] - _slotOffsets[slot]);
    }

    /// <summary>Все объявления имени, спроецированные для UI.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Символы.</returns>
    public IReadOnlyList<ApiSymbol> Declarations(string name)
    {
        IReadOnlyList<int> ids = IdsOf(name);
        var res = new List<ApiSymbol>(ids.Count);
        foreach (int id in ids) res.Add(Project(id));
        return res;
    }

    /// <summary>
    /// Квалифицированное имя (<c>ISystemMessages.pushMessage</c>). Собирается по цепочке
    /// родителей в момент вызова — хранить его для 156 710 символов было бы вдвое дороже
    /// самого индекса, а цепочка не длиннее 9 звеньев.
    /// </summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <returns>Имя с владельцами через точку.</returns>
    public string QualifiedName(int symbolId)
    {
        SymbolEntry e = _entries[symbolId];
        if (e.Parent < 0) return e.Name;

        Span<int> chain = stackalloc int[PythonSymbolScanner.MaxDepth];
        int c = 0;
        int p = e.Parent;
        while (p >= 0 && c < chain.Length) { chain[c++] = p; p = _entries[p].Parent; }

        var sb = new System.Text.StringBuilder(64);
        for (int i = c - 1; i >= 0; i--) sb.Append(_entries[chain[i]].Name).Append('.');
        sb.Append(e.Name);
        return sb.ToString();
    }

    /// <summary>Абсолютный путь к файлу символа.</summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <returns>Полный путь.</returns>
    public string FullPathOf(int symbolId) => _files[_entries[symbolId].FileId].FullPath(RootPath);

    /// <summary>Превращает внутреннюю запись в запись для UI.</summary>
    /// <param name="symbolId">Идентификатор символа.</param>
    /// <param name="score">Балл ранжирования, если символ пришёл из поиска.</param>
    /// <returns>Символ.</returns>
    public ApiSymbol Project(int symbolId, int score = 0)
    {
        SymbolEntry e = _entries[symbolId];
        SourceFileEntry f = _files[e.FileId];

        return new ApiSymbol(
            e.Name,
            QualifiedName(symbolId),
            e.Kind,
            f.DottedModule,
            f.VirtualPath,
            f.RelativePath,
            f.FullPath(RootPath),
            e.Line,
            e.Signature)
        {
            SymbolId = symbolId,
            Owner = e.Parent >= 0 ? _entries[e.Parent].Name : null,
            Decorators = e.Decorators,
            Origin = e.Origin,
            Root = f.Root,
            Tier = f.Tier,
            DeclarationCount = DeclarationCount(e.Name),
            Score = score
        };
    }

    /// <summary>
    /// Непосредственные потомки символа (методы класса, вложенные классы, события класса).
    /// Символы одного файла лежат подряд и в порядке строк, поэтому достаточно линейного прохода
    /// по файлу.
    /// </summary>
    /// <param name="symbolId">Идентификатор символа-родителя.</param>
    /// <returns>Потомки в порядке объявления.</returns>
    public IReadOnlyList<ApiSymbol> Children(int symbolId)
    {
        var res = new List<ApiSymbol>();
        int fileId = _entries[symbolId].FileId;
        for (int i = symbolId + 1; i < _entries.Length && _entries[i].FileId == fileId; i++)
            if (_entries[i].Parent == symbolId) res.Add(Project(i));
        return res;
    }

    /// <summary>Все символы файла в порядке объявления.</summary>
    /// <param name="fileId">Идентификатор файла.</param>
    /// <returns>Символы.</returns>
    public IReadOnlyList<ApiSymbol> SymbolsOfFile(int fileId)
    {
        var res = new List<ApiSymbol>();
        if (fileId < 0 || fileId >= _fileFirst.Length || _fileFirst[fileId] < 0) return res;

        for (int i = _fileFirst[fileId]; i <= _fileLast[fileId]; i++)
            if (_entries[i].FileId == fileId) res.Add(Project(i));
        return res;
    }

    /// <summary>
    /// Находит файл по виртуальному пути из traceback. Принимает и путь с префиксом
    /// <c>scripts/</c> (как в логе), и относительный путь дерева (<c>client/...</c>) —
    /// это и есть мост между логом этапа 1 и браузером API.
    /// </summary>
    /// <param name="virtualPath">Например <c>scripts/client/gui/SystemMessages.py</c>.</param>
    /// <returns>Идентификатор файла либо -1.</returns>
    public int FileIdByVirtualPath(string? virtualPath)
    {
        if (string.IsNullOrWhiteSpace(virtualPath)) return -1;

        string vp = virtualPath.Replace('\\', '/').TrimStart('/');
        if (_fileByVirtualPath.TryGetValue(vp, out int id)) return id;
        if (_fileByVirtualPath.TryGetValue("scripts/" + vp, out id)) return id;

        int slash = vp.IndexOf('/');
        if (slash > 0 && _fileByVirtualPath.TryGetValue(vp[(slash + 1)..], out id)) return id;

        for (int i = 0; i < _files.Length; i++)
            if (string.Equals(_files[i].RelativePath, vp, StringComparison.OrdinalIgnoreCase)) return i;

        return -1;
    }

    /// <summary>
    /// Символ, внутри которого находится строка файла: последнее объявление на строке не ниже
    /// указанной. Именно так кадр traceback превращается в карточку метода.
    /// </summary>
    /// <param name="virtualPath">Виртуальный путь из traceback.</param>
    /// <param name="line">Номер строки.</param>
    /// <returns>Символ либо <c>null</c>.</returns>
    public ApiSymbol? EnclosingSymbol(string? virtualPath, int line)
    {
        int fileId = FileIdByVirtualPath(virtualPath);
        if (fileId < 0 || _fileFirst[fileId] < 0) return null;

        int best = -1;
        for (int i = _fileFirst[fileId]; i <= _fileLast[fileId]; i++)
        {
            if (_entries[i].FileId != fileId) continue;
            if (_entries[i].Kind == SymbolKind.Module) continue;
            if (_entries[i].Line > line) break;
            best = i;
        }
        return best < 0 ? null : Project(best);
    }

    /// <summary>
    /// Битовая маска присутствующих символов имени: буквы в биты 0-25, цифры сворачиваются
    /// в 26-30, всё прочее — в бит 31. Маска запроса обязана быть подмножеством маски имени;
    /// один <c>AND</c> отсекает подавляющее большинство кандидатов ещё до сравнения строк.
    /// </summary>
    /// <param name="s">Строка (ожидается в нижнем регистре).</param>
    /// <returns>Маска.</returns>
    public static uint CharMask(ReadOnlySpan<char> s)
    {
        uint m = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c >= 'a' && c <= 'z') m |= 1u << (c - 'a');
            else if (c >= 'A' && c <= 'Z') m |= 1u << (c - 'A');
            else if (c >= '0' && c <= '9') m |= 1u << (26 + (c - '0') % 5);
            else m |= 1u << 31;
        }
        return m;
    }
}
