using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using WotModCockpit.Core.Bundle;

namespace WotModCockpit.Tests;

/// <summary>
/// Сборщик и читатель сетапа (<see cref="SetupBundleBuilder"/>, <see cref="SetupBundleReader"/>).
/// <para>
/// Здесь заглушка — не настоящий exe, а «MZ» и мусор: формату всё равно, что лежит перед ZIP.
/// Настоящая заглушка гоняется в <see cref="SetupStubTests"/>.
/// </para>
/// </summary>
public sealed class SetupBundleTests : IDisposable
{
    private static readonly DateTimeOffset Created = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly CoreTempDir _tmp = new("wotcockpit-bundle");

    public void Dispose() => _tmp.Dispose();

    // ================================================================= сборка и чтение

    /// <summary>
    /// Главное свойство формата: что упаковали, то и прочитали — описание из meta.xml каждого
    /// архива, размеры, суммы и сами байты модов. Заглушка в начале файла не тронута.
    /// </summary>
    [Fact]
    public void Build_ThenRead_RoundTripsManifestAndBytes()
    {
        string stub = FakeStub();
        string alpha = Mod("ru.test.alpha", "1.0.0", "Альфа", "Первый мод");
        string beta = Mod("ru.test.beta", "2.1.0", "Beta", "второй");

        SetupBundleResult result = SetupBundleBuilder.Build(Request(stub, Out("Сборка 1.exe"), alpha, beta) with
        {
            GameVersion = "1.45.0.0",
        });

        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(new FileInfo(result.OutputPath).Length, result.SizeBytes);

        SetupBundleInfo info = SetupBundleReader.Read(result.OutputPath);
        Assert.Equal(new FileInfo(stub).Length, info.StubLength);
        Assert.Equal(File.ReadAllBytes(stub), File.ReadAllBytes(result.OutputPath)[..(int)info.StubLength]);

        SetupBundleManifest m = info.Manifest;
        Assert.Equal(SetupBundleFormat.FormatVersion, m.Format);
        Assert.Equal("Сборка модов", m.Name);
        Assert.Equal("2026.10.02", m.Version);
        Assert.Equal("2026-10-02T12:00:00Z", m.CreatedUtc);
        Assert.Equal("1.45.0.0", m.GameVersion);
        Assert.Equal(["ru.test.alpha", "ru.test.beta"], m.Components.Select(c => c.Id));

        SetupBundleComponent a = m.Components[0];
        Assert.Equal("Альфа", a.Name);
        Assert.Equal("1.0.0", a.Version);
        Assert.Equal("Первый мод", a.Description);
        Assert.Equal("mods/ru.test.alpha_1.0.0.mtmod", a.File);
        Assert.Equal("ru.test.alpha_1.0.0.mtmod", a.FileName);
        Assert.Equal(new FileInfo(alpha).Length, a.Size);
        Assert.Equal(Sha(alpha), a.Sha256);

        Assert.Equal(File.ReadAllBytes(alpha), SetupBundleReader.ReadComponent(result.OutputPath, "ru.test.alpha"));
        Assert.Equal(File.ReadAllBytes(beta), SetupBundleReader.ReadComponent(result.OutputPath, "ru.test.beta"));
    }

    /// <summary>
    /// <c>bundle.json</c> пишется так, как его ждёт заглушка: camelCase, время строкой,
    /// кириллица как есть, без лишних вычисляемых полей.
    /// </summary>
    [Fact]
    public void Manifest_IsCamelCaseJson_TheStubCanRead()
    {
        string setup = SetupBundleBuilder.Build(Request(FakeStub(), Out("s.exe"), Mod("ru.test.alpha", "1.0.0", "Альфа", "d"))).OutputPath;

        string json = ManifestText(setup);

        Assert.Contains("\"format\": 1", json);
        Assert.Contains("\"createdUtc\": \"2026-10-02T12:00:00Z\"", json);
        Assert.Contains("\"components\": [", json);
        Assert.Contains("\"sha256\": \"", json);
        Assert.Contains("\"name\": \"Альфа\"", json);
        Assert.DoesNotContain("fileName", json);
    }

    /// <summary>Мод без meta.xml в сетап не идёт: установщику не по чему узнать его прежнюю версию.</summary>
    [Fact]
    public void Build_RejectsModWithoutMeta_AndLeavesNothingBehind()
    {
        string bare = Path.Combine(_tmp.Path, "dist", "ru.test.bare_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(bare)!);
        FakeGameDir.WriteArchive(bare, null, [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_bare.pyc")], compress: false);

        string output = Out("s.exe");
        var ex = Assert.Throws<SetupBundleException>(() => SetupBundleBuilder.Build(Request(FakeStub(), output, bare)));

        Assert.Contains("meta.xml", ex.Message);
        AssertNothingWritten(output);
    }

    /// <summary>Битый архив отбивается до записи.</summary>
    [Fact]
    public void Build_RejectsBrokenArchive()
    {
        string broken = Path.Combine(_tmp.Path, "dist", "ru.test.broken_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(broken)!);
        File.WriteAllText(broken, "PK\u0003\u0004 огрызок");

        string output = Out("s.exe");
        var ex = Assert.Throws<SetupBundleException>(() => SetupBundleBuilder.Build(Request(FakeStub(), output, broken)));

        Assert.Contains("ZIP", ex.Message);
        AssertNothingWritten(output);
    }

    /// <summary>Две версии одного мода в одном сетапе — бессмыслица: установщик выключил бы одну другой.</summary>
    [Fact]
    public void Build_RejectsTheSameModTwice()
    {
        string v1 = Mod("ru.test.alpha", "1.0.0", "A", "d");
        string v2 = Mod("ru.test.alpha", "1.1.0", "A", "d", folder: "dist2");

        string output = Out("s.exe");
        var ex = Assert.Throws<SetupBundleException>(() => SetupBundleBuilder.Build(Request(FakeStub(), output, v1, v2)));

        Assert.Contains("дважды", ex.Message);
        AssertNothingWritten(output);
    }

    /// <summary>Существующий сетап без разрешения не перезаписывается; с разрешением — заменяется целиком.</summary>
    [Fact]
    public void Build_OverwritesOnlyWhenAllowed()
    {
        string output = Out("s.exe");
        File.WriteAllText(output, "старый сетап");
        string mod = Mod("ru.test.alpha", "1.0.0", "A", "d");

        Assert.Throws<SetupBundleException>(() => SetupBundleBuilder.Build(Request(FakeStub(), output, mod)));
        Assert.Equal("старый сетап", File.ReadAllText(output));

        SetupBundleBuilder.Build(Request(FakeStub(), output, mod) with { Overwrite = true });
        Assert.Equal("ru.test.alpha", SetupBundleReader.Read(output).Manifest.Components.Single().Id);
        Assert.Empty(Directory.GetFiles(_tmp.Path, "*.tmp-*", SearchOption.AllDirectories));
    }

    /// <summary>Готовый сетап вместо заглушки дал бы сетап в сетапе — отбивается.</summary>
    [Fact]
    public void Build_RejectsAlreadyBuiltSetupAsStub()
    {
        string first = SetupBundleBuilder.Build(Request(FakeStub(), Out("first.exe"), Mod("ru.test.alpha", "1.0.0", "A", "d"))).OutputPath;

        var ex = Assert.Throws<SetupBundleException>(
            () => SetupBundleBuilder.Build(Request(first, Out("second.exe"), Mod("ru.test.beta", "1.0.0", "B", "d"))));

        Assert.Contains("уже собранный сетап", ex.Message);
    }

    /// <summary>Заглушка обязана быть exe: иначе получился бы файл, который Windows не запустит.</summary>
    [Fact]
    public void Build_RejectsStubThatIsNotAnExe()
    {
        string stub = Path.Combine(_tmp.Path, "stub.exe");
        File.WriteAllText(stub, new string('x', 200));

        var ex = Assert.Throws<SetupBundleException>(
            () => SetupBundleBuilder.Build(Request(stub, Out("s.exe"), Mod("ru.test.alpha", "1.0.0", "A", "d"))));

        Assert.Contains("не exe", ex.Message);
    }

    // ================================================================= порча готового файла

    /// <summary>Обрезанный при копировании сетап опознаётся по трейлеру.</summary>
    [Fact]
    public void Read_TruncatedSetup_IsRejected()
    {
        string setup = SetupBundleBuilder.Build(Request(FakeStub(), Out("s.exe"), Mod("ru.test.alpha", "1.0.0", "A", "d"))).OutputPath;
        byte[] bytes = File.ReadAllBytes(setup);
        File.WriteAllBytes(setup, bytes[..^1]);

        Assert.Throws<SetupBundleException>(() => SetupBundleReader.Read(setup));
    }

    /// <summary>Дописанный в хвост байт тоже ломает трейлер — и это опознаётся, а не читается наугад.</summary>
    [Fact]
    public void Read_SetupWithAppendedBytes_IsRejected()
    {
        string setup = SetupBundleBuilder.Build(Request(FakeStub(), Out("s.exe"), Mod("ru.test.alpha", "1.0.0", "A", "d"))).OutputPath;
        using (FileStream fs = new(setup, FileMode.Append))
        {
            fs.WriteByte(0);
        }

        Assert.Throws<SetupBundleException>(() => SetupBundleReader.Read(setup));
    }

    /// <summary>
    /// Описание, которое врёт про содержимое (сумма не та), ловится сверкой — именно она
    /// защищает от подменённого или битого мода внутри сетапа.
    /// </summary>
    [Fact]
    public void Read_ManifestHashMismatch_IsRejected()
    {
        string mod = Mod("ru.test.alpha", "1.0.0", "A", "d");
        var manifest = new SetupBundleManifest
        {
            Name = "x",
            Version = "1",
            Components =
            [
                new SetupBundleComponent
                {
                    Id = "ru.test.alpha",
                    File = "mods/ru.test.alpha_1.0.0.mtmod",
                    Size = new FileInfo(mod).Length,
                    Sha256 = new string('0', 64),
                },
            ],
        };

        string setup = HandMadeSetup(manifest, ("mods/ru.test.alpha_1.0.0.mtmod", File.ReadAllBytes(mod)));

        var ex = Assert.Throws<SetupBundleException>(() => SetupBundleReader.Read(setup));
        Assert.Contains("контрольная сумма", ex.Message);

        // Без сверки содержимого тот же файл читается: проверка суммы — отдельный шаг.
        Assert.Equal("ru.test.alpha", SetupBundleReader.Read(setup, verifyContent: false).Manifest.Components[0].Id);
    }

    /// <summary>Мод, чей meta.xml говорит другой id, не выдаётся за мод из описания.</summary>
    [Fact]
    public void Read_IdInsideArchiveDiffersFromManifest_IsRejected()
    {
        string mod = Mod("ru.test.other", "1.0.0", "A", "d");
        var manifest = new SetupBundleManifest
        {
            Name = "x",
            Version = "1",
            Components =
            [
                new SetupBundleComponent
                {
                    Id = "ru.test.alpha",
                    File = "mods/ru.test.alpha_1.0.0.mtmod",
                    Size = new FileInfo(mod).Length,
                    Sha256 = Sha(mod),
                },
            ],
        };

        string setup = HandMadeSetup(manifest, ("mods/ru.test.alpha_1.0.0.mtmod", File.ReadAllBytes(mod)));

        var ex = Assert.Throws<SetupBundleException>(() => SetupBundleReader.Read(setup));
        Assert.Contains("ru.test.other", ex.Message);
    }

    /// <summary>Запись с путём наружу (<c>mods/../x</c>) не проходит проверку описания.</summary>
    [Fact]
    public void Read_ComponentFileOutsideModsFolder_IsRejected()
    {
        string mod = Mod("ru.test.alpha", "1.0.0", "A", "d");
        var manifest = new SetupBundleManifest
        {
            Name = "x",
            Version = "1",
            Components =
            [
                new SetupBundleComponent
                {
                    Id = "ru.test.alpha",
                    File = "evil/ru.test.alpha_1.0.0.mtmod",
                    Size = new FileInfo(mod).Length,
                    Sha256 = Sha(mod),
                },
            ],
        };

        string setup = HandMadeSetup(manifest, ("evil/ru.test.alpha_1.0.0.mtmod", File.ReadAllBytes(mod)));

        Assert.Throws<SetupBundleException>(() => SetupBundleReader.Read(setup));
    }

    // ================================================================= имена и версии

    [Theory]
    [InlineData("Сборка модов", "2026.10.02", "Сборка модов 2026.10.02.exe")]
    [InlineData("Моды: ночь/день", "1.0", "Моды_ ночь_день 1.0.exe")]
    [InlineData("  ", "  ", "setup.exe")]
    public void DefaultFileName_IsSafeForWindows(string name, string version, string expected) =>
        Assert.Equal(expected, SetupBundleBuilder.DefaultFileName(name, version));

    [Theory]
    [InlineData("2026.10.02", true)]
    [InlineData("1.0.0-beta+2", true)]
    [InlineData("", false)]
    [InlineData("версия 1", false)]
    [InlineData("1/2", false)]
    public void ValidateVersion_AllowsOnlySafeCharacters(string version, bool ok) =>
        Assert.Equal(ok, SetupBundleBuilder.ValidateVersion(version) is null);

    [Fact]
    public void DefaultVersion_IsTodaysDate() =>
        Assert.Equal("2026.10.02", SetupBundleBuilder.DefaultVersion(new DateTime(2026, 10, 2, 23, 59, 0)));

    // ================================================================= кандидаты

    /// <summary>
    /// Кандидаты — только проекты с meta.xml и id; сборка, которая старше правок в src\,
    /// помечается устаревшей, а байткод питона в src\ правкой не считается.
    /// </summary>
    [Fact]
    public void Candidates_SkipProjectsWithoutMeta_AndFlagStaleBuilds()
    {
        using var game = new FakeGameDir();
        string fresh = game.AddProject("wot-mod-fresh", "ru.test.fresh", displayName: "Fresh");
        string stale = game.AddProject("wot-mod-stale", "ru.test.stale", displayName: "Stale");
        string noMeta = game.AddProject("wot-mod-nometa", null);
        string noBuild = game.AddProject("wot-mod-nobuild", "ru.test.nobuild", displayName: "NoBuild", buildArtifact: false);

        DateTime built = DateTime.UtcNow.AddMinutes(-10);
        foreach (string dir in new[] { fresh, stale })
        {
            File.SetLastWriteTimeUtc(Directory.GetFiles(Path.Combine(dir, "dist")).Single(), built);
            foreach (string source in Directory.GetFiles(Path.Combine(dir, "src"), "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(source, built.AddMinutes(-5));
            }
        }

        string edited = Path.Combine(stale, "src", "res", "scripts", "client", "gui", "mods", "mod_stale.py");
        Directory.CreateDirectory(Path.GetDirectoryName(edited)!);
        File.WriteAllText(edited, "# правка после сборки\n");

        string cache = Path.Combine(fresh, "src", "res", "__pycache__", "x.pyc");
        Directory.CreateDirectory(Path.GetDirectoryName(cache)!);
        File.WriteAllText(cache, "кэш питона");

        IReadOnlyList<SetupBundleCandidate> list = SetupBundleCandidates.Read([fresh, stale, noMeta, noBuild]);

        Assert.Equal(["Fresh", "NoBuild", "Stale"], list.Select(c => c.Project.DisplayName));
        Assert.False(list.Single(c => c.ModId == "ru.test.fresh").IsStale);
        Assert.True(list.Single(c => c.ModId == "ru.test.stale").IsStale);
        Assert.False(list.Single(c => c.ModId == "ru.test.nobuild").HasBuild);
    }

    /// <summary>
    /// Файл удалён из src после сборки — время оставшихся не меняется, но сборка устарела: в ней
    /// лежит то, чего в исходниках больше нет. И для .py (в архиве .pyc), и для прочих файлов.
    /// </summary>
    [Fact]
    public void Candidates_SourceRemovedAfterBuild_IsStale()
    {
        using var game = new FakeGameDir();
        string dir = game.AddProject("wot-mod-alpha", "ru.test.alpha", displayName: "Alpha");
        string icon = Path.Combine(dir, "src", "res", "gui", "maps", "icons", "mods", "alpha.png");
        Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
        File.WriteAllText(icon, "png");
        string artifact = Directory.GetFiles(Path.Combine(dir, "dist")).Single();
        FakeGameDir.WriteArchive(artifact, FakeGameDir.Meta("ru.test.alpha", "1.0.0", "Alpha", "описание"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc"), FakeGameDir.Entry("res/gui/maps/icons/mods/alpha.png")], compress: false);
        foreach (string source in Directory.GetFiles(Path.Combine(dir, "src"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(source, DateTime.UtcNow.AddMinutes(-30));
        }

        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(-10));
        Assert.False(SetupBundleCandidates.Read([dir]).Single().IsStale);

        File.Delete(icon);
        Assert.True(SetupBundleCandidates.Read([dir]).Single().BuildHasRemovedSources);

        File.WriteAllText(icon, "png");
        File.SetLastWriteTimeUtc(icon, DateTime.UtcNow.AddMinutes(-30));
        File.Delete(Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods", "mod_alpha.py"));
        Assert.True(SetupBundleCandidates.Read([dir]).Single().IsStale);

        // Оборванная упаковка (build.py сняли кнопкой «Остановить»): не ZIP — тоже не годная сборка.
        File.WriteAllText(artifact, "PK\u0003\u0004 обрывок");
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow);
        Assert.True(SetupBundleCandidates.Read([dir]).Single().IsStale);
    }

    /// <summary>Заглушка ищется ровно в <c>setup\</c> рядом с приложением.</summary>
    [Fact]
    public void StubLocator_LooksInSetupFolderNextToTheApp()
    {
        Assert.Null(SetupStubLocator.Find(_tmp.Path));

        _tmp.WriteFile(@"setup\WotModCockpit.Setup.exe", "MZ");
        Assert.Equal(Path.Combine(_tmp.Path, "setup", "WotModCockpit.Setup.exe"), SetupStubLocator.Find(_tmp.Path));
    }

    /// <summary>Сборка кокпита кладёт настоящую заглушку рядом с тестами — та же, что у приложения.</summary>
    [Fact]
    public void RealStub_IsDeployedNextToTheTests()
    {
        string? stub = SetupStubLocator.Find();

        Assert.NotNull(stub);
        byte[] head = File.ReadAllBytes(stub!)[..2];
        Assert.Equal("MZ"u8.ToArray(), head);
    }

    // ================================================================= помощники

    private SetupBundleRequest Request(string stub, string output, params string[] mods) => new()
    {
        StubPath = stub,
        OutputPath = output,
        Name = "Сборка модов",
        Version = "2026.10.02",
        ModFiles = mods,
        CreatedUtc = Created,
    };

    private string Out(string fileName)
    {
        string dir = Path.Combine(_tmp.Path, "out");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, fileName);
    }

    private string FakeStub()
    {
        string path = Path.Combine(_tmp.Path, "stub", "WotModCockpit.Setup.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = new byte[4096];
        new Random(7).NextBytes(bytes);
        bytes[0] = (byte)'M';
        bytes[1] = (byte)'Z';
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string Mod(string id, string version, string name, string description, string folder = "dist")
    {
        string path = Path.Combine(_tmp.Path, folder, $"{id}_{version}.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        FakeGameDir.WriteArchive(
            path,
            FakeGameDir.Meta(id, version, name, description),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_" + id.Split('.').Last() + ".pyc", "bytecode " + id + " " + version)],
            compress: false);
        return path;
    }

    /// <summary>Сетап, собранный руками мимо сборщика, — чтобы проверить отказы читателя.</summary>
    private string HandMadeSetup(SetupBundleManifest manifest, params (string Entry, byte[] Bytes)[] files)
    {
        string stub = FakeStub();
        string path = Out("hand-" + Guid.NewGuid().ToString("N")[..6] + ".exe");

        using MemoryStream zipBytes = new();
        using (ZipArchive zip = new(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (Stream s = zip.CreateEntry(SetupBundleFormat.ManifestEntryName).Open())
            {
                s.Write(Encoding.UTF8.GetBytes(manifest.ToJson()));
            }

            foreach ((string entry, byte[] bytes) in files)
            {
                using Stream s = zip.CreateEntry(entry).Open();
                s.Write(bytes);
            }
        }

        byte[] stubBytes = File.ReadAllBytes(stub);
        using FileStream fs = new(path, FileMode.CreateNew);
        fs.Write(stubBytes);
        fs.Write(zipBytes.ToArray());
        fs.Write(SetupBundleFormat.CreateTrailer(stubBytes.Length, zipBytes.Length));
        return path;
    }

    private static string ManifestText(string setup)
    {
        using FileStream fs = File.OpenRead(setup);
        (_, long offset, long length) = SetupBundleFormat.ReadTrailer(fs);
        byte[] payload = new byte[length];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(payload);
        using ZipArchive zip = new(new MemoryStream(payload), ZipArchiveMode.Read);
        using StreamReader reader = new(zip.GetEntry(SetupBundleFormat.ManifestEntryName)!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private void AssertNothingWritten(string output)
    {
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(output)!, "*.tmp-*"));
    }
}
