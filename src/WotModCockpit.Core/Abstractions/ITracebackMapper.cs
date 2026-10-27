using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Превращает кадр python-трейсбека из лога в путь к реальному файлу на диске,
/// чтобы двойной клик по строке лога открывал редактор в нужном месте.
/// <para>
/// Задача существует потому, что пути в логе ВИРТУАЛЬНЫЕ — это пути внутри ресурсов игры
/// (<c>res/packages/scripts.pkg</c> либо содержимое <c>.mtmod</c>), и на диске их нет.
/// </para>
/// <para>Три раскладки, между которыми идёт сопоставление (все проверены на эталонной установке клиента 1.44):</para>
/// <list type="number">
///   <item><b>Файл мода.</b> <c>scripts/client/gui/mods/mod_hello_tanker.py</c> →
///         <c>&lt;ProjectDir&gt;\src\res\scripts\client\gui\mods\mod_hello_tanker.py</c>.
///         То есть виртуальный путь дописывается к <c>&lt;SrcDir&gt;\res\</c> один в один.
///         Сюда же попадает пакет мода: <c>scripts/client/gui/mods/hello_tanker/main.py</c> →
///         <c>&lt;SrcDir&gt;\res\scripts\client\gui\mods\hello_tanker\main.py</c>.
///         Признак — совпадение с <see cref="ModProject.EntryPointVirtualPath"/> или префиксом
///         <see cref="ModProject.PackageVirtualPrefix"/>. Результат — <see cref="FrameOrigin.ModProject"/>,
///         номер строки ТОЧНЫЙ.</item>
///   <item><b>Исходник клиента.</b> Здесь есть ловушка: в декомпилированном дереве
///         (<c>&lt;шаблон мода&gt;\.scratch\wot-src</c>, у клиента 1.44 — 8052 файла) префикса <c>scripts/</c> НЕТ —
///         внутри лежат сразу <c>client\</c>, <c>client_common\</c>, <c>common\</c>. Поэтому
///         <c>scripts/common/items/vehicles.py</c> → <c>&lt;wotSrcDir&gt;\common\items\vehicles.py</c>, а
///         <c>scripts/client/predefined_hosts.py</c> → <c>&lt;wotSrcDir&gt;\client\predefined_hosts.py</c>
///         (оба файла существуют — проверено). Результат — <see cref="FrameOrigin.ClientSource"/>,
///         номер строки ПРИБЛИЗИТЕЛЬНЫЙ: декомпилятор не сохраняет нумерацию исходника.</item>
///   <item><b>Россыпь в игре.</b> Если ничего не нашлось, но мод разложен через <c>--dev</c>,
///         файл может лежать в <c>&lt;game&gt;\res_mods\&lt;ver&gt;\scripts\...</c>. Результат —
///         <see cref="FrameOrigin.ResMods"/>. Открывать такой файл в редакторе на правку не надо:
///         это КОПИЯ, правки затрёт следующий <c>--dev</c>. Сначала пробуем свести к файлу проекта.</item>
/// </list>
/// </summary>
public interface ITracebackMapper
{
    /// <summary>
    /// Отображает кадр на файл на диске.
    /// </summary>
    /// <param name="f">Кадр трейсбека.</param>
    /// <param name="p">Проект разработчика — по нему опознаются «свои» файлы. Может быть <c>null</c>.</param>
    /// <param name="wotSrcDir">
    /// Каталог декомпилированных исходников клиента, например
    /// <c>&lt;шаблон мода&gt;\.scratch\wot-src</c>.
    /// Пустая строка или несуществующий каталог = кадры клиента просто не разрешаются.
    /// </param>
    /// <returns>
    /// Абсолютный путь к СУЩЕСТВУЮЩЕМУ файлу либо <c>null</c>, если сопоставить не удалось.
    /// Реализация обязана проверять существование файла: возвращать «красивый, но несуществующий» путь нельзя.
    /// Порядок проб — строго проект → клиент → res_mods.
    /// </returns>
    string? MapToDisk(TracebackFrame f, ModProject p, string wotSrcDir);

    /// <summary>
    /// То же, что <see cref="MapToDisk"/>, но с подробностями: откуда файл и можно ли доверять номеру строки.
    /// Это основной метод для UI — по нему рисуется иконка происхождения и предупреждение
    /// «строка приблизительная» для декомпилированного клиента.
    /// </summary>
    /// <param name="f">Кадр трейсбека.</param>
    /// <param name="p">Проект или <c>null</c>.</param>
    /// <param name="wotSrcDir">Каталог декомпилированных исходников клиента.</param>
    /// <param name="game">Установка игры — нужна для пробы по <c>res_mods</c>. Может быть <c>null</c>.</param>
    /// <returns>Результат сопоставления; при неудаче — <see cref="TracebackMapping.Unresolved"/>. Никогда не <c>null</c>.</returns>
    TracebackMapping Map(TracebackFrame f, ModProject? p, string wotSrcDir, GameInstall? game);

    /// <summary>
    /// Достаёт кадры из записи лога: сначала из тела traceback
    /// (<c>  File "scripts/common/items/vehicles.py", line 4845, in getItemByCompactDescr</c>),
    /// затем из заголовка записи (<see cref="LogEntry.SourceFile"/> + <see cref="LogEntry.SourceLine"/>,
    /// у такого кадра <see cref="TracebackFrame.Function"/> = <c>null</c>).
    /// Порядок — как в файле, то есть самый глубокий кадр последним, ровно как печатает python.
    /// </summary>
    /// <param name="entry">Запись лога.</param>
    /// <returns>Список кадров, возможно пустой. Никогда не <c>null</c>.</returns>
    IReadOnlyList<TracebackFrame> ExtractFrames(LogEntry entry);
}
