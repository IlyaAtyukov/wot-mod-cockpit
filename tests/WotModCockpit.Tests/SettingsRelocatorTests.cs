using WotModCockpit.Core.Models;
using WotModCockpit.Core.Scaffold;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Перенос путей из настроек после переезда папки с репозиториями (<see cref="SettingsRelocator"/>).
///
/// <para><b>Что проверяется.</b> Если папку с репозиториями перенесли, пути в настройках
/// переносятся по совпадающему хвосту. Без переноса кокпит запускается, но в настройках остаются
/// пути в несуществующую папку — и все свои моды в «Модах» становятся чужими, а исходники клиента
/// пропадают из браузера API. Ни ошибки, ни предупреждения.</para>
///
/// <para><b>Почему дерево папок временное, а проверка существования — своя.</b> Перенос
/// примеряет хвост пути ко ВСЕМ родителям каталога кокпита, вплоть до корня диска. На машине,
/// где старая папка ещё жива, настоящий диск подсунул бы свой ответ, и тест проверял бы
/// машину, а не правило. Поэтому существование спрашивается только внутри временного дерева.
/// Настоящий шаблон рядом с кокпитом проверяет отдельный, последний тест (только на настоящей машине).</para>
/// </summary>
public sealed class SettingsRelocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "relocator-" + Guid.NewGuid().ToString("N"));

    public SettingsRelocatorTests() => Directory.CreateDirectory(_root);

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
    /// Типичный переезд: <c>C:\Users\dev\source\repos</c> → <c>D:\dev\repos</c>.
    /// Все виды путей переехали, и выбор пользователя сохранился — дамп 1.45, а не
    /// первый попавшийся <c>wot-src</c>.
    /// </summary>
    [Fact]
    public void MovedReposFolder_EveryPathFollowsToTheNewPlace()
    {
        string newRepos = Dir(@"D\dev\repos");
        string template = Project(newRepos, "wot-mod-template");
        string alpha = Project(newRepos, "wot-mod-alpha");
        Dir(Path.Combine(template, @".scratch\wot-src\client"));        // дамп 1.44 — НЕ он
        string dump145 = Dir(Path.Combine(template, @".scratch\wot-src-1.45\client"));
        string anchor = Dir(Path.Combine(newRepos, @"wot-mod-cockpit\src\WotModCockpit.App\bin\Debug"));

        string oldRepos = Path.Combine(_root, @"C\Users\dev\source\repos");
        var settings = new CockpitSettings
        {
            WotSrcDir = Path.Combine(oldRepos, @"wot-mod-template\.scratch\wot-src-1.45"),
            LastProjectDir = Path.Combine(oldRepos, "wot-mod-alpha"),
            TemplateDir = Path.Combine(oldRepos, "wot-mod-template"),
            RecentProjects = [Path.Combine(oldRepos, "wot-mod-alpha"), Path.Combine(oldRepos, "wot-mod-template")],
        };

        IReadOnlyList<Relocation> moved = SettingsRelocator.Heal(settings, anchor, InTempTree);

        Assert.Equal(Path.GetDirectoryName(dump145), settings.WotSrcDir);
        Assert.Equal(alpha, settings.LastProjectDir);
        Assert.Equal(template, settings.TemplateDir);
        Assert.Equal([alpha, template], settings.RecentProjects);
        Assert.Equal(5, moved.Count);
    }

    /// <summary>Путь, который существует, не трогается никогда, даже если рядом есть «кандидат».</summary>
    [Fact]
    public void ExistingPath_IsNeverTouched()
    {
        string newRepos = Dir(@"D\dev\repos");
        Project(newRepos, "wot-mod-template");
        string anchor = Dir(Path.Combine(newRepos, @"wot-mod-cockpit\bin"));

        string elsewhere = Project(Dir(@"elsewhere\source\repos"), "wot-mod-template");
        var settings = new CockpitSettings { LastProjectDir = elsewhere, TemplateDir = elsewhere };

        IReadOnlyList<Relocation> moved = SettingsRelocator.Heal(settings, anchor, InTempTree);

        Assert.Equal(elsewhere, settings.LastProjectDir);
        Assert.Equal(elsewhere, settings.TemplateDir);
        Assert.Empty(moved);
    }

    /// <summary>
    /// Для пути, которому нового места нет, запись остаётся как была. Удалять запись
    /// пользователя молча нельзя: может, диск просто не подключён.
    /// </summary>
    [Fact]
    public void NoNewPlace_TheValueIsKept_NotErased()
    {
        string newRepos = Dir(@"D\dev\repos");
        string anchor = Dir(Path.Combine(newRepos, @"wot-mod-cockpit\bin"));
        string gone = Path.Combine(_root, @"C\Users\dev\source\repos\wot-mod-gone");
        var settings = new CockpitSettings { RecentProjects = [gone] };

        IReadOnlyList<Relocation> moved = SettingsRelocator.Heal(settings, anchor, InTempTree);

        Assert.Equal([gone], settings.RecentProjects);
        Assert.Empty(moved);
    }

    /// <summary>
    /// Папку репозиториев не только перенесли, но и переименовали: общего с прежним путём —
    /// одно имя проекта. Проект всё равно находится рядом с кокпитом по имени.
    /// </summary>
    [Fact]
    public void RenamedReposFolder_ProjectIsFoundBySiblingName()
    {
        string dev = Dir(@"E\work");
        string alpha = Project(dev, "wot-mod-alpha");
        string anchor = Dir(Path.Combine(dev, @"wot-mod-cockpit\bin"));
        var settings = new CockpitSettings
        {
            LastProjectDir = Path.Combine(_root, @"D\dev\repos\wot-mod-alpha"),
        };

        SettingsRelocator.Heal(settings, anchor, InTempTree);

        Assert.Equal(alpha, settings.LastProjectDir);
    }

    /// <summary>
    /// Исходники клиента не уезжают в папку, похожую только именем.
    /// <para>
    /// Хвост из одной части здесь запрещён: у кокпита у самого есть папка <c>src</c>, и путь
    /// <c>…\old\src</c> иначе «нашёлся» бы в ней. И даже при совпавшем длинном хвосте нужен
    /// признак дампа — папка <c>client</c> внутри.
    /// </para>
    /// </summary>
    [Fact]
    public void ClientSource_IsNotMovedIntoALookalikeFolder()
    {
        string newRepos = Dir(@"D\dev\repos");
        string cockpit = Dir(Path.Combine(newRepos, "wot-mod-cockpit"));
        // Приманка С ПРИЗНАКОМ ДАМПА: client\ внутри есть, и по содержимому она проходит.
        // Не пустить туда путь обязано ТОЛЬКО правило «хвост минимум из двух частей» —
        // иначе тест проверял бы признак дампа, а правило про хвост могло бы сломаться молча.
        Dir(Path.Combine(cockpit, @"src\client"));
        Dir(Path.Combine(newRepos, @"wot-mod-template\.scratch\wot-src-1.45"));   // без client\
        string anchor = Dir(Path.Combine(cockpit, "bin"));

        string oneSegment = Path.Combine(_root, @"C\old\src");
        string noClientDir = Path.Combine(_root, @"C\Users\dev\source\repos\wot-mod-template\.scratch\wot-src-1.45");

        var first = new CockpitSettings { WotSrcDir = oneSegment };
        var second = new CockpitSettings { WotSrcDir = noClientDir };
        SettingsRelocator.Heal(first, anchor, InTempTree);
        SettingsRelocator.Heal(second, anchor, InTempTree);

        Assert.Equal(oneSegment, first.WotSrcDir);
        Assert.Equal(noClientDir, second.WotSrcDir);
    }

    /// <summary>Каталог с тем же именем, но без <c>build.py</c> — не проект, и путь туда не уезжает.</summary>
    [Fact]
    public void ProjectDirWithoutBuildPy_IsNotAccepted()
    {
        string newRepos = Dir(@"D\dev\repos");
        Dir(Path.Combine(newRepos, "wot-mod-alpha"));          // пустой, без build.py
        string anchor = Dir(Path.Combine(newRepos, @"wot-mod-cockpit\bin"));
        string stale = Path.Combine(_root, @"C\Users\dev\source\repos\wot-mod-alpha");
        var settings = new CockpitSettings { LastProjectDir = stale };

        SettingsRelocator.Heal(settings, anchor, InTempTree);

        Assert.Equal(stale, settings.LastProjectDir);
    }

    /// <summary>
    /// Через хранилище: перенесённое сохраняется, и второй запуск уже ничего не переносит.
    /// Перенос обязан попасть на диск — остальные службы читают настройки сами.
    /// </summary>
    [Fact]
    public void HealStore_SavesTheMove_AndTheSecondRunFindsNothing()
    {
        string newRepos = Dir(@"D\dev\repos");
        string alpha = Project(newRepos, "wot-mod-alpha");
        string anchor = Dir(Path.Combine(newRepos, @"wot-mod-cockpit\bin"));
        var store = new JsonSettingsStore(Path.Combine(_root, "settings.json"));
        store.Save(new CockpitSettings
        {
            LastProjectDir = Path.Combine(_root, @"C\Users\dev\source\repos\wot-mod-alpha"),
        });

        IReadOnlyList<Relocation> first = SettingsRelocator.HealStore(store, anchor, InTempTree);
        IReadOnlyList<Relocation> second = SettingsRelocator.HealStore(store, anchor, InTempTree);

        Assert.Single(first);
        Assert.Equal(alpha, store.Load().LastProjectDir);
        Assert.Empty(second);
    }

    /// <summary>
    /// НАСТОЯЩИЙ ШАБЛОН РЯДОМ С КОКПИТОМ: устаревший путь с тем же именем каталога находит шаблон,
    /// который находит и мастер нового проекта. Нужна настоящая раскладка репозиториев, поэтому
    /// только при <c>WOTCOCKPIT_REAL_MACHINE=1</c>.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void RealLayout_StaleTemplatePath_FindsTheTemplateNextToTheCockpit()
    {
        Skip.IfNot(TestSettingsIsolation.RealMachine,
            "нужна настоящая раскладка репозиториев: задайте WOTCOCKPIT_REAL_MACHINE=1");

        string probed = TemplateLocator.ProbeSiblings();
        Skip.If(probed.Length == 0, "шаблона рядом с кокпитом нет — переносить некуда");

        string stale = Path.Combine(@"C:\no-such-old-repos-" + Guid.NewGuid().ToString("N")[..8], Path.GetFileName(probed));
        var settings = new CockpitSettings { TemplateDir = stale };
        SettingsRelocator.Heal(settings, AppContext.BaseDirectory);

        Assert.Equal(Path.GetFullPath(probed), settings.TemplateDir, ignoreCase: true);
    }

    /// <summary>Существует ли путь — но только внутри временного дерева теста.</summary>
    private bool InTempTree(string path) =>
        path.StartsWith(_root, StringComparison.OrdinalIgnoreCase)
        && (Directory.Exists(path) || File.Exists(path));

    private string Dir(string relative)
    {
        string full = Path.IsPathFullyQualified(relative) ? relative : Path.Combine(_root, relative);
        Directory.CreateDirectory(full);
        return full;
    }

    private static string Project(string parent, string name)
    {
        string dir = Path.Combine(parent, name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "build.py"), "# stand");
        return dir;
    }
}
