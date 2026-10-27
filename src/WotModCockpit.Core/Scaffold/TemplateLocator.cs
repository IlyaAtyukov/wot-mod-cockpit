using WotModCockpit.Core.Models;

namespace WotModCockpit.Core.Scaffold;

/// <summary>
/// Где лежит шаблон мода (<c>wot-mod-template</c>) — каталог с <c>build.py</c>, <c>tools\</c> и
/// эталонным <c>src\meta.xml</c>, из которого мастер «Новый мод…» создаёт проекты.
///
/// <para><b>Шаблон — отдельный репозиторий</b>, в кокпит он не вшит: сборка (<c>build.py</c>,
/// <c>tools/py27c.py</c>), проверка синтаксиса (<c>tools/check_py2_syntax.py</c>) и извлечение
/// исходников клиента (<c>tools/extract_client_source.py</c>) живут в нём. Поэтому одно
/// определение «где шаблон» на всё приложение, в таком порядке:</para>
/// <list type="number">
///   <item>переменная окружения <see cref="EnvironmentVariable"/> (<c>WOTCOCKPIT_TEMPLATE_DIR</c>) —
///         для CI, тестов и нестандартных раскладок;</item>
///   <item>настройка <see cref="CockpitSettings.TemplateDir"/> (<c>templateDir</c> в settings.json) —
///         туда же запоминается выбор в мастере;</item>
///   <item>соседний с кокпитом каталог <c>wot-mod-template</c> (а также <c>wot-mod-template-main</c>
///         и любое другое имя на <c>wot-mod-template*</c>, как его распаковывает GitHub ZIP):
///         проба поднимается от каталога сборки вверх и на каждом уровне смотрит соседей.</item>
/// </list>
/// <para>Кандидат принимается, только если в нём есть и <c>build.py</c>, и <c>src\meta.xml</c>:
/// одноимённая пустая папка шаблоном не считается.</para>
/// </summary>
public static class TemplateLocator
{
    /// <summary>Переменная окружения с каталогом шаблона. Главнее настройки и пробы.</summary>
    public const string EnvironmentVariable = "WOTCOCKPIT_TEMPLATE_DIR";

    /// <summary>Каноническое имя каталога шаблона рядом с кокпитом.</summary>
    public const string FolderName = "wot-mod-template";

    /// <summary>
    /// Подсказка «где взять шаблон» для сообщений об ошибке. Одно место на все сообщения,
    /// включая постоянный адрес шаблона.
    /// </summary>
    public const string WhereToGetHint =
        "шаблон мода — отдельный репозиторий wot-mod-template (https://github.com/IlyaAtyukov/wot-mod-template); " +
        "положите его рядом с папкой кокпита или укажите путь к нему";

    /// <summary>Сколько уровней вверх от каталога сборки смотреть соседей.</summary>
    private const int MaxDepth = 12;

    /// <summary>
    /// Находит шаблон по полному порядку: переменная окружения, настройки, соседние каталоги.
    /// </summary>
    /// <param name="settings">Настройки кокпита; <c>null</c> — без настройки.</param>
    /// <returns>Полный путь к шаблону либо пустая строка.</returns>
    public static string Resolve(CockpitSettings? settings) => Resolve(settings?.TemplateDir);

    /// <summary>
    /// Находит шаблон по полному порядку: переменная окружения, явно заданный каталог, соседние каталоги.
    /// </summary>
    /// <param name="configured">Каталог из настроек; пусто — не задан.</param>
    /// <returns>Полный путь к шаблону либо пустая строка.</returns>
    public static string Resolve(string? configured)
    {
        string fromEnvironment = FromEnvironment();
        if (fromEnvironment.Length > 0) return fromEnvironment;

        if (LooksLikeTemplate(configured)) return Full(configured!);

        return ProbeSiblings();
    }

    /// <summary>Шаблон из переменной окружения <see cref="EnvironmentVariable"/>, если она указывает на шаблон.</summary>
    /// <returns>Полный путь либо пустая строка.</returns>
    public static string FromEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return LooksLikeTemplate(value) ? Full(value!) : string.Empty;
    }

    /// <summary>
    /// Ищет шаблон рядом с кокпитом: поднимается от <paramref name="startDirectory"/> (по умолчанию —
    /// каталог сборки) вверх и на каждом уровне проверяет вложенные каталоги <c>wot-mod-template*</c>.
    /// Каноническое имя на одном уровне выигрывает у вариантов вроде <c>wot-mod-template-main</c>.
    /// </summary>
    /// <param name="startDirectory">С какого каталога начинать; <c>null</c> — <see cref="AppContext.BaseDirectory"/>.</param>
    /// <returns>Полный путь к шаблону либо пустая строка.</returns>
    public static string ProbeSiblings(string? startDirectory = null)
    {
        try
        {
            var dir = new DirectoryInfo(string.IsNullOrWhiteSpace(startDirectory) ? AppContext.BaseDirectory : startDirectory);

            for (int depth = 0; depth < MaxDepth && dir is not null; depth++, dir = dir.Parent)
            {
                string exact = Path.Combine(dir.FullName, FolderName);
                if (LooksLikeTemplate(exact)) return Full(exact);

                foreach (string candidate in Variants(dir))
                {
                    if (LooksLikeTemplate(candidate)) return Full(candidate);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            // Проба диска — удобство, а не обязанность.
        }

        return string.Empty;
    }

    /// <summary>Каталог похож на шаблон мода: в нём есть <c>build.py</c> и <c>src\meta.xml</c>.</summary>
    /// <param name="dir">Путь-кандидат.</param>
    /// <returns><c>true</c>, если похож.</returns>
    public static bool LooksLikeTemplate(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;

        try
        {
            string trimmed = dir.Trim().Trim('"');
            return File.Exists(Path.Combine(trimmed, "build.py"))
                   && File.Exists(Path.Combine(trimmed, "src", "meta.xml"));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException
                                       or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Текст отказа «шаблон не найден» с перечнем всех способов его указать. Им пользуются
    /// и мастер, и окно перехода на новую версию клиента — чтобы пользователь везде видел
    /// одни и те же, реально существующие настройки.
    /// </summary>
    /// <param name="tried">Каталог, который пробовали; пусто — не задан вовсе.</param>
    /// <returns>Готовый текст.</returns>
    public static string NotFoundMessage(string? tried)
    {
        string what = string.IsNullOrWhiteSpace(tried)
            ? "каталог шаблона мода не найден"
            : $"каталог шаблона мода не найден: '{tried.Trim()}'";

        return what + ". Укажите его в мастере (поле «Шаблон» — выбор запомнится), в settings.json кокпита " +
               $"(поле templateDir) или в переменной окружения {EnvironmentVariable}; " + WhereToGetHint + ".";
    }

    /// <summary>Вложенные каталоги <c>wot-mod-template*</c>, кроме канонического, в устойчивом порядке.</summary>
    private static IEnumerable<string> Variants(DirectoryInfo parent)
    {
        string[] found;
        try
        {
            found = Directory.GetDirectories(parent.FullName, FolderName + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or PathTooLongException
                                       or System.Security.SecurityException)
        {
            return [];
        }

        return found
            .Where(d => !string.Equals(Path.GetFileName(d), FolderName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
    }

    private static string Full(string dir) => Path.GetFullPath(dir.Trim().Trim('"'));
}
