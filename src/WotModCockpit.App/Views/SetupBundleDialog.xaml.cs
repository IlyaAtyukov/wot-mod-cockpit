using System.ComponentModel;
using System.Windows;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Диалог «Собрать сетап».
/// <para>
/// В code-behind только закрытие окна. Кнопка «Закрыть» с <c>IsCancel</c> закрывает окно сама
/// только без команды; здесь команда есть (она гаснет посреди сборки), поэтому окно закрывает
/// событие <see cref="SetupBundleViewModel.CloseRequested"/>. Крестик в шапке и Alt+F4 посреди
/// сборки не срабатывают: build.py и упаковку на полпути не бросают.
/// </para>
/// </summary>
public partial class SetupBundleDialog : ChromeWindow
{
    private SetupBundleViewModel? _viewModel;

    /// <summary>Создаёт диалог. Модель приходит через <see cref="FrameworkElement.DataContext"/>.</summary>
    public SetupBundleDialog()
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

        _viewModel = DataContext as SetupBundleViewModel;

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
