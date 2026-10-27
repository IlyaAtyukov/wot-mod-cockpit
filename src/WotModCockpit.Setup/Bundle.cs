using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace WotModCockpit.Setup
{
    /// <summary>
    /// Сборка модов, прочитанная из собственного exe.
    /// <para>
    /// Раскладка файла: <c>[exe-заглушка][ZIP][трейлер 32 байта]</c>. Трейлер: 8 байт
    /// <c>WMCSETUP</c>, int32 версия формата, int64 смещение ZIP от начала файла, int64 длина
    /// ZIP, 4 байта резерва (нули); числа little-endian. ZIP лежит вплотную перед трейлером.
    /// Внутри ZIP — <c>bundle.json</c> (описание сборки) и сами моды в <c>mods/</c>.
    /// </para>
    /// <para>
    /// Пишет такой файл кокпит (<c>WotModCockpit.Core.Bundle.SetupBundleBuilder</c>). Это две
    /// стороны одного формата: правка одной без другой ломает все уже собранные сетапы.
    /// </para>
    /// </summary>
    internal sealed class BundleFile : IDisposable
    {
        /// <summary>Размер трейлера в конце файла.</summary>
        public const int TrailerSize = 32;

        /// <summary>Версия формата, которую понимает этот установщик.</summary>
        public const int SupportedFormat = 1;

        /// <summary>Имя описания сборки внутри ZIP.</summary>
        public const string ManifestEntryName = "bundle.json";

        /// <summary>Каталог модов внутри ZIP.</summary>
        public const string ModsFolder = "mods/";

        private const long MaxManifestBytes = 1024 * 1024;

        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WMCSETUP");

        private readonly FileStream _file;
        private readonly ZipArchive _zip;

        private BundleFile(string path, FileStream file, ZipArchive zip, BundleManifest manifest)
        {
            FilePath = path;
            _file = file;
            _zip = zip;
            Manifest = manifest;
        }

        /// <summary>Путь к exe, из которого прочитана сборка.</summary>
        public string FilePath { get; }

        /// <summary>Описание сборки.</summary>
        public BundleManifest Manifest { get; }

        /// <summary>
        /// Открывает сборку внутри файла. Любая беда — <see cref="BundleException"/> с текстом,
        /// который можно показать человеку как есть.
        /// </summary>
        /// <param name="path">Путь к exe сетапа.</param>
        /// <returns>Открытая сборка; вызывающий обязан её освободить.</returns>
        public static BundleFile Open(string path)
        {
            FileStream file = null;
            ZipArchive zip = null;
            try
            {
                file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

                long offset;
                long length;
                ReadTrailer(file, out offset, out length);

                try
                {
                    zip = new ZipArchive(new WindowStream(file, offset, length), ZipArchiveMode.Read, false);
                }
                catch (InvalidDataException ex)
                {
                    throw new BundleException("Сетап повреждён: архив с модами не читается (" + ex.Message + ").", ex);
                }

                BundleManifest manifest = ReadManifest(zip);
                var bundle = new BundleFile(path, file, zip, manifest);
                file = null;
                zip = null;
                return bundle;
            }
            catch (IOException ex)
            {
                throw new BundleException("Не удалось прочитать файл сетапа: " + ex.Message, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new BundleException("Нет доступа к файлу сетапа: " + ex.Message, ex);
            }
            finally
            {
                if (zip != null)
                {
                    zip.Dispose();
                }

                if (file != null)
                {
                    file.Dispose();
                }
            }
        }

        /// <summary>Запись ZIP с файлом мода либо <c>null</c>.</summary>
        /// <param name="component">Мод из описания.</param>
        /// <returns>Запись архива.</returns>
        public ZipArchiveEntry GetEntry(BundleComponent component)
        {
            return _zip.GetEntry(component.File);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _zip.Dispose();
            _file.Dispose();
        }

        private static void ReadTrailer(FileStream file, out long offset, out long length)
        {
            long size = file.Length;
            if (size < TrailerSize + 22)
            {
                throw BundleException.BareStub();
            }

            var trailer = new byte[TrailerSize];
            file.Seek(size - TrailerSize, SeekOrigin.Begin);
            ReadExactly(file, trailer);

            for (int i = 0; i < Magic.Length; i++)
            {
                if (trailer[i] != Magic[i])
                {
                    if (LooksLikeDamagedSetup(file))
                    {
                        throw new BundleException(
                            "Сетап повреждён: файл обрезан или к нему что-то дописано — так бывает при недокачанном или испорченном копировании. Скачайте или скопируйте его заново.");
                    }

                    throw BundleException.BareStub();
                }
            }

            int format = BitConverter.ToInt32(trailer, 8);
            offset = BitConverter.ToInt64(trailer, 12);
            length = BitConverter.ToInt64(trailer, 20);

            if (format < 1 || format > SupportedFormat)
            {
                throw new BundleException(
                    "Сетап собран в формате " + format + ", а этот установщик понимает только формат " + SupportedFormat +
                    ". Пересоберите сетап той же версией кокпита.");
            }

            if (offset <= 0 || length <= 0 || offset + length != size - TrailerSize)
            {
                throw new BundleException(
                    "Сетап повреждён: границы архива с модами не сходятся с размером файла — файл обрезан при копировании или к нему что-то дописано.");
            }
        }

        /// <summary>
        /// Подписи в конце нет — но голая ли это заглушка? В хвосте сетапа лежат конец ZIP
        /// (<c>PK\x05\x06</c>) и подпись <c>WMCSETUP</c>; exe-заглушка так не кончается. Если они
        /// нашлись в последних килобайтах, файл — сетап, у которого отрезали или к которому
        /// дописали хвост.
        /// </summary>
        /// <param name="file">Файл.</param>
        /// <returns>Похоже на повреждённый сетап.</returns>
        private static bool LooksLikeDamagedSetup(FileStream file)
        {
            long size = file.Length;
            int window = (int)Math.Min(size, 4096);
            var tail = new byte[window];
            file.Seek(size - window, SeekOrigin.Begin);
            ReadExactly(file, tail);

            for (int i = 0; i + 4 <= window; i++)
            {
                if (tail[i] == 0x50 && tail[i + 1] == 0x4B && tail[i + 2] == 0x05 && tail[i + 3] == 0x06)
                {
                    return true;
                }

                if (i + Magic.Length <= window && tail[i] == Magic[0])
                {
                    int k = 1;
                    while (k < Magic.Length && tail[i + k] == Magic[k])
                    {
                        k++;
                    }

                    if (k == Magic.Length)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static BundleManifest ReadManifest(ZipArchive zip)
        {
            ZipArchiveEntry entry = zip.GetEntry(ManifestEntryName);
            if (entry == null)
            {
                throw new BundleException("Сетап повреждён: в нём нет описания сборки (" + ManifestEntryName + ").");
            }

            if (entry.Length <= 0 || entry.Length > MaxManifestBytes)
            {
                throw new BundleException("Сетап повреждён: описание сборки пустое или слишком большое.");
            }

            BundleManifest manifest;
            try
            {
                using (Stream s = entry.Open())
                {
                    var serializer = new DataContractJsonSerializer(typeof(BundleManifest));
                    manifest = serializer.ReadObject(s) as BundleManifest;
                }
            }
            catch (SerializationException ex)
            {
                throw new BundleException("Сетап повреждён: описание сборки не читается (" + ex.Message + ").", ex);
            }
            catch (InvalidDataException ex)
            {
                throw new BundleException("Сетап повреждён: описание сборки не распаковывается (" + ex.Message + ").", ex);
            }

            if (manifest == null)
            {
                throw new BundleException("Сетап повреждён: описание сборки пустое.");
            }

            if (manifest.Format < 1 || manifest.Format > SupportedFormat)
            {
                throw new BundleException(
                    "Описание сборки в формате " + manifest.Format + ", а этот установщик понимает только формат " + SupportedFormat + ".");
            }

            if (manifest.Components == null || manifest.Components.Count == 0)
            {
                throw new BundleException("В сетапе нет ни одного мода.");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BundleComponent c in manifest.Components)
            {
                string problem = c == null ? "пустая запись" : c.Validate();
                if (problem == null && zip.GetEntry(c.File) == null)
                {
                    problem = "в архиве нет файла " + c.File;
                }

                if (problem == null && !ids.Add(c.Id))
                {
                    problem = "мод " + c.Id + " описан дважды";
                }

                if (problem == null && !names.Add(c.FileName))
                {
                    problem = "файл " + c.FileName + " встречается дважды";
                }

                if (problem != null)
                {
                    throw new BundleException("Сетап повреждён: " + problem + ".");
                }
            }

            return manifest;
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int read = 0;
            while (read < buffer.Length)
            {
                int n = stream.Read(buffer, read, buffer.Length - read);
                if (n <= 0)
                {
                    throw new EndOfStreamException();
                }

                read += n;
            }
        }
    }

    /// <summary>Описание сборки — <c>bundle.json</c>. Имена полей — те же, что пишет кокпит.</summary>
    [DataContract]
    internal sealed class BundleManifest
    {
        /// <summary>Версия формата.</summary>
        [DataMember(Name = "format")]
        public int Format { get; set; }

        /// <summary>Имя сборки для заголовка окна.</summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>Версия сборки (обычно дата).</summary>
        [DataMember(Name = "version")]
        public string Version { get; set; }

        /// <summary>Когда собрана, ISO 8601 UTC.</summary>
        [DataMember(Name = "createdUtc")]
        public string CreatedUtc { get; set; }

        /// <summary>Версия клиента, под которую собиралась сборка; может быть пустой.</summary>
        [DataMember(Name = "gameVersion")]
        public string GameVersion { get; set; }

        /// <summary>Моды сборки.</summary>
        [DataMember(Name = "components")]
        public List<BundleComponent> Components { get; set; }
    }

    /// <summary>Один мод сборки.</summary>
    [DataContract]
    internal sealed class BundleComponent
    {
        /// <summary><c>&lt;id&gt;</c> из meta.xml мода.</summary>
        [DataMember(Name = "id")]
        public string Id { get; set; }

        /// <summary>Имя для списка.</summary>
        [DataMember(Name = "name")]
        public string Name { get; set; }

        /// <summary>Версия мода.</summary>
        [DataMember(Name = "version")]
        public string Version { get; set; }

        /// <summary>Описание для панели под списком.</summary>
        [DataMember(Name = "description")]
        public string Description { get; set; }

        /// <summary>Путь внутри ZIP: <c>mods/&lt;имя файла&gt;.mtmod</c>.</summary>
        [DataMember(Name = "file")]
        public string File { get; set; }

        /// <summary>Размер файла мода.</summary>
        [DataMember(Name = "size")]
        public long Size { get; set; }

        /// <summary>SHA-256 файла мода, 64 шестнадцатеричных знака.</summary>
        [DataMember(Name = "sha256")]
        public string Sha256 { get; set; }

        /// <summary>Имя файла, под которым мод ляжет в игру.</summary>
        public string FileName
        {
            get
            {
                string file = File ?? string.Empty;
                int slash = file.LastIndexOf('/');
                return slash < 0 ? file : file.Substring(slash + 1);
            }
        }

        /// <summary>Что показывать в списке: имя, а без него — id.</summary>
        public string Title
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Id : Name; }
        }

        /// <summary>
        /// Проверка записи. Имя файла уходит прямо в путь внутри папки игры, поэтому оно обязано
        /// быть голым именем <c>*.mtmod</c> — без каталогов, <c>..</c> и запрещённых символов.
        /// </summary>
        /// <returns><c>null</c>, если всё в порядке, иначе причина.</returns>
        public string Validate()
        {
            if (string.IsNullOrWhiteSpace(Id))
            {
                return "у мода не указан id";
            }

            if (string.IsNullOrWhiteSpace(File) || !File.StartsWith(BundleFile.ModsFolder, StringComparison.Ordinal))
            {
                return "у мода " + Id + " неверный путь файла";
            }

            string name = FileName;
            if (name.Length == 0 ||
                name != File.Substring(BundleFile.ModsFolder.Length) ||
                !name.EndsWith(".mtmod", StringComparison.OrdinalIgnoreCase) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.StartsWith(".", StringComparison.Ordinal))
            {
                return "у мода " + Id + " недопустимое имя файла «" + name + "»";
            }

            if (Size <= 0)
            {
                return "у мода " + Id + " не указан размер";
            }

            if (Sha256 == null || Sha256.Length != 64)
            {
                return "у мода " + Id + " нет контрольной суммы";
            }

            foreach (char ch in Sha256)
            {
                bool hex = (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');
                if (!hex)
                {
                    return "у мода " + Id + " испорчена контрольная сумма";
                }
            }

            return null;
        }
    }

    /// <summary>Беда с самим сетапом. Текст показывается человеку как есть.</summary>
    internal sealed class BundleException : Exception
    {
        public BundleException(string message)
            : base(message)
        {
        }

        public BundleException(string message, Exception inner)
            : base(message, inner)
        {
        }

        private BundleException(string message, bool isBareStub)
            : base(message)
        {
            IsBareStub = isBareStub;
        }

        /// <summary>Это голая заглушка: к exe ещё ничего не дописано.</summary>
        public bool IsBareStub { get; }

        /// <summary>Сообщение для голой заглушки, которую запустили саму по себе.</summary>
        /// <returns>Исключение.</returns>
        public static BundleException BareStub()
        {
            return new BundleException(
                "Это заготовка установщика WoT Mod Cockpit — модов внутри нет.\n\n" +
                "Сетап со своими модами собирается в кокпите: экран «Моды» → «собрать сетап…».",
                true);
        }
    }

    /// <summary>
    /// Окно только на чтение поверх части файла: ZIP внутри exe начинается не с нулевого байта,
    /// а смещения в его каталоге отсчитаны от начала самого ZIP.
    /// </summary>
    internal sealed class WindowStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public WindowStream(Stream inner, long start, long length)
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

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length)
            {
                return 0;
            }

            long remaining = _length - _position;
            if (count > remaining)
            {
                count = (int)remaining;
            }

            // Внутренний поток общий: позиция выставляется перед каждым чтением.
            _inner.Seek(_start + _position, SeekOrigin.Begin);
            int n = _inner.Read(buffer, offset, count);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    target = offset;
                    break;
                case SeekOrigin.Current:
                    target = _position + offset;
                    break;
                default:
                    target = _length + offset;
                    break;
            }

            if (target < 0)
            {
                throw new IOException("Попытка встать перед началом архива.");
            }

            _position = target;
            return _position;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}
