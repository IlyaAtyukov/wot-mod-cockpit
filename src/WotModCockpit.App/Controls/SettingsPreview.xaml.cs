using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Controls;

/// <summary>
/// Выбор шаблона по типу контрола: ключ ресурса — <c>&lt;префикс&gt;&lt;тип&gt;</c>,
/// например <c>Preview.CheckBox</c> или <c>Editor.Slider</c>.
/// <para>
/// Иначе пришлось бы либо заводить одиннадцать классов моделей представления (по одному
/// на тип контрола) ради <c>DataType</c>, либо городить в разметке одиннадцать
/// <c>DataTrigger</c>. Здесь и превью, и редактор свойств пользуются одним и тем же
/// селектором, отличаясь только префиксом.
/// </para>
/// </summary>
public sealed class SettingsTemplateSelector : DataTemplateSelector
{
    /// <summary>Начало ключа ресурса: <c>Preview.</c> либо <c>Editor.</c>.</summary>
    public string Prefix { get; set; } = "Preview.";

    /// <inheritdoc />
    public override DataTemplate? SelectTemplate(object? item, DependencyObject container)
    {
        if (item is not SettingControlViewModel control || container is not FrameworkElement element)
        {
            return null;
        }

        return element.TryFindResource(Prefix + control.TemplateKey) as DataTemplate
               ?? element.TryFindResource(Prefix + "Default") as DataTemplate;
    }
}

/// <summary>
/// Что сейчас тащат мышью. Статикой, а не через <see cref="DataObject"/>: тащим объекты
/// модели представления, а <c>DataObject</c> обязан уметь сериализовать положенное в него
/// для передачи между процессами. Перетаскивание живёт внутри одного окна, и лишний
/// маршалинг здесь ни к чему.
/// </summary>
public static class SettingsDrag
{
    /// <summary>Формат-метка, по которой цель понимает, что тащат именно контрол формы.</summary>
    public const string Format = "WotModCockpit.SettingsDesigner";

    /// <summary>Тип из палитры либо <c>null</c>.</summary>
    public static SettingsControlType? NewType { get; private set; }

    /// <summary>Перетаскиваемый контрол формы либо <c>null</c>.</summary>
    public static SettingControlViewModel? Control { get; private set; }

    /// <summary>Идёт перетаскивание чего-то нашего.</summary>
    public static bool IsActive => NewType is not null || Control is not null;

    /// <summary>Начать перетаскивание типа из палитры.</summary>
    /// <param name="type">Тип контрола.</param>
    /// <returns>Данные для <see cref="DragDrop.DoDragDrop"/>.</returns>
    public static DataObject BeginNew(SettingsControlType type)
    {
        NewType = type;
        Control = null;
        return new DataObject(Format, type.ToString());
    }

    /// <summary>Начать перетаскивание существующего контрола.</summary>
    /// <param name="control">Контрол.</param>
    /// <returns>Данные для <see cref="DragDrop.DoDragDrop"/>.</returns>
    public static DataObject BeginMove(SettingControlViewModel control)
    {
        NewType = null;
        Control = control;
        return new DataObject(Format, control.TypeName);
    }

    /// <summary>Закончить перетаскивание.</summary>
    public static void End()
    {
        NewType = null;
        Control = null;
    }
}

/// <summary>
/// Схематичное превью окна настроек: две колонки, как в самом окне игры, с перетаскиванием.
/// <para>
/// Это НЕ снимок окна и не может им быть: <c>modsSettingsWindow.swf</c> импортирует всю
/// графику и шрифты из десяти библиотек клиента, поэтому настоящие высоты строк и переносы
/// подписей живут в клиенте. Зато размеры раскладки здесь настоящие — они вытащены
/// из констант в байткоде окна (ширина блока 900, колонка 430, отступ между контролами 10).
/// </para>
/// <para>
/// В code-behind ровно то, что в MVVM не выражается: перетаскивание мышью и синхронизация
/// выделения двух списков, у которых одна общая выбранная строка.
/// </para>
/// </summary>
public partial class SettingsPreview : UserControl
{
    private Point _dragStart;
    private InsertionAdorner? _adorner;
    private bool _syncing;

    /// <summary>Создаёт превью.</summary>
    public SettingsPreview()
    {
        InitializeComponent();
    }

    private SettingsDesignerViewModel? Model => DataContext as SettingsDesignerViewModel;

    // =================================================================== выделение

    /// <summary>
    /// Выбранная строка у обоих списков одна на двоих, а <c>SelectedItem</c> у каждого
    /// свой: привязать оба списка двусторонне нельзя — тот, в котором строки нет,
    /// немедленно записал бы в модель <c>null</c>. Поэтому привязка односторонняя,
    /// а обратно значение кладёт этот обработчик.
    /// </summary>
    private void OnColumnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ListBox list || Model is null)
        {
            return;
        }

        if (list.SelectedItem is not SettingControlViewModel control)
        {
            return;
        }

        _syncing = true;
        try
        {
            ListBox other = ReferenceEquals(list, Column1List) ? Column2List : Column1List;
            other.SelectedItem = null;
            Model.Selected = control;
        }
        finally
        {
            _syncing = false;
        }
    }

    // =================================================================== перетаскивание

    private void OnColumnMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void OnColumnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || sender is not ListBox list)
        {
            return;
        }

        Vector moved = _dragStart - e.GetPosition(this);
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        ListBoxItem? item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is not SettingControlViewModel control)
        {
            return;
        }

        if (Model is not null)
        {
            Model.Selected = control;
        }

        try
        {
            DragDrop.DoDragDrop(list, SettingsDrag.BeginMove(control), DragDropEffects.Move);
        }
        finally
        {
            SettingsDrag.End();
            RemoveAdorner();
        }
    }

    private void OnColumnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox list || !SettingsDrag.IsActive)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = SettingsDrag.NewType is not null ? DragDropEffects.Copy : DragDropEffects.Move;
        e.Handled = true;

        DropIndex(list, e, out ListBoxItem? over, out bool below);
        ShowAdorner(list, over, below);
    }

    private void OnColumnDragLeave(object sender, DragEventArgs e) => RemoveAdorner();

    private void OnColumnDrop(object sender, DragEventArgs e)
    {
        RemoveAdorner();

        if (sender is not ListBox list || Model is null || !SettingsDrag.IsActive)
        {
            return;
        }

        int column = ReferenceEquals(list, Column2List) ? 2 : 1;
        int index = DropIndex(list, e, out _, out _);

        if (SettingsDrag.NewType is SettingsControlType type)
        {
            Model.InsertNew(type, column, index);
        }
        else if (SettingsDrag.Control is SettingControlViewModel control)
        {
            Model.MoveControl(control, column, index);
        }

        SettingsDrag.End();
        e.Handled = true;
    }

    /// <summary>
    /// Куда именно вставлять: строка, над верхней половиной которой курсор, — это «перед
    /// ней», над нижней — «после неё». Ниже последней строки — в конец колонки.
    /// </summary>
    private static int DropIndex(ListBox list, DragEventArgs e, out ListBoxItem? over, out bool below)
    {
        over = null;
        below = false;

        for (int i = 0; i < list.Items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem container
                || !container.IsVisible)
            {
                continue;
            }

            Point point = e.GetPosition(container);
            if (point.Y < 0 || point.Y > container.ActualHeight)
            {
                continue;
            }

            over = container;
            below = point.Y > container.ActualHeight / 2;
            return below ? i + 1 : i;
        }

        return list.Items.Count;
    }

    private void ShowAdorner(ListBox list, ListBoxItem? over, bool below)
    {
        UIElement target;
        bool drawBelow;

        if (over is not null)
        {
            target = over;
            drawBelow = below;
        }
        else if (list.Items.Count > 0
                 && list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is ListBoxItem last)
        {
            // Курсор ниже последней строки — линия по её нижнему краю.
            target = last;
            drawBelow = true;
        }
        else
        {
            target = list;
            drawBelow = false;
        }

        AdornerLayer? layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null)
        {
            return;
        }

        if (_adorner is not null
            && ReferenceEquals(_adorner.AdornedElement, target)
            && _adorner.Below == drawBelow)
        {
            return;
        }

        RemoveAdorner();

        _adorner = new InsertionAdorner(target) { Below = drawBelow };
        layer.Add(_adorner);
    }

    private void RemoveAdorner()
    {
        if (_adorner is null)
        {
            return;
        }

        AdornerLayer.GetAdornerLayer(_adorner.AdornedElement)?.Remove(_adorner);
        _adorner = null;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match)
            {
                return match;
            }

            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>
    /// Полоска «сюда встанет контрол». Adorner, а не элемент разметки: линия рисуется
    /// ПОВЕРХ строки, и место в раскладке ей занимать нельзя — иначе список дёргался бы
    /// при каждом движении мыши.
    /// </summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Pen Line = CreatePen();

        public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        /// <summary>Линия под строкой, а не над ней.</summary>
        public bool Below { get; init; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            ArgumentNullException.ThrowIfNull(drawingContext);

            Size size = AdornedElement.RenderSize;
            double y = Below ? Math.Max(1, size.Height - 1) : 1;

            drawingContext.DrawLine(Line, new Point(0, y), new Point(size.Width, y));
        }

        private static Pen CreatePen()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x3A, 0x87, 0xC7));
            brush.Freeze();

            var pen = new Pen(brush, 2);
            pen.Freeze();
            return pen;
        }
    }
}
