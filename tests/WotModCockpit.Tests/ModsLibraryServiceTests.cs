using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты фасада <see cref="ModLibraryService"/>: установка из <c>dist\</c>, удаление с подтверждением,
/// журнал операций, кеш иконок, поиск проектов.
/// <para>
/// Журнал в тестах уводится во временный файл: трогать <c>%APPDATA%\WotModCockpit\</c> из прогона
/// тестов нельзя, там живут настоящие настройки пользователя.
/// </para>
/// </summary>
public class ModsLibraryServiceTests
{
    /// <summary>Служба с подставной пробой процессов и журналом во временной папке.</summary>
    /// <param name="game">Фейковая установка.</param>
    /// <param name="probe">Проба «игра запущена»; <c>null</c> — «не запущена».</param>
    /// <returns>Служба.</returns>
    private static ModLibraryService Service(FakeGameDir game, IGameRunningProbe? probe = null) =>
        new(probe ?? FakeGameProbes.Closed,
            new ModLibraryOptions { OperationLogPath = Path.Combine(game.Root, "log", "mods-operations.log") });

    // ------------------------------------------------------------------ установка

    /// <summary>
    /// Установка копирует артефакт в каталог модов, а предыдущую версию того же мода
    /// ВЫКЛЮЧАЕТ, а не удаляет: старая сборка остаётся на диске и возвращается одним действием.
    /// </summary>
    [Fact]
    public void Install_CopiesArtifact_AndDisablesPreviousVersion()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.1", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.1.mtmod");

        string old = game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "старая"));

        ModOperationResult result = Service(game).Install(game.Install, artifact);

        Assert.True(result.Success, result.Message);
        string installed = Path.Combine(game.ModsDir, "ru.example.alpha_1.0.1.mtmod");
        Assert.True(File.Exists(installed));
        Assert.Equal(File.ReadAllBytes(artifact), File.ReadAllBytes(installed));

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(old + ".off"), "предыдущая версия обязана быть выключена, а не удалена");

        ModOperationResult related = Assert.Single(result.Related);
        Assert.Equal(ModOperation.Disable, related.Operation);
        Assert.True(related.Success);
    }

    /// <summary>Временный файл установки не остаётся в каталоге игры.</summary>
    [Fact]
    public void Install_LeavesNoTempFiles()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-delta", "ru.example.delta", "1.0.0", "Delta Path");
        string artifact = Path.Combine(project, "dist", "ru.example.delta_1.0.0.mtmod");

        Assert.True(Service(game).Install(game.Install, artifact).Success);

        Assert.Empty(Directory.GetFiles(game.ModsDir, "*.cockpit-tmp", SearchOption.AllDirectories));
        Assert.Single(game.Snapshot());
    }

    /// <summary>Тот же файл уже стоит — по умолчанию перезаписи нет.</summary>
    [Fact]
    public void Install_SameFileName_IsRefusedWithoutOverwrite()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");
        string installed = game.AddRawFile("ru.example.alpha_1.0.0.mtmod", "СТАРОЕ содержимое");

        ModOperationResult result = Service(game).Install(game.Install, artifact);

        Assert.Equal(ModOperationStatus.RefusedTargetExists, result.Status);
        Assert.Equal("СТАРОЕ содержимое", File.ReadAllText(installed));
    }

    /// <summary>С явного разрешения та же версия ставится поверх.</summary>
    [Fact]
    public void Install_SameFileName_OverwritesWhenAllowed()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");
        game.AddRawFile("ru.example.alpha_1.0.0.mtmod", "старое");

        ModOperationResult result = Service(game)
            .Install(game.Install, artifact, new ModInstallOptions { OverwriteSameName = true });

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            File.ReadAllBytes(artifact),
            File.ReadAllBytes(Path.Combine(game.ModsDir, "ru.example.alpha_1.0.0.mtmod")));
    }

    /// <summary>При запущенной игре установка запрещена, как и всё остальное.</summary>
    [Fact]
    public void Install_GameRunning_IsRefused()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");

        ModOperationResult result = Service(game, FakeGameProbes.Running).Install(game.Install, artifact);

        Assert.Equal(ModOperationStatus.RefusedGameRunning, result.Status);
        Assert.Empty(game.Snapshot());
    }

    /// <summary>Битый артефакт в игру не поедет: движок его всё равно не загрузит.</summary>
    [Fact]
    public void Install_BrokenArtifact_IsRefused()
    {
        using FakeGameDir game = new();
        string broken = Path.Combine(game.Root, "dist_broken", "ru.local.x_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "не архив");

        ModOperationResult result = Service(game).Install(game.Install, broken);

        Assert.False(result.Success);
        Assert.Empty(game.Snapshot());
    }

    /// <summary>Артефакта нет — внятный отказ «сначала соберите мод».</summary>
    [Fact]
    public void Install_MissingArtifact_IsRefused()
    {
        using FakeGameDir game = new();

        ModOperationResult result = Service(game).Install(game.Install, Path.Combine(game.Root, "dist", "ghost_1.0.mtmod"));

        Assert.Equal(ModOperationStatus.RefusedSourceMissing, result.Status);
    }

    /// <summary>Подпапка, ведущая за пределы каталога модов, отвергается.</summary>
    [Fact]
    public void Install_SubfolderEscapingModsDir_IsRefused()
    {
        using FakeGameDir game = new();
        string project = game.AddProject("wot-mod-alpha", "ru.example.alpha", "1.0.0", "Alpha Path");
        string artifact = Path.Combine(project, "dist", "ru.example.alpha_1.0.0.mtmod");

        ModOperationResult result = Service(game).Install(
            game.Install, artifact, new ModInstallOptions { SubfolderRelative = @"..\..\res_mods\1.44.0.0" });

        Assert.Equal(ModOperationStatus.RefusedOutsideModsDir, result.Status);
        Assert.Empty(game.Snapshot());
    }

    // ------------------------------------------------------------------ удаление

    /// <summary>
    /// Удаление требует ТОЧНОГО имени файла. Не совпало — файл на месте, и кокпит даже не смотрел
    /// на диск. Так интерфейс вынужден показать пользователю, что именно удаляется.
    /// </summary>
    [Fact]
    public void Delete_WrongConfirmation_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("foreign_pack.mtmod");

        ModOperationResult result = Service(game).Delete(game.Install, path, "foreign_pack");

        Assert.Equal(ModOperationStatus.RefusedConfirmationMismatch, result.Status);
        Assert.True(File.Exists(path));
    }

    /// <summary>Совпало имя — файл удаляется, и ровно он один.</summary>
    [Fact]
    public void Delete_WithExactConfirmation_RemovesOnlyThatFile()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("foreign_pack.mtmod");
        string keep = game.AddMod("audioww/voice_1.0.mtmod");

        ModOperationResult result = Service(game).Delete(game.Install, path, "foreign_pack.mtmod");

        Assert.True(result.Success, result.Message);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(keep));
        Assert.True(Directory.Exists(Path.Combine(game.ModsDir, "audioww")), "каталоги не удаляются никогда");
    }

    /// <summary>Удаление за пределами каталога модов невозможно даже с верным именем.</summary>
    [Fact]
    public void Delete_OutsideModsDir_IsRefused()
    {
        using FakeGameDir game = new();
        string outside = Path.Combine(game.Root, "res", "packages", "scripts_1.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "ресурс игры");

        ModOperationResult result = Service(game).Delete(game.Install, outside, "scripts_1.0.mtmod");

        Assert.Equal(ModOperationStatus.RefusedOutsideModsDir, result.Status);
        Assert.True(File.Exists(outside));
    }

    /// <summary>Удаление при запущенной игре запрещено.</summary>
    [Fact]
    public void Delete_GameRunning_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        ModOperationResult result = Service(game, FakeGameProbes.Running).Delete(game.Install, path, "x_1.0.mtmod");

        Assert.Equal(ModOperationStatus.RefusedGameRunning, result.Status);
        Assert.True(File.Exists(path));
    }

    // ------------------------------------------------------------------ журнал

    /// <summary>Каждая операция — строка в журнале, включая отказы: иначе непонятно, что произошло.</summary>
    [Fact]
    public void Journal_RecordsSuccessesAndRefusals()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");
        ModLibraryService service = Service(game);

        Assert.True(service.Disable(game.Install, path).Success);
        Assert.False(service.Delete(game.Install, path + ".off", "неверное имя").Success);

        IReadOnlyList<string> log = service.ReadOperationLog();

        Assert.Equal(2, log.Count);
        Assert.Contains("DISABLE", log[0], StringComparison.Ordinal);
        Assert.Contains(path, log[0], StringComparison.Ordinal);
        Assert.Contains("DELETE", log[1], StringComparison.Ordinal);
        Assert.Contains("REFUSED", log[1], StringComparison.Ordinal);
    }

    /// <summary>Недоступный журнал не ломает операцию — она уже выполнена.</summary>
    [Fact]
    public void Journal_FailureDoesNotBreakOperation()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        // Путь, которого не может быть: запись в журнал обязана молча провалиться.
        ModLibraryService service = new(
            FakeGameProbes.Closed,
            new ModLibraryOptions { OperationLogPath = "\0:\\нет\\такого\\пути.log" });

        ModOperationResult result = service.Disable(game.Install, path);

        Assert.True(result.Success, result.Message);
        Assert.Empty(service.ReadOperationLog());
    }

    /// <summary>
    /// Путь журнала по умолчанию — рядом с settings.json кокпита, и <c>WOTCOCKPIT_SETTINGS_DIR</c>
    /// переносит его вместе с настройками: иначе вторая копия кокпита дописывала бы свои операции
    /// в настоящий журнал пользователя. В тестовой сборке переменная указывает во временный
    /// каталог (<see cref="TestSettingsIsolation"/>), так что и журнал по умолчанию уходит туда.
    /// </summary>
    [Fact]
    public void DefaultOperationLogPath_LivesNextToSettings()
    {
        string path = ModLibraryService.DefaultOperationLogPath();
        string settingsDir = Path.GetDirectoryName(new Core.Services.JsonSettingsStore().SettingsPath)!;

        Assert.Equal(Path.Combine(settingsDir, ModLibraryService.OperationLogFileName), path);

        if (!TestSettingsIsolation.RealMachine)
        {
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), path, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ------------------------------------------------------------------ чтение и обновление

    /// <summary>После выключения одна строка перечитывается с диска, а не «предполагается» по успеху.</summary>
    [Fact]
    public void Refresh_AfterDisable_ReportsNewState()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod", FakeGameDir.Meta("a.x", "1.0", "X", "d"));
        ModLibraryService service = Service(game);

        ModOperationResult op = service.Disable(game.Install, path);
        Assert.True(op.Success, op.Message);

        Assert.Null(service.Refresh(game.Install, path));

        InstalledMod? row = service.Refresh(game.Install, op.TargetPath!);
        Assert.NotNull(row);
        Assert.Equal(ModState.Disabled, row!.State);
        Assert.Equal("a.x", row.ModId);
    }

    /// <summary>Иконка кэшируется по пути + времени + размеру: повторный вызов не лезет в архив снова.</summary>
    [Fact]
    public void ReadIcon_IsCachedBetweenCalls()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "x_1.0.mtmod",
            FakeGameDir.Meta("a.x", "1.0", "X", "d"),
            [FakeGameDir.IconEntry("res/gui/maps/x/modsListApi.png")]);

        ModLibraryService service = Service(game);

        ModIcon first = service.ReadIcon(path, "a.x");
        ModIcon second = service.ReadIcon(path, "a.x");

        Assert.True(first.HasBytes);
        Assert.Same(first, second);
    }

    /// <summary>Сканирование через фасад — то же самое, что через сканер, но по <see cref="GameInstall"/>.</summary>
    [Fact]
    public void Scan_ThroughFacade_UsesModsDirOfInstall()
    {
        using FakeGameDir game = new();
        game.AddMod("x_1.0.mtmod");

        ModLibraryScan scan = Service(game).Scan(game.Install);

        Assert.Single(scan.Mods);
        Assert.Equal(game.ModsDir, scan.ModsDir);
    }

    /// <summary>Состояние игры пробрасывается наружу — им UI гасит кнопки.</summary>
    [Fact]
    public void CheckGame_ReportsProbeState()
    {
        using FakeGameDir game = new();

        Assert.False(Service(game).CheckGame().BlocksWrites);
        Assert.True(Service(game, FakeGameProbes.Running).CheckGame().BlocksWrites);
        Assert.True(Service(game, FakeGameProbes.Unknown).CheckGame().BlocksWrites);
    }

    // ------------------------------------------------------------------ проекты и DI

    /// <summary>
    /// Соседние проекты <c>wot-mod-*</c> находятся по одному известному каталогу — так в списке
    /// «МОИ МОДЫ» появляются все три проекта пользователя, а не только открытый сейчас.
    /// </summary>
    [Fact]
    public void DiscoverSiblingProjects_FindsWotModFolders()
    {
        using CoreTempDir tmp = new("wotcockpit-repos");
        foreach (string name in new[] { "wot-mod-alpha", "wot-mod-delta", "wot-mod-amber" })
        {
            tmp.WriteFile($"{name}/build.py", "# fake");
        }

        tmp.WriteFile("wot-mod-notaproject/readme.md", "нет ни build.py, ни meta.xml");
        tmp.MakeDir("some-other-repo");

        IReadOnlyList<string> found = ModLibraryService.DiscoverSiblingProjects(tmp.Combine("wot-mod-alpha"));

        Assert.Equal(3, found.Count);
        Assert.All(found, f => Assert.StartsWith(tmp.Path, f, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, f => f.EndsWith("wot-mod-amber", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Пустой и несуществующий путь не ломают поиск.</summary>
    [Fact]
    public void DiscoverSiblingProjects_ToleratesGarbage()
    {
        Assert.Empty(ModLibraryService.DiscoverSiblingProjects(null));
        Assert.Empty(ModLibraryService.DiscoverSiblingProjects("   "));
    }

    /// <summary>
    /// Регистрация в DI отдельным методом — чтобы добавление лаунчера не трогало
    /// существующий <c>AddCockpitCore</c>, состав которого проверяют уже написанные тесты.
    /// </summary>
    [Fact]
    public void AddModLibrary_RegistersServices()
    {
        ServiceProvider provider = new ServiceCollection()
            .AddModLibrary()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IGameRunningProbe>());
        IModLibraryService service = provider.GetRequiredService<IModLibraryService>();
        Assert.Same(service, provider.GetRequiredService<IModLibraryService>());
    }
}
