using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WotModCockpit.App.Controls;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты СОБСТВЕННОЙ РАМКИ окна (<see cref="ChromeWindow"/> и <c>Styles/Window.xaml</c>).
///
/// <para><b>Зачем проверять рамку живым окном.</b> Системная рамка выключена, и всё, что
/// обычно рисует Windows, рисует шаблон. Промах здесь не даёт ни ошибки сборки, ни
/// исключения: окно просто открывается ПУСТЫМ. Ровно это и случилось — шаблон брался
/// по <c>DefaultStyleKey</c>, то есть искался в <c>Themes/Generic.xaml</c>, которого в
/// сборке нет; окно осталось без шаблона, а будучи прозрачным — без единого пикселя.
/// На экране был виден рабочий стол, и ни сборка, ни тесты об этом не сообщали.</para>
///
/// <para>Поэтому здесь окно поднимается по-настоящему, за пределами экрана, и
/// проверяется результат: шаблон применился, шапка показывает то, что должна, а углы
/// скруглены НА САМОМ ДЕЛЕ — по отрисованным пикселям, со всех четырёх сторон.</para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ChromeWindowTests
{
    /// <summary>Скругление из <c>Styles/Window.xaml</c>: ключ <c>Window.Corner</c>.</summary>
    private const double Corner = 10.0;

    /// <summary>Толщина видимой рамки в шаблоне.</summary>
    private const double FrameLine = 1.0;

    /// <summary>
    /// Ширина полосы, за которую тянут окно, от ВИДИМОЙ рамки внутрь:
    /// <c>ChromeWindow.ResizeBorder</c>.
    /// </summary>
    private const double ResizeBorder = 6.0;

    private const int WM_NCHITTEST = 0x0084;
    private const int HTCLIENT = 1;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;

    /// <summary>
    /// Шаблон обязан доставаться окнам-НАСЛЕДНИКАМ: боевые окна — это MainWindow и
    /// NewProjectDialog, а не сам ChromeWindow. Неявный стиль по точному типу им бы не
    /// достался, и это второй способ получить пустое окно.
    /// </summary>
    [Fact]
    public void Template_ReachesWindowsDerivedFromChromeWindow()
    {
        (bool hasTemplate, bool hasCaption, bool hasClip) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            return (host.Window.Template is not null,
                    host.Part("PART_Caption") is not null,
                    host.Part("PART_Clip") is not null);
        });

        Assert.True(hasTemplate, "окно осталось без шаблона — на экране оно будет пустым");
        Assert.True(hasCaption, "в шаблоне нет шапки PART_Caption");
        Assert.True(hasClip, "в шаблоне нет обрезаемого содержимого PART_Clip");
    }

    /// <summary>
    /// Шапка показывает заголовок и значок окна. Проверка ловит оборванные привязки:
    /// они не роняют приложение, а тихо оставляют шапку пустой.
    /// </summary>
    [Fact]
    public void Caption_ShowsTitleAndIcon()
    {
        (string? title, bool iconShown) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            host.Window.Title = "Проверка заголовка";
            host.Window.Icon = AppIcon();
            host.Pump();

            var caption = (FrameworkElement)host.Part("PART_Caption")!;
            TextBlock? text = Descendants<TextBlock>(caption).FirstOrDefault();
            Image? image = Descendants<Image>(caption).FirstOrDefault();

            return (text?.Text, image?.Source is not null);
        });

        Assert.Equal("Проверка заголовка", title);
        Assert.True(iconShown, "значок окна не дошёл до шапки");
    }

    /// <summary>
    /// Содержимое обрезано по углам, и обрезка накрывает ВСЮ его площадь.
    /// <para>
    /// Раньше обрезку делала маска с привязками размеров внутри кисти. Привязки
    /// считались в отрыве от дерева и давали не тот размер: слева углы скруглялись,
    /// справа шапка выходила за скругление прямым краем.
    /// </para>
    /// </summary>
    [Fact]
    public void Content_IsClippedOverItsWholeArea()
    {
        (Rect bounds, double radius, Size size) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            var clipped = (FrameworkElement)host.Part("PART_Clip")!;
            var clip = (RectangleGeometry)clipped.Clip;

            return (clip.Rect, clip.RadiusX, new Size(clipped.ActualWidth, clipped.ActualHeight));
        });

        Assert.Equal(0.0, bounds.X);
        Assert.Equal(0.0, bounds.Y);
        Assert.Equal(size.Width, bounds.Width, 1);
        Assert.Equal(size.Height, bounds.Height, 1);

        // Радиус берётся внутренний: содержимое лежит ВНУТРИ рамки, и обрезать его по
        // внешнему радиусу значило бы разрешить закрасить саму линию рамки в углах.
        Assert.Equal(Corner - FrameLine, radius, 1);
    }

    /// <summary>
    /// Обрезка пересчитывается при изменении размера окна. Застывшая на первом размере
    /// обрезка — это срезанное содержимое у одних размеров и прямые углы у других.
    /// </summary>
    [Fact]
    public void Clip_FollowsResize()
    {
        (double before, double after) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show(720, 480);
            double first = ((RectangleGeometry)((FrameworkElement)host.Part("PART_Clip")!).Clip).Rect.Width;

            host.Window.Width = 980;
            host.Pump();

            double second = ((RectangleGeometry)((FrameworkElement)host.Part("PART_Clip")!).Clip).Rect.Width;
            return (first, second);
        });

        Assert.True(after > before + 200,
            $"обрезка не пошла за размером окна: было {before:0}, стало {after:0}");
    }

    /// <summary>
    /// Все четыре угла скруглены НА САМОМ ДЕЛЕ. Проверка по отрисованным пикселям:
    /// то, что обрезка задана, ещё не значит, что она сработала по всем сторонам.
    /// </summary>
    [Fact]
    public void AllFourCorners_AreActuallyRounded()
    {
        (byte[] corners, byte[] inside) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            // Непрозрачное содержимое: незакрытый угол виден как полностью
            // непрозрачный пиксель, и придираться к оттенкам не приходится.
            host.Window.Content = new Border { Background = Brushes.Red };
            host.Pump();

            int width = (int)Math.Round(host.Window.ActualWidth);
            int height = (int)Math.Round(host.Window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host.Window);

            // Точка снаружи дуги, но внутри прямого угла рамки: именно её закрашивает
            // необрезанное содержимое. Дуга радиусом 10 с центром в (10, 10) до (2, 2)
            // не достаёт. Отступов вокруг рамки больше нет, поэтому счёт идёт от края
            // самого окна.
            const int Outside = 2;
            // Точка заведомо внутри: середина скругления.
            const int Inside = (int)Corner + 6;

            byte[] atCorners =
            [
                Alpha(bitmap, Outside, Outside),
                Alpha(bitmap, width - 1 - Outside, Outside),
                Alpha(bitmap, Outside, height - 1 - Outside),
                Alpha(bitmap, width - 1 - Outside, height - 1 - Outside),
            ];
            byte[] atInside =
            [
                Alpha(bitmap, Inside, Inside),
                Alpha(bitmap, width - 1 - Inside, Inside),
                Alpha(bitmap, Inside, height - 1 - Inside),
                Alpha(bitmap, width - 1 - Inside, height - 1 - Inside),
            ];

            return (atCorners, atInside);
        });

        string[] names = ["левый верхний", "правый верхний", "левый нижний", "правый нижний"];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(inside[i] > 250,
                $"{names[i]} угол внутри скругления обязан быть закрашен, а там прозрачность {inside[i]}");

            // РОВНО НОЛЬ, а не «полупрозрачно». Пока вокруг рамки было поле под тень,
            // здесь хватало «< 200»: снаружи дуги лежала тень. Но окно с прозрачностью —
            // слоистое, и Windows отдаёт ему мышь по каждому пикселю: ноль в альфе значит
            // «клик проходит насквозь», всё остальное — «клик ловит окно». Тень ловила
            // клики там, где окна не видно, и край оказывался на десяток пикселей
            // снаружи видимой рамки. Ноль здесь — это и есть «граница окна там, где её видно».
            Assert.True(corners[i] == 0,
                $"{names[i]} угол: снаружи дуги пиксель не прозрачен насквозь ({corners[i]}) — " +
                "там ловится мышь, хотя окна не видно");
        }
    }

    /// <summary>
    /// ВИДИМАЯ РАМКА — ЭТО КРАЙ ОКНА. Крайний столбец и крайняя строка окна обязаны быть
    /// самой рамкой, полностью непрозрачной.
    /// <para>
    /// Раньше вокруг рамки было прозрачное поле в десять пикселей под тень. Выглядело оно
    /// как пустое место, а вело себя как окно: курсор менялся на стрелки изменения размера
    /// ещё до того, как доходил до видимой границы, и тянуть окно приходилось «за воздух».
    /// </para>
    /// </summary>
    [Fact]
    public void VisibleBorder_IsTheEdgeOfTheWindow()
    {
        byte[] edges = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            int width = (int)Math.Round(host.Window.ActualWidth);
            int height = (int)Math.Round(host.Window.ActualHeight);
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(host.Window);

            // Середины сторон: там нет скругления, и пиксель обязан быть рамкой.
            return new[]
            {
                Alpha(bitmap, 0, height / 2),
                Alpha(bitmap, width - 1, height / 2),
                Alpha(bitmap, width / 2, 0),
                Alpha(bitmap, width / 2, height - 1),
            };
        });

        string[] names = ["левый", "правый", "верхний", "нижний"];
        for (int i = 0; i < 4; i++)
        {
            Assert.True(edges[i] == 255,
                $"{names[i]} край окна прозрачен ({edges[i]}): вокруг видимой рамки осталось " +
                "невидимое поле, и тянуть окно придётся за пустое место");
        }
    }

    /// <summary>
    /// Полоса изменения размера НАЧИНАЕТСЯ на видимой рамке и уходит только внутрь.
    /// <para>
    /// Проверяется настоящим сообщением <c>WM_NCHITTEST</c>, отправленным окну: ответ
    /// на него — ровно то, что Windows использует, чтобы решить, какой курсор показать
    /// и что делать при нажатии.
    /// </para>
    /// </summary>
    [Fact]
    public void ResizeGrip_StartsAtTheVisibleBorder()
    {
        (int edgeLeft, int edgeRight, int edgeBottom, int justInside) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            double width = host.Window.ActualWidth;
            double height = host.Window.ActualHeight;
            double middle = height / 2;

            return (
                HitTest(host.Window, 0, middle),
                HitTest(host.Window, width - 1, middle),
                HitTest(host.Window, width / 2, height - 1),
                // Два пикселя за полосой — уже содержимое. Раньше полоса начиналась на
                // десять пикселей СНАРУЖИ рамки и шла на шестнадцать, и эта точка
                // тоже тянула окно.
                HitTest(host.Window, ResizeBorder + 2, middle));
        });

        Assert.Equal(HTLEFT, edgeLeft);
        Assert.Equal(HTRIGHT, edgeRight);
        Assert.Equal(HTBOTTOM, edgeBottom);
        Assert.True(justInside == HTCLIENT,
            $"в {ResizeBorder + 2} пикселях от края — содержимое, а окно отвечает {justInside}: " +
            "полоса изменения размера шире, чем обещано");
    }

    /// <summary>
    /// Угол тянется по диагонали, хотя сам угол СКРУГЛЁН.
    /// <para>
    /// Ловушка, в которую легко попасть, убрав поле под тень: снаружи дуги пиксели
    /// прозрачны насквозь, и мышь через них проходит мимо окна. Если угловую зону
    /// оставить квадратом со стороной в ширину полосы, от неё почти ничего не
    /// останется — её съест скругление. Поэтому угловая зона тянется вдоль сторон
    /// дальше радиуса скругления, как у самой Windows.
    /// </para>
    /// </summary>
    [Fact]
    public void CornerGrip_ReachesPastTheRounding()
    {
        (int alongLeft, int alongTop) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            // Точки на краю, но уже ЗА дугой скругления: там окно видно, и тянуть
            // за них по диагонали естественно.
            double beyondArc = Corner + 3;
            return (HitTest(host.Window, 2, beyondArc),
                    HitTest(host.Window, beyondArc, 2));
        });

        Assert.True(alongLeft == HTTOPLEFT,
            $"у левого края рядом с углом окно отвечает {alongLeft}, а не диагональ — угол не ухватить");
        Assert.True(alongTop == HTTOPLEFT,
            $"у верхнего края рядом с углом окно отвечает {alongTop}, а не диагональ — угол не ухватить");
    }

    /// <summary>
    /// Крестик остаётся крестиком, но правый верхний угол и край над ним по-прежнему тянутся.
    /// <para>
    /// Пока вокруг рамки было поле под тень, края над кнопками шапки тянулись через него.
    /// Теперь кнопки стоят вплотную к краю, и простое правило «кнопки важнее полосы»
    /// отняло бы у окна весь правый верхний угол. Поэтому над кнопками остаётся узкая
    /// полоса у самого края, а тело кнопки нажимается как кнопка.
    /// </para>
    /// </summary>
    [Fact]
    public void CloseButton_StaysClickable_ButTheEdgeAboveItStillResizes()
    {
        (int edge, int body, int corner) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            var caption = (FrameworkElement)host.Part("PART_Caption")!;
            Button close = Descendants<Button>(caption)
                .Single(b => b.Command == SystemCommands.CloseWindowCommand);
            Point centre = close.TranslatePoint(
                new Point(close.ActualWidth / 2, close.ActualHeight / 2), host.Window);
            double width = host.Window.ActualWidth;

            return (HitTest(host.Window, width - 2, centre.Y),
                    HitTest(host.Window, centre.X, centre.Y),
                    HitTest(host.Window, width - 2, 2));
        });

        Assert.True(edge == HTRIGHT,
            $"правый край над крестиком отвечает {edge}: окно не тянется за правый край у шапки");
        Assert.True(body == HTCLIENT,
            $"середина крестика отвечает {body}: нажатие уйдёт не в кнопку");
        Assert.True(corner == HTTOPRIGHT,
            $"правый верхний угол отвечает {corner}: по диагонали окно не ухватить");
    }

    /// <summary>
    /// Отправляет окну <c>WM_NCHITTEST</c> для точки в его координатах и возвращает ответ.
    /// <para>
    /// Координаты в сообщении — пиксели ЭКРАНА, поэтому точка переводится через
    /// <c>PointToScreen</c>: при масштабе, отличном от 100 %, аппаратно-независимые
    /// единицы с пикселями не совпадают.
    /// </para>
    /// </summary>
    private static int HitTest(Window window, double x, double y)
    {
        Point screen = window.PointToScreen(new Point(x, y));
        int sx = (int)Math.Round(screen.X);
        int sy = (int)Math.Round(screen.Y);
        long packed = ((long)(sy & 0xFFFF) << 16) | (long)(sx & 0xFFFF);
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        return (int)SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, new IntPtr(packed));
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Развёрнутое окно теряет углы. У него нет ни рамки, ни тени, и скругление
    /// оставило бы четыре прозрачные зарубки по краям экрана.
    /// </summary>
    [Fact]
    public void MaximizedWindow_LosesTheCorners()
    {
        (bool triggerFound, bool clipDropped) = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();

            // Само разворачивание в тесте не делаем: окно выскочило бы на экран.
            // Проверяется договор между разметкой и кодом — триггер обнуляет радиус…
            bool trigger = host.Window.Template.Triggers
                .OfType<Trigger>()
                .Any(t => t.Property == Window.WindowStateProperty
                          && Equals(t.Value, WindowState.Maximized)
                          && t.Setters.OfType<Setter>().Any(s =>
                              s.TargetName is null
                              && s.Property == ChromeWindow.CornerRadiusProperty
                              && Equals(s.Value, new CornerRadius(0))));

            // …а нулевой радиус снимает обрезку.
            host.Window.CornerRadius = new CornerRadius(0);
            host.Pump();
            bool dropped = ((FrameworkElement)host.Part("PART_Clip")!).Clip is null;

            return (trigger, dropped);
        });

        Assert.True(triggerFound, "в шаблоне нет обнуления радиуса для развёрнутого окна");
        Assert.True(clipDropped, "при нулевом радиусе обрезка обязана сниматься");
    }

    /// <summary>
    /// Кнопки шапки подписаны на системные команды. Кнопка без команды выглядит
    /// рабочей и не делает ничего — а закрыть окно больше нечем.
    /// </summary>
    [Fact]
    public void CaptionButtons_CarrySystemCommands()
    {
        object[] commands = WpfHost.Invoke(() =>
        {
            using var host = OffscreenWindow.Show();
            var caption = (FrameworkElement)host.Part("PART_Caption")!;
            return Descendants<Button>(caption).Select(b => (object?)b.Command).ToArray();
        })!;

        Assert.Contains(SystemCommands.MinimizeWindowCommand, commands);
        Assert.Contains(SystemCommands.MaximizeWindowCommand, commands);
        Assert.Contains(SystemCommands.RestoreWindowCommand, commands);
        Assert.Contains(SystemCommands.CloseWindowCommand, commands);
    }

    /// <summary>
    /// Значок приложения лежит в сборке и содержит мелкие размеры. Панель задач и
    /// список окон берут 16 и 24 пикселя: если их нет, Windows ужимает крупный кадр,
    /// и значок превращается в кашу.
    /// </summary>
    [Fact]
    public void ApplicationIcon_IsEmbeddedWithSmallSizes()
    {
        int[] sizes = WpfHost.Invoke(() =>
        {
            var decoder = BitmapDecoder.Create(
                IconUri(), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Select(f => f.PixelWidth).Distinct().OrderBy(w => w).ToArray();
        });

        Assert.Contains(16, sizes);
        Assert.Contains(24, sizes);
        Assert.Contains(32, sizes);
        Assert.Contains(256, sizes);
    }

    private static Uri IconUri() =>
        new("pack://application:,,,/WotModCockpit.App;component/Resources/cockpit.ico");

    private static BitmapFrame AppIcon() => BitmapFrame.Create(IconUri());

    private static byte Alpha(RenderTargetBitmap bitmap, int x, int y)
    {
        byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return pixel[3];
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (T deep in Descendants<T>(child))
                yield return deep;
        }
    }

    /// <summary>Окно с собственной рамкой, поднятое за пределами экрана.</summary>
    private sealed class OffscreenWindow : IDisposable
    {
        private OffscreenWindow(ChromeWindow window) => Window = window;

        public ChromeWindow Window { get; }

        public static OffscreenWindow Show(double width = 600, double height = 400)
        {
            var window = new Probe
            {
                Width = width,
                Height = height,
                Left = -32000,
                Top = -32000,
                ShowActivated = false,
                ShowInTaskbar = false,
            };

            window.Show();
            window.UpdateLayout();
            return new OffscreenWindow(window);
        }

        /// <summary>Часть шаблона по имени.</summary>
        /// <param name="name">Имя части, например <c>PART_Caption</c>.</param>
        /// <returns>Элемент или <see langword="null"/>, если шаблона нет.</returns>
        public object? Part(string name) => Window.Template?.FindName(name, Window);

        public void Pump()
        {
            Window.UpdateLayout();
            Window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            Window.UpdateLayout();
        }

        public void Dispose() => Window.Close();

        /// <summary>Наследник — как боевые окна: шаблон обязан доставаться и им.</summary>
        private sealed class Probe : ChromeWindow
        {
        }
    }
}
