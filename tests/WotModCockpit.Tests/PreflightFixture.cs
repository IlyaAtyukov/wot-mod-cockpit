using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Preflight;

namespace WotModCockpit.Tests;

/// <summary>
/// Временное дерево проекта мода плюс временное дерево «клиента» — ровно то, что нужно
/// правилам preflight. Каталоги удаляются вместе с фикстурой.
/// </summary>
public sealed class PreflightFixture : IDisposable
{
    private readonly string _root;
    private readonly string _clientRoot;
    private ClientApiOracle? _oracle;
    private bool _clientBuilt;

    /// <summary>Создаёт пустые деревья.</summary>
    public PreflightFixture()
    {
        string id = Guid.NewGuid().ToString("N");
        _root = Path.Combine(Path.GetTempPath(), "wot-cockpit-preflight-" + id);
        _clientRoot = Path.Combine(Path.GetTempPath(), "wot-cockpit-fakeclient-" + id);
        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_clientRoot);
    }

    /// <summary>Корень проекта мода.</summary>
    public string ProjectDir => _root;

    /// <summary>Каталог исходников мода.</summary>
    public string SrcDir => Path.Combine(_root, "src");

    /// <summary>Кладёт файл мода. Путь — относительно корня проекта, прямые слэши.</summary>
    /// <param name="relativePath">Например <c>src/res/scripts/client/gui/mods/mod_x.py</c>.</param>
    /// <param name="text">Содержимое.</param>
    /// <returns>Абсолютный путь.</returns>
    public string AddModFile(string relativePath, string text)
    {
        string full = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        return full;
    }

    /// <summary>Кладёт точку входа мода в каноническое место загрузчика.</summary>
    /// <param name="name">Имя без расширения, например <c>mod_demo</c>.</param>
    /// <param name="text">Содержимое.</param>
    /// <returns>Абсолютный путь.</returns>
    public string AddEntryPoint(string name, string text) =>
        AddModFile($"src/res/scripts/client/gui/mods/{name}.py", text);

    /// <summary>Кладёт модуль в пакет мода.</summary>
    /// <param name="package">Имя пакета.</param>
    /// <param name="module">Имя модуля без расширения.</param>
    /// <param name="text">Содержимое.</param>
    /// <returns>Абсолютный путь.</returns>
    public string AddPackageModule(string package, string module, string text) =>
        AddModFile($"src/res/scripts/client/gui/mods/{package}/{module}.py", text);

    /// <summary>Кладёт файл в поддельное дерево клиента (<c>client/…</c>, <c>common/…</c>).</summary>
    /// <param name="relativePath">Например <c>client/gui/SystemMessages.py</c>.</param>
    /// <param name="text">Содержимое.</param>
    public void AddClientFile(string relativePath, string text)
    {
        string full = Path.Combine(_clientRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        _clientBuilt = false;
        _oracle = null;
    }

    /// <summary>
    /// Оракул по поддельному дереву клиента. Дополнительный список пакетов игры пустой:
    /// в тестах он только мешал бы предсказуемости.
    /// </summary>
    /// <returns>Оракул.</returns>
    public ClientApiOracle Oracle()
    {
        if (_oracle is not null && _clientBuilt) return _oracle;

        ApiIndex index = Directory.EnumerateFiles(_clientRoot, "*.py", SearchOption.AllDirectories).Any()
            ? new ApiIndexBuilder().Build(_clientRoot).Index
            : ApiIndex.Empty;

        _oracle = ClientApiOracle.Create(index, new SourceFileService(), Array.Empty<string>());
        _clientBuilt = true;
        return _oracle;
    }

    /// <summary>Собирает контекст по текущему состоянию дерева мода.</summary>
    /// <param name="options">Настройки; <c>null</c> = по умолчанию.</param>
    /// <returns>Контекст.</returns>
    public PreflightContext Context(PreflightOptions? options = null) =>
        PreflightAnalyzer.BuildContext(SrcDir, _root, Oracle(), null, options);

    /// <summary>Прогоняет ОДНО правило и возвращает его находки.</summary>
    /// <param name="rule">Правило.</param>
    /// <param name="options">Настройки.</param>
    /// <returns>Находки.</returns>
    public IReadOnlyList<PreflightDiagnostic> Run(IPreflightRule rule, PreflightOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var analyzer = new PreflightAnalyzer(new[] { rule });
        return analyzer.Analyze(Context(options)).Diagnostics;
    }

    /// <summary>Прогоняет весь набор правил.</summary>
    /// <param name="options">Настройки.</param>
    /// <returns>Результат.</returns>
    public PreflightResult RunAll(PreflightOptions? options = null) =>
        new PreflightAnalyzer().Analyze(Context(options));

    /// <summary>Удаляет временные деревья.</summary>
    public void Dispose()
    {
        Safe(_root);
        Safe(_clientRoot);

        static void Safe(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
