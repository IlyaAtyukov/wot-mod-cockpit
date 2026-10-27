using System.Buffers.Binary;

namespace WotModCockpit.Core.Bundle;

/// <summary>
/// Формат файла сетапа: <c>[exe-заглушка][ZIP][трейлер 32 байта]</c>.
/// <para>
/// Заглушка — <c>WotModCockpit.Setup.exe</c> (.NET Framework 4.7.2, WinForms): она читает
/// собственный файл с конца, находит трейлер, открывает ZIP и показывает окно с галочками.
/// Трейлер: 8 байт <c>WMCSETUP</c>, int32 версия формата, int64 смещение ZIP от начала файла,
/// int64 длина ZIP, 4 байта резерва (нули); числа little-endian. ZIP лежит вплотную перед
/// трейлером, смещения в его каталоге отсчитаны от начала самого ZIP.
/// </para>
/// <para>
/// Внутри ZIP — <see cref="ManifestEntryName"/> (описание сборки) и моды в
/// <see cref="ModsFolder"/>. Читающая сторона — <c>src\WotModCockpit.Setup\Bundle.cs</c>: две
/// стороны одного формата, правка одной без другой ломает все уже собранные сетапы.
/// </para>
/// <para>
/// Почему не самораспаковывающийся ZIP со смещениями от начала exe: тогда архив пришлось бы
/// писать прямо в хвост заглушки со сдвинутыми смещениями, а проверять его — только целиком.
/// Трейлер даёт обеим сторонам точные границы и позволяет отличить голую заглушку от сетапа.
/// </para>
/// </summary>
public static class SetupBundleFormat
{
    /// <summary>Размер трейлера.</summary>
    public const int TrailerSize = 32;

    /// <summary>Версия формата, которую пишет кокпит и понимает заглушка.</summary>
    public const int FormatVersion = 1;

    /// <summary>Описание сборки внутри ZIP.</summary>
    public const string ManifestEntryName = "bundle.json";

    /// <summary>Каталог модов внутри ZIP.</summary>
    public const string ModsFolder = "mods/";

    /// <summary>Подпись трейлера.</summary>
    public static ReadOnlySpan<byte> Magic => "WMCSETUP"u8;

    /// <summary>Собирает трейлер.</summary>
    /// <param name="payloadOffset">Где начинается ZIP.</param>
    /// <param name="payloadLength">Длина ZIP.</param>
    /// <returns>32 байта трейлера.</returns>
    public static byte[] CreateTrailer(long payloadOffset, long payloadLength)
    {
        byte[] trailer = new byte[TrailerSize];
        Magic.CopyTo(trailer);
        BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(8), FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(12), payloadOffset);
        BinaryPrimitives.WriteInt64LittleEndian(trailer.AsSpan(20), payloadLength);
        return trailer;
    }

    /// <summary>Есть ли в конце потока трейлер сетапа (то есть к exe уже что-то дописано).</summary>
    /// <param name="stream">Поток с возможностью позиционирования.</param>
    /// <returns>Признак.</returns>
    public static bool HasTrailer(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.Length < TrailerSize)
        {
            return false;
        }

        byte[] magic = new byte[Magic.Length];
        stream.Seek(-TrailerSize, SeekOrigin.End);
        stream.ReadExactly(magic);
        return Magic.SequenceEqual(magic);
    }

    /// <summary>Читает и проверяет трейлер.</summary>
    /// <param name="stream">Поток файла сетапа.</param>
    /// <returns>Версия формата, смещение и длина ZIP.</returns>
    /// <exception cref="SetupBundleException">Трейлера нет или он не сходится с размером файла.</exception>
    public static (int Format, long Offset, long Length) ReadTrailer(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        long size = stream.Length;
        if (size < TrailerSize)
        {
            throw new SetupBundleException("файл слишком мал для сетапа");
        }

        byte[] trailer = new byte[TrailerSize];
        stream.Seek(-TrailerSize, SeekOrigin.End);
        stream.ReadExactly(trailer);

        if (!Magic.SequenceEqual(trailer.AsSpan(0, Magic.Length)))
        {
            throw new SetupBundleException("в конце файла нет трейлера сетапа — это не сетап кокпита");
        }

        int format = BinaryPrimitives.ReadInt32LittleEndian(trailer.AsSpan(8));
        long offset = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(12));
        long length = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(20));

        if (format != FormatVersion)
        {
            throw new SetupBundleException($"формат сетапа {format}, а кокпит понимает {FormatVersion}");
        }

        if (offset <= 0 || length <= 0 || offset + length != size - TrailerSize)
        {
            throw new SetupBundleException("границы архива в трейлере не сходятся с размером файла");
        }

        return (format, offset, length);
    }
}

/// <summary>Беда со сборкой или чтением сетапа. Текст годится для показа пользователю.</summary>
public sealed class SetupBundleException : Exception
{
    /// <summary>Создаёт исключение.</summary>
    /// <param name="message">Что не так.</param>
    public SetupBundleException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с причиной.</summary>
    /// <param name="message">Что не так.</param>
    /// <param name="inner">Исходное исключение.</param>
    public SetupBundleException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Окно только на чтение поверх части потока: ZIP внутри сетапа начинается не с нулевого
/// байта файла.
/// </summary>
internal sealed class PayloadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    /// <summary>Создаёт окно.</summary>
    /// <param name="inner">Поток файла; окно его не закрывает.</param>
    /// <param name="start">Начало окна.</param>
    /// <param name="length">Длина окна.</param>
    public PayloadStream(Stream inner, long start, long length)
    {
        _inner = inner;
        _start = start;
        _length = length;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0)
        {
            return 0;
        }

        if (buffer.Length > remaining)
        {
            buffer = buffer[..(int)remaining];
        }

        _inner.Seek(_start + _position, SeekOrigin.Begin);
        int n = _inner.Read(buffer);
        _position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => _length + offset,
        };

        if (target < 0)
        {
            throw new IOException("попытка встать перед началом архива");
        }

        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
