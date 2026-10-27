using System.Text.Json;
using System.Text.Json.Serialization;

namespace WotModCockpit.Core.Models;

/// <summary>Строгость замечания по проекту.</summary>
public enum ValidationSeverity
{
    /// <summary>Замечание к сведению, работать не мешает.</summary>
    Info = 0,

    /// <summary>Подозрительно (например, wotmod.json разошёлся с meta.xml), но собрать можно.</summary>
    Warning = 1,

    /// <summary>Проект в таком виде не соберётся (нет build.py, нет meta.xml, нет точки входа).</summary>
    Error = 2
}

/// <summary>
/// Одно замечание по проекту, найденное при загрузке (<see cref="Abstractions.IProjectStore.Load"/>)
/// или при явной проверке (<see cref="Abstractions.IProjectStore.Validate"/>).
/// Загрузка НЕ бросает исключений на «кривой, но починимый» проект — она возвращает список замечаний,
/// который UI показывает баннером.
/// </summary>
/// <param name="Severity">Строгость.</param>
/// <param name="Code">
/// Стабильный машинный код замечания в SCREAMING_SNAKE_CASE. Фиксированный словарь:
/// <c>NO_BUILD_SCRIPT</c>, <c>NO_META_XML</c>, <c>META_UNREADABLE</c>, <c>NO_ENTRY_POINT</c>,
/// <c>ENTRY_POINT_NOT_MOD_PREFIXED</c>, <c>ID_MISMATCH</c>, <c>VERSION_MISMATCH</c>, <c>NAME_MISMATCH</c>,
/// <c>NO_PACKAGE_DIR</c>, <c>NO_WOTMOD_JSON</c>, <c>PYTHON_NOT_FOUND</c>, <c>GAME_DIR_NOT_FOUND</c>,
/// <c>EMPTY_LOG_TAG</c>.
/// </param>
/// <param name="Message">Текст для человека, по-русски.</param>
/// <param name="Path">Файл или каталог, к которому относится замечание, либо <c>null</c>.</param>
public sealed record ProjectValidation(
    ValidationSeverity Severity,
    string Code,
    string Message,
    string? Path = null);

/// <summary>
/// Общие константы формата <c>wotmod.json</c>. Все реализации <see cref="Abstractions.IProjectStore"/>
/// обязаны читать и писать файл ИМЕННО с этими настройками, иначе проекты станут несовместимы между сборками.
/// </summary>
public static class ProjectStoreDefaults
{
    /// <summary>Имя файла-дескриптора проекта в корне проекта: <c>wotmod.json</c>.</summary>
    public const string FileName = "wotmod.json";

    /// <summary>
    /// Настройки сериализации: camelCase, отступы, без экранирования кириллицы,
    /// неизвестные поля при чтении игнорируются (совместимость вперёд).
    /// </summary>
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };
}
