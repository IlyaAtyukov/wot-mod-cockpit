using WotModCockpit.Core.Preflight;

namespace WotModCockpit.Tests;

/// <summary>
/// Лексическая модель <see cref="PythonLineModel"/>: вырезание литералов, склейка логических
/// операторов, блочная структура. Всё остальное в preflight стоит на ней, поэтому проверяется
/// отдельно от правил.
/// </summary>
public class PreflightModelTests
{
    /// <summary>Содержимое строкового литерала вырезается, а длина строки сохраняется.</summary>
    [Fact]
    public void StringContent_IsBlanked_AndLengthPreserved()
    {
        PythonLineModel model = PythonLineModel.Parse("x = 'BigWorld.quit()'\n");

        PythonPhysicalLine line = model.Lines[0];
        Assert.Equal(line.Raw.Length, line.Code.Length);
        Assert.DoesNotContain("BigWorld", line.Code, StringComparison.Ordinal);
        Assert.Contains("x = '", line.Code, StringComparison.Ordinal);
    }

    /// <summary>Комментарий вырезается целиком.</summary>
    [Fact]
    public void Comment_IsBlanked()
    {
        PythonLineModel model = PythonLineModel.Parse("y = 1  # BigWorld.quit()\n");

        Assert.DoesNotContain("BigWorld", model.Lines[0].Code, StringComparison.Ordinal);
        Assert.Equal("y = 1", model.Lines[0].Trimmed);
    }

    /// <summary>
    /// Код внутри тройного литерала не превращается в операторы. Без этого правила ловили бы
    /// примеры из докстрингов: в эталонном шаблоне их сотни строк.
    /// </summary>
    [Fact]
    public void TripleQuotedBlock_ProducesNoStatements()
    {
        const string source = """
            def f():
                '''ПЛОХО:
                orig = getattr(cls, 'method')
                BigWorld.callback(1.0, f)
                '''
                return 1
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        Assert.DoesNotContain(model.Statements, s => s.Code.Contains("getattr", StringComparison.Ordinal));
        Assert.DoesNotContain(model.Statements, s => s.Code.Contains("BigWorld", StringComparison.Ordinal));
        Assert.Contains(model.Statements, s => s.Trimmed == "return 1");
    }

    /// <summary>Незакрытые скобки склеивают физические строки в один логический оператор.</summary>
    [Fact]
    public void BracketContinuation_JoinsPhysicalLines()
    {
        const string source = "g_eventBus.addListener(events.X,\n                       handler,\n                       scope=1)\n";

        PythonLineModel model = PythonLineModel.Parse(source);

        Assert.Single(model.Statements);
        Assert.Equal(1, model.Statements[0].Line);
        Assert.Equal(3, model.Statements[0].EndLine);
        Assert.Contains("scope=1", model.Statements[0].Code, StringComparison.Ordinal);
    }

    /// <summary>Операторы внутри <c>try:</c> помечены, внутри <c>except</c> — нет.</summary>
    [Fact]
    public void TryBlock_MarksStatements()
    {
        const string source = """
            try:
                import Avatar
            except ImportError:
                Avatar = None
            import BigWorld
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        PythonStatement inTry = model.Statements.Single(s => s.Trimmed == "import Avatar");
        PythonStatement inExcept = model.Statements.Single(s => s.Trimmed == "Avatar = None");
        PythonStatement after = model.Statements.Single(s => s.Trimmed == "import BigWorld");

        Assert.True(inTry.InTry);
        Assert.False(inTry.InExcept);
        Assert.False(inExcept.InTry);
        Assert.True(inExcept.InExcept);
        Assert.False(after.InTry);
        Assert.False(after.InExcept);
    }

    /// <summary>Ветка <c>if &lt;КОНСТАНТА&gt;:</c> распознаётся, обычное условие — нет.</summary>
    [Fact]
    public void ConstantIf_IsDetected()
    {
        const string source = """
            if IS_WG:
                from gui import a
            if config.enabled:
                from gui import b
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        Assert.True(model.Statements.Single(s => s.Trimmed == "from gui import a").InConstantIf);
        Assert.False(model.Statements.Single(s => s.Trimmed == "from gui import b").InConstantIf);
    }

    /// <summary>Определения, декораторы, параметры и границы тела.</summary>
    [Fact]
    public void Definitions_CaptureDecoratorsAndBody()
    {
        const string source = """
            @_safe
            @other.thing(1)
            def init(a, b=2, *args, **kwargs):
                do_work()
                return None

            def fini():
                pass
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        PythonDefinition init = model.Definitions.Single(d => d.Name == "init");
        Assert.Equal(new[] { "_safe", "other.thing" }, init.Decorators);
        Assert.Equal(new[] { "a", "b", "args", "kwargs" }, init.ParameterNames);
        Assert.True(init.IsTopLevel);
        Assert.Contains(model.BodyOf(init), s => s.Trimmed == "do_work()");
        Assert.DoesNotContain(model.BodyOf(init), s => s.Trimmed == "pass");
    }

    /// <summary>Оператор внутри модульного <c>if</c> считается исполняемым при импорте.</summary>
    [Fact]
    public void ModuleLevel_IncludesBodyOfModuleIf()
    {
        const string source = """
            if not _configured:
                configure()

            def f():
                configure()
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        PythonStatement[] calls = model.Statements.Where(s => s.Trimmed == "configure()").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True(calls[0].IsModuleLevel);
        Assert.False(calls[1].IsModuleLevel);
    }

    /// <summary>Объявление кодировки и не-ASCII определяются побайтово, как это делает Python 2.7.</summary>
    [Theory]
    [InlineData("MSG = u'Привет'\n", true, false)]
    [InlineData("# -*- coding: utf-8 -*-\nMSG = u'Привет'\n", true, true)]
    [InlineData("MSG = 'hello'\n", false, false)]
    public void NonAsciiAndCoding_AreDetected(string source, bool nonAscii, bool coding)
    {
        PythonLineModel model = PythonLineModel.Parse(source);

        Assert.Equal(nonAscii, model.HasNonAscii);
        Assert.Equal(coding, model.HasCodingDeclaration);
    }

    /// <summary>Пустой и <c>null</c>-вход не роняют модель.</summary>
    [Fact]
    public void EmptyInput_IsHandled()
    {
        Assert.Empty(PythonLineModel.Parse(null).Statements);
        Assert.Empty(PythonLineModel.Parse(string.Empty).Lines);
    }

    /// <summary>Одноимённые обёртки различаются по месту объявления.</summary>
    [Fact]
    public void FindDefinitionBefore_PicksNearestAbove()
    {
        const string source = """
            def outer_a():
                def wrapper():
                    return 1
                return wrapper

            def outer_b():
                def wrapper():
                    return 2
                return wrapper
            """;

        PythonLineModel model = PythonLineModel.Parse(source);

        PythonDefinition? first = model.FindDefinitionBefore("wrapper", 4);
        PythonDefinition? second = model.FindDefinitionBefore("wrapper", 9);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(2, first!.Line);
        Assert.Equal(7, second!.Line);
    }

    /// <summary>Составное присваивание не считается присваиванием (иначе <c>+=</c> — «монкипатч»).</summary>
    [Theory]
    [InlineData("a = 1", 2)]
    [InlineData("a += 1", -1)]
    [InlineData("a == 1", -1)]
    [InlineData("g_playerEvents.onAvatarReady += handler", -1)]
    [InlineData("f(a=1)", -1)]
    public void IndexOfAssignment_IgnoresAugmentedAndComparisons(string code, int expected)
    {
        Assert.Equal(expected, PreflightSyntax.IndexOfAssignment(code));
    }

    /// <summary>
    /// <see cref="PythonStatement.RawCode"/> совпадает с <see cref="PythonStatement.Code"/>
    /// позиция в позицию, но содержит ЗНАЧЕНИЯ литералов. На этом стоят правила, которым важен
    /// текст строки: режим <c>'w'</c> в <c>open()</c> и приватное имя в <c>setattr()</c>.
    /// </summary>
    [Fact]
    public void RawCode_KeepsLiteralValues_AndStaysAligned()
    {
        PythonLineModel model = PythonLineModel.Parse("f = open('flag.killme', 'w')  # комментарий\n");

        PythonStatement st = Assert.Single(model.Statements);
        Assert.Equal(st.Code.Length, st.RawCode.Length);
        Assert.DoesNotContain("flag.killme", st.Code, StringComparison.Ordinal);
        Assert.Contains("flag.killme", st.RawCode, StringComparison.Ordinal);
        Assert.DoesNotContain("комментарий", st.RawCode, StringComparison.Ordinal);

        int open = PreflightSyntax.FindCall(st.Code, "open", allowMethodReceiver: false);
        IReadOnlyList<string> args = PreflightSyntax.SplitArgumentsRaw(st.Code, st.RawCode, open);

        Assert.Equal(2, args.Count);
        Assert.Equal("'flag.killme'", args[0]);
        Assert.Equal("'w'", args[1]);
    }

    /// <summary>Позиции склеенного многострочного вызова тоже совпадают.</summary>
    [Fact]
    public void RawCode_StaysAligned_AcrossJoinedLines()
    {
        PythonLineModel model = PythonLineModel.Parse("setattr(cls,\n        '__rotate',\n        wrapper)\n");

        PythonStatement st = Assert.Single(model.Statements);
        Assert.Equal(st.Code.Length, st.RawCode.Length);

        int open = PreflightSyntax.FindCall(st.Code, "setattr", allowMethodReceiver: false);
        IReadOnlyList<string> args = PreflightSyntax.SplitArgumentsRaw(st.Code, st.RawCode, open);

        Assert.Equal(3, args.Count);
        Assert.Equal("'__rotate'", args[1]);
    }

    /// <summary>Корневые идентификаторы не включают то, что стоит после точки.</summary>
    [Fact]
    public void RootIdentifiers_SkipAttributeChains()
    {
        string[] roots = PreflightSyntax.RootIdentifiers("player.arena.vehicles.get(x)").Select(t => t.Name).ToArray();

        Assert.Equal(new[] { "player", "x" }, roots);
    }
}
