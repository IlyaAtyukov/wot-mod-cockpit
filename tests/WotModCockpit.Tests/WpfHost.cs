using System.Windows;
using System.Windows.Threading;

namespace WotModCockpit.Tests;

/// <summary>
/// Единственный на процесс WPF-хост: STA-поток, на котором живёт <see cref="Application"/>
/// с боевыми словарями ресурсов (App.xaml сливает DarkTheme.xaml и Controls.xaml
/// в <see cref="Application.Resources"/>, и все <c>{StaticResource}</c> разметки ищутся там).
///
/// <para><b>Почему он один и общий.</b> <see cref="Application"/> — синглтон домена и имеет
/// потоковую привязку: второй экземпляр бросает, а обращение к ресурсам первого из чужого
/// потока — тоже. Поэтому поток поднимается лениво, не гасится, и ВСЕ тесты разметки
/// шлют работу в него.</para>
/// </summary>
internal static class WpfHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>Выполняет функцию в UI-потоке и возвращает результат.</summary>
    /// <typeparam name="T">Тип результата.</typeparam>
    /// <param name="func">Что выполнить.</param>
    /// <returns>Результат.</returns>
    public static T Invoke<T>(Func<T> func) => HostDispatcher.Invoke(func, DispatcherPriority.Send);

    /// <summary>Выполняет действие в UI-потоке.</summary>
    /// <param name="action">Что выполнить.</param>
    public static void Invoke(Action action) => HostDispatcher.Invoke(action, DispatcherPriority.Send);

    private static Dispatcher HostDispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher is not null)
                    return _dispatcher;

                using var ready = new ManualResetEventSlim(false);
                Exception? failure = null;

                var thread = new Thread(() =>
                {
                    try
                    {
                        if (Application.Current is null)
                        {
                            var app = new App.App();
                            app.InitializeComponent();
                            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        }

                        _dispatcher = Dispatcher.CurrentDispatcher;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                    }
                    finally
                    {
                        ready.Set();
                    }

                    if (failure is null)
                        Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "cockpit-wpf-host"
                };

                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();

                if (!ready.Wait(TimeSpan.FromSeconds(30)))
                    throw new InvalidOperationException("WPF-хост не поднялся за 30 секунд.");

                if (failure is not null)
                    throw new InvalidOperationException("WPF-хост не поднялся: " + failure.Message, failure);

                return _dispatcher!;
            }
        }
    }
}
