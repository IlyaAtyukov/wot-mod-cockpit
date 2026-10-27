using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using WotModCockpit.App.ApiBrowser;
using WotModCockpit.Core.ApiIndex;

namespace WotModCockpit.App.ViewModels;

/// <summary>
/// Символ клиента в списке выдачи. Обёртка над <see cref="ApiSymbol"/>: считает всё, что
/// показывается, ОДИН раз в конструкторе — список виртуализован и переиспользует контейнеры,
/// поэтому вычисления в геттерах здесь превращаются в работу на каждый кадр прокрутки.
/// </summary>
public sealed class SymbolViewModel
{
    /// <summary>
    /// С какого числа объявлений одного имени поиск ссылок без уточнения приёмником
    /// становится бессмысленным. Замерено: у <c>update</c> 279 объявлений и 2182 попадания
    /// даже с границами идентификатора, у <c>getVehicle</c> — 25 и 143.
    /// </summary>
    public const int AmbiguityThreshold = 10;

    /// <summary>Создаёт модель представления поверх символа индекса.</summary>
    /// <param name="symbol">Символ из <see cref="IApiBrowserBackend.SearchAsync"/>.</param>
    public SymbolViewModel(ApiSymbol symbol)
    {
        Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));

        KindBadge = KindToBadge(symbol.Kind);
        KindText = KindToText(symbol.Kind);
        TierBadge = symbol.Tier switch
        {
            SourceTier.Generated => "gen",
            SourceTier.Vendor => "lib",
            _ => string.Empty
        };

        Decorators = DecoratorsToText(symbol.Decorators);
        ImportLine = BuildImportLine(symbol);

        IsAmbiguous = symbol.DeclarationCount > AmbiguityThreshold;
        HomonymText = symbol.DeclarationCount > 1
            ? $"объявлено в {symbol.DeclarationCount} местах"
            : string.Empty;

        Header = symbol.Signature is null ? symbol.QualifiedName : symbol.QualifiedName + symbol.Signature;

        ToolTipText = string.Join(
            Environment.NewLine,
            new[]
            {
                Header,
                $"{KindText} · {symbol.Module}",
                $"{symbol.VirtualPath}:{symbol.Line}",
                symbol.FullPath,
                Decorators.Length == 0 ? null : "декораторы: " + Decorators,
                HomonymText.Length == 0 ? null : HomonymText,
                symbol.IsServiceInterface ? "интерфейс сервиса из skeletons/ — берётся через dependency.instance()" : null,
                symbol.Tier == SourceTier.Generated
                    ? "сгенерированный ресурс: таблица из gui/impl/gen — целью монкипатча почти никогда не бывает"
                    : null,
                symbol.Tier == SourceTier.Vendor
                    ? "вендорный код (stdlib / site-packages): к клиенту отношения не имеет"
                    : null
            }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>Исходный символ индекса.</summary>
    public ApiSymbol Symbol { get; }

    /// <summary>Короткое имя.</summary>
    public string Name => Symbol.Name;

    /// <summary>Квалифицированное имя плюс сигнатура — первая строка элемента списка.</summary>
    public string Header { get; }

    /// <summary>Импортируемое имя модуля.</summary>
    public string Module => Symbol.Module;

    /// <summary>Путь как в traceback.</summary>
    public string VirtualPath => Symbol.VirtualPath;

    /// <summary>Абсолютный путь на диске.</summary>
    public string FullPath => Symbol.FullPath;

    /// <summary>Строка объявления.</summary>
    public int Line => Symbol.Line;

    /// <summary>Вторая строка элемента списка: модуль и строка.</summary>
    public string Location => $"{Symbol.Module}:{Symbol.Line}";

    /// <summary>Трёхбуквенный значок вида символа.</summary>
    public string KindBadge { get; }

    /// <summary>Вид символа словами.</summary>
    public string KindText { get; }

    /// <summary>Значок слоя (<c>gen</c>/<c>lib</c>) либо пусто для игрового кода.</summary>
    public string TierBadge { get; }

    /// <summary>Декораторы строкой: <c>@property @staticmethod</c>.</summary>
    public string Decorators { get; }

    /// <summary>Готовая строка импорта для кнопки «копировать импорт».</summary>
    public string ImportLine { get; }

    /// <summary>Сколько ещё мест объявляют это имя, если больше одного.</summary>
    public string HomonymText { get; }

    /// <summary>Имя настолько омонимично, что xref без приёмника бесполезен.</summary>
    public bool IsAmbiguous { get; }

    /// <summary>Файл лежит в <c>skeletons/</c>.</summary>
    public bool IsServiceInterface => Symbol.IsServiceInterface;

    /// <summary>Подсказка на весь элемент.</summary>
    public string ToolTipText { get; }

    /// <summary>
    /// Приёмник, которым имеет смысл уточнить поиск ссылок. У символа индекса его НЕТ, и это
    /// не упущение, а свойство языка.
    /// <para>
    /// Здесь раньше отдавался <see cref="ApiSymbol.Owner"/> — имя класса-владельца. В исходниках
    /// клиента никто не пишет <c>ISettingsCache.update(...)</c>: пишут <c>self.update(...)</c>,
    /// <c>settings.update(...)</c>, <c>g_playerEvents.onAvatarReady += …</c>, то есть приёмником
    /// в МЕСТЕ ВЫЗОВА всегда стоит экземпляр, а не класс. Замерено на настоящем дереве:
    /// 36 проверенных методов из 36 давали с таким «приёмником» ноль попаданий против 34-1873
    /// без него, а методы — это 104 215 из 117 901 <c>def</c> корпуса.
    /// </para>
    /// <para>
    /// Настоящий приёмник знает только каталог (там это глобальный синглтон
    /// <c>g_playerEvents</c> или слот боевой сессии), поэтому его подставляет
    /// <c>ApiCatalogEntry.XrefReceiver</c>, а символ ищется голым именем — и получает полный
    /// список плюс подсказку «частые приёмники», по которой сужение делается одним щелчком.
    /// </para>
    /// </summary>
    public string? XrefReceiver => null;

    private static string KindToBadge(SymbolKind kind) => kind switch
    {
        SymbolKind.Module => "mod",
        SymbolKind.Class => "cls",
        SymbolKind.Function => "def",
        SymbolKind.Method => "mth",
        SymbolKind.NestedFunction => "nst",
        SymbolKind.Event => "evt",
        SymbolKind.Constant => "val",
        SymbolKind.Service => "svc",
        _ => "?"
    };

    private static string KindToText(SymbolKind kind) => kind switch
    {
        SymbolKind.Module => "модуль",
        SymbolKind.Class => "класс",
        SymbolKind.Function => "функция",
        SymbolKind.Method => "метод",
        SymbolKind.NestedFunction => "вложенная функция",
        SymbolKind.Event => "событие",
        SymbolKind.Constant => "константа",
        SymbolKind.Service => "сервис",
        _ => "символ"
    };

    /// <summary>
    /// Разворачивает флаги декораторов в строку. Показывается прямо в выдаче, потому что
    /// <c>@property</c> и <c>@staticmethod</c> напрямую меняют способ монкипатча: обёртка
    /// над property ставится на класс, а не на функцию.
    /// </summary>
    /// <param name="flags">Флаги символа.</param>
    /// <returns>Строка вида <c>@property @abstractmethod</c> либо пустая.</returns>
    private static string DecoratorsToText(DecoratorFlags flags)
    {
        if (flags == DecoratorFlags.None)
            return string.Empty;

        var parts = new List<string>(4);
        if (flags.HasFlag(DecoratorFlags.Property)) parts.Add("@property");
        if (flags.HasFlag(DecoratorFlags.Static)) parts.Add("@staticmethod");
        if (flags.HasFlag(DecoratorFlags.ClassMethod)) parts.Add("@classmethod");
        if (flags.HasFlag(DecoratorFlags.Abstract)) parts.Add("@abstractmethod");
        if (flags.HasFlag(DecoratorFlags.Async)) parts.Add("@async");
        if (flags.HasFlag(DecoratorFlags.DependencyKwargs)) parts.Add("@dependency");
        if (parts.Count == 0 && flags.HasFlag(DecoratorFlags.Other)) parts.Add("@…");

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Строит строку импорта. Метод импортировать нельзя — импортируется его класс,
    /// поэтому для <see cref="SymbolKind.Method"/> берётся первый сегмент квалифицированного
    /// имени, а сам метод уходит в комментарий.
    /// </summary>
    /// <param name="s">Символ.</param>
    /// <returns>Готовая строка Python.</returns>
    private static string BuildImportLine(ApiSymbol s)
    {
        if (string.IsNullOrEmpty(s.Module))
            return "# модуль не определён: " + s.RelativePath;

        if (s.Kind == SymbolKind.Module)
            return $"import {s.Module}";

        string top = s.QualifiedName;
        int dot = top.IndexOf('.');
        if (dot > 0)
            top = top[..dot];

        return s.Kind switch
        {
            SymbolKind.Class or SymbolKind.Function or SymbolKind.Constant or SymbolKind.Service =>
                $"from {s.Module} import {s.Name}",
            _ => $"from {s.Module} import {top}  # .{s.Name}"
        };
    }
}

/// <summary>
/// Одно попадание панели «где вызывается». Неизменяемая обёртка: попаданий бывает несколько
/// сотен, и каждое живёт ровно до следующего запроса.
/// </summary>
public sealed class XrefHitViewModel
{
    /// <summary>Создаёт модель представления поверх попадания.</summary>
    /// <param name="hit">Попадание из <see cref="IApiBrowserBackend.FindReferencesAsync"/>.</param>
    public XrefHitViewModel(ApiXrefHit hit)
    {
        Hit = hit ?? throw new ArgumentNullException(nameof(hit));

        Location = $"{hit.RelativePath}:{hit.Line}";
        KindBadge = hit.Kind switch
        {
            ApiXrefKind.Declaration => "объявл",
            ApiXrefKind.Call => "вызов",
            ApiXrefKind.EventSubscription => "+=/-=",
            ApiXrefKind.AttributeRead => "атрибут",
            ApiXrefKind.Import => "импорт",
            ApiXrefKind.StringLiteral => "строка",
            ApiXrefKind.Comment => "коммент",
            _ => "прочее"
        };

        // Строковый литерал — единственная категория, которую стоит подозревать в мусоре,
        // и её доля после проверки границ идентификатора 0-3.3 %. Поэтому попадание
        // помечается значком, а не прячется.
        IsNoise = hit.Kind is ApiXrefKind.StringLiteral or ApiXrefKind.Comment;

        ToolTipText = $"{hit.VirtualPath}:{hit.Line}{Environment.NewLine}{hit.FullPath}{Environment.NewLine}{hit.Text}";
    }

    /// <summary>Исходное попадание.</summary>
    public ApiXrefHit Hit { get; }

    /// <summary>Файл и строка.</summary>
    public string Location { get; }

    /// <summary>Текст строки.</summary>
    public string Text => Hit.Text;

    /// <summary>Категория попадания коротким словом.</summary>
    public string KindBadge { get; }

    /// <summary>Попадание похоже на мусор (строковый литерал, комментарий).</summary>
    public bool IsNoise { get; }

    /// <summary>Подсказка: виртуальный путь, путь на диске, строка целиком.</summary>
    public string ToolTipText { get; }
}

/// <summary>
/// Узел дерева модулей с ЛЕНИВОЙ подгрузкой. В корпусе 8052 файла и 156 710 символов;
/// строить всё дерево целиком незачем — раскрытый узел подгружает только свой уровень.
/// <para>
/// Уровней три: каталог → файл → символ. Дерево доводит именно до символа, а не до файла:
/// «открыть SystemMessages.py» и «встать на pushMessage» — разные по полезности действия.
/// </para>
/// </summary>
public sealed partial class ApiTreeNodeViewModel : ObservableObject
{
    private static readonly object Placeholder = new();

    private readonly ApiTreeNode? _node;
    private readonly Func<ApiTreeNodeViewModel, Task>? _loader;
    private bool _loaded;

    /// <summary>Создаёт узел-каталог или узел-файл.</summary>
    /// <param name="node">Данные узла.</param>
    /// <param name="loader">Подгрузчик детей. Вызывается один раз при первом раскрытии.</param>
    public ApiTreeNodeViewModel(ApiTreeNode node, Func<ApiTreeNodeViewModel, Task> loader)
    {
        _node = node ?? throw new ArgumentNullException(nameof(node));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));

        // Заглушка обязательна и для каталога, и для файла: без неё TreeView не рисует
        // треугольник, и до содержимого узла нельзя добраться мышью.
        Children.Add(Placeholder);
    }

    /// <summary>Создаёт лист-символ. Детей у него нет и подгружать нечего.</summary>
    /// <param name="symbol">Символ файла.</param>
    public ApiTreeNodeViewModel(SymbolViewModel symbol)
        => Symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));

    /// <summary>Данные узла для каталога и файла; у листа-символа — <c>null</c>.</summary>
    public ApiTreeNode? Node => _node;

    /// <summary>Символ, если это лист-символ.</summary>
    public SymbolViewModel? Symbol { get; }

    /// <summary>Имя для показа.</summary>
    public string Name => Symbol?.Name ?? _node?.Name ?? string.Empty;

    /// <summary>Значок вида символа для листа; для каталога и файла — пусто.</summary>
    public string KindBadge => Symbol?.KindBadge ?? string.Empty;

    /// <summary>Путь относительно корня дерева.</summary>
    public string RelativePath => _node?.RelativePath ?? Symbol?.Symbol.RelativePath ?? string.Empty;

    /// <summary>Узел — каталог.</summary>
    public bool IsDirectory => _node?.IsDirectory ?? false;

    /// <summary>Абсолютный путь на диске.</summary>
    public string FullPath => Symbol?.FullPath ?? _node?.FullPath ?? string.Empty;

    /// <summary>Виртуальный путь.</summary>
    public string VirtualPath => Symbol?.VirtualPath ?? _node?.VirtualPath ?? string.Empty;

    /// <summary>Сколько символов в файле либо в поддереве. 0 — не показывать.</summary>
    public int SymbolCount => _node?.SymbolCount ?? 0;

    /// <summary>Слой корпуса: сгенерированное и вендорное красится приглушённо.</summary>
    public SourceTier Tier => _node?.Tier ?? Symbol?.Symbol.Tier ?? SourceTier.Game;

    /// <summary>Подсказка узла.</summary>
    public string ToolTipText => Symbol?.ToolTipText
                                 ?? (IsDirectory
                                     ? RelativePath
                                     : $"{VirtualPath}{Environment.NewLine}{FullPath}");

    /// <summary>
    /// Дети. Пока узел не раскрыт, здесь лежит один объект-заглушка: без него TreeView
    /// не рисует треугольник раскрытия и до узла нельзя добраться мышью.
    /// </summary>
    public ObservableCollection<object> Children { get; } = new();

    /// <summary>Узел раскрыт.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    /// <summary>Узел выбран.</summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Идёт подгрузка детей.</summary>
    [ObservableProperty]
    private bool _isLoading;

    /// <summary>Заменяет заглушку настоящими детьми.</summary>
    /// <param name="children">Готовые узлы.</param>
    public void SetChildren(IEnumerable<ApiTreeNodeViewModel> children)
    {
        Children.Clear();
        foreach (ApiTreeNodeViewModel child in children)
            Children.Add(child);
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (!value || _loaded || _loader is null)
            return;

        _loaded = true;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            await _loader!(this).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // Подгрузчик уже пишет причину в строку состояния; ронять дерево нельзя.
            _loaded = false;
        }
        finally
        {
            IsLoading = false;
        }
    }
}
