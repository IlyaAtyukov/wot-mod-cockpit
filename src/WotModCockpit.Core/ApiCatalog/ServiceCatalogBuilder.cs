namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Каталог сервисов: интерфейсы из <c>skeletons/</c> плюс таблица слотов боевой сессии.</summary>
public sealed class ServiceCatalog
{
    /// <summary>Создаёт каталог.</summary>
    /// <param name="services">Сервисы, отсортированы по убыванию <see cref="GameService.UsageCount"/>.</param>
    /// <param name="slots">Слоты боевой сессии.</param>
    /// <param name="controllerIdByClass">Класс контроллера → значение <c>BATTLE_CTRL_ID</c>.</param>
    /// <param name="dependencySiteCount">Всего обращений к <c>dependency.instance/descriptor</c>.</param>
    public ServiceCatalog(
        IReadOnlyList<GameService> services,
        IReadOnlyList<BattleControllerSlot> slots,
        IReadOnlyDictionary<string, string> controllerIdByClass,
        int dependencySiteCount)
    {
        Services = services;
        Slots = slots;
        ControllerIdByClass = controllerIdByClass;
        DependencySiteCount = dependencySiteCount;

        var byName = new Dictionary<string, GameService>(services.Count, StringComparer.Ordinal);
        foreach (GameService s in services)
            byName[s.Name] = s;
        ByName = byName;

        var slotByCtrl = new Dictionary<string, BattleControllerSlot>(slots.Count, StringComparer.Ordinal);
        foreach (BattleControllerSlot s in slots)
        {
            if (s.ControllerId.Length > 0)
                slotByCtrl[s.ControllerId] = s;
        }

        SlotByControllerId = slotByCtrl;
    }

    /// <summary>Сервисы, отсортированы по убыванию числа обращений (это и есть ранг полезности).</summary>
    public IReadOnlyList<GameService> Services { get; }

    /// <summary>Слоты <c>sessionProvider.shared|dynamic.*</c>.</summary>
    public IReadOnlyList<BattleControllerSlot> Slots { get; }

    /// <summary>Класс контроллера → значение <c>BATTLE_CTRL_ID</c>.</summary>
    public IReadOnlyDictionary<string, string> ControllerIdByClass { get; }

    /// <summary>Значение <c>BATTLE_CTRL_ID</c> → слот.</summary>
    public IReadOnlyDictionary<string, BattleControllerSlot> SlotByControllerId { get; }

    /// <summary>Быстрый поиск сервиса по имени интерфейса.</summary>
    public IReadOnlyDictionary<string, GameService> ByName { get; }

    /// <summary>Всего обращений к <c>dependency.instance/descriptor</c> по корпусу.</summary>
    public int DependencySiteCount { get; }

    /// <summary>Пустой каталог для случая «дерева исходников нет».</summary>
    public static ServiceCatalog Empty { get; } = new(
        Array.Empty<GameService>(),
        Array.Empty<BattleControllerSlot>(),
        new Dictionary<string, string>(StringComparer.Ordinal),
        0);
}

/// <summary>
/// Правила S1..S4: каталог сервисов клиента строится ИЗ ИСХОДНИКОВ, а не хардкодится.
/// <list type="bullet">
///   <item>S1 — интерфейс: класс нулевого отступа под <c>client/skeletons/**</c>.</item>
///   <item>S2 — ранг полезности: число обращений <c>dependency.instance|descriptor(IFoo)</c> по всему дереву.</item>
///   <item>S3 — реализация: <c>class X(..., IFoo, ...)</c> вне <c>skeletons/</c>.</item>
///   <item>S4 — слоты боя: <c>repositories.py</c> связывает <c>sessionProvider.shared.ammo</c>
///         с <c>BATTLE_CTRL_ID.AMMO</c>, а <c>def getControllerID</c> связывает <c>BATTLE_CTRL_ID.AMMO</c>
///         с классом контроллера. Это самая ценная часть каталога для боевых модов.</item>
/// </list>
/// </summary>
public sealed class ServiceCatalogBuilder
{
    private const string SkeletonPrefix = "client/skeletons/";

    /// <summary>Строит каталог сервисов по скану дерева.</summary>
    /// <param name="scan">Результат лексического прохода. Пустой скан даёт пустой каталог.</param>
    /// <returns>Каталог; никогда не <c>null</c>.</returns>
    public ServiceCatalog Build(ClientSourceScan scan)
    {
        ArgumentNullException.ThrowIfNull(scan);
        if (scan.IsEmpty)
            return ServiceCatalog.Empty;

        // --- S1: интерфейсы из skeletons/ ------------------------------------------
        var skeletons = new Dictionary<string, (ScannedClass Cls, ScannedSourceFile File)>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            if (!f.RelPath.StartsWith(SkeletonPrefix, StringComparison.Ordinal))
                continue;

            foreach (ScannedClass c in f.Classes)
                skeletons[c.Name] = (c, f);
        }

        // --- S2: ранг полезности ----------------------------------------------------
        var instances = new Dictionary<string, int>(StringComparer.Ordinal);
        var descriptors = new Dictionary<string, int>(StringComparer.Ordinal);
        int sites = 0;
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (DependencyUse d in f.DependencyUses)
            {
                sites++;
                Dictionary<string, int> bag = d.IsDescriptor ? descriptors : instances;
                bag.TryGetValue(d.TypeName, out int n);
                bag[d.TypeName] = n + 1;
            }
        }

        // --- S3: реализации ---------------------------------------------------------
        var impls = new Dictionary<string, List<ServiceImplementation>>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            if (f.RelPath.StartsWith(SkeletonPrefix, StringComparison.Ordinal))
                continue;

            foreach (ScannedClass c in f.Classes)
            {
                foreach (string b in c.BaseNames())
                {
                    if (!skeletons.ContainsKey(b))
                        continue;

                    if (!impls.TryGetValue(b, out List<ServiceImplementation>? list))
                    {
                        list = new List<ServiceImplementation>(4);
                        impls[b] = list;
                    }

                    if (list.Count < 5)
                        list.Add(new ServiceImplementation(c.Name, f.RelPath, c.Line));
                }
            }
        }

        var services = new List<GameService>(skeletons.Count);
        foreach ((string name, (ScannedClass cls, ScannedSourceFile file)) in skeletons)
        {
            instances.TryGetValue(name, out int inst);
            descriptors.TryGetValue(name, out int desc);
            var methods = new List<string>(cls.Methods.Count);
            foreach (string m in cls.Methods.Keys)
            {
                if (m.Length > 0 && m[0] != '_')
                    methods.Add(m);
            }

            methods.Sort(StringComparer.Ordinal);

            services.Add(new GameService
            {
                Name = name,
                ModuleName = file.ModuleName,
                RelPath = file.RelPath,
                VirtualPath = file.VirtualPath,
                Line = cls.Line,
                Bases = cls.Bases,
                Methods = methods,
                UsageCount = inst + desc,
                InstanceCount = inst,
                DescriptorCount = desc,
                Implementations = impls.TryGetValue(name, out List<ServiceImplementation>? il)
                    ? il
                    : Array.Empty<ServiceImplementation>(),
            });
        }

        // Сортировка по рангу полезности обязательна: иначе 102 интерфейса из
        // skeletons.gui.game_control похоронят под собой нужные шесть.
        services.Sort(static (a, b) =>
        {
            int c = b.UsageCount.CompareTo(a.UsageCount);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        // --- S4: слоты боевой сессии ------------------------------------------------
        var ctrlIdByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        var ctrlFileByClass = new Dictionary<string, string>(StringComparer.Ordinal);
        var rawSlots = new List<(LocatorSlot Slot, string RelPath)>();
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach ((string cls, string id) in f.ControllerIdByClass)
            {
                ctrlIdByClass[cls] = id;
                ctrlFileByClass[cls] = f.RelPath;
            }

            foreach (LocatorSlot s in f.LocatorSlots)
                rawSlots.Add((s, f.RelPath));
        }

        var classByCtrlId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string cls, string id) in ctrlIdByClass)
            classByCtrlId.TryAdd(id, cls);

        var eventsPerClass = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (ScannedSourceFile f in scan.Files)
        {
            foreach (RawEventDecl e in f.Events)
            {
                if (e.OwnerClass is null)
                    continue;

                eventsPerClass.TryGetValue(e.OwnerClass, out int n);
                eventsPerClass[e.OwnerClass] = n + 1;
            }
        }

        var slots = new List<BattleControllerSlot>(rawSlots.Count);
        var seenSlots = new HashSet<string>(StringComparer.Ordinal);
        foreach ((LocatorSlot s, string _) in rawSlots)
        {
            if (!seenSlots.Add(s.LocatorKind + "." + s.SlotName))
                continue;

            classByCtrlId.TryGetValue(s.ControllerId, out string? ctrlClass);
            eventsPerClass.TryGetValue(ctrlClass ?? string.Empty, out int evCount);
            slots.Add(new BattleControllerSlot
            {
                SlotName = s.SlotName,
                LocatorKind = s.LocatorKind,
                ControllerId = s.ControllerId,
                ControllerClass = ctrlClass,
                ControllerRelPath = ctrlClass is not null && ctrlFileByClass.TryGetValue(ctrlClass, out string? cp) ? cp : null,
                EventCount = evCount,
            });
        }

        slots.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.LocatorKind, b.LocatorKind);
            return c != 0 ? c : string.CompareOrdinal(a.SlotName, b.SlotName);
        });

        return new ServiceCatalog(services, slots, ctrlIdByClass, sites);
    }
}
