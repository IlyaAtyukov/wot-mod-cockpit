using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Экран «Моды»: слева список установленных контейнеров, справа карточка выбранного
/// и действия над ним.
/// <para>
/// В code-behind — только группировка списка по <see cref="InstalledModViewModel.GroupKey"/>
/// (описания групп живут в представлении коллекции, а не в модели — ровно та же причина, что
/// у <see cref="RecipesView"/> и <see cref="ProblemsView"/>) и выделение строки правой кнопкой.
/// </para>
/// </summary>
public partial class ModsView : UserControl
{
    private bool _grouped;

    /// <summary>Создаёт экран.</summary>
    public ModsView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => EnsureGrouping();

    /// <summary>
    /// Выделяет строку, по которой щёлкнули ПРАВОЙ кнопкой, — до того, как откроется меню.
    /// <para>
    /// В WPF правый щелчок выделение не меняет, а меню списка работает через
    /// <c>Selected</c> модели представления. Без этой строчки «Открыть в VS Code» открывало бы
    /// проект мода, выбранного ДО щелчка: меню показывалось бы под курсором, а действие
    /// относилось бы к другой строке. Заодно правая кнопка обновляет и карточку справа —
    /// то есть показывает ровно тот мод, к которому относится меню.
    /// </para>
    /// <para>
    /// Событие НЕ помечается обработанным: дальше оно должно дойти до списка и открыть меню.
    /// </para>
    /// </summary>
    private void OnRowRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem row && !row.IsSelected)
        {
            row.IsSelected = true;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _grouped = false;
        EnsureGrouping();
    }

    private void EnsureGrouping()
    {
        if (_grouped)
        {
            return;
        }

        ICollectionView? view = ModList.ItemsSource is null
            ? null
            : CollectionViewSource.GetDefaultView(ModList.ItemsSource);

        if (view is null)
        {
            return;
        }

        if (view.GroupDescriptions is not null && view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InstalledModViewModel.GroupKey)));
        }

        _grouped = true;
    }
}
