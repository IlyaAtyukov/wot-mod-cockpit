using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.ViewModels;
using WotModCockpit.App.Views;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Bundle;
using WotModCockpit.Core.Migration;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Окно «Переход на новую версию клиента»: модель (<see cref="ClientMigrationViewModel"/>), окно
/// (<see cref="ClientMigrationDialog"/>) и баннер экрана «Моды».
/// <para>
/// Игра фейковая (каталоги <c>mods\1.45.0.0</c> и <c>mods\1.46.0.0</c> во временной папке), служба
/// библиотеки — настоящая, с пробой «игра закрыта». Подменены только извлечение исходников,
/// проверка (её итог задаёт тест), build.py и переключение кокпита на новое дерево.
/// </para>
/// </summary>
[Collection("WpfBindingTrace")]
public sealed class ClientMigrationViewModelTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0);

    private readonly CoreTempDir _tmp = new("wotcockpit-migrationvm");
    private readonly string _root;
    private readonly string _old;
    private readonly string _new;
    private readonly GameInstall _game;
    private readonly string _trees;
    private readonly string _currentTree;
    private readonly ModLibraryService _library;

    public ClientMigrationViewModelTests()
    {
        _root = _tmp.MakeDir("game");
        _old = Path.Combine(_root, "mods", "1.45.0.0");
        _new = Path.Combine(_root, "mods", "1.46.0.0");
        Directory.CreateDirectory(_old);
        Directory.CreateDirectory(_new);
        _game = new GameInstall(_root, "1.46.0.0", _new, Path.Combine(_root, "res_mods", "1.46.0.0"), Path.Combine(_root, "python.log"));

        _trees = _tmp.MakeDir("scratch");
        _currentTree = Tree("wot-src-1.45");

        _library = new ModLibraryService(FakeGameProbes.Closed, new ModLibraryOptions
        {
            OperationLogPath = Path.Combine(_root, "mods-operations.log"),
            WriteOperationLog = false,
        });
    }

    public void Dispose() => _tmp.Dispose();

    // ================================================================= основной путь

    /// <summary>
    /// Дерево новой версии уже есть: извлечения нет, проверка, переключение кокпита и перенос
    /// ТОЛЬКО совместимого. Проблемный мод не отмечен и не тронут, каталог прошлой версии цел.
    /// </summary>
    [Fact]
    public async Task Start_ExistingTree_TransfersOnlyCompatible()
    {
        string newTree = Tree("wot-src-1.46");
        Setup("alpha", "beta");
        string oldBefore = Fingerprint(_old);
        var fx = new Fakes(this) { Verdicts = { ["ru.test.beta"] = false } };
        ClientMigrationViewModel vm = Model(fx);

        Assert.True(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, fx.Extracted);
        Assert.Equal([newTree], fx.Switched);
        Assert.Equal(Path.GetFullPath(newTree), fx.CheckedNewTree);
        Assert.Equal(_currentTree, fx.CheckedOldTree);
        Assert.True(File.Exists(Path.Combine(_new, "ru.test.alpha_1.0.0.mtmod")));
        Assert.False(File.Exists(Path.Combine(_new, "ru.test.beta_1.0.0.mtmod")));
        Assert.Equal(oldBefore, Fingerprint(_old));

        MigrationRowViewModel alpha = Row(vm, "alpha");
        MigrationRowViewModel beta = Row(vm, "beta");
        Assert.True(alpha.IsTransferred);
        Assert.Contains("сборка из dist", alpha.ResultText);
        Assert.False(beta.IsChecked);
        Assert.True(beta.HasProblem);
        Assert.Contains("есть проблемы: 1", beta.VerdictText);
        Assert.Contains("БЛОКИРУЕТ", beta.Details);

        Assert.Equal(1, vm.TransferredCount);
        Assert.StartsWith("Совместимы 1 из 2, перенесено 1.", vm.StatusText);
        Assert.Contains("С проблемами совместимости — 1", vm.StatusText);
        Assert.EndsWith("при следующем запуске игры.", vm.StatusText);
        Assert.Single(fx.Journal, r => r.Success);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Contains(vm.Log, l => l.Contains("уже есть"));
    }

    /// <summary>Дерева нет — извлекается рядом с текущим (<c>wot-src-1.46</c>), и проверка идёт по нему.</summary>
    [Fact]
    public async Task Start_NoTree_ExtractsNextToCurrent()
    {
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        string expected = Path.Combine(_trees, "wot-src-1.46");
        Assert.Equal([expected], fx.ExtractTargets);
        Assert.Equal([expected], fx.Switched);
        Assert.Equal(expected, vm.NewTree);
        Assert.True(Row(vm, "alpha").IsTransferred, vm.StatusText);
    }

    /// <summary>
    /// Без автопереноса только проверка: в игру ничего; дальше «Перенести отмеченные» ставит
    /// отмеченное, а проблемный мод — только после отдельного «да».
    /// </summary>
    [Fact]
    public async Task ManualTransfer_AsksForProblemMods()
    {
        Tree("wot-src-1.46");
        Setup("alpha", "beta");
        var fx = new Fakes(this) { Verdicts = { ["ru.test.beta"] = false } };
        ClientMigrationViewModel vm = Model(fx);
        vm.AutoTransfer = false;

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsChecked);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.Contains("Отмечено к переносу — 1", vm.StatusText);
        Assert.DoesNotContain("запуске игры", vm.StatusText);
        Assert.True(Row(vm, "alpha").IsChecked);
        Assert.True(vm.TransferSelectedCommand.CanExecute(null));

        Row(vm, "beta").IsChecked = true;
        fx.Answer = false;
        await vm.TransferSelectedCommand.ExecuteAsync(null);
        Assert.Single(fx.Questions);
        Assert.Contains("Beta", fx.Questions[0]);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));

        fx.Answer = true;
        await vm.TransferSelectedCommand.ExecuteAsync(null);
        Assert.Equal(2, fx.Questions.Count);
        Assert.True(File.Exists(Path.Combine(_new, "ru.test.alpha_1.0.0.mtmod")));
        Assert.True(File.Exists(Path.Combine(_new, "ru.test.beta_1.0.0.mtmod")));
        Assert.Equal(2, vm.TransferredCount);
        Assert.False(vm.TransferSelectedCommand.CanExecute(null));
    }

    /// <summary>Совместимые без вопроса: вопрос только когда в отмеченном есть проблемные.</summary>
    [Fact]
    public async Task ManualTransfer_OnlyCompatible_NoQuestion()
    {
        Tree("wot-src-1.46");
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx);
        vm.AutoTransfer = false;

        await vm.StartCommand.ExecuteAsync(null);
        await vm.TransferSelectedCommand.ExecuteAsync(null);

        Assert.Empty(fx.Questions);
        Assert.True(Row(vm, "alpha").IsTransferred);
    }

    // ================================================================= сборка

    /// <summary>
    /// Исходники правились после сборки: ставится не устаревший dist, а то, что собрал build.py.
    /// </summary>
    [Fact]
    public async Task StaleBuild_RebuildsAndInstallsWhatBuildPyReported()
    {
        Tree("wot-src-1.46");
        string alpha = Setup("alpha").Single();
        string src = Path.Combine(alpha, "src", "res", "scripts", "client", "gui", "mods", "mod_alpha.py");
        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(5));

        string fresh = Path.Combine(_tmp.Path, "fresh", "ru.test.alpha_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        FakeGameDir.WriteArchive(fresh, FakeGameDir.Meta("ru.test.alpha", "1.0.0", "Alpha", "свежая"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "СВЕЖАЯ СБОРКА")], compress: false);

        var fx = new Fakes(this) { BuildResult = dir => Built(dir, fresh) };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([alpha], fx.Built);
        Assert.Equal(File.ReadAllBytes(fresh), File.ReadAllBytes(Path.Combine(_new, "ru.test.alpha_1.0.0.mtmod")));
        Assert.Contains("пересобран", Row(vm, "alpha").ResultText);
    }

    /// <summary>Сборка упала — мод не перенесён, причина в строке и в итоге; остальные переносятся.</summary>
    [Fact]
    public async Task BuildFails_RowFailed_OthersStillTransferred()
    {
        Tree("wot-src-1.46");
        string[] dirs = Setup("alpha", "beta");
        File.Delete(Directory.GetFiles(Path.Combine(dirs[0], "dist")).Single());

        var fx = new Fakes(this) { BuildResult = dir => Failed(dir) };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        MigrationRowViewModel alpha = Row(vm, "alpha");
        Assert.True(alpha.HasFailed);
        Assert.False(alpha.IsTransferred);
        Assert.Contains("SyntaxError", alpha.ResultText);
        Assert.True(Row(vm, "beta").IsTransferred);
        Assert.Contains("Не удалось перенести — 1", vm.StatusText);
    }

    /// <summary>Игра запущена — служба библиотеки отказывает, отказ виден в строке и в журнале.</summary>
    [Fact]
    public async Task GameRunning_InstallRefused()
    {
        Tree("wot-src-1.46");
        Setup("alpha");
        var running = new ModLibraryService(FakeGameProbes.Running, new ModLibraryOptions
        {
            OperationLogPath = Path.Combine(_root, "mods-operations.log"),
            WriteOperationLog = false,
        });
        var fx = new Fakes(this) { Library = running };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(Row(vm, "alpha").HasFailed);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.Single(fx.Journal, r => !r.Success);
    }

    // ================================================================= сбои и отмена

    /// <summary>Извлечь не вышло (нет uncompyle6): ошибка на виду, ничего не переключено и не перенесено, можно повторить.</summary>
    [Fact]
    public async Task ExtractFails_ShowsReason_CanRetry()
    {
        Setup("alpha");
        var fx = new Fakes(this) { ExtractError = new InvalidOperationException("не найден Python с пакетами uncompyle6 и xdis") };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("uncompyle6", vm.StatusText);
        Assert.Empty(fx.Switched);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.False(vm.IsChecked);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    /// <summary>«Остановить» посреди извлечения: работа прекращается, окно снова можно закрыть.</summary>
    [Fact]
    public async Task Cancel_DuringExtraction()
    {
        Setup("alpha");
        var started = new TaskCompletionSource();
        var fx = new Fakes(this) { ExtractStarted = started };
        ClientMigrationViewModel vm = Model(fx);

        Task run = vm.StartCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsWorking);
        Assert.False(vm.CloseCommand.CanExecute(null));
        Assert.True(vm.CancelCommand.CanExecute(null));

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.IsWorking);
        Assert.Contains("отменён", vm.StatusText);
        Assert.Empty(fx.Switched);
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    /// <summary>Переключить кокпит не вышло — перенос всё равно идёт: моды уже проверены по новому дереву.</summary>
    [Fact]
    public async Task SwitchFails_TransferStillHappens()
    {
        Tree("wot-src-1.46");
        Setup("alpha");
        var fx = new Fakes(this) { SwitchError = new IOException("settings.json занят") };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains(vm.Log, l => l.Contains("НЕ переключён") && l.Contains("settings.json занят"));
        Assert.True(Row(vm, "alpha").IsTransferred);
    }

    /// <summary>Выключенные в прошлой версии — только для сведения: не отмечаются и не переносятся.</summary>
    [Fact]
    public void DisabledMods_InfoOnly_NothingToStart()
    {
        string dir = Project("beta");
        Mod(_old, "ru.test.beta_1.0.0.mtmod.off", "ru.test.beta");
        ClientMigrationState state = ClientMigrationDetector.Detect(_game, [dir]);
        ClientMigrationViewModel vm = new(Context(new Fakes(this), state));

        MigrationRowViewModel row = Assert.Single(vm.Rows);
        Assert.True(row.WasDisabled);
        Assert.False(row.CanTransfer);
        Assert.Contains("был выключен в 1.45.0.0", row.VerdictText);
        Assert.False(vm.StartCommand.CanExecute(null));
    }

    // ================================================================= после ревью

    /// <summary>
    /// Галочка, поставленная человеком у проблемного мода после автопереноса, включает «Перенести
    /// отмеченные»: кнопка узнаёт об этом сама, без повторной проверки.
    /// </summary>
    [Fact]
    public async Task TickingRow_EnablesTransferSelected()
    {
        Tree("wot-src-1.46");
        Setup("alpha", "beta");
        var fx = new Fakes(this) { Verdicts = { ["ru.test.beta"] = false } };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(Row(vm, "alpha").IsTransferred);
        Assert.False(vm.TransferSelectedCommand.CanExecute(null));

        int raised = 0;
        vm.TransferSelectedCommand.CanExecuteChanged += (_, _) => raised++;
        Row(vm, "beta").IsChecked = true;

        Assert.True(raised > 0);
        Assert.True(vm.TransferSelectedCommand.CanExecute(null));
    }

    /// <summary>
    /// Исходников прошлой версии нет, база — дерево неизвестной версии из настроек: «совместим» не
    /// доказано. Ничего не отмечено и не перенесено; ручной перенос — только после вопроса.
    /// </summary>
    [Fact]
    public async Task UnversionedBaseline_NoAutoTransfer_ManualAsks()
    {
        string unversioned = Tree("wot-src");
        string newTree = Tree("wot-src-1.46");
        Directory.Delete(_currentTree, recursive: true);
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx, unversioned);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(unversioned, fx.CheckedOldTree);
        Assert.False(vm.BaselineVerified);
        MigrationRowViewModel alpha = Row(vm, "alpha");
        Assert.False(alpha.IsChecked);
        Assert.False(alpha.IsVerified);
        Assert.Contains("без сравнения с 1.45.0.0", alpha.VerdictText);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.Contains("автоматически ничего не перенесено", vm.StatusText);
        Assert.Equal([newTree], fx.Switched);

        alpha.IsChecked = true;
        fx.Answer = false;
        await vm.TransferSelectedCommand.ExecuteAsync(null);
        Assert.Contains("сравнить не вышло", Assert.Single(fx.Questions));
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));

        fx.Answer = true;
        await vm.TransferSelectedCommand.ExecuteAsync(null);
        Assert.True(alpha.IsTransferred);
    }

    /// <summary>
    /// Окно открыли повторно, когда кокпит уже переключён на новое дерево, а исходников прошлой
    /// версии рядом нет: новое дерево само себе не база — сравнения нет, автопереноса тоже.
    /// </summary>
    [Fact]
    public async Task Reopened_CurrentTreeIsNew_NoBaseline_NoAutoTransfer()
    {
        string newTree = Path.Combine(_tmp.MakeDir("elsewhere"), "wot-src-1.46");
        Directory.CreateDirectory(Path.Combine(newTree, "client"));
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx, newTree);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, fx.Extracted);
        Assert.Equal(Path.GetFullPath(newTree), fx.CheckedNewTree);
        Assert.Null(fx.CheckedOldTree);
        Assert.False(vm.BaselineVerified);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.Contains(vm.Log, l => l.Contains("сравнивать не с чем"));
    }

    /// <summary>Каталог с нужным именем есть, но исходников в нём нет: внятная ошибка, ничего не затёрто.</summary>
    [Fact]
    public async Task TargetExistsButNotATree_ClearError()
    {
        string occupied = Path.Combine(_trees, "wot-src-1.46");
        Directory.CreateDirectory(occupied);
        File.WriteAllText(Path.Combine(occupied, "notes.txt"), "моё");
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, fx.Extracted);
        Assert.Contains("уже есть, но исходников клиента 1.46.0.0 в нём нет", vm.StatusText);
        Assert.Equal("моё", File.ReadAllText(Path.Combine(occupied, "notes.txt")));
        Assert.Empty(fx.Switched);
    }

    /// <summary>Индекс нового дерева пуст — кокпит на него не переключается.</summary>
    [Fact]
    public async Task EmptyNewTree_NotSwitched()
    {
        Tree("wot-src-1.46");
        Setup("alpha");
        var fx = new Fakes(this) { NewModules = 0, Verdicts = { ["ru.test.alpha"] = false } };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Empty(fx.Switched);
        Assert.Contains(vm.Log, l => l.Contains("НЕ переключён") && l.Contains("нет исходников"));
    }

    /// <summary>
    /// Окно открыто повторно (кокпит уже на новом дереве), а исходники прошлой версии лежат рядом:
    /// база найдена, сравнение доказательное, автоперенос идёт.
    /// </summary>
    [Fact]
    public async Task Reopened_PreviousTreeNextToNew_Verified()
    {
        string newTree = Tree("wot-src-1.46");
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx, newTree);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(_currentTree, fx.CheckedOldTree);
        Assert.True(vm.BaselineVerified);
        Assert.True(Row(vm, "alpha").IsTransferred);
    }

    /// <summary>
    /// Моды с общим началом &lt;id&gt; переносятся оба: перенос второго не выключает первый
    /// (правило «выключить прежние версии» узнаёт их по началу имени файла).
    /// </summary>
    [Fact]
    public async Task PrefixSharingIds_BothTransferred_NeitherDisabled()
    {
        Tree("wot-src-1.46");
        Setup("clock_lite", "clock");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.TransferredCount);
        Assert.True(File.Exists(Path.Combine(_new, "ru.test.clock_1.0.0.mtmod")));
        Assert.True(File.Exists(Path.Combine(_new, "ru.test.clock_lite_1.0.0.mtmod")));
        Assert.Empty(Directory.GetFiles(_new, "*.off"));
        Assert.False(ModsViewModel.MigrationInstallOptions.DisablePreviousVersions);
        Assert.False(ModsViewModel.MigrationInstallOptions.OverwriteSameName);
    }

    /// <summary>
    /// База — исходники прошлой версии, но модуля, который импортирует мод, в ней нет: этот мод
    /// проверен не до конца — не отмечен и не перенесён, остальные переносятся.
    /// </summary>
    [Fact]
    public async Task ModuleMissingFromBaseline_RowUnverified_OthersTransferred()
    {
        Tree("wot-src-1.46");
        Setup("alpha", "beta");
        var fx = new Fakes(this) { NotInBaseline = { "ru.test.beta" } };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.BaselineVerified);
        Assert.True(Row(vm, "alpha").IsTransferred);
        MigrationRowViewModel beta = Row(vm, "beta");
        Assert.False(beta.IsVerified);
        Assert.False(beta.IsChecked);
        Assert.False(beta.IsTransferred);
        Assert.Contains("проверен не до конца", beta.VerdictText);
        Assert.DoesNotContain("без сравнения", beta.VerdictText);
        Assert.Contains("Проверены не до конца — 1", vm.StatusText);

        // Ручной перенос: вопрос называет, что именно не сравнено, а не «сравнения не было».
        beta.IsChecked = true;
        fx.Answer = true;
        await vm.TransferSelectedCommand.ExecuteAsync(null);
        string question = Assert.Single(fx.Questions);
        Assert.Contains("модуля new_only нет в исходниках прошлой версии", question);
        Assert.DoesNotContain("сравнить не вышло", question);
        Assert.True(beta.IsTransferred);
    }

    /// <summary>
    /// Из src удалили файл (например, вторую точку входа с перехватом, которого больше нет), а
    /// пересобрать забыли: сборка в dist ещё содержит его — она устарела, и ставится свежая.
    /// </summary>
    [Fact]
    public async Task DeletedSourceFile_MakesDistStale_Rebuilds()
    {
        Tree("wot-src-1.46");
        string alpha = Setup("alpha").Single();
        // В собранном архиве — вторая точка входа; её исходник из src удалили, а пересобрать забыли.
        string legacy = Path.Combine(alpha, "src", "res", "scripts", "client", "gui", "mods", "mod_alpha_legacy.py");
        File.WriteAllText(legacy, "def init():\n    pass\n");
        File.SetLastWriteTimeUtc(legacy, DateTime.UtcNow.AddMinutes(-30));
        string dist = Directory.GetFiles(Path.Combine(alpha, "dist")).Single();
        FakeGameDir.WriteArchive(dist, FakeGameDir.Meta("ru.test.alpha", "1.0.0", "Alpha", "описание Alpha"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "bytecode alpha"),
             FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha_legacy.pyc", "bytecode legacy")], compress: false);
        File.SetLastWriteTimeUtc(dist, DateTime.UtcNow.AddMinutes(-10));
        Assert.False(SetupBundleCandidates.Read([alpha]).Single().IsStale);

        File.Delete(legacy);
        Assert.True(SetupBundleCandidates.Read([alpha]).Single().IsStale);

        string fresh = Path.Combine(_tmp.Path, "fresh", "ru.test.alpha_1.0.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(fresh)!);
        FakeGameDir.WriteArchive(fresh, FakeGameDir.Meta("ru.test.alpha", "1.0.0", "Alpha", "свежая"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "БЕЗ LEGACY")], compress: false);
        var fx = new Fakes(this) { BuildResult = dir => Built(dir, fresh) };
        ClientMigrationViewModel vm = Model(fx);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal([alpha], fx.Built);
        Assert.Equal(File.ReadAllBytes(fresh), File.ReadAllBytes(Path.Combine(_new, "ru.test.alpha_1.0.0.mtmod")));
    }

    /// <summary>«Остановить» посреди сборки снимает build.py, а не ждёт его конца; окно снова закрывается.</summary>
    [Fact]
    public async Task Cancel_DuringBuild_StopsBuild()
    {
        Tree("wot-src-1.46");
        string alpha = Setup("alpha").Single();
        File.Delete(Directory.GetFiles(Path.Combine(alpha, "dist")).Single());
        var started = new TaskCompletionSource();
        var fx = new Fakes(this) { BuildStarted = started };
        ClientMigrationViewModel vm = Model(fx);

        Task run = vm.StartCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsWorking);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(vm.IsWorking);
        Assert.Contains("отменён", vm.StatusText);
        Assert.Empty(Directory.GetFiles(_new, "*.mtmod"));
        Assert.True(vm.CloseCommand.CanExecute(null));
    }

    /// <summary>Текущее дерево лежит в папке игры — новое туда не кладётся: в игру пишут только моды.</summary>
    [Fact]
    public async Task TargetInsideGameFolder_Refused()
    {
        string inGame = Path.Combine(_root, "dev", "wot-src-1.45");
        Directory.CreateDirectory(Path.Combine(inGame, "client"));
        Setup("alpha");
        var fx = new Fakes(this);
        ClientMigrationViewModel vm = Model(fx, inGame);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, fx.Extracted);
        Assert.Contains("в папку игры", vm.StatusText);
        Assert.False(Directory.Exists(Path.Combine(_root, "dev", "wot-src-1.46")));
    }

    /// <summary>Один мод — «его», а не «их».</summary>
    [Fact]
    public async Task Banner_SingleMod_Pronoun()
    {
        string[] dirs = Setup("alpha");
        using ServiceProvider provider = App.AppServices.BuildServiceProvider();
        MainViewModel main = provider.GetRequiredService<MainViewModel>();
        main.GameDir = _root;
        using var mods = new ModsViewModel(main, _library, new FixedLocator(_game), new FixedSettings(dirs[0]), new NoEditor())
        {
            FindTemplateDir = () => null,
        };

        await mods.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => mods.HasPendingMigration);

        Assert.Contains("1 свой мод остался", mods.MigrationBannerText);
        Assert.Contains("в игре его сейчас нет", mods.MigrationBannerText);
        Assert.DoesNotContain(" их ", mods.MigrationBannerText);
    }

    // ================================================================= окно

    /// <summary>Окно собирается со всеми привязками: строки с флажками, вердикты, кнопки.</summary>
    [Fact]
    public async Task Dialog_ShowsRowsVerdictsAndButtons()
    {
        Tree("wot-src-1.46");
        Setup("alpha", "beta");
        var fx = new Fakes(this) { Verdicts = { ["ru.test.beta"] = false } };
        ClientMigrationViewModel vm = Model(fx);
        vm.AutoTransfer = false;
        await vm.StartCommand.ExecuteAsync(null);

        var bindingErrors = new List<string>();
        (int boxes, int checkedBoxes, bool transfer, bool start, string headline, bool closedByCommand) = WpfHost.Invoke(() =>
        {
            using var trace = BindingErrors.Attach(bindingErrors);
            var dialog = new ClientMigrationDialog
            {
                DataContext = vm,
                Left = -32000,
                Top = -32000,
                WindowStartupLocation = WindowStartupLocation.Manual,
                ShowActivated = false,
            };
            dialog.Show();
            Pump();

            // Посмотреть вёрстку глазами: WOTCOCKPIT_TEST_SNAPSHOTS=<папка> сохраняет картинку туда.
            if (Environment.GetEnvironmentVariable("WOTCOCKPIT_TEST_SNAPSHOTS") is { Length: > 0 } keep)
            {
                Directory.CreateDirectory(keep);
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(dialog);
                var png = new System.Windows.Media.Imaging.PngBitmapEncoder();
                png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using FileStream fs = File.Create(Path.Combine(keep, "client-migration-dialog.png"));
                png.Save(fs);
            }

            CheckBox[] rowBoxes = Descendants<CheckBox>(dialog).Where(c => c.Name != "AutoTransferBox").ToArray();
            var result = (
                rowBoxes.Length,
                rowBoxes.Count(b => b.IsChecked == true),
                ((Button)dialog.FindName("TransferButton")!).IsEnabled,
                ((Button)dialog.FindName("StartButton")!).IsEnabled,
                ((TextBlock)dialog.FindName("HeadlineText")!).Text,
                false);

            bool closed = false;
            dialog.Closed += (_, _) => closed = true;
            vm.CloseCommand.Execute(null);
            Pump();
            return result with { Item6 = closed };
        });

        Assert.Empty(bindingErrors);
        Assert.Equal(2, boxes);
        Assert.Equal(1, checkedBoxes);
        Assert.True(transfer);
        Assert.False(start);
        Assert.Contains("1.45.0.0 → 1.46.0.0", headline);
        Assert.True(closedByCommand);
    }

    // ================================================================= экран «Моды»

    /// <summary>
    /// Экран «Моды» сам замечает обновление клиента: баннер со своими модами прошлой версии, кнопка
    /// открывает окно с ними. Без своих модов в прошлой версии баннера нет.
    /// </summary>
    [Fact]
    public async Task ModsScreen_ShowsBanner_AndOpensDialog()
    {
        string[] dirs = Setup("alpha", "beta");

        using ServiceProvider provider = App.AppServices.BuildServiceProvider();
        MainViewModel main = provider.GetRequiredService<MainViewModel>();
        main.GameDir = _root;

        ClientMigrationViewModel? shown = null;
        using var mods = new ModsViewModel(main, _library, new FixedLocator(_game), new FixedSettings(dirs[0]), new NoEditor())
        {
            MigrationHandler = dialog => shown = dialog,
            FindTemplateDir = () => null,
        };

        await mods.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => mods.HasPendingMigration);

        Assert.Contains("1.45.0.0 → 1.46.0.0", mods.MigrationBannerText);
        Assert.Contains("Alpha", mods.MigrationBannerText);
        Assert.Equal("Перейти на 1.46…", mods.MigrationButtonText);
        Assert.True(mods.OpenMigrationCommand.CanExecute(null));

        await mods.OpenMigrationCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal(["Alpha", "Beta"], shown!.Rows.Select(r => r.Title));
        Assert.Equal("1.46.0.0", shown.NewVersion);

        // Свои моды уже стоят в новой версии — переносить нечего, баннер гаснет.
        foreach (string f in Directory.GetFiles(_old, "*.mtmod"))
        {
            File.Copy(f, Path.Combine(_new, Path.GetFileName(f)));
        }

        await mods.RefreshCommand.ExecuteAsync(null);
        await WaitUntil(() => !mods.HasPendingMigration);
        Assert.Equal(string.Empty, mods.MigrationBannerText);
    }

    // ================================================================= помощники

    /// <summary>Подмены вокруг модели: что спросили, что переключили, что собрали.</summary>
    private sealed class Fakes(ClientMigrationViewModelTests t)
    {
        public Dictionary<string, bool> Verdicts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> ExtractTargets { get; } = new();
        public List<string> Switched { get; } = new();
        public List<string> Built { get; } = new();
        public List<string> Questions { get; } = new();
        public List<ModOperationResult> Journal { get; } = new();
        public int Extracted => ExtractTargets.Count;
        public string? CheckedNewTree { get; set; }
        public string? CheckedOldTree { get; set; }
        public bool Answer { get; set; }
        public Exception? ExtractError { get; init; }
        public Exception? SwitchError { get; init; }
        public TaskCompletionSource? ExtractStarted { get; init; }
        public Func<string, ModsBuildOutcome>? BuildResult { get; init; }
        public IModLibraryService? Library { get; init; }
        public int NewModules { get; init; } = 3;
        public TaskCompletionSource? BuildStarted { get; init; }
        public HashSet<string> NotInBaseline { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async Task<ClientSourceExtraction> Extract(string target, IProgress<ClientSourceProgress> progress, CancellationToken ct)
        {
            ExtractTargets.Add(target);
            if (ExtractStarted is not null)
            {
                ExtractStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }

            if (ExtractError is not null)
            {
                throw ExtractError;
            }

            progress.Report(new ClientSourceProgress("декомпиляция", 3, 3));
            Directory.CreateDirectory(Path.Combine(target, "client"));
            return new ClientSourceExtraction(target, "1.46.0.0", 3, 3, 0, 0, 0, TimeSpan.FromSeconds(1));
        }

        public Task<MigrationCheck> Check(ClientMigrationState state, string newTree, string? oldTree, IProgress<string> progress, CancellationToken ct)
        {
            CheckedNewTree = newTree;
            CheckedOldTree = oldTree;
            var store = new JsonProjectStore();
            var candidates = state.Pending.Select(m =>
            {
                ModProject project = store.Load(m.Mine!.ProjectDir)!;
                bool ok = !Verdicts.TryGetValue(project.ModId, out bool v) || v;
                var issues = ok
                    ? new List<CompatibilityIssue>()
                    : [new CompatibilityIssue(true, "сигнатура", "у Avatar.PlayerAvatar.showShotResults поменялась сигнатура")];
                if (NotInBaseline.Contains(project.ModId))
                {
                    issues.Add(new CompatibilityIssue(false, ModCompatibilityChecker.UnverifiedKind, "модуля new_only нет в исходниках прошлой версии"));
                }
                var report = new ModCompatibilityReport(project.ProjectDir, project.ModId, project.DisplayName,
                    ok ? CompatibilityVerdict.Compatible : CompatibilityVerdict.Incompatible, issues, ["Avatar"], []);
                return new MigrationCandidate(m, project, report, null);
            }).ToList();
            // Как у настоящего планировщика: доказательство — только сравнение с деревом прошлой версии.
            return Task.FromResult(new MigrationCheck(newTree, oldTree, ClientSourceTrees.VersionOf(oldTree), candidates, TimeSpan.FromSeconds(1))
            {
                BaselineIsPreviousVersion = oldTree is not null && ClientSourceTrees.IsForVersion(oldTree, state.PreviousVersion!),
                NewTreeModules = NewModules,
            });
        }

        public Task UseClientSources(string dir)
        {
            if (SwitchError is not null)
            {
                throw SwitchError;
            }

            Switched.Add(dir);
            return Task.CompletedTask;
        }

        public async Task<ModsBuildOutcome> Build(string dir, CancellationToken ct)
        {
            Built.Add(dir);
            if (BuildStarted is not null)
            {
                // «Долгая сборка»: как build.py под MainViewModel — отмена даёт итог «отменено», а не исключение.
                BuildStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    return new ModsBuildOutcome(
                        new ModProject { ProjectDir = dir, ModId = "x", Version = "1.0.0" },
                        WotModCockpit.Core.Models.BuildResult.Cancelled(string.Empty, TimeSpan.FromSeconds(1)),
                        "отменено");
                }
            }

            return BuildResult?.Invoke(dir) ?? ModsBuildOutcome.NotStarted("сборка не подменена");
        }

        /// <summary>Как настоящий экран «Моды»: та же служба и те же параметры установки.</summary>
        public ModOperationResult Install(GameInstall game, string artifact) =>
            (Library ?? t._library).Install(game, artifact, ModsViewModel.MigrationInstallOptions);

        public bool Confirm(string caption, string body)
        {
            Questions.Add(body);
            return Answer;
        }
    }

    private ClientMigrationViewModel Model(Fakes fx, string? currentTree = null)
    {
        IReadOnlyList<string> projects = Directory.GetDirectories(Path.Combine(_tmp.Path, "repos"));
        ClientMigrationState state = ClientMigrationDetector.Detect(_game, projects);
        Assert.True(state.NeedsMigration);
        return new ClientMigrationViewModel(Context(fx, state, currentTree));
    }

    private ClientMigrationContext Context(Fakes fx, ClientMigrationState state, string? currentTree = null) => new()
    {
        State = state,
        CurrentTree = currentTree ?? _currentTree,
        TemplateDir = null,
        Extract = fx.Extract,
        Check = fx.Check,
        BuildProject = fx.Build,
        Install = fx.Install,
        UseClientSources = fx.UseClientSources,
        Journal = fx.Journal.Add,
        Confirm = fx.Confirm,
        Now = () => Now,
    };

    /// <summary>Свои проекты со свежей сборкой в dist и их моды, включённые в прошлой версии.</summary>
    private string[] Setup(params string[] names)
    {
        var dirs = new List<string>();
        foreach (string name in names)
        {
            string dir = Project(name);
            Mod(_old, $"ru.test.{name}_1.0.0.mtmod", "ru.test." + name);
            dirs.Add(dir);
        }

        return dirs.ToArray();
    }

    private string Project(string name)
    {
        string dir = Path.Combine(_tmp.Path, "repos", "wot-mod-" + name);
        string id = "ru.test." + name;
        string title = char.ToUpperInvariant(name[0]) + name[1..];
        string mods = Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods");
        Directory.CreateDirectory(mods);
        File.WriteAllText(Path.Combine(dir, "build.py"), "# fake\n");
        File.WriteAllText(Path.Combine(dir, "src", "meta.xml"), FakeGameDir.Meta(id, "1.0.0", title, "описание " + title));
        File.WriteAllText(Path.Combine(mods, $"mod_{name}.py"), "def init():\n    pass\n");
        foreach (string f in Directory.GetFiles(Path.Combine(dir, "src"), "*", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(-30));
        }

        string dist = Path.Combine(dir, "dist");
        Directory.CreateDirectory(dist);
        string artifact = Path.Combine(dist, $"{id}_1.0.0.mtmod");
        FakeGameDir.WriteArchive(artifact, FakeGameDir.Meta(id, "1.0.0", title, "описание " + title),
            [FakeGameDir.Entry($"res/scripts/client/gui/mods/mod_{name}.pyc", "bytecode " + name)], compress: false);
        File.SetLastWriteTimeUtc(artifact, DateTime.UtcNow.AddMinutes(-10));
        return dir;
    }

    private static void Mod(string modsDir, string file, string id)
    {
        string title = char.ToUpperInvariant(id["ru.test.".Length]) + id["ru.test.".Length..][1..];
        FakeGameDir.WriteArchive(Path.Combine(modsDir, file), FakeGameDir.Meta(id, "1.0.0", title, "описание " + title),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod.pyc", "old " + id)], compress: false);
    }

    private string Tree(string name)
    {
        string dir = Path.Combine(_trees, name);
        Directory.CreateDirectory(Path.Combine(dir, "client"));
        return dir;
    }

    private static MigrationRowViewModel Row(ClientMigrationViewModel vm, string name) =>
        vm.Rows.Single(r => r.ModId == "ru.test." + name);

    private static ModsBuildOutcome Built(string dir, string artifact) => new(
        new ModProject { ProjectDir = dir, ModId = "x", Version = "1.0.0" },
        new BuildResult(true, 0, artifact, TimeSpan.FromSeconds(1.5)),
        "собрано");

    private static ModsBuildOutcome Failed(string dir) => new(
        new ModProject { ProjectDir = dir, ModId = "x", Version = "1.0.0" },
        new BuildResult(false, 1, string.Empty, TimeSpan.FromSeconds(0.5)),
        "build.py упал: SyntaxError");

    private static string Fingerprint(string dir) =>
        string.Join("\n", Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f => f + "|" + new FileInfo(f).Length + "|" + File.GetLastWriteTimeUtc(f).Ticks));

    private static async Task WaitUntil(Func<bool> condition)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "условие не выполнилось за 10 с");
            await Task.Delay(20);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (T deeper in Descendants<T>(child))
            {
                yield return deeper;
            }
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 8; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
        }
    }

    /// <summary>Протокол привязок WPF в список: любая ошибка привязки окна роняет тест.</summary>
    private sealed class BindingErrors : System.Diagnostics.TraceListener
    {
        private readonly List<string> _sink;
        private readonly System.Diagnostics.SourceLevels _previous;

        private BindingErrors(List<string> sink, System.Diagnostics.SourceLevels previous)
        {
            _sink = sink;
            _previous = previous;
        }

        public static BindingErrors Attach(List<string> sink)
        {
            System.Diagnostics.PresentationTraceSources.Refresh();
            var listener = new BindingErrors(sink, System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
            return listener;
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _sink.Add(message);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
                System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = _previous;
            }

            base.Dispose(disposing);
        }
    }

    private sealed class FixedLocator(GameInstall install) : IGameInstallLocator
    {
        public GameInstall? Detect(string rootPath) => install;
    }

    private sealed class FixedSettings(string projectDir) : ISettingsStore
    {
        public string SettingsPath => Path.Combine(projectDir, "settings.json");

        public CockpitSettings Load() => new() { LastProjectDir = projectDir };

        public void Save(CockpitSettings settings)
        {
        }
    }

    private sealed class NoEditor : IEditorLauncher
    {
        public bool IsAvailable => false;

        public event EventHandler<string>? LaunchFailed
        {
            add { }
            remove { }
        }

        public void OpenAt(string filePath, int line)
        {
        }

        public void OpenFolder(string folderPath)
        {
        }
    }
}
