using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Хранилище глобальных настроек приложения в <c>%APPDATA%\WotModCockpit\settings.json</c>.
/// Формат — тот же camelCase-JSON, что и у <c>wotmod.json</c> (<see cref="ProjectStoreDefaults.JsonOptions"/>).
/// </summary>
public interface ISettingsStore
{
    /// <summary>Абсолютный путь к файлу настроек. Файла может ещё не быть.</summary>
    string SettingsPath { get; }

    /// <summary>
    /// Читает настройки. Файла нет или он битый — возвращает <c>new CockpitSettings()</c> со значениями
    /// по умолчанию, БЕЗ исключения: настройки не должны мешать приложению запуститься.
    /// </summary>
    /// <returns>Настройки. Никогда не <c>null</c>.</returns>
    CockpitSettings Load();

    /// <summary>
    /// Сохраняет настройки атомарно (временный файл + <c>File.Move(overwrite: true)</c>), создавая каталог при необходимости.
    /// </summary>
    /// <param name="settings">Настройки.</param>
    void Save(CockpitSettings settings);
}
