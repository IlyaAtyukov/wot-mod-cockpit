using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace WotModCockpit.App.Stage4;

/// <summary>
/// Тип контрола окна настроек ModsSettingsAPI.
/// <para>
/// Имена элементов совпадают СИМВОЛ В СИМВОЛ со строками поля <c>type</c> в шаблоне
/// (<c>COMPONENT_TYPE</c> из <c>_constants.py</c> версии 1.6.4), поэтому
/// <c>type.ToString()</c> — это и есть значение, которое уедет в <c>modsettings.dat</c>.
/// Регистр значим: окно диспетчеризует шаблон по точному совпадению строки, а на
/// неизвестном типе пишет в лог <c>Unexpected type of component</c> и подставляет пустышку.
/// </para>
/// </summary>
public enum SettingsControlType
{
    /// <summary>Распорка 400x20 без единого поля. В настройки не попадает.</summary>
    Empty = 0,

    /// <summary>Подпись-заголовок. Единственный способ разбить колонку на секции.</summary>
    Label = 1,

    /// <summary>Галочка. Значение — <c>bool</c>.</summary>
    CheckBox = 2,

    /// <summary>Радиокнопки списком. Значение — ИНДЕКС в <c>options</c>.</summary>
    RadioButtonGroup = 3,

    /// <summary>Выпадающий список. Значение — ИНДЕКС в <c>options</c>.</summary>
    Dropdown = 4,

    /// <summary>Ползунок. Значение — число в диапазоне.</summary>
    Slider = 5,

    /// <summary>Строка ввода. Значение — строка.</summary>
    TextInput = 6,

    /// <summary>Числовой степпер. Значение — число в диапазоне.</summary>
    NumericStepper = 7,

    /// <summary>Горячая клавиша. Значение — СПИСОК кодов клавиш BigWorld.</summary>
    HotKey = 8,

    /// <summary>Выбор цвета. Значение — шесть hex-цифр БЕЗ решётки.</summary>
    ColorChoice = 9,

    /// <summary>Диапазонный ползунок. Значение — пара чисел.</summary>
    RangeSlider = 10
}

/// <summary>Одна строка списка <c>options</c> у переключателей и выпадающего списка.</summary>
/// <param name="Label">Текст пункта. Единственное, что окно 1.6.4 из пункта читает.</param>
/// <param name="Tooltip">
/// Подсказка пункта. Фабрика её положит, но окно 1.6.4 её ИГНОРИРУЕТ
/// (в теле <c>createRadioButtonGroup</c> нет ни одного чтения <c>tooltip</c> у пункта).
/// Поле сохраняется ради совместимости с чужими шаблонами при импорте.
/// </param>
public sealed record SettingsOptionSpec(string Label, string Tooltip = "")
{
    /// <summary>
    /// Чужие ключи ВНУТРИ пункта, пришедшие импортом. Дизайнер их не порождает, но и не
    /// теряет: живой InfoPanel держит в каждом пункте <c>filename</c> и пользуется
    /// вариантом как носителем пути к скину — выбросить такой ключ значит сломать чужой мод
    /// и (без <c>settingsVersion</c>) сбросить настройки игрока, ведь <c>compareTemplates</c>
    /// сравнивает шаблоны как текст.
    /// <para>Значение хранится СЫРЫМ JSON-литералом (<c>"x"</c>, <c>3</c>, <c>true</c>),
    /// чтобы вместе с ключом не потерялся и его тип.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Extra { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>
/// Один компонент шаблона в том виде, в каком дизайнер отдаёт его генератору кода.
/// Неизменяемый снимок: модель представления живёт отдельно и меняется мышью.
/// </summary>
public sealed record SettingsControlSpec
{
    /// <summary>Тип компонента.</summary>
    public required SettingsControlType Type { get; init; }

    /// <summary>Подпись. Пусто у <see cref="SettingsControlType.Empty"/>.</summary>
    public string Text { get; init; } = string.Empty;

    /// <summary>Готовая строка подсказки (уже склеенная из <c>{HEADER}…{BODY}…</c>). Пусто — подсказки нет.</summary>
    public string Tooltip { get; init; } = string.Empty;

    /// <summary>Ключ в словаре настроек. Пусто у <c>Label</c> и <c>Empty</c>.</summary>
    public string VarName { get; init; } = string.Empty;

    /// <summary>
    /// Значение по умолчанию: <c>bool</c>, <c>long</c>, <c>double</c>, <c>string</c>,
    /// <c>IReadOnlyList&lt;int&gt;</c> (HotKey) либо <c>IReadOnlyList&lt;double&gt;</c> (RangeSlider).
    /// </summary>
    public object? Value { get; init; }

    /// <summary>Пункты списка. Непусто только у <c>RadioButtonGroup</c> и <c>Dropdown</c>.</summary>
    public IReadOnlyList<SettingsOptionSpec> Options { get; init; } = Array.Empty<SettingsOptionSpec>();

    /// <summary>Минимум диапазона (Slider, NumericStepper, RangeSlider).</summary>
    public double Minimum { get; init; }

    /// <summary>Максимум диапазона.</summary>
    public double Maximum { get; init; }

    /// <summary>Шаг. У окна это <c>snapInterval</c>.</summary>
    public double SnapInterval { get; init; } = 1;

    /// <summary>Формат метки ползунка. Все вхождения <c>{{value}}</c> заменяются значением.</summary>
    public string Format { get; init; } = "{{value}}";

    /// <summary>
    /// Ширина в пикселях. Поддержана ТОЛЬКО у <c>Dropdown</c>, <c>Slider</c> и <c>TextInput</c>;
    /// 0 означает «не писать в шаблон» — окно подставит своё умолчание 200.
    /// </summary>
    public int Width { get; init; }

    /// <summary>Разрешить ручной ввод числа в степпере (<c>canManualInput</c>).</summary>
    public bool CanManualInput { get; init; }

    /// <summary>RangeSlider: <c>divisionStep</c> — шаг делений шкалы.</summary>
    public double DivisionStep { get; init; } = 10;

    /// <summary>RangeSlider: <c>minRangeDistance</c> — минимальная ширина выбранного диапазона.</summary>
    public double MinRangeDistance { get; init; } = 1;

    /// <summary>RangeSlider: <c>divisionLabelStep</c> — через сколько делений подписывать шкалу.</summary>
    public double DivisionLabelStep { get; init; } = 10;

    /// <summary>RangeSlider: <c>divisionLabelPostfix</c> — приписка к подписям шкалы.</summary>
    public string DivisionLabelPostfix { get; init; } = string.Empty;

    /// <summary>
    /// Чужие ключи компонента, пришедшие импортом (<c>itemRenderer</c> у двух живых
    /// выпадающих списков). Дизайнер их НЕ создаёт, но и не теряет: без
    /// <c>settingsVersion</c> API сравнивает шаблоны как ТЕКСТ, и потерянный ключ —
    /// это молчаливый сброс настроек игрока.
    /// <para>Значение хранится СЫРЫМ JSON-литералом (<c>"x"</c>, <c>3</c>, <c>true</c>),
    /// чтобы вместе с ключом не потерялся и его тип.</para>
    /// </summary>
    public IReadOnlyDictionary<string, string> Unknown { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Имена числовых полей (<c>minimum</c>, <c>maximum</c>, <c>snapInterval</c>,
    /// <c>value</c>), которые обязаны печататься с дробной частью.
    ///
    /// <para>Причина — не косметика. Числа компонента живут здесь как <c>double</c>, и целое
    /// по значению <c>1.0</c> без этой пометки печаталось бы как <c>1</c>, то есть в Python 2.7
    /// становилось бы <c>int</c>: у ползунка «прозрачность» 0…1 с шагом 0,05 выражение
    /// <c>settings.get('opacity') / 2</c> давало бы 0 вместо 0.5, а тип значения скакал бы
    /// с <c>int</c> на <c>float</c> после первого «Применить». Обратное огрубление тоже
    /// недопустимо: в 13 живых шаблонах целые границы (<c>minimum 0</c>, <c>maximum 100</c>)
    /// хранятся именно как <c>int</c>, и приписка <c>.0</c> всем подряд изменила бы чужой
    /// шаблон.</para>
    /// </summary>
    public IReadOnlySet<string> FloatKeys { get; init; } = SettingsControlSpec.NoFloatKeys;

    /// <summary>Пустой набор — общий, чтобы не плодить объекты на каждый компонент.</summary>
    internal static readonly IReadOnlySet<string> NoFloatKeys =
        new HashSet<string>(StringComparer.Ordinal);
}

/// <summary>
/// Шаблон окна настроек целиком — корень формы. Соответствует одному ключу
/// в секции <c>templates</c> файла <c>modsettings.dat</c>.
/// </summary>
public sealed record SettingsFormSpec
{
    /// <summary>Ключ мода в состоянии API. Только ASCII-строка, неизменная после выпуска.</summary>
    public string Linkage { get; init; } = string.Empty;

    /// <summary>Заголовок блока мода в окне.</summary>
    public string ModDisplayName { get; init; } = string.Empty;

    /// <summary>
    /// Версия состава формы. Формально необязательна, фактически обязательна: без неё
    /// API сравнивает ВЕСЬ JSON шаблона и молча сбрасывает настройки игрока при любой правке.
    /// </summary>
    public int SettingsVersion { get; init; } = 1;

    /// <summary>Над блоком мода есть тумблер вкл/выкл (ключ <c>enabled</c> в шаблоне).</summary>
    public bool HasEnabled { get; init; } = true;

    /// <summary>Значение тумблера по умолчанию.</summary>
    public bool EnabledDefault { get; init; } = true;

    /// <summary>Левая колонка.</summary>
    public IReadOnlyList<SettingsControlSpec> Column1 { get; init; } = Array.Empty<SettingsControlSpec>();

    /// <summary>Правая колонка.</summary>
    public IReadOnlyList<SettingsControlSpec> Column2 { get; init; } = Array.Empty<SettingsControlSpec>();

    /// <summary>Все компоненты обеих колонок подряд.</summary>
    public IEnumerable<SettingsControlSpec> AllControls => Column1.Concat(Column2);
}

/// <summary>Насколько серьёзно замечание дизайнера.</summary>
public enum SettingsIssueSeverity
{
    /// <summary>Справка. Генерации не мешает.</summary>
    Info = 0,

    /// <summary>Форма соберётся, но поведёт себя не так, как ожидает автор.</summary>
    Warning = 1,

    /// <summary>Генерировать нельзя: окно сломается либо настройки потеряются.</summary>
    Error = 2
}

/// <summary>Одно замечание к форме.</summary>
/// <param name="Severity">Серьёзность.</param>
/// <param name="Message">Что не так и чем это кончится.</param>
public sealed record SettingsIssue(SettingsIssueSeverity Severity, string Message)
{
    /// <summary>Имя поля формы, к которому относится замечание (<c>linkage</c>, <c>varName</c>, …).</summary>
    public string Field { get; init; } = string.Empty;

    /// <summary>Индекс компонента в своей колонке; -1 — замечание уровня формы.</summary>
    public int ControlIndex { get; init; } = -1;

    /// <summary>Номер колонки компонента (1 или 2); 0 — замечание уровня формы.</summary>
    public int Column { get; init; }
}

/// <summary>Что генератор должен знать про проект, чтобы выдать пути и имена.</summary>
public sealed record SettingsCodeRequest
{
    /// <summary>Корень проекта мода.</summary>
    public string ProjectDir { get; init; } = string.Empty;

    /// <summary>Каталог исходников относительно корня (<c>src</c>).</summary>
    public string SrcDirRelative { get; init; } = "src";

    /// <summary>Имя python-пакета мода (<c>hello_tanker</c>).</summary>
    public string PackageName { get; init; } = string.Empty;

    /// <summary>Тег лога мода (<c>[HelloTanker</c> без скобки).</summary>
    public string LogTag { get; init; } = string.Empty;

    /// <summary>Идентификатор мода из meta.xml.</summary>
    public string ModId { get; init; } = string.Empty;

    /// <summary>
    /// Путь к своему JSON-конфигу ОТНОСИТЕЛЬНО корня игры
    /// (<c>mods/configs/&lt;пакет&gt;/settings.json</c>). Именно в него зеркалятся значения:
    /// ModsSettingsAPI при смене шаблона сбрасывает настройки игрока, и без зеркала
    /// каждое обновление мода стирало бы его выбор.
    /// </summary>
    public string ConfigRelativePath { get; init; } = string.Empty;

    /// <summary>
    /// Путь к КОНФИГУ МОДА из рецепта 09 (<c>ModProject.GameConfigRelative</c>), если он есть.
    /// <para>Нужен ровно для одной проверки: <see cref="ConfigRelativePath"/> не имеет права
    /// совпасть с ним. Модуль настроек переписывает свой файл ЦЕЛИКОМ при каждом «Применить»
    /// игрока — указав туда конфиг мода, кокпит стирал бы у игрока все ключи рецепта 09.
    /// Запрет живёт в самом шве, а не у одного вызывающего: охранка, до которой экран не
    /// доходит, — мёртвый код.</para>
    /// </summary>
    public string GameConfigRelative { get; init; } = string.Empty;

    /// <summary>Создавать ли пользовательский <c>settings.py</c> (только если его ещё нет).</summary>
    public bool CreateUserFile { get; init; } = true;
}

/// <summary>Что делать с файлом при записи.</summary>
public enum SettingsFileKind
{
    /// <summary>Полностью машинный файл: переписывается целиком при каждой генерации.</summary>
    Generated = 0,

    /// <summary>Создаётся ОДИН РАЗ и дальше принадлежит пользователю: перезаписывать нельзя.</summary>
    CreateOnce = 1,

    /// <summary>Источник истины дизайнера (<c>settings.design.json</c>).</summary>
    Design = 2
}

/// <summary>Один файл, который дизайнер предлагает записать в проект.</summary>
/// <param name="RelativePath">Путь относительно корня проекта, прямые слэши.</param>
/// <param name="Content">Содержимое целиком.</param>
/// <param name="Kind">Как с ним обращаться при записи.</param>
public sealed record SettingsCodeFile(string RelativePath, string Content, SettingsFileKind Kind)
{
    /// <summary>Одна строка про файл для списка предпросмотра.</summary>
    public string Note { get; init; } = string.Empty;

    /// <summary>
    /// Это ФРАГМЕНТ для показа и копирования, а не файл: экран не считает ему путь
    /// и никогда не пишет его в проект. Так показываются JSON шаблона и вызов
    /// <c>register()</c> для <c>main.py</c> — вставить его за человека нельзя
    /// (вызов обязан стоять внутри <c>onAccountShowGUI</c>, а дописывание в конец файла
    /// положило бы его на уровень модуля — ровно то, что запрещает WOT001).
    /// </summary>
    public bool IsSnippet { get; init; }

    /// <summary>Подсветка как у python-исходника (для <c>settings.design.json</c> — нет).</summary>
    public bool IsPython => RelativePath.EndsWith(".py", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Итог генерации.</summary>
/// <param name="Files">Файлы в порядке показа.</param>
/// <param name="Issues">Замечания генератора (сверх тех, что нашёл сам дизайнер).</param>
public sealed record SettingsCodeResult(
    IReadOnlyList<SettingsCodeFile> Files,
    IReadOnlyList<SettingsIssue> Issues)
{
    /// <summary>Почему сгенерировать не удалось. <c>null</c> — удалось.</summary>
    public string? Error { get; init; }

    /// <summary>Кто сгенерировал — показывается пользователю, чтобы «чей это код» не было загадкой.</summary>
    public string GeneratorName { get; init; } = string.Empty;

    /// <summary>Отказ.</summary>
    /// <param name="error">Причина.</param>
    /// <returns>Пустой результат с причиной.</returns>
    public static SettingsCodeResult Failed(string error) => new(
        Array.Empty<SettingsCodeFile>(),
        Array.Empty<SettingsIssue>())
    { Error = error };
}

/// <summary>Итог импорта формы.</summary>
/// <param name="Form">Разобранная форма либо <c>null</c>.</param>
/// <param name="Notes">Что стоит знать про импорт: чего не поняли, что переписали.</param>
public sealed record SettingsImportResult(SettingsFormSpec? Form, IReadOnlyList<string> Notes)
{
    /// <summary>Почему не получилось. <c>null</c> — получилось.</summary>
    public string? Error { get; init; }

    /// <summary>Откуда взяли (путь к файлу) — показывается пользователю.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>Отказ.</summary>
    /// <param name="error">Причина.</param>
    /// <returns>Пустой результат с причиной.</returns>
    public static SettingsImportResult Failed(string error) =>
        new(null, Array.Empty<string>()) { Error = error };
}

/// <summary>
/// ШОВ между экраном «Дизайнер настроек» и реализацией генератора/импортёра.
/// <para>
/// Причина та же, что у <see cref="Stage3.IPreflightBackend"/>: UI и генератор пишутся
/// параллельно. Экран обязан открываться, собирать форму, проверять её и показывать JSON
/// шаблона в любой момент этой работы. Код генерирует тот, кто зарегистрирован в контейнере;
/// пока настоящей реализации нет, работает встроенный генератор кокпита
/// (<see cref="BuiltInSettingsDesignerBackend"/>), который выдаёт ровно ту пару файлов,
/// что описана в спецификации этапа 4.
/// </para>
/// <para>
/// <b>Как подключить свою реализацию.</b> Зарегистрировать её в
/// <c>AppServices.BuildServiceProvider</c> ДО вызова <see cref="Stage4Registration.AddBackends"/>:
/// регистрация идёт через <c>TryAdd</c>, поэтому первая выигрывает.
/// </para>
/// </summary>
public interface ISettingsDesignerBackend
{
    /// <summary>Генератор подключён и им можно пользоваться.</summary>
    bool IsAvailable { get; }

    /// <summary>Почему генератор недоступен. Пусто, если <see cref="IsAvailable"/>.</summary>
    string UnavailableReason { get; }

    /// <summary>Имя генератора для показа пользователю.</summary>
    string Name { get; }

    /// <summary>
    /// Коды клавиш клиента для редактора горячей клавиши. Пустой список означает
    /// «своей таблицы нет» — экран возьмёт встроенную (<see cref="SettingsHotKey.KnownKeys"/>).
    /// </summary>
    IReadOnlyList<SettingsKeyCode> KnownKeys { get; }

    /// <summary>
    /// Дополнительные замечания к форме, которых UI сам знать не может.
    /// Свои правила (V1…V13) дизайнер проверяет сам и в реализацию не заглядывает.
    /// </summary>
    /// <param name="form">Форма.</param>
    /// <returns>Замечания; пустой список — возражений нет.</returns>
    IReadOnlyList<SettingsIssue> Validate(SettingsFormSpec form);

    /// <summary>
    /// Собрать код. Ничего не пишет на диск: запись — дело экрана, который сначала
    /// показывает предпросмотр и спрашивает подтверждение.
    /// </summary>
    /// <param name="form">Форма.</param>
    /// <param name="request">Паспорт проекта.</param>
    /// <returns>Файлы и замечания. Отказ — это <see cref="SettingsCodeResult.Error"/>, а не исключение.</returns>
    SettingsCodeResult Generate(SettingsFormSpec form, SettingsCodeRequest request);

    /// <summary>Шаблон в том виде, в каком он уедет в <c>modsettings.dat</c>.</summary>
    /// <param name="form">Форма.</param>
    /// <returns>JSON либо пустая строка, если реализация этого не умеет.</returns>
    string RenderTemplateJson(SettingsFormSpec form);

    /// <summary>
    /// Прочитать форму из проекта: сначала <c>settings.design.json</c>, затем — если
    /// реализация это умеет — разбором кода мода.
    /// </summary>
    /// <param name="projectDir">Корень проекта.</param>
    /// <returns>Итог; <see cref="SettingsImportResult.Error"/> при отказе.</returns>
    SettingsImportResult ImportFromProject(string projectDir);
}

/// <summary>Код клавиши клиента для выпадающего списка редактора горячей клавиши.</summary>
/// <param name="Code">Код BigWorld (значение <c>KEY_*</c> из <c>client/Keys.py</c>).</param>
/// <param name="Name">Имя константы (<c>KEY_F3</c>).</param>
/// <param name="Label">Как показывать человеку (<c>F3</c>).</param>
public sealed record SettingsKeyCode(int Code, string Name, string Label)
{
    /// <summary>Строка для списка: <c>F3 · KEY_F3 · 61</c>.</summary>
    public string Display => $"{Label}   ({Name} = {Code})";
}

/// <summary>
/// «Генератор не подключён». Экран с ним открывается, форму собирает и проверяет,
/// но кода не выдаёт. В боевом контейнере не используется — там стоит
/// <see cref="BuiltInSettingsDesignerBackend"/>; класс нужен тестам и тому случаю,
/// когда генерацию надо осознанно выключить.
/// </summary>
public sealed class UnavailableSettingsDesignerBackend : ISettingsDesignerBackend
{
    private const string Reason = "генератор кода настроек не подключён (ISettingsDesignerBackend)";

    /// <inheritdoc />
    public bool IsAvailable => false;

    /// <inheritdoc />
    public string UnavailableReason => Reason;

    /// <inheritdoc />
    public string Name => "нет генератора";

    /// <inheritdoc />
    public IReadOnlyList<SettingsKeyCode> KnownKeys => Array.Empty<SettingsKeyCode>();

    /// <inheritdoc />
    public IReadOnlyList<SettingsIssue> Validate(SettingsFormSpec form) => Array.Empty<SettingsIssue>();

    /// <inheritdoc />
    public SettingsCodeResult Generate(SettingsFormSpec form, SettingsCodeRequest request)
        => SettingsCodeResult.Failed(Reason);

    /// <inheritdoc />
    public string RenderTemplateJson(SettingsFormSpec form) => string.Empty;

    /// <inheritdoc />
    public SettingsImportResult ImportFromProject(string projectDir) => SettingsImportResult.Failed(Reason);
}

/// <summary>Регистрация швов этапа 4.</summary>
public static class Stage4Registration
{
    /// <summary>
    /// Добавляет шов дизайнера настроек. Через <c>TryAdd</c>: если настоящую реализацию
    /// уже зарегистрировали выше, она и останется.
    /// </summary>
    /// <param name="services">Коллекция сервисов приложения.</param>
    public static void AddBackends(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ISettingsDesignerBackend, BuiltInSettingsDesignerBackend>();
    }
}
