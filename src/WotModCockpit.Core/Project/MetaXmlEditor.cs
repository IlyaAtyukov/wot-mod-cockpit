using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WotModCockpit.Core.Project;

/// <summary>
/// Имя и описание мода в <c>src\meta.xml</c> проекта: чтение и правка ровно этих двух полей.
/// <para>
/// <b>Почему не <see cref="Abstractions.IProjectStore.SyncMetaXml"/>.</b> Готовый писатель
/// в кокпите есть, но для правки с экрана «Моды» он не годится по трём причинам, и каждая
/// проверена прогоном на копиях настоящих файлов:
/// </para>
/// <list type="bullet">
///   <item>пишет СРАЗУ id, version, name и description — модель, собранная не из свежего
///   файла, затёрла бы id и version;</item>
///   <item>не сохраняет файл побайтно: <c>encoding="UTF-8"</c> становится <c>utf-8</c>, BOM
///   теряется, CRLF превращается в LF, а в конец дописывается CRLF — переводы строк
///   становятся смешанными;</item>
///   <item>битый XML МОЛЧА заменяется пустым <c>&lt;root/&gt;</c> — всё содержимое пропадает.</item>
/// </list>
/// <para>
/// <b>Как устроено здесь.</b> Файл сначала целиком проверяется парсером; битый, с DOCTYPE или
/// не в UTF-8 — отказ, ничего не пишется. Затем меняются ТОЛЬКО тексты двух элементов, прямо
/// в исходном тексте, по найденным границам: декларация, BOM, переводы строк, отступы и все
/// прочие элементы остаются теми же байтами. Перед записью результат ещё раз разбирается и
/// сверяется: имя и описание — новые, id и version — прежние. Не сошлось — файл не трогается.
/// Запись атомарная: временный файл рядом и замена.
/// </para>
/// <para>
/// <b>Какие элементы.</b> Прямые дети корня с ТОЧНЫМ именем <c>name</c> и <c>description</c> —
/// ровно так их ищет <c>build.py</c> (<c>root.find(tag)</c>, с учётом регистра). Элемент с тем же
/// именем в другом регистре (<c>&lt;Name&gt;</c>) — отказ: build.py его не видит, и правка
/// «рядом» только запутала бы файл.
/// </para>
/// </summary>
public static class MetaXmlEditor
{
    /// <summary>Потолок длины имени. В списках модов длиннее всё равно обрежется.</summary>
    public const int MaxNameLength = 120;

    /// <summary>Потолок длины описания.</summary>
    public const int MaxDescriptionLength = 2000;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    private static readonly Regex DeclaredEncoding = new(
        @"\A\s*<\?xml[^>]*?\bencoding\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AnyNewLine = new(@"\r\n|\r|\n", RegexOptions.CultureInvariant);

    /// <summary>Читает id, version, name и description из <c>src\meta.xml</c>.</summary>
    /// <param name="path">Путь к <c>meta.xml</c>.</param>
    /// <returns>Поля; отсутствующий элемент — пустая строка.</returns>
    /// <exception cref="MetaXmlException">Файла нет, он битый или не в UTF-8.</exception>
    public static MetaXmlFields Read(string path)
    {
        Document doc = Load(path);
        return doc.Fields();
    }

    /// <summary>
    /// Ставит новые имя и (или) описание, не меняя в файле больше ничего.
    /// <para>
    /// <b><c>null</c> — поле НЕ ТРОГАТЬ.</b> Это не удобство, а защита, и нашла её необходимость
    /// независимая проверка. Раньше писались оба поля всегда, и поле, которое в диалоге не
    /// правили, переписывалось снимком, взятым при открытии диалога. Правка того же поля,
    /// сделанная за это время в редакторе, молча откатывалась, а комментарий внутри нетронутого
    /// поля молча пропадал.
    /// </para>
    /// <para>
    /// Значение, совпадающее с тем, что уже в файле, тоже не трогает ни одного байта. Если
    /// менять оказалось нечего, файл не открывается на запись вовсе.
    /// </para>
    /// </summary>
    /// <param name="path">Путь к <c>meta.xml</c>.</param>
    /// <param name="name">Новое имя (обрезается по краям) либо <c>null</c> — не трогать.</param>
    /// <param name="description">
    /// Новое описание (обрезается по краям, переводы строк приводятся к стилю файла) либо
    /// <c>null</c> — не трогать.
    /// </param>
    /// <returns><c>true</c> — файл записан; <c>false</c> — менять было нечего.</returns>
    /// <exception cref="MetaXmlException">Значения не прошли проверку либо файл править нельзя.</exception>
    /// <exception cref="IOException">Файл не удалось записать.</exception>
    /// <exception cref="UnauthorizedAccessException">Нет прав на запись.</exception>
    public static bool Write(string path, string? name, string? description)
    {
        name = name?.Trim();
        description = description?.Trim();

        string? error = (name is null ? null : ValidateName(name))
                        ?? (description is null ? null : ValidateDescription(description));
        if (error is not null)
        {
            throw new MetaXmlException(error);
        }

        Document doc = Load(path);
        MetaXmlFields before = doc.Fields();

        var changes = new List<(string Tag, string Value)>();
        if (name is not null && name != before.Name)
        {
            changes.Add(("name", name));
        }

        if (description is not null && Lf(description) != before.Description)
        {
            changes.Add(("description", description));
        }

        if (changes.Count == 0)
        {
            return false;
        }

        // Комментарий внутри поля в диалоге не виден — правка удалила бы его молча.
        foreach ((string tag, _) in changes)
        {
            doc.EnsurePlainText(tag);
        }

        string written;
        try
        {
            written = Apply(doc, changes);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        {
            // Ошибка границ — не повод ронять приложение окном со стеком: для пользователя это
            // обычный отказ, файл при этом не тронут.
            throw new MetaXmlException("внутренняя ошибка правки, файл не тронут: " + ex.Message);
        }

        // ПОСЛЕДНЯЯ ПРОВЕРКА ДО ДИСКА. Правка шла по границам в исходном тексте, и если их поиск
        // где-то ошибся, это обязано вскрыться здесь, а не в файле пользователя.
        MetaXmlFields after;
        try
        {
            after = Parse(written, path).Fields();
        }
        catch (MetaXmlException ex)
        {
            throw new MetaXmlException("правка дала битый XML, файл не тронут: " + ex.Message);
        }

        string expectedName = changes.Any(c => c.Tag == "name") ? name! : before.Name;
        string expectedDescription = changes.Any(c => c.Tag == "description") ? Lf(description!) : before.Description;
        if (after.Name != expectedName || after.Description != expectedDescription
            || after.Id != before.Id || after.Version != before.Version)
        {
            throw new MetaXmlException("правка не сошлась при проверке, файл не тронут");
        }

        byte[] body = StrictUtf8.GetBytes(written);
        WriteAtomic(path, doc.HasBom ? [.. Bom, .. body] : body);
        return true;
    }

    /// <summary>Переводы строк так, как их отдаёт разобранный XML: только LF.</summary>
    private static string Lf(string text) => AnyNewLine.Replace(text, "\n");

    /// <summary>Новый текст файла: меняются только тексты перечисленных полей.</summary>
    private static string Apply(Document doc, List<(string Tag, string Value)> changes)
    {
        string text = doc.Text;
        var edits = new List<Edit>();
        var missing = new List<string>();

        foreach ((string tag, string value) in changes)
        {
            string escaped = Escape(AnyNewLine.Replace(value, doc.NewLine));
            Child? child = doc.FindChild(tag);
            if (child is null)
            {
                missing.Add("<" + tag + ">" + escaped + "</" + tag + ">");
            }
            else if (child.SelfClosing)
            {
                // <name/> или <name attr="…"/>: тег раскрывается, атрибуты сохраняются.
                edits.Add(new Edit(child.StartTagStart, child.StartTagEnd,
                    Expand(text[child.StartTagStart..child.StartTagEnd]) + escaped + "</" + child.Name + ">"));
            }
            else
            {
                edits.Add(new Edit(child.ContentStart, child.ContentEnd, escaped));
            }
        }

        if (missing.Count > 0)
        {
            edits.Add(doc.Insertion(missing));
        }

        // С конца к началу: правка не сдвигает границы тех, что стоят раньше.
        var result = new StringBuilder(text);
        foreach (Edit edit in edits.OrderByDescending(e => e.Start))
        {
            result.Remove(edit.Start, edit.End - edit.Start).Insert(edit.Start, edit.Replacement);
        }

        return result.ToString();
    }

    /// <summary>Самозакрытый тег <c>&lt;x a="1"/&gt;</c> → открывающий <c>&lt;x a="1"&gt;</c>.</summary>
    private static string Expand(string selfClosingTag) =>
        selfClosingTag[..selfClosingTag.LastIndexOf('/')].TrimEnd() + ">";

    /// <summary>Проверка имени. <c>null</c> — годится.</summary>
    /// <param name="name">Имя (уже обрезанное по краям).</param>
    /// <returns>Причина отказа либо <c>null</c>.</returns>
    public static string? ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (name.Trim().Length == 0)
        {
            return "Имя не может быть пустым: build.py предупредит о пустом <name>, а в списке модов вместо имени окажется имя файла.";
        }

        if (name.Contains('\n') || name.Contains('\r'))
        {
            return "Имя — одна строка: перевод строки разорвёт строку в списке модов.";
        }

        if (name.Length > MaxNameLength)
        {
            return $"Имя длиннее {MaxNameLength} символов ({name.Length}): в списках оно всё равно обрежется.";
        }

        return BadCharacter(name);
    }

    /// <summary>Проверка описания. <c>null</c> — годится. Пустое описание допустимо.</summary>
    /// <param name="description">Описание (уже обрезанное по краям).</param>
    /// <returns>Причина отказа либо <c>null</c>.</returns>
    public static string? ValidateDescription(string description)
    {
        ArgumentNullException.ThrowIfNull(description);

        if (description.Length > MaxDescriptionLength)
        {
            return $"Описание длиннее {MaxDescriptionLength} символов ({description.Length}).";
        }

        return BadCharacter(description);
    }

    // ------------------------------------------------------------------ внутреннее

    /// <summary>Символ, который XML 1.0 не допускает в тексте вовсе (управляющие, кроме табуляции и переводов строк).</summary>
    private static string? BadCharacter(string text)
    {
        foreach (char c in text)
        {
            if ((c < 0x20 && c is not '\t' and not '\n' and not '\r') || c is '￾' or '￿')
            {
                return $"В тексте есть управляющий символ U+{(int)c:X4} — в XML он недопустим.";
            }
        }

        return null;
    }

    /// <summary>Экранирование текста элемента: только то, без чего XML не разберётся.</summary>
    private static string Escape(string text) =>
        text.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    private static Document Load(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            throw new MetaXmlException("в проекте нет src\\meta.xml — имя и описание хранить негде");
        }
        catch (DirectoryNotFoundException)
        {
            throw new MetaXmlException("в проекте нет src\\meta.xml — имя и описание хранить негде");
        }

        bool bom = bytes.AsSpan().StartsWith(Bom);
        string text;
        try
        {
            text = StrictUtf8.GetString(bom ? bytes[3..] : bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new MetaXmlException("meta.xml не в UTF-8 — кокпит правит только UTF-8, чтобы не испортить буквы");
        }

        Match declared = DeclaredEncoding.Match(text);
        if (declared.Success && !string.Equals(declared.Groups[1].Value, "utf-8", StringComparison.OrdinalIgnoreCase))
        {
            throw new MetaXmlException(
                $"meta.xml объявлен в кодировке {declared.Groups[1].Value} — кокпит правит только UTF-8, чтобы не испортить буквы");
        }

        Document doc = Parse(text, path);
        doc.HasBom = bom;
        return doc;
    }

    private static Document Parse(string text, string path)
    {
        XDocument xml;
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(text), settings);
            xml = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException ex)
        {
            throw new MetaXmlException(
                "meta.xml битый (" + ex.Message + ") — править его кокпит не будет, чтобы не потерять содержимое. Поправьте файл в редакторе.");
        }

        XElement root = xml.Root ?? throw new MetaXmlException("в meta.xml нет корневого элемента");

        foreach (string tag in new[] { "id", "version", "name", "description" })
        {
            XElement[] exact = root.Elements().Where(e => e.Name.LocalName == tag).ToArray();
            if (exact.Length > 1)
            {
                throw new MetaXmlException($"в meta.xml {exact.Length} элемента <{tag}> — поправьте файл в редакторе, чтобы остался один");
            }

            XElement? other = root.Elements().FirstOrDefault(e =>
                e.Name.LocalName != tag && string.Equals(e.Name.LocalName, tag, StringComparison.OrdinalIgnoreCase));
            if (other is not null)
            {
                throw new MetaXmlException(
                    $"в meta.xml элемент <{other.Name.LocalName}> вместо <{tag}> — build.py различает регистр и его не видит. Поправьте в редакторе.");
            }

            if (exact.Length == 1 && exact[0].HasElements)
            {
                throw new MetaXmlException($"внутри <{tag}> в meta.xml есть другие элементы — такой файл кокпит не правит");
            }
        }

        return new Document(text, root, Scan(text), NewLineOf(text));
    }

    /// <summary>Какой перевод строки у файла: CRLF, если он есть хоть раз, иначе LF.</summary>
    private static string NewLineOf(string text) => text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>
    /// Границы прямых детей корня в ИСХОДНОМ тексте. Файл к этому моменту уже проверен
    /// парсером на правильность, поэтому разбор здесь простой: «&lt;» в тексте всегда начинает
    /// разметку, а значения атрибутов берутся в кавычки.
    /// </summary>
    private static Layout Scan(string text)
    {
        var children = new List<Child>();
        var open = new Stack<(string Name, int Depth, int StartTagStart, int StartTagEnd)>();
        int depth = 0;
        string rootName = string.Empty;
        int rootStartTagStart = -1;
        int rootStartTagEnd = -1;
        bool rootSelfClosing = false;
        int rootEndTagStart = -1;
        int i = 0;

        while (true)
        {
            int lt = text.IndexOf('<', i);
            if (lt < 0)
            {
                break;
            }

            if (Starts(text, lt, "<?"))
            {
                i = End(text, lt, "?>");
            }
            else if (Starts(text, lt, "<!--"))
            {
                i = End(text, lt, "-->");
            }
            else if (Starts(text, lt, "<![CDATA["))
            {
                i = End(text, lt, "]]>");
            }
            else if (Starts(text, lt, "</"))
            {
                int gt = text.IndexOf('>', lt);
                depth--;
                (string name, int d, int startTagStart, int startTagEnd) = open.Pop();
                if (d == 0)
                {
                    rootEndTagStart = lt;
                }
                else if (d == 1)
                {
                    children.Add(new Child(name, startTagStart, startTagEnd, startTagEnd, lt, gt + 1, SelfClosing: false));
                }

                i = gt + 1;
            }
            else
            {
                int gt = TagEnd(text, lt);
                string name = TagName(text, lt + 1);
                bool selfClosing = text[gt - 1] == '/';
                if (depth == 0 && rootStartTagStart < 0)
                {
                    rootName = name;
                    rootStartTagStart = lt;
                    rootStartTagEnd = gt + 1;
                    rootSelfClosing = selfClosing;
                }

                if (selfClosing)
                {
                    if (depth == 1)
                    {
                        children.Add(new Child(name, lt, gt + 1, gt + 1, gt + 1, gt + 1, SelfClosing: true));
                    }
                }
                else
                {
                    open.Push((name, depth, lt, gt + 1));
                    depth++;
                }

                i = gt + 1;
            }
        }

        return new Layout(children, new RootTag(rootName, rootStartTagStart, rootStartTagEnd, rootSelfClosing, rootEndTagStart));
    }

    private static bool Starts(string text, int at, string prefix) =>
        string.CompareOrdinal(text, at, prefix, 0, prefix.Length) == 0;

    private static int End(string text, int from, string terminator) =>
        text.IndexOf(terminator, from, StringComparison.Ordinal) + terminator.Length;

    /// <summary>Закрывающая «&gt;» открывающего тега, с учётом кавычек в атрибутах.</summary>
    private static int TagEnd(string text, int lt)
    {
        char quote = '\0';
        for (int j = lt + 1; j < text.Length; j++)
        {
            char c = text[j];
            if (quote != '\0')
            {
                if (c == quote)
                {
                    quote = '\0';
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (c == '>')
            {
                return j;
            }
        }

        return text.Length - 1;
    }

    /// <summary>
    /// Имя тега КАК В ТЕКСТЕ, с префиксом (<c>x:name</c>): закрывающий тег обязан повторить его
    /// буква в букву, иначе XML битый.
    /// </summary>
    private static string TagName(string text, int from)
    {
        int j = from;
        while (j < text.Length && !char.IsWhiteSpace(text[j]) && text[j] is not '/' and not '>')
        {
            j++;
        }

        return text[from..j];
    }

    /// <summary>Имя без префикса: <c>x:name</c> → <c>name</c>. Так элементы сравнивает и парсер.</summary>
    private static string LocalName(string qualified)
    {
        int colon = qualified.IndexOf(':');
        return colon >= 0 ? qualified[(colon + 1)..] : qualified;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
        string temp = Path.Combine(dir, Path.GetFileName(path) + ".cockpit-tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }

    private sealed record Edit(int Start, int End, string Replacement);

    /// <summary>Прямой ребёнок корня; <c>Name</c> — имя тега как в тексте, с префиксом.</summary>
    private sealed record Child(
        string Name, int StartTagStart, int StartTagEnd, int ContentStart, int ContentEnd, int EndTagEnd, bool SelfClosing);

    /// <summary>
    /// Корень; <c>Name</c> — имя как в тексте, с префиксом; <c>EndTagStart</c> — начало
    /// <c>&lt;/root&gt;</c>, у самозакрытого корня -1.
    /// </summary>
    private sealed record RootTag(string Name, int StartTagStart, int StartTagEnd, bool SelfClosing, int EndTagStart);

    private sealed record Layout(IReadOnlyList<Child> Children, RootTag Root);

    private sealed class Document(string text, XElement root, Layout layout, string newLine)
    {
        private const string DefaultIndent = "  ";

        public string Text { get; } = text;

        public string NewLine { get; } = newLine;

        public bool HasBom { get; set; }

        public MetaXmlFields Fields() => new(Value("id"), Value("version"), Value("name"), Value("description"));

        public Child? FindChild(string tag) => layout.Children.FirstOrDefault(c => LocalName(c.Name) == tag);

        /// <summary>
        /// Отказ, если внутри поля есть что-то кроме текста — комментарий или инструкция
        /// обработки. В окне правки их не видно, а новый текст заменяет содержимое целиком:
        /// правка удалила бы их молча.
        /// </summary>
        public void EnsurePlainText(string tag)
        {
            XElement? element = root.Elements().FirstOrDefault(e => e.Name.LocalName == tag);
            if (element is not null && element.Nodes().Any(n => n is not XText))
            {
                throw new MetaXmlException(
                    $"внутри <{tag}> в meta.xml есть комментарий — в окне правки его не видно, и правка удалила бы его молча. Поправьте <{tag}> в редакторе.");
            }
        }

        /// <summary>
        /// Правка, дописывающая недостающие элементы в корень, каждый с новой строки.
        /// <list type="bullet">
        ///   <item>есть соседи — после последнего, с его отступом;</item>
        ///   <item><c>&lt;root&gt;&lt;/root&gt;</c> или только пробелы и комментарии внутри — перед
        ///   <c>&lt;/root&gt;</c>, который встаёт на свою строку;</item>
        ///   <item><c>&lt;root/&gt;</c> — корень раскрывается: <c>&lt;root&gt;</c>, элементы,
        ///   <c>&lt;/root&gt;</c>.</item>
        /// </list>
        /// </summary>
        public Edit Insertion(IReadOnlyList<string> elements)
        {
            Child? last = layout.Children.OrderBy(c => c.EndTagEnd).LastOrDefault();
            if (last is not null)
            {
                int lineStart = Text.LastIndexOf('\n', Math.Max(0, last.StartTagStart - 1)) + 1;
                string before = Text[lineStart..last.StartTagStart];
                return new Edit(last.EndTagEnd, last.EndTagEnd, Lines(elements, before.All(char.IsWhiteSpace) ? before : DefaultIndent));
            }

            RootTag rootTag = layout.Root;
            if (rootTag.SelfClosing)
            {
                return new Edit(rootTag.StartTagStart, rootTag.StartTagEnd,
                    Expand(Text[rootTag.StartTagStart..rootTag.StartTagEnd]) + Lines(elements, DefaultIndent)
                    + NewLine + "</" + rootTag.Name + ">");
            }

            // Вставка сразу после последнего непробельного символа содержимого (комментария) либо
            // в самое начало пустого корня. Хвост с переводом строки перед </root> остаётся как
            // был; хвост без перевода строки (<root>  </root>) заменяется им.
            int end = rootTag.EndTagStart;
            int at = end;
            while (at > rootTag.StartTagEnd && char.IsWhiteSpace(Text[at - 1]))
            {
                at--;
            }

            string lines = Lines(elements, DefaultIndent);
            return Text.AsSpan(at, end - at).Contains('\n')
                ? new Edit(at, at, lines)
                : new Edit(at, end, lines + NewLine);
        }

        private string Lines(IReadOnlyList<string> elements, string indent) =>
            string.Concat(elements.Select(e => NewLine + indent + e));

        private string Value(string tag) =>
            root.Elements().FirstOrDefault(e => e.Name.LocalName == tag)?.Value.Trim() ?? string.Empty;
    }
}

/// <summary>Поля <c>meta.xml</c>, которые показывает и правит кокпит.</summary>
/// <param name="Id"><c>&lt;id&gt;</c>.</param>
/// <param name="Version"><c>&lt;version&gt;</c>.</param>
/// <param name="Name"><c>&lt;name&gt;</c>.</param>
/// <param name="Description"><c>&lt;description&gt;</c>; переводы строк — LF, как их отдаёт XML.</param>
public sealed record MetaXmlFields(string Id, string Version, string Name, string Description);

/// <summary>Отказ править <c>meta.xml</c>. Сообщение — для человека, по-русски.</summary>
public sealed class MetaXmlException : Exception
{
    /// <summary>Создаёт исключение.</summary>
    /// <param name="message">Причина отказа.</param>
    public MetaXmlException(string message)
        : base(message)
    {
    }
}
