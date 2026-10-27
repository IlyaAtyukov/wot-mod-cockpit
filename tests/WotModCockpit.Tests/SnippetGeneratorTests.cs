using System.Text;
using WotModCockpit.Core.ApiCatalog;

namespace WotModCockpit.Tests;

/// <summary>
/// Запуск НАСТОЯЩЕГО валидатора синтаксиса Python 2.7 из шаблона мода.
/// Проверять сгенерированный код как-то иначе бессмысленно: клиент исполняет 2.7, один py3-изм
/// в теле мода даёт SyntaxError и мод молча мёртв.
/// <para>Запуск — общий с рецептами (<see cref="RecipeToolchain"/>): тот же Python 3 из
/// <see cref="TestPython"/>, а не первый <c>python.exe</c> из PATH. Под 2.7, который у модмейкера
/// нередко стоит в PATH первым, валидатор не падает, а молча теряет часть проверок — и тест мог
/// бы позеленеть зря.</para>
/// </summary>
public static class Py2Validator
{
    /// <summary>Валидатор из репозитория шаблона мода.</summary>
    public static string ScriptPath => RecipeToolchain.SyntaxChecker;

    /// <summary>Валидатор и Python 3 на месте.</summary>
    public static bool Available => RecipeToolchain.CheckerAvailable;

    /// <summary>Что именно не найдено — для сообщения о пропуске теста.</summary>
    public static string Why => RecipeToolchain.MissingReason;

    /// <summary>Прогоняет валидатор по каталогу с <c>.py</c>.</summary>
    /// <param name="dir">Каталог со сниппетами.</param>
    /// <returns>Код возврата (0 — чисто) и полный вывод.</returns>
    public static (int ExitCode, string Output) Run(string dir) => RecipeToolchain.RunSyntaxCheck(dir);
}

/// <summary>
/// Тесты генератора сниппетов. Главный из них — прогон сгенерированного кода через настоящий
/// <c>check_py2_syntax.py</c>: подсунуть мододелу невалидный код хуже, чем не дать ничего.
/// Плюс структурные проверки правил, которые синтаксис не ловит: парная отписка, сохранение
/// и вызов оригинала, изоляция своей работы в <c>try/except</c>, отказ от восстановления
/// оригинала в <c>fini()</c>.
/// </summary>
public class SnippetGeneratorTests : IDisposable
{
    private readonly SnippetGenerator _gen = new();
    private readonly string _out;

    /// <summary>Готовит временный каталог для сниппетов.</summary>
    public SnippetGeneratorTests()
    {
        _out = Path.Combine(Path.GetTempPath(), "wot-cockpit-snip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_out);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_out))
                Directory.Delete(_out, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не повод валить тест
        }

        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------ настоящий валидатор

    /// <summary>
    /// Сниппеты для РЕАЛЬНЫХ элементов каталога (события всех пяти видов доступа, горячие точки,
    /// сервисы) пишутся в файлы и прогоняются через настоящий валидатор Python 2.7.
    /// Это и есть главный тест фичи.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void RealCatalogSnippets_PassThePython27Validator()
    {
        TestEnvironment.RequireClientSource();
        Skip.IfNot(Py2Validator.Available, Py2Validator.Why);

        ApiCatalogSnapshot snap = CatalogSource.Snapshot;
        int written = 0;

        // По одному событию на каждый резолвер плюс поимённо самые ходовые.
        foreach (OwnerResolution r in Enum.GetValues<OwnerResolution>())
        {
            GameEvent? ev = snap.Events.Events.FirstOrDefault(e => e.Resolution == r && e.HasSignature)
                            ?? snap.Events.Events.FirstOrDefault(e => e.Resolution == r);
            if (ev is null)
                continue;

            Write($"ev_{r}_{ev.Name}", _gen.ForEvent(ev, "hello_tanker"));
            written++;
        }

        foreach (string name in new[]
                 {
                     "onArenaPeriodChange", "onVehicleKilled", "onAvatarReady", "onAccountShowGUI",
                     "onPlayerFeedbackReceived", "onVehicleStateUpdated", "onBattleResultsReceived",
                     "onRoundFinished", "onSpaceCreate",
                 })
        {
            if (!snap.Events.ByName.TryGetValue(name, out IReadOnlyList<GameEvent>? list))
                continue;

            GameEvent ev = list.OrderBy(e => e.Resolution == OwnerResolution.Unresolved ? 1 : 0).First();
            Write("evn_" + name, _gen.ForEvent(ev, "hello_tanker"));
            written++;
        }

        foreach (HookPoint h in snap.Hooks.Where(x => x.IsUsable).Take(10))
        {
            Write($"hook_{h.Holder}_{h.Member}", _gen.ForHook(h, "hello_tanker"));
            written++;
        }

        foreach (GameService s in snap.Services.Services.Take(8))
        {
            BattleControllerSlot? slot = s.Name == "IBattleSessionProvider"
                ? snap.Services.Slots.FirstOrDefault(x => x.SlotName == "feedback")
                : null;
            Write("svc_" + s.Name, _gen.ForService(s, "hello_tanker", slot));
            written++;
        }

        Assert.True(written >= 25, $"ожидалось 25+ сниппетов, сгенерировано {written}");

        (int exit, string output) = Py2Validator.Run(_out);
        Assert.True(exit == 0, $"валидатор Python 2.7 вернул {exit}:\n{output}");
    }

    /// <summary>
    /// Тот же прогон валидатора, но на синтетических элементах каталога — он работает и без
    /// дерева исходников. Сюда специально собраны все неудобные случаи: нерезолвнутый владелец,
    /// mangled-имя, аргументы-выражения вместо имён, мусорный идентификатор мода, отсутствующая
    /// в клиенте горячая точка, сервис со слотом боя.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void AwkwardCases_PassThePython27Validator()
    {
        Skip.IfNot(Py2Validator.Available, Py2Validator.Why);

        Write("a_singleton", _gen.ForEvent(Ev("onArenaPeriodChange", OwnerResolution.Singleton,
            import: "from PlayerEvents import g_playerEvents", owner: "g_playerEvents",
            sig: new[] { "period", "periodEndTime", "periodLength", "periodAdditionalInfo" }), "hello_tanker"));

        Write("a_nested", _gen.ForEvent(Ev("onUserActionReceived", OwnerResolution.Nested,
            import: "from messenger.m_constants import g_messengerEvents", owner: "g_messengerEvents.users",
            sig: new[] { "actionID", "user" }), "hello_tanker"));

        Write("a_service", _gen.ForEvent(Ev("onSpaceCreate", OwnerResolution.Service,
            iface: "IHangarSpace", ifaceModule: "skeletons.gui.shared.utils"), "hello_tanker"));

        Write("a2_battle_slot", _gen.ForEvent(Ev("onPlayerFeedbackReceived", OwnerResolution.BattleSlot,
            locator: "shared", slot: "feedback", battle: true, sig: new[] { "feedbackEvents" }), "hello_tanker"));

        Write("a2_entity", _gen.ForEvent(Ev("onVehicleKilled", OwnerResolution.EntityAttribute,
            owner: "BigWorld.player().arena", battle: true,
            sig: new[] { "victimID", "killerID", "equipmentID", "reason", "numVehiclesAffected" }), "hello_tanker"));

        Write("a_unresolved", _gen.ForEvent(Ev("onSomethingHidden", OwnerResolution.Unresolved), "hello_tanker"));

        Write("a_mangled", _gen.ForEvent(Ev("hidden", OwnerResolution.Singleton, owner: "g_x",
            import: "from x import g_x", privacy: 2, ownerClass: "_Owner"), "hello_tanker"));

        // Аргументы места возбуждения — выражения, а не имена: обработчик обязан стать *args.
        Write("a_expr_args", _gen.ForEvent(Ev("onBattleResultsReceived", OwnerResolution.Singleton,
            owner: "g_playerEvents", import: "from PlayerEvents import g_playerEvents",
            sig: new[] { "True", "BattleResultsCache.convertToFullForm(self.__battleResults)" }), "hello_tanker"));

        // Мусорный идентификатор мода не имеет права породить невалидный python.
        Write("a_dirty_modid", _gen.ForEvent(Ev("onX", OwnerResolution.Singleton, owner: "g_x", import: "from x import g_x"),
            "Мой мод 2.0!"));

        Write("b_verified", _gen.ForHook(Hook("Vehicle", "onHealthChanged", HookStatus.Verified,
            module: "Vehicle", signature: "self, newHealth, oldHealth, attackerID, attackReasonID"), "hello_tanker"));

        Write("b_module_level", _gen.ForHook(Hook("game", "handleKeyEvent", HookStatus.ModuleLevel,
            module: "game", signature: "event"), "hello_tanker"));

        Write("b_mangled", _gen.ForHook(Hook("TimersPanel", "_TimersPanel__setFireInVehicle", HookStatus.Verified,
            module: "gui.Scaleform.daapi.view.battle.shared.timers_panel", signature: "self, isInFire"), "hello_tanker"));

        Write("b_missing", _gen.ForHook(Hook("ServerInfoPresenter", "_ServerInfoPresenter__updateModel", HookStatus.Missing),
            "hello_tanker"));

        Write("c_service", _gen.ForService(Svc("IItemsCache", "skeletons.gui.shared"), "hello_tanker"));
        Write("c_service_slot", _gen.ForService(
            Svc("IBattleSessionProvider", "skeletons.gui.battle_session"),
            "hello_tanker",
            new BattleControllerSlot { SlotName = "feedback", LocatorKind = "shared", ControllerId = "FEEDBACK" }));

        (int exit, string output) = Py2Validator.Run(_out);
        Assert.True(exit == 0, $"валидатор Python 2.7 вернул {exit}:\n{output}");
    }

    // ------------------------------------------------------------------ структура шаблонов

    /// <summary>Шаблон A: подписка обязана иметь парную отписку, пригодную для <c>fini()</c>.</summary>
    [Fact]
    public void TemplateA_HasMatchingUnsubscribe()
    {
        GeneratedSnippet s = _gen.ForEvent(
            Ev("onArenaPeriodChange", OwnerResolution.Singleton, owner: "g_playerEvents",
                import: "from PlayerEvents import g_playerEvents", sig: new[] { "period" }),
            "hello_tanker");

        Assert.Equal(SnippetKind.EventSubscription, s.Kind);
        Assert.Contains("owner.onArenaPeriodChange += _onArenaPeriodChange", s.Code, StringComparison.Ordinal);
        Assert.Contains("owner.onArenaPeriodChange -= _onArenaPeriodChange", s.Code, StringComparison.Ordinal);
        Assert.Contains("def unsubscribe():", s.Code, StringComparison.Ordinal);
        Assert.Contains("def fini():\n    unsubscribe()", s.Code, StringComparison.Ordinal);
        Assert.Contains("def _onArenaPeriodChange(period):", s.Code, StringComparison.Ordinal);
        Assert.EndsWith("\n", s.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Шаблон A2 выбирается автоматически для владельцев, которых вне боя не существует,
    /// и подписка становится двухступенчатой.
    /// </summary>
    [Theory]
    [InlineData(OwnerResolution.BattleSlot)]
    [InlineData(OwnerResolution.EntityAttribute)]
    public void TemplateA2_IsChosenForBattleOnlyOwners(OwnerResolution resolution)
    {
        GameEvent ev = resolution == OwnerResolution.BattleSlot
            ? Ev("onX", resolution, locator: "shared", slot: "feedback", battle: true)
            : Ev("onX", resolution, owner: "BigWorld.player().arena", battle: true);

        GeneratedSnippet s = _gen.ForEvent(ev, "hello_tanker");

        Assert.Equal(SnippetKind.LateEventSubscription, s.Kind);
        Assert.Contains("g_playerEvents.onAvatarBecomePlayer += subscribe", s.Code, StringComparison.Ordinal);
        Assert.Contains("g_playerEvents.onAvatarBecomeNonPlayer += unsubscribe", s.Code, StringComparison.Ordinal);
        Assert.Contains("g_playerEvents.onAvatarBecomePlayer -= subscribe", s.Code, StringComparison.Ordinal);
        Assert.Contains("    unsubscribe()\n", s.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Обработчик всегда в <c>try/except</c>: у <c>Event.Event</c> исключение из обработчика
    /// всплывает в клиент и уносит с собой боевой интерфейс.
    /// </summary>
    [Fact]
    public void TemplateA_HandlerBodyIsGuarded()
    {
        string code = _gen.ForEvent(Ev("onX", OwnerResolution.Singleton, owner: "g_x", import: "from x import g_x"), "m").Code;

        int handler = code.IndexOf("def _onX(", StringComparison.Ordinal);
        int guard = code.IndexOf("    try:", handler, StringComparison.Ordinal);
        int except = code.IndexOf("    except Exception:", handler, StringComparison.Ordinal);

        Assert.True(handler >= 0 && guard > handler && except > guard);
    }

    /// <summary>Аргументы-выражения дают честный <c>*args</c> и предупреждение, а не угаданную арность.</summary>
    [Fact]
    public void NonIdentifierSignature_FallsBackToStarArgs()
    {
        GeneratedSnippet s = _gen.ForEvent(
            Ev("onBattleResultsReceived", OwnerResolution.Singleton, owner: "g_playerEvents",
                import: "from PlayerEvents import g_playerEvents",
                sig: new[] { "True", "BattleResultsCache.convertToFullForm(self.__x)" }),
            "hello_tanker");

        Assert.Contains("def _onBattleResultsReceived(*args, **kwargs):", s.Code, StringComparison.Ordinal);
        Assert.Contains(s.Warnings, w => w.Contains("*args", StringComparison.Ordinal));
    }

    /// <summary>
    /// Разная АРНОСТЬ мест возбуждения тоже обязана давать <c>*args</c>.
    /// <para>
    /// В корпусе 1.44 таких событий 16, из них 6 (например <c>SeqTaskQueue.onInited</c>:
    /// два места возбуждения с <c>(index)</c> и два с <c>()</c>) раньше получали обработчик
    /// с фиксированной арностью по «победившему» варианту. Половина мест возбуждения давала бы
    /// <c>TypeError</c>, а у <c>Event.Event</c> исключение из обработчика уходит в клиент.
    /// </para>
    /// </summary>
    [Fact]
    public void ConflictingArityAcrossRaiseSites_FallsBackToStarArgs()
    {
        GameEvent ev = Ev("onInited", OwnerResolution.Singleton, owner: "g_x",
                          import: "from x import g_x", sig: new[] { "index" }) with
        {
            SignatureCandidates = new[]
            {
                new EventSignatureCandidate(new[] { "index" }, 2, "client/tasks.py", 205),
                new EventSignatureCandidate(Array.Empty<string>(), 2, "client/tasks.py", 193),
            },
        };

        GeneratedSnippet s = _gen.ForEvent(ev, "hello_tanker");

        Assert.Contains("def _onInited(*args, **kwargs):", s.Code, StringComparison.Ordinal);
        Assert.DoesNotContain("def _onInited(index):", s.Code, StringComparison.Ordinal);
        Assert.Contains(s.Warnings, w => w.Contains("разным числом аргументов", StringComparison.Ordinal));
    }

    /// <summary>Совпадающая арность у всех мест возбуждения фиксированный обработчик не ломает.</summary>
    [Fact]
    public void AgreeingArityAcrossRaiseSites_KeepsNamedArguments()
    {
        GameEvent ev = Ev("onChanged", OwnerResolution.Singleton, owner: "g_x",
                          import: "from x import g_x", sig: new[] { "vehicleID" }) with
        {
            SignatureCandidates = new[]
            {
                new EventSignatureCandidate(new[] { "vehicleID" }, 3, "client/a.py", 10),
                new EventSignatureCandidate(new[] { "vehID" }, 1, "client/b.py", 20),
            },
        };

        GeneratedSnippet s = _gen.ForEvent(ev, "hello_tanker");

        Assert.Contains("def _onChanged(vehicleID):", s.Code, StringComparison.Ordinal);
    }

    /// <summary>Сигнатура не восстановлена — тоже <c>*args</c> плюс честное предупреждение.</summary>
    [Fact]
    public void MissingSignature_FallsBackToStarArgsWithWarning()
    {
        GeneratedSnippet s = _gen.ForEvent(Ev("onX", OwnerResolution.Singleton, owner: "g_x", import: "from x import g_x"), "m");

        Assert.Contains("def _onX(*args, **kwargs):", s.Code, StringComparison.Ordinal);
        Assert.Contains(s.Warnings, w => w.Contains("восстановить не удалось", StringComparison.Ordinal));
    }

    /// <summary>Приватное событие подписывается по mangled-имени, и об этом сказано вслух.</summary>
    [Fact]
    public void PrivateEvent_UsesMangledAttribute()
    {
        GeneratedSnippet s = _gen.ForEvent(
            Ev("hidden", OwnerResolution.Singleton, owner: "g_x", import: "from x import g_x", privacy: 2, ownerClass: "_Owner"),
            "m");

        Assert.Contains("owner._Owner__hidden += _hidden", s.Code, StringComparison.Ordinal);
        Assert.Contains(s.Warnings, w => w.Contains("mangled", StringComparison.Ordinal));
    }

    /// <summary>
    /// Шаблон B: пять правил кооперативного монкипатча. Каждое куплено болью, поэтому каждое
    /// проверяется отдельно.
    /// </summary>
    [Fact]
    public void TemplateB_IsCooperative()
    {
        string code = _gen.ForHook(
            Hook("Vehicle", "onHealthChanged", HookStatus.Verified, module: "Vehicle",
                signature: "self, newHealth, oldHealth, attackerID, attackReasonID"),
            "hello_tanker").Code;

        // 1. Оригинал берётся в момент установки и сохраняется.
        Assert.Contains("original = getattr(holder, methodName, None)", code, StringComparison.Ordinal);
        Assert.Contains("_PATCHED[key] = original", code, StringComparison.Ordinal);

        // 2. Оригинал вызывается первым, его результат уходит наружу.
        Assert.Contains("result = original(*args, **kwargs)", code, StringComparison.Ordinal);
        Assert.Contains("return result", code, StringComparison.Ordinal);

        // 3. Обёртка со звёздочными аргументами — переживёт смену сигнатуры.
        Assert.Contains("def wrapper(*args, **kwargs):", code, StringComparison.Ordinal);

        // 4. Своя работа изолирована.
        Assert.Contains("_logger.exception(", code, StringComparison.Ordinal);

        // 5. fini() разоружается, а НЕ возвращает оригинал: иначе снесёт патч другого мода.
        int fini = code.IndexOf("def fini():", StringComparison.Ordinal);
        Assert.True(fini > 0);
        Assert.Contains("_ENABLED[0] = False", code[fini..], StringComparison.Ordinal);
        Assert.DoesNotContain("setattr(", code[fini..], StringComparison.Ordinal);

        // Идемпотентность при повторном импорте.
        Assert.Contains("if key in _PATCHED:", code, StringComparison.Ordinal);
        Assert.Contains("from Vehicle import Vehicle", code, StringComparison.Ordinal);
        Assert.Contains("_install(Vehicle, 'onHealthChanged'", code, StringComparison.Ordinal);
    }

    /// <summary>Держатель-модуль импортируется как модуль, а не как класс.</summary>
    [Fact]
    public void TemplateB_ModuleHolder_IsImportedAsModule()
    {
        string code = _gen.ForHook(Hook("game", "handleKeyEvent", HookStatus.ModuleLevel, module: "game", signature: "event"), "m").Code;

        Assert.Contains("import game\n", code, StringComparison.Ordinal);
        Assert.DoesNotContain("from game import game", code, StringComparison.Ordinal);
        Assert.Contains("_install(game, 'handleKeyEvent'", code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Мёртвая горячая точка честно помечается. Ровно это спасёт пользователя при переходе
    /// на новую версию клиента.
    /// </summary>
    [Fact]
    public void TemplateB_MissingHook_IsLoudlyMarked()
    {
        GeneratedSnippet s = _gen.ForHook(Hook("Ghost", "gone", HookStatus.Missing), "m");

        Assert.Contains(s.Warnings, w => w.Contains("НЕ НАЙДЕН", StringComparison.Ordinal));
        Assert.Contains("не найден", s.Code, StringComparison.Ordinal);
    }

    /// <summary>Шаблон C: три режима отказа DI проверяются все три, объявление на уровне класса ленивое.</summary>
    [Fact]
    public void TemplateC_ChecksAllThreeFailureModes()
    {
        GeneratedSnippet s = _gen.ForService(
            Svc("IBattleSessionProvider", "skeletons.gui.battle_session"),
            "hello_tanker",
            new BattleControllerSlot { SlotName = "feedback", LocatorKind = "shared", ControllerId = "FEEDBACK" });

        Assert.Contains("if not dependency.isConfigured():", s.Code, StringComparison.Ordinal);
        Assert.Contains("if not dependency.hasInstance(IBattleSessionProvider):", s.Code, StringComparison.Ordinal);
        Assert.Contains("return dependency.instance(IBattleSessionProvider)", s.Code, StringComparison.Ordinal);
        Assert.Contains("dependency.descriptor(IBattleSessionProvider)", s.Code, StringComparison.Ordinal);
        Assert.Contains("getattr(locator, 'feedback', None)", s.Code, StringComparison.Ordinal);
        Assert.Contains(s.Warnings, w => w.Contains("вне боя", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ каталог горячих точек

    /// <summary>Курируемый список цел и без дублей: ключ — пара (держатель, член).</summary>
    [Fact]
    public void CuratedHooks_AreWellFormed()
    {
        Assert.Equal(252, HookCatalog.Curated.Count);
        Assert.All(HookCatalog.Curated, h =>
        {
            Assert.False(string.IsNullOrWhiteSpace(h.Holder));
            Assert.False(string.IsNullOrWhiteSpace(h.Member));
            Assert.True(h.SiteCount > 0);
            Assert.NotEmpty(h.Mods);
        });

        Assert.Equal(HookCatalog.Curated.Count, HookCatalog.Curated.Select(h => h.Holder + "|" + h.Member).Distinct().Count());
    }

    /// <summary>Без дерева исходников ни одна точка не выдаётся за проверенную.</summary>
    [Fact]
    public void HooksWithoutSources_AreNeverClaimedValid()
    {
        IReadOnlyList<HookPoint> hooks = new HookCatalog().Verify(ClientSourceScan.Empty(@"C:\nope"));

        Assert.Equal(HookCatalog.Curated.Count, hooks.Count);
        Assert.All(hooks, h => Assert.False(h.IsUsable));
    }

    /// <summary>
    /// Проверка по синтетическому дереву: есть в исходниках — Verified, нет — Missing.
    /// Заодно проверяется де-манглинг <c>_Cls__m</c> → <c>__m</c>.
    /// </summary>
    [Fact]
    public void HookVerification_TellsLiveFromDead()
    {
        using var tree = new FakeSourceTree();
        tree.Add("client/Vehicle.py", """
            class Vehicle(BigWorld.Entity):

                def onHealthChanged(self, newHealth, oldHealth, attackerID, attackReasonID):
                    pass
            """);
        tree.Add("client/gui/Scaleform/daapi/view/battle/shared/timers_panel.py", """
            class TimersPanel(TimersPanelMeta):

                def __setFireInVehicle(self, isInFire):
                    pass
            """);

        var byKey = new HookCatalog().Verify(new ClientSourceScanner().Scan(tree.Root))
            .ToDictionary(h => h.Holder + "|" + h.Member);

        HookPoint live = byKey["Vehicle|onHealthChanged"];
        Assert.Equal(HookStatus.Verified, live.Status);
        Assert.Equal("Vehicle", live.ModuleName);
        Assert.Equal("self, newHealth, oldHealth, attackerID, attackReasonID", live.Signature);

        HookPoint mangled = byKey["TimersPanel|_TimersPanel__setFireInVehicle"];
        Assert.Equal(HookStatus.Verified, mangled.Status);
        Assert.Equal("__setFireInVehicle", mangled.DeclaredName);

        Assert.Equal(HookStatus.Missing, byKey["ServerInfoPresenter|_ServerInfoPresenter__updateModel"].Status);
    }

    /// <summary>Сверка курируемого списка с реальным клиентом.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_HookVerification()
    {
        TestEnvironment.RequireClientSource();

        ApiCatalogSnapshot snap = CatalogSource.Snapshot;
        var byKey = snap.Hooks.ToDictionary(h => h.Holder + "|" + h.Member);

        Assert.Equal(252, snap.Hooks.Count);
        Assert.True(snap.VerifiedHookCount >= 200, $"подтверждено {snap.VerifiedHookCount} из 252, ожидалось 200+");

        // Мёртвые точки обязаны быть видны: это WG-ветки XVM, которых в клиенте Леста нет.
        Assert.True(snap.Hooks.Count(h => h.Status == HookStatus.Missing) >= 30);
        Assert.Equal(HookStatus.Missing, byKey["ServerInfoPresenter|_ServerInfoPresenter__updateModel"].Status);

        HookPoint key = byKey["game|handleKeyEvent"];
        Assert.Equal(HookStatus.ModuleLevel, key.Status);
        Assert.Equal("game", key.ModuleName);
        Assert.Equal(3, key.ModCount);

        HookPoint health = byKey["Vehicle|onHealthChanged"];
        Assert.Equal(HookStatus.Verified, health.Status);
        Assert.Equal("Vehicle", health.ModuleName);
        Assert.Equal("self, newHealth, oldHealth, attackerID, attackReasonID", health.Signature);

        // as_*S объявлены в Meta-базе, а патчить надо конкретный класс — обе стороны должны быть видны.
        HookPoint server = byKey["LobbyHeader|as_setServerS"];
        Assert.Equal(HookStatus.Inherited, server.Status);
        Assert.Equal("LobbyHeaderMeta", server.DeclaringClass);
        Assert.NotEqual(server.ModuleName, server.HolderModuleName);
    }

    // ------------------------------------------------------------------ вспомогательное

    private void Write(string name, GeneratedSnippet s)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        File.WriteAllText(Path.Combine(_out, name + ".py"), s.Code, new UTF8Encoding(false));
    }

    private static GameEvent Ev(
        string name,
        OwnerResolution resolution,
        string? owner = null,
        string? import = null,
        string? iface = null,
        string? ifaceModule = null,
        string? locator = null,
        string? slot = null,
        bool battle = false,
        int privacy = 0,
        string? ownerClass = null,
        string[]? sig = null) => new()
        {
            Name = name,
            Privacy = privacy,
            OwnerClass = ownerClass ?? "Owner",
            CtorKind = EventCtorKind.Event,
            RelPath = "client/X.py",
            VirtualPath = "scripts/client/X.py",
            ModuleName = "X",
            Line = 42,
            Resolution = resolution,
            ImportLine = import,
            OwnerExpression = owner,
            ServiceInterface = iface ?? (resolution == OwnerResolution.BattleSlot ? "IBattleSessionProvider" : null),
            ServiceModule = ifaceModule ?? (resolution == OwnerResolution.BattleSlot ? "skeletons.gui.battle_session" : null),
            LocatorKind = locator,
            SlotName = slot,
            ControllerId = slot is null ? null : "FEEDBACK",
            RequiresBattle = battle,
            Signature = sig ?? Array.Empty<string>(),
            HasSignature = sig is not null,
        };

    private static HookPoint Hook(
        string holder,
        string member,
        HookStatus status,
        string? module = null,
        string signature = "") => new()
        {
            Holder = holder,
            Member = member,
            Mods = new[] { "com.modxvm.xvm" },
            SiteCount = 1,
            PatchKinds = new[] { "overrideMethod" },
            Status = status,
            ModuleName = module,
            HolderModuleName = module,
            RelPath = module is null ? null : "client/" + module.Replace('.', '/') + ".py",
            VirtualPath = module is null ? null : "scripts/client/" + module.Replace('.', '/') + ".py",
            Line = status == HookStatus.Missing ? 0 : 100,
            Signature = signature,
            DeclaredName = status == HookStatus.Missing ? null : member,
        };

    private static GameService Svc(string name, string module) => new()
    {
        Name = name,
        ModuleName = module,
        RelPath = "client/" + module.Replace('.', '/') + ".py",
        VirtualPath = "scripts/client/" + module.Replace('.', '/') + ".py",
        Line = 8,
        Methods = new[] { "doWork" },
        UsageCount = 674,
        InstanceCount = 600,
        DescriptorCount = 74,
    };
}
