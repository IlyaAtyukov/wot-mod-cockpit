using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="PythonBuildRunner"/>.
/// <para>
/// ЖЁСТКОЕ ПРАВИЛО ЭТОГО ФАЙЛА: реально запускаются ТОЛЬКО безопасные действия
/// (<see cref="BuildAction.Build"/> и <see cref="BuildAction.List"/>), да и те с
/// <c>--dist</c>, переставленным во временный каталог. Действия
/// <see cref="BuildAction.BuildAndInstall"/> / <see cref="BuildAction.DevDeploy"/> /
/// <see cref="BuildAction.Uninstall"/> пишут в каталог игры и проверяются
/// ИСКЛЮЧИТЕЛЬНО разбором командной строки, без запуска процесса.
/// </para>
/// <para>Настоящие запуски требуют Python 3 (<c>WOTCOCKPIT_PYTHON</c> или автопоиск), а сборка
/// шаблона — ещё и сам шаблон мода; без них тесты пропускаются, а не «проходят» впустую.</para>
/// </summary>
public class BuildRunnerTests
{
    private readonly IBuildRunner _runner = new PythonBuildRunner();

    /// <summary>Проект-образец, не привязанный к диску: нужен только для разбора аргументов.</summary>
    private static ModProject SampleProject() => new()
    {
        ProjectDir = @"C:\proj\hello",
        ModId = "ru.local.hello_tanker",
        Version = "1.0.0",
        EntryPointModule = "mod_hello_tanker",
        PackageName = "hello_tanker",
        PythonExe = "python",
        GameDir = @"C:\Games\Tanki"
    };

    // ------------------------------------------------------------------ командная строка

    /// <summary>Обычная сборка: флага действия нет, <c>--clean</c> приходит из умолчаний.</summary>
    [Fact]
    public void Arguments_Build_HasNoActionFlag()
    {
        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(BuildAction.Build, SampleProject());

        Assert.Equal(@"C:\proj\hello\build.py", args[0]);
        Assert.Equal(@"C:\proj\hello\src", ValueAfter(args, "--src"));
        Assert.Equal(@"C:\proj\hello\dist", ValueAfter(args, "--dist"));
        Assert.Equal(@"C:\Games\Tanki", ValueAfter(args, "--game-dir"));
        Assert.Contains("--clean", args);
        Assert.DoesNotContain("--install", args);
        Assert.DoesNotContain("--dev", args);
        Assert.DoesNotContain("--uninstall", args);
        Assert.DoesNotContain("--list", args);
    }

    /// <summary>Каждому действию — ровно один флаг скрипта, и ни одного лишнего.</summary>
    [Theory]
    [InlineData(BuildAction.BuildAndInstall, "--install")]
    [InlineData(BuildAction.DevDeploy, "--dev")]
    [InlineData(BuildAction.Uninstall, "--uninstall")]
    [InlineData(BuildAction.List, "--list")]
    public void Arguments_ActionFlagIsExclusive(BuildAction action, string expectedFlag)
    {
        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(action, SampleProject());

        Assert.Contains(expectedFlag, args);

        foreach (string other in new[] { "--install", "--dev", "--uninstall", "--list" })
        {
            if (other != expectedFlag)
            {
                Assert.DoesNotContain(other, args);
            }
        }
    }

    /// <summary>
    /// Умолчания по действиям: <c>--clean</c> ставится только там, где результат кладётся в <c>dist\</c>.
    /// Для <c>--dev</c>/<c>--uninstall</c>/<c>--list</c> чистить dist смысла нет.
    /// </summary>
    [Theory]
    [InlineData(BuildAction.Build, true)]
    [InlineData(BuildAction.BuildAndInstall, true)]
    [InlineData(BuildAction.DevDeploy, false)]
    [InlineData(BuildAction.Uninstall, false)]
    [InlineData(BuildAction.List, false)]
    public void Arguments_DefaultCleanPerAction(BuildAction action, bool expectClean)
    {
        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(action, SampleProject());

        Assert.Equal(expectClean, args.Contains("--clean"));
    }

    /// <summary>Явно переданные опции уважаются как есть и умолчаниями не перебиваются.</summary>
    [Fact]
    public void Arguments_ExplicitOptionsWin()
    {
        BuildRunOptions options = new()
        {
            Clean = false,
            NoSyntaxCheck = true,
            CompileAll = true,
            KeepPy = true,
            PrintListing = true,
            Python27Path = @"C:\Python27\python.exe",
            GameDirOverride = @"E:\Fake\WoT",
            AdditionalArgs = ["--no-dir-entries"]
        };

        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(BuildAction.Build, SampleProject(), options);

        Assert.DoesNotContain("--clean", args);
        Assert.Contains("--no-syntax-check", args);
        Assert.Contains("--compile-all", args);
        Assert.Contains("--keep-py", args);
        Assert.Contains("--list", args);
        Assert.Contains("--no-dir-entries", args);
        Assert.Equal(@"C:\Python27\python.exe", ValueAfter(args, "--py27"));
        Assert.Equal(@"E:\Fake\WoT", ValueAfter(args, "--game-dir"));
    }

    /// <summary><c>--list</c> как основное действие не должен дублироваться из <c>PrintListing</c>.</summary>
    [Fact]
    public void Arguments_ListAction_DoesNotDuplicateListFlag()
    {
        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(
            BuildAction.List, SampleProject(), new BuildRunOptions { PrintListing = true });

        Assert.Single(args, a => a == "--list");
    }

    /// <summary>Пустой <c>GameDir</c> = флаг вообще не передаётся, build.py возьмёт свой дефолт.</summary>
    [Fact]
    public void Arguments_EmptyGameDir_OmitsFlag()
    {
        ModProject p = SampleProject();
        p.GameDir = string.Empty;

        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(BuildAction.Build, p);

        Assert.DoesNotContain("--game-dir", args);
    }

    /// <summary>Постоянные флаги проекта дописываются в хвост.</summary>
    [Fact]
    public void Arguments_ExtraBuildArgsAreAppended()
    {
        ModProject p = SampleProject();
        p.ExtraBuildArgs = ["--no-dir-entries", "--name", "custom"];

        IReadOnlyList<string> args = PythonBuildRunner.BuildArgumentList(BuildAction.Build, p);

        Assert.Contains("--no-dir-entries", args);
        Assert.Equal("custom", ValueAfter(args, "--name"));
    }

    /// <summary>Пути с пробелами обязаны попасть в эхо командной строки в кавычках.</summary>
    [Fact]
    public void CommandLine_QuotesPathsWithSpaces()
    {
        ModProject p = SampleProject();
        p.ProjectDir = @"C:\Users\dev\my mods\hello";

        string cmd = PythonBuildRunner.BuildCommandLine(
            p.PythonExe, PythonBuildRunner.BuildArgumentList(BuildAction.Build, p));

        Assert.Contains(@"""C:\Users\dev\my mods\hello\build.py""", cmd);
        Assert.StartsWith("python ", cmd);
    }

    // ------------------------------------------------------------------ разбор вывода

    /// <summary>Путь артефакта берётся из настоящей строки, которую печатает build.py.</summary>
    [Fact]
    public void ArtifactPath_ParsedFromRealOkLine()
    {
        const string line =
            @"[build] OK   C:\Users\dev\source\repos\wot-mod-template\dist\ru.local.hello_tanker_1.0.0.mtmod (62848 bytes, 10 entries, all Stored)";

        Assert.Equal(
            @"C:\Users\dev\source\repos\wot-mod-template\dist\ru.local.hello_tanker_1.0.0.mtmod",
            PythonBuildRunner.TryParseArtifactPath(line));
    }

    /// <summary>Прочие строки <c>[build] OK</c> артефактом не являются.</summary>
    [Theory]
    [InlineData("[build] OK   python 2.7 syntax check passed (tools/check_py2_syntax.py)")]
    [InlineData("[build] finished clean")]
    [InlineData("[build] not installed (pass --install to copy into C:\\Games\\Tanki\\mods\\1.44.0.0)")]
    [InlineData("")]
    public void ArtifactPath_IgnoresOtherLines(string line) =>
        Assert.Null(PythonBuildRunner.TryParseArtifactPath(line));

    // ------------------------------------------------------------------ реальные запуски (безопасные)

    /// <summary>
    /// Настоящий запуск сборки шаблона. В игру НЕ пишет: действие <see cref="BuildAction.Build"/>,
    /// а <c>--dist</c> отведён во временный каталог, поэтому и <c>dist\</c> шаблона не трогается.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task RunAsync_Build_ProducesMtmodAndParsesArtifactPath()
    {

        using CoreTempDir tmp = new("wotcockpit-dist");
        ModProject p = TemplateProjectWithDist(tmp);

        List<BuildOutputLine> lines = [];
        BuildResult result = await _runner.RunAsync(
            BuildAction.Build, p, null, new Progress<BuildOutputLine>(lines.Add));

        string dump = string.Join(Environment.NewLine, lines.Select(l => $"{l.Stream}: {l.Text}"));

        Assert.True(result.Success, dump);
        Assert.Equal(0, result.ExitCode);
        Assert.EndsWith(".mtmod", result.ArtifactPath);
        Assert.True(File.Exists(result.ArtifactPath), result.ArtifactPath);
        Assert.StartsWith(tmp.Path, result.ArtifactPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Duration > TimeSpan.Zero);
        Assert.Null(result.FailureReason);

        // Эхо команды идёт служебной строкой, вывод скрипта — stdout.
        Assert.Contains(lines, l => l.Stream == BuildStreamKind.Diagnostic && l.Text.Contains("build.py"));
        Assert.Contains(lines, l => l.Stream == BuildStreamKind.Stdout && l.Text.StartsWith("[build]"));

        // И ничего не уехало в игру — ту самую, которую build.py получил в --game-dir.
        Assert.Contains("--game-dir", result.CommandLine, StringComparison.Ordinal);
        Assert.Contains(Path.Combine(tmp.Path, "game"), result.CommandLine, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "game", "mods")));
        Assert.False(Directory.Exists(Path.Combine(tmp.Path, "game", "res_mods")));
    }

    /// <summary><c>--list</c> печатает листинг архива и в игру не пишет.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task RunAsync_List_PrintsArchiveListing()
    {

        using CoreTempDir tmp = new("wotcockpit-dist");
        ModProject p = TemplateProjectWithDist(tmp);

        List<BuildOutputLine> lines = [];
        BuildResult result = await _runner.RunAsync(
            BuildAction.List, p, null, new Progress<BuildOutputLine>(lines.Add));

        string dump = string.Join(Environment.NewLine, lines.Select(l => $"{l.Stream}: {l.Text}"));

        Assert.True(result.Success, dump);
        Assert.Contains(lines, l => l.Text.Contains("mod_hello_tanker.pyc", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Поток строк отдаётся по мере поступления и перечисляется РОВНО один раз.</summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public async Task Start_ReadOutputAsync_IsSingleUse()
    {

        using CoreTempDir tmp = new("wotcockpit-dist");
        ModProject p = TemplateProjectWithDist(tmp);

        await using IBuildRun run = _runner.Start(BuildAction.Build, p);

        Assert.Contains("build.py", run.CommandLine);

        int count = 0;
        await foreach (BuildOutputLine _ in run.ReadOutputAsync())
        {
            count++;
        }

        Assert.True(count > 0);

        BuildResult result = await run.Completion;
        Assert.True(result.Success);

        // Повторное перечисление живого канала запрещено контрактом IBuildRun.
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (BuildOutputLine _ in run.ReadOutputAsync())
            {
                // до тела не дойдёт
            }
        });
    }

    /// <summary>
    /// Нет python — <see cref="BuildResult.ExitCodeLaunchFailed"/>, а не исключение.
    /// Шаблон не нужен: до запуска скрипта дело не доходит, хватает заглушки build.py.
    /// </summary>
    [Fact]
    public async Task RunAsync_MissingPython_ReportsLaunchFailure()
    {
        using CoreTempDir tmp = new("wotcockpit-dist");
        tmp.WriteFile("build.py", "print('[build] stub')\n");
        ModProject p = new()
        {
            ProjectDir = tmp.Path,
            ModId = "x",
            PythonExe = "python-that-does-not-exist-" + Guid.NewGuid().ToString("N")
        };

        List<BuildOutputLine> lines = [];
        BuildResult result = await _runner.RunAsync(
            BuildAction.Build, p, null, new Progress<BuildOutputLine>(lines.Add));

        Assert.False(result.Success);
        Assert.Equal(BuildResult.ExitCodeLaunchFailed, result.ExitCode);
        Assert.Equal(string.Empty, result.ArtifactPath);
        Assert.False(string.IsNullOrEmpty(result.FailureReason));
        Assert.Contains(lines, l => l.Stream == BuildStreamKind.Diagnostic);
    }

    /// <summary>Нет build.py — тоже провал запуска, процесс даже не стартует.</summary>
    [Fact]
    public async Task RunAsync_MissingBuildScript_ReportsLaunchFailure()
    {
        using CoreTempDir tmp = new();
        ModProject p = new() { ProjectDir = tmp.Path, ModId = "x", PythonExe = "python" };

        BuildResult result = await _runner.RunAsync(BuildAction.Build, p, null, null);

        Assert.False(result.Success);
        Assert.Equal(BuildResult.ExitCodeLaunchFailed, result.ExitCode);
        Assert.Contains("build.py", result.FailureReason!);
    }

    /// <summary>
    /// Отмена убивает процесс и даёт <see cref="BuildResult.ExitCodeCancelled"/>,
    /// а не <see cref="OperationCanceledException"/>. Скрипт — заведомо долгий, но безобидный.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public async Task RunAsync_Cancelled_KillsProcessAndReportsCancellation()
    {
        string python = TestPython.Require();
        using CoreTempDir tmp = new();
        tmp.WriteFile("build.py", "import time\nprint('[build] started', flush=True)\ntime.sleep(120)\n");

        ModProject p = new() { ProjectDir = tmp.Path, ModId = "x", PythonExe = python };

        using CancellationTokenSource cts = new();
        List<BuildOutputLine> lines = [];

        Task<BuildResult> task = _runner.RunAsync(
            BuildAction.Build, p, null, new Progress<BuildOutputLine>(l => { lock (lines) { lines.Add(l); } }), cts.Token);

        // Дать процессу подняться и напечатать первую строку.
        await Task.Delay(TimeSpan.FromSeconds(3));
        cts.Cancel();

        BuildResult result = await task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(result.Success);
        Assert.Equal(BuildResult.ExitCodeCancelled, result.ExitCode);
        Assert.Equal(string.Empty, result.ArtifactPath);
        Assert.False(string.IsNullOrEmpty(result.FailureReason));
    }

    /// <summary>Таймаут — тот же путь, что и отмена пользователем.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public async Task RunAsync_Timeout_ReportsCancellation()
    {
        string python = TestPython.Require();
        using CoreTempDir tmp = new();
        tmp.WriteFile("build.py", "import time\ntime.sleep(120)\n");

        ModProject p = new() { ProjectDir = tmp.Path, ModId = "x", PythonExe = python };

        BuildResult result = await _runner
            .RunAsync(BuildAction.Build, p, new BuildRunOptions { Timeout = TimeSpan.FromSeconds(3) }, null)
            .WaitAsync(TimeSpan.FromSeconds(40));

        Assert.False(result.Success);
        Assert.Equal(BuildResult.ExitCodeCancelled, result.ExitCode);
        Assert.Contains("таймаут", result.FailureReason!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Ненулевой код возврата — провал с сохранением кода.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public async Task RunAsync_NonZeroExitCode_IsFailure()
    {
        string python = TestPython.Require();
        using CoreTempDir tmp = new();
        tmp.WriteFile("build.py", "import sys\nsys.stderr.write('boom\\n')\nsys.exit(3)\n");

        ModProject p = new() { ProjectDir = tmp.Path, ModId = "x", PythonExe = python };

        List<BuildOutputLine> lines = [];
        BuildResult result = await _runner.RunAsync(
            BuildAction.Build, p, null, new Progress<BuildOutputLine>(l => { lock (lines) { lines.Add(l); } }));

        Assert.False(result.Success);
        Assert.Equal(3, result.ExitCode);
        Assert.Contains(lines, l => l.Stream == BuildStreamKind.Stderr && l.Text.Contains("boom"));
    }

    /// <summary>
    /// Кириллица из stdout скрипта не должна биться о кодировку консоли. UTF-8 для дочернего
    /// процесса включает сам <see cref="PythonBuildRunner"/>, поэтому тест его НЕ включает.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public async Task RunAsync_KeepsCyrillicOutput()
    {
        string python = TestPython.Require();
        using CoreTempDir tmp = new();
        tmp.WriteFile("build.py", "print('[build] сборка завершена')\n");

        ModProject p = new() { ProjectDir = tmp.Path, ModId = "x", PythonExe = python };

        List<BuildOutputLine> lines = [];
        BuildResult result = await _runner.RunAsync(
            BuildAction.Build, p, null, new Progress<BuildOutputLine>(l => { lock (lines) { lines.Add(l); } }));

        Assert.True(result.Success);
        Assert.Contains(lines, l => l.Text.Contains("сборка завершена"));
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>
    /// Проект шаблона, у которого <c>dist</c> и каталог игры переставлены во временный каталог:
    /// так реальные запуски не трогают ни <c>dist\</c> самого шаблона, ни настоящую установку.
    /// Пустой <c>gameDir</c> не годится: без <c>--game-dir</c> build.py берёт свой каталог игры по
    /// умолчанию и даже при обычной сборке читает его <c>paths.xml</c> и все <c>.mtmod</c> в
    /// <c>mods\&lt;версия&gt;</c>. Пустая «игра» без <c>paths.xml</c> — предупреждение и запасная
    /// версия клиента, код возврата 0. Каталог игры лежит РЯДОМ с <c>dist</c>, а не внутри:
    /// <c>--clean</c> стирает <c>dist</c> целиком.
    /// Шаблона или Python нет — тест пропускается.
    /// </summary>
    /// <param name="tmp">Временный каталог под dist и «игру».</param>
    /// <returns>Проект.</returns>
    private static ModProject TemplateProjectWithDist(CoreTempDir tmp)
    {
        string templateDir = TestEnvironment.RequireTemplate();
        string python = TestPython.Require();

        ModProject p = new JsonProjectStore().Load(templateDir)!;
        p.DistDirRelative = Path.Combine(tmp.Path, "dist");   // абсолютный путь: Path.Combine вернёт его как есть
        p.GameDir = Directory.CreateDirectory(Path.Combine(tmp.Path, "game")).FullName;
        p.EffectiveGameDir = string.Empty;
        p.PythonExe = python;
        return p;
    }

    /// <summary>Значение, идущее следом за флагом.</summary>
    /// <param name="args">Аргументы.</param>
    /// <param name="flag">Флаг.</param>
    /// <returns>Значение либо <c>null</c>.</returns>
    private static string? ValueAfter(IReadOnlyList<string> args, string flag)
    {
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == flag)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
