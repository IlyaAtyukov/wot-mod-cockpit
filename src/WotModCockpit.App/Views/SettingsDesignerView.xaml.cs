using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App.Views;

/// <summary>
/// Дизайнер настроек ModsSettingsAPI: палитра слева, схема окна игры в центре,
/// свойства выбранного контрола справа.
/// <para>
/// В code-behind — только то, что в MVVM не выражается: перетаскивание из палитры
/// (жест мыши), двойной щелчок по строке палитры и переход от строки списка замечаний
/// к контролу, о котором замечание.
/// </para>
/// </summary>
public partial class SettingsDesignerView : UserControl
{
    private Point _dragStart;

    /// <summary>Создаёт экран.</summary>
    public SettingsDesignerView()
    {
        InitializeComponent();
    }

    private SettingsDesignerViewModel? Model => DataContext as SettingsDesignerViewModel;

    // =================================================================== палитра

    private void OnPaletteMouseDown(object sender, MouseButtonEventArgs e) => _dragStart = e.GetPosition(this);

    private void OnPaletteMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
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
        if (item?.DataContext is not SettingsPaletteItem palette)
        {
            return;
        }

        try
        {
            DragDrop.DoDragDrop(PaletteList, SettingsDrag.BeginNew(palette.Type), DragDropEffects.Copy);
        }
        finally
        {
            SettingsDrag.End();
        }
    }

    private void OnPaletteDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        ListBoxItem? item = FindAncestor<ListBoxItem>(source);
        if (item?.DataContext is not SettingsPaletteItem palette)
        {
            return;
        }

        Model?.AddFromPaletteCommand.Execute(palette);
    }

    // =================================================================== замечания

    /// <summary>
    /// Щелчок по замечанию выбирает контрол, о котором оно. Это единственный способ
    /// добраться от «ошибок: 3» до места, которое надо чинить, не разглядывая обе колонки.
    /// </summary>
    private void OnIssueSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox list
            || list.SelectedItem is not SettingsDesignerIssue issue
            || issue.Control is null
            || Model is null)
        {
            return;
        }

        Model.Selected = issue.Control;
    }

    private void OnCloseImport(object sender, RoutedEventArgs e)
    {
        if (Model is not null)
        {
            Model.ShowImport = false;
        }
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
}
