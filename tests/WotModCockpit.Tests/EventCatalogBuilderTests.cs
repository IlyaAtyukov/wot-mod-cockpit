using System.Text;
using System.Threading;
using WotModCockpit.Core.ApiCatalog;

namespace WotModCockpit.Tests;

/// <summary>
/// Общая для тестов каталогов сборка по РЕАЛЬНОМУ дереву клиента. Ленивая и одна на весь прогон:
/// полный проход стоит около 0.6 с, платить его в каждом тесте незачем.
/// </summary>
public static class CatalogSource
{
    /// <summary>
    /// Дерево исходников клиента (<see cref="ReposLayout.ClientSource"/>): только при
    /// <c>WOTCOCKPIT_REAL_MACHINE=1</c>, путь — <c>WOTCOCKPIT_CLIENT_SRC</c>.
    /// </summary>
    public static readonly string Dir = ReposLayout.ClientSource;

    /// <summary>Дерево на месте и тесты на своей машине включены.</summary>
    public static bool Available => Directory.Exists(Dir);

    private static readonly Lazy<ApiCatalogSnapshot> LazySnapshot =
        new(() => new ApiCatalogService().Build(Dir), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Снимок каталогов по реальному дереву.</summary>
    public static ApiCatalogSnapshot Snapshot => LazySnapshot.Value;
}

/// <summary>
/// Утилита для синтетических деревьев: файлы пишутся с шапкой uncompyle6 и в CRLF,
/// потому что реальное дерево именно такое, и без CRLF ловушка №1 (пустая строка равна "\r",
/// её отступ 0, стек родителей рушится на каждом методе) не воспроизводится.
/// <para>Тело сначала приводится к LF: сырые строки в тестах наследуют окончания строк
/// исходника, а он после клонирования с <c>core.autocrlf=true</c> лежит в CRLF. Без этого
/// «\r\n» превращалось бы в «\r\r\n», и тесты падали бы только у части разработчиков.</para>
/// </summary>
public sealed class FakeSourceTree : IDisposable
{
    /// <summary>Создаёт временный каталог.</summary>
    public FakeSourceTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "wot-cockpit-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    /// <summary>Корень дерева.</summary>
    public string Root { get; }

    /// <summary>Кладёт файл, дописывая 4-строчную шапку uncompyle6 и переводя всё в CRLF.</summary>
    /// <param name="relPath">Путь вида <c>client/PlayerEvents.py</c>.</param>
    /// <param name="body">Тело файла; строки разделяются <c>\n</c> или <c>\r\n</c>.</param>
    public void Add(string relPath, string body)
    {
        string full = Path.Combine(Root, relPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var sb = new StringBuilder();
        sb.Append("# uncompyle6 version 3.9.3\n");
        sb.Append("# Python bytecode version base 2.7 (62211)\n");
        sb.Append("# Decompiled from: Python 3.12.10\n");
        sb.Append("# Embedded file name: scripts/").Append(relPath).Append('\n');
        sb.Append(body.Replace("\r\n", "\n"));
        File.WriteAllText(full, sb.ToString().Replace("\n", "\r\n"), new UTF8Encoding(false));
    }

    /// <summary>Строит каталоги по этому дереву.</summary>
    /// <returns>Снимок.</returns>
    public ApiCatalogSnapshot Build() => new ApiCatalogService().Build(Root);

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не повод валить тест
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Тесты каталога событий: правила E1 (объявление), E2 (путь до владельца) и E3 (сигнатура)
/// на синтетических деревьях плюс сверка с реальным деревом (эталонные числа — по клиенту 1.44).
/// </summary>
public class EventCatalogBuilderTests
{
    // ------------------------------------------------------------------ E1: объявление

    /// <summary>
    /// Базовый случай: событие объявлено как атрибут, владелец — объемлющий класс,
    /// виртуальный путь берётся из шапки. Индексом def/class такое событие не ловится вообще.
    /// </summary>
    [Fact]
    public void Declaration_IsFoundWithOwnerAndVirtualPath()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/PlayerEvents.py", """
            import Event

            class _PlayerEvents(object):

                def __init__(self):
                    self.onAvatarBecomePlayer = Event.Event()
                    return


            g_playerEvents = _PlayerEvents()
            return
            """);

        ApiCatalogSnapshot snap = tree.Build();
        GameEvent ev = Assert.Single(snap.Events.Events);

        Assert.Equal("onAvatarBecomePlayer", ev.Name);
        Assert.Equal("_PlayerEvents", ev.OwnerClass);
        Assert.Equal("scripts/client/PlayerEvents.py", ev.VirtualPath);
        Assert.Equal("PlayerEvents", ev.ModuleName);
        Assert.Equal(EventCtorKind.Event, ev.CtorKind);
    }

    /// <summary>
    /// Ловушка №1: файлы в CRLF, uncompyle6 ставит пустую строку между методами, и без среза '\r'
    /// пустая строка получает отступ 0 и разрушает стек родителей. Симптом — 0 событий с владельцем.
    /// </summary>
    [Fact]
    public void CrlfBlankLines_DoNotDestroyOwnerStack()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/Many.py", """
            class Owner(object):

                def __init__(self):
                    self.onFirst = Event.Event()

                def other(self):
                    pass

                def more(self):
                    self.onSecond = Event.Event()
            """);

        ApiCatalogSnapshot snap = tree.Build();

        Assert.Equal(2, snap.Events.Events.Count);
        Assert.All(snap.Events.Events, e => Assert.Equal("Owner", e.OwnerClass));
    }

    /// <summary>
    /// Аргумент конструктора (менеджер событий) — норма, а не повод отбросить строку:
    /// <c>Event.Event(em)</c> и <c>Event.Event(self.__eManager)</c> встречаются в реальном коде.
    /// </summary>
    [Theory]
    [InlineData("        self.onX = Event.Event(em)", "em")]
    [InlineData("        self.onX = Event.Event(self.__eManager)", "self.__eManager")]
    [InlineData("        self.onX = Event.Event()", "")]
    [InlineData("        self.onX = Event.SafeEvent()", "")]
    public void ConstructorArgument_IsNotAReasonToSkip(string line, string expectedManager)
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/A.py", "class Owner(object):\n\n    def __init__(self):\n" + line + "\n");

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal("onX", ev.Name);
        Assert.Equal(expectedManager, ev.ManagerArg);
    }

    /// <summary>
    /// Конструктор различается: у <c>SafeEvent</c> исключение из обработчика не всплывает,
    /// у <c>Event</c> — всплывает и способно уронить бой. Это надо писать прямо в карточке.
    /// </summary>
    [Fact]
    public void SafeEvent_DoesNotPropagateHandlerExceptions()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/A.py", """
            class Owner(object):

                def __init__(self):
                    self.onPlain = Event.Event()
                    self.onSafe = Event.SafeEvent()
                    self.onSuspended = Event.SuspendedEvent()
            """);

        var byName = tree.Build().Events.Events.ToDictionary(e => e.Name);

        Assert.True(byName["onPlain"].PropagatesHandlerExceptions);
        Assert.False(byName["onSafe"].PropagatesHandlerExceptions);
        Assert.True(byName["onSuspended"].PropagatesHandlerExceptions);
        Assert.Equal(EventCtorKind.SuspendedEvent, byName["onSuspended"].CtorKind);
    }

    /// <summary>
    /// Имя события НЕ обязано начинаться с <c>on</c> (13 таких из 1030), а приватность кодируется
    /// подчёркиваниями: снаружи на <c>self.__x</c> подписываются по mangled-имени <c>_Owner__x</c>.
    /// </summary>
    [Fact]
    public void PrivateAndNonOnNames_AreHandled()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/A.py", """
            class _Owner(object):

                def __init__(self):
                    self.setGoals = Event.Event()
                    self.__hidden = Event.Event()
                    self._half = Event.Event()
            """);

        var byName = tree.Build().Events.Events.ToDictionary(e => e.Name);

        Assert.Equal("setGoals", byName["setGoals"].SubscriptionAttribute);
        Assert.Equal(2, byName["hidden"].Privacy);
        Assert.Equal("_Owner__hidden", byName["hidden"].SubscriptionAttribute);
        Assert.Equal("_half", byName["half"].SubscriptionAttribute);
    }

    // ------------------------------------------------------------------ E2: путь до владельца

    /// <summary>R1: модульный синглтон в файле владельца даёт готовую строку импорта.</summary>
    [Fact]
    public void R1_Singleton_GivesImportLine()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/gui/notify_center/events.py", """
            class _NotifyCenterEvents(object):

                def __init__(self):
                    self.onNotify = Event.Event()


            g_notifyCenterEvents = _NotifyCenterEvents()
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal(OwnerResolution.Singleton, ev.Resolution);
        Assert.Equal("from gui.notify_center.events import g_notifyCenterEvents", ev.ImportLine);
        Assert.Equal("g_notifyCenterEvents", ev.OwnerExpression);
        Assert.False(ev.RequiresBattle);
    }

    /// <summary>R2: класс реализует интерфейс из <c>skeletons/</c> — путь через DI.</summary>
    [Fact]
    public void R2_Service_GivesDependencyPath()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/gui/shared/utils.py", "class IHangarSpace(object):\n\n    def go(self):\n        pass\n");
        tree.Add("client/HangarSpace.py", """
            class HangarSpace(IHangarSpace):

                def __init__(self):
                    self.onSpaceCreate = Event.Event()
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal(OwnerResolution.Service, ev.Resolution);
        Assert.Equal("IHangarSpace", ev.ServiceInterface);
        Assert.Equal("skeletons.gui.shared.utils", ev.ServiceModule);
    }

    /// <summary>
    /// R3: владелец — боевой контроллер. Путь собирается из двух независимых мест исходников:
    /// <c>repositories.py</c> даёт «слот → BATTLE_CTRL_ID», сам контроллер — «класс → BATTLE_CTRL_ID».
    /// Такое событие требует шаблона A2: вне боя слот равен <c>None</c>.
    /// </summary>
    [Fact]
    public void R3_BattleSlot_IsAssembledFromTwoPlaces()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/gui/battle_session.py", "class IBattleSessionProvider(object):\n\n    def shared(self):\n        pass\n");
        tree.Add("client/gui/battle_control/controllers/repositories.py", """
            class SharedControllersLocator(_ControllersLocator, ISharedControllersLocator):

                def feedback(self):
                    return self._repository.getController(BATTLE_CTRL_ID.FEEDBACK)
            """);
        tree.Add("client/gui/battle_control/controllers/feedback_adaptor.py", """
            class BattleFeedbackAdaptor(IBattleController):

                def __init__(self):
                    self.onPlayerFeedbackReceived = Event.Event(self.__eManager)

                def getControllerID(self):
                    return BATTLE_CTRL_ID.FEEDBACK
            """);

        ApiCatalogSnapshot snap = tree.Build();
        GameEvent ev = Assert.Single(snap.Events.Events);

        Assert.Equal(OwnerResolution.BattleSlot, ev.Resolution);
        Assert.Equal("shared", ev.LocatorKind);
        Assert.Equal("feedback", ev.SlotName);
        Assert.Equal("FEEDBACK", ev.ControllerId);
        Assert.True(ev.RequiresBattle);

        BattleControllerSlot slot = Assert.Single(snap.Services.Slots);
        Assert.Equal("BattleFeedbackAdaptor", slot.ControllerClass);
        Assert.Equal("sessionProvider.shared.feedback", slot.AccessExpression);
    }

    /// <summary>R4: владелец — атрибут игровой сущности; такое событие тоже живёт только в бою.</summary>
    [Fact]
    public void R4_EntityAttribute_RequiresBattle()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client_common/ClientArena.py", """
            class ClientArena(object):

                def __init__(self, em):
                    self.onVehicleKilled = Event.Event(em)
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal(OwnerResolution.EntityAttribute, ev.Resolution);
        Assert.Equal("BigWorld.player().arena", ev.OwnerExpression);
        Assert.True(ev.RequiresBattle);
    }

    /// <summary>R5: владелец создан внутри синглтона и отдан его property — путь <c>g_x.users</c>.</summary>
    [Fact]
    public void R5_Nested_GoesThroughParentProperty()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/messenger/m_constants.py", """
            class _UsersSharedEvents(object):

                def __init__(self):
                    self.onUserActionReceived = Event.Event()


            class _MessengerEvents(object):

                def __init__(self):
                    self.__users = _UsersSharedEvents()

                def users(self):
                    return self.__users


            g_messengerEvents = _MessengerEvents()
            """);

        var events = tree.Build().Events.Events.ToDictionary(e => e.Name);
        GameEvent ev = events["onUserActionReceived"];

        Assert.Equal(OwnerResolution.Nested, ev.Resolution);
        Assert.Equal("g_messengerEvents.users", ev.OwnerExpression);
        Assert.Equal("from messenger.m_constants import g_messengerEvents", ev.ImportLine);
    }

    /// <summary>
    /// Путь не найден — так и пишем. Выдуманный путь хуже отсутствующего: по нему мододел
    /// напишет код, который молча не сработает.
    /// </summary>
    [Fact]
    public void Unresolved_SaysSoHonestly()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/Deep.py", "class Hidden(object):\n\n    def __init__(self):\n        self.onSomething = Event.Event()\n");

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal(OwnerResolution.Unresolved, ev.Resolution);
        Assert.Null(ev.OwnerExpression);
        Assert.Contains("только изнутри клиента", ev.AccessPathDisplay, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ E3: сигнатура

    /// <summary>Сигнатуру даёт место возбуждения: в объявлении она всегда пустая.</summary>
    [Fact]
    public void E3_SignatureComesFromRaiseSite()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client_common/ClientArena.py", """
            class ClientArena(object):

                def __init__(self, em):
                    self.onVehicleKilled = Event.Event(em)

                def __onVehicleKilled(self, argStr):
                    self.onVehicleKilled(victimID, killerID, equipmentID, reason, numVehiclesAffected)
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.True(ev.HasSignature);
        Assert.Equal(new[] { "victimID", "killerID", "equipmentID", "reason", "numVehiclesAffected" }, ev.Signature);
    }

    /// <summary>
    /// E3-caveat: имена событий сталкиваются между владельцами, и без скоупинга по получателю
    /// событие получает ЧУЖУЮ сигнатуру. Проверяем, что вызов из другого класса не подмешивается.
    /// </summary>
    [Fact]
    public void E3_ScopedByOwner_ForeignRaiseSiteDoesNotLeak()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client_common/ClientArena.py", """
            class ClientArena(object):

                def __init__(self, em):
                    self.onVehicleHealthChanged = Event.Event(em)

                def hit(self):
                    self.onVehicleHealthChanged(vehicleID, attackerID, damage)


            class VehicleAppearance(object):

                def ready(self):
                    self.onVehicleHealthChanged()
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.Equal(new[] { "vehicleID", "attackerID", "damage" }, ev.Signature);
        Assert.Single(ev.SignatureCandidates);
    }

    /// <summary>
    /// E3-bis: <c>g_playerEvents.onArenaPeriodChange(*self.__periodInfo)</c> раскрывается по
    /// property класса, где стоит вызов. Заодно это проверка межфайловой части правила E3:
    /// событие объявлено в одном файле, а возбуждается в другом.
    /// </summary>
    [Fact]
    public void E3bis_StarArgs_AreExpandedThroughProperties()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/PlayerEvents.py", """
            class _PlayerEvents(object):

                def __init__(self):
                    self.onArenaPeriodChange = Event.Event()


            g_playerEvents = _PlayerEvents()
            """);
        tree.Add("client_common/ClientArena.py", """
            class ClientArena(object):

                period = property((lambda self: self.__periodInfo[0]))
                periodEndTime = property((lambda self: self.__periodInfo[1]))
                periodLength = property((lambda self: self.__periodInfo[2]))
                periodAdditionalInfo = property((lambda self: self.__periodInfo[3]))

                def __onPeriodChange(self, argStr):
                    g_playerEvents.onArenaPeriodChange(*self.__periodInfo)
            """);

        GameEvent ev = Assert.Single(tree.Build().Events.Events);

        Assert.True(ev.HasSignature);
        Assert.Equal(new[] { "period", "periodEndTime", "periodLength", "periodAdditionalInfo" }, ev.Signature);
    }

    // ------------------------------------------------------------------ устойчивость

    /// <summary>
    /// Битая декомпиляция и литерал с неэкранированным апострофом не имеют права ни уронить
    /// разбор, ни съесть остаток файла: именно на этом теряются сотни объявлений у парсера,
    /// который тащит состояние между строками.
    /// </summary>
    [Fact]
    public void BrokenDecompilation_DoesNotEatTheRestOfTheFile()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/Broken.py", """
            class Owner(object):

                def __init__(self):
                    self.onBefore = Event.Event()

                def isDailySubsQuestParse error at or near `LOAD_FAST' instruction at offset 0

                def warn(self):
                    _logger.warning(bu'Couldn't find 'actualValue' field in data %s', recData)

                def more(self):
                    self.onAfter = Event.Event()
            """);

        var names = tree.Build().Events.Events.Select(e => e.Name).ToList();

        Assert.Contains("onBefore", names);
        Assert.Contains("onAfter", names);
    }

    /// <summary>Патологические входы разбираются без исключений.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("# uncompyle6 version 3.9.3")]
    [InlineData("class A(object):\n    def f(self):\n        self.onX = Event.Event()")]
    [InlineData("\t\tself.onX = Event.Event()")]
    [InlineData("class A:\n class B:\n  class C:\n   class D:\n    self.onX = Event.Event()")]
    public void PathologicalInput_DoesNotThrow(string raw)
    {
        ScannedSourceFile file = ClientSourceScanner.ParseText(raw, "client/x.py");

        Assert.NotNull(file);
        Assert.Equal("x", file.ModuleName);
    }

    /// <summary>
    /// Дерева может не быть вовсе — у другого пользователя дампа uncompyle6 нет.
    /// Это не ошибка: каталоги пустые, статус честный, ничего не бросается.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"C:\nope\wot-src")]
    [InlineData(@"Z:\wot-src")]
    public void MissingSourceTree_YieldsEmptyCatalogsAndHonestStatus(string? dir)
    {
        var svc = new ApiCatalogService();

        ApiCatalogSnapshot snap = svc.Build(dir);

        Assert.Equal(ApiCatalogStatus.NoSources, snap.Status);
        Assert.Empty(snap.Events.Events);
        Assert.Empty(snap.Services.Services);
        Assert.Equal(0, snap.FileCount);
        Assert.All(snap.Hooks, h => Assert.Equal(HookStatus.Unknown, h.Status));
        Assert.Same(snap, svc.Current);
    }

    /// <summary>Асинхронная сборка возвращает то же самое и меняет <c>Current</c>.</summary>
    [Fact]
    public async Task BuildAsync_ReplacesCurrentSnapshot()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/A.py", "class Owner(object):\n\n    def __init__(self):\n        self.onX = Event.Event()\n");
        var svc = new ApiCatalogService();
        ApiCatalogSnapshot? raised = null;
        svc.CatalogChanged += (_, s) => raised = s;

        Assert.Equal(ApiCatalogStatus.NotBuilt, svc.Current.Status);
        ApiCatalogSnapshot snap = await svc.BuildAsync(tree.Root);

        Assert.Equal(ApiCatalogStatus.Ready, snap.Status);
        Assert.Same(snap, svc.Current);
        Assert.Same(snap, raised);
    }

    // ------------------------------------------------------------------ реальное дерево

    /// <summary>
    /// Эталонные цифры по реальному дереву клиента 1.44. Если сканер начнёт терять или выдумывать
    /// события, тест обязан упасть. Дерево другой версии даёт другие числа законно — на нём тест
    /// пропускается.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_MatchesReferenceCounts()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        EventCatalog cat = CatalogSource.Snapshot.Events;

        // Числа стали на 3 меньше прежних (1030/893/300/327/1004) ровно потому, что правило
        // объявления перестало считать событием ЛОКАЛЬНУЮ ПЕРЕМЕННУЮ внутри метода:
        // «eventHandler = Event.Event()» в client/BattleReplay.py:1330 (внутри
        // BattleReplay.setDataCallback), common/Lib/lib-tk/Tkinter.py:938 и common/Lib/sched.py:20.
        // Событий класса BattleReplay.eventHandler в клиенте не существует.
        Assert.Equal(1027, cat.Events.Count);
        Assert.Equal(891, cat.ByName.Count);
        Assert.Equal(298, cat.Events.Select(e => e.RelPath).Distinct().Count());
        Assert.Equal(325, cat.Events.Where(e => e.OwnerClass is not null).Select(e => e.OwnerClass).Distinct().Count());
        Assert.Equal(1001, cat.Events.Count(e => e.CtorKind == EventCtorKind.Event));
        Assert.Equal(19, cat.Events.Count(e => e.CtorKind == EventCtorKind.SuspendedEvent));
        Assert.Equal(7, cat.Events.Count(e => e.CtorKind == EventCtorKind.SafeEvent));

        // Ложных событий-локальных переменных в каталоге больше нет ни одного.
        Assert.DoesNotContain(cat.Events, e => e.SubscriptionAttribute == "eventHandler");
    }

    /// <summary>Резолверы E2 дают ровно то же покрытие, что и эталонная реализация правила (клиент 1.44).</summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    // 465, а не 468: три «события» оказались локальными переменными внутри методов и ушли
    // из каталога вместе со своим Unresolved (см. RealTree_MatchesReferenceCounts).
    [InlineData(OwnerResolution.Unresolved, 465)]
    [InlineData(OwnerResolution.Service, 238)]
    [InlineData(OwnerResolution.BattleSlot, 128)]
    [InlineData(OwnerResolution.Singleton, 127)]
    [InlineData(OwnerResolution.Nested, 37)]
    [InlineData(OwnerResolution.EntityAttribute, 32)]
    public void RealTree_ResolverCoverage(OwnerResolution resolution, int expected)
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        Assert.Equal(expected, CatalogSource.Snapshot.Events.Events.Count(e => e.Resolution == resolution));
    }

    /// <summary>
    /// Killer-фича: загрузчик модов отдаёт моду ровно 6 колбэков, а через <c>g_playerEvents</c>
    /// в корпусе подписываются на десятки различных событий (67 в клиенте 1.44). Ради этой
    /// разницы каталог и существует.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_PlayerEvents_ExposeFarMoreThanTheSixLoaderCallbacks()
    {
        TestEnvironment.RequireClientSource();

        IReadOnlyList<EventSubscriptionStat> subs = CatalogSource.Snapshot.PlayerEventSubscriptions();
        var names = subs.Select(s => s.EventName).ToHashSet(StringComparer.Ordinal);

        if (TestEnvironment.ClientSourceIs("1.44"))
        {
            Assert.Equal(67, names.Count);
        }
        else
        {
            Assert.True(names.Count > 6 * 4, $"подписок на g_playerEvents всего {names.Count}");
        }

        foreach (string loaderCallback in new[]
                 {
                     "onAccountShowGUI", "onAccountBecomePlayer", "onAccountBecomeNonPlayer",
                     "onAvatarBecomePlayer",
                 })
        {
            Assert.Contains(loaderCallback, names);
        }

        // Именно то, до чего мододел иначе не доберётся.
        foreach (string extra in new[] { "onArenaPeriodChange", "onAvatarReady", "onBattleResultsReceived", "onRoundFinished" })
            Assert.Contains(extra, names);
    }

    /// <summary>
    /// Карточки ключевых событий целиком: путь доступа и восстановленная сигнатура.
    /// Это регрессия на самое ценное, что каталог умеет.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_KeyEventCards()
    {
        TestEnvironment.RequireClientSource();

        EventCatalog cat = CatalogSource.Snapshot.Events;

        GameEvent period = cat.ByName["onArenaPeriodChange"].Single(e => e.OwnerClass == "_PlayerEvents");
        Assert.Equal(OwnerResolution.Singleton, period.Resolution);
        Assert.Equal("from PlayerEvents import g_playerEvents", period.ImportLine);
        Assert.Equal(new[] { "period", "periodEndTime", "periodLength", "periodAdditionalInfo" }, period.Signature);

        GameEvent killed = cat.ByName["onVehicleKilled"].Single(e => e.OwnerClass == "ClientArena");
        Assert.Equal(OwnerResolution.EntityAttribute, killed.Resolution);
        Assert.Equal("BigWorld.player().arena", killed.OwnerExpression);
        Assert.Equal(new[] { "victimID", "killerID", "equipmentID", "reason", "numVehiclesAffected" }, killed.Signature);

        GameEvent feedback = cat.ByName["onPlayerFeedbackReceived"].Single(e => e.OwnerClass == "BattleFeedbackAdaptor");
        Assert.Equal(OwnerResolution.BattleSlot, feedback.Resolution);
        Assert.Equal("sessionProvider.shared.feedback", feedback.OwnerExpression);
        Assert.True(feedback.RequiresBattle);

        GameEvent state = cat.ByName["onVehicleStateUpdated"].Single(e => e.OwnerClass == "VehicleStateController");
        Assert.Equal(new[] { "stateID", "value" }, state.Signature);
    }

    /// <summary>
    /// Ни один файл реального дерева не должен пропускаться при чтении. Число файлов сверяется
    /// только на эталонном дереве клиента 1.44 (8052).
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_NoFileIsSkipped()
    {
        TestEnvironment.RequireClientSource();

        ApiCatalogSnapshot snap = CatalogSource.Snapshot;

        Assert.Equal(ApiCatalogStatus.Ready, snap.Status);
        Assert.Equal(0, snap.SkippedFileCount);
        if (TestEnvironment.ClientSourceIs("1.44"))
        {
            Assert.Equal(8052, snap.FileCount);
        }
    }
}
