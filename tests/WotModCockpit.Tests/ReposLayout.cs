using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.Tests;

/// <summary>
/// Где лежат внешние для кокпита каталоги, которые читают тесты: шаблон мода, дерево исходников
/// клиента, свои проекты модов. Вычисляется, а не зашивается.
///
/// <para><b>Зачем.</b> Путь, записанный в тест строкой, переживает только ту машину, на которой
/// его записали. Стоит перенести папку с репозиториями — и сотня тестов тихо уходит в
/// «пропущено» (путей нет — проверять нечем), а прогон выглядит почти зелёным, хотя половина
/// страховки исчезла.</para>
///
/// <para><b>Как.</b> Шаблон ищется тем же способом, что и в самом кокпите:
/// <see cref="TestEnvironment.TemplateDirVariable"/>, иначе
/// <see cref="ProjectScaffolder.ProbeTemplateDirectory"/> — подъём от каталога сборки до соседнего
/// <c>wot-mod-template</c> с <c>build.py</c>. Если кокпит свой шаблон найдёт, найдут и тесты,
/// и наоборот.</para>
///
/// <para><b>Своя машина — только по согласию.</b> Проекты модов и дерево исходников клиента —
/// это файлы конкретной машины, а не зависимости кокпита. Пока не задано
/// <c>WOTCOCKPIT_REAL_MACHINE=1</c>, <see cref="Root"/> и <see cref="ClientSource"/> указывают на
/// заведомо несуществующую папку, имя которой говорит, как их включить. Тесты, которые проверяют
/// наличие путей, пропустятся с сообщением, в котором сразу видно, что случилось.</para>
/// </summary>
internal static class ReposLayout
{
    /// <summary>
    /// Каталог со своими проектами модов: <see cref="TestEnvironment.ReposDirVariable"/>, иначе
    /// родитель найденного шаблона. Только при <c>WOTCOCKPIT_REAL_MACHINE=1</c>.
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>Шаблон мода.</summary>
    public static string Template { get; } = FindTemplate();

    /// <summary>
    /// Дерево исходников клиента: <see cref="TestEnvironment.ClientSourceVariable"/>, иначе
    /// <c>&lt;шаблон&gt;\.scratch\wot-src</c>. Только при <c>WOTCOCKPIT_REAL_MACHINE=1</c>.
    /// </summary>
    public static string ClientSource { get; } = FindClientSource();

    /// <summary>Проект мода в <see cref="Root"/>, например <c>wot-mod-my_mod</c>.</summary>
    /// <param name="name">Имя каталога проекта.</param>
    /// <returns>Путь к каталогу.</returns>
    public static string Project(string name) => Path.Combine(Root, name);

    /// <summary>Шаблон из переменной окружения или найденный рядом с кокпитом; пустая строка — не найден.</summary>
    /// <returns>Путь либо пустая строка.</returns>
    private static string ProbeTemplate() =>
        TestEnvironment.Read(TestEnvironment.TemplateDirVariable) ?? ProjectScaffolder.ProbeTemplateDirectory();

    private static string FindRoot()
    {
        if (!TestEnvironment.IsRealMachine)
        {
            return OffPath("repos");
        }

        if (TestEnvironment.Read(TestEnvironment.ReposDirVariable) is { } fromEnvironment)
        {
            return fromEnvironment;
        }

        string template = ProbeTemplate();
        if (template.Length > 0 && Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(template)) is { Length: > 0 } root)
        {
            return root;
        }

        return NotFoundPath("repos-root-not-found-next-to-the-cockpit");
    }

    private static string FindTemplate()
    {
        string template = ProbeTemplate();
        return template.Length > 0
            ? template
            : Path.Combine(NotFoundPath("template-not-found-next-to-the-cockpit"), "wot-mod-template");
    }

    private static string FindClientSource()
    {
        if (!TestEnvironment.IsRealMachine)
        {
            return OffPath("wot-src");
        }

        return TestEnvironment.Read(TestEnvironment.ClientSourceVariable)
               ?? Path.Combine(Template, ".scratch", "wot-src");
    }

    /// <summary>Заведомо несуществующий путь, имя которого говорит, как включить тесты на своей машине.</summary>
    /// <param name="what">Что именно не подключено.</param>
    /// <returns>Путь.</returns>
    private static string OffPath(string what) =>
        Path.Combine(NotFoundPath($"real-machine-tests-off (set {TestEnvironment.RealMachineVariable}=1)"), what);

    /// <summary>Заведомо несуществующий путь с говорящим именем в корне диска сборки.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Путь.</returns>
    private static string NotFoundPath(string name) =>
        Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory) ?? "C:\\", name);
}
