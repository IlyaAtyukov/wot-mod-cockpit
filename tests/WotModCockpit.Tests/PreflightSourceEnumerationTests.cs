using WotModCockpit.Core.Preflight;

namespace WotModCockpit.Tests;

/// <summary>
/// Какие файлы префлайт вообще берёт в разбор (<see cref="PreflightAnalyzer.EnumerateSources"/>).
///
/// <para><b>Что было не так.</b> Префлайт пропускает служебные папки проекта — <c>.scratch</c>,
/// <c>dist</c>, <c>.tools</c>, <c>__pycache__</c>, — но искал их имена в ПОЛНОМ пути файла.
/// Проект, у которого папка с таким именем стояла где-нибудь выше по пути, например
/// <c>D:\dist\моды\мой-мод</c>, разбирался как ПУСТОЙ: ноль файлов, ноль ошибок, «всё хорошо».
/// Ни предупреждения, ни следа в выводе.</para>
///
/// <para>Нашлось это при переезде папки репозиториев: корпус чужих модов для замеров правил
/// лёг в <c>wot-mod-template\.scratch\mod-corpus</c>, и шесть замеров «сколько ложных
/// срабатываний» прошли ВПУСТУЮ — на нуле файлов любой бюджет соблюдён.</para>
/// </summary>
public sealed class PreflightSourceEnumerationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "preflight-enum-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог не критичен
        }
    }

    /// <summary>
    /// ГЛАВНАЯ ПРОВЕРКА: проект, лежащий ПОД папкой со служебным именем, разбирается целиком.
    /// Имя папки выше проверяемого каталога — не дело префлайта.
    /// </summary>
    [Theory]
    [InlineData("dist")]
    [InlineData(".scratch")]
    [InlineData(".tools")]
    [InlineData("__pycache__")]
    public void ProjectUnderAServiceNamedFolder_IsStillAnalyzed(string ancestor)
    {
        string src = Path.Combine(_root, ancestor, "my-mod", "src");
        string file = Write(src, @"res\scripts\client\gui\mods\mod_x.py");

        string[] found = [.. PreflightAnalyzer.EnumerateSources(src)];

        Assert.True(found.Length == 1 && found[0] == file,
            $"проект под папкой «{ancestor}» разобран как пустой: найдено файлов {found.Length}");
    }

    /// <summary>
    /// А служебные папки ВНУТРИ проверяемого каталога по-прежнему пропускаются — ради этого
    /// правило и писалось: дамп клиента в <c>.scratch</c> и сборка в <c>dist</c> — не код мода.
    /// </summary>
    [Fact]
    public void ServiceFoldersInsideTheProject_AreStillSkipped()
    {
        string src = Path.Combine(_root, "my-mod");
        string ok = Write(src, @"src\res\scripts\client\gui\mods\mod_x.py");
        Write(src, @".scratch\wot-src\client\gui\a.py");
        Write(src, @"dist\_stage\res\b.py");
        Write(src, @".tools\venv\c.py");
        Write(src, @"src\res\scripts\client\gui\mods\__pycache__\d.py");

        string[] found = [.. PreflightAnalyzer.EnumerateSources(src)];

        Assert.Equal([ok], found);
    }

    private static string Write(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "import BigWorld\n");
        return path;
    }
}
