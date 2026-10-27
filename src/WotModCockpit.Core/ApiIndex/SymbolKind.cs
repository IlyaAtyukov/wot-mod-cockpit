namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Вид символа в индексе API клиента.
/// <para>
/// Ключевое отличие <see cref="Function"/> от <see cref="Method"/> — только отступ и стек родителей:
/// AST здесь недоступен (см. <see cref="PythonSymbolScanner"/>), поэтому вид выводится лексически.
/// </para>
/// </summary>
public enum SymbolKind : byte
{
    /// <summary>Файл-модуль целиком. Имя = имя файла без <c>.py</c> (для <c>__init__.py</c> — имя пакета).</summary>
    Module = 0,

    /// <summary>Объявление <c>class X</c> (а также <c>X = namedtuple(...)</c> и <c>X = type(...)</c>, см. <see cref="SymbolOrigin"/>).</summary>
    Class = 1,

    /// <summary>Функция уровня модуля: <c>def</c> без объемлющего <c>class</c>/<c>def</c>.</summary>
    Function = 2,

    /// <summary>Метод: <c>def</c> внутри <c>class</c>.</summary>
    Method = 3,

    /// <summary>Функция внутри функции. Целью поиска почти никогда не бывает, ранжируется вниз.</summary>
    NestedFunction = 4,

    /// <summary>
    /// Событие клиента: <c>self.onX = Event.Event()</c>. Индексом <c>def</c>/<c>class</c> НЕ ловится
    /// (у <c>onArenaPeriodChange</c> ноль объявлений как def/class при 32 подписках), поэтому это
    /// отдельный вид символа — и именно он отвечает на вопрос «что мне перехватить».
    /// </summary>
    Event = 5,

    /// <summary>Константа: присваивание <c>ALL_CAPS = ...</c>. Индексируется только при
    /// <see cref="ApiIndexOptions.IndexConstants"/>.</summary>
    Constant = 6,

    /// <summary>
    /// Модульный синглтон: <c>g_playerEvents = _PlayerEvents()</c> на нулевом отступе (142 штуки).
    /// Именно через него мод и подписывается на события, при том что <c>def</c>/<c>class</c>
    /// с таким именем в корпусе нет вовсе.
    /// <para>
    /// Интерфейс сервиса из <c>client/skeletons/**</c> этим видом НЕ помечается: он остаётся
    /// обычным <see cref="Class"/>, а признак сервиса выводится из пути файла
    /// (<see cref="ApiSymbol.IsServiceInterface"/>).
    /// </para>
    /// </summary>
    Service = 7
}

/// <summary>
/// Откуда взялся символ. Нужен, чтобы отличать настоящее ключевое слово от синтезированного
/// символа: эталонные числа корпуса (117 901 <c>def</c> и 38 809 <c>class</c>) считаются
/// только по <see cref="Keyword"/>.
/// </summary>
public enum SymbolOrigin : byte
{
    /// <summary>Ключевое слово <c>def</c> или <c>class</c>.</summary>
    Keyword = 0,

    /// <summary><c>X = namedtuple('X', ...)</c>. В корпусе 559 штук — в 7 раз чаще, чем <c>type()</c>.</summary>
    NamedTuple = 1,

    /// <summary><c>X = type('X', bases, dict)</c> — динамическое создание типа, 82 штуки.</summary>
    TypeCall = 2,

    /// <summary><c>self.onX = Event.Event()</c> и его разновидности.</summary>
    EventAttribute = 3,

    /// <summary>Присваивание константы <c>ALL_CAPS = ...</c>.</summary>
    Assignment = 4,

    /// <summary>Символ-модуль, синтезированный из имени файла.</summary>
    ModuleFile = 5
}

/// <summary>
/// Декораторы перед <c>def</c>/<c>class</c>, свёрнутые в флаги.
/// Практическая ценность: <c>@property</c> и <c>@staticmethod</c> напрямую меняют способ монкипатча
/// (в корпусе 5445 и 2380 соответственно), поэтому их надо видеть прямо в выдаче поиска.
/// </summary>
[Flags]
public enum DecoratorFlags : ushort
{
    /// <summary>Декораторов не было.</summary>
    None = 0,

    /// <summary><c>@staticmethod</c> (2380 в корпусе).</summary>
    Static = 1,

    /// <summary><c>@classmethod</c> (2924).</summary>
    ClassMethod = 2,

    /// <summary><c>@property</c> (5445) — включая <c>@x.setter</c>/<c>@x.deleter</c>.</summary>
    Property = 4,

    /// <summary><c>@abstractmethod</c> (147) — почти всегда интерфейс из <c>skeletons/</c>.</summary>
    Abstract = 8,

    /// <summary><c>@adisp_process</c>, <c>@adisp_async</c>, <c>@th_async</c> — асинхронный вызов клиента.</summary>
    Async = 16,

    /// <summary><c>@dependency.replace_none_kwargs</c> — сервис подставляется в аргументы (393).</summary>
    DependencyKwargs = 32,

    /// <summary>Любой другой декоратор (в корпусе 314 различных видов).</summary>
    Other = 64
}
