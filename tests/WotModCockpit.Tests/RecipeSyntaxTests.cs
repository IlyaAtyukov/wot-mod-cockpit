using System.Diagnostics;
using System.Text;
using WotModCockpit.Core.Recipes;

namespace WotModCockpit.Tests;

/// <summary>
/// Общие для тестов рецептов пути к шаблону мода и запуск его валидатора синтаксиса.
/// </summary>
public static class RecipeToolchain
{
    /// <summary>Корень шаблона мода (<see cref="ReposLayout.Template"/>).</summary>
    public static readonly string TemplateDir = ReposLayout.Template;

    /// <summary>Валидатор синтаксиса Python 2.7 из шаблона.</summary>
    public static string SyntaxChecker => Path.Combine(TemplateDir, "tools", "check_py2_syntax.py");

    /// <summary>Валидатор на месте и есть Python 3, чтобы его запустить.</summary>
    public static bool CheckerAvailable => File.Exists(SyntaxChecker) && TestPython.Install is not null;

    /// <summary>Чего не хватает для запуска валидатора — для сообщения о пропуске теста.</summary>
    public static string MissingReason =>
        !Directory.Exists(TemplateDir) ? TestEnvironment.TemplateMissingReason
        : !File.Exists(SyntaxChecker) ? $"нет валидатора ({SyntaxChecker})"
        : TestPython.MissingReason;

    /// <summary>Запускает валидатор на файле и возвращает код возврата и вывод.</summary>
    /// <param name="path">Путь к проверяемому файлу или каталогу.</param>
    /// <returns>Код возврата и объединённый вывод.</returns>
    public static (int ExitCode, string Output) RunSyntaxCheck(string path)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestPython.Install?.ExecutablePath ?? "python",
            WorkingDirectory = TemplateDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(SyntaxChecker);
        psi.ArgumentList.Add(path);
        TestPython.UseUtf8(psi);

        using Process p = Process.Start(psi)!;
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, stdout + stderr);
    }
}

/// <summary>
/// ГЛАВНЫЙ ТЕСТ КАТАЛОГА РЕЦЕПТОВ.
///
/// <para>Невалидный рецепт хуже отсутствующего: пользователь вставит его в мод, получит
/// SyntaxError уже внутри клиента (тело мода компилируется ТАМ, а не при сборке)
/// и потратит вечер, разыскивая ошибку в чужом коде. Поэтому каждый рецепт — и в исходном
/// виде, и после подстановки паспорта проекта — прогоняется через настоящий
/// <c>tools/check_py2_syntax.py</c>, тот же самый, который стоит гейтом в build.py.</para>
/// </summary>
public class RecipeSyntaxTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Создаёт временный каталог для отрендеренных рецептов.</summary>
    public RecipeSyntaxTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-recipes-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог; не удалился — не беда.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Каждый рецепт после подстановки реального паспорта проекта — валидный Python 2.7.
    /// Это и есть контракт каталога: то, что вставили, компилируется внутри клиента.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void EveryRecipe_AfterSubstitution_PassesPython27SyntaxCheck()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        var service = new RecipeService();
        RecipeParameters parameters = RecipeParameters.From("ru.local.battle_timer", "battle_timer");

        foreach (Recipe recipe in service.All)
        {
            string code = service.Render(recipe, parameters);
            File.WriteAllText(Path.Combine(_temp, $"{recipe.Id.ToLowerInvariant()}.py"), code, new UTF8Encoding(false));
        }

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, $"check_py2_syntax.py вернул {exitCode}:\n{output}");
        Assert.DoesNotContain("[FAIL]", output, StringComparison.Ordinal);
    }

    /// <summary>
    /// Рецепты валидны и БЕЗ подстановки: токены-заглушки (<c>MyMod</c>, <c>my_mod</c>)
    /// сами по себе законный питон, поэтому каталог годен и при незаданном проекте.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void EveryRecipe_Unsubstituted_PassesPython27SyntaxCheck()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        var service = new RecipeService();
        foreach (Recipe recipe in service.All)
        {
            File.WriteAllText(Path.Combine(_temp, $"{recipe.Id.ToLowerInvariant()}.py"), recipe.Code, new UTF8Encoding(false));
        }

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, $"check_py2_syntax.py вернул {exitCode}:\n{output}");
    }

    /// <summary>
    /// Подстановка с экзотическим, но допустимым паспортом тоже даёт валидный питон:
    /// длинное имя, цифры и подчёркивания не должны ломать ни строковые литералы,
    /// ни отступы.
    /// </summary>
    [SkippableFact]
    [Trait("Needs", "Template")]
    [Trait("Needs", "Python")]
    public void EveryRecipe_WithUnusualNames_PassesPython27SyntaxCheck()
    {
        Skip.IfNot(RecipeToolchain.CheckerAvailable, RecipeToolchain.MissingReason);

        var service = new RecipeService();
        RecipeParameters parameters = RecipeParameters.From(
            "org.example.very_long_mod_name_42", "very_long_mod_name_42", "Very Long Mod Name 42", "VeryLongModName42");

        foreach (Recipe recipe in service.All)
        {
            File.WriteAllText(
                Path.Combine(_temp, $"{recipe.Id.ToLowerInvariant()}.py"),
                service.Render(recipe, parameters),
                new UTF8Encoding(false));
        }

        (int exitCode, string output) = RecipeToolchain.RunSyntaxCheck(_temp);

        Assert.True(exitCode == 0, $"check_py2_syntax.py вернул {exitCode}:\n{output}");
    }

    /// <summary>
    /// Каждый рецепт объявляет кодировку. Файл с кириллицей без строки
    /// <c>coding: utf-8</c> Python 2.7 компилировать отказывается (PEP 263), а комментарии
    /// во всех рецептах русские — то есть без этой строки рецепт мёртв внутри клиента.
    /// </summary>
    [Fact]
    public void EveryRecipe_DeclaresUtf8Coding()
    {
        foreach (Recipe recipe in RecipeLibrary.All)
        {
            string head = string.Join('\n', recipe.Code.Split('\n').Take(2));
            Assert.True(
                head.Contains("coding", StringComparison.Ordinal) && head.Contains("utf-8", StringComparison.Ordinal),
                $"{recipe.Id}: нет строки '# -*- coding: utf-8 -*-' в первых двух строках");
        }
    }

    /// <summary>
    /// Ни один рецепт не ВЫЗЫВАЕТ LOG_DEBUG: на релизном клиенте это пустая заглушка
    /// (SVR_RELEASE=4 ниже _logLevel=RELEASE=5), записи не будет вообще.
    /// <para>Упоминать её в комментарии можно и нужно — рецепт R02 именно этим и объясняет,
    /// почему брать debug_utils.LOG_DEBUG нельзя. Поэтому проверяется вызов, а не подстрока.</para>
    /// </summary>
    [Fact]
    public void NoRecipe_CallsLogDebug()
    {
        foreach (Recipe recipe in RecipeLibrary.All)
        {
            foreach (string line in recipe.Code.Split('\n'))
            {
                string t = line.TrimStart();
                if (t.StartsWith('#')) continue;

                Assert.False(
                    t.Contains("LOG_DEBUG(", StringComparison.Ordinal),
                    $"{recipe.Id}: вызов LOG_DEBUG — на релизном клиенте это no-op: '{t.Trim()}'");
            }
        }
    }

    /// <summary>
    /// Ни один рецепт не оставляет голого <c>print</c>: в python.log он попадает без уровня,
    /// без имени модуля и может бросить UnicodeEncodeError.
    /// </summary>
    [Fact]
    public void NoRecipe_UsesPrint()
    {
        foreach (Recipe recipe in RecipeLibrary.All)
        {
            foreach (string line in recipe.Code.Split('\n'))
            {
                string t = line.TrimStart();
                Assert.False(
                    t.StartsWith("print ", StringComparison.Ordinal) || t.StartsWith("print(", StringComparison.Ordinal),
                    $"{recipe.Id}: голый print — '{t.Trim()}'");
            }
        }
    }
}
