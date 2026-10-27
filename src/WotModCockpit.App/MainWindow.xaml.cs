using System.ComponentModel;
using System.Windows;
using WotModCockpit.App.Controls;
using WotModCockpit.App.ViewModels;

namespace WotModCockpit.App;

/// <summary>
/// Единственное окно кокпита. Резолвится из контейнера вместе со своей моделью представления.
/// <para>
/// В code-behind оставлено ровно три вещи, которые в MVVM не выражаются:
/// подъём состояния после загрузки окна, освобождение тейлера при закрытии и
/// схлопывание колонки панели сборки (ширина <c>ColumnDefinition</c> не биндится —
/// ColumnDefinition не входит в визуальное дерево и не видит DataContext окна через RelativeSource).
/// </para>
/// </summary>
public partial class MainWindow : ChromeWindow
{
    private readonly MainViewModel _viewModel;
    private readonly Stage3ViewModel _stage3;
    private readonly SettingsDesignerViewModel _designer;
    private GridLength _buildColumnWidth = new(420);
    private GridLength _problemsRowHeight = new(230);

    /// <summary>Создаёт окно и привязывает его к модели представления.</summary>
    /// <param name="viewModel">Модель представления главного экрана.</param>
    /// <param name="stage3">
    /// Оболочка этапа 3 (Problems, рецепты, мастер нового проекта). Резолвится здесь, а не
    /// внутри <see cref="MainViewModel"/>: она сама ссылается на модель главного экрана,
    /// и в конструкторе такой связи быть не может.
    /// </param>
    /// <param name="designer">
    /// Дизайнер настроек ModsSettingsAPI (этап 4). Резолвится здесь по той же причине,
    /// что и оболочка этапа 3: он сам ссылается на модель главного экрана.
    /// </param>
    public MainWindow(MainViewModel viewModel, Stage3ViewModel stage3, SettingsDesignerViewModel designer)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _stage3 = stage3 ?? throw new ArgumentNullException(nameof(stage3));
        _designer = designer ?? throw new ArgumentNullException(nameof(designer));

        InitializeComponent();

        DataContext = _viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _stage3.PropertyChanged += OnStage3PropertyChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        ApplyBuildPanelWidth();
        ApplyProblemsRowHeight();
        _viewModel.Initialize();

        // Порядок важен: проект поднимается внутри Initialize, и только после него
        // панелям этапа 3 и дизайнеру настроек есть что показывать.
        _stage3.Initialize();
        _designer.Initialize();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _stage3.PropertyChanged -= OnStage3PropertyChanged;
        _stage3.Dispose();
        _designer.Dispose();
        _viewModel.Dispose();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ShowBuildPanel))
        {
            ApplyBuildPanelWidth();
        }
    }

    private void OnStage3PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Stage3ViewModel.ShowProblems))
        {
            ApplyProblemsRowHeight();
        }
    }

    /// <summary>
    /// Схлопывает или возвращает док Problems, запоминая высоту, которую пользователь
    /// выставил сплиттером. Строка держит пиксельную высоту (иначе её нечем тянуть),
    /// поэтому свёрнутую панель надо явно обнулять — сама по себе она оставила бы
    /// 230 px пустоты.
    /// </summary>
    private void ApplyProblemsRowHeight()
    {
        if (_stage3.ShowProblems)
        {
            ProblemsRow.Height = _problemsRowHeight;
            return;
        }

        if (ProblemsRow.Height.Value > 0)
        {
            _problemsRowHeight = ProblemsRow.Height;
        }

        ProblemsRow.Height = new GridLength(0);
    }

    /// <summary>
    /// Схлопывает или возвращает колонку панели сборки, запоминая ширину,
    /// которую пользователь выставил сплиттером.
    /// </summary>
    private void ApplyBuildPanelWidth()
    {
        if (_viewModel.ShowBuildPanel)
        {
            BuildColumn.Width = _buildColumnWidth;
            return;
        }

        if (BuildColumn.Width.Value > 0)
        {
            _buildColumnWidth = BuildColumn.Width;
        }

        BuildColumn.Width = new GridLength(0);
    }
}
