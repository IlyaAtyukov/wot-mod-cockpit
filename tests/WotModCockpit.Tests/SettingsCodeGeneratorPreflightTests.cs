using System.Text;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Preflight;
using WotModCockpit.Core.Settings;
using ClientIndex = WotModCockpit.Core.ApiIndex.ApiIndex;

namespace WotModCockpit.Tests;

/// <summary>
/// Сгенерированный код проходит СОБСТВЕННЫЙ preflight кокпита — тот самый
/// <see cref="IPreflightService"/> этапа 3, со всеми 22 правилами.
///
/// <para>Это не самопроверка ради галочки. Дизайнер порождает код, который уедет в чужую игру;
/// если кокпит сам находит в нём ошибки, доверять ему нельзя. Отдельно важно WOT018: импорт
/// ModsSettingsAPI обязан быть обёрнут, а мод — работать, когда чужого мода не установлено.</para>
/// </summary>
public class SettingsCodeGeneratorPreflightTests : IDisposable
{
    private readonly List<string> _temps = [];

    /// <summary>Убирает временные деревья.</summary>
    public void Dispose()
    {
        foreach (string dir in _temps)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Каждая из пяти форм, положенная в дерево мода, не даёт НИ ОДНОЙ находки preflight
    /// на своих двух файлах — ни ошибки, ни предупреждения, ни замечания.
    /// </summary>
    [Fact]
    public async Task EverySample_ProducesNoPreflightFindings()
    {
        foreach ((string name, SettingsTemplate template) in SettingsDesignSamples.All())
        {
            (ModProject project, GeneratedSettingsCode code) = BuildModTree(name, template);

            PreflightResult result = await Service().AnalyzeAsync(project);

            IReadOnlyList<PreflightDiagnostic> mine =
            [
                .. result.Diagnostics.Where(d =>
                    d.FilePath.EndsWith(code.SchemaFileName, StringComparison.OrdinalIgnoreCase) ||
                    d.FilePath.EndsWith(code.UserFileName, StringComparison.OrdinalIgnoreCase))
            ];

            Assert.True(mine.Count == 0,
                $"[{name}] preflight нашёл в сгенерированном коде:{Environment.NewLine}" +
                string.Join(Environment.NewLine, mine.Select(d => d.ToString())));

            Assert.True(result.ErrorCount == 0,
                $"[{name}] ошибки preflight по всему дереву:{Environment.NewLine}" +
                string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));

            Assert.True(result.FilesAnalyzed >= 4, $"[{name}] разобрано файлов: {result.FilesAnalyzed}");
        }
    }

    /// <summary>
    /// WOT018 отдельно и прицельно: правило, ради которого дизайнер вообще существует в таком
    /// виде. Импорт чужого мода в <c>settings.py</c> обёрнут, имя объявлено до <c>try</c>,
    /// и есть проверка на <c>None</c> — значит без установленного ModsSettingsAPI мод
    /// продолжит работать на своём JSON, а не свалится с <c>ImportError</c>.
    /// </summary>
    [Fact]
    public async Task ForeignApiImport_DoesNotTriggerWot018()
    {
        (ModProject project, _) = BuildModTree("wot018", SettingsDesignSamples.AllControlTypes());

        PreflightResult result = await Service().AnalyzeAsync(project);

        IReadOnlyList<PreflightDiagnostic> wot018 =
            [.. result.Diagnostics.Where(d => d.RuleId == "WOT018")];

        Assert.True(wot018.Count == 0,
            "WOT018 сработало на сгенерированном коде:" + Environment.NewLine +
            string.Join(Environment.NewLine, wot018.Select(d => d.ToString())));
    }

    /// <summary>
    /// Контрольный опыт: если убрать <c>try/except ImportError</c>, WOT018 обязано сработать.
    /// Без него предыдущий тест ничего не доказывал бы — правило могло просто молчать всегда.
    /// </summary>
    [Fact]
    public async Task NakedForeignApiImport_DoesTriggerWot018()
    {
        (ModProject project, GeneratedSettingsCode code) = BuildModTree("naked", SettingsDesignSamples.Minimal());

        string userFile = Path.Combine(PackageDir(project), code.UserFileName);
        string text = File.ReadAllText(userFile, Encoding.UTF8);

        // Разворачиваем защищённый импорт в голый, на уровне модуля.
        text = "from gui.modsSettingsApi import g_modsSettingsApi\n" + text;
        File.WriteAllText(userFile, text, new UTF8Encoding(false));

        PreflightResult result = await Service().AnalyzeAsync(project);

        Assert.Contains(result.Diagnostics, d => d.RuleId == "WOT018");
    }

    /// <summary>
    /// Дерево эталонного шаблона мода ПЛЮС сгенерированные настройки — вместе, как это и будет
    /// у пользователя. На эталоне ошибок нет по определению (это проверяет этап 3), поэтому
    /// любая находка здесь принадлежит дизайнеру.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    public async Task ReferenceTemplateWithGeneratedSettings_StaysClean()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(PreflightRealModTests.TemplateDir, "src")),
            $"нет эталонного шаблона ({PreflightRealModTests.TemplateDir})");

        string root = NewTemp("cockpit-settings-template");
        CopyDirectory(Path.Combine(PreflightRealModTests.TemplateDir, "src"), Path.Combine(root, "src"));

        var project = new ModProject
        {
            ProjectDir = root,
            ModId = "ru.local.hello_tanker",
            DisplayName = "Hello Tanker",
            PackageName = "hello_tanker",
            EntryPointModule = "mod_hello_tanker",
            LogTag = "[HelloTanker"
        };

        var design = SettingsDesign.ForProject(project);
        design.Template = SettingsDesignSamples.AllControlTypes();
        design.Template.Linkage = project.ModId;

        SettingsApplyResult applied = new SettingsDesignerService().Apply(project, design);
        Assert.True(applied.Success, applied.Summary);

        // Подключаем модуль так, как это описано в подсказке дизайнера.
        string mainPy = Path.Combine(PackageDir(project), "main.py");
        File.AppendAllText(mainPy,
            "\n\nimport settings as _cockpit_settings\n" +
            "\n\ndef _cockpit_settings_ready():\n" +
            "    \"\"\"Звать из onAccountShowGUI.\"\"\"\n" +
            "    return _cockpit_settings.register()\n",
            new UTF8Encoding(false));

        PreflightResult result = await Service().AnalyzeAsync(project);

        Assert.True(result.ErrorCount == 0,
            "ошибки preflight на эталоне с настройками:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));

        Assert.True(result.WarningCount == 0,
            "предупреждения preflight на эталоне с настройками:" + Environment.NewLine +
            string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.ToString())));
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>
    /// Служба preflight с реальным индексом клиента, если дамп исходников на месте.
    /// Без дампа правила WOT002/003/004 молчат, остальные 19 работают как обычно.
    /// </summary>
    private static IPreflightService Service()
    {
        ClientIndex index = RealWotSource.Available ? RealWotSource.Index : ClientIndex.Empty;
        return new PreflightService(new SnapshotIndexService(index), new SourceFileService());
    }

    private static string PackageDir(ModProject project) =>
        Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods", project.PackageName);

    /// <summary>
    /// Синтетическое дерево мода: точка входа, пакет, тело и сгенерированные настройки.
    /// Точка входа повторяет идиому эталонного шаблона (весь код внутри <c>try/except</c>),
    /// чтобы находки принадлежали дизайнеру, а не подделке вокруг него.
    /// </summary>
    private (ModProject Project, GeneratedSettingsCode Code) BuildModTree(string name, SettingsTemplate template)
    {
        string root = NewTemp("cockpit-settings-tree-" + name);

        var project = new ModProject
        {
            ProjectDir = root,
            ModId = template.Linkage,
            DisplayName = template.ModDisplayName,
            PackageName = "hello_tanker",
            EntryPointModule = "mod_hello_tanker",
            LogTag = "[HelloTanker"
        };

        string modsDir = Path.Combine(project.SrcDir, "res", "scripts", "client", "gui", "mods");
        string packageDir = Path.Combine(modsDir, project.PackageName);
        Directory.CreateDirectory(packageDir);

        // Завершающий перевод строки обязателен: compile() в Python 2.7 без него даёт
        // «unexpected EOF while parsing», а тело мода компилируется уже внутри клиента.
        Write(Path.Combine(modsDir, "mod_hello_tanker.py"), EntryPointStub + LineEnd);
        Write(Path.Combine(packageDir, "__init__.py"), PackageInit + LineEnd);
        Write(Path.Combine(packageDir, "main.py"), MainModule + LineEnd);

        var design = SettingsDesign.ForProject(project);
        design.Template = template;
        design.Code = SettingsDesignSamples.Options() with { PackageName = project.PackageName };

        SettingsApplyResult applied = new SettingsDesignerService().Apply(project, design);
        Assert.True(applied.Success, $"[{name}] {applied.Summary}");
        Assert.NotNull(applied.Code);

        return (project, applied.Code!);
    }

    private string NewTemp(string prefix)
    {
        string dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _temps.Add(dir);
        return dir;
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            if (dir.Contains("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        }

        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            if (file.Contains("__pycache__", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
        }
    }

    /// <summary>Завершающий перевод строки для питоновских заготовок.</summary>
    private const string LineEnd = "\n";

    private const string EntryPointStub = """
        # -*- coding: utf-8 -*-
        # Точка входа мода: тонкий стаб, весь код в пакете.
        import sys

        ENTRY = 'gui.mods.hello_tanker.main'

        _impl = None


        def _call(name, args):
            if _impl is None:
                return
            handler = getattr(_impl, name, None)
            if handler is None:
                return
            try:
                handler(*args)
            except Exception:
                import debug_utils
                debug_utils.LOG_CURRENT_EXCEPTION()


        def init():
            global _impl
            try:
                __import__(ENTRY)
                _impl = sys.modules[ENTRY]
            except Exception:
                _impl = None
            _call('init', ())


        def fini():
            _call('fini', ())


        def onAccountShowGUI(ctx):
            _call('onAccountShowGUI', (ctx,))
        """;

    private const string PackageInit = """
        # -*- coding: utf-8 -*-
        # Пакет с телом мода.
        """;

    private const string MainModule = """
        # -*- coding: utf-8 -*-
        # Тело мода: сюда дизайнер настроек не пишет.
        import logging
        import traceback

        import settings

        LOG_PREFIX = '[HelloTanker]'
        _logger = logging.getLogger('HelloTanker')


        def init():
            try:
                _logger.info('%s init', LOG_PREFIX)
            except Exception:
                _logger.error('%s init failed:\n%s', LOG_PREFIX, traceback.format_exc())


        def fini():
            try:
                settings.unsubscribe(_on_settings)
            except Exception:
                _logger.error('%s fini failed:\n%s', LOG_PREFIX, traceback.format_exc())


        def onAccountShowGUI(ctx):
            try:
                settings.register()
                settings.subscribe(_on_settings)
            except Exception:
                _logger.error('%s onAccountShowGUI failed:\n%s', LOG_PREFIX, traceback.format_exc())


        def _on_settings(values):
            _logger.info('%s settings applied: %s', LOG_PREFIX, values)
        """;

    /// <summary>Минимальная подделка службы индекса: preflight нужен только снимок.</summary>
    private sealed class SnapshotIndexService(ClientIndex index) : IApiIndexService
    {
        public ClientIndex Index { get; } = index;

        public ApiIndexBuildStatus Status => ApiIndexBuildStatus.NotBuilt;

        public IReadOnlyList<ApiIndexBuildError> Errors => [];

        public event EventHandler<ApiIndexBuildStatus>? StatusChanged { add { } remove { } }

        public Task<ApiIndexBuildStatus> RebuildAsync(string? sourceRoot = null,
            IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public Task<ApiIndexBuildStatus> EnsureBuiltAsync(string? sourceRoot = null,
            IProgress<ApiIndexProgress>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Status);

        public void Cancel() { }

        public IReadOnlyList<ApiSymbol> Search(string? query, SymbolSearchOptions? options = null) => [];

        public Task<XrefResult> FindReferencesAsync(XrefQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ApiSymbol? ResolveTracebackFrame(string? virtualPath, int line) => null;

        public SourceSnippet? GetSnippet(ApiSymbol symbol, int before = 0, int after = 12) => null;

        public IReadOnlyList<ApiSymbol> Children(ApiSymbol symbol) => [];
    }
}
