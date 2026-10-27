using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Загрузка и сохранение дескриптора проекта мода (<c>wotmod.json</c> в корне проекта).
/// <para>
/// Разделение источников истины, нарушать которое НЕЛЬЗЯ:
/// <list type="bullet">
///   <item><c>src\meta.xml</c> — манифест САМОГО МОДА: <c>&lt;id&gt;</c>, <c>&lt;version&gt;</c>, <c>&lt;name&gt;</c>, <c>&lt;description&gt;</c>.
///         Его читает build.py и игра. Кокпит его НЕ переписывает без явной команды.</item>
///   <item><c>wotmod.json</c> — дескриптор КОКПИТА: где python, какой тег в логе, какие флаги сборки.
///         Пишет только кокпит; build.py про него не знает.</item>
/// </list>
/// </para>
/// </summary>
public interface IProjectStore
{
    /// <summary>
    /// Загружает проект из каталога.
    /// <para>Алгоритм, обязательный к соблюдению:</para>
    /// <list type="number">
    ///   <item>если есть <c>&lt;dir&gt;\wotmod.json</c> — десериализовать его
    ///         (<see cref="ProjectStoreDefaults.JsonOptions"/>), неизвестные поля игнорировать;</item>
    ///   <item>иначе — построить проект «на лету» из того, что лежит на диске (см. <see cref="Sniff"/>);</item>
    ///   <item>всегда проставить <see cref="ModProject.ProjectDir"/> фактическим абсолютным путём <paramref name="dir"/>,
    ///         игнорируя то, что могло быть записано в файле;</item>
    ///   <item>перечитать <c>src\meta.xml</c> и подтянуть из него <see cref="ModProject.ModId"/>,
    ///         <see cref="ModProject.Version"/>, <see cref="ModProject.DisplayName"/>,
    ///         <see cref="ModProject.Description"/> — meta.xml главнее кэша в json.</item>
    /// </list>
    /// </summary>
    /// <param name="dir">Каталог проекта. Относительный путь допустим.</param>
    /// <returns>
    /// Проект либо <c>null</c>, если каталога нет или он вообще не похож на проект мода
    /// (нет ни <c>wotmod.json</c>, ни <c>build.py</c>, ни <c>src\meta.xml</c>).
    /// Кривой, но починимый проект (например, meta.xml есть, а точки входа нет) должен ВЕРНУТЬСЯ,
    /// а проблемы — уехать в <see cref="Validate"/>. Исключения на «плохой JSON» бросать нельзя:
    /// битый <c>wotmod.json</c> = поведение как при его отсутствии + замечание <c>NO_WOTMOD_JSON</c>.
    /// </returns>
    ModProject? Load(string dir);

    /// <summary>
    /// Сохраняет дескриптор в <c>&lt;p.ProjectDir&gt;\wotmod.json</c>.
    /// Пишет атомарно (временный файл рядом + <c>File.Move(overwrite: true)</c>), в UTF-8 без BOM.
    /// <see cref="ModProject.ProjectDir"/> и прочие <c>[JsonIgnore]</c>-свойства в файл не попадают.
    /// <c>src\meta.xml</c> НЕ трогает — для этого есть <see cref="SyncMetaXml"/>.
    /// </summary>
    /// <param name="p">Проект. <see cref="ModProject.ProjectDir"/> обязан быть заполнен, иначе <see cref="ArgumentException"/>.</param>
    void Save(ModProject p);

    /// <summary>
    /// Определяет параметры проекта по содержимому каталога, не читая <c>wotmod.json</c>.
    /// Используется при первом открытии чужого/шаблонного каталога и как запасной путь в <see cref="Load"/>.
    /// <para>Что и откуда берётся:</para>
    /// <list type="bullet">
    ///   <item><see cref="ModProject.ModId"/>/<see cref="ModProject.Version"/>/<see cref="ModProject.DisplayName"/>/
    ///         <see cref="ModProject.Description"/> — из <c>src\meta.xml</c>;</item>
    ///   <item><see cref="ModProject.EntryPointModule"/> — единственный файл <c>src\res\scripts\client\gui\mods\mod_*.py</c>
    ///         (без расширения). Если их несколько — берётся первый по алфавиту, остальные дают замечание;</item>
    ///   <item><see cref="ModProject.PackageName"/> — единственный подкаталог в <c>src\res\scripts\client\gui\mods\</c>;</item>
    ///   <item><see cref="ModProject.LogTag"/> — эвристика: искать в исходнике точки входа литерал вида <c>'[Xxx</c>
    ///         (в шаблоне это <c>'[HelloTanker/loader] '</c> → тег <c>[HelloTanker</c>). Не нашли — пустая строка.</item>
    /// </list>
    /// </summary>
    /// <param name="dir">Каталог проекта.</param>
    /// <returns>Проект либо <c>null</c>, если каталог не похож на проект мода.</returns>
    ModProject? Sniff(string dir);

    /// <summary>
    /// Проверяет проект и возвращает замечания. Ничего не пишет и не запускает.
    /// Пустой список = проект в порядке. Коды замечаний перечислены в <see cref="ProjectValidation"/>.
    /// </summary>
    /// <param name="p">Проект.</param>
    /// <returns>Список замечаний, отсортированный по убыванию строгости. Никогда не <c>null</c>.</returns>
    IReadOnlyList<ProjectValidation> Validate(ModProject p);

    /// <summary>
    /// Записывает <see cref="ModProject.ModId"/>, <see cref="ModProject.Version"/>,
    /// <see cref="ModProject.DisplayName"/>, <see cref="ModProject.Description"/> обратно в <c>src\meta.xml</c>,
    /// сохраняя форматирование и остальные элементы файла.
    /// Вызывается ТОЛЬКО по явному действию пользователя (например, кнопкой «поднять версию»),
    /// потому что meta.xml — исходник мода, а не наш кэш.
    /// </summary>
    /// <param name="p">Проект.</param>
    void SyncMetaXml(ModProject p);
}
