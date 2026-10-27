using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Регрессия на гонку загрузки доменных каталогов.
/// <para>
/// Первый каталог строится около секунды: это ВТОРОЙ полный проход по дереву исходников,
/// отдельный от индекса символов (замерено: 657-924 мс на 8052 файлах). Всё это время
/// пользователь может щёлкать вкладки «События» / «Сервисы» / «Горячие точки».
/// </para>
/// <para>
/// Без номера поколения ответ на УСТАРЕВШИЙ запрос дописывался в общий список поверх нового,
/// и на настоящих данных вкладка «Сервисы» показывала 1245 записей вместо 215 — склейку
/// 1030 событий с 215 сервисами, причём строка состояния честно писала «показано 1245 из 1245».
/// </para>
/// </summary>
public sealed class ApiBrowserCatalogRaceTests : IDisposable
{
    private readonly UiThread _ui = new();
    private ApiBrowserViewModel? _vm;

    /// <summary>Гасит модель и UI-поток.</summary>
    public void Dispose()
    {
        if (_vm is not null)
        {
            try
            {
                _ui.Invoke(() => _vm!.Dispose());
            }
            catch (Exception)
            {
                // закрытие теста не должно маскировать причину падения самого теста
            }
        }

        _ui.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Переключение вкладки, пока первый каталог ещё не доехал, обязано оставить в списке
    /// РОВНО последний запрошенный каталог, а не сумму двух.
    /// </summary>
    [Fact]
    public void SwitchingPaneWhileCatalogLoads_KeepsOnlyLastCatalog()
    {
        var backend = new SlowCatalogBackend();

        // События отдаём медленно (как настоящий первый проход по дереву), сервисы — сразу.
        backend.Delays[ApiCatalogKind.Events] = TimeSpan.FromMilliseconds(400);
        backend.Entries[ApiCatalogKind.Events] = Make(ApiCatalogKind.Events, "onAvatarBecomePlayer", "onArenaPeriodChange");
        backend.Entries[ApiCatalogKind.Services] = Make(ApiCatalogKind.Services, "IItemsCache");

        ApiBrowserViewModel vm = Create(backend);

        // Один тик UI: пользователь щёлкнул «События» и сразу «Сервисы».
        _ui.Invoke(() =>
        {
            vm.Pane = ApiBrowserPane.Events;
            vm.Pane = ApiBrowserPane.Services;
        });

        // Ждём дольше медленного каталога, чтобы устаревший ответ ТОЧНО успел прийти.
        _ui.WaitUntil(
            () => vm.CatalogItems.Count > 0,
            "каталог сервисов загрузился",
            TimeSpan.FromSeconds(10));
        _ui.Pump(rounds: 8, pauseMs: 80);

        _ui.Invoke(() =>
        {
            Assert.Equal(ApiBrowserPane.Services, vm.Pane);
            Assert.Single(vm.CatalogItems);
            Assert.Equal("IItemsCache", vm.CatalogItems[0].Entry.Name);
            Assert.DoesNotContain(vm.CatalogItems, i => i.Entry.Kind == ApiCatalogKind.Events);
            Assert.Contains("1", vm.CatalogStatus, StringComparison.Ordinal);
        });
    }

    private ApiBrowserViewModel Create(IApiBrowserBackend backend)
    {
        ApiBrowserViewModel vm = _ui.Invoke(() =>
        {
            var model = new ApiBrowserViewModel(backend, new NullStore(), new NoEditor());
            model.AttachSettings(new CockpitSettings { WotSrcDir = string.Empty }, () => { });
            model.Initialize();
            return model;
        });

        _vm = vm;
        return vm;
    }

    private static List<ApiCatalogEntry> Make(ApiCatalogKind kind, params string[] names)
    {
        var list = new List<ApiCatalogEntry>();
        foreach (string n in names)
        {
            list.Add(new ApiCatalogEntry
            {
                Kind = kind,
                Name = n,
                XrefName = n,
                RelativePath = "client/PlayerEvents.py",
                VirtualPath = "scripts/client/PlayerEvents.py",
                FullPath = @"C:\src\client\PlayerEvents.py",
                Line = 1
            });
        }

        return list;
    }

    /// <summary>Шов, у которого каждый каталог отдаётся со своей задержкой.</summary>
    private sealed class SlowCatalogBackend : IApiBrowserBackend
    {
        public Dictionary<ApiCatalogKind, TimeSpan> Delays { get; } = new();

        public Dictionary<ApiCatalogKind, List<ApiCatalogEntry>> Entries { get; } = new();

        public ApiIndexStatus Status { get; } = new()
        {
            State = ApiIndexState.Ready,
            TreePath = @"C:\src",
            FilesTotal = 8052,
            SymbolCount = 166_436
        };

        public event EventHandler<ApiIndexStatus>? StatusChanged;

        public Task<ApiIndexStatus> ReindexAsync(string? treePath, CancellationToken cancellationToken)
        {
            StatusChanged?.Invoke(this, Status);
            return Task.FromResult(Status);
        }

        public Task<IReadOnlyList<ApiSymbol>> SearchAsync(string query, ApiSearchFilter filter, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiSymbol>>(Array.Empty<ApiSymbol>());

        public Task<IReadOnlyList<ApiTreeNode>> GetTreeChildrenAsync(string? parentRelativePath, ApiSearchFilter filter, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiTreeNode>>(Array.Empty<ApiTreeNode>());

        public Task<IReadOnlyList<ApiSymbol>> GetFileSymbolsAsync(string relativePath, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ApiSymbol>>(Array.Empty<ApiSymbol>());

        public Task<ApiXrefResult> FindReferencesAsync(ApiXrefRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new ApiXrefResult(
                request.Name,
                Array.Empty<ApiXrefHit>(),
                0,
                0,
                0,
                Array.Empty<string>(),
                TimeSpan.Zero));

        public Task<ApiSymbol?> ResolveVirtualPathAsync(string virtualPath, int line, CancellationToken cancellationToken)
            => Task.FromResult<ApiSymbol?>(null);

        public async Task<IReadOnlyList<ApiCatalogEntry>> GetCatalogAsync(ApiCatalogKind kind, CancellationToken cancellationToken)
        {
            if (Delays.TryGetValue(kind, out TimeSpan d) && d > TimeSpan.Zero)
                await Task.Delay(d, cancellationToken).ConfigureAwait(false);

            return Entries.TryGetValue(kind, out List<ApiCatalogEntry>? list)
                ? list
                : Array.Empty<ApiCatalogEntry>();
        }

        public ApiSnippet? BuildSnippet(ApiSnippetRequest request) => null;
    }

    private sealed class NullStore : ISettingsStore
    {
        public string SettingsPath => Path.Combine(Path.GetTempPath(), "cockpit-race-test-settings.json");

        public CockpitSettings Load() => new();

        public void Save(CockpitSettings settings)
        {
            // тест не должен писать в профиль пользователя
        }
    }

    private sealed class NoEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenFolder(string path)
        {
        }

        public void OpenAt(string filePath, int line)
        {
        }
    }
}
