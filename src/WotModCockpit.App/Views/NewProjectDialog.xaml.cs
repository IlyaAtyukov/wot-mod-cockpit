using System.Windows;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Диалог мастера нового проекта.
/// <para>
/// В code-behind только закрытие окна: <see cref="Window.DialogResult"/> — свойство окна,
/// а не модели, и выставить его из ViewModel можно лишь через событие. Всё остальное
/// (валидация, предпросмотр, создание) живёт в <see cref="NewProjectViewModel"/>.
/// </para>
/// </summary>
public partial class NewProjectDialog : ChromeWindow
{
    private NewProjectViewModel? _viewModel;

    /// <summary>Создаёт диалог. Модель приходит через <see cref="FrameworkElement.DataContext"/>.</summary>
    public NewProjectDialog()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Closed += OnClosed;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.CloseRequested -= OnCloseRequested;
        }

        _viewModel = DataContext as NewProjectViewModel;

        if (_viewModel is not null)
        {
            _viewModel.CloseRequested += OnCloseRequested;
        }
    }

    private void OnCloseRequested(object? sender, bool created)
    {
        try
        {
            DialogResult = created;
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
