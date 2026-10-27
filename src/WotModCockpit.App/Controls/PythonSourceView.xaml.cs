using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace WotModCockpit.App.Controls;

/// <summary>
/// Просмотрщик исходника клиента: AvalonEdit с подсветкой Python 2.7, прокруткой к строке
/// объявления и подсветкой самой этой строки.
/// <para>
/// Три вещи, ради которых здесь есть код помимо разметки:
/// </para>
/// <list type="number">
///   <item><b>Чтение с диска уходит в пул потоков и отменяется.</b> В корпусе есть файлы по
///         5 МБ (<c>gui/impl/gen/resources/strings.py</c>), и щёлканье по списку результатов
///         не должно морозить окно.</item>
///   <item><b>У больших файлов подсветка выключается.</b> Подсветка AvalonEdit тянет состояние
///         Span от начала документа, и на мегабайтах прокрутка в конец файла становится
///         заметно дорогой. Порог <see cref="HighlightSizeLimit"/>; о выключении говорится
///         в <see cref="Notice"/>, а не молча.</item>
///   <item><b>Отказ — это сообщение поверх пустого поля, а не исключение.</b> Файла может
///         не быть на диске вовсе: индекс строится по дереву, которое пользователь волен
///         снести между двумя щелчками.</item>
/// </list>
/// </summary>
public partial class PythonSourceView : UserControl
{
    /// <summary>
    /// Порог, выше которого подсветка синтаксиса выключается (в символах).
    /// 1.5 МБ выбраны по корпусу: p99 файла — 61 КБ, то есть порог не задевает 99 % файлов
    /// и срабатывает только на четвёрке сгенерированных таблиц ресурсов.
    /// </summary>
    public const int HighlightSizeLimit = 1_500_000;

    private static IHighlightingDefinition? _python;
    private static bool _pythonLoadFailed;

    private readonly DefinitionLineRenderer _lineRenderer = new();

    private CancellationTokenSource? _loadCts;
    private string? _loadedPath;

    /// <summary>В редакторе есть текст: можно прокручивать к строке и подсвечивать её.</summary>
    private bool _hasContent;

    /// <summary>Создаёт просмотрщик и подключает подсветку.</summary>
    public PythonSourceView()
    {
        InitializeComponent();

        Editor.TextArea.TextView.BackgroundRenderers.Add(_lineRenderer);
        Editor.TextArea.TextView.LinkTextForegroundBrush = new SolidColorBrush(Color.FromRgb(0x6F, 0xB3, 0xE0));
        Editor.Options.EnableHyperlinks = false;
        Editor.Options.EnableEmailHyperlinks = false;
        Editor.Options.HighlightCurrentLine = false;
        Editor.Options.ShowBoxForControlCharacters = false;

        // Ctrl+F внутри файла. Дешёвая и очень нужная вещь: карточка символа часто
        // открывает файл на 3000 строк, и «где ещё в этом файле» решается прямо здесь.
        SearchPanel.Install(Editor);

        ShowOverlay(EmptyText);
    }

    // ============================================================ свойства зависимостей

    /// <summary>Абсолютный путь к показываемому файлу. <c>null</c> или пусто = пустое состояние.</summary>
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath),
        typeof(string),
        typeof(PythonSourceView),
        new PropertyMetadata(null, OnSourcePathChanged));

    /// <summary>Строка объявления (1-based). 0 или меньше = не подсвечивать и не прокручивать.</summary>
    public static readonly DependencyProperty HighlightedLineProperty = DependencyProperty.Register(
        nameof(HighlightedLine),
        typeof(int),
        typeof(PythonSourceView),
        new PropertyMetadata(0, OnHighlightedLineChanged));

    /// <summary>Что писать, когда файл не выбран.</summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(PythonSourceView),
        new PropertyMetadata("Символ не выбран."));

    /// <summary>
    /// Замечание о показанном файле («подсветка выключена: файл 5.1 МБ»), либо пустая строка.
    /// Только для чтения снаружи — заголовок карточки показывает его рядом с путём.
    /// </summary>
    public static readonly DependencyProperty NoticeProperty = DependencyProperty.Register(
        nameof(Notice),
        typeof(string),
        typeof(PythonSourceView),
        new PropertyMetadata(string.Empty));

    /// <summary>
    /// Готовый текст вместо файла на диске. Если задан (не <c>null</c>), он ПЕРЕКРЫВАЕТ
    /// <see cref="SourcePath"/> и никакого чтения с диска не происходит.
    /// <para>
    /// Нужен каталогу рецептов этапа 3: там показывается не файл клиента, а код, собранный
    /// подстановкой параметров прямо в момент набора. Заводить ради этого второй просмотрщик
    /// с той же подсветкой Python 2.7, той же нумерацией строк и тем же Ctrl+F было бы
    /// дублированием.
    /// </para>
    /// </summary>
    public static readonly DependencyProperty SourceTextProperty = DependencyProperty.Register(
        nameof(SourceText),
        typeof(string),
        typeof(PythonSourceView),
        new PropertyMetadata(null, OnSourceTextChanged));

    /// <inheritdoc cref="SourceTextProperty" />
    public string? SourceText
    {
        get => (string?)GetValue(SourceTextProperty);
        set => SetValue(SourceTextProperty, value);
    }

    /// <inheritdoc cref="SourcePathProperty" />
    public string? SourcePath
    {
        get => (string?)GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    /// <inheritdoc cref="HighlightedLineProperty" />
    public int HighlightedLine
    {
        get => (int)GetValue(HighlightedLineProperty);
        set => SetValue(HighlightedLineProperty, value);
    }

    /// <inheritdoc cref="EmptyTextProperty" />
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <inheritdoc cref="NoticeProperty" />
    public string Notice
    {
        get => (string)GetValue(NoticeProperty);
        private set => SetValue(NoticeProperty, value);
    }

    // ============================================================ загрузка

    private static void OnSourcePathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PythonSourceView)d).ReloadAsync();

    private static void OnSourceTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((PythonSourceView)d).ReloadAsync();

    private static void OnHighlightedLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (PythonSourceView)d;

        // Файл ещё грузится — прокрутку сделает сам загрузчик, когда текст доедет.
        if (view._hasContent)
            view.ApplyLine();
    }

    /// <summary>
    /// Перечитать текущий файл с диска (например, после того как его пересобрал декомпилятор).
    /// </summary>
    public void Reload() => ReloadAsync(force: true);

    private async void ReloadAsync(bool force = false)
    {
        string? path = SourcePath;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;

        // Готовый текст перекрывает файл: см. SourceTextProperty.
        string? inline = SourceText;
        if (inline is not null)
        {
            _loadedPath = null;
            ShowContent(inline);
            return;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _loadedPath = null;
            _hasContent = false;
            Editor.Text = string.Empty;
            Notice = string.Empty;
            ShowOverlay(EmptyText);
            return;
        }

        if (!force && string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
        {
            ApplyLine();
            return;
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        CancellationToken token = cts.Token;

        string text;
        try
        {
            text = await Task.Run(() => File.ReadAllText(path!), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _loadedPath = null;
            _hasContent = false;
            Editor.Text = string.Empty;
            Notice = string.Empty;
            ShowOverlay($"Файл открыть не удалось:\n{path}\n\n{ex.Message}");
            return;
        }

        // Пока читали, пользователь мог щёлкнуть другой символ.
        if (token.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            return;

        _loadedPath = path;
        ShowContent(text);
    }

    /// <summary>
    /// Показывает готовый текст: подсветка, содержимое, замечание, прокрутка к строке.
    /// Общий хвост для файла с диска и для <see cref="SourceText"/>.
    /// </summary>
    /// <param name="text">Текст.</param>
    private void ShowContent(string text)
    {
        Editor.SyntaxHighlighting = text.Length > HighlightSizeLimit ? null : LoadPythonHighlighting();
        Editor.Text = text;
        _hasContent = text.Length > 0;

        Notice = text.Length > HighlightSizeLimit
            ? $"подсветка выключена: файл {text.Length / (1024.0 * 1024.0):0.#} МБ"
            : _pythonLoadFailed
                ? "подсветка недоступна: Python-Mode.xshd не загрузился"
                : string.Empty;

        HideOverlay();
        ApplyLine();
    }

    private void ApplyLine()
    {
        int line = HighlightedLine;
        int max = Editor.Document?.LineCount ?? 0;

        if (line <= 0 || max == 0)
        {
            _lineRenderer.Line = 0;
            Editor.TextArea.TextView.InvalidateLayer(_lineRenderer.Layer);
            return;
        }

        // Номер строки приходит из индекса, а файл на диске мог поменяться: клампим,
        // иначе AvalonEdit бросит ArgumentOutOfRangeException прямо в UI-поток.
        line = Math.Clamp(line, 1, max);

        _lineRenderer.Line = line;

        // ScrollToLine у AvalonEdit УЖЕ центрирует: внутри это
        // ScrollTo(line, column, VisualYPosition.LineMiddle, ViewportHeight / 2, ...).
        // Никакой доводки поверх него делать нельзя: WPF пересчитывает
        // ScrollViewer.VerticalOffset только на следующем проходе разметки, поэтому
        // арифметика по свежепрочитанному VerticalOffset берёт СТАРОЕ значение (у только
        // что открытого файла — ноль) и своим ScrollToVerticalOffset отменяет прокрутку,
        // оставляя пользователя в начале файла. Проверено тестом
        // ApiBrowserViewTests.SourceViewer_ScrollsToHighlightedLine.
        Editor.ScrollToLine(line);

        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        Editor.TextArea.TextView.InvalidateLayer(_lineRenderer.Layer);
    }

    private void ShowOverlay(string message)
    {
        OverlayText.Text = message;
        Overlay.Visibility = Visibility.Visible;
    }

    private void HideOverlay() => Overlay.Visibility = Visibility.Collapsed;

    /// <summary>
    /// Грузит определение подсветки из встроенного ресурса. Один раз на процесс:
    /// разбор XSHD стоит миллисекунды, но делать его на каждый щелчок незачем.
    /// Отказ не фатален — файл просто покажется без цвета.
    /// </summary>
    /// <returns>Определение подсветки либо <c>null</c>.</returns>
    private static IHighlightingDefinition? LoadPythonHighlighting()
    {
        if (_python is not null || _pythonLoadFailed)
            return _python;

        try
        {
            using Stream? stream = typeof(PythonSourceView).Assembly
                .GetManifestResourceStream("WotModCockpit.App.Resources.Python-Mode.xshd");

            if (stream is null)
            {
                _pythonLoadFailed = true;
                return null;
            }

            using XmlReader reader = XmlReader.Create(stream);
            _python = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            return _python;
        }
        catch (Exception)
        {
            // Битый ресурс не должен ронять окно: подсветка — украшение, а не функция.
            _pythonLoadFailed = true;
            return null;
        }
    }

    /// <summary>
    /// Заливка строки объявления. Отдельный слой рисования, а не выделение текста:
    /// выделение пользователь снимает первым же щелчком, а «вот здесь объявлен символ»
    /// должно оставаться на экране, пока показан этот символ.
    /// </summary>
    private sealed class DefinitionLineRenderer : IBackgroundRenderer
    {
        private static readonly Brush Fill = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x2C, 0x6F, 0xA8)));
        private static readonly Pen Edge = Freeze(new Pen(Freeze(new SolidColorBrush(Color.FromRgb(0x3A, 0x87, 0xC7))), 1));

        /// <summary>Подсвечиваемая строка, 1-based. 0 = ничего не рисовать.</summary>
        public int Line { get; set; }

        /// <inheritdoc />
        public KnownLayer Layer => KnownLayer.Background;

        /// <inheritdoc />
        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Line <= 0 || textView.Document is null || Line > textView.Document.LineCount)
                return;

            textView.EnsureVisualLines();

            // Рисуем по визуальной строке, а не по сегменту текста: у сегмента пустой строки
            // нулевая ширина, и подсветка объявления просто исчезла бы. Плюс так полоса идёт
            // на всю ширину окна и читается как «вот эта строка», а не как выделение текста.
            VisualLine? visual = textView.GetVisualLine(Line);
            if (visual is null)
                return;

            double top = visual.VisualTop - textView.ScrollOffset.Y;
            drawingContext.DrawRectangle(Fill, Edge, new Rect(0, top, Math.Max(0, textView.ActualWidth), visual.Height));
        }

        private static T Freeze<T>(T freezable) where T : Freezable
        {
            freezable.Freeze();
            return freezable;
        }
    }
}
