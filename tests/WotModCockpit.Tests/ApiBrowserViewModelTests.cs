using System.Diagnostics;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Поведенческие тесты экрана «Браузер API».
/// <para>
/// Проверяется ровно то, что отличает работающий экран от неработающего: он открывается
/// без индекса, переживает сервис-заглушку, ищет асинхронно и с дебаунсом, не теряет
/// контекст при переходе по ссылке и честно предупреждает про омонимию.
/// </para>
/// <para>
/// Всё крутится на настоящем UI-потоке (<see cref="UiThread"/>), но без окна: у модели
/// два <see cref="System.Windows.Threading.DispatcherTimer"/> (дебаунс поиска 150 мс и
/// задержка индикатора 300 мс), а таймер тикает только внутри живого цикла диспетчера.
/// </para>
/// </summary>
public class ApiBrowserViewModelTests : IDisposable
{
    private readonly UiThread _ui = new();
    private ApiBrowserViewModel? _vm;

    /// <summary>Гасит модель и UI-поток.</summary>
    public void Dispose()
    {
        if (_vm is not null)
        {
            try
            {
                _ui.Invoke(() => _vm!.Dispose());
            }
            catch (Exception)
            {
                // закрытие теста не должно маскировать причину падения самого теста
            }
        }

        _ui.Dispose();
        GC.SuppressFinalize(this);
    }

    // ================================================================= пустые состояния

    /// <summary>
    /// Дерева исходников нет — экран обязан открыться и ОБЪЯСНИТЬ, что делать.
    /// «Пусто» без объяснения в инструменте разработчика хуже, чем ошибка.
    /// </summary>
    [Fact]
    public void NoSourceTree_ShowsInstructionsNotEmptiness()
    {
        ApiBrowserViewModel vm = Create(new FakeBackend
        {
            Status = new ApiIndexStatus { State = ApiIndexState.NotConfigured }
        });

        _ui.Invoke(() =>
        {
            Assert.False(vm.HasIndex);
            Assert.Contains("uncompyle6", vm.EmptyStateText, StringComparison.Ordinal);
            Assert.Contains("выбрать папку", vm.EmptyStateText, StringComparison.Ordinal);
            Assert.Empty(vm.Results);
        });
    }

    /// <summary>
    /// Реализация Core может быть ещё заглушкой. Экран обязан это пережить: никаких
    /// исключений наружу, отказ превращается в строку состояния.
    /// </summary>
    [Fact]
    public void ThrowingBackend_DoesNotCrashScreen()
    {
        ApiBrowserViewModel vm = Create(new ThrowingBackend());

        _ui.Invoke(() => vm.SearchText = "pushMessage");
        _ui.Pump(rounds: 6);

        _ui.Invoke(() =>
        {
            Assert.Empty(vm.Results);
            Assert.Contains("поиск", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Заглушка из контейнера — рабочая: у неё все методы отвечают, а не бросают.
    /// Это нижняя граница, ниже которой экран уже не откроется.
    /// </summary>
    [Fact]
    public async Task UnavailableBackend_AnswersEveryCall()
    {
        var backend = new UnavailableApiBrowserBackend();

        Assert.NotNull(backend.Status);
        Assert.Empty(await backend.SearchAsync("x", ApiSearchFilter.Default, default));
        Assert.Empty(await backend.GetTreeChildrenAsync(null, ApiSearchFilter.Default, default));
        Assert.Empty(await backend.GetFileSymbolsAsync("client/x.py", default));
        Assert.Empty((await backend.FindReferencesAsync(new ApiXrefRequest { Name = "x" }, default)).Hits);
        Assert.Null(await backend.ResolveVirtualPathAsync("scripts/client/x.py", 1, default));
        Assert.Empty(await backend.GetCatalogAsync(ApiCatalogKind.Events, default));
        Assert.Null(backend.BuildSnippet(new ApiSnippetRequest { Kind = ApiSnippetKind.MonkeyPatch }));
    }

    // ================================================================= поиск

    /// <summary>
    /// Поиск идёт с задержкой и в фоне: набор «pushMessage» по буквам не должен порождать
    /// десять запросов, а результат обязан доехать без ручного нажатия «искать».
    /// </summary>
    [Fact]
    public void Search_IsDebouncedAndAsynchronous()
    {
        var backend = new FakeBackend();
        backend.Symbols.Add(Symbol("pushMessage", SymbolKind.Method, "gui.SystemMessages", owner: "ISystemMessages"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() =>
        {
            vm.SearchText = "p";
            vm.SearchText = "pu";
            vm.SearchText = "pus";
            vm.SearchText = "pushMessage";
        });

        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача поиска доехала",
            diagnostics: () => $"status={vm.SearchStatus} запросов={backend.SearchCalls}");

        _ui.Invoke(() =>
        {
            Assert.Equal("pushMessage", vm.Results[0].Name);

            // Четыре присваивания подряд — один запрос: дебаунс перезапускает таймер.
            Assert.Equal(1, backend.SearchCalls);
            Assert.Equal("pushMessage", backend.LastQuery);
        });
    }

    /// <summary>Пустой запрос не идёт в индекс и очищает выдачу.</summary>
    [Fact]
    public void Search_EmptyQuery_ClearsWithoutHittingIndex()
    {
        var backend = new FakeBackend();
        backend.Symbols.Add(Symbol("update", SymbolKind.Method, "gui.settings"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "update");
        _ui.WaitUntil(() => vm.Results.Count > 0, "первая выдача");

        _ui.Invoke(() => vm.SearchText = "   ");
        _ui.Pump(rounds: 6);

        _ui.Invoke(() =>
        {
            Assert.Empty(vm.Results);
            Assert.Equal(1, backend.SearchCalls);
        });
    }

    /// <summary>Слои корпуса по умолчанию отсечены, а галочки доезжают до запроса.</summary>
    [Fact]
    public void Search_LayerFilters_ReachTheQuery()
    {
        var backend = new FakeBackend();

        // Непустая выдача здесь обязательна: на ПУСТОЙ модель делает второй запрос со всеми
        // слоями, чтобы честно сказать «в игровом коде ничего, N в сгенерированных»
        // (см. Search_NothingInGameCode_SaysWhereItActuallyIs), и счётчик запросов перестал бы
        // отвечать на вопрос этого теста — доезжают ли галочки до фильтра.
        backend.Symbols.Add(Symbol("strings", SymbolKind.Constant, "gui.Scaleform.locale.strings"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "strings");
        _ui.WaitUntil(() => backend.SearchCalls == 1, "первый запрос");

        _ui.Invoke(() =>
        {
            Assert.False(backend.LastFilter!.IncludeGenerated);
            Assert.False(backend.LastFilter!.IncludeVendor);
            vm.IncludeGenerated = true;
        });

        _ui.WaitUntil(() => backend.SearchCalls == 2, "перезапрос после галочки");
        _ui.Invoke(() => Assert.True(backend.LastFilter!.IncludeGenerated));
    }

    // ================================================================= выбор символа

    /// <summary>
    /// Выбор символа открывает исходник на строке объявления и сразу запускает поиск ссылок:
    /// полный проход по корпусу стоит единицы миллисекунд, ждать нажатия отдельной кнопки незачем.
    /// </summary>
    [Fact]
    public void SelectingSymbol_OpensSourceAndRunsXref()
    {
        var backend = new FakeBackend();
        backend.Symbols.Add(Symbol("pushMessage", SymbolKind.Method, "gui.SystemMessages", owner: "SystemMessages", line: 127));
        backend.Hits.Add(new ApiXrefHit("client/gui/x.py", "scripts/client/gui/x.py", @"C:\src\client\gui\x.py", 42,
            "SystemMessages.pushMessage('hi')", ApiXrefKind.Call));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "pushMessage");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача");

        _ui.Invoke(() => vm.SelectedResult = vm.Results[0]);
        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "ссылки доехали",
            diagnostics: () => $"xref={vm.XrefStatus}");

        _ui.Invoke(() =>
        {
            Assert.Equal(127, vm.SourceLine);
            Assert.EndsWith("SystemMessages.py", vm.SourcePath!, StringComparison.Ordinal);
            Assert.Equal("scripts/client/gui/SystemMessages.py", vm.SourceVirtualPath);

            // Имя класса-владельца приёмником НЕ становится, хотя галочка «приёмник» включена:
            // в исходниках клиента вызывают self.pushMessage / systemMessages.pushMessage,
            // а «SystemMessages.pushMessage» не встречается нигде — замерено, 0 попаданий
            // против 189 у голого имени.
            Assert.Equal("pushMessage", vm.XrefTitle);
            Assert.Null(backend.LastXref!.Receiver);
        });
    }

    /// <summary>
    /// Переход по ссылке меняет то, что показано в центре, но НЕ сбрасывает выбранный символ:
    /// иначе один щелчок по месту вызова уносил бы вместе с собой и панель сниппетов.
    /// </summary>
    [Fact]
    public void ClickingXrefHit_KeepsSelectedSymbol()
    {
        var backend = new FakeBackend();
        backend.Symbols.Add(Symbol("onAvatarReady", SymbolKind.Event, "PlayerEvents", owner: "_PlayerEvents", line: 57));
        backend.Hits.Add(new ApiXrefHit("client/AutoShootGunController.py", "scripts/client/AutoShootGunController.py",
            @"C:\src\client\AutoShootGunController.py", 352,
            "g_playerEvents.onAvatarReady += self.__onAvatarReady", ApiXrefKind.EventSubscription));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "onAvatarReady");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача");
        _ui.Invoke(() => vm.SelectedResult = vm.Results[0]);
        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "ссылки");

        _ui.Invoke(() =>
        {
            SymbolViewModel selected = vm.CurrentSymbol!;
            vm.SelectedXrefHit = vm.XrefHits[0];

            Assert.Same(selected, vm.CurrentSymbol);
            Assert.Equal(352, vm.SourceLine);
            Assert.EndsWith("AutoShootGunController.py", vm.SourcePath!, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Омонимия — главный источник бесполезных ответов xref. Про неё надо СКАЗАТЬ,
    /// а не выдать две тысячи строк как ответ на вопрос.
    /// </summary>
    [Fact]
    public void Xref_HomonymousName_WarnsInsteadOfDumping()
    {
        var backend = new FakeBackend
        {
            XrefTotal = 2182,
            XrefFiles = 611,
            XrefDeclarations = 279
        };

        backend.Symbols.Add(Symbol("update", SymbolKind.Method, "gui.settings", owner: null, declarations: 279));
        backend.Hits.Add(new ApiXrefHit("client/a.py", "scripts/client/a.py", @"C:\src\client\a.py", 1, "x.update()", ApiXrefKind.Call));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "update");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача");
        _ui.Invoke(() => vm.SelectedResult = vm.Results[0]);
        _ui.WaitUntil(() => vm.XrefStatus.Contains("279", StringComparison.Ordinal), "сводка про омонимию",
            diagnostics: () => vm.XrefStatus);

        _ui.Invoke(() =>
        {
            Assert.Contains("бесполезен", vm.XrefStatus, StringComparison.Ordinal);
            Assert.Contains("settings", vm.XrefReceiverHint, StringComparison.Ordinal);
            Assert.True(vm.Results[0].IsAmbiguous);
        });
    }

    /// <summary>
    /// Сниппет подписки строится ПО КАТАЛОГУ, а каталоги ленивые. Пользователь, нашедший
    /// событие поиском и не заходивший на вкладку «события», получал «для этой цели сниппета
    /// нет» — при том что сниппет строится прекрасно, надо лишь сперва построить каталог.
    /// </summary>
    [Fact]
    public void Snippet_ForSymbol_BuildsCatalogsOnDemand()
    {
        var backend = new FakeBackend
        {
            SnippetNeedsCatalog = true,
            Snippet = new ApiSnippet("подписка на onAvatarReady", "def init():\n    subscribe()\n", string.Empty)
        };

        backend.Symbols.Add(Symbol("onAvatarReady", SymbolKind.Event, "PlayerEvents", owner: "_PlayerEvents"));
        backend.Catalog.Add(CatalogEntry("onAvatarReady"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "onAvatarReady");
        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача");
        _ui.Invoke(() => vm.SelectedResult = vm.Results[0]);

        // Панель каталогов не открывалась ни разу.
        Assert.False(_ui.Invoke(() => backend.CatalogRequested));

        _ui.Invoke(() => vm.CopySnippetCommand.Execute(ApiSnippetKind.EventSubscription));
        _ui.WaitUntil(() => vm.HasSnippet, "сниппет собран после прогрева каталога",
            diagnostics: () => vm.StatusMessage);

        _ui.Invoke(() => Assert.Contains("def init()", vm.SnippetPreview, StringComparison.Ordinal));
    }

    /// <summary>
    /// Уточнение приёмником дало ноль — панель обязана показать поиск ГОЛЫМ именем и честно
    /// сказать, что произошло. Пустой список здесь особенно вреден тем, что вместе с ним пуста
    /// и подсказка «частые приёмники» (она строится из попаданий), то есть человеку не дают
    /// ни ответа, ни способа его получить.
    /// </summary>
    [Fact]
    public void Xref_ReceiverWithNoHits_FallsBackToBareName()
    {
        var backend = new FakeBackend { NoHitsWithReceiver = true };
        backend.Catalog.Add(CatalogEntry("onAvatarReady"));
        backend.Hits.Add(new ApiXrefHit("client/AutoShootGunController.py", "scripts/client/AutoShootGunController.py",
            @"C:\src\client\AutoShootGunController.py", 352,
            "self.arena.onAvatarReady += self.__onAvatarReady", ApiXrefKind.EventSubscription));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог");
        _ui.Invoke(() => vm.SelectedCatalogItem = vm.CatalogItems[0]);
        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "откат на голое имя",
            diagnostics: () => $"title={vm.XrefTitle} status={vm.XrefStatus}");

        _ui.Invoke(() =>
        {
            Assert.Null(backend.LastXref!.Receiver);
            Assert.Equal("onAvatarReady", vm.XrefTitle);
            Assert.Contains("g_playerEvents", vm.XrefStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("ссылок не найдено", vm.XrefStatus, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// У смены выделения такой же дебаунс, как у набора текста, и по той же причине:
    /// каждый xref — это полное перечитывание 8052 файлов, а при пролистывании списка
    /// стрелками результат всех запросов, кроме последнего, всё равно выбрасывается.
    /// </summary>
    [Fact]
    public void Xref_RapidSelectionChanges_AreDebounced()
    {
        var backend = new FakeBackend();
        backend.Symbols.Add(Symbol("onAvatarReady", SymbolKind.Event, "PlayerEvents", owner: "_PlayerEvents"));
        backend.Symbols.Add(Symbol("onAvatarBecomePlayer", SymbolKind.Event, "PlayerEvents", owner: "_PlayerEvents"));
        backend.Symbols.Add(Symbol("onArenaPeriodChange", SymbolKind.Event, "PlayerEvents", owner: "_PlayerEvents"));
        backend.Hits.Add(new ApiXrefHit("client/a.py", "scripts/client/a.py", @"C:\src\client\a.py", 1,
            "g_playerEvents.onAvatarReady += x", ApiXrefKind.EventSubscription));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "on");
        _ui.WaitUntil(() => vm.Results.Count >= 3, "выдача");

        // Три «нажатия стрелки вниз» в одном такте UI — как при пролистывании списка.
        _ui.Invoke(() =>
        {
            vm.SelectedResult = vm.Results[0];
            vm.SelectedResult = vm.Results[1];
            vm.SelectedResult = vm.Results[2];
        });

        _ui.WaitUntil(() => vm.XrefHits.Count > 0, "ссылки по последнему выбору",
            diagnostics: () => vm.XrefStatus);
        _ui.Pump(rounds: 4);

        _ui.Invoke(() =>
        {
            Assert.Equal(1, backend.XrefCalls);
            Assert.Equal("onArenaPeriodChange", backend.LastXref!.Name);
        });
    }

    /// <summary>
    /// Имя может жить ТОЛЬКО в скрытом слое (18 738 имён — в сгенерированных ресурсах,
    /// 12 301 — в вендорных). «Ничего не найдено» в этом случае — ложь: надо сказать, где
    /// оно есть и какой галочкой его показать.
    /// </summary>
    [Fact]
    public void Search_NothingInGameCode_SaysWhereItActuallyIs()
    {
        var backend = new FakeBackend { OnlyWithAllLayers = true };
        backend.Symbols.Add(Symbol("AccountDashboardModel", SymbolKind.Class,
            "gui.impl.gen.view_models.views.lobby.account_dashboard.account_dashboard_model",
            tier: SourceTier.Generated));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.SearchText = "AccountDashboardModel");
        _ui.WaitUntil(() => vm.SearchStatus.Contains("сгенерированных", StringComparison.Ordinal),
            "приписка про скрытый слой", diagnostics: () => vm.SearchStatus);

        _ui.Invoke(() =>
        {
            Assert.Empty(vm.Results);
            Assert.DoesNotContain("ничего не найдено", vm.SearchStatus, StringComparison.Ordinal);
            Assert.Contains("включите фильтр", vm.SearchStatus, StringComparison.Ordinal);
        });
    }

    // ================================================================= каталоги

    /// <summary>Каталог грузится при переключении панели и фильтруется на месте.</summary>
    [Fact]
    public void Catalog_LoadsOnPaneSwitch_AndFilters()
    {
        var backend = new FakeBackend();
        backend.Catalog.Add(CatalogEntry("onArenaPeriodChange", battleOnly: true, throws: true));
        backend.Catalog.Add(CatalogEntry("onAccountShowGUI"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count == 2, "каталог загрузился",
            diagnostics: () => vm.CatalogStatus);

        _ui.Invoke(() => vm.CatalogFilterText = "arena");
        _ui.Invoke(() =>
        {
            Assert.Single(vm.CatalogItems);
            Assert.Equal("onArenaPeriodChange", vm.CatalogItems[0].Entry.Name);
            Assert.Contains("из 2", vm.CatalogStatus, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Предупреждения записи каталога — это не украшение: <c>Event</c> перебрасывает исключение
    /// обработчика в клиент, а боевой владелец вне боя равен <c>None</c>.
    /// <para>
    /// При этом в СТРОКЕ списка остаётся только то, что запись различает: «Event, а не SafeEvent»
    /// верно у 1023 записей из 1030 (99.3 %), то есть на строке это чистый шум, съедавший
    /// половину её высоты.
    /// </para>
    /// </summary>
    [Fact]
    public void CatalogItem_SurfacesBehaviourWarnings()
    {
        var item = new CatalogItemViewModel(CatalogEntry("onVehicleKilled", battleOnly: true, throws: true));

        Assert.True(item.HasWarning);
        Assert.Contains("SafeEvent", item.Warning, StringComparison.Ordinal);
        Assert.Contains("только в бою", item.Warning, StringComparison.Ordinal);
        Assert.Equal(ApiSnippetKind.LateEventSubscription, item.SnippetKind);

        Assert.DoesNotContain("SafeEvent", item.RowWarning, StringComparison.Ordinal);
        Assert.Contains("только в бою", item.RowWarning, StringComparison.Ordinal);
    }

    /// <summary>
    /// Порядок выдачи каталога — это и есть ответ. Совпадение по ИМЕНИ обязано идти выше
    /// совпадения по пути доступа: на настоящем каталоге запрос «battle» давал 231 запись,
    /// в первой тридцатке которой 28 совпали по строке «# BATTLE_CTRL_ID…», а ни одного
    /// события про вход в бой видно не было.
    /// </summary>
    [Fact]
    public void Catalog_NameMatches_RankAboveAccessPathMatches()
    {
        var backend = new FakeBackend();
        backend.Catalog.Add(PathOnlyEntry("onUpdated", "sessionProvider.shared.battleField"));
        backend.Catalog.Add(PathOnlyEntry("onSyncCompleted", "IEpicBattleMetaGameController"));
        backend.Catalog.Add(PathOnlyEntry("onBattleStarted", "arena.onPeriodChange"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count == 3, "каталог", diagnostics: () => vm.CatalogStatus);

        _ui.Invoke(() => vm.CatalogFilterText = "battle");

        _ui.Invoke(() =>
        {
            Assert.Equal(3, vm.CatalogItems.Count);
            Assert.Equal("onBattleStarted", vm.CatalogItems[0].Entry.Name);
        });
    }

    /// <summary>
    /// Слово человека → слово клиента. «Бой» в клиенте называется Arena, и без этой
    /// маршрутизации самый естественный запрос давал ноль: каталог отвечает только тому,
    /// кто уже знает ответ.
    /// </summary>
    [Fact]
    public void Catalog_RussianQuery_IsRoutedToClientVocabulary()
    {
        var backend = new FakeBackend();
        backend.Catalog.Add(PathOnlyEntry("onArenaPeriodChange", "arena.onPeriodChange"));
        backend.Catalog.Add(PathOnlyEntry("onSettingsChanged", "settingsCore.onSettingsChanged"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count == 2, "каталог", diagnostics: () => vm.CatalogStatus);

        _ui.Invoke(() => vm.CatalogFilterText = "бой");

        _ui.Invoke(() =>
        {
            Assert.Single(vm.CatalogItems);
            Assert.Equal("onArenaPeriodChange", vm.CatalogItems[0].Entry.Name);

            // Подмена не молчаливая: в строке состояния сказано, чем именно искали.
            Assert.Contains("arena", vm.CatalogStatus, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>
    /// Шесть колбэков загрузчика закреплены сверху. В общем порядке (по числу подписок) они
    /// лежат на позициях 37, 49, 74, 91, 92 и 94 из 1030 — то есть не видны вовсе, при том что
    /// подсказка над списком прямо про них и написана.
    /// </summary>
    [Fact]
    public void Catalog_LoaderCallbacks_ArePinnedOnTop()
    {
        var backend = new FakeBackend();
        backend.Catalog.Add(PathOnlyEntry("onUpdated", "x.onUpdated"));
        backend.Catalog.Add(PathOnlyEntry("onSyncCompleted", "y.onSyncCompleted"));
        backend.Catalog.Add(CatalogEntry("onAvatarBecomePlayer"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count == 3, "каталог", diagnostics: () => vm.CatalogStatus);

        _ui.Invoke(() =>
        {
            Assert.Equal("onAvatarBecomePlayer", vm.CatalogItems[0].Entry.Name);
            Assert.True(vm.CatalogItems[0].IsLoaderCallback);
            Assert.False(vm.CatalogItems[1].IsLoaderCallback);
        });
    }

    /// <summary>
    /// Каталог патчей без индекса отвечает лишь на половину вопроса: «что патчат моды» — да,
    /// «есть ли это в вашем клиенте» — нет. Молчать об этом нельзя: тогда все 252 записи
    /// читаются как «есть в вашем клиенте», а подсказка обещает красные пометки, которых
    /// не будет никогда.
    /// </summary>
    [Fact]
    public void Hotspots_WithoutIndex_SayTheyAreNotVerified()
    {
        var backend = new FakeBackend
        {
            Status = new ApiIndexStatus { State = ApiIndexState.NotConfigured }
        };

        backend.Catalog.Add(new ApiCatalogEntry
        {
            Kind = ApiCatalogKind.Hotspots,
            Name = "LootBoxesEntryPointWidget.getIsActive",
            XrefName = "getIsActive"
        });

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Hotspots);
        _ui.WaitUntil(() => vm.CatalogItems.Count == 1, "каталог патчей", diagnostics: () => vm.CatalogStatus);

        _ui.Invoke(() =>
        {
            Assert.Contains("НЕ проверено", vm.CatalogStatus, StringComparison.Ordinal);
            Assert.Contains("НИЧЕГО не проверено", vm.CatalogHint, StringComparison.Ordinal);
            Assert.DoesNotContain("Красным помечено", vm.CatalogHint, StringComparison.Ordinal);
        });
    }

    /// <summary>Устаревшая запись каталога помечается, а не выбрасывается: это тоже ответ.</summary>
    [Fact]
    public void CatalogItem_StaleEntry_IsMarkedNotHidden()
    {
        var entry = new ApiCatalogEntry
        {
            Kind = ApiCatalogKind.Hotspots,
            Name = "ServerInfoPresenter.__updateModel",
            IsStale = true
        };

        var item = new CatalogItemViewModel(entry);

        Assert.True(item.IsStale);
        Assert.Contains("нет", item.Warning, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Галочку «приёмник» можно выключить и включить обратно, не потеряв сам приёмник.
    /// <para>
    /// Уточнение приёмником — главный инструмент против омонимии (<c>g_playerEvents.onAvatarReady</c>
    /// даёт 33 попадания против 115 у голого имени), и он приходит из записи каталога.
    /// Если выключенная галочка стирает приёмник насовсем, то включить её обратно нечем:
    /// панель молча остаётся бесполезной, пока пользователь не выберет запись заново.
    /// </para>
    /// </summary>
    [Fact]
    public void Xref_ReceiverSurvivesTogglingItOff()
    {
        var backend = new FakeBackend();
        backend.Catalog.Add(CatalogEntry("onAvatarReady"));

        // Попадание обязательно: если уточнение приёмником даёт НОЛЬ, модель специально
        // повторяет запрос голым именем (см. Xref_ReceiverWithNoHits_FallsBackToBareName),
        // и приёмник в последнем запросе оказался бы стёрт не галочкой, а откатом.
        backend.Hits.Add(new ApiXrefHit("client/AutoShootGunController.py", "scripts/client/AutoShootGunController.py",
            @"C:\src\client\AutoShootGunController.py", 352,
            "g_playerEvents.onAvatarReady += self.__onAvatarReady", ApiXrefKind.EventSubscription));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог");
        _ui.Invoke(() => vm.SelectedCatalogItem = vm.CatalogItems[0]);
        _ui.WaitUntil(() => backend.LastXref is not null, "первый поиск ссылок");

        // Приёмник берётся из поля записи (XrefReceiver), а не разбирается из строки
        // пути доступа, показываемой человеку.
        Assert.Equal("g_playerEvents", _ui.Invoke(() => backend.LastXref!.Receiver));

        _ui.Invoke(() => vm.XrefUseReceiver = false);
        _ui.WaitUntil(() => backend.LastXref!.Receiver is null, "поиск без уточнения");

        _ui.Invoke(() => vm.XrefUseReceiver = true);
        _ui.WaitUntil(() => backend.LastXref!.Receiver is not null, "приёмник вернулся");

        Assert.Equal("g_playerEvents", _ui.Invoke(() => backend.LastXref!.Receiver));
    }

    // ================================================================= сниппеты

    /// <summary>Сниппет показывается целиком, а не только уезжает в буфер обмена.</summary>
    [Fact]
    public void Snippet_IsShownNotOnlyCopied()
    {
        var backend = new FakeBackend
        {
            Snippet = new ApiSnippet("подписка на onAvatarReady", "def init():\n    subscribe()\n", "отписка в fini() обязательна")
        };

        backend.Catalog.Add(CatalogEntry("onAvatarReady"));

        ApiBrowserViewModel vm = Create(backend);

        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог");

        _ui.Invoke(() =>
        {
            vm.SelectedCatalogItem = vm.CatalogItems[0];
            vm.CopyCatalogSnippetCommand.Execute(null);

            Assert.True(vm.HasSnippet);
            Assert.Contains("def init()", vm.SnippetPreview, StringComparison.Ordinal);
            Assert.Contains("fini()", vm.SnippetNote, StringComparison.Ordinal);
        });
    }

    /// <summary>
    /// Сниппет строится для ПОСЛЕДНЕГО выбора, а не для того, что случайно осталось выбранным
    /// в другой панели.
    /// <para>
    /// Сценарий, который это ловит: мододел открыл каталог событий, ткнул в
    /// <c>onArenaPeriodChange</c>, ушёл на поиск, нашёл <c>pushMessage</c> и нажал «монкипатч».
    /// Выбор в списке каталога никуда не делся, а генератор отдаёт приоритет записи каталога
    /// (в ней есть резолвер владельца и сигнатура) — и в буфер уезжает подписка на чужое
    /// событие вместо патча выбранного метода. Молча и с правдоподобным видом.
    /// </para>
    /// </summary>
    [Fact]
    public void Snippet_FollowsLastSelection_NotStaleCatalogPick()
    {
        var backend = new FakeBackend
        {
            Snippet = new ApiSnippet("что-нибудь", "code", string.Empty)
        };

        backend.Catalog.Add(CatalogEntry("onArenaPeriodChange"));
        backend.Symbols.Add(Symbol("pushMessage", SymbolKind.Method, "gui.SystemMessages", "SystemMessagesInterface"));

        ApiBrowserViewModel vm = Create(backend);

        // 1. выбрали запись каталога
        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог");
        _ui.Invoke(() => vm.SelectedCatalogItem = vm.CatalogItems[0]);

        // 2. ушли на поиск и выбрали символ — выбор в каталоге при этом остаётся
        _ui.Invoke(() =>
        {
            vm.Pane = ApiBrowserPane.Search;
            vm.SearchText = "pushMessage";
        });

        _ui.WaitUntil(() => vm.Results.Count > 0, "выдача поиска");
        _ui.Invoke(() => vm.SelectedResult = vm.Results[0]);

        // 3. просим монкипатч — он обязан относиться к символу, а не к записи каталога
        _ui.Invoke(() => vm.CopySnippetCommand.Execute(ApiSnippetKind.MonkeyPatch));

        ApiSnippetRequest? request = backend.LastSnippetRequest;

        Assert.NotNull(request);
        Assert.Equal(ApiSnippetKind.MonkeyPatch, request!.Kind);
        Assert.NotNull(request.Symbol);
        Assert.Equal("pushMessage", request.Symbol!.Name);
        Assert.True(
            request.Entry is null,
            "в запрос уехала запись каталога (" + request.Entry?.Name + "), хотя последним выбирали символ");

        // И обратно: вернулись в каталог — снова строится сниппет записи каталога.
        _ui.Invoke(() => vm.Pane = ApiBrowserPane.Events);
        _ui.WaitUntil(() => vm.CatalogItems.Count > 0, "каталог");
        _ui.Invoke(() =>
        {
            vm.SelectedCatalogItem = vm.CatalogItems[0];
            vm.CopyCatalogSnippetCommand.Execute(null);
        });

        Assert.NotNull(backend.LastSnippetRequest!.Entry);
        Assert.Equal("onArenaPeriodChange", backend.LastSnippetRequest.Entry!.Name);
    }

    // ================================================================= мост к этапу 1

    /// <summary>
    /// Кадр traceback открывается в браузере по ВИРТУАЛЬНОМУ пути: это то самое поле,
    /// которым оперирует python.log, и оно же хранится в индексе.
    /// </summary>
    [Fact]
    public async Task OpenVirtualPath_ResolvesFrameToSymbol()
    {
        var backend = new FakeBackend();
        backend.Resolved = Symbol("__destroyGUI", SymbolKind.Method, "Avatar", owner: "PlayerAvatar", line: 880);

        ApiBrowserViewModel vm = Create(backend);

        bool found = await _ui.Dispatcher.InvokeAsync(
            () => vm.OpenVirtualPathAsync("scripts/client/Avatar.py", 1684)).Task.Unwrap();

        Assert.True(found);

        _ui.Invoke(() =>
        {
            Assert.Equal("__destroyGUI", vm.CurrentSymbol!.Name);

            // Строка берётся из traceback, а не из объявления символа: пользователь пришёл
            // смотреть КАДР, карточка символа тут только контекст.
            Assert.Equal(1684, vm.SourceLine);
        });
    }

    /// <summary>Файла нет в индексе — команда честно возвращает false и не бросает.</summary>
    [Fact]
    public async Task OpenVirtualPath_UnknownFile_ReturnsFalse()
    {
        ApiBrowserViewModel vm = Create(new FakeBackend());

        bool found = await _ui.Dispatcher.InvokeAsync(
            () => vm.OpenVirtualPathAsync("scripts/client/NoSuch.py", 10)).Task.Unwrap();

        Assert.False(found);
        _ui.Invoke(() => Assert.Contains("нет файла", vm.StatusMessage, StringComparison.Ordinal));
    }

    // ================================================================= проекция символа

    /// <summary>
    /// Строка импорта. Метод импортировать нельзя — импортируется его класс, и это ровно та
    /// ошибка, которую мододел делает первой.
    /// </summary>
    [Theory]
    [InlineData(SymbolKind.Class, "IItemsCache", "IItemsCache", "from skeletons.gui.shared import IItemsCache")]
    [InlineData(SymbolKind.Function, "pushMessage", "pushMessage", "from skeletons.gui.shared import pushMessage")]
    [InlineData(SymbolKind.Method, "pushMessage", "ISystemMessages.pushMessage", "from skeletons.gui.shared import ISystemMessages  # .pushMessage")]
    public void SymbolViewModel_ImportLine_MatchesKind(SymbolKind kind, string name, string qualified, string expected)
    {
        var symbol = new ApiSymbol(
            name,
            qualified,
            kind,
            "skeletons.gui.shared",
            "scripts/client/skeletons/gui/shared/__init__.py",
            "client/skeletons/gui/shared/__init__.py",
            @"C:\src\client\skeletons\gui\shared\__init__.py",
            8,
            null);

        Assert.Equal(expected, new SymbolViewModel(symbol).ImportLine);
    }

    /// <summary>Декораторы видны прямо в выдаче: @property меняет способ монкипатча.</summary>
    [Fact]
    public void SymbolViewModel_ShowsDecorators()
    {
        var symbol = Symbol("arenaPeriod", SymbolKind.Method, "skeletons.gui.battle_session", owner: "ISharedControllersLocator") with
        {
            Decorators = DecoratorFlags.Property | DecoratorFlags.Abstract
        };

        var vm = new SymbolViewModel(symbol);

        Assert.Contains("@property", vm.Decorators, StringComparison.Ordinal);
        Assert.Contains("@abstractmethod", vm.Decorators, StringComparison.Ordinal);
    }

    // ================================================================= подсветка

    /// <summary>
    /// Определение подсветки должно лежать во встроенных ресурсах и разбираться AvalonEdit.
    /// Тест дешёвый, а ловит он два самых обидных отказа: забытый EmbeddedResource в csproj
    /// и опечатку в XSHD, которые иначе видно только глазами на запущенном приложении.
    /// </summary>
    [Fact]
    public void PythonHighlighting_IsEmbeddedAndParses()
    {
        _ui.Invoke(() =>
        {
            using Stream? stream = typeof(WotModCockpit.App.Controls.PythonSourceView).Assembly
                .GetManifestResourceStream("WotModCockpit.App.Resources.Python-Mode.xshd");

            Assert.NotNull(stream);

            using XmlReader reader = XmlReader.Create(stream!);
            IHighlightingDefinition definition = HighlightingLoader.Load(reader, HighlightingManager.Instance);

            Assert.Equal("Python27", definition.Name);
            Assert.NotNull(definition.MainRuleSet);
        });
    }

    // ================================================================= контейнер

    /// <summary>
    /// Экран обязан резолвиться из настоящего контейнера приложения вместе со своим швом.
    /// Это защита от самой частой поломки интеграции — забытой регистрации.
    /// </summary>
    [Fact]
    public void Container_ResolvesBrowserAndBackend()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        var backend = provider.GetRequiredService<IApiBrowserBackend>();
        Assert.NotNull(backend);
        Assert.NotNull(backend.Status);

        // ViewModel строится на UI-потоке: в конструкторе живут DispatcherTimer'ы.
        _ui.Invoke(() =>
        {
            var vm = provider.GetRequiredService<ApiBrowserViewModel>();
            Assert.NotNull(vm);
            vm.Dispose();
        });
    }

    // ================================================================= реальное дерево

    /// <summary>
    /// Сквозная проверка на настоящем дереве клиента: индекс строится, поиск отвечает,
    /// а первым результатом по «pushMessage» идёт интерфейс из skeletons/ — та самая
    /// выдача, которую разведка признала правильной.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_SearchReturnsExpectedTopHit()
    {
        TestEnvironment.RequireClientSource();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var backend = provider.GetRequiredService<IApiBrowserBackend>();

        var sw = Stopwatch.StartNew();
        ApiIndexStatus status = backend.ReindexAsync(RealWotSource.Dir, CancellationToken.None)
            .GetAwaiter().GetResult();
        sw.Stop();

        Assert.Equal(ApiIndexState.Ready, status.State);
        Assert.True(status.SymbolCount > 100_000, $"символов всего {status.SymbolCount}");

        IReadOnlyList<ApiSymbol> found = backend
            .SearchAsync("pushMessage", ApiSearchFilter.Default, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotEmpty(found);
        Assert.Equal("pushMessage", found[0].Name);
        Assert.Contains("skeletons", found[0].RelativePath, StringComparison.Ordinal);

        ApiXrefResult xref = backend
            .FindReferencesAsync(new ApiXrefRequest { Name = "pushMessage" }, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(xref.TotalHits > 0, "ссылок на pushMessage не нашлось");
    }

    /// <summary>
    /// Дерево модулей на настоящем корпусе: три подкорня, ленивая подгрузка уровня,
    /// файл раскрывается в свои символы.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_ModuleTreeExpandsToSymbols()
    {
        TestEnvironment.RequireClientSource();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var backend = provider.GetRequiredService<IApiBrowserBackend>();
        backend.ReindexAsync(RealWotSource.Dir, CancellationToken.None).GetAwaiter().GetResult();

        IReadOnlyList<ApiTreeNode> roots = backend
            .GetTreeChildrenAsync(null, ApiSearchFilter.Default, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.Contains(roots, n => n is { IsDirectory: true, Name: "client" });
        Assert.Contains(roots, n => n is { IsDirectory: true, Name: "common" });

        IReadOnlyList<ApiSymbol> symbols = backend
            .GetFileSymbolsAsync("client/PlayerEvents.py", CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.NotEmpty(symbols);
        Assert.Contains(symbols, s => s.Name == "onAvatarBecomePlayer");
    }

    /// <summary>
    /// Каталог событий на настоящем дереве: он и есть ответ на вопрос «что мне перехватить».
    /// Загрузчик отдаёт моду 6 колбэков — каталог обязан показать на порядок больше.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_EventCatalogIsRicherThanLoaderCallbacks()
    {
        TestEnvironment.RequireClientSource();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var backend = provider.GetRequiredService<IApiBrowserBackend>();
        backend.ReindexAsync(RealWotSource.Dir, CancellationToken.None).GetAwaiter().GetResult();

        IReadOnlyList<ApiCatalogEntry> events = backend
            .GetCatalogAsync(ApiCatalogKind.Events, CancellationToken.None)
            .GetAwaiter().GetResult();

        Assert.True(events.Count > 500, $"событий в каталоге всего {events.Count}");
        Assert.Contains(events, e => e.Name == "onArenaPeriodChange");

        // Сниппет строится по записи каталога и обязан быть непустым кодом.
        ApiCatalogEntry sample = events.First(e => e.Name == "onArenaPeriodChange");
        ApiSnippet? snippet = backend.BuildSnippet(new ApiSnippetRequest
        {
            Kind = ApiSnippetKind.EventSubscription,
            Entry = sample,
            ModId = "ru.local.hello_tanker"
        });

        Assert.NotNull(snippet);
        Assert.Contains("def init()", snippet!.Code, StringComparison.Ordinal);
        Assert.Contains("onArenaPeriodChange", snippet.Code, StringComparison.Ordinal);
    }

    /// <summary>
    /// Панель ссылок обязана НАХОДИТЬ ссылки по записи каталога.
    /// <para>
    /// Это не абстрактная проверка контракта: приёмник для уточнения запроса раньше
    /// выводился разбором строки пути доступа, написанной для человека
    /// (<c>from PlayerEvents import g_playerEvents  -&gt;  g_playerEvents.onAvatarReady</c>,
    /// <c>dependency.instance(IItemsCache)</c>, <c>BigWorld.player().arena</c>,
    /// <c>владелец достижим только изнутри клиента</c>). Поиск ссылок ищет подстроку
    /// «приёмник.имя», поэтому на настоящем дереве панель отвечала «ссылок не найдено»
    /// ВСЕГДА — при 17 подписках на onAvatarReady и 674 обращениях к IItemsCache.
    /// Проверяется именно результат поиска, а не значение поля: поле легко подогнать.
    /// </para>
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    [Trait("Needs", "ClientSource")]
    public void RealTree_CatalogXrefFindsRealReferences()
    {
        TestEnvironment.RequireClientSource();

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var backend = provider.GetRequiredService<IApiBrowserBackend>();
        backend.ReindexAsync(RealWotSource.Dir, CancellationToken.None).GetAwaiter().GetResult();

        IReadOnlyList<ApiCatalogEntry> events = backend
            .GetCatalogAsync(ApiCatalogKind.Events, CancellationToken.None)
            .GetAwaiter().GetResult();

        // 1. Событие синглтона: подписка идёт через g_playerEvents.
        ApiCatalogEntry singleton = events.First(e =>
            e.Name == "onAvatarReady" && e.XrefReceiver == "g_playerEvents");

        Assert.True(Xref(backend, singleton).TotalHits > 0,
            "ссылок на g_playerEvents.onAvatarReady не нашлось");

        // 2. Событие сущности: владелец живёт только в бою, приёмник — arena,
        //    а НЕ выражение BigWorld.player().arena целиком.
        ApiCatalogEntry entity = events.First(e =>
            e.Name == "onAvatarReady" && e.XrefReceiver == "arena");

        Assert.True(Xref(backend, entity).TotalHits > 0,
            "ссылок на arena.onAvatarReady не нашлось");

        // 3. Событие, у которого владелец не выведен: уточнять нечем, но голое имя
        //    обязано искаться, а не превращаться в русскую фразу из карточки.
        ApiCatalogEntry unresolved = events.First(e =>
            e.AccessPath.StartsWith("владелец", StringComparison.Ordinal) && e.Weight > 0);

        Assert.Null(unresolved.XrefReceiver);
        Assert.True(Xref(backend, unresolved).TotalHits > 0,
            $"ссылок на {unresolved.Name} не нашлось");

        // 4. Сервис: приёмника нет, ищется само имя интерфейса.
        ApiCatalogEntry service = backend
            .GetCatalogAsync(ApiCatalogKind.Services, CancellationToken.None)
            .GetAwaiter().GetResult()
            .First(e => e.Name == "IItemsCache");

        Assert.Null(service.XrefReceiver);
        Assert.True(Xref(backend, service).TotalHits > 100,
            "обращений к IItemsCache должно быть много");

        // 5. Горячая точка: в карточке «Класс.член», а искать надо голый член —
        //    «Класс.член» пишут чужие моды, в дереве клиента этого нет.
        IReadOnlyList<ApiCatalogEntry> hooks = backend
            .GetCatalogAsync(ApiCatalogKind.Hotspots, CancellationToken.None)
            .GetAwaiter().GetResult();

        // Проверяются ВСЕ подтверждённые точки, а не первая попавшаяся: каждая из них
        // объявлена в клиенте, значит хотя бы одно попадание (само объявление) обязано быть.
        List<ApiCatalogEntry> alive = hooks.Where(h => !h.IsStale && h.XrefName.Length > 0).ToList();
        Assert.True(alive.Count > 100, $"подтверждённых точек всего {alive.Count}");

        List<string> silent = alive
            .Where(h => Xref(backend, h).TotalHits == 0)
            .Select(h => h.Name)
            .ToList();

        Assert.True(silent.Count == 0, "точки без единой ссылки: " + string.Join(", ", silent.Take(10)));
    }

    /// <summary>Поиск ссылок ровно так, как его делает экран по выбранной записи каталога.</summary>
    /// <param name="backend">Шов браузера.</param>
    /// <param name="entry">Запись каталога.</param>
    /// <returns>Результат поиска.</returns>
    private static ApiXrefResult Xref(IApiBrowserBackend backend, ApiCatalogEntry entry) => backend
        .FindReferencesAsync(
            new ApiXrefRequest
            {
                Name = entry.XrefName.Length > 0 ? entry.XrefName : entry.Name,
                Receiver = entry.XrefReceiver
            },
            CancellationToken.None)
        .GetAwaiter().GetResult();

    // ================================================================= вспомогательное

    private ApiBrowserViewModel Create(IApiBrowserBackend backend)
    {
        ApiBrowserViewModel vm = _ui.Invoke(() =>
        {
            var model = new ApiBrowserViewModel(backend, new NullSettingsStore(), new NoopEditor());
            model.AttachSettings(new CockpitSettings { WotSrcDir = string.Empty }, () => { });
            model.Initialize();
            return model;
        });

        _vm = vm;
        return vm;
    }

    private static ApiSymbol Symbol(
        string name,
        SymbolKind kind,
        string module,
        string? owner = null,
        int line = 10,
        int declarations = 1,
        SourceTier tier = SourceTier.Game)
    {
        string relative = "client/" + module.Replace('.', '/') + ".py";

        return new ApiSymbol(
            name,
            owner is null ? name : owner + "." + name,
            kind,
            module,
            "scripts/" + relative,
            relative,
            @"C:\src\" + relative.Replace('/', '\\'),
            line,
            kind is SymbolKind.Method or SymbolKind.Function ? "(self)" : null)
        {
            Owner = owner,
            DeclarationCount = declarations,
            Tier = tier
        };
    }

    /// <summary>
    /// Запись, у которой запрошенное слово встречается ТОЛЬКО в пути доступа, но не в имени.
    /// Ровно из таких состоит первая тридцатка настоящего каталога на запрос «battle».
    /// </summary>
    /// <param name="name">Имя события.</param>
    /// <param name="accessPath">Путь доступа.</param>
    /// <returns>Запись каталога.</returns>
    private static ApiCatalogEntry PathOnlyEntry(string name, string accessPath) => new()
    {
        Kind = ApiCatalogKind.Events,
        Name = name,
        Owner = "SomeOwner",
        AccessPath = accessPath,
        XrefName = name,
        VirtualPath = "scripts/client/x.py",
        RelativePath = "client/x.py",
        FullPath = @"C:\src\client\x.py",
        Line = 10
    };

    private static ApiCatalogEntry CatalogEntry(string name, bool battleOnly = false, bool throws = false) => new()
    {
        Kind = ApiCatalogKind.Events,
        Name = name,
        Owner = "_PlayerEvents",

        // AccessPath — РОВНО такой, каким его отдаёт Core (GameEvent.AccessPathDisplay):
        // импорт, стрелка и повтор имени. Здесь когда-то стояла укороченная строка
        // "from PlayerEvents import g_playerEvents", и из-за этого мимо тестов прошёл дефект:
        // UI выводил приёмник разбором этой строки и на НАСТОЯЩИХ данных получал
        // "g_playerEvents  ->  g_playerEvents.onAvatarReady", то есть ноль ссылок там,
        // где их 17. Подставные данные обязаны быть такими же кривыми, как настоящие.
        AccessPath = $"from PlayerEvents import g_playerEvents  ->  g_playerEvents.{name}",

        // Приёмник и имя для поиска считает шов по ПОЛЯМ записи Core, а не по строке выше.
        XrefName = name,
        XrefReceiver = "g_playerEvents",

        VirtualPath = "scripts/client/PlayerEvents.py",
        RelativePath = "client/PlayerEvents.py",
        FullPath = @"C:\src\client\PlayerEvents.py",
        Line = 57,
        BattleOnly = battleOnly,
        ThrowsToClient = throws
    };

    // ---------------------------------------------------------------- подставные сервисы

    /// <summary>Шов-пустышка с настраиваемыми ответами и счётчиками вызовов.</summary>
    private sealed class FakeBackend : IApiBrowserBackend
    {
        public List<ApiSymbol> Symbols { get; } = new();

        public List<ApiXrefHit> Hits { get; } = new();

        public List<ApiCatalogEntry> Catalog { get; } = new();

        public ApiSymbol? Resolved { get; set; }

        public ApiSnippet? Snippet { get; set; }

        /// <summary>Сниппет строится только после того, как каталоги построены (как в Core).</summary>
        public bool SnippetNeedsCatalog { get; set; }

        /// <summary>Каталог уже спрашивали.</summary>
        public bool CatalogRequested { get; private set; }

        public int XrefTotal { get; set; } = -1;

        public int XrefFiles { get; set; } = 1;

        public int XrefDeclarations { get; set; }

        public int SearchCalls { get; private set; }

        public string? LastQuery { get; private set; }

        public ApiSearchFilter? LastFilter { get; private set; }

        public ApiXrefRequest? LastXref { get; private set; }

        /// <summary>Сколько раз спрашивали ссылки — по нему проверяется дебаунс выделения.</summary>
        public int XrefCalls { get; private set; }

        /// <summary>Уточнение приёмником не находит ничего (как на настоящем дереве для метода).</summary>
        public bool NoHitsWithReceiver { get; set; }

        /// <summary>Символы видны только при включённых обеих галочках слоёв.</summary>
        public bool OnlyWithAllLayers { get; set; }

        public ApiIndexStatus Status { get; set; } = new()
        {
            State = ApiIndexState.Ready,
            TreePath = @"C:\src",
            FilesTotal = 8052,
            SymbolCount = 156_710
        };

        public event EventHandler<ApiIndexStatus>? StatusChanged;

        public Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
        {
            StatusChanged?.Invoke(this, Status);
            return Task.FromResult(Status);
        }

        public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
        {
            SearchCalls++;
            LastQuery = query;
            LastFilter = filter;

            // Имя, живущее ТОЛЬКО в скрытых слоях: при выключенных галочках его не видно.
            if (OnlyWithAllLayers && !(filter.IncludeGenerated && filter.IncludeVendor))
                return Task.FromResult<IReadOnlyList<ApiSymbol>>(Array.Empty<ApiSymbol>());

            return Task.FromResult<IReadOnlyList<ApiSymbol>>(Symbols);
        }

        public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(string? parentRelativePath, ApiSearchFilter filter, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiTreeNode>>(Array.Empty<ApiTreeNode>());

        public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiSymbol>>(Symbols);

        public Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
        {
            LastXref = request;
            XrefCalls++;

            // «Приёмник дал ноль» — самый частый случай на настоящем дереве: в местах вызова
            // стоит экземпляр, а не то имя, которым уточнили запрос.
            if (NoHitsWithReceiver && request.Receiver is not null)
            {
                return Task.FromResult(new ApiXrefResult(
                    request.Receiver + "." + request.Name,
                    Array.Empty<ApiXrefHit>(),
                    0, 0, XrefDeclarations,
                    Array.Empty<string>(),
                    TimeSpan.FromMilliseconds(3)));
            }

            return Task.FromResult(new ApiXrefResult(
                request.Receiver is null ? request.Name : request.Receiver + "." + request.Name,
                Hits,
                XrefTotal < 0 ? Hits.Count : XrefTotal,
                XrefFiles,
                XrefDeclarations,
                XrefDeclarations > 1 ? new[] { "settings", "g_playerEvents" } : Array.Empty<string>(),
                TimeSpan.FromMilliseconds(3)));
        }

        public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
            => Task.FromResult(Resolved);

        public Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
        {
            CatalogRequested = true;
            return Task.FromResult<IReadOnlyList<ApiCatalogEntry>>(Catalog);
        }

        public ApiSnippetRequest? LastSnippetRequest { get; private set; }

        public ApiSnippet? BuildSnippet(ApiSnippetRequest request)
        {
            LastSnippetRequest = request;
            return SnippetNeedsCatalog && !CatalogRequested ? null : Snippet;
        }
    }

    /// <summary>Шов, который бросает на каждом вызове: так выглядит незаконченный сервис Core.</summary>
    private sealed class ThrowingBackend : IApiBrowserBackend
    {
        public ApiIndexStatus Status { get; } = new() { State = ApiIndexState.Ready, SymbolCount = 1 };

        public event EventHandler<ApiIndexStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(string? parentRelativePath, ApiSearchFilter filter, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
            => throw new NotImplementedException();

        public ApiSnippet? BuildSnippet(ApiSnippetRequest request) => throw new NotImplementedException();
    }

    private sealed class NullSettingsStore : ISettingsStore
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "cockpit-test-settings.json");

        public CockpitSettings Load() => new();

        public void Save(CockpitSettings settings)
        {
            // тест не должен писать в профиль пользователя
        }
    }

    private sealed class NoopEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenFolder(string path)
        {
        }

        public void OpenAt(string filePath, int line)
        {
        }
    }
}
