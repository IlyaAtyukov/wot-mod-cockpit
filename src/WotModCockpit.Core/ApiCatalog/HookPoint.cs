namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Итог проверки горячей точки по исходникам ТЕКУЩЕЙ версии клиента.</summary>
public enum HookStatus
{
    /// <summary>Проверка не выполнялась: дерева исходников нет.</summary>
    Unknown,

    /// <summary>Метод найден прямо в классе-держателе.</summary>
    Verified,

    /// <summary>
    /// Метод найден не в самом классе, а в его базе. Так живут все <c>as_*S</c>:
    /// они объявлены в <c>*Meta</c>-базе, а моды патчат их по имени наследника.
    /// </summary>
    Inherited,

    /// <summary>Держатель — модуль, а не класс: например <c>game.handleKeyEvent</c>.</summary>
    ModuleLevel,

    /// <summary>
    /// В исходниках текущей версии не найдено. Патч мёртв. Это самостоятельная ценность:
    /// 45 из 252 точек в разведке оказались WG-ветками XVM, которых в клиенте Леста 1.44 нет.
    /// </summary>
    Missing,
}

/// <summary>
/// Горячая точка — место, которое реальные моды патчат на практике. Каталог курируемый:
/// он получен вскрытием 28 <c>.mtmod</c> из установленной игры (309 <c>.pyc</c>, 237 декомпилированы,
/// 7 модов разобраны целиком) и списком его не заменить — по исходникам клиента нельзя узнать,
/// ЧТО патчат другие.
/// <para>
/// Но курируемый список ОБЯЗАН проверяться по индексу: если класса или метода нет в исходниках
/// текущей версии, запись помечается <see cref="HookStatus.Missing"/>, а не показывается как валидная.
/// Это то, что спасёт пользователя при переходе на 1.45.
/// </para>
/// <para>
/// Ключ — тройка (модуль, класс, член), НИКОГДА не пара (класс, член): в корпусе 16 866 классов
/// верхнего уровня, и <c>Vehicle.onHealthChanged</c> (сущность <c>client/Vehicle.py</c>) — это совсем
/// не <c>Vehicle.isAmmoFull</c> (GUI-item <c>gui.shared.gui_items.Vehicle</c>).
/// </para>
/// </summary>
public sealed record HookPoint
{
    /// <summary>Класс или модуль-держатель, как его пишут моды: <c>Vehicle</c>, <c>game</c>, <c>LobbyHeader</c>.</summary>
    public required string Holder { get; init; }

    /// <summary>
    /// Имя патчимого члена ровно так, как его пишут моды. Приватные члены моды пишут mangled-именем
    /// <c>_Class__method</c>, тогда как в исходнике объявлено <c>__method</c> — де-манглинг делает проверка.
    /// </summary>
    public required string Member { get; init; }

    /// <summary>Идентификаторы модов, которые это патчат. Межмодовый консенсус = сила сигнала.</summary>
    public IReadOnlyList<string> Mods { get; init; } = Array.Empty<string>();

    /// <summary>Сколько точек патча найдено суммарно (один мод может патчить одно место дважды).</summary>
    public int SiteCount { get; init; }

    /// <summary>Идиомы патча, встреченные у модов: <c>overrideMethod</c>, <c>registerEvent</c>, <c>setattr</c>.</summary>
    public IReadOnlyList<string> PatchKinds { get; init; } = Array.Empty<string>();

    /// <summary>Итог проверки по исходникам.</summary>
    public HookStatus Status { get; init; } = HookStatus.Unknown;

    /// <summary>Импортируемый модуль, где член реально объявлен. Заполняется проверкой.</summary>
    public string? ModuleName { get; init; }

    /// <summary>Файл относительно корня дерева. Заполняется проверкой.</summary>
    public string? RelPath { get; init; }

    /// <summary>Виртуальный путь из шапки — мост к traceback этапа 1.</summary>
    public string? VirtualPath { get; init; }

    /// <summary>Номер строки объявления в исходниках. 0, если не найдено.</summary>
    public int Line { get; init; }

    /// <summary>Сырой текст сигнатуры, например <c>self, newHealth, oldHealth, attackerID, attackReasonID</c>.</summary>
    public string Signature { get; init; } = string.Empty;

    /// <summary>
    /// Модуль САМОГО ДЕРЖАТЕЛЯ — то, что надо импортировать в моде. Это не одно и то же, что
    /// <see cref="ModuleName"/>: для <c>as_*S</c> член объявлен в <c>*Meta</c>-базе (другой модуль),
    /// а патчить моды обязаны конкретный класс, иначе изменится поведение всех наследников.
    /// </summary>
    public string? HolderModuleName { get; init; }

    /// <summary>Имя класса-держателя встречается больше чем в одном модуле — импорт надо перепроверить.</summary>
    public bool HolderModuleAmbiguous { get; init; }

    /// <summary>Имя, под которым член реально объявлен (после де-манглинга <c>_Cls__m</c> → <c>__m</c>).</summary>
    public string? DeclaredName { get; init; }

    /// <summary>Класс, в котором член реально объявлен: для <c>as_*S</c> это <c>*Meta</c>-база.</summary>
    public string? DeclaringClass { get; init; }

    /// <summary>Точка подтверждена в исходниках и на неё можно генерировать сниппет.</summary>
    public bool IsUsable => Status is HookStatus.Verified or HookStatus.Inherited or HookStatus.ModuleLevel;

    /// <summary>Сколько разных модов трогают это место.</summary>
    public int ModCount => Mods.Count;
}
