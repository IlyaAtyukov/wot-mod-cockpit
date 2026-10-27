namespace WotModCockpit.Core.Models;

/// <summary>
/// Фактическое состояние мода в каталоге игры на момент опроса. Строится ТОЛЬКО чтением диска —
/// <see cref="Abstractions.IDeployProbe"/> ничего не пишет и ничего не запускает.
/// <para>
/// Главная задача этой модели — поймать классическую ловушку: в <c>paths.xml</c> строка
/// <c>./res_mods/1.44.0.0</c> идёт ПЕРВОЙ, а <c>./mods/1.44.0.0</c> (*.mtmod) — второй. Поэтому любой
/// одноимённый файл из россыпи в <c>res_mods</c> МОЛЧА перекрывает файл из установленного <c>.mtmod</c>,
/// и разработчик правит .mtmod, а игра грузит старую россыпь. См. <see cref="IsShadowed"/>.
/// </para>
/// </summary>
/// <param name="InMods">true, если в <c>&lt;game&gt;\mods\&lt;ver&gt;\</c> найден <c>.mtmod</c> этого мода.</param>
/// <param name="MtmodPath">
/// Абсолютный путь к найденному <c>.mtmod</c>, либо <c>null</c>. Поиск — по маске <c>&lt;ModId&gt;_*.mtmod</c>
/// РЕКУРСИВНО (в paths.xml у этой строки <c>mode="recursive"</c>, игра ищет по подкаталогам).
/// Если файлов несколько (остались разные версии) — берётся самый свежий по времени записи,
/// а остальные попадают в <see cref="StaleMtmodPaths"/>: это тоже частая причина «правлю, а не меняется».
/// </param>
/// <param name="InResMods">true, если в <c>&lt;game&gt;\res_mods\&lt;ver&gt;\</c> лежат файлы этого мода (dev-раскладка).</param>
/// <param name="ResModsFiles">
/// Абсолютные пути найденных в <c>res_mods</c> файлов мода. Что считается «файлом мода»:
/// <c>res_mods\&lt;ver&gt;\scripts\client\gui\mods\&lt;EntryPointModule&gt;.py[c]</c> и всё содержимое
/// <c>res_mods\&lt;ver&gt;\scripts\client\gui\mods\&lt;PackageName&gt;\</c>. Пустой список, если ничего нет.
/// НИКОГДА не <c>null</c>.
/// </param>
/// <param name="IsShadowed">
/// true ⟺ <see cref="InMods"/> И <see cref="InResMods"/> одновременно, то есть россыпь в res_mods
/// перекрывает установленный .mtmod. Это НЕ ошибка сама по себе (так и работает Dev-раскладка),
/// но UI обязан показать это жёлтым баннером: «в игре действует россыпь из res_mods, .mtmod не читается».
/// </param>
public sealed record DeployState(
    bool InMods,
    string? MtmodPath,
    bool InResMods,
    IReadOnlyList<string> ResModsFiles,
    bool IsShadowed)
{
    /// <summary>
    /// Прочие найденные <c>.mtmod</c> того же мода (другие версии), кроме выбранного <see cref="MtmodPath"/>.
    /// Пустой список — норма. Непустой — повод предложить кнопку «удалить старые версии».
    /// </summary>
    public IReadOnlyList<string> StaleMtmodPaths { get; init; } = Array.Empty<string>();

    /// <summary>Время записи <see cref="MtmodPath"/>, если файл найден. Нужно, чтобы сказать «установлено 12 минут назад».</summary>
    public DateTimeOffset? MtmodWriteTimeUtc { get; init; }

    /// <summary>Размер <see cref="MtmodPath"/> в байтах, если файл найден.</summary>
    public long? MtmodSizeBytes { get; init; }

    /// <summary>
    /// Время самого свежего файла из <see cref="ResModsFiles"/>. Сравнение с <see cref="MtmodWriteTimeUtc"/>
    /// позволяет сказать, что именно новее — россыпь или пакет.
    /// </summary>
    public DateTimeOffset? ResModsNewestWriteTimeUtc { get; init; }

    /// <summary>true, если мода нет в игре ни в каком виде.</summary>
    public bool IsAbsent => !InMods && !InResMods;

    /// <summary>Пустое состояние «нигде не установлен» — годится как значение по умолчанию для UI до первого опроса.</summary>
    public static DeployState Absent { get; } =
        new(false, null, false, Array.Empty<string>(), false);
}
