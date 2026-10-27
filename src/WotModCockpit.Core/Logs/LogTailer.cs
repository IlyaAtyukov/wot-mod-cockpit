using System.Text;
using WotModCockpit.Core.Abstractions;

namespace WotModCockpit.Core.Services;

/// <summary>
/// Хвост <c>&lt;game&gt;\python.log</c> опросом по таймеру. Реализация <see cref="ILogTailer"/>.
/// <para>
/// Почему опрос, а не только <c>FileSystemWatcher</c>: игра дописывает в уже открытый дескриптор,
/// и уведомления файловой системы на такие дозаписи приходят нерегулярно (метаданные каталога
/// обновляются лениво). Надёжная схема — таймер на 250 мс, сравнение <c>FileInfo.Length</c>
/// с прочитанным смещением; <c>FileSystemWatcher</c> висит рядом только как ускоритель и
/// его отсутствие ни на что не влияет.
/// </para>
/// <para>
/// Что здесь сделано ради выживания при УСЕЧЕНИИ (движок режет файл с 2048 КБ до 512 КБ):
/// </para>
/// <list type="number">
///   <item>длина файла стала МЕНЬШЕ прочитанного смещения → это усечение;</item>
///   <item>поднимается <see cref="FileTruncated"/> ДО первой строки нового содержимого;</item>
///   <item>смещение сбрасывается в 0, буфер неполной строки и состояние UTF-8 декодера очищаются,
///         чтение продолжается с начала нового содержимого;</item>
///   <item>уже показанный префикс нового содержимого ОТРЕЗАЕТСЯ. Движок не выбрасывает файл целиком,
///         а оставляет его последние 512 КБ, поэтому наивное «читаем с нуля» выдаёт потребителю
///         ~3000 строк, которые он только что видел. Отсечка идёт по сигнатуре из двух последних
///         выданных строк: ищем её последнее вхождение в новом содержимом и отдаём только то,
///         что после неё. Сигнатура не нашлась (файл действительно подменили) — отдаём всё.</item>
/// </list>
/// <para>
/// Пересоздание файла (удалили и создали заново) ловится тем же способом плюс сравнением
/// <c>CreationTimeUtc</c>. Исчезновение файла на ходу тоже приравнено к усечению: событие
/// <see cref="FileTruncated"/> поднимается при его повторном появлении, ДО первой строки —
/// иначе потребитель не узнает, что все ранее выданные смещения стали недействительными.
/// Отсутствие файла на старте — не ошибка: тейлер просто ждёт его появления.
/// </para>
/// </summary>
public sealed class PollingLogTailer : ILogTailer
{
    /// <summary>Период опроса по умолчанию.</summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _gate = new();
    private readonly TimeSpan _pollInterval;
    private readonly StringBuilder _partial = new();
    private readonly Decoder _decoder = CreateDecoder();
    private readonly byte[] _buffer = new byte[64 * 1024];
    private readonly char[] _chars = new char[64 * 1024 + 8];

    private Timer? _timer;
    private FileSystemWatcher? _watcher;
    private long _position;
    private DateTime _creationTimeUtc = DateTime.MinValue;
    private bool _seenFile;
    private bool _wasMissing;
    private volatile bool _running;
    private bool _disposed;

    /// <summary>Последняя выданная строка и предыдущая — сигнатура «докуда потребитель уже видел».</summary>
    private string? _lastEmitted;
    private string? _prevEmitted;

    /// <summary>Сигнатура, по которой надо отрезать уже показанный префикс в ближайшем чтении.</summary>
    private string[]? _dedupeAnchor;

    /// <summary>Создаёт тейлер для файла с периодом опроса по умолчанию (250 мс).</summary>
    /// <param name="filePath">Абсолютный путь к <c>python.log</c>. Файл может не существовать.</param>
    public PollingLogTailer(string filePath) : this(filePath, DefaultPollInterval)
    {
    }

    /// <summary>Создаёт тейлер с явно заданным периодом опроса (нужно тестам).</summary>
    /// <param name="filePath">Абсолютный путь к файлу.</param>
    /// <param name="pollInterval">Период опроса; значения меньше 10 мс поднимаются до 10 мс.</param>
    public PollingLogTailer(string filePath, TimeSpan pollInterval)
    {
        FilePath = filePath ?? throw new ArgumentNullException(nameof(filePath));
        _pollInterval = pollInterval < TimeSpan.FromMilliseconds(10)
            ? TimeSpan.FromMilliseconds(10)
            : pollInterval;
    }

    /// <inheritdoc />
    public string FilePath { get; }

    /// <inheritdoc />
    public bool IsRunning => _running;

    /// <inheritdoc />
    public event EventHandler<string>? LineRead;

    /// <inheritdoc />
    public event EventHandler? FileTruncated;

    /// <inheritdoc />
    public event EventHandler<Exception>? Error;

    /// <summary>
    /// Текущее смещение чтения в байтах от начала файла. Диагностическое свойство:
    /// после усечения обнуляется.
    /// </summary>
    public long Position
    {
        get { lock (_gate) { return _position; } }
    }

    /// <summary>
    /// Сколько строк было отброшено как «уже показанные» при последнем усечении.
    /// Диагностическое свойство: на реальной ротации 2048→512 КБ здесь оказываются тысячи.
    /// </summary>
    public int LastTruncationSkippedLines { get; private set; }

    /// <inheritdoc />
    public void Start(int primeTailBytes = 64 * 1024)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
                return;

            _partial.Clear();
            _decoder.Reset();
            _position = 0;
            _seenFile = false;
            _wasMissing = false;
            _creationTimeUtc = DateTime.MinValue;
            _lastEmitted = null;
            _prevEmitted = null;
            _dedupeAnchor = null;
            LastTruncationSkippedLines = 0;

            Prime(primeTailBytes);

            _running = true;
            _timer = new Timer(_ => Poll(), null, _pollInterval, _pollInterval);
            TryStartWatcher();
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        Timer? timer;
        FileSystemWatcher? watcher;

        lock (_gate)
        {
            if (!_running && _timer is null && _watcher is null)
                return;

            _running = false;
            timer = _timer;
            watcher = _watcher;
            _timer = null;
            _watcher = null;
        }

        timer?.Dispose();
        if (watcher is not null)
        {
            try
            {
                watcher.EnableRaisingEvents = false;
                watcher.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // выключение наблюдателя не должно ронять остановку
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Stop();

        lock (_gate)
        {
            _disposed = true;
        }

        LineRead = null;
        FileTruncated = null;
        Error = null;
    }

    // ---------------------------------------------------------------- внутренности

    /// <summary>
    /// Прогрев: прочитать последние <paramref name="primeTailBytes"/> байт и отдать их строками,
    /// отбросив первую (заведомо обрезанную посередине). 0 — читать только новое.
    /// </summary>
    /// <param name="primeTailBytes">Сколько последних байт прочитать.</param>
    private void Prime(int primeTailBytes)
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists)
                return;

            _seenFile = true;
            _creationTimeUtc = info.CreationTimeUtc;

            long length = info.Length;
            if (primeTailBytes <= 0 || length == 0)
            {
                _position = length;
                return;
            }

            long start = Math.Max(0, length - primeTailBytes);
            _position = start;

            List<string> lines = ReadNewLines(length);
            int skip = start > 0 && lines.Count > 0 ? 1 : 0;   // первая строка обрезана посередине
            for (int i = skip; i < lines.Count; i++)
                RaiseLine(lines[i]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            RaiseError(ex);
        }
    }

    /// <summary>Один такт опроса: проверить длину, поймать усечение, дочитать новое.</summary>
    private void Poll()
    {
        if (!Monitor.TryEnter(_gate))
            return;                                   // предыдущий такт ещё не закончился

        try
        {
            if (!_running)
                return;

            FileInfo info;
            try
            {
                info = new FileInfo(FilePath);
                info.Refresh();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseError(ex);
                return;
            }

            if (!info.Exists)
            {
                // Файл ещё не создан (игра не запускалась) либо удалён. Ждём появления.
                // Пропажу ЗАПОМИНАЕМ: по контракту ILogTailer подмена файла целиком обязана
                // выглядеть для потребителя так же, как усечение, а поднять FileTruncated можно
                // только когда файл появится — строго ДО первой строки нового содержимого.
                if (_seenFile)
                {
                    _seenFile = false;
                    _wasMissing = true;
                    ResetToStart(raiseTruncated: false);
                }

                return;
            }

            long length;
            DateTime creation;
            try
            {
                length = info.Length;
                creation = info.CreationTimeUtc;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseError(ex);
                return;
            }

            bool recreated = _seenFile && creation != _creationTimeUtc;
            bool truncated = _seenFile && length < _position;
            bool reappeared = _wasMissing;

            _seenFile = true;
            _wasMissing = false;
            _creationTimeUtc = creation;

            if (truncated || recreated || reappeared)
                ResetToStart(raiseTruncated: true);

            if (length <= _position)
                return;

            List<string> lines;
            try
            {
                lines = ReadNewLines(length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RaiseError(ex);
                return;
            }

            EmitLines(lines);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    /// <summary>Сброс состояния чтения на начало файла.</summary>
    /// <param name="raiseTruncated">Поднимать ли <see cref="FileTruncated"/>.</param>
    private void ResetToStart(bool raiseTruncated)
    {
        _position = 0;
        _partial.Clear();
        _decoder.Reset();

        if (!raiseTruncated)
            return;

        // Запоминаем, докуда потребитель уже дочитан: следующее чтение отрежет этот префикс.
        _dedupeAnchor = _lastEmitted is null
            ? null
            : _prevEmitted is null ? [_lastEmitted] : [_prevEmitted, _lastEmitted];

        LastTruncationSkippedLines = 0;
        RaiseTruncated();
    }

    /// <summary>
    /// Отдаёт прочитанные строки, отрезав тот префикс, который потребитель уже видел до усечения.
    /// </summary>
    /// <param name="lines">Строки, прочитанные одним заходом.</param>
    private void EmitLines(List<string> lines)
    {
        string[]? anchor = _dedupeAnchor;
        if (anchor is not null)
        {
            _dedupeAnchor = null;

            int cut = LastIndexOfSequence(lines, anchor);
            if (cut >= 0)
            {
                LastTruncationSkippedLines = cut;
                lines.RemoveRange(0, cut);
            }
        }

        foreach (string line in lines)
            RaiseLine(line);
    }

    /// <summary>
    /// Индекс ПОСЛЕ последнего вхождения последовательности <paramref name="needle"/> в
    /// <paramref name="hay"/>, либо -1, если её там нет.
    /// </summary>
    /// <param name="hay">Где искать.</param>
    /// <param name="needle">Что искать (сигнатура из одной-двух строк).</param>
    /// <returns>Сколько ведущих строк надо отбросить, либо -1.</returns>
    private static int LastIndexOfSequence(List<string> hay, string[] needle)
    {
        for (int start = hay.Count - needle.Length; start >= 0; start--)
        {
            bool hit = true;
            for (int k = 0; k < needle.Length && hit; k++)
                hit = string.Equals(hay[start + k], needle[k], StringComparison.Ordinal);

            if (hit)
                return start + needle.Length;
        }

        return -1;
    }

    /// <summary>
    /// Дочитывает файл от <see cref="_position"/> до <paramref name="upTo"/> и возвращает
    /// ПОЛНЫЕ строки. Хвост без перевода строки остаётся в <see cref="_partial"/> до следующего раза.
    /// </summary>
    /// <param name="upTo">Длина файла на момент опроса.</param>
    /// <returns>Полные строки без <c>\r\n</c>.</returns>
    private List<string> ReadNewLines(long upTo)
    {
        var result = new List<string>();

        // FileShare.ReadWrite | FileShare.Delete обязателен: игра держит файл открытым на запись,
        // без Delete не переживём подмену файла.
        using var fs = new FileStream(
            FilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.SequentialScan);

        if (_position > fs.Length)
        {
            // Файл ужался между FileInfo.Length и открытием — та же реакция, что на усечение.
            ResetToStart(raiseTruncated: true);
        }

        fs.Seek(_position, SeekOrigin.Begin);

        long remaining = Math.Max(0, Math.Min(upTo, fs.Length) - _position);
        while (remaining > 0)
        {
            int want = (int)Math.Min(_buffer.Length, remaining);
            int read = fs.Read(_buffer, 0, want);
            if (read <= 0)
                break;

            _position += read;
            remaining -= read;

            // Декодер СТАТЕФУЛЬНЫЙ: многобайтовый символ может быть разрезан границей чтения.
            int chars = _decoder.GetChars(_buffer, 0, read, _chars, 0, flush: false);
            AppendAndSplit(_chars, chars, result);
        }

        return result;
    }

    /// <summary>Добавляет декодированный кусок в буфер и вырезает из него законченные строки.</summary>
    /// <param name="chars">Буфер символов.</param>
    /// <param name="count">Сколько символов значимо.</param>
    /// <param name="sink">Куда складывать готовые строки.</param>
    private void AppendAndSplit(char[] chars, int count, List<string> sink)
    {
        for (int i = 0; i < count; i++)
        {
            char c = chars[i];
            if (c == '\n')
            {
                if (_partial.Length > 0 && _partial[^1] == '\r')
                    _partial.Length--;

                sink.Add(_partial.ToString());
                _partial.Clear();
            }
            else
            {
                _partial.Append(c);
            }
        }
    }

    /// <summary>Вешает <see cref="FileSystemWatcher"/> на каталог файла как ускоритель опроса.</summary>
    private void TryStartWatcher()
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(FilePath));
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                return;

            var watcher = new FileSystemWatcher(dir, Path.GetFileName(FilePath))
            {
                NotifyFilter = NotifyFilters.Size | NotifyFilters.LastWrite | NotifyFilters.FileName |
                               NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };

            watcher.Changed += (_, _) => Poll();
            watcher.Created += (_, _) => Poll();
            watcher.Renamed += (_, _) => Poll();
            watcher.Deleted += (_, _) => Poll();
            watcher.Error += (_, e) => RaiseError(e.GetException());
            watcher.EnableRaisingEvents = true;

            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException
                                       or PlatformNotSupportedException)
        {
            // Ускоритель необязателен: опрос по таймеру справится и без него.
            _watcher = null;
        }
    }

    private static Decoder CreateDecoder() =>
        // Битые байты не должны ронять чтение: заменяем на U+FFFD.
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false).GetDecoder();

    private void RaiseLine(string line)
    {
        _prevEmitted = _lastEmitted;
        _lastEmitted = line;

        try
        {
            LineRead?.Invoke(this, line);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    private void RaiseTruncated()
    {
        try
        {
            FileTruncated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            RaiseError(ex);
        }
    }

    private void RaiseError(Exception ex)
    {
        try
        {
            Error?.Invoke(this, ex);
        }
        catch
        {
            // Подписчик на Error сам упал — дальше эскалировать некуда.
        }
    }
}

/// <summary>
/// Фабрика <see cref="PollingLogTailer"/>. Зарегистрирована в DI синглтоном; созданным тейлером
/// владеет вызывающий и обязан его освободить.
/// </summary>
public sealed class PollingLogTailerFactory : ILogTailerFactory
{
    /// <inheritdoc />
    public ILogTailer Create(string filePath) => new PollingLogTailer(filePath);
}
