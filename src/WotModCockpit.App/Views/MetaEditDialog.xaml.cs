using System.Windows;
using System.Windows.Controls;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Диалог «Имя и описание мода».
/// <para>
/// В code-behind две вещи, которые из модели не выразить: закрытие окна и фокус.
/// </para>
/// <list type="bullet">
///   <item><b>Закрытие.</b> <see cref="Window.DialogResult"/> — свойство окна, а не модели. И
///   ловушка, в которую легко попасть: кнопка с <c>IsCancel</c> закрывает окно САМА только
///   тогда, когда у неё нет команды. Здесь команда есть, поэтому окно закрывает событие
///   <see cref="MetaEditViewModel.CloseRequested"/>, — иначе «Отмена» и Escape выполнили бы
///   команду, а окно осталось бы открытым.</item>
///   <item><b>Фокус.</b> Окно открывается пунктом «Переименовать…» или «Изменить описание…», и
///   курсор обязан стоять в том поле, ради которого его открыли. Сам WPF фокус никуда не ставит.</item>
/// </list>
/// </summary>
public partial class MetaEditDialog : ChromeWindow
{
    private MetaEditViewModel? _viewModel;

    /// <summary>Создаёт диалог. Модель приходит через <see cref="FrameworkElement.DataContext"/>.</summary>
    public MetaEditDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as MetaEditViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TextBox target = _viewModel?.FocusDescription == true ? DescriptionBox : NameBox;
        target.Focus();

        // Имя обычно меняют целиком — выделено всё. Описание обычно дописывают — курсор в конце.
        if (ReferenceEquals(target, NameBox))
        {
            target.SelectAll();
        }
        else
        {
            target.CaretIndex = target.Text.Length;
        }
    }

    private void OnCloseRequested(object? sender, bool save)
    {
        try
        {
            DialogResult = save;
        }
        catch (InvalidOperationException)
        {
            // Окно показано не как диалог (ShowDialog не вызывался) — просто закрываем.
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
            _viewModel = null;
        }
    }
}
