using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Панель живого <c>python.log</c>: фильтры, поиск и виртуализированная лента записей.
/// <para>
/// В code-behind живёт ровно то, что моделью представления не выражается: прокрутка
/// к последней записи (это работа со ScrollViewer из шаблона) и разворачивание записи
/// двойным щелчком. Никакой логики кокпита здесь нет.
/// </para>
/// </summary>
public partial class LogView : UserControl
{
    private MainViewModel? _viewModel;
    private ScrollViewer? _scrollViewer;

    /// <summary>Создаёт панель.</summary>
    public LogView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
        Unloaded += OnUnloaded;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _viewModel = e.NewValue as MainViewModel;

        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }
    }

    /// <summary>
    /// Прокрутка к концу. Идёт через ScrollViewer шаблона, а не через
    /// <see cref="ListBox.ScrollIntoView"/>: при 20 000 записей это дешевле и не трогает выделение.
    /// </summary>
    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        _scrollViewer ??= FindScrollViewer(EntriesList);

        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollToEnd();
            return;
        }

        // Шаблон списка ещё не развёрнут — откладываем до конца отрисовки.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                _scrollViewer ??= FindScrollViewer(EntriesList);
                _scrollViewer?.ScrollToEnd();
            }));
    }

    /// <summary>Двойной щелчок по строке разворачивает и сворачивает её traceback.</summary>
    private void OnEntriesDoubleClick(object sender, MouseButtonEventArgs e) => ToggleSelected();

    /// <summary>
    /// Enter и Space разворачивают выбранную запись — по ленте лога должно быть можно
    /// ходить с клавиатуры, не снимая рук с неё ради мыши.
    /// </summary>
    private void OnEntriesKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Space))
        {
            return;
        }

        if (ToggleSelected())
        {
            e.Handled = true;
        }
    }

    private bool ToggleSelected()
    {
        if (EntriesList.SelectedItem is not LogEntryViewModel entry || !entry.CanExpand)
        {
            return false;
        }

        entry.IsExpanded = !entry.IsExpanded;
        return true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ScrollViewer? found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
