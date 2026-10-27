using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Каталог рецептов: список слева, код с подстановками в центре, поля параметров справа.
/// <para>
/// В code-behind — только группировка списка по рубрике: описания групп живут в представлении
/// коллекции, а не в модели (см. <see cref="ProblemsView"/>, там та же причина).
/// </para>
/// </summary>
public partial class RecipesView : UserControl
{
    private bool _grouped;

    /// <summary>Создаёт экран.</summary>
    public RecipesView()
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

    private void EnsureGrouping()
    {
        if (_grouped)
        {
            return;
        }

        ICollectionView? view = RecipeList.ItemsSource is null
            ? null
            : CollectionViewSource.GetDefaultView(RecipeList.ItemsSource);

        if (view is null)
        {
            return;
        }

        if (view.GroupDescriptions is not null && view.GroupDescriptions.Count == 0)
        {
            view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(RecipeItemViewModel.Category)));
        }

        _grouped = true;
    }
}
