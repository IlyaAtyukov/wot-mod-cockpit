using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WotModCockpit.App.Stage4;

/// <summary>Один мод из живого состояния ModsSettingsAPI.</summary>
/// <param name="Linkage">Ключ мода.</param>
/// <param name="DisplayName">Заголовок блока в окне настроек.</param>
/// <param name="ControlCount">Сколько компонентов в обеих колонках.</param>
/// <param name="HasSettingsVersion">У шаблона есть <c>settingsVersion</c>.</param>
public sealed record LiveModTemplate(string Linkage, string DisplayName, int ControlCount, bool HasSettingsVersion)
{
    /// <summary>Сам шаблон. Нужен импорту; наружу как модель дизайнера превращается отдельно.</summary>
    internal JsonObject? Template { get; init; }

    /// <summary>Строка для списка.</summary>
    public string Display => $"{DisplayName}   ·   {Linkage}   ·   контролов: {ControlCount}"
                             + (HasSettingsVersion ? string.Empty : "   ·   без settingsVersion");
}

/// <summary>
/// ЧИТАТЕЛЬ живого состояния ModsSettingsAPI — файла
/// <c>%APPDATA%\Lesta\MirTankov\mods\modsettings.dat</c>.
/// <para>
/// <b>Только чтение.</b> В этом файле лежат НАСТОЙКИ ИГРОКА для всех установленных модов;
/// испортить его — значит обнулить чужие настройки. Класс открывает файл на чтение
/// с общим доступом и не имеет ни одного метода записи. Кокпиту он нужен ради двух вещей:
/// подсмотреть, как устроены рабочие шаблоны чужих модов, и не дать назначить горячую
/// клавишу, которую уже занял другой мод.
/// </para>
/// <para>
/// Формат — обычный JSON с тремя ключами верхнего уровня: <c>templates</c> (как задумал
/// автор мода), <c>settings</c> (что выбрал игрок) и <c>storage</c> (произвольные данные
/// модов; у пользователя пуста и по коду API непригодна к использованию).
/// </para>
/// </summary>
public sealed class LiveModSettingsReader
{
    /// <summary>Путь к файлу состояния относительно каталога настроек игры.</summary>
    private const string Relative = @"mods\modsettings.dat";

    /// <summary>
    /// Где искать файл. Первый — «Мир танков» (Lesta), второй — клиент Wargaming:
    /// у обоих один и тот же ModsSettingsAPI и один и тот же формат состояния.
    /// </summary>
    private static readonly string[] Profiles =
    {
        @"Lesta\MirTankov",
        @"Wargaming.net\WorldOfTanks"
    };

    /// <summary>Найденный путь к файлу либо пустая строка.</summary>
    /// <returns>Абсолютный путь.</returns>
    public static string FindDefaultPath()
    {
        string appData;
        try
        {
            appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        catch (Exception)
        {
            return string.Empty;
        }

        if (appData.Length == 0)
        {
            return string.Empty;
        }

        foreach (string profile in Profiles)
        {
            string candidate = Path.Combine(appData, profile, Relative);
            try
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (Exception)
            {
                // Профиль мог оказаться недоступен — просто пробуем следующий.
            }
        }

        return string.Empty;
    }

    /// <summary>Шаблоны, прочитанные последним вызовом <see cref="Read"/>.</summary>
    public IReadOnlyList<LiveModTemplate> Templates { get; private set; } = Array.Empty<LiveModTemplate>();

    /// <summary>Путь, с которого читали.</summary>
    public string FilePath { get; private set; } = string.Empty;

    /// <summary>Почему прочитать не удалось. Пусто — удалось.</summary>
    public string Error { get; private set; } = string.Empty;

    /// <summary>
    /// Горячие клавиши, уже занятые установленными модами: <c>varName мода</c> → сочетание.
    /// Собирается из секции <c>settings</c>, потому что именно там лежит ВЫБОР игрока,
    /// а не то, что предложил автор.
    /// </summary>
    public IReadOnlyList<(string Linkage, string VarName, IReadOnlyList<int> Keys)> BusyHotKeys { get; private set; }
        = Array.Empty<(string, string, IReadOnlyList<int>)>();

    /// <summary>
    /// Читает файл состояния. Ничего не пишет и не создаёт.
    /// </summary>
    /// <param name="path">Путь; пусто — искать самому.</param>
    /// <returns>true, если файл прочитан.</returns>
    public bool Read(string? path = null)
    {
        Templates = Array.Empty<LiveModTemplate>();
        BusyHotKeys = Array.Empty<(string, string, IReadOnlyList<int>)>();
        Error = string.Empty;

        FilePath = string.IsNullOrWhiteSpace(path) ? FindDefaultPath() : path!;

        if (FilePath.Length == 0)
        {
            Error = "файл modsettings.dat не найден: ModsSettingsAPI ни разу не запускался "
                    + "либо игра стоит под другим профилем";
            return false;
        }

        string text;
        try
        {
            // FileShare.ReadWrite: клиент игры может держать файл открытым.
            using var stream = new FileStream(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Error = "modsettings.dat не прочитан: " + ex.Message;
            return false;
        }

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
        }
        catch (JsonException ex)
        {
            Error = "modsettings.dat не разобран как JSON: " + ex.Message;
            return false;
        }

        if (root is not JsonObject obj)
        {
            Error = "modsettings.dat пуст";
            return false;
        }

        var templates = new List<LiveModTemplate>();
        if (obj["templates"] is JsonObject section)
        {
            foreach (KeyValuePair<string, JsonNode?> pair in section)
            {
                if (pair.Value is not JsonObject template)
                {
                    continue;
                }

                int count = Count(template["column1"]) + Count(template["column2"]);

                templates.Add(new LiveModTemplate(
                    pair.Key,
                    DisplayNameOf(template, pair.Key),
                    count,
                    template["settingsVersion"] is not null)
                {
                    Template = template
                });
            }
        }

        templates.Sort(static (a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        Templates = templates;

        BusyHotKeys = CollectHotKeys(obj);
        return true;
    }

    /// <summary>Превращает выбранный живой шаблон в форму дизайнера.</summary>
    /// <param name="template">Строка списка.</param>
    /// <returns>Итог импорта.</returns>
    public SettingsImportResult ToForm(LiveModTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);

        if (template.Template is null)
        {
            return SettingsImportResult.Failed("шаблон не прочитан");
        }

        var notes = new List<string>
        {
            "это ЧУЖОЙ шаблон из настроек игрока; кокпит прочитал его и ничего не менял в файле"
        };

        SettingsFormSpec form = SettingsDesignFile.FromLiveTemplate(template.Template, template.Linkage, notes);

        return new SettingsImportResult(form, notes) { Source = FilePath };
    }

    private static int Count(JsonNode? column) => column is JsonArray array ? array.Count : 0;

    /// <summary>
    /// Заголовок блока мода из чужого шаблона.
    /// <para>
    /// Читается ЧЕРЕЗ ПРОВЕРКУ ТИПА, а не через <c>GetValue&lt;string&gt;()</c>: файл пишут
    /// тринадцать чужих модов и сам API, и в повреждённом состоянии на месте строки может
    /// оказаться число, <c>true</c> или вложенный объект. <c>GetValue&lt;string&gt;()</c> на
    /// таком узле бросает <see cref="InvalidOperationException"/> — то есть чтение всего
    /// живого состояния падало бы из-за одного испорченного мода, хотя <see cref="Read"/>
    /// обязан о беде ДОКЛАДЫВАТЬ, а не бросать. Не строка — берём ключ мода: он есть всегда.
    /// </para>
    /// </summary>
    private static string DisplayNameOf(JsonObject template, string fallback)
    {
        if (template["modDisplayName"] is JsonValue value
            && value.GetValueKind() == JsonValueKind.String
            && value.TryGetValue(out string? name)
            && !string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return fallback;
    }

    private static IReadOnlyList<(string, string, IReadOnlyList<int>)> CollectHotKeys(JsonObject root)
    {
        var busy = new List<(string, string, IReadOnlyList<int>)>();

        if (root["templates"] is not JsonObject templates || root["settings"] is not JsonObject settings)
        {
            return busy;
        }

        foreach (KeyValuePair<string, JsonNode?> pair in templates)
        {
            if (pair.Value is not JsonObject template || settings[pair.Key] is not JsonObject modSettings)
            {
                continue;
            }

            foreach (string column in new[] { "column1", "column2" })
            {
                if (template[column] is not JsonArray array)
                {
                    continue;
                }

                foreach (JsonNode? node in array)
                {
                    if (node is not JsonObject item
                        || item["type"]?.ToString() != nameof(SettingsControlType.HotKey))
                    {
                        continue;
                    }

                    string varName = item["varName"]?.ToString() ?? string.Empty;
                    if (varName.Length == 0)
                    {
                        continue;
                    }

                    // Значение игрока важнее авторского: именно оно сейчас занимает клавишу.
                    JsonNode? value = modSettings[varName] ?? item["value"];
                    IReadOnlyList<int> keys = SettingsHotKey.Flatten(Flat(value));

                    if (keys.Count > 0)
                    {
                        busy.Add((pair.Key, varName, keys));
                    }
                }
            }
        }

        return busy;
    }

    private static IEnumerable<object?> Flat(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            yield break;
        }

        foreach (JsonNode? item in array)
        {
            if (item is JsonArray nested)
            {
                yield return Flat(nested).ToList();
            }
            else if (item is not null && item.GetValueKind() == JsonValueKind.Number)
            {
                yield return item.GetValue<double>();
            }
        }
    }
}
