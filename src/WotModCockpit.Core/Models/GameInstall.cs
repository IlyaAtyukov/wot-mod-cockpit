namespace WotModCockpit.Core.Models;

/// <summary>
/// Обнаруженная установка «Мира танков» (Lesta) и её актуальные каталоги модов.
/// Всё, что здесь лежит, получено разбором <c>&lt;корень&gt;/paths.xml</c> — см. <see cref="Abstractions.IGameInstallLocator"/>.
/// </summary>
/// <param name="RootPath">
/// Абсолютный путь к корню установки — каталог, где лежат <c>Tanki.exe</c>, <c>paths.xml</c> и <c>python.log</c>.
/// Например <c>C:\Games\Tanki</c>. Всегда без завершающего разделителя.
/// </param>
/// <param name="Version">
/// Активная версия клиента в формате четырёх чисел, например <c>1.44.0.0</c>.
/// Берётся из имени последнего сегмента путей в <c>paths.xml</c>, а НЕ из имени каталога и НЕ из версии .exe.
/// </param>
/// <param name="ModsDir">
/// Абсолютный путь к каталогу упакованных модов: <c>&lt;Root&gt;\mods\&lt;Version&gt;</c>.
/// Соответствует строке <c>&lt;Path mask="*.mtmod" mode="recursive" root="res"&gt;./mods/1.44.0.0&lt;/Path&gt;</c>.
/// Каталог может физически отсутствовать — это не ошибка, игра создаёт его сама.
/// </param>
/// <param name="ResModsDir">
/// Абсолютный путь к каталогу «россыпи»: <c>&lt;Root&gt;\res_mods\&lt;Version&gt;</c>.
/// Соответствует строке <c>&lt;Path cacheSubdirs="true"&gt;./res_mods/1.44.0.0&lt;/Path&gt;</c>.
/// ВАЖНО: в <c>paths.xml</c> эта строка идёт ПЕРВОЙ, поэтому файлы отсюда молча перекрывают
/// одноимённые файлы из любого <c>.mtmod</c>. Детектится через <see cref="DeployState.IsShadowed"/>.
/// </param>
/// <param name="PythonLogPath">
/// Абсолютный путь к <c>&lt;Root&gt;\python.log</c>. Файл может не существовать до первого запуска игры.
/// Движок пишет в него в режиме дозаписи ('a') и, дойдя до 2048 КБ, УСЕКАЕТ файл до последних 512 КБ,
/// поэтому любой читатель обязан переживать уменьшение длины файла (см. <see cref="Abstractions.ILogTailer"/>).
/// </param>
public sealed record GameInstall(
    string RootPath,
    string Version,
    string ModsDir,
    string ResModsDir,
    string PythonLogPath)
{
    /// <summary>Абсолютный путь к <c>paths.xml</c>, из которого получены остальные поля.</summary>
    public string PathsXmlPath => Path.Combine(RootPath, "paths.xml");

    /// <summary>
    /// Каталог пользовательских конфигов модов: <c>&lt;Root&gt;\mods\configs</c>.
    /// Именно сюда кладётся <c>config.json</c> мода (например <c>mods\configs\hello_tanker\config.json</c>).
    /// Версии в пути НЕТ — конфиги живут вне версионных каталогов.
    /// </summary>
    public string ModConfigsDir => Path.Combine(RootPath, "mods", "configs");

    /// <summary>Ожидаемый путь к исполняемому файлу клиента. Существование НЕ гарантируется.</summary>
    public string ClientExePath => Path.Combine(RootPath, "Tanki.exe");
}
