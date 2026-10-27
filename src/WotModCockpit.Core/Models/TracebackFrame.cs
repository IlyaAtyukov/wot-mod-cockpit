namespace WotModCockpit.Core.Models;

/// <summary>
/// Один кадр python-трейсбека из <c>python.log</c>.
/// <para>
/// Источники, из которых кадр собирается (оба реально встречаются в файле):
/// <code>
/// // 1) строка тела traceback (из LogEntry.Continuation):
///   File "scripts/common/items/vehicles.py", line 4845, in getItemByCompactDescr
/// // 2) блок (файл, строка) в заголовке самой записи — Function при этом null:
/// 2026-08-28 01:02:11.103: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)
/// </code>
/// </para>
/// </summary>
/// <param name="VirtualPath">
/// Путь ВНУТРИ ресурсов игры, ровно как в логе: ПРЯМЫЕ слэши, обычно с префиксом <c>scripts/</c>
/// (<c>scripts/client/gui/mods/mod_hello_tanker.py</c>, <c>scripts/common/items/vehicles.py</c>).
/// На диске такого файла НЕ существует — он живёт в <c>res/packages/scripts.pkg</c> или внутри <c>.mtmod</c>.
/// </param>
/// <param name="Line">Номер строки (1-based), как его напечатал интерпретатор.</param>
/// <param name="Function">Имя функции из <c>, in &lt;имя&gt;</c>. <c>null</c>, если кадр взят из блока <c>(файл, строка)</c>.</param>
public sealed record TracebackFrame(string VirtualPath, int Line, string? Function)
{
    /// <summary>Исходная строка лога, из которой распознан кадр (дословно). Пусто, если кадр собран искусственно.</summary>
    public string RawLine { get; init; } = string.Empty;
}

/// <summary>Откуда взялся файл, на который удалось отобразить кадр.</summary>
public enum FrameOrigin
{
    /// <summary>Не удалось сопоставить ни с чем.</summary>
    Unresolved = 0,

    /// <summary>Файл проекта разработчика — точка входа или модуль пакета мода. Такой кадр открываем в редакторе.</summary>
    ModProject = 1,

    /// <summary>Декомпилированный исходник клиента из <c>.scratch\wot-src</c>. Открываем только на чтение.</summary>
    ClientSource = 2,

    /// <summary>Файл из россыпи в <c>res_mods\&lt;ver&gt;\</c> — то есть чей-то чужой (или наш dev-) деплой.</summary>
    ResMods = 3
}

/// <summary>
/// Результат отображения кадра трейсбека на реальный файл на диске.
/// </summary>
/// <param name="Frame">Исходный кадр.</param>
/// <param name="DiskPath">Абсолютный путь к существующему файлу либо <c>null</c>, если сопоставить не удалось.</param>
/// <param name="Origin">Откуда взялся файл.</param>
/// <param name="Line">
/// Номер строки для перехода. Обычно равен <see cref="TracebackFrame.Line"/>, но для декомпилированных
/// исходников клиента может отличаться — декомпилятор не сохраняет нумерацию один в один,
/// поэтому переход туда считается ПРИБЛИЗИТЕЛЬНЫМ (см. <see cref="IsApproximate"/>).
/// </param>
/// <param name="IsApproximate">
/// true, если номер строки не гарантирован (декомпилированный клиент). UI должен это показывать,
/// чтобы разработчик не искал баг не в той строке.
/// </param>
public sealed record TracebackMapping(
    TracebackFrame Frame,
    string? DiskPath,
    FrameOrigin Origin,
    int Line,
    bool IsApproximate)
{
    /// <summary>true, если есть куда переходить.</summary>
    public bool IsResolved => !string.IsNullOrEmpty(DiskPath);

    /// <summary>Готовый «не сопоставили» для кадра.</summary>
    public static TracebackMapping Unresolved(TracebackFrame frame) =>
        new(frame, null, FrameOrigin.Unresolved, frame.Line, false);
}
