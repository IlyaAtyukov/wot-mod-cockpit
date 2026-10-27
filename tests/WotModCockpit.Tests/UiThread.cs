using System.Diagnostics;
using System.Windows.Threading;

namespace WotModCockpit.Tests;

/// <summary>
/// Настоящий UI-поток для тестов <see cref="WotModCockpit.App.ViewModels.MainViewModel"/>:
/// STA-поток со своим <see cref="Dispatcher"/> и работающим циклом сообщений.
/// <para>
/// Без него конвейер ленты не проверить: он держится на <see cref="DispatcherTimer"/>
/// (такт выгрузки 100 мс), а таймер тикает только внутри живого цикла диспетчера.
/// Окно при этом не создаётся — тестируется модель представления, а не разметка.
/// </para>
/// </summary>
public sealed class UiThread : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    /// <summary>Поднимает STA-поток и дожидается готовности его диспетчера.</summary>
    public UiThread()
    {
        using var ready = new ManualResetEventSlim(false);

        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "cockpit-ui-test"
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(10)))
            throw new InvalidOperationException("UI-поток не поднялся за 10 секунд.");
    }

    /// <summary>Диспетчер этого потока.</summary>
    public Dispatcher Dispatcher => _dispatcher!;

    /// <summary>Выполняет действие в UI-потоке и дожидается конца.</summary>
    /// <param name="action">Что выполнить.</param>
    public void Invoke(Action action) => Dispatcher.Invoke(action, DispatcherPriority.Send);

    /// <summary>Выполняет функцию в UI-потоке и возвращает результат.</summary>
    /// <typeparam name="T">Тип результата.</typeparam>
    /// <param name="func">Что выполнить.</param>
    /// <returns>Результат функции.</returns>
    public T Invoke<T>(Func<T> func) => Dispatcher.Invoke(func, DispatcherPriority.Send);

    /// <summary>
    /// Ждёт выполнения условия, давая UI-потоку крутить очередь и тикать таймерами.
    /// Условие вычисляется В UI-потоке — трогать модель представления снаружи нельзя.
    /// </summary>
    /// <param name="condition">Условие.</param>
    /// <param name="what">Что именно ждём (для сообщения об ошибке).</param>
    /// <param name="timeout">Терпение; по умолчанию 10 секунд.</param>
    /// <param name="diagnostics">Что показать, если не дождались.</param>
    public void WaitUntil(
        Func<bool> condition,
        string what,
        TimeSpan? timeout = null,
        Func<string>? diagnostics = null)
    {
        TimeSpan limit = timeout ?? TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < limit)
        {
            if (Invoke(condition))
                return;

            Thread.Sleep(15);
        }

        string tail = diagnostics is null ? string.Empty : " Состояние: " + Invoke(diagnostics);
        Assert.Fail($"Не дождались: {what} (за {limit.TotalSeconds:0} с).{tail}");
    }

    /// <summary>Прокручивает очередь диспетчера, чтобы отработали уже запланированные такты.</summary>
    /// <param name="rounds">Сколько раз пропустить очередь через себя.</param>
    /// <param name="pauseMs">Пауза между прокрутками — чтобы успели тикнуть таймеры.</param>
    public void Pump(int rounds = 4, int pauseMs = 60)
    {
        for (int i = 0; i < rounds; i++)
        {
            Invoke(() => { });
            Thread.Sleep(pauseMs);
        }

        Invoke(() => { });
    }

    /// <summary>Останавливает диспетчер и дожидается конца потока.</summary>
    public void Dispose()
    {
        try
        {
            Dispatcher.InvokeShutdown();
        }
        catch (Exception)
        {
            // диспетчер мог уже завершиться — закрытию это не мешает
        }

        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
