using System.IO.Compression;
using System.Text;
using WotModCockpit.Core.ApiIndex;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Переход на новую версию клиента, часть Core: версии, деревья исходников, распознавание
/// оставшихся в прошлой версии модов, след мода, проверка совместимости и извлечение исходников.
/// <para>
/// Всё — на фейковых каталогах во временной папке: настоящая игра и настоящие деревья здесь
/// не читаются и тем более не пишутся. Декомпилятор подменён: uncompyle6 не запускается.
/// </para>
/// </summary>
public sealed class ClientMigrationCoreTests : IDisposable
{
    private readonly CoreTempDir _tmp = new("wotcockpit-migration");

    public void Dispose() => _tmp.Dispose();

    // ================================================================= версии

    [Theory]
    [InlineData("1.46.0.0", "1.45.0.0", 1)]
    [InlineData("1.45.0.0", "1.46.0.0", -1)]
    [InlineData("1.46", "1.46.0.0", 0)]
    [InlineData("1.10.0.0", "1.9.0.0", 1)]
    public void Compare_Numeric(string a, string b, int sign) =>
        Assert.Equal(sign, Math.Sign(ClientVersions.Compare(a, b)));

    [Theory]
    [InlineData("1.46.0.0", "1.46")]
    [InlineData("1.46.1.0", "1.46.1")]
    [InlineData("2.0.0.0", "2.0")]
    public void Short_DropsTrailingZerosButKeepsTwoParts(string version, string expected) =>
        Assert.Equal(expected, ClientVersions.Short(version));

    /// <summary>Каталоги модов по возрастанию версии; посторонние папки — мимо.</summary>
    [Fact]
    public void ModsDirs_OnlyVersionLikeFolders_Ascending()
    {
        string root = _tmp.MakeDir("game");
        foreach (string d in new[] { "1.46.0.0", "1.9.0.0", "1.45.0.0", "backup", "configs" })
        {
            Directory.CreateDirectory(Path.Combine(root, "mods", d));
        }

        Assert.Equal(["1.9.0.0", "1.45.0.0", "1.46.0.0"], ClientVersions.ModsDirs(root).Select(x => x.Version));
    }

    // ================================================================= деревья исходников

    /// <summary>Версия дерева — из отметки кокпита, без неё — из имени каталога, иначе неизвестна.</summary>
    [Fact]
    public void VersionOf_MarkerThenNameThenUnknown()
    {
        string named = Tree("wot-src-1.45");
        string unnamed = Tree("wot-src");
        string marked = Tree("whatever");
        ClientSourceTrees.WriteMarker(marked, new ClientSourceMarker { Version = "1.46.0.0" });

        Assert.Equal("1.45", ClientSourceTrees.VersionOf(named));
        Assert.Null(ClientSourceTrees.VersionOf(unnamed));
        Assert.Equal("1.46.0.0", ClientSourceTrees.VersionOf(marked));
        Assert.True(ClientSourceTrees.IsForVersion(marked, "1.46"));
        Assert.True(ClientSourceTrees.IsForVersion(named, "1.45.0.0"));
        Assert.False(ClientSourceTrees.IsForVersion(unnamed, "1.45.0.0"));
    }

    /// <summary>Новое дерево кладётся рядом с текущим; готовое находится и по короткому, и по полному имени.</summary>
    [Fact]
    public void DefaultDirFor_AndFindExisting_NextToCurrentTree()
    {
        string current = Tree("scratch/wot-src");
        string expected = Path.Combine(Path.GetDirectoryName(current)!, "wot-src-1.46");

        Assert.Equal(expected, ClientSourceTrees.DefaultDirFor("1.46.0.0", current, null));
        Assert.Null(ClientSourceTrees.FindExisting("1.46.0.0", current, null));

        string full = Tree("scratch/wot-src-1.46.0.0");
        Assert.Equal(full, ClientSourceTrees.FindExisting("1.46.0.0", current, null));

        Tree("scratch/wot-src-1.46");
        Assert.Equal(expected, ClientSourceTrees.FindExisting("1.46.0.0", current, null));

        // Текущее дерево уже нужной версии — оно и есть.
        Assert.Equal(Path.GetFullPath(expected), ClientSourceTrees.FindExisting("1.46.0.0", expected, null));
    }

    /// <summary>Без текущего дерева — в .scratch шаблона.</summary>
    [Fact]
    public void DefaultDirFor_WithoutCurrentTree_GoesToTemplateScratch()
    {
        string template = _tmp.MakeDir("wot-mod-template");
        Assert.Equal(Path.Combine(template, ".scratch", "wot-src-1.46"), ClientSourceTrees.DefaultDirFor("1.46.0.0", null, template));
    }

    [Fact]
    public void ReadExtraModules_SkipsCommentsAndBlank_NullWithoutFile()
    {
        string tree = Tree("wot-src-1.46");
        Assert.Null(ClientSourceTrees.ReadExtraModules(tree));

        File.WriteAllText(Path.Combine(tree, ClientSourceTrees.ExtraModulesFile), "# шапка\n\nbattle_royale.gui\n  story_mode.x  \n");
        Assert.Equal(["battle_royale.gui", "story_mode.x"], ClientSourceTrees.ReadExtraModules(tree));
    }

    // ================================================================= распознавание

    /// <summary>
    /// Типичный случай после обновления: свои моды в прошлой версии, новая пустая. К переносу —
    /// включённые свои (по одному на мод, самый свежий файл); выключенные — отдельно; чужие — никак.
    /// </summary>
    [Fact]
    public void Detect_OwnEnabledArePending_DisabledListedSeparately_ForeignIgnored()
    {
        Game g = NewGame();
        string alpha = Project("alpha");
        string beta = Project("beta");
        string gamma = Project("gamma");

        Mod(g.Old, "ru.test.alpha_1.0.0.mtmod.off", "ru.test.alpha", DateTime.UtcNow.AddDays(-3));
        Mod(g.Old, "ru.test.alpha_1.1.0.mtmod", "ru.test.alpha", DateTime.UtcNow.AddDays(-1));
        Mod(g.Old, "ru.test.beta_1.0.0.mtmod.off", "ru.test.beta", DateTime.UtcNow.AddDays(-2));
        Mod(g.Old, "foreign_2.0.mtmod", "com.other.foreign", DateTime.UtcNow);
        Mod(g.Old, "ru.test.gamma_1.0.0.mtmod", "ru.test.gamma", DateTime.UtcNow.AddDays(-2));

        // gamma уже стоит в новой версии — переносить нечего.
        Mod(g.New, "ru.test.gamma_1.0.0.mtmod", "ru.test.gamma", DateTime.UtcNow);

        ClientMigrationState s = ClientMigrationDetector.Detect(g.Install, [alpha, beta, gamma]);

        Assert.Equal("1.45.0.0", s.PreviousVersion);
        Assert.Equal(g.Old, s.PreviousModsDir);
        Assert.True(s.NeedsMigration);
        Assert.Equal(["ru.test.alpha_1.1.0.mtmod"], s.Pending.Select(m => m.FileName));
        Assert.Equal(["ru.test.beta_1.0.0.mtmod.off"], s.Disabled.Select(m => m.FileName));
    }

    /// <summary>
    /// Прошлая версия — самая новая из старых СО СВОИМИ модами: каталог без своих модов
    /// пропускается, каталог новее активной версии не рассматривается вовсе.
    /// </summary>
    [Fact]
    public void Detect_PicksNewestOlderDirWithOwnMods()
    {
        Game g = NewGame();
        string alpha = Project("alpha");
        string v144 = Path.Combine(g.Root, "mods", "1.44.0.0");
        string v147 = Path.Combine(g.Root, "mods", "1.47.0.0");
        Directory.CreateDirectory(v144);
        Directory.CreateDirectory(v147);

        Mod(v144, "ru.test.alpha_0.9.0.mtmod", "ru.test.alpha", DateTime.UtcNow.AddDays(-30));
        Mod(g.Old, "foreign_1.0.mtmod", "com.other.foreign", DateTime.UtcNow);
        Mod(v147, "ru.test.alpha_2.0.0.mtmod", "ru.test.alpha", DateTime.UtcNow);

        ClientMigrationState s = ClientMigrationDetector.Detect(g.Install, [alpha]);

        Assert.Equal("1.44.0.0", s.PreviousVersion);
        Assert.Equal(["ru.test.alpha_0.9.0.mtmod"], s.Pending.Select(m => m.FileName));
    }

    /// <summary>Мод выключен и в новой версии — он там есть, переносить его не надо.</summary>
    [Fact]
    public void Detect_DisabledCopyInNewVersion_CountsAsPresent()
    {
        Game g = NewGame();
        string alpha = Project("alpha");
        Mod(g.Old, "ru.test.alpha_1.0.0.mtmod", "ru.test.alpha", DateTime.UtcNow);
        Mod(g.New, "ru.test.alpha_1.0.0.mtmod.off", "ru.test.alpha", DateTime.UtcNow);

        ClientMigrationState s = ClientMigrationDetector.Detect(g.Install, [alpha]);

        Assert.False(s.NeedsMigration);
        Assert.Empty(s.Disabled);
    }

    [Fact]
    public void Detect_NoProjectsOrNoOldDirs_Nothing()
    {
        Game g = NewGame();
        Mod(g.Old, "ru.test.alpha_1.0.0.mtmod", "ru.test.alpha", DateTime.UtcNow);

        Assert.False(ClientMigrationDetector.Detect(g.Install, []).NeedsMigration);
        Assert.Null(ClientMigrationDetector.Detect(g.Install, []).PreviousVersion);

        Directory.Delete(g.Old, recursive: true);
        Assert.Null(ClientMigrationDetector.Detect(g.Install, [Project("alpha")]).PreviousVersion);
    }

    /// <summary>Распознавание только читает: оба каталога модов после него те же байт в байт.</summary>
    [Fact]
    public void Detect_IsReadOnly()
    {
        Game g = NewGame();
        string alpha = Project("alpha");
        Mod(g.Old, "ru.test.alpha_1.0.0.mtmod", "ru.test.alpha", DateTime.UtcNow);
        Mod(g.New, "foreign_1.0.mtmod", "com.other.foreign", DateTime.UtcNow);
        string before = Fingerprint(g.Root);

        ClientMigrationDetector.Detect(g.Install, [alpha]);

        Assert.Equal(before, Fingerprint(g.Root));
    }

    // ================================================================= след мода

    /// <summary>Комментарии и длинные строки — не упоминания; строка-идентификатор — упоминание.</summary>
    [Fact]
    public void Footprint_NamesFromCode_AndIdentifierStrings_NotFromComments()
    {
        ModFootprint f = ModFootprint.Read(Path.Combine(_tmp.Path, "nothing")).Scan("main.py",
            "# PlayerAvatar в комментарии\n" +
            "x = BigWorld.player()\n" +
            "HOOKS = ('showShotResults', 'не идентификатор')\n" +
            "doc = '''onEnterWorld в докстринге'''\n" +
            "s = \"незакрытая строка\n" +
            "y = Vehicle\n");

        Assert.True(f.Mentions("BigWorld"));
        Assert.True(f.Mentions("player"));
        Assert.True(f.Mentions("showShotResults"));
        Assert.True(f.Mentions("Vehicle"));
        Assert.False(f.Mentions("PlayerAvatar"));
        Assert.False(f.Mentions("onEnterWorld"));
        Assert.Equal("main.py:6", f.Names["Vehicle"]);
    }

    /// <summary>Импорты: обычные, с «as», «from … import» в скобках на несколько строк; относительные — мимо.</summary>
    [Fact]
    public void Footprint_ImportCandidates()
    {
        ModFootprint f = ModFootprint.Read(Path.Combine(_tmp.Path, "nothing")).Scan("main.py",
            "import BigWorld, gui.SystemMessages as sm\n" +
            "from gui.battle_control import (avatar_getter,\n" +
            "    g_sessionProvider)\n" +
            "from . import local\n" +
            "m = __import__('vehicle_systems.model_assembler')\n");

        Assert.Contains("BigWorld", f.ImportCandidates.Keys);
        Assert.Contains("gui.SystemMessages", f.ImportCandidates.Keys);
        Assert.Contains("gui.battle_control", f.ImportCandidates.Keys);
        Assert.Contains("gui.battle_control.avatar_getter", f.ImportCandidates.Keys);
        Assert.Contains("gui.battle_control.g_sessionProvider", f.ImportCandidates.Keys);
        Assert.DoesNotContain(f.ImportCandidates.Keys, k => k.Contains("local", StringComparison.Ordinal));
        Assert.Contains("vehicle_systems.model_assembler", f.DottedStrings.Keys);
    }

    // ================================================================= проверка совместимости

    /// <summary>Клиент не изменился — совместим, изменившихся модулей нет.</summary>
    [Fact]
    public void Check_Unchanged_Compatible()
    {
        (string oldTree, string newTree) = Trees();
        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        Assert.Contains("Avatar", r.ClientModules);
        Assert.Contains("constants", r.ClientModules);
        Assert.Empty(r.ChangedClientModules);
    }

    /// <summary>Модуль изменился, но то, что использует мод, — нет: совместим, с заметкой.</summary>
    [Fact]
    public void Check_UnrelatedChange_CompatibleWithNote()
    {
        (string oldTree, string newTree) = Trees();
        Append(newTree, "client/Avatar.py", "\ndef brandNewHelper(a, b):\n    return a\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        Assert.Equal(["Avatar"], r.ChangedClientModules);
        Assert.Contains(r.Issues, i => !i.Blocking && i.Kind == "модули изменились");
    }

    /// <summary>У перехватываемого метода поменялась сигнатура — блокирует.</summary>
    [Fact]
    public void Check_HookedMethodSignatureChanged_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/Avatar.py", "def showShotResults(self, results):", "def showShotResults(self, results, isRicochet):");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.False(r.IsCompatible);
        CompatibilityIssue issue = Assert.Single(r.BlockingIssues, i => i.Kind == "сигнатура");
        Assert.Contains("PlayerAvatar.showShotResults", issue.Message);
        Assert.StartsWith("res/scripts/client/gui/mods/mod_hook.py:", issue.ModLocation);
    }

    /// <summary>Упомянутый метод исчез — блокирует.</summary>
    [Fact]
    public void Check_MentionedMethodRemoved_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/Avatar.py", "    def onEnterWorld(self, prereqs):\n        pass\n", string.Empty);

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.False(r.IsCompatible);
        Assert.Contains(r.BlockingIssues, i => i.Kind == "удалено" && i.Message.Contains("PlayerAvatar.onEnterWorld"));
    }

    /// <summary>
    /// Исчез метод с ходовым именем у класса, который мод не упоминает, — не тревога: <c>update</c>
    /// у сотни классов.
    /// </summary>
    [Fact]
    public void Check_RemovedMethodOfUnmentionedClass_NotBlocking()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/Avatar.py", "    def update(self):\n        pass\n", "    def renamed(self):\n        pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
    }

    /// <summary>Модуль, который мод импортирует, исчез — блокирует; если он переехал в доп. пакет — нет.</summary>
    [Fact]
    public void Check_ImportedModuleRemoved_BlocksUnlessInExtraPackages()
    {
        (string oldTree, string newTree) = Trees();
        File.Delete(Path.Combine(newTree, "client", "gui", "shared_helpers.py"));
        ModProject project = HookProject();

        ModCompatibilityReport blocked = Checker(oldTree, newTree).Check(project);
        Assert.False(blocked.IsCompatible);
        Assert.Contains(blocked.BlockingIssues, i => i.Kind == "модуль удалён" && i.Message.Contains("gui.shared_helpers"));

        // Переехал в дополнительный пакет: не удалён, но и сравнить не с чем — проверен не до конца.
        ModCompatibilityReport moved = Checker(oldTree, newTree, newExtra: ["gui.shared_helpers"]).Check(project);
        Assert.DoesNotContain(moved.Issues, i => i.Kind == "модуль удалён");
        CompatibilityIssue movedNote = Assert.Single(moved.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind);
        Assert.False(movedNote.Blocking);
        Assert.Contains("дополнительный пакет", movedNote.Message);

        // Не декомпилировался: в игре есть, исходника нет — тоже не «удалён», а «проверить нельзя».
        ModCompatibilityReport failed = new ModCompatibilityChecker(Index(oldTree), Index(newTree), new SourceFileService(), [], [],
            newFailedModules: ["gui.shared_helpers"]).Check(project);
        Assert.DoesNotContain(failed.Issues, i => i.Kind == "модуль удалён" || i.Kind.StartsWith("preflight", StringComparison.Ordinal));
        Assert.Contains(failed.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("не декомпилировался"));
    }

    /// <summary>
    /// Модуль не декомпилировался ни в старой, ни в новой версии (тот же uncompyle6 спотыкается на
    /// нём каждый раз) — его нет ни в одном индексе, но мод, который его импортирует, проверен не до
    /// конца. Встроенный список модулей доп. пакетов при этом не теряется.
    /// </summary>
    [Fact]
    public void Check_ModuleFailedInBothVersions_Unverified()
    {
        (string oldTree, string newTree) = Trees();
        ModProject project = Simple("ghost",
            "import ghost_module\n" +
            "import battle_royale\n" +
            "\n" +
            "def init():\n" +
            "    pass\n");

        ModCompatibilityReport r = new ModCompatibilityChecker(Index(oldTree), Index(newTree), new SourceFileService(), null, null,
            newFailedModules: ["ghost_module"]).Check(project);

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        Assert.Contains(r.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("ghost_module"));
        Assert.DoesNotContain(r.Issues, i => i.Kind.StartsWith("preflight", StringComparison.Ordinal));
    }

    /// <summary>
    /// Функция, которую uncompyle6 не разобрал в старой версии (<c>def hookedParse error at or near …</c>),
    /// в индекс не попала; мод её перехватывает — сравнить не с чем, мод проверен не до конца.
    /// </summary>
    [Fact]
    public void Check_UnparsedHookedFunction_Unverified()
    {
        (string oldTree, string newTree) = Trees();
        Write(oldTree, "client/hooks_src.py",
            "class Target(object):\n\n    def hookedParse error at or near `LOAD_FAST' instruction at offset 0\n\n    def other(self):\n        pass\n");
        Write(newTree, "client/hooks_src.py",
            "class Target(object):\n\n    def hooked(self, a, b):\n        pass\n\n    def other(self):\n        pass\n");
        ModProject project = Simple("hooker",
            "from hooks_src import Target\n" +
            "_orig = Target.hooked\n" +
            "def _hook(self, a):\n" +
            "    return _orig(self, a)\n" +
            "Target.hooked = _hook\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains(r.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("hooks_src.hooked"));
    }

    /// <summary>
    /// Модуль не декомпилировался в прошлой версии (его нет в индексе, он в списке дерева), а в
    /// новой его нет вовсе — удалён: блокирует. Давней WOT002 он не прикрывается: старый оракул
    /// знает, что модуль тогда был. Переехал в доп. пакет — «сравнить не с чем».
    /// </summary>
    [Fact]
    public async Task Check_ModuleFailedInOldThenRemoved_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        File.WriteAllLines(Path.Combine(oldTree, ClientSourceTrees.FailedModulesFile), ["# не разобраны", "gui.lost_module"]);
        ModProject project = Simple("lost", "from gui.lost_module import Thing\n\ndef init():\n    pass\n");

        // Через планировщик: список прошлой версии берётся из её дерева.
        Game g = NewGame();
        Mod(g.Old, "ru.test.lost_1.0.0.mtmod", "ru.test.lost", DateTime.UtcNow);
        ClientMigrationState state = ClientMigrationDetector.Detect(g.Install, [project.ProjectDir]);
        MigrationCheck check = await new ClientMigrationPlanner(new Core.Services.JsonProjectStore())
            .CheckAsync(state, newTree, oldTree, null, CancellationToken.None);
        ModCompatibilityReport removed = check.Candidates.Single().Report!;

        Assert.False(removed.IsCompatible);
        Assert.Contains(removed.BlockingIssues, i => i.Kind == "модуль удалён" && i.Message.Contains("gui.lost_module"));

        ModCompatibilityReport moved = new ModCompatibilityChecker(Index(oldTree), Index(newTree), new SourceFileService(), [], ["gui.lost_module"],
            oldFailedModules: ["gui.lost_module"]).Check(project);
        Assert.True(moved.IsCompatible, string.Join("\n", moved.Issues));
        Assert.Contains(moved.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("gui.lost_module"));
    }

    /// <summary>Не разобрана только в НОВОЙ версии — не «удалено» (блок), а «сравнить не с чем».</summary>
    [Fact]
    public void Check_UnparsedOnlyInNew_NotRemoved()
    {
        (string oldTree, string newTree) = Trees();
        Write(oldTree, "client/hooks_src.py", "class Target(object):\n\n    def hooked(self, a, b):\n        pass\n");
        Write(newTree, "client/hooks_src.py", "class Target(object):\n\n    def hookedParse error at or near `LOAD_FAST' instruction at offset 0\n");
        ModProject project = Simple("hooker2", "from hooks_src import Target\n_orig = Target.hooked\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        Assert.Contains(r.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("hooks_src.hooked"));
    }

    /// <summary>
    /// Неразобранный метод ДРУГОГО класса с тем же именем не прикрывает удаление разобранного
    /// (как в UnitRoster.py: BaseUnitRoster.checkVehicleLevel и BaseUnitRosterLimits.checkVehicleLevel).
    /// </summary>
    [Fact]
    public void Check_UnparsedSameNameInOtherClass_DoesNotHideRemoval()
    {
        (string oldTree, string newTree) = Trees();
        const string limits = "class RosterLimits(object):\n\n    def checkLevelParse error at or near `LOAD_FAST' instruction at offset 0\n";
        Write(oldTree, "common/roster.py", "class Roster(object):\n\n    def checkLevel(self, level):\n        pass\n\n\n" + limits);
        Write(newTree, "common/roster.py", "class Roster(object):\n\n    def other(self):\n        pass\n\n\n" + limits);
        ModProject project = Simple("roster", "from roster import Roster\n_orig = Roster.checkLevel\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains(r.BlockingIssues, i => i.Kind == "удалено" && i.Message.Contains("Roster.checkLevel"));
    }

    /// <summary>Модуль не изменился, но функция в нём не разобрана в обеих версиях — всё равно «сравнить не с чем».</summary>
    [Fact]
    public void Check_UnparsedInUnchangedModule_StillUnverified()
    {
        (string oldTree, string newTree) = Trees();
        const string text = "clamp = Parse error at or near `LOAD_FAST' instruction at offset 0\n\ndef other():\n    pass\n";
        Write(oldTree, "client/mathz.py", text);
        Write(newTree, "client/mathz.py", text);
        ModProject project = Simple("clamper", "from mathz import clamp\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Empty(r.ChangedClientModules);
        Assert.Contains(r.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind && i.Message.Contains("mathz.clamp"));
    }

    /// <summary>Пометка частичного файла из ручного разбора («uncompyle6: parse error») — тоже не изменение.</summary>
    [Fact]
    public void Check_ManualPartialHeader_NotAChange()
    {
        (string oldTree, string newTree) = Trees();
        const string code = "def clamp(minVal, maxVal, val):\n    return val\n";
        Write(oldTree, "client/mathy.py", "# uncompyle6 version 3.9.3\n" + code);
        Write(newTree, "client/mathy.py", "# ВНИМАНИЕ: декомпиляция частичная (uncompyle6: parse error), текст обрывается\n# uncompyle6 version 3.9.3\n" + code);
        ModProject project = Simple("mathy", "import mathy\n\ndef init():\n    pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Empty(r.ChangedClientModules);
    }

    /// <summary>
    /// Частичный модуль: в старом дереве (разобранном вручную) он с отладочным выводом парсера,
    /// в новом — без. Текст модуля тот же — модуль не «изменился».
    /// </summary>
    [Fact]
    public void Check_ParserDumpOnOneSide_NotAChange()
    {
        (string oldTree, string newTree) = Trees();
        const string code = "def clamp(minVal, maxVal, val):\n    return val\n";
        Write(oldTree, "client/mathx.py", ClientSourceExtractor.PartialHeader + "\n# uncompyle6 version 3.9.3\n" + ParserDump + code);
        Write(newTree, "client/mathx.py", ClientSourceExtractor.PartialHeader + "\n# uncompyle6 version 3.9.3\n" + code);
        ModProject project = Simple("mathx", "import mathx\n\ndef init():\n    pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains("mathx", r.ClientModules);
        Assert.Empty(r.ChangedClientModules);
    }

    /// <summary>Упомянутая константа поменяла значение — заметка, не запрет.</summary>
    [Fact]
    public void Check_ConstantValueChanged_NoteOnly()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "common/constants.py", "ARENA_PERIOD_BATTLE = 3", "ARENA_PERIOD_BATTLE = 4");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        CompatibilityIssue note = Assert.Single(r.Issues, i => i.Kind == "константа");
        Assert.False(note.Blocking);
        Assert.Contains("«3»", note.Message);
        Assert.Contains("«4»", note.Message);
    }

    /// <summary>Служебные строки декомпилятора (путь к .pyc, версия) — не изменение модуля.</summary>
    [Fact]
    public void Check_DecompilerHeadersDiffer_NotAChange()
    {
        (string oldTree, string newTree) = Trees();
        string avatar = Path.Combine(newTree, "client", "Avatar.py");
        File.WriteAllText(avatar, "# uncompyle6 version 3.9.3\n# Decompiled from: Python 3.12\n" + File.ReadAllText(avatar) + "# okay decompiling x.pyc\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(HookProject());

        Assert.Empty(r.ChangedClientModules);
    }

    /// <summary>Без прошлого дерева сравнивать не с чем: проверяется только новое, и мод проходит.</summary>
    [Fact]
    public void Check_NoBaseline_StillChecksPreflight()
    {
        (_, string newTree) = Trees();
        var checker = new ModCompatibilityChecker(null, Index(newTree), new SourceFileService());

        Assert.False(checker.HasBaseline);
        ModCompatibilityReport r = checker.Check(HookProject());
        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
    }

    /// <summary>Планировщик: индексы по разу, итог на каждый ждущий мод, с проектом.</summary>
    [Fact]
    public async Task Planner_ChecksEachPendingMod()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/Avatar.py", "def showShotResults(self, results):", "def showShotResults(self):");
        Game g = NewGame();
        ModProject hook = HookProject();
        string plain = Project("plain");
        Mod(g.Old, "ru.test.hook_1.0.0.mtmod", "ru.test.hook", DateTime.UtcNow);
        Mod(g.Old, "ru.test.plain_1.0.0.mtmod", "ru.test.plain", DateTime.UtcNow);
        ClientMigrationState state = ClientMigrationDetector.Detect(g.Install, [hook.ProjectDir, plain]);
        Assert.Equal(2, state.Pending.Count);

        var stages = new List<string>();
        MigrationCheck check = await new ClientMigrationPlanner(new Core.Services.JsonProjectStore())
            .CheckAsync(state, newTree, oldTree, new SyncProgress<string>(stages.Add), CancellationToken.None);

        Assert.Equal(oldTree, check.OldTree);
        Assert.Equal("1.45", check.OldTreeVersion);
        MigrationCandidate h = check.Candidates.Single(c => c.ModId == "ru.test.hook");
        MigrationCandidate p = check.Candidates.Single(c => c.ModId == "ru.test.plain");
        Assert.False(h.IsCompatible);
        Assert.True(p.IsCompatible, p.Problem ?? string.Join("\n", p.Report!.Issues));
        Assert.NotNull(p.Project);
        Assert.NotEmpty(stages);

        // То же дерево вместо старого — без сравнения.
        MigrationCheck same = await new ClientMigrationPlanner(new Core.Services.JsonProjectStore())
            .CheckAsync(state, newTree, newTree, null, CancellationToken.None);
        Assert.Null(same.OldTree);
    }

    // ================================================================= проверка: после ревью

    /// <summary>Синглтон модуля (g_*) исчез — блокирует, даже если мод берёт его под try.</summary>
    [Fact]
    public void Check_ModuleSingletonRemoved_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/gui/__init__.py", "g_keyEventHandlers = set()\n", string.Empty);
        ModProject project = Simple("keys",
            "def init():\n" +
            "    try:\n" +
            "        from gui import g_keyEventHandlers\n" +
            "        g_keyEventHandlers.add(None)\n" +
            "    except Exception:\n" +
            "        pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.False(r.IsCompatible);
        CompatibilityIssue issue = Assert.Single(r.BlockingIssues, i => i.Kind == "удалено");
        Assert.StartsWith("gui.g_keyEventHandlers ", issue.Message);
    }

    /// <summary>
    /// Событие, до которого мод добирается через синглтон (<c>g_playerEvents.onArenaPeriodChange</c>),
    /// переименовали — блокирует, хотя имени класса <c>_PlayerEvents</c> в моде нет.
    /// </summary>
    [Fact]
    public void Check_EventViaSingletonRenamed_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/PlayerEvents.py", "self.onArenaPeriodChange = ", "self.onArenaPeriodChanged = ");
        ModProject project = Simple("period",
            "from PlayerEvents import g_playerEvents\n" +
            "def _onPeriod(*args):\n" +
            "    pass\n" +
            "def init():\n" +
            "    try:\n" +
            "        g_playerEvents.onArenaPeriodChange += _onPeriod\n" +
            "    except Exception:\n" +
            "        pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.False(r.IsCompatible);
        Assert.Contains(r.BlockingIssues, i => i.Kind == "удалено" && i.Message.Contains("_PlayerEvents.onArenaPeriodChange"));
    }

    /// <summary>Метод, вызываемый через синглтон, исчез — блокирует; имени класса в моде нет.</summary>
    [Fact]
    public void Check_MethodViaSingletonRemoved_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/PlayerEvents.py", "    def isPlayerEntityChanging(self):\n        return False\n", string.Empty);
        ModProject project = Simple("changing",
            "from PlayerEvents import g_playerEvents\n" +
            "def init():\n" +
            "    try:\n" +
            "        g_playerEvents.isPlayerEntityChanging()\n" +
            "    except Exception:\n" +
            "        pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains(r.BlockingIssues, i => i.Kind == "удалено" && i.Message.Contains("_PlayerEvents.isPlayerEntityChanging"));
    }

    /// <summary>
    /// Закрытый метод перехвачен через <c>setattr(type(obj), '_SiegeCtrl__onUpdate', …)</c>: имени
    /// класса в моде нет вовсе, только искажённое имя — сменившаяся сигнатура всё равно блокирует.
    /// </summary>
    [Fact]
    public void Check_MangledNameAlone_IsEnoughToCompare()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/siege_ctrl.py", "def __onUpdate(self, a):", "def __onUpdate(self, a, b):");
        ModProject project = Simple("ctrl",
            "import siege_ctrl\n" +
            "def patch(obj):\n" +
            "    setattr(type(obj), '_SiegeCtrl__onUpdate', lambda self, a: None)\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains(r.BlockingIssues, i => i.Kind == "сигнатура" && i.Message.Contains("SiegeCtrl.__onUpdate"));
    }

    /// <summary>
    /// Модуль, который мод импортирует, есть в новой версии, но не в базе сравнения: заметка
    /// «нет в прошлой версии» — перенос не блокирует, но мод проверен не до конца.
    /// </summary>
    [Fact]
    public void Check_ModuleMissingFromBaseline_UnverifiedNote()
    {
        (string oldTree, string newTree) = Trees();
        Write(newTree, "client/new_only.py", "def helper():\n    return 1\n");
        ModProject project = Simple("newonly", "import new_only\n\ndef init():\n    pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.True(r.IsCompatible, string.Join("\n", r.Issues));
        CompatibilityIssue note = Assert.Single(r.Issues, i => i.Kind == ModCompatibilityChecker.UnverifiedKind);
        Assert.False(note.Blocking);
        Assert.Contains("new_only", note.Message);
    }

    /// <summary>
    /// Перехват закрытого метода в искажённом виде (<c>_SiegeHelper__onSiegeStateUpdated</c>):
    /// сменившаяся сигнатура блокирует так же, как у открытого.
    /// </summary>
    [Fact]
    public void Check_MangledPrivateMethodSignatureChanged_Blocks()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "client/SiegeHelper.py", "(self, vehicleID, newState)", "(self, vehicleID, newState, timeToNextState)");
        ModProject project = Simple("siege",
            "from SiegeHelper import SiegeHelper\n" +
            "_orig = SiegeHelper._SiegeHelper__onSiegeStateUpdated\n" +
            "def _hook(self, vehicleID, newState):\n" +
            "    return _orig(self, vehicleID, newState)\n" +
            "SiegeHelper._SiegeHelper__onSiegeStateUpdated = _hook\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.False(r.IsCompatible);
        CompatibilityIssue issue = Assert.Single(r.BlockingIssues, i => i.Kind == "сигнатура");
        Assert.Contains("SiegeHelper.__onSiegeStateUpdated", issue.Message);
        Assert.StartsWith("res/scripts/client/gui/mods/mod_siege.py:2", issue.ModLocation);
    }

    /// <summary>Функция с именем своего модуля (<c>th_async.th_async</c>) сравнивается, а не заслоняется символом модуля.</summary>
    [Fact]
    public void Check_FunctionNamedLikeItsModule_SignatureCompared()
    {
        (string oldTree, string newTree) = Trees();
        Replace(newTree, "common/th_async.py", "def th_async(func=None):", "def th_async(func=None, executorClass=None):");
        ModProject project = Simple("async",
            "from th_async import th_async\n" +
            "@th_async\n" +
            "def work():\n" +
            "    pass\n");

        ModCompatibilityReport r = Checker(oldTree, newTree).Check(project);

        Assert.Contains(r.BlockingIssues, i => i.Kind == "сигнатура" && i.Message.Contains("th_async.th_async"));
    }

    /// <summary>Пустой индекс новой версии: проверять нечем — блокирует, а не «совместим».</summary>
    [Fact]
    public void Check_EmptyNewIndex_Blocks()
    {
        (string oldTree, _) = Trees();
        string empty = Tree("trees/wot-src-empty");

        ModCompatibilityReport withBaseline = Checker(oldTree, empty).Check(HookProject());
        ModCompatibilityReport alone = new ModCompatibilityChecker(null, Index(empty), new SourceFileService()).Check(HookProject());

        Assert.Contains(withBaseline.BlockingIssues, i => i.Kind == "нет исходников");
        Assert.False(alone.IsCompatible);
        Assert.Contains(alone.BlockingIssues, i => i.Kind == "нет исходников");
    }

    /// <summary>
    /// Давняя WOT002 (неизвестный модуль) не становится «новой» из-за того, что у версий разное
    /// число модулей в индексе — оно стоит в тексте находки.
    /// </summary>
    [Fact]
    public void Check_OldWot002_NotNewJustBecauseModuleCountDiffers()
    {
        (string oldTree, string newTree) = Trees();
        Write(newTree, "client/brand_new_module.py", "X = 1\n");
        ModProject project = Simple("unknown", "import missing_client_module\n\ndef init():\n    pass\n");

        ModCompatibilityReport alone = new ModCompatibilityChecker(null, Index(newTree), new SourceFileService(), null, []).Check(project);
        Assert.Contains(alone.Issues, i => i.Kind == "preflight " + Core.Preflight.UnknownApiRule.ModuleRuleId);

        ModCompatibilityReport compared = Checker(oldTree, newTree).Check(project);
        Assert.DoesNotContain(compared.Issues, i => i.Kind == "preflight " + Core.Preflight.UnknownApiRule.ModuleRuleId);
    }

    /// <summary>Дерево той же или более новой версии — не база: сравнения нет, доказательства тоже.</summary>
    [Fact]
    public async Task Planner_SameOrNewerVersionTree_IsNotABaseline()
    {
        (string oldTree, string newTree) = Trees();
        string sameVersion = Tree("trees/wot-src-1.46.0.0");
        Game g = NewGame();
        ModProject hook = HookProject();
        Mod(g.Old, "ru.test.hook_1.0.0.mtmod", "ru.test.hook", DateTime.UtcNow);
        ClientMigrationState state = ClientMigrationDetector.Detect(g.Install, [hook.ProjectDir]);
        var planner = new ClientMigrationPlanner(new Core.Services.JsonProjectStore());

        MigrationCheck same = await planner.CheckAsync(state, newTree, sameVersion, null, CancellationToken.None);
        Assert.Null(same.OldTree);
        Assert.False(same.BaselineIsPreviousVersion);

        MigrationCheck proper = await planner.CheckAsync(state, newTree, oldTree, null, CancellationToken.None);
        Assert.True(proper.BaselineIsPreviousVersion);
        Assert.True(proper.NewTreeModules > 0);

        string unversioned = Tree("trees/wot-src");
        Write(unversioned, "client/Avatar.py", "class PlayerAvatar(object):\n    pass\n");
        MigrationCheck unknown = await planner.CheckAsync(state, newTree, unversioned, null, CancellationToken.None);
        Assert.Equal(unversioned, unknown.OldTree);
        Assert.False(unknown.BaselineIsPreviousVersion);
    }

    /// <summary>
    /// База с правильным именем, но без исходников (пустой client\) — сравнения не было: не
    /// «прошлая версия», не OldTree, а EmptyBaseline с путём.
    /// </summary>
    [Fact]
    public async Task Planner_EmptyWellNamedBaseline_IsNotVerified()
    {
        (_, string newTree) = Trees();
        string empty = Tree("trees-empty/wot-src-1.45");
        Game g = NewGame();
        ModProject hook = HookProject();
        Mod(g.Old, "ru.test.hook_1.0.0.mtmod", "ru.test.hook", DateTime.UtcNow);
        ClientMigrationState state = ClientMigrationDetector.Detect(g.Install, [hook.ProjectDir]);

        MigrationCheck check = await new ClientMigrationPlanner(new Core.Services.JsonProjectStore())
            .CheckAsync(state, newTree, empty, null, CancellationToken.None);

        Assert.False(check.BaselineIsPreviousVersion);
        Assert.Null(check.OldTree);
        Assert.Equal(Path.GetFullPath(empty), check.EmptyBaseline);
    }

    /// <summary>
    /// Моды с общим началом <c>&lt;id&gt;</c> (clock и clock_lite): каждый файл связан со своим
    /// проектом, сколько бы проектов ни стояло раньше в списке; переход видит оба мода.
    /// </summary>
    [Fact]
    public void Detect_PrefixSharingIds_EachLinkedToOwnProject()
    {
        Game g = NewGame();
        string clock = Project("clock");
        string lite = Project("clock_lite");
        Mod(g.Old, "ru.test.clock_1.0.0.mtmod", "ru.test.clock", DateTime.UtcNow.AddDays(-1));
        Mod(g.Old, "ru.test.clock_lite_1.0.0.mtmod", "ru.test.clock_lite", DateTime.UtcNow);

        // Архив без meta.xml связывается по имени файла — с самым длинным подходящим <id>.
        string bare = Path.Combine(g.Old, "ru.test.clock_lite_0.9.0.mtmod.off");
        FakeGameDir.WriteArchive(bare, null, [FakeGameDir.Entry("res/x.pyc")], compress: false);

        ModLibraryScan scan = new ModScanner().Scan(g.Old, [clock, lite]);
        Assert.Equal(clock, scan.Mods.Single(m => m.FileName == "ru.test.clock_1.0.0.mtmod").Mine!.ProjectDir);
        Assert.Equal(lite, scan.Mods.Single(m => m.FileName == "ru.test.clock_lite_1.0.0.mtmod").Mine!.ProjectDir);
        Assert.Equal(lite, scan.Mods.Single(m => m.FileName == "ru.test.clock_lite_0.9.0.mtmod.off").Mine!.ProjectDir);

        ClientMigrationState s = ClientMigrationDetector.Detect(g.Install, [clock, lite]);
        Assert.Equal(["ru.test.clock", "ru.test.clock_lite"], s.Pending.Select(m => m.Mine!.ModId).OrderBy(x => x));
    }

    /// <summary>
    /// Установка на экране «Моды» выключает прежние версии ТОГО ЖЕ мода, а не всех, чьё имя файла
    /// начинается так же: clock_lite не страдает от установки clock.
    /// </summary>
    [Fact]
    public void Install_DisablesOnlySameMod_NotPrefixSibling()
    {
        Game g = NewGame();
        Mod(g.New, "ru.test.clock_1.0.0.mtmod", "ru.test.clock", DateTime.UtcNow.AddDays(-2));
        Mod(g.New, "ru.test.clock_lite_1.0.0.mtmod", "ru.test.clock_lite", DateTime.UtcNow.AddDays(-2));
        string bareOld = Path.Combine(g.New, "ru.test.clock_0.9.0.mtmod");
        FakeGameDir.WriteArchive(bareOld, null, [FakeGameDir.Entry("res/x.pyc")], compress: false);
        string newBuild = Path.Combine(_tmp.MakeDir("dist"), "ru.test.clock_1.1.0.mtmod");
        Mod(Path.GetDirectoryName(newBuild)!, Path.GetFileName(newBuild), "ru.test.clock", DateTime.UtcNow);
        var library = new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
        {
            OperationLogPath = Path.Combine(g.Root, "ops.log"),
            WriteOperationLog = false,
        });

        ModOperationResult r = library.Install(g.Install, newBuild, new ModInstallOptions { DisablePreviousVersions = true });

        Assert.True(r.Success, r.Message);
        Assert.True(File.Exists(Path.Combine(g.New, "ru.test.clock_1.0.0.mtmod.off")));
        Assert.True(File.Exists(bareOld + ".off"));
        Assert.True(File.Exists(Path.Combine(g.New, "ru.test.clock_lite_1.0.0.mtmod")));
        Assert.False(File.Exists(Path.Combine(g.New, "ru.test.clock_lite_1.0.0.mtmod.off")));
    }

    /// <summary>Чужой архив со своим &lt;id&gt; не становится «моим» из-за совпадения начала имени файла.</summary>
    [Fact]
    public void Scan_ForeignIdWithMyPrefix_NotMine()
    {
        Game g = NewGame();
        string clock = Project("clock");
        Mod(g.Old, "ru.test.clock_addon_1.0.mtmod", "com.other.addon", DateTime.UtcNow);

        ModLibraryScan scan = new ModScanner().Scan(g.Old, [clock]);

        Assert.False(scan.Mods.Single().IsMine);
    }

    /// <summary>Каталог с именем версии, но с отметкой другой версии, — не та версия.</summary>
    [Fact]
    public void FindExisting_MarkerOverridesName()
    {
        string current = Tree("scratch/wot-src");
        string misnamed = Tree("scratch/wot-src-1.46");
        ClientSourceTrees.WriteMarker(misnamed, new ClientSourceMarker { Version = "1.45.0.0" });

        Assert.Null(ClientSourceTrees.FindExisting("1.46.0.0", current, null));
    }

    // ================================================================= вывод uncompyle6

    /// <summary>Успех — только со строкой «# okay decompiling»; код возврата 0 сам по себе ничего не значит.</summary>
    [Fact]
    public void Interpret_SuccessOnlyWithOkayMarker()
    {
        const string header = "# uncompyle6 version 3.9.3\n# Python bytecode version base 2.7 (62211)\n# Embedded file name: scripts/client/X.py\n";

        DecompileOutcome ok = Uncompyle6Decompiler.Interpret(header + "x = 1\n# okay decompiling X.pyc\n", string.Empty, 0);
        Assert.True(ok.HasText);
        Assert.False(ok.Partial);
        Assert.Null(ok.Error);

        // Разбор модуля упал: в stdout шапка и отладка парсера (как печатает настоящий uncompyle6
        // 3.9.3), ни строки исходника; ошибка в stderr, код 0. Это «не вышло», а не «частично».
        DecompileOutcome moduleFailed = Uncompyle6Decompiler.Interpret(header + ParserDump + "\n", "Traceback...\nParserError: bad grammar\n", 0);
        Assert.False(moduleFailed.HasText);
        Assert.Equal("ParserError: bad grammar", moduleFailed.Error);

        // Неподдерживаемый байткод: сообщение голой строкой в stdout, код 0.
        DecompileOutcome unsupported = Uncompyle6Decompiler.Interpret(header + "Unsupported Python version, 3.11, for decompilation\n", string.Empty, 0);
        Assert.False(unsupported.HasText);
        Assert.Contains("Unsupported Python version", unsupported.Error);

        // Код есть, а строки успеха нет и признака «сдался на вложенной функции» тоже — не вышло.
        DecompileOutcome truncated = Uncompyle6Decompiler.Interpret(header + "def f():\n    return 1\n", "ValueError: boom\n", 0);
        Assert.False(truncated.HasText);

        // Вложенная функция не разобралась: отладка парсера, затем текст модуля; признак — в stderr.
        DecompileOutcome partial = Uncompyle6Decompiler.Interpret(
            header + ParserDump + "import random, math\n\ndef clamp(a, b, v):\n    pass\n",
            "\n# file X.pyc\n# Deparsing stopped due to parse error\n", 0);
        Assert.True(partial.HasText);
        Assert.True(partial.Partial);
        Assert.Equal("# Deparsing stopped due to parse error", partial.Error);
        Assert.DoesNotContain("Stacks of completed symbols", partial.Text);
        Assert.DoesNotContain("LOAD_FAST", partial.Text);
        Assert.Contains("import random, math", partial.Text);
        Assert.StartsWith("# uncompyle6 version", partial.Text);

        DecompileOutcome crashed = Uncompyle6Decompiler.Interpret(header + "x = 1\n# okay decompiling X.pyc\n", "Segfault", 1);
        Assert.False(crashed.HasText);
    }

    /// <summary>Отладочный вывод парсера в том виде, в каком его печатает uncompyle6 3.9.3 (сокращён).</summary>
    private const string ParserDump =
        "\n-- Stacks of completed symbols:\n" +
        "START ::= |- stmts . \n" +
        "with ::= expr . SETUP_WITH POP_TOP \\e_suite_stmts_opt POP_BLOCK LOAD_CONST COME_FROM_WITH\n" +
        "yield ::= expr . YIELD_VALUE\n" +
        "Instruction context:\n" +
        "-> \n" +
        " L.  45         0  LOAD_FAST             2  'val'\n" +
        "                   3  LOAD_FAST             0  'minVal'\n" +
        "                  35  RETURN_VALUE_LAMBDA\n";

    // ================================================================= извлечение исходников

    /// <summary>
    /// scripts.pkg → дерево: только client/client_common/common, частичные с пометкой, неудачные
    /// посчитаны, модули доп. пакетов и отметка записаны, временный каталог убран.
    /// </summary>
    [Fact]
    public async Task Extract_BuildsTree_WithMarkerAndExtraModules()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg",
            "scripts/client/Avatar.pyc",
            "scripts/client/gui/mods/__init__.pyc",
            "scripts/common/constants.pyc",
            "scripts/client_common/shared.pyc",
            "scripts/client/broken.pyc",
            "scripts/client/half.pyc",
            "scripts/item_defs/skip.pyc",
            "scripts/client/readme.txt");
        Pkg(g.Root, "battle_royale.pkg", "battle_royale/scripts/client/battle_royale/gui/__init__.pyc", "battle_royale/scripts/client/battle_royale/gui/page.pyc");
        string target = Path.Combine(_tmp.Path, "src", "wot-src-1.46");

        var progress = new List<ClientSourceProgress>();
        ClientSourceExtraction r = await new ClientSourceExtractor(new FakeDecompiler(), parallelism: 3)
            .ExtractAsync(g.Install, target, new SyncProgress<ClientSourceProgress>(progress.Add), CancellationToken.None);

        Assert.Equal(target, r.TreeDir);
        Assert.Equal(6, r.Total);
        Assert.Equal(4, r.Decompiled);
        Assert.Equal(1, r.Partial);
        Assert.Equal(1, r.Failed);
        Assert.Equal(2, r.ExtraModules);

        Assert.Equal("# decompiled scripts/client/Avatar.pyc", File.ReadAllText(Path.Combine(target, "client", "Avatar.py")));
        Assert.True(File.Exists(Path.Combine(target, "client", "gui", "mods", "__init__.py")));
        Assert.True(File.Exists(Path.Combine(target, "client_common", "shared.py")));
        Assert.StartsWith(ClientSourceExtractor.PartialHeader + "\n", File.ReadAllText(Path.Combine(target, "client", "half.py")));
        Assert.False(File.Exists(Path.Combine(target, "client", "broken.py")));
        Assert.False(Directory.Exists(Path.Combine(target, "item_defs")));
        Assert.False(Directory.Exists(Path.Combine(target, ".pyc")));

        Assert.Equal("1.46.0.0", ClientSourceTrees.VersionOf(target));
        Assert.Equal(["battle_royale.gui", "battle_royale.gui.page"], ClientSourceTrees.ReadExtraModules(target));
        Assert.Equal(["broken"], ClientSourceTrees.ReadFailedModules(target));
        Assert.True(ClientSourceTrees.LooksLikeTree(target));
        Assert.Single(Directory.GetDirectories(Path.GetDirectoryName(target)!));
        Assert.Contains(progress, p => p.Stage == "декомпиляция" && p.Done == 6 && p.Total == 6);
    }

    /// <summary>
    /// Цель глубоко, файлы пакета с длинными путями — а декомпилятору достаются короткие пути:
    /// Python без длинных путей Windows не открыл бы .pyc длиннее 260 символов (так на настоящем
    /// клиенте пропали 26 файлов gui\impl\gen\view_models\…). Копии после работы убраны.
    /// </summary>
    [Fact]
    public async Task Extract_DeepTarget_DecompilerGetsShortPaths()
    {
        Game g = NewGame();
        const string deep = "scripts/client/gui/impl/gen/view_models/views/lobby/personal_missions/tooltips/personal_missions_quests_type_tooltip_model.pyc";
        Pkg(g.Root, "scripts.pkg", deep, "scripts/client/Avatar.pyc");
        string target = Path.Combine(_tmp.MakeDir(string.Join("/", Enumerable.Repeat("very-long-folder-name", 5))), "wot-src-1.46");
        string[] before = Directory.GetDirectories(Path.GetTempPath(), "wotcockpit-pyc-*");
        var decompiler = new FakeDecompiler();

        ClientSourceExtraction r = await new ClientSourceExtractor(decompiler).ExtractAsync(g.Install, target, null, CancellationToken.None);

        Assert.Equal(2, r.Decompiled);
        Assert.True(Path.Combine(target, deep["scripts/".Length..^1]).Length > 260);
        Assert.All(decompiler.Seen, p => Assert.True(p.Length < 120, p));
        Assert.True(File.Exists(Path.Combine(target, deep["scripts/".Length..^1].Replace('/', Path.DirectorySeparatorChar))));
        Assert.Equal(before, Directory.GetDirectories(Path.GetTempPath(), "wotcockpit-pyc-*"));
    }

    /// <summary>Готовый каталог не перезаписывается.</summary>
    [Fact]
    public async Task Extract_TargetExists_Refuses()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", "scripts/client/Avatar.pyc");
        string target = _tmp.MakeDir("wot-src-1.46");
        File.WriteAllText(Path.Combine(target, "mine.txt"), "моё");

        await Assert.ThrowsAsync<IOException>(() =>
            new ClientSourceExtractor(new FakeDecompiler()).ExtractAsync(g.Install, target, null, CancellationToken.None));
        Assert.Equal("моё", File.ReadAllText(Path.Combine(target, "mine.txt")));
    }

    /// <summary>Отмена посреди декомпиляции: ни дерева, ни временного каталога.</summary>
    [Fact]
    public async Task Extract_Cancelled_LeavesNothing()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", Enumerable.Range(0, 40).Select(i => $"scripts/client/m{i}.pyc").ToArray());
        string parent = _tmp.MakeDir("trees");
        string target = Path.Combine(parent, "wot-src-1.46");
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ClientSourceExtractor(new FakeDecompiler(onCall: n => { if (n == 5) cts.Cancel(); }), parallelism: 1)
                .ExtractAsync(g.Install, target, null, cts.Token));

        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    /// <summary>
    /// Не декомпилировался ни один файл (сломанный uncompyle6): ошибка с первой причиной, а не
    /// пустое «готовое» дерево, на которое кокпит переключился бы.
    /// </summary>
    [Fact]
    public async Task Extract_AllFailed_ThrowsAndLeavesNothing()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", "scripts/client/broken.pyc", "scripts/common/broken.pyc");
        string parent = _tmp.MakeDir("trees-failed");
        string target = Path.Combine(parent, "wot-src-1.46");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClientSourceExtractor(new FakeDecompiler()).ExtractAsync(g.Install, target, null, CancellationToken.None));

        Assert.Contains("не вышло 2 из 2", ex.Message);
        Assert.Contains("Parse error", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    /// <summary>Только частичные файлы, ни одного целого — тоже не дерево: декомпилятор не справился.</summary>
    [Fact]
    public async Task Extract_OnlyPartial_Throws()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", "scripts/client/half.pyc", "scripts/common/half.pyc");
        string parent = _tmp.MakeDir("trees-partial");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClientSourceExtractor(new FakeDecompiler()).ExtractAsync(g.Install, Path.Combine(parent, "wot-src-1.46"), null, CancellationToken.None));

        Assert.Contains("частично 2", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    /// <summary>В scripts.pkg нет ни одного нужного .pyc — ошибка, а не пустое дерево.</summary>
    [Fact]
    public async Task Extract_NoMatchingEntries_Throws()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", "scripts/item_defs/x.pyc", "readme.txt");
        string parent = _tmp.MakeDir("trees-empty");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ClientSourceExtractor(new FakeDecompiler()).ExtractAsync(g.Install, Path.Combine(parent, "wot-src-1.46"), null, CancellationToken.None));

        Assert.Empty(Directory.GetFileSystemEntries(parent));
    }

    /// <summary>
    /// Готовое дерево не переименовать (файл в нём держит чужой процесс): работа НЕ удаляется —
    /// временный каталог остаётся, его путь в ошибке.
    /// </summary>
    [Fact]
    public async Task Extract_MoveFails_KeepsFinishedWork()
    {
        Game g = NewGame();
        Pkg(g.Root, "scripts.pkg", "scripts/client/a.pyc", "scripts/client/b.pyc", "scripts/client/c.pyc");
        string parent = _tmp.MakeDir("trees-locked");
        string target = Path.Combine(parent, "wot-src-1.46");
        FileStream? held = null;

        try
        {
            var decompiler = new FakeDecompiler(onCall: n =>
            {
                if (n == 3)
                {
                    // Третий файл: первый уже записан — «антивирус» открывает его и не отпускает.
                    string staging = Directory.GetDirectories(parent, "wot-src-1.46.partial-*").Single();
                    held = new FileStream(Path.Combine(staging, "client", "a.py"), FileMode.Open, FileAccess.Read, FileShare.None);
                }
            });

            var ex = await Assert.ThrowsAsync<IOException>(() =>
                new ClientSourceExtractor(decompiler, parallelism: 1).ExtractAsync(g.Install, target, null, CancellationToken.None));

            string kept = Directory.GetDirectories(parent, "wot-src-1.46.partial-*").Single();
            Assert.Contains(kept, ex.Message);
            Assert.False(Directory.Exists(target));
            Assert.True(File.Exists(Path.Combine(kept, "client", "c.py")));
            Assert.True(File.Exists(Path.Combine(kept, ClientSourceTrees.MarkerFile)));
        }
        finally
        {
            held?.Dispose();
        }
    }

    /// <summary>Нет scripts.pkg — понятная ошибка, а не пустое дерево.</summary>
    [Fact]
    public async Task Extract_NoScriptsPkg_Throws()
    {
        Game g = NewGame();
        string target = Path.Combine(_tmp.Path, "wot-src-1.46");

        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            new ClientSourceExtractor(new FakeDecompiler()).ExtractAsync(g.Install, target, null, CancellationToken.None));
        Assert.False(Directory.Exists(target));
    }

    // ================================================================= помощники

    private sealed record Game(string Root, string Old, string New, GameInstall Install);

    private Game NewGame()
    {
        string root = _tmp.MakeDir("game-" + Guid.NewGuid().ToString("N")[..6]);
        string old = Path.Combine(root, "mods", "1.45.0.0");
        string @new = Path.Combine(root, "mods", "1.46.0.0");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(@new);
        File.WriteAllText(Path.Combine(@new, "readme.txt"), "положите моды сюда");
        return new Game(root, old, @new, new GameInstall(root, "1.46.0.0", @new,
            Path.Combine(root, "res_mods", "1.46.0.0"), Path.Combine(root, "python.log")));
    }

    /// <summary>Свой проект: build.py, src\meta.xml и точка входа.</summary>
    private string Project(string name, string? code = null)
    {
        string dir = Path.Combine(_tmp.Path, "repos", "wot-mod-" + name);
        string id = "ru.test." + name;
        Directory.CreateDirectory(Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods"));
        File.WriteAllText(Path.Combine(dir, "build.py"), "# fake\n");
        File.WriteAllText(Path.Combine(dir, "src", "meta.xml"), FakeGameDir.Meta(id, "1.0.0", char.ToUpperInvariant(name[0]) + name[1..], "описание"));
        File.WriteAllText(Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods", $"mod_{name}.py"),
            code ?? "import BigWorld\n\ndef init():\n    pass\n");
        return dir;
    }

    /// <summary>Проект-перехватчик: метод аватара, константа и модуль помощников клиента.</summary>
    private ModProject HookProject()
    {
        string dir = Project("hook",
            "from Avatar import PlayerAvatar\n" +
            "import constants\n" +
            "from gui.shared_helpers import formatName\n" +
            "\n" +
            "_orig = PlayerAvatar.showShotResults\n" +
            "\n" +
            "def _showShotResults(self, results):\n" +
            "    if constants.ARENA_PERIOD_BATTLE:\n" +
            "        formatName(results)\n" +
            "    return _orig(self, results)\n" +
            "\n" +
            "PlayerAvatar.showShotResults = _showShotResults\n" +
            "HOOKED = ('onEnterWorld',)\n" +
            "\n" +
            "def init():\n" +
            "    try:\n" +
            "        helper = None\n" +
            "        helper.update()\n" +
            "    except Exception:\n" +
            "        pass\n");
        return new ModProject { ProjectDir = dir, ModId = "ru.test.hook", DisplayName = "Hook", Version = "1.0.0" };
    }

    /// <summary>Проект с одним файлом мода заданного текста.</summary>
    private ModProject Simple(string name, string code) => new()
    {
        ProjectDir = Project(name, code),
        ModId = "ru.test." + name,
        DisplayName = name,
        Version = "1.0.0",
    };

    private static void Mod(string modsDir, string file, string id, DateTime writeUtc)
    {
        string path = Path.Combine(modsDir, file);
        FakeGameDir.WriteArchive(path, FakeGameDir.Meta(id, "1.0.0", id, "описание"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_x.pyc", "bytecode " + file)], compress: false);
        File.SetLastWriteTimeUtc(path, writeUtc);
    }

    /// <summary>Пара деревьев «1.45 → 1.46» с одинаковым содержимым; тест портит новое как нужно.</summary>
    private (string OldTree, string NewTree) Trees()
    {
        string oldTree = Tree("trees/wot-src-1.45");
        string newTree = Tree("trees/wot-src-1.46");
        foreach (string tree in new[] { oldTree, newTree })
        {
            Write(tree, "client/Avatar.py",
                "import BigWorld\n" +
                "\n" +
                "class PlayerAvatar(BigWorld.Entity):\n" +
                "\n" +
                "    def onEnterWorld(self, prereqs):\n" +
                "        pass\n" +
                "\n" +
                "    def showShotResults(self, results):\n" +
                "        pass\n" +
                "\n" +
                "\n" +
                "class AvatarHelper(object):\n" +
                "\n" +
                "    def update(self):\n" +
                "        pass\n");
            Write(tree, "common/constants.py", "ARENA_PERIOD_IDLE = 0\nARENA_PERIOD_BATTLE = 3\n");
            Write(tree, "client/gui/__init__.py", "g_keyEventHandlers = set()\ng_mouseEventHandlers = set()\n");
            Write(tree, "client/gui/shared_helpers.py", "def formatName(vehicleID):\n    return str(vehicleID)\n");
            Write(tree, "client/SiegeHelper.py",
                "class SiegeHelper(object):\n" +
                "\n" +
                "    def __onSiegeStateUpdated(self, vehicleID, newState):\n" +
                "        pass\n");
            Write(tree, "common/th_async.py", "def th_async(func=None):\n    return func\n");
            Write(tree, "client/PlayerEvents.py",
                "import Event\n" +
                "\n" +
                "class _PlayerEvents(object):\n" +
                "\n" +
                "    def __init__(self):\n" +
                "        self.onArenaPeriodChange = Event.Event()\n" +
                "        self.onAvatarReady = Event.Event()\n" +
                "\n" +
                "    def isPlayerEntityChanging(self):\n" +
                "        return False\n" +
                "\n" +
                "\n" +
                "g_playerEvents = _PlayerEvents()\n");
            Write(tree, "client/siege_ctrl.py",
                "class SiegeCtrl(object):\n" +
                "\n" +
                "    def __onUpdate(self, a):\n" +
                "        pass\n");
        }

        return (oldTree, newTree);
    }

    private string Tree(string relative)
    {
        string dir = _tmp.MakeDir(relative);
        Directory.CreateDirectory(Path.Combine(dir, "client"));
        return dir;
    }

    private static void Write(string tree, string rel, string text)
    {
        string path = Path.Combine(tree, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static void Append(string tree, string rel, string text) =>
        File.AppendAllText(Path.Combine(tree, rel.Replace('/', Path.DirectorySeparatorChar)), text);

    private static void Replace(string tree, string rel, string from, string to)
    {
        string path = Path.Combine(tree, rel.Replace('/', Path.DirectorySeparatorChar));
        string text = File.ReadAllText(path);
        Assert.Contains(from, text);
        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static ApiIndex Index(string tree) => ClientMigrationPlanner.BuildIndex(tree, CancellationToken.None);

    private static ModCompatibilityChecker Checker(string oldTree, string newTree, IReadOnlyList<string>? newExtra = null) =>
        new(Index(oldTree), Index(newTree), new SourceFileService(), [], newExtra ?? []);

    /// <summary>Пакет игры: ZIP с пустыми записями.</summary>
    private static void Pkg(string gameRoot, string name, params string[] entries)
    {
        string dir = Path.Combine(gameRoot, "res", "packages");
        Directory.CreateDirectory(dir);
        using ZipArchive zip = ZipFile.Open(Path.Combine(dir, name), ZipArchiveMode.Create);
        foreach (string e in entries)
        {
            using Stream s = zip.CreateEntry(e).Open();
            s.Write(Encoding.ASCII.GetBytes("pyc " + e));
        }
    }

    private static string Fingerprint(string root) =>
        string.Join("\n", Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => f + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks));

    /// <summary>
    /// Декомпилятор-подмена: текст — путь записи; <c>broken</c> — неудача, <c>half</c> — частичная.
    /// </summary>
    private sealed class FakeDecompiler(Action<int>? onCall = null) : IPycDecompiler
    {
        private int _calls;

        /// <summary>Пути .pyc, которые дали декомпилятору.</summary>
        public System.Collections.Concurrent.ConcurrentBag<string> Seen { get; } = new();

        public Task<DecompileOutcome> DecompileAsync(string pycPath, CancellationToken ct)
        {
            Seen.Add(pycPath);
            onCall?.Invoke(Interlocked.Increment(ref _calls));
            ct.ThrowIfCancellationRequested();

            // Копии .pyc лежат под номерами — какой это файл пакета, знает только его содержимое.
            string entry = File.ReadAllText(pycPath)["pyc ".Length..];
            string name = Path.GetFileNameWithoutExtension(entry);
            return Task.FromResult(name switch
            {
                "broken" => new DecompileOutcome(string.Empty, false, "Parse error"),
                "half" => new DecompileOutcome("# decompiled " + entry, true, "Parse error at or near"),
                _ => new DecompileOutcome("# decompiled " + entry, false, null),
            });
        }
    }

    /// <summary><see cref="IProgress{T}"/> без контекста синхронизации: вызов сразу, в том же потоке.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        private readonly object _gate = new();

        public void Report(T value)
        {
            lock (_gate)
            {
                report(value);
            }
        }
    }
}
