using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.App.Stage3;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Снимки окон для README (<c>docs/*.png</c>) — с настоящей разметки и настоящих моделей,
/// но на ВЫДУМАННЫХ данных: фейковая игра, три выдуманных проекта <c>ru.example.*</c>,
/// выдуманные чужие моды, крошечное синтетическое дерево «исходников клиента», написанное
/// прямо здесь, синтетический python.log и фикстура <c>Resources/modsettings.sample.json</c>.
///
/// <para><b>Как запустить.</b> Без переменной <c>WOTCOCKPIT_DOC_SCREENSHOTS</c> тесты пропускаются
/// с причиной; переменная — папка, куда класть картинки (существующие файлы перезаписываются):</para>
/// <code>
/// $env:WOTCOCKPIT_DOC_SCREENSHOTS = "$PWD\docs"
/// dotnet test tests/WotModCockpit.Tests/WotModCockpit.Tests.csproj --filter "FullyQualifiedName~DocScreenshotTests"
/// </code>
///
/// <para><b>Пути на картинках.</b> Всё лежит во временной папке, но в шапке окна должно быть
/// видно что-то нейтральное, а не <c>C:\Users\&lt;имя&gt;\AppData\Local\Temp\…</c>. Поэтому
/// раскладка временной папки повторяет обычную машину (<c>Games\Tanki</c>, <c>Users\dev\repos</c>,
/// <c>Users\dev\wot-src</c>), а перед снимком у видимого текста корень временной папки
/// заменяется на <c>C:</c> — только на время снимка, привязки потом возвращаются. После замены
/// весь видимый текст окна проверяется: любой абсолютный путь вне нейтральных корней, имя
/// пользователя, имя машины, временная папка или имя настоящего мода роняют тест, и картинка
/// не пишется.</para>
///
/// <para><b>Чего здесь нет намеренно.</b> Окно не читает <c>%APPDATA%</c>: настройки — свой
/// settings.json во временной папке, журнал операций модов — там же, а живой
/// <c>modsettings.dat</c> игрока подменён фикстурой ДО первого чтения (обработчик
/// <c>Loaded</c> главного окна снимается, и его шаги повторяются здесь же, но дизайнер настроек
/// получает читатель фикстуры). В каталог настоящей игры ничего не пишется и не читается.
/// С машины берётся только версия найденного Python для строки состояния (путь к нему на снимке
/// нейтральный) — и рабочий стол: подтверждение «Выключить мод» — системное окно, оно на секунду
/// появляется на экране, снимается и закрывается кнопкой «Нет».</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class DocScreenshotTests
{
    /// <summary>Переменная окружения: папка для картинок. Пусто — тесты пропускаются.</summary>
    public const string OutputVariable = "WOTCOCKPIT_DOC_SCREENSHOTS";

    private static readonly DateTime Today = new(2026, 10, 27, 21, 30, 0);

    /// <summary>
    /// Главное окно на всех пяти экранах: сборка и лог с панелью Problems, браузер API (поиск и
    /// каталог событий), рецепты, дизайнер настроек (своя форма и импорт чужого шаблона),
    /// экран «Моды» и его подтверждение «Выключить мод».
    /// </summary>
    [SkippableFact]
    [Trait("Env", "DocScreenshots")]
    public void MainWindow_AllScreens()
    {
        string outDir = RequireOutputDir();

        using var world = new DocWorld();
        world.BuildMainWorld();

        using CockpitSession session = CockpitSession.Open(world);

        // ---------------------------------------------------------------- 1. сборка и лог
        session.WaitForStartup();
        session.Shot(Path.Combine(outDir, "main-window.png"));

        // ---------------------------------------------------------------- 2. Problems
        session.Ui(() =>
        {
            ProblemsViewModel problems = session.Stage3.Problems;
            problems.Selected = problems.Problems.FirstOrDefault(p => p.Code == "WOT003") ?? problems.Problems.FirstOrDefault();
            ((RowDefinition)session.Window.FindName("ProblemsRow")).Height = new GridLength(330);
        });
        session.Settle(600);
        session.ShotProblemsDock(Path.Combine(outDir, "problems.png"));
        session.Ui(() => ((RowDefinition)session.Window.FindName("ProblemsRow")).Height = new GridLength(230));

        // ---------------------------------------------------------------- 3. рецепты
        session.Ui(() => session.Stage3.IsRecipesScreen = true);
        session.WaitUntil(() => session.Stage3.Recipes.Recipes.Count > 0, "каталог рецептов");
        session.Ui(() =>
        {
            RecipesViewModel recipes = session.Stage3.Recipes;
            recipes.Selected = recipes.Recipes.FirstOrDefault(r => r.Id == "R05") ?? recipes.Recipes.First();
        });
        session.WaitUntil(() => session.Stage3.Recipes.HasCode, "код рецепта");
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "recipes.png"));

        // ---------------------------------------------------------------- 4. браузер API: поиск
        session.Ui(() =>
        {
            session.Main.IsApiScreen = true;
            session.Api.Pane = ApiBrowserPane.Search;
            session.Api.SearchText = "onAvatarReady";
        });
        session.WaitUntil(() => session.Api.Results.Count > 0 && !session.Api.IsSearching, "результаты поиска");
        session.Ui(() => session.Api.SelectedResult =
            session.Api.Results.FirstOrDefault(r => r.Header.Contains("_PlayerEvents.onAvatarReady", StringComparison.Ordinal))
            ?? session.Api.Results.First());
        session.WaitUntil(() => session.Api.XrefHits.Count > 0 && !session.Api.IsXrefRunning, "список «где это вызывается»");
        session.ShowSnippet(ApiSnippetKind.EventSubscription, fromCatalog: false);
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "api-browser.png"));

        // ---------------------------------------------------------------- 5. браузер API: события
        session.Ui(() =>
        {
            session.Api.Pane = ApiBrowserPane.Events;
            session.Api.CatalogFilterText = "бой";
        });
        session.WaitUntil(() => session.Api.CatalogItems.Count > 0 && !session.Api.IsCatalogLoading, "каталог событий");
        session.Settle(400);
        session.Ui(() => session.Api.SelectedCatalogItem = session.Api.CatalogItems.First());
        session.WaitUntil(() => session.Api.XrefHits.Count > 0 && !session.Api.IsXrefRunning, "вызовы события");
        session.ShowSnippet(ApiSnippetKind.EventSubscription, fromCatalog: true);
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "api-browser-events.png"));

        // ---------------------------------------------------------------- 6. дизайнер: своя форма
        session.Ui(() =>
        {
            session.Main.IsSettingsScreen = true;
            SettingsDesignerViewModel d = session.Designer;
            d.LoadSpec(DocWorld.ClockForm());
            d.Selected = d.Column1.FirstOrDefault(c => c.VarName == "opacity") ?? d.Column1.LastOrDefault();
            d.Revalidate();
            Assert.True(d.CanGenerate, "форма для снимка не генерируется: " + d.IssuesHeader);
            d.GenerateCodeCommand.Execute(null);
            d.SelectedArtifact = d.Artifacts.FirstOrDefault(a => a.Title == "settings_schema.py") ?? d.SelectedArtifact;
        });
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "settings-designer.png"));

        // ---------------------------------------------------------------- 7. дизайнер: импорт чужого шаблона
        session.Ui(() =>
        {
            SettingsDesignerViewModel d = session.Designer;
            d.ShowImport = true;
            d.SelectedLiveTemplate = d.LiveTemplates.First(t => t.Linkage == DocWorld.ImportedLinkage);
            d.ImportFromLiveCommand.Execute(null);   // подтверждение — «да», файл фикстуры только читается
            Assert.Equal(DocWorld.ImportedLinkage, d.Linkage);
            d.Revalidate();
        });
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "settings-designer-import.png"));

        // ---------------------------------------------------------------- 8. экран «Моды»
        session.Ui(() => session.Main.IsModsScreen = true);
        session.WaitUntil(() => session.Mods.Mods.Count > 0 && !session.Mods.IsScanning, "список модов");
        session.Ui(() => session.Mods.Selected = session.Mods.Mods.First(r => r.FileName == DocWorld.ClockFile));
        session.Settle(900);
        session.Shot(Path.Combine(outDir, "mods-screen.png"));

        // ---------------------------------------------------------------- 9. подтверждение «Выключить мод»
        (string caption, string body) = session.CaptureDisableConfirmation();
        NativeMessageBoxShot.Save(caption, world.ToDisplay(body), Path.Combine(outDir, "mods-confirm.png"), world);
    }

    /// <summary>Диалог «Собрать сетап из своих модов» на трёх выдуманных проектах.</summary>
    [SkippableFact]
    [Trait("Env", "DocScreenshots")]
    public void SetupBundleDialog_Shot()
    {
        string outDir = RequireOutputDir();

        using var world = new DocWorld();
        world.BuildMainWorld();

        string stub = Path.Combine(world.Root, "Users", "dev", "tools", "WotModCockpit.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(stub)!);
        byte[] bytes = new byte[2048];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(stub, bytes);

        string output = Path.Combine(world.Root, "Users", "dev", "Desktop");
        Directory.CreateDirectory(output);

        IReadOnlyList<SetupBundleCandidate> candidates = SetupBundleCandidates.Read(world.ProjectDirs);

        var vm = new SetupBundleViewModel(candidates, new SetupBundleContext
        {
            StubPath = stub,
            GameRoot = world.GameDir,
            GameVersion = DocWorld.GameVersion,
            BuildProject = _ => Task.FromResult(ModsBuildOutcome.NotStarted("сборка в снимке не нужна")),
            Confirm = (_, _) => false,
            Settings = new SetupBundleSettings
            {
                OutputDir = output,
                Name = "Моды для друзей",
                Rebuild = false,
            },
            Now = () => Today,
        });

        WpfHost.Invoke(() =>
        {
            var dialog = new SetupBundleDialog { DataContext = vm };
            ShowOffscreen(dialog);
            try
            {
                Task build = vm.BuildCommand.ExecuteAsync(null);
                PumpUntil(() => build.IsCompleted, TimeSpan.FromSeconds(20));
                Pump();
                Assert.True(vm.LastRunSucceeded, "сетап для снимка не собрался: " + vm.StatusText);
                DocShots.Save(dialog, Path.Combine(outDir, "setup-bundle.png"), world, crop: null);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    /// <summary>
    /// Окно «Переход на новую версию клиента» после проверки: два мода совместимы и перенесены,
    /// у одного проблема совместимости, и он остался на месте.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "DocScreenshots")]
    public void ClientMigrationDialog_Shot()
    {
        string outDir = RequireOutputDir();

        using var world = new DocWorld();
        world.BuildMigrationWorld();

        var library = new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
        {
            OperationLogPath = Path.Combine(world.SettingsDir, ModLibraryService.OperationLogFileName),
            WriteOperationLog = false,
        });

        var game = new GameInstall(
            world.GameDir,
            DocWorld.GameVersion,
            world.ModsDir,
            Path.Combine(world.GameDir, "res_mods", DocWorld.GameVersion),
            Path.Combine(world.GameDir, "python.log"));

        ClientMigrationState state = ClientMigrationDetector.Detect(game, world.ProjectDirs);
        Assert.True(state.NeedsMigration, "в фейковой игре не нашлось своих модов прошлой версии");

        var store = new JsonProjectStore();
        var vm = new ClientMigrationViewModel(new ClientMigrationContext
        {
            State = state,
            CurrentTree = world.ClientSource145,
            TemplateDir = null,
            Extract = (target, progress, _) =>
            {
                progress.Report(new ClientSourceProgress("декомпиляция", 214, 214));
                Directory.CreateDirectory(Path.Combine(target, "client"));
                return Task.FromResult(new ClientSourceExtraction(target, DocWorld.GameVersion, 214, 214, 0, 0, 0, TimeSpan.FromSeconds(41)));
            },
            Check = (s, newTree, oldTree, progress, _) =>
            {
                progress.Report("сравнение с прошлой версией клиента");
                var candidates = s.Pending.Select(m =>
                {
                    ModProject project = store.Load(m.Mine!.ProjectDir)!;
                    bool broken = project.ModId == DocWorld.NotesId;
                    var issues = broken
                        ? new List<CompatibilityIssue>
                        {
                            new(true, "сигнатура", "у PlayerEvents._PlayerEvents.onArenaPeriodChange поменялось число аргументов"),
                        }
                        : new List<CompatibilityIssue>();
                    var report = new ModCompatibilityReport(project.ProjectDir, project.ModId, project.DisplayName,
                        broken ? CompatibilityVerdict.Incompatible : CompatibilityVerdict.Compatible, issues, ["PlayerEvents", "Avatar"], []);
                    return new MigrationCandidate(m, project, report, null);
                }).ToList();

                return Task.FromResult(new MigrationCheck(newTree, oldTree, ClientSourceTrees.VersionOf(oldTree), candidates, TimeSpan.FromSeconds(3))
                {
                    BaselineIsPreviousVersion = oldTree is not null && ClientSourceTrees.IsForVersion(oldTree, s.PreviousVersion!),
                    NewTreeModules = 12,
                });
            },
            BuildProject = (_, _) => Task.FromResult(ModsBuildOutcome.NotStarted("сборка в снимке не нужна")),
            Install = (g, artifact) => library.Install(g, artifact, ModsViewModel.MigrationInstallOptions),
            UseClientSources = _ => Task.CompletedTask,
            Journal = _ => { },
            Confirm = (_, _) => false,
            Now = () => Today,
        });

        WpfHost.Invoke(() =>
        {
            var dialog = new ClientMigrationDialog { DataContext = vm };
            ShowOffscreen(dialog);
            try
            {
                Task run = vm.StartCommand.ExecuteAsync(null);
                PumpUntil(() => run.IsCompleted, TimeSpan.FromSeconds(20));
                Pump();

                // Журнал прокручен к концу, как у живого окна после перехода: видны переносы.
                var log = (ListBox)dialog.FindName("LogList");
                if (log.Items.Count > 0)
                {
                    log.ScrollIntoView(log.Items[^1]);
                }

                Pump();
                DocShots.Save(dialog, Path.Combine(outDir, "client-migration.png"), world, crop: null);
            }
            finally
            {
                dialog.Close();
            }
        });
    }

    // ================================================================= общие помощники

    private static string RequireOutputDir()
    {
        string? dir = Environment.GetEnvironmentVariable(OutputVariable);
        Skip.If(string.IsNullOrWhiteSpace(dir),
            $"снимки окон для README пишутся только по переменной {OutputVariable}=<папка>");
        string full = Path.GetFullPath(dir!.Trim());
        Directory.CreateDirectory(full);
        return full;
    }

    private static void ShowOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -32000;
        window.Top = -32000;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
        window.Show();
        Pump();
    }

    /// <summary>Крутит очередь диспетчера ИЗНУТРИ потока WPF, пока не выполнится условие.</summary>
    private static void PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > timeout)
            {
                throw new TimeoutException("условие снимка не выполнилось за " + timeout.TotalSeconds + " с");
            }

            Pump();
            Thread.Sleep(15);
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
    }

    // ================================================================= сеанс главного окна

    /// <summary>
    /// Главное окно с контейнером, собранным как в <see cref="AppServices"/>, но со своими
    /// настройками, тихим редактором и библиотекой модов без журнала в профиле пользователя.
    /// Живёт в потоке <see cref="WpfHost"/>; ожидания идут из потока теста, чтобы окно в это
    /// время жило своей жизнью (таймеры, фоновые задачи, привязки).
    /// </summary>
    private sealed class CockpitSession : IDisposable
    {
        private readonly DocWorld _world;
        private ServiceProvider _provider = null!;

        private CockpitSession(DocWorld world) => _world = world;

        public MainWindow Window { get; private set; } = null!;

        public MainViewModel Main { get; private set; } = null!;

        public Stage3ViewModel Stage3 { get; private set; } = null!;

        public SettingsDesignerViewModel Designer { get; private set; } = null!;

        public ModsViewModel Mods { get; private set; } = null!;

        public ApiBrowserViewModel Api { get; private set; } = null!;

        public static CockpitSession Open(DocWorld world)
        {
            var session = new CockpitSession(world);
            WpfHost.Invoke(session.Build);
            return session;
        }

        private void Build()
        {
            var services = new ServiceCollection();
            services.AddCockpitCore();

            // Свои настройки и тихий редактор: позже зарегистрированное побеждает.
            services.AddSingleton<ISettingsStore>(new JsonSettingsStore(_world.SettingsPath));
            services.AddSingleton<IEditorLauncher>(new QuietEditor());

            services.AddSingleton<IApiBrowserBackend, CoreApiBrowserBackend>();
            Stage3Registration.AddBackends(services);
            services.AddSingleton<ISettingsDesignerBackend, CoreSettingsDesignerBackend>();
            Stage4Registration.AddBackends(services);
            services.AddSingleton<IModLibraryService>(_ => new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
            {
                OperationLogPath = Path.Combine(_world.SettingsDir, ModLibraryService.OperationLogFileName),
                WriteOperationLog = false,
            }));

            services.AddSingleton<ApiBrowserViewModel>();
            services.AddSingleton<ModsViewModel>();
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<ProblemsViewModel>();
            services.AddSingleton<RecipesViewModel>();
            services.AddSingleton<Stage3ViewModel>();
            services.AddSingleton<SettingsDesignerViewModel>();
            services.AddSingleton<MainWindow>();

            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            Mods = _provider.GetRequiredService<ModsViewModel>();
            Window = _provider.GetRequiredService<MainWindow>();
            Main = _provider.GetRequiredService<MainViewModel>();
            Stage3 = _provider.GetRequiredService<Stage3ViewModel>();
            Designer = _provider.GetRequiredService<SettingsDesignerViewModel>();
            Api = _provider.GetRequiredService<ApiBrowserViewModel>();

            Mods.FindTemplateDir = () => null;
            Mods.FindSetupStub = () => null;
            Main.ConfirmHandler = (_, _) => false;
            Designer.ConfirmHandler = (_, _) => true;
            Stage3.Recipes.ConfirmHandler = (_, _) => false;

            // Подъём окна делается здесь, а не в его Loaded: там дизайнер настроек читает
            // живой modsettings.dat из профиля пользователя, а снимку нужна фикстура.
            DetachLoadedHandler(Window);

            Window.Width = 1440;
            Window.Height = 860;
            ShowOffscreen(Window);

            // Отработай Loaded всё-таки — дизайнер записал бы сюда путь живого файла или ошибку его чтения.
            Assert.True(Designer.LivePath.Length == 0 && Designer.LiveError.Length == 0,
                "обработчик Loaded главного окна не снялся: дизайнер читал живой modsettings.dat");

            Main.Initialize();
            Stage3.Initialize();
            InitializeDesigner();
        }

        private void InitializeDesigner()
        {
            var reader = new LiveModSettingsReader();
            Assert.True(reader.Read(_world.ModSettingsPath), "фикстура modsettings не прочиталась: " + reader.Error);

            FieldInfo live = typeof(SettingsDesignerViewModel).GetField("_live", BindingFlags.Instance | BindingFlags.NonPublic)
                             ?? throw new InvalidOperationException("у SettingsDesignerViewModel больше нет поля _live — поправьте снимок");
            live.SetValue(Designer, reader);

            Designer.LivePath = reader.FilePath;
            Designer.LiveError = string.Empty;
            Designer.LiveTemplates.Clear();
            foreach (LiveModTemplate template in reader.Templates)
            {
                Designer.LiveTemplates.Add(template);
            }

            Designer.SetProject(Main.CurrentProject);
            Designer.Revalidate();
        }

        private static void DetachLoadedHandler(MainWindow window)
        {
            MethodInfo onLoaded = typeof(MainWindow).GetMethod(
                                      "OnLoaded",
                                      BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                                      binder: null,
                                      [typeof(object), typeof(RoutedEventArgs)],
                                      modifiers: null)
                                  ?? throw new InvalidOperationException("у MainWindow больше нет OnLoaded — поправьте снимок");
            var handler = (RoutedEventHandler)Delegate.CreateDelegate(typeof(RoutedEventHandler), window, onLoaded);
            window.Loaded -= handler;
        }

        /// <summary>Ждёт, пока окно поднимется целиком: лог, python, индекс, проверка проекта и Problems.</summary>
        public void WaitForStartup()
        {
            WaitUntil(() => Main.CurrentProject is not null, "проект");
            WaitUntil(() => Main.LogEntries.Count >= DocWorld.LogEntryCount, "строки python.log");
            WaitUntil(() => !Main.PythonInfo.StartsWith("ищем", StringComparison.Ordinal)
                            && !Main.PythonInfo.StartsWith("python не проверен", StringComparison.Ordinal), "поиск python");
            WaitUntil(() => Api.HasIndex, "индекс браузера API");

            // Первый прогон preflight мог уйти раньше индекса: тогда правила WOT002–WOT004
            // сверять было не с чем. Повторяем по готовому индексу и ждём итога.
            Task<PreflightReport> rerun = Ui(() => Stage3.Problems.RunAsync(Main.CurrentProject));
            WaitUntil(() => rerun.IsCompleted, "preflight");
            Assert.Null(rerun.Result.Error);

            Ui(() =>
            {
                // Строка состояния python: версия настоящая, путь — нейтральный.
                Main.PythonInfo = Regex.Replace(Main.PythonInfo, @" · .*$", @" · C:\Python312\python.exe");
                Main.StatusMessage = "готов";
            });

            Settle(1200);
        }

        public T Ui<T>(Func<T> func) => WpfHost.Invoke(func);

        public void Ui(Action action) => WpfHost.Invoke(action);

        /// <summary>Ждёт условие, проверяя его в потоке WPF; само окно в это время работает.</summary>
        public void WaitUntil(Func<bool> condition, string what)
        {
            var watch = Stopwatch.StartNew();
            while (!WpfHost.Invoke(condition))
            {
                if (watch.Elapsed > TimeSpan.FromSeconds(60))
                {
                    throw new TimeoutException("снимок не дождался: " + what);
                }

                Thread.Sleep(50);
            }
        }

        /// <summary>Даёт окну доработать: таймеры дебаунса, фоновая подгрузка исходника, раскладка.</summary>
        public void Settle(int milliseconds)
        {
            Thread.Sleep(milliseconds);
            WpfHost.Invoke(() =>
            {
                Pump();
                Window.UpdateLayout();
            });
        }

        public void Shot(string path) => WpfHost.Invoke(() => DocShots.Save(Window, path, _world, crop: null));

        /// <summary>
        /// Панель сниппетов — как после нажатия «подписка», но без буфера обмена: кнопка браузера
        /// кладёт код в буфер, а снимок не должен трогать буфер того, кто его запускает. Сниппет
        /// строит тот же шов, что и кнопка; каталоги для него строятся заранее, как и у кнопки.
        /// </summary>
        public void ShowSnippet(ApiSnippetKind kind, bool fromCatalog)
        {
            var backend = Ui(() => _provider.GetRequiredService<IApiBrowserBackend>());
            Task<IReadOnlyList<ApiCatalogEntry>> catalog = Ui(() => backend.GetCatalogAsync(ApiCatalogKind.Events, CancellationToken.None));
            WaitUntil(() => catalog.IsCompleted, "каталог событий для сниппета");

            Ui(() =>
            {
                var request = new ApiSnippetRequest
                {
                    Kind = kind,
                    Symbol = fromCatalog ? null : Api.CurrentSymbol?.Symbol,
                    Entry = fromCatalog ? Api.SelectedCatalogItem?.Entry : null,
                    ModId = Api.ModId,
                };

                ApiSnippet snippet = backend.BuildSnippet(request)
                                     ?? throw new InvalidOperationException("генератор не построил сниппет для снимка");
                Api.SnippetPreview = snippet.Code;
                Api.SnippetNote = snippet.Note;
                Api.SnippetTarget = snippet.Title;
                Api.StatusMessage = "сниппет скопирован: " + snippet.Title;
            });
        }

        /// <summary>Нижняя часть окна: док Problems со строкой состояния.</summary>
        public void ShotProblemsDock(string path)
        {
            WpfHost.Invoke(() =>
            {
                var dock = (FrameworkElement)Window.FindName("ProblemsSplitter");
                Point top = dock.TranslatePoint(new Point(0, 0), Window);
                int y = Math.Max(0, (int)Math.Floor(top.Y) - 1);
                int width = (int)Math.Ceiling(Window.ActualWidth);
                int height = (int)Math.Ceiling(Window.ActualHeight) - y;
                DocShots.Save(Window, path, _world, new Int32Rect(0, y, width, height));
            });
        }

        /// <summary>
        /// «Выключить» на выбранном моде: текст подтверждения собирает сама модель экрана,
        /// ответ — «нет», и в каталоге модов ничего не меняется.
        /// </summary>
        public (string Caption, string Body) CaptureDisableConfirmation()
        {
            string caption = string.Empty;
            string body = string.Empty;
            Task disable = Ui(() =>
            {
                Mods.ConfirmHandler = (c, b) =>
                {
                    caption = c;
                    body = b;
                    return false;
                };
                return Mods.DisableCommand.ExecuteAsync(null);
            });
            WaitUntil(() => disable.IsCompleted, "подтверждение выключения");
            Assert.False(string.IsNullOrEmpty(body), "модель не спросила подтверждения");
            return (caption, body);
        }

        public void Dispose()
        {
            WpfHost.Invoke(() =>
            {
                Window?.Close();
                _provider?.Dispose();
            });
        }
    }

    private sealed class QuietEditor : IEditorLauncher
    {
        public bool IsAvailable => true;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }

    // ================================================================= снимок: замена путей и проверка

    /// <summary>Снимок окна в PNG с заменой путей временной папки и проверкой видимого текста.</summary>
    private static class DocShots
    {
        public static void Save(Window window, string path, DocWorld world, Int32Rect? crop)
        {
            Pump();
            window.UpdateLayout();

            using (DisplayScrub.Apply(window, world.ToDisplay))
            {
                window.UpdateLayout();

                List<string> leaks = VisibleTexts(window)
                    .SelectMany(t => world.Leaks(t).Select(l => l + "   ⟵   «" + Shorten(t) + "»"))
                    .Distinct()
                    .ToList();
                leaks.AddRange(world.Leaks(window.Title));
                if (leaks.Count > 0)
                {
                    throw new InvalidOperationException(
                        "на снимке " + Path.GetFileName(path) + " осталось то, чего в README быть не должно:"
                        + Environment.NewLine + string.Join(Environment.NewLine, leaks));
                }

                int width = (int)Math.Ceiling(window.ActualWidth);
                int height = (int)Math.Ceiling(window.ActualHeight);
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);

                BitmapSource image = crop is { } rect ? new CroppedBitmap(bitmap, rect) : bitmap;
                WritePng(image, path);
            }

            window.UpdateLayout();
        }

        public static void WritePng(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using FileStream fs = File.Create(path);
            encoder.Save(fs);
        }

        private static string Shorten(string text) =>
            text.Length <= 160 ? text.Replace('\n', ' ') : text[..157].Replace('\n', ' ') + "…";

        /// <summary>Весь текст, который окно может нарисовать: подписи, поля ввода, редакторы кода.</summary>
        private static IEnumerable<string> VisibleTexts(DependencyObject root)
        {
            foreach (DependencyObject d in Visuals(root))
            {
                switch (d)
                {
                    case TextBlock tb when tb.IsVisible:
                        yield return tb.Text;
                        break;
                    case TextBox box when box.IsVisible:
                        yield return box.Text;
                        break;
                    case AccessText access when access.IsVisible:
                        yield return access.Text;
                        break;
                    case ICSharpCode.AvalonEdit.TextEditor editor when editor.IsVisible:
                        yield return editor.Text;
                        break;
                }
            }
        }

        public static IEnumerable<DependencyObject> Visuals(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (DependencyObject deeper in Visuals(child))
                {
                    yield return deeper;
                }
            }
        }
    }

    /// <summary>
    /// Временная замена текста на экране: привязка снимается, вместо неё ставится текст с
    /// нейтральными путями, после снимка всё возвращается как было. Модели при этом не
    /// узнают ничего: у привязанного поля ввода значение не уходит в источник.
    /// </summary>
    private sealed class DisplayScrub : IDisposable
    {
        private readonly Func<string, string> _map;
        private readonly List<Action> _restore = new();

        private DisplayScrub(Func<string, string> map) => _map = map;

        public static DisplayScrub Apply(DependencyObject root, Func<string, string> map)
        {
            var scrub = new DisplayScrub(map);
            foreach (DependencyObject d in DocShots.Visuals(root))
            {
                switch (d)
                {
                    case TextBlock tb:
                        scrub.ScrubTextBlock(tb);
                        break;
                    case TextBox box:
                        scrub.Property(box, TextBox.TextProperty, box.Text);
                        break;
                }
            }

            return scrub;
        }

        private void ScrubTextBlock(TextBlock tb)
        {
            ValueSource source = DependencyPropertyHelper.GetValueSource(tb, TextBlock.TextProperty);
            if (source.BaseValueSource == BaseValueSource.Default && !source.IsExpression)
            {
                // Текст задан строчными элементами (Run) прямо в разметке — их и правим.
                // Копия списка: правка единственного Run заставляет TextBlock пересобрать строчные элементы.
                foreach (Inline inline in tb.Inlines.ToList())
                {
                    ScrubInline(inline);
                }

                return;
            }

            Property(tb, TextBlock.TextProperty, tb.Text);
        }

        private void ScrubInline(Inline inline)
        {
            switch (inline)
            {
                case Run run:
                    Property(run, Run.TextProperty, run.Text);
                    break;
                case Span span:
                    foreach (Inline child in span.Inlines.ToList())
                    {
                        ScrubInline(child);
                    }

                    break;
            }
        }

        private void Property(DependencyObject owner, DependencyProperty property, string? current)
        {
            if (string.IsNullOrEmpty(current))
            {
                return;
            }

            string replaced = _map(current);
            if (string.Equals(replaced, current, StringComparison.Ordinal))
            {
                return;
            }

            BindingBase? binding = BindingOperations.GetBindingBase(owner, property);
            object local = owner.ReadLocalValue(property);
            if (binding is not null)
            {
                BindingOperations.ClearBinding(owner, property);
            }

            owner.SetValue(property, replaced);

            _restore.Add(() =>
            {
                if (binding is not null)
                {
                    BindingOperations.SetBinding(owner, property, binding);
                }
                else if (local == DependencyProperty.UnsetValue)
                {
                    owner.ClearValue(property);
                }
                else
                {
                    owner.SetValue(property, local);
                }
            });
        }

        public void Dispose()
        {
            for (int i = _restore.Count - 1; i >= 0; i--)
            {
                _restore[i]();
            }
        }
    }

    // ================================================================= системное окно подтверждения

    /// <summary>
    /// Подтверждения экрана «Моды» — системный MessageBox, а не окно WPF: RenderTargetBitmap его
    /// не нарисует. Поэтому окно показывается по-настоящему (тот же вызов, что в
    /// <c>ModsViewModel.DefaultConfirm</c>), снимается само окно через PrintWindow и закрывается
    /// кнопкой «Нет». Без рабочего стола (служба, CI) снимок пропускается.
    /// </summary>
    private static class NativeMessageBoxShot
    {
        private const uint PwRenderFullContent = 2;
        private const uint WmCommand = 0x0111;
        private const int IdNo = 7;
        private const int DwmwaExtendedFrameBounds = 9;
        private static readonly IntPtr PerMonitorAwareV2 = new(-4);

        public static void Save(string caption, string body, string path, DocWorld world)
        {
            List<string> leaks = world.Leaks(body).Concat(world.Leaks(caption)).ToList();
            if (leaks.Count > 0)
            {
                throw new InvalidOperationException("в тексте подтверждения осталось лишнее: " + string.Join(", ", leaks));
            }

            Skip.IfNot(Environment.UserInteractive, "нет рабочего стола — системное окно подтверждения не снять");

            var shower = new Thread(() =>
            {
                SetThreadDpiAwarenessContext(PerMonitorAwareV2);
                MessageBox.Show(body, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            })
            {
                IsBackground = true,
                Name = "doc-messagebox",
            };
            shower.SetApartmentState(ApartmentState.STA);
            shower.Start();

            IntPtr previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
            IntPtr hwnd = IntPtr.Zero;
            try
            {
                var watch = Stopwatch.StartNew();
                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {
                    hwnd = FindWindow("#32770", caption);
                    if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd))
                    {
                        break;
                    }

                    Thread.Sleep(50);
                }

                Assert.True(hwnd != IntPtr.Zero, "окно подтверждения не появилось");

                // Окно появляется с анимацией: даём ему дорисоваться.
                Thread.Sleep(700);
                BitmapSource image = Capture(hwnd);
                DocShots.WritePng(image, path);
            }
            finally
            {
                if (hwnd != IntPtr.Zero)
                {
                    PostMessage(hwnd, WmCommand, new IntPtr(IdNo), IntPtr.Zero);
                }

                shower.Join(TimeSpan.FromSeconds(10));
                SetThreadDpiAwarenessContext(previous);
            }
        }

        private static BitmapSource Capture(IntPtr hwnd)
        {
            Assert.True(GetWindowRect(hwnd, out Rect32 window), "не удалось узнать размер окна подтверждения");
            int width = window.Right - window.Left;
            int height = window.Bottom - window.Top;

            // Видимая рамка: у окон Windows 10 вокруг неё невидимые поля под захват мышью.
            Rect32 visible = window;
            if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect32 frame, Marshal.SizeOf<Rect32>()) == 0)
            {
                visible = frame;
            }

            IntPtr screen = GetDC(IntPtr.Zero);
            IntPtr memory = CreateCompatibleDC(screen);
            IntPtr bitmap = CreateCompatibleBitmap(screen, width, height);
            IntPtr old = SelectObject(memory, bitmap);
            try
            {
                Assert.True(PrintWindow(hwnd, memory, PwRenderFullContent), "PrintWindow не нарисовал окно подтверждения");
                SelectObject(memory, old);

                BitmapSource full = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                var rect = new Int32Rect(
                    Math.Clamp(visible.Left - window.Left, 0, width - 1),
                    Math.Clamp(visible.Top - window.Top, 0, height - 1),
                    0,
                    0);
                rect.Width = Math.Clamp(visible.Right - visible.Left, 1, width - rect.X);
                rect.Height = Math.Clamp(visible.Bottom - visible.Top, 1, height - rect.Y);

                var cropped = new CroppedBitmap(full, rect);
                var opaque = new FormatConvertedBitmap(cropped, PixelFormats.Bgr24, null, 0);
                opaque.Freeze();
                return opaque;
            }
            finally
            {
                DeleteObject(bitmap);
                DeleteDC(memory);
                _ = ReleaseDC(IntPtr.Zero, screen);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect32
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? className, string? windowName);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetWindowRect(IntPtr hwnd, out Rect32 rect);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect32 value, int size);
    }

    // ================================================================= выдуманная машина

    /// <summary>
    /// Выдуманная машина мододела во временной папке. Раскладка повторяет обычную
    /// (<c>Games\Tanki</c>, <c>Users\dev\…</c>), чтобы после замены корня на <c>C:</c> пути на
    /// снимках выглядели естественно. Ни одного имени настоящего мода здесь нет.
    /// </summary>
    private sealed class DocWorld : IDisposable
    {
        public const string GameVersion = "1.46.0.0";
        public const string PreviousVersion = "1.45.0.0";

        public const string ClockId = "ru.example.battle_clock";
        public const string NotesId = "ru.example.hangar_notes";
        public const string TimerId = "ru.example.shot_timer";
        public const string ClockFile = ClockId + "_1.2.0.mtmod";

        /// <summary>Шаблон фикстуры, который «импортируется из игры» на снимке.</summary>
        public const string ImportedLinkage = "ru.example.notices";

        /// <summary>Когда «правили» исходники и когда «собрали» dist: сборка свежее исходников.</summary>
        private static readonly DateTime SourcesWritten = new(2026, 10, 27, 18, 5, 0, DateTimeKind.Local);

        private static readonly DateTime BuildWritten = new(2026, 10, 27, 19, 40, 0, DateTimeKind.Local);

        /// <summary>Сколько записей лога должно доехать до ленты, прежде чем снимать.</summary>
        public const int LogEntryCount = 24;

        /// <summary>
        /// Слова, которых не должно быть на снимках (имена своих и чужих модов с машины, где снимают).
        /// Список — личный и в репозиторий не кладётся: переменная <see cref="DenylistVariable"/>
        /// с путём к файлу (по слову в строке, <c>#</c> — комментарий), иначе файл
        /// <c>docs/.screenshot-denylist.txt</c> рядом с выводом, если он есть (он в .gitignore).
        /// </summary>
        private static readonly string[] ForbiddenNames = LoadDenylist();

        /// <summary>Переменная окружения с путём к личному стоп-списку слов для снимков.</summary>
        public const string DenylistVariable = "WOTCOCKPIT_DOC_DENYLIST";

        private static string[] LoadDenylist()
        {
            string? path = TestEnvironment.Read(DenylistVariable);
            if (path is null && TestEnvironment.Read("WOTCOCKPIT_DOC_SCREENSHOTS") is { } output)
            {
                path = Path.Combine(output, ".screenshot-denylist.txt");
            }

            if (path is null || !File.Exists(path))
            {
                return [];
            }

            return File.ReadAllLines(path)
                .Select(l => l.Trim().ToLowerInvariant())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
        }

        private static readonly string[] NeutralRoots = [@"C:\Games\Tanki", @"C:\Users\dev", @"C:\Python312"];

        private readonly CoreTempDir _tmp = new("wcdoc");

        public DocWorld()
        {
            Root = _tmp.Path;
            GameDir = Path.Combine(Root, "Games", "Tanki");
            ModsDir = Path.Combine(GameDir, "mods", GameVersion);
            ReposDir = Path.Combine(Root, "Users", "dev", "repos");
            ClientSource = Path.Combine(Root, "Users", "dev", "wot-src");
            ClientSource145 = Path.Combine(Root, "Users", "dev", "wot-src-1.45");
            SettingsDir = Path.Combine(Root, "Users", "dev", "AppData", "Roaming", "WotModCockpit");
            ModSettingsPath = Path.Combine(Root, "Users", "dev", "AppData", "Roaming", "Lesta", "MirTankov", "mods", "modsettings.dat");
            Directory.CreateDirectory(ModsDir);
            Directory.CreateDirectory(ReposDir);
            Directory.CreateDirectory(SettingsDir);
        }

        public string Root { get; }

        public string GameDir { get; }

        public string ModsDir { get; }

        public string ReposDir { get; }

        public string ClientSource { get; }

        public string ClientSource145 { get; }

        public string SettingsDir { get; }

        public string SettingsPath => Path.Combine(SettingsDir, JsonSettingsStore.FileName);

        public string ModSettingsPath { get; }

        public string ClockProject => Path.Combine(ReposDir, "wot-mod-battle-clock");

        public string NotesProject => Path.Combine(ReposDir, "wot-mod-hangar-notes");

        public string TimerProject => Path.Combine(ReposDir, "wot-mod-shot-timer");

        public IReadOnlyList<string> ProjectDirs => [ClockProject, NotesProject, TimerProject];

        /// <summary>Как путь выглядит на снимке: корень временной папки становится <c>C:</c>.</summary>
        public string ToDisplay(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            string result = text.Replace(Root, "C:", StringComparison.OrdinalIgnoreCase);
            return result.Replace(Root.Replace('\\', '/'), "C:", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Что в тексте выдаёт настоящую машину или настоящие моды.</summary>
        public IEnumerable<string> Leaks(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            foreach (Match m in Regex.Matches(text, @"(?<![A-Za-z])[A-Za-z]:[\\/][^\s""'«»|<>*?]*"))
            {
                if (!NeutralRoots.Any(r => m.Value.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return "путь " + m.Value;
                }
            }

            var markers = new List<string>
            {
                Root,
                Path.GetTempPath().TrimEnd('\\'),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            string user = Environment.UserName;
            if (user.Length > 0 && !string.Equals(user, "dev", StringComparison.OrdinalIgnoreCase))
            {
                markers.Add(@"\" + user + @"\");
                markers.Add("/" + user + "/");
            }

            string machine = Environment.MachineName;
            if (machine.Length >= 6)
            {
                markers.Add(machine);
            }

            foreach (string marker in markers.Where(m => m.Length > 0))
            {
                if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    yield return "след машины «" + marker + "»";
                }
            }

            foreach (string name in ForbiddenNames)
            {
                if (text.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    yield return "имя настоящего мода «" + name + "»";
                }
            }
        }

        public void Dispose() => _tmp.Dispose();

        // ------------------------------------------------------------- главная раскладка

        /// <summary>Игра 1.46 с модами, три своих проекта, дерево клиента, лог, настройки, фикстура.</summary>
        public void BuildMainWorld()
        {
            WriteGame();
            Dictionary<string, string> artifacts = WriteProjects();

            // Свой мод стоит в игре байт в байт как в dist, второй свой выключен, третий не ставили.
            File.Copy(artifacts[ClockId], Path.Combine(ModsDir, ClockFile));
            File.Copy(artifacts[NotesId], Path.Combine(ModsDir, NotesId + "_0.9.0.mtmod.off"));
            WriteForeignMods();
            WritePythonLog();
            SyntheticClientTree.Write(ClientSource);

            Directory.CreateDirectory(Path.GetDirectoryName(ModSettingsPath)!);
            File.Copy(SettingsTestData.StateCopyPath, ModSettingsPath, overwrite: true);

            new JsonSettingsStore(SettingsPath).Save(new CockpitSettings
            {
                GameDir = GameDir,
                LastProjectDir = ClockProject,
                RecentProjects = [ClockProject, NotesProject, TimerProject],
                WotSrcDir = ClientSource,
                ModIdPrefix = "ru.example",
            });
        }

        /// <summary>Клиент обновился до 1.46: свои моды остались в <c>mods\1.45.0.0</c>.</summary>
        public void BuildMigrationWorld()
        {
            WriteGame();
            Dictionary<string, string> artifacts = WriteProjects();

            string old = Path.Combine(GameDir, "mods", PreviousVersion);
            Directory.CreateDirectory(old);
            File.Copy(artifacts[ClockId], Path.Combine(old, ClockFile));
            File.Copy(artifacts[NotesId], Path.Combine(old, NotesId + "_0.9.0.mtmod"));
            File.Copy(artifacts[TimerId], Path.Combine(old, TimerId + "_1.0.0.mtmod"));

            Directory.CreateDirectory(Path.Combine(ClientSource145, "client"));
            File.WriteAllText(Path.Combine(ClientSource145, ".cockpit-client.json"), "{\"version\":\"" + PreviousVersion + "\"}");
        }

        private void WriteGame()
        {
            File.WriteAllText(Path.Combine(GameDir, "paths.xml"), $"""
                <root>
                  <Paths>
                    <Path cacheSubdirs="true">./res_mods/{GameVersion}</Path>
                    <Path mask="*.mtmod" mode="recursive" root="res">./mods/{GameVersion}</Path>
                    <Packages>
                      <Package type="sandbox,sd,hd">./res/packages/scripts.pkg</Package>
                    </Packages>
                    <Path>./res</Path>
                  </Paths>
                </root>
                """);
            Directory.CreateDirectory(Path.Combine(GameDir, "res_mods", GameVersion));
        }

        // ------------------------------------------------------------- свои проекты

        private Dictionary<string, string> WriteProjects()
        {
            var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);

            byte[] clockIcon = WpfHost.Invoke(() => IconArt.Clock());
            byte[] notesIcon = WpfHost.Invoke(() => IconArt.Notes());
            byte[] timerIcon = WpfHost.Invoke(() => IconArt.Target());

            artifacts[ClockId] = WriteProject(ClockProject, ClockId, "1.2.0", "Battle Clock",
                "Часы боя в углу экрана: сколько прошло и сколько осталось до конца боя.",
                "battle_clock", clockIcon, BrokenModSources.Files());

            artifacts[NotesId] = WriteProject(NotesProject, NotesId, "0.9.0", "Hangar Notes",
                "Заметки к технике в ангаре: короткий текст в карточке танка.",
                "hangar_notes", notesIcon, CleanModSources.Files("hangar_notes", "HangarNotes"));

            artifacts[TimerId] = WriteProject(TimerProject, TimerId, "1.0.0", "Shot Timer",
                "Таймер перезарядки над прицелом.",
                "shot_timer", timerIcon, CleanModSources.Files("shot_timer", "ShotTimer"));

            return artifacts;
        }

        private static string WriteProject(
            string dir,
            string id,
            string version,
            string title,
            string description,
            string package,
            byte[] icon,
            IReadOnlyDictionary<string, string> sources)
        {
            string src = Path.Combine(dir, "src");
            Directory.CreateDirectory(src);
            File.WriteAllText(Path.Combine(dir, "build.py"), "# build.py из шаблона мода\n");
            string meta = FakeGameDir.Meta(id, version, title, description);
            File.WriteAllText(Path.Combine(src, "meta.xml"), meta, new UTF8Encoding(false));

            foreach ((string relative, string text) in sources)
            {
                string full = Path.Combine(src, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, text.Replace("\r\n", "\n"), new UTF8Encoding(false));
            }

            string iconPath = Path.Combine(src, "res", "gui", "maps", "icons", "mods", id, "modsListApi.png");
            Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
            File.WriteAllBytes(iconPath, icon);

            // Дескриптор кокпита: без него шапка показывала бы «wotmod.json ещё не создан».
            var store = new JsonProjectStore();
            ModProject project = store.Load(dir) ?? throw new InvalidOperationException("проект не опознан: " + dir);
            store.Save(project);

            foreach (string f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTime(f, SourcesWritten);
            }

            // Сборка в dist: ровно то, что лежит в src, — иначе экран «Моды» счёл бы её устаревшей.
            var entries = new List<KeyValuePair<string, byte[]>>();
            foreach ((string relative, string text) in sources)
            {
                if (relative.EndsWith(".py", StringComparison.Ordinal))
                {
                    entries.Add(new(relative[..^3] + ".pyc", Encoding.UTF8.GetBytes("compiled " + text.Length)));
                }
            }

            entries.Add(new($"res/gui/maps/icons/mods/{id}/modsListApi.png", icon));

            string dist = Path.Combine(dir, "dist");
            Directory.CreateDirectory(dist);
            string artifact = Path.Combine(dist, $"{id}_{version}.mtmod");
            FakeGameDir.WriteArchive(artifact, meta, entries, compress: false);
            File.SetLastWriteTime(artifact, BuildWritten);
            return artifact;
        }

        /// <summary>Форма окна настроек «Battle Clock», собранная в дизайнере.</summary>
        public static SettingsFormSpec ClockForm() => new()
        {
            Linkage = ClockId,
            ModDisplayName = "Battle Clock",
            SettingsVersion = 2,
            HasEnabled = true,
            EnabledDefault = true,
            Column1 =
            [
                new SettingsControlSpec { Type = SettingsControlType.Label, Text = "Часы боя" },
                new SettingsControlSpec
                {
                    Type = SettingsControlType.CheckBox,
                    Text = "Показывать часы",
                    Tooltip = "{HEADER}Часы боя{/HEADER}{BODY}Сколько прошло с начала боя.{/BODY}",
                    VarName = "showClock",
                    Value = true,
                },
                new SettingsControlSpec
                {
                    Type = SettingsControlType.Dropdown,
                    Text = "Где показывать",
                    VarName = "position",
                    Value = 0d,
                    Width = 200,
                    Options =
                    [
                        new SettingsOptionSpec("Сверху по центру"),
                        new SettingsOptionSpec("Справа над миникартой"),
                        new SettingsOptionSpec("Слева под счётом"),
                    ],
                },
                new SettingsControlSpec
                {
                    Type = SettingsControlType.Slider,
                    Text = "Прозрачность",
                    VarName = "opacity",
                    Value = 85d,
                    Minimum = 20,
                    Maximum = 100,
                    SnapInterval = 5,
                    Format = "{{value}} %",
                },
            ],
            Column2 =
            [
                new SettingsControlSpec
                {
                    Type = SettingsControlType.RadioButtonGroup,
                    Text = "Что показывать",
                    VarName = "mode",
                    Value = 1d,
                    Options =
                    [
                        new SettingsOptionSpec("Сколько прошло"),
                        new SettingsOptionSpec("Сколько осталось"),
                    ],
                },
                new SettingsControlSpec
                {
                    Type = SettingsControlType.HotKey,
                    Text = "Скрыть или показать",
                    VarName = "toggleKey",
                    Value = new[] { -2, 61 },
                },
                new SettingsControlSpec
                {
                    Type = SettingsControlType.ColorChoice,
                    Text = "Цвет цифр",
                    VarName = "digitsColor",
                    Value = "F2C94C",
                },
            ],
        };

        // ------------------------------------------------------------- чужие моды

        private void WriteForeignMods()
        {
            byte[] packIcon = WpfHost.Invoke(() => IconArt.Diamond());

            Foreign("com.example.foreign_pack_1.4.2.mtmod", "com.example.foreign_pack", "1.4.2", "Foreign Pack",
                "Набор доработок интерфейса — пример чужого мода.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_foreign_pack.pyc"),
                 FakeGameDir.Entry("res/scripts/client/gui/mods/foreign_pack/__init__.pyc"),
                 new("res/gui/maps/foreign_pack/modsListApi.png", packIcon)]);
            Foreign("tv.example.battleatlas_9.9.mtmod", "tv.example.battleatlas", "9.9", "Battle Atlas",
                "Подписи карт и позиций на миникарте.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_battleatlas.pyc")]);
            Foreign("me.example.crosshair_2.0.1.mtmod", "me.example.crosshair", "2.0.1", "Crosshair Example",
                "Сменный прицел.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_crosshair.pyc")]);
            Foreign("com.example.hangar_stats_1.3.0.mtmod", "com.example.hangar_stats", "1.3.0", "Hangar Stats",
                "Статистика танка в ангаре.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_hangar_stats.pyc")]);
            Foreign("org.example.minimap_plus_0.7.0.mtmod", "org.example.minimap_plus", "0.7.0", "Minimap Plus",
                "Дополнительные слои миникарты.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_minimap_plus.pyc")]);
            Foreign("org.example.native_bridge_1.1.mtmod", "org.example.native_bridge", "1.1", "Native Bridge",
                "Библиотека с нативным модулем.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_native_bridge.pyc"),
                 FakeGameDir.Entry("res/scripts/client/gui/mods/native_bridge/_bridge.pyd")]);
            Foreign("mod_foreign_tool.mtmod", null, null, null, null,
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_foreign_tool.pyc")]);
            Foreign("com.example.old_marker_0.3.mtmod.off", "com.example.old_marker", "0.3", "Old Marker",
                "Старый маркер, выключен.",
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_old_marker.pyc")]);
        }

        private void Foreign(string file, string? id, string? version, string? title, string? description,
            IEnumerable<KeyValuePair<string, byte[]>> entries)
        {
            string? meta = id is null ? null : FakeGameDir.Meta(id, version!, title!, description!);
            FakeGameDir.WriteArchive(Path.Combine(ModsDir, file), meta, entries, compress: false);
        }

        // ------------------------------------------------------------- python.log

        private void WritePythonLog()
        {
            string[] lines =
            [
                "2026-10-27 21:14:01.512: INFO: [Scaleform] Create GUI Component: 'gui/flash/lobby.swf'",
                "2026-10-27 21:14:01.887: INFO: [MOD] mod loaded: mod_battle_clock",
                "2026-10-27 21:14:01.889: INFO: [BattleClock] init: version 1.2.0",
                "2026-10-27 21:14:01.903: INFO: [MOD] mod loaded: mod_foreign_pack",
                "2026-10-27 21:14:01.905: INFO: [NOTE] package loaded: Foreign Pack v1.4.2 (build 7)",
                "2026-10-27 21:14:01.931: INFO: [MOD] mod loaded: mod_battleatlas",
                "2026-10-27 21:14:02.344: INFO: [gui.app_loader.loader] Space is updated: LobbySpace()",
                "2026-10-27 21:14:02.610: INFO: [BattleClock] hangar: settings loaded (position=0, opacity=85)",
                "2026-10-27 21:14:02.977: WARNING: [ForeignPack] config not found, defaults are used",
                "2026-10-27 21:15:37.020: INFO: [gui.app_loader.loader] Space is updated: BattleLoadingSpace()",
                "2026-10-27 21:15:37.208: INFO: [Avatar] [INIT_STEPS] Avatar.onBecomePlayer: arenaId(1000000000000001)",
                "2026-10-27 21:15:40.233: INFO: [Scaleform] Create GUI Component: 'gui/flash/battle.swf'",
                "2026-10-27 21:15:41.412: INFO: [gui.app_loader.loader] Space is updated: BattleSpace()",
                "2026-10-27 21:15:41.500: WARNING: [BattleClock] arena period is unknown yet, waiting for onArenaPeriodChange",
                "2026-10-27 21:15:41.512: ERROR: [EXCEPTION] (scripts/client/gui/mods/battle_clock/main.py, 22)",
                "Traceback (most recent call last):",
                "  File \"scripts/client/gui/mods/battle_clock/main.py\", line 22, in _onAvatarReady",
                "    from gui.battle_control import g_sessionProvider",
                "ImportError: cannot import name g_sessionProvider",
                "2026-10-27 21:15:41.530: INFO: [BattleClock] period changed: 2",
                "2026-10-27 21:15:51.530: INFO: [BattleClock] period changed: 3",
                "2026-10-27 21:15:51.602: INFO: [Scaleform] Create GUI Component: 'gui/flash/battleVehicleMarkersApp.swf'",
                "2026-10-27 21:16:02.118: INFO: [ForeignPack] battle started",
                "2026-10-27 21:22:47.390: INFO: [gui.app_loader.loader] Space is updated: LobbySpace()",
                "2026-10-27 21:22:47.512: INFO: [Scaleform] Destroy GUI Component: 'gui/flash/battle.swf'",
                "2026-10-27 21:22:48.004: INFO: [BattleClock] battle finished: 06:57",
                "2026-10-27 21:22:48.115: INFO: [NOTE] [ForeignPack] battle results cached",
                "2026-10-27 21:22:49.640: INFO: [Scaleform] Create GUI Component: 'gui/flash/lobby.swf'",
            ];

            File.WriteAllText(Path.Combine(GameDir, "python.log"), string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
        }
    }

    // ================================================================= исходники выдуманных модов

    /// <summary>
    /// «Battle Clock» с типичными ошибками новичка: имени нет в модуле клиента, кириллица без
    /// строки coding, getattr без значения по умолчанию, подписка без отписки, петля callback
    /// без отмены, LOG_DEBUG и print. Строка 22 совпадает с traceback в python.log.
    /// </summary>
    private static class BrokenModSources
    {
        public static IReadOnlyDictionary<string, string> Files() => new Dictionary<string, string>
        {
            ["res/scripts/client/gui/mods/mod_battle_clock.py"] = """
                # -*- coding: utf-8 -*-
                import logging

                from gui.mods.battle_clock import main

                LOG_PREFIX = '[BattleClock]'
                _logger = logging.getLogger('BattleClock')


                def init():
                    _logger.info('%s init', LOG_PREFIX)
                    main.init()


                def fini():
                    main.fini()
                """,
            ["res/scripts/client/gui/mods/battle_clock/__init__.py"] = string.Empty,
            ["res/scripts/client/gui/mods/battle_clock/main.py"] = """
                import BigWorld
                import Avatar
                from debug_utils import LOG_DEBUG
                from PlayerEvents import g_playerEvents

                TITLE = 'Часы боя'
                _timerID = None


                def init():
                    g_playerEvents.onAvatarReady += _onAvatarReady
                    g_playerEvents.onArenaPeriodChange += _onPeriodChange


                def fini():
                    g_playerEvents.onArenaPeriodChange -= _onPeriodChange


                def _onAvatarReady():
                    LOG_DEBUG('avatar ready', TITLE)
                    print 'battle clock: start'
                    from gui.battle_control import g_sessionProvider
                    _tick()


                def _onPeriodChange(period, periodEndTime, *args):
                    original = getattr(Avatar.PlayerAvatar, 'showShotResults')
                    LOG_DEBUG('period', period, original)


                def _tick():
                    global _timerID
                    _timerID = BigWorld.callback(1.0, _tick)
                """,
        };
    }

    /// <summary>Аккуратный мод без замечаний: точка входа с защитой и пустое тело.</summary>
    private static class CleanModSources
    {
        public static IReadOnlyDictionary<string, string> Files(string package, string tag) => new Dictionary<string, string>
        {
            [$"res/scripts/client/gui/mods/mod_{package}.py"] = $$"""
                # -*- coding: utf-8 -*-
                import logging

                LOG_PREFIX = '[{{tag}}]'
                _logger = logging.getLogger('{{tag}}')


                def init():
                    try:
                        from gui.mods.{{package}} import main
                        main.init()
                    except Exception:
                        _logger.exception('%s init failed', LOG_PREFIX)


                def fini():
                    pass
                """,
            [$"res/scripts/client/gui/mods/{package}/__init__.py"] = string.Empty,
            [$"res/scripts/client/gui/mods/{package}/main.py"] = """
                # -*- coding: utf-8 -*-


                def init():
                    pass
                """,
        };
    }

    // ================================================================= синтетическое дерево клиента

    /// <summary>
    /// Крошечное дерево «исходников клиента» для браузера API, рецептов и preflight. Написано
    /// здесь с нуля: только имена публичного API, которыми пользуются моды (и которые называют
    /// README и рецепты), с пустыми телами. Ни строки настоящего клиента.
    /// </summary>
    private static class SyntheticClientTree
    {
        public static void Write(string root)
        {
            foreach ((string relative, string body) in Files())
            {
                string full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                string text = "# Embedded file name: scripts/" + relative + "\n" + body.Replace("\r\n", "\n");
                File.WriteAllText(full, text, new UTF8Encoding(false));
            }

            File.WriteAllText(Path.Combine(root, ".cockpit-client.json"), "{\"version\":\"" + DocWorld.GameVersion + "\"}");
        }

        private static IEnumerable<(string, string)> Files()
        {
            yield return ("common/Event.py", """
                class Event(object):

                    def __init__(self, manager=None):
                        self.__delegates = []
                        if manager is not None:
                            manager.register(self)

                    def __call__(self, *args, **kwargs):
                        for delegate in self.__delegates[:]:
                            delegate(*args, **kwargs)

                    def __iadd__(self, delegate):
                        if delegate not in self.__delegates:
                            self.__delegates.append(delegate)
                        return self

                    def __isub__(self, delegate):
                        if delegate in self.__delegates:
                            self.__delegates.remove(delegate)
                        return self

                    def clear(self):
                        del self.__delegates[:]


                class EventManager(object):

                    def __init__(self):
                        self.__events = []

                    def register(self, event):
                        self.__events.append(event)

                    def clear(self):
                        for event in self.__events:
                            event.clear()
                """);

            yield return ("client/PlayerEvents.py", """
                import Event

                class _PlayerEvents(object):

                    def __init__(self):
                        self.__eventManager = Event.EventManager()
                        em = self.__eventManager
                        self.onAccountShowGUI = Event.Event(em)
                        self.onAccountBecomePlayer = Event.Event(em)
                        self.onAccountBecomeNonPlayer = Event.Event(em)
                        self.onAvatarBecomePlayer = Event.Event(em)
                        self.onAvatarBecomeNonPlayer = Event.Event(em)
                        self.onAvatarReady = Event.Event(em)
                        self.onArenaCreated = Event.Event(em)
                        self.onArenaPeriodChange = Event.Event(em)
                        self.onKickedFromArena = Event.Event(em)
                        self.onBattleResultsReceived = Event.Event(em)
                        self.onClientUpdated = Event.Event(em)

                    def destroy(self):
                        self.__eventManager.clear()


                g_playerEvents = _PlayerEvents()
                """);

            yield return ("client/Avatar.py", """
                import BigWorld
                from PlayerEvents import g_playerEvents

                class PlayerAvatar(BigWorld.Entity):

                    def __init__(self):
                        self.__isReady = False

                    def onBecomePlayer(self):
                        g_playerEvents.onAvatarBecomePlayer()

                    def onEnterWorld(self, prereqs):
                        self.__isReady = True
                        g_playerEvents.onAvatarReady()

                    def onBecomeNonPlayer(self):
                        g_playerEvents.onAvatarBecomeNonPlayer()

                    def onKickedFromArena(self, reasonCode):
                        g_playerEvents.onKickedFromArena(reasonCode)

                    def showShotResults(self, results):
                        pass
                """);

            yield return ("client_common/ClientArena.py", """
                import Event
                from PlayerEvents import g_playerEvents

                class ClientArena(object):

                    def __init__(self, arenaUniqueID, arenaTypeID):
                        self.onPeriodChange = Event.Event()
                        self.onVehicleKilled = Event.Event()
                        self.onNewVehicleListReceived = Event.Event()
                        self.period = 0
                        g_playerEvents.onArenaCreated()

                    def _onPeriodChange(self, period, periodEndTime, periodLength):
                        self.period = period
                        self.onPeriodChange(period, periodEndTime, periodLength)
                        g_playerEvents.onArenaPeriodChange(period, periodEndTime, periodLength)

                    def _onVehicleKilled(self, victimID, killerID, reason):
                        self.onVehicleKilled(victimID, killerID, reason)
                """);

            yield return ("client/gui/hud/__init__.py", string.Empty);
            yield return ("client/gui/hud/clock_panel.py", """
                from PlayerEvents import g_playerEvents

                class ClockPanel(object):

                    def start(self):
                        g_playerEvents.onAvatarReady += self.__onAvatarReady
                        g_playerEvents.onArenaPeriodChange += self.__onPeriodChange

                    def stop(self):
                        g_playerEvents.onAvatarReady -= self.__onAvatarReady
                        g_playerEvents.onArenaPeriodChange -= self.__onPeriodChange

                    def __onAvatarReady(self):
                        self.visible = True

                    def __onPeriodChange(self, period, *args):
                        self.period = period
                """);

            yield return ("client/gui/hud/components.py", """
                from PlayerEvents import g_playerEvents

                class HudComponent(object):

                    def init(self):
                        g_playerEvents.onAvatarReady += self._onAvatarReady

                    def fini(self):
                        g_playerEvents.onAvatarReady -= self._onAvatarReady

                    def _onAvatarReady(self):
                        pass


                class TeamCounter(HudComponent):

                    def _onAvatarReady(self):
                        self.update()


                class DamagePanel(HudComponent):

                    def _onAvatarReady(self):
                        self.reset()


                class MinimapMarkers(HudComponent):

                    def _onAvatarReady(self):
                        self.redraw()
                """);

            yield return ("client/gui/lobby/__init__.py", string.Empty);
            yield return ("client/gui/lobby/hangar_banner.py", """
                from PlayerEvents import g_playerEvents

                class HangarBanner(object):

                    def init(self):
                        g_playerEvents.onAccountShowGUI += self.__onShowGUI
                        g_playerEvents.onClientUpdated += self.__onClientUpdated

                    def fini(self):
                        g_playerEvents.onAccountShowGUI -= self.__onShowGUI
                        g_playerEvents.onClientUpdated -= self.__onClientUpdated

                    def __onShowGUI(self, ctx):
                        self.show()

                    def __onClientUpdated(self, diff, updateOnlyLobbyCtx):
                        self.refresh()
                """);

            yield return ("client/gui/battle_results/__init__.py", string.Empty);
            yield return ("client/gui/battle_results/results_cache.py", """
                from PlayerEvents import g_playerEvents

                class ResultsCache(object):

                    def init(self):
                        g_playerEvents.onBattleResultsReceived += self.__onResults
                        g_playerEvents.onKickedFromArena += self.__onKicked

                    def fini(self):
                        g_playerEvents.onBattleResultsReceived -= self.__onResults
                        g_playerEvents.onKickedFromArena -= self.__onKicked

                    def __onResults(self, isPlayerVehicle, results):
                        self.store(results)

                    def __onKicked(self, reasonCode):
                        self.clear()
                """);

            yield return ("client/sound/__init__.py", string.Empty);
            yield return ("client/sound/battle_music.py", """
                from PlayerEvents import g_playerEvents

                class BattleMusic(object):

                    def start(self):
                        g_playerEvents.onAvatarReady += self.__onAvatarReady
                        g_playerEvents.onAvatarBecomeNonPlayer += self.__onLeave
                        g_playerEvents.onArenaPeriodChange += self.__onPeriod

                    def stop(self):
                        g_playerEvents.onAvatarReady -= self.__onAvatarReady
                        g_playerEvents.onAvatarBecomeNonPlayer -= self.__onLeave
                        g_playerEvents.onArenaPeriodChange -= self.__onPeriod

                    def __onAvatarReady(self):
                        self.play('battle')

                    def __onLeave(self):
                        self.stopAll()

                    def __onPeriod(self, period, *args):
                        self.play('period_%d' % period)
                """);

            // Модули, на которые опираются рецепты: только имена.
            yield return ("common/constants.py", """
                ARENA_GUI_TYPE_LABEL = {}


                class ARENA_PERIOD:
                    IDLE = 0
                    WAITING = 1
                    PREBATTLE = 2
                    BATTLE = 3
                    AFTERBATTLE = 4


                class VEHICLE_HIT_FLAGS:
                    VEHICLE_KILLED = 1
                """);
            yield return ("common/debug_utils.py", """
                def LOG_NOTE(msg, *args):
                    pass


                def LOG_WARNING(msg, *args):
                    pass


                def LOG_ERROR(msg, *args):
                    pass


                def LOG_DEBUG(msg, *args):
                    pass
                """);
            yield return ("common/ArenaType.py", """
                g_cache = {}
                """);
            yield return ("client/BattleReplay.py", """
                class BattleReplay(object):

                    @property
                    def isPlaying(self):
                        return False


                g_replayCtrl = BattleReplay()
                """);
            yield return ("client/CurrentVehicle.py", """
                class _CurrentVehicle(object):

                    @property
                    def item(self):
                        return None


                g_currentVehicle = _CurrentVehicle()
                """);
            yield return ("client/Keys.py", """
                KEY_F1 = 59
                KEY_F2 = 60
                KEY_F3 = 61
                """);
            yield return ("client/gui/__init__.py", """
                g_keyEventHandlers = set()
                """);
            yield return ("client/gui/InputHandler.py", """
                class _InputHandler(object):

                    def __init__(self):
                        self.onKeyDown = None
                        self.onKeyUp = None


                g_instance = _InputHandler()
                """);
            yield return ("client/gui/SystemMessages.py", """
                class SM_TYPE(object):
                    Information = 'Information'
                    Warning = 'Warning'
                    Error = 'Error'


                def pushMessage(text, type=SM_TYPE.Information, priority=None, messageData=None):
                    pass


                def pushI18nMessage(key, *args, **kwargs):
                    pass
                """);
            yield return ("client/gui/battle_control/__init__.py", """
                def isBattleRunning():
                    return False
                """);
            yield return ("client/helpers/__init__.py", string.Empty);
            yield return ("client/helpers/dependency.py", """
                def instance(interface):
                    return None


                def hasInstance(interface):
                    return False


                def descriptor(interface):
                    return None
                """);
            yield return ("client/helpers/i18n.py", """
                def makeString(key, *args, **kwargs):
                    return key
                """);
            yield return ("client/skeletons/__init__.py", string.Empty);
            yield return ("client/skeletons/gui/__init__.py", string.Empty);
            yield return ("client/skeletons/gui/app_loader.py", """
                class GuiGlobalSpaceID(object):
                    UNDEFINED = 0
                    LOGIN = 1
                    LOBBY = 2
                    BATTLE_LOADING = 3
                    BATTLE = 4


                class IAppLoader(object):

                    def getSpaceID(self):
                        raise NotImplementedError
                """);
            yield return ("client/skeletons/gui/battle_session.py", """
                class IBattleSessionProvider(object):

                    @property
                    def arenaVisitor(self):
                        raise NotImplementedError
                """);
            yield return ("client/skeletons/gui/lobby_context.py", """
                class ILobbyContext(object):

                    def getServerSettings(self):
                        raise NotImplementedError
                """);
            yield return ("client/skeletons/gui/shared/__init__.py", """
                class IItemsCache(object):

                    @property
                    def items(self):
                        raise NotImplementedError
                """);
            yield return ("client/messenger/__init__.py", string.Empty);
            yield return ("client/messenger/MessengerEntry.py", """
                class MessengerEntry(object):

                    @property
                    def gui(self):
                        return None


                g_instance = MessengerEntry()
                """);
            yield return ("client/messenger/gui/__init__.py", string.Empty);
            yield return ("client/messenger/gui/entry_decorator.py", """
                def addClientMessage(message, isCurrentPlayer=False):
                    pass
                """);
        }
    }

    // ================================================================= иконки своих модов

    /// <summary>Иконки 50×50 для списка модов: рисуются здесь же, векторно.</summary>
    private static class IconArt
    {
        private static readonly Brush Back = new SolidColorBrush(Color.FromRgb(0x22, 0x2C, 0x36));

        public static byte[] Clock() => Draw(dc =>
        {
            var accent = new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xC9, 0x4C)), 3.5);
            dc.DrawEllipse(null, accent, new Point(25, 26), 15, 15);
            dc.DrawLine(accent, new Point(25, 26), new Point(25, 16));
            dc.DrawLine(accent, new Point(25, 26), new Point(32, 30));
            dc.DrawLine(accent, new Point(20, 7), new Point(30, 7));
        });

        public static byte[] Notes() => Draw(dc =>
        {
            var accent = new Pen(new SolidColorBrush(Color.FromRgb(0x6F, 0xCF, 0x97)), 3);
            dc.DrawRoundedRectangle(null, accent, new Rect(12, 9, 26, 32), 3, 3);
            dc.DrawLine(accent, new Point(18, 18), new Point(32, 18));
            dc.DrawLine(accent, new Point(18, 25), new Point(32, 25));
            dc.DrawLine(accent, new Point(18, 32), new Point(27, 32));
        });

        public static byte[] Target() => Draw(dc =>
        {
            var accent = new Pen(new SolidColorBrush(Color.FromRgb(0x56, 0xCC, 0xF2)), 3);
            dc.DrawEllipse(null, accent, new Point(25, 25), 13, 13);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0x56, 0xCC, 0xF2)), null, new Point(25, 25), 3, 3);
            dc.DrawLine(accent, new Point(25, 6), new Point(25, 14));
            dc.DrawLine(accent, new Point(25, 36), new Point(25, 44));
            dc.DrawLine(accent, new Point(6, 25), new Point(14, 25));
            dc.DrawLine(accent, new Point(36, 25), new Point(44, 25));
        });

        public static byte[] Diamond() => Draw(dc =>
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                g.BeginFigure(new Point(25, 8), isFilled: true, isClosed: true);
                g.LineTo(new Point(42, 25), true, false);
                g.LineTo(new Point(25, 42), true, false);
                g.LineTo(new Point(8, 25), true, false);
            }

            dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xBB, 0x6B, 0xD9)), null, geometry);
        });

        private static byte[] Draw(Action<DrawingContext> glyph)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                dc.DrawRoundedRectangle(Back, null, new Rect(0, 0, 50, 50), 8, 8);
                glyph(dc);
            }

            var bitmap = new RenderTargetBitmap(50, 50, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return ms.ToArray();
        }
    }
}
