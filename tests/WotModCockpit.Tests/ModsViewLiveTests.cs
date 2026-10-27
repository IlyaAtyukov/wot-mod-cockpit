using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Экран «Моды» целиком: НАСТОЯЩАЯ модель поверх фейкового каталога игры в НАСТОЯЩЕМ списке.
///
/// <para><b>Зачем, когда есть тесты модели.</b> Выделение живёт в двух местах — в
/// <c>Selected</c> модели и в <c>SelectedItem</c> списка (с группировкой), связанных в обе
/// стороны, а перечитывание заменяет ВСЕ объекты строк новыми. Тесты модели, где списка нет,
/// не увидели бы, если бы список при этом сбрасывал выделение. Теперь каталог перечитывается
/// при каждом показе экрана и по каждому изменению в каталоге, и слетающее выделение закрывало
/// бы карточку прямо под рукой пользователя — поэтому это проверяется в живом окне.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ModsViewLiveTests
{
    /// <summary>Перечитывание каталога не снимает выделение: и в модели, и в самом списке.</summary>
    [Fact]
    public void Refresh_KeepsTheSelectedRow_InTheRealList()
    {
        using FakeGameDir game = new();
        _ = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        game.AddMod("ru.example.alpha_1.0.0.mtmod", FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "описание Alpha Path"));
        game.AddMod("tv.example.battleatlas_9.9.mtmod", FakeGameDir.Meta("tv.example.battleatlas", "9.9", "Battle Atlas", "чужой"));

        using var screen = WpfHost.Invoke(() => new LiveScreen(game));

        Wait(WpfHost.Invoke(() => screen.Vm.EnsureLoadedAsync()));

        string chosen = WpfHost.Invoke(() =>
        {
            InstalledModViewModel row = screen.Vm.Mods.First(r => r.FileName == "tv.example.battleatlas_9.9.mtmod");
            screen.List.SelectedItem = row;
            Pump();
            return row.FullPath;
        });

        Wait(WpfHost.Invoke(() => screen.Vm.RefreshCommand.ExecuteAsync(null)));

        (string? model, string? list, bool same) = WpfHost.Invoke(() =>
        {
            Pump();
            return (screen.Vm.Selected?.FullPath,
                    (screen.List.SelectedItem as InstalledModViewModel)?.FullPath,
                    ReferenceEquals(screen.Vm.Selected, screen.List.SelectedItem));
        });

        Assert.Equal(chosen, model);
        Assert.Equal(chosen, list);
        Assert.True(same, "модель и список выделяют разные объекты строки");
    }

    private static void Wait(Task task)
    {
        if (!task.Wait(TimeSpan.FromSeconds(30)))
        {
            Assert.Fail("операция экрана не завершилась за 30 секунд");
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deep in Descendants<T>(child))
            {
                yield return deep;
            }
        }
    }

    /// <summary>Экран в окне за пределами экрана. Создаётся и закрывается в потоке WPF-хоста.</summary>
    private sealed class LiveScreen : IDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly Window _window;

        public LiveScreen(FakeGameDir game)
        {
            _provider = App.AppServices.BuildServiceProvider();
            MainViewModel main = _provider.GetRequiredService<MainViewModel>();
            main.GameDir = game.Root;

            Vm = new ModsViewModel(
                main,
                new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
                {
                    OperationLogPath = Path.Combine(game.Root, "mods-operations.log"),
                    WriteOperationLog = false
                }),
                new FixedLocator(game.Install),
                new FixedSettings(Path.Combine(game.Root, "projects", "wot-mod-alpha")),
                new SilentEditor());

            var view = new ModsView { DataContext = Vm };
            _window = new Window
            {
                Content = view,
                Width = 1200,
                Height = 800,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
                ShowInTaskbar = false
            };
            _window.Show();
            Pump();

            List = Descendants<ListBox>(view).First(l => l.Name == "ModList");
        }

        public ModsViewModel Vm { get; }

        public ListBox List { get; }

        public void Dispose() => WpfHost.Invoke(() =>
        {
            _window.Close();
            Vm.Dispose();
            _provider.Dispose();
        });
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
            // тесты настройки не пишут
        }
    }

    private sealed class SilentEditor : IEditorLauncher
    {
        /// <summary>Не поднимается никогда: редактор в тестах не запускается.</summary>
        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public bool IsAvailable => false;

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }
}
