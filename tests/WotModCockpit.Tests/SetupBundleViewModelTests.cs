using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Диалог «Собрать сетап»: модель (<see cref="SetupBundleViewModel"/>), окно
/// (<see cref="SetupBundleDialog"/>) и кнопка экрана «Моды».
/// <para>
/// Сборка проектов подменяется (build.py здесь не запускается), а упаковка — настоящая:
/// <see cref="SetupBundleBuilder"/> с фальшивой заглушкой, итог проверяется читателем сетапа.
/// </para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class SetupBundleViewModelTests : IDisposable
{
    private static readonly DateTime Today = new(2026, 10, 2, 15, 0, 0);

    /// <summary>Фальшивая игра: класть сетап внутрь неё нельзя.</summary>
    private readonly FakeGameDir _game = new();

    /// <summary>Рабочая папка вне игры: проекты и сетапы.</summary>
    private readonly CoreTempDir _work = new("wotcockpit-setupvm");

    private readonly string _stub;
    private readonly string _out;

    public SetupBundleViewModelTests()
    {
        _stub = Path.Combine(_work.Path, "stub", "WotModCockpit.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(_stub)!);
        byte[] bytes = new byte[2048];
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(_stub, bytes);

        _out = Path.Combine(_work.Path, "setups");
    }

    public void Dispose()
    {
        _game.Dispose();
        _work.Dispose();
    }

    // ================================================================= выбор

    /// <summary>
    /// Снятая в прошлый раз галочка остаётся снятой, отмеченная — отмеченной, а проект, которого
    /// тогда ещё не было, отмечается сам.
    /// </summary>
    [Fact]
    public void Defaults_RememberChoice_AndCheckNewProjects()
    {
        var settings = new SetupBundleSettings
        {
            SelectedModIds = ["ru.test.alpha"],
            KnownModIds = ["ru.test.alpha", "ru.test.beta"],
        };

        SetupBundleViewModel vm = Model(Candidates("alpha", "beta", "gamma"), settings: settings);

        Assert.True(Row(vm, "alpha").IsChecked);
        Assert.False(Row(vm, "beta").IsChecked);
        Assert.True(Row(vm, "gamma").IsChecked);
        Assert.Equal("выбрано 2 из 3", vm.SelectionSummary);
        Assert.Equal("2026.10.02", vm.BundleVersion);
        Assert.Equal(SetupBundleSettings.DefaultName, vm.BundleName);
    }

    /// <summary>Выбор возвращается в настройки целиком: отмеченные и все виденные моды.</summary>
    [Fact]
    public void CurrentSettings_KeepSelectionNameFolderAndKnownIds()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha", "beta"));
        Row(vm, "beta").IsChecked = false;
        vm.BundleName = "  Моды для друга ";
        vm.Rebuild = false;

        SetupBundleSettings s = vm.CurrentSettings();

        Assert.Equal(["ru.test.alpha"], s.SelectedModIds);
        Assert.Equal(["ru.test.alpha", "ru.test.beta"], s.KnownModIds);
        Assert.Equal("Моды для друга", s.Name);
        Assert.Equal(_out, s.OutputDir);
        Assert.False(s.Rebuild);
    }

    /// <summary>«все» и «ни одного» меняют галочки и итог разом.</summary>
    [Fact]
    public void SelectAllAndNone_UpdateSummaryAndCommand()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha", "beta"));

        vm.SelectNoneCommand.Execute(null);
        Assert.Equal(0, vm.SelectedCount);
        Assert.False(vm.BuildCommand.CanExecute(null));
        Assert.Equal("Отметьте хотя бы один мод.", vm.Problem);

        vm.SelectAllCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCount);
        Assert.True(vm.BuildCommand.CanExecute(null));
    }

    // ================================================================= что мешает собрать

    [Fact]
    public void Problem_NoStub_BlocksBuild()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha"), stub: null);

        Assert.Contains("Заглушка установщика не найдена", vm.Problem);
        Assert.False(vm.CanBuild);
    }

    /// <summary>Положить сетап в папку игры нельзя: кокпит туда не пишет.</summary>
    [Fact]
    public void Problem_OutputInsideGame_BlocksBuild()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha"));
        vm.OutputDir = Path.Combine(_game.Root, "mods");

        Assert.Contains("папку игры", vm.Problem);
        Assert.False(vm.CanBuild);
    }

    /// <summary>Без пересборки проект без сборки упаковать нечем.</summary>
    [Fact]
    public void Problem_NoBuild_WithoutRebuild_BlocksBuild()
    {
        string dir = _game.AddProject("wot-mod-raw", "ru.test.raw", displayName: "Raw", buildArtifact: false);
        SetupBundleViewModel vm = Model(SetupBundleCandidates.Read([dir]));
        vm.Rebuild = false;

        Assert.Contains("нет сборки", vm.Problem);

        vm.Rebuild = true;
        Assert.Equal(string.Empty, vm.Problem);
    }

    /// <summary>Один мод в двух проектах (например, копия шаблона без смены id) — сетап не соберётся.</summary>
    [Fact]
    public void Problem_SameModIdInTwoProjects_BlocksBuild()
    {
        string a = _game.AddProject("wot-mod-one", "ru.test.same", displayName: "One");
        string b = _game.AddProject("wot-mod-two", "ru.test.same", displayName: "Two");
        SetupBundleViewModel vm = Model(SetupBundleCandidates.Read([a, b]));

        Assert.Contains("ru.test.same", vm.Problem);
    }

    [Fact]
    public void Problem_BadVersion_BlocksBuild()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha"));
        vm.BundleVersion = "версия 1";

        Assert.Contains("латинские", vm.Problem);
        Assert.False(vm.CanBuild);
    }

    // ================================================================= сборка

    /// <summary>Без пересборки в сетап уходят готовые сборки из dist — те самые байты.</summary>
    [Fact]
    public async Task Build_WithoutRebuild_PacksDistArtifacts()
    {
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha", "beta");
        int builds = 0;
        SetupBundleViewModel vm = Model(candidates, build: _ =>
        {
            builds++;
            return Task.FromResult(ModsBuildOutcome.NotStarted("не должно вызываться"));
        });
        vm.Rebuild = false;

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.Equal(0, builds);
        Assert.True(vm.LastRunSucceeded, vm.StatusText);
        Assert.NotNull(vm.ResultPath);
        Assert.Equal(Path.Combine(_out, "Сборка модов 2026.10.02.exe"), vm.ResultPath);

        SetupBundleInfo info = SetupBundleReader.Read(vm.ResultPath!);
        Assert.Equal(["ru.test.alpha", "ru.test.beta"], info.Manifest.Components.Select(c => c.Id));
        foreach (SetupBundleCandidate c in candidates)
        {
            Assert.Equal(File.ReadAllBytes(c.Project.BuiltArtifactPath!), SetupBundleReader.ReadComponent(vm.ResultPath!, c.ModId));
        }

        Assert.StartsWith("Готово", vm.StatusText);
        Assert.True(vm.OpenFolderCommand.CanExecute(null));
    }

    /// <summary>
    /// С пересборкой упаковывается ровно то, что сообщил build.py, а не угаданный файл из dist:
    /// здесь «build.py» кладёт свежий архив в другое место, и в сетапе должен оказаться он.
    /// </summary>
    [Fact]
    public async Task Build_WithRebuild_PacksWhatBuildPyReported()
    {
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha");
        string fresh = Path.Combine(_game.Root, "fresh", "ru.test.alpha_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        FakeGameDir.WriteArchive(fresh, FakeGameDir.Meta("ru.test.alpha", "1.0.0", "Alpha", "свежая"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "СВЕЖАЯ СБОРКА")], compress: false);

        var built = new List<string>();
        SetupBundleViewModel vm = Model(candidates, build: dir =>
        {
            built.Add(dir);
            return Task.FromResult(Built(dir, fresh));
        });

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.True(vm.LastRunSucceeded, vm.StatusText);
        Assert.Equal([candidates[0].Project.ProjectDir], built);
        Assert.Equal(File.ReadAllBytes(fresh), SetupBundleReader.ReadComponent(vm.ResultPath!, "ru.test.alpha"));
        Assert.Contains(vm.Log, l => l.Contains("собран «Alpha»"));
    }

    /// <summary>Первая же неудачная сборка останавливает всё: сетапа нет, следующие проекты не собираются.</summary>
    [Fact]
    public async Task Build_FirstFailedBuild_StopsEverything()
    {
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha", "beta", "gamma");
        int calls = 0;
        SetupBundleViewModel vm = Model(candidates, build: dir =>
        {
            calls++;
            return Task.FromResult(dir.EndsWith("beta", StringComparison.Ordinal)
                ? Failed(dir)
                : Built(dir, candidates.Single(c => c.Project.ProjectDir == dir).Project.BuiltArtifactPath!));
        });

        await vm.BuildCommand.ExecuteAsync(null);

        Assert.Equal(2, calls);
        Assert.False(vm.LastRunSucceeded);
        Assert.Null(vm.ResultPath);
        Assert.Contains("Beta", vm.StatusText);
        Assert.False(Directory.Exists(_out) && Directory.EnumerateFiles(_out).Any());
    }

    /// <summary>Существующий сетап заменяется только после вопроса; «нет» оставляет его как был.</summary>
    [Fact]
    public async Task Build_ExistingFile_AsksBeforeReplacing()
    {
        Directory.CreateDirectory(_out);
        string target = Path.Combine(_out, "Сборка модов 2026.10.02.exe");
        File.WriteAllText(target, "старый сетап");

        bool answer = false;
        int asked = 0;
        SetupBundleViewModel vm = Model(Candidates("alpha"), confirm: (_, body) =>
        {
            asked++;
            Assert.Contains(target, body);
            return answer;
        });
        vm.Rebuild = false;
        Assert.Contains("уже есть", vm.Warning);

        await vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal(1, asked);
        Assert.Equal("старый сетап", File.ReadAllText(target));
        Assert.Null(vm.ResultPath);

        answer = true;
        await vm.BuildCommand.ExecuteAsync(null);
        Assert.Equal(2, asked);
        Assert.Equal("ru.test.alpha", SetupBundleReader.Read(target).Manifest.Components.Single().Id);
    }

    /// <summary>Устаревшая сборка без пересборки — не запрет, но предупреждение.</summary>
    [Fact]
    public void Warning_StaleBuildWithoutRebuild()
    {
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha");
        string src = Path.Combine(candidates[0].Project.ProjectDir, "src", "mod_alpha.py");
        File.WriteAllText(src, "# правка");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(5));

        SetupBundleViewModel vm = Model(SetupBundleCandidates.Read([candidates[0].Project.ProjectDir]));
        vm.Rebuild = false;

        Assert.Contains("старше правок", vm.Warning);
        Assert.Equal(string.Empty, vm.Problem);
        Assert.Contains("старше правок", Row(vm, "alpha").BuildState);
    }

    // ================================================================= окно

    /// <summary>
    /// Окно собирается со всеми привязками: строки проектов с флажками, поля, кнопки. Картинка
    /// окна сохраняется во временную папку — по ней удобно проверять вёрстку глазами.
    /// </summary>
    [Fact]
    public void Dialog_ShowsProjectsWithCheckboxes()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha", "beta"));
        Row(vm, "beta").IsChecked = false;

        (int checkboxes, int checkedBoxes, string name, bool buildEnabled, string png) = WpfHost.Invoke(() =>
        {
            SetupBundleDialog dialog = Open(vm);
            try
            {
                CheckBox[] boxes = Descendants<CheckBox>(dialog).Where(c => c.Name != "RebuildBox").ToArray();
                string path = Path.Combine(_work.Path, "dialog.png");
                Snapshot(dialog, path);

                // Посмотреть вёрстку глазами: WOTCOCKPIT_TEST_SNAPSHOTS=<папка> сохраняет картинку туда.
                if (Environment.GetEnvironmentVariable("WOTCOCKPIT_TEST_SNAPSHOTS") is { Length: > 0 } keep)
                {
                    Directory.CreateDirectory(keep);
                    File.Copy(path, Path.Combine(keep, "setup-dialog.png"), overwrite: true);
                }
                return (boxes.Length, boxes.Count(b => b.IsChecked == true),
                        ((TextBox)dialog.FindName("NameBox")!).Text,
                        ((Button)dialog.FindName("BuildButton")!).IsEnabled,
                        path);
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.Equal(2, checkboxes);
        Assert.Equal(1, checkedBoxes);
        Assert.Equal(SetupBundleSettings.DefaultName, name);
        Assert.True(buildEnabled);
        Assert.True(new FileInfo(png).Length > 1000);
    }

    /// <summary>
    /// После удачной сборки в окне видны итог, журнал и кнопка «Показать в папке», которая
    /// показывает именно готовый файл.
    /// </summary>
    [Fact]
    public void Dialog_AfterBuild_ShowsResultLogAndRevealButton()
    {
        string? revealed = null;
        SetupBundleViewModel vm = new(Candidates("alpha", "beta"), new SetupBundleContext
        {
            StubPath = _stub,
            GameRoot = _game.Root,
            BuildProject = _ => Task.FromResult(ModsBuildOutcome.NotStarted("не нужна")),
            Confirm = (_, _) => false,
            Reveal = path => revealed = path,
            Settings = new SetupBundleSettings { OutputDir = _out, Rebuild = false },
            Now = () => Today,
        });

        (bool revealVisible, int logLines, string status) = WpfHost.Invoke(() =>
        {
            SetupBundleDialog dialog = Open(vm);
            try
            {
                Task build = vm.BuildCommand.ExecuteAsync(null);
                for (int i = 0; i < 200 && !build.IsCompleted; i++)
                {
                    Pump();
                    Thread.Sleep(10);
                }

                Pump();
                string path = Path.Combine(_work.Path, "dialog-done.png");
                Snapshot(dialog, path);
                if (Environment.GetEnvironmentVariable("WOTCOCKPIT_TEST_SNAPSHOTS") is { Length: > 0 } keep)
                {
                    File.Copy(path, Path.Combine(keep, "setup-dialog-done.png"), overwrite: true);
                }

                var reveal = (Button)dialog.FindName("OpenFolderButton")!;
                bool visible = reveal.IsVisible;
                var peer = new ButtonAutomationPeer(reveal);
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                Pump();   // нажатие через автоматизацию доходит до кнопки через очередь диспетчера
                return (visible, ((ListBox)dialog.FindName("LogList")!).Items.Count, ((TextBlock)dialog.FindName("StatusLine")!).Text);
            }
            finally
            {
                dialog.Close();
            }
        });

        Assert.True(revealVisible);
        Assert.Equal(vm.ResultPath, revealed);
        Assert.True(logLines >= 3, "в журнале должны быть оба мода и путь сетапа");
        Assert.StartsWith("Готово", status);

        // Только что собранный файл — не «такой файл уже есть».
        Assert.Equal(string.Empty, vm.Warning);
    }

    /// <summary>
    /// Выбор отмеченного мода переживает закрытие окна без сборки: после повторного открытия
    /// снятая галочка остаётся снятой (настройки — те, что модель отдала при закрытии).
    /// </summary>
    [Fact]
    public void CurrentSettings_RoundTripThroughANewDialog()
    {
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha", "beta");
        SetupBundleViewModel first = Model(candidates);
        Row(first, "alpha").IsChecked = false;

        SetupBundleViewModel second = Model(candidates, settings: first.CurrentSettings());

        Assert.False(Row(second, "alpha").IsChecked);
        Assert.True(Row(second, "beta").IsChecked);
    }

    /// <summary>«Закрыть» закрывает окно — через событие модели, кнопка с командой сама этого не делает.</summary>
    [Fact]
    public void Dialog_CloseButton_ClosesTheWindow()
    {
        SetupBundleViewModel vm = Model(Candidates("alpha"));

        bool stillOpen = WpfHost.Invoke(() =>
        {
            SetupBundleDialog dialog = Open(vm);
            var peer = new ButtonAutomationPeer((Button)dialog.FindName("CloseButton")!);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
            Pump();
            bool open = dialog.IsVisible;
            if (open)
            {
                dialog.Close();
            }

            return open;
        });

        Assert.False(stillOpen, "после «Закрыть» окно осталось открытым");
    }

    /// <summary>Посреди сборки окно не закрывается ни кнопкой, ни крестиком.</summary>
    [Fact]
    public void Dialog_WhileWorking_RefusesToClose()
    {
        var gate = new TaskCompletionSource<ModsBuildOutcome>();
        IReadOnlyList<SetupBundleCandidate> candidates = Candidates("alpha");
        SetupBundleViewModel vm = Model(candidates, build: _ => gate.Task);

        (bool closeEnabled, bool openAfterClose) = WpfHost.Invoke(() =>
        {
            SetupBundleDialog dialog = Open(vm);
            vm.BuildCommand.Execute(null);
            Pump();
            bool enabled = ((Button)dialog.FindName("CloseButton")!).IsEnabled;
            dialog.Close();
            Pump();
            bool open = dialog.IsVisible;

            gate.SetResult(ModsBuildOutcome.NotStarted("тест закончен"));
            Pump();
            dialog.Close();
            return (enabled, open);
        });

        Assert.False(closeEnabled);
        Assert.True(openAfterClose, "окно закрылось посреди сборки");
    }

    // ================================================================= экран «Моды»

    /// <summary>
    /// Кнопка экрана «Моды» открывает диалог со своими проектами (соседи известного проекта), а
    /// выбор после закрытия уходит в общие настройки главной модели.
    /// </summary>
    [Fact]
    public async Task ModsScreen_BuildSetup_OpensDialog_AndRemembersChoice()
    {
        string known = _game.AddProject("wot-mod-alpha", "ru.test.alpha", displayName: "Alpha");
        _game.AddProject("wot-mod-beta", "ru.test.beta", displayName: "Beta");

        using ServiceProvider provider = App.AppServices.BuildServiceProvider();
        MainViewModel main = provider.GetRequiredService<MainViewModel>();
        main.GameDir = _game.Root;

        SetupBundleViewModel? shown = null;
        using var mods = new ModsViewModel(
            main,
            new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
            {
                OperationLogPath = Path.Combine(_game.Root, "mods-operations.log"),
                WriteOperationLog = false,
            }),
            new FixedLocator(_game.Install),
            new FixedSettings(known),
            new NoEditor())
        {
            FindSetupStub = () => _stub,
            SetupBundleHandler = dialog =>
            {
                shown = dialog;
                dialog.Components.Single(c => c.ModId == "ru.test.beta").IsChecked = false;
            },
        };

        await mods.BuildSetupCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(["Alpha", "Beta"], shown!.Components.Select(c => c.Title));
        Assert.Equal(string.Empty, shown.Problem);
        Assert.Equal(["ru.test.alpha"], main.SetupBundleSettings.SelectedModIds);
        Assert.Contains("ru.test.beta", main.SetupBundleSettings.KnownModIds);
        Assert.Equal("сетап не собирали", mods.StatusText);
        Assert.True(mods.BuildSetupCommand.CanExecute(null));
    }

    // ================================================================= помощники

    private SetupBundleViewModel Model(
        IReadOnlyList<SetupBundleCandidate> candidates,
        Func<string, Task<ModsBuildOutcome>>? build = null,
        Func<string, string, bool>? confirm = null,
        SetupBundleSettings? settings = null,
        string? stub = "default")
    {
        settings ??= new SetupBundleSettings();
        if (string.IsNullOrEmpty(settings.OutputDir))
        {
            settings.OutputDir = _out;
        }

        return new SetupBundleViewModel(candidates, new SetupBundleContext
        {
            StubPath = stub == "default" ? _stub : stub,
            GameRoot = _game.Root,
            GameVersion = "1.45.0.0",
            BuildProject = build ?? (dir => Task.FromResult(ModsBuildOutcome.NotStarted("сборка не подменена"))),
            Confirm = confirm ?? ((_, _) => false),
            Settings = settings,
            Now = () => Today,
        });
    }

    /// <summary>Проекты с собранным dist, вне каталога игры (так в жизни и бывает).</summary>
    private IReadOnlyList<SetupBundleCandidate> Candidates(params string[] names)
    {
        string root = Path.Combine(_work.Path, "repos");
        var dirs = new List<string>();
        foreach (string name in names)
        {
            string dir = Path.Combine(root, "wot-mod-" + name);
            string id = "ru.test." + name;
            string title = char.ToUpperInvariant(name[0]) + name[1..];
            Directory.CreateDirectory(Path.Combine(dir, "src"));
            File.WriteAllText(Path.Combine(dir, "build.py"), "# fake\n");
            File.WriteAllText(Path.Combine(dir, "src", "meta.xml"), FakeGameDir.Meta(id, "1.0.0", title, "описание " + title));
            Directory.CreateDirectory(Path.Combine(dir, "dist"));
            string artifact = Path.Combine(dir, "dist", id + "_1.0.0.mtmod");
            FakeGameDir.WriteArchive(artifact, FakeGameDir.Meta(id, "1.0.0", title, "описание " + title),
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_" + name + ".pyc", "bytecode " + name)], compress: false);
            // Исходник того, что лежит в архиве: сборка, в которой есть файл без исходника, устарела.
            string entry = Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods", "mod_" + name + ".py");
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, "def init():\n    pass\n");
            File.SetLastWriteTimeUtc(entry, DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(Path.Combine(dir, "src", "meta.xml"), DateTime.UtcNow.AddMinutes(-30));
            File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(-10));
            dirs.Add(dir);
        }

        return SetupBundleCandidates.Read(dirs);
    }

    private static SetupComponentViewModel Row(SetupBundleViewModel vm, string name) =>
        vm.Components.Single(c => c.ModId == "ru.test." + name);

    private static ModsBuildOutcome Built(string dir, string artifact) => new(
        new ModProject { ProjectDir = dir, ModId = "x", Version = "1.0.0" },
        new BuildResult(true, 0, artifact, TimeSpan.FromSeconds(1.5)),
        "собрано");

    private static ModsBuildOutcome Failed(string dir) => new(
        new ModProject { ProjectDir = dir, ModId = "x", Version = "1.0.0" },
        new BuildResult(false, 1, string.Empty, TimeSpan.FromSeconds(0.5)),
        "build.py упал: SyntaxError");

    private static SetupBundleDialog Open(SetupBundleViewModel vm)
    {
        var dialog = new SetupBundleDialog
        {
            DataContext = vm,
            Left = -32000,
            Top = -32000,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowActivated = false,
        };
        dialog.Show();
        Pump();
        return dialog;
    }

    private static void Snapshot(Window window, string path)
    {
        var target = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        target.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(target));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
    }

    private sealed class FixedLocator(GameInstall install) : IGameInstallLocator
    {
        public GameInstall? Detect(string rootPath) => install;
    }

    private sealed class FixedSettings(string projectDir) : ISettingsStore
    {
        public string SettingsPath => Path.Combine(projectDir, "settings.json");

        public CockpitSettings Load() => new() { LastProjectDir = projectDir };

        public void Save(CockpitSettings settings)
        {
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

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }
}
