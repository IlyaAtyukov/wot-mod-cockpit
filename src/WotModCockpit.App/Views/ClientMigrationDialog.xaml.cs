using System.ComponentModel;
using System.Windows;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Окно «Переход на новую версию клиента».
/// <para>
/// В code-behind только закрытие окна — так же, как у <see cref="SetupBundleDialog"/>: окно
/// закрывает событие <see cref="ClientMigrationViewModel.CloseRequested"/>, а крестик и Alt+F4
/// посреди работы не срабатывают — декомпиляцию и перенос на полпути не бросают (для этого есть
/// «Остановить»).
/// </para>
/// </summary>
public partial class ClientMigrationDialog : ChromeWindow
{
    private ClientMigrationViewModel? _viewModel;

    /// <summary>Создаёт окно. Модель приходит через <see cref="FrameworkElement.DataContext"/>.</summary>
    public ClientMigrationDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as ClientMigrationViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, EventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_viewModel?.IsWorking == true)
        {
            e.Cancel = true;
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
