namespace WotModCockpit.Core.Models;

/// <summary>
/// Что именно попросить у <c>build.py</c>. Каждому значению соответствует РОВНО один флаг скрипта.
/// Три значения ПИШУТ в каталог игры — они допустимы только по явному нажатию пользователя.
/// </summary>
public enum BuildAction
{
    /// <summary>
    /// Собрать <c>.mtmod</c> в <c>dist\</c> и остановиться. Флагов действия нет — это вызов build.py «пустым».
    /// В игру НЕ пишет. Безопасно вызывать автоматически (например, по сохранению файла).
    /// </summary>
    Build = 0,

    /// <summary>
    /// Собрать и скопировать <c>.mtmod</c> в <c>&lt;game&gt;\mods\&lt;ver&gt;\</c>. Флаг <c>--install</c>.
    /// ПИШЕТ В ИГРУ. Только по явному нажатию.
    /// </summary>
    BuildAndInstall = 1,

    /// <summary>
    /// Разложить исходники россыпью в <c>&lt;game&gt;\res_mods\&lt;ver&gt;\</c> для быстрой итерации. Флаг <c>--dev</c>.
    /// ПИШЕТ В ИГРУ. Помни: res_mods стоит в paths.xml ПЕРВЫМ и перекрывает установленный .mtmod —
    /// после Dev-раскладки <see cref="DeployState.IsShadowed"/> станет true, и это надо показать пользователю.
    /// </summary>
    DevDeploy = 2,

    /// <summary>
    /// Убрать из игры и <c>.mtmod</c>, и россыпь — то, что положили <c>--install</c>/<c>--dev</c>. Флаг <c>--uninstall</c>.
    /// ПИШЕТ В ИГРУ (удаляет). Только по явному нажатию.
    /// </summary>
    Uninstall = 3,

    /// <summary>
    /// Напечатать листинг содержимого собранного архива. Флаг <c>--list</c>. В игру НЕ пишет.
    /// </summary>
    List = 4
}

/// <summary>Расширения-помощники для <see cref="BuildAction"/>.</summary>
public static class BuildActionExtensions
{
    /// <summary>
    /// Флаг командной строки build.py для действия, либо <c>null</c> для <see cref="BuildAction.Build"/>
    /// (у обычной сборки собственного флага нет).
    /// </summary>
    public static string? ToBuildFlag(this BuildAction action) => action switch
    {
        BuildAction.Build => null,
        BuildAction.BuildAndInstall => "--install",
        BuildAction.DevDeploy => "--dev",
        BuildAction.Uninstall => "--uninstall",
        BuildAction.List => "--list",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null)
    };

    /// <summary>
    /// true, если действие пишет в каталог игры. UI ОБЯЗАН требовать явного нажатия для таких действий
    /// и никогда не запускать их автоматически (по watcher'у, по таймеру, при старте).
    /// </summary>
    public static bool WritesToGame(this BuildAction action) =>
        action is BuildAction.BuildAndInstall or BuildAction.DevDeploy or BuildAction.Uninstall;
}
