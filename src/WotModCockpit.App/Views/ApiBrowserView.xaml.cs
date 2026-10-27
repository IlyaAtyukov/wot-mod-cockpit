using System.Windows;
using System.Windows.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Экран «Браузер API»: слева поиск / дерево модулей / доменные каталоги, в центре исходник
/// клиента, справа «где это вызывается» и генератор сниппетов.
/// <para>
/// Код-behind здесь ровно один метод, и он существует по технической причине:
/// <see cref="TreeView.SelectedItem"/> — свойство только для чтения, привязать его к модели
/// нельзя. Всё остальное сделано привязками.
/// </para>
/// </summary>
public partial class ApiBrowserView : UserControl
{
    /// <summary>Создаёт экран.</summary>
    public ApiBrowserView() => InitializeComponent();

    private void OnTreeSelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is ApiBrowserViewModel vm && e.NewValue is ApiTreeNodeViewModel node)
            vm.SelectedTreeNode = node;
    }
}
