using System.Text.RegularExpressions;
using WotModCockpit.Core.Scaffold;

namespace WotModCockpit.App.Stage3;

/// <summary>
/// Одна точка подстановки в коде рецепта: что ищется в тексте и чем заменяется.
/// </summary>
/// <param name="Token">Литерал, который стоит в коде рецепта (<c>my_mod</c>, <c>[MyMod]</c>).</param>
/// <param name="Label">Заголовок поля в форме параметров.</param>
/// <param name="Default">Значение по умолчанию — всегда равно <paramref name="Token"/>.</param>
/// <param name="Hint">Подсказка под полем.</param>
/// <param name="FromProject">
/// Значение подставляется из паспорта открытого проекта. Такие поля заполняются сами,
/// и именно они связывают вставленный код с фильтром живого python.log этапа 1.
/// </param>
public sealed record RecipeToken(
    string Token,
    string Label,
    string Default,
    string Hint,
    bool FromProject);

/// <summary>
/// Подстановка параметров в код рецепта. Живёт в UI, а не в Core, по одной причине:
/// предпросмотр обязан обновляться на КАЖДОЕ нажатие клавиши в поле параметра, а это
/// значит «мгновенно и без обращения к сервису».
/// <para>
/// Замена идёт ОДНИМ проходом слева направо, в каждой позиции выигрывает самый длинный
/// токен, а курсор перепрыгивает через уже вставленное значение
/// (<see cref="NameConventions.ReplaceTokensOnce"/>). Двух вещей это не даёт сделать
/// одновременно: <c>my_mod</c> — подстрока <c>ru.local.my_mod</c> (лечится длиной), и
/// новое значение может САМО содержать другой токен — <c>MyMod Pro</c> в поле
/// «отображаемое имя» цепочкой <c>Replace</c> превращалось в <c>&lt;тег лога&gt; Pro</c>.
/// </para>
/// </summary>
public static class RecipeRenderer
{
    /// <summary>Токены паспорта проекта — те же, что стоят в исходном коде рецептов.</summary>
    public static IReadOnlyList<RecipeToken> ProjectTokens { get; } = new[]
    {
        new RecipeToken(
            "ru.local.my_mod",
            "идентификатор мода",
            "ru.local.my_mod",
            "то же, что <id> в src\\meta.xml",
            FromProject: true),
        new RecipeToken(
            "my_mod",
            "имя пакета",
            "my_mod",
            "каталог тела мода и имя папки конфигов; только [a-z0-9_]",
            FromProject: true),
        new RecipeToken(
            "My Mod",
            "отображаемое имя",
            "My Mod",
            "то же, что <name> в src\\meta.xml",
            FromProject: true),
        new RecipeToken(
            "MyMod",
            "тег лога",
            "MyMod",
            "по нему фильтруется живой python.log; в коде это [MyMod] и getLogger('MyMod')",
            FromProject: true)
    };

    /// <summary>Строка «# ПОДСТАВЬ» в конце строки кода — маркер точки параметризации.</summary>
    private static readonly Regex SubstituteMarker = new(
        @"^\s*(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<lit>u?[""'](?<val>[^""']*)[""'])[^#]*#.*ПОДСТАВ",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Шаблонный плейсхолдер вида <c>{{event}}</c> — на случай, если Core их использует.</summary>
    private static readonly Regex CurlyPlaceholder = new(
        @"\{\{(?<key>[A-Za-z_][A-Za-z0-9_]*)\}\}",
        RegexOptions.Compiled);

    /// <summary>
    /// Находит в коде рецепта все точки подстановки.
    /// <para>
    /// Источников три: (1) фиксированные токены паспорта, если они в коде вообще есть;
    /// (2) строки, помеченные комментарием <c>ПОДСТАВЬ</c> — там автор рецепта прямо сказал,
    /// что значение чужое (имя класса, метода, события, клавиши); (3) плейсхолдеры
    /// <c>{{ключ}}</c>. Дубликаты снимаются по тексту токена, приоритет у паспорта.
    /// </para>
    /// </summary>
    /// <param name="code">Код рецепта как есть.</param>
    /// <returns>Токены в порядке показа: сначала паспорт, потом найденные в коде.</returns>
    public static IReadOnlyList<RecipeToken> Scan(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Array.Empty<RecipeToken>();
        }

        var result = new List<RecipeToken>(8);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (RecipeToken token in ProjectTokens)
        {
            if (code.Contains(token.Token, StringComparison.Ordinal) && seen.Add(token.Token))
            {
                result.Add(token);
            }
        }

        foreach (string line in code.Split('\n'))
        {
            Match m = SubstituteMarker.Match(line);
            if (!m.Success)
            {
                continue;
            }

            string value = m.Groups["val"].Value;

            // Пустой литерал заменять нечем: замена пустой строки — это вставка значения
            // между каждыми двумя символами файла.
            if (value.Length == 0 || !seen.Add(value))
            {
                continue;
            }

            result.Add(new RecipeToken(
                value,
                m.Groups["name"].Value,
                value,
                "помечено в рецепте как «ПОДСТАВЬ»",
                FromProject: false));
        }

        foreach (Match m in CurlyPlaceholder.Matches(code))
        {
            string token = m.Value;
            if (!seen.Add(token))
            {
                continue;
            }

            result.Add(new RecipeToken(
                token,
                m.Groups["key"].Value,
                token,
                "плейсхолдер шаблона",
                FromProject: false));
        }

        return result;
    }

    /// <summary>
    /// Подставляет значения в код.
    /// </summary>
    /// <param name="code">Код рецепта.</param>
    /// <param name="values">Пары «токен → значение». Пустые и совпадающие с токеном пропускаются.</param>
    /// <returns>Код с подстановками. Исходная строка, если подставлять нечего.</returns>
    public static string Render(string? code, IEnumerable<KeyValuePair<string, string>>? values)
    {
        if (string.IsNullOrEmpty(code) || values is null)
        {
            return code ?? string.Empty;
        }

        List<(string From, string To)> pairs = values
            .Where(p => p.Key.Length > 0
                        && p.Value is not null
                        && !string.Equals(p.Key, p.Value, StringComparison.Ordinal))
            .Select(p => (p.Key, p.Value))
            .ToList();

        if (pairs.Count == 0)
        {
            return code;
        }

        // ОДИН проход слева направо, а не цепочка Replace: см. комментарий к классу.
        return NameConventions.ReplaceTokensOnce(code, pairs, out _);
    }

    /// <summary>
    /// Есть ли в тексте байты вне ASCII. Нужно перед вставкой в чужой файл: Python 2.7 по
    /// PEP 263 отказывается компилировать файл с не-ASCII символом, если в первых двух
    /// строках нет объявления кодировки (правило WOT015), а тело мода компилируется уже
    /// ВНУТРИ клиента — то есть ошибка вылезет одной строкой в python.log.
    /// </summary>
    /// <param name="text">Текст.</param>
    /// <returns>true, если есть символы вне ASCII.</returns>
    public static bool HasNonAscii(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (char c in text)
        {
            if (c > 0x7F)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Объявлена ли кодировка в первых двух строках файла (PEP 263).
    /// </summary>
    /// <param name="fileText">Содержимое файла.</param>
    /// <returns>true, если строка <c>coding: …</c> найдена там, где Python её ищет.</returns>
    public static bool HasCodingDeclaration(string? fileText)
    {
        if (string.IsNullOrEmpty(fileText))
        {
            return false;
        }

        string[] lines = fileText.Split('\n');
        for (int i = 0; i < Math.Min(2, lines.Length); i++)
        {
            if (CodingLine.IsMatch(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Имена <c>def</c>/<c>class</c> верхнего уровня в тексте.
    /// <para>
    /// Разбор берётся из Core (<see cref="WotModCockpit.Core.Preflight.PythonLineModel"/>),
    /// а не пишется заново: там уже вырезаны комментарии и содержимое строковых литералов,
    /// поэтому <c>def</c> внутри докстринга рецепта именем не считается. Своя регулярка
    /// на этом и спотыкалась бы: почти каждый рецепт цитирует в докстринге код клиента.
    /// </para>
    /// </summary>
    /// <param name="code">Код.</param>
    /// <returns>Имена в порядке объявления, без повторов.</returns>
    public static IReadOnlyList<string> TopLevelNames(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return Array.Empty<string>();
        }

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Core.Preflight.PythonDefinition def in Core.Preflight.PythonLineModel.Parse(code).Definitions)
        {
            if (!def.IsTopLevel || def.Name.Length == 0)
            {
                continue;
            }

            if (seen.Add(def.Name))
            {
                names.Add(def.Name);
            }
        }

        return names;
    }

    /// <summary>
    /// Имена верхнего уровня, которые есть И в новом блоке, И в файле-приёмнике.
    ///
    /// <para><b>Зачем.</b> «Вставить в файл» дописывает рецепт в КОНЕЦ файла дословно.
    /// Имена у рецептов пересекаются намеренно (<c>subscribe</c>/<c>unsubscribe</c> —
    /// в R05, R06, R13 и R16; <c>register</c> — в R08, R14, R15; <c>_safe</c> — в R01 и R05),
    /// а модуль Python исполняется сверху вниз, поэтому в рантайме доживает только ПОСЛЕДНЕЕ
    /// объявление. Измерено: R05 + R06 в одном main.py дают файл, где три подписки R05
    /// на g_playerEvents не выполняются НИКОГДА, при этом check_py2_syntax отвечает [OK].
    /// Правило preflight WOT022 находит это уже постфактум — а здесь можно предупредить
    /// ДО записи.</para>
    /// </summary>
    /// <param name="block">Вставляемый код.</param>
    /// <param name="existingFileText">Текущее содержимое файла-приёмника.</param>
    /// <returns>Конфликтующие имена; пустой список — конфликтов нет.</returns>
    public static IReadOnlyList<string> NameClashes(string? block, string? existingFileText)
    {
        IReadOnlyList<string> incoming = TopLevelNames(block);
        if (incoming.Count == 0)
        {
            return Array.Empty<string>();
        }

        var present = new HashSet<string>(TopLevelNames(existingFileText), StringComparer.Ordinal);
        return [.. incoming.Where(present.Contains)];
    }

    /// <summary>
    /// Путь указывает на ТОЧКУ ВХОДА мода (<c>…/gui/mods/mod_*.py</c>), а не на тело.
    ///
    /// <para>Раскладку задаёт загрузчик клиента, её же проверяет правило WOT014: файлы
    /// <c>mod_*.py</c> лежат прямо в <c>scripts/client/gui/mods/</c> и компилируются в
    /// <c>.pyc</c> самим build.py через <c>tools/py27c.py</c>. Этот компилятор не
    /// поддерживает замыкания, а 7 рецептов из 16 их используют, и сборка упадёт
    /// с Py27CompileError, которая на рецепт никак не сошлётся.</para>
    /// </summary>
    /// <param name="relativePath">Путь файла относительно корня проекта, любые разделители.</param>
    /// <returns>true, если это точка входа.</returns>
    public static bool IsEntryPointFile(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string normalized = relativePath.Replace('\\', '/');
        int at = normalized.LastIndexOf('/');
        string file = at < 0 ? normalized : normalized[(at + 1)..];

        return file.StartsWith("mod_", StringComparison.Ordinal)
               && file.EndsWith(".py", StringComparison.OrdinalIgnoreCase)
               && normalized.Contains("gui/mods/", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex CodingLine = new(
        @"coding[:=]\s*([-\w.]+)",
        RegexOptions.Compiled);
}
