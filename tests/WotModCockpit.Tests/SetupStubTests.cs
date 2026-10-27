using System.Diagnostics;
using System.Text;
using WotModCockpit.Core.Bundle;

namespace WotModCockpit.Tests;

/// <summary>
/// Настоящий установщик: заглушка <c>setup\WotModCockpit.Setup.exe</c>, которую сборка кладёт
/// рядом с тестами, + сетап из фальшивых модов, + фальшивая игра во временной папке.
/// <para>
/// Установщик гоняется в тихом режиме (<c>--silent --game:...</c>): в нём та же установка, что
/// за кнопкой окна. Настоящая папка игры не трогается никогда — папка игры всегда
/// передаётся явно, а без неё тихий режим отказывается работать (это тоже проверяется).
/// </para>
/// <para>
/// Если у разработчика в этот момент запущена игра, установщик честно отказывается (код 3), и
/// стенд пропускается, а не падает.
/// </para>
/// </summary>
public sealed class SetupStubTests : IDisposable
{
    private const string ClientVersion = "1.45.0.0";

    private readonly CoreTempDir _tmp = new("wotcockpit-setupstub");
    private readonly string _game;
    private readonly string _modsDir;

    public SetupStubTests()
    {
        _game = Path.Combine(_tmp.Path, "game");
        _modsDir = Path.Combine(_game, "mods", ClientVersion);
        Directory.CreateDirectory(_modsDir);
        File.WriteAllText(Path.Combine(_game, "paths.xml"), PathsXml(ClientVersion));
    }

    public void Dispose() => _tmp.Dispose();

    // ================================================================= состав

    /// <summary><c>--list</c> печатает состав сборки и ничего не пишет.</summary>
    [SkippableFact]
    public void List_PrintsComponents()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"), ("ru.test.beta", "2.0.0", "Бета"));

        (int code, string output) = Run(setup, "--list");

        Assert.Equal(0, code);
        Assert.Contains("ru.test.alpha\t1.0.0\tАльфа", output);
        Assert.Contains("ru.test.beta\t2.0.0\tБета", output);
        Assert.Empty(Directory.GetFiles(_modsDir, "*", SearchOption.AllDirectories));
    }

    /// <summary>Голая заглушка сама по себе не сетап: говорит об этом и выходит с кодом 5.</summary>
    [SkippableFact]
    public void BareStub_SaysItIsJustATemplate()
    {
        (int code, string output) = Run(Stub(), "--list");

        Assert.Equal(5, code);
        Assert.Contains("заготовка установщика", output);
    }

    /// <summary>Обрезанный сетап не открывается: код 5 и понятная причина.</summary>
    [SkippableFact]
    public void TruncatedSetup_IsReportedAsBroken()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));
        byte[] bytes = File.ReadAllBytes(setup);
        File.WriteAllBytes(setup, bytes[..^7]);

        (int code, string output) = Run(setup, "--list");

        Assert.Equal(5, code);
        Assert.Contains("повреждён", output);
    }

    // ================================================================= установка

    /// <summary>
    /// Главный сценарий: всё ставится, прежняя версия своего мода выключается в <c>.off</c>
    /// (а не удаляется), чужой мод — даже с похожим именем — не тронут ни байтом.
    /// </summary>
    [SkippableFact]
    public void Silent_InstallsAll_DisablesOldVersion_LeavesForeignModsAlone()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"), ("ru.test.beta", "2.0.0", "Бета"));

        string old = GameMod("ru.test.alpha_0.9.0.mtmod", "ru.test.alpha", "0.9.0");
        string foreign = GameMod("other/foreign_1.0.mtmod", "com.other.foreign", "1.0");
        string lookalike = GameMod("ru.test.alpha_tools_1.0.mtmod", "ru.test.alpha_tools", "1.0");
        byte[] foreignBytes = File.ReadAllBytes(foreign);
        byte[] lookalikeBytes = File.ReadAllBytes(lookalike);
        string log = Path.Combine(_tmp.Path, "setup.log");

        (int code, string output) = RunInstall(setup, "--silent", "--game:" + _game, "--log:" + log);

        Assert.Equal(0, code);
        AssertInstalled(setup, "ru.test.alpha", "ru.test.alpha_1.0.0.mtmod");
        AssertInstalled(setup, "ru.test.beta", "ru.test.beta_2.0.0.mtmod");

        Assert.False(File.Exists(old));
        Assert.True(File.Exists(old + ".off"));

        Assert.Equal(foreignBytes, File.ReadAllBytes(foreign));
        Assert.Equal(lookalikeBytes, File.ReadAllBytes(lookalike));

        Assert.Contains("выключена прежняя версия", output);
        Assert.Contains("[ok]", File.ReadAllText(log, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(_modsDir, "*" + ".wmcsetup-tmp", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Прежняя версия, лежащая в подпапке модпака, тоже выключается: игра читает каталог
    /// рекурсивно и загрузила бы обе.
    /// </summary>
    [SkippableFact]
    public void Silent_DisablesOldVersionInSubfolder()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));
        string old = GameMod("pack/ru.test.alpha_0.9.0.mtmod", "ru.test.alpha", "0.9.0");

        (int code, _) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(0, code);
        Assert.True(File.Exists(old + ".off"));
        AssertInstalled(setup, "ru.test.alpha", "ru.test.alpha_1.0.0.mtmod");
    }

    /// <summary><c>--components</c> ставит только перечисленное.</summary>
    [SkippableFact]
    public void Silent_InstallsOnlySelectedComponents()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"), ("ru.test.beta", "2.0.0", "Бета"));

        (int code, _) = RunInstall(setup, "--silent", "--game:" + _game, "--components:ru.test.beta");

        Assert.Equal(0, code);
        AssertInstalled(setup, "ru.test.beta", "ru.test.beta_2.0.0.mtmod");
        Assert.False(File.Exists(Path.Combine(_modsDir, "ru.test.alpha_1.0.0.mtmod")));
    }

    /// <summary>
    /// Выключенная копия с тем же именем уже лежит — перезаписывать её нельзя, оставлять две
    /// включённые версии тоже. Этот мод не ставится вовсе и ничего не меняется; остальные моды
    /// ставятся, код возврата 1.
    /// </summary>
    [SkippableFact]
    public void Silent_OffTwinBlocksThatModOnly_AndChangesNothingForIt()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"), ("ru.test.beta", "2.0.0", "Бета"));
        string old = GameMod("ru.test.alpha_0.9.0.mtmod", "ru.test.alpha", "0.9.0");
        string twin = GameMod("ru.test.alpha_0.9.0.mtmod.off", "ru.test.alpha", "0.9.0-old");
        byte[] oldBytes = File.ReadAllBytes(old);
        byte[] twinBytes = File.ReadAllBytes(twin);

        (int code, string output) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(1, code);
        Assert.Contains("[FAIL]", output);
        Assert.Equal(oldBytes, File.ReadAllBytes(old));
        Assert.Equal(twinBytes, File.ReadAllBytes(twin));
        Assert.False(File.Exists(Path.Combine(_modsDir, "ru.test.alpha_1.0.0.mtmod")));
        AssertInstalled(setup, "ru.test.beta", "ru.test.beta_2.0.0.mtmod");
    }

    /// <summary>Та же версия, но другая сборка — файл заменяется на месте, без лишних <c>.off</c>.</summary>
    [SkippableFact]
    public void Silent_SameVersionDifferentBuild_ReplacesInPlace()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));
        string same = GameMod("ru.test.alpha_1.0.0.mtmod", "ru.test.alpha", "1.0.0", payload: "старая сборка");

        (int code, string output) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(0, code);
        Assert.Contains("заменён", output);
        AssertInstalled(setup, "ru.test.alpha", "ru.test.alpha_1.0.0.mtmod");
        Assert.False(File.Exists(same + ".off"));
    }

    /// <summary>Каталога модов ещё нет (моды ни разу не ставили) — он создаётся по paths.xml.</summary>
    [SkippableFact]
    public void Silent_CreatesModsFolderFromPathsXml()
    {
        Directory.Delete(_modsDir, recursive: true);
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));

        (int code, _) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(0, code);
        AssertInstalled(setup, "ru.test.alpha", "ru.test.alpha_1.0.0.mtmod");
    }

    /// <summary>Сборка под другой клиент ставится, но с предупреждением.</summary>
    [SkippableFact]
    public void Silent_WarnsAboutDifferentClientVersion()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));
        File.WriteAllText(Path.Combine(_game, "paths.xml"), PathsXml("1.46.0.0"));

        (int code, string output) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(0, code);
        Assert.Contains("собиралась под клиент " + ClientVersion, output);
        Assert.True(File.Exists(Path.Combine(_game, "mods", "1.46.0.0", "ru.test.alpha_1.0.0.mtmod")));
    }

    // ================================================================= отказы

    /// <summary>Без явной папки игры тихий режим не ставит ничего — даже если игра найдётся сама.</summary>
    [SkippableFact]
    public void Silent_WithoutGameFolder_Refuses()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));

        (int code, string output) = Run(setup, "--silent");

        Assert.Equal(2, code);
        Assert.Contains("--game", output);
    }

    /// <summary>Мод, которого в сборке нет, — ошибка ключей, а не тихий пропуск.</summary>
    [SkippableFact]
    public void Silent_UnknownComponent_Refuses()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));

        (int code, string output) = Run(setup, "--silent", "--game:" + _game, "--components:ru.test.nope");

        Assert.Equal(2, code);
        Assert.Contains("ru.test.nope", output);
        Assert.Empty(Directory.GetFiles(_modsDir));
    }

    /// <summary>Папка без paths.xml — не игра: код 4, ничего не создано.</summary>
    [SkippableFact]
    public void Silent_NotAGameFolder_Refuses()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"));
        string notGame = Path.Combine(_tmp.Path, "not-a-game");
        Directory.CreateDirectory(notGame);

        (int code, string output) = Run(setup, "--silent", "--game:" + notGame);

        Assert.Equal(4, code);
        Assert.Contains("paths.xml", output);
        Assert.Empty(Directory.GetFileSystemEntries(notGame));
    }

    // ================================================================= окно

    /// <summary>
    /// Окно собирается и рисуется (служебный ключ <c>--render</c>): список модов, папка игры,
    /// кнопки. Картинка сохраняется рядом с выводом тестов — её удобно посмотреть глазами.
    /// </summary>
    [SkippableFact]
    public void Window_Renders()
    {
        string setup = BuildSetup(("ru.test.alpha", "1.0.0", "Альфа"), ("ru.test.beta", "2.0.0", "Бета"));
        GameMod("ru.test.alpha_0.9.0.mtmod", "ru.test.alpha", "0.9.0");
        string png = Path.Combine(_tmp.Path, "window.png");

        (int code, _) = Run(setup, "--render:" + png, "--game:" + _game);

        Assert.False(File.Exists(png + ".error.txt"), File.Exists(png + ".error.txt") ? File.ReadAllText(png + ".error.txt") : string.Empty);
        Assert.Equal(0, code);
        Assert.True(new FileInfo(png).Length > 1000);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, File.ReadAllBytes(png)[..4]);
    }

    // ================================================================= настоящие проекты

    /// <summary>
    /// Настоящие проекты пользователя: их сборки из <c>dist\</c> (только чтение) проходят все
    /// проверки сборщика, сетап собирается и ставится установщиком во временную «игру» байт в
    /// байт. Каталоги проектов и настоящая игра не меняются.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void RealProjects_PackAndInstallIntoTempGame()
    {
        string[] dirs = Directory.Exists(ReposLayout.Root)
            ? Directory.GetDirectories(ReposLayout.Root, "wot-mod-*")
            : [];
        IReadOnlyList<SetupBundleCandidate> built = SetupBundleCandidates.Read(dirs).Where(c => c.HasBuild).ToArray();
        Skip.If(built.Count == 0, "рядом нет собранных проектов модов");

        string setup = Path.Combine(_tmp.Path, "out", "Настоящие моды.exe");
        SetupBundleResult result = SetupBundleBuilder.Build(new SetupBundleRequest
        {
            StubPath = Stub(),
            OutputPath = setup,
            Name = "Настоящие моды",
            Version = "2026.10.02",
            GameVersion = ClientVersion,
            ModFiles = built.Select(c => c.Project.BuiltArtifactPath!).ToArray(),
        });

        Assert.Equal(built.Select(c => c.ModId), result.Manifest.Components.Select(c => c.Id));

        (int code, string output) = RunInstall(setup, "--silent", "--game:" + _game);

        Assert.Equal(0, code);
        foreach (SetupBundleCandidate c in built)
        {
            string target = Path.Combine(_modsDir, Path.GetFileName(c.Project.BuiltArtifactPath!));
            Assert.Equal(File.ReadAllBytes(c.Project.BuiltArtifactPath!), File.ReadAllBytes(target));
        }

        Assert.Contains("Готово: установлено " + built.Count, output);
    }

    // ================================================================= помощники

    private static string Stub()
    {
        string? stub = SetupStubLocator.Find();
        Skip.If(stub is null, "заглушка установщика не собрана рядом с тестами (setup\\WotModCockpit.Setup.exe)");
        return stub!;
    }

    private string BuildSetup(params (string Id, string Version, string Name)[] mods)
    {
        var files = new List<string>();
        foreach ((string id, string version, string name) in mods)
        {
            string path = Path.Combine(_tmp.Path, "dist", $"{id}_{version}.mtmod");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            FakeGameDir.WriteArchive(
                path,
                FakeGameDir.Meta(id, version, name, "описание " + name),
                [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_" + id.Split('.').Last() + ".pyc", "новая сборка " + id)],
                compress: false);
            files.Add(path);
        }

        string output = Path.Combine(_tmp.Path, "out", "Сборка " + Guid.NewGuid().ToString("N")[..6] + ".exe");
        return SetupBundleBuilder.Build(new SetupBundleRequest
        {
            StubPath = Stub(),
            OutputPath = output,
            Name = "Тестовая сборка",
            Version = "2026.10.02",
            GameVersion = ClientVersion,
            ModFiles = files,
        }).OutputPath;
    }

    private string GameMod(string relative, string id, string version, string payload = "старая версия")
    {
        string path = Path.Combine(_modsDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FakeGameDir.WriteArchive(
            path,
            FakeGameDir.Meta(id, version, id, "в игре"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_x.pyc", payload + " " + id + " " + version)],
            compress: false);
        return path;
    }

    private void AssertInstalled(string setup, string id, string fileName)
    {
        string target = Path.Combine(_modsDir, fileName);
        Assert.True(File.Exists(target), "не установлен: " + target);
        Assert.Equal(SetupBundleReader.ReadComponent(setup, id), File.ReadAllBytes(target));
    }

    /// <summary>Запуск установки: если игра у разработчика запущена, стенд пропускается.</summary>
    private static (int Code, string Output) RunInstall(string setup, params string[] args)
    {
        (int code, string output) = Run(setup, args);
        Skip.If(code == 3, "игра запущена — установщик правильно отказался, стенд установки пропущен");
        return (code, output);
    }

    private static (int Code, string Output) Run(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using Process p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(60_000))
        {
            p.Kill(entireProcessTree: true);
            Assert.Fail("установщик не завершился за минуту: " + exe + " " + string.Join(' ', args));
        }

        return (p.ExitCode, output);
    }

    private static string PathsXml(string version) =>
        $"""
         <root>
           <Paths>
             <Path cacheSubdirs="true">./res_mods/{version}</Path>
             <Path mask="*.mtmod" mode="recursive" root="res">./mods/{version}</Path>
             <Packages>
               <Package type="sd,hd">./res/packages/scripts.pkg</Package>
             </Packages>
             <Path>./res</Path>
           </Paths>
         </root>
         """;
}
