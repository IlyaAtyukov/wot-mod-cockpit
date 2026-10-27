using System.Runtime.CompilerServices;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Уводит настройки кокпита из <c>%APPDATA%</c> разработчика на всё время прогона тестов.
///
/// <para><b>Зачем.</b> Около трёх десятков тестов поднимают настоящий контейнер
/// (<c>AppServices.BuildServiceProvider()</c>), а в нём — <see cref="JsonSettingsStore"/> с путём по
/// умолчанию. Без этого инициализатора результаты зависели бы от того, что лежит в собственном
/// settings.json разработчика (каталог игры, дерево исходников, pyright), а любой код, который
/// однажды сохранит настройки из такого контейнера, затёр бы их во время <c>dotnet test</c>.</para>
///
/// <para><b>Как.</b> До первого теста переменная окружения <c>WOTCOCKPIT_SETTINGS_DIR</c>
/// указывает на чистый временный каталог этого процесса; хранилище по умолчанию её читает.
/// Значение, заданное до прогона (например, для второй копии кокпита), при этом заменяется:
/// иначе тесты читали бы — а со временем и переписывали — тот settings.json. На настоящей машине
/// автора (<c>WOTCOCKPIT_REAL_MACHINE=1</c>) переменная не трогается — тесты этого режима как раз
/// и проверяют настоящие настройки.</para>
///
/// <para><b>Почему в каталоге сразу лежит <c>{}</c>.</b> Без файла каждый <c>Load()</c> считался
/// бы первым запуском и пробовал машину: искал игру (реестр, типовые папки на всех дисках),
/// соседний шаблон и дерево исходников клиента рядом с ним. «Изолированные» настройки тогда
/// несли бы установку и дерево исходников разработчика. Пустой объект читается как настройки
/// по умолчанию, и досева не происходит.</para>
/// </summary>
internal static class TestSettingsIsolation
{
    /// <summary>
    /// Прогон на настоящей машине — то же правило, что у <see cref="TestEnvironment.IsRealMachine"/>:
    /// одно значение переменной не может одновременно включать тесты игры и оставлять настройки
    /// изолированными.
    /// </summary>
    public static bool RealMachine => TestEnvironment.IsRealMachine;

    /// <summary>Временный каталог настроек этого прогона либо <c>null</c>, если подмены не было.</summary>
    public static string? SettingsDir { get; private set; }

    /// <summary>Срабатывает при загрузке тестовой сборки, раньше любого теста.</summary>
#pragma warning disable CA2255 // Тестовая сборка — не библиотека для чужого кода: инициализатор здесь и нужен.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        if (RealMachine)
        {
            return;
        }

        string dir = Path.Combine(
            Path.GetTempPath(),
            "wotcockpit-test-settings-" + Environment.ProcessId + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, JsonSettingsStore.FileName), "{}");

        SettingsDir = dir;
        Environment.SetEnvironmentVariable(JsonSettingsStore.SettingsDirEnvironmentVariable, dir);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Временный каталог — не повод ронять завершение прогона.
            }
        };
    }
}
