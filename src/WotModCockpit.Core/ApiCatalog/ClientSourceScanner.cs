using System.Text.RegularExpressions;

namespace WotModCockpit.Core.ApiCatalog;

/// <summary>
/// Один лексический проход по декомпилированному дереву клиента. Собирает СЫРЫЕ факты,
/// из которых <see cref="EventCatalogBuilder"/>, <see cref="ServiceCatalogBuilder"/> и
/// <see cref="HookCatalog"/> строят доменные каталоги.
/// <para>
/// Почему лексический, а не AST: исходники — вывод uncompyle6, и они невалидны НИ как Python 2,
/// НИ как Python 3. Замерено на этом дереве: <c>ast.parse</c> (Python 3) отвергает 595 файлов из 8052
/// (литералы <c>0777</c>/<c>123L</c>, <c>print x</c>, <c>except X, e</c>), а голый <c>return</c>
/// на уровне модуля есть во ВСЕХ 8052 файлах — в настоящем Python 2.7 это тоже SyntaxError.
/// То есть «взять готовый парсер» здесь физически нельзя.
/// </para>
/// <para>
/// Второе жёсткое правило: состояние НЕ переносится между строками (никакого счётчика кавычек
/// или баланса скобок). В 135 файлах uncompyle6 печатает литералы с неэкранированными апострофами,
/// например <c>_logger.warning(bu'Couldn't find 'actualValue' field ...')</c> в
/// <c>client/messenger/formatters/service_channel.py:275</c>. Любой парсер с состоянием на этом
/// рассинхронизируется и МОЛЧА съедает остаток файла (измерено: −391 def в одном файле, −1520 по корпусу).
/// Единственное исключение — восстановление сигнатуры события (<see cref="ScanCallSites"/>),
/// где баланс скобок ведётся локально, максимум на 10 строк, и при неудаче сигнатура просто не выдаётся.
/// </para>
/// <para>Отступ считается как в эталонных питон-извлекателях: пробел = 1, табуляция = 8.</para>
/// </summary>
public sealed class ClientSourceScanner
{
    /// <summary>Правило E1: объявление события <c>self.onX = Event.Event(...)</c>.</summary>
    private static readonly Regex EventDecl = new(
        @"^(?<indent>[ \t]*)(?<target>(?:self|cls)\.(?<priv>_{0,2})(?<name>[A-Za-z_][A-Za-z_0-9]*)" +
        @"|(?<gname>[A-Za-z_][A-Za-z_0-9]*))\s*=\s*(?:Event\.)?(?<kind>Event|SafeEvent|SuspendedEvent)\s*\((?<args>[^)]*)\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Двоеточие необязательно: у части классов список баз перенесён на следующую строку,
    // и требование ':' теряло такие классы для проверки горячих точек.
    private static readonly Regex ClassDecl = new(
        @"^(?<indent>[ \t]*)class\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*(?:\((?<bases>[^)]*)\))?\s*:?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DefDecl = new(
        @"^(?<indent>[ \t]*)def\s+(?<name>[A-Za-z_][A-Za-z_0-9]*)\s*\((?<args>[^)]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>R1: <c>g_playerEvents = _PlayerEvents()</c> на нулевом отступе.</summary>
    private static readonly Regex GlobalSingleton = new(
        @"^(?<g>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*(?<cls>[A-Za-z_][A-Za-z_0-9]*)\s*\(\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>R5: <c>self.__users = _UsersSharedEvents()</c> внутри класса-родителя.</summary>
    private static readonly Regex NestedAssign = new(
        @"^[ \t]+self\.(?<attr>_{0,2}[A-Za-z_0-9]+)\s*=\s*(?<child>[A-Za-z_][A-Za-z_0-9]*)\s*\(\s*\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>E3-bis: <c>period = property((lambda self: self.__periodInfo[0]))</c>.</summary>
    private static readonly Regex TupleProperty = new(
        @"^[ \t]+(?<n>[A-Za-z_][A-Za-z_0-9]*)\s*=\s*property\(\(?lambda self: self\.(?<attr>_{1,2}[A-Za-z_0-9]+)\[(?<i>\d+)\]\)?\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Сервисы: <c>dependency.instance(IItemsCache)</c> / <c>dependency.descriptor(...)</c>.</summary>
    private static readonly Regex DependencyCall = new(
        @"dependency\.(?<how>instance|descriptor)\s*\(\s*(?<t>[A-Za-z_][A-Za-z_0-9.]*)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>S4: <c>return self._repository.getController(BATTLE_CTRL_ID.AMMO)</c>.</summary>
    private static readonly Regex GetController = new(
        @"getController\(BATTLE_CTRL_ID\.(?<id>[A-Za-z_0-9]+)\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>S4: <c>def getControllerID(self): return BATTLE_CTRL_ID.AMMO</c>.</summary>
    private static readonly Regex ReturnControllerId = new(
        @"return BATTLE_CTRL_ID\.(?<id>[A-Za-z_0-9]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ReturnSelfAttr = new(
        @"^[ \t]*return self\.(?<attr>_{0,2}[A-Za-z_0-9]+)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string EmbeddedMarker = "# Embedded file name: ";

    /// <summary>Классы-локаторы боевых контроллеров (реализация, не skeleton).</summary>
    private static readonly string[] LocatorClasses = { "SharedControllersLocator", "DynamicControllersLocator" };

    /// <summary>
    /// Обходит дерево и разбирает каждый <c>.py</c>. Не бросает при отсутствующем каталоге:
    /// возвращает пустой скан со <see cref="ClientSourceScan.Root"/> = переданный путь.
    /// </summary>
    /// <param name="root">Корень дерева uncompyle6 (внутри — <c>client/</c>, <c>client_common/</c>, <c>common/</c>).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Скан; при отсутствии дерева — пустой, но валидный объект.</returns>
    public ClientSourceScan Scan(string? root, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return ClientSourceScan.Empty(root ?? string.Empty);

        string[] paths;
        try
        {
            paths = Directory.GetFiles(root, "*.py", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ClientSourceScan.Empty(root);
        }

        var files = new ScannedSourceFile?[paths.Length];
        int skipped = 0;

        Parallel.For(0, paths.Length, new ParallelOptions { CancellationToken = ct }, i =>
        {
            try
            {
                files[i] = ScanFile(paths[i], MakeRelative(root, paths[i]));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Один плохой файл не имеет права уронить построение всех трёх каталогов.
                // Ловится ВСЁ, кроме отмены: у ApiIndexBuilder ровно то же правило, а узкий
                // фильтр (IOException/UnauthorizedAccess/OutOfMemory) выпускал наружу любую
                // другую ошибку лексера, и один файл превращал весь снимок в статус Failed.
                // Напоминание, зачем это нужно: 10 файлов из 8052 декомпилировались с ошибками
                // прямо в тело кода. Число пропущенных доезжает до UI через SkippedFiles.
                Interlocked.Increment(ref skipped);
            }
        });

        var ok = new List<ScannedSourceFile>(paths.Length);
        foreach (ScannedSourceFile? f in files)
        {
            if (f is not null)
                ok.Add(f);
        }

        ok.Sort(static (a, b) => string.CompareOrdinal(a.RelPath, b.RelPath));
        return new ClientSourceScan(root, ok, skipped);
    }

    private static string MakeRelative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>Разбирает один файл. Публичен ради тестов на патологических входах.</summary>
    /// <param name="path">Путь к файлу на диске.</param>
    /// <param name="relPath">Путь относительно корня дерева, с прямыми слэшами.</param>
    /// <returns>Разобранный файл.</returns>
    public static ScannedSourceFile ScanFile(string path, string relPath)
    {
        string text = File.ReadAllText(path);
        return ParseText(text, relPath);
    }

    /// <summary>
    /// Разбирает уже прочитанный текст. Отдельный метод, чтобы тесты могли скармливать
    /// патологические входы без файлов на диске.
    /// </summary>
    /// <param name="text">Содержимое файла.</param>
    /// <param name="relPath">Путь относительно корня дерева, с прямыми слэшами.</param>
    /// <returns>Разобранный файл.</returns>
    public static ScannedSourceFile ParseText(string text, string relPath)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(relPath);

        string[] lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (raw.Length > 0 && raw[^1] == '\r')
                lines[i] = raw[..^1];
        }

        var file = new ScannedSourceFile(relPath, ReadVirtualPath(text), ToModuleName(relPath));

        // Стек (отступ, это класс?, имя) — только для правила E1 (владелец события).
        var stack = new List<(int Indent, bool IsClass, string Name)>(16);
        ScannedClass? curTop = null;          // последний класс НУЛЕВОГО отступа
        string? pendingLocatorSlot = null;    // имя property внутри *ControllersLocator
        string? pendingPropName = null;       // имя property, ждём `return self.__attr`
        int pendingPropLine = -1;
        bool pendingControllerId = false;
        bool isLocatorFile = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            int lineNo = i + 1;

            // Пустая строка и комментарий стек не трогают. КРИТИЧНО: если не срезать '\r',
            // пустая строка будет равна "\r", получит отступ 0 и разрушит стек на каждом методе.
            string st = line.TrimStart(' ', '\t');
            if (st.Length == 0 || st[0] == '#')
                continue;

            int ind = IndentOf(line);

            // --- отложенные двухстрочные правила -----------------------------------
            if (pendingControllerId)
            {
                Match rc = ReturnControllerId.Match(line);
                if (rc.Success && curTop is not null)
                {
                    file.AddControllerId(curTop.Name, rc.Groups["id"].Value);
                    pendingControllerId = false;
                }
                else if (ind <= 4)
                {
                    pendingControllerId = false;
                }
            }

            if (pendingPropName is not null)
            {
                if (lineNo - pendingPropLine <= 3)
                {
                    Match ra = ReturnSelfAttr.Match(line);
                    if (ra.Success && curTop is not null)
                    {
                        file.AddPropertyAccessor(curTop.Name, ra.Groups["attr"].Value, pendingPropName);
                        pendingPropName = null;
                    }
                }
                else
                {
                    pendingPropName = null;
                }
            }

            if (pendingLocatorSlot is not null)
            {
                Match gc = GetController.Match(line);
                if (gc.Success && curTop is not null)
                {
                    string kind = curTop.Name.StartsWith("Shared", StringComparison.Ordinal) ? "shared" : "dynamic";
                    file.AddLocatorSlot(new LocatorSlot(kind, pendingLocatorSlot, gc.Groups["id"].Value));
                    pendingLocatorSlot = null;
                }
                else if (ind <= 4 && st.StartsWith("def ", StringComparison.Ordinal))
                {
                    pendingLocatorSlot = null;
                }
            }

            // --- стек владельцев (E1) ----------------------------------------------
            while (stack.Count > 0 && stack[^1].Indent >= ind)
                stack.RemoveAt(stack.Count - 1);

            // --- class -------------------------------------------------------------
            if (st.StartsWith("class ", StringComparison.Ordinal))
            {
                Match mc = ClassDecl.Match(line);
                if (mc.Success)
                {
                    string name = mc.Groups["name"].Value;
                    stack.Add((ind, true, name));
                    if (ind == 0)
                    {
                        curTop = new ScannedClass(name, mc.Groups["bases"].Value.Trim(), lineNo);
                        file.AddClass(curTop);
                        isLocatorFile = Array.IndexOf(LocatorClasses, name) >= 0;
                    }

                    continue;
                }
            }

            // --- def ---------------------------------------------------------------
            if (st.StartsWith("def ", StringComparison.Ordinal))
            {
                Match md = DefDecl.Match(line);
                if (md.Success)
                {
                    string name = md.Groups["name"].Value;
                    string args = md.Groups["args"].Value;
                    stack.Add((ind, false, name));

                    if (ind == 0)
                    {
                        file.AddModuleFunction(new ScannedFunction(name, lineNo, args));
                        curTop = null;
                        isLocatorFile = false;
                    }
                    else if (ind == 4 && curTop is not null)
                    {
                        curTop.Methods[name] = new ScannedFunction(name, lineNo, args);
                        if (name == "getControllerID")
                            pendingControllerId = true;
                        if (isLocatorFile && args.Trim() == "self")
                            pendingLocatorSlot = name;
                        if (args.Trim() == "self")
                        {
                            pendingPropName = name;
                            pendingPropLine = lineNo;
                        }
                    }

                    continue;
                }
            }

            // --- R1: модульный синглтон --------------------------------------------
            if (ind == 0 && line.EndsWith("()", StringComparison.Ordinal))
            {
                Match ms = GlobalSingleton.Match(line);
                if (ms.Success)
                {
                    file.AddSingleton(new ModuleSingleton(ms.Groups["g"].Value, ms.Groups["cls"].Value, lineNo));
                    continue;
                }
            }

            // --- E1: объявление события ---------------------------------------------
            if (line.Contains("Event(", StringComparison.Ordinal))
            {
                Match me = EventDecl.Match(line);
                if (me.Success)
                {
                    string? cls = null;
                    string? fn = null;
                    foreach ((int _, bool isClass, string name) in stack)
                    {
                        if (isClass)
                            cls = name;
                        else
                            fn = name;
                    }

                    bool selfBound = me.Groups["name"].Success;

                    // Без self./cls. слева объявлением события считается только строка в теле
                    // класса или модуля. Внутри метода это ЛОКАЛЬНАЯ ПЕРЕМЕННАЯ:
                    // client/BattleReplay.py:1330 «eventHandler = Event.Event()» внутри
                    // setDataCallback давал каталогу несуществующее событие класса BattleReplay.
                    if (!selfBound && stack.Count > 0 && !stack[^1].IsClass)
                        continue;

                    file.AddEvent(new RawEventDecl(
                        selfBound ? me.Groups["name"].Value : me.Groups["gname"].Value,
                        me.Groups["priv"].Success ? me.Groups["priv"].Value.Length : 0,
                        me.Groups["kind"].Value,
                        cls,
                        fn,
                        selfBound,
                        me.Groups["args"].Value.Trim(),
                        lineNo));
                    continue;
                }
            }

            // --- R5: вложенный владелец ---------------------------------------------
            if (curTop is not null && line.EndsWith("()", StringComparison.Ordinal) && st.StartsWith("self.", StringComparison.Ordinal))
            {
                Match mn = NestedAssign.Match(line);
                if (mn.Success)
                    file.AddNestedOwner(new NestedOwner(mn.Groups["child"].Value, curTop.Name, mn.Groups["attr"].Value, lineNo));
            }

            // --- E3-bis: property, раскрывающая кортеж -------------------------------
            if (curTop is not null && line.Contains("property(", StringComparison.Ordinal))
            {
                Match mt = TupleProperty.Match(line);
                if (mt.Success)
                {
                    file.AddTupleProperty(
                        curTop.Name,
                        mt.Groups["attr"].Value,
                        int.Parse(mt.Groups["i"].Value, System.Globalization.CultureInfo.InvariantCulture),
                        mt.Groups["n"].Value);
                }
            }

            // --- S2: обращения к DI ---------------------------------------------------
            if (line.Contains("dependency.", StringComparison.Ordinal))
            {
                foreach (Match m in DependencyCall.Matches(line))
                {
                    string t = m.Groups["t"].Value;
                    int dot = t.LastIndexOf('.');
                    if (dot >= 0)
                        t = t[(dot + 1)..];
                    file.AddDependencyUse(new DependencyUse(t, m.Groups["how"].Value == "descriptor"));
                }
            }

            // --- подписки / отписки ----------------------------------------------------
            TryAddSubscription(file, line, lineNo);
        }

        // Второй проход по ЭТОМУ ЖЕ файлу: места возбуждения событий (правило E3).
        // Идёт отдельным проходом потому, что имена событий и property-раскрыватели кортежей
        // становятся известны только после первого прохода.
        ScanCallSites(lines, file);

        return file;
    }

    /// <summary>Отступ: пробел = 1, табуляция = 8 (как в эталонных извлекателях).</summary>
    private static int IndentOf(string s)
    {
        int n = 0;
        foreach (char c in s)
        {
            if (c == ' ')
                n++;
            else if (c == '\t')
                n += 8;
            else
                break;
        }

        return n;
    }

    private static void TryAddSubscription(ScannedSourceFile file, string line, int lineNo)
    {
        int pos = line.IndexOf("+=", StringComparison.Ordinal);
        bool remove = false;
        if (pos < 0)
        {
            pos = line.IndexOf("-=", StringComparison.Ordinal);
            remove = true;
        }

        if (pos <= 0)
            return;

        ReadOnlySpan<char> left = line.AsSpan(0, pos).TrimEnd();
        int dot = left.LastIndexOf('.');
        if (dot <= 0 || dot == left.Length - 1)
            return;

        ReadOnlySpan<char> name = left[(dot + 1)..];
        if (!IsIdentifier(name))
            return;

        ReadOnlySpan<char> recv = left[..dot].TrimStart();
        if (recv.Length == 0 || recv.Length > 80)
            return;

        foreach (char c in recv)
        {
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.' && c != '(' && c != ')' && c != '[' && c != ']' && c != '\'')
                return;
        }

        file.AddSubscription(new EventSubscription(recv.ToString(), name.ToString(), remove, lineNo));
    }

    private static bool IsIdentifier(ReadOnlySpan<char> s)
    {
        if (s.Length == 0 || (!char.IsLetter(s[0]) && s[0] != '_'))
            return false;

        foreach (char c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    /// <summary>
    /// Правило E3: сигнатуру события даёт не объявление (там всегда пусто), а место возбуждения.
    /// Собираются ДВА вида мест, и оба скоупятся по получателю:
    /// <list type="bullet">
    ///   <item><c>self.onVehicleKilled(victimID, ...)</c> внутри класса-владельца — основной случай;</item>
    ///   <item><c>g_playerEvents.onArenaPeriodChange(*self.__periodInfo)</c> — событие объявлено
    ///         в одном файле, а возбуждается в другом через глобальный синглтон. Без этого вида
    ///         события <c>_PlayerEvents</c> (80 штук, самые ходовые) остаются вообще без сигнатуры.</item>
    /// </list>
    /// Без скоупинга <c>onVehicleHealthChanged</c> арены получает сигнатуру от <c>self.appearance</c>
    /// — это проверено, ровно так и происходит.
    /// </summary>
    private static void ScanCallSites(string[] lines, ScannedSourceFile file)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (RawEventDecl e in file.Events)
            names.Add(e.Name);

        var stack = new List<(int Indent, bool IsClass, string Name)>(16);

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string st = line.TrimStart(' ', '\t');
            if (st.Length == 0 || st[0] == '#')
                continue;

            int ind = IndentOf(line);
            while (stack.Count > 0 && stack[^1].Indent >= ind)
                stack.RemoveAt(stack.Count - 1);

            if (st.StartsWith("class ", StringComparison.Ordinal))
            {
                Match mc = ClassDecl.Match(line);
                if (mc.Success)
                {
                    stack.Add((ind, true, mc.Groups["name"].Value));
                    continue;
                }
            }

            if (st.StartsWith("def ", StringComparison.Ordinal))
            {
                Match md = DefDecl.Match(line);
                if (md.Success)
                {
                    stack.Add((ind, false, md.Groups["name"].Value));
                    continue;
                }
            }

            string owner = string.Empty;
            foreach ((int _, bool isClass, string name) in stack)
            {
                if (isClass)
                    owner = name;
            }

            // (а) self.<event>(...) внутри класса-владельца.
            if (owner.Length > 0 && names.Count > 0)
            {
                int from = 0;
                while (true)
                {
                    int p = line.IndexOf("self.", from, StringComparison.Ordinal);
                    if (p < 0)
                        break;

                    from = p + 5;
                    if (p > 0 && (char.IsLetterOrDigit(line[p - 1]) || line[p - 1] == '_' || line[p - 1] == '.'))
                        continue;

                    int e = from;
                    while (e < line.Length && (char.IsLetterOrDigit(line[e]) || line[e] == '_'))
                        e++;

                    if (e >= line.Length || line[e] != '(')
                        continue;

                    string called = line[from..e].TrimStart('_');
                    if (!names.Contains(called))
                        continue;

                    IReadOnlyList<string>? args = ReadArgs(lines, i, e);
                    if (args is not null)
                        file.AddCallSite(new RawCallSite("self", owner, called, Expand(args, owner, file), i + 1));
                }
            }

            // (б) g_singleton[.attr].<event>(...) — возбуждение через глобальный синглтон.
            if (line.Contains("g_", StringComparison.Ordinal))
                ScanGlobalCallSites(lines, i, owner, file);
        }
    }

    /// <summary>
    /// Ищет в строке вызовы вида <c>g_playerEvents.onX(...)</c> и <c>g_messengerEvents.users.onX(...)</c>.
    /// Ограничение получателя корнем <c>g_</c> сделано намеренно: именно к глобальным синглтонам
    /// мод и получает доступ (резолверы R1 и R5), а сплошной сбор всех вызовов <c>x.y(</c> дал бы
    /// миллионы бесполезных записей.
    /// </summary>
    private static void ScanGlobalCallSites(string[] lines, int lineIdx, string ownerClass, ScannedSourceFile file)
    {
        string line = lines[lineIdx];
        int from = 0;
        while (true)
        {
            int p = line.IndexOf("g_", from, StringComparison.Ordinal);
            if (p < 0)
                return;

            from = p + 2;
            if (p > 0 && (char.IsLetterOrDigit(line[p - 1]) || line[p - 1] == '_' || line[p - 1] == '.'))
                continue;

            int e = p;
            var segments = new List<string>(3);
            while (true)
            {
                int s = e;
                while (e < line.Length && (char.IsLetterOrDigit(line[e]) || line[e] == '_'))
                    e++;

                if (e == s)
                    break;

                segments.Add(line[s..e]);
                if (e < line.Length && line[e] == '.')
                {
                    e++;
                    continue;
                }

                break;
            }

            from = Math.Max(from, e);
            if (segments.Count < 2 || e >= line.Length || line[e] != '(')
                continue;

            string name = segments[^1];
            if (!name.StartsWith("on", StringComparison.Ordinal))
                continue;

            IReadOnlyList<string>? args = ReadArgs(lines, lineIdx, e);
            if (args is null)
                continue;

            string receiver = string.Join('.', segments.Take(segments.Count - 1));
            file.AddCallSite(new RawCallSite(receiver, ownerClass, name, Expand(args, ownerClass, file), lineIdx + 1));
        }
    }

    /// <summary>
    /// Правило E3-bis: <c>g_playerEvents.onArenaPeriodChange(*self.__periodInfo)</c> раскрывается по
    /// property того же класса — <c>ClientArena.py:111-114</c> даёт
    /// <c>(period, periodEndTime, periodLength, periodAdditionalInfo)</c>. Именно это делает
    /// карточку события полезной вместо бесполезного <c>(*self.__periodInfo)</c>.
    /// </summary>
    private static IReadOnlyList<string> Expand(IReadOnlyList<string> args, string ownerClass, ScannedSourceFile file)
    {
        if (args.Count != 1 || ownerClass.Length == 0)
            return args;

        string a = args[0];
        if (a.Length < 7 || a[0] != '*' || a[1] == '*')
            return args;

        string expr = a[1..].Trim();
        if (!expr.StartsWith("self.", StringComparison.Ordinal))
            return args;

        string attr = expr[5..];
        return file.TupleProperties.TryGetValue(ownerClass + "|" + attr, out SortedList<int, string>? props) && props.Count > 0
            ? new List<string>(props.Values)
            : args;
    }

    /// <summary>
    /// Читает список аргументов вызова, начиная с открывающей скобки. Баланс скобок ведётся
    /// ЛОКАЛЬНО и не более чем на 10 строк — при неудаче возвращается <c>null</c>, и событие
    /// просто остаётся без сигнатуры. Так поломанный литерал портит одну карточку, а не файл.
    /// </summary>
    private static IReadOnlyList<string>? ReadArgs(string[] lines, int lineIdx, int openParen)
    {
        var buf = new System.Text.StringBuilder(128);
        int depth = 0;
        var parts = new List<string>();
        int start = 0;

        for (int li = lineIdx; li < lines.Length && li < lineIdx + 10; li++)
        {
            string s = lines[li];
            int from = li == lineIdx ? openParen : 0;
            for (int i = from; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[' || c == '{')
                {
                    depth++;
                    if (depth == 1)
                    {
                        start = buf.Length;
                        continue;
                    }
                }
                else if (c == ')' || c == ']' || c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        parts.Add(buf.ToString(start, buf.Length - start));
                        return SplitTopLevel(parts[^1]);
                    }

                    if (depth < 0)
                        return null;
                }

                if (depth >= 1)
                    buf.Append(c);
            }

            buf.Append(' ');
        }

        return null;
    }

    private static IReadOnlyList<string> SplitTopLevel(string inner)
    {
        var res = new List<string>();
        int depth = 0;
        int start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '(' || c == '[' || c == '{')
                depth++;
            else if (c == ')' || c == ']' || c == '}')
                depth--;
            else if (c == ',' && depth == 0)
            {
                res.Add(inner[start..i].Trim());
                start = i + 1;
            }
        }

        string tail = inner[start..].Trim();
        if (tail.Length > 0)
            res.Add(tail);

        for (int i = res.Count - 1; i >= 0; i--)
        {
            if (res[i].Length == 0)
                res.RemoveAt(i);
        }

        return res;
    }

    /// <summary>
    /// Виртуальный путь из 4-й строки шапки uncompyle6. Есть во всех 8052 файлах дерева;
    /// именно им оперирует traceback в <c>python.log</c>, поэтому это мост к этапу 1.
    /// </summary>
    internal static string? ReadVirtualPath(string text)
    {
        int limit = Math.Min(text.Length, 512);
        int p = text.AsSpan(0, limit).IndexOf(EmbeddedMarker, StringComparison.Ordinal);
        if (p < 0)
            return null;

        p += EmbeddedMarker.Length;
        int e = p;
        while (e < text.Length && text[e] != '\r' && text[e] != '\n')
            e++;

        string v = text[p..e].Trim();
        return v.Length == 0 ? null : v;
    }

    /// <summary>
    /// Импортируемое имя модуля: <c>client/gui/SystemMessages.py</c> → <c>gui.SystemMessages</c>.
    /// Первый сегмент физического пути (<c>client/</c>, <c>client_common/</c>, <c>common/</c>) —
    /// это корень дерева, а не пакет, и его надо отбросить.
    /// </summary>
    internal static string ToModuleName(string relPath)
    {
        string s = relPath.Replace('\\', '/');
        int slash = s.IndexOf('/');
        if (slash >= 0)
            s = s[(slash + 1)..];

        if (s.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            s = s[..^3];

        s = s.Replace('/', '.');
        if (s.EndsWith(".__init__", StringComparison.Ordinal))
            s = s[..^9];
        else if (s == "__init__")
            s = string.Empty;

        return s;
    }
}
