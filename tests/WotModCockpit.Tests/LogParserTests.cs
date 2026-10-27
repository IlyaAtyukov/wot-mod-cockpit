using System.IO;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="PythonLogParser"/> на строках, нормализованных по образцу реального
/// <c>python.log</c> клиента: формат каждой строки сохранён, а идентификаторы боя, техники
/// и имена сторонних пакетов заменены нейтральными.
/// Сама игра из тестов не читается — только эти константы.
/// </summary>
public class LogParserTests
{
    private readonly ILogParser _parser = new PythonLogParser();

    /// <summary>Выборка строк по образцу реального файла: по одной на каждый встреченный вариант формата.</summary>
    public static readonly string[] RealLines =
    {
        // обрезанная усечением первая строка файла: даты нет, от времени остался хвост
        "1:54.515: INFO: [MOD] mod loaded: mod_infopanel",
        "2026-08-28 01:01:55.680: INFO: [NOTE] package loaded: Example Pack v1.0 (build 1)",
        "2026-08-28 01:02:00.502: INFO: [XFW/CrashFix] init: applying crashfixes",
        "2026-08-28 01:02:01.006: INFO: [XVM/PyMacro] Loading py_macro: [cfg]/py_macro/damage_log.py",
        "2026-08-28 01:02:03.215: INFO: [gui.app_loader.loader] Space is updated: WaitingSpace()",
        "2026-08-28 01:02:03.345: INFO: [Scaleform] Create GUI Component: 'gui/flash/battle.swf'",
        "2026-08-28 01:02:03.468: NOTICE: [NOTE] (scripts/client/game.py, 433) [SPACE] Loading space: spaces/99_poland",
        "2026-08-28 01:02:03.124: INFO: [Avatar] [INIT_STEPS] Avatar.onBecomePlayer: arenaId(1000000000000001)",
        "2026-08-28 01:02:04.965: WARNING: [gui.shared.gui_items.vehicle_equipment] Length of arguments is not valid, args: ((1001, 16, False), (1002, 4, False)) for equipment: _ShellsEquipment, guiItemType: shell, capacity: 3",
        "2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)",
        "2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)",
        "2026-08-28 01:02:46.794: DEBUG: [sentry_sdk.errors] [Tracing] Create new propagation context: {'span_id': '80092cc512dd9da3', 'parent_span_id': None}",
        "2026-08-28 01:16:32.636: ERROR: [Gameface] Accessing a property on a destroyed object ",
        "2026-08-28 01:16:28.890: INFO: =============================",
        "2026-08-28 01:16:28.890: INFO: 2026-08-28 01:16:28: [ERROR] mods/openwg_libraries/xfw/events.pyc",
        "2026-08-28 01:16:28.890: ERROR: Traceback (most recent call last):",
        "2026-08-28 01:16:28.894: ERROR:   File \"res_mods/configs/xvm/py_macro\\xvm\\hitLog.py\", line 953, in _Vehicle_onHealthChanged",
        "2026-08-28 01:16:28.896: ERROR: KeyError: 1001"
    };

    /// <summary>Реальные строки-продолжения (тело traceback) — у них нет собственного таймстампа.</summary>
    public static readonly string[] RealContinuationLines =
    {
        "Traceback (most recent call last):",
        "  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr",
        "  File \"scripts/common/items/__init__.py\", line 419, in parseIntCompactDescr",
        "TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'",
        "    g_dataHitLog.onHealthChanged(self, newHealth, attackerID, attackReasonID)",
        ""
    };

    // ---------------------------------------------------------------- калибровка

    /// <summary>
    /// Ни одна реальная строка с таймстампом не должна потеряться и ни одна не должна уйти
    /// в UNKNOWN. Это калибровочный тест: доля нераспознанных обязана быть нулевой.
    /// </summary>
    [Fact]
    public void Parse_RecognizesEveryRealLine()
    {
        int recognized = 0;
        var failures = new List<string>();

        foreach (string line in RealLines)
        {
            LogEntry? e = _parser.Parse(line);
            if (e is null || e.Level == LogLevels.Unknown)
            {
                failures.Add(line);
                continue;
            }

            recognized++;
        }

        Assert.Empty(failures);
        Assert.Equal(RealLines.Length, recognized);
    }

    /// <summary>Строки тела traceback обязаны опознаваться как продолжение, а не как новые записи.</summary>
    [Fact]
    public void IsContinuation_TrueForTracebackBody()
    {
        foreach (string line in RealContinuationLines)
            Assert.True(_parser.IsContinuation(line), line);

        foreach (string line in RealLines)
        {
            // Единственное исключение — обрезанная усечением первая строка файла:
            // у неё нет полного таймстампа, поэтому формально это «продолжение».
            bool expectedContinuation = line.StartsWith("1:54.515", StringComparison.Ordinal);
            Assert.Equal(expectedContinuation, _parser.IsContinuation(line));
        }
    }

    // ---------------------------------------------------------------- разбор полей

    /// <summary>Канонический разбор строки с блоком <c>(файл, строка)</c>.</summary>
    [Fact]
    public void Parse_NoticeWithSourceBlock()
    {
        const string line =
            "2026-08-28 01:02:03.468: NOTICE: [NOTE] (scripts/client/game.py, 433) [SPACE] Loading space: spaces/99_poland";

        LogEntry e = _parser.Parse(line)!;

        Assert.NotNull(e.Timestamp);
        Assert.Equal(new DateTime(2026, 8, 28, 1, 2, 3, 468), e.Timestamp!.Value.DateTime);
        Assert.Equal("NOTICE", e.Level);
        Assert.Equal(LogSeverity.Info, e.Severity);
        Assert.Equal("NOTE", e.Category);
        Assert.Equal("scripts/client/game.py", e.SourceFile);
        Assert.Equal(433, e.SourceLine);
        Assert.Equal("[SPACE] Loading space: spaces/99_poland", e.Message);
        Assert.Equal(line, e.RawLine);
        Assert.Empty(e.Continuation);
        Assert.False(e.IsMine);
    }

    /// <summary>
    /// Круглые скобки внутри сообщения не должны воровать роль блока <c>(файл, строка)</c>:
    /// как источник считается только ПЕРВЫЙ блок вида «путь.py, число».
    /// </summary>
    [Fact]
    public void Parse_ParenthesesInMessageDoNotBecomeSourceBlock()
    {
        const string line =
            "2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)";

        LogEntry e = _parser.Parse(line)!;

        Assert.Equal("ERROR", e.Level);
        Assert.Equal("ERROR", e.Category);
        Assert.Equal("scripts/common/items/vehicles.py", e.SourceFile);
        Assert.Equal(4851, e.SourceLine);
        Assert.Equal("(compact description to XML mismatch?) (None,)", e.Message);
    }

    /// <summary>Блок <c>(файл, строка)</c> без сообщения — так печатается заголовок EXCEPTION.</summary>
    [Fact]
    public void Parse_ExceptionHeaderHasEmptyMessage()
    {
        LogEntry e = _parser.Parse("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)")!;

        Assert.Equal("EXCEPTION", e.Category);
        Assert.Equal("scripts/common/items/vehicles.py", e.SourceFile);
        Assert.Equal(4850, e.SourceLine);
        Assert.Equal(string.Empty, e.Message);
        Assert.Equal(LogSeverity.Error, e.Severity);
    }

    /// <summary>Строка без блока <c>(файл, строка)</c>: оба поля источника — null.</summary>
    [Fact]
    public void Parse_WithoutSourceBlock()
    {
        LogEntry e = _parser.Parse("2026-08-28 01:02:00.502: INFO: [XFW/CrashFix] init: applying crashfixes")!;

        Assert.Equal("INFO", e.Level);
        Assert.Equal("XFW/CrashFix", e.Category);
        Assert.Null(e.SourceFile);
        Assert.Null(e.SourceLine);
        Assert.Equal("init: applying crashfixes", e.Message);
    }

    /// <summary>Категории бывают точечные и со слэшем — берётся содержимое ПЕРВЫХ квадратных скобок.</summary>
    [Theory]
    [InlineData("2026-08-28 01:02:01.006: INFO: [XVM/PyMacro] Loading py_macro: [cfg]/py_macro/damage_log.py",
        "XVM/PyMacro", "Loading py_macro: [cfg]/py_macro/damage_log.py")]
    [InlineData("2026-08-28 01:02:03.215: INFO: [gui.app_loader.loader] Space is updated: WaitingSpace()",
        "gui.app_loader.loader", "Space is updated: WaitingSpace()")]
    [InlineData("2026-08-28 01:02:03.124: INFO: [Avatar] [INIT_STEPS] Avatar.onBecomePlayer: arenaId(1000000000000001)",
        "Avatar", "[INIT_STEPS] Avatar.onBecomePlayer: arenaId(1000000000000001)")]
    [InlineData("2026-08-28 01:16:32.636: ERROR: [Gameface] Accessing a property on a destroyed object ",
        "Gameface", "Accessing a property on a destroyed object")]
    public void Parse_CategoryVariants(string line, string category, string message)
    {
        LogEntry e = _parser.Parse(line)!;

        Assert.Equal(category, e.Category);
        Assert.Equal(message, e.Message);
    }

    /// <summary>Строка без категории вообще: категория — пустая строка, сообщение целиком.</summary>
    [Theory]
    [InlineData("2026-08-28 01:16:28.890: INFO: =============================", "=============================")]
    [InlineData("2026-08-28 01:16:28.890: INFO: 2026-08-28 01:16:28: [ERROR] mods/openwg_libraries/xfw/events.pyc",
        "2026-08-28 01:16:28: [ERROR] mods/openwg_libraries/xfw/events.pyc")]
    [InlineData("2026-08-28 01:16:28.896: ERROR: KeyError: 1001", "KeyError: 1001")]
    public void Parse_WithoutCategory(string line, string message)
    {
        LogEntry e = _parser.Parse(line)!;

        Assert.Equal(string.Empty, e.Category);
        Assert.Equal(message, e.Message);
    }

    /// <summary>
    /// Первая строка файла после усечения движком обрезана посередине: даты нет,
    /// от времени остался хвост. Разбираем как запись с Timestamp = null.
    /// </summary>
    [Fact]
    public void Parse_TruncatedFirstLine_HasNullTimestampButValidFields()
    {
        LogEntry e = _parser.Parse("1:54.515: INFO: [MOD] mod loaded: mod_infopanel")!;

        Assert.Null(e.Timestamp);
        Assert.Equal("INFO", e.Level);
        Assert.Equal("MOD", e.Category);
        Assert.Equal("mod loaded: mod_infopanel", e.Message);
    }

    /// <summary>Незнакомый уровень не выбрасывается: он кладётся в Level как есть.</summary>
    [Theory]
    [InlineData("CRITICAL")]
    [InlineData("HACK")]
    [InlineData("TRACE")]
    public void Parse_UnknownLevel_IsKeptVerbatim(string level)
    {
        LogEntry e = _parser.Parse($"2026-08-28 01:02:00.502: {level}: [NOTE] что-то случилось")!;

        Assert.Equal(level, e.Level);
        Assert.Equal(LogSeverity.Unknown, e.Severity);
        Assert.Equal("что-то случилось", e.Message);
    }

    /// <summary>Пустая строка и мусор без таймстампа — null, без исключения.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("Traceback (most recent call last):")]
    [InlineData("  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr")]
    [InlineData("TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'")]
    public void Parse_ContinuationOrGarbage_ReturnsNull(string line)
        => Assert.Null(_parser.Parse(line));

    /// <summary>Кириллица в сообщении не должна ломать разбор.</summary>
    [Fact]
    public void Parse_CyrillicMessage()
    {
        LogEntry e = _parser.Parse("2026-08-29 00:27:40.691: NOTICE: [NOTE] (scripts/client/predefined_hosts.py, 782) текст")!;

        Assert.Equal("scripts/client/predefined_hosts.py", e.SourceFile);
        Assert.Equal(782, e.SourceLine);
        Assert.Equal("текст", e.Message);
    }

    // ---------------------------------------------------------------- MarkMine

    private static ModProject Project() => new()
    {
        ProjectDir = @"C:\proj\hello_tanker",
        ModId = "ru.local.hello_tanker",
        Version = "1.0.0",
        EntryPointModule = "mod_hello_tanker",
        PackageName = "hello_tanker",
        LogTag = "[HelloTanker"
    };

    /// <summary>Тег мода в категории или в сообщении делает запись «моей».</summary>
    [Theory]
    [InlineData("2026-08-28 01:02:00.100: INFO: [HelloTanker/loader] mod loaded")]
    [InlineData("2026-08-28 01:02:00.100: INFO: [NOTE] [HelloTanker] hello, tanker!")]
    public void MarkMine_ByTag(string line)
    {
        LogEntry e = _parser.MarkMine(_parser.Parse(line)!, Project());
        Assert.True(e.IsMine);
    }

    /// <summary>Виртуальный путь точки входа и путь внутри пакета мода тоже дают «моё».</summary>
    [Theory]
    [InlineData("scripts/client/gui/mods/mod_hello_tanker.py")]
    [InlineData("scripts/client/gui/mods/hello_tanker/main.py")]
    public void MarkMine_ByVirtualPath(string virtualPath)
    {
        LogEntry e = _parser.Parse($"2026-08-28 01:02:00.100: ERROR: [EXCEPTION] ({virtualPath}, 67)")!;

        Assert.True(_parser.MarkMine(e, Project()).IsMine);
    }

    /// <summary>Кадр в теле traceback, указывающий на файл мода, тоже делает запись «моей».</summary>
    [Fact]
    public void MarkMine_ByTracebackFrameInContinuation()
    {
        LogEntry e = _parser.Parse("2026-08-28 01:02:00.100: ERROR: [EXCEPTION] (scripts/client/gui/mods/__init__.py, 100)")!
            with
        {
            Continuation = new[]
            {
                "Traceback (most recent call last):",
                "  File \"scripts/client/gui/mods/hello_tanker/main.py\", line 42, in _on_avatar_ready",
                "AttributeError: 'NoneType' object has no attribute 'arena'"
            }
        };

        Assert.True(_parser.MarkMine(e, Project()).IsMine);
    }

    /// <summary>Чужая запись «моей» не считается; без проекта и с пустым тегом — тоже.</summary>
    [Fact]
    public void MarkMine_ForeignEntry_StaysFalse()
    {
        LogEntry e = _parser.Parse("2026-08-28 01:02:01.006: INFO: [XVM/PyMacro] Loading py_macro: [cfg]/py_macro/damage_log.py")!;

        Assert.False(_parser.MarkMine(e, Project()).IsMine);
        Assert.False(_parser.MarkMine(e, null).IsMine);

        ModProject noTag = Project();
        noTag.LogTag = string.Empty;
        noTag.EntryPointModule = string.Empty;
        noTag.PackageName = string.Empty;
        Assert.False(_parser.MarkMine(e, noTag).IsMine);
    }

    // ---------------------------------------------------------------- склейка

    /// <summary>
    /// <see cref="LogEntryAssembler"/> обязан приклеить тело traceback к предыдущей записи
    /// и отдать её одной штукой. Фрагмент повторяет по форме кусок реального лога.
    /// </summary>
    [Fact]
    public void Assembler_GluesTracebackToPreviousEntry()
    {
        string[] fragment =
        {
            "2026-08-28 01:02:10.859: INFO: [Scaleform] Create GUI Component: 'gui/flash/battleVehicleMarkersApp.swf'",
            "2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)",
            "Traceback (most recent call last):",
            "  File \"scripts/common/items/vehicles.py\", line 4845, in getItemByCompactDescr",
            "  File \"scripts/common/items/__init__.py\", line 419, in parseIntCompactDescr",
            "TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'",
            "2026-08-28 01:02:11.100: ERROR: [ERROR] (scripts/common/items/vehicles.py, 4851) (compact description to XML mismatch?) (None,)"
        };

        var assembler = new LogEntryAssembler(_parser);
        var entries = new List<LogEntry>();

        foreach (string line in fragment)
            entries.AddRange(assembler.Push(line));

        entries.AddRange(assembler.Flush());

        Assert.Equal(3, entries.Count);
        Assert.Empty(entries[0].Continuation);
        Assert.False(entries[0].HasTraceback);

        Assert.Equal(4, entries[1].Continuation.Count);
        Assert.True(entries[1].HasTraceback);
        Assert.Equal("TypeError: unsupported operand type(s) for &: 'NoneType' and 'int'", entries[1].Continuation[3]);

        Assert.Empty(entries[2].Continuation);
    }

    /// <summary>Обрезанная первая строка файла поднимается сборщиком в самостоятельную запись.</summary>
    [Fact]
    public void Assembler_SalvagesTruncatedFirstLine()
    {
        var assembler = new LogEntryAssembler(_parser);
        var entries = new List<LogEntry>();

        entries.AddRange(assembler.Push("1:54.515: INFO: [MOD] mod loaded: mod_infopanel"));
        entries.AddRange(assembler.Push("2026-08-28 01:01:55.680: INFO: [NOTE] package loaded: Example Pack v1.0 (build 1)"));
        entries.AddRange(assembler.Flush());

        Assert.Equal(2, entries.Count);
        Assert.Null(entries[0].Timestamp);
        Assert.Equal("MOD", entries[0].Category);
        Assert.NotNull(entries[1].Timestamp);
    }

    /// <summary>Байтовые смещения считаются в UTF-8 и включают перевод строки.</summary>
    [Fact]
    public void Assembler_TracksByteOffsets()
    {
        var assembler = new LogEntryAssembler(_parser) { TrackByteOffsets = true };
        var entries = new List<LogEntry>();

        string first = "2026-08-28 01:01:55.680: INFO: [NOTE] a";
        string second = "2026-08-28 01:01:55.681: INFO: [NOTE] b";

        entries.AddRange(assembler.Push(first));
        entries.AddRange(assembler.Push(second));
        entries.AddRange(assembler.Flush());

        Assert.Equal(0, entries[0].ByteOffset);
        Assert.Equal(first.Length + 1, entries[1].ByteOffset);
    }
}
