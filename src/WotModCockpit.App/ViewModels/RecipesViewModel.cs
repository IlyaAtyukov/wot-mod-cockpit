using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WotModCockpit.App.Collections;
using WotModCockpit.App.Stage3;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.App.ViewModels;

/// <summary>Строка каталога рецептов.</summary>
public sealed partial class RecipeItemViewModel : ObservableObject
{
    /// <summary>Создаёт строку.</summary>
    /// <param name="card">Рецепт.</param>
    public RecipeItemViewModel(RecipeCard card) => Card = card;

    /// <summary>Сам рецепт.</summary>
    public RecipeCard Card { get; }

    /// <summary>Идентификатор (<c>R01</c>).</summary>
    public string Id => Card.Id;

    /// <summary>Заголовок.</summary>
    public string Title => Card.Title;

    /// <summary>Рубрика — она же ключ группировки списка.</summary>
    public string Category => Card.Category;

    /// <summary>Одна строка про рецепт.</summary>
    public string Summary => Card.Summary;

    /// <summary>Значок сложности.</summary>
    public string LevelBadge => Card.Level switch
    {
        RecipeLevel.Beginner => "новичок",
        RecipeLevel.Advanced => "продвинутый",
        _ => "средний"
    };

    /// <summary>Итог сверки с индексом клиента. <c>null</c> — ещё не сверяли.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIncompatible))]
    [NotifyPropertyChangedFor(nameof(CompatibilityText))]
    [NotifyPropertyChangedFor(nameof(HasCompatibilityNote))]
    private RecipeCompatibility? _compatibility;

    /// <summary>
    /// Рецепт ссылается на то, чего в этой версии клиента нет. Не «сломан»: часть символов
    /// могла уехать в другой модуль, и это ровно повод посмотреть глазами.
    /// </summary>
    public bool IsIncompatible => Compatibility?.State == RecipeCompatibilityState.NeedsReview;

    /// <summary>Строка про совместимость.</summary>
    public string CompatibilityText => Compatibility?.Message ?? string.Empty;

    /// <summary>Есть что сказать про совместимость.</summary>
    public bool HasCompatibilityNote => IsIncompatible;

    /// <summary>Подсказка строки списка.</summary>
    public string ToolTipText => string.IsNullOrEmpty(Card.Rationale)
        ? Card.Summary
        : Card.Summary + "\n\n" + Card.Rationale;
}

/// <summary>
/// Одно поле подстановки. Правка значения перерисовывает предпросмотр — за это отвечает
/// обратный вызов, который ставит <see cref="RecipesViewModel"/>.
/// </summary>
public sealed partial class RecipeParamViewModel : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Создаёт поле.</summary>
    /// <param name="token">Точка подстановки.</param>
    /// <param name="value">Начальное значение.</param>
    /// <param name="changed">Что позвать при изменении значения.</param>
    public RecipeParamViewModel(RecipeToken token, string value, Action changed)
    {
        Token = token;
        _value = value;
        _changed = changed;
    }

    /// <summary>Точка подстановки.</summary>
    public RecipeToken Token { get; }

    /// <summary>Заголовок поля.</summary>
    public string Label => Token.Label;

    /// <summary>Подсказка под полем.</summary>
    public string Hint => Token.Hint;

    /// <summary>Что именно ищется в коде рецепта.</summary>
    public string Pattern => Token.Token;

    /// <summary>Значение подставляется из паспорта проекта.</summary>
    public bool FromProject => Token.FromProject;

    /// <summary>Значение.</summary>
    [ObservableProperty]
    private string _value;

    partial void OnValueChanged(string value) => _changed();
}

/// <summary>
/// Каталог рецептов: готовые куски кода на Python 2.7 под типовые задачи мода.
/// <para>
/// Экран отвечает на вопрос «мне нужно показать сообщение в ангаре, с чего начать» —
/// и отвечает не ссылкой на документацию, а кодом, который уже прошёл проверку синтаксиса
/// Python 2.7 и уже содержит try/except там, где код исполняет движок.
/// </para>
/// <para>
/// Три вещи, из-за которых здесь больше кода, чем «список плюс текст»:
/// </para>
/// <list type="number">
///   <item><b>Живая параметризация.</b> Рецепты написаны с токенами <c>my_mod</c> и <c>MyMod</c>;
///         подставляются они из паспорта ОТКРЫТОГО проекта, поэтому вставленный код сразу
///         пишет в лог под тем тегом, по которому фильтрует лента этапа 1.</item>
///   <item><b>Вставка не портит файл молча.</b> Перед дописыванием показывается и файл,
///         и текст, рядом кладётся <c>.bak</c>, а если в коде есть кириллица, а у файла нет
///         строки <c>coding</c> — об этом предупреждают отдельно (правило WOT015: Python 2.7
///         откажется компилировать такой файл уже ВНУТРИ клиента).</item>
///   <item><b>Совместимость сверяется с индексом.</b> Рецепт, ссылающийся на исчезнувший
///         символ, помечается — но пометка «не проверяли» и пометка «нет в клиенте» это
///         разные пометки, и путать их нельзя.</item>
/// </list>
/// </summary>
public sealed partial class RecipesViewModel : ObservableObject, IDisposable
{
    private readonly IRecipeBackend _backend;
    private readonly IEditorLauncher _editor;

    private readonly List<RecipeItemViewModel> _all = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private ModProject? _project;
    private bool _loaded;
    private bool _disposed;

    /// <summary>
    /// Рецепт, который выбран пользователем, но временно не проходит фильтр.
    /// <para>
    /// Список привязан к <c>ListBox.SelectedItem</c>, и подмена содержимого коллекции гасит
    /// выбор. Без этой памяти набранная в поиске строка, которая не подходит к выбранному
    /// рецепту, стирала выбор НАСОВСЕМ: очистка поиска возвращала список, но предпросмотр
    /// оставался пустым, хотя пользователь ничего не отменял.
    /// </para>
    /// </summary>
    private RecipeItemViewModel? _selectionHiddenByFilter;

    /// <summary>Создаёт модель экрана.</summary>
    /// <param name="backend">Шов к каталогу рецептов Core.</param>
    /// <param name="editor">Внешний редактор: после вставки файл открывается в нём.</param>
    public RecipesViewModel(IRecipeBackend backend, IEditorLauncher editor)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    }

    // =================================================================== список

    /// <summary>Рецепты после фильтра.</summary>
    public BulkObservableCollection<RecipeItemViewModel> Recipes { get; } = new();

    /// <summary>Выбранный рецепт.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(Rationale))]
    [NotifyPropertyChangedFor(nameof(Pitfall))]
    [NotifyPropertyChangedFor(nameof(SelectedTitle))]
    [NotifyPropertyChangedFor(nameof(CompatibilityText))]
    [NotifyCanExecuteChangedFor(nameof(CopyCodeCommand))]
    [NotifyCanExecuteChangedFor(nameof(InsertIntoFileCommand))]
    private RecipeItemViewModel? _selected;

    /// <summary>Что-то выбрано.</summary>
    public bool HasSelection => Selected is not null;

    /// <summary>Заголовок карточки.</summary>
    public string SelectedTitle => Selected is null
        ? string.Empty
        : $"{Selected.Id} · {Selected.Title}";

    /// <summary>Зачем и когда применять.</summary>
    public string Rationale => Selected?.Card.Rationale ?? string.Empty;

    /// <summary>Где ломается — самая ценная часть карточки.</summary>
    public string Pitfall => Selected?.Card.Pitfall ?? string.Empty;

    /// <summary>Строка про совместимость выбранного рецепта.</summary>
    public string CompatibilityText => Selected?.CompatibilityText ?? string.Empty;

    /// <summary>Поиск по каталогу.</summary>
    [ObservableProperty]
    private string _searchText = string.Empty;

    /// <summary>Показывать только рецепты для новичка.</summary>
    [ObservableProperty]
    private bool _onlyBeginner;

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnOnlyBeginnerChanged(bool value) => ApplyFilter();

    partial void OnSelectedChanged(RecipeItemViewModel? value)
    {
        // Осознанный выбор перекрывает память фильтра: вернуть надо то, что выбрано сейчас,
        // а не то, что было выбрано до набора строки поиска.
        if (value is not null)
        {
            _selectionHiddenByFilter = null;
        }

        BuildParameters();
        Render();
        OnPropertyChanged(nameof(SymbolsText));
        ShowSymbolCommand.NotifyCanExecuteChanged();
    }

    // =================================================================== параметры и предпросмотр

    /// <summary>Поля подстановки для выбранного рецепта.</summary>
    public ObservableCollection<RecipeParamViewModel> Parameters { get; } = new();

    /// <summary>Код рецепта с подставленными значениями. Это и есть то, что копируется и вставляется.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    private string _renderedCode = string.Empty;

    /// <summary>Есть что показывать.</summary>
    public bool HasCode => RenderedCode.Length > 0;

    /// <summary>Символы клиента, от которых зависит рецепт — одной строкой.</summary>
    public string SymbolsText => Selected is null
        ? string.Empty
        : string.Join(", ", Selected.Card.Symbols.Select(s => s.Display));

    private void BuildParameters()
    {
        Parameters.Clear();

        RecipeItemViewModel? recipe = Selected;
        if (recipe is null)
        {
            return;
        }

        foreach (RecipeToken token in RecipeRenderer.Scan(recipe.Card.Code))
        {
            string value = _values.TryGetValue(token.Token, out string? stored)
                ? stored
                : DefaultFor(token);

            Parameters.Add(new RecipeParamViewModel(token, value, OnParameterChanged));
        }
    }

    /// <summary>
    /// Значение поля по умолчанию. Паспортные поля берутся из ОТКРЫТОГО проекта — это и есть
    /// связка с этапом 1: тег лога в рецепте совпадёт с тем, по которому фильтруется лента.
    /// </summary>
    /// <param name="token">Точка подстановки.</param>
    /// <returns>Значение.</returns>
    private string DefaultFor(RecipeToken token)
    {
        ModProject? p = _project;
        if (p is null || !token.FromProject)
        {
            return token.Default;
        }

        return token.Token switch
        {
            "ru.local.my_mod" => Pick(p.ModId, token.Default),
            "my_mod" => Pick(p.PackageName, token.Default),
            "My Mod" => Pick(p.DisplayName, token.Default),
            "MyMod" => Pick(NormalizeTag(p.LogTag, p.PackageName), token.Default),
            _ => token.Default
        };

        static string Pick(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    /// <summary>
    /// Тег лога без скобок. В <c>wotmod.json</c> он хранится как <c>[HelloTanker</c> —
    /// с открывающей скобкой и без закрывающей, чтобы ловить и <c>[HelloTanker]</c>,
    /// и <c>[HelloTanker/loader]</c>. В код рецепта скобки подставлять нельзя.
    /// </summary>
    /// <param name="logTag">Тег из паспорта проекта.</param>
    /// <param name="package">Имя пакета — запасной источник.</param>
    /// <returns>Голое имя тега либо пустая строка.</returns>
    private static string NormalizeTag(string? logTag, string? package)
    {
        string tag = (logTag ?? string.Empty).Trim().Trim('[', ']');
        if (tag.Length > 0)
        {
            return tag;
        }

        string pkg = (package ?? string.Empty).Trim();
        if (pkg.Length == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(pkg.Length);
        bool upper = true;
        foreach (char c in pkg)
        {
            if (c is '_' or '-' or ' ')
            {
                upper = true;
                continue;
            }

            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    private void OnParameterChanged()
    {
        foreach (RecipeParamViewModel p in Parameters)
        {
            _values[p.Pattern] = p.Value;
        }

        Render();
    }

    private void Render()
    {
        RecipeItemViewModel? recipe = Selected;
        if (recipe is null)
        {
            RenderedCode = string.Empty;
            return;
        }

        var map = new List<KeyValuePair<string, string>>(Parameters.Count);
        foreach (RecipeParamViewModel p in Parameters)
        {
            map.Add(new KeyValuePair<string, string>(p.Pattern, p.Value ?? string.Empty));
        }

        RenderedCode = RecipeRenderer.Render(recipe.Card.Code, map);
    }

    /// <summary>Вернуть паспортным полям значения из проекта (после того, как их правили руками).</summary>
    [RelayCommand]
    private void ResetParameters()
    {
        foreach (RecipeParamViewModel p in Parameters)
        {
            _values.Remove(p.Pattern);
            p.Value = DefaultFor(p.Token);
        }

        Render();
        StatusText = "значения полей возвращены к паспорту проекта";
    }

    // =================================================================== загрузка каталога

    /// <summary>Идёт загрузка каталога или сверка совместимости.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private bool _isLoading;

    /// <summary>Строка состояния экрана.</summary>
    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>Сводка по каталогу.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EmptyStateText))]
    private string _catalogStatus = string.Empty;

    /// <summary>Каталог подключён.</summary>
    public bool IsAvailable => _backend.IsAvailable;

    /// <summary>Что показать вместо списка.</summary>
    public string EmptyStateText
    {
        get
        {
            if (!_backend.IsAvailable)
            {
                return "Каталог рецептов не подключён.\n\n"
                       + _backend.UnavailableReason + "\n\n"
                       + "Остальные экраны кокпита от этого не зависят.";
            }

            if (IsLoading)
            {
                return "Загружаем каталог рецептов…";
            }

            return _all.Count == 0
                ? "Каталог пуст."
                : "Ни один рецепт не проходит фильтр.\n\nОчистите поиск или снимите «только для новичка».";
        }
    }

    /// <summary>
    /// Поднимает экран: читает каталог и сверяет рецепты с индексом клиента.
    /// Вызывается лениво — при первом показе вкладки.
    /// </summary>
    /// <returns>Задача загрузки.</returns>
    public async Task LoadAsync()
    {
        if (_loaded || !_backend.IsAvailable)
        {
            OnPropertyChanged(nameof(EmptyStateText));
            return;
        }

        _loaded = true;

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;

        IsLoading = true;
        CatalogStatus = "загрузка…";

        try
        {
            IReadOnlyList<RecipeCard> cards = await _backend
                .GetRecipesAsync(cts.Token)
                .ConfigureAwait(true);

            if (cts.IsCancellationRequested || !ReferenceEquals(_cts, cts))
            {
                return;
            }

            // Карточки пересозданы — запомненный экземпляр указывает в прошлую загрузку
            // и в новый список уже не попадёт.
            _selectionHiddenByFilter = null;

            _all.Clear();
            _all.AddRange(cards.Select(c => new RecipeItemViewModel(c)));
            ApplyFilter();

            CatalogStatus = $"рецептов: {_all.Count}";
        }
        catch (OperationCanceledException)
        {
            _loaded = false;
        }
        catch (Exception ex)
        {
            _loaded = false;
            CatalogStatus = "каталог недоступен";
            StatusText = "рецепты: " + Short(ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
            }
        }

        await CheckCompatibilityAsync(cts).ConfigureAwait(true);
    }

    /// <summary>
    /// Сверяет каждый рецепт с индексом клиента. Идёт после показа списка и по одному
    /// рецепту: список обязан появиться сразу, а пометки — доехать сами.
    /// </summary>
    /// <param name="cts">Токен загрузки каталога.</param>
    private async Task CheckCompatibilityAsync(CancellationTokenSource cts)
    {
        int reviewed = 0;

        foreach (RecipeItemViewModel vm in _all.ToArray())
        {
            if (cts.IsCancellationRequested || !ReferenceEquals(_cts, cts))
            {
                return;
            }

            try
            {
                RecipeCompatibility result = await _backend.CheckAsync(vm.Card, cts.Token).ConfigureAwait(true);
                vm.Compatibility = result;

                if (result.State == RecipeCompatibilityState.NeedsReview)
                {
                    reviewed++;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // Сверка — удобство. Её отказ не имеет права прятать каталог.
                vm.Compatibility = RecipeCompatibility.Unknown;
            }
        }

        if (ReferenceEquals(_cts, cts))
        {
            CatalogStatus = reviewed == 0
                ? $"рецептов: {_all.Count} · все сверены с индексом клиента"
                : $"рецептов: {_all.Count} · требуют проверки руками: {reviewed}";

            OnPropertyChanged(nameof(CompatibilityText));
        }
    }

    private void ApplyFilter()
    {
        string needle = SearchText?.Trim() ?? string.Empty;

        var shown = new List<RecipeItemViewModel>(_all.Count);
        foreach (RecipeItemViewModel vm in _all)
        {
            if (OnlyBeginner && vm.Card.Level != RecipeLevel.Beginner)
            {
                continue;
            }

            if (needle.Length > 0 && !vm.Card.SearchText.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            shown.Add(vm);
        }

        // Снимок ДО подмены коллекции: ReplaceAll поднимает Reset, ListBox тут же гасит
        // SelectedItem, и к следующей строке Selected уже был бы null.
        RecipeItemViewModel? remembered = Selected ?? _selectionHiddenByFilter;

        Recipes.ReplaceAll(shown);

        if (remembered is not null && shown.Contains(remembered))
        {
            _selectionHiddenByFilter = null;
            Selected = remembered;
        }
        else
        {
            _selectionHiddenByFilter = remembered;
            Selected = null;
        }

        OnPropertyChanged(nameof(EmptyStateText));
    }

    // =================================================================== проект и вставка

    /// <summary>Файлы проекта, куда можно вставить рецепт (относительные пути от корня).</summary>
    public ObservableCollection<string> TargetFiles { get; } = new();

    /// <summary>Куда вставлять.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertIntoFileCommand))]
    private string? _selectedTargetFile;

    /// <summary>Есть открытый проект — значит, есть куда вставлять.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InsertHint))]
    private bool _hasProject;

    /// <summary>Подсказка под выбором файла.</summary>
    public string InsertHint => HasProject
        ? "рецепт дописывается В КОНЕЦ файла; рядом остаётся копия .bak"
        : "проект не выбран — вставлять некуда, но код можно скопировать";

    /// <summary>
    /// Диалог подтверждения вставки. Свойством — чтобы его можно было подменить в тестах,
    /// ровно как <see cref="MainViewModel.ConfirmHandler"/>.
    /// </summary>
    public Func<string, string, bool> ConfirmHandler { get; set; } = DefaultConfirm;

    /// <summary>Просят показать символ рецепта в браузере API.</summary>
    public event EventHandler<RecipeSymbol>? ApiBrowserRequested;

    /// <summary>Сменился открытый проект: обновляются паспортные поля и список файлов для вставки.</summary>
    /// <param name="project">Проект либо <c>null</c>.</param>
    public void SetProject(ModProject? project)
    {
        _project = project;
        HasProject = project is not null;

        // Значения, введённые руками, при смене проекта не переживают: они относились
        // к прошлому паспорту, и молча подставить их в чужой мод было бы хуже, чем сбросить.
        _values.Clear();
        RefreshTargetFiles();
        BuildParameters();
        Render();
    }

    private void RefreshTargetFiles()
    {
        TargetFiles.Clear();
        SelectedTargetFile = null;

        ModProject? p = _project;
        if (p is null)
        {
            return;
        }

        string root;
        try
        {
            root = p.SrcDir;
            if (!Directory.Exists(root))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            StatusText = "поиск файлов проекта: " + Short(ex);
            return;
        }

        try
        {
            foreach (string file in Directory
                         .EnumerateFiles(root, "*.py", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                TargetFiles.Add(Path.GetRelativePath(p.ProjectDir, file).Replace('\\', '/'));
            }
        }
        catch (Exception ex)
        {
            StatusText = "поиск файлов проекта: " + Short(ex);
            return;
        }

        // Тело мода — самая частая цель: точка входа это стаб, в неё код класть незачем.
        string preferred = string.IsNullOrEmpty(p.PackageName)
            ? string.Empty
            : $"src/res/scripts/client/gui/mods/{p.PackageName}/main.py";

        SelectedTargetFile = TargetFiles.FirstOrDefault(
                                 f => string.Equals(f, preferred, StringComparison.OrdinalIgnoreCase))
                             ?? TargetFiles.FirstOrDefault(f => f.EndsWith("/main.py", StringComparison.OrdinalIgnoreCase))
                             ?? TargetFiles.FirstOrDefault();
    }

    private bool CanCopyCode() => Selected is not null && RenderedCode.Length > 0;

    /// <summary>Скопировать код рецепта в буфер обмена.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyCode))]
    private void CopyCode()
    {
        try
        {
            Clipboard.SetText(RenderedCode);
            StatusText = $"скопировано: {Selected?.Id} · {Selected?.Title}";
        }
        catch (Exception ex)
        {
            StatusText = "буфер обмена: " + Short(ex);
        }
    }

    private bool CanInsertIntoFile() =>
        Selected is not null
        && RenderedCode.Length > 0
        && _project is not null
        && !string.IsNullOrEmpty(SelectedTargetFile);

    /// <summary>
    /// Дописать рецепт в конец выбранного файла проекта.
    /// <para>
    /// Порядок жёсткий и менять его нельзя: сперва человеку показывают ПОЛНЫЙ путь и то,
    /// что именно будет добавлено, затем рядом с файлом появляется <c>.bak</c>, и только
    /// потом файл переписывается. Молча испортить чужой исходник кокпит права не имеет.
    /// </para>
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanInsertIntoFile))]
    private void InsertIntoFile()
    {
        ModProject? project = _project;
        RecipeItemViewModel? recipe = Selected;
        string? relative = SelectedTargetFile;

        if (project is null || recipe is null || string.IsNullOrEmpty(relative))
        {
            return;
        }

        string path = Path.Combine(project.ProjectDir, relative.Replace('/', Path.DirectorySeparatorChar));

        string existing;
        try
        {
            existing = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = "файл не прочитан: " + Short(ex);
            return;
        }

        string newLine = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string block = BuildBlock(recipe, RenderedCode, newLine);

        // Правило WOT015: Python 2.7 отказывается компилировать файл с не-ASCII символом,
        // если в первых двух строках нет объявления кодировки. Тело мода компилируется уже
        // ВНУТРИ клиента, поэтому ошибка вылезет одной строкой в python.log и мод будет мёртв.
        bool codingProblem = RecipeRenderer.HasNonAscii(block)
                             && !RecipeRenderer.HasCodingDeclaration(existing);

        string warning = codingProblem
            ? newLine + "ВНИМАНИЕ: в коде есть кириллица, а в первых двух строках файла нет" + newLine
              + "строки  # -*- coding: utf-8 -*-  — Python 2.7 откажется компилировать такой" + newLine
              + "файл прямо в клиенте (правило WOT015). Добавьте её сами после вставки." + newLine
            : string.Empty;

        // Имена рецептов ПЕРЕСЕКАЮТСЯ намеренно (subscribe/unsubscribe — в четырёх рецептах,
        // register — в трёх, _safe — в двух). Модуль исполняется сверху вниз, поэтому второе
        // объявление молча съедает первое: R05 + R06 в одном main.py дают файл, где три
        // подписки R05 не выполняются НИКОГДА, а check_py2_syntax при этом отвечает [OK].
        IReadOnlyList<string> clashes = RecipeRenderer.NameClashes(block, existing);
        if (clashes.Count > 0)
        {
            warning += newLine
                       + "ВНИМАНИЕ: имена верхнего уровня уже объявлены в этом файле:" + newLine
                       + "    " + string.Join(", ", clashes) + newLine
                       + "Модуль исполняется сверху вниз, поэтому в рантайме останется только" + newLine
                       + "ПОСЛЕДНЕЕ объявление, а прежнее перестанет работать молча — ни" + newLine
                       + "синтаксическая проверка, ни python.log про это не скажут ничего." + newLine
                       + "Переименуйте одно из них после вставки (правило WOT022)." + newLine;
        }

        // Точка входа компилируется в .pyc самим build.py через tools/py27c.py, а тот не
        // поддерживает замыкания — их используют 7 рецептов из 16, и сборка упадёт
        // с Py27CompileError, которая на рецепт никак не сошлётся.
        if (RecipeRenderer.IsEntryPointFile(relative))
        {
            warning += newLine
                       + "ВНИМАНИЕ: выбрана ТОЧКА ВХОДА мода, а не его тело." + newLine
                       + "Загрузчик клиента импортирует только mod_*.pyc из gui/mods/, и этот" + newLine
                       + "файл build.py компилирует сам через tools/py27c.py — компилятор без" + newLine
                       + "поддержки замыканий. 7 рецептов из 16 замыкания используют, и сборка" + newLine
                       + "упадёт с Py27CompileError, не сославшись на рецепт." + newLine
                       + "Правильный приёмник — тело мода: gui/mods/<пакет>/main.py." + newLine;
        }

        string preview = Preview(block, 40);

        if (!ConfirmHandler(
                "Вставить рецепт в файл",
                $"В КОНЕЦ файла проекта будет дописано {block.Length} символов:{newLine}{newLine}" +
                $"    {path}{newLine}{newLine}" +
                $"Рядом останется копия {Path.GetFileName(path)}.bak.{newLine}" +
                warning + newLine +
                "Будет добавлено:" + newLine +
                "----------------------------------------" + newLine +
                preview + newLine +
                "----------------------------------------" + newLine + newLine +
                "Продолжить?"))
        {
            StatusText = "вставка отменена";
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Copy(path, path + ".bak", overwrite: true);
            }
            else
            {
                string? dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            string tail = existing.Length == 0 || existing.EndsWith('\n') ? string.Empty : newLine;

            // UTF-8 БЕЗ BOM: py2.7 терпит BOM в начале .py, но tools/check_py2_syntax.py и
            // компилятор байткода читают файл как обычный текст, и лишние три байта в начале
            // модуля мода не нужны никому.
            File.WriteAllText(path, existing + tail + block, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch (Exception ex)
        {
            StatusText = "вставка не удалась: " + Short(ex);
            return;
        }

        StatusText = $"{recipe.Id} дописан в {relative}"
                     + (codingProblem ? " · проверьте строку coding в начале файла" : string.Empty)
                     + (clashes.Count > 0 ? $" · перекрыто имён: {clashes.Count} ({string.Join(", ", clashes)})" : string.Empty);

        try
        {
            _editor.OpenAt(path, CountLines(existing) + 1);
        }
        catch (Exception)
        {
            // Редактора может не быть — вставка от этого не становится неуспешной.
        }
    }

    private static string BuildBlock(RecipeItemViewModel recipe, string code, string newLine)
    {
        var sb = new StringBuilder();
        sb.Append(newLine).Append(newLine);
        sb.Append("# ").Append(new string('=', 74)).Append(newLine);
        sb.Append("# рецепт ").Append(recipe.Id).Append(" · ").Append(recipe.Title).Append(newLine);
        sb.Append("# вставлено WoT Mod Cockpit ")
          .Append(DateTime.Now.ToString("dd.MM.yyyy HH:mm"))
          .Append(newLine);
        sb.Append("# ").Append(new string('=', 74)).Append(newLine);
        sb.Append(code.Replace("\r\n", "\n").Replace("\n", newLine));

        if (!code.EndsWith('\n'))
        {
            sb.Append(newLine);
        }

        return sb.ToString();
    }

    private static string Preview(string block, int maxLines)
    {
        string[] lines = block.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= maxLines)
        {
            return string.Join(Environment.NewLine, lines);
        }

        return string.Join(Environment.NewLine, lines.Take(maxLines))
               + Environment.NewLine
               + $"… ещё {lines.Length - maxLines} строк";
    }

    private static int CountLines(string text)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        int n = 1;
        foreach (char c in text)
        {
            if (c == '\n')
            {
                n++;
            }
        }

        return n;
    }

    private bool CanShowSymbol() => Selected is not null && Selected.Card.Symbols.Count > 0;

    /// <summary>
    /// Показать в браузере API символ, на котором держится рецепт. Это второй мост к этапу 2:
    /// от «вот готовый код» к «вот где это объявлено в вашем клиенте».
    /// </summary>
    /// <param name="symbol">Символ либо <c>null</c> — тогда берётся первый у рецепта.</param>
    [RelayCommand(CanExecute = nameof(CanShowSymbol))]
    private void ShowSymbol(RecipeSymbol? symbol)
    {
        RecipeSymbol? target = symbol ?? Selected?.Card.Symbols.FirstOrDefault();
        if (target is null)
        {
            return;
        }

        ApiBrowserRequested?.Invoke(this, target);
    }

    private static bool DefaultConfirm(string caption, string body)
        => MessageBox.Show(
               body,
               caption,
               MessageBoxButton.YesNo,
               MessageBoxImage.Question,
               MessageBoxResult.No) == MessageBoxResult.Yes;

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private static string Short(Exception ex) => ex switch
    {
        NotImplementedException => "сервис Core ещё не реализован (заглушка)",
        _ => ex.Message
    };
}
