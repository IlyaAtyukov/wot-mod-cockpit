using System.Text;
using System.Text.RegularExpressions;

namespace WotModCockpit.Core.Migration;

/// <summary>
/// След мода в клиенте: какие имена мод упоминает в коде и какие модули импортирует.
/// <para>
/// Читается из исходников мода без исполнения и без синтаксического разбора Python 2: свой
/// маленький лексер отделяет код от комментариев и строк. Имена берутся из кода, а из строк —
/// только строки-идентификаторы целиком: перехват по имени (<c>METHODS = ('showShotResults', …)</c>
/// и <c>setattr(cls, name, …)</c>) упоминает метод клиента именно строкой. Длинные строки и
/// докстринги в след не попадают — иначе каждое упоминание в комментарии к коду считалось бы
/// зависимостью.
/// </para>
/// </summary>
public sealed class ModFootprint
{
    private static readonly Regex Identifier = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly Regex DottedName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)+$", RegexOptions.Compiled);

    private static readonly Regex ImportLine = new(
        @"^[ \t]*import[ \t]+(?<list>[\w.]+(?:[ \t]+as[ \t]+\w+)?(?:[ \t]*,[ \t]*[\w.]+(?:[ \t]+as[ \t]+\w+)?)*)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly Regex FromImport = new(
        @"^[ \t]*from[ \t]+(?<module>[\w.]+)[ \t]+import[ \t]+(?<names>\([^)]*\)|[^\r\n]+)",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _imports = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _dottedStrings = new(StringComparer.Ordinal);

    private ModFootprint()
    {
    }

    /// <summary>Имена из кода мода → первое место, где встретились (<c>main.py:12</c>).</summary>
    public IReadOnlyDictionary<string, string> Names => _names;

    /// <summary>
    /// Кандидаты в модули → место. Для <c>from X import a, b</c> — и <c>X</c>, и <c>X.a</c>, <c>X.b</c>:
    /// что из этого модуль, а что атрибут, решает индекс клиента.
    /// </summary>
    public IReadOnlyDictionary<string, string> ImportCandidates => _imports;

    /// <summary>
    /// Строки вида <c>'a.b.c'</c> — возможные имена модулей для <c>__import__</c> и
    /// <c>importlib.import_module</c>. Модулем клиента считается только то, что есть в индексе.
    /// </summary>
    public IReadOnlyDictionary<string, string> DottedStrings => _dottedStrings;

    /// <summary>Мод упоминает имя.</summary>
    /// <param name="name">Имя.</param>
    /// <returns>Признак.</returns>
    public bool Mentions(string name) => _names.ContainsKey(name);

    /// <summary>Читает все <c>.py</c> мода.</summary>
    /// <param name="srcDir">Каталог <c>src</c> проекта.</param>
    /// <returns>След мода.</returns>
    public static ModFootprint Read(string srcDir)
    {
        var footprint = new ModFootprint();
        if (!Directory.Exists(srcDir))
        {
            return footprint;
        }

        foreach (string file in Directory.EnumerateFiles(srcDir, "*.py", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = Path.GetRelativePath(srcDir, file).Replace('\\', '/');
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            footprint.Scan(rel, text);
        }

        return footprint;
    }

    /// <summary>Разбирает один файл (открыто для тестов).</summary>
    /// <param name="relativePath">Путь для сообщений.</param>
    /// <param name="text">Текст файла.</param>
    /// <returns>Этот же след.</returns>
    public ModFootprint Scan(string relativePath, string text)
    {
        var code = new StringBuilder(text.Length);
        int line = 1;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                line++;
                code.Append(c);
                i++;
                continue;
            }

            if (c == '#')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c is '\'' or '"')
            {
                int startLine = line;
                bool triple = i + 2 < text.Length && text[i + 1] == c && text[i + 2] == c;
                int quoteLen = triple ? 3 : 1;
                int j = i + quoteLen;
                var literal = new StringBuilder();
                while (j < text.Length)
                {
                    char d = text[j];
                    if (d == '\\' && j + 1 < text.Length)
                    {
                        literal.Append(d).Append(text[j + 1]);
                        if (text[j + 1] == '\n')
                        {
                            line++;
                        }

                        j += 2;
                        continue;
                    }

                    if (d == '\n')
                    {
                        if (!triple)
                        {
                            break;                  // незакрытая строка: перенос разберёт внешний цикл
                        }

                        line++;
                    }

                    if (d == c && (!triple || (j + 2 < text.Length && text[j + 1] == c && text[j + 2] == c)))
                    {
                        j += quoteLen;
                        break;
                    }

                    literal.Append(d);
                    j++;
                }

                string value = literal.ToString();
                string where = relativePath + ":" + startLine;
                if (!triple && Identifier.IsMatch(value))
                {
                    _names.TryAdd(value, where);
                }
                else if (!triple && DottedName.IsMatch(value))
                {
                    _dottedStrings.TryAdd(value, where);
                }

                // Строка в коде остаётся пустой: разбор импортов не должен видеть её содержимого,
                // но переносы строк внутри неё сохраняются — иначе собьются номера строк.
                code.Append("''");
                for (int k = 0; k < line - startLine; k++)
                {
                    code.Append('\n');
                }

                i = j;
                continue;
            }

            if (char.IsAsciiLetter(c) || c == '_')
            {
                int start = i;
                while (i < text.Length && (char.IsAsciiLetterOrDigit(text[i]) || text[i] == '_'))
                {
                    i++;
                }

                string name = text[start..i];
                _names.TryAdd(name, relativePath + ":" + line);
                code.Append(name);
                continue;
            }

            code.Append(c);
            i++;
        }

        CollectImports(relativePath, code.ToString());
        return this;
    }

    private void CollectImports(string relativePath, string code)
    {
        foreach (Match m in ImportLine.Matches(code))
        {
            string where = relativePath + ":" + LineOf(code, m.Index);
            foreach (string part in m.Groups["list"].Value.Split(','))
            {
                string module = part.Trim().Split(' ', '\t')[0].Trim();
                if (module.Length > 0)
                {
                    _imports.TryAdd(module, where);
                }
            }
        }

        foreach (Match m in FromImport.Matches(code))
        {
            string module = m.Groups["module"].Value;
            if (module.StartsWith('.'))
            {
                continue;                       // относительный импорт — свой пакет
            }

            string where = relativePath + ":" + LineOf(code, m.Index);
            _imports.TryAdd(module, where);
            string names = m.Groups["names"].Value.Trim().TrimStart('(').TrimEnd(')');
            foreach (string part in names.Split(','))
            {
                string name = part.Trim().Split(' ', '\t', '\r', '\n')[0].Trim();
                if (name.Length > 0 && name != "*" && Identifier.IsMatch(name))
                {
                    _imports.TryAdd(module + "." + name, where);
                }
            }
        }
    }

    private static int LineOf(string text, int index)
    {
        int line = 1;
        for (int k = 0; k < index && k < text.Length; k++)
        {
            if (text[k] == '\n')
            {
                line++;
            }
        }

        return line;
    }
}
