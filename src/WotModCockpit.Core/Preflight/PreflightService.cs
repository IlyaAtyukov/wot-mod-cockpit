using WotModCockpit.Core.ApiIndex;
using ClientIndex = WotModCockpit.Core.ApiIndex.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Preflight;

/// <summary>
/// Реализация <see cref="IPreflightService"/> поверх индекса этапа 2.
///
/// <para>Оракул клиентского API кешируется по ЭКЗЕМПЛЯРУ индекса: <see cref="ApiIndexService"/>
/// при пересборке создаёт новый неизменяемый <see cref="ApiIndex"/>, поэтому сравнение
/// по ссылке — точный и бесплатный признак «индекс сменился, оракул устарел».</para>
///
/// <para>Работа идёт в пуле потоков (<see cref="Task.Run(Action)"/>): анализ упирается
/// в чтение файлов и не должен занимать поток UI. Для эталонного шаблона это единицы
/// миллисекунд, но у мода может быть и сотня файлов.</para>
/// </summary>
public sealed class PreflightService : IPreflightService
{
    private readonly IApiIndexService _index;
    private readonly SourceFileService _files;
    private readonly PreflightAnalyzer _analyzer;
    private readonly object _gate = new();

    private ClientIndex? _oracleFor;
    private ClientApiOracle? _oracle;

    /// <summary>Создаёт службу.</summary>
    /// <param name="index">Индекс клиента (этап 2).</param>
    /// <param name="files">Кеш чтения исходников.</param>
    /// <param name="analyzer">Анализатор; <c>null</c> = набор правил по умолчанию.</param>
    public PreflightService(IApiIndexService index, SourceFileService files, PreflightAnalyzer? analyzer = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(files);

        _index = index;
        _files = files;
        _analyzer = analyzer ?? new PreflightAnalyzer();
    }

    /// <inheritdoc/>
    public IReadOnlyList<IPreflightRule> Rules => _analyzer.Rules;

    /// <inheritdoc/>
    public PreflightOptions Options { get; } = new();

    /// <inheritdoc/>
    public Task<PreflightResult> AnalyzeAsync(ModProject project, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(project);
        return AnalyzeAsync(project.SrcDir, project.ProjectDir, project, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<PreflightResult> AnalyzeAsync(string sourceRoot, string? projectDir = null, ModProject? project = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceRoot);

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(sourceRoot)) return PreflightResult.Empty;

            ClientApiOracle oracle = GetOracle();
            cancellationToken.ThrowIfCancellationRequested();

            PreflightContext context = PreflightAnalyzer.BuildContext(sourceRoot, projectDir, oracle, project, Options);
            cancellationToken.ThrowIfCancellationRequested();

            return _analyzer.Analyze(context);
        }, cancellationToken);
    }

    /// <summary>Оракул для текущего индекса; пересобирается только при смене индекса.</summary>
    /// <returns>Оракул.</returns>
    public ClientApiOracle GetOracle()
    {
        ClientIndex current = _index.Index;
        lock (_gate)
        {
            if (_oracle is not null && ReferenceEquals(_oracleFor, current)) return _oracle;
            // Дерево, извлечённое кокпитом, несёт свой список модулей дополнительных пакетов той
            // же версии клиента; без него — встроенный список. Модули, которые не декомпилировались,
            // в игре тоже есть — для preflight они как модули дополнительных пакетов, без исходника.
            _oracle = ClientApiOracle.Create(current, _files, ClientApiOracle.CombineExtraModules(
                Migration.ClientSourceTrees.ReadExtraModules(current.RootPath),
                Migration.ClientSourceTrees.ReadFailedModules(current.RootPath)));
            _oracleFor = current;
            return _oracle;
        }
    }
}
