using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiCatalog;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Экран «Браузер API» поверх НАСТОЯЩЕГО шва Core — без подставного бэкенда.
///
/// <para><b>Зачем отдельный файл.</b> Соседние тесты работают на <c>FakeBackend</c>, который
/// возвращает заранее положенные попадания на любой запрос. Такой бэкенд не способен увидеть
/// целый класс дефектов: он отвечает одинаково и на <c>update</c>, и на <c>Foo.update</c>.
/// Ровно на этом и проехал главный дефект панели «где вызывается» — приёмником подставлялось
/// ИМЯ КЛАССА-владельца, и на настоящем дереве панель отвечала «ссылок не найдено» для
/// 36 методов из 36 проверенных, при том что голое имя даёт от 34 до 1873 попаданий.
/// Здесь дерево настоящее (маленькое, но настоящее), индекс и поиск ссылок — настоящие.</para>
/// </summary>
public class ApiBrowserRealBackendTests : IDisposable
{
    private readonly UiThread _ui = new();
    private readonly string _root;
    private readonly List<IDisposable> _disposables = new();
    private ApiBrowserViewModel? _vm;

    /// <summary>Пишет маленькое дерево клиента во временный каталог.</summary>
    public ApiBrowserRealBackendTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wot-cockpit-realbackend-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        WriteTree();
    }

    /// <summary>Гасит модель, сервисы и временный каталог.</summary>
    public void Dispose()
    {
        if (_vm is not null)
        {
            try { _ui.Invoke(() => _vm!.Dispose()); }
            catch (Exception) { /* закрытие не должно маскировать причину падения теста */ }
        }

        foreach (IDisposable d in _disposables)
        {
            try { d.Dispose(); }
            catch (Exception) { /* см. выше */ }
        }

        _ui.Dispose();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог — не повод валить тест
        }

        GC.SuppressFinalize(this);
    }

    // ================================================================= дерево

    private void Write(string relativePath, params string[] lines)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Join("\r\n", lines) + "\r\n");
    }

    private static string[] Header(string virtualPath) =>
    [
        "# uncompyle6 version 3.9.3",
        "# Python bytecode version base 2.7 (62211)",
        "# Decompiled from: Python 3.12.10",
        "# Embedded file name: " + virtualPath
    ];

    /// <summary>
    /// Дерево повторяет ровно те формы, из-за которых уточнение приёмником и не работало:
    /// метод зовут через <c>self.</c> и через переменную-экземпляр, а на событие подписываются
    /// через модульный синглтон.
    /// </summary>
    private void WriteTree()
    {
        Write("client/skeletons/gui/system_messages.py",
            [.. Header("scripts/client/skeletons/gui/system_messages.py"),
             "class ISystemMessages(object):",
             "",
             "    def pushMessage(self, message, type=None):",
             "        raise NotImplementedError"]);

        Write("client/gui/SystemMessages.py",
            [.. Header("scripts/client/gui/SystemMessages.py"),
             "def pushMessage(text, type=None):",
             "    pass",
             "",
             "class SystemMessagesInterface(object):",
             "",
             "    def pushMessage(self, text):",
             "        pass"]);

        Write("client/PlayerEvents.py",
            [.. Header("scripts/client/PlayerEvents.py"),
             "class _PlayerEvents(object):",
             "",
             "    def __init__(self):",
             "        self.onAvatarReady = Event.Event()",
             "        self.onAccountShowGUI = Event.Event()",
             "",
             "",
             "g_playerEvents = _PlayerEvents()"]);

        Write("client/Account.py",
            [.. Header("scripts/client/Account.py"),
             "from PlayerEvents import g_playerEvents",
             "",
             "class PlayerAccount(object):",
             "",
             "    def start(self):",
             "        g_playerEvents.onAvatarReady += self.__onReady",
             "        self.pushMessage('hello')",
             "",
             "    def stop(self):",
             "        g_playerEvents.onAvatarReady -= self.__onReady",
             "        systemMessages = _get()",
             "        systemMessages.pushMessage('bye')"]);

        Write("common/Event.py",
            [.. Header("scripts/common/Event.py"),
             "class Event(list):",
             "",
             "    def __call__(self, *args):",
             "        pass"]);
    }

    private CoreApiBrowserBackend Backend()
    {
        var index = new ApiIndexService(new Settings(_root), new SourceFileService());
        _disposables.Add(index);
        return new CoreApiBrowserBackend(index, new ApiCatalogService());
    }

    private ApiBrowserViewModel Screen(IApiBrowserBackend backend)
    {
        ApiBrowserViewModel vm = _ui.Invoke(() =>
        {
            var model = new ApiBrowserViewModel(backend, new Settings(_root), new NoopEditor());
            model.AttachSettings(new CockpitSettings { WotSrcDir = _root }, () => { });
            model.Initialize();
            return model;
        });

        _vm = vm;
        _ui.WaitUntil(() => vm.HasIndex, "индекс собран", diagnostics: () => vm.IndexStatusText);
        return vm;
    }

    // ================================================================= xref по методу

    /// <summary>
    /// РЕГРЕССИЯ ГЛАВНОГО ДЕФЕКТА. Метод, выбранный в выдаче поиска, обязан дать НЕПУСТОЙ
    /// список мест вызова при включённой галочке «приёмник».
    /// <para>
    /// Раньше приёмником подставлялось имя класса-владельца (<c>ISystemMessages.pushMessage</c>),
    /// а в исходниках так не пишут никогда: пишут <c>self.pushMessage</c> и
    /// <c>systemMessages.pushMessage</c>. Панель отвечала «ссылок не найдено» и — что хуже —
    /// не показывала даже подсказку «частые приёмники», потому что та строится из попаданий.
    /// </para>
    /// </summary>
    [Fact]
    public void Method_SelectedInSearch_HasReferences()
    {
        ApiBrowserViewModel vm = Screen(Backend());

        _ui.Invoke(() => vm.SearchText = "pushMessage");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача поиска", diagnostics: () => vm.SearchStatus);

        SymbolViewModel method = _ui.Invoke(() => vm.Results.First(r => r.Symbol.Kind == SymbolKind.Method));
        Assert.NotNull(method.Symbol.Owner);   // владелец у метода есть — но приёмником он не становится

        _ui.Invoke(() => vm.SelectedResult = method);
        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "места вызова метода",
            diagnostics: () => $"title={vm.XrefTitle} status={vm.XrefStatus} hint={vm.XrefReceiverHint}");

        _ui.Invoke(() =>
        {
            Assert.True(vm.XrefUseReceiver, "галочка «приёмник» по умолчанию включена — в этом и был дефект");
            Assert.Equal("pushMessage", vm.XrefTitle);
            Assert.DoesNotContain("ссылок не найдено", vm.XrefStatus, StringComparison.Ordinal);

            // Подсказка про частые приёмники — то, чем пользователь сузит список сам.
            Assert.Contains("self", vm.XrefReceiverHint, StringComparison.Ordinal);
            Assert.Contains(vm.XrefHits, h => h.Text.Contains("systemMessages.pushMessage", StringComparison.Ordinal));
        });
    }

    /// <summary>
    /// Событие каталога — противоположный случай: у него приёмник НАСТОЯЩИЙ
    /// (модульный синглтон <c>g_playerEvents</c>), и уточнение обязано работать и сужать.
    /// </summary>
    [Fact]
    public void CatalogEvent_KeepsRealSingletonReceiver()
    {
        ApiBrowserViewModel vm = Screen(Backend());

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог событий", diagnostics: () => vm.CatalogStatus);

        CatalogItemViewModel? ready = _ui.Invoke(() => vm.CatalogItems.FirstOrDefault(i => i.Entry.Name == "onAvatarReady"));
        Assert.NotNull(ready);
        Assert.Equal("g_playerEvents", ready!.Entry.XrefReceiver);

        _ui.Invoke(() => vm.SelectedCatalogItem = ready);
        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "подписки на событие",
            diagnostics: () => $"title={vm.XrefTitle} status={vm.XrefStatus}");

        _ui.Invoke(() =>
        {
            Assert.Equal("g_playerEvents.onAvatarReady", vm.XrefTitle);
            Assert.Contains(vm.XrefHits, h => h.Hit.Kind == ApiXrefKind.EventSubscription);
        });
    }

    /// <summary>
    /// Модульный синглтон должен НАХОДИТЬСЯ поиском: это главная точка входа мода
    /// (<c>g_playerEvents.onX += handler</c>), и её отсутствие в индексе читалось как
    /// «такого в API нет».
    /// </summary>
    [Fact]
    public void ModuleSingleton_IsSearchableAndHasReferences()
    {
        ApiBrowserViewModel vm = Screen(Backend());

        _ui.Invoke(() => vm.SearchText = "g_playerEvents");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача поиска", diagnostics: () => vm.SearchStatus);

        _ui.Invoke(() =>
        {
            SymbolViewModel first = vm.Results[0];
            Assert.Equal("g_playerEvents", first.Name);
            Assert.Equal("PlayerEvents.g_playerEvents", first.Symbol.QualifiedName);
            Assert.Equal("from PlayerEvents import g_playerEvents", first.ImportLine);
            vm.SelectedResult = first;
        });

        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "употребления синглтона",
            diagnostics: () => vm.XrefStatus);
    }

    /// <summary>
    /// Запрос в форме, которой мододел называет API (и которую кокпит сам показывает
    /// в заголовке карточки), обязан находить символ.
    /// </summary>
    [Fact]
    public void DottedQuery_FindsTheSymbol()
    {
        ApiBrowserViewModel vm = Screen(Backend());

        _ui.Invoke(() => vm.SearchText = "ISystemMessages.pushMessage");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача по запросу с точкой",
            diagnostics: () => vm.SearchStatus);

        _ui.Invoke(() => Assert.Equal("ISystemMessages.pushMessage", vm.Results[0].Symbol.QualifiedName));
    }

    // ================================================================= каталоги и переиндексация

    /// <summary>
    /// Переиндексация обязана пересобирать и доменные каталоги. Главный сценарий кнопки
    /// «переиндексировать» — «я перегенерировал дамп клиента новой версией», путь при этом
    /// ТОТ ЖЕ, и сравнения одного лишь пути мало: раньше символы обновлялись, а списки
    /// событий, сервисов и горячих точек оставались от предыдущего дампа.
    /// </summary>
    [Fact]
    public async Task Reindex_RebuildsCatalogs()
    {
        CoreApiBrowserBackend backend = Backend();

        await backend.ReindexAsync(_root, CancellationToken.None);
        IReadOnlyList<ApiCatalogEntry> before = await backend.GetCatalogAsync(ApiCatalogKind.Events, CancellationToken.None);
        Assert.Contains(before, e => e.Name == "onAvatarReady");
        Assert.DoesNotContain(before, e => e.Name == "onBrandNewEvent");

        // «Новая версия клиента»: тот же путь, другое содержимое.
        Write("client/PlayerEvents.py",
            [.. Header("scripts/client/PlayerEvents.py"),
             "class _PlayerEvents(object):",
             "",
             "    def __init__(self):",
             "        self.onAvatarReady = Event.Event()",
             "        self.onBrandNewEvent = Event.Event()",
             "",
             "",
             "g_playerEvents = _PlayerEvents()"]);

        await backend.ReindexAsync(_root, CancellationToken.None);
        IReadOnlyList<ApiCatalogEntry> after = await backend.GetCatalogAsync(ApiCatalogKind.Events, CancellationToken.None);

        Assert.Contains(after, e => e.Name == "onBrandNewEvent");
    }

    /// <summary>
    /// Отмена индексации НЕ имеет права уничтожать уже работающий индекс: «прервать» означает
    /// «не строй новый», а не «выключи браузер API».
    /// </summary>
    [Fact]
    public async Task CancelledRebuild_KeepsPreviousIndex()
    {
        var service = new ApiIndexService(new Settings(_root), new SourceFileService());
        _disposables.Add(service);

        ApiIndexBuildStatus first = await service.RebuildAsync(_root);
        Assert.True(first.IsReady);
        int symbols = first.SymbolCount;
        Assert.True(symbols > 0);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        ApiIndexBuildStatus second = await service.RebuildAsync(_root, null, cancelled.Token);

        // Индекс цел, поиск по нему работает, и в сообщении честно сказано, что показано.
        Assert.True(second.IsReady, second.Message);
        Assert.Equal(symbols, service.Index.SymbolCount);
        Assert.Contains("прервана", second.Message, StringComparison.Ordinal);
        Assert.NotEmpty(service.Search("pushMessage"));
    }

    // ================================================================= настоящее дерево клиента

    /// <summary>
    /// То же самое, но на НАСТОЯЩЕМ дереве клиента и на тех именно методах, на которых
    /// замерялся дефект (клиент 1.44): <c>ISettingsCache.update</c> давал 0 против 1873,
    /// <c>IItemsRequester.getVehicle</c> — 0 против 141.
    /// </summary>
    [SkippableTheory]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    [InlineData("update")]
    [InlineData("getVehicle")]
    [InlineData("pushMessage")]
    [InlineData("startVisual")]
    public void RealTree_AnyMethod_HasReferences(string query)
    {
        TestEnvironment.RequireClientSource();

        var index = new ApiIndexService(new Settings(RealWotSource.Dir), new SourceFileService());
        _disposables.Add(index);
        var backend = new CoreApiBrowserBackend(index, new ApiCatalogService());

        ApiBrowserViewModel vm = _ui.Invoke(() =>
        {
            var model = new ApiBrowserViewModel(backend, new Settings(RealWotSource.Dir), new NoopEditor());
            model.AttachSettings(new CockpitSettings { WotSrcDir = RealWotSource.Dir }, () => { });
            model.Initialize();
            return model;
        });

        _vm = vm;
        _ui.WaitUntil(() => vm.HasIndex, "индекс настоящего дерева", TimeSpan.FromSeconds(60),
            () => vm.IndexStatusText);

        _ui.Invoke(() => vm.SearchText = query);
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача", diagnostics: () => vm.SearchStatus);

        SymbolViewModel method = _ui.Invoke(() => vm.Results.First(r => r.Symbol.Kind == SymbolKind.Method));
        _ui.Invoke(() => vm.SelectedResult = method);

        _ui.WaitUntil(() => vm.XrefHits.Count > 0, $"места вызова {method.Symbol.QualifiedName}",
            TimeSpan.FromSeconds(30),
            () => $"title={vm.XrefTitle} status={vm.XrefStatus}");

        _ui.Invoke(() => Assert.DoesNotContain("ссылок не найдено", vm.XrefStatus, StringComparison.Ordinal));
    }

    private sealed class Settings(string root) : ISettingsStore
    {
        public string SettingsPath => "<в тесте на диск ничего не пишется>";

        public CockpitSettings Load() => new() { WotSrcDir = root };

        public void Save(CockpitSettings settings) { }
    }

    private sealed class NoopEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenFolder(string path) { }

        public void OpenAt(string filePath, int line) { }
    }
}
