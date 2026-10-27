namespace WotModCockpit.Core.Models;

/// <summary>
/// Последний выбор в диалоге «Собрать сетап» на экране «Моды» — часть
/// <see cref="CockpitSettings"/>. Запоминается, чтобы следующая сборка начиналась с того же.
/// </summary>
public sealed class SetupBundleSettings
{
    /// <summary>Имя сборки по умолчанию — заголовок окна сетапа.</summary>
    public const string DefaultName = "Сборка модов";

    /// <summary>Имя сборки.</summary>
    public string Name { get; set; } = DefaultName;

    /// <summary>Куда класть сетап. Пусто — <c>Документы\WoT Mod Cockpit\Сетапы</c>.</summary>
    public string OutputDir { get; set; } = string.Empty;

    /// <summary>Пересобирать выбранные проекты перед упаковкой (build.py без установки в игру).</summary>
    public bool Rebuild { get; set; } = true;

    /// <summary>Моды (<c>&lt;id&gt;</c>), отмеченные в прошлый раз.</summary>
    public List<string> SelectedModIds { get; set; } = new();

    /// <summary>
    /// Все моды, которые диалог видел в прошлый раз. Нужны, чтобы отличить «человек снял
    /// галочку» от «этого проекта тогда ещё не было»: новый проект отмечается сам.
    /// </summary>
    public List<string> KnownModIds { get; set; } = new();

    /// <summary>Каталог сетапов по умолчанию.</summary>
    /// <returns>Путь.</returns>
    public static string DefaultOutputDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WoT Mod Cockpit", "Сетапы");
}
