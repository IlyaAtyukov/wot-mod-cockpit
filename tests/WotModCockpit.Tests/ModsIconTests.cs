using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="ModIcons"/>: достать PNG из <c>.mtmod</c> точечно, не распаковывая архив,
/// и посчитать стабильную заглушку для модов, у которых иконки нет вовсе.
/// <para>
/// Формат проверяется тот самый, что у всех реальных иконок игрового списка: PNG 50×50.
/// </para>
/// </summary>
public class ModsIconTests
{
    /// <summary>Иконка по «правильному» пути своих модов находится и читается вместе с размером.</summary>
    [Fact]
    public void Read_FindsIconByModIdPath()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "d"),
            [FakeGameDir.IconEntry("res/gui/maps/icons/mods/ru.example.alpha/modsListApi.png")]);

        ModIcon icon = ModIcons.Read(path, "ru.example.alpha");

        Assert.Equal(ModIconSource.Archive, icon.Source);
        Assert.True(icon.HasBytes);
        Assert.Equal("res/gui/maps/icons/mods/ru.example.alpha/modsListApi.png", icon.EntryPath);
        Assert.Equal(50, icon.Width);
        Assert.Equal(50, icon.Height);
        Assert.True(icon.IsGameSize);
    }

    /// <summary>
    /// Чужие моды кладут иконку по своим путям (<c>gui/maps/battlehits/modsListApi.png</c> и т.п.) —
    /// их тоже надо находить, иначе список будет из одних заглушек.
    /// </summary>
    [Fact]
    public void Read_FindsForeignIconByFileName()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "me.poliroid.battlehits_2.2.5.mtmod",
            FakeGameDir.Meta("battlehits", "2.2.5", "Battle Hits", "d"),
            [
                FakeGameDir.Entry("res/scripts/client/gui/mods/mod_battlehits.pyc", "bytecode"),
                FakeGameDir.IconEntry("res/gui/maps/battlehits/modsListApi.png")
            ]);

        ModIcon icon = ModIcons.Read(path, "battlehits");

        Assert.Equal(ModIconSource.Archive, icon.Source);
        Assert.Equal("res/gui/maps/battlehits/modsListApi.png", icon.EntryPath);
        Assert.True(icon.IsGameSize);
    }

    /// <summary>Когда путь по id есть, он побеждает любую другую иконку в архиве.</summary>
    [Fact]
    public void Read_PrefersIdSpecificPath()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "ru.example.delta_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.delta", "1.0.0", "Delta Path", "d"),
            [
                FakeGameDir.IconEntry("res/gui/maps/other/modsListApi.png", 32),
                FakeGameDir.IconEntry("res/gui/maps/icons/mods/ru.example.delta/modsListApi.png")
            ]);

        ModIcon icon = ModIcons.Read(path, "ru.example.delta");

        Assert.Equal("res/gui/maps/icons/mods/ru.example.delta/modsListApi.png", icon.EntryPath);
        Assert.Equal(50, icon.Width);
    }

    /// <summary>Иконки нет — это норма (так у большинства модов), а не ошибка.</summary>
    [Fact]
    public void Read_NoIcon_ReturnsNone()
    {
        using FakeGameDir game = new();
        string path = game.AddMod("audioww/voice_1.0.mtmod", null,
            [FakeGameDir.Entry("res/audioww/voice.bnk", "звук")]);

        ModIcon icon = ModIcons.Read(path);

        Assert.Equal(ModIconSource.None, icon.Source);
        Assert.False(icon.HasBytes);
    }

    /// <summary>Битый архив не бросает исключение — строка списка всё равно должна нарисоваться.</summary>
    [Fact]
    public void Read_BrokenArchive_ReturnsFailed()
    {
        using FakeGameDir game = new();
        string path = game.AddBrokenMod();

        ModIcon icon = ModIcons.Read(path);

        Assert.Equal(ModIconSource.Unreadable, icon.Source);
        Assert.NotNull(icon.Error);
    }

    /// <summary>Несуществующий файл тоже не роняет вызов.</summary>
    [Fact]
    public void Read_MissingFile_ReturnsFailed()
    {
        ModIcon icon = ModIcons.Read(@"C:\nowhere\ghost.mtmod");

        Assert.Equal(ModIconSource.Unreadable, icon.Source);
    }

    /// <summary>Выключенный мод (<c>.mtmod.off</c>) — обычный ZIP: иконка обязана читаться и у него.</summary>
    [Fact]
    public void Read_WorksForDisabledMod()
    {
        using FakeGameDir game = new();
        string path = game.AddMod(
            "x_1.0.mtmod.off",
            FakeGameDir.Meta("a.x", "1.0", "X", "d"),
            [FakeGameDir.IconEntry("res/gui/maps/x/modsListApi.png")]);

        Assert.True(ModIcons.Read(path, "a.x").HasBytes);
    }

    /// <summary>Сканер сам находит иконку и кладёт её путь в строку — UI не должен искать её заново.</summary>
    [Fact]
    public void Scanner_FillsIconEntryPath()
    {
        using FakeGameDir game = new();
        game.AddMod(
            "ru.example.alpha_1.0.0.mtmod",
            FakeGameDir.Meta("ru.example.alpha", "1.0.0", "Alpha Path", "d"),
            [FakeGameDir.IconEntry("res/gui/maps/icons/mods/ru.example.alpha/modsListApi.png")]);
        game.AddMod("noicon_1.0.mtmod");

        ModLibraryScan scan = new ModScanner().Scan(game.ModsDir);

        InstalledMod withIcon = scan.Mods.Single(m => m.FileName.StartsWith("ru.example", StringComparison.Ordinal));
        InstalledMod without = scan.Mods.Single(m => m.FileName.StartsWith("noicon", StringComparison.Ordinal));

        Assert.Equal("res/gui/maps/icons/mods/ru.example.alpha/modsListApi.png", withIcon.IconEntryPath);
        Assert.Null(without.IconEntryPath);
    }

    // ------------------------------------------------------------------ пути для игры

    /// <summary>
    /// Путь, который мод передаёт в <c>addModification(icon=...)</c>, идёт ОТНОСИТЕЛЬНО <c>res/</c>:
    /// без <c>res/</c> в начале и без <c>../../</c> — префикс ModsListAPI дописывает сам
    /// (<c>__fixModIcon</c> возвращает <c>'../../%s'</c>). Ошибка здесь не диагностируется никак:
    /// мод молча получит чужую серую иконку.
    /// </summary>
    [Fact]
    public void ApiIconPath_IsRelativeToRes_WithoutPrefixes()
    {
        string api = ModIcons.ApiIconPathFor("ru.example.alpha");

        Assert.Equal("gui/maps/icons/mods/ru.example.alpha/modsListApi.png", api);
        Assert.DoesNotContain("../", api, StringComparison.Ordinal);
        Assert.False(api.StartsWith("res/", StringComparison.Ordinal));
        Assert.False(api.StartsWith('/'));
    }

    /// <summary>А путь внутри архива — тот же самый, но с <c>res/</c>: это уже запись ZIP.</summary>
    [Fact]
    public void ArchiveEntryPath_IsApiPathUnderRes()
    {
        string entry = ModIcons.ArchiveEntryPathFor("ru.example.amber");

        Assert.Equal("res/gui/maps/icons/mods/ru.example.amber/modsListApi.png", entry);
        Assert.Equal("res/" + ModIcons.ApiIconPathFor("ru.example.amber"), entry);
    }

    // ------------------------------------------------------------------ заглушка

    /// <summary>
    /// Заглушка обязана быть устойчивой: один и тот же мод всегда одного цвета и с одной буквой,
    /// иначе список будет «мигать» между обновлениями.
    /// </summary>
    [Fact]
    public void Placeholder_IsStable()
    {
        ModIconPlaceholder a = ModIcons.Placeholder("tv.example.spotted", "Example: Spotted");
        ModIconPlaceholder b = ModIcons.Placeholder("tv.example.spotted", "Example: Spotted");

        Assert.Equal(a, b);
        Assert.Single(a.Letter);
        Assert.StartsWith("#", a.ColorHex, StringComparison.Ordinal);
        Assert.Equal(7, a.ColorHex.Length);
    }

    /// <summary>
    /// Буква берётся из последнего сегмента точечного имени, иначе половина списка была бы
    /// из «M» (me.poliroid.*) и «T» (tv.example.*).
    /// </summary>
    /// <param name="key">Ключ мода.</param>
    /// <param name="name">Имя мода.</param>
    /// <param name="expected">Ожидаемая буква.</param>
    [Theory]
    [InlineData("me.poliroid.battlehits", null, "B")]
    [InlineData("tv.example.sessionlog", null, "S")]
    [InlineData("ru.example.alpha", "Alpha Path", "P")]
    [InlineData("", "", "?")]
    public void Placeholder_PicksMeaningfulLetter(string key, string? name, string expected) =>
        Assert.Equal(expected, ModIcons.Placeholder(key, name).Letter);

    /// <summary>Разные моды должны различаться цветом — иначе заглушка бесполезна.</summary>
    [Fact]
    public void Placeholder_DifferentModsGetDifferentColors()
    {
        string[] ids =
        [
            "me.poliroid.battlehits", "tv.example.spotted", "com.modxvm.xvm", "foreign_pack",
            "net.openwg.common", "izeberg.modssettingsapi", "lesta_replays_upload", "crosshairs_api"
        ];

        int distinct = ids.Select(id => ModIcons.Placeholder(id, null).ColorHex).Distinct().Count();

        Assert.True(distinct >= 4, $"палитра вырождается: всего {distinct} цветов на {ids.Length} модов");
    }

    /// <summary>
    /// В палитре заглушек нет ни зелёного, ни голубого: в кокпите эти два цвета означают
    /// «мой мод» и «моя машина», и чужой мод не должен ими притворяться.
    /// </summary>
    [Fact]
    public void Placeholder_PaletteAvoidsMineColors()
    {
        string[] forbidden = ["#7BEC37", "#4FC3F7"];

        for (int i = 0; i < 64; i++)
        {
            string color = ModIcons.Placeholder("mod-" + i, null).ColorHex;
            Assert.DoesNotContain(color, forbidden);
        }
    }

    // ------------------------------------------------------------------ PNG

    /// <summary>Размер читается из заголовка PNG, ничего не декодируя.</summary>
    [Fact]
    public void ReadPngSize_ParsesIhdr()
    {
        Assert.Equal((50, 50), ModIcons.ReadPngSize(PngFactory.Square(50))!.Value);
        Assert.Equal((24, 24), ModIcons.ReadPngSize(PngFactory.Square(24))!.Value);
        Assert.Null(ModIcons.ReadPngSize("это не png, а текст подлиннее двадцати четырёх байт"u8));
        Assert.Null(ModIcons.ReadPngSize([1, 2, 3]));
    }
}
