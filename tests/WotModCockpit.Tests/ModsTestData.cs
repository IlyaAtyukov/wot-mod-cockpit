using System.IO.Compression;
using System.Text;
using WotModCockpit.Core.Models;
using WotModCockpit.Core.Mods;

namespace WotModCockpit.Tests;

/// <summary>
/// Строитель ФЕЙКОВОГО каталога игры во временной папке. Все тесты лаунчера, кроме помеченных
/// <c>Env=RealMachine</c>, работают только здесь: настоящая установка игры используется
/// исключительно на чтение, и ни один тест туда не пишет.
/// <para>
/// Архивы делаются настоящими ZIP и по умолчанию методом Stored — как у реальных модов
/// (движок иначе скажет «load error: compression not supported»).
/// </para>
/// </summary>
internal sealed class FakeGameDir : IDisposable
{
    private readonly CoreTempDir _tmp = new("wotcockpit-mods");

    /// <summary>Корень фейковой установки.</summary>
    public string Root => _tmp.Path;

    /// <summary>Каталог модов <c>&lt;root&gt;\mods\1.44.0.0</c>.</summary>
    public string ModsDir => Path.Combine(Root, "mods", "1.44.0.0");

    /// <summary>Описание установки для сервисов Core.</summary>
    public GameInstall Install => new(
        Root,
        "1.44.0.0",
        ModsDir,
        Path.Combine(Root, "res_mods", "1.44.0.0"),
        Path.Combine(Root, "python.log"));

    /// <summary>Создаёт каталог модов.</summary>
    public FakeGameDir() => Directory.CreateDirectory(ModsDir);

    /// <summary>Путь внутри фейковой установки, без создания.</summary>
    /// <param name="relative">Путь относительно корня.</param>
    /// <returns>Абсолютный путь.</returns>
    public string Combine(string relative) =>
        Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Кладёт в каталог модов настоящий <c>.mtmod</c>.
    /// </summary>
    /// <param name="relativeName">
    /// Имя файла относительно каталога модов; может содержать подпапку (<c>audioww/x.mtmod</c>).
    /// </param>
    /// <param name="metaXml">Содержимое <c>meta.xml</c>; <c>null</c> — мода без meta.xml (на живой установке таких большинство).</param>
    /// <param name="entries">Прочие записи архива: путь внутри архива → содержимое.</param>
    /// <param name="compress">true — сжать записи (проверка «мод не загрузится»).</param>
    /// <returns>Абсолютный путь созданного файла.</returns>
    public string AddMod(
        string relativeName,
        string? metaXml = null,
        IEnumerable<KeyValuePair<string, byte[]>>? entries = null,
        bool compress = false)
    {
        string full = Path.Combine(ModsDir, relativeName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        WriteArchive(full, metaXml, entries, compress);
        return full;
    }

    /// <summary>Кладёт в каталог модов произвольный файл (например с чужим расширением).</summary>
    /// <param name="relativeName">Имя относительно каталога модов.</param>
    /// <param name="content">Содержимое.</param>
    /// <returns>Абсолютный путь.</returns>
    public string AddRawFile(string relativeName, string content = "not a mod")
    {
        string full = Path.Combine(ModsDir, relativeName.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    /// <summary>
    /// Кладёт «битый» мод: правильное расширение, мусор внутри. Такой файл обязан появиться
    /// в списке с пометкой, а не уронить обход.
    /// </summary>
    /// <param name="relativeName">Имя относительно каталога модов.</param>
    /// <returns>Абсолютный путь.</returns>
    public string AddBrokenMod(string relativeName = "broken_1.0.0.mtmod") =>
        AddRawFile(relativeName, "PK\u0003\u0004 это не архив, а огрызок");

    /// <summary>
    /// Создаёт фейковый проект мода: <c>src\meta.xml</c> и, при желании, собранный <c>dist\</c>.
    /// </summary>
    /// <param name="folderName">Имя каталога проекта, например <c>wot-mod-alpha</c>.</param>
    /// <param name="modId">Идентификатор мода.</param>
    /// <param name="version">Версия.</param>
    /// <param name="displayName">Имя мода.</param>
    /// <param name="buildArtifact">Собрать ли артефакт в <c>dist\</c>.</param>
    /// <returns>Абсолютный путь каталога проекта.</returns>
    public string AddProject(
        string folderName,
        string? modId,
        string version = "1.0.0",
        string displayName = "Fake Mod",
        bool buildArtifact = true)
    {
        string dir = Path.Combine(Root, "projects", folderName);
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        File.WriteAllText(Path.Combine(dir, "build.py"), "# fake\n");

        if (modId is not null)
        {
            File.WriteAllText(
                Path.Combine(dir, "src", "meta.xml"),
                Meta(modId, version, displayName, "описание " + displayName),
                new UTF8Encoding(false));
        }

        if (buildArtifact && modId is not null)
        {
            // Исходник того, что кладётся в архив: build.py раскладывает src один к одному, и
            // сборка с файлом без исходника считается устаревшей.
            string entry = Path.Combine(dir, "src", "res", "scripts", "client", "gui", "mods",
                $"mod_{folderName.Replace("wot-mod-", string.Empty)}.py");
            Directory.CreateDirectory(Path.GetDirectoryName(entry)!);
            File.WriteAllText(entry, "def init():\n    pass\n");

            string dist = Path.Combine(dir, "dist");
            Directory.CreateDirectory(dist);
            WriteArchive(
                Path.Combine(dist, $"{modId}_{version}.mtmod"),
                Meta(modId, version, displayName, "описание " + displayName),
                [new KeyValuePair<string, byte[]>(
                    $"res/scripts/client/gui/mods/mod_{folderName.Replace("wot-mod-", string.Empty)}.pyc",
                    Encoding.ASCII.GetBytes("fake bytecode"))],
                compress: false);
        }

        return dir;
    }

    /// <summary>Копирует файл байт в байт (для проверки «установлено совпадает со сборкой»).</summary>
    /// <param name="source">Откуда.</param>
    /// <param name="targetRelative">Куда, относительно каталога модов.</param>
    /// <returns>Абсолютный путь копии.</returns>
    public string CopyIntoMods(string source, string targetRelative)
    {
        string full = Path.Combine(ModsDir, targetRelative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.Copy(source, full, overwrite: true);
        return full;
    }

    /// <summary>Перечисляет все файлы каталога модов — контрольный замер «ничего не пропало».</summary>
    /// <returns>Отсортированные относительные пути.</returns>
    public string[] Snapshot()
    {
        string[] files = Directory.GetFiles(ModsDir, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);
        return files;
    }

    /// <inheritdoc />
    public void Dispose() => _tmp.Dispose();

    // ------------------------------------------------------------------ статические помощники

    /// <summary>Собирает <c>meta.xml</c> в том же виде, в каком его кладут реальные моды.</summary>
    /// <param name="id">Идентификатор.</param>
    /// <param name="version">Версия.</param>
    /// <param name="name">Имя.</param>
    /// <param name="description">Описание.</param>
    /// <returns>Текст XML.</returns>
    public static string Meta(string id, string version, string name, string description) =>
        $"""
         <?xml version="1.0" encoding="UTF-8"?>
         <root>
           <id>{id}</id>
           <version>{version}</version>
           <name>{name}</name>
           <description>{description}</description>
         </root>
         """;

    /// <summary>Пишет ZIP-архив с заданными записями.</summary>
    /// <param name="path">Куда.</param>
    /// <param name="metaXml">Содержимое meta.xml либо <c>null</c>.</param>
    /// <param name="entries">Прочие записи.</param>
    /// <param name="compress">Сжимать ли (по умолчанию Stored, как у всех реальных модов).</param>
    public static void WriteArchive(
        string path,
        string? metaXml,
        IEnumerable<KeyValuePair<string, byte[]>>? entries,
        bool compress)
    {
        CompressionLevel level = compress ? CompressionLevel.Optimal : CompressionLevel.NoCompression;

        using FileStream fs = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using (ZipArchive zip = new(fs, ZipArchiveMode.Create, leaveOpen: true))
        {
            if (metaXml is not null)
            {
                WriteEntry(zip, "meta.xml", new UTF8Encoding(false).GetBytes(metaXml), level);
            }

            foreach (KeyValuePair<string, byte[]> e in entries ?? [])
            {
                WriteEntry(zip, e.Key, e.Value, level);
            }
        }

        // Сразу на диск, вместе с метаданными. Иначе Windows сбрасывает их позже сама и
        // досылает наблюдателю экрана «Моды» запоздалые события об этом файле — уже тогда,
        // когда тест проверяет, КТО перечитал список.
        fs.Flush(flushToDisk: true);
    }

    /// <summary>Пишет одну запись архива.</summary>
    /// <param name="zip">Архив.</param>
    /// <param name="name">Путь записи.</param>
    /// <param name="bytes">Содержимое.</param>
    /// <param name="level">Уровень сжатия.</param>
    private static void WriteEntry(ZipArchive zip, string name, byte[] bytes, CompressionLevel level)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, level);
        using Stream s = entry.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    /// <summary>Запись архива из строки — сокращение для читаемости тестов.</summary>
    /// <param name="path">Путь внутри архива.</param>
    /// <param name="content">Текст.</param>
    /// <returns>Пара «путь → байты».</returns>
    public static KeyValuePair<string, byte[]> Entry(string path, string content = "x") =>
        new(path, Encoding.UTF8.GetBytes(content));

    /// <summary>Иконка мода: настоящий PNG заданного размера.</summary>
    /// <param name="path">Путь внутри архива.</param>
    /// <param name="size">Сторона квадрата.</param>
    /// <returns>Пара «путь → байты PNG».</returns>
    public static KeyValuePair<string, byte[]> IconEntry(string path, int size = ModIcons.GameIconSize) =>
        new(path, PngFactory.Square(size));
}

/// <summary>
/// Генератор настоящих PNG для тестов. Нужен именно настоящий (а не обрубок заголовка),
/// потому что кокпит отдаёт байты иконки в UI, и тест обязан проверять то же, что увидит WPF.
/// </summary>
internal static class PngFactory
{
    /// <summary>Делает квадратный непрозрачный PNG 8 бит RGBA — ровно тот формат, что у всех игровых иконок.</summary>
    /// <param name="size">Сторона.</param>
    /// <returns>Содержимое файла PNG.</returns>
    public static byte[] Square(int size)
    {
        using MemoryStream ms = new();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, size);
        WriteBigEndian(ihdr, 4, size);
        ihdr[8] = 8;    // бит на канал
        ihdr[9] = 6;    // color type 6 = RGBA
        WriteChunk(ms, "IHDR", ihdr);

        // Сырые строки: фильтр 0 + RGBA на пиксель.
        byte[] raw = new byte[size * (1 + size * 4)];
        for (int y = 0; y < size; y++)
        {
            int row = y * (1 + size * 4);
            raw[row] = 0;
            for (int x = 0; x < size; x++)
            {
                int p = row + 1 + x * 4;
                raw[p] = 0x4F;
                raw[p + 1] = 0xC3;
                raw[p + 2] = 0xF7;
                raw[p + 3] = 0xFF;
            }
        }

        using MemoryStream deflated = new();
        using (System.IO.Compression.ZLibStream z = new(deflated, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(raw, 0, raw.Length);
        }

        WriteChunk(ms, "IDAT", deflated.ToArray());
        WriteChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>Пишет чанк PNG с длиной, типом и CRC32.</summary>
    /// <param name="ms">Поток.</param>
    /// <param name="type">Тип чанка.</param>
    /// <param name="data">Данные.</param>
    private static void WriteChunk(MemoryStream ms, string type, byte[] data)
    {
        byte[] length = new byte[4];
        WriteBigEndian(length, 0, data.Length);
        ms.Write(length);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        ms.Write(typeBytes);
        ms.Write(data);

        byte[] forCrc = new byte[typeBytes.Length + data.Length];
        Buffer.BlockCopy(typeBytes, 0, forCrc, 0, typeBytes.Length);
        Buffer.BlockCopy(data, 0, forCrc, typeBytes.Length, data.Length);

        byte[] crc = new byte[4];
        WriteBigEndian(crc, 0, unchecked((int)Crc32(forCrc)));
        ms.Write(crc);
    }

    /// <summary>Записывает 32-битное целое в порядке big-endian.</summary>
    /// <param name="buffer">Буфер.</param>
    /// <param name="offset">Смещение.</param>
    /// <param name="value">Значение.</param>
    private static void WriteBigEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>CRC32 по стандарту PNG.</summary>
    /// <param name="data">Данные.</param>
    /// <returns>Контрольная сумма.</returns>
    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return crc ^ 0xFFFFFFFFu;
    }
}

/// <summary>Проба «игра запущена», управляемая тестом.</summary>
internal static class FakeGameProbes
{
    /// <summary>Игра не запущена — операции разрешены.</summary>
    public static IGameRunningProbe Closed { get; } = new FixedGameRunningProbe(GameRunningState.NotRunning);

    /// <summary>Игра запущена — все операции записи обязаны быть отвергнуты.</summary>
    public static IGameRunningProbe Running { get; } = new FixedGameRunningProbe(
        new GameRunningState(GameRunningStatus.Running, "Tanki", 7412, "ИГРА ЗАПУЩЕНА (Tanki.exe, PID 7412)"));

    /// <summary>Выяснить не удалось — тоже запрет, неизвестность трактуется в пользу отказа.</summary>
    public static IGameRunningProbe Unknown { get; } = new FixedGameRunningProbe(
        new GameRunningState(GameRunningStatus.Unknown, null, null, "не удалось проверить процессы"));
}
