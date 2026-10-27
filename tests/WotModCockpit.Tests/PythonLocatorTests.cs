using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="PythonLocator"/>. Ничего не устанавливают и не меняют — только ищут
/// уже установленный Python 3.x и спрашивают у него версию.
/// <para>Тесты, которым нужен установленный Python, пропускаются, если в PATH нет ни
/// <c>python</c>, ни лаунчера <c>py</c>. Предусловие проверяется НЕЗАВИСИМО от поисковика:
/// иначе сломанный поиск выглядел бы как «Python не установлен», и тест молча пропускался бы.</para>
/// </summary>
public class PythonLocatorTests
{
    /// <summary>Есть Python 3.x — он обязан находиться: без него build.py не запустить.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void Locate_FindsPython3()
    {
        RequirePythonOnPath("python.exe", "py.exe");

        PythonInstall? python = PythonLocator.Locate();

        Assert.NotNull(python);
        Assert.True(python!.IsPython3, $"нашлось: {python.ExecutablePath} ({python.Version})");
        Assert.Equal(3, python.Major);
        Assert.True(python.Minor >= 6, $"слишком старый Python: {python.Version}");
        Assert.True(File.Exists(python.ExecutablePath), python.ExecutablePath);
        Assert.True(Path.IsPathRooted(python.ExecutablePath), python.ExecutablePath);
    }

    /// <summary>Явно указанная команда <c>python</c> из PATH должна разрешаться в реальный exe.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void Resolve_PathCommand_ReturnsRealExecutable()
    {
        RequirePythonOnPath("python.exe");

        PythonInstall? python = PythonLocator.Resolve("python");

        Assert.NotNull(python);
        Assert.True(File.Exists(python!.ExecutablePath), python.ExecutablePath);
        Assert.StartsWith("3.", python.Version);
    }

    /// <summary>Абсолютный путь к найденному exe должен разрешаться сам в себя.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void Resolve_AbsolutePath_Works()
    {
        RequirePythonOnPath("python.exe", "py.exe");

        PythonInstall? found = PythonLocator.Locate();
        Assert.NotNull(found);

        PythonInstall? again = PythonLocator.Resolve(found!.ExecutablePath);

        Assert.NotNull(again);
        Assert.Equal(found.ExecutablePath, again!.ExecutablePath, ignoreCase: true);
        Assert.Equal(found.Version, again.Version);
    }

    /// <summary>Заведомо отсутствующая команда — <c>null</c>, а не <c>Win32Exception</c>.</summary>
    [Fact]
    public void Resolve_MissingCommand_ReturnsNullWithoutThrowing() =>
        Assert.Null(PythonLocator.Resolve("python-nope-" + Guid.NewGuid().ToString("N")));

    /// <summary>Пустая строка = «искать самому».</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_Blank_FallsBackToLocate(string? command) =>
        Assert.Equal(PythonLocator.Locate()?.ExecutablePath, PythonLocator.Resolve(command)?.ExecutablePath);

    /// <summary>Перечисление не должно содержать дублей по пути.</summary>
    [SkippableFact]
    [Trait("Needs", "Python")]
    public void LocateAll_HasNoDuplicatePaths()
    {
        RequirePythonOnPath("python.exe", "py.exe");

        IReadOnlyList<PythonInstall> all = PythonLocator.LocateAll();

        Assert.NotEmpty(all);
        Assert.Equal(
            all.Count,
            all.Select(i => i.ExecutablePath.ToUpperInvariant()).Distinct().Count());
    }

    /// <summary>Разбор версии в <see cref="PythonInstall"/> — чистая арифметика строк.</summary>
    [Theory]
    [InlineData("3.12.10", 3, 12, true)]
    [InlineData("3.9", 3, 9, true)]
    [InlineData("2.7.18", 2, 7, false)]
    [InlineData("", 0, 0, false)]
    public void PythonInstall_ParsesVersionParts(string version, int major, int minor, bool isPython3)
    {
        PythonInstall install = new(@"C:\x\python.exe", version, "test");

        Assert.Equal(major, install.Major);
        Assert.Equal(minor, install.Minor);
        Assert.Equal(isPython3, install.IsPython3);
    }

    /// <summary>
    /// Пропускает тест, если ни одного из исполняемых файлов нет в PATH. Заглушки нулевого
    /// размера из <c>WindowsApps</c> (они открывают магазин) за Python не считаются.
    /// </summary>
    /// <param name="names">Имена исполняемых файлов: <c>python.exe</c>, <c>py.exe</c>.</param>
    private static void RequirePythonOnPath(params string[] names)
    {
        string[] dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        bool found = names.Any(name => dirs.Any(dir =>
        {
            try
            {
                var file = new FileInfo(Path.Combine(dir.Trim().Trim('"'), name));
                return file.Exists && file.Length > 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                return false;   // мусор в PATH — не повод валить тест
            }
        }));

        Skip.IfNot(found, $"Python не установлен: в PATH нет {string.Join(" и ", names)}");
    }
}
