namespace WotModCockpit.Core.ApiCatalog;

/// <summary>Конструктор, которым объявлено событие. Определяет поведение при исключении в обработчике.</summary>
public enum EventCtorKind
{
    /// <summary>
    /// <c>Event.Event()</c> — 1004 объявления из 1030. <c>common/Event.py:33-36</c> в блоке
    /// <c>except</c> делает <c>LOG_CURRENT_EXCEPTION(); raise</c>, то есть исключение из обработчика
    /// ВСПЛЫВАЕТ в клиент. Обработчик обязан быть целиком в <c>try/except</c>.
    /// </summary>
    Event,

    /// <summary>
    /// <c>Event.SafeEvent()</c> — 7 объявлений. Переопределяет <c>__call__</c> без <c>raise</c>:
    /// исключение только логируется.
    /// </summary>
    SafeEvent,

    /// <summary><c>Event.SuspendedEvent()</c> — 19 объявлений. Умеет откладывать вызовы.</summary>
    SuspendedEvent,
}

/// <summary>
/// Каким резолвером найден путь до объекта-владельца события (правило E2).
/// Порядок применения: R1 → R3 → R2 → R4 → R5, первый выигравший побеждает.
/// </summary>
public enum OwnerResolution
{
    /// <summary>Путь не найден: владелец достижим только изнутри клиента. 45% событий.</summary>
    Unresolved,

    /// <summary>R1: в файле владельца есть <c>g_x = OwnerClass()</c> — доступен через импорт.</summary>
    Singleton,

    /// <summary>R3: владелец — боевой контроллер, доступен как <c>sessionProvider.shared|dynamic.&lt;слот&gt;</c>.</summary>
    BattleSlot,

    /// <summary>R2: владелец реализует интерфейс из <c>skeletons/</c> — доступен через <c>dependency.instance</c>.</summary>
    Service,

    /// <summary>R4: владелец — атрибут игровой сущности, например <c>BigWorld.player().arena</c>.</summary>
    EntityAttribute,

    /// <summary>R5: владелец создаётся внутри резолвимого родителя и отдаётся его property.</summary>
    Nested,
}

/// <summary>
/// Событие клиента: то, на что мододел может подписаться, чтобы узнать о происходящем в игре.
/// <para>
/// Зачем каталог вообще нужен: загрузчик модов отдаёт моду РОВНО 6 колбэков
/// (<c>gui/shared/personality.py</c>: onAccountBecomeNonPlayer, onAvatarBecomePlayer,
/// onAccountBecomePlayer, onConnected, onDisconnected, onAccountShowGUI), а в корпусе
/// объявлено 1030 событий. Всё остальное надо добывать подпиской, и найти это можно
/// только сканированием исходников.
/// </para>
/// <para>
/// Событие НЕ ловится индексом def/class: оно объявлено как атрибут
/// <c>self.onArenaPeriodChange = Event.Event()</c>. Поэтому это отдельная сущность каталога.
/// </para>
/// </summary>
public sealed record GameEvent
{
    /// <summary>Имя события без ведущих подчёркиваний, например <c>onArenaPeriodChange</c>.</summary>
    public required string Name { get; init; }

    /// <summary>Сколько ведущих подчёркиваний было в объявлении: 0 (1023 события), 1 (3) или 2 (4).</summary>
    public int Privacy { get; init; }

    /// <summary>Класс-владелец, например <c>_PlayerEvents</c>. <c>null</c> для объявления на уровне модуля.</summary>
    public string? OwnerClass { get; init; }

    /// <summary>Конструктор события.</summary>
    public EventCtorKind CtorKind { get; init; }

    /// <summary>Аргумент конструктора (менеджер событий), например <c>self.__eManager</c>. Может быть пустым.</summary>
    public string ManagerArg { get; init; } = string.Empty;

    /// <summary>Путь файла относительно корня дерева, например <c>client/PlayerEvents.py</c>.</summary>
    public required string RelPath { get; init; }

    /// <summary>
    /// Виртуальный путь из шапки uncompyle6, например <c>scripts/client/PlayerEvents.py</c>.
    /// Это мост к этапу 1: traceback в <c>python.log</c> печатает именно его.
    /// </summary>
    public string? VirtualPath { get; init; }

    /// <summary>Импортируемое имя модуля, например <c>PlayerEvents</c>.</summary>
    public string ModuleName { get; init; } = string.Empty;

    /// <summary>Номер строки объявления.</summary>
    public int Line { get; init; }

    /// <summary>Каким резолвером получен путь до владельца.</summary>
    public OwnerResolution Resolution { get; init; }

    /// <summary>Строка импорта для сниппета, например <c>from PlayerEvents import g_playerEvents</c>.</summary>
    public string? ImportLine { get; init; }

    /// <summary>
    /// Выражение владельца в коде мода, например <c>g_playerEvents</c>, <c>g_messengerEvents.users</c>
    /// или <c>BigWorld.player().arena</c>. <c>null</c>, если владелец не резолвится.
    /// </summary>
    public string? OwnerExpression { get; init; }

    /// <summary>Интерфейс сервиса для R2, например <c>IBattleSessionProvider</c>.</summary>
    public string? ServiceInterface { get; init; }

    /// <summary>Модуль интерфейса для R2, например <c>skeletons.gui.battle_session</c>.</summary>
    public string? ServiceModule { get; init; }

    /// <summary>Для R3: <c>shared</c> либо <c>dynamic</c>.</summary>
    public string? LocatorKind { get; init; }

    /// <summary>Для R3: имя слота, например <c>feedback</c>.</summary>
    public string? SlotName { get; init; }

    /// <summary>Для R3: значение <c>BATTLE_CTRL_ID</c>, например <c>FEEDBACK</c>.</summary>
    public string? ControllerId { get; init; }

    /// <summary>
    /// Владелец существует только в бою. Верно для R3 (все <c>sessionProvider.shared|dynamic.*</c>
    /// вне боя равны <c>None</c>: <c>_EmptyRepository.getController</c> возвращает <c>None</c>)
    /// и для R4 (<c>BigWorld.player().arena</c>). Такому событию нужен двухступенчатый шаблон A2.
    /// </summary>
    public bool RequiresBattle { get; init; }

    /// <summary>
    /// Лучшая восстановленная сигнатура: имена аргументов из места возбуждения события.
    /// Пустой список означает «событие возбуждается без аргументов» ЛИБО «сигнатуру восстановить не удалось» —
    /// различать помогает <see cref="HasSignature"/>.
    /// </summary>
    public IReadOnlyList<string> Signature { get; init; } = Array.Empty<string>();

    /// <summary>Сигнатура действительно найдена по месту возбуждения, а не подставлена по умолчанию.</summary>
    public bool HasSignature { get; init; }

    /// <summary>Все найденные варианты сигнатуры с частотой, отсортированы по убыванию частоты.</summary>
    public IReadOnlyList<EventSignatureCandidate> SignatureCandidates { get; init; } = Array.Empty<EventSignatureCandidate>();

    /// <summary>Сколько раз в корпусе встречается <c>+=</c> на событие с таким именем.</summary>
    public int SubscriptionCount { get; init; }

    /// <summary>Сколько раз это имя события объявлено в корпусе (степень омонимии).</summary>
    public int HomonymCount { get; init; }

    /// <summary>
    /// Имя атрибута, по которому на событие подписываются. Для приватных событий это mangled-имя:
    /// <c>self.__x</c> внутри <c>class _Foo</c> доступно снаружи как <c>_Foo__x</c>.
    /// </summary>
    public string SubscriptionAttribute
    {
        get
        {
            if (Privacy == 0)
                return Name;

            if (Privacy == 1)
                return "_" + Name;

            string owner = (OwnerClass ?? string.Empty).TrimStart('_');
            return owner.Length == 0 ? "__" + Name : "_" + owner + "__" + Name;
        }
    }

    /// <summary>Человекочитаемый путь доступа для UI. Никогда не выдумывается: при неудаче — честная фраза.</summary>
    public string AccessPathDisplay => Resolution switch
    {
        OwnerResolution.Singleton => $"{ImportLine}  ->  {OwnerExpression}.{SubscriptionAttribute}",
        OwnerResolution.Nested => $"{ImportLine}  ->  {OwnerExpression}.{SubscriptionAttribute}",
        OwnerResolution.Service => $"dependency.instance({ServiceInterface})  # {ServiceModule}",
        OwnerResolution.BattleSlot => $"sessionProvider.{LocatorKind}.{SlotName}  # BATTLE_CTRL_ID.{ControllerId}",
        OwnerResolution.EntityAttribute => OwnerExpression ?? string.Empty,
        _ => "владелец достижим только изнутри клиента",
    };

    /// <summary>
    /// Предупреждение об исключениях: у <see cref="EventCtorKind.Event"/> исключение из обработчика
    /// всплывает в клиент и способно уронить бой, у <see cref="EventCtorKind.SafeEvent"/> — нет.
    /// </summary>
    public bool PropagatesHandlerExceptions => CtorKind != EventCtorKind.SafeEvent;
}

/// <summary>Вариант сигнатуры события с числом мест возбуждения, где он встретился.</summary>
/// <param name="Args">Имена аргументов.</param>
/// <param name="Count">Сколько мест возбуждения дали такой список.</param>
/// <param name="RelPath">Файл первого места возбуждения.</param>
/// <param name="Line">Строка первого места возбуждения.</param>
public sealed record EventSignatureCandidate(IReadOnlyList<string> Args, int Count, string RelPath, int Line);
