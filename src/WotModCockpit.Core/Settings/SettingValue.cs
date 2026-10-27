using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WotModCockpit.Core.Settings;

/// <summary>Какого рода значение лежит в <see cref="SettingValue"/>.</summary>
public enum SettingValueKind
{
    /// <summary><c>None</c> в Python, <c>null</c> в JSON.</summary>
    Null = 0,

    /// <summary><c>True</c>/<c>False</c>.</summary>
    Bool,

    /// <summary>Целое. Отделено от <see cref="Number"/> НАМЕРЕННО, см. класс.</summary>
    Integer,

    /// <summary>Дробное.</summary>
    Number,

    /// <summary>Строка (в Python — <c>unicode</c>).</summary>
    String,

    /// <summary>Список.</summary>
    List,

    /// <summary>Словарь.</summary>
    Map
}

/// <summary>
/// Одно значение из шаблона ModsSettingsAPI: то, что лежит в ключах <c>value</c>,
/// <c>minimum</c>, <c>snapInterval</c> и им подобных.
///
/// <para><b>Почему не <c>object?</c> и почему целое отделено от дробного.</b>
/// Шаблон уезжает в <c>modsettings.dat</c> обычным <c>json.dumps</c>, а
/// <c>api.compareTemplates</c> при отсутствии <c>settingsVersion</c> сравнивает
/// ШАБЛОНЫ КАК ТЕКСТ (<c>jsonDump(new) != jsonDump(old)</c>). В Python 2
/// <c>json.dumps(3)</c> даёт <c>3</c>, а <c>json.dumps(3.0)</c> — <c>3.0</c>.
/// Значит модель, которая свела бы оба к <c>double</c>, при обратной записи
/// поменяла бы текст шаблона и ТИХО СБРОСИЛА БЫ НАСТРОЙКИ ИГРОКА. Ползунок масштаба вида
/// <c>minimum 0.8 / maximum 3.0 / snapInterval 0.01</c>, какие встречаются у живых модов, —
/// и это ловится сразу.</para>
///
/// <para>По той же причине хранится исходный текст числа (<see cref="RawText"/>):
/// импорт-экспорт обязан возвращать байт в байт то, что было.</para>
/// </summary>
public sealed class SettingValue : IEquatable<SettingValue>
{
    private static readonly IReadOnlyList<SettingValue> NoItems = Array.Empty<SettingValue>();

    private static readonly IReadOnlyList<KeyValuePair<string, SettingValue>> NoEntries =
        Array.Empty<KeyValuePair<string, SettingValue>>();

    private readonly bool _bool;
    private readonly long _integer;
    private readonly double _number;
    private readonly string? _string;
    private readonly IReadOnlyList<SettingValue>? _items;
    private readonly IReadOnlyList<KeyValuePair<string, SettingValue>>? _entries;

    private SettingValue(
        SettingValueKind kind,
        bool b = false,
        long i = 0,
        double d = 0,
        string? s = null,
        IReadOnlyList<SettingValue>? items = null,
        IReadOnlyList<KeyValuePair<string, SettingValue>>? entries = null,
        string? raw = null)
    {
        Kind = kind;
        _bool = b;
        _integer = i;
        _number = d;
        _string = s;
        _items = items;
        _entries = entries;
        RawText = raw;
    }

    /// <summary>Что именно тут лежит.</summary>
    public SettingValueKind Kind { get; }

    /// <summary>
    /// Исходный текст числа, если значение пришло разбором (<c>"3.0"</c>, <c>"0.01"</c>).
    /// При записи используется он — так шаблон возвращается байт в байт.
    /// </summary>
    public string? RawText { get; }

    /// <summary>Единственный <c>null</c>.</summary>
    public static SettingValue Null { get; } = new(SettingValueKind.Null);

    /// <summary><c>True</c>.</summary>
    public static SettingValue True { get; } = new(SettingValueKind.Bool, b: true);

    /// <summary><c>False</c>.</summary>
    public static SettingValue False { get; } = new(SettingValueKind.Bool, b: false);

    /// <summary>Логическое значение.</summary>
    /// <param name="value">Значение.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue Of(bool value) => value ? True : False;

    /// <summary>Целое.</summary>
    /// <param name="value">Значение.</param>
    /// <param name="raw">Исходный текст, если значение пришло разбором.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue Of(long value, string? raw = null) =>
        new(SettingValueKind.Integer, i: value, d: value, raw: raw);

    /// <summary>
    /// Дробное. Без <paramref name="raw"/> печатается так, чтобы ВСЕГДА иметь точку
    /// (<c>3</c> -> <c>3.0</c>) — как это делает <c>repr</c> питоновского float.
    /// </summary>
    /// <param name="value">Значение.</param>
    /// <param name="raw">Исходный текст, если значение пришло разбором.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue Of(double value, string? raw = null) =>
        new(SettingValueKind.Number, d: value, i: (long)value, raw: raw);

    /// <summary>Строка. <c>null</c> даёт <see cref="Null"/>.</summary>
    /// <param name="value">Значение.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue Of(string? value) =>
        value is null ? Null : new SettingValue(SettingValueKind.String, s: value);

    /// <summary>Список.</summary>
    /// <param name="items">Элементы.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue List(IEnumerable<SettingValue> items) =>
        new(SettingValueKind.List, items: items.ToArray());

    /// <summary>Список целых — самая частая форма (значение <c>HotKey</c>).</summary>
    /// <param name="items">Коды.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue List(params long[] items) =>
        new(SettingValueKind.List, items: items.Select(x => Of(x)).ToArray());

    /// <summary>Словарь. Порядок ключей сохраняется.</summary>
    /// <param name="entries">Пары.</param>
    /// <returns>Обёртка.</returns>
    public static SettingValue Map(IEnumerable<KeyValuePair<string, SettingValue>> entries) =>
        new(SettingValueKind.Map, entries: entries.ToArray());

    /// <summary>Значение — число (целое или дробное).</summary>
    public bool IsNumeric => Kind is SettingValueKind.Integer or SettingValueKind.Number;

    /// <summary>Логическое значение; для остальных типов — <c>false</c>.</summary>
    public bool AsBool => Kind == SettingValueKind.Bool && _bool;

    /// <summary>Число как <see cref="double"/>; для нечисловых — 0.</summary>
    public double AsNumber => IsNumeric ? (Kind == SettingValueKind.Integer ? _integer : _number) : 0d;

    /// <summary>Целое; для дробного — усечение, для нечислового — 0.</summary>
    public long AsInteger => Kind == SettingValueKind.Integer ? _integer : IsNumeric ? (long)_number : 0L;

    /// <summary>Строка; для остальных типов — <c>null</c>.</summary>
    public string? AsString => Kind == SettingValueKind.String ? _string : null;

    /// <summary>Элементы списка; для остальных типов — пусто.</summary>
    public IReadOnlyList<SettingValue> Items => _items ?? NoItems;

    /// <summary>Пары словаря в исходном порядке; для остальных типов — пусто.</summary>
    public IReadOnlyList<KeyValuePair<string, SettingValue>> Entries => _entries ?? NoEntries;

    /// <summary>Значение по ключу словаря.</summary>
    /// <param name="key">Ключ.</param>
    /// <returns>Значение или <c>null</c>, если ключа нет либо это не словарь.</returns>
    public SettingValue? Get(string key)
    {
        foreach (KeyValuePair<string, SettingValue> e in Entries)
            if (string.Equals(e.Key, key, StringComparison.Ordinal))
                return e.Value;
        return null;
    }

    /// <summary>Есть ли такой ключ в словаре.</summary>
    /// <param name="key">Ключ.</param>
    /// <returns><c>true</c>, если есть.</returns>
    public bool Has(string key) => Get(key) is not null;

    /// <summary>Значение целиком целочисленное (в том числе дробное без дробной части).</summary>
    public bool IsIntegral => Kind == SettingValueKind.Integer ||
                              (Kind == SettingValueKind.Number && Math.Abs(_number % 1d) < double.Epsilon);

    // ------------------------------------------------------------------ разбор

    /// <summary>Перевести узел <c>System.Text.Json</c> в значение модели.</summary>
    /// <param name="element">Узел.</param>
    /// <returns>Значение.</returns>
    /// <exception cref="NotSupportedException">Узел неизвестного вида.</exception>
    public static SettingValue FromJson(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return Null;
            case JsonValueKind.True:
                return True;
            case JsonValueKind.False:
                return False;
            case JsonValueKind.String:
                return Of(element.GetString());
            case JsonValueKind.Number:
                {
                    string raw = element.GetRawText();
                    // TryGetInt64 строг к форме: "3" проходит, "3.0" — нет.
                    // Ровно это различие и надо сохранить.
                    return element.TryGetInt64(out long l) ? Of(l, raw) : Of(element.GetDouble(), raw);
                }
            case JsonValueKind.Array:
                {
                    var items = new List<SettingValue>();
                    foreach (JsonElement item in element.EnumerateArray()) items.Add(FromJson(item));
                    return new SettingValue(SettingValueKind.List, items: items);
                }
            case JsonValueKind.Object:
                {
                    var entries = new List<KeyValuePair<string, SettingValue>>();
                    foreach (JsonProperty p in element.EnumerateObject())
                        entries.Add(new KeyValuePair<string, SettingValue>(p.Name, FromJson(p.Value)));
                    return new SettingValue(SettingValueKind.Map, entries: entries);
                }
            default:
                throw new NotSupportedException($"неизвестный вид узла JSON: {element.ValueKind}");
        }
    }

    /// <summary>Разобрать текст JSON.</summary>
    /// <param name="json">Текст.</param>
    /// <returns>Значение.</returns>
    public static SettingValue ParseJson(string json)
    {
        using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip,
            MaxDepth = 64
        });
        return FromJson(doc.RootElement.Clone());
    }

    // ------------------------------------------------------------------ запись

    /// <summary>Записать значение в поток JSON.</summary>
    /// <param name="writer">Писатель.</param>
    /// <param name="sortKeys">Сортировать ключи словарей (как <c>json.dumps(sort_keys=True)</c> у API).</param>
    public void WriteTo(Utf8JsonWriter writer, bool sortKeys = false)
    {
        ArgumentNullException.ThrowIfNull(writer);
        switch (Kind)
        {
            case SettingValueKind.Null:
                writer.WriteNullValue();
                break;
            case SettingValueKind.Bool:
                writer.WriteBooleanValue(_bool);
                break;
            case SettingValueKind.Integer:
            case SettingValueKind.Number:
                writer.WriteRawValue(NumberText(), skipInputValidation: true);
                break;
            case SettingValueKind.String:
                writer.WriteStringValue(_string);
                break;
            case SettingValueKind.List:
                writer.WriteStartArray();
                foreach (SettingValue item in Items) item.WriteTo(writer, sortKeys);
                writer.WriteEndArray();
                break;
            case SettingValueKind.Map:
                writer.WriteStartObject();
                IEnumerable<KeyValuePair<string, SettingValue>> entries = sortKeys
                    ? Entries.OrderBy(e => e.Key, StringComparer.Ordinal)
                    : Entries;
                foreach (KeyValuePair<string, SettingValue> e in entries)
                {
                    writer.WritePropertyName(e.Key);
                    e.Value.WriteTo(writer, sortKeys);
                }
                writer.WriteEndObject();
                break;
            default:
                throw new NotSupportedException($"нечего писать для {Kind}");
        }
    }

    /// <summary>Текст числа ровно в том виде, в каком его напечатал бы Python.</summary>
    /// <returns>Текст.</returns>
    public string NumberText()
    {
        if (RawText is { Length: > 0 }) return RawText;
        if (Kind == SettingValueKind.Integer) return _integer.ToString(CultureInfo.InvariantCulture);
        string text = _number.ToString("R", CultureInfo.InvariantCulture);
        // repr(3.0) в Python — '3.0', а "R" в .NET даёт "3". Дописываем дробную часть,
        // иначе тип числа в шаблоне поменяется и compareTemplates увидит различие.
        if (text.IndexOf('.') < 0 && text.IndexOf('e') < 0 && text.IndexOf('E') < 0 &&
            text.IndexOf('N') < 0 && text.IndexOf('I') < 0)
        {
            text += ".0";
        }
        return text;
    }

    /// <summary>JSON без переносов строк, с исходным порядком ключей.</summary>
    /// <returns>Текст JSON.</returns>
    public string ToJson() => ToJson(indented: false, sortKeys: false);

    /// <summary>
    /// Канонический JSON: ключи отсортированы, пробелов нет. Именно в таком виде
    /// шаблоны сравнимы между собой — API сериализует их с <c>sort_keys=True</c>.
    /// </summary>
    /// <returns>Текст JSON.</returns>
    public string ToCanonicalJson() => ToJson(indented: false, sortKeys: true);

    /// <summary>JSON с управлением форматом.</summary>
    /// <param name="indented">С отступами.</param>
    /// <param name="sortKeys">С сортировкой ключей.</param>
    /// <returns>Текст JSON.</returns>
    public string ToJson(bool indented, bool sortKeys)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = indented,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            SkipValidation = false
        }))
        {
            WriteTo(writer, sortKeys);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <inheritdoc/>
    public override string ToString() => ToJson();

    /// <inheritdoc/>
    public bool Equals(SettingValue? other) =>
        other is not null && string.Equals(ToCanonicalJson(), other.ToCanonicalJson(), StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as SettingValue);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToCanonicalJson());
}
