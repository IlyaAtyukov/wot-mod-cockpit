using WotModCockpit.Core.Preflight;
using WotModCockpit.Core.Preflight.Rules;

namespace WotModCockpit.Tests;

/// <summary>
/// По одному положительному и одному отрицательному случаю на КАЖДОЕ правило.
/// Отрицательный случай важнее: правило, которое ругается на правильный код, хуже, чем
/// правило, которого нет.
/// </summary>
public class PreflightRuleTests
{
    private const string FakeSystemMessages = """
        def pushMessage(text, type=None):
            return None

        SM_TYPE = 1
        """;

    private static void AddFakeClient(PreflightFixture fx)
    {
        fx.AddClientFile("client/gui/__init__.py", "\n");
        fx.AddClientFile("client/gui/SystemMessages.py", FakeSystemMessages);
        fx.AddClientFile("client/PlayerEvents.py", "class _PlayerEvents(object):\n    pass\n\ng_playerEvents = _PlayerEvents()\n");
    }

    // ------------------------------------------------------------------ WOT001

    /// <summary>Игровая работа при импорте — ошибка в цепочке точки входа.</summary>
    [Fact]
    public void Wot001_ModuleLevelGameWork_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            import BigWorld

            BigWorld.callback(1.0, None)
            """);

        IReadOnlyList<PreflightDiagnostic> hits = fx.Run(new ModuleLevelGameWorkRule());

        PreflightDiagnostic hit = Assert.Single(hits);
        Assert.Equal("WOT001", hit.RuleId);
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
        Assert.Contains("BigWorld.quit()", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Та же работа внутри функции и под <c>try</c> — молчание.</summary>
    [Fact]
    public void Wot001_WorkInsideInit_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            import logging
            import BigWorld

            LOG_PREFIX = '[Demo]'
            _logger = logging.getLogger('Demo')


            def init():
                try:
                    BigWorld.callback(1.0, None)
                except Exception:
                    pass
            """);

        Assert.Empty(fx.Run(new ModuleLevelGameWorkRule()));
    }

    /// <summary>Модульный <c>try</c> гасит правило — это и есть предлагаемое лечение.</summary>
    [Fact]
    public void Wot001_ModuleLevelTry_Suppresses()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            import BigWorld

            try:
                BigWorld.callback(1.0, None)
            except Exception:
                pass
            """);

        Assert.Empty(fx.Run(new ModuleLevelGameWorkRule()));
    }

    // ------------------------------------------------------------- WOT002/003/004

    /// <summary>Импорт несуществующего модуля клиента.</summary>
    [Fact]
    public void Wot002_MissingModule_IsReported()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", "from gui.impl.lobby.page.chats_presenter import ChatsPresenter\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new UnknownApiRule()));
        Assert.Equal("WOT002", hit.RuleId);
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
    }

    /// <summary>Существующий модуль и существующее имя — молчание.</summary>
    [Fact]
    public void Wot002_ExistingModule_IsClean()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", "from gui.SystemMessages import pushMessage\n");

        Assert.Empty(fx.Run(new UnknownApiRule()));
    }

    /// <summary>Импорт под <c>try</c> — законная идиома необязательной зависимости.</summary>
    [Fact]
    public void Wot002_ImportInsideTry_IsClean()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", """
            try:
                from gui.impl.lobby.page.chats_presenter import ChatsPresenter
            except ImportError:
                ChatsPresenter = None
            """);

        Assert.Empty(fx.Run(new UnknownApiRule()));
    }

    /// <summary>Модуль есть, имени нет — самый ценный вид находки.</summary>
    [Fact]
    public void Wot003_MissingName_IsReported()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", "from gui.SystemMessages import getLobbyStateMachine\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new UnknownApiRule()));
        Assert.Equal("WOT003", hit.RuleId);
        Assert.Contains("getLobbyStateMachine", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Внутри <c>if &lt;КОНСТАНТА&gt;:</c> отсутствие имени — только предупреждение.</summary>
    [Fact]
    public void Wot003_InsideConstantIf_IsWarning()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", """
            IS_WG = False
            if IS_WG:
                from gui.SystemMessages import getLobbyStateMachine
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new UnknownApiRule()));
        Assert.Equal("WOT003", hit.RuleId);
        Assert.Equal(PreflightSeverity.Warning, hit.Severity);
    }

    /// <summary>Обращение к несуществующему атрибуту игрового модуля.</summary>
    [Fact]
    public void Wot004_MissingAttribute_IsReported()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", """
            from gui import SystemMessages


            def show():
                SystemMessages.pushNothing('x')
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new UnknownApiRule()));
        Assert.Equal("WOT004", hit.RuleId);
        Assert.Contains("pushNothing", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Существующий атрибут и затенённое имя — молчание.</summary>
    [Fact]
    public void Wot004_ExistingAttributeAndShadowedName_AreClean()
    {
        using var fx = new PreflightFixture();
        AddFakeClient(fx);
        fx.AddEntryPoint("mod_demo", """
            from gui import SystemMessages


            def show():
                SystemMessages.pushMessage('x')


            def other(SystemMessages):
                return SystemMessages.whatever
            """);

        Assert.Empty(fx.Run(new UnknownApiRule()));
    }

    /// <summary>Без индекса клиента правило молчит: сверять не с чем, и врать оно не должно.</summary>
    [Fact]
    public void Wot002_WithoutIndex_IsSilent()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "from gui.no.such.module import Nothing\n");

        Assert.Empty(fx.Run(new UnknownApiRule()));
    }

    // ------------------------------------------------------------------ WOT005

    /// <summary><c>init()</c> без защиты — ошибка.</summary>
    [Fact]
    public void Wot005_UnguardedInit_IsError()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            def init():
                from PlayerEvents import g_playerEvents
                g_playerEvents.onAvatarReady += _handler


            def _handler():
                pass
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new EntryPointGuardRule()));
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
        Assert.Contains("BigWorld.quit()", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Декоратор-предохранитель и делегирование защищённой функции — молчание.</summary>
    [Fact]
    public void Wot005_GuardedByDecoratorOrDelegate_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            def _call(name):
                try:
                    return name
                except Exception:
                    return None


            def _safe(func):
                return func


            @_safe
            def init():
                pass


            def fini():
                _call('fini')


            def onConnected():
                _call('onConnected')
            """);

        Assert.Empty(fx.Run(new EntryPointGuardRule()));
    }

    // ------------------------------------------------------------------ WOT006

    /// <summary>Обработчик события без try/except.</summary>
    [Fact]
    public void Wot006_UnguardedHandler_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            def _on_avatar_ready():
                report()


            g_playerEvents.onAvatarReady += _on_avatar_ready
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new EventHandlerGuardRule()));
        Assert.Equal("WOT006", hit.RuleId);
        Assert.Contains("Event.__call__", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Обработчик под декоратором-предохранителем.</summary>
    [Fact]
    public void Wot006_GuardedHandler_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            def _safe(func):
                return func


            @_safe
            def _on_avatar_ready():
                report()


            g_playerEvents.onAvatarReady += _on_avatar_ready
            """);

        Assert.Empty(fx.Run(new EventHandlerGuardRule()));
    }

    // ------------------------------------------------------------------ WOT007

    /// <summary>Обёртка не зовёт оригинал.</summary>
    [Fact]
    public void Wot007_OriginalNeverCalled_IsError()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def install(cls):
                original = getattr(cls, 'showShotResults', None)

                def wrapper(self, results):
                    count(results)

                setattr(cls, 'showShotResults', wrapper)
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new MonkeyPatchOriginalRule()));
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
        Assert.Contains("не вызывает оригинал", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Оригинал вызывается вне своего try — эталонный вариант.</summary>
    [Fact]
    public void Wot007_OriginalAlwaysCalled_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def install(cls):
                original = getattr(cls, 'showShotResults', None)

                def wrapper(self, results, *args, **kwargs):
                    try:
                        count(results)
                    except Exception:
                        pass
                    return original(self, results, *args, **kwargs)

                setattr(cls, 'showShotResults', wrapper)
            """);

        Assert.Empty(fx.Run(new MonkeyPatchOriginalRule()));
    }

    /// <summary>Оригинал в одном try со своей логикой — предупреждение.</summary>
    [Fact]
    public void Wot007_OriginalInsideSharedTry_IsWarning()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def install(cls):
                original = getattr(cls, 'showShotResults', None)

                def wrapper(self, results):
                    try:
                        count(results)
                        return original(self, results)
                    except Exception:
                        pass

                setattr(cls, 'showShotResults', wrapper)
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new MonkeyPatchOriginalRule()));
        Assert.Equal(PreflightSeverity.Warning, hit.Severity);
    }

    /// <summary>
    /// Фреймворковая обёртка ПЕРЕДАЁТ оригинал дальше (<c>func(old, *args)</c>) — вызывать
    /// его будет обработчик, и ругаться нельзя. Ровно так устроены XFW и responsive_reticle.
    /// </summary>
    [Fact]
    public void Wot007_OriginalForwardedToHandler_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "framework", """
            def override(cls, name, func):
                old = getattr(cls, name, None)

                def wrapper(*args, **kwargs):
                    return func(old, *args, **kwargs)

                setattr(cls, name, wrapper)
            """);

        Assert.Empty(fx.Run(new MonkeyPatchOriginalRule()));
    }

    /// <summary>
    /// Полная замена метода через декоратор-патчер — предупреждение, а не ошибка:
    /// это может быть сознательным решением автора.
    /// </summary>
    [Fact]
    public void Wot007_DecoratorFullReplacement_IsWarning()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            @overrideIn(VehicleGunRotator)
            def __rotate(func, self, shotPoint, timeDiff):
                self._VehicleGunRotator__turretRotationSpeed = 0.0
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new MonkeyPatchOriginalRule()));
        Assert.Equal(PreflightSeverity.Warning, hit.Severity);
    }

    // ------------------------------------------------------------------ WOT008

    /// <summary>Снятие оригинала двухаргументным <c>getattr</c>.</summary>
    [Fact]
    public void Wot008_GetAttrWithoutDefault_IsError()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def install(cls, method, newm):
                orig = getattr(cls, method)
                setattr(cls, method, newm)
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new GetAttrWithoutDefaultRule()));
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
    }

    /// <summary>Трёхаргументный <c>getattr</c> с проверкой на <c>None</c>.</summary>
    [Fact]
    public void Wot008_GetAttrWithDefault_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def install(cls, method, newm):
                orig = getattr(cls, method, None)
                if orig is None:
                    return
                setattr(cls, method, newm)
            """);

        Assert.Empty(fx.Run(new GetAttrWithoutDefaultRule()));
    }

    // ------------------------------------------------------------------ WOT009

    /// <summary>Подписка на долгоживущий синглтон без отписки.</summary>
    [Fact]
    public void Wot009_SubscriptionWithoutUnsubscribe_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            def init():
                g_playerEvents.onArenaPeriodChange += _on_period
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new SubscriptionLeakRule()));
        Assert.Equal("WOT009", hit.RuleId);
        Assert.Contains("g_playerEvents.onArenaPeriodChange", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Пара найдена в другом файле дерева — молчание.</summary>
    [Fact]
    public void Wot009_UnsubscribeInAnotherFile_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", "def init():\n    g_playerEvents.onArenaPeriodChange += _on_period\n");
        fx.AddPackageModule("demo", "teardown", "def fini():\n    g_playerEvents.onArenaPeriodChange -= _on_period\n");

        Assert.Empty(fx.Run(new SubscriptionLeakRule()));
    }

    /// <summary>Подписка на собственный объект мода не проверяется.</summary>
    [Fact]
    public void Wot009_OwnEventOwner_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            class g_eventsManager(object):
                onSettingsChanged = None


            def init():
                g_eventsManager.onSettingsChanged += _on_changed
            """);

        Assert.Empty(fx.Run(new SubscriptionLeakRule()));
    }

    // ------------------------------------------------------------------ WOT010

    /// <summary>Самоперепланирующаяся петля без отмены.</summary>
    [Fact]
    public void Wot010_SelfReschedulingLoop_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "retry", """
            def _schedule_patch(attempt=0):
                if attempt >= 60:
                    return
                BigWorld.callback(1.0, lambda: _schedule_patch(attempt + 1))
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new CallbackLoopRule()));
        Assert.Equal("WOT010", hit.RuleId);
    }

    /// <summary>Петля с сохранением id и отменой в дереве — молчание.</summary>
    [Fact]
    public void Wot010_LoopWithCancel_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "retry", """
            _id = None


            def _tick():
                global _id
                _id = BigWorld.callback(1.0, _tick)


            def fini():
                BigWorld.cancelCallback(_id)
            """);

        Assert.Empty(fx.Run(new CallbackLoopRule()));
    }

    // ------------------------------------------------------------------ WOT011

    /// <summary>Слушателей добавлено больше, чем снято.</summary>
    [Fact]
    public void Wot011_UnbalancedListeners_AreReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            def fini():
                pass


            def init():
                g_eventBus.addListener(EVENT, on_app, scope=1)
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new EventBusListenerRule()));
        Assert.Equal("WOT011", hit.RuleId);
        Assert.Equal(PreflightSeverity.Warning, hit.Severity);
    }

    /// <summary>Баланс соблюдён — молчание.</summary>
    [Fact]
    public void Wot011_BalancedListeners_AreClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "hooks", """
            def init():
                g_eventBus.addListener(EVENT, on_app, scope=1)


            def fini():
                g_eventBus.removeListener(EVENT, on_app, scope=1)
            """);

        Assert.Empty(fx.Run(new EventBusListenerRule()));
    }

    // ------------------------------------------------------------------ WOT012

    /// <summary>Мод не пампит лог — замечание.</summary>
    [Fact]
    public void Wot012_NoLogPump_IsInfo()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "def init():\n    pass\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new LogPumpRule()));
        Assert.Equal(PreflightSeverity.Info, hit.Severity);
        Assert.Contains("IS_DEVELOPMENT", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Косвенная петля пампинга (через свою обёртку) распознаётся.</summary>
    [Fact]
    public void Wot012_IndirectPumpLoop_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            def _flush_python_log():
                BigWorld.flushPythonLog()
                _delayed('logpump', 0.2, _flush_python_log)
            """);

        Assert.Empty(fx.Run(new LogPumpRule()));
    }

    // ------------------------------------------------------------------ WOT013

    /// <summary><c>LOG_DEBUG</c> — заглушка на релизе.</summary>
    [Fact]
    public void Wot013_LogDebug_IsInfo()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "def f():\n    LOG_DEBUG('[Demo] loaded')\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new LogDebugNoOpRule()));
        Assert.Equal(PreflightSeverity.Info, hit.Severity);
    }

    /// <summary><c>LOG_NOTE</c> и упоминание в комментарии — молчание.</summary>
    [Fact]
    public void Wot013_LogNoteAndComment_AreClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "# LOG_DEBUG here would be a no-op\ndef f():\n    LOG_NOTE('[Demo] loaded')\n");

        Assert.Empty(fx.Run(new LogDebugNoOpRule()));
    }

    // ------------------------------------------------------------------ WOT014

    /// <summary>Точка входа в подкаталоге — загрузчик её не увидит.</summary>
    [Fact]
    public void Wot014_EntryPointInSubdirectory_IsError()
    {
        using var fx = new PreflightFixture();
        fx.AddModFile("src/res/scripts/client/gui/mods/demo/mod_demo.py", "def init():\n    pass\n");

        IReadOnlyList<PreflightDiagnostic> hits = fx.Run(new EntryPointLayoutRule());

        Assert.Equal(2, hits.Count);                       // «в подкаталоге» и «прямых детей нет»
        Assert.All(hits, h => Assert.Equal(PreflightSeverity.Error, h.Severity));
    }

    /// <summary>Каноническая раскладка — молчание.</summary>
    [Fact]
    public void Wot014_CanonicalLayout_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "def init():\n    pass\n");
        fx.AddPackageModule("demo", "main", "def init():\n    pass\n");

        Assert.Empty(fx.Run(new EntryPointLayoutRule()));
    }

    // ------------------------------------------------------------------ WOT015

    /// <summary>Кириллица без объявления кодировки — SyntaxError внутри клиента.</summary>
    [Fact]
    public void Wot015_NonAsciiWithoutCoding_IsError()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "text", "MSG = u'Привет, танкист'\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new CodingDeclarationRule()));
        Assert.Equal(PreflightSeverity.Error, hit.Severity);
        Assert.Contains("Non-ASCII", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>С объявлением кодировки — молчание.</summary>
    [Fact]
    public void Wot015_NonAsciiWithCoding_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "text", "# -*- coding: utf-8 -*-\nMSG = u'Привет, танкист'\n");

        Assert.Empty(fx.Run(new CodingDeclarationRule()));
    }

    // ------------------------------------------------------------------ WOT016

    /// <summary>Запись по относительному пути.</summary>
    [Fact]
    public void Wot016_RelativeWritePath_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "flag", """
            def mark():
                f = open('replay_need_to_play.killme', 'w')
                f.close()
            """);

        PreflightDiagnostic hit = Assert.Single(fx.Run(new FilePathRule()));
        Assert.Equal("WOT016", hit.RuleId);
    }

    /// <summary>Путь от найденного корня игры и чтение — молчание.</summary>
    [Fact]
    public void Wot016_AnchoredPathAndRead_AreClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "flag", """
            import os

            CONFIG_PATH = os.path.join(os.getcwd(), 'mods', 'configs', 'demo', 'config.json')


            def save():
                f = open(CONFIG_PATH, 'w')
                f.close()


            def load():
                f = open('mods/configs/demo/config.json', 'rb')
                f.close()
            """);

        Assert.Empty(fx.Run(new FilePathRule()));
    }

    // ------------------------------------------------------------------ WOT017

    /// <summary>Не-ASCII без префикса <c>u</c> в аргументе GUI-вызова.</summary>
    [Fact]
    public void Wot017_ByteStringToGui_IsReported()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "ui", "# -*- coding: utf-8 -*-\ndef show():\n    SystemMessages.pushMessage('Мод загружен')\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new StrUnicodeMixRule()));
        Assert.Equal("WOT017", hit.RuleId);
    }

    /// <summary>С префиксом <c>u</c> — молчание.</summary>
    [Fact]
    public void Wot017_UnicodeLiteral_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "ui", "# -*- coding: utf-8 -*-\ndef show():\n    SystemMessages.pushMessage(u'Мод загружен')\n");

        Assert.Empty(fx.Run(new StrUnicodeMixRule()));
    }

    // ------------------------------------------------------------------ WOT018

    /// <summary>Голый импорт API чужого мода.</summary>
    [Fact]
    public void Wot018_BareModApiImport_IsWarning()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "api", "from gui.modsListApi import g_modsListApi\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new ModApiImportRule()));
        Assert.Equal(PreflightSeverity.Warning, hit.Severity);
    }

    /// <summary>Импорт под <c>try</c> с проверкой на <c>None</c>.</summary>
    [Fact]
    public void Wot018_GuardedModApiImport_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "api", """
            g_modsListApi = None
            try:
                from gui.modsListApi import g_modsListApi
            except ImportError:
                pass

            if g_modsListApi is not None:
                pass
            """);

        Assert.Empty(fx.Run(new ModApiImportRule()));
    }

    // ------------------------------------------------------------------ WOT019

    /// <summary>Приватное имя без манглинга.</summary>
    [Fact]
    public void Wot019_UnmangledPrivateName_IsWarning()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", "def f(cls, w):\n    setattr(cls, '__rotate', w)\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new NameManglingRule()));
        Assert.Equal("WOT019", hit.RuleId);
        Assert.Contains("_Cls__rotate", hit.Message, StringComparison.Ordinal);
    }

    /// <summary>Манглированное имя и dunder — молчание.</summary>
    [Fact]
    public void Wot019_MangledAndDunder_AreClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "patch", """
            def f(cls, w):
                setattr(cls, '_VehicleGunRotator__rotate', w)
                setattr(w, '__name__', 'rotate')
            """);

        Assert.Empty(fx.Run(new NameManglingRule()));
    }

    // ------------------------------------------------------------------ WOT020

    /// <summary><c>print</c> вместо логгера.</summary>
    [Fact]
    public void Wot020_Print_IsInfo()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "def f():\n    print '[Demo] loaded'\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new PrintStatementRule()));
        Assert.Equal(PreflightSeverity.Info, hit.Severity);
    }

    /// <summary>Логгер и слово <c>printer</c> — молчание.</summary>
    [Fact]
    public void Wot020_LoggerAndSimilarName_AreClean()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "def f():\n    printer_name = 'x'\n    _logger.info('[Demo] loaded')\n");

        Assert.Empty(fx.Run(new PrintStatementRule()));
    }

    // ------------------------------------------------------------------ WOT021

    /// <summary>Finder в <c>sys.meta_path</c> без снятия.</summary>
    [Fact]
    public void Wot021_MetaPathWithoutRemoval_IsInfo()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "import sys\n\n\ndef install(f):\n    sys.meta_path.insert(0, f)\n");

        PreflightDiagnostic hit = Assert.Single(fx.Run(new SysPathRule()));
        Assert.Equal(PreflightSeverity.Info, hit.Severity);
    }

    /// <summary>Симметричная пара install/remove — молчание.</summary>
    [Fact]
    public void Wot021_SymmetricMetaPath_IsClean()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", """
            import sys


            def install(f):
                sys.meta_path.insert(0, f)


            def remove(i):
                del sys.meta_path[i]
            """);

        Assert.Empty(fx.Run(new SysPathRule()));
    }

    // ------------------------------------------------------------- инфраструктура

    /// <summary>Выключенное правило не запускается.</summary>
    [Fact]
    public void DisabledRule_DoesNotRun()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "def f():\n    print 'x'\n");

        var options = new PreflightOptions();
        options.DisabledRules.Add("WOT020");

        PreflightResult result = fx.RunAll(options);

        Assert.DoesNotContain(result.Diagnostics, d => d.RuleId == "WOT020");
        Assert.DoesNotContain("WOT020", result.RulesRun);
    }

    /// <summary>Свой декоратор-предохранитель настраивается списком.</summary>
    [Fact]
    public void CustomSafeDecorator_SuppressesGuardRules()
    {
        using var fx = new PreflightFixture();
        fx.AddEntryPoint("mod_demo", "@my_guard\ndef init():\n    do_work()\n");

        Assert.Single(fx.Run(new EntryPointGuardRule()));

        var options = new PreflightOptions();
        options.SafeDecorators.Add("my_guard");
        Assert.Empty(fx.Run(new EntryPointGuardRule(), options));
    }

    /// <summary>Упавшее правило не отменяет прогон остальных.</summary>
    [Fact]
    public void ThrowingRule_IsReportedButDoesNotAbort()
    {
        using var fx = new PreflightFixture();
        fx.AddPackageModule("demo", "log", "def f():\n    print 'x'\n");

        var analyzer = new PreflightAnalyzer(new IPreflightRule[] { new ThrowingRule(), new PrintStatementRule() });
        PreflightResult result = analyzer.Analyze(fx.Context());

        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT999");
        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT020");
    }

    private sealed class ThrowingRule : IPreflightRule
    {
        public string Id => "WOT999";

        public string Title => "Правило, которое всегда падает";

        public void Analyze(PreflightContext context, ICollection<PreflightDiagnostic> output) =>
            throw new InvalidOperationException("проверка живучести");
    }
}
