using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace WotModCockpit.App.Controls;

/// <summary>
/// Окно с СОБСТВЕННОЙ рамкой: скруглённые углы, своя шапка, свои кнопки.
/// <para>
/// Системная рамка выключена (<c>WindowStyle.None</c>), а прозрачность включена
/// (<c>AllowsTransparency</c>) — иначе скругления на Windows 10 не получить вовсе:
/// DWM умеет скруглять окна только с Windows 11 (атрибут
/// <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>), а здесь целевая система — Windows 10.
/// Значит углы рисуются самим приложением, а всё, что при этом теряется, надо
/// вернуть руками. Именно этим класс и занят.
/// </para>
/// <para>
/// ЧТО ВОЗВРАЩАЕТСЯ РУКАМИ И ПОЧЕМУ ИМЕННО ТАК:
/// <list type="bullet">
///   <item><b>Изменение размера за края.</b> Обрабатывается <c>WM_NCHITTEST</c>:
///   окно отвечает системе, что курсор стоит на краю, и дальше тянет САМА система.
///   Это даёт правильные курсоры, двойной клик по краю, прилипание к сторонам и
///   работу с клавиатуры — ничего из этого не получить, развешивая
///   <c>Thumb</c> по периметру.</item>
///   <item><b>Перетаскивание за шапку.</b> Тем же сообщением: для шапки
///   возвращается <c>HTCAPTION</c>, и система сама даёт и перетаскивание, и
///   Aero Snap, и двойной клик для разворота. Вызов <c>DragMove()</c> дал бы
///   только перетаскивание.</item>
///   <item><b>Разворот на весь экран.</b> Обрабатывается <c>WM_GETMINMAXINFO</c>.
///   Без этого окно без системной рамки разворачивается поверх панели задач:
///   система считает границей весь экран, а не рабочую область.</item>
///   <item><b>Обрезка содержимого по углам.</b> Скруглена рамка, а содержимое
///   внутри неё по-прежнему прямоугольное и вылезает за углы. Обрезку ставит
///   <see cref="UpdateClip"/>.</item>
/// </list>
/// </para>
/// <para>
/// Шаблон окна лежит в <c>Styles/Window.xaml</c>; здесь только то, что в XAML не
/// выражается.
/// </para>
/// </summary>
public class ChromeWindow : Window
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_NCHITTEST = 0x0084;

    private const int HTCLIENT = 1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// Ширина полосы, за которую тянут окно, в аппаратно-независимых пикселях.
    /// <para>
    /// Полоса начинается НА видимой рамке и уходит только внутрь окна. Наружу ей
    /// идти некуда: снаружи рамки у окна нет ни одного пикселя, и Windows отдаёт
    /// мышь сквозь прозрачное. Раньше вокруг рамки было поле под тень в десять
    /// пикселей, полоса накрывала и его — и окно начинало тянуться ещё до видимой
    /// границы, за пустое на вид место.
    /// </para>
    /// </summary>
    private const double ResizeBorder = 6.0;

    /// <summary>
    /// Полоса изменения размера ПОВЕРХ кнопок шапки — тоньше обычной.
    /// <para>
    /// Кнопки шапки стоят вплотную к верхнему и правому краю. Отдай им край целиком —
    /// и правый верхний угол вместе с верхним краем над кнопками перестанет тянуться
    /// вовсе. Отдай полосе всю её ширину — и верхние шесть пикселей крестика будут
    /// менять размер вместо закрытия. Поэтому над кнопками полоса узкая: линия рамки и
    /// два пикселя за ней. Так устроена рамка и у самой Windows.
    /// </para>
    /// </summary>
    private const double ResizeBorderOverButtons = 3.0;

    /// <summary>Содержимое окна, которое обрезается по скруглённым углам.</summary>
    private FrameworkElement? _clipped;

    /// <summary>Скругление углов окна. Одно значение и на рамку, и на обрезку содержимого.</summary>
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius), typeof(CornerRadius), typeof(ChromeWindow),
            new FrameworkPropertyMetadata(default(CornerRadius), OnCornerRadiusChanged));

    /// <summary>Скругление углов окна; задаётся стилем, обнуляется в развёрнутом состоянии.</summary>
    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static void OnCornerRadiusChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((ChromeWindow)d).UpdateClip();

    /// <summary>Создаёт окно с собственной рамкой и подписывает системные команды.</summary>
    protected ChromeWindow()
    {
        // ШАБЛОН БЕРЁТСЯ ПО ТИПУ ЭТОГО класса, а не наследника, и ищется там, где
        // лежат кисти темы, — в ресурсах приложения.
        //
        // Двух очевидных способов сделать это НЕ существует, и оба стоили пустого
        // прозрачного окна:
        //   * DefaultStyleKeyProperty.OverrideMetadata ищет стиль ТОЛЬКО в
        //     Themes/Generic.xaml сборки. Оттуда не видно Styles/DarkTheme.xaml,
        //     а без кистей шаблон не собрать;
        //   * безымянный Style с TargetType="ChromeWindow" в ресурсах приложения
        //     сам по себе не подходит: неявный стиль ищется по ТОЧНОМУ типу
        //     элемента, а окна в приложении — наследники (MainWindow и прочие).
        // Ссылка на ресурс по ключу typeof(ChromeWindow) решает оба: ключ — базовый
        // тип, а поиск идёт обычной цепочкой ресурсов, до App.Resources включительно.
        SetResourceReference(StyleProperty, typeof(ChromeWindow));

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        // Фон рисует шаблон (скруглённая рамка). У самого окна он обязан быть
        // прозрачным, иначе за скруглением останутся прямые углы.
        Background = Brushes.Transparent;

        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand,
            (_, _) => SystemCommands.MinimizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand,
            (_, _) => SystemCommands.MaximizeWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand,
            (_, _) => SystemCommands.RestoreWindow(this)));
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand,
            (_, _) => SystemCommands.CloseWindow(this)));
    }

    /// <summary>Находит обрезаемое содержимое и берёт его размер под наблюдение.</summary>
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (_clipped is not null)
        {
            _clipped.SizeChanged -= OnClippedSizeChanged;
        }

        _clipped = GetTemplateChild("PART_Clip") as FrameworkElement;
        if (_clipped is not null)
        {
            _clipped.SizeChanged += OnClippedSizeChanged;
        }

        UpdateClip();
    }

    private void OnClippedSizeChanged(object sender, SizeChangedEventArgs e) => UpdateClip();

    /// <summary>
    /// Обрезает содержимое окна по скруглённым углам.
    /// <para>
    /// Рамку со скруглением рисует <c>Border</c> в шаблоне, но своё содержимое
    /// он НЕ обрезает: шапка и панели доходят до углов прямыми краями и
    /// закрашивают скругление. Обрезка живёт здесь, а не в разметке, потому что
    /// геометрия зависит от текущего размера содержимого — выразить это
    /// привязками внутри кисти не выходит: они считаются в отрыве от дерева и
    /// молча дают не тот размер.
    /// </para>
    /// <para>
    /// Радиус берётся НЕ внешний, а внутренний: содержимое лежит внутри рамки,
    /// сдвинуто на её толщину, и обрезать его по внешнему радиусу значило бы
    /// разрешить ему закрасить саму линию рамки в углах.
    /// </para>
    /// </summary>
    private void UpdateClip()
    {
        if (_clipped is null)
        {
            return;
        }

        double inset = VisualTreeHelper.GetParent(_clipped) is Border shell
            ? shell.BorderThickness.Left
            : 0.0;
        double radius = Math.Max(0.0, CornerRadius.TopLeft - inset);
        double width = _clipped.ActualWidth;
        double height = _clipped.ActualHeight;

        _clipped.Clip = radius <= 0.0 || width <= 0.0 || height <= 0.0
            ? null
            : new RectangleGeometry(new Rect(0.0, 0.0, width, height), radius, radius);
    }

    /// <summary>Подключает обработчик системных сообщений сразу после создания окна.</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_GETMINMAXINFO:
                LimitMaximizeToWorkArea(hwnd, lParam);
                return IntPtr.Zero;

            case WM_NCHITTEST:
                int area = HitTest(lParam);
                if (area == HTCLIENT)
                {
                    // Обычное содержимое: пусть WPF разбирается сам.
                    return IntPtr.Zero;
                }

                handled = true;
                return new IntPtr(area);
        }

        return IntPtr.Zero;
    }

    /// <summary>Определяет, что под курсором: край окна, шапка или обычное содержимое.</summary>
    /// <param name="lParam">Координаты курсора на экране, упакованные системой.</param>
    /// <returns>Код области <c>HT*</c>.</returns>
    private int HitTest(IntPtr lParam)
    {
        // Координаты приходят В ПИКСЕЛЯХ ЭКРАНА, а WPF живёт в аппаратно-независимых.
        // Без пересчёта полоса у края «уезжала» бы при масштабе, отличном от 100 %.
        int screenX = unchecked((short)(long)lParam);
        int screenY = unchecked((short)((long)lParam >> 16));
        Point point;
        try
        {
            point = PointFromScreen(new Point(screenX, screenY));
        }
        catch (InvalidOperationException)
        {
            return HTCLIENT;
        }

        CaptionArea caption = CaptionAt(point);

        // Развёрнутое окно не тянут за края — там их попросту нет.
        if (WindowState != WindowState.Maximized && ResizeMode != ResizeMode.NoResize)
        {
            // Край окна и есть видимая рамка, поэтому полоса считается от него.
            // Над кнопками шапки она уже — см. ResizeBorderOverButtons.
            double band = caption == CaptionArea.Control ? ResizeBorderOverButtons : ResizeBorder;
            bool left = point.X <= band;
            bool right = point.X >= ActualWidth - band;
            bool top = point.Y <= band;
            bool bottom = point.Y >= ActualHeight - band;

            // УГЛОВАЯ ЗОНА ДЛИННЕЕ ПОЛОСЫ. Угол скруглён, и снаружи дуги пиксели
            // прозрачны насквозь — мышь через них проходит мимо окна. Квадрат
            // ResizeBorder x ResizeBorder в углу почти целиком лёг бы за дугу, и
            // по диагонали окно было бы не ухватить. Поэтому у каждой стороны
            // первые «радиус + полоса» пикселей от угла тянут по диагонали — так
            // устроена рамка и у самой Windows.
            double grip = Math.Max(0.0, CornerRadius.TopLeft) + band;
            bool nearLeft = point.X <= grip;
            bool nearRight = point.X >= ActualWidth - grip;
            bool nearTop = point.Y <= grip;
            bool nearBottom = point.Y >= ActualHeight - grip;

            if ((top && nearLeft) || (left && nearTop)) return HTTOPLEFT;
            if ((top && nearRight) || (right && nearTop)) return HTTOPRIGHT;
            if ((bottom && nearLeft) || (left && nearBottom)) return HTBOTTOMLEFT;
            if ((bottom && nearRight) || (right && nearBottom)) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }

        return caption == CaptionArea.Drag ? HTCAPTION : HTCLIENT;
    }

    /// <summary>Что находится под курсором в шапке окна.</summary>
    private enum CaptionArea
    {
        /// <summary>Курсор вне шапки.</summary>
        None,

        /// <summary>Пустое место шапки: за него окно перетаскивают.</summary>
        Drag,

        /// <summary>Кнопка или другой орган управления: нажатие принадлежит приложению.</summary>
        Control,
    }

    /// <summary>
    /// Разбирает, на что именно указывает курсор в шапке.
    /// <para>
    /// Одной проверки «попал в полосу шапки по высоте» мало: в шапке живут кнопки,
    /// и если отдать системе всю полосу, по ним нельзя будет кликнуть — нажатие
    /// уйдёт в перетаскивание окна. Поэтому под курсором ищется настоящий элемент,
    /// и всё интерактивное отдаётся приложению.
    /// </para>
    /// </summary>
    private CaptionArea CaptionAt(Point point)
    {
        if (GetTemplateChild("PART_Caption") is not FrameworkElement caption)
        {
            return CaptionArea.None;
        }

        Point local = caption.PointFromScreen(PointToScreen(point));
        if (local.X < 0 || local.Y < 0 || local.X > caption.ActualWidth || local.Y > caption.ActualHeight)
        {
            return CaptionArea.None;
        }

        DependencyObject? hit = caption.InputHitTest(local) as DependencyObject;
        while (hit is not null && hit != caption)
        {
            if (hit is ButtonBase or TextBoxBase or Selector or Thumb)
            {
                return CaptionArea.Control;
            }

            hit = VisualTreeHelper.GetParent(hit) ?? LogicalTreeHelper.GetParent(hit);
        }

        return CaptionArea.Drag;
    }

    /// <summary>Даёт системе правильные границы разворота — рабочую область, а не весь экран.</summary>
    private static void LimitMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            return;
        }

        MONITORINFO info = new() { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return;
        }

        MINMAXINFO minMax = Marshal.PtrToStructure<MINMAXINFO>(lParam);
        minMax.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        minMax.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        minMax.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        minMax.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
        Marshal.StructureToPtr(minMax, lParam, true);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
