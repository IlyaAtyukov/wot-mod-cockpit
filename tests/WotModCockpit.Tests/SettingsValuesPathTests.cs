using Microsoft.Extensions.DependencyInjection;
using WotModCockpit.App.Stage4;
using WotModCockpit.App.ViewModels;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Settings;

namespace WotModCockpit.Tests;

/// <summary>
/// КУДА МОДУЛЬ НАСТРОЕК ПИШЕТ ЗНАЧЕНИЯ ИГРОКА.
///
/// <para>Сгенерированный <c>settings.py</c> в <c>_save()</c> делает
/// <c>json.dumps(_values)</c> — то есть переписывает свой файл ЦЕЛИКОМ при каждом
/// «Применить». Указать ему <c>config.json</c> рецепта 09 означает стереть у игрока конфиг
/// мода вместе со всеми ключами, которые туда положил разработчик. Core на такое совпадение
/// отказывается генерировать (<see cref="SettingsDesignerService.Apply"/>), но экран зовёт
/// генератор через шов и мимо той проверки проходил — поэтому проверка нужна и здесь,
/// на настоящем пути «экран → шов → код».</para>
/// </summary>
public class SettingsValuesPathTests : IDisposable
{
    private readonly string _temp;

    /// <summary>Временное дерево проекта.</summary>
    public SettingsValuesPathTests()
    {
        _temp = Path.Combine(Path.GetTempPath(), "cockpit-valuespath-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_temp);
    }

    /// <summary>Убирает временное дерево.</summary>
    public void Dispose()
    {
        try { if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// У проекта прописан конфиг мода из рецепта 09 — настройки обязаны уехать в СОСЕДНИЙ
    /// файл того же каталога, а не в сам конфиг.
    /// </summary>
    [Fact]
    public void Generated_SettingsFile_NeverEqualsRecipe09Config()
    {
        string settingsPy = WpfHost.Invoke(() => UserModule(
            @"mods\configs\hello_tanker\config.json"));

        Assert.Contains("'settings.json'", settingsPy, StringComparison.Ordinal);
        Assert.DoesNotContain("'config.json'", settingsPy, StringComparison.Ordinal);
        // Каталог остаётся тот же: так удобнее и разработчику, и игроку.
        Assert.Contains("'mods', 'configs', 'hello_tanker', 'settings.json'", settingsPy, StringComparison.Ordinal);
    }

    /// <summary>Конфиг мода не задан — путь выводится из имени пакета, как и раньше.</summary>
    [Fact]
    public void Generated_SettingsFile_FallsBackToPackage()
    {
        string settingsPy = WpfHost.Invoke(() => UserModule(string.Empty));

        Assert.Contains("'mods', 'configs', 'hello_tanker', 'settings.json'", settingsPy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Патологический случай: конфиг мода САМ назван <c>settings.json</c>. Общий файл на двоих
    /// — это те же потерянные ключи, поэтому имя обязано разойтись.
    /// </summary>
    [Fact]
    public void Generated_SettingsFile_StepsAsideWhenConfigIsAlsoSettingsJson()
    {
        string settingsPy = WpfHost.Invoke(() => UserModule("mods/configs/hello_tanker/settings.json"));

        Assert.Contains("'settings.values.json'", settingsPy, StringComparison.Ordinal);
    }

    /// <summary>
    /// Собирает форму в настоящем экране дизайнера и возвращает текст сгенерированного
    /// <c>settings.py</c>.
    /// </summary>
    /// <param name="gameConfigRelative">Значение <see cref="ModProject.GameConfigRelative"/>.</param>
    /// <returns>Текст пользовательского модуля.</returns>
    private string UserModule(string gameConfigRelative)
    {
        using ServiceProvider provider = WotModCockpit.App.AppServices.BuildServiceProvider();
        var vm = provider.GetRequiredService<SettingsDesignerViewModel>();

        vm.SetProject(new ModProject
        {
            ProjectDir = _temp,
            SrcDirRelative = "src",
            PackageName = "hello_tanker",
            ModId = "ru.local.hello_tanker",
            LogTag = "[HelloTanker",
            GameConfigRelative = gameConfigRelative
        });

        vm.ModDisplayName = "Hello Tanker";
        vm.Linkage = "ru.local.hello_tanker";

        SettingControlViewModel control = vm.InsertNew(SettingsControlType.CheckBox, 1, -1);
        control.Text = "Показывать приветствие";
        control.VarName = "showGreeting";

        Assert.True(vm.CanGenerate, "форма не готова к генерации: " + vm.StatusText);
        vm.GenerateCodeCommand.Execute(null);

        GeneratedArtifactViewModel? artifact = vm.Artifacts
            .FirstOrDefault(a => a.FullPath.EndsWith("settings.py", StringComparison.Ordinal));

        Assert.True(artifact is not null, "settings.py не сгенерирован: " + vm.StatusText);
        return artifact!.Content;
    }
}
