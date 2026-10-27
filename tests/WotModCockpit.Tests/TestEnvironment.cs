using System.Diagnostics;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Переменные окружения, которыми включаются тесты, зависящие от машины, и помощники для их
/// пропуска.
/// <para><b>Правило.</b> Обычный прогон <c>dotnet test</c> герметичен: он не читает ни установку
/// игры, ни соседние проекты модов, ни дерево исходников клиента, ни корпус чужих модов, ни файлы
/// в <c>%APPDATA%</c>. Тесты, которым всё это нужно, помечены <c>[Trait("Env", "RealMachine")]</c>
/// и пропускаются с понятной причиной, пока не задано <c>WOTCOCKPIT_REAL_MACHINE=1</c>. Тогда же
/// их можно отфильтровать: <c>dotnet test --filter "Env!=RealMachine"</c>.</para>
/// <para>Шаблон мода и Python — не свойства чьей-то машины, а обычные зависимости. Тесты на них
/// помечены <c>[Trait("Needs", "Template")]</c> и <c>[Trait("Needs", "Python")]</c>, идут без
/// переключателя и пропускаются, если зависимости нет.</para>
/// <para>Пути к внешнему задаются переменными: <see cref="GameDirVariable"/>,
/// <see cref="ReposDirVariable"/>, <see cref="TemplateDirVariable"/>,
/// <see cref="ClientSourceVariable"/> (+ <see cref="ClientSourceVersionVariable"/>),
/// <see cref="ModCorpusVariable"/>, <see cref="PythonVariable"/>.</para>
/// </summary>
internal static class TestEnvironment
{
    /// <summary>Переключатель тестов на настоящей машине: <c>1</c> — включены.</summary>
    public const string RealMachineVariable = "WOTCOCKPIT_REAL_MACHINE";

    /// <summary>Каталог установленной игры (тот, где лежит <c>paths.xml</c>).</summary>
    public const string GameDirVariable = "WOTCOCKPIT_GAME_DIR";

    /// <summary>Каталог, в котором лежат свои проекты модов (<c>wot-mod-*</c>).</summary>
    public const string ReposDirVariable = "WOTCOCKPIT_REPOS_DIR";

    /// <summary>Каталог шаблона мода <c>wot-mod-template</c>. Его же читает и сам кокпит.</summary>
    public const string TemplateDirVariable = "WOTCOCKPIT_TEMPLATE_DIR";

    /// <summary>Дерево декомпилированных исходников клиента (<c>client\</c>, <c>common\</c> …).</summary>
    public const string ClientSourceVariable = "WOTCOCKPIT_CLIENT_SRC";

    /// <summary>
    /// Версия клиента, из которого извлечено дерево, если у дерева нет отметки
    /// <c>.cockpit-client.json</c> и версия не видна из имени каталога (<c>wot-src-1.44</c>).
    /// </summary>
    public const string ClientSourceVersionVariable = "WOTCOCKPIT_CLIENT_SRC_VERSION";

    /// <summary>Корпус распакованных сторонних модов для проверки правил preflight.</summary>
    public const string ModCorpusVariable = "WOTCOCKPIT_MOD_CORPUS_DIR";

    /// <summary>Интерпретатор Python 3 для тестов (по умолчанию — найденный автоматически).</summary>
    public const string PythonVariable = "WOTCOCKPIT_PYTHON";

    /// <summary>Включены ли тесты на настоящей машине.</summary>
    public static bool IsRealMachine { get; } = Read(RealMachineVariable) is "1" or "true" or "True" or "TRUE";

    /// <summary>Причина пропуска для теста на настоящей машине, когда переключатель выключен.</summary>
    public static string RealMachineOffReason =>
        $"тест читает файлы этой машины и включается переменной {RealMachineVariable}=1";

    /// <summary>
    /// Каталог игры для тестов на настоящей машине либо <c>null</c>: <see cref="GameDirVariable"/>,
    /// а при включённом <see cref="RealMachineVariable"/> без неё — та установка, которую находит
    /// сам кокпит (реестр, типовые папки).
    /// </summary>
    public static string? GameDir => Read(GameDirVariable) ?? (IsRealMachine ? Core.Services.GameDirectoryFinder.FindFirst() : null);

    /// <summary>Значение переменной окружения без пробелов и кавычек по краям; пустое — <c>null</c>.</summary>
    /// <param name="name">Имя переменной.</param>
    /// <returns>Значение либо <c>null</c>.</returns>
    public static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name)?.Trim().Trim('"').Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>Пропускает тест, если тесты на настоящей машине не включены.</summary>
    public static void RequireRealMachine() => Skip.IfNot(IsRealMachine, RealMachineOffReason);

    /// <summary>
    /// Каталог настоящей установки игры с <c>paths.xml</c>. Тест пропускается, если переключатель
    /// выключен или игры по указанному пути нет. Установка используется только на чтение.
    /// </summary>
    /// <returns>Каталог игры.</returns>
    public static string RequireGameDir()
    {
        RequireRealMachine();

        string? dir = GameDir;
        Skip.If(dir is null || !File.Exists(Path.Combine(dir, "paths.xml")),
            $"нет установки игры ({dir ?? "каталог не задан"}, paths.xml) — укажите его в {GameDirVariable}");
        return Path.TrimEndingDirectorySeparator(dir!);
    }

    /// <summary>
    /// Настоящая установка игры, разобранная по <c>paths.xml</c>: активная версия берётся оттуда,
    /// а не угадывается.
    /// </summary>
    /// <returns>Установка.</returns>
    public static GameInstall RequireGame()
    {
        string dir = RequireGameDir();
        GameInstall? game = new PathsXmlGameInstallLocator().Detect(dir);
        Skip.If(game is null, $"paths.xml в {dir} не разобрался");
        return game!;
    }

    /// <summary>
    /// Каталог со своими проектами модов (<see cref="ReposLayout.Root"/>). Тест пропускается,
    /// если переключатель выключен или каталога нет.
    /// </summary>
    /// <returns>Каталог.</returns>
    public static string RequireReposDir()
    {
        RequireRealMachine();
        Skip.IfNot(Directory.Exists(ReposLayout.Root),
            $"нет каталога с проектами модов ({ReposLayout.Root}) — укажите его в {ReposDirVariable}");
        return ReposLayout.Root;
    }

    /// <summary>
    /// Шаблон мода (<see cref="ReposLayout.Template"/>). Тест пропускается, если шаблона нет.
    /// </summary>
    /// <returns>Каталог шаблона.</returns>
    public static string RequireTemplate()
    {
        Skip.IfNot(File.Exists(Path.Combine(ReposLayout.Template, "build.py")), TemplateMissingReason);
        return ReposLayout.Template;
    }

    /// <summary>Причина пропуска, когда шаблона мода нет.</summary>
    public static string TemplateMissingReason =>
        $"нет шаблона мода ({ReposLayout.Template}) — положите wot-mod-template рядом с кокпитом " +
        $"или укажите его в {TemplateDirVariable}";

    /// <summary>
    /// Дерево исходников клиента (<see cref="ReposLayout.ClientSource"/>). Тест пропускается, если
    /// переключатель выключен или дерева нет.
    /// </summary>
    /// <returns>Каталог дерева.</returns>
    public static string RequireClientSource()
    {
        RequireRealMachine();
        Skip.IfNot(Directory.Exists(ReposLayout.ClientSource),
            $"нет дерева исходников клиента ({ReposLayout.ClientSource}) — укажите его в {ClientSourceVariable}");
        return ReposLayout.ClientSource;
    }

    /// <summary>
    /// Версия дерева исходников клиента: из отметки или имени каталога, а без них — из
    /// <see cref="ClientSourceVersionVariable"/>. <c>null</c> — версия неизвестна.
    /// </summary>
    public static string? ClientSourceVersion =>
        ClientSourceTrees.VersionOf(ReposLayout.ClientSource) ?? Read(ClientSourceVersionVariable);

    /// <summary>
    /// Дерево исходников клиента — той версии, на которой сняты эталонные числа. Для проверок,
    /// где версионно-независимая часть идёт на любом дереве, а точные числа — только на эталоне.
    /// </summary>
    /// <param name="version">Версия клиента (<c>1.44</c>).</param>
    /// <returns>Признак.</returns>
    public static bool ClientSourceIs(string version) =>
        ClientSourceVersion is { } actual &&
        ClientVersions.Compare(ClientVersions.Short(actual), ClientVersions.Short(version)) == 0;

    /// <summary>
    /// Эталонное дерево клиента конкретной версии: на нём сняты точные числа (файлов, символов,
    /// событий). Дерево другой версии или другого декомпилятора даёт другие числа законно, поэтому
    /// тест не падает, а пропускается.
    /// </summary>
    /// <param name="version">Версия клиента, на которой сняты числа (<c>1.44</c>).</param>
    /// <returns>Каталог дерева.</returns>
    public static string RequireReferenceClientSource(string version)
    {
        string dir = RequireClientSource();
        Skip.IfNot(ClientSourceIs(version),
            $"дерево клиента не {version} (версия: {ClientSourceVersion ?? "неизвестна"}) — эталонные числа " +
            $"не применимы; версию дерева без отметки {ClientSourceTrees.MarkerFile} задаёт {ClientSourceVersionVariable}");
        return dir;
    }
}

/// <summary>
/// Python для тестов, которые запускают процессы: <see cref="TestEnvironment.PythonVariable"/>,
/// иначе тот же автопоиск, что у кокпита (<see cref="PythonLocator.Locate"/>).
/// <para>Ввод-вывод процесса всегда в UTF-8: без этого Python пишет в канал в кодовой странице
/// ANSI, и кириллица в выводе (русские комментарии, временные пути под русским именем
/// пользователя) превращается в кракозябры или в <c>UnicodeEncodeError</c> на нерусской Windows.</para>
/// </summary>
internal static class TestPython
{
    private static readonly Lazy<PythonInstall?> Found = new(Find, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Найденный интерпретатор либо <c>null</c>.</summary>
    public static PythonInstall? Install => Found.Value;

    /// <summary>Причина пропуска, когда Python нет.</summary>
    public static string MissingReason =>
        TestEnvironment.Read(TestEnvironment.PythonVariable) is { } explicitPath
            ? $"{TestEnvironment.PythonVariable}={explicitPath} не запускается как Python"
            : $"Python 3 не найден (ни в PATH, ни через py) — поставьте его или укажите {TestEnvironment.PythonVariable}";

    /// <summary>Путь к интерпретатору; тест пропускается, если Python нет.</summary>
    /// <returns>Абсолютный путь к <c>python.exe</c>.</returns>
    public static string Require()
    {
        Skip.If(Install is null, MissingReason);
        return Install!.ExecutablePath;
    }

    /// <summary>Переводит ввод-вывод дочернего Python в UTF-8 — на любой локали Windows.</summary>
    /// <param name="psi">Параметры запуска.</param>
    public static void UseUtf8(ProcessStartInfo psi)
    {
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
    }

    private static PythonInstall? Find()
    {
        string? explicitPath = TestEnvironment.Read(TestEnvironment.PythonVariable);
        PythonInstall? found = explicitPath is null ? PythonLocator.Locate() : PythonLocator.Resolve(explicitPath);
        return found is { IsPython3: true } ? found : null;
    }
}
