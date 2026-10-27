using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Mods;

/// <summary>Настройки установки собранного мода в игру.</summary>
public sealed record ModInstallOptions
{
    /// <summary>Значения по умолчанию: предыдущие версии выключаются, перезаписи нет.</summary>
    public static ModInstallOptions Default { get; } = new();

    /// <summary>
    /// Выключать ли другие установленные версии того же мода (переименованием в <c>.mtmod.off</c>).
    /// Именно ВЫКЛЮЧАТЬ, а не удалять: две версии одного id в каталоге — классическая причина
    /// «правлю, а в игре не меняется», но удалять чужую или предыдущую сборку кокпит права не имеет.
    /// </summary>
    public bool DisablePreviousVersions { get; init; } = true;

    /// <summary>
    /// Разрешить перезапись файла с ТЕМ ЖЕ именем (то есть переустановку той же версии поверх).
    /// По умолчанию запрещено: любая молчаливая перезапись в каталоге игры — способ потерять чужой файл.
    /// </summary>
    public bool OverwriteSameName { get; init; }

    /// <summary>
    /// Подпапка внутри каталога модов, куда класть файл (<c>null</c> — корень).
    /// Проверяется на выход за пределы каталога модов, как и всё остальное.
    /// </summary>
    public string? SubfolderRelative { get; init; }
}

/// <summary>
/// Фасад лаунчера модов для интерфейса: один снимок каталога, одна точка для операций,
/// одна точка для иконок.
/// <para><b>Правила, которые реализация обязана соблюдать:</b></para>
/// <list type="number">
///   <item>При старте приложения — НИ ОДНОЙ операции записи. Только <see cref="Scan"/>.</item>
///   <item>Любая запись — исключительно внутри <see cref="GameInstall.ModsDir"/> и только по файлам
///         <c>*.mtmod</c> / <c>*.mtmod.off</c>. Каталоги <c>res\</c>, <c>res_mods\</c>,
///         <c>mods\configs\</c>, <c>paths.xml</c> не трогаются никогда, даже на чтение-запись.</item>
///   <item>Любая запись запрещена, пока запущена игра (и пока не удалось выяснить, запущена ли она).</item>
///   <item>Отказ возвращается ЗНАЧЕНИЕМ с полным путём и причиной, а не исключением.</item>
///   <item>Массовых операций нет. Каждая операция — над одним файлом, который пользователь видит.</item>
/// </list>
/// </summary>
public interface IModLibraryService
{
    /// <summary>Путь к журналу операций — его же показывает интерфейс.</summary>
    string OperationLogPath { get; }

    /// <summary>
    /// Обходит каталог модов игры и связывает найденное с проектами пользователя. ТОЛЬКО ЧТЕНИЕ.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="projectDirs">
    /// Каталоги проектов пользователя (из настроек кокпита). <c>null</c> — тогда все моды считаются чужими.
    /// </param>
    /// <returns>Снимок каталога. Никогда не <c>null</c>, исключений не бросает.</returns>
    ModLibraryScan Scan(GameInstall game, IReadOnlyList<string>? projectDirs = null);

    /// <summary>
    /// Перечитывает одну строку с диска. Нужен после операции: перечитывать 128 архивов
    /// ради одного изменившегося файла незачем.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="fullPath">Путь к файлу мода.</param>
    /// <param name="projectDirs">Каталоги проектов пользователя.</param>
    /// <returns>Актуальное описание либо <c>null</c>, если файла больше нет.</returns>
    InstalledMod? Refresh(GameInstall game, string fullPath, IReadOnlyList<string>? projectDirs = null);

    /// <summary>
    /// Сводка по россыпи в <c>res_mods\&lt;версия&gt;</c> — разделом «не управляется».
    /// Показывать её обязательно: россыпь читается РАНЬШЕ пакетов и молча перекрывает их,
    /// поэтому «выключил мод, а он всё равно работает» объясняется чаще всего именно ею.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <returns>Сводка; только чтение.</returns>
    ResModsSummary ScanResMods(GameInstall game);

    /// <summary>Запущена ли игра. Тем же ответом UI гасит кнопки и рисует жёлтый баннер.</summary>
    /// <returns>Состояние.</returns>
    GameRunningState CheckGame();

    /// <summary>Включает мод (<c>.mtmod.off</c> → <c>.mtmod</c>).</summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="fullPath">Путь к выключенному файлу.</param>
    /// <param name="expect">Снимок файла на момент подтверждения; можно <c>null</c>.</param>
    /// <returns>Результат операции.</returns>
    ModOperationResult Enable(GameInstall game, string fullPath, ModWriteExpectation? expect = null);

    /// <summary>Выключает мод (<c>.mtmod</c> → <c>.mtmod.off</c>). Обратимо, файл остаётся на месте.</summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="fullPath">Путь к включённому файлу.</param>
    /// <param name="expect">Снимок файла на момент подтверждения; можно <c>null</c>.</param>
    /// <returns>Результат операции.</returns>
    ModOperationResult Disable(GameInstall game, string fullPath, ModWriteExpectation? expect = null);

    /// <summary>
    /// Ставит в игру собранный артефакт из <c>dist\</c>.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="artifactPath">Путь к <c>.mtmod</c> в каталоге сборки проекта.</param>
    /// <param name="options">Настройки установки; <c>null</c> — по умолчанию.</param>
    /// <returns>
    /// Результат. Выключенные попутно предыдущие версии перечислены в
    /// <see cref="ModOperationResult.Related"/>.
    /// </returns>
    ModOperationResult Install(GameInstall game, string artifactPath, ModInstallOptions? options = null);

    /// <summary>
    /// Удаляет файл мода. Единственная необратимая операция, поэтому требует, чтобы вызывающий
    /// передал ТОЧНОЕ имя файла — так интерфейс вынужден показать пользователю, что именно удаляется.
    /// Каталоги не удаляются никогда.
    /// </summary>
    /// <param name="game">Установка игры.</param>
    /// <param name="fullPath">Путь к файлу.</param>
    /// <param name="confirmFileName">Имя файла, напечатанное пользователем.</param>
    /// <param name="expect">Снимок файла на момент подтверждения; можно <c>null</c>.</param>
    /// <returns>Результат операции.</returns>
    ModOperationResult Delete(GameInstall game, string fullPath, string confirmFileName, ModWriteExpectation? expect = null);

    /// <summary>
    /// Достаёт иконку мода из архива (точечное чтение одной записи). Результат кэшируется
    /// по пути + времени записи + размеру, поэтому повторный вызов для той же строки бесплатен.
    /// </summary>
    /// <param name="mtmodPath">Путь к файлу мода.</param>
    /// <param name="modId">Известный id мода — уточняет поиск.</param>
    /// <returns>Иконка либо <see cref="ModIcon.None"/>.</returns>
    ModIcon ReadIcon(string mtmodPath, string? modId = null);

    /// <summary>Последние строки журнала операций — для ленты на экране.</summary>
    /// <param name="maxLines">Сколько строк вернуть.</param>
    /// <returns>Строки от старых к новым; пустой список, если журнала нет.</returns>
    IReadOnlyList<string> ReadOperationLog(int maxLines = 200);
}
