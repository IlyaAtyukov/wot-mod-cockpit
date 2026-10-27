using WotModCockpit.Core.ApiCatalog;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты каталога сервисов: правила S1 (интерфейсы из <c>skeletons/</c>), S2 (ранг полезности
/// по числу обращений к DI), S3 (реализации) и S4 (таблица слотов боевой сессии).
/// </summary>
public class ServiceCatalogBuilderTests
{
    // ------------------------------------------------------------------ S1: интерфейсы

    /// <summary>
    /// Интерфейс — класс нулевого отступа под <c>client/skeletons/**</c>. Импортируемый модуль
    /// получается отбрасыванием первого сегмента пути; хвост <c>/__init__</c> отрезается.
    /// </summary>
    [Theory]
    [InlineData("client/skeletons/gui/battle_session.py", "skeletons.gui.battle_session")]
    [InlineData("client/skeletons/gui/shared/__init__.py", "skeletons.gui.shared")]
    [InlineData("client/skeletons/connection_mgr.py", "skeletons.connection_mgr")]
    public void S1_ModuleName_DropsRootSegmentAndInit(string relPath, string expectedModule)
    {
        using var tree = new FakeSourceTree();
        tree.Add(relPath, """
            class IThing(object):

                def doWork(self):
                    pass

                def _hidden(self):
                    pass
            """);

        GameService svc = Assert.Single(tree.Build().Services.Services);

        Assert.Equal("IThing", svc.Name);
        Assert.Equal(expectedModule, svc.ModuleName);
        Assert.True(svc.IsInterface);
        Assert.Equal(new[] { "doWork" }, svc.Methods);
    }

    /// <summary>Классы вне <c>skeletons/</c> в каталог сервисов не попадают.</summary>
    [Fact]
    public void S1_OnlySkeletonsAreServices()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/a.py", "class IReal(object):\n    pass\n");
        tree.Add("client/gui/b.py", "class INotASkeleton(object):\n    pass\n");

        GameService svc = Assert.Single(tree.Build().Services.Services);

        Assert.Equal("IReal", svc.Name);
    }

    // ------------------------------------------------------------------ S2: ранг полезности

    /// <summary>
    /// Ранг — это число обращений к <c>dependency.instance/descriptor</c>, и сортировка по нему
    /// обязательна: иначе сотня интерфейсов из одного модуля похоронит под собой нужные шесть.
    /// </summary>
    [Fact]
    public void S2_UsageCount_DrivesOrdering()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/s.py", "class IHot(object):\n    pass\n\n\nclass ICold(object):\n    pass\n");
        tree.Add("client/one.py", """
            class A(object):

                def f(self):
                    x = dependency.instance(IHot)
                    y = dependency.instance(IHot)
                    z = dependency.descriptor(IHot)
                    w = dependency.instance(ICold)
            """);

        ServiceCatalog cat = tree.Build().Services;

        Assert.Equal(new[] { "IHot", "ICold" }, cat.Services.Select(s => s.Name));
        Assert.Equal(3, cat.ByName["IHot"].UsageCount);
        Assert.Equal(2, cat.ByName["IHot"].InstanceCount);
        Assert.Equal(1, cat.ByName["IHot"].DescriptorCount);
        Assert.Equal(4, cat.DependencySiteCount);
    }

    /// <summary>Квалифицированное имя типа сводится к короткому: <c>a.b.IHot</c> — тот же <c>IHot</c>.</summary>
    [Fact]
    public void S2_QualifiedTypeName_IsReducedToShortName()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/s.py", "class IHot(object):\n    pass\n");
        tree.Add("client/one.py", "class A(object):\n\n    def f(self):\n        x = dependency.instance(skeletons.s.IHot)\n");

        Assert.Equal(1, tree.Build().Services.ByName["IHot"].UsageCount);
    }

    // ------------------------------------------------------------------ S3: реализации

    /// <summary>Реализация — класс вне <c>skeletons/</c>, у которого интерфейс в базах.</summary>
    [Fact]
    public void S3_Implementation_IsFoundOutsideSkeletons()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/skeletons/s.py", "class IHangarSpace(object):\n    pass\n");
        tree.Add("client/HangarSpace.py", "class HangarSpace(IHangarSpace):\n\n    def go(self):\n        pass\n");

        ServiceImplementation impl = Assert.Single(tree.Build().Services.ByName["IHangarSpace"].Implementations);

        Assert.Equal("HangarSpace", impl.ClassName);
        Assert.Equal("client/HangarSpace.py", impl.RelPath);
    }

    // ------------------------------------------------------------------ S4: слоты боя

    /// <summary>
    /// Таблица слотов собирается из двух независимых мест и без них бесполезна: сам по себе
    /// <c>sessionProvider.shared.ammo</c> ничего не говорит о том, какой это класс и какие у него события.
    /// </summary>
    [Fact]
    public void S4_SlotTable_LinksSlotToControllerClass()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/gui/battle_control/controllers/repositories.py", """
            class SharedControllersLocator(_ControllersLocator, ISharedControllersLocator):

                def ammo(self):
                    return self._repository.getController(BATTLE_CTRL_ID.AMMO)


            class DynamicControllersLocator(_ControllersLocator, IDynamicControllersLocator):

                def radar(self):
                    return self._repository.getController(BATTLE_CTRL_ID.RADAR)
            """);
        tree.Add("client/gui/battle_control/controllers/consumables/ammo_ctrl.py", """
            class AmmoController(IBattleController):

                def __init__(self):
                    self.onShellsAdded = Event.Event()

                def getControllerID(self):
                    return BATTLE_CTRL_ID.AMMO
            """);

        ServiceCatalog cat = tree.Build().Services;

        Assert.Equal(2, cat.Slots.Count);
        BattleControllerSlot ammo = cat.Slots.Single(s => s.SlotName == "ammo");
        Assert.Equal("shared", ammo.LocatorKind);
        Assert.Equal("AMMO", ammo.ControllerId);
        Assert.Equal("AmmoController", ammo.ControllerClass);
        Assert.Equal(1, ammo.EventCount);

        BattleControllerSlot radar = cat.Slots.Single(s => s.SlotName == "radar");
        Assert.Equal("dynamic", radar.LocatorKind);
        Assert.Null(radar.ControllerClass);
    }

    // ------------------------------------------------------------------ пустое состояние

    /// <summary>Без дерева исходников каталог пустой, а не сломанный.</summary>
    [Fact]
    public void NoSources_YieldsEmptyCatalog()
    {
        ServiceCatalog cat = new ServiceCatalogBuilder().Build(ClientSourceScan.Empty(@"C:\nope"));

        Assert.Empty(cat.Services);
        Assert.Empty(cat.Slots);
        Assert.Equal(0, cat.DependencySiteCount);
    }

    // ------------------------------------------------------------------ реальное дерево

    /// <summary>Эталонные цифры каталога сервисов по реальному дереву клиента 1.44.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_MatchesReferenceCounts()
    {
        TestEnvironment.RequireReferenceClientSource("1.44");

        ServiceCatalog cat = CatalogSource.Snapshot.Services;

        Assert.Equal(215, cat.Services.Count);
        Assert.Equal(206, cat.Services.Count(s => s.IsInterface));
        Assert.Equal(47, cat.Services.Select(s => s.ModuleName).Distinct().Count());
        Assert.Equal(3881, cat.DependencySiteCount);
        Assert.Equal(130, cat.Services.Count(s => s.UsageCount > 0));
    }

    /// <summary>
    /// Порядок выдачи: самые нужные сервисы наверху. Без сортировки по числу обращений
    /// сотня интерфейсов одного лишь <c>skeletons.gui.game_control</c> сделает список бесполезным.
    /// Точная тройка лидеров и число обращений сверяются только на эталонном дереве клиента 1.44.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_TopServicesAreTheUsefulOnes()
    {
        TestEnvironment.RequireClientSource();

        ServiceCatalog cat = CatalogSource.Snapshot.Services;

        if (TestEnvironment.ClientSourceIs("1.44"))
        {
            Assert.Equal("IItemsCache", cat.Services[0].Name);
            Assert.Equal(674, cat.Services[0].UsageCount);
            Assert.Equal("ILobbyContext", cat.Services[1].Name);
            Assert.Equal("IBattleSessionProvider", cat.Services[2].Name);
        }

        Assert.Equal("skeletons.gui.battle_session", cat.ByName["IBattleSessionProvider"].ModuleName);
        Assert.Equal("skeletons.gui.shared", cat.ByName["IItemsCache"].ModuleName);
        Assert.Equal("skeletons.gui.system_messages", cat.ByName["ISystemMessages"].ModuleName);

        // Список отсортирован по убыванию полезности — это контракт, а не случайность.
        for (int i = 1; i < cat.Services.Count; i++)
            Assert.True(cat.Services[i - 1].UsageCount >= cat.Services[i].UsageCount);
    }

    /// <summary>
    /// Таблица слотов боевой сессии на реальном дереве — самая ценная часть для боевых модов.
    /// Число слотов сверяется только на эталонном дереве клиента 1.44.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_BattleSlots()
    {
        TestEnvironment.RequireClientSource();

        ServiceCatalog cat = CatalogSource.Snapshot.Services;

        if (TestEnvironment.ClientSourceIs("1.44"))
        {
            Assert.Equal(27, cat.Slots.Count(s => s.LocatorKind == "shared"));
            Assert.Equal(31, cat.Slots.Count(s => s.LocatorKind == "dynamic"));
        }

        BattleControllerSlot feedback = cat.Slots.Single(s => s.SlotName == "feedback");
        Assert.Equal("FEEDBACK", feedback.ControllerId);
        Assert.Equal("BattleFeedbackAdaptor", feedback.ControllerClass);
        Assert.True(feedback.EventCount >= 20, $"у BattleFeedbackAdaptor ожидалось 20+ событий, найдено {feedback.EventCount}");

        BattleControllerSlot ammo = cat.Slots.Single(s => s.SlotName == "ammo");
        Assert.Equal("AMMO", ammo.ControllerId);
        Assert.Equal("AmmoController", ammo.ControllerClass);
    }
}
