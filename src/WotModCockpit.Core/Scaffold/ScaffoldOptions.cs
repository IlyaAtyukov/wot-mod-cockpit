using System.Text;
using System.Text.RegularExpressions;

namespace WotModCockpit.Core.Scaffold;

/// <summary>
/// Правила именования мода. Вынесены отдельно, потому что одно и то же имя пакета
/// разъезжается по трём независимым написаниям (<c>battle_timer</c>, <c>BattleTimer</c>,
/// <c>Battle Timer</c>) в девяти местах шаблона, и любое расхождение делает мод молча мёртвым.
/// </summary>
public static partial class NameConventions
{
    /// <summary>Максимальная длина имени пакета: дальше начинаются пути длиннее MAX_PATH внутри VFS.</summary>
    public const int MaxPackageLength = 48;

    /// <summary>
    /// Модули клиента 1.44 верхнего уровня, чьи имена нельзя брать под имя пакета мода:
    /// пакет мода лежит в <c>gui.mods.&lt;пакет&gt;</c>, но <c>_PATCH_MARKER</c>, каталог
    /// конфигов и имя логгера всё равно станут неотличимы от клиентских, а главное —
    /// такой пакет невозможно обсуждать в логе.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gui", "mods", "main", "constants", "helpers", "messenger", "account", "avatar",
        "vehicle", "arena", "game", "items", "shared", "common", "client", "scripts",
        "test", "tests", "res", "build", "dist", "tools", "src", "logging", "json", "os", "sys"
    };

    /// <summary>
    /// Имена устройств DOS, которые Windows резервирует в КАЖДОМ каталоге. Все они подходят
    /// под <c>^[a-z][a-z0-9_]*$</c> и ни одно не занято модулем клиента, поэтому раньше
    /// проходили проверку — а мастер потом падал сырым системным текстом
    /// «Could not find a part of the path '\\.\con'», потому что каталог с таким именем
    /// создать нельзя в принципе (проверено запуском на con, prn, aux, nul, com1, lpt1).
    /// </summary>
    private static readonly HashSet<string> WindowsDeviceNames = BuildWindowsDeviceNames();

    private static HashSet<string> BuildWindowsDeviceNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "con", "prn", "aux", "nul"
        };

        for (int i = 1; i <= 9; i++)
        {
            names.Add("com" + i);
            names.Add("lpt" + i);
        }

        return names;
    }

    /// <summary>
    /// Приводит тег лога к каноническому виду: <c>[TankRadar]</c> → <c>TankRadar</c>.
    ///
    /// <para>Скобки снимаются молча и намеренно. Эталонный шаблон печатает
    /// <c>LOG_PREFIX = '[HelloTanker]'</c>, а его README учит грепать
    /// <c>grep -F "[HelloTanker/loader]"</c> — то есть пользователь видит тег ИМЕННО
    /// в скобках и копирует его в мастер в этом виде. Отказ «тег лога не должен содержать
    /// скобки» в этом месте наказывал за правильно понятый шаблон, поэтому его больше нет:
    /// скобки добавляет сам шаблон, здесь хранится голое имя.</para>
    /// </summary>
    /// <param name="tag">Тег как его ввёл пользователь.</param>
    /// <returns>Тег без окружающих скобок и пробелов.</returns>
    public static string NormalizeLogTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return string.Empty;

        string t = tag.Trim();

        // Цикл, а не одно снятие: '[[Tag]]' — тоже понятное намерение пользователя.
        while (t.Length >= 2 && t[0] == '[' && t[^1] == ']')
        {
            t = t[1..^1].Trim();
        }

        return t;
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex PackageRegex();

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9_]+)+$")]
    private static partial Regex ModIdRegex();

    /// <summary>Имя пакета допустимо как имя питоновского пакета и как имя каталога.</summary>
    /// <param name="package">Проверяемое имя.</param>
    /// <param name="error">Причина отказа.</param>
    /// <returns><c>true</c>, если имя годится.</returns>
    public static bool IsValidPackage(string? package, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(package))
        {
            error = "имя пакета не задано";
            return false;
        }

        string p = package.Trim();
        if (p.Length > MaxPackageLength)
        {
            error = $"имя пакета длиннее {MaxPackageLength} символов";
            return false;
        }

        if (!PackageRegex().IsMatch(p))
        {
            error = "имя пакета: только строчная латиница, цифры и '_', первый символ — буква (например battle_timer)";
            return false;
        }

        if (ReservedNames.Contains(p))
        {
            error = $"имя '{p}' занято модулем клиента — возьмите другое";
            return false;
        }

        if (WindowsDeviceNames.Contains(p))
        {
            error = $"имя '{p}' занято устройством Windows (CON, PRN, AUX, NUL, COM1-9, LPT1-9) — " +
                    "каталог и файл с таким именем создать нельзя, возьмите другое";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Идентификатор мода вида <c>ru.local.battle_timer</c>. Разделители пути запрещены:
    /// <c>build.py</c> формирует имя артефакта как <c>&lt;id&gt;_&lt;version&gt;.mtmod</c>
    /// и режет их с предупреждением, а артефакт получает неожиданное имя.
    /// </summary>
    /// <param name="modId">Проверяемый идентификатор.</param>
    /// <param name="error">Причина отказа.</param>
    /// <returns><c>true</c>, если идентификатор годится.</returns>
    public static bool IsValidModId(string? modId, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(modId))
        {
            error = "идентификатор мода не задан";
            return false;
        }

        string id = modId.Trim();
        if (id.IndexOfAny(['/', '\\']) >= 0)
        {
            error = "идентификатор не должен содержать '/' и '\\': build.py обрежет их и артефакт получит другое имя";
            return false;
        }

        if (!ModIdRegex().IsMatch(id))
        {
            error = "идентификатор: строчная латиница, цифры, точки и '_', минимум две части (например ru.local.battle_timer)";
            return false;
        }

        return true;
    }

    /// <summary>Версия вида <c>1.0.0</c>: её подставляют и в meta.xml, и в имя артефакта.</summary>
    /// <param name="version">Проверяемая версия.</param>
    /// <param name="error">Причина отказа.</param>
    /// <returns><c>true</c>, если версия годится.</returns>
    public static bool IsValidVersion(string? version, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(version))
        {
            error = "версия не задана";
            return false;
        }

        foreach (char c in version.Trim())
        {
            if (!char.IsAsciiDigit(c) && c != '.')
            {
                error = "версия: только цифры и точки (например 1.0.0)";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Подставляет НАБОР токенов за ОДИН проход: то, что уже подставлено, дальше не
    /// просматривается.
    ///
    /// <para>Почему не цепочка <c>string.Replace</c>. Токены шаблона пересекаются
    /// (<c>hello_tanker</c> — часть <c>ru.local.hello_tanker</c>, <c>MyMod</c> — часть
    /// <c>MyMod Pro</c>), и последовательная замена бьёт по СВОЕМУ ЖЕ результату:
    /// пакет <c>hello_tanker_pro</c> давал <c>&lt;id&gt;ru.local.hello_tanker_pro_pro&lt;/id&gt;</c>,
    /// а отображаемое имя <c>MyMod Pro</c> превращалось в <c>MM Pro</c> следующей же
    /// заменой тега лога. Сортировка «сначала длинные» это не лечит: она защищает от
    /// пересечения ИСХОДНЫХ токенов, а не от того, что новое значение содержит другой токен.</para>
    ///
    /// <para>Здесь текст просматривается слева направо ровно один раз, в каждой позиции
    /// берётся самый длинный подходящий токен, и курсор перепрыгивает через ВСТАВЛЕННОЕ
    /// значение.</para>
    /// </summary>
    /// <param name="text">Исходный текст.</param>
    /// <param name="substitutions">Пары «токен → значение». Пустые токены игнорируются.</param>
    /// <param name="replaced">Сколько подстановок сделано.</param>
    /// <returns>Текст с подстановками.</returns>
    public static string ReplaceTokensOnce(
        string? text,
        IReadOnlyList<(string From, string To)> substitutions,
        out int replaced)
    {
        replaced = 0;
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        ArgumentNullException.ThrowIfNull(substitutions);

        // Длинные раньше коротких: в одной позиции 'ru.local.my_mod' обязан выиграть у 'my_mod'.
        // OrderByDescending устойчив, поэтому при равной длине сохраняется порядок объявления.
        (string From, string To)[] ordered =
            [.. substitutions.Where(s => !string.IsNullOrEmpty(s.From)).OrderByDescending(s => s.From.Length)];

        if (ordered.Length == 0) return text;

        var sb = new StringBuilder(text.Length + 32);
        int i = 0;

        while (i < text.Length)
        {
            bool hit = false;
            foreach ((string from, string to) in ordered)
            {
                if (i + from.Length > text.Length) continue;
                if (string.CompareOrdinal(text, i, from, 0, from.Length) != 0) continue;

                sb.Append(to);
                i += from.Length;
                replaced++;
                hit = true;
                break;
            }

            if (!hit) sb.Append(text[i++]);
        }

        return sb.ToString();
    }

    /// <summary><c>battle_timer</c> → <c>BattleTimer</c>. Это тег лога и имя логгера.</summary>
    /// <param name="package">Имя пакета в snake_case.</param>
    /// <returns>PascalCase.</returns>
    public static string ToPascalCase(string? package)
    {
        if (string.IsNullOrWhiteSpace(package)) return string.Empty;

        var sb = new StringBuilder(package.Length);
        bool upper = true;
        foreach (char c in package.Trim())
        {
            if (c is '_' or '-' or ' ') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return sb.ToString();
    }

    /// <summary><c>battle_timer</c> → <c>Battle Timer</c>.</summary>
    /// <param name="package">Имя пакета в snake_case.</param>
    /// <returns>Слова с заглавных букв через пробел.</returns>
    public static string ToDisplayName(string? package)
    {
        if (string.IsNullOrWhiteSpace(package)) return string.Empty;

        string[] parts = package.Trim().Split(['_', '-', ' '], StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    /// <summary>
    /// <c>Battle Timer</c> → <c>battle_timer</c>. Нужна, чтобы мастер мог вывести имя пакета
    /// из того единственного поля, которое пользователь действительно хочет заполнять.
    /// </summary>
    /// <param name="displayName">Отображаемое имя.</param>
    /// <returns>snake_case; пустая строка, если из имени ничего не выводится.</returns>
    public static string ToSnakeCase(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName)) return string.Empty;

        var sb = new StringBuilder(displayName.Length + 4);
        char prev = '\0';
        foreach (char c in displayName.Trim())
        {
            if (char.IsAsciiLetterUpper(c))
            {
                // Граница слова в PascalCase, но не в начале и не внутри аббревиатуры.
                if (sb.Length > 0 && prev != '_' && !char.IsAsciiLetterUpper(prev)) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && prev != '_')
            {
                sb.Append('_');
            }

            prev = sb.Length > 0 ? sb[^1] : '\0';
        }

        string s = sb.ToString().Trim('_');
        // Имя пакета обязано начинаться с буквы: 'battle_timer', а не '2fast'.
        if (s.Length > 0 && char.IsAsciiDigit(s[0])) s = "mod_" + s;
        return s;
    }
}

/// <summary>
/// Что мастер должен подставить в шаблон. Пять полей закрывают все двенадцать мест
/// подстановки: остальное выводится из них.
/// </summary>
public sealed record ScaffoldOptions
{
    /// <summary>Куда создать проект. Каталог создаётся, если его нет.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>
    /// Имя пакета в snake_case: <c>battle_timer</c>. Отсюда выводятся имя каталога пакета,
    /// имя файла точки входа <c>mod_battle_timer.py</c>, константы PACKAGE/ENTRY/RES_DIR,
    /// каталог конфигов и <c>_PATCH_MARKER</c>.
    /// </summary>
    public required string Package { get; init; }

    /// <summary>
    /// Идентификатор мода: <c>ru.local.battle_timer</c>. Пусто — вывести как
    /// <c>&lt;<see cref="ModIdPrefix"/>&gt;.&lt;пакет&gt;</c>.
    /// </summary>
    public string? ModId { get; init; }

    /// <summary>
    /// Префикс выводимого идентификатора (<c>modIdPrefix</c> из настроек кокпита). Пусто —
    /// <c>ru.local</c> (<see cref="Models.CockpitSettings.DefaultModIdPrefix"/>). Для публикуемых
    /// модов лучше свой обратный домен: <c>io.github.&lt;ник&gt;</c>.
    /// </summary>
    public string? ModIdPrefix { get; init; }

    /// <summary>Отображаемое имя: <c>Battle Timer</c>. Пусто — вывести из пакета.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Версия. По умолчанию <c>1.0.0</c>.</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>
    /// Тег лога: <c>BattleTimer</c>. Пусто — вывести из пакета. Именно по нему фильтрует
    /// живой python.log этапа 1.
    /// <para>Скобки писать не нужно, но можно: <c>[BattleTimer]</c> принимается и приводится
    /// к <c>BattleTimer</c> в <see cref="NameConventions.NormalizeLogTag"/> — в шаблоне тег
    /// стоит внутри скобок (<c>LOG_PREFIX = '[HelloTanker]'</c>), и скопировать его оттуда
    /// вместе со скобками — нормальное поведение пользователя, а не ошибка.</para>
    /// </summary>
    public string? LogTag { get; init; }

    /// <summary>Описание для meta.xml.</summary>
    public string? Description { get; init; }

    /// <summary>Корень шаблона. Пусто — взять из настроек кокпита.</summary>
    public string? TemplateDirectory { get; init; }

    /// <summary>
    /// Корень установленной игры (<c>C:\Games\Tanki</c>). Заполняется из настроек кокпита.
    ///
    /// <para>Нужен ровно для одного: не дать создать проект ВНУТРИ игры. Кокпит во всём
    /// остальном UI подписывает игру «только чтение», а мастер до этой проверки спокойно
    /// раскладывал 14 файлов исходников в <c>mods\&lt;версия&gt;</c> — то есть в тот самый
    /// каталог, который читают панель деплоя и детектор перекрытия .mtmod раскладкой.
    /// Пусто — проверка не делается.</para>
    /// </summary>
    public string? GameDirectory { get; init; }

    /// <summary>
    /// Разрешить запись в непустой каталог. По умолчанию <c>false</c>:
    /// затереть чужой проект молча — худшее, что может сделать мастер.
    /// </summary>
    public bool Overwrite { get; init; }

    /// <summary>Идентификатор с подставленным значением по умолчанию.</summary>
    public string EffectiveModId =>
        string.IsNullOrWhiteSpace(ModId) ? EffectiveModIdPrefix + "." + (Package?.Trim() ?? string.Empty) : ModId.Trim();

    /// <summary>Префикс идентификатора без точек по краям; пусто — <c>ru.local</c>.</summary>
    public string EffectiveModIdPrefix
    {
        get
        {
            string prefix = (ModIdPrefix ?? string.Empty).Trim().Trim('.');
            return prefix.Length == 0 ? Models.CockpitSettings.DefaultModIdPrefix : prefix;
        }
    }

    /// <summary>Отображаемое имя с подставленным значением по умолчанию.</summary>
    public string EffectiveDisplayName =>
        string.IsNullOrWhiteSpace(DisplayName) ? NameConventions.ToDisplayName(Package) : DisplayName.Trim();

    /// <summary>
    /// Тег лога с подставленным значением по умолчанию и снятыми скобками:
    /// <c>[TankRadar]</c> и <c>TankRadar</c> дают одно и то же.
    /// </summary>
    public string EffectiveLogTag =>
        string.IsNullOrWhiteSpace(LogTag)
            ? NameConventions.ToPascalCase(Package)
            : NameConventions.NormalizeLogTag(LogTag);

    /// <summary>Описание с подставленным значением по умолчанию.</summary>
    public string EffectiveDescription =>
        string.IsNullOrWhiteSpace(Description) ? EffectiveDisplayName + " — мод для клиента «Мир танков»." : Description.Trim();

    /// <summary>Проверяет поля до того, как на диске что-то создано.</summary>
    /// <returns>Список ошибок; пустой — можно создавать.</returns>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(TargetDirectory)) errors.Add("не задан каталог назначения");

        if (!NameConventions.IsValidPackage(Package, out string pkgError)) errors.Add(pkgError);
        if (!NameConventions.IsValidModId(EffectiveModId, out string idError)) errors.Add(idError);
        if (!NameConventions.IsValidVersion(Version, out string verError)) errors.Add(verError);

        if (string.IsNullOrWhiteSpace(EffectiveDisplayName)) errors.Add("отображаемое имя пусто");

        string tag = EffectiveLogTag;
        if (string.IsNullOrWhiteSpace(tag))
        {
            errors.Add("тег лога пуст");
        }
        else if (tag.IndexOfAny(['[', ']', '\r', '\n']) >= 0)
        {
            // Сюда доходят только скобки ВНУТРИ тега ('Tank]Radar'): окружающие уже сняты
            // в EffectiveLogTag. Такие ломают и фильтр живого лога, и разбор traceback.
            errors.Add("скобки в тег лога подставляются автоматически — введите просто TankRadar, " +
                       "в логе получится [TankRadar]; скобки и переводы строк ВНУТРИ тега недопустимы");
        }

        return errors;
    }
}

/// <summary>Итог работы мастера.</summary>
/// <param name="Success">Проект создан.</param>
/// <param name="TargetDirectory">Куда создан.</param>
/// <param name="EntryPointPath">Путь к <c>mod_&lt;пакет&gt;.py</c>.</param>
/// <param name="PackageDirectory">Путь к каталогу пакета с телом мода.</param>
/// <param name="FilesWritten">Сколько файлов записано.</param>
/// <param name="Replacements">Сколько токенов подставлено.</param>
/// <param name="Errors">Ошибки, если не создан.</param>
/// <param name="Log">Пошаговый протокол — его показывают пользователю.</param>
public sealed record ScaffoldResult(
    bool Success,
    string TargetDirectory,
    string EntryPointPath,
    string PackageDirectory,
    int FilesWritten,
    int Replacements,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Log)
{
    /// <summary>Неудача с одной причиной.</summary>
    /// <param name="target">Каталог назначения.</param>
    /// <param name="errors">Причины.</param>
    /// <returns>Результат-отказ.</returns>
    public static ScaffoldResult Failed(string target, params string[] errors) =>
        new(false, target, string.Empty, string.Empty, 0, 0, errors, []);
}
