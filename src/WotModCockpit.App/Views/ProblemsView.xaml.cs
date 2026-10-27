using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Панель Problems: находки preflight по коду мода.
/// <para>
/// В code-behind оставлены ровно две вещи, которые в MVVM не выражаются:
/// </para>
/// <list type="number">
///   <item><b>Группировка списка.</b> Описания групп живут не в модели, а в представлении
///         коллекции. Ставятся они здесь, а не через <c>CollectionViewSource</c> в ресурсах,
///         потому что ресурс не входит в дерево и его привязка к <c>DataContext</c> держится
///         на контексте наследования — лишняя тонкость там, где хватает трёх строк кода.</item>
///   <item><b>Двойной щелчок по строке.</b> Это жест, а не команда: у <see cref="ListBox"/>
///         нет привязываемого события «активирован элемент».</item>
/// </list>
/// </summary>
public partial class ProblemsView : UserControl
{
    private bool _grouped;

    /// <summary>Создаёт панель.</summary>
    public ProblemsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureGrouping();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _grouped = false;
        EnsureGrouping();
    }

    /// <summary>
    /// Включает группировку по <see cref="ProblemItemViewModel.GroupKey"/>. Один раз:
    /// представление коллекции живёт столько же, сколько сама коллекция, а она у модели одна.
    /// </summary>
    private void EnsureGrouping()
    {
        if (_grouped)
        {
            return;
        }

        ICollectionView? view = ProblemList.ItemsSource is null
            ? null
            : CollectionViewSource.GetDefaultView(ProblemList.ItemsSource);

        if (view is null)
        {
            return;
        }

        if (view.GroupDescriptions is not null && view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ProblemItemViewModel.GroupKey)));
        }

        _grouped = true;
    }

    /// <summary>
    /// Двойной щелчок открывает файл мода в VS Code на строке находки — как в панели
    /// Problems у Visual Studio.
    /// </summary>
    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not ProblemsViewModel vm)
        {
            return;
        }

        // Щелчок мимо строки (по пустому месту или по заголовку группы) не должен
        // открывать редактор на предыдущем выборе.
        if (e.OriginalSource is not DependencyObject source || ItemAt(source) is null)
        {
            return;
        }

        if (vm.OpenInEditorCommand.CanExecute(vm.Selected))
        {
            vm.OpenInEditorCommand.Execute(vm.Selected);
        }
    }

    private static ListBoxItem? ItemAt(DependencyObject source)
    {
        DependencyObject? current = source;

        while (current is not null and not ListBoxItem)
        {
            // OriginalSource у щелчка по тексту — это Run, а он не Visual: визуальный
            // обход на нём бросает InvalidOperationException. Поэтому для не-Visual
            // поднимаемся по логическому дереву.
            current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return current as ListBoxItem;
    }
}
