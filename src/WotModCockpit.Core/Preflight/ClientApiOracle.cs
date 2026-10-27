using System.Collections.Concurrent;
using System.Reflection;
using WotModCockpit.Core.ApiIndex;
using ClientIndex = WotModCockpit.Core.ApiIndex.ApiIndex;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Что известно про один модуль клиента: его экспорты и «прозрачность».
/// </summary>
/// <param name="Exports">Имена, доступные как <c>module.X</c> и через <c>from module import X</c>.</param>
/// <param name="IsOpaque">
/// Модуль делает <c>from &lt;нативный или неизвестный&gt; import *</c>: полный список его имён
/// вычислить нельзя, поэтому проверять имена в нём НЕЛЬЗЯ — будут ложные срабатывания.
/// </param>
public sealed record ModuleFacts(IReadOnlySet<string> Exports, bool IsOpaque);

/// <summary>
/// Ответчик на вопрос «есть ли это в клиенте 1.44». Единственный источник истины для
/// правил WOT002/WOT003/WOT004.
///
/// <para><b>Что он знает и откуда.</b> Основа — индекс этапа 2
/// (<see cref="ClientIndex"/>, 8052 файла из <c>scripts.pkg</c>). К нему добавлены три списка,
/// без которых правило «символа нет в 1.44» даёт систематические ложные срабатывания:</para>
/// <list type="number">
///   <item>1567 модулей из 21 ДОПОЛНИТЕЛЬНОГО пакета игры (<c>battle_royale.pkg</c>,
///         <c>frontline.pkg</c>, <c>comp7.pkg</c>, …, 154 top-level имени) — индексом они
///         не покрыты, и без них любой <c>from frontline… import</c> помечался бы ошибкой;</item>
///   <item>нативные модули движка (<c>BigWorld</c>, <c>ResMgr</c>, <c>Math</c>, <c>GUI</c>,
///         <c>CGF</c>, …): у них нет <c>.py</c> вовсе, атрибуты у них не проверяются в принципе.
///         Список получен перебором неразрешимых импортов В САМОМ клиенте
///         (<c>BigWorld</c> — 848 импортов, <c>Math</c> — 230, <c>ResMgr</c> — 149);</item>
///   <item>встроенные C-модули Python 2.7 (<c>sys</c>, <c>time</c>, <c>itertools</c>,
///         <c>cPickle</c>, …): файлов в <c>common/Lib</c> у них тоже нет.</item>
/// </list>
///
/// <para><b>Транзитивное разворачивание <c>from X import *</c> — не украшение, а условие
/// работоспособности.</b> Замерено разведкой на корпусе из 188 файлов реальных модов:
/// без разворачивания проверка атрибутов давала 124 срабатывания, с разворачиванием — 2,
/// причём одни только <c>dependency.instance</c>/<c>dependency.descriptor</c> давали 108,
/// потому что <c>helpers/dependency.py</c> — это чистый ре-экспорт
/// <c>from dependency_injection_container import *</c>.</para>
/// </summary>
public sealed class ClientApiOracle
{
    /// <summary>Глубина разворачивания <c>from X import *</c>. Три круга покрывают весь клиент.</summary>
    public const int StarImportDepth = 3;

    private readonly ClientIndex _index;
    private readonly SourceFileService _files;
    private readonly HashSet<string> _modules = new(StringComparer.Ordinal);
    private readonly HashSet<string> _packages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _moduleFile = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ModuleFacts?> _facts = new(StringComparer.Ordinal);

    private ClientApiOracle(ClientIndex index, SourceFileService files)
    {
        _index = index;
        _files = files;
    }

    /// <summary>Индекс собран — правилам WOT002/003/004 есть с чем сверяться.</summary>
    public bool IsReady => !_index.IsEmpty;

    /// <summary>Сколько модулей знает оракул (индекс + дополнительные пакеты).</summary>
    public int ModuleCount => _modules.Count;

    /// <summary>
    /// Нативные модули движка: <c>.py</c> у них нет, поэтому существование их атрибутов
    /// проверить нечем. Ни один из них не является ошибкой импорта.
    /// </summary>
    public static IReadOnlySet<string> NativeModules { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "BigWorld", "ResMgr", "Math", "GUI", "CGF", "AnimationSequence", "Physics", "Pixie",
        "Vehicular", "VSE", "Windowing", "PyGUIBase", "PyGUI", "WWISE", "_WWISE", "SoundGroups_native",
        "PostProcessing", "Bloom", "FX", "DataLinks", "NetworkFilters", "ScaleformFileLoader",
        "_Scaleform", "Steam", "Personality", "Projectiles", "VideoCamera", "TextStyles",
        "BWPersonality", "Bitmap", "Cat", "FantasyDemo", "Options", "Utils", "LGC", "AL",
        "PrefabsLoading", "Health", "Window", "EdgeDrawer", "Sound", "Listeners", "FocusManager"
    };

    /// <summary>
    /// Встроенные модули Python 2.7 (C-расширения): файла <c>.py</c> у них нет ни в
    /// <c>common/Lib</c>, ни где-либо ещё, но импортировать их мод имеет полное право.
    /// </summary>
    public static IReadOnlySet<string> BuiltinModules { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "sys", "time", "math", "cmath", "itertools", "operator", "gc", "imp", "marshal", "array",
        "thread", "select", "signal", "errno", "zlib", "bz2", "binascii", "datetime", "exceptions",
        "cPickle", "cStringIO", "strop", "unicodedata", "audioop", "zipimport", "parser", "mmap",
        "__builtin__", "__main__", "__future__", "future_builtins", "msvcrt", "nt", "posix",
        "_ast", "_bisect", "_codecs", "_collections", "_csv", "_ctypes", "_functools", "_hashlib",
        "_heapq", "_hotshot", "_io", "_json", "_locale", "_lsprof", "_md5", "_multibytecodec",
        "_multiprocessing", "_random", "_sha", "_sha256", "_sha512", "_socket", "_sqlite3", "_sre",
        "_ssl", "_struct", "_subprocess", "_symtable", "_tkinter", "_warnings", "_weakref",
        "_winreg", "_elementtree", "_curses", "pyexpat", "fcntl", "grp", "pwd", "termios",
        "readline", "crypt", "resource", "syslog", "dbm", "_dummy_threading"
    };

    /// <summary>
    /// Пакеты API ЧУЖИХ модов и фреймворков. В клиенте их нет и быть не должно, поэтому
    /// WOT002 обязан их не трогать (иначе 15 срабатываний из 44 на корпусе — это .pyd XFW),
    /// а WOT018 — наоборот, требовать для них <c>try/except ImportError</c>.
    /// </summary>
    public static IReadOnlySet<string> KnownModApiModules { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "gui.modsListApi", "gui.modsSettingsApi", "gui.vxSettingsApi", "gui.mods.mod_battle_observer",
        "xfw", "xfw_actionscript", "xfw_loader", "xfw_native", "xfw_multilaunch", "xvm_main",
        "openwg_loader", "openwg_vfs", "openwg_mq", "openwg_network", "openwg_audio",
        "openwg_filewatcher", "openwg_fonts", "crosshairs_api", "vxSettingsApi"
    };

    /// <summary>
    /// Собирает оракул. Дешёвая операция: один проход по <see cref="ClientIndex.Files"/>
    /// плюс чтение встроенного списка дополнительных пакетов. Исходники модулей читаются
    /// ЛЕНИВО и только те, про которые спросили.
    /// </summary>
    /// <param name="index">Индекс клиента; допустим <see cref="ClientIndex.Empty"/>.</param>
    /// <param name="files">Служба чтения исходников (кеш).</param>
    /// <param name="extraModules">Дополнительные модули; <c>null</c> = встроенный список 21 пакета.</param>
    /// <returns>Оракул.</returns>
    public static ClientApiOracle Create(ClientIndex index, SourceFileService files, IEnumerable<string>? extraModules = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(files);

        var oracle = new ClientApiOracle(index, files);

        for (int i = 0; i < index.Files.Count; i++)
        {
            SourceFileEntry f = index.Files[i];
            string dotted = f.DottedModule;
            if (dotted.Length == 0) continue;

            oracle.AddModule(dotted, i, f.Tier);

            // stdlib и вендорные пакеты лежат под common/Lib и common/site-packages,
            // а импортируются БЕЗ этого префикса: common/Lib/HTMLParser.py -> import HTMLParser,
            // common/site-packages/typing-3.6.2/typing.py -> import typing.
            string? alias = StripVendorPrefix(f.RelativePath);
            if (alias is not null) oracle.AddModule(alias, i, f.Tier);
        }

        foreach (string m in extraModules ?? LoadExtraModules())
            oracle.AddModule(m, -1, SourceTier.Game);

        return oracle;
    }

    private void AddModule(string dotted, int fileId, SourceTier tier)
    {
        _modules.Add(dotted);

        int dot = dotted.IndexOf('.');
        while (dot > 0)
        {
            _packages.Add(dotted[..dot]);
            dot = dotted.IndexOf('.', dot + 1);
        }

        if (fileId < 0) return;
        // Игровой код важнее вендорного: если имя совпало (common/Lib/json и свой json),
        // экспорты берём у игрового.
        if (!_moduleFile.TryGetValue(dotted, out int existing)) _moduleFile[dotted] = fileId;
        else if (tier == SourceTier.Game && _index.Files[existing].Tier != SourceTier.Game) _moduleFile[dotted] = fileId;
    }

    /// <summary>
    /// Превращает <c>common/Lib/email/mime/text.py</c> в <c>email.mime.text</c>, а
    /// <c>common/site-packages/typing-3.6.2/typing.py</c> — в <c>typing</c>.
    /// Версия каталога дистрибутива содержит точки, поэтому резать надо путь, а НЕ точечное имя.
    /// </summary>
    /// <param name="relativePath">Путь относительно корня дерева, прямые слэши.</param>
    /// <returns>Импортируемое имя без вендорного префикса либо <c>null</c>.</returns>
    public static string? StripVendorPrefix(string relativePath)
    {
        string[] parts = relativePath.Split('/');
        if (parts.Length < 2) return null;

        int start = 1;                                  // 0 — корень дерева (common/client/…)
        bool stripped = false;
        if (start < parts.Length && parts[start] == "Lib") { start++; stripped = true; }
        if (start < parts.Length && parts[start] == "site-packages")
        {
            start++;
            stripped = true;
            if (start < parts.Length && parts[start].Contains('-', StringComparison.Ordinal)
                && parts[start].Any(char.IsDigit))
                start++;                                 // typing-3.6.2, enum34-1.1.6, future-0.18.2
        }
        if (!stripped || start >= parts.Length) return null;

        var tail = new List<string>(parts.Length - start);
        for (int i = start; i < parts.Length; i++) tail.Add(parts[i]);

        string last = tail[^1];
        if (last.EndsWith(".py", StringComparison.Ordinal)) last = last[..^3];
        if (last == "__init__")
        {
            tail.RemoveAt(tail.Count - 1);
            if (tail.Count == 0) return null;
        }
        else tail[^1] = last;

        return string.Join('.', tail);
    }

    /// <summary>
    /// Список модулей к оракулу: свой список дерева либо встроенный, плюс модули, которые в этом
    /// дереве не декомпилировались (в игре они есть, исходника нет). Без своего списка
    /// не декомпилировавшиеся добавляются к встроенному, а не заменяют его.
    /// </summary>
    /// <param name="extraModules">Модули доп. пакетов из дерева; <c>null</c> — встроенный список.</param>
    /// <param name="failedModules">Не декомпилировавшиеся модули; <c>null</c> — нет.</param>
    /// <returns>Что передать в <see cref="Create"/>.</returns>
    internal static IReadOnlyList<string>? CombineExtraModules(IReadOnlyList<string>? extraModules, IReadOnlyList<string>? failedModules)
    {
        if (failedModules is null || failedModules.Count == 0)
        {
            return extraModules;
        }

        return (extraModules ?? LoadExtraModules()).Concat(failedModules).ToArray();
    }

    private static IReadOnlyList<string> LoadExtraModules()
    {
        try
        {
            Assembly asm = typeof(ClientApiOracle).Assembly;
            using Stream? stream = asm.GetManifestResourceStream("WotModCockpit.Core.Preflight.Data.ExtraClientModules.txt");
            if (stream is null) return Array.Empty<string>();

            using var reader = new StreamReader(stream);
            var list = new List<string>(1600);
            while (reader.ReadLine() is { } line)
            {
                string t = line.Trim();
                if (t.Length > 0 && t[0] != '#') list.Add(t);
            }
            return list;
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    // --------------------------------------------------------------------- запросы

    /// <summary>Модуль (или пакет) существует в клиенте 1.44.</summary>
    /// <param name="dotted">Точечное имя.</param>
    /// <returns><c>true</c>, если существует.</returns>
    public bool ModuleExists(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return false;
        return _modules.Contains(dotted) || _packages.Contains(dotted);
    }

    /// <summary>Модуль нативный (реализован в движке, <c>.py</c> нет).</summary>
    /// <param name="dotted">Точечное имя.</param>
    /// <returns><c>true</c>, если нативный.</returns>
    public static bool IsNative(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return false;
        int dot = dotted.IndexOf('.');
        return NativeModules.Contains(dot < 0 ? dotted : dotted[..dot]);
    }

    /// <summary>Модуль — встроенный модуль Python 2.7.</summary>
    /// <param name="dotted">Точечное имя.</param>
    /// <returns><c>true</c>, если встроенный.</returns>
    public static bool IsBuiltin(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return false;
        int dot = dotted.IndexOf('.');
        return BuiltinModules.Contains(dot < 0 ? dotted : dotted[..dot]);
    }

    /// <summary>Модуль — известный API чужого мода (XFW, ModsListAPI, ModsSettingsAPI…).</summary>
    /// <param name="dotted">Точечное имя.</param>
    /// <returns><c>true</c>, если это API другого мода.</returns>
    public static bool IsModApi(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return false;
        foreach (string known in KnownModApiModules)
            if (dotted == known || dotted.StartsWith(known + ".", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// Модуль — stdlib Python 2.7 или вендорная библиотека из <c>common/Lib</c> и
    /// <c>common/site-packages</c> (<c>json</c>, <c>logging</c>, <c>typing</c>, <c>enum</c>).
    /// Такие имена к игре отношения не имеют: их нельзя считать «игровым API».
    /// </summary>
    /// <param name="dotted">Точечное имя.</param>
    /// <returns><c>true</c>, если stdlib/вендор.</returns>
    public bool IsVendorModule(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return false;
        if (IsBuiltin(dotted)) return true;

        string root = dotted;
        while (true)
        {
            if (_moduleFile.TryGetValue(root, out int id))
                return _index.Files[id].Tier == SourceTier.Vendor;
            int dot = root.LastIndexOf('.');
            if (dot <= 0) return false;
            root = root[..dot];
        }
    }

    /// <summary>
    /// Имя — модульный синглтон клиента (<c>g_playerEvents = _PlayerEvents()</c> на нулевом
    /// отступе, таких в клиенте 142). Именно такие объекты живут всю сессию, и подписка на них
    /// без отписки утекает по-настоящему.
    /// </summary>
    /// <param name="name">Короткое имя.</param>
    /// <returns><c>true</c>, если синглтон клиента.</returns>
    public bool IsClientSingleton(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        foreach (int id in _index.IdsOf(name))
            if (_index.GetEntry(id).Kind is SymbolKind.Service) return true;
        return false;
    }

    /// <summary>
    /// Клиент 1.44 объявляет метод (или функцию) с таким именем, и если указан класс —
    /// именно в нём.
    ///
    /// <para><b>Зачем.</b> Отличить перекрытие ШТАТНОГО метода клиента
    /// (<c>Avatar.PlayerAvatar.onEnterWorld = my</c> — оригинал потерян навсегда) от
    /// добавления НОВОГО атрибута на чужой класс (<c>ToolTip.xvm_hide = …</c> у XVM,
    /// <c>MarkersManager.daapi_as2py = …</c> — сохранять там просто нечего). Без этой
    /// проверки WOT007 давал 5 ложных срабатываний на одном только XVM.</para>
    /// </summary>
    /// <param name="name">Имя метода.</param>
    /// <param name="ownerClass">Имя класса-владельца; <c>null</c> — не проверять владельца.</param>
    /// <returns><c>true</c>, если такое объявление в клиенте есть.</returns>
    public bool DeclaresMethod(string? name, string? ownerClass = null)
    {
        if (string.IsNullOrEmpty(name)) return false;

        foreach (int id in _index.IdsOf(name))
        {
            SymbolEntry entry = _index.GetEntry(id);
            if (entry.Kind is not (SymbolKind.Method or SymbolKind.Function)) continue;
            if (_index.Files[entry.FileId].Tier != SourceTier.Game) continue;
            if (string.IsNullOrEmpty(ownerClass)) return true;

            if (entry.Parent < 0) continue;
            SymbolEntry parent = _index.GetEntry(entry.Parent);
            if (parent.Kind == SymbolKind.Class && string.Equals(parent.Name, ownerClass, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>У модуля есть подмодуль с таким именем.</summary>
    /// <param name="dotted">Модуль-пакет.</param>
    /// <param name="name">Имя подмодуля.</param>
    /// <returns><c>true</c>, если подмодуль есть.</returns>
    public bool HasSubmodule(string dotted, string name) => ModuleExists(dotted + "." + name);

    /// <summary>
    /// Экспорты модуля с транзитивным разворачиванием <c>from X import *</c>.
    /// <c>null</c> — про модуль ничего не известно (нет исходника: дополнительный пакет,
    /// нативный модуль) либо он непрозрачен; в обоих случаях имена в нём проверять НЕЛЬЗЯ.
    /// </summary>
    /// <param name="dotted">Точечное имя модуля.</param>
    /// <returns>Факты либо <c>null</c>.</returns>
    public ModuleFacts? FactsOf(string? dotted)
    {
        if (string.IsNullOrEmpty(dotted)) return null;
        return _facts.GetOrAdd(dotted, key => BuildFacts(key, 0));
    }

    private ModuleFacts? BuildFacts(string dotted, int depth)
    {
        if (!_moduleFile.TryGetValue(dotted, out int fileId)) return null;

        string path = _index.Files[fileId].FullPath(_index.RootPath);
        string? text = _files.ReadText(path);
        if (text is null) return null;

        PythonLineModel model = PythonLineModel.Parse(text);
        var exports = new HashSet<string>(StringComparer.Ordinal);
        bool opaque = false;

        foreach (PythonDefinition def in model.Definitions)
            if (def.IsTopLevel) exports.Add(def.Name);

        var stars = new List<string>();
        foreach (PythonStatement st in model.Statements)
        {
            if (st.EnclosingDefinition >= 0) continue;   // тело def/class — не экспорт модуля
            CollectBindings(st, exports, stars);
        }

        // Подмодули пакета доступны как атрибуты после импорта.
        string prefix = dotted + ".";
        foreach (string m in _modules)
        {
            if (!m.StartsWith(prefix, StringComparison.Ordinal)) continue;
            string tail = m[prefix.Length..];
            int dot = tail.IndexOf('.');
            exports.Add(dot < 0 ? tail : tail[..dot]);
        }

        foreach (string star in stars)
        {
            if (depth >= StarImportDepth) { opaque = true; continue; }

            string target = ResolveSibling(dotted, star);
            ModuleFacts? inner = _facts.GetOrAdd(target, key => BuildFacts(key, depth + 1));
            if (inner is null) { opaque = true; continue; }
            if (inner.IsOpaque) opaque = true;
            foreach (string name in inner.Exports) exports.Add(name);
        }

        return new ModuleFacts(exports, opaque);
    }

    /// <summary>
    /// В Python 2.7 действует НЕЯВНЫЙ относительный импорт: внутри пакета <c>helpers</c>
    /// строка <c>from dependency_injection_container import *</c> означает сначала
    /// <c>helpers.dependency_injection_container</c> и только потом абсолютный модуль.
    /// Без этого разворачивание звёздочек не находит цель и модуль становится непрозрачным.
    /// </summary>
    private string ResolveSibling(string current, string target)
    {
        int dot = current.LastIndexOf('.');
        if (dot > 0)
        {
            string sibling = current[..(dot + 1)] + target;
            if (_moduleFile.ContainsKey(sibling)) return sibling;
        }
        if (_moduleFile.ContainsKey(current + "." + target)) return current + "." + target;
        return target;
    }

    /// <summary>Имена, которые оператор вводит в область видимости модуля.</summary>
    private static void CollectBindings(PythonStatement st, HashSet<string> exports, List<string> stars)
    {
        string code = st.Trimmed;

        if (st.StartsWith("import"))
        {
            foreach (string piece in code[6..].Split(','))
            {
                string p = piece.Trim();
                if (p.Length == 0) continue;
                int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                if (asAt >= 0) { exports.Add(p[(asAt + 4)..].Trim()); continue; }
                int dot = p.IndexOf('.');
                exports.Add(dot < 0 ? p : p[..dot]);
            }
            return;
        }

        if (st.StartsWith("from"))
        {
            int importAt = code.IndexOf(" import ", StringComparison.Ordinal);
            if (importAt < 0) return;
            string module = code[4..importAt].Trim();
            string names = code[(importAt + 8)..].Trim().Trim('(', ')');

            if (names.StartsWith('*'))
            {
                if (module.Length > 0 && module[0] != '.') stars.Add(module);
                return;
            }

            foreach (string piece in names.Split(','))
            {
                string p = piece.Trim();
                if (p.Length == 0) continue;
                int asAt = p.IndexOf(" as ", StringComparison.Ordinal);
                exports.Add(asAt >= 0 ? p[(asAt + 4)..].Trim() : p);
            }
            return;
        }

        // Присваивание на уровне модуля: X = ..., X, Y = ..., X += ...
        int eq = FindAssignment(code);
        if (eq <= 0) return;

        string left = code[..eq].TrimEnd();
        if (left.EndsWith('+') || left.EndsWith('-') || left.EndsWith('*') || left.EndsWith('/')
            || left.EndsWith('|') || left.EndsWith('&') || left.EndsWith('^') || left.EndsWith('%'))
            left = left[..^1].TrimEnd();

        foreach (string piece in left.Split(','))
        {
            string p = piece.Trim().Trim('(', ')', '[', ']').Trim();
            if (p.Length == 0) continue;
            if (p.Contains('.', StringComparison.Ordinal) || p.Contains('[', StringComparison.Ordinal)) continue;
            if (!char.IsLetter(p[0]) && p[0] != '_') continue;
            bool ok = true;
            foreach (char c in p) if (!char.IsLetterOrDigit(c) && c != '_') { ok = false; break; }
            if (ok) exports.Add(p);
        }
    }

    /// <summary>Индекс <c>=</c> верхнего уровня (не <c>==</c>, не внутри скобок) либо -1.</summary>
    private static int FindAssignment(string code)
    {
        int depth = 0;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == '=' && depth == 0)
            {
                if (i + 1 < code.Length && code[i + 1] == '=') { i++; continue; }
                if (i > 0 && code[i - 1] is '=' or '!' or '<' or '>') continue;
                return i;
            }
        }
        return -1;
    }
}
