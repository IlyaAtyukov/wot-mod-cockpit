namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Функция или метод, найденные лексически: имя, строка и текст списка аргументов.</summary>
/// <param name="Name">Имя.</param>
/// <param name="Line">Номер строки (с единицы).</param>
/// <param name="Args">Сырой текст между скобками сигнатуры, например <c>self, vehicleID</c>.</param>
public sealed record ScannedFunction(string Name, int Line, string Args);

/// <summary>Класс нулевого отступа вместе с методами отступа 4.</summary>
public sealed class ScannedClass
{
    /// <summary>Создаёт запись о классе.</summary>
    /// <param name="name">Имя класса.</param>
    /// <param name="bases">Сырой текст списка баз.</param>
    /// <param name="line">Номер строки объявления.</param>
    public ScannedClass(string name, string bases, int line)
    {
        Name = name;
        Bases = bases;
        Line = line;
    }

    /// <summary>Имя класса.</summary>
    public string Name { get; }

    /// <summary>Сырой текст списка баз, например <c>_ControllersLocator, ISharedControllersLocator</c>.</summary>
    public string Bases { get; }

    /// <summary>Номер строки объявления.</summary>
    public int Line { get; }

    /// <summary>Методы отступа 4. При повторном объявлении имени побеждает последнее.</summary>
    public Dictionary<string, ScannedFunction> Methods { get; } = new(StringComparer.Ordinal);

    /// <summary>Имена базовых классов, очищенные от точек (<c>a.b.IFoo</c> → <c>IFoo</c>).</summary>
    /// <returns>Перечисление коротких имён баз.</returns>
    public IEnumerable<string> BaseNames()
    {
        foreach (string part in Bases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string b = part;
            int paren = b.IndexOf('(');
            if (paren >= 0)
                b = b[..paren];

            int dot = b.LastIndexOf('.');
            if (dot >= 0)
                b = b[(dot + 1)..];

            b = b.Trim();
            if (b.Length > 0 && (char.IsLetter(b[0]) || b[0] == '_'))
                yield return b;
        }
    }
}

/// <summary>Сырое объявление события (правило E1).</summary>
/// <param name="Name">Имя без ведущих подчёркиваний.</param>
/// <param name="Privacy">Сколько ведущих подчёркиваний было: 0, 1 или 2.</param>
/// <param name="CtorKind">Конструктор: <c>Event</c>, <c>SafeEvent</c> или <c>SuspendedEvent</c>.</param>
/// <param name="OwnerClass">Ближайший объемлющий класс или <c>null</c>.</param>
/// <param name="OwnerFunction">Ближайшая объемлющая функция или <c>null</c>.</param>
/// <param name="SelfBound">Объявлено через <c>self.</c>/<c>cls.</c>, а не на уровне модуля.</param>
/// <param name="ManagerArg">Аргумент конструктора, например <c>self.__eManager</c>.</param>
/// <param name="Line">Номер строки.</param>
public sealed record RawEventDecl(
    string Name,
    int Privacy,
    string CtorKind,
    string? OwnerClass,
    string? OwnerFunction,
    bool SelfBound,
    string ManagerArg,
    int Line);

/// <summary>Модульный синглтон <c>g_playerEvents = _PlayerEvents()</c> (резолвер R1).</summary>
/// <param name="Global">Имя глобальной переменной.</param>
/// <param name="ClassName">Имя класса.</param>
/// <param name="Line">Номер строки.</param>
public sealed record ModuleSingleton(string Global, string ClassName, int Line);

/// <summary>Вложенный владелец <c>self.__users = _UsersSharedEvents()</c> (резолвер R5).</summary>
/// <param name="ChildClass">Класс-потомок.</param>
/// <param name="ParentClass">Класс-родитель.</param>
/// <param name="Attribute">Имя атрибута, например <c>__users</c>.</param>
/// <param name="Line">Номер строки.</param>
public sealed record NestedOwner(string ChildClass, string ParentClass, string Attribute, int Line);

/// <summary>Обращение к DI: <c>dependency.instance(IItemsCache)</c>.</summary>
/// <param name="TypeName">Короткое имя типа.</param>
/// <param name="IsDescriptor">Ленивая форма <c>descriptor</c>, а не <c>instance</c>.</param>
public sealed record DependencyUse(string TypeName, bool IsDescriptor);

/// <summary>Слот локатора боевых контроллеров (правило S4).</summary>
/// <param name="LocatorKind"><c>shared</c> или <c>dynamic</c>.</param>
/// <param name="SlotName">Имя слота, например <c>ammo</c>.</param>
/// <param name="ControllerId">Значение <c>BATTLE_CTRL_ID</c>, например <c>AMMO</c>.</param>
public sealed record LocatorSlot(string LocatorKind, string SlotName, string ControllerId);

/// <summary>Подписка или отписка <c>g_playerEvents.onAvatarReady += handler</c>.</summary>
/// <param name="Receiver">Текст выражения-получателя.</param>
/// <param name="EventName">Имя события.</param>
/// <param name="IsUnsubscribe">Это <c>-=</c>, а не <c>+=</c>.</param>
/// <param name="Line">Номер строки.</param>
public sealed record EventSubscription(string Receiver, string EventName, bool IsUnsubscribe, int Line);

/// <summary>
/// Место возбуждения события — источник сигнатуры (правило E3).
/// <para>
/// <paramref name="Receiver"/> — это и есть скоупинг, без которого правило ломается:
/// <c>onVehicleHealthChanged</c> объявлено и у <c>ClientArena</c>, и у <c>appearance</c>, и без
/// проверки получателя арена получает сигнатуру от чужого объекта (проверено, так и выходит).
/// </para>
/// </summary>
/// <param name="Receiver">Получатель: <c>self</c> либо цепочка вида <c>g_playerEvents</c>.</param>
/// <param name="OwnerClass">Класс, внутри которого стоит вызов; пусто, если вызов вне класса.</param>
/// <param name="EventName">Имя события.</param>
/// <param name="Args">Аргументы вызова; звёздочная распаковка уже раскрыта по правилу E3-bis.</param>
/// <param name="Line">Номер строки.</param>
public sealed record RawCallSite(string Receiver, string OwnerClass, string EventName, IReadOnlyList<string> Args, int Line);

/// <summary>Результат разбора одного файла.</summary>
public sealed class ScannedSourceFile
{
    private List<ScannedClass>? _classes;
    private Dictionary<string, ScannedFunction>? _moduleFunctions;
    private List<RawEventDecl>? _events;
    private List<ModuleSingleton>? _singletons;
    private List<NestedOwner>? _nested;
    private List<DependencyUse>? _deps;
    private List<LocatorSlot>? _slots;
    private List<EventSubscription>? _subs;
    private List<RawCallSite>? _calls;
    private Dictionary<string, string>? _controllerIds;
    private Dictionary<string, string>? _propertyAccessors;
    private Dictionary<string, SortedList<int, string>>? _tupleProps;

    /// <summary>Создаёт запись о файле.</summary>
    /// <param name="relPath">Путь относительно корня дерева, с прямыми слэшами.</param>
    /// <param name="virtualPath">Виртуальный путь из шапки uncompyle6 или <c>null</c>.</param>
    /// <param name="moduleName">Импортируемое имя модуля.</param>
    public ScannedSourceFile(string relPath, string? virtualPath, string moduleName)
    {
        RelPath = relPath;
        VirtualPath = virtualPath;
        ModuleName = moduleName;
    }

    /// <summary>Путь относительно корня дерева, с прямыми слэшами.</summary>
    public string RelPath { get; }

    /// <summary>Виртуальный путь <c>scripts/client/...</c> из шапки — то, чем оперирует traceback.</summary>
    public string? VirtualPath { get; }

    /// <summary>Импортируемое имя модуля, например <c>gui.SystemMessages</c>.</summary>
    public string ModuleName { get; }

    /// <summary>Классы нулевого отступа.</summary>
    public IReadOnlyList<ScannedClass> Classes => (IReadOnlyList<ScannedClass>?)_classes ?? Array.Empty<ScannedClass>();

    /// <summary>Функции нулевого отступа.</summary>
    public IReadOnlyDictionary<string, ScannedFunction> ModuleFunctions
        => _moduleFunctions ?? EmptyFunctions;

    /// <summary>Объявления событий (E1).</summary>
    public IReadOnlyList<RawEventDecl> Events => (IReadOnlyList<RawEventDecl>?)_events ?? Array.Empty<RawEventDecl>();

    /// <summary>Модульные синглтоны (R1).</summary>
    public IReadOnlyList<ModuleSingleton> Singletons => (IReadOnlyList<ModuleSingleton>?)_singletons ?? Array.Empty<ModuleSingleton>();

    /// <summary>Вложенные владельцы (R5).</summary>
    public IReadOnlyList<NestedOwner> NestedOwners => (IReadOnlyList<NestedOwner>?)_nested ?? Array.Empty<NestedOwner>();

    /// <summary>Обращения к DI (S2).</summary>
    public IReadOnlyList<DependencyUse> DependencyUses => (IReadOnlyList<DependencyUse>?)_deps ?? Array.Empty<DependencyUse>();

    /// <summary>Слоты локаторов боевых контроллеров (S4).</summary>
    public IReadOnlyList<LocatorSlot> LocatorSlots => (IReadOnlyList<LocatorSlot>?)_slots ?? Array.Empty<LocatorSlot>();

    /// <summary>Подписки и отписки.</summary>
    public IReadOnlyList<EventSubscription> Subscriptions => (IReadOnlyList<EventSubscription>?)_subs ?? Array.Empty<EventSubscription>();

    /// <summary>Места возбуждения событий (E3).</summary>
    public IReadOnlyList<RawCallSite> CallSites => (IReadOnlyList<RawCallSite>?)_calls ?? Array.Empty<RawCallSite>();

    /// <summary>Класс контроллера → значение <c>BATTLE_CTRL_ID</c>.</summary>
    public IReadOnlyDictionary<string, string> ControllerIdByClass => _controllerIds ?? EmptyStrings;

    /// <summary>Ключ <c>Класс|__атрибут</c> → имя публичного property, которое его отдаёт (R5).</summary>
    public IReadOnlyDictionary<string, string> PropertyAccessors => _propertyAccessors ?? EmptyStrings;

    /// <summary>Ключ <c>Класс|__атрибут</c> → имена property по индексам кортежа (E3-bis).</summary>
    public IReadOnlyDictionary<string, SortedList<int, string>> TupleProperties => _tupleProps ?? EmptyTuples;

    private static readonly Dictionary<string, ScannedFunction> EmptyFunctions = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> EmptyStrings = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, SortedList<int, string>> EmptyTuples = new(StringComparer.Ordinal);

    internal void AddClass(ScannedClass c) => (_classes ??= new List<ScannedClass>(4)).Add(c);

    internal void AddModuleFunction(ScannedFunction f)
        => (_moduleFunctions ??= new Dictionary<string, ScannedFunction>(StringComparer.Ordinal))[f.Name] = f;

    internal void AddEvent(RawEventDecl e) => (_events ??= new List<RawEventDecl>(4)).Add(e);

    internal void AddSingleton(ModuleSingleton s) => (_singletons ??= new List<ModuleSingleton>(2)).Add(s);

    internal void AddNestedOwner(NestedOwner n) => (_nested ??= new List<NestedOwner>(2)).Add(n);

    internal void AddDependencyUse(DependencyUse d) => (_deps ??= new List<DependencyUse>(4)).Add(d);

    internal void AddLocatorSlot(LocatorSlot s) => (_slots ??= new List<LocatorSlot>(4)).Add(s);

    internal void AddSubscription(EventSubscription s) => (_subs ??= new List<EventSubscription>(4)).Add(s);

    internal void AddCallSite(RawCallSite c) => (_calls ??= new List<RawCallSite>(4)).Add(c);

    internal void AddControllerId(string cls, string id)
        => (_controllerIds ??= new Dictionary<string, string>(StringComparer.Ordinal))[cls] = id;

    internal void AddPropertyAccessor(string cls, string attr, string prop)
        => (_propertyAccessors ??= new Dictionary<string, string>(StringComparer.Ordinal))[cls + "|" + attr] = prop;

    internal void AddTupleProperty(string cls, string attr, int index, string prop)
    {
        _tupleProps ??= new Dictionary<string, SortedList<int, string>>(StringComparer.Ordinal);
        string key = cls + "|" + attr;
        if (!_tupleProps.TryGetValue(key, out SortedList<int, string>? list))
        {
            list = new SortedList<int, string>();
            _tupleProps[key] = list;
        }

        list[index] = prop;
    }
}

/// <summary>
/// Результат лексического прохода по всему дереву. Ничего не интерпретирует — только факты,
/// из которых строятся каталоги.
/// </summary>
public sealed class ClientSourceScan
{
    /// <summary>Создаёт скан.</summary>
    /// <param name="root">Корень дерева.</param>
    /// <param name="files">Разобранные файлы.</param>
    /// <param name="skippedFiles">Сколько файлов не удалось прочитать.</param>
    public ClientSourceScan(string root, IReadOnlyList<ScannedSourceFile> files, int skippedFiles)
    {
        Root = root;
        Files = files;
        SkippedFiles = skippedFiles;
    }

    /// <summary>Корень дерева, по которому шёл скан.</summary>
    public string Root { get; }

    /// <summary>Разобранные файлы, отсортированы по <see cref="ScannedSourceFile.RelPath"/>.</summary>
    public IReadOnlyList<ScannedSourceFile> Files { get; }

    /// <summary>
    /// Сколько файлов пропущено из-за ошибки чтения. Значение показывается в UI: если оно
    /// вдруг вырастет на новой версии клиента, мододел обязан это увидеть.
    /// </summary>
    public int SkippedFiles { get; }

    /// <summary>Есть ли что-то в скане.</summary>
    public bool IsEmpty => Files.Count == 0;

    /// <summary>Пустой скан для случая «дерева исходников нет».</summary>
    /// <param name="root">Путь, который пытались просканировать.</param>
    /// <returns>Пустой, но валидный объект.</returns>
    public static ClientSourceScan Empty(string root) => new(root, Array.Empty<ScannedSourceFile>(), 0);
}
