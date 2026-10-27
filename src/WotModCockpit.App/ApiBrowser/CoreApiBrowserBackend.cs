using System.IO;
using WotModCockpit.Core.ApiCatalog;
using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.App.ApiBrowser;

/// <summary>
/// Реализация шва: <see cref="IApiBrowserBackend"/> поверх двух сервисов Core —
/// индекса символов (<see cref="IApiIndexService"/>) и доменных каталогов
/// (<see cref="IApiCatalogService"/>).
/// <para>
/// Здесь нет ни разбора, ни поиска, ни ранжирования, ни генерации кода: всё это живёт в Core
/// и покрыто его тестами. У этого класса три обязанности, и все три — переводческие:
/// </para>
/// <list type="number">
///   <item>перевести модели Core в модель экрана и обратно;</item>
///   <item>собрать дерево модулей — структуры «дерево» в индексе нет и не должно быть,
///         она выводится из плоского <see cref="ApiIndex.Files"/> по одному уровню за раскрытие;</item>
///   <item>довести до генератора сниппетов ИСХОДНУЮ запись каталога, а не её проекцию
///         (см. <see cref="ApiCatalogEntry.Source"/>).</item>
/// </list>
/// <para>
/// Каталоги строятся ЛЕНИВО, при первом открытии соответствующей панели: их построение —
/// это ещё один полный проход по дереву, и платить за него на старте приложения незачем,
/// если пользователь пришёл искать символ.
/// </para>
/// </summary>
public sealed class CoreApiBrowserBackend : IApiBrowserBackend
{
    private readonly IApiIndexService _index;
    private readonly IApiCatalogService _catalogs;

    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly object _cacheLock = new();

    /// <summary>Сколько символов в каждом файле. Индексируется <c>FileId</c>.</summary>
    private int[] _symbolsPerFile = Array.Empty<int>();

    /// <summary>Относительный путь → <c>FileId</c>.</summary>
    private Dictionary<string, int>? _fileIdByPath;

    /// <summary>Индекс, для которого посчитаны кэши. Смена ссылки = пересборка.</summary>
    private ApiIndex? _cachedFor;

    /// <summary>
    /// Индекс, для которого построены доменные каталоги.
    /// <para>
    /// Сравнивать только корень дерева нельзя: главный сценарий кнопки «переиндексировать» —
    /// «я перегенерировал дамп клиента новой версией», путь при этом ТОТ ЖЕ. Раньше после такой
    /// переиндексации символы были свежие, а списки событий, сервисов и горячих точек оставались
    /// от предыдущего дампа, и об этом нигде не говорилось.
    /// </para>
    /// </summary>
    private ApiIndex? _catalogsBuiltFor;

    /// <summary>Создаёт адаптер.</summary>
    /// <param name="index">Сервис индекса символов.</param>
    /// <param name="catalogs">Сервис доменных каталогов.</param>
    public CoreApiBrowserBackend(IApiIndexService index, IApiCatalogService catalogs)
    {
        _index = index ?? throw new ArgumentNullException(nameof(index));
        _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));

        _index.StatusChanged += (_, s) => StatusChanged?.Invoke(this, Translate(s));
    }

    /// <inheritdoc />
    public ApiIndexStatus Status => Translate(_index.Status);

    /// <inheritdoc />
    public event EventHandler<ApiIndexStatus>? StatusChanged;

    /// <inheritdoc />
    public async Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
    {
        // Прогресс идёт тем же каналом, что и состояние: экрану нужен не процент,
        // а признак жизни плюс «обработано из всего» для индикатора.
        var progress = new Progress<ApiIndexProgress>(p => StatusChanged?.Invoke(
            this,
            new ApiIndexStatus
            {
                State = ApiIndexState.Indexing,
                TreePath = treePath ?? _index.Status.RootPath,
                FilesTotal = p.Total,
                FilesDone = p.Processed
            }));

        ApiIndexBuildStatus status = await _index
            .RebuildAsync(treePath, progress, cancellationToken)
            .ConfigureAwait(false);

        InvalidateCaches();
        return Translate(status);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        var options = new SymbolSearchOptions
        {
            Limit = filter.Limit,
            IncludeGenerated = filter.IncludeGenerated,
            IncludeVendor = filter.IncludeVendor,
            Kinds = filter.Kind is null ? null : new[] { filter.Kind.Value }
        };

        // Поиск синхронный и стоит доли миллисекунды, но выполняется на каждое нажатие
        // клавиши, поэтому всё равно уходит с UI-потока: отзывчивость мыши дороже Task.
        return Task.Run<IReadOnlyList<ApiSymbol>>(() => _index.Search(query, options), cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(
        string? parentRelativePath,
        ApiSearchFilter filter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);

        return Task.Run<IReadOnlyList<ApiTreeNode>>(
            () => BuildTreeChildren(parentRelativePath, filter),
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
        => Task.Run<IReadOnlyList<ApiSymbol>>(
            () =>
            {
                ApiIndex index = _index.Index;
                int fileId = FileId(index, relativePath);
                return fileId < 0 ? Array.Empty<ApiSymbol>() : index.SymbolsOfFile(fileId);
            },
            cancellationToken);

    /// <inheritdoc />
    public async Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var query = new XrefQuery
        {
            Name = request.Name,
            Receiver = request.Receiver,
            IgnoreCase = request.IgnoreCase,
            IncludeGenerated = request.IncludeGenerated,
            IncludeVendor = request.IncludeVendor,
            Limit = request.Limit
        };

        XrefResult result = await _index.FindReferencesAsync(query, cancellationToken).ConfigureAwait(false);

        var hits = new List<ApiXrefHit>(result.Hits.Count);
        foreach (XrefHit hit in result.Hits)
        {
            hits.Add(new ApiXrefHit(
                hit.RelativePath,
                hit.VirtualPath,
                hit.FullPath,
                hit.Line,
                hit.Text,
                Translate(hit.Category))
            {
                Tier = hit.Tier,
                Root = hit.Root
            });
        }

        return new ApiXrefResult(
            result.Needle,
            hits,
            result.TotalHits,
            result.FileCount,
            result.DeclarationCount,
            result.TopReceivers.Select(r => r.Receiver).ToArray(),
            result.Elapsed);
    }

    /// <inheritdoc />
    public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
        => Task.FromResult(_index.ResolveTracebackFrame(virtualPath, line));

    // ================================================================= каталоги

    /// <inheritdoc />
    public async Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
    {
        ApiCatalogSnapshot snapshot = await EnsureCatalogsAsync(cancellationToken).ConfigureAwait(false);

        return kind switch
        {
            ApiCatalogKind.Events => MapEvents(snapshot),
            ApiCatalogKind.Services => MapServices(snapshot),
            _ => MapHooks(snapshot)
        };
    }

    /// <summary>
    /// Строит каталоги, если они ещё не построены для текущего корня дерева.
    /// Под семафором: три панели браузера могут попросить каталог одновременно, а полный
    /// проход по дереву незачем делать трижды.
    /// </summary>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Готовый снимок; при любом отказе — снимок со статусом, а не исключение.</returns>
    private async Task<ApiCatalogSnapshot> EnsureCatalogsAsync(CancellationToken cancellationToken)
    {
        string root = _index.Status.RootPath;
        ApiIndex index = _index.Index;

        if (IsFresh(_catalogs.Current, root, index))
            return _catalogs.Current;

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsFresh(_catalogs.Current, root, index))
                return _catalogs.Current;

            ApiCatalogSnapshot built = await _catalogs.BuildAsync(root, cancellationToken).ConfigureAwait(false);

            lock (_cacheLock)
                _catalogsBuiltFor = index;

            return built;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    /// <summary>
    /// Годится ли готовый снимок каталогов. Годится он только тогда, когда построен и по тому же
    /// корню, и по ТОМУ ЖЕ экземпляру индекса: пересборка индекса создаёт новый экземпляр,
    /// а значит дерево на диске изменилось и каталоги устарели вместе с ним.
    /// </summary>
    /// <param name="snapshot">Текущий снимок.</param>
    /// <param name="root">Корень дерева.</param>
    /// <param name="index">Текущий индекс.</param>
    /// <returns>true, если снимок можно отдавать как есть.</returns>
    private bool IsFresh(ApiCatalogSnapshot snapshot, string root, ApiIndex index)
    {
        if (snapshot.Status != ApiCatalogStatus.Ready)
            return false;

        if (!string.Equals(snapshot.SourceRoot, root, StringComparison.OrdinalIgnoreCase))
            return false;

        lock (_cacheLock)
            return ReferenceEquals(_catalogsBuiltFor, index);
    }

    /// <summary>
    /// События. Порядок — по числу подписок в клиенте, а не по алфавиту: событие, на которое
    /// подписан сам клиент в 261 месте, полезнее того, на которое не подписан никто, а искать
    /// по имени всё равно проще фильтром.
    /// </summary>
    /// <param name="snapshot">Снимок каталогов.</param>
    /// <returns>Записи каталога событий.</returns>
    private static IReadOnlyList<ApiCatalogEntry> MapEvents(ApiCatalogSnapshot snapshot)
    {
        var list = new List<ApiCatalogEntry>(snapshot.Events.Events.Count);

        foreach (GameEvent ev in snapshot.Events.Events)
        {
            string signature = ev.HasSignature
                ? "(" + string.Join(", ", ev.Signature) + ")"
                : string.Empty;

            list.Add(new ApiCatalogEntry
            {
                Kind = ApiCatalogKind.Events,
                Name = ev.SubscriptionAttribute,
                Owner = ev.OwnerClass ?? ev.ModuleName,
                AccessPath = ev.AccessPathDisplay,
                XrefName = ev.SubscriptionAttribute,
                XrefReceiver = XrefReceiverOf(ev),
                Signature = signature,
                Badge = ev.SubscriptionCount > 0 ? ev.SubscriptionCount + " подписок" : ev.ModuleName,
                VirtualPath = ev.VirtualPath ?? ev.RelPath,
                RelativePath = ev.RelPath,
                FullPath = FullPath(snapshot.SourceRoot, ev.RelPath),
                Line = ev.Line,
                Weight = ev.SubscriptionCount,
                ThrowsToClient = ev.PropagatesHandlerExceptions,
                BattleOnly = ev.RequiresBattle,
                Detail = DescribeEvent(ev),
                Source = ev
            });
        }

        list.Sort((a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Name, b.Name);
        });

        return list;
    }

    /// <summary>
    /// Приёмник для поиска ссылок на событие.
    /// <para>
    /// Строится ИЗ ПОЛЕЙ записи, а не из строки <see cref="GameEvent.AccessPathDisplay"/>:
    /// та собрана для человека и содержит и импорт, и стрелку, и комментарий с решёткой.
    /// Поиск ссылок ищет подстроку «приёмник.имя», поэтому годится только голый
    /// идентификатор — иначе панель ссылок честно отвечает «ссылок не найдено» там,
    /// где их 17.
    /// </para>
    /// </summary>
    /// <param name="ev">Событие каталога.</param>
    /// <returns>Идентификатор приёмника либо <c>null</c>, если уточнять нечем.</returns>
    private static string? XrefReceiverOf(GameEvent ev) => ev.Resolution switch
    {
        // g_playerEvents.onAvatarReady — сужает 115 попаданий до 33.
        OwnerResolution.Singleton or OwnerResolution.Nested => LastIdentifier(ev.OwnerExpression),

        // BigWorld.player().arena -> arena: в коде клиента пишут self.arena.onX,
        // player.arena.onX, arena.onX — общее у них ровно последнее звено.
        OwnerResolution.EntityAttribute => LastIdentifier(ev.OwnerExpression),

        // sessionProvider.shared.vehicleState -> vehicleState по той же причине:
        // сам провайдер в коде зовётся то sessionProvider, то __sessionProvider.
        OwnerResolution.BattleSlot => LastIdentifier(ev.SlotName),

        // Сервис приходит в поле с произвольным именем (self.__itemsCache, self._items):
        // общего приёмника нет, поэтому ищем голое имя события.
        _ => null
    };

    /// <summary>
    /// Последнее звено выражения доступа, если это чистый идентификатор.
    /// <c>BigWorld.player().arena</c> → <c>arena</c>; <c>dependency.instance(I)</c> → <c>null</c>.
    /// </summary>
    /// <param name="expression">Выражение либо <c>null</c>.</param>
    /// <returns>Идентификатор либо <c>null</c>.</returns>
    private static string? LastIdentifier(string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        string tail = expression.Trim();

        int cut = tail.LastIndexOfAny(new[] { '.', ')', ']', ' ', '\t' });
        if (cut >= 0)
            tail = tail[(cut + 1)..];

        if (tail.Length == 0 || (!char.IsLetter(tail[0]) && tail[0] != '_'))
            return null;

        foreach (char c in tail)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return null;
        }

        return tail;
    }

    private static string DescribeEvent(GameEvent ev)
    {
        var parts = new List<string>(4)
        {
            ev.Resolution == OwnerResolution.Unresolved
                ? "Путь до владельца не выведен: подписаться снаружи, скорее всего, нельзя."
                : "Подписка: " + ev.AccessPathDisplay + " += обработчик"
        };

        if (!ev.HasSignature)
        {
            parts.Add("Сигнатура не восстановлена: ни одного места возбуждения не нашлось. "
                      + "Объявляйте обработчик как def handler(*args).");
        }
        else if (ev.SignatureCandidates.Count > 1)
        {
            parts.Add("Вариантов сигнатуры несколько — выбран самый частый по местам возбуждения.");
        }

        if (ev.HomonymCount > 1)
            parts.Add($"Имя события встречается у {ev.HomonymCount} разных владельцев.");

        return string.Join(Environment.NewLine, parts);
    }

    /// <summary>
    /// Сервисы. Порядок — по числу обращений <c>dependency.instance/descriptor</c>: иначе
    /// сотня интерфейсов из одного <c>skeletons.gui.game_control</c> хоронит под собой нужные шесть.
    /// </summary>
    /// <param name="snapshot">Снимок каталогов.</param>
    /// <returns>Записи каталога сервисов.</returns>
    private static IReadOnlyList<ApiCatalogEntry> MapServices(ApiCatalogSnapshot snapshot)
    {
        var list = new List<ApiCatalogEntry>(snapshot.Services.Services.Count);

        foreach (GameService svc in snapshot.Services.Services)
        {
            var detail = new List<string>(3)
            {
                $"Обращений в клиенте: {svc.UsageCount} (instance {svc.InstanceCount}, descriptor {svc.DescriptorCount})."
            };

            if (svc.Implementations.Count > 0)
            {
                ServiceImplementation impl = svc.Implementations[0];
                detail.Add($"Реализация: {impl.ClassName} — {impl.RelPath}:{impl.Line}"
                           + (svc.Implementations.Count > 1 ? $" (и ещё {svc.Implementations.Count - 1})" : string.Empty));
            }

            detail.Add("Вне подходящего контекста сервис может отсутствовать: проверяйте isConfigured, "
                       + "hasInstance и сам результат на None.");

            list.Add(new ApiCatalogEntry
            {
                Kind = ApiCatalogKind.Services,
                Name = svc.Name,
                Owner = svc.ModuleName,
                AccessPath = $"dependency.instance({svc.Name})",

                // Приёмника у интерфейса нет: его имя и есть то, что ищется
                // (dependency.instance(IItemsCache), @dependency.descriptor и импорты).
                XrefName = svc.Name,
                XrefReceiver = null,
                Badge = svc.UsageCount > 0 ? svc.UsageCount + " обращений" : string.Empty,
                VirtualPath = svc.VirtualPath ?? svc.RelPath,
                RelativePath = svc.RelPath,
                FullPath = FullPath(snapshot.SourceRoot, svc.RelPath),
                Line = svc.Line,
                Weight = svc.UsageCount,
                Detail = string.Join(Environment.NewLine, detail),
                Source = svc
            });
        }

        list.Sort((a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Name, b.Name);
        });

        return list;
    }

    /// <summary>
    /// Горячие точки. Ненайденные в текущем клиенте НЕ выбрасываются, а помечаются
    /// <see cref="ApiCatalogEntry.IsStale"/>: «этого в вашей версии больше нет» — тоже ответ,
    /// и именно он спасает при переходе на следующую версию игры.
    /// </summary>
    /// <param name="snapshot">Снимок каталогов.</param>
    /// <returns>Записи каталога горячих точек.</returns>
    private static IReadOnlyList<ApiCatalogEntry> MapHooks(ApiCatalogSnapshot snapshot)
    {
        var list = new List<ApiCatalogEntry>(snapshot.Hooks.Count);

        foreach (HookPoint hook in snapshot.Hooks)
        {
            var detail = new List<string>(4)
            {
                hook.Status switch
                {
                    HookStatus.Verified => "Найдено в исходниках вашего клиента.",
                    HookStatus.Inherited => "Объявлено не в самом классе, а в его базе (так живут все as_*S из *Meta).",
                    HookStatus.ModuleLevel => "Держатель — модуль, а не класс.",
                    HookStatus.Missing => "В исходниках вашего клиента НЕ НАЙДЕНО: патч мёртв, это ветка под другой клиент.",
                    _ => "Не проверено: индекс не построен."
                }
            };

            if (hook.Mods.Count > 0)
                detail.Add("Патчат моды: " + string.Join(", ", hook.Mods));

            if (hook.PatchKinds.Count > 0)
                detail.Add("Идиомы патча: " + string.Join(", ", hook.PatchKinds));

            if (hook.HolderModuleAmbiguous)
                detail.Add("Имя класса встречается в нескольких модулях — проверьте, тот ли это держатель.");

            list.Add(new ApiCatalogEntry
            {
                Kind = ApiCatalogKind.Hotspots,
                Name = hook.Holder + "." + hook.Member,
                Owner = hook.HolderModuleName ?? hook.ModuleName ?? string.Empty,
                AccessPath = hook.ModuleName is null ? string.Empty : $"from {hook.ModuleName} import {hook.Holder}",

                // В карточке показывается пара «Класс.член», а искать надо член — и именно
                // тот, под которым он ОБЪЯВЛЕН. Моды патчат приватный член по mangled-имени
                // (_TimersPanel__setFireInVehicle), а в исходниках клиента стоит
                // __setFireInVehicle: искать mangled-имя в дереве клиента бессмысленно.
                //
                // Приёмником держатель НЕ становится, хотя соблазн есть: клиент зовёт свои
                // же методы через self (self._updateDeviceState), а модульные функции —
                // вообще голым именем (handleKeyEvent(event)). Уточнение «Класс.член»
                // встречается только в чужих модах, то есть в дереве клиента даёт ноль.
                // Ноль вместо списка мест — ровно то, ради чего эта правка и делалась.
                XrefName = string.IsNullOrEmpty(hook.DeclaredName) ? hook.Member : hook.DeclaredName,
                XrefReceiver = null,
                Signature = hook.Signature,
                Badge = hook.ModCount > 0 ? hook.ModCount + " модов" : string.Empty,
                VirtualPath = hook.VirtualPath ?? string.Empty,
                RelativePath = hook.RelPath ?? string.Empty,
                FullPath = hook.RelPath is null ? string.Empty : FullPath(snapshot.SourceRoot, hook.RelPath),
                Line = hook.Line,
                Weight = hook.ModCount * 1000 + hook.SiteCount,
                IsStale = hook.Status == HookStatus.Missing,
                Detail = string.Join(Environment.NewLine, detail),
                Source = hook
            });
        }

        list.Sort((a, b) =>
        {
            int byWeight = b.Weight.CompareTo(a.Weight);
            return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Name, b.Name);
        });

        return list;
    }

    private static string FullPath(string root, string relativePath)
    {
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relativePath))
            return string.Empty;

        try
        {
            return Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }
        catch (ArgumentException)
        {
            return string.Empty;
        }
    }

    // ================================================================= сниппеты

    /// <inheritdoc />
    public ApiSnippet? BuildSnippet(ApiSnippetRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string modId = string.IsNullOrWhiteSpace(request.ModId) ? "my_mod" : request.ModId;
        SnippetGenerator generator = _catalogs.Snippets;

        // Приоритет у записи каталога: в ней есть резолвер владельца, восстановленная сигнатура
        // и предупреждения, которых из одного лишь символа индекса не получить.
        GeneratedSnippet? generated = request.Entry?.Source switch
        {
            GameEvent ev => generator.ForEvent(ev, modId),
            GameService svc => generator.ForService(svc, modId, SlotFor(svc)),
            HookPoint hook => generator.ForHook(hook, modId),
            _ => null
        };

        generated ??= FromSymbol(generator, request, modId);

        return generated is null
            ? null
            : new ApiSnippet(
                generated.Title,
                generated.Code,
                string.Join(Environment.NewLine, generated.Warnings));
    }

    /// <summary>
    /// Сниппет по символу индекса, когда каталожной записи нет (пользователь выбрал символ
    /// в поиске или в дереве). Сначала пробуем найти настоящую запись каталога по имени —
    /// она богаче; и только если её нет, синтезируем минимальную.
    /// </summary>
    /// <param name="generator">Генератор Core.</param>
    /// <param name="request">Запрос.</param>
    /// <param name="modId">Идентификатор мода.</param>
    /// <returns>Сниппет либо <c>null</c>, если строить не из чего.</returns>
    private GeneratedSnippet? FromSymbol(SnippetGenerator generator, ApiSnippetRequest request, string modId)
    {
        if (request.Symbol is not { } symbol)
            return null;

        ApiCatalogSnapshot snapshot = _catalogs.Current;

        switch (request.Kind)
        {
            case ApiSnippetKind.EventSubscription or ApiSnippetKind.LateEventSubscription:
            {
                GameEvent? known = FindEvent(snapshot, symbol);
                return known is null ? null : generator.ForEvent(known, modId);
            }

            case ApiSnippetKind.ServiceAccess:
            {
                GameService? known = snapshot.Services.Services
                    .FirstOrDefault(s => string.Equals(s.Name, symbol.Name, StringComparison.Ordinal));

                return known is null ? null : generator.ForService(known, modId, SlotFor(known));
            }

            default:
            {
                // Монкипатч строится по любому символу: держатель — владелец из индекса,
                // и он по определению проверен, раз символ пришёл из построенного индекса.
                string holder = OwnerOf(symbol);

                var hook = new HookPoint
                {
                    Holder = holder,
                    Member = symbol.Name,
                    ModuleName = symbol.Module,
                    HolderModuleName = symbol.Module,
                    RelPath = symbol.RelativePath,
                    VirtualPath = symbol.VirtualPath,
                    Line = symbol.Line,
                    Signature = symbol.Signature ?? string.Empty,
                    DeclaredName = symbol.Name,
                    DeclaringClass = symbol.Owner,
                    Status = symbol.Kind == SymbolKind.Method ? HookStatus.Verified : HookStatus.ModuleLevel,
                    SiteCount = 0
                };

                return generator.ForHook(hook, modId);
            }
        }
    }

    private static GameEvent? FindEvent(ApiCatalogSnapshot snapshot, ApiSymbol symbol)
    {
        if (!snapshot.Events.ByName.TryGetValue(symbol.Name, out IReadOnlyList<GameEvent>? candidates)
            || candidates.Count == 0)
        {
            return null;
        }

        // Имя события не уникально (893 имени на 1030 объявлений), поэтому сначала пробуем
        // совпадение по файлу — иначе карточка получит путь доступа от чужого владельца.
        foreach (GameEvent candidate in candidates)
        {
            if (string.Equals(candidate.RelPath, symbol.RelativePath, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }

        return candidates[0];
    }

    private BattleControllerSlot? SlotFor(GameService service)
        => _catalogs.Current.Services.Slots
            .FirstOrDefault(s => string.Equals(s.ControllerClass, service.Name, StringComparison.Ordinal));

    private static string OwnerOf(ApiSymbol symbol)
    {
        if (!string.IsNullOrEmpty(symbol.Owner))
            return symbol.Owner;

        int dot = symbol.Module.LastIndexOf('.');
        return dot >= 0 ? symbol.Module[(dot + 1)..] : symbol.Module;
    }

    // ================================================================= перевод моделей

    private static ApiIndexStatus Translate(ApiIndexBuildStatus status) => new()
    {
        State = status.State switch
        {
            ApiIndexBuildState.NotConfigured => ApiIndexState.NotConfigured,
            ApiIndexBuildState.RootMissing => ApiIndexState.Missing,
            ApiIndexBuildState.Building => ApiIndexState.Indexing,
            ApiIndexBuildState.Ready => ApiIndexState.Ready,
            _ => ApiIndexState.Failed
        },
        TreePath = status.RootPath,
        FilesTotal = status.FileCount,
        FilesDone = status.FileCount,
        SkippedFiles = status.ErrorCount,
        SymbolCount = status.SymbolCount,
        Elapsed = status.BuildTime,

        // Message у Core — уже готовая к показу строка; для отказа и отмены она и есть причина.
        Error = status.State is ApiIndexBuildState.Failed or ApiIndexBuildState.Cancelled
            ? status.Message
            : null
    };

    private static ApiXrefKind Translate(XrefCategory category) => category switch
    {
        XrefCategory.Declaration => ApiXrefKind.Declaration,
        XrefCategory.Call => ApiXrefKind.Call,
        XrefCategory.Subscribe or XrefCategory.Unsubscribe => ApiXrefKind.EventSubscription,
        XrefCategory.AttributeRead => ApiXrefKind.AttributeRead,
        XrefCategory.Import => ApiXrefKind.Import,
        XrefCategory.StringLiteral => ApiXrefKind.StringLiteral,
        XrefCategory.Comment => ApiXrefKind.Comment,
        _ => ApiXrefKind.Other
    };

    // ================================================================= дерево модулей

    /// <summary>
    /// Собирает один уровень дерева из плоского списка файлов. Дерева как структуры в индексе
    /// нет и не должно быть: 8052 пути дают больше тысячи каталогов, а раскрытых узлов
    /// за сеанс бывает десяток.
    /// </summary>
    /// <param name="parent">Каталог-родитель либо <c>null</c> для корней.</param>
    /// <param name="filter">Фильтр слоёв.</param>
    /// <returns>Каталоги, затем файлы, оба по алфавиту.</returns>
    private IReadOnlyList<ApiTreeNode> BuildTreeChildren(string? parent, ApiSearchFilter filter)
    {
        ApiIndex index = _index.Index;
        if (index.IsEmpty)
            return Array.Empty<ApiTreeNode>();

        int[] counts = SymbolCounts(index);
        string prefix = string.IsNullOrEmpty(parent) ? string.Empty : parent + "/";

        var directories = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = new List<ApiTreeNode>();

        for (int i = 0; i < index.Files.Count; i++)
        {
            SourceFileEntry file = index.Files[i];

            if (!Allowed(file.Tier, filter))
                continue;

            string path = file.RelativePath;
            if (prefix.Length > 0 && !path.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            string rest = path[prefix.Length..];
            int slash = rest.IndexOf('/');
            int symbols = i < counts.Length ? counts[i] : 0;

            if (slash < 0)
            {
                files.Add(new ApiTreeNode(rest, path, false)
                {
                    VirtualPath = file.VirtualPath,
                    FullPath = file.FullPath(index.RootPath),
                    SymbolCount = symbols,
                    Tier = file.Tier
                });
            }
            else
            {
                string name = rest[..slash];
                directories.TryGetValue(name, out int accumulated);
                directories[name] = accumulated + symbols;
            }
        }

        var result = new List<ApiTreeNode>(directories.Count + files.Count);

        foreach (KeyValuePair<string, int> dir in directories.OrderBy(d => d.Key, StringComparer.OrdinalIgnoreCase))
            result.Add(new ApiTreeNode(dir.Key, prefix + dir.Key, true) { SymbolCount = dir.Value });

        result.AddRange(files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static bool Allowed(SourceTier tier, ApiSearchFilter filter) => tier switch
    {
        SourceTier.Generated => filter.IncludeGenerated,
        SourceTier.Vendor => filter.IncludeVendor,
        _ => true
    };

    // ================================================================= кэши на одну сборку

    /// <summary>
    /// Сбрасывает кэши, если индекс сменился. Оба кэша привязаны к одному экземпляру
    /// <see cref="ApiIndex"/> и обнуляются вместе: смешать кэш старой сборки с новыми файлами
    /// означало бы открывать не тот файл по щелчку в дереве.
    /// </summary>
    /// <param name="index">Текущий индекс.</param>
    private void EnsureCacheGenerationLocked(ApiIndex index)
    {
        if (ReferenceEquals(_cachedFor, index))
            return;

        _cachedFor = index;
        _fileIdByPath = null;
        _symbolsPerFile = Array.Empty<int>();
    }

    private int[] SymbolCounts(ApiIndex index)
    {
        lock (_cacheLock)
        {
            EnsureCacheGenerationLocked(index);

            if (_symbolsPerFile.Length == index.Files.Count && index.Files.Count > 0)
                return _symbolsPerFile;

            var counts = new int[index.Files.Count];
            for (int id = 0; id < index.SymbolCount; id++)
            {
                int fileId = index.GetEntry(id).FileId;
                if ((uint)fileId < (uint)counts.Length)
                    counts[fileId]++;
            }

            _symbolsPerFile = counts;
            return counts;
        }
    }

    private int FileId(ApiIndex index, string relativePath)
    {
        lock (_cacheLock)
        {
            EnsureCacheGenerationLocked(index);

            if (_fileIdByPath is null)
            {
                var map = new Dictionary<string, int>(index.Files.Count, StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < index.Files.Count; i++)
                    map[index.Files[i].RelativePath] = i;

                _fileIdByPath = map;
            }

            return _fileIdByPath.TryGetValue(relativePath, out int id) ? id : -1;
        }
    }

    private void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _cachedFor = null;
            _fileIdByPath = null;
            _symbolsPerFile = Array.Empty<int>();

            // Каталоги тоже привязаны к сборке, а не к пути: после «переиндексировать»
            // следующий GetCatalogAsync обязан построить их заново.
            _catalogsBuiltFor = null;
        }
    }
}
