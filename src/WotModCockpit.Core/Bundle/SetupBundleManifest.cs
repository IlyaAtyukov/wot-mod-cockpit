using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotModCockpit.Core.Bundle;

/// <summary>
/// Описание сборки — <c>bundle.json</c> внутри сетапа.
/// <para>
/// Имена полей — camelCase, и это контракт с заглушкой: она читает файл
/// <c>DataContractJsonSerializer</c>'ом .NET Framework по тем же именам
/// (<c>src\WotModCockpit.Setup\Bundle.cs</c>). Время — строкой ISO 8601: формат дат этого
/// сериализатора (<c>\/Date(…)\/</c>) с System.Text.Json несовместим.
/// </para>
/// </summary>
public sealed record SetupBundleManifest
{
    /// <summary>Версия формата.</summary>
    public int Format { get; init; } = SetupBundleFormat.FormatVersion;

    /// <summary>Имя сборки — заголовок окна сетапа.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Версия сборки, обычно дата.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Когда собрана, ISO 8601 UTC.</summary>
    public string CreatedUtc { get; init; } = string.Empty;

    /// <summary>Версия клиента, под которую собиралась сборка; пусто — неизвестна.</summary>
    public string GameVersion { get; init; } = string.Empty;

    /// <summary>Моды сборки в порядке показа.</summary>
    public IReadOnlyList<SetupBundleComponent> Components { get; init; } = Array.Empty<SetupBundleComponent>();

    /// <summary>Настройки JSON: camelCase, отступы, кириллица как есть.</summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Текст <c>bundle.json</c>.</summary>
    /// <returns>JSON.</returns>
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Разбирает <c>bundle.json</c>.</summary>
    /// <param name="json">Текст.</param>
    /// <returns>Описание сборки.</returns>
    /// <exception cref="SetupBundleException">Текст не разбирается.</exception>
    public static SetupBundleManifest FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<SetupBundleManifest>(json, JsonOptions)
                   ?? throw new SetupBundleException("описание сборки пустое");
        }
        catch (JsonException ex)
        {
            throw new SetupBundleException("описание сборки не разбирается: " + ex.Message, ex);
        }
    }
}

/// <summary>Один мод сборки.</summary>
public sealed record SetupBundleComponent
{
    /// <summary><c>&lt;id&gt;</c> из meta.xml мода.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Имя для списка в сетапе.</summary>
    public string Name { get; init; } = string.Empty;

    /// <summary>Версия мода.</summary>
    public string Version { get; init; } = string.Empty;

    /// <summary>Описание — показывается под списком.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>Путь внутри ZIP: <c>mods/&lt;имя файла&gt;.mtmod</c>.</summary>
    public string File { get; init; } = string.Empty;

    /// <summary>Размер файла мода.</summary>
    public long Size { get; init; }

    /// <summary>SHA-256 файла мода — заглушка сверяет его перед установкой.</summary>
    public string Sha256 { get; init; } = string.Empty;

    /// <summary>Имя файла, под которым мод ляжет в игру.</summary>
    [JsonIgnore]
    public string FileName => File.StartsWith(SetupBundleFormat.ModsFolder, StringComparison.Ordinal)
        ? File[SetupBundleFormat.ModsFolder.Length..]
        : Path.GetFileName(File);
}
