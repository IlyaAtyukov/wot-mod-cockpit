using System.Text;
using System.Xml.Linq;
using WotModCockpit.Core.Mods;
using WotModCockpit.Core.Project;

namespace WotModCockpit.Tests;

/// <summary>
/// Правка имени и описания в <c>src\meta.xml</c> (<see cref="MetaXmlEditor"/>).
///
/// <para><b>Главное свойство — побайтовое.</b> Файл после правки обязан совпасть с исходным во
/// ВСЁМ, кроме текстов двух элементов: декларация, BOM, переводы строк, отступы, комментарии,
/// прочие элементы. Готовый писатель кокпита (<c>SyncMetaXml</c>) этого не держит — он меняет
/// регистр кодировки в декларации, теряет BOM и CRLF, а битый файл молча заменяет пустым
/// корнем. Поэтому здесь сравниваются не «значения прочитались», а байты целиком.</para>
///
/// <para>Все записи — во временный каталог. Настоящие проекты (только при
/// <c>WOTCOCKPIT_REAL_MACHINE=1</c>) только читаются и копируются.</para>
/// </summary>
public sealed class MetaXmlEditorTests : IDisposable
{
    /// <summary>Типичный meta.xml проекта, созданного из шаблона: LF, без BOM, отступ 2 пробела.</summary>
    private const string Canonical =
        "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
        "<root>\n" +
        "  <id>ru.example.meta_demo</id>\n" +
        "  <version>1.0.0</version>\n" +
        "  <!-- комментарий обязан пережить правку -->\n" +
        "  <name>Demo Mod</name>\n" +
        "  <description>Описание демо.</description>\n" +
        "</root>\n";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "metaxml-" + Guid.NewGuid().ToString("N"));

    public MetaXmlEditorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // временный каталог не критичен
        }
    }

    /// <summary>ГЛАВНАЯ ПРОВЕРКА: меняются тексты двух элементов, и больше НИ ОДНОГО байта.</summary>
    [Fact]
    public void Write_ChangesOnlyTheTwoTexts_EveryOtherByteStays()
    {
        string path = File(Canonical);

        MetaXmlEditor.Write(path, "Демо-мод", "Новое описание демо.");

        string expected = Canonical
            .Replace("<name>Demo Mod</name>", "<name>Демо-мод</name>")
            .Replace("<description>Описание демо.</description>", "<description>Новое описание демо.</description>");
        Assert.Equal(Encoding.UTF8.GetBytes(expected), System.IO.File.ReadAllBytes(path));
    }

    /// <summary>BOM и CRLF остаются, а декларация — в том же регистре, что была.</summary>
    [Fact]
    public void Write_KeepsBomCrLfAndTheDeclarationAsTheyWere()
    {
        string crlf = Canonical.Replace("\n", "\r\n");
        string path = File(crlf, bom: true);

        MetaXmlEditor.Write(path, "Новое имя", "Строка один\nстрока два");

        byte[] bytes = System.IO.File.ReadAllBytes(path);
        Assert.True(bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }), "BOM потерян");
        string text = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty));
        Assert.Contains("<description>Строка один\r\nстрока два</description>", text);
    }

    /// <summary>Переводы строк в описании приводятся к стилю ФАЙЛА, а не того, кто печатал.</summary>
    [Fact]
    public void Write_MultiLineDescription_TakesTheFilesNewLine()
    {
        string path = File(Canonical);

        MetaXmlEditor.Write(path, "Имя", "первая\r\nвторая\rтретья");

        string text = System.IO.File.ReadAllText(path);
        Assert.Contains("<description>первая\nвторая\nтретья</description>", text);
        Assert.DoesNotContain("\r", text);
    }

    /// <summary>Разметка в тексте экранируется, и прочитанное совпадает с написанным.</summary>
    [Fact]
    public void Write_EscapesMarkup_AndReadsBackTheSameText()
    {
        string path = File(Canonical);

        MetaXmlEditor.Write(path, "A & B <x>", "if a > b && c < d");

        string text = System.IO.File.ReadAllText(path);
        Assert.Contains("<name>A &amp; B &lt;x&gt;</name>", text);
        MetaXmlFields read = MetaXmlEditor.Read(path);
        Assert.Equal("A & B <x>", read.Name);
        Assert.Equal("if a > b && c < d", read.Description);
    }

    /// <summary>
    /// Битый файл не правится, и на диске он остаётся ровно тем, что был.
    /// <para>
    /// Готовый писатель кокпита в этом месте заменял файл пустым корнем — всё содержимое
    /// пропадало молча. Здесь отказ с объяснением.
    /// </para>
    /// </summary>
    [Fact]
    public void Write_BrokenXml_IsRefused_AndTheFileStaysAsItWas()
    {
        string broken = Canonical.Replace("</name>", "</nme>");
        string path = File(broken);
        byte[] before = System.IO.File.ReadAllBytes(path);

        var ex = Assert.Throws<MetaXmlException>(() => MetaXmlEditor.Write(path, "Имя", "Описание"));

        Assert.Contains("битый", ex.Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
    }

    /// <summary>Файл в другой кодировке не правится: запись в UTF-8 испортила бы все буквы.</summary>
    [Fact]
    public void Write_NonUtf8Declaration_IsRefused()
    {
        string path = File(Canonical.Replace("encoding=\"UTF-8\"", "encoding=\"windows-1251\""));
        byte[] before = System.IO.File.ReadAllBytes(path);

        var ex = Assert.Throws<MetaXmlException>(() => MetaXmlEditor.Write(path, "Имя", "Описание"));

        Assert.Contains("windows-1251", ex.Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
    }

    /// <summary>Элемент в другом регистре build.py не видит — править «рядом» с ним нельзя.</summary>
    [Fact]
    public void Write_ElementInAnotherCase_IsRefused()
    {
        string path = File(Canonical.Replace("<name>Demo Mod</name>", "<Name>Demo Mod</Name>"));

        var ex = Assert.Throws<MetaXmlException>(() => MetaXmlEditor.Write(path, "Имя", "Описание"));

        Assert.Contains("<Name>", ex.Message);
    }

    /// <summary>Недостающие элементы дописываются после последнего, с тем же отступом и переводом строки.</summary>
    [Fact]
    public void Write_MissingElements_AreAddedWithTheSameIndent()
    {
        string path = File(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<root>\n" +
            "  <id>ru.local.x</id>\n" +
            "  <version>1.0.0</version>\n" +
            "</root>\n");

        MetaXmlEditor.Write(path, "Икс", "Описание икса");

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<root>\n" +
            "  <id>ru.local.x</id>\n" +
            "  <version>1.0.0</version>\n" +
            "  <name>Икс</name>\n" +
            "  <description>Описание икса</description>\n" +
            "</root>\n",
            System.IO.File.ReadAllText(path));
    }

    /// <summary>Пустой элемент вида <c>&lt;description/&gt;</c> раскрывается и получает текст.</summary>
    [Fact]
    public void Write_SelfClosingElement_IsExpanded()
    {
        string path = File(Canonical.Replace("<description>Описание демо.</description>", "<description/>"));

        MetaXmlEditor.Write(path, "Имя", "Теперь есть");

        Assert.Contains("<description>Теперь есть</description>", System.IO.File.ReadAllText(path));
    }

    // ================================================================= только изменённое

    /// <summary>
    /// <c>null</c> — поле не трогается НИ НА БАЙТ. Раньше писались оба поля всегда, и
    /// комментарий внутри нетронутого поля пропадал молча.
    /// </summary>
    [Fact]
    public void Write_NullField_IsNotTouched_EvenWithACommentInside()
    {
        string withComment = Canonical.Replace("<name>Demo Mod</name>", "<name>Demo Mod<!-- рабочее --></name>");
        string path = File(withComment);

        Assert.True(MetaXmlEditor.Write(path, null, "Новое описание"));

        Assert.Equal(
            Encoding.UTF8.GetBytes(withComment.Replace("<description>Описание демо.</description>", "<description>Новое описание</description>")),
            System.IO.File.ReadAllBytes(path));
    }

    /// <summary>
    /// Правка, сделанная в редакторе, пока окно кокпита было открыто, не откатывается: окно
    /// меняло только имя, и описание из файла остаётся тем, что туда записал редактор.
    /// </summary>
    [Fact]
    public void Write_ChangeMadeInTheEditorMeanwhile_IsNotRolledBack()
    {
        string path = File(Canonical);
        MetaXmlFields snapshot = MetaXmlEditor.Read(path);

        // Пока окно открыто, в VS Code поправили описание.
        System.IO.File.WriteAllText(path, Canonical.Replace("Описание демо.", "Поправлено в редакторе."), new UTF8Encoding(false));

        // Окно меняло только имя; описание в нём — старый снимок, и оно в файл не уходит.
        Assert.Equal("Описание демо.", snapshot.Description);
        MetaXmlEditor.Write(path, "Демо-мод", null);

        MetaXmlFields after = MetaXmlEditor.Read(path);
        Assert.Equal("Демо-мод", after.Name);
        Assert.Equal("Поправлено в редакторе.", after.Description);
    }

    /// <summary>Значение, которое уже стоит в файле, не пишется: файл не трогается вовсе.</summary>
    [Fact]
    public void Write_SameValues_WritesNothing()
    {
        string path = File(Canonical);
        byte[] before = System.IO.File.ReadAllBytes(path);
        DateTime stamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        System.IO.File.SetLastWriteTimeUtc(path, stamp);

        bool written = MetaXmlEditor.Write(path, "  Demo Mod ", "Описание демо.");

        Assert.False(written);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
        Assert.Equal(stamp, System.IO.File.GetLastWriteTimeUtc(path));
    }

    /// <summary>
    /// Поле, внутри которого есть комментарий, не правится: в окне его не видно, и новый текст
    /// удалил бы его молча. Отказ с объяснением, файл цел.
    /// </summary>
    [Fact]
    public void Write_FieldWithACommentInside_IsRefused()
    {
        string path = File(Canonical.Replace("<name>Demo Mod</name>", "<name>Demo Mod<!-- рабочее --></name>"));
        byte[] before = System.IO.File.ReadAllBytes(path);

        var ex = Assert.Throws<MetaXmlException>(() => MetaXmlEditor.Write(path, "Демо-мод", null));

        Assert.Contains("комментарий", ex.Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
    }

    // ================================================================= пустой корень

    /// <summary>
    /// Корень без детей: элементы встают каждый на свою строку, <c>&lt;/root&gt;</c> — на свою.
    /// Раньше <c>&lt;root/&gt;</c> ронял правку исключением «индекс вне диапазона».
    /// </summary>
    [Theory]
    [InlineData("<root/>")]
    [InlineData("<root />")]
    [InlineData("<root></root>")]
    [InlineData("<root>\n</root>")]
    [InlineData("<root>   </root>")]
    public void Write_EmptyRoot_GetsTheElementsOnTheirOwnLines(string root)
    {
        string path = File("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + root + "\n");

        Assert.True(MetaXmlEditor.Write(path, "Икс", "Описание икса"));

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<root>\n" +
            "  <name>Икс</name>\n" +
            "  <description>Описание икса</description>\n" +
            "</root>\n",
            System.IO.File.ReadAllText(path));
    }

    /// <summary>Раскрытый корень сохраняет атрибуты и переводы строк файла (CRLF).</summary>
    [Fact]
    public void Write_SelfClosingRootWithAttributes_KeepsThemAndCrLf()
    {
        string path = File("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<root kind=\"mod\"/>\r\n");

        MetaXmlEditor.Write(path, "Икс", null);

        Assert.Equal(
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n<root kind=\"mod\">\r\n  <name>Икс</name>\r\n</root>\r\n",
            System.IO.File.ReadAllText(path));
    }

    /// <summary>Комментарий в пустом корне остаётся, элемент встаёт после него.</summary>
    [Fact]
    public void Write_RootWithOnlyAComment_KeepsItAndAddsAfter()
    {
        string path = File("<root>\n  <!-- заготовка -->\n</root>\n");

        MetaXmlEditor.Write(path, "Икс", null);

        Assert.Equal("<root>\n  <!-- заготовка -->\n  <name>Икс</name>\n</root>\n", System.IO.File.ReadAllText(path));
    }

    /// <summary>
    /// Самозакрытый элемент с префиксом закрывается тем же префиксом: <c>&lt;m:name/&gt;</c> →
    /// <c>&lt;m:name&gt;…&lt;/m:name&gt;</c>, а не битое <c>&lt;/name&gt;</c>.
    /// </summary>
    [Fact]
    public void Write_PrefixedSelfClosingElement_ClosesWithThePrefix()
    {
        string path = File("<root xmlns:m=\"urn:x\">\n  <m:name/>\n</root>\n");

        MetaXmlEditor.Write(path, "Икс", null);

        Assert.Equal("<root xmlns:m=\"urn:x\">\n  <m:name>Икс</m:name>\n</root>\n", System.IO.File.ReadAllText(path));
        Assert.Equal("Икс", MetaXmlEditor.Read(path).Name);
    }

    /// <summary>Негодные значения отбиваются ДО того, как файл открыт на запись.</summary>
    [Theory]
    [InlineData("", "пустое имя")]
    [InlineData("   ", "имя из пробелов")]
    [InlineData("две\nстроки", "перевод строки в имени")]
    [InlineData("звонок\u0007", "управляющий символ")]
    public void Write_BadName_IsRefused_AndTheFileStaysAsItWas(string name, string why)
    {
        string path = File(Canonical);
        byte[] before = System.IO.File.ReadAllBytes(path);

        Assert.Throws<MetaXmlException>(() => MetaXmlEditor.Write(path, name, "Описание"));

        Assert.True(before.AsSpan().SequenceEqual(System.IO.File.ReadAllBytes(path)), $"файл изменился: {why}");
    }

    /// <summary>Слишком длинное имя и описание отбиваются с понятной причиной.</summary>
    [Fact]
    public void Validation_TooLong_HasAReason()
    {
        Assert.NotNull(MetaXmlEditor.ValidateName(new string('я', MetaXmlEditor.MaxNameLength + 1)));
        Assert.Null(MetaXmlEditor.ValidateName(new string('я', MetaXmlEditor.MaxNameLength)));
        Assert.NotNull(MetaXmlEditor.ValidateDescription(new string('я', MetaXmlEditor.MaxDescriptionLength + 1)));
        Assert.Null(MetaXmlEditor.ValidateDescription(string.Empty));
    }

    /// <summary>Атомарная запись не оставляет рядом временных файлов.</summary>
    [Fact]
    public void Write_LeavesNoTemporaryFilesBehind()
    {
        string path = File(Canonical);

        MetaXmlEditor.Write(path, "Имя", "Описание");

        Assert.Equal(["meta.xml"], Directory.GetFiles(_dir).Select(file => Path.GetFileName(file)).ToArray());
    }

    /// <summary>
    /// Результат видят ОБЕ стороны: и build.py (прямой ребёнок корня, с учётом регистра), и
    /// сканер кокпита, который строит список «Моды».
    /// </summary>
    [Fact]
    public void Write_TheNewValues_AreWhatBuildPyAndTheScannerSee()
    {
        string project = Path.Combine(_dir, "wot-mod-x");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        string path = Path.Combine(project, "src", "meta.xml");
        System.IO.File.WriteAllText(path, Canonical, new UTF8Encoding(false));

        MetaXmlEditor.Write(path, "Демо-мод", "Новое описание демо.");

        // Как build.py: root.find('name').text.strip()
        XElement root = XDocument.Load(path).Root!;
        Assert.Equal("Демо-мод", root.Element("name")!.Value.Trim());

        ModProjectInfo scanned = new ModScanner().ReadProjects([project]).Single();
        Assert.Equal("Демо-мод", scanned.DisplayName);
        Assert.Equal("Новое описание демо.", scanned.Description);
    }

    /// <summary>
    /// На КОПИЯХ настоящих meta.xml своих проектов (<c>WOTCOCKPIT_REPOS_DIR</c>) правка меняет
    /// ровно две строки. Сами проекты только читаются.
    /// </summary>
    [SkippableFact]
    [Trait("Env", "RealMachine")]
    public void Write_OnCopiesOfTheRealProjects_ChangesExactlyTwoLines()
    {
        string reposDir = TestEnvironment.RequireReposDir();
        string[] real = Directory.GetDirectories(reposDir, "wot-mod-*")
            .Select(dir => Path.Combine(dir, "src", "meta.xml"))
            .Where(System.IO.File.Exists)
            .ToArray();
        Skip.If(real.Length == 0, $"в {reposDir} нет проектов с src\\meta.xml");

        foreach (string original in real)
        {
            string copy = Path.Combine(_dir, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(original))!) + ".meta.xml");
            System.IO.File.Copy(original, copy);
            string[] before = System.IO.File.ReadAllLines(copy);

            MetaXmlEditor.Write(copy, "Проверочное имя", "Проверочное описание");

            string[] after = System.IO.File.ReadAllLines(copy);
            Assert.Equal(before.Length, after.Length);
            string[] changed = before.Where((line, i) => line != after[i]).ToArray();
            Assert.True(changed.Length is >= 1 and <= 2,
                $"{original}: изменилось строк {changed.Length}: {string.Join(" | ", changed)}");
            Assert.All(changed, line => Assert.Matches(@"<(name|description)[ >/]", line));
        }
    }

    private string File(string content, bool bom = false)
    {
        string path = Path.Combine(_dir, "meta.xml");
        byte[] body = Encoding.UTF8.GetBytes(content);
        System.IO.File.WriteAllBytes(path, bom ? [0xEF, 0xBB, 0xBF, .. body] : body);
        return path;
    }
}
