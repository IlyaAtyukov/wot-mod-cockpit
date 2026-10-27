using System.Diagnostics;

namespace WotModCockpit.Core.ApiCatalog;

/// <summary>
/// Реализация <see cref="IApiCatalogService"/>: один лексический проход по дереву, поверх него —
/// три каталога.
/// <para>
/// Порядок построения важен: сначала сервисы (они дают карту <c>skeletons/</c> и таблицу слотов
/// боевой сессии), потом события (резолверы R2 и R3 опираются на эту карту), потом сверка
/// горячих точек.
/// </para>
/// <para>
/// Дискового кеша здесь нет и не будет. Замерено: полный проход стоит доли секунды, а кеш даёт
/// выигрыш ~70 мс на тёплом старте, стоит 22-26 мс на одну только валидацию и приносит целый класс
/// багов рассогласования формата. Построение идёт в фоне, поэтому этих долей секунды никто не видит.
/// </para>
/// </summary>
public sealed class ApiCatalogService : IApiCatalogService
{
    private readonly ClientSourceScanner _scanner = new();
    private readonly ServiceCatalogBuilder _services = new();
    private readonly EventCatalogBuilder _events = new();
    private readonly HookCatalog _hooks = new();
    private volatile ApiCatalogSnapshot _current = ApiCatalogSnapshot.NotBuiltYet;

    /// <inheritdoc />
    public ApiCatalogSnapshot Current => _current;

    /// <inheritdoc />
    public event EventHandler<ApiCatalogSnapshot>? CatalogChanged;

    /// <inheritdoc />
    public SnippetGenerator Snippets { get; } = new();

    /// <inheritdoc />
    public Task<ApiCatalogSnapshot> BuildAsync(string? wotSrcDir, CancellationToken ct = default)
        => Task.Run(() => Build(wotSrcDir, ct), ct);

    /// <summary>
    /// Синхронное построение — то же самое, что <see cref="BuildAsync"/>, но без ухода в пул потоков.
    /// Используется тестами и вызывающим кодом, который уже находится в фоне.
    /// </summary>
    /// <param name="wotSrcDir">Корень дампа uncompyle6 или <c>null</c>.</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Построенный снимок.</returns>
    public ApiCatalogSnapshot Build(string? wotSrcDir, CancellationToken ct = default)
    {
        string root = wotSrcDir ?? string.Empty;
        var sw = Stopwatch.StartNew();

        ApiCatalogSnapshot snapshot;
        try
        {
            ClientSourceScan scan = _scanner.Scan(root, ct);
            if (scan.IsEmpty)
            {
                snapshot = ApiCatalogSnapshot.NoSourcesAt(root);
            }
            else
            {
                ServiceCatalog services = _services.Build(scan);
                EventCatalog events = _events.Build(scan, services);
                IReadOnlyList<HookPoint> hooks = _hooks.Verify(scan);
                sw.Stop();
                snapshot = new ApiCatalogSnapshot(
                    ApiCatalogStatus.Ready,
                    root,
                    events,
                    services,
                    hooks,
                    scan.Files.Count,
                    scan.SkippedFiles,
                    sw.Elapsed);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            sw.Stop();
            snapshot = new ApiCatalogSnapshot(
                ApiCatalogStatus.Failed,
                root,
                EventCatalog.Empty,
                ServiceCatalog.Empty,
                Array.Empty<HookPoint>(),
                0,
                0,
                sw.Elapsed,
                e.Message);
        }

        _current = snapshot;
        CatalogChanged?.Invoke(this, snapshot);
        return snapshot;
    }
}
