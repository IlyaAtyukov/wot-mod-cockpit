using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Abstractions;

/// <summary>
/// Запускает <c>build.py</c> проекта отдельным процессом и отдаёт его вывод построчно, по мере появления.
/// <para>
/// Кокпит НИКОГДА не повторяет логику тулчейна на C#: ни сборку zip, ни компиляцию .pyc
/// (<c>tools/py27c.py</c>), ни проверку синтаксиса (<c>tools/check_py2_syntax.py</c>), ни установку в игру.
/// Всё это делает python-скрипт, а кокпит только формирует командную строку и показывает вывод.
/// Это же и единственный способ, которым в каталог игры что-то попадает.
/// </para>
/// <para>Реальная командная строка выглядит так:</para>
/// <code>
/// &lt;PythonExe&gt; "&lt;ProjectDir&gt;\build.py" --src "&lt;SrcDir&gt;" --dist "&lt;DistDir&gt;" --game-dir "&lt;GameDir&gt;" [--install|--dev|--uninstall|--list] [--clean] [...]
/// </code>
/// <para>Реальный вывод build.py (снят запуском, stdout):</para>
/// <code>
/// [build] entry points found: mod_hello_tanker.py
/// [build] mod packages (stay plain .py): hello_tanker
/// [build] OK   python 2.7 syntax check passed (tools/check_py2_syntax.py)
/// [build] meta.xml: id=ru.local.hello_tanker version=1.0.0 name=Hello Tanker
/// [build] compiled (py27c) src/res/.../mod_hello_tanker.py  -&gt;  mod_hello_tanker.pyc  [dfile=scripts/client/gui/mods/mod_hello_tanker.py]
/// [build] OK   C:\...\dist\ru.local.hello_tanker_1.0.0.mtmod (62848 bytes, 10 entries, all Stored)
/// [build] not installed (pass --install to copy into C:\Games\Tanki\mods\1.44.0.0)
/// [build] finished clean
/// </code>
/// Путь к артефакту достаётся именно из строки <c>[build] OK   &lt;путь&gt; (N bytes, ...)</c>.
/// </summary>
public interface IBuildRunner
{
    /// <summary>
    /// Запускает процесс и немедленно возвращает управление, отдав ручку запуска.
    /// Сам процесс стартует здесь же (синхронно), поэтому неудача запуска (нет python, нет build.py)
    /// не бросает исключение, а превращается в <see cref="IBuildRun.Completion"/> с
    /// <see cref="BuildResult.ExitCodeLaunchFailed"/> и одной строкой <see cref="BuildStreamKind.Diagnostic"/> в выводе.
    /// </summary>
    /// <param name="action">Что делать. Помни: три из пяти действий ПИШУТ в каталог игры (<see cref="BuildActionExtensions.WritesToGame"/>).</param>
    /// <param name="project">Проект. Из него берутся python, пути и постоянные флаги.</param>
    /// <param name="options">Разовые параметры запуска. <c>null</c> = <c>new BuildRunOptions()</c>.</param>
    /// <returns>Ручка запуска. Вызывающий обязан её освободить (<c>await using</c>).</returns>
    IBuildRun Start(BuildAction action, ModProject project, BuildRunOptions? options = null);

    /// <summary>
    /// Удобная обёртка на один вызов: запускает, скармливает каждую строку в <paramref name="progress"/>
    /// и дожидается конца. Отмена <paramref name="ct"/> убивает процесс и даёт
    /// <see cref="BuildResult.ExitCodeCancelled"/>, а не <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <param name="action">Что делать.</param>
    /// <param name="project">Проект.</param>
    /// <param name="options">Разовые параметры запуска.</param>
    /// <param name="progress">Приёмник строк вывода. Вызывается из фонового потока — в WPF нужен маршалинг в UI-поток.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Итог запуска. Никогда не <c>null</c>.</returns>
    Task<BuildResult> RunAsync(
        BuildAction action,
        ModProject project,
        BuildRunOptions? options,
        IProgress<BuildOutputLine>? progress,
        CancellationToken ct = default);
}

/// <summary>
/// Ручка одного запущенного процесса сборки: поток строк + итог.
/// <para>Канонический сценарий использования:</para>
/// <code>
/// await using var run = runner.Start(BuildAction.Build, project);
/// await foreach (var line in run.ReadOutputAsync(ct))
///     AppendToUi(line);            // stdout и stderr уже слиты в один поток в порядке поступления
/// BuildResult result = await run.Completion;
/// </code>
/// </summary>
public interface IBuildRun : IAsyncDisposable
{
    /// <summary>Полная командная строка запуска — для эха в панель сборки и кнопки «скопировать команду».</summary>
    string CommandLine { get; }

    /// <summary>Момент старта процесса (UTC).</summary>
    DateTimeOffset StartedUtc { get; }

    /// <summary>
    /// Строки вывода в порядке поступления; stdout и stderr слиты в один поток
    /// (различить их можно по <see cref="BuildOutputLine.Stream"/>). Перечисление завершается,
    /// когда процесс закрыл оба потока.
    /// <para>
    /// Перечислять можно РОВНО ОДИН РАЗ: это живой канал, а не буфер.
    /// Повторный вызов обязан бросить <see cref="InvalidOperationException"/>.
    /// Если строки нужны нескольким потребителям — накапливайте их у себя.
    /// </para>
    /// <para>
    /// Отмена <paramref name="ct"/> прекращает перечисление И убивает процесс
    /// (<see cref="Cancel"/> вызывается неявно), после чего <see cref="Completion"/> отдаёт
    /// <see cref="BuildResult.ExitCodeCancelled"/>. <see cref="OperationCanceledException"/> наружу не летит.
    /// </para>
    /// </summary>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Асинхронная последовательность строк.</returns>
    IAsyncEnumerable<BuildOutputLine> ReadOutputAsync(CancellationToken ct = default);

    /// <summary>
    /// Задача, завершающаяся итогом сборки. Никогда не заканчивается ошибкой и никогда не отменяется —
    /// любой сбой описан полями <see cref="BuildResult"/>. Ждать её можно, не перечисляя вывод.
    /// </summary>
    Task<BuildResult> Completion { get; }

    /// <summary>
    /// Убивает процесс вместе с деревом дочерних. Идемпотентен: повторный вызов и вызов после
    /// завершения — не ошибка. Прерывание на середине <see cref="BuildAction.DevDeploy"/> может
    /// оставить в <c>res_mods</c> половину файлов — UI обязан после отмены заново опросить
    /// <see cref="IDeployProbe"/>.
    /// </summary>
    void Cancel();
}
