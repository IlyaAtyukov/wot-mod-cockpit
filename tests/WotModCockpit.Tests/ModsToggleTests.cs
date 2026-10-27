using System.Security.Cryptography;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="ModToggle"/> — единственного места кокпита, которое вообще пишет в каталог игры.
/// Всё происходит на ФЕЙКОВОМ каталоге во временной папке; настоящая папка игры здесь
/// не участвует никак.
/// <para>
/// Проверяется не столько «умеет переименовывать», сколько «отказывается, когда должен»:
/// вне каталога модов, при запущенной игре, при занятой цели, при изменившемся файле.
/// </para>
/// </summary>
public class ModsToggleTests
{
    /// <summary>Переключатель с «игра не запущена».</summary>
    private static ModToggle Toggle() => new(FakeGameProbes.Closed);

    // ------------------------------------------------------------------ счастливый путь

    /// <summary>
    /// Выключение и включение обратно дают ПОБАЙТОВО тот же файл под тем же именем.
    /// Это и есть обещание «обратимо, ничего не теряется».
    /// </summary>
    [Fact]
    public void DisableThenEnable_RestoresExactSameFile()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha", "d"),
            [FakeGameDir.Entry("res/scripts/client/gui/mods/mod_alpha.pyc", "bytecode")]);

        byte[] before = Sha256(path);
        long sizeBefore = new FileInfo(path).Length;

        ModOperationResult off = Toggle().Disable(game.ModsDir, path);

        Assert.True(off.Success, off.Message);
        Assert.Equal(path + ".off", off.TargetPath);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".off"));

        ModOperationResult on = Toggle().Enable(game.ModsDir, path + ".off");

        Assert.True(on.Success, on.Message);
        Assert.Equal(path, on.TargetPath);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".off"));
        Assert.Equal(before, Sha256(path));
        Assert.Equal(sizeBefore, new FileInfo(path).Length);
    }

    /// <summary>
    /// Выключенный файл перестаёт попадать под маску <c>*.mtmod</c> — ровно так его перестаёт
    /// видеть движок (маска в paths.xml, перечисление то же самое Win32).
    /// </summary>
    [Fact]
    public void DisabledFile_DisappearsFromMtmodMask()
    {
        using FakeGameDir game = new();
        string a = game.AddMod("a_1.0.mtmod");
        game.AddMod("b_1.0.mtmod");

        Assert.Equal(2, Directory.GetFiles(game.ModsDir, "*.mtmod", SearchOption.AllDirectories).Length);

        Assert.True(Toggle().Disable(game.ModsDir, a).Success);

        string[] visible = Directory.GetFiles(game.ModsDir, "*.mtmod", SearchOption.AllDirectories);
        Assert.Single(visible);
        Assert.EndsWith("b_1.0.mtmod", visible[0]);
        Assert.True(File.Exists(a + ".off"), "файл обязан остаться на диске, просто под другим именем");
    }

    /// <summary>Мод в подпапке выключается там же, где лежал: группировка модпака не ломается.</summary>
    [Fact]
    public void Disable_KeepsFileInItsSubfolder()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("audioww/voice_2.0.mtmod");

        ModOperationResult result = Toggle().Disable(game.ModsDir, path);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(result.TargetPath));
        Assert.True(File.Exists(Path.Combine(game.ModsDir, "audioww", "voice_2.0.mtmod.off")));
    }

    /// <summary>Операция трогает ровно один файл и ничего больше — контрольный замер каталога.</summary>
    [Fact]
    public void Disable_TouchesNothingElse()
    {
        using FakeGameDir game = new();
        string target = game.AddMod("target_1.0.mtmod");
        game.AddMod("audioww/a_1.0.mtmod");
        game.AddMod("foreign_pack/b_1.0.mtmod");
        game.AddRawFile("readme.txt");
        game.AddBrokenMod();

        string[] before = game.Snapshot();
        Assert.True(Toggle().Disable(game.ModsDir, target).Success);
        string[] after = game.Snapshot();

        Assert.Equal(before.Length, after.Length);
        IEnumerable<string> changed = before.Except(after, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(target, Assert.Single(changed));
    }

    // ------------------------------------------------------------------ отказы: границы

    /// <summary>
    /// Файл вне каталога модов не трогается никогда — даже если он называется как мод.
    /// Это главный запрет всего экрана.
    /// </summary>
    [Fact]
    public void Disable_OutsideModsDir_IsRefused()
    {
        using FakeGameDir game = new();
        string outside = Path.Combine(game.Root, "res", "packages", "evil_1.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "важный файл игры");

        ModOperationResult result = Toggle().Disable(game.ModsDir, outside);

        Assert.False(result.Success);
        Assert.Equal(ModOperationStatus.RefusedOutsideModsDir, result.Status);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(outside + ".off"));
    }

    /// <summary>Выход за каталог модов через <c>..</c> тоже не проходит: пути нормализуются.</summary>
    [Fact]
    public void Disable_PathTraversal_IsRefused()
    {
        using FakeGameDir game = new();
        string outside = Path.Combine(game.Root, "res_mods", "sneaky_1.0.mtmod");
        Directory.CreateDirectory(Path.GetDirectoryName(outside)!);
        File.WriteAllText(outside, "россыпь");

        string traversal = Path.Combine(game.ModsDir, "..", "..", "res_mods", "sneaky_1.0.mtmod");

        ModOperationResult result = Toggle().Disable(game.ModsDir, traversal);

        Assert.Equal(ModOperationStatus.RefusedOutsideModsDir, result.Status);
        Assert.True(File.Exists(outside));
    }

    /// <summary>
    /// Соседний каталог с тем же префиксом — не «внутри»: <c>mods\1.44.0.0-backup</c>
    /// не должен сойти за <c>mods\1.44.0.0</c>.
    /// </summary>
    [Fact]
    public void IsInside_SiblingWithSamePrefix_IsOutside()
    {
        Assert.False(ModToggle.IsInside(@"C:\Games\Tanki\mods\1.44.0.0", @"C:\Games\Tanki\mods\1.44.0.0-backup\x.mtmod"));
        Assert.True(ModToggle.IsInside(@"C:\Games\Tanki\mods\1.44.0.0", @"C:\Games\Tanki\mods\1.44.0.0\x.mtmod"));
        Assert.True(ModToggle.IsInside(@"C:\Games\Tanki\mods\1.44.0.0\", @"C:\Games\Tanki\mods\1.44.0.0\audioww\x.mtmod"));
        Assert.False(ModToggle.IsInside(@"C:\Games\Tanki\mods\1.44.0.0", @"C:\Games\Tanki\mods\1.44.0.0"));
        Assert.False(ModToggle.IsInside(string.Empty, @"C:\x.mtmod"));
    }

    /// <summary>Не файл мода — отказ, даже внутри каталога модов.</summary>
    [Fact]
    public void Disable_NotAModFile_IsRefused()
    {
        using FakeGameDir game = new();
        string txt = game.AddRawFile("configs/notes.txt", "заметки");

        ModOperationResult result = Toggle().Disable(game.ModsDir, txt);

        Assert.Equal(ModOperationStatus.RefusedNotModFile, result.Status);
        Assert.True(File.Exists(txt));
    }

    // ------------------------------------------------------------------ отказы: игра

    /// <summary>Игра запущена — писать нельзя: моды читаются один раз при старте клиента.</summary>
    [Fact]
    public void Disable_GameRunning_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        ModToggle toggle = new(FakeGameProbes.Running);
        ModOperationResult result = toggle.Disable(game.ModsDir, path);

        Assert.Equal(ModOperationStatus.RefusedGameRunning, result.Status);
        Assert.Contains("ЗАПУЩЕНА", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".off"));
    }

    /// <summary>
    /// Не удалось выяснить, запущена ли игра — тоже запрет. Неизвестность трактуется
    /// в пользу отказа, потому что цена ошибки несимметрична.
    /// </summary>
    [Fact]
    public void Disable_GameStateUnknown_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        ModOperationResult result = new ModToggle(FakeGameProbes.Unknown).Disable(game.ModsDir, path);

        Assert.Equal(ModOperationStatus.RefusedGameRunning, result.Status);
        Assert.True(File.Exists(path));
    }

    /// <summary>Включение при запущенной игре запрещено ровно так же, как выключение.</summary>
    [Fact]
    public void Enable_GameRunning_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod.off");

        ModOperationResult result = new ModToggle(FakeGameProbes.Running).Enable(game.ModsDir, path);

        Assert.Equal(ModOperationStatus.RefusedGameRunning, result.Status);
        Assert.True(File.Exists(path));
    }

    // ------------------------------------------------------------------ отказы: состояние диска

    /// <summary>
    /// Целевое имя занято — отказ, и ни одного байта не тронуто. Перезапись здесь запрещена
    /// намеренно: <c>File.Move(overwrite: true)</c> уничтожил бы существующий файл молча.
    /// </summary>
    [Fact]
    public void Disable_TargetAlreadyExists_IsRefused_AndNothingIsOverwritten()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");
        string occupied = game.AddRawFile("x_1.0.mtmod.off", "ЧУЖОЕ СОДЕРЖИМОЕ, терять нельзя");

        ModOperationResult result = Toggle().Disable(game.ModsDir, path);

        Assert.Equal(ModOperationStatus.RefusedTargetExists, result.Status);
        Assert.True(File.Exists(path));
        Assert.Equal("ЧУЖОЕ СОДЕРЖИМОЕ, терять нельзя", File.ReadAllText(occupied));
    }

    /// <summary>Файл уже в нужном состоянии — операция не нужна, и об этом надо сказать честно.</summary>
    [Fact]
    public void Disable_AlreadyDisabled_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod.off");

        ModOperationResult result = Toggle().Disable(game.ModsDir, path);

        Assert.Equal(ModOperationStatus.RefusedAlreadyInState, result.Status);
        Assert.True(File.Exists(path));
    }

    /// <summary>Файла нет — отказ, а не исключение (список мог устареть).</summary>
    [Fact]
    public void Disable_MissingSource_IsRefused()
    {
        using FakeGameDir game = new();

        ModOperationResult result = Toggle().Disable(game.ModsDir, Path.Combine(game.ModsDir, "ghost_1.0.mtmod"));

        Assert.Equal(ModOperationStatus.RefusedSourceMissing, result.Status);
    }

    /// <summary>
    /// Файл изменился с момента показа (модпак обновился, пока был открыт диалог) — операция
    /// отменяется, потому что пользователь подтверждал не это.
    /// </summary>
    [Fact]
    public void Disable_FileChangedSinceSnapshot_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod", FakeGameDir.Meta("a.x", "1.0", "X", "d"));

        ModWriteExpectation stale = new(SizeBytes: 42, LastWriteTimeUtc: DateTimeOffset.UtcNow.AddDays(-3));

        ModOperationResult result = Toggle().Disable(game.ModsDir, path, stale);

        Assert.Equal(ModOperationStatus.RefusedChanged, result.Status);
        Assert.True(File.Exists(path));
    }

    /// <summary>Актуальный снимок операции не мешает.</summary>
    [Fact]
    public void Disable_WithFreshSnapshot_Succeeds()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod", FakeGameDir.Meta("a.x", "1.0", "X", "d"));

        InstalledMod row = new ModScanner().ScanOne(game.ModsDir, path)!;

        ModOperationResult result = Toggle().Disable(game.ModsDir, path, ModWriteExpectation.From(row));

        Assert.True(result.Success, result.Message);
    }

    /// <summary>
    /// Файл удерживается другим процессом — операция не начинается вовсе. Это вторая,
    /// независимая от проверки процессов защита: на случай, если клиент держит файл открытым.
    /// </summary>
    [Fact]
    public void Disable_LockedFile_IsRefused()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            ModOperationResult result = Toggle().Disable(game.ModsDir, path);

            Assert.Equal(ModOperationStatus.RefusedFileLocked, result.Status);
            Assert.True(File.Exists(path));
            Assert.False(File.Exists(path + ".off"));
        }
    }

    /// <summary>Каталога модов нет — отказ с внятной причиной.</summary>
    [Fact]
    public void Disable_ModsDirMissing_IsRefused()
    {
        ModOperationResult result = Toggle().Disable(@"C:\nowhere\mods\1.44.0.0", @"C:\nowhere\mods\1.44.0.0\x.mtmod");

        Assert.Equal(ModOperationStatus.RefusedModsDirMissing, result.Status);
    }

    // ------------------------------------------------------------------ проверка без операции

    /// <summary>
    /// <see cref="ModToggle.Validate"/> — та же проверка, но без записи: ею UI гасит кнопки.
    /// <c>null</c> означает «можно».
    /// </summary>
    [Fact]
    public void Validate_ReturnsNullWhenAllowed_AndRefusalOtherwise()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        Assert.Null(Toggle().Validate(ModOperation.Disable, game.ModsDir, path));
        Assert.NotNull(new ModToggle(FakeGameProbes.Running).Validate(ModOperation.Disable, game.ModsDir, path));
        Assert.True(File.Exists(path), "проверка обязана быть чисто читающей");
    }

    /// <summary>Строка журнала содержит и путь, и результат — по ней можно восстановить, что произошло.</summary>
    [Fact]
    public void ToLogLine_ContainsPathsAndOutcome()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("x_1.0.mtmod");

        string line = Toggle().Disable(game.ModsDir, path).ToLogLine();

        Assert.Contains("DISABLE", line, StringComparison.Ordinal);
        Assert.Contains(path, line, StringComparison.Ordinal);
        Assert.Contains(".mtmod.off", line, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', line);
    }

    // ------------------------------------------------------------------ помощники

    /// <summary>SHA-256 файла — им проверяется «байт в байт».</summary>
    /// <param name="path">Путь.</param>
    /// <returns>Хеш.</returns>
    private static byte[] Sha256(string path)
    {
        using FileStream fs = File.OpenRead(path);
        return SHA256.HashData(fs);
    }
}
