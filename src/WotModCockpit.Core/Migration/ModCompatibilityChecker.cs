using System.Text.RegularExpressions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Preflight;
using ClientIndex = WotModCockpit.Core.ApiIndex.ApiIndex;

namespace WotModCockpit.Core.Migration;

/// <summary>Вердикт проверки мода на новой версии клиента.</summary>
public enum CompatibilityVerdict
{
    /// <summary>Ничего из того, на что опирается мод, в новой версии не сломалось.</summary>
    Compatible,

    /// <summary>Есть хотя бы одна блокирующая проблема — автоматически не переносится.</summary>
    Incompatible,
}

/// <summary>Одна находка проверки.</summary>
/// <param name="Blocking">Блокирует ли автоматический перенос.</param>
/// <param name="Kind">Вид: модуль удалён, сигнатура, константа, preflight…</param>
/// <param name="Message">Что случилось — по-русски, для человека.</param>
public sealed record CompatibilityIssue(bool Blocking, string Kind, string Message)
{
    /// <summary>Где в моде (<c>gui/mods/x/main.py:12</c>).</summary>
    public string? ModLocation { get; init; }

    /// <summary>Где в клиенте (<c>client/Avatar.py:1604</c>).</summary>
    public string? ClientLocation { get; init; }

    /// <summary>Строка для журнала и подсказки.</summary>
    public override string ToString() =>
        (Blocking ? "БЛОКИРУЕТ: " : "заметка: ") + Message +
        (ModLocation is null ? string.Empty : " [мод " + ModLocation + "]") +
        (ClientLocation is null ? string.Empty : " [клиент " + ClientLocation + "]");
}

/// <summary>Итог проверки одного мода.</summary>
/// <param name="ProjectDir">Каталог проекта.</param>
/// <param name="ModId">Идентификатор мода.</param>
/// <param name="DisplayName">Имя мода.</param>
/// <param name="Verdict">Вердикт.</param>
/// <param name="Issues">Находки — блокирующие и заметки.</param>
/// <param name="ClientModules">Модули клиента, которые мод импортирует.</param>
/// <param name="ChangedClientModules">Из них изменились в новой версии.</param>
public sealed record ModCompatibilityReport(
    string ProjectDir,
    string ModId,
    string DisplayName,
    CompatibilityVerdict Verdict,
    IReadOnlyList<CompatibilityIssue> Issues,
    IReadOnlyList<string> ClientModules,
    IReadOnlyList<string> ChangedClientModules)
{
    /// <summary>Можно переносить автоматически.</summary>
    public bool IsCompatible => Verdict == CompatibilityVerdict.Compatible;

    /// <summary>Блокирующие находки.</summary>
    public IReadOnlyList<CompatibilityIssue> BlockingIssues => Issues.Where(i => i.Blocking).ToArray();
}

/// <summary>
/// Проверка своего мода на новой версии клиента: что из клиента он использует и что из этого
/// в новой версии сломалось. Только чтение — ни мод, ни деревья не меняются.
/// <para>
/// Блокирует перенос:
/// </para>
/// <list type="bullet">
///   <item>модуль клиента, который мод импортирует, в новой версии исчез;</item>
///   <item>в таком модуле исчезли функция, класс, метод, событие, константа или синглтон
///         (<c>g_*</c>), которые мод упоминает (метод — только вместе с именем его класса:
///         <c>update</c> у сотни классов не повод для тревоги; закрытый метод — и в искажённом
///         виде <c>_Class__name</c>, в котором его перехватывают);</item>
///   <item>новые исходники пусты — проверять не по чему;</item>
///   <item>у такой функции или метода поменялась сигнатура — перехваты берут аргументы по
///         позиции, и сдвиг тихо подменил бы один аргумент другим;</item>
///   <item>новая ошибка preflight, которой на старой версии не было.</item>
/// </list>
/// <para>
/// Заметки, перенос не блокируют: изменившиеся модули, которые мод импортирует, новое значение
/// упомянутой константы, новые предупреждения preflight. Сравнивается ТОЛЬКО новое: давние
/// замечания мода на переход не влияют.
/// </para>
/// <para>
/// Граница метода: связь «объект → класс» без исполнения не установить, поэтому атрибуты
/// объектов, полученных вызовами (<c>BigWorld.player().arena.positions</c>), проверяются
/// только если мод импортирует модуль, где они объявлены. Это проверка-сито, а не доказательство:
/// её задача — не пустить автоматом то, что сломано заметно.
/// </para>
/// </summary>
public sealed class ModCompatibilityChecker
{
    private readonly ClientIndex? _old;
    private readonly ClientIndex _new;
    private readonly SourceFileService _files;
    private readonly PreflightAnalyzer _analyzer;
    private readonly ClientApiOracle? _oldOracle;
    private readonly ClientApiOracle _newOracle;
    private readonly Dictionary<string, int> _oldModules;
    private readonly Dictionary<string, int> _newModules;
    private readonly HashSet<string> _newExtra;
    private readonly HashSet<string> _newFailed;
    private readonly HashSet<string> _oldFailed;

    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    /// <summary>Неразобранная функция: <c>def имяParse error at or near …</c> или <c>имя = Parse error at or near …</c>.</summary>
    private static readonly Regex UnparsedDef = new(
        @"^[ \t]*(?:def[ \t]+(?<name>[A-Za-z_]\w*?)|(?<name>[A-Za-z_]\w*)[ \t]*=[ \t]*)Parse error at or near",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Объявление класса — для привязки неразобранной функции к её классу.</summary>
    private static readonly Regex ClassLine = new(@"^[ \t]*class[ \t]+(?<name>[A-Za-z_]\w*)", RegexOptions.Compiled);

    /// <summary><c>g_x = _Owner(…)</c> — синглтон, созданный из класса того же модуля.</summary>
    private static readonly Regex SingletonOfClass = new(@"^\s*\w+\s*=\s*(?<cls>[A-Za-z_]\w*)\s*\(", RegexOptions.Compiled);

    /// <summary>
    /// Вид заметки «модуль, который импортирует мод, сравнить не с чем»: его нет в исходниках
    /// прошлой версии, он не декомпилировался в новой или переехал в дополнительный пакет. Сама по
    /// себе она перенос не блокирует, но мод с ней проверен не до конца — автоматически не переносится.
    /// </summary>
    public const string UnverifiedKind = "сравнить не с чем";

    /// <summary>Создаёт проверку.</summary>
    /// <param name="oldIndex">Индекс прошлой версии; <c>null</c> — сравнивать не с чем, проверяется только новая.</param>
    /// <param name="newIndex">Индекс новой версии.</param>
    /// <param name="files">Чтение исходников клиента.</param>
    /// <param name="oldExtraModules">Модули дополнительных пакетов прошлой версии.</param>
    /// <param name="newExtraModules">Модули дополнительных пакетов новой версии.</param>
    /// <param name="analyzer">Preflight; <c>null</c> — с правилами по умолчанию.</param>
    /// <param name="newFailedModules">Модули новой версии, которые не декомпилировались (есть в игре, исходника нет).</param>
    /// <param name="oldFailedModules">То же для прошлой версии.</param>
    public ModCompatibilityChecker(
        ClientIndex? oldIndex,
        ClientIndex newIndex,
        SourceFileService files,
        IReadOnlyList<string>? oldExtraModules = null,
        IReadOnlyList<string>? newExtraModules = null,
        PreflightAnalyzer? analyzer = null,
        IReadOnlyList<string>? newFailedModules = null,
        IReadOnlyList<string>? oldFailedModules = null)
    {
        _old = oldIndex is { IsEmpty: false } ? oldIndex : null;
        _new = newIndex ?? throw new ArgumentNullException(nameof(newIndex));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _analyzer = analyzer ?? new PreflightAnalyzer();
        // Старый оракул тоже знает свои не декомпилировавшиеся модули: иначе WOT002 на их импорт
        // была бы и «до», и «после», и настоящая новая ошибка (модуль удалили) сошла бы за давнюю.
        _oldOracle = _old is null ? null : ClientApiOracle.Create(_old, files, ClientApiOracle.CombineExtraModules(oldExtraModules, oldFailedModules));
        _newFailed = new HashSet<string>(newFailedModules ?? Array.Empty<string>(), StringComparer.Ordinal);
        _oldFailed = _old is null ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(oldFailedModules ?? Array.Empty<string>(), StringComparer.Ordinal);

        // Не декомпилировавшиеся модули в игре есть: для preflight они — как модули доп. пакетов,
        // существуют, но без исходника. Иначе их импорт выглядел бы новой ошибкой WOT002.
        _newOracle = ClientApiOracle.Create(_new, files, ClientApiOracle.CombineExtraModules(newExtraModules, newFailedModules));
        _oldModules = _old is null ? new Dictionary<string, int>() : ModuleMap(_old);
        _newModules = ModuleMap(_new);
        _newExtra = new HashSet<string>(newExtraModules ?? Array.Empty<string>(), StringComparer.Ordinal);
    }

    /// <summary>Есть ли база для сравнения.</summary>
    public bool HasBaseline => _old is not null;

    /// <summary>Проверяет мод.</summary>
    /// <param name="project">Проект мода.</param>
    /// <returns>Итог.</returns>
    public ModCompatibilityReport Check(ModProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var issues = new List<CompatibilityIssue>();
        ModFootprint footprint = ModFootprint.Read(project.SrcDir);

        // Пустой индекс новой версии — проверять нечем: ни один импорт не находит модуль, а
        // preflight без индекса молчит. «Совместим» тут значил бы «не проверялся».
        if (_new.IsEmpty)
        {
            issues.Add(new CompatibilityIssue(true, "нет исходников",
                "в исходниках новой версии клиента нет ни одного модуля — проверить мод не по чему"));
        }

        // 1. Какие модули клиента мод импортирует.
        var modules = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach ((string candidate, string where) in footprint.ImportCandidates.Concat(footprint.DottedStrings))
        {
            string? module = ResolveModule(candidate);
            if (module is not null)
            {
                modules.TryAdd(module, where);
            }
        }

        var changed = new List<string>();
        foreach ((string module, string where) in modules)
        {
            bool inOld = _oldModules.TryGetValue(module, out int oldFile);
            bool inNew = _newModules.TryGetValue(module, out int newFile);

            if (!inNew && _newFailed.Contains(module))
            {
                // Модуль в игре есть, но в новой версии не декомпилировался — и неважно, был ли
                // он в старой (тот же декомпилятор обычно спотыкается на нём каждый раз): что мод
                // из него берёт, не проверено. Не удалён — проверен не до конца.
                issues.Add(new CompatibilityIssue(false, UnverifiedKind,
                    $"модуль {module} в новой версии не декомпилировался — проверить его нельзя")
                {
                    ModLocation = where,
                    ClientLocation = inOld ? _old!.Files[oldFile].RelativePath : null,
                });
                continue;
            }

            if (!inOld && !inNew && _oldFailed.Contains(module))
            {
                // В прошлой версии модуль был (не декомпилировался), в новой его нет вовсе. Переехал
                // в дополнительный пакет — сравнить не с чем; иначе — удалён.
                issues.Add(_newExtra.Contains(module)
                    ? new CompatibilityIssue(false, UnverifiedKind,
                        $"модуль {module} переехал в дополнительный пакет — его исходников нет ни в прошлой, ни в новой версии, сравнить не с чем")
                    {
                        ModLocation = where,
                    }
                    : new CompatibilityIssue(true, "модуль удалён", $"модуля {module} в новой версии клиента нет, а мод его импортирует")
                    {
                        ModLocation = where,
                    });
                continue;
            }

            if (inOld && !inNew)
            {
                // Модуль есть в игре, но исходника нет: переехал в дополнительный пакет (оттуда
                // кокпит берёт только имена). Он не удалён — но и сравнить его не с чем: мод
                // проверен не до конца и автоматом не переносится.
                string? unverifiable = _newExtra.Contains(module)
                    ? $"модуль {module} переехал в дополнительный пакет — его исходников в новой версии нет, сравнить не с чем"
                    : null;

                issues.Add(unverifiable is null
                    ? new CompatibilityIssue(true, "модуль удалён", $"модуля {module} в новой версии клиента нет, а мод его импортирует")
                    {
                        ModLocation = where,
                        ClientLocation = _old!.Files[oldFile].RelativePath,
                    }
                    : new CompatibilityIssue(false, UnverifiedKind, unverifiable)
                    {
                        ModLocation = where,
                        ClientLocation = _old!.Files[oldFile].RelativePath,
                    });
                continue;
            }

            if (!inOld && inNew && _old is not null)
            {
                // Мод собран под прошлую версию, а модуля в её исходниках нет: либо он подключается
                // необязательно, либо база неполная (оборванное копирование). Сравнить не с чем —
                // и автоматом такой мод не переносится (см. UnverifiedKind).
                issues.Add(new CompatibilityIssue(false, UnverifiedKind,
                    $"модуля {module} нет в исходниках прошлой версии — сравнить его не с чем")
                {
                    ModLocation = where,
                    ClientLocation = _new.Files[newFile].RelativePath,
                });
                continue;
            }

            if (!inOld || !inNew)
            {
                continue;
            }

            string oldPath = _old!.Files[oldFile].FullPath(_old.RootPath);
            string newPath = _new.Files[newFile].FullPath(_new.RootPath);
            // Неразобранная функция не сравнивается и при одинаковом тексте: строка «Parse error…»
            // одна и та же, что бы ни было внутри функции.
            HashSet<string> newUnparsed = UnparsedNames(newPath);
            if (!SameText(oldPath, newPath))
            {
                changed.Add(module);
                CompareSymbols(module, oldFile, newFile, footprint, issues, newUnparsed);
            }

            CheckUnparsed(module, oldPath, newPath, footprint, issues);
        }

        if (changed.Count > 0)
        {
            issues.Add(new CompatibilityIssue(false, "модули изменились",
                "в новой версии изменились модули клиента, которые импортирует мод: " + string.Join(", ", changed)));
        }

        // 2. Preflight: только новое.
        ComparePreflight(project, issues);

        CompatibilityVerdict verdict = issues.Any(i => i.Blocking) ? CompatibilityVerdict.Incompatible : CompatibilityVerdict.Compatible;
        return new ModCompatibilityReport(
            project.ProjectDir,
            project.ModId,
            project.DisplayName.Length > 0 ? project.DisplayName : project.ModId,
            verdict,
            issues,
            modules.Keys.ToArray(),
            changed);
    }

    private void CompareSymbols(string module, int oldFile, int newFile, ModFootprint footprint, List<CompatibilityIssue> issues,
        HashSet<string> newUnparsed)
    {
        Dictionary<string, ApiSymbol> newSymbols = SymbolsByName(_new.SymbolsOfFile(newFile));
        Dictionary<string, ApiSymbol> oldSymbols = SymbolsByName(_old!.SymbolsOfFile(oldFile));

        // Классы, до которых мод добирается через синглтон модуля: g_playerEvents = _PlayerEvents()
        // — и g_playerEvents.onArenaPeriodChange упоминает член _PlayerEvents, хотя имени класса
        // в моде нет.
        HashSet<string> reachedClasses = ClassesReachedThroughSingletons(oldSymbols.Values, footprint);

        foreach (ApiSymbol s in oldSymbols.Values)
        {
            // Синглтоны модуля (g_*, SymbolKind.Service) — сравниваются: это главная точка входа
            // мода в клиент, и их переименование ломает мод так же, как удалённая функция.
            if (s.Kind is SymbolKind.NestedFunction)
            {
                continue;
            }

            string? modWhere = MentionOf(s, footprint, out bool mangled);
            if (modWhere is null)
            {
                continue;
            }

            // Член класса считается зависимостью, только если мод упоминает и сам класс — по имени
            // или через синглтон модуля. Закрытое имя в искажённом виде (_Class__name) класс уже
            // называет. Синглтон (g_*) принадлежит модулю, а не классу: его «класс» — имя модуля.
            // События — без этого условия: их имена (onArenaPeriodChange) своеобразны, а до объекта
            // с ними мод обычно добирается вызовами, и имени класса не пишет вовсе.
            string top = s.QualifiedName.Split('.')[0];
            if (!mangled && s.Kind != SymbolKind.Event && s.QualifiedName.Contains('.') &&
                !footprint.Mentions(top) && !reachedClasses.Contains(top))
            {
                continue;
            }

            string oldWhere = $"{s.RelativePath}:{s.Line}";

            // У синглтона в QualifiedName первым идёт короткое имя модуля: gui.gui.g_x — не то,
            // что стоит показывать человеку.
            string shown = s.Kind == SymbolKind.Service ? s.Name : s.QualifiedName;

            if (!newSymbols.TryGetValue(s.QualifiedName, out ApiSymbol? n))
            {
                // Функция в новой версии есть, но не разобрана — не «удалена»: заметку «сравнить
                // не с чем» даст CheckUnparsed.
                if (newUnparsed.Contains(UnparsedKey(s)))
                {
                    continue;
                }

                issues.Add(new CompatibilityIssue(true, "удалено",
                    $"{module}.{shown} в новой версии клиента нет, а мод его упоминает")
                {
                    ModLocation = modWhere,
                    ClientLocation = oldWhere,
                });
                continue;
            }

            if (s.Kind is SymbolKind.Function or SymbolKind.Method &&
                !string.Equals(Normalize(s.Signature), Normalize(n.Signature), StringComparison.Ordinal))
            {
                issues.Add(new CompatibilityIssue(true, "сигнатура",
                    $"у {module}.{s.QualifiedName} поменялась сигнатура: было {s.Signature ?? "()"}, стало {n.Signature ?? "()"}")
                {
                    ModLocation = modWhere,
                    ClientLocation = $"{n.RelativePath}:{n.Line}",
                });
                continue;
            }

            if (s.Kind == SymbolKind.Constant)
            {
                string? before = ValueAt(s);
                string? after = ValueAt(n);
                if (before is not null && after is not null && !string.Equals(before, after, StringComparison.Ordinal))
                {
                    issues.Add(new CompatibilityIssue(false, "константа",
                        $"{module}.{s.QualifiedName}: значение было «{before}», стало «{after}»")
                    {
                        ModLocation = modWhere,
                        ClientLocation = $"{n.RelativePath}:{n.Line}",
                    });
                }
            }
        }
    }

    private void ComparePreflight(ModProject project, List<CompatibilityIssue> issues)
    {
        if (!Directory.Exists(project.SrcDir))
        {
            issues.Add(new CompatibilityIssue(true, "проект", "в проекте нет каталога src — проверять нечего"));
            return;
        }

        PreflightResult now = _analyzer.Analyze(PreflightAnalyzer.BuildContext(project.SrcDir, project.ProjectDir, _newOracle, project));
        var before = new HashSet<string>(StringComparer.Ordinal);
        if (_oldOracle is not null)
        {
            PreflightResult was = _analyzer.Analyze(PreflightAnalyzer.BuildContext(project.SrcDir, project.ProjectDir, _oldOracle, project));
            foreach (PreflightDiagnostic d in was.Diagnostics)
            {
                before.Add(Key(d));
            }
        }

        foreach (PreflightDiagnostic d in now.Diagnostics)
        {
            if (d.Severity == PreflightSeverity.Info || before.Contains(Key(d)))
            {
                continue;
            }

            issues.Add(new CompatibilityIssue(d.Severity == PreflightSeverity.Error, "preflight " + d.RuleId, d.Message)
            {
                ModLocation = d.Location,
            });
        }
    }

    /// <summary>
    /// Ключ находки preflight для сравнения версий. В тексте WOT002 есть число модулей индекса —
    /// оно разное у любых двух версий клиента, и давняя находка выглядела бы новой; числа в нём
    /// не учитываются. У остальных правил числа — суть находки, они остаются.
    /// </summary>
    private static string Key(PreflightDiagnostic d)
    {
        string message = d.RuleId == UnknownApiRule.ModuleRuleId ? Digits.Replace(d.Message, "#") : d.Message;
        return d.RuleId + "|" + d.RelativePath + "|" + d.Line + "|" + message;
    }

    /// <summary>
    /// Функции, которые uncompyle6 не разобрал в одной из версий: в тексте вместо них
    /// <c>def имяParse error at or near …</c> или <c>имя = Parse error at or near …</c>, и в индекс они
    /// не попадают — сравнивать нечего, а молчать нельзя, если мод их упоминает.
    /// </summary>
    private void CheckUnparsed(string module, string oldPath, string newPath, ModFootprint footprint, List<CompatibilityIssue> issues)
    {
        // Для заметки класс не важен: мод упоминает имя, а какой из классов его — не установить.
        var names = new SortedSet<string>(UnparsedNames(oldPath).Concat(UnparsedNames(newPath)).Select(BareName), StringComparer.Ordinal);

        foreach (string name in names)
        {
            // Закрытый метод мод называет искажённым именем _Class__name — его тоже ищем.
            string? where = footprint.Names.TryGetValue(name, out string? direct)
                ? direct
                : name.StartsWith("__", StringComparison.Ordinal)
                    ? footprint.Names.FirstOrDefault(kv => kv.Key.EndsWith(name, StringComparison.Ordinal) && kv.Key.StartsWith('_')).Value
                    : null;
            if (where is null)
            {
                continue;
            }

            issues.Add(new CompatibilityIssue(false, UnverifiedKind,
                $"{module}.{name} в прошлой или новой версии не декомпилировался — сравнить не с чем")
            {
                ModLocation = where,
            });
        }
    }

    /// <summary>
    /// Неразобранные функции файла дерева — вместе с классом, в котором они стоят:
    /// <c>Класс.имя</c>, у функции модуля — <c>.имя</c>. Класс нужен, чтобы неразобранный метод
    /// одного класса не прикрыл удаление одноимённого разобранного метода другого
    /// (<c>UnitRoster.py</c>: <c>BaseUnitRoster.checkVehicleLevel</c> и
    /// <c>BaseUnitRosterLimits.checkVehicleLevel</c>).
    /// </summary>
    private HashSet<string> UnparsedNames(string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        string? text = _files.ReadText(path);
        if (text is null || !text.Contains("Parse error at or near", StringComparison.Ordinal))
        {
            return names;
        }

        var classes = new List<(int Indent, string Name)>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            int indent = line.Length - trimmed.Length;
            while (classes.Count > 0 && classes[^1].Indent >= indent)
            {
                classes.RemoveAt(classes.Count - 1);
            }

            Match cls = ClassLine.Match(line);
            if (cls.Success)
            {
                classes.Add((indent, cls.Groups["name"].Value));
                continue;
            }

            Match m = UnparsedDef.Match(line);
            if (m.Success)
            {
                names.Add((classes.Count > 0 ? classes[^1].Name : string.Empty) + "." + m.Groups["name"].Value);
            }
        }

        return names;
    }

    private static string BareName(string scoped) => scoped[(scoped.LastIndexOf('.') + 1)..];

    /// <summary>Ключ символа индекса в наборе <see cref="UnparsedNames"/>: класс-владелец и имя.</summary>
    private static string UnparsedKey(ApiSymbol s)
    {
        string[] parts = s.QualifiedName.Split('.');
        string owner = s.Kind != SymbolKind.Service && parts.Length >= 2 ? parts[^2] : string.Empty;
        return owner + "." + s.Name;
    }

    /// <summary>
    /// Классы модуля, экземпляры которых лежат в упомянутых модом синглтонах
    /// (<c>g_playerEvents = _PlayerEvents()</c>). Класс берётся из строки объявления синглтона.
    /// </summary>
    private HashSet<string> ClassesReachedThroughSingletons(IEnumerable<ApiSymbol> symbols, ModFootprint footprint)
    {
        var classes = new HashSet<string>(StringComparer.Ordinal);
        foreach (ApiSymbol s in symbols)
        {
            if (s.Kind != SymbolKind.Service || !footprint.Mentions(s.Name))
            {
                continue;
            }

            string? line = _files.GetSnippet(s.FullPath, s.Line, 0, 0)?.Lines.FirstOrDefault();
            Match m = line is null ? Match.Empty : SingletonOfClass.Match(line);
            if (m.Success)
            {
                classes.Add(m.Groups["cls"].Value);
            }
        }

        return classes;
    }

    /// <summary>
    /// Где мод упоминает символ клиента, либо <c>null</c>. Закрытый метод класса
    /// (<c>__onSiegeStateUpdated</c>) снаружи доступен только в искажённом виде
    /// <c>_PlayerAvatar__onSiegeStateUpdated</c> — так его и перехватывают, — поэтому ищется и он.
    /// </summary>
    private static string? MentionOf(ApiSymbol s, ModFootprint footprint, out bool mangled)
    {
        mangled = false;
        if (footprint.Names.TryGetValue(s.Name, out string? where))
        {
            return where;
        }

        string[] parts = s.QualifiedName.Split('.');
        if (parts.Length >= 2 && s.Name.StartsWith("__", StringComparison.Ordinal) && !s.Name.EndsWith("__", StringComparison.Ordinal))
        {
            string owner = parts[^2].TrimStart('_');
            if (owner.Length > 0 && footprint.Names.TryGetValue("_" + owner + s.Name, out where))
            {
                mangled = true;
                return where;
            }
        }

        return null;
    }

    /// <summary>
    /// Кандидат из импорта → модуль клиента. <c>from X import a</c> даёт кандидатов <c>X</c> и
    /// <c>X.a</c>: если <c>X.a</c> не модуль, это атрибут модуля <c>X</c>.
    /// </summary>
    private string? ResolveModule(string candidate)
    {
        // Не декомпилировавшийся модуль — тоже модуль клиента: его нет ни в одном индексе, но мод,
        // который его импортирует, проверен не до конца, и это надо сказать.
        if (_newModules.ContainsKey(candidate) || _oldModules.ContainsKey(candidate) ||
            _newFailed.Contains(candidate) || _oldFailed.Contains(candidate))
        {
            return candidate;
        }

        return null;
    }

    private bool SameText(string oldPath, string newPath)
    {
        string? a = _files.ReadText(oldPath);
        string? b = _files.ReadText(newPath);
        if (a is null || b is null)
        {
            return a == b;
        }

        return string.Equals(Body(a), Body(b), StringComparison.Ordinal);
    }

    /// <summary>
    /// Текст без служебных строк декомпилятора (путь к .pyc в них разный всегда), без отладочного
    /// вывода парсера (деревья, разобранные вручную или прежним кокпитом, хранят его в частичных
    /// файлах, а новое — нет) и без пустых строк (на месте вырезанного вывода остаётся пустая, а
    /// ручной разбор местами удваивал переводы строк); иначе одинаковый модуль выглядел бы изменившимся.
    /// </summary>
    private static string Body(string text) =>
        string.Join('\n', Uncompyle6Decompiler.StripParserDump(text).Split('\n')
            .Select(l => l.TrimEnd('\r', ' ', '\t'))
            .Where(l => l.Length > 0
                        && !l.StartsWith("# okay decompiling", StringComparison.Ordinal)
                        && !l.StartsWith("# Decompiled from:", StringComparison.Ordinal)
                        && !l.StartsWith("# uncompyle6 version", StringComparison.Ordinal)
                        && !l.StartsWith(ClientSourceExtractor.PartialHeaderPrefix, StringComparison.Ordinal)));

    private string? ValueAt(ApiSymbol symbol)
    {
        SourceSnippet? snippet = _files.GetSnippet(symbol.FullPath, symbol.Line, 0, 0);
        string? line = snippet?.Lines.FirstOrDefault();
        if (line is null)
        {
            return null;
        }

        int eq = line.IndexOf('=');
        return eq < 0 ? null : string.Join(' ', line[(eq + 1)..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Normalize(string? signature) =>
        string.Join(string.Empty, (signature ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));

    private static Dictionary<string, ApiSymbol> SymbolsByName(IReadOnlyList<ApiSymbol> symbols)
    {
        var map = new Dictionary<string, ApiSymbol>(StringComparer.Ordinal);
        foreach (ApiSymbol s in symbols)
        {
            // Символ самого модуля носит его короткое имя — то же, что у одноимённого класса или
            // функции верхнего уровня (Vehicle.Vehicle, th_async.th_async). Он идёт в файле первым
            // и занял бы ключ вместо них.
            if (s.Kind == SymbolKind.Module)
            {
                continue;
            }

            map.TryAdd(s.QualifiedName, s);
        }

        return map;
    }

    private static Dictionary<string, int> ModuleMap(ClientIndex index)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < index.Files.Count; i++)
        {
            string dotted = index.Files[i].DottedModule;
            if (dotted.Length > 0)
            {
                map.TryAdd(dotted, i);
            }
        }

        return map;
    }
}
