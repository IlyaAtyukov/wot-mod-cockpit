namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Статистика подписок на одно событие у одного получателя — это готовый пример «как подписаться».</summary>
/// <param name="Receiver">Выражение-получатель, например <c>g_playerEvents</c>.</param>
/// <param name="EventName">Имя события.</param>
/// <param name="SubscribeCount">Сколько раз встретилось <c>+=</c>.</param>
/// <param name="UnsubscribeCount">Сколько раз встретилось <c>-=</c>.</param>
/// <param name="RelPath">Файл первого примера.</param>
/// <param name="Line">Строка первого примера.</param>
public sealed record EventSubscriptionStat(
    string Receiver,
    string EventName,
    int SubscribeCount,
    int UnsubscribeCount,
    string RelPath,
    int Line);

/// <summary>Каталог событий клиента.</summary>
public sealed class EventCatalog
{
    /// <summary>Создаёт каталог.</summary>
    /// <param name="events">События.</param>
    /// <param name="subscriptions">Статистика подписок по парам (получатель, событие).</param>
    public EventCatalog(IReadOnlyList<GameEvent> events, IReadOnlyList<EventSubscriptionStat> subscriptions)
    {
        Events = events;
        Subscriptions = subscriptions;

        var byName = new Dictionary<string, List<GameEvent>>(StringComparer.Ordinal);
        foreach (GameEvent e in events)
        {
            if (!byName.TryGetValue(e.Name, out List<GameEvent>? list))
            {
                list = new List<GameEvent>(1);
                byName[e.Name] = list;
            }

            list.Add(e);
        }

        ByName = byName.ToDictionary(static kv => kv.Key, static kv => (IReadOnlyList<GameEvent>)kv.Value, StringComparer.Ordinal);
    }

    /// <summary>Все события, отсортированы по имени, затем по файлу.</summary>
    public IReadOnlyList<GameEvent> Events { get; }

    /// <summary>Подписки: готовые примеры и мера востребованности события.</summary>
    public IReadOnlyList<EventSubscriptionStat> Subscriptions { get; }

    /// <summary>События по имени. Одно имя может быть объявлено у нескольких владельцев.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<GameEvent>> ByName { get; }

    /// <summary>
    /// Имена событий, на которые в корпусе подписываются через указанный объект.
    /// Ради этого метода каталог и существует: у <c>g_playerEvents</c> так находится 67 событий,
    /// тогда как загрузчик модов отдаёт моду только 6.
    /// </summary>
    /// <param name="receiver">Выражение-получатель, например <c>g_playerEvents</c>.</param>
    /// <returns>Имена событий, по убыванию числа подписок.</returns>
    public IReadOnlyList<EventSubscriptionStat> SubscribedVia(string receiver)
    {
        var res = new List<EventSubscriptionStat>();
        foreach (EventSubscriptionStat s in Subscriptions)
        {
            if (string.Equals(s.Receiver, receiver, StringComparison.Ordinal))
                res.Add(s);
        }

        res.Sort(static (a, b) =>
        {
            int c = b.SubscribeCount.CompareTo(a.SubscribeCount);
            return c != 0 ? c : string.CompareOrdinal(a.EventName, b.EventName);
        });

        return res;
    }

    /// <summary>Пустой каталог для случая «дерева исходников нет».</summary>
    public static EventCatalog Empty { get; } = new(Array.Empty<GameEvent>(), Array.Empty<EventSubscriptionStat>());
}

/// <summary>
/// Правила E1..E3: каталог событий строится ИЗ ИСХОДНИКОВ по шаблону присваивания <c>Event</c>,
/// а не хардкодится списком.
/// <list type="bullet">
///   <item>E1 — объявление: <c>self.&lt;name&gt; = [Event.]Event|SafeEvent|SuspendedEvent(...)</c>.
///         Скобки могут быть НЕпустыми (<c>Event.Event(em)</c>, <c>Event.Event(self.__eManager)</c>) —
///         это не повод отбрасывать строку. Владелец — ближайший объемлющий класс по стеку отступов.</item>
///   <item>E2 — путь доступа: пять резолверов в порядке R1 → R3 → R2 → R4 → R5. Дают 55% покрытия;
///         остальным честно пишется «владелец достижим только изнутри клиента», а не выдуманный путь.</item>
///   <item>E3 — сигнатура: берётся из МЕСТА ВОЗБУЖДЕНИЯ (в объявлении она всегда пустая) и
///         обязательно скоупится по получателю, иначе <c>onVehicleHealthChanged</c> арены получит
///         сигнатуру от <c>self.appearance</c> — проверено, так и происходит без скоупинга.</item>
/// </list>
/// </summary>
public sealed class EventCatalogBuilder
{
    private const string SkeletonPrefix = "client/skeletons/";

    /// <summary>
    /// R4: жёсткая таблица атрибутов игровой сущности. Единственное курируемое место в каталоге
    /// событий — вывести эти пути лексически нельзя, они заданы семантикой BigWorld.
    /// </summary>
    private static readonly Dictionary<string, string> PlayerAttributes = new(StringComparer.Ordinal)
    {
        ["ClientArena"] = "BigWorld.player().arena",
        ["AvatarInputHandler"] = "BigWorld.player().inputHandler",
        ["VehicleGunRotator"] = "BigWorld.player().gunRotator",
    };

    /// <summary>Строит каталог событий поверх скана и уже построенного каталога сервисов.</summary>
    /// <param name="scan">Результат лексического прохода.</param>
    /// <param name="services">Каталог сервисов — нужен резолверам R2 и R3.</param>
    /// <returns>Каталог; никогда не <c>null</c>.</returns>
    public EventCatalog Build(ClientSourceScan scan, ServiceCatalog services)
    {
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(services);
        if (scan.IsEmpty)
            return EventCatalog.Empty;

        // --- индексы для резолверов ------------------------------------------------
        var singletonsByFile = new Dictionary<string, List<ModuleSingleton>>(StringComparer.Ordinal);
        var skeletonNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            if (f.Singletons.Count > 0)
                singletonsByFile[f.RelPath] = new List<ModuleSingleton>(f.Singletons);

            if (f.RelPath.StartsWith(SkeletonPrefix, StringComparison.Ordinal))
            {
                foreach (ScannedClass c in f.Classes)
                    skeletonNames.Add(c.Name);
            }
        }

        // R2: конкретный класс → интерфейс из skeletons/, который он реализует. Первый выигрывает.
        var implOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            if (f.RelPath.StartsWith(SkeletonPrefix, StringComparison.Ordinal))
                continue;

            foreach (ScannedClass c in f.Classes)
            {
                foreach (string b in c.BaseNames())
                {
                    if (b.Length > 0 && b[0] == 'I' && skeletonNames.Contains(b))
                    {
                        implOf.TryAdd(c.Name, b);
                        break;
                    }
                }
            }
        }

        // R5: класс-потомок → (родитель, атрибут, файл родителя).
        var nestedOf = new Dictionary<string, (NestedOwner Owner, ScannedSourceFile File)>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (NestedOwner n in f.NestedOwners)
                nestedOf.TryAdd(n.ChildClass, (n, f));
        }

        // --- подписки ----------------------------------------------------------------
        var subs = new Dictionary<string, (string Receiver, string Name, int Plus, int Minus, string Rel, int Line)>(StringComparer.Ordinal);
        var subsByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (EventSubscription s in f.Subscriptions)
            {
                string key = s.Receiver + "." + s.EventName;
                if (!subs.TryGetValue(key, out var cur))
                    cur = (s.Receiver, s.EventName, 0, 0, f.RelPath, s.Line);

                subs[key] = s.IsUnsubscribe
                    ? cur with { Minus = cur.Minus + 1 }
                    : cur with { Plus = cur.Plus + 1 };

                if (!s.IsUnsubscribe)
                {
                    subsByName.TryGetValue(s.EventName, out int n);
                    subsByName[s.EventName] = n + 1;
                }
            }
        }

        // --- омонимия ------------------------------------------------------------------
        var declCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (RawEventDecl e in f.Events)
            {
                declCount.TryGetValue(e.Name, out int n);
                declCount[e.Name] = n + 1;
            }
        }

        // --- места возбуждения через глобальный синглтон (правило E3, межфайловая часть) ----
        var globalSites = new Dictionary<string, List<(RawCallSite Site, string Rel)>>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (RawCallSite cs in f.CallSites)
            {
                if (string.Equals(cs.Receiver, "self", StringComparison.Ordinal))
                    continue;

                string key = cs.Receiver + "|" + cs.EventName;
                if (!globalSites.TryGetValue(key, out List<(RawCallSite, string)>? list))
                {
                    list = new List<(RawCallSite, string)>(2);
                    globalSites[key] = list;
                }

                list.Add((cs, f.RelPath));
            }
        }

        // --- сами события ----------------------------------------------------------------
        var events = new List<GameEvent>(1200);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (RawEventDecl raw in f.Events)
            {
                GameEvent ev = Resolve(raw, f, services, singletonsByFile, implOf, nestedOf);

                (IReadOnlyList<string> best, bool found, IReadOnlyList<EventSignatureCandidate> candidates) =
                    RecoverSignature(raw, ev, f, globalSites);

                subsByName.TryGetValue(raw.Name, out int subCount);
                declCount.TryGetValue(raw.Name, out int homonyms);

                events.Add(ev with
                {
                    Signature = best,
                    HasSignature = found,
                    SignatureCandidates = candidates,
                    SubscriptionCount = subCount,
                    HomonymCount = homonyms,
                });
            }
        }

        events.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Name, b.Name);
            if (c != 0)
                return c;

            c = string.CompareOrdinal(a.RelPath, b.RelPath);
            return c != 0 ? c : a.Line.CompareTo(b.Line);
        });

        var stats = new List<EventSubscriptionStat>(subs.Count);
        foreach (var v in subs.Values)
            stats.Add(new EventSubscriptionStat(v.Receiver, v.Name, v.Plus, v.Minus, v.Rel, v.Line));

        stats.Sort(static (a, b) =>
        {
            int c = b.SubscribeCount.CompareTo(a.SubscribeCount);
            if (c != 0)
                return c;

            c = string.CompareOrdinal(a.Receiver, b.Receiver);
            return c != 0 ? c : string.CompareOrdinal(a.EventName, b.EventName);
        });

        return new EventCatalog(events, stats);
    }

    /// <summary>Правило E2: пять резолверов в порядке R1 → R3 → R2 → R4 → R5, первый выигрывает.</summary>
    private static GameEvent Resolve(
        RawEventDecl raw,
        ScannedSourceFile file,
        ServiceCatalog services,
        Dictionary<string, List<ModuleSingleton>> singletonsByFile,
        Dictionary<string, string> implOf,
        Dictionary<string, (NestedOwner Owner, ScannedSourceFile File)> nestedOf)
    {
        var ev = new GameEvent
        {
            Name = raw.Name,
            Privacy = raw.Privacy,
            OwnerClass = raw.OwnerClass,
            CtorKind = raw.CtorKind switch
            {
                "SafeEvent" => EventCtorKind.SafeEvent,
                "SuspendedEvent" => EventCtorKind.SuspendedEvent,
                _ => EventCtorKind.Event,
            },
            ManagerArg = raw.ManagerArg,
            RelPath = file.RelPath,
            VirtualPath = file.VirtualPath,
            ModuleName = file.ModuleName,
            Line = raw.Line,
            Resolution = OwnerResolution.Unresolved,
        };

        string? cls = raw.OwnerClass;
        if (cls is null)
            return ev;

        // R1: модульный синглтон в файле владельца.
        if (singletonsByFile.TryGetValue(file.RelPath, out List<ModuleSingleton>? sl))
        {
            foreach (ModuleSingleton s in sl)
            {
                if (!string.Equals(s.ClassName, cls, StringComparison.Ordinal))
                    continue;

                return ev with
                {
                    Resolution = OwnerResolution.Singleton,
                    ImportLine = $"from {file.ModuleName} import {s.Global}",
                    OwnerExpression = s.Global,
                };
            }
        }

        // R3: слот боевой сессии.
        if (services.ControllerIdByClass.TryGetValue(cls, out string? ctrlId) &&
            services.SlotByControllerId.TryGetValue(ctrlId, out BattleControllerSlot? slot))
        {
            return ev with
            {
                Resolution = OwnerResolution.BattleSlot,
                LocatorKind = slot.LocatorKind,
                SlotName = slot.SlotName,
                ControllerId = ctrlId,
                OwnerExpression = slot.AccessExpression,
                ServiceInterface = "IBattleSessionProvider",
                ServiceModule = "skeletons.gui.battle_session",
                RequiresBattle = true,
            };
        }

        // R2: DI-сервис.
        if (implOf.TryGetValue(cls, out string? iface))
        {
            services.ByName.TryGetValue(iface, out GameService? svc);
            return ev with
            {
                Resolution = OwnerResolution.Service,
                ServiceInterface = iface,
                ServiceModule = svc?.ModuleName,
                ImportLine = svc is null ? null : $"from {svc.ModuleName} import {iface}",
                OwnerExpression = "service",
            };
        }

        // R4: атрибут игровой сущности.
        if (PlayerAttributes.TryGetValue(cls, out string? attrPath))
        {
            return ev with
            {
                Resolution = OwnerResolution.EntityAttribute,
                OwnerExpression = attrPath,
                RequiresBattle = true,
            };
        }

        // R5: вложенный владелец внутри резолвимого родителя.
        if (nestedOf.TryGetValue(cls, out var nested))
        {
            NestedOwner n = nested.Owner;
            ScannedSourceFile pf = nested.File;
            if (singletonsByFile.TryGetValue(pf.RelPath, out List<ModuleSingleton>? psl))
            {
                foreach (ModuleSingleton s in psl)
                {
                    if (!string.Equals(s.ClassName, n.ParentClass, StringComparison.Ordinal))
                        continue;

                    string pub = pf.PropertyAccessors.TryGetValue(n.ParentClass + "|" + n.Attribute, out string? p)
                        ? p
                        : n.Attribute.TrimStart('_');

                    return ev with
                    {
                        Resolution = OwnerResolution.Nested,
                        ImportLine = $"from {pf.ModuleName} import {s.Global}",
                        OwnerExpression = $"{s.Global}.{pub}",
                    };
                }
            }
        }

        return ev;
    }

    /// <summary>
    /// Правило E3: сигнатура из мест возбуждения, строго со скоупингом по получателю.
    /// Принимаются два вида мест: <c>self.&lt;event&gt;(...)</c> внутри класса-владельца в файле
    /// объявления и <c>&lt;резолвнутый путь&gt;.&lt;event&gt;(...)</c> где угодно в корпусе.
    /// Никакие другие получатели не принимаются: иначе событие получает чужую сигнатуру.
    /// </summary>
    private static (IReadOnlyList<string> Best, bool Found, IReadOnlyList<EventSignatureCandidate> Candidates)
        RecoverSignature(
            RawEventDecl raw,
            GameEvent ev,
            ScannedSourceFile file,
            Dictionary<string, List<(RawCallSite Site, string Rel)>> globalSites)
    {
        var groups = new Dictionary<string, (IReadOnlyList<string> Args, int Count, string Rel, int Line)>(StringComparer.Ordinal);

        void Add(IReadOnlyList<string> args, string rel, int line)
        {
            string key = string.Join(',', args);
            if (groups.TryGetValue(key, out var g))
                groups[key] = g with { Count = g.Count + 1 };
            else
                groups[key] = (args, 1, rel, line);
        }

        if (raw.OwnerClass is not null)
        {
            foreach (RawCallSite cs in file.CallSites)
            {
                if (string.Equals(cs.Receiver, "self", StringComparison.Ordinal) &&
                    string.Equals(cs.EventName, raw.Name, StringComparison.Ordinal) &&
                    string.Equals(cs.OwnerClass, raw.OwnerClass, StringComparison.Ordinal))
                {
                    Add(cs.Args, file.RelPath, cs.Line);
                }
            }
        }

        if (ev.OwnerExpression is { Length: > 0 } expr &&
            ev.Resolution is OwnerResolution.Singleton or OwnerResolution.Nested &&
            globalSites.TryGetValue(expr + "|" + raw.Name, out List<(RawCallSite Site, string Rel)>? gs))
        {
            foreach ((RawCallSite site, string rel) in gs)
                Add(site.Args, rel, site.Line);
        }

        if (groups.Count == 0)
            return (Array.Empty<string>(), false, Array.Empty<EventSignatureCandidate>());

        var cands = new List<EventSignatureCandidate>(groups.Count);
        foreach (var g in groups.Values)
            cands.Add(new EventSignatureCandidate(g.Args, g.Count, g.Rel, g.Line));

        // Побеждает самый частый вариант; при равенстве — более информативный (больше аргументов).
        cands.Sort(static (a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            if (c != 0)
                return c;

            c = b.Args.Count.CompareTo(a.Args.Count);
            return c != 0 ? c : a.Line.CompareTo(b.Line);
        });

        return (cands[0].Args, true, cands);
    }
}
