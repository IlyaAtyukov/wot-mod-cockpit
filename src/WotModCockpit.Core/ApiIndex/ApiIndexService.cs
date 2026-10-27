using WotModCockpit.Core.Abstractions;

namespace WotModCockpit.Core.ApiIndex;

/// <summary>
/// Реализация фасада браузера API.
///
/// <para>Правила, которым подчинён этот класс:</para>
/// <list type="bullet">
///   <item>Сборка всегда идёт в фоне (719 мс холодно, 105-127 мс тепло) и никогда не блокирует UI.</item>
///   <item>Ни один сценарий не бросает наружу: нет дерева — <see cref="ApiIndexBuildState.NotConfigured"/>,
///         нет каталога — <see cref="ApiIndexBuildState.RootMissing"/>, отмена —
///         <see cref="ApiIndexBuildState.Cancelled"/>.</item>
///   <item>Индекс подменяется целиком и атомарно, поэтому читатели (поиск из UI-потока)
///         не видят полусобранного состояния и блокировок не требуют.</item>
/// </list>
/// </summary>
public sealed class ApiIndexService : IApiIndexService, IDisposable
{
    private readonly ISettingsStore _settings;
    private readonly SourceFileService _files;
    private readonly ApiIndexBuilder _builder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile ApiIndex _index = ApiIndex.Empty;
    private volatile ApiIndexBuildStatus _status = ApiIndexBuildStatus.NotBuilt;
    private volatile IReadOnlyList<ApiIndexBuildError> _errors = [];
    private CancellationTokenSource? _cts;
    private bool _disposed;

    /// <summary>Создаёт сервис.</summary>
    /// <param name="settings">Хранилище настроек — оттуда берётся <c>WotSrcDir</c>.</param>
    /// <param name="files">Служба чтения файлов с кешем.</param>
    /// <param name="options">Настройки индексации; <c>null</c> = по умолчанию.</param>
    public ApiIndexService(ISettingsStore settings, SourceFileService files, ApiIndexOptions? options = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _builder = new ApiIndexBuilder(options);
    }

    /// <inheritdoc/>
    public ApiIndex Index => _index;

    /// <inheritdoc/>
    public ApiIndexBuildStatus Status => _status;

    /// <inheritdoc/>
    public IReadOnlyList<ApiIndexBuildError> Errors => _errors;

    /// <inheritdoc/>
    public event EventHandler<ApiIndexBuildStatus>? StatusChanged;

    /// <inheritdoc/>
    public async Task<ApiIndexBuildStatus> RebuildAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = ResolveRoot(sourceRoot);

        // Новая сборка отменяет предыдущую: пользователь мог сменить путь, пока шла старая.
        Cancel();

        bool acquired = false;
        CancellationTokenSource? cts = null;

        try
        {
            // Ожидание семафора тоже отменяемо, поэтому оно ВНУТРИ try: иначе уже отменённый
            // токен выбросил бы OperationCanceledException мимо обработчика, и вызывающий
            // получил бы исключение вместо статуса «отменено».
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _cts = cts;

            SourceTreeLayout layout = SourceTreeLayout.Inspect(root);
            if (!layout.IsConfigured)
                return SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.NotConfigured, string.Empty, 0, 0, 0, TimeSpan.Zero, layout.Describe()), ApiIndex.Empty, []);

            if (!layout.Exists)
                return SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.RootMissing, layout.Path, 0, 0, 0, TimeSpan.Zero, layout.Describe()), ApiIndex.Empty, []);

            // Каталог существует, но внутри нет ни client\, ни common\ — это не дамп uncompyle6.
            // Индексировать его нельзя молча: получится «Ready, 0 символов в 0 файлах», что в UI
            // неотличимо от успеха, и человек, промахнувшийся папкой в диалоге «выбрать папку…»,
            // остаётся без единой подсказки — поиск просто ничего не находит. Это самая частая
            // ошибка первого запуска, поэтому она обязана быть отдельным состоянием с инструкцией.
            if (!layout.LooksLikeClientSource)
                return SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.RootMissing, layout.Path, 0, 0, 0, TimeSpan.Zero, layout.Describe()), ApiIndex.Empty, []);

            SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.Building, layout.Path, 0, 0, 0, TimeSpan.Zero,
                $"Индексация дерева исходников: {layout.Path}"), null, null);

            ApiIndexBuildResult result = await _builder.BuildAsync(layout.Path, progress, cts.Token).ConfigureAwait(false);
            _files.Clear();

            string message = $"Индекс готов: {result.Index.Files.Count} файлов, {result.Index.SymbolCount} символов за {result.Elapsed.TotalMilliseconds:F0} мс";
            if (result.Errors.Count > 0) message += $"; пропущено файлов: {result.Errors.Count}";

            return SetStatus(new ApiIndexBuildStatus(
                ApiIndexBuildState.Ready,
                layout.Path,
                result.Index.Files.Count,
                result.Index.SymbolCount,
                result.Errors.Count,
                result.Elapsed,
                message), result.Index, result.Errors);
        }
        catch (OperationCanceledException)
        {
            // Отмена НЕ имеет права уничтожать уже работающий индекс: «прервать» означает
            // «не строй новый», а не «выключи браузер API». Раньше сюда передавался
            // ApiIndex.Empty, и после нажатия «отменить» пользователь оставался без индекса
            // вовсе — при том что предыдущий был цел и полностью пригоден.
            ApiIndex previous = _index;
            if (!previous.IsEmpty)
            {
                ApiIndexBuildStatus before = _status;
                return SetStatus(new ApiIndexBuildStatus(
                    ApiIndexBuildState.Ready,
                    before.RootPath.Length > 0 ? before.RootPath : previous.RootPath,
                    previous.Files.Count,
                    previous.SymbolCount,
                    before.ErrorCount,
                    before.BuildTime,
                    $"Индексация прервана, показан предыдущий индекс: {previous.Files.Count} файлов, {previous.SymbolCount} символов"),
                    null, null);
            }

            return SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.Cancelled, root, 0, 0, 0, TimeSpan.Zero,
                "Индексация отменена"), ApiIndex.Empty, []);
        }
        catch (Exception ex)
        {
            // Браузер API не имеет права уронить кокпит: этап 1 обязан продолжать работать.
            return SetStatus(new ApiIndexBuildStatus(ApiIndexBuildState.Failed, root, 0, 0, 0, TimeSpan.Zero,
                $"Индексация не удалась: {ex.GetType().Name}: {ex.Message}"), ApiIndex.Empty, []);
        }
        finally
        {
            _cts = null;
            cts?.Dispose();
            if (acquired) _gate.Release();
        }
    }

    /// <inheritdoc/>
    public Task<ApiIndexBuildStatus> EnsureBuiltAsync(string? sourceRoot = null, IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string root = ResolveRoot(sourceRoot);
        ApiIndexBuildStatus current = _status;

        if (current.IsReady && string.Equals(current.RootPath, SourceTreeLayout.Inspect(root).Path, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(current);

        return RebuildAsync(root, progress, cancellationToken);
    }

    /// <inheritdoc/>
    public void Cancel()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { /* сборка уже завершилась сама */ }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ApiSymbol> Search(string? query, SymbolSearchOptions? options = null) =>
        SymbolSearch.Search(_index, query, options);

    /// <inheritdoc/>
    public Task<XrefResult> FindReferencesAsync(XrefQuery query, CancellationToken cancellationToken = default) =>
        XrefSearch.FindAsync(_index, query, cancellationToken);

    /// <inheritdoc/>
    public ApiSymbol? ResolveTracebackFrame(string? virtualPath, int line) =>
        _index.EnclosingSymbol(virtualPath, line);

    /// <inheritdoc/>
    public SourceSnippet? GetSnippet(ApiSymbol symbol, int before = 0, int after = 12)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return _files.GetSnippet(symbol.FullPath, symbol.Line, before, after);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ApiSymbol> Children(ApiSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (symbol.SymbolId < 0 || symbol.SymbolId >= _index.SymbolCount) return [];
        return _index.Children(symbol.SymbolId);
    }

    /// <summary>Освобождает семафор и токен отмены.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel();
        _gate.Dispose();
    }

    private string ResolveRoot(string? sourceRoot)
    {
        if (!string.IsNullOrWhiteSpace(sourceRoot)) return sourceRoot;

        try { return _settings.Load().WotSrcDir ?? string.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private ApiIndexBuildStatus SetStatus(ApiIndexBuildStatus status, ApiIndex? index, IReadOnlyList<ApiIndexBuildError>? errors)
    {
        if (index is not null) _index = index;
        if (errors is not null) _errors = errors;
        _status = status;
        StatusChanged?.Invoke(this, status);
        return status;
    }
}
