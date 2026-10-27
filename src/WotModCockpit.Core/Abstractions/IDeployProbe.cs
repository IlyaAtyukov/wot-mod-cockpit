using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Смотрит, в каком виде мод сейчас лежит в каталоге игры. ТОЛЬКО ЧТЕНИЕ:
/// реализация не создаёт, не удаляет и не переименовывает ничего в <c>&lt;game&gt;</c>,
/// не запускает build.py и не запускает игру.
/// <para>
/// Ради чего всё: в <c>paths.xml</c> строка <c>./res_mods/1.44.0.0</c> стоит ПЕРЕД строкой
/// <c>./mods/1.44.0.0</c> с <c>mask="*.mtmod"</c>. Значит, при совпадении имён файл из россыпи
/// в <c>res_mods</c> побеждает файл из <c>.mtmod</c>. Разработчик правит .mtmod, пересобирает,
/// ставит — а игра продолжает грузить забытую с прошлого раза dev-раскладку. Именно этот случай
/// обязан ловить <see cref="DeployState.IsShadowed"/>.
/// </para>
/// </summary>
public interface IDeployProbe
{
    /// <summary>
    /// Опрашивает каталоги игры и возвращает состояние.
    /// <para>Что где искать:</para>
    /// <list type="bullet">
    ///   <item><b>mods:</b> в <see cref="GameInstall.ModsDir"/> РЕКУРСИВНО по маске
    ///         <c>&lt;ModId&gt;_*.mtmod</c> (в paths.xml у этой строки <c>mode="recursive"</c>).
    ///         Несколько находок = разные версии: свежайшая идёт в <see cref="DeployState.MtmodPath"/>,
    ///         остальные — в <see cref="DeployState.StaleMtmodPaths"/>.</item>
    ///   <item><b>res_mods:</b> в <see cref="GameInstall.ResModsDir"/> проверить
    ///         <c>scripts\client\gui\mods\&lt;EntryPointModule&gt;.pyc</c>, тот же файл с <c>.py</c>,
    ///         и всё содержимое каталога <c>scripts\client\gui\mods\&lt;PackageName&gt;\</c>.</item>
    ///   <item><b>IsShadowed:</b> true ⟺ нашлось И там, И там.</item>
    /// </list>
    /// </summary>
    /// <param name="g">Установка игры. Каталоги могут физически отсутствовать — это не ошибка.</param>
    /// <param name="p">Проект: из него берутся ModId, EntryPointModule и PackageName.</param>
    /// <returns>
    /// Состояние деплоя. Никогда не <c>null</c>: если ничего не нашлось — <see cref="DeployState.Absent"/>.
    /// Ошибки доступа к отдельным подкаталогам глотаются (каталог просто считается пустым),
    /// исключение наружу не летит — опрос дёргается часто, в том числе по таймеру.
    /// </returns>
    DeployState Probe(GameInstall g, ModProject p);
}
