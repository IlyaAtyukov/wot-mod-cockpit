using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Services;

namespace WotModCockpit.App;

/// <summary>
/// Точка входа WPF. Здесь только DI и глобальный перехват исключений — никакой логики кокпита.
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _provider;

    /// <summary>
    /// Контейнер приложения. Доступен после <see cref="OnStartup"/>.
    /// Обращение до старта бросает <see cref="InvalidOperationException"/>.
    /// </summary>
    public IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("Контейнер ещё не собран: OnStartup не выполнялся.");

    /// <summary>Текущий экземпляр приложения, приведённый к <see cref="App"/>.</summary>
    public static new App Current => (App)Application.Current;

    /// <inheritdoc />
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ConfigureBindingTrace();

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _provider = AppServices.BuildServiceProvider();

        // ДО главного окна, и это не случайно: часть служб читает настройки в момент
        // создания, а создаются они вместе с окном. Если папку с репозиториями перенесли,
        // к этому моменту пути в настройках уже обязаны смотреть на новое место — иначе
        // проекты пользователя в «Модах» окажутся чужими, а исходники клиента пропадут.
        SettingsRelocator.HealStore(
            _provider.GetRequiredService<ISettingsStore>(), AppContext.BaseDirectory);

        var window = _provider.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        _provider?.Dispose();
        _provider = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Включает протокол ошибок привязки WPF, если задано <c>WOTCOCKPIT_TRACE_BINDINGS=1</c>.
    /// <para>
    /// Ошибки привязки WPF по умолчанию уходят молча: свойство просто остаётся пустым.
    /// В кокпите это опасно — «пустая панель» неотличима от «мод в игре не найден».
    /// Протокол пишется в <c>%TEMP%\wotcockpit-bindings.log</c> и по умолчанию выключен,
    /// чтобы ничего не стоить в обычном запуске.
    /// </para>
    /// </summary>
    private static void ConfigureBindingTrace()
    {
        if (Environment.GetEnvironmentVariable("WOTCOCKPIT_TRACE_BINDINGS") != "1")
        {
            return;
        }

        try
        {
            string path = Path.Combine(Path.GetTempPath(), "wotcockpit-bindings.log");
            var writer = new StreamWriter(path, append: false) { AutoFlush = true };

            PresentationTraceSources.Refresh();
            PresentationTraceSources.DataBindingSource.Listeners.Add(new TextWriterTraceListener(writer));
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        }
        catch (IOException)
        {
            // Диагностика не имеет права мешать запуску.
        }
    }

    /// <summary>
    /// Показывает необработанное исключение UI-потока вместо тихого падения.
    /// Каркасная версия: UI-агент заменит на нормальный диалог с копированием стека.
    /// </summary>
    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.ToString(),
            "WoT Mod Cockpit — необработанное исключение",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
