using System.Diagnostics;
using System.IO;
using System.Text;
using WotModCockpit.Core.Abstractions;
using WotModCockpit.Core.Services;

namespace WotModCockpit.Tests;

/// <summary>
/// Тесты <see cref="PollingLogTailer"/> на временном файле: дозапись, УСЕЧЕНИЕ (движок режет
/// python.log с 2048 КБ до 512 КБ), пересоздание файла, отсутствие файла на старте,
/// прогрев хвостом и совместный доступ на запись.
/// <para>Игра здесь не участвует: настоящую папку игры тесты не трогают вообще.</para>
/// </summary>
public class LogTailerTests : IDisposable
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly string _dir;
    private readonly string _log;

    /// <summary>Готовит временный каталог под лог.</summary>
    public LogTailerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wot-cockpit-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "python.log");
    }

    /// <summary>Убирает временный каталог.</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // файл мог остаться открытым у ОС — тест это не должно ронять
        }

        GC.SuppressFinalize(this);
    }

    // ---------------------------------------------------------------- дозапись

    /// <summary>Дозапись в конец файла: новые строки должны приехать в LineRead в порядке файла.</summary>
    [Fact]
    public void Append_DeliversNewLinesInOrder()
    {
        Write("2026-08-28 01:01:55.680: INFO: [NOTE] стартовая строка\n");

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };

        tailer.Start(primeTailBytes: 0);              // только новое
        Assert.True(tailer.IsRunning);

        Append("2026-08-28 01:02:00.502: INFO: [XFW/CrashFix] init: applying crashfixes\n");
        Append("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)\n");

        WaitFor(() => Count(lines) >= 2, lines);

        lock (lines)
        {
            Assert.Equal(2, lines.Count);
            Assert.Equal("2026-08-28 01:02:00.502: INFO: [XFW/CrashFix] init: applying crashfixes", lines[0]);
            Assert.Equal("2026-08-28 01:02:11.100: ERROR: [EXCEPTION] (scripts/common/items/vehicles.py, 4850)", lines[1]);
        }
    }

    /// <summary>Хвост без перевода строки не отдаётся, пока строка не дописана целиком.</summary>
    [Fact]
    public void PartialLine_IsNotDeliveredUntilNewline()
    {
        Write(string.Empty);

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 0);

        Append("2026-08-28 01:02:00.502: INFO: [NOTE] нед");
        Thread.Sleep(150);
        Assert.Equal(0, Count(lines));

        Append("описанная строка\n");
        WaitFor(() => Count(lines) >= 1, lines);

        lock (lines)
            Assert.Equal("2026-08-28 01:02:00.502: INFO: [NOTE] недописанная строка", lines[0]);
    }

    /// <summary>Разделители <c>\r\n</c> тоже режутся корректно: <c>\r</c> в строку не попадает.</summary>
    [Fact]
    public void CrLf_IsStripped()
    {
        Write(string.Empty);

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 0);

        Append("первая\r\nвторая\r\n");
        WaitFor(() => Count(lines) >= 2, lines);

        lock (lines)
        {
            Assert.Equal("первая", lines[0]);
            Assert.Equal("вторая", lines[1]);
        }
    }

    // ---------------------------------------------------------------- усечение

    /// <summary>
    /// ГЛАВНЫЙ тест: движок обрезает python.log (2048 КБ → 512 КБ). Длина файла становится
    /// МЕНЬШЕ прочитанного смещения — тейлер обязан поднять FileTruncated и читать с нуля,
    /// а не выдавать мусор и не зависать.
    /// </summary>
    [Fact]
    public void Truncation_RaisesEventAndRereadsFromStart()
    {
        // 1. Наполняем файл «старым» содержимым и дочитываем его до конца.
        var sb = new StringBuilder();
        for (int i = 0; i < 400; i++)
            sb.Append("2026-08-28 01:02:").Append((i % 60).ToString("00")).Append(".000: INFO: [NOTE] старая строка ").Append(i).Append('\n');

        Write(sb.ToString());

        var lines = new List<string>();
        int truncations = 0;
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.FileTruncated += (_, _) => Interlocked.Increment(ref truncations);

        tailer.Start(primeTailBytes: 0);
        Append("2026-08-28 01:03:00.000: INFO: [NOTE] последняя старая\n");
        WaitFor(() => Count(lines) >= 1, lines);

        long positionBefore = tailer.Position;
        Assert.True(positionBefore > 0);

        lock (lines) lines.Clear();

        // 2. Усечение: файл переписывается коротким «хвостом», как это делает движок.
        TruncateTo("2026-08-28 01:04:00.000: INFO: [NOTE] после усечения 1\n");

        WaitFor(() => Volatile.Read(ref truncations) >= 1 && Count(lines) >= 1, lines);

        Assert.True(truncations >= 1, "FileTruncated не поднялось");
        lock (lines)
            Assert.Equal("2026-08-28 01:04:00.000: INFO: [NOTE] после усечения 1", lines[0]);

        // 3. После усечения дозапись продолжает работать.
        Append("2026-08-28 01:04:01.000: INFO: [NOTE] после усечения 2\n");
        WaitFor(() => Count(lines) >= 2, lines);

        lock (lines)
            Assert.Equal("2026-08-28 01:04:01.000: INFO: [NOTE] после усечения 2", lines[1]);
    }

    /// <summary>
    /// Движок не выбрасывает лог целиком, а оставляет его ПОСЛЕДНИЕ 512 КБ, то есть в файле
    /// остаются строки, которые потребитель уже видел. Тейлер обязан отдать после усечения
    /// только НОВОЕ, иначе лента получает несколько тысяч дубликатов прямо под разделителем.
    /// </summary>
    [Fact]
    public void EngineStyleTruncation_DoesNotRepeatAlreadyShownLines()
    {
        Write(string.Empty);

        var lines = new List<string>();
        int truncations = 0;
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.FileTruncated += (_, _) => Interlocked.Increment(ref truncations);
        tailer.Start(primeTailBytes: 0);

        // 1. Десять строк, все показаны пользователю.
        var all = new List<string>();
        for (int i = 1; i <= 10; i++)
            all.Add($"2026-08-28 01:0{i / 10}:{i % 10:0}0.000: INFO: [NOTE] строка {i}");

        Append(string.Join("\n", all) + "\n");
        WaitFor(() => Count(lines) >= 10, lines);

        // 2. Движок режет файл, оставляя ПОСЛЕДНИЕ пять строк — они уже были на экране.
        TruncateTo(string.Join("\n", all.Skip(5)) + "\n");
        WaitFor(() => Volatile.Read(ref truncations) >= 1, lines);
        Thread.Sleep(200);                                   // даём шанс приехать дубликатам

        List<string> afterTruncation;
        lock (lines)
            afterTruncation = lines.Skip(10).ToList();

        Assert.True(
            afterTruncation.Count == 0,
            "после усечения повторно отданы уже показанные строки: " + string.Join(" | ", afterTruncation));

        Assert.Equal(5, tailer.LastTruncationSkippedLines);

        // 3. То, что дописано ПОСЛЕ усечения, приезжает как обычно и ровно один раз.
        Append("2026-08-28 01:11:00.000: INFO: [NOTE] строка 11\n");
        WaitFor(() => Count(lines) >= 11, lines);
        Thread.Sleep(120);

        lock (lines)
        {
            Assert.Equal(11, lines.Count);
            Assert.Equal("2026-08-28 01:11:00.000: INFO: [NOTE] строка 11", lines[^1]);
        }
    }

    /// <summary>
    /// Подмена файла целиком: содержимого старого лога в новом нет, поэтому отсекать нечего —
    /// отдаётся всё, и ни одна строка не теряется.
    /// </summary>
    [Fact]
    public void Truncation_WithCompletelyNewContent_EmitsEverything()
    {
        // Старое содержимое заведомо длиннее нового: иначе усечение вообще не детектируется
        // (длина файла не уменьшилась) — как и у движка, режущего 2048 КБ до 512 КБ.
        var sb = new StringBuilder();
        for (int i = 0; i < 100; i++)
            sb.Append("2026-08-28 01:01:00.000: INFO: [NOTE] старое содержимое ").Append(i).Append('\n');

        Write(sb.ToString());

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 1024 * 1024);
        WaitFor(() => Count(lines) >= 100, lines);

        int primed = Count(lines);

        TruncateTo("2026-08-28 02:00:00.000: INFO: [NOTE] совсем другое 1\n" +
                   "2026-08-28 02:00:01.000: INFO: [NOTE] совсем другое 2\n");

        WaitFor(() => Count(lines) >= primed + 2, lines);

        lock (lines)
        {
            Assert.Equal("2026-08-28 02:00:00.000: INFO: [NOTE] совсем другое 1", lines[primed]);
            Assert.Equal("2026-08-28 02:00:01.000: INFO: [NOTE] совсем другое 2", lines[primed + 1]);
        }

        Assert.Equal(0, tailer.LastTruncationSkippedLines);
    }

    /// <summary>Файл удалили и создали заново — реакция та же, что на усечение.</summary>
    [Fact]
    public void Recreation_IsHandledLikeTruncation()
    {
        Write("2026-08-28 01:01:00.000: INFO: [NOTE] до пересоздания раз\n" +
              "2026-08-28 01:01:01.000: INFO: [NOTE] до пересоздания два\n");

        var lines = new List<string>();
        int truncations = 0;
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.FileTruncated += (_, _) => Interlocked.Increment(ref truncations);

        tailer.Start(primeTailBytes: 0);
        Append("2026-08-28 01:01:02.000: INFO: [NOTE] хвост\n");
        WaitFor(() => Count(lines) >= 1, lines);
        lock (lines) lines.Clear();

        File.Delete(_log);

        // Ждём, чтобы такт опроса ЗАМЕТИЛ пропажу файла: именно этот путь раньше молча
        // сбрасывал состояние без FileTruncated, и потребитель не узнавал, что смещения
        // всех ранее выданных записей стали недействительными.
        Thread.Sleep(150);
        Write("2026-08-28 02:00:00.000: INFO: [NOTE] новый файл\n");

        WaitFor(() => Count(lines) >= 1, lines);

        lock (lines)
            Assert.Equal("2026-08-28 02:00:00.000: INFO: [NOTE] новый файл", lines[0]);

        WaitFor(() => Volatile.Read(ref truncations) >= 1, lines);
        Assert.True(truncations >= 1, "подмена файла не подняла FileTruncated — нарушен контракт ILogTailer");
    }

    // ---------------------------------------------------------------- прогрев и отсутствие файла

    /// <summary>
    /// Прогрев хвостом: читаются последние N байт, а первая (заведомо обрезанная посередине)
    /// строка отбрасывается — ровно так же движок оставляет огрызок после усечения.
    /// </summary>
    [Fact]
    public void Prime_ReadsTailAndDropsFirstCutLine()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < 200; i++)
            sb.Append("2026-08-28 01:02:03.000: INFO: [NOTE] строка номер ").Append(i).Append('\n');

        Write(sb.ToString());

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };

        tailer.Start(primeTailBytes: 300);            // заведомо меньше файла

        int primed = Count(lines);
        Assert.InRange(primed, 1, 20);

        lock (lines)
        {
            // Первая отданная строка — целая, а не обрывок.
            Assert.StartsWith("2026-08-28 01:02:03.000: INFO:", lines[0], StringComparison.Ordinal);
            Assert.EndsWith("строка номер 199", lines[^1], StringComparison.Ordinal);
        }
    }

    /// <summary>Прогрев с primeTailBytes = 0 не отдаёт ничего из уже написанного.</summary>
    [Fact]
    public void Prime_Zero_ReadsOnlyNew()
    {
        Write("2026-08-28 01:01:00.000: INFO: [NOTE] старое\n");

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 0);

        Thread.Sleep(150);
        Assert.Equal(0, Count(lines));
    }

    /// <summary>Файла нет на старте — не ошибка: тейлер ждёт его появления.</summary>
    [Fact]
    public void MissingFileAtStart_WaitsForCreation()
    {
        Assert.False(File.Exists(_log));

        var lines = new List<string>();
        var errors = new List<Exception>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Error += (_, e) => { lock (errors) errors.Add(e); };

        tailer.Start();                               // с прогревом по умолчанию
        Assert.True(tailer.IsRunning);
        Thread.Sleep(100);

        Write("2026-08-28 01:01:00.000: INFO: [NOTE] игра наконец запустилась\n");
        WaitFor(() => Count(lines) >= 1, lines);

        lock (lines)
            Assert.Equal("2026-08-28 01:01:00.000: INFO: [NOTE] игра наконец запустилась", lines[0]);

        lock (errors)
            Assert.Empty(errors);
    }

    // ---------------------------------------------------------------- совместный доступ

    /// <summary>
    /// Пока тейлер следит за файлом, писатель обязан спокойно держать его открытым на запись:
    /// без FileShare.ReadWrite | FileShare.Delete игра не смогла бы писать лог.
    /// </summary>
    [Fact]
    public void Tailing_DoesNotBlockWriter()
    {
        Write(string.Empty);

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 0);

        // Держим файл открытым на запись, как это делает движок (openMode 'a').
        using (var writer = new FileStream(_log, FileMode.Append, FileAccess.Write,
                   FileShare.ReadWrite | FileShare.Delete))
        using (var sw = new StreamWriter(writer, new UTF8Encoding(false)))
        {
            sw.AutoFlush = true;
            sw.Write("2026-08-28 01:02:00.000: INFO: [NOTE] пишем при живом тейлере\n");
            WaitFor(() => Count(lines) >= 1, lines);
        }

        lock (lines)
            Assert.Equal("2026-08-28 01:02:00.000: INFO: [NOTE] пишем при живом тейлере", lines[0]);
    }

    /// <summary>Битые байты не роняют чтение — подставляется U+FFFD, а строка доезжает.</summary>
    [Fact]
    public void InvalidUtf8_DoesNotThrow()
    {
        Write(string.Empty);

        var lines = new List<string>();
        var errors = new List<Exception>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Error += (_, e) => { lock (errors) errors.Add(e); };
        tailer.Start(primeTailBytes: 0);

        var bytes = new List<byte>();
        bytes.AddRange(Encoding.UTF8.GetBytes("2026-08-28 01:02:00.000: INFO: [NOTE] "));
        bytes.AddRange(new byte[] { 0xC3, 0x28, 0xFF, 0xFE });      // заведомо невалидный UTF-8
        bytes.AddRange(Encoding.UTF8.GetBytes(" хвост\n"));
        using (var fs = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            fs.Write(bytes.ToArray(), 0, bytes.Count);

        WaitFor(() => Count(lines) >= 1, lines);

        lock (lines)
        {
            Assert.StartsWith("2026-08-28 01:02:00.000: INFO: [NOTE] ", lines[0], StringComparison.Ordinal);
            Assert.EndsWith(" хвост", lines[0], StringComparison.Ordinal);
        }

        lock (errors)
            Assert.Empty(errors);
    }

    /// <summary>Многобайтовый символ, разрезанный границей чтения, не должен превращаться в мусор.</summary>
    [Fact]
    public void MultiByteCharSplitAcrossReads_IsDecodedCorrectly()
    {
        Write(string.Empty);

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };
        tailer.Start(primeTailBytes: 0);

        byte[] cyrillic = Encoding.UTF8.GetBytes("привет\n");
        using (var fs = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.Write(cyrillic, 0, 3);                 // рвём ровно посреди двухбайтовой буквы
            fs.Flush();
            Thread.Sleep(120);                        // тейлер успевает прочитать половину символа
            fs.Write(cyrillic, 3, cyrillic.Length - 3);
            fs.Flush();
        }

        WaitFor(() => Count(lines) >= 1, lines);

        lock (lines)
            Assert.Equal("привет", lines[0]);
    }

    // ---------------------------------------------------------------- жизненный цикл

    /// <summary>Start/Stop идемпотентны, после Stop события не приходят.</summary>
    [Fact]
    public void StartStop_AreIdempotent()
    {
        Write(string.Empty);

        var lines = new List<string>();
        using var tailer = new PollingLogTailer(_log, Poll);
        tailer.LineRead += (_, l) => { lock (lines) lines.Add(l); };

        tailer.Start(primeTailBytes: 0);
        tailer.Start(primeTailBytes: 0);
        Assert.True(tailer.IsRunning);

        tailer.Stop();
        tailer.Stop();
        Assert.False(tailer.IsRunning);

        Append("2026-08-28 01:02:00.000: INFO: [NOTE] после остановки\n");
        Thread.Sleep(200);
        Assert.Equal(0, Count(lines));
    }

    /// <summary>Фабрика отдаёт остановленный тейлер на указанный путь.</summary>
    [Fact]
    public void Factory_CreatesStoppedTailer()
    {
        ILogTailerFactory factory = new PollingLogTailerFactory();
        using ILogTailer tailer = factory.Create(_log);

        Assert.Equal(_log, tailer.FilePath);
        Assert.False(tailer.IsRunning);
    }

    // ---------------------------------------------------------------- вспомогательное

    private void Write(string content) =>
        File.WriteAllText(_log, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

    private void Append(string content)
    {
        using var fs = new FileStream(_log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        fs.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Имитирует поведение движка: файл переписывается коротким хвостом.</summary>
    private void TruncateTo(string newContent)
    {
        using var fs = new FileStream(_log, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        byte[] bytes = new UTF8Encoding(false).GetBytes(newContent);
        fs.SetLength(0);
        fs.Write(bytes, 0, bytes.Length);
    }

    private static int Count(List<string> lines)
    {
        lock (lines)
            return lines.Count;
    }

    private static void WaitFor(Func<bool> condition, List<string> diagnostics)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Timeout)
        {
            if (condition())
                return;

            Thread.Sleep(20);
        }

        string got;
        lock (diagnostics)
            got = string.Join(" | ", diagnostics);

        Assert.Fail($"Условие не выполнилось за {Timeout.TotalSeconds:0} с. Получено: [{got}]");
    }
}
