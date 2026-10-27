using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="PathsXmlGameInstallLocator"/>.
/// Часть тестов идёт по НАСТОЯЩЕЙ установке игры (<c>WOTCOCKPIT_REAL_MACHINE=1</c>, каталог —
/// <c>WOTCOCKPIT_GAME_DIR</c>) и только читает её; остальные строят фейковые каталоги во временной папке.
/// </summary>
public class GameInstallLocatorTests
{
    private readonly IGameInstallLocator _locator = new PathsXmlGameInstallLocator();

    /// <summary>
    /// Активная версия, прочитанная из <c>paths.xml</c> НЕЗАВИСИМО от определителя.
    /// <para>
    /// Раньше здесь стояла строка «1.44.0.0», и в день обновления клиента до 1.45 два теста
    /// покраснели на верной работе кода: определитель вернул новую версию, а ожидание осталось
    /// старым. Проверять надо не номер, а СОГЛАСИЕ определителя с файлом — номер меняет Леста,
    /// и к правильности кода это отношения не имеет.
    /// </para>
    /// </summary>
    /// <param name="gameDir">Каталог игры.</param>
    /// <returns>Версия либо <c>null</c>.</returns>
    private static string? VersionFromPathsXml(string gameDir)
    {
        string path = Path.Combine(gameDir, "paths.xml");
        if (!File.Exists(path))
        {
            return null;
        }

        // Берём ИМЕННО строку с mask="*.mtmod": строка res_mods стоит раньше и дала бы
        // ту же цифру только по совпадению.
        foreach (string line in File.ReadAllLines(path))
        {
            if (!line.Contains("*.mtmod", StringComparison.Ordinal))
            {
                continue;
            }

            int start = line.IndexOf("./mods/", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            start += "./mods/".Length;
            int end = line.IndexOf('<', start);
            return end < 0 ? null : line[start..end].Trim();
        }

        return null;
    }

    // ------------------------------------------------------------------ реальная установка

    /// <summary>
    /// Главная проверка: активная версия берётся из paths.xml, а НЕ из «самого большого номера»
    /// в <c>mods\</c> (там копятся каталоги всех прошлых версий, начиная с 1.28.0.0).
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Detect_RealInstall_ReadsActiveVersionFromPathsXml()
    {
        string gameDir = Path.GetFullPath(TestEnvironment.RequireGameDir());

        string? expected = VersionFromPathsXml(gameDir);
        Skip.If(expected is null, "в paths.xml нет строки mask=\"*.mtmod\" — сверять не с чем");

        GameInstall? install = _locator.Detect(gameDir);

        Assert.NotNull(install);
        Assert.Equal(expected, install!.Version);
        Assert.Equal(gameDir, install.RootPath);
        Assert.Equal(Path.Combine(gameDir, "mods", expected!), install.ModsDir);
        Assert.Equal(Path.Combine(gameDir, "res_mods", expected!), install.ResModsDir);
        Assert.Equal(Path.Combine(gameDir, "python.log"), install.PythonLogPath);
        Assert.Equal(Path.Combine(gameDir, "paths.xml"), install.PathsXmlPath);
        Assert.Equal(Path.Combine(gameDir, "mods", "configs"), install.ModConfigsDir);
    }

    /// <summary>Завершающий разделитель в аргументе не должен просачиваться в <c>RootPath</c>.</summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Detect_RealInstall_TrailingSeparatorTrimmed()
    {
        string gameDir = Path.GetFullPath(TestEnvironment.RequireGameDir());

        string? expected = VersionFromPathsXml(gameDir);
        Skip.If(expected is null, "в paths.xml нет строки mask=\"*.mtmod\" — сверять не с чем");

        GameInstall? install = _locator.Detect(gameDir + @"\");

        Assert.NotNull(install);
        Assert.Equal(gameDir, install!.RootPath);
        Assert.Equal(expected, install.Version);
    }

    // ------------------------------------------------------------------ краевые случаи

    /// <summary>Пустой/пробельный путь — <c>null</c>, а не исключение.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Detect_BlankPath_ReturnsNull(string path) => Assert.Null(_locator.Detect(path));

    /// <summary>Несуществующий каталог — <c>null</c>.</summary>
    [Fact]
    public void Detect_MissingDirectory_ReturnsNull() =>
        Assert.Null(_locator.Detect(Path.Combine(Path.GetTempPath(), "no-such-game-" + Guid.NewGuid().ToString("N"))));

    /// <summary>Каталог есть, paths.xml нет — <c>null</c>.</summary>
    [Fact]
    public void Detect_NoPathsXml_ReturnsNull()
    {
        using CoreTempDir tmp = new();
        Assert.Null(_locator.Detect(tmp.Path));
    }

    /// <summary>Файл не разбирается как XML — <c>null</c>, без исключения.</summary>
    [Fact]
    public void Detect_MalformedXml_ReturnsNull()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", "<root><Paths><Path>./res_mods/1.44.0.0");

        Assert.Null(_locator.Detect(tmp.Path));
    }

    /// <summary>XML валиден, но подходящих строк <c>&lt;Path&gt;</c> нет — <c>null</c>.</summary>
    [Fact]
    public void Detect_NoVersionedPaths_ReturnsNull()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", """
            <root>
              <Paths>
                <Packages>
                  <Package type="sd,hd">./res/packages/scripts.pkg</Package>
                </Packages>
                <Path>./res</Path>
              </Paths>
            </root>
            """);

        Assert.Null(_locator.Detect(tmp.Path));
    }

    /// <summary>
    /// Игра установлена где угодно, а не в одном «привычном» каталоге — путь корня должен просто
    /// взяться из аргумента, никакой привязки к букве диска в коде быть не должно.
    /// </summary>
    [Fact]
    public void Detect_InstallOutsideDriveD_Works()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", PathsXml("1.44.0.0", "1.44.0.0"));

        GameInstall? install = _locator.Detect(tmp.Path);

        Assert.NotNull(install);
        Assert.Equal("1.44.0.0", install!.Version);
        Assert.Equal(Path.Combine(tmp.Path, "mods", "1.44.0.0"), install.ModsDir);
        Assert.Equal(Path.Combine(tmp.Path, "res_mods", "1.44.0.0"), install.ResModsDir);
        Assert.StartsWith(Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), install.RootPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Версии в двух строках разошлись: истина — строка с <c>mask="*.mtmod"</c>.
    /// Каталоги при этом каждый из своей строки: игра читает именно их.
    /// </summary>
    [Fact]
    public void Detect_VersionMismatch_MtmodLineWins()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", PathsXml(resModsVersion: "1.43.0.0", modsVersion: "1.44.0.0"));

        GameInstall? install = _locator.Detect(tmp.Path);

        Assert.NotNull(install);
        Assert.Equal("1.44.0.0", install!.Version);
        Assert.Equal(Path.Combine(tmp.Path, "mods", "1.44.0.0"), install.ModsDir);
        Assert.Equal(Path.Combine(tmp.Path, "res_mods", "1.43.0.0"), install.ResModsDir);
    }

    /// <summary>Строки с *.mtmod нет вовсе — версия берётся из res_mods, установка всё равно возвращается.</summary>
    [Fact]
    public void Detect_OnlyResModsLine_UsesItsVersion()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", """
            <root>
              <Paths>
                <Path cacheSubdirs="true">./res_mods/1.41.0.0</Path>
                <Path>./res</Path>
              </Paths>
            </root>
            """);

        GameInstall? install = _locator.Detect(tmp.Path);

        Assert.NotNull(install);
        Assert.Equal("1.41.0.0", install!.Version);
        Assert.Equal(Path.Combine(tmp.Path, "mods", "1.41.0.0"), install.ModsDir);
        Assert.Equal(Path.Combine(tmp.Path, "res_mods", "1.41.0.0"), install.ResModsDir);
    }

    /// <summary>Каталоги mods/res_mods физически отсутствуют — это НЕ ошибка.</summary>
    [Fact]
    public void Detect_MissingModDirectories_IsNotAnError()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", PathsXml("1.44.0.0", "1.44.0.0"));

        GameInstall? install = _locator.Detect(tmp.Path);

        Assert.NotNull(install);
        Assert.False(Directory.Exists(install!.ModsDir));
        Assert.False(Directory.Exists(install.ResModsDir));
    }

    /// <summary>Относительный путь приводится к абсолютному.</summary>
    [Fact]
    public void Detect_RelativePath_IsMadeAbsolute()
    {
        using CoreTempDir tmp = new();
        tmp.WriteFile("paths.xml", PathsXml("1.44.0.0", "1.44.0.0"));

        string saved = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(Path.GetDirectoryName(tmp.Path)!);
            GameInstall? install = _locator.Detect(Path.GetFileName(tmp.Path));

            Assert.NotNull(install);
            Assert.True(Path.IsPathRooted(install!.RootPath));
            Assert.Equal("1.44.0.0", install.Version);
        }
        finally
        {
            Directory.SetCurrentDirectory(saved);
        }
    }

    /// <summary>Замыкающий <c>./res</c> не должен опознаваться как версионный каталог.</summary>
    [Theory]
    [InlineData("1.44.0.0", true)]
    [InlineData("1.36.1.0", true)]
    [InlineData("res", false)]
    [InlineData("packages", false)]
    [InlineData("144", false)]
    [InlineData("", false)]
    public void LooksLikeVersion_RecognisesOnlyVersionSegments(string segment, bool expected) =>
        Assert.Equal(expected, PathsXmlGameInstallLocator.LooksLikeVersion(segment));

    /// <summary>Минимальный paths.xml в раскладке настоящего файла игры.</summary>
    /// <param name="resModsVersion">Версия в строке res_mods.</param>
    /// <param name="modsVersion">Версия в строке *.mtmod.</param>
    /// <returns>Текст XML.</returns>
    private static string PathsXml(string resModsVersion, string modsVersion) => $"""
        <root>
          <Paths>
            <Path cacheSubdirs="true">./res_mods/{resModsVersion}</Path>
            <Path mask="*.mtmod" mode="recursive" root="res">./mods/{modsVersion}</Path>
            <Packages>
              <Package type="sandbox,sd,hd">./res/packages/scripts.pkg</Package>
            </Packages>
            <Path>./res</Path>
          </Paths>
        </root>
        """;
}

/// <summary>
/// Одноразовый временный каталог для тестов Core-сервисов.
/// Всё, что тесты пишут, живёт здесь и здесь же умирает — в настоящую установку игры
/// и в каталог шаблона мода тесты не пишут НИЧЕГО.
/// </summary>
internal sealed class CoreTempDir : IDisposable
{
    /// <summary>Создаёт пустой временный каталог.</summary>
    /// <param name="prefix">Префикс имени, чтобы в <c>%TEMP%</c> было понятно, чей это мусор.</param>
    public CoreTempDir(string prefix = "wotcockpit")
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path);
    }

    /// <summary>Абсолютный путь к каталогу.</summary>
    public string Path { get; }

    /// <summary>Создаёт файл с текстом, попутно создавая недостающие подкаталоги.</summary>
    /// <param name="relativePath">Путь относительно каталога.</param>
    /// <param name="content">Содержимое.</param>
    /// <returns>Абсолютный путь к созданному файлу.</returns>
    public string WriteFile(string relativePath, string content = "")
    {
        string full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>Создаёт подкаталог.</summary>
    /// <param name="relativePath">Путь относительно каталога.</param>
    /// <returns>Абсолютный путь к каталогу.</returns>
    public string MakeDir(string relativePath)
    {
        string full = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    /// <summary>Путь внутри каталога, без создания.</summary>
    /// <param name="relativePath">Путь относительно каталога.</param>
    /// <returns>Абсолютный путь.</returns>
    public string Combine(string relativePath) =>
        System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // мусор в %TEMP% — не повод валить прогон
        }
    }
}
