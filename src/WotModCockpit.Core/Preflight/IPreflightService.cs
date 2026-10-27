using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Фасад preflight для UI: асинхронный прогон правил по проекту мода.
///
/// <para>Обязан корректно работать <b>без дерева исходников клиента</b>: тогда правила
/// WOT002/003/004 молчат (сверять не с чем), а остальные 18 работают как обычно.</para>
/// </summary>
public interface IPreflightService
{
    /// <summary>Правила, которые может запустить служба.</summary>
    IReadOnlyList<IPreflightRule> Rules { get; }

    /// <summary>Настройки прогона: выключенные правила, список декораторов-предохранителей и т. п.</summary>
    PreflightOptions Options { get; }

    /// <summary>
    /// Проверяет проект мода. Не бросает: отсутствие каталога, нечитаемые файлы и упавшее
    /// правило превращаются в находки, а не в исключение.
    /// </summary>
    /// <param name="project">Проект мода.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат.</returns>
    Task<PreflightResult> AnalyzeAsync(ModProject project, CancellationToken cancellationToken = default);

    /// <summary>Проверяет произвольный каталог с исходниками мода.</summary>
    /// <param name="sourceRoot">Каталог <c>src</c>.</param>
    /// <param name="projectDir">Корень проекта для относительных путей; <c>null</c> = <paramref name="sourceRoot"/>.</param>
    /// <param name="project">Паспорт мода, если он известен.</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <returns>Результат.</returns>
    Task<PreflightResult> AnalyzeAsync(string sourceRoot, string? projectDir = null, ModProject? project = null,
        CancellationToken cancellationToken = default);
}
