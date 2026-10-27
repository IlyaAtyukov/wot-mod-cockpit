using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;

namespace WotModCockpit.Tests;

/// <summary>
/// Дымовые тесты каркаса: решение собирается, DI резолвит все контракты,
/// заглушки честно сообщают о нереализованности.
/// </summary>
public class SmokeTests
{
    /// <summary>Все контракты Core должны резолвиться из контейнера.</summary>
    [Theory]
    [InlineData(typeof(IGameInstallLocator))]
    [InlineData(typeof(IProjectStore))]
    [InlineData(typeof(IBuildRunner))]
    [InlineData(typeof(ILogParser))]
    [InlineData(typeof(ILogTailerFactory))]
    [InlineData(typeof(ITracebackMapper))]
    [InlineData(typeof(IDeployProbe))]
    [InlineData(typeof(IEditorLauncher))]
    [InlineData(typeof(ISettingsStore))]
    public void CoreContainer_ResolvesEveryContract(Type contract)
    {
        using ServiceProvider provider = new ServiceCollection()
            .AddCockpitCore()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        object service = provider.GetRequiredService(contract);

        Assert.NotNull(service);
        Assert.True(contract.IsInstanceOfType(service));
    }

    /// <summary>Композиционный корень WPF-приложения собирается и проходит ValidateOnBuild.</summary>
    [Fact]
    public void AppServices_BuildServiceProvider_Succeeds()
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IBuildRunner>());
        Assert.NotNull(provider.GetRequiredService<ILogParser>());
    }

    /// <summary>
    /// Заглушек больше не осталось: каждый сервис из контейнера делает работу, а не бросает
    /// <c>NotImplementedException</c>. Тест закрывает именно этот регресс — на путях, которые
    /// исполняются при старте приложения.
    /// </summary>
    [Fact]
    public void Services_AreImplemented_NotStubs()
    {
        using ServiceProvider provider = new ServiceCollection().AddCockpitCore().BuildServiceProvider();

        // Бытовой отказ = null, а не исключение (см. IGameInstallLocator.Detect).
        Assert.Null(provider.GetRequiredService<IGameInstallLocator>()
            .Detect(Path.Combine(Path.GetTempPath(), "wot-cockpit-no-such-install")));

        Assert.NotNull(provider.GetRequiredService<ILogParser>()
            .Parse("2026-08-29 00:27:40.691: NOTICE: [NOTE] x"));

        // ISettingsStore.Load зовётся из MainViewModel.Initialize при каждом старте окна.
        Assert.NotNull(provider.GetRequiredService<ISettingsStore>().Load());
    }

    /// <summary>
    /// Настоящий контейнер в тестах не трогает настройки разработчика: хранилище по умолчанию
    /// смотрит в каталог из <c>WOTCOCKPIT_SETTINGS_DIR</c>, который тестовая сборка уводит во
    /// временный (<see cref="TestSettingsIsolation"/>). Там уже лежит пустой settings.json, поэтому
    /// загрузка не досеивает пути пробой машины: ни игры, ни шаблона, ни дерева исходников
    /// разработчика в «изолированных» настройках нет. На настоящей машине проверять нечего.
    /// </summary>
    [SkippableFact]
    public void AppServices_SettingsStore_IsIsolatedFromRealAppData()
    {
        Skip.If(TestSettingsIsolation.RealMachine, "на настоящей машине настройки не изолируются — проверять нечего");

        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        ISettingsStore store = provider.GetRequiredService<ISettingsStore>();
        string path = store.SettingsPath;

        string appData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Core.Services.JsonSettingsStore.AppFolderName);

        Assert.False(path.StartsWith(appData, StringComparison.OrdinalIgnoreCase), "тест читает настоящие настройки: " + path);
        Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);

        CockpitSettings loaded = store.Load();
        Assert.Equal(string.Empty, loaded.GameDir);
        Assert.Equal(string.Empty, loaded.TemplateDir);
        Assert.Equal(string.Empty, loaded.WotSrcDir);
    }

    /// <summary>Вычисляемые пути <see cref="ModProject"/> — чистая арифметика строк, она обязана работать уже сейчас.</summary>
    [Fact]
    public void ModProject_DerivedPaths_MatchTemplateLayout()
    {
        var project = new ModProject
        {
            ProjectDir = @"C:\Users\dev\source\repos\wot-mod-template",
            ModId = "ru.local.hello_tanker",
            Version = "1.0.0",
            EntryPointModule = "mod_hello_tanker",
            PackageName = "hello_tanker"
        };

        Assert.Equal(
            @"C:\Users\dev\source\repos\wot-mod-template\src\res\scripts\client\gui\mods\mod_hello_tanker.py",
            project.EntryPointSourcePath);

        Assert.Equal("scripts/client/gui/mods/mod_hello_tanker.py", project.EntryPointVirtualPath);
        Assert.Equal("scripts/client/gui/mods/hello_tanker/", project.PackageVirtualPrefix);

        Assert.Equal(
            @"C:\Users\dev\source\repos\wot-mod-template\dist\ru.local.hello_tanker_1.0.0.mtmod",
            project.ExpectedArtifactPath);

        Assert.Equal(@"C:\Users\dev\source\repos\wot-mod-template\build.py", project.BuildScriptPath);
    }

    /// <summary>
    /// Каталог игры и Python этой машины в <c>wotmod.json</c> не попадают: вычисленные значения
    /// живут в отдельных свойствах, которые не сериализуются. Иначе проект, закоммиченный одним
    /// разработчиком, у другого собирался бы в чужой несуществующий каталог.
    /// </summary>
    [Fact]
    public void ModProject_EffectiveValues_AreNotSerialized()
    {
        var project = new ModProject
        {
            ModId = "ru.example.alpha",
            EffectiveGameDir = @"C:\Games\Tanki",
            EffectivePythonExe = @"C:\Python312\python.exe"
        };

        string json = System.Text.Json.JsonSerializer.Serialize(project, ProjectStoreDefaults.JsonOptions);

        Assert.DoesNotContain("Tanki", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Python312", json, StringComparison.Ordinal);
        Assert.DoesNotContain("effective", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"gameDir\": \"\"", json, StringComparison.Ordinal);
        Assert.Contains("\"pythonExe\": \"\"", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Сборка берёт вычисленные значения, а сохранённые в проекте — только как запасные.
    /// </summary>
    [Fact]
    public void BuildRunner_PrefersEffectiveValues()
    {
        var project = new ModProject
        {
            ProjectDir = @"C:\Users\dev\source\repos\wot-mod-alpha",
            GameDir = @"C:\Games\Old",
            EffectiveGameDir = @"C:\Games\Tanki",
            PythonExe = "python",
            EffectivePythonExe = @"C:\Python312\python.exe"
        };

        IReadOnlyList<string> args = Core.Services.PythonBuildRunner.BuildArgumentList(BuildAction.Build, project);
        int at = args.ToList().IndexOf("--game-dir");

        Assert.True(at >= 0);
        Assert.Equal(@"C:\Games\Tanki", args[at + 1]);
        Assert.Equal(@"C:\Python312\python.exe", Core.Services.PythonBuildRunner.PythonCommandFor(project));

        project.EffectivePythonExe = string.Empty;
        Assert.Equal("python", Core.Services.PythonBuildRunner.PythonCommandFor(project));

        project.PythonExe = string.Empty;
        Assert.Equal("python", Core.Services.PythonBuildRunner.PythonCommandFor(project));
    }

    /// <summary>Флаги build.py и признак «пишет в игру» должны совпадать со справкой скрипта.</summary>
    [Theory]
    [InlineData(BuildAction.Build, null, false)]
    [InlineData(BuildAction.BuildAndInstall, "--install", true)]
    [InlineData(BuildAction.DevDeploy, "--dev", true)]
    [InlineData(BuildAction.Uninstall, "--uninstall", true)]
    [InlineData(BuildAction.List, "--list", false)]
    public void BuildAction_MapsToScriptFlag(BuildAction action, string? expectedFlag, bool writesToGame)
    {
        Assert.Equal(expectedFlag, action.ToBuildFlag());
        Assert.Equal(writesToGame, action.WritesToGame());
    }

    /// <summary>Огрубление уровней python.log: реально встречающиеся значения INFO/ERROR/DEBUG/WARNING/NOTICE.</summary>
    [Theory]
    [InlineData("DEBUG", LogSeverity.Debug)]
    [InlineData("INFO", LogSeverity.Info)]
    [InlineData("NOTICE", LogSeverity.Info)]
    [InlineData("WARNING", LogSeverity.Warning)]
    [InlineData("ERROR", LogSeverity.Error)]
    [InlineData("CRITICAL", LogSeverity.Unknown)]
    public void LogLevels_ToSeverity_CoversRealLevels(string level, LogSeverity expected)
        => Assert.Equal(expected, LogLevels.ToSeverity(level));

    /// <summary>Производные пути установки игры: конфиги модов лежат ВНЕ версионного каталога.</summary>
    [Fact]
    public void GameInstall_DerivedPaths()
    {
        var game = new GameInstall(
            @"C:\Games\Tanki",
            "1.44.0.0",
            @"C:\Games\Tanki\mods\1.44.0.0",
            @"C:\Games\Tanki\res_mods\1.44.0.0",
            @"C:\Games\Tanki\python.log");

        Assert.Equal(@"C:\Games\Tanki\paths.xml", game.PathsXmlPath);
        Assert.Equal(@"C:\Games\Tanki\mods\configs", game.ModConfigsDir);
        Assert.Equal(@"C:\Games\Tanki\Tanki.exe", game.ClientExePath);
    }

    /// <summary>Пустое состояние деплоя — корректное значение по умолчанию для UI.</summary>
    [Fact]
    public void DeployState_Absent_IsEmptyAndNotShadowed()
    {
        DeployState state = DeployState.Absent;

        Assert.True(state.IsAbsent);
        Assert.False(state.IsShadowed);
        Assert.Empty(state.ResModsFiles);
        Assert.Null(state.MtmodPath);
    }
}
