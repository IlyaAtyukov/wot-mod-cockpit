using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты лексического сканера. Каждый случай здесь — не выдумка, а воспроизведение реальной
/// особенности вывода uncompyle6, из-за которой наивный парсер молча теряет треть корпуса.
/// </summary>
public class PythonSymbolScannerTests
{
    /// <summary>Собирает файл из строк с CRLF — ровно так, как их печатает uncompyle6.</summary>
    private static string Crlf(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    private static List<SymbolEntry> Scan(string source, ApiIndexOptions? options = null)
    {
        var list = new List<SymbolEntry>();
        new PythonSymbolScanner(options).Scan(0, source, list, new StringPool());
        return list;
    }

    private static SymbolEntry Single(List<SymbolEntry> symbols, string name) =>
        symbols.Single(s => s.Name == name);

    // ------------------------------------------------------------------ ловушка CRLF

    /// <summary>
    /// Главная ловушка корпуса: файлы в CRLF, поэтому «пустая» строка после разбиения по <c>\n</c>
    /// равна <c>"\r"</c>. Если её не обрезать, отступ считается нулевым и стек родителей рушится
    /// на каждой пустой строке между методами — а uncompyle6 ставит её между КАЖДЫМИ двумя
    /// методами. Симптом бага: методов 0 вместо 104 215.
    /// </summary>
    [Fact]
    public void Crlf_BlankLinesBetweenMethods_DoNotBreakParentStack()
    {
        string src = Crlf(
            "class A(object):",
            "",
            "    def first(self):",
            "        pass",
            "",
            "    def second(self):",
            "        pass",
            "");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolKind.Class, Single(symbols, "A").Kind);
        Assert.Equal(SymbolKind.Method, Single(symbols, "first").Kind);
        Assert.Equal(SymbolKind.Method, Single(symbols, "second").Kind);
        Assert.Equal(0, Single(symbols, "first").Parent);
        Assert.Equal(0, Single(symbols, "second").Parent);
    }

    /// <summary>Файл только с LF (такие тоже попадаются) должен разбираться так же.</summary>
    [Fact]
    public void LfOnly_ParsesLikeCrlf()
    {
        List<SymbolEntry> symbols = Scan("class A:\n    def m(self):\n        pass\n");

        Assert.Equal(SymbolKind.Class, Single(symbols, "A").Kind);
        Assert.Equal(SymbolKind.Method, Single(symbols, "m").Kind);
    }

    /// <summary>Табы в отступе (79 файлов корпуса) считаются как 8 пробелов.</summary>
    [Fact]
    public void Tabs_AreTreatedAsIndentation()
    {
        List<SymbolEntry> symbols = Scan("class A:\n\tdef m(self):\n\t\tpass\n");

        Assert.Equal(SymbolKind.Method, Single(symbols, "m").Kind);
        Assert.Equal(8, Single(symbols, "m").Indent);
    }

    // ------------------------------------------------------------------ невалидный Python

    /// <summary>
    /// Литерал с неэкранированным апострофом (135 файлов, 600 строк корпуса; образец —
    /// <c>client/messenger/formatters/service_channel.py:275</c>). Любой парсер с состоянием
    /// на нём рассинхронизируется и съедает ОСТАТОК ФАЙЛА: замеренная потеря — 391 <c>def</c>
    /// в одном этом файле. Сканер обязан продолжить как ни в чём не бывало.
    /// </summary>
    [Fact]
    public void UnescapedApostropheInLiteral_DoesNotSwallowRestOfFile()
    {
        string src = Crlf(
            "def before():",
            "    pass",
            "",
            "def broken():",
            "    _logger.warning(bu'Couldn't find 'actualValue' field in data %s', recData)",
            "",
            "def after():",
            "    pass",
            "",
            "class AfterClass(object):",
            "",
            "    def method(self):",
            "        pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolKind.Function, Single(symbols, "before").Kind);
        Assert.Equal(SymbolKind.Function, Single(symbols, "after").Kind);
        Assert.Equal(SymbolKind.Class, Single(symbols, "AfterClass").Kind);
        Assert.Equal(SymbolKind.Method, Single(symbols, "method").Kind);
    }

    /// <summary>
    /// Битая декомпиляция (10 файлов корпуса): <c>def isDailySubsQuestParse error at or near ...</c>.
    /// Требование открывающей скобки после имени отсекает такое само собой — придумывать
    /// отдельную эвристику не нужно.
    /// </summary>
    [Fact]
    public void BrokenDecompilation_DefWithoutParen_IsSkipped()
    {
        string src = Crlf(
            "def brokenParse error at or near `LOAD_FAST' instruction at offset 0",
            "",
            "def ok():",
            "    pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Single(symbols);
        Assert.Equal("ok", symbols[0].Name);
    }

    /// <summary>
    /// Строки-продолжения uncompyle6 всегда печатаются ГЛУБЖЕ оператора, а закрывающая скобка
    /// приклеивается к последнему элементу. Именно поэтому отслеживать баланс скобок не нужно:
    /// продолжение не всплывает на отступ родителя и стек не ломает.
    /// </summary>
    [Fact]
    public void ContinuationLines_DoNotBreakStack()
    {
        string src = Crlf(
            "SM_TYPE = Enumeration('System message type', [",
            " 9, ",
            " 10, ",
            " 106])",
            "CURRENCY_TO_SM_TYPE = {(Currency.CREDITS): (SM_TYPE.PurchaseForCredits), ",
            "   (Currency.GOLD): (SM_TYPE.PurchaseForGold)}",
            "",
            "def pushMessage(text):",
            "    pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolKind.Function, Single(symbols, "pushMessage").Kind);
    }

    /// <summary>Вложенность глубже <see cref="PythonSymbolScanner.MaxDepth"/> не роняет сканер.</summary>
    [Fact]
    public void DeepNesting_BeyondStackLimit_DoesNotThrow()
    {
        string src = string.Concat(Enumerable.Range(0, 80).Select(i => new string(' ', i * 2) + $"class C{i}(object):\r\n"));

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(80, symbols.Count);
        Assert.All(symbols, s => Assert.Equal(SymbolKind.Class, s.Kind));
    }

    /// <summary>Патологические входы: пустой файл, только шапка, обрыв без перевода строки.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("# uncompyle6 version 3.9.3\r\n# Python bytecode version base 2.7\r\n# Decompiled from: x\r\n# Embedded file name: scripts/client/X.py\r\n")]
    [InlineData("def a():\r\n    pass\r\ndef b():")]
    [InlineData("\r\n\r\n\r\n")]
    public void PathologicalInput_DoesNotThrow(string source)
    {
        Exception? ex = Record.Exception(() => Scan(source));
        Assert.Null(ex);
    }

    /// <summary>Голый <c>return</c> на уровне модуля есть во всех 8052 файлах и ни на что не влияет.</summary>
    [Fact]
    public void BareModuleLevelReturn_IsIgnored()
    {
        string src = Crlf("def f():", "    pass", "", "g_x = _X()", "return");

        List<SymbolEntry> got = Scan(src);

        // Ровно два символа: функция f и модульный синглтон g_x. Строка «return» символом
        // не становится — ни сама по себе, ни ломая стек отступов для соседей.
        Assert.Equal(2, got.Count);
        Assert.Equal("f", got[0].Name);
        Assert.Equal("g_x", got[1].Name);
        Assert.Equal(SymbolKind.Service, got[1].Kind);
    }

    // ------------------------------------------------------------------ виды символов

    /// <summary>Функция, метод, вложенная функция и вложенный класс различаются только отступом.</summary>
    [Fact]
    public void Kinds_AreDerivedFromIndentStack()
    {
        string src = Crlf(
            "def topLevel():",
            "",
            "    def inner():",
            "        pass",
            "",
            "class Outer(object):",
            "",
            "    class Inner(object):",
            "        pass",
            "",
            "    def method(self):",
            "        pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolKind.Function, Single(symbols, "topLevel").Kind);
        Assert.Equal(SymbolKind.NestedFunction, Single(symbols, "inner").Kind);
        Assert.Equal(SymbolKind.Class, Single(symbols, "Outer").Kind);
        Assert.Equal(SymbolKind.Class, Single(symbols, "Inner").Kind);
        Assert.Equal(SymbolKind.Method, Single(symbols, "method").Kind);
        Assert.Equal(symbols.FindIndex(s => s.Name == "Outer"), Single(symbols, "Inner").Parent);
    }

    /// <summary>
    /// Условный <c>def</c> (под <c>if</c>/<c>try</c>) обязан видеть НАСТОЯЩЕГО родителя:
    /// кадр блока кладётся в стек без символа. Таких определений в корпусе 617.
    /// </summary>
    [Fact]
    public void ConditionalDef_SeesRealParent()
    {
        string src = Crlf(
            "class A(object):",
            "",
            "    if IS_DEVELOPMENT:",
            "",
            "        def devOnly(self):",
            "            pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolKind.Method, Single(symbols, "devOnly").Kind);
        Assert.Equal(0, Single(symbols, "devOnly").Parent);
    }

    // ------------------------------------------------------------------ декораторы

    /// <summary>Декораторы сворачиваются во флаги и переживают несколько строк <c>@</c> подряд.</summary>
    [Fact]
    public void Decorators_AreCapturedAsFlags()
    {
        string src = Crlf(
            "class A(object):",
            "",
            "    @property",
            "    def value(self):",
            "        pass",
            "",
            "    @staticmethod",
            "    def sm():",
            "        pass",
            "",
            "    @classmethod",
            "    def cm(cls):",
            "        pass",
            "",
            "    @adisp_process",
            "    def slow(self):",
            "        pass",
            "",
            "    @abstractmethod",
            "    @dependency.replace_none_kwargs(IItemsCache)",
            "    def both(self):",
            "        pass",
            "",
            "    def plain(self):",
            "        pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(DecoratorFlags.Property, Single(symbols, "value").Decorators);
        Assert.Equal(DecoratorFlags.Static, Single(symbols, "sm").Decorators);
        Assert.Equal(DecoratorFlags.ClassMethod, Single(symbols, "cm").Decorators);
        Assert.Equal(DecoratorFlags.Async, Single(symbols, "slow").Decorators);
        Assert.Equal(DecoratorFlags.Abstract | DecoratorFlags.DependencyKwargs, Single(symbols, "both").Decorators);
        Assert.Equal(DecoratorFlags.None, Single(symbols, "plain").Decorators);
    }

    // ------------------------------------------------------------------ сигнатуры

    /// <summary>Сигнатура забирается вместе со скобками; для класса это список баз.</summary>
    [Fact]
    public void Signature_IsCaptured()
    {
        string src = Crlf(
            "class A(object, IFoo):",
            "",
            "    def onVehicleKilled(self, victimID, killerID=None):",
            "        pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal("(object, IFoo)", Single(symbols, "A").Signature);
        Assert.Equal("(self, victimID, killerID=None)", Single(symbols, "onVehicleKilled").Signature);
    }

    /// <summary>
    /// Многострочная сигнатура (в корпусе их 11 на 117 901) склеивается ограниченным просмотром
    /// вперёд, который НЕ меняет ни стек, ни позицию разбора.
    /// </summary>
    [Fact]
    public void Signature_MultiLine_IsJoined()
    {
        string src = Crlf(
            "def wide(self, a,",
            "         b,",
            "         c):",
            "    pass",
            "",
            "def after():",
            "    pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal("(self, a, b, c)", Single(symbols, "wide").Signature);
        // просмотр вперёд не съел следующие определения
        Assert.Equal(SymbolKind.Function, Single(symbols, "after").Kind);
    }

    /// <summary>Захват сигнатур можно выключить — тогда её просто нет.</summary>
    [Fact]
    public void Signature_CanBeDisabled()
    {
        List<SymbolEntry> symbols = Scan(Crlf("def f(a):", "    pass"),
            ApiIndexOptions.Default with { CaptureSignatures = false });

        Assert.Null(symbols[0].Signature);
    }

    // ------------------------------------------------------------------ события

    /// <summary>
    /// Три реальные формы объявления события плюс имя не на <c>on</c>
    /// (<c>self.setInFocusForPlayer</c> в <c>feedback_adaptor.py:89</c>). Владелец события —
    /// ближайший объемлющий КЛАСС, а не метод <c>__init__</c>, в котором стоит присваивание.
    /// </summary>
    [Fact]
    public void Events_AllRealForms_AreIndexed()
    {
        string src = Crlf(
            "class _PlayerEvents(object):",
            "",
            "    def __init__(self):",
            "        self.onAccountBecomePlayer = Event.Event()",
            "        self.onVehicleKilled = Event.Event(em)",
            "        self.onPlayerFeedbackReceived = Event.Event(self.__eManager)",
            "        self.setInFocusForPlayer = Event.Event(self.__eManager)",
            "        self.onSafe = Event.SafeEvent()",
            "        self.onSuspended = SuspendedEvent(em)",
            "        self.notAnEvent = 5",
            "        if self.onAccountBecomePlayer == Event.Event:",
            "            pass");

        List<SymbolEntry> symbols = Scan(src);
        List<SymbolEntry> events = symbols.Where(s => s.Kind == SymbolKind.Event).ToList();

        Assert.Equal(6, events.Count);
        Assert.Contains(events, e => e.Name == "onAccountBecomePlayer");
        Assert.Contains(events, e => e.Name == "onVehicleKilled");
        Assert.Contains(events, e => e.Name == "setInFocusForPlayer");
        Assert.Contains(events, e => e.Name == "onSuspended");
        Assert.DoesNotContain(events, e => e.Name == "notAnEvent");

        // владелец — класс (индекс 0), а не метод __init__ (индекс 1)
        Assert.All(events, e => Assert.Equal(0, e.Parent));
        Assert.Equal(SymbolOrigin.EventAttribute, events[0].Origin);
    }

    /// <summary>
    /// Событие без <c>self.</c>/<c>cls.</c> внутри МЕТОДА — это локальная переменная, а не
    /// атрибут класса. Настоящий случай: <c>eventHandler = Event.Event()</c> внутри
    /// <c>BattleReplay.setDataCallback</c> (<c>client/BattleReplay.py:1330</c>) попадал в индекс
    /// как событие класса <c>BattleReplay</c>, которого у него нет. В теле класса и на уровне
    /// модуля такая строка событием остаётся.
    /// </summary>
    [Fact]
    public void Event_InsideMethodWithoutSelf_IsNotAnEvent()
    {
        string src = Crlf(
            "class BattleReplay(object):",
            "",
            "    onClassLevel = Event.Event()",
            "",
            "    def setDataCallback(self, name, callback):",
            "        eventHandler = Event.Event()",
            "        self.onRealAttribute = Event.Event()",
            "        return eventHandler",
            "",
            "g_moduleLevel = Event.Event()");

        List<SymbolEntry> events = Scan(src).Where(s => s.Kind == SymbolKind.Event).ToList();

        Assert.DoesNotContain(events, e => e.Name == "eventHandler");
        Assert.Contains(events, e => e.Name == "onClassLevel");
        Assert.Contains(events, e => e.Name == "onRealAttribute");
        Assert.Contains(events, e => e.Name == "g_moduleLevel");
    }

    // ------------------------------------------------------------------ модульные синглтоны

    /// <summary>
    /// <c>g_playerEvents = _PlayerEvents()</c> — главная точка входа мода: подписка пишется
    /// именно через него, а <c>def</c>/<c>class</c> с таким именем в корпусе нет вовсе.
    /// Владельцем становится символ-модуль, поэтому в выдаче видно
    /// <c>PlayerEvents.g_playerEvents</c>, а не голое имя.
    /// </summary>
    [Fact]
    public void ModuleSingleton_IsIndexedAsService()
    {
        var list = new List<SymbolEntry>
        {
            // так его кладёт построитель: символ-модуль идёт первым в списке файла
            new() { FileId = 0, Line = 1, Parent = -1, Name = "PlayerEvents", Kind = SymbolKind.Module, Origin = SymbolOrigin.ModuleFile }
        };

        new PythonSymbolScanner().Scan(0, Crlf(
            "class _PlayerEvents(object):",
            "    pass",
            "",
            "g_playerEvents = _PlayerEvents()",
            "g_replayCtrl = None",
            "g_replayCtrl = _ReplayCtrl()",
            "notASingleton = _Other()",
            "    g_indented = _Nested()"), list, new StringPool());

        List<SymbolEntry> singletons = list.Where(s => s.Kind == SymbolKind.Service).ToList();

        Assert.Equal(2, singletons.Count);
        Assert.Contains(singletons, s => s.Name == "g_playerEvents" && s.Parent == 0);

        // Переприсваивание в том же файле даёт ОДИН символ, а не два.
        Assert.Single(singletons, s => s.Name == "g_replayCtrl");

        // Ни имя без префикса g_, ни строка с отступом синглтоном не считаются.
        Assert.DoesNotContain(list, s => s.Name == "notASingleton");
        Assert.DoesNotContain(list, s => s.Name == "g_indented");
    }

    /// <summary>Правило синглтонов выключается, как и остальные.</summary>
    [Fact]
    public void ModuleSingletons_CanBeDisabled()
    {
        List<SymbolEntry> symbols = Scan(
            Crlf("g_playerEvents = _PlayerEvents()"),
            ApiIndexOptions.Default with { IndexModuleSingletons = false });

        Assert.DoesNotContain(symbols, s => s.Kind == SymbolKind.Service);
    }

    /// <summary>
    /// Сигнатура длиннее прежнего потолка 512 забирается целиком. В игровом коде такой ровно
    /// один — <c>makeCustomizationItemModel</c> (742 символа объявления,
    /// <c>customization_item_data_packer.py:109</c>), и он показывался голым именем,
    /// как будто у него нет параметров вообще.
    /// </summary>
    [Fact]
    public void Signature_LongerThan512_IsStillCaptured()
    {
        string args = string.Join(", ", Enumerable.Range(0, 38).Select(i => $"parameterNumber{i}=None"));
        Assert.True(args.Length is > 512 and < PythonSymbolScanner.SignatureLookahead);

        List<SymbolEntry> symbols = Scan(Crlf($"def makeCustomizationItemModel({args}):", "    pass"));

        string? signature = Single(symbols, "makeCustomizationItemModel").Signature;
        Assert.NotNull(signature);
        Assert.EndsWith("parameterNumber37=None)", signature, StringComparison.Ordinal);
    }

    /// <summary>Индексацию событий можно выключить.</summary>
    [Fact]
    public void Events_CanBeDisabled()
    {
        List<SymbolEntry> symbols = Scan(
            Crlf("class A(object):", "    def __init__(self):", "        self.onX = Event.Event()"),
            ApiIndexOptions.Default with { IndexEvents = false });

        Assert.DoesNotContain(symbols, s => s.Kind == SymbolKind.Event);
    }

    // ------------------------------------------------------------------ синтетические классы

    /// <summary><c>namedtuple</c> (559 штук) и <c>type()</c> (82) индексируются как классы.</summary>
    [Fact]
    public void NamedTupleAndType_AreIndexedAsClasses()
    {
        string src = Crlf(
            "MessageData = namedtuple('MessageData', ('text', 'type'))",
            "Other = collections.namedtuple('Other', 'a b')",
            "Dyn = type('Dyn', (object,), {})",
            "x = someOtherCall(1)",
            "if type(x) is int:",
            "    pass");

        List<SymbolEntry> symbols = Scan(src);

        Assert.Equal(SymbolOrigin.NamedTuple, Single(symbols, "MessageData").Origin);
        Assert.Equal(SymbolKind.Class, Single(symbols, "MessageData").Kind);
        Assert.Equal(SymbolOrigin.NamedTuple, Single(symbols, "Other").Origin);
        Assert.Equal(SymbolOrigin.TypeCall, Single(symbols, "Dyn").Origin);
        Assert.DoesNotContain(symbols, s => s.Name == "x");
    }

    // ------------------------------------------------------------------ константы и docstring

    /// <summary>Константы по умолчанию не индексируются, по флагу — индексируются.</summary>
    [Fact]
    public void Constants_AreOptional()
    {
        string src = Crlf("ARENA_PERIOD = 3", "MAX_HP = 100", "lowerCase = 1", "A == B");

        Assert.DoesNotContain(Scan(src), s => s.Kind == SymbolKind.Constant);

        List<SymbolEntry> with = Scan(src, ApiIndexOptions.Default with { IndexConstants = true });
        Assert.Equal(2, with.Count(s => s.Kind == SymbolKind.Constant));
        Assert.Contains(with, s => s.Name == "ARENA_PERIOD");
    }

    /// <summary>
    /// Ложное срабатывание внутри docstring — известная и ИЗМЕРЕННАЯ цена отказа от состояния:
    /// 148 символов на 156 710 (0.094 %), и все 148 лежат в вендорной библиотеке, то есть
    /// отсекаются фильтром слоя. Тест фиксирует это поведение, а не притворяется, что его нет.
    /// </summary>
    [Fact]
    public void ClassInsideDocstring_IsFalsePositiveByDesign_AndSkippableOnDemand()
    {
        string src = Crlf(
            "def real():",
            "    \"\"\"",
            "    class Fake(object):",
            "        def fake(self): pass",
            "    \"\"\"",
            "    pass");

        Assert.Contains(Scan(src), s => s.Name == "Fake");
        Assert.DoesNotContain(
            Scan(src, ApiIndexOptions.Default with { SkipDocstrings = true }),
            s => s.Name == "Fake");
    }

    // ------------------------------------------------------------------ шапка uncompyle6

    /// <summary>
    /// Виртуальный путь из 4-й строки шапки — мост к traceback этапа 1
    /// (<c>python.log</c> оперирует именно им).
    /// </summary>
    [Fact]
    public void VirtualPath_IsExtractedFromHeader()
    {
        string src = Crlf(
            "# uncompyle6 version 3.9.3",
            "# Python bytecode version base 2.7 (62211)",
            "# Decompiled from: Python 3.12.10",
            "# Embedded file name: scripts/client/gui/SystemMessages.py",
            "from collections import namedtuple");

        Assert.Equal("scripts/client/gui/SystemMessages.py",
            SourceFileService.ExtractVirtualPath(src).ToString());
    }

    /// <summary>Нет шапки — пустой спан, а не исключение.</summary>
    [Fact]
    public void VirtualPath_MissingHeader_ReturnsEmpty()
    {
        Assert.True(SourceFileService.ExtractVirtualPath("def f():\r\n    pass\r\n").IsEmpty);
        Assert.True(SourceFileService.ExtractVirtualPath("").IsEmpty);
    }

    /// <summary>Маркер ищется только в начале файла: совпадение в середине — не шапка.</summary>
    [Fact]
    public void VirtualPath_MarkerBeyondHeaderWindow_IsIgnored()
    {
        string src = new string('#', SourceFileService.HeaderScanLength + 10) + "\r\n# Embedded file name: scripts/x.py\r\n";

        Assert.True(SourceFileService.ExtractVirtualPath(src).IsEmpty);
    }

    // ------------------------------------------------------------------ пул строк

    /// <summary>Пул возвращает ОДИН экземпляр на одинаковые имена — на этом экономятся 2.4 МБ.</summary>
    [Fact]
    public void StringPool_ReturnsSameInstanceForEqualSpans()
    {
        var pool = new StringPool(capacityPow2: 4);

        string a = pool.Intern("onAvatarReady".AsSpan());
        string b = pool.Intern(("x" + "onAvatarReady").AsSpan()[1..]);

        Assert.Same(a, b);
        Assert.Equal(1, pool.Count);
    }

    /// <summary>Пул переживает рост таблицы и не теряет строки.</summary>
    [Fact]
    public void StringPool_GrowsWithoutLosingEntries()
    {
        var pool = new StringPool(capacityPow2: 2);
        string[] names = Enumerable.Range(0, 500).Select(i => "name" + i).ToArray();

        foreach (string n in names) pool.Intern(n.AsSpan());

        Assert.Equal(500, pool.Count);
        foreach (string n in names) Assert.Equal(n, pool.Intern(n.AsSpan()));
    }
}
